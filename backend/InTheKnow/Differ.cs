using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InTheKnow;

sealed record ChangeRow(string EventKey, string Kind, string? Field, JsonNode? OldValue, JsonNode? NewValue,
                        string EventName, DateTimeOffset EventStartsAt);

sealed record DiffResult(IReadOnlyList<ChangeRow> Changes, IReadOnlyList<EventState> Upserts, IReadOnlyList<string> Deletes);

// Stored snapshot vs current states → changes (contracts/detected-change.md), with the horizon rule (research R7).
static class Differ
{
    public static DiffResult Diff(IReadOnlyDictionary<string, EventState> stored, IReadOnlyList<EventState> current,
                                  DateTimeOffset? previousHorizon, DateTimeOffset currentHorizon)
    {
        var changes = new List<ChangeRow>();
        var upserts = new List<EventState>();
        var deletes = new List<string>();
        var seen = new HashSet<string>();

        foreach (var now in current)
        {
            seen.Add(now.Key);
            if (!stored.TryGetValue(now.Key, out var old))
            {
                upserts.Add(now);
                if (!(now.OccurrenceStart > previousHorizon)) changes.Add(new(now.Key, "added", null, null, EventObject(now), now.Name, now.StartsAt));
                continue;
            }
            var before = changes.Count;
            void Changed(string field, JsonNode? a, JsonNode? b)
            {
                if (!JsonNode.DeepEquals(Norm(a), Norm(b))) changes.Add(new(now.Key, "changed", field, a?.DeepClone(), b?.DeepClone(), now.Name, now.StartsAt));
            }
            Changed("name", old.Name, now.Name);
            if (old.StartsAt != now.StartsAt || old.EndsAt != now.EndsAt) Changed("time", Time(old), Time(now));
            Changed("status", old.Status, now.Status);
            foreach (var key in Keys(old.Fields, now.Fields).Where(k => k != "details"))
                Changed(key, old.Fields[key], now.Fields[key]);
            var (oldDetails, newDetails) = (old.Fields["details"] as JsonObject, now.Fields["details"] as JsonObject);
            foreach (var label in Keys(oldDetails, newDetails))
                Changed("details." + label, oldDetails?[label], newDetails?[label]);
            if (changes.Count > before) upserts.Add(now);
        }

        foreach (var old in stored.Values.Where(s => !seen.Contains(s.Key)))
        {
            deletes.Add(old.Key);
            if (!(old.OccurrenceStart > currentHorizon)) changes.Add(new(old.Key, "removed", null, EventObject(old), null, old.Name, old.StartsAt));
        }
        return new(changes, upserts, deletes);
    }

    // SHA-256 over the events sorted by key, serialised canonically.
    public static byte[] SnapshotHash(IReadOnlyList<EventState> events)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var e in events.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                w.WriteStartArray();
                w.WriteStringValue(e.Key);
                w.WriteStringValue(e.Name);
                w.WriteStringValue(FeedParser.Iso(e.StartsAt));
                w.WriteStringValue(FeedParser.Iso(e.EndsAt));
                w.WriteStringValue(e.Status);
                w.WriteStringValue(e.OccurrenceStart is { } o ? FeedParser.Iso(o) : null);
                e.Fields.WriteTo(w);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
        return SHA256.HashData(buffer.ToArray());
    }

    public static JsonObject EventObject(EventState e) => new()
    {
        ["key"] = e.Key,
        ["name"] = e.Name,
        ["start"] = FeedParser.Iso(e.StartsAt),
        ["end"] = FeedParser.Iso(e.EndsAt),
        ["status"] = e.Status,
        ["fields"] = e.Fields.DeepClone(),
    };

    public static bool IsEmpty(JsonNode? n) => n switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonObject o => o.Count == 0,
        JsonValue v => v.TryGetValue<string>(out var s) && s.Length == 0,
        _ => false,
    };

    static JsonNode? Norm(JsonNode? n) => IsEmpty(n) ? null : n;

    static JsonObject Time(EventState e) => new() { ["start"] = FeedParser.Iso(e.StartsAt), ["end"] = FeedParser.Iso(e.EndsAt) };

    static IEnumerable<string> Keys(JsonObject? a, JsonObject? b) =>
        (a?.Select(kv => kv.Key) ?? []).Union(b?.Select(kv => kv.Key) ?? []).Order(StringComparer.Ordinal);
}
