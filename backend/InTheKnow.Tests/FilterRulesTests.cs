using System.Text.Json.Nodes;
using Xunit;

namespace InTheKnow.Tests;

// contracts/rules.md § Validation (SC-008).
public class FilterRulesTests
{
    static string? Error(string json) => FilterRules.Parse(JsonNode.Parse(json)).Error;

    static string Rule(string when, string outcome = "\"hide\"") => $$"""[{ "outcome": {{outcome}}, "when": {{when}} }]""";

    static string Many(int n, string item) => "[" + string.Join(",", Enumerable.Repeat(item, n)) + "]";

    [Theory]
    [InlineData("{}", "invalid_rules")]
    [InlineData("null", "invalid_rules")]
    [InlineData("[1]", "invalid_rules")]
    [InlineData("""[{ "outcome": "hide", "when": { "eventEnded": true }, "extra": 1 }]""", "invalid_rules")]
    [InlineData("""[{ "when": { "eventEnded": true } }]""", "invalid_outcome")]
    [InlineData("""[{ "outcome": "show", "when": { "eventEnded": true } }]""", "invalid_outcome")]
    [InlineData("""[{ "outcome": 1, "when": { "eventEnded": true } }]""", "invalid_outcome")]
    [InlineData("""[{ "outcome": "hide" }]""", "empty_conditions")]
    [InlineData("""[{ "outcome": "hide", "when": {} }]""", "empty_conditions")]
    [InlineData("""[{ "outcome": "hide", "when": [] }]""", "invalid_rules")]
    public void RuleShape(string json, string reason) => Assert.Equal(reason, Error(json));

    [Theory]
    [InlineData("""{ "unknown": 1 }""", "invalid_rules")]
    [InlineData("""{ "changeKind": "location" }""", "invalid_condition")]
    [InlineData("""{ "changeKind": [] }""", "invalid_condition")]
    [InlineData("""{ "changeKind": ["moved"] }""", "invalid_condition")]
    [InlineData("""{ "changeKind": [null] }""", "invalid_condition")]
    [InlineData("""{ "eventEnded": false }""", "invalid_condition")]
    [InlineData("""{ "eventEnded": "true" }""", "invalid_condition")]
    [InlineData("""{ "statusFrom": "done" }""", "invalid_condition")]
    [InlineData("""{ "statusTo": ["confirmed"] }""", "invalid_condition")]
    [InlineData("""{ "nameContains": "" }""", "invalid_condition")]
    [InlineData("""{ "nameContains": 5 }""", "invalid_condition")]
    [InlineData("""{ "group": [""] }""", "invalid_condition")]
    [InlineData("""{ "room": "AL00" }""", "invalid_condition")]
    [InlineData("""{ "teacher": [1] }""", "invalid_condition")]
    [InlineData("""{ "activityType": [] }""", "invalid_condition")]
    public void Conditions(string when, string reason) => Assert.Equal(reason, Error(Rule(when)));

    [Fact]
    public void Limits()
    {
        var rule = """{ "outcome": "hide", "when": { "eventEnded": true } }""";
        Assert.Null(Error(Many(50, rule)));
        Assert.Equal("invalid_rules", Error(Many(51, rule)));
        Assert.Null(Error(Rule($$"""{ "group": {{Many(50, "\"g\"")}} }""")));
        Assert.Equal("invalid_condition", Error(Rule($$"""{ "group": {{Many(51, "\"g\"")}} }""")));
        Assert.Null(Error(Rule($$"""{ "nameContains": "{{new string('x', 100)}}" }""")));
        Assert.Equal("invalid_condition", Error(Rule($$"""{ "nameContains": "{{new string('x', 101)}}" }""")));
        Assert.Equal("invalid_condition", Error(Rule($$"""{ "teacher": ["{{new string('x', 101)}}"] }""")));
    }

    // 005 contracts/api.md § 3: the index of the first rule at fault, and none when the array itself is.
    [Theory]
    [InlineData(0, """{ "when": { "eventEnded": true } }""", "invalid_outcome")]
    [InlineData(2, """{ "outcome": "hide", "when": {} }""", "empty_conditions")]
    [InlineData(4, """{ "outcome": "hide", "when": { "eventEnded": false } }""", "invalid_condition")]
    [InlineData(2, """{ "outcome": "hide", "when": { "eventEnded": true, "unknown": 1 } }""", "invalid_rules")]
    [InlineData(4, """{ "outcome": "hide", "when": { "eventEnded": true }, "extra": 1 }""", "invalid_rules")]
    public void RuleIndex(int index, string bad, string reason)
    {
        var good = """{ "outcome": "hide", "when": { "eventEnded": true } }""";
        var json = "[" + string.Join(",", Enumerable.Range(0, 5).Select(i => i == index ? bad : good)) + "]";
        var (rules, error, rule) = FilterRules.Parse(JsonNode.Parse(json));
        Assert.Equal((null, reason, index), (rules, error, rule));
    }

    [Fact]
    public void NoRuleIndexForTheArray()
    {
        Assert.Equal(("invalid_rules", (int?)null), Pair(FilterRules.Parse(JsonNode.Parse("{}"))));
        var rule = """{ "outcome": "hide", "when": { "eventEnded": true } }""";
        Assert.Equal(("invalid_rules", (int?)null), Pair(FilterRules.Parse(JsonNode.Parse(Many(51, rule)))));
        static (string?, int?) Pair((IReadOnlyList<Rule>?, string? Error, int? Rule) r) => (r.Error, r.Rule);
    }

    [Fact]
    public void ValidRulesRoundTrip()
    {
        // MyTimetables conditions are valid on any calendar (FR-020, FR-024).
        var json = """
            [{"outcome":"showOnly","when":{"group":["ES1"],"room":["R1"],"teacher":["Last, First"],"activityType":["test"],"changeKind":["teachers","time"]}},
             {"outcome":"lowPriority","when":{"statusFrom":"none","statusTo":"cancelled","nameContains":"Zelfstudie"}}]
            """;
        var (rules, error, _) = FilterRules.Parse(JsonNode.Parse(json));
        Assert.Null(error);
        Assert.Equal(["showOnly", "lowPriority"], rules!.Select(r => r.Outcome));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), FilterRules.ToJson(rules!)));
    }

    // The Default rules as seeded by the migration.
    internal static IReadOnlyList<Rule> DefaultRules()
    {
        using var stream = typeof(Db).Assembly.GetManifestResourceStream("InTheKnow.Migrations.002_filters.sql")!;
        var sql = new StreamReader(stream).ReadToEnd();
        var json = sql[(sql.IndexOf("'[", StringComparison.Ordinal) + 1)..(sql.IndexOf("]'", StringComparison.Ordinal) + 1)];
        var (rules, error, _) = FilterRules.Parse(JsonNode.Parse(json));
        Assert.Null(error);
        return rules!;
    }

    [Fact]
    public void DefaultRulesParse() => Assert.Equal(["hide", "hide", "hide", "lowPriority"], DefaultRules().Select(r => r.Outcome));
}
