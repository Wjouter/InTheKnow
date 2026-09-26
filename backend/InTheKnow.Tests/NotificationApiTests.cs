using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static InTheKnow.Tests.LinkIdentityTests;

namespace InTheKnow.Tests;

// The notification routes of specs/003 contracts/api.md.
public class NotificationApiTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    const string A = "a@example.nl";

    static async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(DeliveryHost d, HttpMethod method, string path, string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var r = await d.H.Client.SendAsync(request, Ct);
        var text = await r.Content.ReadAsStringAsync(Ct);
        return (r.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    static async Task Expect(Task<(HttpStatusCode Status, JsonNode? Body)> call, HttpStatusCode status, string? reason = null)
    {
        var (s, body) = await call;
        Assert.Equal(status, s);
        if (reason is not null) Assert.Equal(reason, body!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Channels()
    {
        await using var d = await DeliveryHost.CreateAsync("email");
        Task<(HttpStatusCode, JsonNode?)> Add(string json, string s = "s1") => SendAsync(d, HttpMethod.Post, $"/api/subscribers/{s}/channels", json);

        var (status, body) = await Add("""{"type":"email","target":" a@example.nl "}""");
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(["id", "type", "target", "status", "failureReason", "lastMessageAt", "createdAt"], body!.AsObject().Select(kv => kv.Key));
        Assert.Equal(("email", A, "awaitingConfirmation"), (body["type"]!.GetValue<string>(), body["target"]!.GetValue<string>(), body["status"]!.GetValue<string>()));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z$", body["createdAt"]!.GetValue<string>()); // UTC with Z, as in 001
        Assert.Null(body["failureReason"]);
        Assert.Null(body["lastMessageAt"]);
        var id = body["id"]!.GetValue<long>();

        await Expect(Add("""{"type":"fax","target":"a@b.nl"}"""), HttpStatusCode.BadRequest, "invalid_type");
        await Expect(Add("""{"target":"a@b.nl"}"""), HttpStatusCode.BadRequest, "invalid_type");
        await Expect(Add("""{"type":"email","target":"Name <a@b.nl>"}"""), HttpStatusCode.BadRequest, "invalid_target");
        await Expect(Add("""{"type":"email"}"""), HttpStatusCode.BadRequest, "invalid_target");
        await Expect(Add("""{"type":"email","target":"A@example.nl"}"""), HttpStatusCode.Conflict, "target_taken");
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.Created, (await Add($$"""{"type":"email","target":"{{i}}@example.nl"}""")).Item1);
        await Expect(Add("""{"type":"email","target":"x@example.nl"}"""), HttpStatusCode.Conflict, "channel_limit");
        await Expect(Add("""{"type":"email","target":"x@example.nl"}""", "a b"), HttpStatusCode.BadRequest, "invalid_subscriber");

        var list = (await SendAsync(d, HttpMethod.Get, "/api/subscribers/s1/channels")).Body!["items"]!.AsArray();
        Assert.Equal(5, list.Count);
        Assert.Equal(list.Select(c => c!["id"]!.GetValue<long>()).Order(), list.Select(c => c!["id"]!.GetValue<long>()));
        Assert.Empty((await SendAsync(d, HttpMethod.Get, "/api/subscribers/s2/channels")).Body!["items"]!.AsArray());

        // Another subscriber's channel id is 404 on every channel route.
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Delete, ""), (HttpMethod.Post, "/pause"), (HttpMethod.Post, "/resume"), (HttpMethod.Post, "/confirmation"), (HttpMethod.Post, "/test"),
        })
            await Expect(SendAsync(d, method, $"/api/subscribers/s2/channels/{id}{path}"), HttpStatusCode.NotFound);

        // Lifecycle routes.
        await Expect(SendAsync(d, HttpMethod.Post, $"/api/subscribers/s1/channels/{id}/pause"), HttpStatusCode.Conflict, "invalid_status");
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(d, HttpMethod.Post, $"/api/subscribers/s1/channels/{id}/confirmation")).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(d, HttpMethod.Post, $"/api/subscribers/s1/channels/{id}/confirmation")).Status);
        await Expect(SendAsync(d, HttpMethod.Post, $"/api/subscribers/s1/channels/{id}/confirmation"), HttpStatusCode.TooManyRequests, "confirmation_limit");
        await d.ConfirmLatestAsync(A);
        await Expect(SendAsync(d, HttpMethod.Post, $"/api/subscribers/s1/channels/{id}/confirmation"), HttpStatusCode.Conflict, "invalid_status");
        var paused = await SendAsync(d, HttpMethod.Post, $"/api/subscribers/s1/channels/{id}/pause");
        Assert.Equal((HttpStatusCode.OK, "paused"), (paused.Status, paused.Body!["status"]!.GetValue<string>()));
        var test = await SendAsync(d, HttpMethod.Post, $"/api/subscribers/s1/channels/{id}/test");
        Assert.Equal((HttpStatusCode.Conflict, "channel_not_active", "paused"),
            (test.Status, test.Body!["reason"]!.GetValue<string>(), test.Body["channelStatus"]!.GetValue<string>()));
        await Expect(SendAsync(d, HttpMethod.Post, $"/api/subscribers/s1/channels/{id}/pause"), HttpStatusCode.Conflict, "invalid_status");
        var resumed = await SendAsync(d, HttpMethod.Post, $"/api/subscribers/s1/channels/{id}/resume");
        Assert.Equal((HttpStatusCode.OK, "active"), (resumed.Status, resumed.Body!["status"]!.GetValue<string>()));
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(d, HttpMethod.Post, $"/api/subscribers/s1/channels/{id}/test")).Status);
        await Expect(SendAsync(d, HttpMethod.Post, $"/api/subscribers/s1/channels/{id}/test"), HttpStatusCode.TooManyRequests, "test_limit");
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(d, HttpMethod.Delete, $"/api/subscribers/s1/channels/{id}")).Status);
        await Expect(SendAsync(d, HttpMethod.Delete, $"/api/subscribers/s1/channels/{id}"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnconfiguredTypeIs503()
    {
        await using var d = await DeliveryHost.CreateAsync("test");
        var id = await d.AddActiveChannelAsync("s1", A);
        var failing = await CheckerTests.ChannelAsync(d.Db, "s1", "failing", type: "test");
        ((TestTypeUnderTest)d.C).Channel.Configured = false;
        await Expect(SendAsync(d, HttpMethod.Post, "/api/subscribers/s1/channels", """{"type":"test","target":"b@example.nl"}"""), HttpStatusCode.ServiceUnavailable, "type_unavailable");
        await Expect(SendAsync(d, HttpMethod.Post, $"/api/subscribers/s1/channels/{id}/test"), HttpStatusCode.ServiceUnavailable, "type_unavailable");
        await Expect(SendAsync(d, HttpMethod.Post, $"/api/subscribers/s1/channels/{failing}/confirmation"), HttpStatusCode.ServiceUnavailable, "type_unavailable");
    }

    [Fact]
    public async Task DeclinedTargetIs409()
    {
        await using var d = await DeliveryHost.CreateAsync("test");
        var r = await SendAsync(d, HttpMethod.Post, "/api/subscribers/s1/channels", """{"type":"test","target":"a@example.nl"}""");
        var id = r.Body!["id"]!.GetValue<long>();
        await d.NotifyAsync(3);
        await d.PostPageAsync(d.To(A).Single().DeclineUrl!);
        await Expect(SendAsync(d, HttpMethod.Post, $"/api/subscribers/s1/channels/{id}/confirmation"), HttpStatusCode.Conflict, "target_declined");
        await SendAsync(d, HttpMethod.Delete, $"/api/subscribers/s1/channels/{id}");
        await Expect(SendAsync(d, HttpMethod.Post, "/api/subscribers/s1/channels", """{"type":"test","target":"A@example.nl"}"""), HttpStatusCode.Conflict, "target_declined");
    }

    [Fact]
    public async Task SubscriptionNotifications()
    {
        await using var d = await DeliveryHost.CreateAsync("test");
        var calendar = await d.SubscribeAsync("s1", MtLink());
        var path = $"/api/subscribers/s1/calendars/{calendar}/notifications";
        const string feed = "Hogeschool Inholland - Rooster - ALET26VT4A";

        var get = await SendAsync(d, HttpMethod.Get, path);
        Assert.Equal("""{"notify":true,"label":null,"name":"Hogeschool Inholland - Rooster - ALET26VT4A"}""", get.Body!.ToJsonString());
        var put = await SendAsync(d, HttpMethod.Put, path, """{"label":"Minor robotics"}""");
        Assert.Equal("""{"notify":true,"label":"Minor robotics","name":"Minor robotics"}""", put.Body!.ToJsonString());
        await Expect(SendAsync(d, HttpMethod.Put, path, """{"label":""}"""), HttpStatusCode.BadRequest, "invalid_label");
        await Expect(SendAsync(d, HttpMethod.Put, path, """{"notify":"yes"}"""), HttpStatusCode.BadRequest, "invalid_notify");
        await Expect(SendAsync(d, HttpMethod.Put, path, "{}"), HttpStatusCode.BadRequest, "empty_body");
        Assert.Equal("""{"notify":false,"label":"Minor robotics","name":"Minor robotics"}""", (await SendAsync(d, HttpMethod.Put, path, """{"notify":false}""")).Body!.ToJsonString());

        var calendars = (await d.H.JsonAsync("/api/subscribers/s1/calendars")).AsArray();
        var c = Assert.Single(calendars)!;
        Assert.Equal((false, "Minor robotics", "Minor robotics"), (c["notify"]!.GetValue<bool>(), c["label"]!.GetValue<string>(), c["name"]!.GetValue<string>()));
        Assert.Equal($$"""{"notify":false,"label":null,"name":"{{feed}}"}""", (await SendAsync(d, HttpMethod.Put, path, """{"label":null}""")).Body!.ToJsonString());

        await Expect(SendAsync(d, HttpMethod.Get, $"/api/subscribers/s2/calendars/{calendar}/notifications"), HttpStatusCode.NotFound);
        await Expect(SendAsync(d, HttpMethod.Put, $"/api/subscribers/s2/calendars/{calendar}/notifications", """{"notify":false}"""), HttpStatusCode.NotFound);
        await Expect(SendAsync(d, HttpMethod.Get, $"/api/subscribers/a%20b/calendars/{calendar}/notifications"), HttpStatusCode.BadRequest, "invalid_subscriber");

        // No response carries a calendar link or feed token (SC-010).
        var token = new Uri(MtLink()).Query;
        foreach (var p in new[] { path, "/api/subscribers/s1/calendars", "/api/subscribers/s1/channels" })
            Assert.DoesNotContain("/ical?", (await SendAsync(d, HttpMethod.Get, p)).Body!.ToJsonString());
        Assert.DoesNotContain(token, (await SendAsync(d, HttpMethod.Get, "/api/subscribers/s1/calendars")).Body!.ToJsonString());
    }

    [Fact]
    public async Task NotificationSettings()
    {
        await using var d = await DeliveryHost.CreateAsync("test");
        const string path = "/api/subscribers/s1/notification-settings";
        Assert.Equal("""{"language":"en","timeZone":null,"effectiveTimeZone":"Europe/Amsterdam"}""", (await SendAsync(d, HttpMethod.Get, path)).Body!.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(d, HttpMethod.Put, path, """{"language":"nl"}""")).Status);
        var zone = await SendAsync(d, HttpMethod.Put, path, """{"timeZone":"America/New_York"}""");
        Assert.Equal("""{"language":"nl","timeZone":"America/New_York","effectiveTimeZone":"America/New_York"}""", zone.Body!.ToJsonString());
        await Expect(SendAsync(d, HttpMethod.Put, path, """{"language":"de"}"""), HttpStatusCode.BadRequest, "invalid_language");
        await Expect(SendAsync(d, HttpMethod.Put, path, """{"timeZone":"W. Europe Standard Time"}"""), HttpStatusCode.BadRequest, "invalid_time_zone");
        await Expect(SendAsync(d, HttpMethod.Put, path, """{"timeZone":"Mars/Base"}"""), HttpStatusCode.BadRequest, "invalid_time_zone");
        await Expect(SendAsync(d, HttpMethod.Put, path, "{}"), HttpStatusCode.BadRequest, "empty_body");
        Assert.Equal("""{"language":"nl","timeZone":"America/New_York","effectiveTimeZone":"America/New_York"}""", (await SendAsync(d, HttpMethod.Get, path)).Body!.ToJsonString());
        Assert.Equal("""{"language":"nl","timeZone":null,"effectiveTimeZone":"Europe/Amsterdam"}""", (await SendAsync(d, HttpMethod.Put, path, """{"timeZone":null}""")).Body!.ToJsonString());
    }

    // FR-030: every new /api route needs the key; the /n/ pages do not.
    [Fact]
    public async Task OperatorKey()
    {
        await using var d = await DeliveryHost.CreateAsync("test");
        using var bare = d.H.Factory.CreateClient();
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/api/subscribers/s1/channels"), (HttpMethod.Post, "/api/subscribers/s1/channels"),
            (HttpMethod.Delete, "/api/subscribers/s1/channels/1"), (HttpMethod.Post, "/api/subscribers/s1/channels/1/pause"),
            (HttpMethod.Post, "/api/subscribers/s1/channels/1/resume"), (HttpMethod.Post, "/api/subscribers/s1/channels/1/confirmation"),
            (HttpMethod.Post, "/api/subscribers/s1/channels/1/test"), (HttpMethod.Get, "/api/subscribers/s1/calendars/1/notifications"),
            (HttpMethod.Put, "/api/subscribers/s1/calendars/1/notifications"), (HttpMethod.Get, "/api/subscribers/s1/notification-settings"),
            (HttpMethod.Put, "/api/subscribers/s1/notification-settings"), (HttpMethod.Get, "/api/subscribers/s1/channel-types"),
            (HttpMethod.Get, "/api/channel-types"),
        })
            Assert.Equal(HttpStatusCode.Unauthorized, (await bare.SendAsync(new HttpRequestMessage(method, path), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bare.GetAsync($"/n/confirm/{InTheKnow.Channels.NewToken()}", Ct)).StatusCode);
    }

    // US6-3: the types a subscriber can add now.
    [Fact]
    public async Task ChannelTypesForSubscribers()
    {
        async Task<string> TypesAsync(ApiHost h) => (await h.JsonAsync("/api/subscribers/s1/channel-types")).ToJsonString();
        await using (var h = await ApiHost.CreateAsync([.. EmailChannelTests.EmailSettings(25).Select(s => (s.Item1, s.Item2!))]))
        {
            Assert.Equal("""{"items":[{"type":"email"}]}""", await TypesAsync(h));
            var r = await h.Client.PostAsJsonAsync("/api/subscribers/s1/channels", new { type = "test", target = A }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("invalid_type", JsonNode.Parse(await r.Content.ReadAsStringAsync(Ct))!["reason"]!.GetValue<string>());
        }
        await using (var h = await ApiHost.CreateAsync())
            Assert.Equal("""{"items":[]}""", await TypesAsync(h));
        await using (var h = await ApiHost.CreateAsync(s => s.AddSingleton<IChannelType>(new RecordingChannel()),
                         [.. EmailChannelTests.EmailSettings(25).Select(s => (s.Item1, s.Item2!))]))
            Assert.Equal("""{"items":[{"type":"email"},{"type":"test"}]}""", await TypesAsync(h));
    }

    // FR-030a, US4-7: the operator's status per type.
    [Fact]
    public async Task ChannelTypeStatus()
    {
        await using var d = await DeliveryHost.CreateAsync("email");
        async Task<JsonNode> Status() => Assert.Single((await d.H.JsonAsync("/api/channel-types"))["items"]!.AsArray())!;
        var before = await Status();
        Assert.Equal("""{"type":"email","configured":true,"lastAttemptAt":null,"lastOk":null,"lastReason":null,"consecutiveFailures":0,"failingSince":null,"waiting":0}""",
            before.ToJsonString());

        var url = MtLink();
        await d.SubscribeAsync("s1", url);
        await d.AddActiveChannelAsync("s1", A);
        await d.AddActiveChannelAsync("s1", "b@example.nl");
        await d.PostAsync("/api/subscribers/s2/channels", new { type = "email", target = "c@example.nl" });
        await d.RecordChangesAsync(url, "mt-nl-edited.ics");
        d.C.Outage = true;
        await d.AdvanceAndNotifyAsync(d.Window, passes: 1);
        var failing = await Status();
        Assert.Equal((false, "unreachable", 1, 3L), (failing["lastOk"]!.GetValue<bool>(), failing["lastReason"]!.GetValue<string>(),
            failing["consecutiveFailures"]!.GetValue<int>(), failing["waiting"]!.GetValue<long>()));
        Assert.EndsWith("Z", failing["failingSince"]!.GetValue<string>());
        Assert.EndsWith("Z", failing["lastAttemptAt"]!.GetValue<string>());
        var since = failing["failingSince"]!.GetValue<DateTimeOffset>();
        await d.AdvanceAndNotifyAsync(TimeSpan.FromMinutes(1), passes: 1);
        var again = await Status();
        Assert.Equal((2, since), (again["consecutiveFailures"]!.GetValue<int>(), again["failingSince"]!.GetValue<DateTimeOffset>()));
        Assert.DoesNotContain("@", again.ToJsonString());
        Assert.DoesNotContain("s1", again.ToJsonString());

        d.C.Outage = false;
        await d.AdvanceAndNotifyAsync(TimeSpan.FromMinutes(2));
        var ok = await Status();
        Assert.Equal((true, 0, 0L), (ok["lastOk"]!.GetValue<bool>(), ok["consecutiveFailures"]!.GetValue<int>(), ok["waiting"]!.GetValue<long>()));
        Assert.Null(ok["failingSince"]);
    }
}
