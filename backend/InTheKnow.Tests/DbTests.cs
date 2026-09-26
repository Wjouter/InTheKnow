using Npgsql;
using Xunit;

namespace InTheKnow.Tests;

public class DbTests
{
    [Fact]
    public async Task MigrateTwiceAppliesOnce()
    {
        await using var db = await TestDb.CreateAsync();
        await Db.MigrateAsync(db.DataSource);
        await Db.MigrateAsync(db.DataSource);
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM schema_migration WHERE version = 1"));
    }

    [Fact]
    public async Task SecondInstanceIsRefused()
    {
        await using var db = await TestDb.CreateAsync();
        var first = await Db.TakeInstanceLockAsync(db.DataSource);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Db.TakeInstanceLockAsync(db.DataSource));
        await first.DisposeAsync();
        await using var second = await Db.TakeInstanceLockAsync(db.DataSource);
    }

    [Fact]
    public async Task ConstraintsAreLive()
    {
        await using var db = await TestDb.CreateAsync();
        var e = await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync(
            "INSERT INTO calendar (identity_hash, host, next_check_at, status) VALUES ($1, 'x', now(), 'x')", new byte[32]));
        Assert.Equal(PostgresErrorCodes.CheckViolation, e.SqlState);
    }

    // 002_filters.sql: constraints and cross-table triggers.
    [Fact]
    public async Task FilterConstraintsAndTriggers()
    {
        await using var db = await TestDb.CreateAsync();
        async Task Rejected(string sql, params object[] args) => await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync(sql, args));

        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM preset WHERE is_default AND owner IS NULL AND name = 'Default'"));
        await Rejected("INSERT INTO preset (name, rules, is_default) VALUES ('Other', '[]', true)");
        await Rejected("INSERT INTO preset (name, rules) VALUES ('Default', '[]')"); // NULLS NOT DISTINCT
        await db.ExecAsync("INSERT INTO preset (owner, name, rules) VALUES ('s1', 'Default', '[]')");

        var (calendar, links) = await db.SeedCalendarAsync("a.example", ["https://a.example/1", "https://a.example/2"], "s1");
        await db.ExecAsync("INSERT INTO calendar_link (calendar_id, subscriber, url) VALUES ($1, 's2', 'https://a.example/3')", calendar);
        await Rejected("INSERT INTO subscription_filter (calendar_id, subscriber) VALUES ($1, 's1')", calendar);
        await Rejected("INSERT INTO subscription_filter (calendar_id, subscriber, preset_id, rules) VALUES ($1, 's1', 1, '[]')", calendar);
        await Rejected("INSERT INTO detected_change (calendar_id, event_key, kind, new_value, event_name, event_starts_at, detected_at, event_before) VALUES ($1, 'k', 'added', '{}', 'n', now(), now(), '{}')", calendar);

        // Deferred triggers: another subscriber's preset, or no link at all.
        var personal = await db.ScalarAsync("SELECT id FROM preset WHERE owner = 's1'");
        await Rejected("INSERT INTO subscription_filter (calendar_id, subscriber, preset_id) VALUES ($1, 's2', $2)", calendar, personal);
        await Rejected("INSERT INTO subscription_filter (calendar_id, subscriber, rules) VALUES ($1, 's3', '[]')", calendar);
        await Rejected("UPDATE preset SET owner = 's2' WHERE id = $1", personal);

        // Deleting one of two links keeps the filter; deleting the last removes it.
        await db.ExecAsync("INSERT INTO subscription_filter (calendar_id, subscriber, preset_id) VALUES ($1, 's1', $2)", calendar, personal);
        await db.ExecAsync("DELETE FROM calendar_link WHERE id = $1", links[0]);
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM subscription_filter"));
        await db.ExecAsync("DELETE FROM calendar_link WHERE id = $1", links[1]);
        Assert.Equal(0, await db.ScalarAsync("SELECT count(*) FROM subscription_filter"));
    }

    // 003_notifications.sql: constraints and cross-table triggers.
    [Fact]
    public async Task NotificationConstraintsAndTriggers()
    {
        await using var db = await TestDb.CreateAsync();
        async Task<PostgresException> Rejected(string sql, params object[] args) => await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync(sql, args));
        var n = 0;
        string Token() => (n++).ToString().PadLeft(43, 't');
        Task<long> Channel(string subscriber, string target, string status = "awaitingConfirmation", string? reason = null, bool activated = false) =>
            db.ScalarAsync("""
                INSERT INTO channel (subscriber, type, target, status, status_since, failure_reason, activated_at, unsubscribe_token, created_at)
                VALUES ($1, 'email', $2, $3, now(), $4, CASE WHEN $5 THEN now() END, $6, now()) RETURNING id
                """, subscriber, target, status, (object?)reason ?? DBNull.Value, activated, Token());

        const string insert = """
            INSERT INTO channel (subscriber, type, target, status, status_since, failure_reason, activated_at, unsubscribe_token, created_at)
            VALUES ($1, 'email', $2, $3, now(), $4, $5, $6, now())
            """;
        await Rejected(insert, "s1", "a@x.nl", "failing", DBNull.Value, DBNull.Value, Token());
        await Rejected(insert, "s1", "a@x.nl", "active", "smtp_550", DateTimeOffset.UtcNow, Token());
        await Rejected(insert, "s1", "a@x.nl", "active", DBNull.Value, DBNull.Value, Token());
        await Rejected(insert, "s1", "a@x.nl", "sleeping", DBNull.Value, DBNull.Value, Token());
        await Rejected(insert, "s1", "a@x.nl", "paused", DBNull.Value, DBNull.Value, "short");

        var channel = await Channel("s1", "a@x.nl");
        await Rejected(insert, "s1", "A@X.nl", "paused", DBNull.Value, DBNull.Value, Token());
        await Channel("s2", "a@x.nl"); // another subscriber may add the same address

        // channel_message shapes.
        const string message = "INSERT INTO channel_message (channel_id, kind, requested_at, sent_at, language, token_hash, expires_at) VALUES ($1, $2, now(), $3, $4, $5, $6)";
        object sent = DateTimeOffset.UtcNow, none = DBNull.Value, hash = new byte[32];
        await Rejected(message, channel, "test", sent, "en", hash, none);
        await Rejected(message, channel, "test", sent, none, none, none);
        await Rejected(message, channel, "confirmation", sent, "en", none, sent);
        await Rejected(message, channel, "confirmation", sent, "en", hash, none);
        await Rejected(message, channel, "confirmation", none, none, hash, none);
        await Rejected(message, channel, "confirmation", none, none, none, sent);
        await db.ExecAsync(message, channel, "confirmation", sent, "en", hash, sent);
        await db.ExecAsync(message, channel, "test", sent, "nl", none, none);

        // delivery reasons.
        var (calendar, links) = await db.SeedCalendarAsync("a.example", ["https://a.example/1", "https://a.example/2"], "s1");
        var (other, _) = await db.SeedCalendarAsync("b.example", ["https://b.example/1"], "s1");
        await db.ExecAsync("INSERT INTO calendar_link (calendar_id, subscriber, url) VALUES ($1, 's2', 'https://a.example/3')", calendar);
        Task<long> Change(long cal) => db.ScalarAsync(
            "INSERT INTO detected_change (calendar_id, event_key, kind, new_value, event_name, event_starts_at, detected_at) VALUES ($1, 'k', 'added', '{}', 'n', now(), now()) RETURNING id", cal);
        var change = await Change(calendar);
        await Rejected("INSERT INTO delivery (channel_id, change_id, status) VALUES ($1, $2, 'skipped')", channel, change);
        await Rejected("INSERT INTO delivery (channel_id, change_id, status, reason) VALUES ($1, $2, 'sent', 'hidden')", channel, change);

        // Labels and the link requirement (deferred to commit).
        await Rejected("INSERT INTO subscription_setting (calendar_id, subscriber, label) VALUES ($1, 's1', $2)", calendar, "a\nb");
        await Rejected("INSERT INTO subscription_setting (calendar_id, subscriber) VALUES ($1, 's3')", calendar);

        // At most 5 channels per subscriber; another subscriber is unaffected.
        for (var i = 0; i < 4; i++) await Channel("s3", $"{i}@x.nl");
        await Channel("s1", "b@x.nl"); await Channel("s1", "c@x.nl"); await Channel("s1", "d@x.nl"); await Channel("s1", "e@x.nl");
        var limit = await Rejected(insert, "s1", "f@x.nl", "paused", DBNull.Value, DBNull.Value, Token());
        Assert.Equal(("23514", "channel_limit"), (limit.SqlState, limit.ConstraintName));
        await Channel("s3", "4@x.nl");

        // Removing the last link skips pending deliveries of that calendar only, and drops the settings row.
        var s2Channel = (long)(await db.RowsAsync("SELECT id FROM channel WHERE subscriber = 's2'")).Single()[0]!;
        var otherChange = await Change(other);
        foreach (var (ch, c) in new[] { (channel, change), (channel, otherChange), (s2Channel, change) })
            await db.ExecAsync("INSERT INTO delivery (channel_id, change_id) VALUES ($1, $2)", ch, c);
        await db.ExecAsync("INSERT INTO subscription_setting (calendar_id, subscriber, notify) VALUES ($1, 's1', false)", calendar);
        await db.ExecAsync("DELETE FROM calendar_link WHERE id = $1", links[0]);
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM subscription_setting"));
        Assert.Equal(3, await db.ScalarAsync("SELECT count(*) FROM delivery WHERE status = 'pending'"));
        await db.ExecAsync("DELETE FROM calendar_link WHERE id = $1", links[1]);
        Assert.Equal(0, await db.ScalarAsync("SELECT count(*) FROM subscription_setting"));
        var rows = await db.RowsAsync("SELECT channel_id, change_id, status, reason FROM delivery ORDER BY channel_id, change_id");
        Assert.Equal([[channel, change, "skipped", "subscriptionRemoved"], [channel, otherChange, "pending", null], [s2Channel, change, "pending", null]], rows);
    }
}
