using Npgsql;
using NpgsqlTypes;

namespace InTheKnow;

sealed record TypeStatus(string Type, bool Configured, DateTimeOffset? LastAttemptAt, bool? LastOk, string? LastReason,
                         int ConsecutiveFailures, DateTimeOffset? FailingSince, long Waiting);

// The sender (research R4, R5, R6): per configured type, queued confirmation and test messages first, then one digest per
// due channel. Windows, backoff and expiry derive from detected_at and the app clock. One channel at a time, in one
// transaction that holds its locks across the send, so a state change lands either before a message or after it.
// Logs channel ids, types and reason codes only (FR-031).
sealed class Notifier(NpgsqlDataSource db, IEnumerable<IChannelType> types, Presets presets, Channels channels,
                      Settings settings, TimeProvider time, ILogger<Notifier> log) : BackgroundService
{
    const int Listed = 50;
    static readonly TimeSpan IdleSleep = TimeSpan.FromSeconds(1), ExpiryEvery = TimeSpan.FromMinutes(1), CleanupEvery = TimeSpan.FromMinutes(10);
    static readonly TimeSpan ConfirmationValidity = TimeSpan.FromHours(48), MaxBackoff = TimeSpan.FromHours(1);

    // In memory per type (research R6, R10): the rate slot, the type-wide backoff, the batch of due channels and the operator status.
    sealed class TypeState
    {
        public DateTimeOffset NextFree = DateTimeOffset.MinValue, NextTry = DateTimeOffset.MinValue;
        public int Failures;
        public readonly Queue<long> Due = new();
        public DateTimeOffset? LastAttemptAt, FailingSince;
        public bool? LastOk;
        public string? LastReason;
        public int ConsecutiveFailures;
    }

    readonly Dictionary<string, TypeState> states = types.ToDictionary(t => t.Name, _ => new TypeState());
    DateTimeOffset nextExpiry = DateTimeOffset.MinValue, nextCleanup = DateTimeOffset.MinValue;
    NotificationsOptions O => settings.Options.Notifications;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueAsync(time.GetUtcNow(), stoppingToken);
                await Task.Delay(IdleSleep, time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                log.LogError("Notifier pass failed with {ExceptionType}", e.GetType().Name);
                await Task.Delay(IdleSleep, time, stoppingToken);
            }
        }
    }

    // One pass at `now`; the test seam.
    public async Task RunDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (now >= nextExpiry)
        {
            await ExpireAsync(now, ct);
            nextExpiry = now + ExpiryEvery;
        }
        if (now >= nextCleanup)
        {
            await channels.CleanupAsync(now, ct);
            nextCleanup = now + CleanupEvery;
        }
        foreach (var type in types.Where(t => t.Configured)) await RunTypeAsync(type, states[type.Name], now, ct);
    }

    public async Task<IReadOnlyList<TypeStatus>> StatusAsync(CancellationToken ct)
    {
        var result = new List<TypeStatus>();
        foreach (var type in types.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            await using var cmd = db.CreateCommand("""
                SELECT (SELECT count(DISTINCT d.channel_id) FROM delivery d JOIN channel ch ON ch.id = d.channel_id WHERE d.status = 'pending' AND ch.type = $1)
                     + (SELECT count(*) FROM channel_message m JOIN channel ch ON ch.id = m.channel_id WHERE m.sent_at IS NULL AND ch.type = $1)
                """);
            cmd.Parameters.Add(new() { Value = type.Name });
            var waiting = (long)(await cmd.ExecuteScalarAsync(ct))!;
            var s = states[type.Name];
            lock (s)
                result.Add(new(type.Name, type.Configured, s.LastAttemptAt, s.LastOk, s.LastReason, s.ConsecutiveFailures, s.FailingSince, waiting));
        }
        return result;
    }

    // FR-026: every type, configured or not (edge case "Channel type configured off").
    async Task ExpireAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE delivery d SET status = 'expired', status_at = $1 FROM detected_change c
            WHERE d.status = 'pending' AND c.id = d.change_id AND c.detected_at < $2
            """);
        cmd.Parameters.Add(new() { Value = now });
        cmd.Parameters.Add(new() { Value = now - O.RetryWindow });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    async Task RunTypeAsync(IChannelType type, TypeState s, DateTimeOffset now, CancellationToken ct)
    {
        if (now < s.NextTry)
        {
            s.Due.Clear(); // the next query orders them again
            return;
        }
        // Queued messages take their slots before digests (research R6).
        foreach (var id in await IdsAsync("""
            SELECT m.id FROM channel_message m JOIN channel ch ON ch.id = m.channel_id
            WHERE m.sent_at IS NULL AND ch.type = $1 AND (ch.next_attempt_at IS NULL OR ch.next_attempt_at <= $2)
            ORDER BY ch.failed_attempts, m.requested_at LIMIT 100
            """, ct, type.Name, now))
        {
            if (s.NextFree > now) return;
            if (await GuardAsync(type, s, id, now, ct, SendQueuedAsync) == Stop.Type) return;
        }

        // ponytail: the due query groups every pending row; add a stored per-channel due time if it shows up in profiles (research R6)
        if (s.Due.Count == 0)
            foreach (var id in await IdsAsync("""
                SELECT d.channel_id FROM delivery d JOIN detected_change c ON c.id = d.change_id JOIN channel ch ON ch.id = d.channel_id
                WHERE d.status = 'pending' AND ch.type = $1 AND (ch.next_attempt_at IS NULL OR ch.next_attempt_at <= $2)
                GROUP BY d.channel_id, ch.failed_attempts HAVING min(c.detected_at) <= $3 ORDER BY ch.failed_attempts, min(c.detected_at) LIMIT 100
                """, ct, type.Name, now, now - O.CollectionWindow))
                s.Due.Enqueue(id);
        while (s.Due.Count > 0 && s.NextFree <= now)
            if (await GuardAsync(type, s, s.Due.Dequeue(), now, ct, SendDigestAsync) == Stop.Type) return;
    }

    enum Stop { None, Type }

    delegate Task<(long ChannelId, SendResult Result)?> Step(IChannelType type, TypeState s, long id, DateTimeOffset now, CancellationToken ct);

    // Runs one message step and applies its result. A database error rolls back that one channel and counts as no attempt.
    async Task<Stop> GuardAsync(IChannelType type, TypeState s, long id, DateTimeOffset now, CancellationToken ct, Step step)
    {
        try
        {
            if (await step(type, s, id, now, ct) is not var (channelId, result)) return Stop.None;
            return await ApplyAsync(type, s, channelId, result, now, ct);
        }
        catch (NpgsqlException e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Notifier ({Type}): database error {ExceptionType}", type.Name, e.GetType().Name);
            return Stop.None;
        }
    }

    // A confirmation or test message (research R7). The confirmation token is created here, right before sending.
    async Task<(long, SendResult)?> SendQueuedAsync(IChannelType type, TypeState s, long id, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var find = new NpgsqlCommand("""
            SELECT m.kind, m.channel_id, ch.status, ch.target, ch.unsubscribe_token, ch.next_attempt_at, m.sent_at IS NOT NULL,
                   coalesce(ss.language, 'en'), ss.time_zone
            FROM channel_message m JOIN channel ch ON ch.id = m.channel_id LEFT JOIN subscriber_setting ss ON ss.subscriber = ch.subscriber
            WHERE m.id = $1 FOR NO KEY UPDATE OF ch FOR UPDATE OF m
            """, conn, tx)
        { Parameters = { new() { Value = id } } };
        string kind, status, target, unsubscribe, language;
        long channelId;
        TimeZoneInfo zone;
        await using (var r = await find.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct) || r.GetBoolean(6)) return null;
            if (!r.IsDBNull(5) && r.GetFieldValue<DateTimeOffset>(5) > now) return null;
            (kind, channelId, status, target, unsubscribe, language) = (r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(7));
            zone = Zone(r.IsDBNull(8) ? null : r.GetString(8));
        }
        if (kind == "confirmation" ? status is "active" or "paused" : status != "active")
        {
            await ExecAsync(conn, tx, "DELETE FROM channel_message WHERE id = $1", ct, id); // no longer wanted
            await tx.CommitAsync(ct);
            return null;
        }

        Message message;
        if (kind == "confirmation")
        {
            var token = Channels.NewToken();
            await ExecAsync(conn, tx, "UPDATE channel_message SET sent_at = $2, language = $3, token_hash = $4, expires_at = $5 WHERE id = $1",
                ct, id, now, language, Channels.Hash(token), now + ConfirmationValidity);
            message = new(MessageKind.Confirmation, language, zone, $"{O.PublicBaseUrl}/n/confirm/{token}", $"{O.PublicBaseUrl}/n/decline/{token}",
                null, null, [], 0);
        }
        else
        {
            await ExecAsync(conn, tx, "UPDATE channel_message SET sent_at = $2, language = $3 WHERE id = $1", ct, id, now, language);
            message = new(MessageKind.Test, language, zone, null, null, Unsubscribe(unsubscribe), $"{O.FrontendBaseUrl}/notifications", [], 0);
        }
        var result = await SendAsync(type, s, target, message, now, ct);
        if (result.Status == SendStatus.Sent)
        {
            await SentAsync(conn, tx, channelId, now, ct);
            await tx.CommitAsync(ct);
        }
        return (channelId, result);
    }

    // One digest for a due channel (research R5): lock, re-check at send time, evaluate the filter, send, mark.
    // ponytail: a row lock and a connection are held across one send (at most the 30 s timeout); split into claim/commit if API latency during outages matters (research R5)
    async Task<(long, SendResult)?> SendDigestAsync(IChannelType type, TypeState s, long channelId, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var find = new NpgsqlCommand("""
            SELECT ch.subscriber, ch.status, ch.target, ch.unsubscribe_token, ch.next_attempt_at, coalesce(ss.language, 'en'), ss.time_zone
            FROM channel ch LEFT JOIN subscriber_setting ss ON ss.subscriber = ch.subscriber
            WHERE ch.id = $1 AND ch.type = $2 FOR NO KEY UPDATE OF ch
            """, conn, tx)
        { Parameters = { new() { Value = channelId }, new() { Value = type.Name } } };
        string subscriber, status, target, unsubscribe, language;
        TimeZoneInfo zone;
        await using (var r = await find.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null; // removed meanwhile
            if (!r.IsDBNull(4) && r.GetFieldValue<DateTimeOffset>(4) > now) return null;
            (subscriber, status, target, unsubscribe, language) = (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(5));
            zone = Zone(r.IsDBNull(6) ? null : r.GetString(6));
        }

        var pending = new List<ChangeView>();
        await using (var cmd = new NpgsqlCommand($"""
            SELECT {ChangeView.Columns} FROM delivery d JOIN detected_change c ON c.id = d.change_id
            WHERE d.channel_id = $1 AND d.status = 'pending' ORDER BY d.change_id FOR UPDATE OF d
            """, conn, tx)
        { Parameters = { new() { Value = channelId } } })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) pending.Add(ChangeView.Read(r));
        // A stale batch entry: its changes were sent meanwhile, and newer ones start their own window.
        if (pending.Count == 0 || pending.Min(c => c.DetectedAt) > now - O.CollectionWindow) return null;

        var filters = await presets.LoadFiltersAsync(subscriber, ct);
        var calendars = new Dictionary<long, (string Name, bool Notify)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT s.calendar_id, coalesce(ss.label, c.feed_name, c.host), coalesce(ss.notify, true)
            FROM subscription s JOIN calendar c ON c.id = s.calendar_id
            LEFT JOIN subscription_setting ss ON ss.calendar_id = s.calendar_id AND ss.subscriber = s.subscriber
            WHERE s.subscriber = $1
            """, conn, tx)
        { Parameters = { new() { Value = subscriber } } })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) calendars[r.GetInt64(0)] = (r.GetString(1), r.GetBoolean(2));

        // The send-time re-check (FR-011, FR-012), in this order.
        var skipped = new List<(long Id, string Reason)>();
        var selected = new List<ChangeItem>();
        foreach (var c in pending)
        {
            string? reason = status != "active" ? "channelNotActive"
                : calendars.TryGetValue(c.CalendarId, out var cal) && !cal.Notify ? "notificationsOff"
                : !filters.ContainsKey(c.CalendarId) ? "subscriptionRemoved"
                : null;
            var outcome = reason is null ? FilterEngine.Outcome(filters[c.CalendarId].Filter.Rules, c, filters[c.CalendarId].Active) : Outcome.Normal;
            if (outcome == Outcome.Hidden) reason = "hidden";
            if (reason is null) selected.Add(new(c, outcome == Outcome.LowPriority));
            else skipped.Add((c.Id, reason));
        }
        await ExecAsync(conn, tx, """
            UPDATE delivery d SET status = 'skipped', reason = u.reason, status_at = $2
            FROM unnest($3::bigint[], $4::text[]) u(id, reason) WHERE d.channel_id = $1 AND d.change_id = u.id
            """, ct, channelId, now, skipped.Select(x => x.Id).ToArray(), skipped.Select(x => x.Reason).ToArray());
        if (selected.Count == 0)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        // FR-015: the first 50 in detection order are listed; all of them count as sent.
        var groups = selected.Take(Listed).GroupBy(i => i.Change.CalendarId)
            .Select(g => new CalendarGroup(g.Key, calendars.GetValueOrDefault(g.Key).Name ?? "", $"{O.FrontendBaseUrl}/calendars/{g.Key}/notifications", [.. g]))
            .ToList();
        var message = new Message(MessageKind.Changes, language, zone, null, null, Unsubscribe(unsubscribe), null, groups,
            Math.Max(0, selected.Count - Listed));
        var result = await SendAsync(type, s, target, message, now, ct);
        if (result.Status == SendStatus.Sent)
        {
            await ExecAsync(conn, tx, "UPDATE delivery SET status = 'sent', status_at = $2 WHERE channel_id = $1 AND change_id = ANY($3)",
                ct, channelId, now, selected.Select(i => i.Change.Id).ToArray());
            await SentAsync(conn, tx, channelId, now, ct);
            await tx.CommitAsync(ct);
        }
        return (channelId, result);
    }

    // Takes the type's next rate slot (FR-016), then sends. Any exception but cancellation counts as a target failure.
    async Task<SendResult> SendAsync(IChannelType type, TypeState s, string target, Message message, DateTimeOffset now, CancellationToken ct)
    {
        // ponytail: single sequential sender, one connection per message; add per-type workers or MailKit when the limit exceeds what the loop reaches (research R6)
        s.NextFree = (s.NextFree > now ? s.NextFree : now) + TimeSpan.FromMinutes(1) / type.MaxMessagesPerMinute;
        try { return await type.SendAsync(target, message, ct); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning("Notifier ({Type}): send threw {ExceptionType}", type.Name, e.GetType().Name);
            return new(SendStatus.TargetFailed, "exception");
        }
    }

    static Task SentAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long channelId, DateTimeOffset now, CancellationToken ct) =>
        ExecAsync(conn, tx, "UPDATE channel SET last_message_at = $2, failed_attempts = 0, next_attempt_at = NULL WHERE id = $1", ct, channelId, now);

    // The shared handling of a send result (contracts/channel-type.md). The send transaction was already committed or rolled back.
    async Task<Stop> ApplyAsync(IChannelType type, TypeState s, long channelId, SendResult result, DateTimeOffset now, CancellationToken ct)
    {
        lock (s)
        {
            s.LastAttemptAt = now;
            s.LastOk = result.Status == SendStatus.Sent;
            s.LastReason = result.Reason;
            if (result.Status == SendStatus.Sent) (s.ConsecutiveFailures, s.FailingSince) = (0, null);
            else (s.ConsecutiveFailures, s.FailingSince) = (s.ConsecutiveFailures + 1, s.FailingSince ?? now);
        }
        if (result.Status == SendStatus.Sent)
        {
            (s.Failures, s.NextTry) = (0, DateTimeOffset.MinValue);
            return Stop.None;
        }
        log.LogWarning("Channel {ChannelId} ({Type}): send failed with {Reason}", channelId, type.Name, result.Reason);
        await using var conn = await db.OpenConnectionAsync(ct);
        if (result.Status == SendStatus.TargetRejected)
        {
            // FR-027: this target will never work.
            await using var tx = await conn.BeginTransactionAsync(ct);
            await ExecAsync(conn, tx, """
                WITH ch AS (UPDATE channel SET status = 'failing', status_since = $2, failure_reason = $3 WHERE id = $1)
                UPDATE delivery SET status = 'expired', status_at = $2 WHERE channel_id = $1 AND status = 'pending'
                """, ct, channelId, now, result.Reason ?? "rejected");
            await ExecAsync(conn, tx, "DELETE FROM channel_message WHERE channel_id = $1 AND sent_at IS NULL", ct, channelId);
            await tx.CommitAsync(ct);
            return Stop.None;
        }
        // A channel backoff for TargetFailed, and for TypeUnavailable too, so a single bad message cannot stall the type (research R4).
        await ExecAsync(conn, null, """
            UPDATE channel SET failed_attempts = failed_attempts + 1,
                next_attempt_at = $2 + least(interval '1 minute' * power(2, least(failed_attempts, 10)), interval '1 hour')
            WHERE id = $1
            """, ct, channelId, now);
        if (result.Status != SendStatus.TypeUnavailable) return Stop.None;
        s.Failures++;
        s.NextTry = now + Backoff(s.Failures);
        s.Due.Clear();
        return Stop.Type;
    }

    static TimeSpan Backoff(int failures) =>
        TimeSpan.FromMinutes(Math.Pow(2, Math.Min(failures - 1, 10))) is var d && d < MaxBackoff ? d : MaxBackoff;

    TimeZoneInfo Zone(string? id) => id is not null && Texts.FindTimeZone(id) is { } z ? z : settings.DefaultTimeZone;

    string Unsubscribe(string token) => $"{O.PublicBaseUrl}/n/unsubscribe/{token}";

    async Task<List<long>> IdsAsync(string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = db.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var ids = new List<long>();
        while (await r.ReadAsync(ct)) ids.Add(r.GetInt64(0));
        return ids;
    }

    static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.Add(a is string[] s ? new() { Value = s, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text } : new() { Value = a });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
