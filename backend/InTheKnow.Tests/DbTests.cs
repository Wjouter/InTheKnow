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
}
