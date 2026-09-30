using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace InTheKnow;

sealed record ChannelRow(long Id, long Subscriber, string Type, string Target, string Status, string? FailureReason,
                         DateTimeOffset? LastMessageAt, DateTimeOffset CreatedAt, bool AccountAddress = false);

// Error is the contracts/api.md `reason`; "not_found" means 404. Channel is also set with channel_not_active (US7-2).
sealed record ChannelResult(ChannelRow? Channel, string? Error);

sealed record SubscriptionNotifications(bool Notify, string? Label, string Name);

sealed record NotificationSettings(string Language, string? TimeZone, string EffectiveTimeZone);

enum PageOutcome { Done, AlreadyDone, Expired, Declined, Unavailable, NotFound }

// Language picks the page text (FR-017).
sealed record PageResult(PageOutcome Outcome, string Language);

// Channel storage and lifecycle (data-model.md § Channel lifecycle, research R7, R10), per-subscription notify/label and
// the subscriber's language and time zone. Every time decision uses the app clock. Never logs or returns another
// subscriber's data; a PostgresException never escapes, since its detail can hold the address (FR-031).
sealed partial class Channels(NpgsqlDataSource db, IEnumerable<IChannelType> types, Settings settings, TimeProvider time)
{
    const int MaxConfirmations = 3, MaxTests = 5;
    static readonly TimeSpan ConfirmationValidity = TimeSpan.FromHours(48), Unused = TimeSpan.FromDays(30), Day = TimeSpan.FromDays(1);
    const string Columns = "id, subscriber, type, target, status, failure_reason, last_message_at, created_at, is_account_address";

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")] private static partial Regex TokenShape();

    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    // research R7: salted with the account id, so equal addresses of different accounts do not correlate.
    public static byte[] DeclinedHash(long subscriber, string address) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(subscriber.ToString(CultureInfo.InvariantCulture) + "\n" + address.ToLowerInvariant()));

    IChannelType? Type(string name) => types.FirstOrDefault(t => t.Name == name);

    public async Task<List<ChannelRow>> ListAsync(long subscriber, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM channel WHERE subscriber = $1 ORDER BY id");
        cmd.Parameters.Add(new() { Value = subscriber });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<ChannelRow>();
        while (await r.ReadAsync(ct)) rows.Add(Read(r));
        return rows;
    }

    public async Task<ChannelResult> AddAsync(long subscriber, string type, string target, CancellationToken ct)
    {
        if (Type(type) is not { } t) return Fail("invalid_type");
        if (t.Normalize(target) is not { } normalized) return Fail("invalid_target");
        if (!t.Configured) return Fail("type_unavailable");
        await using var conn = await db.OpenConnectionAsync(ct);
        if (await DeclinedAsync(conn, null, subscriber, normalized, ct)) return Fail("target_declined");
        var now = time.GetUtcNow();
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO channel (subscriber, type, target, status, status_since, unsubscribe_token, created_at)
                VALUES ($1, $2, $3, 'awaitingConfirmation', $4, $5, $4) RETURNING {Columns}
                """, conn, tx)
            { Parameters = { new() { Value = subscriber }, new() { Value = type }, new() { Value = normalized }, new() { Value = now }, new() { Value = NewToken() } } };
            ChannelRow row;
            await using (var r = await insert.ExecuteReaderAsync(ct))
            {
                await r.ReadAsync(ct);
                row = Read(r);
            }
            await QueueAsync(conn, tx, row.Id, "confirmation", now, ct);
            await tx.CommitAsync(ct);
            return new(row, null);
        }
        catch (PostgresException e) when (e is { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: "channel_limit" }) { return Fail("channel_limit"); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return Fail("target_taken"); }
    }

    // A confirmation link (FR-005, FR-007): nothing changes unless the channel can become active now.
    public async Task<PageResult> ConfirmAsync(string token, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await MessageAsync(conn, tx, token, ct) is not { } m) return new(PageOutcome.NotFound, "en");
        var now = time.GetUtcNow();
        var outcome =
            m.UsedAt is not null || m.Status is "active" or "paused" ? PageOutcome.AlreadyDone
            : m.ExpiresAt < now ? PageOutcome.Expired
            : await DeclinedAsync(conn, tx, m.Subscriber, m.Target, ct) ? PageOutcome.Declined
            : Type(m.Type) is not { Configured: true } ? PageOutcome.Unavailable
            : PageOutcome.Done;
        if (outcome == PageOutcome.Done)
        {
            await ExecAsync(conn, tx, """
                UPDATE channel SET status = 'active', status_since = $2, activated_at = $2, failure_reason = NULL,
                    failed_attempts = 0, next_attempt_at = NULL
                WHERE id = $1
                """, ct, m.ChannelId, now);
            await ExecAsync(conn, tx, "UPDATE channel_message SET used_at = $2 WHERE id = $1", ct, m.Id, now);
            await tx.CommitAsync(ct);
        }
        return new(outcome, m.Language);
    }

    // "This wasn't me" (FR-006): works for as long as the token row exists, from any status, and never re-activates.
    public async Task<PageResult> DeclineAsync(string token, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await MessageAsync(conn, tx, token, ct) is not { } m) return new(PageOutcome.NotFound, "en");
        var now = time.GetUtcNow();
        if (m.Status != "unsubscribed") await StopAsync(conn, tx, m.ChannelId, "unsubscribed", now, ct);
        // A queued confirmation must not follow a decline.
        await ExecAsync(conn, tx, "DELETE FROM channel_message WHERE channel_id = $1 AND sent_at IS NULL", ct, m.ChannelId);
        await ExecAsync(conn, tx, "INSERT INTO declined_address (subscriber, address_hash, created_at) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING",
            ct, m.Subscriber, DeclinedHash(m.Subscriber, m.Target), now);
        await tx.CommitAsync(ct);
        return new(PageOutcome.Done, m.Language);
    }

    // The mail client's unsubscribe (FR-010, RFC 8058). The page follows the subscriber's current language.
    public async Task<PageResult> UnsubscribeAsync(string token, CancellationToken ct)
    {
        if (!TokenShape().IsMatch(token)) return new(PageOutcome.NotFound, "en");
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var find = new NpgsqlCommand("""
            SELECT ch.id, ch.status, a.language FROM channel ch JOIN account a ON a.id = ch.subscriber
            WHERE ch.unsubscribe_token = $1 FOR UPDATE OF ch
            """, conn, tx)
        { Parameters = { new() { Value = token } } };
        long id;
        string status, language;
        await using (var r = await find.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return new(PageOutcome.NotFound, "en");
            (id, status, language) = (r.GetInt64(0), r.GetString(1), r.GetString(2));
        }
        if (status is not ("active" or "paused" or "failing")) return new(PageOutcome.AlreadyDone, language);
        await StopAsync(conn, tx, id, "unsubscribed", time.GetUtcNow(), ct);
        await tx.CommitAsync(ct);
        return new(PageOutcome.Done, language);
    }

    // The language of a /n/ page before any action: the email's for a confirmation token, the subscriber's current one for
    // an unsubscribe token. Null when the token is unknown.
    public async Task<string?> PageLanguageAsync(bool unsubscribe, string token, CancellationToken ct)
    {
        if (!TokenShape().IsMatch(token)) return null;
        await using var cmd = db.CreateCommand(unsubscribe
            ? "SELECT a.language FROM channel ch JOIN account a ON a.id = ch.subscriber WHERE ch.unsubscribe_token = $1"
            : "SELECT language FROM channel_message WHERE token_hash = $1");
        cmd.Parameters.Add(new() { Value = unsubscribe ? token : Hash(token) });
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    public Task<ChannelResult> PauseAsync(long subscriber, long id, CancellationToken ct) =>
        TransitionAsync(subscriber, id, ct, async (conn, tx, row, now) =>
        {
            if (row.Status != "active") return "invalid_status";
            await StopAsync(conn, tx, id, "paused", now, ct);
            return null;
        });

    public Task<ChannelResult> ResumeAsync(long subscriber, long id, CancellationToken ct) =>
        TransitionAsync(subscriber, id, ct, async (conn, tx, row, now) =>
        {
            if (row.Status != "paused") return "invalid_status";
            await ExecAsync(conn, tx, """
                UPDATE channel SET status = 'active', status_since = $2, activated_at = $2, failed_attempts = 0, next_attempt_at = NULL WHERE id = $1
                """, ct, id, now);
            return null;
        });

    // FR-029: the cascade removes its deliveries and messages.
    public async Task<ChannelResult> RemoveAsync(long subscriber, long id, CancellationToken ct)
    {
        // The account address goes only with the account or a confirmed email change (004).
        await using var cmd = db.CreateCommand("""
            WITH d AS (DELETE FROM channel WHERE id = $1 AND subscriber = $2 AND NOT is_account_address RETURNING 1)
            SELECT (SELECT count(*) FROM d), EXISTS (SELECT 1 FROM channel WHERE id = $1 AND subscriber = $2 AND is_account_address)
            """);
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = subscriber });
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await rd.ReadAsync(ct);
        return rd.GetInt64(0) > 0 ? new(null, null) : Fail(rd.GetBoolean(1) ? "account_address" : "not_found");
    }

    // FR-006, research R10: at most 3 confirmation emails per channel per 24 h, the initial one included.
    public Task<ChannelResult> RequestConfirmationAsync(long subscriber, long id, CancellationToken ct) =>
        TransitionAsync(subscriber, id, ct, async (conn, tx, row, now) =>
        {
            if (row.Status is not ("awaitingConfirmation" or "unsubscribed" or "failing")) return "invalid_status";
            if (Type(row.Type) is not { Configured: true }) return "type_unavailable";
            if (await DeclinedAsync(conn, tx, subscriber, row.Target, ct)) return "target_declined";
            if (await CountAsync(conn, tx, id, "confirmation", now, ct) >= MaxConfirmations) return "confirmation_limit";
            await QueueAsync(conn, tx, id, "confirmation", now, ct);
            return null;
        }, keepStatus: true);

    // FR-023: at most 5 test messages per channel per 24 h, to an active channel only.
    public Task<ChannelResult> RequestTestAsync(long subscriber, long id, CancellationToken ct) =>
        TransitionAsync(subscriber, id, ct, async (conn, tx, row, now) =>
        {
            if (row.Status != "active") return "channel_not_active";
            if (Type(row.Type) is not { Configured: true }) return "type_unavailable";
            if (await CountAsync(conn, tx, id, "test", now, ct) >= MaxTests) return "test_limit";
            await QueueAsync(conn, tx, id, "test", now, ct);
            return null;
        }, keepStatus: true);

    // FR-029a, research R10. The declined_address rows stay.
    public async Task CleanupAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        foreach (var (sql, arg) in new (string, DateTimeOffset)[]
        {
            ("""
             DELETE FROM channel ch WHERE ch.status IN ('awaitingConfirmation', 'unsubscribed') AND ch.status_since < $1 AND NOT ch.is_account_address
               AND NOT EXISTS (SELECT 1 FROM channel_message m WHERE m.channel_id = ch.id AND m.kind = 'confirmation' AND m.sent_at >= $1)
             """, now - Unused),
            ("DELETE FROM channel_message WHERE requested_at < $1", now - Unused),
            ("DELETE FROM channel_message WHERE sent_at IS NULL AND requested_at < $1", now - settings.Options.Notifications.RetryWindow),
        })
        {
            await using var cmd = db.CreateCommand(sql);
            cmd.Parameters.Add(new() { Value = arg });
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // 004 FR-028, research R15: a confirmed account gets its own address as an active email channel. It counts toward the
    // limit, and an unconfirmed account owns no channels, so there is always room.
    public static Task AddAccountAddressAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, string email, DateTimeOffset now,
                                              CancellationToken ct) =>
        ExecAsync(conn, tx, """
            INSERT INTO channel (subscriber, type, target, status, status_since, activated_at, unsubscribe_token, created_at, is_account_address)
            VALUES ($1, 'email', $2, 'active', $3, $3, $4, $3, true)
            """, ct, accountId, email.Trim(), now, NewToken());

    // FR-023, research R15: the account's own channel follows its new address. A separate channel for that address goes
    // first, with its pending deliveries, so the unique target index holds. Without a flagged channel nothing moves.
    public static async Task MoveAccountAddressAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, string newEmail, CancellationToken ct)
    {
        await using (var find = new NpgsqlCommand("SELECT 1 FROM channel WHERE subscriber = $1 AND is_account_address", conn, tx)
        { Parameters = { new() { Value = accountId } } })
            if (await find.ExecuteScalarAsync(ct) is null) return;
        await ExecAsync(conn, tx, "DELETE FROM channel WHERE subscriber = $1 AND NOT is_account_address AND type = 'email' AND lower(target) = lower($2)",
            ct, accountId, newEmail);
        await ExecAsync(conn, tx, "UPDATE channel SET target = $2 WHERE subscriber = $1 AND is_account_address", ct, accountId, newEmail);
    }

    // Null when the subscriber has no subscription to the calendar.
    public async Task<SubscriptionNotifications?> GetSubscriptionAsync(long subscriber, long calendarId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT coalesce(ss.notify, true), ss.label, coalesce(ss.label, c.feed_name, c.host)
            FROM subscription s JOIN calendar c ON c.id = s.calendar_id
            LEFT JOIN subscription_setting ss ON ss.calendar_id = s.calendar_id AND ss.subscriber = s.subscriber
            WHERE s.subscriber = $1 AND s.calendar_id = $2
            """);
        cmd.Parameters.Add(new() { Value = subscriber });
        cmd.Parameters.Add(new() { Value = calendarId });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2)) : null;
    }

    // Body { notify?, label? } (contracts/api.md); a missing field keeps its value, a null label clears it.
    public async Task<(SubscriptionNotifications? Value, string? Error)> SetSubscriptionAsync(long subscriber, long calendarId, JsonObject body, CancellationToken ct)
    {
        var (hasNotify, hasLabel) = (body.ContainsKey("notify"), body.ContainsKey("label"));
        if (!hasNotify && !hasLabel) return (null, "empty_body");
        bool? notify = null;
        if (hasNotify)
        {
            if (body["notify"] is not JsonValue v || v.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) return (null, "invalid_notify");
            notify = v.GetValue<bool>();
        }
        string? label = null;
        if (hasLabel && body["label"] is not null)
        {
            label = FilterRules.Text(body["label"]);
            if (label is not { Length: >= 1 and <= 100 } || label.Any(char.IsControl)) return (null, "invalid_label");
        }

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var upsert = new NpgsqlCommand("""
            INSERT INTO subscription_setting (calendar_id, subscriber, notify, label)
            SELECT $1, $2, coalesce($3, true), $4 WHERE EXISTS (SELECT 1 FROM calendar_link WHERE calendar_id = $1 AND subscriber = $2)
            ON CONFLICT (calendar_id, subscriber) DO UPDATE
            SET notify = coalesce($3, subscription_setting.notify), label = CASE WHEN $5 THEN excluded.label ELSE subscription_setting.label END
            """, conn, tx))
        {
            upsert.Parameters.Add(new() { Value = calendarId });
            upsert.Parameters.Add(new() { Value = subscriber });
            upsert.Parameters.Add(new() { Value = (object?)notify ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean });
            upsert.Parameters.Add(new() { Value = (object?)label ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            upsert.Parameters.Add(new() { Value = hasLabel });
            if (await upsert.ExecuteNonQueryAsync(ct) == 0) return (null, "not_found");
        }
        // Off: pending changes of this calendar are skipped on every channel of the subscriber. On creates nothing backwards.
        if (notify == false)
            await ExecAsync(conn, tx, """
                UPDATE delivery d SET status = 'skipped', reason = 'notificationsOff', status_at = $3
                FROM channel ch, detected_change c
                WHERE d.status = 'pending' AND ch.id = d.channel_id AND ch.subscriber = $1 AND c.id = d.change_id AND c.calendar_id = $2
                """, ct, subscriber, calendarId, time.GetUtcNow());
        try { await tx.CommitAsync(ct); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation) { return (null, "not_found"); } // link removed meanwhile
        return (await GetSubscriptionAsync(subscriber, calendarId, ct), null);
    }

    // The account's language and time zone (004 research R1); no time zone means the operator default (FR-018a).
    public async Task<NotificationSettings> GetSettingsAsync(long subscriber, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT language, time_zone FROM account WHERE id = $1");
        cmd.Parameters.Add(new() { Value = subscriber });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var (language, zone) = (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
        return new(language, zone, zone ?? settings.DefaultTimeZone.Id);
    }

    // Body { language?, timeZone? }; a null time zone clears it. A rejected request keeps the previous values.
    public async Task<(NotificationSettings? Value, string? Error)> SetSettingsAsync(long subscriber, JsonObject body, CancellationToken ct)
    {
        var (hasLanguage, hasZone) = (body.ContainsKey("language"), body.ContainsKey("timeZone"));
        if (!hasLanguage && !hasZone) return (null, "empty_body");
        var language = hasLanguage ? FilterRules.Text(body["language"]) : null;
        if (hasLanguage && language is not ("en" or "nl")) return (null, "invalid_language");
        string? zone = null;
        if (hasZone && body["timeZone"] is not null)
        {
            if (FilterRules.Text(body["timeZone"]) is not { } id || Texts.FindTimeZone(id) is not { } z) return (null, "invalid_time_zone");
            zone = z.Id;
        }
        await using var cmd = db.CreateCommand("""
            UPDATE account SET language = coalesce($2, language), time_zone = CASE WHEN $4 THEN $3 ELSE time_zone END WHERE id = $1
            """);
        cmd.Parameters.Add(new() { Value = subscriber });
        cmd.Parameters.Add(new() { Value = (object?)language ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = (object?)zone ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = hasZone });
        await cmd.ExecuteNonQueryAsync(ct);
        return (await GetSettingsAsync(subscriber, ct), null);
    }

    // Sets a status that ends sending and skips the pending changes (pause, unsubscribe, decline).
    static Task StopAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long id, string status, DateTimeOffset now, CancellationToken ct) =>
        ExecAsync(conn, tx, """
            WITH ch AS (UPDATE channel SET status = $2, status_since = $3, failure_reason = NULL WHERE id = $1)
            UPDATE delivery SET status = 'skipped', reason = 'channelNotActive', status_at = $3 WHERE channel_id = $1 AND status = 'pending'
            """, ct, id, status, now);

    // Locks the subscriber's channel, runs one lifecycle step and returns the channel as it is after it.
    async Task<ChannelResult> TransitionAsync(long subscriber, long id, CancellationToken ct,
        Func<NpgsqlConnection, NpgsqlTransaction, ChannelRow, DateTimeOffset, Task<string?>> step, bool keepStatus = false)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await LockAsync(conn, tx, subscriber, id, ct) is not { } row) return Fail("not_found");
        var now = time.GetUtcNow();
        if (await step(conn, tx, row, now) is { } error) return new(row, error);
        if (!keepStatus) await ExecAsync(conn, tx, "UPDATE channel SET status_since = $2 WHERE id = $1", ct, id, now);
        var after = await LockAsync(conn, tx, subscriber, id, ct);
        await tx.CommitAsync(ct);
        return new(after, null);
    }

    static async Task<ChannelRow?> LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long subscriber, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {Columns} FROM channel WHERE id = $1 AND subscriber = $2 FOR UPDATE", conn, tx)
        { Parameters = { new() { Value = id }, new() { Value = subscriber } } };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    sealed record TokenMessage(long Id, long ChannelId, DateTimeOffset? UsedAt, DateTimeOffset? ExpiresAt, string Language,
                               string Status, string Type, long Subscriber, string Target);

    // The sent confirmation a token belongs to, with its channel locked.
    static async Task<TokenMessage?> MessageAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string token, CancellationToken ct)
    {
        if (!TokenShape().IsMatch(token)) return null;
        await using var cmd = new NpgsqlCommand("""
            SELECT m.id, m.channel_id, m.used_at, m.expires_at, m.language, ch.status, ch.type, ch.subscriber, ch.target
            FROM channel_message m JOIN channel ch ON ch.id = m.channel_id
            WHERE m.token_hash = $1 FOR UPDATE
            """, conn, tx)
        { Parameters = { new() { Value = Hash(token) } } };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
            r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7), r.GetString(8));
    }

    static async Task<bool> DeclinedAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long subscriber, string address, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM declined_address WHERE subscriber = $1 AND address_hash = $2", conn, tx)
        { Parameters = { new() { Value = subscriber }, new() { Value = DeclinedHash(subscriber, address) } } };
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    static async Task<long> CountAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long id, string kind, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM channel_message WHERE channel_id = $1 AND kind = $2 AND requested_at > $3", conn, tx)
        { Parameters = { new() { Value = id }, new() { Value = kind }, new() { Value = now - Day } } };
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    static Task QueueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long id, string kind, DateTimeOffset now, CancellationToken ct) =>
        ExecAsync(conn, tx, "INSERT INTO channel_message (channel_id, kind, requested_at) VALUES ($1, $2, $3)", ct, id, kind, now);

    static ChannelRow Read(NpgsqlDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
            r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.GetFieldValue<DateTimeOffset>(7), r.GetBoolean(8));

    static ChannelResult Fail(string reason) => new(null, reason);

    static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
