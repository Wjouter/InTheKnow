using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace InTheKnow.Tests;

// Channels built directly on a test database, with a RecordingChannel and an EmailChannel.
public class ChannelsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Fixture : IAsyncDisposable
    {
        public required TestDb Db { get; init; }
        public required (long S1, long S2, long S3) Accounts { get; init; }
        public long S1 => Accounts.S1;
        public long S2 => Accounts.S2;
        public long S3 => Accounts.S3;
        public FakeTimeProvider Time { get; } = new(Harness.T0);
        public RecordingChannel Test { get; } = new();
        public required Settings Settings { get; init; }
        public Channels Channels => new(Db.DataSource, [Test, new EmailChannel(Settings)], Settings, Time);

        public static async Task<Fixture> CreateAsync()
        {
            var db = await TestDb.CreateAsync();
            return new() { Db = db, Accounts = await TestHost.AccountsAsync(db.DataSource), Settings = db.Settings(EmailChannelTests.EmailSettings(25)) };
        }

        public async Task<long> AddAsync(long? subscriber = null, string target = "a@example.nl", string type = "test")
        {
            var r = await Channels.AddAsync(subscriber ?? S1, type, target, Ct);
            Assert.Null(r.Error);
            return r.Channel!.Id;
        }

        // What the notifier does when it sends the oldest queued confirmation: returns its token.
        public async Task<string> SendConfirmationAsync(long channel, string language = "en")
        {
            var token = Channels.NewToken();
            await Db.ExecAsync("""
                UPDATE channel_message SET sent_at = $2, language = $3, token_hash = $4, expires_at = $5
                WHERE id = (SELECT min(id) FROM channel_message WHERE channel_id = $1 AND kind = 'confirmation' AND sent_at IS NULL)
                """, channel, Time.GetUtcNow(), language, Channels.Hash(token), Time.GetUtcNow() + TimeSpan.FromHours(48));
            return token;
        }

        public async Task<string> StatusAsync(long channel) => (string)(await Db.RowsAsync("SELECT status FROM channel WHERE id = $1", channel)).Single()[0]!;

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task AddValidatesAndStoresNothingOnRejection()
    {
        await using var f = await Fixture.CreateAsync();
        async Task Rejected(string reason, string type, string target, long? subscriber = null) =>
            Assert.Equal(reason, (await f.Channels.AddAsync(subscriber ?? f.S1, type, target, Ct)).Error);

        await Rejected("invalid_type", "fax", "a@example.nl");
        foreach (var bad in new[] { "a", "Name <a@b.nl>", new string('a', 244) + "@example.nl", "   " })
            await Rejected("invalid_target", "email", bad);
        Assert.Equal(0, await f.Db.ScalarAsync("SELECT count(*) FROM channel"));

        var added = await f.Channels.AddAsync(f.S1, "email", "  a@example.nl ", Ct);
        var c = added.Channel!;
        Assert.Equal(("a@example.nl", "awaitingConfirmation", "email"), (c.Target, c.Status, c.Type));
        var token = (string)(await f.Db.RowsAsync("SELECT unsubscribe_token FROM channel WHERE id = $1", c.Id)).Single()[0]!;
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        Assert.Equal([["confirmation", null]], await f.Db.RowsAsync("SELECT kind, sent_at FROM channel_message WHERE channel_id = $1", c.Id));

        await Rejected("target_taken", "email", "A@Example.NL");
        Assert.Null((await f.Channels.AddAsync(f.S2, "email", "a@example.nl", Ct)).Error); // another subscriber may
        for (var i = 0; i < 4; i++) await f.AddAsync(f.S1, $"{i}@example.nl");
        await Rejected("channel_limit", "test", "x@example.nl");
        await Rejected("target_taken", "email", "a@example.nl"); // the unique index fails before the limit
        Assert.Equal(5, await f.Db.ScalarAsync("SELECT count(*) FROM channel WHERE subscriber = $1", f.S1));
        Assert.Equal(6, await f.Db.ScalarAsync("SELECT count(*) FROM channel_message"));
    }

    [Fact]
    public async Task UnconfiguredTypeIsUnavailable()
    {
        await using var f = await Fixture.CreateAsync();
        f.Test.Configured = false;
        Assert.Equal("type_unavailable", (await f.Channels.AddAsync(f.S1, "test", "a@example.nl", Ct)).Error);
    }

    [Fact]
    public async Task Confirm()
    {
        await using var f = await Fixture.CreateAsync();
        var channel = await f.AddAsync();
        var token = await f.SendConfirmationAsync(channel);

        Assert.Equal(new PageResult(PageOutcome.NotFound, "en"), await f.Channels.ConfirmAsync(Channels.NewToken(), Ct));
        Assert.Equal(PageOutcome.NotFound, (await f.Channels.ConfirmAsync("short", Ct)).Outcome);
        f.Test.Configured = false;
        Assert.Equal(PageOutcome.Unavailable, (await f.Channels.ConfirmAsync(token, Ct)).Outcome);
        Assert.Equal("awaitingConfirmation", await f.StatusAsync(channel));
        f.Test.Configured = true;

        Assert.Equal(new PageResult(PageOutcome.Done, "en"), await f.Channels.ConfirmAsync(token, Ct));
        Assert.Equal([["active", Harness.T0.UtcDateTime]], await f.Db.RowsAsync("SELECT status, activated_at FROM channel WHERE id = $1", channel));
        Assert.Equal(1, await f.Db.ScalarAsync("SELECT count(*) FROM channel_message WHERE used_at IS NOT NULL"));
        Assert.Equal(PageOutcome.AlreadyDone, (await f.Channels.ConfirmAsync(token, Ct)).Outcome);
    }

    [Fact]
    public async Task ConfirmationExpiresAfter48Hours()
    {
        await using var f = await Fixture.CreateAsync();
        var channel = await f.AddAsync();
        var token = await f.SendConfirmationAsync(channel, "nl");
        f.Time.Advance(TimeSpan.FromHours(48) + TimeSpan.FromSeconds(1));
        Assert.Equal(new PageResult(PageOutcome.Expired, "nl"), await f.Channels.ConfirmAsync(token, Ct));
        Assert.Equal("awaitingConfirmation", await f.StatusAsync(channel));
    }

    [Fact]
    public async Task LabelsAndNames()
    {
        await using var f = await Fixture.CreateAsync();
        var (calendar, _) = await f.Db.SeedCalendarAsync("a.example", ["https://a.example/1"], f.S1);
        async Task<SubscriptionNotifications?> Get() => await f.Channels.GetSubscriptionAsync(f.S1, calendar, Ct);
        Task<(SubscriptionNotifications? Value, string? Error)> Set(string json) =>
            f.Channels.SetSubscriptionAsync(f.S1, calendar, JsonNode.Parse(json)!.AsObject(), Ct);

        Assert.Equal(new SubscriptionNotifications(true, null, "a.example"), await Get());
        await f.Db.ExecAsync("UPDATE calendar SET feed_name = 'Feed' WHERE id = $1", calendar);
        Assert.Equal("Feed", (await Get())!.Name);
        Assert.Equal(new SubscriptionNotifications(true, "Mine", "Mine"), (await Set("""{"label":"Mine"}""")).Value);
        foreach (var bad in new[] { "\"\"", $"\"{new string('x', 101)}\"", "\"a\\nb\"", "5" })
            Assert.Equal("invalid_label", (await Set($$"""{"label":{{bad}}}""")).Error);
        Assert.Equal("Mine", (await Get())!.Label);
        Assert.Equal(new string('x', 100), (await Set($$"""{"label":"{{new string('x', 100)}}"}""")).Value!.Label);
        Assert.Equal(new SubscriptionNotifications(true, null, "Feed"), (await Set("""{"label":null}""")).Value);
        Assert.Equal("empty_body", (await Set("{}")).Error);
        Assert.Equal("invalid_notify", (await Set("""{"notify":"yes"}""")).Error);
        Assert.Equal(new SubscriptionNotifications(false, null, "Feed"), (await Set("""{"notify":false}""")).Value);
        Assert.Equal("not_found", (await f.Channels.SetSubscriptionAsync(f.S2, calendar, new JsonObject { ["label"] = "x" }, Ct)).Error);
        Assert.Null(await f.Channels.GetSubscriptionAsync(f.S2, calendar, Ct));
    }

    // US2: pause, resume, remove, request a confirmation.
    [Fact]
    public async Task Lifecycle()
    {
        await using var f = await Fixture.CreateAsync();
        var channel = await f.AddAsync();
        var c = f.Channels;
        Assert.Equal("invalid_status", (await c.PauseAsync(f.S1, channel, Ct)).Error);
        Assert.Equal("invalid_status", (await c.ResumeAsync(f.S1, channel, Ct)).Error);
        await c.ConfirmAsync(await f.SendConfirmationAsync(channel), Ct);
        Assert.Equal("invalid_status", (await c.RequestConfirmationAsync(f.S1, channel, Ct)).Error);
        Assert.Equal("paused", (await c.PauseAsync(f.S1, channel, Ct)).Channel!.Status);
        Assert.Equal("invalid_status", (await c.RequestConfirmationAsync(f.S1, channel, Ct)).Error);
        Assert.Equal("invalid_status", (await c.PauseAsync(f.S1, channel, Ct)).Error);
        Assert.Equal("active", (await c.ResumeAsync(f.S1, channel, Ct)).Channel!.Status);
        Assert.Equal("not_found", (await c.PauseAsync(f.S2, channel, Ct)).Error);
        Assert.Equal("not_found", (await c.RemoveAsync(f.S2, channel, Ct)).Error);
        Assert.Null((await c.RemoveAsync(f.S1, channel, Ct)).Error);
        Assert.Equal(0, await f.Db.ScalarAsync("SELECT count(*) FROM channel"));
        Assert.Equal(0, await f.Db.ScalarAsync("SELECT count(*) FROM channel_message"));
    }

    // FR-006, research R10: 3 confirmations per 24 h, the initial one included.
    [Fact]
    public async Task ConfirmationLimit()
    {
        await using var f = await Fixture.CreateAsync();
        var channel = await f.AddAsync();
        Assert.Null((await f.Channels.RequestConfirmationAsync(f.S1, channel, Ct)).Error);
        f.Time.Advance(TimeSpan.FromHours(1));
        Assert.Null((await f.Channels.RequestConfirmationAsync(f.S1, channel, Ct)).Error);
        Assert.Equal("confirmation_limit", (await f.Channels.RequestConfirmationAsync(f.S1, channel, Ct)).Error);
        f.Time.Advance(TimeSpan.FromHours(23) + TimeSpan.FromSeconds(1));
        Assert.Null((await f.Channels.RequestConfirmationAsync(f.S1, channel, Ct)).Error);
        f.Test.Configured = false;
        f.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal("type_unavailable", (await f.Channels.RequestConfirmationAsync(f.S1, channel, Ct)).Error);
    }

    // FR-006: "this wasn't me".
    [Fact]
    public async Task Decline()
    {
        await using var f = await Fixture.CreateAsync();
        var channel = await f.AddAsync(target: "A@example.nl");
        var old = await f.SendConfirmationAsync(channel);
        Assert.Null((await f.Channels.RequestConfirmationAsync(f.S1, channel, Ct)).Error);
        var token = await f.SendConfirmationAsync(channel);
        Assert.Null((await f.Channels.RequestConfirmationAsync(f.S1, channel, Ct)).Error); // queued, then declined

        Assert.Equal(PageOutcome.Done, (await f.Channels.DeclineAsync(token, Ct)).Outcome);
        Assert.Equal("unsubscribed", await f.StatusAsync(channel));
        Assert.Equal(1, await f.Db.ScalarAsync("SELECT count(*) FROM declined_address"));
        Assert.Equal(0, await f.Db.ScalarAsync("SELECT count(*) FROM channel_message WHERE sent_at IS NULL"));
        Assert.Equal(PageOutcome.Done, (await f.Channels.DeclineAsync(token, Ct)).Outcome);
        Assert.Equal(1, await f.Db.ScalarAsync("SELECT count(*) FROM declined_address"));

        Assert.Equal("target_declined", (await f.Channels.RequestConfirmationAsync(f.S1, channel, Ct)).Error);
        Assert.Equal(PageOutcome.Declined, (await f.Channels.ConfirmAsync(old, Ct)).Outcome);
        Assert.Equal("target_declined", (await f.Channels.AddAsync(f.S1, "test", "a@EXAMPLE.nl", Ct)).Error);
        Assert.Null((await f.Channels.RemoveAsync(f.S1, channel, Ct)).Error);
        Assert.Equal("target_declined", (await f.Channels.AddAsync(f.S1, "test", "a@example.nl", Ct)).Error);
        Assert.Null((await f.Channels.AddAsync(f.S2, "test", "a@example.nl", Ct)).Error);
    }

    // A decline also stops a confirmation that was queued before it.
    [Fact]
    public async Task DeclineDropsTheQueuedConfirmation()
    {
        await using var f = await Fixture.CreateAsync();
        var channel = await f.AddAsync();
        var token = await f.SendConfirmationAsync(channel);
        Assert.Null((await f.Channels.RequestConfirmationAsync(f.S1, channel, Ct)).Error);
        await f.Channels.DeclineAsync(token, Ct);
        var settings = f.Db.Settings(EmailChannelTests.EmailSettings(25));
        var notifier = new Notifier(f.Db.DataSource, [f.Test], new Presets(f.Db.DataSource, settings), f.Channels,
            TestHost.Accounts(f.Db, settings, f.Time), settings, f.Time, NullLogger<Notifier>.Instance);
        await notifier.RunDueAsync(f.Time.GetUtcNow(), Ct);
        Assert.Empty(f.Test.Sent);
    }

    [Theory]
    [InlineData("active", "unsubscribed")]
    [InlineData("paused", "unsubscribed")]
    [InlineData("failing", "unsubscribed")]
    [InlineData("awaitingConfirmation", "awaitingConfirmation")]
    [InlineData("unsubscribed", "unsubscribed")]
    public async Task Unsubscribe(string from, string to)
    {
        await using var f = await Fixture.CreateAsync();
        var channel = await CheckerTests.ChannelAsync(f.Db, f.S1, from, type: "test");
        var token = (string)(await f.Db.RowsAsync("SELECT unsubscribe_token FROM channel WHERE id = $1", channel)).Single()[0]!;
        Assert.Equal(from is "active" or "paused" or "failing" ? PageOutcome.Done : PageOutcome.AlreadyDone, (await f.Channels.UnsubscribeAsync(token, Ct)).Outcome);
        Assert.Equal(to, await f.StatusAsync(channel));
        Assert.Equal(1, await f.Db.ScalarAsync("SELECT count(*) FROM channel WHERE failure_reason IS NULL"));
        Assert.Equal(PageOutcome.NotFound, (await f.Channels.UnsubscribeAsync(Channels.NewToken(), Ct)).Outcome);
    }

    // US4-4, T051: a failing channel can be confirmed again, which clears its reason.
    [Fact]
    public async Task FailingChannelCanBeConfirmedAgain()
    {
        await using var f = await Fixture.CreateAsync();
        var channel = await CheckerTests.ChannelAsync(f.Db, f.S1, "failing", type: "test");
        Assert.Null((await f.Channels.RequestConfirmationAsync(f.S1, channel, Ct)).Error);
        Assert.Equal(PageOutcome.Done, (await f.Channels.ConfirmAsync(await f.SendConfirmationAsync(channel), Ct)).Outcome);
        Assert.Equal([["active", null]], await f.Db.RowsAsync("SELECT status, failure_reason FROM channel WHERE id = $1", channel));
    }

    // FR-029a, FR-029.
    [Fact]
    public async Task Cleanup()
    {
        await using var f = await Fixture.CreateAsync();
        var (_, links) = await f.Db.SeedCalendarAsync("a.example", ["https://a.example/1"], f.S1);
        await f.Channels.SetSettingsAsync(f.S1, new JsonObject { ["language"] = "nl" }, Ct);
        await f.AddAsync(target: "old@example.nl");
        var gone = await f.AddAsync(f.S2, "gone@example.nl");
        await f.Channels.DeclineAsync(await f.SendConfirmationAsync(gone), Ct);
        var active = await CheckerTests.ChannelAsync(f.Db, f.S1, "active", Harness.T0, type: "test");
        var paused = await CheckerTests.ChannelAsync(f.Db, f.S1, "paused", Harness.T0, type: "test");
        var failing = await CheckerTests.ChannelAsync(f.Db, f.S1, "failing", Harness.T0, type: "test");
        f.Time.Advance(TimeSpan.FromDays(1));
        var recent = await f.AddAsync(target: "recent@example.nl");
        await f.SendConfirmationAsync(recent); // sent 29 days before the cleanup
        f.Time.Advance(TimeSpan.FromDays(29) + TimeSpan.FromSeconds(1));

        await f.Channels.CleanupAsync(f.Time.GetUtcNow(), Ct);
        Assert.Equal([active, paused, failing, recent], (await f.Db.RowsAsync("SELECT id FROM channel ORDER BY id")).Select(r => (long)r[0]!));
        Assert.Equal([recent], (await f.Db.RowsAsync("SELECT channel_id FROM channel_message")).Select(r => (long)r[0]!));
        Assert.Equal("target_declined", (await f.Channels.AddAsync(f.S2, "test", "gone@example.nl", Ct)).Error); // the block outlives the cleanup

        // Removing the last subscription keeps the channels, the settings and the declined addresses.
        await f.Db.ExecAsync("DELETE FROM calendar_link WHERE id = $1", links[0]);
        Assert.Equal(4, await f.Db.ScalarAsync("SELECT count(*) FROM channel"));
        Assert.Equal("nl", (await f.Channels.GetSettingsAsync(f.S1, Ct)).Language);
        Assert.Equal(1, await f.Db.ScalarAsync("SELECT count(*) FROM declined_address"));
    }

    // FR-023: test messages.
    [Fact]
    public async Task RequestTest()
    {
        await using var f = await Fixture.CreateAsync();
        var active = await CheckerTests.ChannelAsync(f.Db, f.S1, "active", type: "test");
        var paused = await CheckerTests.ChannelAsync(f.Db, f.S1, "paused", type: "test");
        var r = await f.Channels.RequestTestAsync(f.S1, paused, Ct);
        Assert.Equal(("channel_not_active", "paused"), (r.Error, r.Channel!.Status));
        for (var i = 0; i < 5; i++) Assert.Null((await f.Channels.RequestTestAsync(f.S1, active, Ct)).Error);
        Assert.Equal("test_limit", (await f.Channels.RequestTestAsync(f.S1, active, Ct)).Error);
        Assert.Equal(5, await f.Db.ScalarAsync("SELECT count(*) FROM channel_message WHERE kind = 'test' AND sent_at IS NULL"));
        f.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        f.Test.Configured = false;
        Assert.Equal("type_unavailable", (await f.Channels.RequestTestAsync(f.S1, active, Ct)).Error);
        Assert.Equal("not_found", (await f.Channels.RequestTestAsync(f.S2, active, Ct)).Error);
    }

    // FR-017, FR-018a.
    [Fact]
    public async Task NotificationSettings()
    {
        await using var f = await Fixture.CreateAsync();
        Task<(NotificationSettings? Value, string? Error)> Set(string json) => f.Channels.SetSettingsAsync(f.S1, JsonNode.Parse(json)!.AsObject(), Ct);
        Assert.Equal(new NotificationSettings("en", null, "Europe/Amsterdam"), await f.Channels.GetSettingsAsync(f.S1, Ct));
        Assert.Equal(new NotificationSettings("nl", null, "Europe/Amsterdam"), (await Set("""{"language":"nl"}""")).Value);
        Assert.Equal(new NotificationSettings("nl", "America/New_York", "America/New_York"), (await Set("""{"timeZone":"America/New_York"}""")).Value);
        Assert.Equal("invalid_language", (await Set("""{"language":"de"}""")).Error);
        Assert.Equal("invalid_time_zone", (await Set("""{"timeZone":"W. Europe Standard Time"}""")).Error);
        Assert.Equal("invalid_time_zone", (await Set("""{"timeZone":"Mars/Base"}""")).Error);
        Assert.Equal("empty_body", (await Set("{}")).Error);
        Assert.Equal(new NotificationSettings("nl", "America/New_York", "America/New_York"), await f.Channels.GetSettingsAsync(f.S1, Ct));
        Assert.Equal(new NotificationSettings("nl", null, "Europe/Amsterdam"), (await Set("""{"timeZone":null}""")).Value);
    }
}
