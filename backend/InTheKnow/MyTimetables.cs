using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InTheKnow;

sealed record EventState(string Key, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Status,
                         DateTimeOffset? OccurrenceStart, JsonObject Fields);

// Normalises an occurrence into the compared state; on configured domains it parses the MyTimetables
// description into language-independent parts (research R9, FR-015 to FR-018, FR-016a).
static partial class MyTimetables
{
    static readonly string[] DraftPrefixes = ["[CONCEPT] ", "[DRAFT] "];
    static readonly string[] VolatileBlocks =
    [
        "Laatst gesynchroniseerd op", "Last synchronised on",
        "Deze afspraak wordt beheerd door MyTimetables.", "This appointment is managed by MyTimetables.",
    ];
    static readonly Dictionary<string, string> Types = new()
    {
        ["Les"] = "lesson",
        ["Lesson"] = "lesson",
        ["Toets"] = "test",
        ["Test"] = "test",
        ["Vakantie"] = "holiday",
        ["Holiday"] = "holiday",
        ["Afspraak"] = "appointment",
        ["Appointment"] = "appointment",
    };

    [GeneratedRegex(@"\n\s*\n")] private static partial Regex BlankLine();
    [GeneratedRegex(@"^([^\d:\n]+):(\s|$)")] private static partial Regex Label();

    public static EventState ToState(Occurrence o, Features? f)
    {
        EventState State(string name, string? status, params (string Key, JsonNode? Value)[] fields) =>
            new(o.Key, name, o.StartsAt, o.EndsAt, status, o.OccurrenceStart, Object(fields));

        if (f is null) return State(o.Name, o.Status, ("location", o.Location), ("description", o.Description));

        var (name, status) = (o.Name, o.Status);
        if (f.DraftMarkerDetection && DraftPrefixes.FirstOrDefault(p => name.StartsWith(p, StringComparison.Ordinal)) is { } prefix)
            (name, status) = (name[prefix.Length..], "tentative");

        var blocks = BlankLine().Split((o.Description ?? "").Replace("\r\n", "\n"))
            .Select(b => b.Trim())
            .Where(b => b.Length > 0 && !VolatileBlocks.Any(v => b.StartsWith(v, StringComparison.Ordinal)))
            .ToList();
        if (!f.EventDetails) return State(name, status, ("location", o.Location), ("description", string.Join("\n\n", blocks)));

        string? type = null;
        List<string> teachers = [], groups = [], text = [];
        var details = new List<(string, JsonNode?)>();
        foreach (var block in blocks)
        {
            var m = Label().Match(block);
            if (!m.Success)
            {
                text.Add(block);
                continue;
            }
            var value = string.Join("\n", block[(m.Groups[1].Length + 1)..].Split('\n').Select(l => l.Trim())).Trim();
            switch (m.Groups[1].Value)
            {
                case "Type": type = Types.GetValueOrDefault(value, value); break;
                case "Ruimte(s)" or "Room(s)": break; // rooms come from LOCATION
                case "Docent(en)" or "Staff member(s)" when f.TeacherDetection: teachers = Teachers(value); break;
                case "Docent(en)" or "Staff member(s)": details.Add(("teachers", value)); break;
                case "Groep(en)" or "Student set(s)": groups = [.. value.Split(',')]; break;
                case var label: details.Add((label, value)); break;
            }
        }
        if (text.Count > 0) details.Add(("text", string.Join("\n\n", text)));

        return State(name, status,
            ("type", type),
            ("rooms", List((o.Location ?? "").Split(','))),
            ("teachers", List(teachers)),
            ("groups", List(groups)),
            ("details", Object([.. details])));
    }

    // "Last, First" pairs (FR-017); an odd count keeps the whole value as one entry.
    static List<string> Teachers(string value)
    {
        var parts = value.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        return parts.Count % 2 == 1 ? [value.Trim()] : [.. parts.Chunk(2).Select(p => $"{p[0]}, {p[1]}")];
    }

    // Trimmed, de-duplicated and sorted, so lists compare as sets (FR-026).
    static JsonArray List(IEnumerable<string> items) =>
        [.. items.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct().Order(StringComparer.Ordinal).Select(i => (JsonNode)i)];

    // Keys sorted, and empty values omitted so that "absent" and "empty" are equal.
    static JsonObject Object((string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject();
        foreach (var (key, value) in fields.OrderBy(f => f.Key, StringComparer.Ordinal))
            if (!Differ.IsEmpty(value)) o[key] = value;
        return o;
    }
}
