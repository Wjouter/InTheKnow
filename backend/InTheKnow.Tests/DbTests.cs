using System.Security.Cryptography;
using Npgsql;
using Xunit;

namespace InTheKnow.Tests;

public class DbTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

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
        var (s1, s2, s3) = (await TestHost.AccountAsync(db.DataSource), await TestHost.AccountAsync(db.DataSource), await TestHost.AccountAsync(db.DataSource));
        await db.ExecAsync("INSERT INTO preset (owner, name, rules) VALUES ($1, 'Default', '[]')", s1);

        var (calendar, links) = await db.SeedCalendarAsync("a.example", ["https://a.example/1", "https://a.example/2"], s1);
        await db.ExecAsync("INSERT INTO calendar_link (calendar_id, subscriber, url) VALUES ($1, $2, 'https://a.example/3')", calendar, s2);
        await Rejected("INSERT INTO subscription_filter (calendar_id, subscriber) VALUES ($1, $2)", calendar, s1);
        await Rejected("INSERT INTO subscription_filter (calendar_id, subscriber, preset_id, rules) VALUES ($1, $2, 1, '[]')", calendar, s1);
        await Rejected("INSERT INTO detected_change (calendar_id, event_key, kind, new_value, event_name, event_starts_at, detected_at, event_before) VALUES ($1, 'k', 'added', '{}', 'n', now(), now(), '{}')", calendar);

        // Deferred triggers: another subscriber's preset, or no link at all.
        var personal = await db.ScalarAsync("SELECT id FROM preset WHERE owner = $1", s1);
        await Rejected("INSERT INTO subscription_filter (calendar_id, subscriber, preset_id) VALUES ($1, $2, $3)", calendar, s2, personal);
        await Rejected("INSERT INTO subscription_filter (calendar_id, subscriber, rules) VALUES ($1, $2, '[]')", calendar, s3);
        await Rejected("UPDATE preset SET owner = $2 WHERE id = $1", personal, s2);

        // Deleting one of two links keeps the filter; deleting the last removes it.
        await db.ExecAsync("INSERT INTO subscription_filter (calendar_id, subscriber, preset_id) VALUES ($1, $2, $3)", calendar, s1, personal);
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
        var (s1, s2, s3) = (await TestHost.AccountAsync(db.DataSource), await TestHost.AccountAsync(db.DataSource), await TestHost.AccountAsync(db.DataSource));
        var n = 0;
        string Token() => (n++).ToString().PadLeft(43, 't');
        Task<long> Channel(long subscriber, string target, string status = "awaitingConfirmation", string? reason = null, bool activated = false) =>
            db.ScalarAsync("""
                INSERT INTO channel (subscriber, type, target, status, status_since, failure_reason, activated_at, unsubscribe_token, created_at)
                VALUES ($1, 'email', $2, $3, now(), $4, CASE WHEN $5 THEN now() END, $6, now()) RETURNING id
                """, subscriber, target, status, (object?)reason ?? DBNull.Value, activated, Token());

        const string insert = """
            INSERT INTO channel (subscriber, type, target, status, status_since, failure_reason, activated_at, unsubscribe_token, created_at)
            VALUES ($1, 'email', $2, $3, now(), $4, $5, $6, now())
            """;
        await Rejected(insert, s1, "a@x.nl", "failing", DBNull.Value, DBNull.Value, Token());
        await Rejected(insert, s1, "a@x.nl", "active", "smtp_550", DateTimeOffset.UtcNow, Token());
        await Rejected(insert, s1, "a@x.nl", "active", DBNull.Value, DBNull.Value, Token());
        await Rejected(insert, s1, "a@x.nl", "sleeping", DBNull.Value, DBNull.Value, Token());
        await Rejected(insert, s1, "a@x.nl", "paused", DBNull.Value, DBNull.Value, "short");

        var channel = await Channel(s1, "a@x.nl");
        await Rejected(insert, s1, "A@X.nl", "paused", DBNull.Value, DBNull.Value, Token());
        await Channel(s2, "a@x.nl"); // another subscriber may add the same address

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
        var (calendar, links) = await db.SeedCalendarAsync("a.example", ["https://a.example/1", "https://a.example/2"], s1);
        var (other, _) = await db.SeedCalendarAsync("b.example", ["https://b.example/1"], s1);
        await db.ExecAsync("INSERT INTO calendar_link (calendar_id, subscriber, url) VALUES ($1, $2, 'https://a.example/3')", calendar, s2);
        Task<long> Change(long cal) => db.ScalarAsync(
            "INSERT INTO detected_change (calendar_id, event_key, kind, new_value, event_name, event_starts_at, detected_at) VALUES ($1, 'k', 'added', '{}', 'n', now(), now()) RETURNING id", cal);
        var change = await Change(calendar);
        await Rejected("INSERT INTO delivery (channel_id, change_id, status) VALUES ($1, $2, 'skipped')", channel, change);
        await Rejected("INSERT INTO delivery (channel_id, change_id, status, reason) VALUES ($1, $2, 'sent', 'hidden')", channel, change);

        // Labels and the link requirement (deferred to commit).
        await Rejected("INSERT INTO subscription_setting (calendar_id, subscriber, label) VALUES ($1, $2, $3)", calendar, s1, "a\nb");
        await Rejected("INSERT INTO subscription_setting (calendar_id, subscriber) VALUES ($1, $2)", calendar, s3);

        // At most 5 channels per subscriber; another subscriber is unaffected.
        for (var i = 0; i < 4; i++) await Channel(s3, $"{i}@x.nl");
        await Channel(s1, "b@x.nl"); await Channel(s1, "c@x.nl"); await Channel(s1, "d@x.nl"); await Channel(s1, "e@x.nl");
        var limit = await Rejected(insert, s1, "f@x.nl", "paused", DBNull.Value, DBNull.Value, Token());
        Assert.Equal(("23514", "channel_limit"), (limit.SqlState, limit.ConstraintName));
        await Channel(s3, "4@x.nl");

        // Removing the last link skips pending deliveries of that calendar only, and drops the settings row.
        var s2Channel = (long)(await db.RowsAsync("SELECT id FROM channel WHERE subscriber = $1", s2)).Single()[0]!;
        var otherChange = await Change(other);
        foreach (var (ch, c) in new[] { (channel, change), (channel, otherChange), (s2Channel, change) })
            await db.ExecAsync("INSERT INTO delivery (channel_id, change_id) VALUES ($1, $2)", ch, c);
        await db.ExecAsync("INSERT INTO subscription_setting (calendar_id, subscriber, notify) VALUES ($1, $2, false)", calendar, s1);
        await db.ExecAsync("DELETE FROM calendar_link WHERE id = $1", links[0]);
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM subscription_setting"));
        Assert.Equal(3, await db.ScalarAsync("SELECT count(*) FROM delivery WHERE status = 'pending'"));
        await db.ExecAsync("DELETE FROM calendar_link WHERE id = $1", links[1]);
        Assert.Equal(0, await db.ScalarAsync("SELECT count(*) FROM subscription_setting"));
        var rows = await db.RowsAsync("SELECT channel_id, change_id, status, reason FROM delivery ORDER BY channel_id, change_id");
        Assert.Equal([[channel, change, "skipped", "subscriptionRemoved"], [channel, otherChange, "pending", null], [s2Channel, change, "pending", null]], rows);
    }

    // 004_accounts.sql: every check of data-model.md § Tables.
    [Fact]
    public async Task AccountConstraints()
    {
        await using var db = await TestDb.CreateAsync();
        async Task Rejected(string sql, params object[] args) => await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync(sql, args));
        object now = DateTimeOffset.UtcNow, none = DBNull.Value, hash = new byte[32];

        Assert.Equal([[true, false, Array.Empty<string>()]], await db.RowsAsync("SELECT id, open, allowed_domains FROM signup_settings"));

        // account
        var a = await TestHost.AccountAsync(db.DataSource, "Case@example.nl");
        const string account = "INSERT INTO account (email, language, created_at, confirmed_at, disabled_at, role) VALUES ($1, $2, now(), $3, $4, $5)";
        await Rejected(account, "case@EXAMPLE.nl", "en", now, none, "user");
        await Rejected(account, "u1@example.nl", "en", none, none, "admin");
        await Rejected(account, "u2@example.nl", "en", none, now, "user");
        await Rejected(account, "u3@example.nl", "en", now, now, "admin");
        await Rejected(account, "u4@example.nl", "en", now, none, "owner");
        await Rejected(account, "u5@example.nl", "de", now, none, "user");
        await db.ExecAsync(account, "u6@example.nl", "nl", now, now, "user");

        // sign_in_method
        var b = await db.ScalarAsync("INSERT INTO account (email, language, created_at) VALUES ('b@example.nl', 'en', now()) RETURNING id");
        const string method = "INSERT INTO sign_in_method (account_id, type, key, secret, created_at) VALUES ($1, $2, $3, $4, now())";
        await Rejected(method, a, "password", none, "hash");
        await Rejected(method, b, "password", none, none);
        await Rejected(method, b, "password", "k", "hash");
        await Rejected(method, b, "Pass word", "k", none);
        await db.ExecAsync(method, b, "test", "k", none);
        await Rejected(method, a, "test", "k", none);

        // session
        var m = await db.ScalarAsync("SELECT id FROM sign_in_method WHERE account_id = $1", a);
        const string session = """
            INSERT INTO session (account_id, method_id, token_hash, device, awaiting_code, second_factor, created_at, last_used_at)
            VALUES ($1, $2, $3, 'Firefox on Linux', $4, $5, now(), now())
            """;
        await Rejected(session, a, m, new byte[31], false, false);
        await Rejected(session, a, m, hash, true, true);
        await db.ExecAsync(session, a, m, hash, true, false);

        // two_factor
        await Rejected("INSERT INTO two_factor (account_id, secret) VALUES ($1, $2)", a, new byte[19]);
        await db.ExecAsync("INSERT INTO two_factor (account_id, secret) VALUES ($1, $2)", a, new byte[20]);

        // invite
        const string invite = """
            INSERT INTO invite (kind, email, language, grants_admin, max_uses, used, token_hash, created_at, expires_at)
            VALUES ($1, $2, 'en', $3, $4, $5, $6, $7, $8)
            """;
        var t = DateTimeOffset.UtcNow;
        object week = t.AddDays(7);
        await Rejected(invite, "shareable", none, true, 1, 0, RandomNumberGenerator.GetBytes(32), t, week);
        await Rejected(invite, "named", "n@example.nl", false, 2, 0, none, t, week);
        await Rejected(invite, "named", none, false, 1, 0, none, t, week);
        await Rejected(invite, "shareable", "n@example.nl", false, 1, 0, RandomNumberGenerator.GetBytes(32), t, week);
        await Rejected(invite, "shareable", none, false, 1, 0, none, t, week);
        await Rejected(invite, "named", "n@example.nl", false, 1, 0, none, t, t.AddDays(91));
        await Rejected(invite, "shareable", none, false, 3, 4, RandomNumberGenerator.GetBytes(32), t, week);
        await db.ExecAsync(invite, "named", "n@example.nl", true, 1, 0, none, t, t.AddDays(90));
        await Rejected(invite, "named", "N@EXAMPLE.nl", false, 1, 0, none, t, week);
        var inviteId = await db.ScalarAsync("SELECT id FROM invite");

        // account_message
        const string message = """
            INSERT INTO account_message (kind, account_id, invite_id, to_address, language, requested_at, sent_at, expired_at, token_hash, token_expires_at, used_at)
            VALUES ($1, $2, $3, 'a@example.nl', 'en', now(), $4, $5, $6, $7, $8)
            """;
        await Rejected(message, "nope", a, none, none, none, none, none, none);
        await Rejected(message, "confirm", none, none, none, none, none, none, none);
        await Rejected(message, "invite", none, none, none, none, none, none, none);
        await Rejected(message, "reset", a, inviteId, none, none, none, none, none);
        await Rejected(message, "passwordChanged", a, none, now, none, RandomNumberGenerator.GetBytes(32), now, none);
        await Rejected(message, "confirm", a, none, now, none, none, none, none);
        await Rejected(message, "confirm", a, none, none, none, RandomNumberGenerator.GetBytes(32), now, none);
        await Rejected(message, "confirm", a, none, now, none, RandomNumberGenerator.GetBytes(32), none, none);
        await Rejected(message, "passwordChanged", a, none, now, none, none, none, now);
        await Rejected(message, "passwordChanged", a, none, now, now, none, none, none);
        await db.ExecAsync(message, "confirm", a, none, now, none, RandomNumberGenerator.GetBytes(32), now, now);
        await db.ExecAsync(message, "invite", none, inviteId, none, none, none, none, none);
        await db.ExecAsync(message, "deleted", none, none, none, now, none, none, none);

        // signup_settings
        await Rejected("INSERT INTO signup_settings (open, allowed_domains) VALUES (true, '{}')");
        await Rejected("UPDATE signup_settings SET allowed_domains = '{Inholland.nl}'");
        await Rejected("UPDATE signup_settings SET allowed_domains = $1", (object)Enumerable.Range(0, 101).Select(i => $"d{i}.nl").ToArray());
        await db.ExecAsync("UPDATE signup_settings SET allowed_domains = $1", (object)Enumerable.Range(0, 100).Select(i => $"d{i}.nl").ToArray());

        // security_event
        await Rejected("INSERT INTO security_event (occurred_at, type) VALUES (now(), 'Sign-in')");
        await db.ExecAsync("INSERT INTO security_event (occurred_at, type) VALUES (now(), 'signIn')");

        // channel
        const string channel = """
            INSERT INTO channel (subscriber, type, target, status, status_since, unsubscribe_token, created_at, is_account_address)
            VALUES ($1, $2, $3, 'paused', now(), $4, now(), $5)
            """;
        await Rejected(channel, a, "sms", "x", new string('a', 43), true);
        await db.ExecAsync(channel, a, "email", "a@example.nl", new string('b', 43), true);
        await Rejected(channel, a, "email", "c@example.nl", new string('c', 43), true);

        // delivery reason
        var (calendar, _) = await db.SeedCalendarAsync("a.example", ["https://a.example/1"], a);
        var change = await db.ScalarAsync(
            "INSERT INTO detected_change (calendar_id, event_key, kind, new_value, event_name, event_starts_at, detected_at) VALUES ($1, 'k', 'added', '{}', 'n', now(), now()) RETURNING id", calendar);
        var channelId = await db.ScalarAsync("SELECT id FROM channel WHERE subscriber = $1", a);
        await db.ExecAsync("INSERT INTO delivery (channel_id, change_id, status, reason) VALUES ($1, $2, 'skipped', 'accountDisabled')", channelId, change);
    }

    [Fact]
    public async Task KeepOneMethod()
    {
        await using var db = await TestDb.CreateAsync();
        var a = await TestHost.AccountAsync(db.DataSource);
        var only = await db.ScalarAsync("SELECT id FROM sign_in_method WHERE account_id = $1", a);
        var e = await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync("DELETE FROM sign_in_method WHERE id = $1", only));
        Assert.Equal(("23514", "last_method"), (e.SqlState, e.ConstraintName));

        // Delete and insert in one transaction: checked at commit.
        await using (var conn = await db.DataSource.OpenConnectionAsync(Ct))
        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            await Exec(conn, tx, "DELETE FROM sign_in_method WHERE id = $1", only);
            await Exec(conn, tx, "INSERT INTO sign_in_method (account_id, type, key, created_at) VALUES ($1, 'test', 'k1', now())", a);
            await tx.CommitAsync(Ct);
        }
        await db.ExecAsync("INSERT INTO sign_in_method (account_id, type, key, created_at) VALUES ($1, 'test', 'k2', now())", a);
        await db.ExecAsync("DELETE FROM sign_in_method WHERE key = 'k1'");
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM sign_in_method WHERE account_id = $1", a));

        // Two concurrent removals of an account's two methods: exactly one fails.
        await db.ExecAsync("INSERT INTO sign_in_method (account_id, type, key, created_at) VALUES ($1, 'test', 'k3', now())", a);
        Assert.Equal(1, await FailuresAsync(db, "last_method",
            (c, tx) => Exec(c, tx, "DELETE FROM sign_in_method WHERE key = 'k2'"),
            (c, tx) => Exec(c, tx, "DELETE FROM sign_in_method WHERE key = 'k3'")));
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM sign_in_method WHERE account_id = $1", a));

        // Deleting the account cascades its methods.
        await db.ExecAsync("DELETE FROM account WHERE id = $1", a);
        Assert.Equal(0, await db.ScalarAsync("SELECT count(*) FROM sign_in_method"));
    }

    [Fact]
    public async Task KeepLastAdmin()
    {
        await using var db = await TestDb.CreateAsync();
        var user = await TestHost.AccountAsync(db.DataSource);
        await db.ExecAsync("UPDATE account SET role = 'user' WHERE id = $1", user); // no admin ever: nothing fires

        var a = await TestHost.AccountAsync(db.DataSource, role: "admin");
        foreach (var sql in new[] { "UPDATE account SET role = 'user' WHERE id = $1", "DELETE FROM account WHERE id = $1" })
        {
            var e = await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync(sql, a));
            Assert.Equal(("23514", "last_admin"), (e.SqlState, e.ConstraintName));
        }

        var b = await TestHost.AccountAsync(db.DataSource, role: "admin");
        await db.ExecAsync("UPDATE account SET role = 'user' WHERE id = $1", b);
        await db.ExecAsync("UPDATE account SET role = 'admin' WHERE id = $1", b);

        // Two admins demoting each other at once: exactly one fails.
        Assert.Equal(1, await FailuresAsync(db, "last_admin",
            (c, tx) => Exec(c, tx, "UPDATE account SET role = 'user' WHERE id = $1", a),
            (c, tx) => Exec(c, tx, "UPDATE account SET role = 'user' WHERE id = $1", b)));
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM account WHERE role = 'admin'"));
    }

    // FR-024, SC-008: deleting an account removes everything it owns, and nothing else.
    [Fact]
    public async Task AccountDeletionCascades()
    {
        await using var db = await TestDb.CreateAsync();
        var a = await TestHost.AccountAsync(db.DataSource);
        var other = await TestHost.AccountAsync(db.DataSource);
        var (calendar, _) = await db.SeedCalendarAsync("a.example", ["https://a.example/1"], a);
        var preset = await db.ScalarAsync("INSERT INTO preset (owner, name, rules) VALUES ($1, 'Mine', '[]') RETURNING id", a);
        await db.ExecAsync("INSERT INTO subscription_filter (calendar_id, subscriber, preset_id) VALUES ($1, $2, $3)", calendar, a, preset);
        await db.ExecAsync("INSERT INTO subscription_setting (calendar_id, subscriber, notify) VALUES ($1, $2, false)", calendar, a);
        var channel = await db.ScalarAsync("""
            INSERT INTO channel (subscriber, type, target, status, status_since, activated_at, unsubscribe_token, created_at)
            VALUES ($1, 'email', 'a@example.nl', 'active', now(), now(), $2, now()) RETURNING id
            """, a, new string('t', 43));
        var change = await db.ScalarAsync(
            "INSERT INTO detected_change (calendar_id, event_key, kind, new_value, event_name, event_starts_at, detected_at) VALUES ($1, 'k', 'added', '{}', 'n', now(), now()) RETURNING id", calendar);
        await db.ExecAsync("INSERT INTO delivery (channel_id, change_id) VALUES ($1, $2)", channel, change);
        await db.ExecAsync("INSERT INTO channel_message (channel_id, kind, requested_at) VALUES ($1, 'test', now())", channel);
        await db.ExecAsync("INSERT INTO declined_address (subscriber, address_hash, created_at) VALUES ($1, $2, now())", a, new byte[32]);
        var method = await db.ScalarAsync("SELECT id FROM sign_in_method WHERE account_id = $1", a);
        await db.ExecAsync("""
            INSERT INTO session (account_id, method_id, token_hash, device, created_at, last_used_at) VALUES ($1, $2, $3, 'd', now(), now())
            """, a, method, RandomNumberGenerator.GetBytes(32));
        await db.ExecAsync("INSERT INTO two_factor (account_id, secret, enabled_at) VALUES ($1, $2, now())", a, new byte[20]);
        await db.ExecAsync("INSERT INTO recovery_code (account_id, code_hash) VALUES ($1, $2)", a, new byte[32]);
        await db.ExecAsync("INSERT INTO account_message (kind, account_id, to_address, language, requested_at) VALUES ('passwordChanged', $1, 'a@example.nl', 'en', now())", a);
        await db.ExecAsync("""
            INSERT INTO invite (kind, email, language, max_uses, created_by, created_at, expires_at) VALUES ('named', 'g@example.nl', 'en', 1, $1, now(), now() + interval '7 days')
            """, a);
        await db.ExecAsync("INSERT INTO security_event (occurred_at, type, account_id) VALUES (now(), 'signIn', $1)", a);

        // A preset in use cannot be deleted on its own.
        await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync("DELETE FROM preset WHERE id = $1", preset));

        await db.ExecAsync("DELETE FROM account WHERE id = $1", a);
        foreach (var (table, column) in new[]
        {
            ("calendar_link", "subscriber"), ("subscription_filter", "subscriber"), ("subscription_setting", "subscriber"), ("channel", "subscriber"),
            ("declined_address", "subscriber"), ("preset", "owner"), ("session", "account_id"), ("sign_in_method", "account_id"),
            ("two_factor", "account_id"), ("recovery_code", "account_id"), ("account_message", "account_id"), ("invite", "created_by"),
        })
            Assert.Equal(0, await db.ScalarAsync($"SELECT count(*) FROM {table} WHERE {column} = $1", a));
        Assert.Equal(0, await db.ScalarAsync("SELECT count(*) FROM delivery"));
        Assert.Equal(0, await db.ScalarAsync("SELECT count(*) FROM channel_message"));
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM invite WHERE created_by IS NULL"));
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM security_event WHERE account_id = $1", a));
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM account WHERE id = $1", other));
    }

    static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a });
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    // Runs each step in its own transaction concurrently, and counts the commits refused with the named constraint.
    static async Task<int> FailuresAsync(TestDb db, string constraint, params Func<NpgsqlConnection, NpgsqlTransaction, Task>[] steps)
    {
        var results = await Task.WhenAll(steps.Select(step => Task.Run(async () =>
        {
            await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
            await using var tx = await conn.BeginTransactionAsync(Ct);
            try
            {
                await step(conn, tx);
                await tx.CommitAsync(Ct);
                return false;
            }
            catch (PostgresException e) when (e.ConstraintName == constraint) { return true; }
        }, Ct)));
        return results.Count(failed => failed);
    }
}
