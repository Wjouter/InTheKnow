using System.Security.Claims;
using Npgsql;

namespace InTheKnow;

sealed record AddLinkRequest(string? Url);

// The HTTP API from contracts/api.md. No response ever contains a link (FR-032). Sessions replace the operator key
// (004): the fallback policy puts every route behind a signed-in session, which authorization checks before the body
// is bound. User routes act for the signed-in account under /api/me; the former operator routes need an admin under
// /api/admin (004 contracts/api.md § Moved routes).
static class Api
{
    const int Batch = 500;

    public static RouteGroupBuilder Me(WebApplication app) => app.MapGroup("/api/me");

    public static RouteGroupBuilder Admin(WebApplication app) => app.MapGroup("/api/admin").RequireAuthorization("admin");

    public static void MapApi(this WebApplication app)
    {
        var me = Me(app);

        me.MapPost("/links", async (ClaimsPrincipal user, AddLinkRequest body, Subscriptions subscriptions, HttpResponse response, CancellationToken ct) =>
        {
            var r = await subscriptions.AddLinkAsync(user.AccountId(), body.Url ?? "", ct);
            switch (r.Error)
            {
                case null:
                    var result = new { calendarId = r.CalendarId, linkId = r.LinkId, calendarIsNew = r.CalendarIsNew };
                    return Results.Json(result, statusCode: r.AlreadyExisted ? 200 : 201);
                case "host_budget_exhausted":
                    response.Headers.RetryAfter = "5";
                    return Problem(429, r.Error);
                case var e when e.StartsWith("validation_failed:", StringComparison.Ordinal):
                    return Problem(422, e);
                default:
                    return Problem(400, r.Error);
            }
        });

        me.MapGet("/calendars", async (ClaimsPrincipal user, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var cmd = db.CreateCommand("""
                SELECT c.id, c.status, c.last_checked_at, c.last_ok, c.last_error, c.consecutive_failures, c.next_check_at,
                       l.id, l.flagged_at IS NOT NULL, l.created_at, coalesce(ss.notify, true), ss.label, coalesce(ss.label, c.feed_name, c.host)
                FROM calendar_link l JOIN calendar c ON c.id = l.calendar_id
                LEFT JOIN subscription_setting ss ON ss.calendar_id = c.id AND ss.subscriber = l.subscriber
                WHERE l.subscriber = $1 ORDER BY c.id, l.id
                """);
            cmd.Parameters.Add(new() { Value = user.AccountId() });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var calendars = new List<(long Id, Dictionary<string, object?> Json, List<object> Links)>();
            while (await r.ReadAsync(ct))
            {
                if (calendars.Count == 0 || calendars[^1].Id != r.GetInt64(0))
                {
                    var json = CalendarJson(r);
                    // 003 FR-028: the notification switch, the label and the name emails use.
                    json["notify"] = r.GetBoolean(10);
                    json["label"] = r.IsDBNull(11) ? null : r.GetString(11);
                    json["name"] = r.GetString(12);
                    var links = new List<object>();
                    json["links"] = links;
                    calendars.Add((r.GetInt64(0), json, links));
                }
                calendars[^1].Links.Add(new { linkId = r.GetInt64(7), flagged = r.GetBoolean(8), createdAt = r.GetDateTime(9) });
            }
            return Results.Json(calendars.Select(c => c.Json));
        });

        me.MapDelete("/calendars/{calendarId:long}", async (ClaimsPrincipal user, long calendarId, Subscriptions subscriptions, CancellationToken ct) =>
            await subscriptions.RemoveAsync(user.AccountId(), calendarId, ct) ? Results.NoContent() : Results.NotFound());

        // Read-only: delivery status lives in `delivery` (003).
        // Each change carries its outcome under the subscriber's filter; hidden ones only with includeHidden (FR-008).
        // order=newest with a `before` cursor reads the same batches backwards (005 contracts/api.md § 1).
        me.MapGet("/changes", async (ClaimsPrincipal user, long? after, long? before, int? limit, string? includeHidden, string? order,
                                     NpgsqlDataSource db, Presets presets, CancellationToken ct) =>
        {
            if (limit is < 1 or > 500) return Problem(400, "invalid_limit");
            if (includeHidden is not (null or "true" or "false")) return Problem(400, "invalid_include_hidden");
            if (order is not (null or "oldest" or "newest")) return Problem(400, "invalid_order");
            var newest = order == "newest";
            if (before is not null && !newest || after is not null && newest) return Problem(400, "invalid_cursor");
            var take = limit ?? 100;
            var filters = await presets.LoadFiltersAsync(user.AccountId(), ct);
            var items = new List<(long Id, object Json)>();
            // Keyset batches until the page is full, so a page is never empty while visible changes remain (research R3).
            // ponytail: a full keyset scan per page; upgrade to an indexed pre-filter or a materialised outcome only if SC-009 measurement fails
            for (long from = newest ? before ?? long.MaxValue : after ?? 0, read = Batch; filters.Count > 0 && items.Count <= take && read == Batch;)
            {
                await using var cmd = db.CreateCommand($"""
                    SELECT {ChangeView.Columns}
                    FROM detected_change c WHERE c.calendar_id = ANY($1) AND c.id {(newest ? "<" : ">")} $2
                    ORDER BY c.id {(newest ? "DESC" : "")} LIMIT $3
                    """);
                cmd.Parameters.Add(new() { Value = filters.Keys.ToArray() });
                cmd.Parameters.Add(new() { Value = from });
                cmd.Parameters.Add(new() { Value = Batch });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                for (read = 0; items.Count <= take && await r.ReadAsync(ct); read++)
                {
                    var (startsAt, detectedAt) = (r.GetDateTime(8), r.GetDateTime(9));
                    var c = ChangeView.Read(r);
                    from = c.Id;
                    var (filter, active) = filters[c.CalendarId];
                    var outcome = FilterEngine.Outcome(filter.Rules, c, active);
                    if (outcome == Outcome.Hidden && includeHidden != "true") continue;
                    items.Add((c.Id, new
                    {
                        id = c.Id,
                        calendarId = c.CalendarId,
                        eventKey = c.EventKey,
                        kind = c.Kind,
                        field = c.Field,
                        oldValue = c.OldValue,
                        newValue = c.NewValue,
                        eventName = c.EventName,
                        eventStartsAt = startsAt,
                        detectedAt,
                        outcome = FilterEngine.Name(outcome),
                    }));
                }
            }
            return Page(items, take);
        });

        Admin(app).MapGet("/calendars", async (string? status, bool? overdue, long? after, int? limit, NpgsqlDataSource db, TimeProvider time, CancellationToken ct) =>
        {
            if (status is not (null or "ok" or "failing")) return Problem(400, "invalid_status");
            if (limit is < 1 or > 500) return Problem(400, "invalid_limit");
            var take = limit ?? 100;
            // The link counts in the select list are evaluated for the returned page only.
            await using var cmd = db.CreateCommand("""
                SELECT c.id, c.status, c.last_checked_at, c.last_ok, c.last_error, c.consecutive_failures, c.next_check_at,
                       (SELECT count(DISTINCT subscriber) FROM calendar_link WHERE calendar_id = c.id),
                       (SELECT count(*) FROM calendar_link WHERE calendar_id = c.id AND flagged_at IS NULL),
                       (SELECT count(*) FROM calendar_link WHERE calendar_id = c.id)
                FROM calendar c
                WHERE c.id > $1 AND ($2::text IS NULL OR c.status = $2) AND (NOT $3 OR c.next_check_at < $4)
                ORDER BY c.id LIMIT $5
                """);
            cmd.Parameters.Add(new() { Value = after ?? 0 });
            cmd.Parameters.Add(new() { Value = (object?)status ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            cmd.Parameters.Add(new() { Value = overdue == true });
            cmd.Parameters.Add(new() { Value = time.GetUtcNow() - TimeSpan.FromMinutes(1) });
            cmd.Parameters.Add(new() { Value = take + 1 });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var items = new List<(long Id, object Json)>();
            while (await r.ReadAsync(ct))
            {
                var json = CalendarJson(r);
                json["subscribers"] = r.GetInt64(7);
                json["validLinks"] = r.GetInt64(8);
                json["totalLinks"] = r.GetInt64(9);
                items.Add((r.GetInt64(0), json));
            }
            return Page(items, take);
        });
    }

    internal static IResult Problem(int status, string reason, Dictionary<string, object?>? extra = null) =>
        Results.Problem(statusCode: status, extensions: new Dictionary<string, object?>(extra ?? []) { ["reason"] = reason });

    // Keyset paging: `next` is the last returned id when one more row existed.
    static IResult Page(List<(long Id, object Json)> rows, int limit) => Results.Json(new
    {
        items = rows.Take(limit).Select(i => i.Json),
        next = rows.Count > limit ? rows[limit - 1].Id : (long?)null,
    });

    static Dictionary<string, object?> CalendarJson(NpgsqlDataReader r) => new()
    {
        ["calendarId"] = r.GetInt64(0),
        ["status"] = r.GetString(1),
        ["lastCheckedAt"] = r.IsDBNull(2) ? null : r.GetDateTime(2),
        ["lastOk"] = r.IsDBNull(3) ? null : r.GetBoolean(3),
        ["lastError"] = r.IsDBNull(4) ? null : r.GetString(4),
        ["consecutiveFailures"] = r.GetInt32(5),
        ["nextCheckAt"] = r.GetDateTime(6),
    };
}
