using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;

namespace InTheKnow;

// What the engine reads from one detected_change row.
sealed record ChangeView(long Id, long CalendarId, string EventKey, string Kind, string? Field, JsonNode? OldValue, JsonNode? NewValue,
                         string EventName, DateTimeOffset EventStartsAt, DateTimeOffset DetectedAt, JsonNode? EventBefore, JsonNode? EventAfter)
{
    // The one reader of a detected_change row, aliased c; the changes route and the notifier share it.
    internal const string Columns = "c.id, c.calendar_id, c.event_key, c.kind, c.field, c.old_value::text, c.new_value::text, c.event_name, " +
                                    "c.event_starts_at, c.detected_at, c.event_before::text, c.event_after::text";

    internal static ChangeView Read(NpgsqlDataReader r, int start = 0)
    {
        JsonNode? Json(int i) => r.IsDBNull(start + i) ? null : JsonNode.Parse(r.GetString(start + i));
        return new(r.GetInt64(start), r.GetInt64(start + 1), r.GetString(start + 2), r.GetString(start + 3),
            r.IsDBNull(start + 4) ? null : r.GetString(start + 4), Json(5), Json(6), r.GetString(start + 7),
            r.GetFieldValue<DateTimeOffset>(start + 8), r.GetFieldValue<DateTimeOffset>(start + 9), Json(10), Json(11));
    }
}

enum Outcome { Normal, LowPriority, Hidden }

// The single evaluation of a change against a filter (FR-009, contracts/rules.md § Outcome, research R5/R6).
// Pure; missing or odd stored data never matches instead of throwing.
static class FilterEngine
{
    public static Outcome Outcome(IReadOnlyList<Rule> rules, ChangeView change, ActiveConditions active)
    {
        var used = rules.Where(active.IsActive).ToList();
        var states = States(change);
        bool Any(string outcome) => used.Any(r => r.Outcome == outcome && Matches(r, change, states));
        if (used.Any(r => r.Outcome == "showOnly") && !Any("showOnly")) return InTheKnow.Outcome.Hidden;
        if (Any("hide")) return InTheKnow.Outcome.Hidden;
        return Any("lowPriority") ? InTheKnow.Outcome.LowPriority : InTheKnow.Outcome.Normal;
    }

    public static string Name(Outcome o) => o switch
    {
        InTheKnow.Outcome.Hidden => "hidden",
        InTheKnow.Outcome.LowPriority => "lowPriority",
        _ => "normal",
    };

    static bool Matches(Rule r, ChangeView c, JsonObject[] states) => r.When.All(kv => kv.Key switch
    {
        "changeKind" => Kind(c) is { } kind && Values(kv.Value).Contains(kind),
        "eventEnded" => states.Length > 0 && states.All(s => End(s) < c.DetectedAt),
        "statusFrom" => c is { Kind: "changed", Field: "status" } && (FilterRules.Text(c.OldValue) ?? "none") == FilterRules.Text(kv.Value),
        "statusTo" => c is { Kind: "changed", Field: "status" } && (FilterRules.Text(c.NewValue) ?? "none") == FilterRules.Text(kv.Value),
        "nameContains" => states.Any(s => FilterRules.Text(s["name"])?.Contains(FilterRules.Text(kv.Value)!, StringComparison.OrdinalIgnoreCase) == true),
        "activityType" => states.Any(s => FilterRules.Text(Fields(s)?["type"]) is { } t && Values(kv.Value).Contains(t, StringComparer.OrdinalIgnoreCase)),
        "group" => AnyIn(states, "groups", kv.Value),
        "room" => AnyIn(states, "rooms", kv.Value),
        "teacher" => AnyIn(states, "teachers", kv.Value),
        _ => false,
    });

    // Recorded field → changeKind value (research R5).
    static string? Kind(ChangeView c) => c.Kind != "changed" ? c.Kind : c.Field switch
    {
        "name" or "time" or "status" => c.Field,
        "location" or "rooms" => "location",
        "description" => "description",
        "type" => "activityType",
        "groups" or "teachers" => c.Field,
        { } f when f.StartsWith("details.", StringComparison.Ordinal) => "description",
        _ => null,
    };

    // The event before and after; added has only after, removed only before.
    static JsonObject[] States(ChangeView c) =>
    [
        .. new[] { c.EventBefore ?? (c.Kind == "removed" ? c.OldValue : null), c.EventAfter ?? (c.Kind == "added" ? c.NewValue : null) }
            .OfType<JsonObject>(),
    ];

    static DateTimeOffset? End(JsonObject s) =>
        DateTimeOffset.TryParse(FilterRules.Text(s["end"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e) ? e : null;

    static JsonObject? Fields(JsonObject s) => s["fields"] as JsonObject;

    static IEnumerable<string> Values(JsonNode? n) => n is JsonArray a ? a.Select(FilterRules.Text).OfType<string>() : [];

    static bool AnyIn(JsonObject[] states, string field, JsonNode? wanted) =>
        states.Any(s => Values(Fields(s)?[field]).Any(v => Values(wanted).Contains(v, StringComparer.OrdinalIgnoreCase)));
}
