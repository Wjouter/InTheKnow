using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static InTheKnow.Tests.LinkIdentityTests;

namespace InTheKnow.Tests;

// specs/004 US2, SC-002, SC-006: every /api/me route acts only for the signed-in account; theories over password and test.
public class OwnershipTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    public static TheoryData<string> Methods => SignInTests.Methods;
    const long Missing = 999_999_999;

    // Every /api/me route, with a request that would change or reveal another account's item if ownership failed.
    // Ids: {calendarId}, {channelId}, and {id} of the kind named here. A new route without an entry fails the test.
    internal static readonly Dictionary<string, (string? Body, string? IdKind)> Routes = new()
    {
        ["GET /api/me"] = (null, null),
        ["POST /api/me/reauthenticate"] = ("{}", null),
        ["POST /api/me/links"] = ("{}", null),
        ["GET /api/me/calendars"] = (null, null),
        ["DELETE /api/me/calendars/{calendarId:long}"] = (null, null),
        ["GET /api/me/changes"] = (null, null),
        ["GET /api/me/calendars/{calendarId:long}/filter"] = (null, null),
        ["PUT /api/me/calendars/{calendarId:long}/filter"] = ("""{"rules":[]}""", null),
        ["PUT /api/me/calendars/{calendarId:long}/filter/preset"] = ("""{"presetId":1}""", null),
        ["GET /api/me/presets"] = (null, null),
        ["POST /api/me/presets"] = ("{}", null),
        ["PUT /api/me/presets/{id:long}"] = ("""{"name":"Taken"}""", "preset"),
        ["DELETE /api/me/presets/{id:long}"] = (null, "preset"),
        ["GET /api/me/channels"] = (null, null),
        ["POST /api/me/channels"] = ("{}", null),
        ["DELETE /api/me/channels/{channelId:long}"] = (null, null),
        ["POST /api/me/channels/{channelId:long}/pause"] = (null, null),
        ["POST /api/me/channels/{channelId:long}/resume"] = (null, null),
        ["POST /api/me/channels/{channelId:long}/confirmation"] = (null, null),
        ["POST /api/me/channels/{channelId:long}/test"] = (null, null),
        ["GET /api/me/calendars/{calendarId:long}/notifications"] = (null, null),
        ["PUT /api/me/calendars/{calendarId:long}/notifications"] = ("""{"notify":false,"label":"Taken"}""", null),
        ["GET /api/me/notification-settings"] = (null, null),
        ["PUT /api/me/notification-settings"] = ("{}", null),
        ["PUT /api/me/settings"] = ("""{"homePage":"combined"}""", null),
        ["GET /api/me/channel-types"] = (null, null),
        // US4 (T044)
        ["POST /api/me/2fa/setup"] = (null, null),
        ["POST /api/me/2fa/enable"] = ("""{"code":"000000"}""", null),
        ["DELETE /api/me/2fa"] = (null, null),
        ["POST /api/me/2fa/recovery-codes"] = ("""{"code":"000000"}""", null),
        // US5 (T049)
        ["PUT /api/me/password"] = ("""{"password":"taken-over-password"}""", null),
        ["POST /api/me/email"] = ("""{"email":"taken@example.nl"}""", null),
        ["DELETE /api/me"] = (null, null),
        ["GET /api/me/sessions"] = (null, null),
        ["DELETE /api/me/sessions/{id:long}"] = (null, "session"),
        // US6 (T053)
        ["GET /api/me/methods"] = (null, null),
        ["DELETE /api/me/methods/{id:long}"] = (null, "method"),
    };

    internal static List<(string Key, string Method, string Pattern)> MeRoutes(ApiHost h) =>
        [.. h.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/me", StringComparison.Ordinal))
            .SelectMany(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Select(m => ($"{m} {e.RoutePattern.RawText}", m, e.RoutePattern.RawText!)))];

    sealed record Items(long Calendar, long Channel, Dictionary<string, long> Ids);

    static string Fill(string pattern, Items items) => pattern
        .Replace("{calendarId:long}", items.Calendar.ToString()).Replace("{channelId:long}", items.Channel.ToString())
        .Replace("{id:long}", items.Ids.Values.FirstOrDefault().ToString());

    static async Task<(HttpStatusCode, string)> CallAsync(HttpClient client, string method, string path, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var r = await client.SendAsync(request, Ct);
        var text = await r.Content.ReadAsStringAsync(Ct);
        if (text.Length > 0 && JsonNode.Parse(text) is JsonObject o) { o.Remove("traceId"); text = o.ToJsonString(); }
        return (r.StatusCode, text);
    }

    static async Task<List<string>> SnapshotAsync(ApiHost h, long account)
    {
        var rows = new List<string>();
        foreach (var (table, column) in new[]
        {
            ("calendar_link", "subscriber"), ("subscription_filter", "subscriber"), ("preset", "owner"), ("channel", "subscriber"),
            ("subscription_setting", "subscriber"), ("session", "account_id"),
        })
            rows.AddRange((await h.Db.RowsAsync($"SELECT row_to_json(t)::text FROM {table} t WHERE {column} = $1 ORDER BY 1", account)).Select(r => (string)r[0]!));
        return rows;
    }

    [Theory, MemberData(nameof(Methods))]
    public async Task EveryMeRouteIsScopedToTheAccount(string method)
    {
        await using var h = await SignInTests.HostAsync(s => s.AddSingleton<IChannelType>(new RecordingChannel()));
        var a = await h.SignInViaAsync(method, (await h.SignInAsync(method: method)).Email);
        var b = await h.SignInViaAsync(method, (await h.SignInAsync(method: method)).Email);

        // A's items.
        var calendar = JsonNode.Parse(await (await h.AddAsync(a, MtLink())).Content.ReadAsStringAsync(Ct))!["calendarId"]!.GetValue<long>();
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(a.Client, "PUT", $"/api/me/calendars/{calendar}/filter", """{"rules":[]}""")).Item1);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(a.Client, "PUT", $"/api/me/calendars/{calendar}/notifications", """{"label":"Mine"}""")).Item1);
        var preset = JsonNode.Parse((await CallAsync(a.Client, "POST", "/api/me/presets", """{"name":"Mine","rules":[]}""")).Item2)!["id"]!.GetValue<long>();
        var channel = JsonNode.Parse((await CallAsync(a.Client, "POST", "/api/me/channels", """{"type":"test","target":"a@example.nl"}""")).Item2)!["id"]!.GetValue<long>();
        var ids = new Dictionary<string, long> { ["preset"] = preset, ["session"] = a.SessionId };
        ids["method"] = await h.Db.ScalarAsync("SELECT id FROM sign_in_method WHERE account_id = $1", a.AccountId);

        // US2-1: A's rows carry A's id.
        foreach (var table in new[] { "calendar_link", "subscription_filter", "channel", "subscription_setting" })
            Assert.Equal(1, await h.Db.ScalarAsync($"SELECT count(*) FROM {table} WHERE subscriber = $1", a.AccountId));
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM preset WHERE owner = $1", a.AccountId));

        var routes = MeRoutes(h);
        Assert.NotEmpty(routes);
        var unlisted = routes.Where(r => !Routes.ContainsKey(r.Key)).Select(r => r.Key).ToList();
        Assert.True(unlisted.Count == 0, "routes without an ownership entry: " + string.Join(", ", unlisted));

        // B's lists are empty.
        Assert.Equal("[]", (await b.JsonAsync("/api/me/calendars")).ToJsonString());
        Assert.Empty((await b.JsonAsync("/api/me/changes?includeHidden=true"))["items"]!.AsArray());
        Assert.Empty((await b.JsonAsync("/api/me/channels"))["items"]!.AsArray());
        Assert.DoesNotContain((await b.JsonAsync("/api/me/presets"))["items"]!.AsArray(), p => p!["scope"]!.GetValue<string>() == "personal");

        // As B, A's ids get exactly what a missing id gets, and A's rows do not change.
        var before = await SnapshotAsync(h, a.AccountId);
        foreach (var (key, verb, pattern) in routes)
        {
            var (body, kind) = Routes[key];
            if (!pattern.Contains('{'))
            {
                await CallAsync(b.Client, verb, pattern, body); // no id: it can only act on B's own items
                continue;
            }
            var theirs = new Items(calendar, channel, kind is null ? [] : new() { [kind] = ids[kind] });
            var missing = new Items(Missing, Missing, kind is null ? [] : new() { [kind] = Missing });
            var withTheirs = await CallAsync(b.Client, verb, Fill(pattern, theirs), body);
            var withMissing = await CallAsync(b.Client, verb, Fill(pattern, missing), body);
            Assert.True(withTheirs == withMissing, $"{key}: {withTheirs} vs {withMissing}");
        }
        Assert.Equal(before, await SnapshotAsync(h, a.AccountId));

        // US2-3: no session, an idle one and an awaiting-code one are all signed out on every route.
        var idle = SignIn.NewToken();
        var awaiting = SignIn.NewToken();
        var bMethod = await h.Db.ScalarAsync("SELECT id FROM sign_in_method WHERE account_id = $1", b.AccountId);
        foreach (var (token, awaitingCode, lastUsed) in new[] { (idle, false, h.Time.GetUtcNow() - TimeSpan.FromDays(31)), (awaiting, true, h.Time.GetUtcNow()) })
            await h.Db.ExecAsync("""
                INSERT INTO session (account_id, method_id, token_hash, device, awaiting_code, created_at, last_used_at) VALUES ($1, $2, $3, 'd', $4, $5, $5)
                """, b.AccountId, bMethod, token.Hash, awaitingCode, lastUsed);
        foreach (var client in new[] { h.Factory.CreateClient(), h.ClientFor(idle.Token), h.ClientFor(awaiting.Token) })
            foreach (var (key, verb, pattern) in routes)
            {
                var (status, text) = await CallAsync(client, verb, Fill(pattern, new(calendar, channel, [])), Routes[key].Body);
                Assert.True(status == HttpStatusCode.Unauthorized && text.Contains("not_signed_in"), $"{key}: {status}");
            }
    }

    // US2-4, US2-5, FR-028: a confirmed account has its own address as an active channel, which counts toward the limit.
    [Fact]
    public async Task ConfirmedAccountGetsItsAddressChannel()
    {
        await using var d = await SignUpTests.HostAsync();
        await SignUpTests.SignUpAsync(d, "student@example.nl", language: "nl");
        await d.NotifyAsync(2);
        var token = SignUpTests.TokenOf(SignUpTests.Mails(d, "confirm").Single().Message);
        Assert.Equal(HttpStatusCode.NoContent, (await SignUpTests.PostAsync(d, "/api/auth/confirm", new { token, password = TestHost.Password })).StatusCode);
        var s = await d.H.SignInViaAsync("password", "student@example.nl");

        var channel = Assert.Single((await s.JsonAsync("/api/me/channels"))["items"]!.AsArray())!;
        Assert.Equal(("email", "student@example.nl", "active", true), (channel["type"]!.GetValue<string>(), channel["target"]!.GetValue<string>(),
            channel["status"]!.GetValue<string>(), channel["accountAddress"]!.GetValue<bool>()));

        Assert.Equal("""{"language":"nl","timeZone":null,"effectiveTimeZone":"Europe/Amsterdam"}""", (await s.JsonAsync("/api/me/notification-settings")).ToJsonString());
        var put = await s.Client.PutAsync("/api/me/notification-settings", new StringContent("""{"language":"en","timeZone":"America/New_York"}""", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal([["en", "America/New_York"]], await d.Db.RowsAsync("SELECT language, time_zone FROM account WHERE id = $1", s.AccountId));

        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Created, (await d.PostAsync(s, "/api/me/channels", new { type = "email", target = $"{i}@example.nl" })).StatusCode);
        var sixth = await d.PostAsync(s, "/api/me/channels", new { type = "email", target = "6@example.nl" });
        Assert.Equal((HttpStatusCode.Conflict, "channel_limit"), (sixth.StatusCode, await TestHost.ReasonAsync(sixth)));
    }
}
