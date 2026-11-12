using System.Security.Claims;
using System.Text.Json.Nodes;
using Npgsql;

namespace InTheKnow;

// GET /api/me/changes/{id}/history (010 contracts/api.md): every kept change of the lesson change `id` belongs to,
// with the lesson's current or last known details. Read-only.
static class History
{
    public static void MapHistory(this RouteGroupBuilder me) =>
        me.MapGet("/changes/{id:long}/history", async (ClaimsPrincipal user, long id, NpgsqlDataSource db, Presets presets,
                                                        Settings settings, TimeProvider time, CancellationToken ct) =>
        {
            long calendarId;
            string eventKey;
            await using (var cmd = db.CreateCommand("SELECT calendar_id, event_key FROM detected_change WHERE id = $1"))
            {
                cmd.Parameters.Add(new() { Value = id });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return NotFound();
                (calendarId, eventKey) = (r.GetInt64(0), r.GetString(1));
            }
            // FR-016: not following the timetable gives the same response as a missing change.
            if (!(await presets.LoadFiltersAsync(user.AccountId(), ct, calendarId)).TryGetValue(calendarId, out var f)) return NotFound();

            var rows = new List<ChangeView>();
            await using (var cmd = db.CreateCommand($"""
                SELECT {ChangeView.Columns} FROM detected_change c
                WHERE c.calendar_id = $1 AND c.event_key = $2 ORDER BY c.id DESC
                """))
            {
                cmd.Parameters.Add(new() { Value = calendarId });
                cmd.Parameters.Add(new() { Value = eventKey });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) rows.Add(ChangeView.Read(r));
            }
            if (rows.Count == 0) return NotFound(); // expired between the two reads

            string calendarName;
            DateTimeOffset? baselineAt;
            await using (var cmd = db.CreateCommand("""
                SELECT coalesce(ss.label, c.feed_name, c.host), c.baseline_at FROM calendar c
                LEFT JOIN subscription_setting ss ON ss.calendar_id = c.id AND ss.subscriber = $2
                WHERE c.id = $1
                """))
            {
                cmd.Parameters.Add(new() { Value = calendarId });
                cmd.Parameters.Add(new() { Value = user.AccountId() });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return NotFound();
                calendarName = r.GetString(0);
                baselineAt = r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1);
            }

            var retention = settings.Options.ChangeRetention;
            var cutoff = time.GetUtcNow() - retention;
            return Results.Json(new
            {
                changeId = id,
                calendarId,
                calendarName,
                current = await CurrentAsync(db, calendarId, eventKey, rows, ct),
                historyStartsAt = baselineAt is { } b ? (b > cutoff ? b : cutoff).UtcDateTime : (DateTime?)null,
                retentionDays = (int)retention.TotalDays,
                items = rows.Select(c => Item(c, FilterEngine.Outcome(f.Filter.Rules, c, f.Active))),
            });
        });

    static IResult NotFound() => Api.Problem(404, "not_found");

    // The item shape of GET /api/me/changes.
    internal static object Item(ChangeView c, Outcome outcome) => new
    {
        id = c.Id,
        calendarId = c.CalendarId,
        eventKey = c.EventKey,
        kind = c.Kind,
        field = c.Field,
        oldValue = c.OldValue,
        newValue = c.NewValue,
        eventName = c.EventName,
        eventStartsAt = c.EventStartsAt.UtcDateTime,
        detectedAt = c.DetectedAt.UtcDateTime,
        outcome = FilterEngine.Name(outcome),
    };

    // Research R5: the snapshot row, else the removed row, else the newest row holding an event object.
    static async Task<object> CurrentAsync(NpgsqlDataSource db, long calendarId, string eventKey, List<ChangeView> rows, CancellationToken ct)
    {
        await using (var cmd = db.CreateCommand("""
            SELECT name, starts_at, ends_at, status, fields::text FROM event_snapshot WHERE calendar_id = $1 AND event_key = $2
            """))
        {
            cmd.Parameters.Add(new() { Value = calendarId });
            cmd.Parameters.Add(new() { Value = eventKey });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                var e = new JsonObject
                {
                    ["key"] = eventKey,
                    ["name"] = r.GetString(0),
                    ["start"] = FeedParser.Iso(r.GetFieldValue<DateTimeOffset>(1)),
                    ["end"] = FeedParser.Iso(r.GetFieldValue<DateTimeOffset>(2)),
                    ["status"] = r.IsDBNull(3) ? null : r.GetString(3),
                    ["fields"] = JsonNode.Parse(r.GetString(4)),
                };
                return new { state = "present", @event = e, removedAt = (DateTime?)null };
            }
        }
        if (rows[0] is { Kind: "removed" } removed)
            return new { state = "removed", @event = removed.OldValue, removedAt = (DateTime?)removed.DetectedAt.UtcDateTime };
        var last = rows.Select(c => c.EventAfter ?? (c.Kind == "added" ? c.NewValue : c.Kind == "removed" ? c.OldValue : null))
                       .FirstOrDefault(e => e is JsonObject);
        return new { state = "gone", @event = last, removedAt = (DateTime?)null };
    }
}
