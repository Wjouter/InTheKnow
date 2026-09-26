using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace InTheKnow;

sealed record AddLinkResult(long? CalendarId, long? LinkId, bool CalendarIsNew, bool AlreadyExisted, string? Error);

// Adding a link (validate, identity, attach, baseline a new calendar) and removing a subscription.
// Never logs the link (FR-028).
sealed partial class Subscriptions(NpgsqlDataSource db, Fetcher fetcher, HostBudget budget, Settings settings, TimeProvider time)
{
    static readonly TimeSpan MaxValidationWait = TimeSpan.FromSeconds(5);

    [GeneratedRegex("^[A-Za-z0-9._@-]{1,100}$")] public static partial Regex SubscriberPattern();

    static AddLinkResult Fail(string reason) => new(null, null, false, false, reason);

    public async Task<AddLinkResult> AddLinkAsync(string subscriber, string url, CancellationToken ct)
    {
        if (!SubscriberPattern().IsMatch(subscriber)) return Fail("invalid_subscriber");
        var (uri, reason) = await fetcher.CheckUrlAsync(url, ct);
        if (uri is null) return Fail(reason!);
        var link = uri.AbsoluteUri;
        var host = uri.IdnHost.ToLowerInvariant();
        var (key, derived) = LinkIdentity.KeyFor(uri, settings);
        var identity = LinkIdentity.Hash(key);

        await using (var cmd = db.CreateCommand("""
            SELECT c.id, l.id FROM calendar c LEFT JOIN calendar_link l ON l.calendar_id = c.id AND l.subscriber = $2 AND l.url = $3
            WHERE c.identity_hash = $1
            """))
        {
            cmd.Parameters.Add(new() { Value = identity });
            cmd.Parameters.Add(new() { Value = subscriber });
            cmd.Parameters.Add(new() { Value = link });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                if (!r.IsDBNull(1)) return new(r.GetInt64(0), r.GetInt64(1), false, true, null);
            }
            else derived = false; // no existing calendar to join
        }

        // Validation shares the host budget with checks (FR-012a); it waits at most 5 s for a slot.
        var now = time.GetUtcNow();
        if (budget.TryReserve(host, now, MaxValidationWait) is not { } slot) return Fail("host_budget_exhausted");
        if (slot > now) await Task.Delay(slot - now, time, ct);

        var fetched = await fetcher.FetchAsync(uri, ct);
        var horizon = now.AddMonths(settings.Options.ExpansionHorizonMonths);
        var parsed = fetched.Error is null ? FeedParser.Parse(fetched.Body!, horizon, NullLogger.Instance, 0, ct) : null;
        if ((fetched.Error ?? parsed!.Error) is { } error) return Fail("validation_failed:" + error);
        var features = settings.FeaturesFor(host);
        // A forged class-group link (right timetable, junk token) returns an empty feed (research R11, US2-5).
        if (parsed!.Events!.Count == 0 && (features?.EmptyFeedProtection == true || derived)) return Fail("validation_failed:empty_feed");

        var states = parsed.Events.Select(o => MyTimetables.ToState(o, features)).ToList();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        long? created;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO calendar (identity_hash, host, next_check_at, baseline_features, snapshot_hash, horizon_end,
                                  last_checked_at, last_ok, last_event_count, last_change_count, feed_name)
            VALUES ($1, $2, $3, $4, $5, $6, $7, true, $8, 0, $9)
            ON CONFLICT (identity_hash) DO NOTHING RETURNING id
            """, conn, tx))
        {
            foreach (var v in new object[] { identity, host, now + settings.Options.CheckInterval, settings.CanonicalFeaturesFor(host),
                                             Differ.SnapshotHash(states), horizon, now, states.Count })
                insert.Parameters.Add(new() { Value = v });
            insert.Parameters.Add(new() { Value = (object?)FeedParser.CleanName(parsed.FeedName) ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            created = (long?)await insert.ExecuteScalarAsync(ct);
        }
        var calendarId = created ?? (long)(await Scalar(conn, tx, "SELECT id FROM calendar WHERE identity_hash = $1", ct, identity))!;

        var linkId = (long?)await Scalar(conn, tx, """
            INSERT INTO calendar_link (calendar_id, subscriber, url) VALUES ($1, $2, $3)
            ON CONFLICT (calendar_id, subscriber, url) DO NOTHING RETURNING id
            """, ct, calendarId, subscriber, link);
        if (linkId is null)
        {
            // A concurrent add of the same link won.
            await tx.RollbackAsync(ct);
            var existing = (long)(await Scalar(conn, null, "SELECT id FROM calendar_link WHERE calendar_id = $1 AND subscriber = $2 AND url = $3",
                                               ct, calendarId, subscriber, link))!;
            return new(calendarId, existing, false, true, null);
        }
        if (created is not null)
        {
            // A new calendar is baselined from its validation fetch; a joining link stores nothing (research R11).
            await Scalar(conn, tx, "UPDATE calendar SET last_link_id = $1 WHERE id = $2", ct, linkId, calendarId);
            await Scalar(conn, tx, "UPDATE calendar_link SET last_used_at = $1 WHERE id = $2", ct, now, linkId);
            await Checker.CopySnapshotAsync(conn, calendarId, states, ct);
        }
        await tx.CommitAsync(ct);
        return new(calendarId, linkId, created is not null, false, null);
    }

    // Deletes the subscriber's links to the calendar, and the calendar itself when no links remain (FR-008).
    public async Task<bool> RemoveAsync(string subscriber, long calendarId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var delete = new NpgsqlCommand("DELETE FROM calendar_link WHERE calendar_id = $1 AND subscriber = $2", conn, tx)
        { Parameters = { new() { Value = calendarId }, new() { Value = subscriber } } })
            if (await delete.ExecuteNonQueryAsync(ct) == 0) return false;
        await Scalar(conn, tx, "DELETE FROM calendar WHERE id = $1 AND NOT EXISTS (SELECT 1 FROM calendar_link WHERE calendar_id = $1)", ct, calendarId);
        await tx.CommitAsync(ct);
        return true;
    }

    static async Task<object?> Scalar(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a });
        return await cmd.ExecuteScalarAsync(ct);
    }
}
