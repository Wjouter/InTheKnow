using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace InTheKnow;

// The dispatcher (claim + lease, worker slots, 2-minute limit), the check transaction and retention
// (research R4, R14, data-model.md § One check). Logs ids and reason codes only, never links (FR-028).
sealed class Checker(NpgsqlDataSource db, Fetcher fetcher, HostBudget budget, Settings settings, TimeProvider time, ILogger<Checker> log)
    : BackgroundService
{
    static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);
    static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    static readonly TimeSpan IdleSleep = TimeSpan.FromSeconds(1);
    static readonly TimeSpan RetentionEvery = TimeSpan.FromMinutes(10);
    const int RetentionBatch = 10_000;

    readonly SemaphoreSlim slots = new(settings.Options.MaxConcurrentChecks);
    InTheKnowOptions O => settings.Options;

    // Test seam: runs after parsing, right before the outcome transaction.
    internal Func<long, CancellationToken, Task>? BeforeWriteHook;

    sealed class Claim(long id, string host, Guid lease, DateTimeOffset startedAt)
    {
        public long Id => id;
        public string Host => host;
        public Guid Lease => lease;
        public DateTimeOffset StartedAt => startedAt;
        public long? LinkId; // set once the check picked its fetch link
    }

    sealed record Fence(string Status, string? BaselineFeatures, byte[]? SnapshotHash, DateTimeOffset? HorizonEnd, bool HasSnapshot);

    // Runs dispatch passes at `now` until nothing is claimable, then awaits the checks it started.
    public async Task RunDueChecksAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var started = new List<Task>();
        while (await TryStartOneAsync(now, ct) is { } check) started.Add(check);
        await Task.WhenAll(started);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextRetention = time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            try
            {
                if (now >= nextRetention)
                {
                    await RunRetentionAsync(now, stoppingToken);
                    nextRetention = now + RetentionEvery;
                }
                if (await TryStartOneAsync(now, stoppingToken) is not null) continue;
                var wait = budget.EarliestFree(now) is { } free && free - now < IdleSleep ? free - now : IdleSleep;
                await Task.Delay(wait, time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                log.LogError("Dispatcher pass failed with {ExceptionType}", e.GetType().Name);
                await Task.Delay(IdleSleep, time, stoppingToken);
            }
        }
    }

    // One pass: a worker slot, then the longest-waiting due calendar on a host with a free budget slot.
    async Task<Task?> TryStartOneAsync(DateTimeOffset now, CancellationToken stoppingToken)
    {
        await slots.WaitAsync(stoppingToken);
        Claim? claim;
        try { claim = await ClaimAsync(now, budget.BusyHosts(now), stoppingToken); }
        catch
        {
            slots.Release();
            throw;
        }
        if (claim is null)
        {
            slots.Release();
            return null;
        }
        var slot = budget.TryReserve(claim.Host, now, TimeSpan.MaxValue)!.Value;
        return RunGuardedAsync(claim, slot, stoppingToken);
    }

    async Task<Claim?> ClaimAsync(DateTimeOffset now, string[] busyHosts, CancellationToken ct)
    {
        var lease = Guid.NewGuid();
        // ponytail: while the main host is busy, a pass that finds nothing walks its whole due backlog (host <> ALL can't use an index); ms at thousands of rows, add a per-host queue if it shows up in profiles
        await using var cmd = db.CreateCommand("""
            UPDATE calendar SET next_check_at = $1, lease_id = $2
            WHERE id = (SELECT id FROM calendar WHERE next_check_at <= $3 AND host <> ALL($4)
                        ORDER BY next_check_at, id LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING id, host
            """);
        cmd.Parameters.Add(new() { Value = now + Lease });
        cmd.Parameters.Add(new() { Value = lease });
        cmd.Parameters.Add(new() { Value = now });
        cmd.Parameters.Add(new() { Value = busyHosts });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new Claim(r.GetInt64(0), r.GetString(1), lease, now) : null;
    }

    async Task RunGuardedAsync(Claim claim, DateTimeOffset slot, CancellationToken stoppingToken)
    {
        var released = 0;
        void Release()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) slots.Release();
        }
        try
        {
            if (slot > claim.StartedAt) await Task.Delay(slot - claim.StartedAt, time, stoppingToken);
            using var limit = new CancellationTokenSource(Limit, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, stoppingToken);
            try
            {
                await CheckAsync(claim, linked.Token).WaitAsync(Limit, time, stoppingToken);
            }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException && !stoppingToken.IsCancellationRequested)
            {
                // The abandoned check may still be running; its later writes fail the lease fence.
                Release();
                log.LogWarning("Calendar {CalendarId}: check exceeded the time limit", claim.Id);
                await using var conn = await db.OpenConnectionAsync(stoppingToken);
                await using var tx = await conn.BeginTransactionAsync(stoppingToken);
                if (await FenceAsync(conn, tx, claim, stoppingToken) is { } fence)
                {
                    await WriteFailureAsync(conn, tx, claim, fence.Status, "timeout", stoppingToken);
                    await tx.CommitAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception e)
        {
            log.LogError("Calendar {CalendarId}: check failed with {ExceptionType}", claim.Id, e.GetType().Name);
        }
        finally { Release(); }
    }

    async Task CheckAsync(Claim claim, CancellationToken ct)
    {
        (long Id, string Url)? link;
        await using (var read = await db.OpenConnectionAsync(ct)) link = await SelectLinkAsync(read, claim.Id, ct);
        if (link is not { } l) return; // no links: the calendar is being removed
        claim.LinkId = l.Id;

        var fetched = await fetcher.FetchAsync(new Uri(l.Url), ct);
        var horizon = claim.StartedAt.AddMonths(O.ExpansionHorizonMonths);
        var parsed = fetched.Error is null ? FeedParser.Parse(fetched.Body!, horizon, log, claim.Id, ct) : null;
        var error = fetched.Error ?? parsed!.Error;
        List<EventState>? states = null;
        byte[]? hash = null;
        if (error is null)
        {
            var features = settings.FeaturesFor(claim.Host);
            states = [.. parsed!.Events!.Select(o => MyTimetables.ToState(o, features))];
            hash = Differ.SnapshotHash(states);
        }
        ct.ThrowIfCancellationRequested();
        if (BeforeWriteHook is { } hook) await hook(claim.Id, ct);

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await FenceAsync(conn, tx, claim, ct) is not { } fence) return; // lost the lease: write nothing

        // Rows 1 and 2 of data-model.md § One check: failures keep the snapshot.
        if (error is not null)
        {
            log.LogWarning("Calendar {CalendarId}: link {LinkId} failed with {Reason}", claim.Id, l.Id, error);
            await WriteFailureAsync(conn, tx, claim, fence.Status, error, ct);
        }
        else if (states!.Count == 0 && settings.FeaturesFor(claim.Host)?.EmptyFeedProtection == true && fence.HasSnapshot)
        {
            log.LogWarning("Calendar {CalendarId}: link {LinkId} returned an empty feed and is flagged", claim.Id, l.Id);
            await ExecAsync(conn, tx, "UPDATE calendar_link SET flagged_at = $1 WHERE id = $2", ct, claim.StartedAt, l.Id);
            var anyValid = await ScalarAsync(conn, tx,
                "SELECT EXISTS (SELECT 1 FROM calendar_link WHERE calendar_id = $1 AND flagged_at IS NULL)", ct, claim.Id) is true;
            await WriteFailureAsync(conn, tx, claim, anyValid ? fence.Status : "failing", "empty_feed", ct);
        }
        else
        {
            var canonical = settings.CanonicalFeaturesFor(claim.Host);
            var changeCount = 0;
            if (fence.BaselineFeatures != canonical)
            {
                // Row 3: a baseline records no changes.
                await ExecAsync(conn, tx, "DELETE FROM event_snapshot WHERE calendar_id = $1", ct, claim.Id);
                await CopySnapshotAsync(conn, claim.Id, states, ct);
            }
            else if (fence.SnapshotHash is null || !hash!.AsSpan().SequenceEqual(fence.SnapshotHash))
            {
                // Row 5: write only the affected rows. (Row 4, an equal hash, touches no snapshot rows.)
                var diff = Differ.Diff(await LoadSnapshotAsync(conn, tx, claim.Id, ct), states, fence.HorizonEnd, horizon);
                await ApplyDiffAsync(conn, tx, claim, diff, ct);
                changeCount = diff.Changes.Count;
            }
            await ExecAsync(conn, tx, """
                UPDATE calendar SET status = 'ok', consecutive_failures = 0, failing_since = NULL, last_ok = true, last_error = NULL,
                    last_checked_at = $2, last_link_id = $3, last_event_count = $4, last_change_count = $5, snapshot_hash = $6,
                    horizon_end = $7, baseline_features = $8, next_check_at = $9, lease_id = NULL
                WHERE id = $1
                """, ct, claim.Id, claim.StartedAt, l.Id, states.Count, changeCount, hash!, horizon, canonical, claim.StartedAt + O.CheckInterval);
            await ExecAsync(conn, tx, "UPDATE calendar_link SET flagged_at = NULL, last_used_at = $1 WHERE id = $2", ct, claim.StartedAt, l.Id);
        }
        await tx.CommitAsync(ct);
    }

    // FR-008: the oldest valid link while ok; otherwise the link that has waited longest.
    static async Task<(long Id, string Url)?> SelectLinkAsync(NpgsqlConnection conn, long calendarId, CancellationToken ct)
    {
        foreach (var sql in new[]
        {
            """
            SELECT l.id, l.url FROM calendar_link l JOIN calendar c ON c.id = l.calendar_id
            WHERE l.calendar_id = $1 AND c.status = 'ok' AND l.flagged_at IS NULL ORDER BY l.id LIMIT 1
            """,
            "SELECT id, url FROM calendar_link WHERE calendar_id = $1 ORDER BY last_used_at NULLS FIRST, id LIMIT 1",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn) { Parameters = { new() { Value = calendarId } } };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) return (r.GetInt64(0), r.GetString(1));
        }
        return null;
    }

    // The lease fence (research R4): no row means this check lost its lease and must write nothing.
    static async Task<Fence?> FenceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Claim claim, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT status, baseline_features, snapshot_hash, horizon_end, EXISTS (SELECT 1 FROM event_snapshot WHERE calendar_id = $1)
            FROM calendar WHERE id = $1 AND lease_id = $2 FOR UPDATE
            """, conn, tx)
        { Parameters = { new() { Value = claim.Id }, new() { Value = claim.Lease } } };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : (byte[])r[2],
                   r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3), r.GetBoolean(4));
    }

    async Task WriteFailureAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Claim claim, string status, string error, CancellationToken ct)
    {
        await ExecAsync(conn, tx, """
            UPDATE calendar SET last_ok = false, last_error = $2, last_checked_at = $3, last_link_id = COALESCE($4, last_link_id),
                consecutive_failures = consecutive_failures + 1, failing_since = COALESCE(failing_since, $3),
                status = $5, next_check_at = $6, lease_id = NULL
            WHERE id = $1
            """, ct, claim.Id, error, claim.StartedAt, claim.LinkId, status,
            claim.StartedAt + (status == "failing" ? O.FailingCheckInterval : O.CheckInterval));
        if (claim.LinkId is { } linkId)
            await ExecAsync(conn, tx, "UPDATE calendar_link SET last_used_at = $1 WHERE id = $2", ct, claim.StartedAt, linkId);
    }

    static async Task<Dictionary<string, EventState>> LoadSnapshotAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long calendarId, CancellationToken ct)
    {
        var stored = new Dictionary<string, EventState>();
        await using var cmd = new NpgsqlCommand(
            "SELECT event_key, name, starts_at, ends_at, status, occurrence_start, fields::text FROM event_snapshot WHERE calendar_id = $1", conn, tx)
        { Parameters = { new() { Value = calendarId } } };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            stored[r.GetString(0)] = new(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<DateTimeOffset>(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5), JsonNode.Parse(r.GetString(6))!.AsObject());
        return stored;
    }

    static async Task ApplyDiffAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Claim claim, DiffResult diff, CancellationToken ct)
    {
        if (diff.Deletes.Count > 0)
            await ExecAsync(conn, tx, "DELETE FROM event_snapshot WHERE calendar_id = $1 AND event_key = ANY($2)", ct, claim.Id, diff.Deletes.ToArray());
        if (diff.Upserts.Count > 0)
        {
            var u = diff.Upserts;
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO event_snapshot (calendar_id, event_key, name, starts_at, ends_at, status, occurrence_start, fields)
                SELECT $1, * FROM unnest($2::text[], $3::text[], $4::timestamptz[], $5::timestamptz[], $6::text[], $7::timestamptz[], $8::jsonb[])
                ON CONFLICT (calendar_id, event_key) DO UPDATE SET name = excluded.name, starts_at = excluded.starts_at, ends_at = excluded.ends_at,
                    status = excluded.status, occurrence_start = excluded.occurrence_start, fields = excluded.fields
                """, conn, tx);
            cmd.Parameters.Add(new() { Value = claim.Id });
            cmd.Parameters.Add(new() { Value = u.Select(s => s.Key).ToArray() });
            cmd.Parameters.Add(new() { Value = u.Select(s => s.Name).ToArray() });
            cmd.Parameters.Add(new() { Value = u.Select(s => s.StartsAt).ToArray() });
            cmd.Parameters.Add(new() { Value = u.Select(s => s.EndsAt).ToArray() });
            cmd.Parameters.Add(new() { Value = u.Select(s => s.Status).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            cmd.Parameters.Add(new() { Value = u.Select(s => s.OccurrenceStart).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
            cmd.Parameters.Add(new() { Value = u.Select(s => s.Fields.ToJsonString()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (diff.Changes.Count == 0) return;
        await using var w = await conn.BeginBinaryImportAsync("""
            COPY detected_change (calendar_id, event_key, kind, field, old_value, new_value, event_name, event_starts_at, detected_at)
            FROM STDIN (FORMAT BINARY)
            """, ct);
        foreach (var c in diff.Changes)
        {
            await w.StartRowAsync(ct);
            await w.WriteAsync(claim.Id, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(c.EventKey, NpgsqlDbType.Text, ct);
            await w.WriteAsync(c.Kind, NpgsqlDbType.Text, ct);
            await WriteNullableAsync(w, c.Field, NpgsqlDbType.Text, ct);
            await WriteNullableAsync(w, c.OldValue?.ToJsonString(), NpgsqlDbType.Jsonb, ct);
            await WriteNullableAsync(w, c.NewValue?.ToJsonString(), NpgsqlDbType.Jsonb, ct);
            await w.WriteAsync(c.EventName, NpgsqlDbType.Text, ct);
            await w.WriteAsync(c.EventStartsAt, NpgsqlDbType.TimestampTz, ct);
            await w.WriteAsync(claim.StartedAt, NpgsqlDbType.TimestampTz, ct);
        }
        await w.CompleteAsync(ct);
    }

    // Binary COPY of a full snapshot inside the caller's open transaction; add-link reuses it for a new calendar's baseline.
    internal static async Task CopySnapshotAsync(NpgsqlConnection conn, long calendarId, IReadOnlyList<EventState> states, CancellationToken ct)
    {
        await using var w = await conn.BeginBinaryImportAsync(
            "COPY event_snapshot (calendar_id, event_key, name, starts_at, ends_at, status, occurrence_start, fields) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var s in states)
        {
            await w.StartRowAsync(ct);
            await w.WriteAsync(calendarId, NpgsqlDbType.Bigint, ct);
            await w.WriteAsync(s.Key, NpgsqlDbType.Text, ct);
            await w.WriteAsync(s.Name, NpgsqlDbType.Text, ct);
            await w.WriteAsync(s.StartsAt, NpgsqlDbType.TimestampTz, ct);
            await w.WriteAsync(s.EndsAt, NpgsqlDbType.TimestampTz, ct);
            await WriteNullableAsync(w, s.Status, NpgsqlDbType.Text, ct);
            if (s.OccurrenceStart is { } o) await w.WriteAsync(o, NpgsqlDbType.TimestampTz, ct);
            else await w.WriteNullAsync(ct);
            await w.WriteAsync(s.Fields.ToJsonString(), NpgsqlDbType.Jsonb, ct);
        }
        await w.CompleteAsync(ct);
    }

    static Task WriteNullableAsync(NpgsqlBinaryImporter w, string? value, NpgsqlDbType type, CancellationToken ct) =>
        value is null ? w.WriteNullAsync(ct) : w.WriteAsync(value, type, ct);

    // FR-025a: batched, so an expiring burst never becomes one long transaction.
    public async Task RunRetentionAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        int deleted;
        do
        {
            await using var cmd = db.CreateCommand(
                "DELETE FROM detected_change WHERE id IN (SELECT id FROM detected_change WHERE detected_at < $1 LIMIT $2)");
            cmd.Parameters.Add(new() { Value = now - O.ChangeRetention });
            cmd.Parameters.Add(new() { Value = RetentionBatch });
            deleted = await cmd.ExecuteNonQueryAsync(ct);
        } while (deleted >= RetentionBatch);
    }

    static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params object?[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    static async Task<object?> ScalarAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params object?[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a ?? DBNull.Value });
        return await cmd.ExecuteScalarAsync(ct);
    }
}
