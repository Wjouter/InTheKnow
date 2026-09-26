using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static InTheKnow.Tests.LinkIdentityTests;

namespace InTheKnow.Tests;

// Wraps a channel type and records each Message whose send returned Sent. `Before` runs ahead of every send.
sealed class Recorder(IChannelType inner) : IChannelType
{
    readonly List<(string Target, Message Message)> delivered = [];
    public readonly List<Func<string, CancellationToken, Task>> Before = [];
    public int Attempts;

    public string Name => inner.Name;
    public bool Configured => inner.Configured;
    public int MaxMessagesPerMinute => inner.MaxMessagesPerMinute;
    public string? Normalize(string target) => inner.Normalize(target);
    public IReadOnlyList<(string Target, Message Message)> Delivered { get { lock (delivered) return [.. delivered]; } }

    public async Task<SendResult> SendAsync(string target, Message message, CancellationToken ct)
    {
        foreach (var b in Before) await b(target, ct);
        Interlocked.Increment(ref Attempts);
        var r = await inner.SendAsync(target, message, ct);
        if (r.Status == SendStatus.Sent) lock (delivered) delivered.Add((target, message));
        return r;
    }
}

// One channel type under the delivery suite (SC-008): how to register it, and how to make it fail.
interface IChannelUnderTest : IAsyncDisposable
{
    string Type { get; }
    (string Key, string Value)[] Settings { get; }
    void Register(IServiceCollection services);
    Recorder Recorder { get; }
    IReadOnlyList<(string Target, Message Message)> Delivered => Recorder.Delivered;
    void FailTemporarily(string target, int times);
    void RejectPermanently(string target);
    void PoisonMessage(string target);
    bool Outage { get; set; }
    int MaxMessagesPerMinute { set; }
}

sealed class EmailUnderTest : IChannelUnderTest
{
    public required FakeSmtp Smtp { get; init; }
    readonly ConcurrentDictionary<string, int> failures = new(StringComparer.OrdinalIgnoreCase);
    Recorder? recorder;
    int rate = 60_000;

    public static async Task<EmailUnderTest> CreateAsync() => new() { Smtp = await FakeSmtp.StartAsync() };

    public string Type => "email";
    public (string Key, string Value)[] Settings =>
        [.. EmailChannelTests.EmailSettings(Smtp.Port).Select(s => (s.Item1, s.Item2!)), ("InTheKnow:Notifications:Email:MaxMessagesPerMinute", rate.ToString())];
    public Recorder Recorder => recorder ?? throw new InvalidOperationException("not resolved yet");
    public int MaxMessagesPerMinute { set => rate = value; }

    public void Register(IServiceCollection services) => services.AddSingleton<IChannelType>(sp =>
    {
        recorder = new Recorder(new EmailChannel(sp.GetRequiredService<Settings>()));
        // RCPT 450 for the scripted number of attempts, then 250.
        recorder.Before.Add((target, _) =>
        {
            if (failures.TryGetValue(target, out var n)) Smtp.Recipient(target, n > 0 ? 450 : null);
            if (n > 0) failures[target] = n - 1;
            return Task.CompletedTask;
        });
        return recorder;
    });

    public void FailTemporarily(string target, int times) => failures[target] = times;
    public void RejectPermanently(string target) => Smtp.Recipient(target, 550);
    public void PoisonMessage(string target) => Smtp.DataReplyFor(target, 554);
    public bool Outage { get => Smtp.Refuse; set => Smtp.Refuse = value; }

    // Every captured mail went through the recorder, and the other way round.
    public IReadOnlyList<CapturedMail> Mails => Smtp.Mails;
    public ValueTask DisposeAsync() => Smtp.DisposeAsync();
}

sealed class TestTypeUnderTest : IChannelUnderTest
{
    public readonly RecordingChannel Channel = new() { MaxMessagesPerMinute = 60_000 };
    public Recorder Recorder { get; }

    public TestTypeUnderTest() => Recorder = new(Channel);

    public string Type => "test";
    public (string Key, string Value)[] Settings =>
        [("InTheKnow:Notifications:PublicBaseUrl", EmailChannelTests.Public), ("InTheKnow:Notifications:FrontendBaseUrl", EmailChannelTests.Frontend)];
    public int MaxMessagesPerMinute { set => Channel.MaxMessagesPerMinute = value; }
    public void Register(IServiceCollection services) => services.AddSingleton<IChannelType>(Recorder);
    public void FailTemporarily(string target, int times) => Channel.Script(target, SendStatus.TargetFailed, times);
    public void RejectPermanently(string target) => Channel.Script(target, SendStatus.TargetRejected, int.MaxValue);
    public void PoisonMessage(string target) => Channel.Script(target, SendStatus.TypeUnavailable, int.MaxValue);
    public bool Outage { get => Channel.Outage; set => Channel.Outage = value; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// The in-memory app with the channel types under test and no background services; the notifier is driven by hand.
sealed class DeliveryHost : IAsyncDisposable
{
    public required ApiHost H { get; init; }
    public required IChannelUnderTest[] Types { get; init; }
    public IChannelUnderTest C => Types[0];
    public TestDb Db => H.Db;
    public Notifier Notifier => H.Factory.Services.GetRequiredService<Notifier>();
    public Channels Channels => H.Factory.Services.GetRequiredService<Channels>();
    public TimeSpan Window => H.Factory.Services.GetRequiredService<Settings>().Options.Notifications.CollectionWindow;
    public DateTimeOffset Now => H.Time.GetUtcNow();
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<IChannelUnderTest> UnderAsync(string type) => type == "email" ? await EmailUnderTest.CreateAsync() : new TestTypeUnderTest();

    public static async Task<DeliveryHost> CreateAsync(string type, params (string Key, string Value)[] extra) =>
        await CreateAsync([await UnderAsync(type)], extra);

    public static async Task<DeliveryHost> CreateAsync(IChannelUnderTest[] types, params (string Key, string Value)[] extra)
    {
        var h = await ApiHost.CreateAsync(s =>
        {
            s.RemoveAll<IChannelType>();
            foreach (var t in types) t.Register(s);
        }, [.. types.SelectMany(t => t.Settings), .. extra]);
        var d = new DeliveryHost { H = h, Types = types };
        _ = d.Notifier; // resolves the channel types
        return d;
    }

    // A link through the API (validation through FakeHttp); returns the calendar id.
    public async Task<long> SubscribeAsync(string subscriber, string url, string fixture = "mt-nl.ics")
    {
        var r = await H.AddAsync(subscriber, url, fixture);
        Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString());
        return JsonNode.Parse(await r.Content.ReadAsStringAsync(Ct))!["calendarId"]!.GetValue<long>();
    }

    // Inserted directly as active, for the stories that do not test consent.
    public Task<long> AddActiveChannelAsync(string subscriber, string target, string? type = null) =>
        CheckerTests.ChannelAsync(Db, subscriber, "active", Now, target, type ?? C.Type);

    // Swaps the feed and runs a real check: the only way changes (and so deliveries) are recorded (research R1).
    public async Task RecordChangesAsync(string url, string fixture)
    {
        H.Http.Map[new Uri(url).AbsoluteUri] = fixture;
        H.Time.Advance(TimeSpan.FromSeconds(1));
        await Db.ExecAsync("UPDATE calendar SET next_check_at = $1 WHERE id IN (SELECT calendar_id FROM calendar_link WHERE url = $2)", Now, new Uri(url).AbsoluteUri);
        await H.Checker.RunDueChecksAsync(Now, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    // Passes 1 ms apart: one send per type per pass at the harness's 60,000 messages per minute.
    public async Task NotifyAsync(int passes = 30)
    {
        for (var i = 0; i < passes; i++)
        {
            await Notifier.RunDueAsync(Now, Ct);
            H.Time.Advance(TimeSpan.FromMilliseconds(1));
        }
    }

    public async Task AdvanceAndNotifyAsync(TimeSpan span, int passes = 30)
    {
        H.Time.Advance(span);
        await NotifyAsync(passes);
    }

    public async Task<Dictionary<long, (string Status, string? Reason)>> DeliveryStatusesAsync(long channelId) =>
        (await Db.RowsAsync("SELECT change_id, status, reason FROM delivery WHERE channel_id = $1", channelId))
            .ToDictionary(r => (long)r[0]!, r => ((string)r[1]!, (string?)r[2]));

    // Change id → outcome under the subscriber's filter now, from 002's changes route.
    public async Task<List<(long Id, string Outcome)>> OutcomesAsync(string subscriber) =>
        [.. (await H.JsonAsync($"/api/subscribers/{subscriber}/changes?includeHidden=true&limit=500"))["items"]!.AsArray()
            .Select(i => (i!["id"]!.GetValue<long>(), i["outcome"]!.GetValue<string>()))];

    public List<Message> To(string target) => [.. C.Delivered.Where(d => d.Target == target).Select(d => d.Message)];

    public static List<long> Ids(Message m) => [.. m.Calendars.SelectMany(g => g.Changes).Select(i => i.Change.Id)];

    public async Task<string> TextAsync(string sql, params object[] args) => (string)(await Db.RowsAsync(sql, args)).Single()[0]!;

    public Task<HttpResponseMessage> PutAsync(string path, string json) =>
        H.Client.PutAsync(path, new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => H.Client.PostAsJsonAsync(path, body ?? new { }, Ct);

    // A public /n/ page, posted without the operator key.
    public async Task<HttpResponseMessage> PostPageAsync(string url, HttpContent? body = null)
    {
        using var bare = H.Factory.CreateClient();
        return await bare.PostAsync(new Uri(url).PathAndQuery, body, Ct);
    }

    // The confirmation flow through the API and the confirmation email: add, send, confirm. Returns the channel id.
    public async Task<long> AddConfirmedChannelAsync(string subscriber, string target)
    {
        var r = await PostAsync($"/api/subscribers/{subscriber}/channels", new { type = C.Type, target });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var id = JsonNode.Parse(await r.Content.ReadAsStringAsync(Ct))!["id"]!.GetValue<long>();
        await ConfirmLatestAsync(target);
        return id;
    }

    public async Task ConfirmLatestAsync(string target)
    {
        await NotifyAsync(3);
        var confirmation = To(target).Last(m => m.Kind == MessageKind.Confirmation);
        Assert.Equal(HttpStatusCode.OK, (await PostPageAsync(confirmation.ConfirmUrl!)).StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (C is EmailUnderTest e) Assert.Equal(e.Mails.Count, e.Recorder.Delivered.Count);
        await H.DisposeAsync();
        foreach (var t in Types) await t.DisposeAsync();
    }
}

public class DeliveryTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    public static TheoryData<string> Types => ["email", "test"];
    const string A = "a@example.nl", B = "b@example.nl";
    static readonly string Url1 = MtLink(user: "1001"), Url2 = MtLink(user: "2002");
    const string GenericUrl = "https://calendar.example.com/a.ics";

    // SC-001, US1-3, US1-4: one message with the normal and low-priority changes; hidden ones are skipped.
    [Theory, MemberData(nameof(Types))]
    public async Task OneMessageWithTheVisibleChanges(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        await d.SubscribeAsync("s1", Url1);
        var channel = await d.AddActiveChannelAsync("s1", A);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        var outcomes = await d.OutcomesAsync("s1");
        Assert.Equal(["hidden", "lowPriority", "normal"], outcomes.Select(o => o.Outcome).Distinct().Order());

        await d.AdvanceAndNotifyAsync(d.Window);
        var m = Assert.Single(d.To(A));
        Assert.Equal(MessageKind.Changes, m.Kind);
        var visible = outcomes.Where(o => o.Outcome != "hidden").ToList();
        Assert.Equal(visible.Select(o => o.Id), DeliveryHost.Ids(m));
        Assert.Equal(visible.Select(o => o.Outcome == "lowPriority"), m.Calendars.SelectMany(g => g.Changes).Select(i => i.LowPriority));
        Assert.Equal(0, m.NotListed);
        Assert.Equal($"{EmailChannelTests.Public}/n/unsubscribe/", m.UnsubscribeUrl![..^43]);
        Assert.Equal(outcomes.ToDictionary(o => o.Id, o => o.Outcome == "hidden" ? ("skipped", (string?)"hidden") : ("sent", null)),
            await d.DeliveryStatusesAsync(channel));
    }

    // No window before its time; a change after a sent message starts a new window (US1-8).
    [Theory, MemberData(nameof(Types))]
    public async Task CollectionWindow(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        await d.SubscribeAsync("s1", Url1);
        await d.AddActiveChannelAsync("s1", A);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        await d.AdvanceAndNotifyAsync(d.Window - TimeSpan.FromSeconds(2));
        Assert.Empty(d.To(A));

        // Changes of a second check within the window join the same message.
        await d.RecordChangesAsync(Url1, "mt-nl.ics");
        await d.AdvanceAndNotifyAsync(TimeSpan.FromSeconds(1));
        var m = Assert.Single(d.To(A));
        var both = (await d.OutcomesAsync("s1")).Where(o => o.Outcome != "hidden").Select(o => o.Id);
        Assert.Equal(both, DeliveryHost.Ids(m));

        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        await d.AdvanceAndNotifyAsync(d.Window - TimeSpan.FromSeconds(1));
        Assert.Single(d.To(A));
        await d.AdvanceAndNotifyAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, d.To(A).Count);
    }

    // US1-5: an edit of the filter before the window ends counts.
    [Theory, MemberData(nameof(Types))]
    public async Task FilterIsAppliedAtSendTime(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        var calendar = await d.SubscribeAsync("s1", Url1);
        var channel = await d.AddActiveChannelAsync("s1", A);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        var put = await d.PutAsync($"/api/subscribers/s1/calendars/{calendar}/filter", """{"rules":[{"outcome":"hide","when":{"changeKind":["name"]}}]}""");
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var outcomes = await d.OutcomesAsync("s1");
        var name = Assert.Single(outcomes, o => o.Outcome == "hidden").Id;

        await d.AdvanceAndNotifyAsync(d.Window);
        var m = Assert.Single(d.To(A));
        Assert.DoesNotContain(name, DeliveryHost.Ids(m));
        Assert.Equal(outcomes.Count - 1, DeliveryHost.Ids(m).Count);
        Assert.Equal(("skipped", "hidden"), (await d.DeliveryStatusesAsync(channel))[name]);
    }

    // SC-002, US1-6: an unconfirmed channel gets only its confirmation, and confirming later sends nothing earlier.
    [Theory, MemberData(nameof(Types))]
    public async Task OnlyTheConfirmationBeforeConsent(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        await d.SubscribeAsync("s1", Url1);
        var r = await d.PostAsync("/api/subscribers/s1/channels", new { type = d.C.Type, target = A });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        await d.AdvanceAndNotifyAsync(d.Window);
        var confirmation = Assert.Single(d.To(A));
        Assert.Equal(MessageKind.Confirmation, confirmation.Kind);
        Assert.StartsWith($"{EmailChannelTests.Public}/n/confirm/", confirmation.ConfirmUrl);
        Assert.StartsWith($"{EmailChannelTests.Public}/n/decline/", confirmation.DeclineUrl);
        Assert.Null(confirmation.UnsubscribeUrl);
        Assert.Equal(0, await d.Db.ScalarAsync("SELECT count(*) FROM delivery"));

        await d.ConfirmLatestAsync(A);
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.Single(d.To(A));
        await d.RecordChangesAsync(Url1, "mt-nl.ics");
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.Equal([MessageKind.Confirmation, MessageKind.Changes], d.To(A).Select(m => m.Kind));
    }

    // Subscribing after a change gets nothing for it; US1-7: subscribers of one shared calendar each get their own message.
    [Theory, MemberData(nameof(Types))]
    public async Task SharedCalendarAndLateSubscribers(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        var calendar = await d.SubscribeAsync("s1", Url1);
        await d.AddActiveChannelAsync("s1", A);
        await d.AddActiveChannelAsync("s2", B);
        await d.AddActiveChannelAsync("s3", "c@example.nl");
        Assert.Equal(calendar, await d.SubscribeAsync("s2", Url2));
        var put = await d.PutAsync($"/api/subscribers/s2/calendars/{calendar}/filter", """{"rules":[{"outcome":"hide","when":{"changeKind":["name"]}}]}""");
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        Assert.Equal(calendar, await d.SubscribeAsync("s3", MtLink(user: "3003")));

        await d.AdvanceAndNotifyAsync(d.Window);
        var (a, b) = (Assert.Single(d.To(A)), Assert.Single(d.To(B)));
        Assert.Empty(d.To("c@example.nl"));
        Assert.Equal((await d.OutcomesAsync("s1")).Where(o => o.Outcome != "hidden").Select(o => o.Id), DeliveryHost.Ids(a));
        Assert.Equal((await d.OutcomesAsync("s2")).Where(o => o.Outcome != "hidden").Select(o => o.Id), DeliveryHost.Ids(b));
        Assert.NotEqual(DeliveryHost.Ids(a), DeliveryHost.Ids(b));
        Assert.DoesNotContain(B, a.ToString());
        Assert.DoesNotContain(A, b.ToString());
        if (d.C is EmailUnderTest e)
            foreach (var mail in e.Mails)
            {
                Assert.Single(mail.Recipients);
                Assert.DoesNotContain(mail.Recipients[0] == A ? B : A, mail.Data);
            }
    }

    // US1-9: every active channel of the subscriber receives it.
    [Theory, MemberData(nameof(Types))]
    public async Task EveryActiveChannelReceives(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        await d.SubscribeAsync("s1", Url1);
        await d.AddActiveChannelAsync("s1", A);
        await d.AddActiveChannelAsync("s1", B);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.Equal(DeliveryHost.Ids(Assert.Single(d.To(A))), DeliveryHost.Ids(Assert.Single(d.To(B))));
    }

    // US1-10: a calendar is named by its label, else its feed name, else its host.
    [Theory, MemberData(nameof(Types))]
    public async Task CalendarNames(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        var labelled = await d.SubscribeAsync("s1", Url1);
        var feed = await d.SubscribeAsync("s1", MtLink(id: "ALET26VT4B"));
        var generic = await d.SubscribeAsync("s1", GenericUrl, "generic.ics");
        Assert.Equal(3, new[] { labelled, feed, generic }.Distinct().Count());
        Assert.Equal(HttpStatusCode.OK, (await d.PutAsync($"/api/subscribers/s1/calendars/{labelled}/notifications", """{"label":"Minor robotics"}""")).StatusCode);
        await d.AddActiveChannelAsync("s1", A);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        await d.RecordChangesAsync(MtLink(id: "ALET26VT4B"), "mt-nl-edited.ics");
        await d.RecordChangesAsync(GenericUrl, "generic-edited.ics");

        await d.AdvanceAndNotifyAsync(d.Window);
        var names = Assert.Single(d.To(A)).Calendars.ToDictionary(g => g.CalendarId, g => (g.Name, g.SettingsUrl));
        Assert.Equal(("Minor robotics", $"{EmailChannelTests.Frontend}/calendars/{labelled}/notifications"), names[labelled]);
        Assert.Equal("Hogeschool Inholland - Rooster - ALET26VT4A", names[feed].Name);
        Assert.Equal("calendar.example.com", names[generic].Name);
    }

    // US2-2, US2-5: a calendar switched off skips its pending changes on every channel; on again sends only later ones.
    [Theory, MemberData(nameof(Types))]
    public async Task NotificationSwitch(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        var x = await d.SubscribeAsync("s1", Url1);
        var y = await d.SubscribeAsync("s1", GenericUrl, "generic.ics");
        var (a, b) = (await d.AddActiveChannelAsync("s1", A), await d.AddActiveChannelAsync("s1", B));
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        await d.RecordChangesAsync(GenericUrl, "generic-edited.ics");
        var recorded = (await d.OutcomesAsync("s1")).Count;

        Assert.Equal(HttpStatusCode.OK, (await d.PutAsync($"/api/subscribers/s1/calendars/{x}/notifications", """{"notify":false}""")).StatusCode);
        foreach (var channel in new[] { a, b })
            Assert.All(await ChangesOfAsync(d, channel, x), s => Assert.Equal(("skipped", "notificationsOff"), s));
        await d.AdvanceAndNotifyAsync(d.Window);
        foreach (var target in new[] { A, B })
            Assert.Equal([y], Assert.Single(d.To(target)).Calendars.Select(g => g.CalendarId));

        // Changes recorded while off get no row; the change list still shows them.
        var rows = await d.Db.ScalarAsync("SELECT count(*) FROM delivery");
        await d.RecordChangesAsync(Url1, "mt-nl.ics");
        Assert.Equal(rows, await d.Db.ScalarAsync("SELECT count(*) FROM delivery"));
        Assert.True((await d.OutcomesAsync("s1")).Count > recorded);

        Assert.Equal(HttpStatusCode.OK, (await d.PutAsync($"/api/subscribers/s1/calendars/{x}/notifications", """{"notify":true}""")).StatusCode);
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.Single(d.To(A));
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        await d.AdvanceAndNotifyAsync(d.Window);
        var later = d.To(A)[1];
        Assert.Equal([x], later.Calendars.Select(g => g.CalendarId));
        Assert.All(DeliveryHost.Ids(later), id => Assert.True(id > rows));
    }

    static async Task<List<(string, string?)>> ChangesOfAsync(DeliveryHost d, long channel, long calendar) =>
        [.. (await d.Db.RowsAsync("""
            SELECT d.status, d.reason FROM delivery d JOIN detected_change c ON c.id = d.change_id WHERE d.channel_id = $1 AND c.calendar_id = $2
            """, channel, calendar)).Select(r => ((string)r[0]!, (string?)r[1]))];

    // US2-7: a pause skips the pending changes; after a resume only later changes are sent.
    [Theory, MemberData(nameof(Types))]
    public async Task PauseAndResume(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        await d.SubscribeAsync("s1", Url1);
        var channel = await d.AddActiveChannelAsync("s1", A);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        Assert.Equal(HttpStatusCode.OK, (await d.PostAsync($"/api/subscribers/s1/channels/{channel}/pause")).StatusCode);
        Assert.All((await d.DeliveryStatusesAsync(channel)).Values, s => Assert.Equal(("skipped", "channelNotActive"), s));
        await d.RecordChangesAsync(Url1, "mt-nl.ics");
        var before = await d.Db.ScalarAsync("SELECT max(id) FROM detected_change");
        Assert.Equal(HttpStatusCode.OK, (await d.PostAsync($"/api/subscribers/s1/channels/{channel}/resume")).StatusCode);
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.Empty(d.To(A));
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.All(DeliveryHost.Ids(Assert.Single(d.To(A))), id => Assert.True(id > before));
    }

    // US2-8: removing the channel deletes it and its deliveries.
    [Theory, MemberData(nameof(Types))]
    public async Task RemoveChannel(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        await d.SubscribeAsync("s1", Url1);
        var channel = await d.AddActiveChannelAsync("s1", A);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        Assert.Equal(HttpStatusCode.NoContent, (await d.H.Client.DeleteAsync($"/api/subscribers/s1/channels/{channel}", Ct)).StatusCode);
        Assert.Equal(0, await d.Db.ScalarAsync("SELECT count(*) FROM delivery"));
        Assert.Equal(0, await d.Db.ScalarAsync("SELECT count(*) FROM channel"));
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.Empty(d.C.Delivered);
    }

    // US2-3, US2-6: a one-click unsubscribe stops sending; a new confirmation resumes it with no backfill.
    [Theory, MemberData(nameof(Types))]
    public async Task UnsubscribeAndConfirmAgain(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        await d.SubscribeAsync("s1", Url1);
        var channel = await d.AddConfirmedChannelAsync("s1", A);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        var token = await d.TextAsync("SELECT unsubscribe_token FROM channel WHERE id = $1", channel);
        var r = await d.PostPageAsync($"{EmailChannelTests.Public}/n/unsubscribe/{token}",
            new FormUrlEncodedContent([new("List-Unsubscribe", "One-Click")]));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("unsubscribed", await d.TextAsync("SELECT status FROM channel WHERE id = $1", channel));
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.Equal([MessageKind.Confirmation], d.To(A).Select(m => m.Kind));

        Assert.Equal(HttpStatusCode.Accepted, (await d.PostAsync($"/api/subscribers/s1/channels/{channel}/confirmation")).StatusCode);
        await d.ConfirmLatestAsync(A);
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.Equal([MessageKind.Confirmation, MessageKind.Confirmation], d.To(A).Select(m => m.Kind));
        await d.RecordChangesAsync(Url1, "mt-nl.ics");
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.Equal(MessageKind.Changes, d.To(A)[^1].Kind);
    }

    // Edge case: removing the subscription skips its pending changes.
    [Theory, MemberData(nameof(Types))]
    public async Task RemovingTheSubscriptionSkips(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        var calendar = await d.SubscribeAsync("s1", Url1);
        await d.SubscribeAsync("s2", Url2); // keeps the calendar
        var channel = await d.AddActiveChannelAsync("s1", A);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        Assert.Equal(HttpStatusCode.NoContent, (await d.H.Client.DeleteAsync($"/api/subscribers/s1/calendars/{calendar}", Ct)).StatusCode);
        Assert.All((await d.DeliveryStatusesAsync(channel)).Values, s => Assert.Equal(("skipped", "subscriptionRemoved"), s));
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.Empty(d.C.Delivered);
    }

    // FR-011, SC-003: the send-time re-check catches a state change that bypassed the skip.
    [Theory, MemberData(nameof(Types))]
    public async Task SendTimeRecheck(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        var calendar = await d.SubscribeAsync("s1", Url1);
        var paused = await d.AddActiveChannelAsync("s1", A);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        await d.Db.ExecAsync("UPDATE channel SET status = 'paused' WHERE id = $1", paused);
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.Empty(d.C.Delivered);
        Assert.All((await d.DeliveryStatusesAsync(paused)).Values, s => Assert.Equal(("skipped", "channelNotActive"), s));

        var off = await d.AddActiveChannelAsync("s1", B);
        await d.RecordChangesAsync(Url1, "mt-nl.ics");
        await d.Db.ExecAsync("INSERT INTO subscription_setting (calendar_id, subscriber, notify) VALUES ($1, 's1', false)", calendar);
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.Empty(d.C.Delivered);
        Assert.All((await d.DeliveryStatusesAsync(off)).Values, s => Assert.Equal(("skipped", "notificationsOff"), s));
    }

    // US3-1, US3-3: language and time zone are read when the message is sent.
    [Theory, MemberData(nameof(Types))]
    public async Task LanguageAndTimeZoneAtSendTime(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        await d.SubscribeAsync("s1", Url1);
        await d.SubscribeAsync("s2", Url2);
        await d.AddActiveChannelAsync("s1", A);
        await d.AddActiveChannelAsync("s2", B);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        Assert.Equal(HttpStatusCode.OK, (await d.PutAsync("/api/subscribers/s2/notification-settings", """{"language":"nl","timeZone":"America/New_York"}""")).StatusCode);
        await d.AdvanceAndNotifyAsync(d.Window);
        var (a, b) = (Assert.Single(d.To(A)), Assert.Single(d.To(B)));
        Assert.Equal(("en", "Europe/Amsterdam"), (a.Language, a.TimeZone.Id));
        Assert.Equal(("nl", "America/New_York"), (b.Language, b.TimeZone.Id));

        var r = await d.PostAsync("/api/subscribers/s2/channels", new { type = d.C.Type, target = "c@example.nl" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        await d.NotifyAsync(3);
        Assert.Equal("nl", Assert.Single(d.To("c@example.nl")).Language);
        Assert.Equal("nl", await d.TextAsync("SELECT language FROM channel_message WHERE sent_at IS NOT NULL"));
    }

    // Send attempt times, from the recorder.
    static List<DateTimeOffset> Attempts(DeliveryHost d)
    {
        var times = new List<DateTimeOffset>();
        d.C.Recorder.Before.Add((_, _) =>
        {
            lock (times) times.Add(d.Now);
            return Task.CompletedTask;
        });
        return times;
    }

    // US4-1, US4-6, SC-005: an outage fails no channel, backs the type off, and then delivers once.
    [Theory, MemberData(nameof(Types))]
    public async Task OutageBacksOffTheType(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        await d.SubscribeAsync("s1", Url1);
        var channels = new List<long>();
        foreach (var t in new[] { A, B, "c@example.nl" }) channels.Add(await d.AddActiveChannelAsync("s1", t));
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        var times = Attempts(d);
        d.C.Outage = true;
        d.H.Time.Advance(d.Window);
        for (var end = d.Now + TimeSpan.FromMinutes(30); d.Now < end; d.H.Time.Advance(TimeSpan.FromSeconds(1)))
            await d.Notifier.RunDueAsync(d.Now, Ct);

        Assert.Empty(d.C.Delivered);
        Assert.All(await d.Db.RowsAsync("SELECT status FROM channel"), r => Assert.Equal("active", r[0]));
        // Each attempt counts once, for the channel that hit it; the type waits 1, 2, 4, 8 minutes in between (attempts at 0, 1, 3, 7, 15).
        Assert.Equal(times.Count, await d.Db.ScalarAsync("SELECT sum(failed_attempts) FROM channel"));
        var gaps = times.Zip(times.Skip(1), (x, y) => y - x).ToList();
        Assert.Equal(4, gaps.Count);
        for (var i = 0; i < gaps.Count; i++)
            Assert.InRange(gaps[i], TimeSpan.FromMinutes(1 << i), TimeSpan.FromMinutes(1 << i) + TimeSpan.FromSeconds(1));

        d.C.Outage = false;
        await d.AdvanceAndNotifyAsync(TimeSpan.FromMinutes(35));
        var visible = (await d.OutcomesAsync("s1")).Where(o => o.Outcome != "hidden").Select(o => o.Id).ToList();
        foreach (var t in new[] { A, B, "c@example.nl" }) Assert.Equal(visible, DeliveryHost.Ids(Assert.Single(d.To(t))));
        Assert.All(await d.Db.RowsAsync("SELECT failed_attempts, next_attempt_at FROM channel"), r => Assert.Equal([0, null], r));
    }

    // Research R4: a temporary failure for one target backs off only that channel.
    [Theory, MemberData(nameof(Types))]
    public async Task ChannelBackoff(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        await d.SubscribeAsync("s1", Url1);
        var a = await d.AddActiveChannelAsync("s1", A);
        await d.AddActiveChannelAsync("s1", B);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        d.C.FailTemporarily(A, 2);
        async Task<(int, DateTimeOffset?)> BackoffAsync()
        {
            var r = (await d.Db.RowsAsync("SELECT failed_attempts, next_attempt_at FROM channel WHERE id = $1", a)).Single();
            return ((int)r[0]!, r[1] is DateTime t ? new DateTimeOffset(t) : null);
        }

        d.H.Time.Advance(d.Window);
        var first = d.Now;
        await d.NotifyAsync();
        Assert.Single(d.To(B));
        Assert.Empty(d.To(A));
        Assert.Equal((1, first + TimeSpan.FromMinutes(1)), await BackoffAsync());
        d.H.Time.Advance(first + TimeSpan.FromMinutes(1) - d.Now);
        var second = d.Now;
        await d.NotifyAsync();
        Assert.Equal((2, second + TimeSpan.FromMinutes(2)), await BackoffAsync());
        await d.AdvanceAndNotifyAsync(TimeSpan.FromMinutes(2));
        Assert.Single(d.To(A));
        Assert.Equal((0, null), await BackoffAsync());
    }

    // Research R4: one message that always fails type-wide must not stall the type.
    [Theory, MemberData(nameof(Types))]
    public async Task BadMessageDoesNotStallTheType(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        await d.SubscribeAsync("s1", Url1);
        await d.SubscribeAsync("s2", MtLink(id: "ALET26VT4B", user: "2002"));
        var a = await d.AddActiveChannelAsync("s1", A);
        await d.AddActiveChannelAsync("s2", B);
        await d.AddActiveChannelAsync("s2", "c@example.nl");
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics"); // A's changes are the oldest due
        await d.RecordChangesAsync(MtLink(id: "ALET26VT4B", user: "2002"), "mt-nl-edited.ics");
        d.C.PoisonMessage(A);
        d.H.Time.Advance(d.Window);
        for (var end = d.Now + TimeSpan.FromMinutes(3); d.Now < end; d.H.Time.Advance(TimeSpan.FromSeconds(1)))
            await d.Notifier.RunDueAsync(d.Now, Ct);
        Assert.Single(d.To(B));
        Assert.Single(d.To("c@example.nl"));
        Assert.Empty(d.To(A));
        Assert.Equal("active", await d.TextAsync("SELECT status FROM channel WHERE id = $1", a));

        await d.AdvanceAndNotifyAsync(TimeSpan.FromDays(1));
        Assert.All((await d.DeliveryStatusesAsync(a)).Values, s => Assert.NotEqual("pending", s.Status));
        Assert.Contains(("expired", null), (await d.DeliveryStatusesAsync(a)).Values);
    }

    // US4-2: pending changes older than the retry window expire and are never sent.
    [Theory, MemberData(nameof(Types))]
    public async Task ExpiryAfterTheRetryWindow(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        await d.SubscribeAsync("s1", Url1);
        var a = await d.AddActiveChannelAsync("s1", A);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        d.C.Outage = true;
        for (var end = d.Now + TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1); d.Now < end; d.H.Time.Advance(TimeSpan.FromMinutes(10)))
            await d.Notifier.RunDueAsync(d.Now, Ct);
        await d.NotifyAsync(1);
        Assert.All((await d.DeliveryStatusesAsync(a)).Values, s => Assert.Equal(("expired", null), s));
        d.C.Outage = false;
        await d.AdvanceAndNotifyAsync(TimeSpan.FromHours(1));
        Assert.Empty(d.C.Delivered);
    }

    // Edge case "Channel type configured off": pending changes still expire.
    [Fact]
    public async Task UnconfiguredTypeStillExpires()
    {
        await using var d = await DeliveryHost.CreateAsync("test");
        await d.SubscribeAsync("s1", Url1);
        var a = await d.AddActiveChannelAsync("s1", A);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        ((TestTypeUnderTest)d.C).Channel.Configured = false;
        await d.AdvanceAndNotifyAsync(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        Assert.All((await d.DeliveryStatusesAsync(a)).Values, s => Assert.Equal(("expired", null), s));
        Assert.Empty(d.C.Delivered);
    }

    // US4-3, US4-4: a permanent rejection fails only its channel; confirming again sends nothing from while it was failing.
    [Theory, MemberData(nameof(Types))]
    public async Task PermanentRejection(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        await d.SubscribeAsync("s1", Url1);
        var a = await d.AddActiveChannelAsync("s1", A);
        await d.AddActiveChannelAsync("s1", B);
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        Assert.Equal(HttpStatusCode.Accepted, (await d.PostAsync($"/api/subscribers/s1/channels/{a}/test")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await d.PostAsync($"/api/subscribers/s1/channels/{a}/test")).StatusCode);
        d.C.RejectPermanently(A);
        await d.AdvanceAndNotifyAsync(d.Window);

        Assert.Equal([["failing", type == "email" ? "smtp_550" : "scripted"]], await d.Db.RowsAsync("SELECT status, failure_reason FROM channel WHERE id = $1", a));
        Assert.All((await d.DeliveryStatusesAsync(a)).Values, s => Assert.Equal(("expired", null), s));
        Assert.Equal(0, await d.Db.ScalarAsync("SELECT count(*) FROM channel_message WHERE sent_at IS NULL"));
        Assert.Empty(d.To(A));
        Assert.Single(d.To(B));

        // US4-4: changes recorded while failing are never sent.
        await d.RecordChangesAsync(Url1, "mt-nl.ics");
        var before = await d.Db.ScalarAsync("SELECT max(id) FROM detected_change");
        d.C.FailTemporarily(A, 0); // the address works again
        Assert.Equal(HttpStatusCode.Accepted, (await d.PostAsync($"/api/subscribers/s1/channels/{a}/confirmation")).StatusCode);
        await d.ConfirmLatestAsync(A);
        Assert.Equal("active", await d.TextAsync("SELECT status FROM channel WHERE id = $1", a));
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.Equal([MessageKind.Confirmation], d.To(A).Select(m => m.Kind));
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.All(DeliveryHost.Ids(d.To(A)[^1]), id => Assert.True(id > before));
    }

    // US4-5, SC-004: a pass cancelled mid-way and a fresh notifier never send a change twice.
    [Theory, MemberData(nameof(Types))]
    public async Task RestartNeverDuplicates(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        await d.SubscribeAsync("s1", Url1);
        string[] targets = [A, B, "c@example.nl"];
        foreach (var t in targets) await d.AddActiveChannelAsync("s1", t);
        CancellationTokenSource? cancel = null;
        var sends = 0;
        d.C.Recorder.Before.Add((_, ct) =>
        {
            // Cancel before the second channel's inner send, so the test never lands in FR-025's accepted-then-crash window.
            if (cancel is not null && ++sends == 2) cancel.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        foreach (var fixture in new[] { "mt-nl-edited.ics", "mt-nl.ics", "mt-nl-edited.ics" })
        {
            await d.RecordChangesAsync(Url1, fixture);
            d.H.Time.Advance(d.Window);
            (cancel, sends) = (new CancellationTokenSource(), 0);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    await d.Notifier.RunDueAsync(d.Now, cancel.Token);
                    d.H.Time.Advance(TimeSpan.FromMilliseconds(1));
                }
            });
            cancel = null;
            var fresh = ActivatorUtilities.CreateInstance<Notifier>(d.H.Factory.Services);
            for (var i = 0; i < 10; i++)
            {
                await fresh.RunDueAsync(d.Now, Ct);
                d.H.Time.Advance(TimeSpan.FromMilliseconds(1));
            }
        }
        var visible = (await d.OutcomesAsync("s1")).Where(o => o.Outcome != "hidden").Select(o => o.Id).Order().ToList();
        foreach (var t in targets)
        {
            var ids = d.To(t).SelectMany(DeliveryHost.Ids).ToList();
            Assert.Equal(visible, ids.Order());
            Assert.Equal(3, d.To(t).Count);
        }
    }

    // US5-2, FR-015: at most 50 listed; all of them count as sent.
    [Theory, MemberData(nameof(Types))]
    public async Task FiftyListed(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        var calendar = await d.SubscribeAsync("s1", Url1);
        Assert.Equal(HttpStatusCode.OK, (await d.PutAsync($"/api/subscribers/s1/calendars/{calendar}/filter", """{"rules":[]}""")).StatusCode);
        var a = await d.AddActiveChannelAsync("s1", A);
        for (var i = 0; i < 8; i++) await d.RecordChangesAsync(Url1, i % 2 == 0 ? "mt-nl-edited.ics" : "mt-nl.ics");
        var all = (await d.OutcomesAsync("s1")).Select(o => o.Id).ToList();
        Assert.True(all.Count > 50);
        await d.AdvanceAndNotifyAsync(d.Window);
        var m = Assert.Single(d.To(A));
        Assert.Equal(all.Take(50), DeliveryHost.Ids(m));
        Assert.Equal(all.Count - 50, m.NotListed);
        Assert.All((await d.DeliveryStatusesAsync(a)).Values, s => Assert.Equal(("sent", null), s));
    }

    // US5-3, FR-016: never more than the configured messages per minute; the rest wait. Queued messages go first.
    [Theory, MemberData(nameof(Types))]
    public async Task RateLimit(string type)
    {
        var under = await DeliveryHost.UnderAsync(type);
        under.MaxMessagesPerMinute = 6;
        await using var d = await DeliveryHost.CreateAsync([under]);
        for (var s = 1; s <= 4; s++)
        {
            await d.SubscribeAsync($"s{s}", MtLink(user: $"{s}00{s}"));
            for (var c = 0; c < 5; c++) await d.AddActiveChannelAsync($"s{s}", $"{s}.{c}@example.nl");
        }
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        var times = Attempts(d);
        d.H.Time.Advance(d.Window);
        var r = await d.PostAsync("/api/subscribers/s5/channels", new { type = d.C.Type, target = "late@example.nl" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);

        var start = d.Now;
        for (; d.Now < start + TimeSpan.FromSeconds(60); d.H.Time.Advance(TimeSpan.FromSeconds(1))) await d.Notifier.RunDueAsync(d.Now, Ct);
        Assert.InRange(times.Count, 5, 6);
        Assert.Equal(MessageKind.Confirmation, d.C.Delivered[0].Message.Kind);
        Assert.True(await d.Db.ScalarAsync("SELECT count(*) FROM delivery WHERE status = 'pending'") > 0);

        for (; d.Now < start + TimeSpan.FromMinutes(5); d.H.Time.Advance(TimeSpan.FromSeconds(1))) await d.Notifier.RunDueAsync(d.Now, Ct);
        Assert.Equal(21, d.C.Delivered.Count);
        foreach (var t in times) Assert.True(times.Count(x => x >= t && x < t + TimeSpan.FromMinutes(1)) <= 6);
    }

    // SC-007, research R1: 1,000 subscribers × 300 changes. Run explicitly; record the fan-out time in tasks.md T059.
    [Fact(Explicit = true)]
    public async Task MassChange()
    {
        var under = new TestTypeUnderTest();
        under.MaxMessagesPerMinute = 60;
        await using var d = await DeliveryHost.CreateAsync([under]);
        static string Feed(string suffix) =>
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//mass//EN\r\n" + string.Concat(Enumerable.Range(0, 300).Select(i =>
                $"BEGIN:VEVENT\r\nUID:mass{i}@test\r\nDTSTAMP:20260901T000000Z\r\nDTSTART:2026110{1 + i % 9}T{8 + i % 8:00}0000Z\r\n" +
                $"DTEND:2026110{1 + i % 9}T{9 + i % 8:00}0000Z\r\nSUMMARY:Lesson {i}{suffix}\r\nEND:VEVENT\r\n")) + "END:VCALENDAR\r\n";
        var (before, after) = (Path.GetTempFileName(), Path.GetTempFileName());
        await File.WriteAllTextAsync(before, Feed(""), Ct);
        await File.WriteAllTextAsync(after, Feed(" (moved)"), Ct);
        try
        {
            var url = "https://mass.example.com/feed.ics";
            await d.SubscribeAsync("s0", url, before);
            var calendar = await d.Db.ScalarAsync("SELECT calendar_id FROM calendar_link");
            await d.Db.ExecAsync("""
                INSERT INTO calendar_link (calendar_id, subscriber, url) SELECT $1, 's' || g, 'https://mass.example.com/feed.ics?' || g FROM generate_series(1, 999) g
                """, calendar);
            await d.Db.ExecAsync("""
                INSERT INTO channel (subscriber, type, target, status, status_since, activated_at, unsubscribe_token, created_at)
                SELECT 's' || g, 'test', g || '@example.nl', 'active', $1, $1, lpad(g::text, 43, 't'), $1 FROM generate_series(0, 999) g
                """, d.Now);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            await d.RecordChangesAsync(url, after);
            TestContext.Current.TestOutputHelper!.WriteLine($"MassChange: the check with the fan-out took {watch.ElapsedMilliseconds} ms");
            Assert.Equal(300_000, await d.Db.ScalarAsync("SELECT count(*) FROM delivery"));

            var times = Attempts(d);
            d.H.Time.Advance(d.Window);
            watch.Restart();
            for (var i = 0; i < 1100 && d.C.Delivered.Count < 1000; i++, d.H.Time.Advance(TimeSpan.FromSeconds(1)))
                await d.Notifier.RunDueAsync(d.Now, Ct);
            TestContext.Current.TestOutputHelper!.WriteLine($"MassChange: 1,000 digests took {watch.ElapsedMilliseconds} ms");
            Assert.Equal(1000, d.C.Delivered.Count);
            Assert.All(d.C.Delivered, m => Assert.Equal((50, 250), (DeliveryHost.Ids(m.Message).Count, m.Message.NotListed)));
            for (var i = 60; i < times.Count; i++) Assert.True(times[i] - times[i - 60] >= TimeSpan.FromMinutes(1));
        }
        finally
        {
            File.Delete(before);
            File.Delete(after);
        }
    }

    // US6-1, US6-2: email and the test type side by side behave the same, and fail independently.
    [Fact]
    public async Task EmailAndTestTypeSideBySide()
    {
        var (email, test) = (await EmailUnderTest.CreateAsync(), new TestTypeUnderTest());
        await using var d = await DeliveryHost.CreateAsync([email, test]);
        await d.SubscribeAsync("s1", Url1);
        var e = await d.AddActiveChannelAsync("s1", A, "email");
        var t = await d.AddActiveChannelAsync("s1", B, "test");
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        await d.AdvanceAndNotifyAsync(d.Window);
        var (em, tm) = (Assert.Single(email.Recorder.Delivered).Message, Assert.Single(test.Recorder.Delivered).Message);
        Assert.Equal(em.Calendars.Select(g => (g.CalendarId, g.Name, g.SettingsUrl)), tm.Calendars.Select(g => (g.CalendarId, g.Name, g.SettingsUrl)));
        Assert.Equal(DeliveryHost.Ids(em), DeliveryHost.Ids(tm));
        Assert.Equal((em.Language, em.NotListed), (tm.Language, tm.NotListed));

        // The same channel backoff as email's (ChannelBackoff), then the same failing status; email keeps receiving.
        test.FailTemporarily(B, 2);
        await d.RecordChangesAsync(Url1, "mt-nl.ics");
        d.H.Time.Advance(d.Window);
        var first = d.Now;
        await d.NotifyAsync();
        Assert.Equal(2, email.Recorder.Delivered.Count);
        Assert.Equal([[1, (first + TimeSpan.FromMinutes(1)).UtcDateTime]], await d.Db.RowsAsync("SELECT failed_attempts, next_attempt_at FROM channel WHERE id = $1", t));
        test.RejectPermanently(B);
        await d.AdvanceAndNotifyAsync(TimeSpan.FromMinutes(1));
        Assert.Equal([["failing", "scripted"]], await d.Db.RowsAsync("SELECT status, failure_reason FROM channel WHERE id = $1", t));
        Assert.Equal("active", await d.TextAsync("SELECT status FROM channel WHERE id = $1", e));

        // An outage of the test type does not back off email.
        test.Outage = true;
        var u = await d.AddActiveChannelAsync("s1", "c@example.nl", "test");
        await d.RecordChangesAsync(Url1, "mt-nl-edited.ics");
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.Equal(3, email.Recorder.Delivered.Count);
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT failed_attempts FROM channel WHERE id = $1", u));
    }

    // US7-1: a test message; a pause before it is sent drops it.
    [Theory, MemberData(nameof(Types))]
    public async Task TestMessage(string type)
    {
        await using var d = await DeliveryHost.CreateAsync(type);
        var a = await d.AddActiveChannelAsync("s1", A);
        Assert.Equal(HttpStatusCode.OK, (await d.PutAsync("/api/subscribers/s1/notification-settings", """{"language":"nl"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await d.PostAsync($"/api/subscribers/s1/channels/{a}/test")).StatusCode);
        await d.NotifyAsync(3);
        var m = Assert.Single(d.To(A));
        Assert.Equal((MessageKind.Test, "nl", $"{EmailChannelTests.Frontend}/notifications"), (m.Kind, m.Language, m.SettingsUrl));
        Assert.StartsWith($"{EmailChannelTests.Public}/n/unsubscribe/", m.UnsubscribeUrl);
        Assert.Empty(m.Calendars);
        Assert.Equal([["nl"]], await d.Db.RowsAsync("SELECT language FROM channel_message WHERE kind = 'test' AND sent_at IS NOT NULL"));
        await d.NotifyAsync(3);
        Assert.Single(d.To(A));

        Assert.Equal(HttpStatusCode.Accepted, (await d.PostAsync($"/api/subscribers/s1/channels/{a}/test")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await d.PostAsync($"/api/subscribers/s1/channels/{a}/pause")).StatusCode);
        await d.NotifyAsync(3);
        Assert.Single(d.To(A));
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM channel_message"));
    }
}
