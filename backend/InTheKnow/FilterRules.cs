using System.Text.Json;
using System.Text.Json.Nodes;

namespace InTheKnow;

sealed record Rule(string Outcome, JsonObject When);

// The rule format and its validation (contracts/rules.md). A rule is a closed object {outcome, when{…}}.
static class FilterRules
{
    const int MaxRules = 50, MaxValues = 50, MaxText = 100;
    static readonly string[] Outcomes = ["showOnly", "hide", "lowPriority"];
    static readonly string[] ChangeKinds = ["added", "removed", "name", "time", "status", "location", "description", "activityType", "groups", "teachers"];
    static readonly string[] Statuses = ["confirmed", "tentative", "cancelled", "none"];

    // Rule is the 0-based index of the rule at fault, or null when the array itself is (005 contracts/api.md § 3).
    public static (IReadOnlyList<Rule>? Rules, string? Error, int? Rule) Parse(JsonNode? json)
    {
        if (json is not JsonArray array || array.Count > MaxRules) return (null, "invalid_rules", null);
        var rules = new List<Rule>();
        for (var i = 0; i < array.Count; i++)
        {
            if (Check(array[i]) is { } error) return (null, error, i);
            var rule = (JsonObject)array[i]!;
            rules.Add(new(Text(rule["outcome"])!, (JsonObject)rule["when"]!.DeepClone()));
        }
        return (rules, null, null);
    }

    static string? Check(JsonNode? node)
    {
        if (node is not JsonObject rule || rule.Any(kv => kv.Key is not ("outcome" or "when"))) return "invalid_rules";
        if (Text(rule["outcome"]) is not { } outcome || !Outcomes.Contains(outcome)) return "invalid_outcome";
        if (rule["when"] is not JsonObject when) return rule["when"] is null ? "empty_conditions" : "invalid_rules";
        if (when.Count == 0) return "empty_conditions";
        foreach (var (key, value) in when)
        {
            bool? valid = key switch
            {
                "changeKind" => Values(value, v => ChangeKinds.Contains(v)),
                "eventEnded" => value?.GetValueKind() == JsonValueKind.True,
                "statusFrom" or "statusTo" => Text(value) is { } s && Statuses.Contains(s),
                "nameContains" => Text(value) is { Length: >= 1 and <= MaxText },
                "activityType" or "group" or "room" or "teacher" => Values(value, v => v.Length is >= 1 and <= MaxText),
                _ => null,
            };
            if (valid is null) return "invalid_rules";
            if (valid == false) return "invalid_condition";
        }
        return null;
    }

    public static JsonArray ToJson(IReadOnlyList<Rule> rules) =>
        [.. rules.Select(r => (JsonNode)new JsonObject { ["outcome"] = r.Outcome, ["when"] = r.When.DeepClone() })];

    internal static string? Text(JsonNode? n) => n?.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null;

    static bool Values(JsonNode? n, Func<string, bool> valid) =>
        n is JsonArray { Count: >= 1 and <= MaxValues } a && a.All(v => Text(v) is { } s && valid(s));
}

// Which conditions work on a calendar (research R5). Teacher also gates the changeKind value "teachers",
// ActivityType the values "activityType" and "groups".
sealed record ActiveConditions(bool ActivityType, bool Group, bool Room, bool Teacher)
{
    public static ActiveConditions For(Features? f) =>
        f is { EventDetails: true } ? new(true, true, true, f.TeacherDetection) : new(false, false, false, false);

    // A changeKind condition is inactive only when every value is.
    public bool IsActive(Rule r) => r.When.All(kv => kv.Key switch
    {
        "activityType" => ActivityType,
        "group" => Group,
        "room" => Room,
        "teacher" => Teacher,
        "changeKind" => kv.Value!.AsArray().Any(v => v!.GetValue<string>() switch
        {
            "activityType" or "groups" => ActivityType,
            "teachers" => Teacher,
            _ => true,
        }),
        _ => true,
    });

    public string[] Keys =>
    [
        "changeKind", "eventEnded", "status", "nameContains",
        .. ActivityType ? ["activityType"] : Array.Empty<string>(),
        .. Group ? ["group"] : Array.Empty<string>(),
        .. Room ? ["room"] : Array.Empty<string>(),
        .. Teacher ? ["teacher"] : Array.Empty<string>(),
    ];
}
