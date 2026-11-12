using System.Net;
using System.Text.Json.Nodes;
using Xunit;
using static InTheKnow.Tests.LinkIdentityTests;

namespace InTheKnow.Tests;

// 010: GET /api/me/changes/{id}/history (contracts/api.md § Backend tests).
public class HistoryTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static readonly DateTimeOffset D = FilterEngineTests.Detected;

    static JsonObject Ev(string name = "Lesson", int endDays = 1, string? status = "confirmed", string? type = null) =>
        FilterEngineTests.Ev(name, endDays, status, type);

    // One detected_change row for lesson `key`, found at `at`.
    static async Task<long> RecordAsync(TestDb db, long calendarId, string key, string kind, DateTimeOffset at, JsonObject? before = null,
                                        JsonObject? after = null, string? field = null, JsonNode? oldValue = null, JsonNode? newValue = null)
    {
        var (o, n, b, a) = kind switch
        {
            "added" => (null, after, null, null),
            "removed" => (before, null, null, null),
            _ => (oldValue ?? "a", newValue ?? "b", before, after),
        };
        await using var cmd = db.DataSource.CreateCommand("""
            INSERT INTO detected_change (calendar_id, event_key, kind, field, old_value, new_value, event_name, event_starts_at, detected_at,
                                         event_before, event_after)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $8, $9, $10) RETURNING id
            """);
        cmd.Parameters.Add(new() { Value = calendarId });
        cmd.Parameters.Add(new() { Value = key });
        cmd.Parameters.Add(new() { Value = kind });
        cmd.Parameters.Add(new() { Value = (object?)field ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        foreach (var j in new[] { o, n })
            cmd.Parameters.Add(new() { Value = (object?)j?.ToJsonString() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new() { Value = (after ?? before)?["name"]?.GetValue<string>() ?? "Lesson" });
        cmd.Parameters.Add(new() { Value = at });
        foreach (var j in new[] { b, a })
            cmd.Parameters.Add(new() { Value = (object?)j?.ToJsonString() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb });
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    static Task SnapshotAsync(TestDb db, long calendarId, string key, string name) => db.ExecAsync("""
        INSERT INTO event_snapshot (calendar_id, event_key, name, starts_at, ends_at, status, fields)
        VALUES ($1, $2, $3, '2026-10-02T08:00:00Z', '2026-10-02T09:30:00Z', 'confirmed', '{"rooms":["B2.14"]}')
        """, calendarId, key, name);

    static async Task<long> SeedAsync(ApiHost h, SignedIn who) =>
        (await h.Db.SeedCalendarAsync(Harness.Mt, [$"https://{Harness.Mt}/ical?{Guid.NewGuid():N}"], who.AccountId)).CalendarId;

    static Task<JsonNode> HistoryAsync(SignedIn who, long id) => who.JsonAsync($"/api/me/changes/{id}/history");

    static List<(long Id, string Outcome)> Items(JsonNode h) =>
        [.. h["items"]!.AsArray().Select(i => (i!["id"]!.GetValue<long>(), i["outcome"]!.GetValue<string>()))];

    // The response minus the per-request trace id.
    static async Task<(HttpStatusCode, string?, string)> NotFoundAsync(SignedIn who, string path)
    {
        var r = await who.Client.GetAsync(path, Ct);
        var body = JsonNode.Parse(await r.Content.ReadAsStringAsync(Ct))!.AsObject();
        body.Remove("traceId");
        return (r.StatusCode, r.Content.Headers.ContentType?.ToString(), body.ToJsonString());
    }

    // FR-016, SC-005
    [Fact]
    public async Task NotFoundIsTheSameForEveryCause()
    {
        await using var h = await ApiHost.CreateAsync();
        var (s1, s2, s3) = (await h.SignInAsync(), await h.SignInAsync(), await h.SignInAsync());
        var calendar = await SeedAsync(h, s1);
        // s3 follows too, so the change outlives s1's links and the last case tests access, not deletion.
        await h.Db.ExecAsync("INSERT INTO calendar_link (calendar_id, subscriber, url) VALUES ($1, $2, 'https://x.example/s3')", calendar, s3.AccountId);
        var mine = await RecordAsync(h.Db, calendar, "k", "changed", D, Ev(), Ev(), "rooms");
        var deleted = await RecordAsync(h.Db, calendar, "k", "changed", D, Ev(), Ev(), "rooms");
        await h.Db.ExecAsync("DELETE FROM detected_change WHERE id = $1", deleted);

        var unknown = await NotFoundAsync(s1, $"/api/me/changes/{mine + 100}/history");
        Assert.Equal(HttpStatusCode.NotFound, unknown.Item1);
        Assert.Contains("not_found", unknown.Item3);
        Assert.Equal(unknown, await NotFoundAsync(s1, $"/api/me/changes/{deleted}/history"));
        Assert.Equal(unknown, await NotFoundAsync(s2, $"/api/me/changes/{mine}/history"));

        await HistoryAsync(s1, mine);
        Assert.Equal(HttpStatusCode.NoContent, (await s1.Client.DeleteAsync($"/api/me/calendars/{calendar}", Ct)).StatusCode);
        Assert.Equal(unknown, await NotFoundAsync(s1, $"/api/me/changes/{mine}/history"));
        await HistoryAsync(s3, mine);
        Assert.Equal(HttpStatusCode.NotFound, (await s1.Client.GetAsync("/api/me/changes/abc/history", Ct)).StatusCode);
    }

    // FR-009, FR-013: every row of the lesson, newest first, hidden ones included with their outcome.
    [Fact]
    public async Task EveryRowOfTheLessonNewestFirstWithOutcomes()
    {
        await using var h = await ApiHost.CreateAsync();
        var s1 = await h.SignInAsync();
        var calendar = await SeedAsync(h, s1);
        var added = await RecordAsync(h.Db, calendar, "k", "added", D, after: Ev());
        var other = await RecordAsync(h.Db, calendar, "other", "changed", D, Ev(), Ev(), "rooms");
        var low = await RecordAsync(h.Db, calendar, "k", "changed", D.AddHours(1), Ev(status: "tentative"), Ev(), "status", "tentative", "confirmed");
        var hidden = await RecordAsync(h.Db, calendar, "k", "changed", D.AddHours(2), Ev(endDays: -1), Ev(endDays: -1), "rooms");

        var body = await HistoryAsync(s1, low);
        Assert.Equal([(hidden, "hidden"), (low, "lowPriority"), (added, "normal")], Items(body));
        Assert.DoesNotContain(other, Items(body).Select(i => i.Id));
        Assert.Equal(low, body["changeId"]!.GetValue<long>());
        Assert.Equal(calendar, body["calendarId"]!.GetValue<long>());
        Assert.Equal(Harness.Mt, body["calendarName"]!.GetValue<string>());
        Assert.Equal(365, body["retentionDays"]!.GetValue<int>());
        var item = body["items"]![0]!.AsObject();
        Assert.Equal(["id", "calendarId", "eventKey", "kind", "field", "oldValue", "newValue", "eventName", "eventStartsAt", "detectedAt", "outcome"],
            item.Select(kv => kv.Key));

        // The same item as the feed gives.
        var feed = (await s1.JsonAsync("/api/me/changes?includeHidden=true"))["items"]!.AsArray().Single(i => i!["id"]!.GetValue<long>() == hidden)!;
        Assert.True(JsonNode.DeepEquals(feed, item));

        // A hidden change opens its history too.
        Assert.Equal(3, Items(await HistoryAsync(s1, hidden)).Count);
    }

    [Fact]
    public async Task TwoLinksGiveNoDuplicates()
    {
        await using var h = await ApiHost.CreateAsync();
        var s1 = await h.SignInAsync();
        var (calendar, _) = await h.Db.SeedCalendarAsync(Harness.Mt, [$"https://{Harness.Mt}/ical?a", $"https://{Harness.Mt}/ical?b"], s1.AccountId);
        var id = await RecordAsync(h.Db, calendar, "k", "changed", D, Ev(), Ev(), "rooms");
        Assert.Equal([(id, "normal")], Items(await HistoryAsync(s1, id)));
    }

    // SC-003 correctness: a lesson with 100 rows returns them all.
    [Fact]
    public async Task HundredRows()
    {
        await using var h = await ApiHost.CreateAsync();
        var s1 = await h.SignInAsync();
        var calendar = await SeedAsync(h, s1);
        var ids = new List<long>();
        for (var i = 0; i < 100; i++) ids.Add(await RecordAsync(h.Db, calendar, "k", "changed", D.AddHours(i), Ev(), Ev(), "rooms"));
        Assert.Equal(ids.AsEnumerable().Reverse(), Items(await HistoryAsync(s1, ids[0])).Select(i => i.Id));
    }

    // Research R5
    [Fact]
    public async Task CurrentDetails()
    {
        await using var h = await ApiHost.CreateAsync();
        var s1 = await h.SignInAsync();
        var calendar = await SeedAsync(h, s1);

        // present: from the snapshot.
        await SnapshotAsync(h.Db, calendar, "p", "Now");
        var p = await RecordAsync(h.Db, calendar, "p", "changed", D, Ev("Then"), Ev("Then"), "rooms");
        var current = (await HistoryAsync(s1, p))["current"]!;
        Assert.Equal("present", current["state"]!.GetValue<string>());
        Assert.Equal("Now", current["event"]!["name"]!.GetValue<string>());
        Assert.Equal("2026-10-02T08:00:00Z", current["event"]!["start"]!.GetValue<string>());
        Assert.Equal("""["B2.14"]""", current["event"]!["fields"]!["rooms"]!.ToJsonString());
        Assert.Null(current["removedAt"]);

        // removed: from the removed row, with its time.
        await RecordAsync(h.Db, calendar, "r", "added", D, after: Ev("Gone soon"));
        var r = await RecordAsync(h.Db, calendar, "r", "removed", D.AddHours(1), Ev("Gone soon"));
        current = (await HistoryAsync(s1, r))["current"]!;
        Assert.Equal("removed", current["state"]!.GetValue<string>());
        Assert.Equal("Gone soon", current["event"]!["name"]!.GetValue<string>());
        Assert.Equal(D.AddHours(1), current["removedAt"]!.GetValue<DateTimeOffset>());

        // present again after removal and reappearance.
        await RecordAsync(h.Db, calendar, "r", "added", D.AddHours(2), after: Ev("Back"));
        await SnapshotAsync(h.Db, calendar, "r", "Back");
        Assert.Equal("present", (await HistoryAsync(s1, r))["current"]!["state"]!.GetValue<string>());

        // gone: no snapshot row and no removal, as after a re-baseline drops it; the newest event object.
        var g = await RecordAsync(h.Db, calendar, "g", "changed", D, Ev("Old name"), Ev("Last name"), "name", "Old name", "Last name");
        current = (await HistoryAsync(s1, g))["current"]!;
        Assert.Equal("gone", current["state"]!.GetValue<string>());
        Assert.Equal("Last name", current["event"]!["name"]!.GetValue<string>());
        Assert.Null(current["removedAt"]);
    }

    // Research R4: baseline_at from add-link and a features-change re-baseline, not from a normal check.
    [Fact]
    public async Task HistoryStartsAt()
    {
        await using var h = await ApiHost.CreateAsync(("InTheKnow:ChangeRetention", "90.00:00:00"));
        var s1 = await h.SignInAsync();

        // Unknown for a calendar baselined before the column existed. Not due, so it leaves the host budget to the checks below.
        var (old, _) = await h.Db.SeedCalendarAsync(Harness.Mt, [$"https://{Harness.Mt}/ical?old"], s1.AccountId, DateTimeOffset.MaxValue);
        var o = await RecordAsync(h.Db, old, "k", "changed", h.Time.GetUtcNow(), Ev(), Ev(), "rooms");
        var body = await HistoryAsync(s1, o);
        Assert.Null(body["historyStartsAt"]);
        Assert.Equal(90, body["retentionDays"]!.GetValue<int>());

        // Add-link baseline.
        var url = MtLink(hex: "01");
        var added = JsonNode.Parse(await (await h.AddAsync(s1, url)).Content.ReadAsStringAsync(Ct))!;
        var calendar = added["calendarId"]!.GetValue<long>();
        var baseline = h.Time.GetUtcNow();
        var k = await RecordAsync(h.Db, calendar, "k", "changed", baseline, Ev(), Ev(), "rooms");
        Assert.Equal(baseline, (await HistoryAsync(s1, k))["historyStartsAt"]!.GetValue<DateTimeOffset>());

        // A normal check leaves it.
        h.Http.Map[new Uri(url).AbsoluteUri] = "mt-nl-edited.ics";
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await h.Checker.RunDueChecksAsync(h.Time.GetUtcNow(), Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.True(await h.Db.ScalarAsync("SELECT count(*) FROM detected_change WHERE calendar_id = $1 AND event_key <> 'k'", calendar) > 0);
        Assert.Equal(baseline, (await HistoryAsync(s1, k))["historyStartsAt"]!.GetValue<DateTimeOffset>());

        // A features change re-baselines.
        await h.Db.ExecAsync("UPDATE calendar SET baseline_features = 'before' WHERE id = $1", calendar);
        h.Time.Advance(TimeSpan.FromMinutes(10));
        var rebaseline = h.Time.GetUtcNow();
        await h.Checker.RunDueChecksAsync(rebaseline, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(rebaseline, (await HistoryAsync(s1, k))["historyStartsAt"]!.GetValue<DateTimeOffset>());

        // Older than the retention period: the retention cut-off.
        await h.Db.ExecAsync("UPDATE calendar SET baseline_at = $1 WHERE id = $2", h.Time.GetUtcNow() - TimeSpan.FromDays(100), calendar);
        Assert.Equal(h.Time.GetUtcNow() - TimeSpan.FromDays(90), (await HistoryAsync(s1, k))["historyStartsAt"]!.GetValue<DateTimeOffset>());
    }
}
