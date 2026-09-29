using System.Text.Json.Nodes;
using Xunit;
using static InTheKnow.Outcome;

namespace InTheKnow.Tests;

// contracts/rules.md § Outcome, research R5/R6.
public class FilterEngineTests
{
    internal static readonly DateTimeOffset Detected = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    internal static readonly ActiveConditions Mt = ActiveConditions.For(new Features(true, true, true, true, true, true));
    internal static readonly ActiveConditions MtNoTeachers = ActiveConditions.For(new Features(true, true, true, false, true, true));
    internal static readonly ActiveConditions Generic = ActiveConditions.For(null);

    internal static JsonObject Ev(string name = "Lesson", int endDays = 1, string? status = "confirmed", string? type = null,
                                  string[]? groups = null, string[]? rooms = null, string[]? teachers = null)
    {
        var fields = new JsonObject();
        if (type is not null) fields["type"] = type;
        if (groups is not null) fields["groups"] = new JsonArray([.. groups.Select(g => (JsonNode)g)]);
        if (rooms is not null) fields["rooms"] = new JsonArray([.. rooms.Select(g => (JsonNode)g)]);
        if (teachers is not null) fields["teachers"] = new JsonArray([.. teachers.Select(g => (JsonNode)g)]);
        var end = Detected.AddDays(endDays);
        return new JsonObject
        {
            ["key"] = "k", ["name"] = name, ["start"] = FeedParser.Iso(end.AddHours(-1)), ["end"] = FeedParser.Iso(end),
            ["status"] = status, ["fields"] = fields,
        };
    }

    internal static ChangeView Changed(string field, JsonNode? old, JsonNode? now, JsonObject? before = null, JsonObject? after = null) =>
        new(1, 1, "k", "changed", field, old, now, "Lesson", Detected, Detected, before ?? Ev(), after ?? Ev());

    internal static ChangeView Added(JsonObject e) => new(1, 1, "k", "added", null, null, e, "Lesson", Detected, Detected, null, null);

    internal static ChangeView Removed(JsonObject e) => new(1, 1, "k", "removed", null, e, null, "Lesson", Detected, Detected, null, null);

    internal static IReadOnlyList<Rule> R(string json)
    {
        var (rules, error, _) = FilterRules.Parse(JsonNode.Parse(json));
        Assert.Null(error);
        return rules!;
    }

    static Outcome O(string rules, ChangeView c, ActiveConditions? active = null) => FilterEngine.Outcome(R(rules), c, active ?? Mt);

    static ChangeView Named(string name) => Added(Ev(name));

    [Fact]
    public void OutcomeOrder()
    {
        const string rules = """
            [{"outcome":"showOnly","when":{"nameContains":"A"}},
             {"outcome":"hide","when":{"nameContains":"B"}},
             {"outcome":"lowPriority","when":{"nameContains":"C"}}]
            """;
        Assert.Equal(Hidden, O(rules, Named("X")));
        Assert.Equal(Normal, O(rules, Named("A")));
        Assert.Equal(Hidden, O(rules, Named("AB")));
        Assert.Equal(LowPriority, O(rules, Named("AC")));
        Assert.Equal(Hidden, O(rules, Named("ABC")));
        Assert.Equal(Normal, O("[]", Named("X")));
    }

    [Fact]
    public void TwoShowOnlyRulesAreOr()
    {
        const string rules = """[{"outcome":"showOnly","when":{"nameContains":"A"}},{"outcome":"showOnly","when":{"nameContains":"B"}}]""";
        Assert.Equal(Normal, O(rules, Named("A")));
        Assert.Equal(Normal, O(rules, Named("B")));
        Assert.Equal(Hidden, O(rules, Named("C")));
    }

    [Fact]
    public void ConditionsInOneRuleAreAnd()
    {
        const string rules = """[{"outcome":"hide","when":{"nameContains":"A","changeKind":["added"]}}]""";
        Assert.Equal(Hidden, O(rules, Named("A")));
        Assert.Equal(Normal, O(rules, Removed(Ev("A"))));
    }

    // SC-006, edge case "allow-list with nothing active".
    [Fact]
    public void InactiveRulesAreIgnored()
    {
        var c = Added(Ev(groups: ["ES2"], type: "test"));
        Assert.Equal(Hidden, O("""[{"outcome":"showOnly","when":{"group":["ES1"]}}]""", c));
        Assert.Equal(Normal, O("""[{"outcome":"showOnly","when":{"group":["ES1"]}}]""", c, Generic));
        Assert.Equal(Normal, O("""[{"outcome":"hide","when":{"activityType":["test"]}}]""", c, Generic));
        Assert.Equal(Normal, O("""[{"outcome":"hide","when":{"teacher":["X"]}}]""", Added(Ev(teachers: ["X"])), MtNoTeachers));
        Assert.Equal(Hidden, O("""[{"outcome":"hide","when":{"teacher":["x"]}}]""", Added(Ev(teachers: ["X"]))));
    }

    [Theory]
    [InlineData("name", "name")]
    [InlineData("time", "time")]
    [InlineData("status", "status")]
    [InlineData("location", "location")]
    [InlineData("rooms", "location")]
    [InlineData("description", "description")]
    [InlineData("details.text", "description")]
    [InlineData("details.teachers", "description")]
    [InlineData("type", "activityType")]
    [InlineData("groups", "groups")]
    [InlineData("teachers", "teachers")]
    public void ChangeKindMapping(string field, string kind)
    {
        var c = Changed(field, "a", "b");
        Assert.Equal(Hidden, O($$$"""[{"outcome":"hide","when":{"changeKind":["{{{kind}}}"]}}]""", c));
        var other = kind == "name" ? "time" : "name";
        Assert.Equal(Normal, O($$$"""[{"outcome":"hide","when":{"changeKind":["{{{other}}}","added","removed"]}}]""", c));
    }

    [Fact]
    public void AddedAndRemovedKinds()
    {
        Assert.Equal(Hidden, O("""[{"outcome":"hide","when":{"changeKind":["added"]}}]""", Added(Ev())));
        Assert.Equal(Normal, O("""[{"outcome":"hide","when":{"changeKind":["added"]}}]""", Removed(Ev())));
        Assert.Equal(Hidden, O("""[{"outcome":"hide","when":{"changeKind":["removed"]}}]""", Removed(Ev())));
    }

    [Fact]
    public void EventConditionsMatchBeforeOrAfter()
    {
        var moved = Changed("groups", new JsonArray("ES1"), new JsonArray("ES2"), Ev(groups: ["ES1"]), Ev(groups: ["ES2"]));
        Assert.Equal(Hidden, O("""[{"outcome":"hide","when":{"group":["es1"]}}]""", moved));
        Assert.Equal(Hidden, O("""[{"outcome":"hide","when":{"group":["ES2"]}}]""", moved));
        Assert.Equal(Normal, O("""[{"outcome":"hide","when":{"group":["ES3"]}}]""", moved));

        var renamed = Changed("name", "Les", "Zelfstudie", Ev("Les"), Ev("Zelfstudie"));
        Assert.Equal(Hidden, O("""[{"outcome":"hide","when":{"nameContains":"zelfSTUDIE"}}]""", renamed));
        Assert.Equal(Hidden, O("""[{"outcome":"hide","when":{"nameContains":"les"}}]""", renamed));

        Assert.Equal(Hidden, O("""[{"outcome":"hide","when":{"room":["r1"]}}]""", Added(Ev(rooms: ["R0", "R1"]))));
        Assert.Equal(Hidden, O("""[{"outcome":"hide","when":{"activityType":["TEST"]}}]""", Removed(Ev(type: "test"))));
        Assert.Equal(Normal, O("""[{"outcome":"hide","when":{"activityType":["test"]}}]""", Added(Ev(type: "lesson"))));
        Assert.Equal(Normal, O("""[{"outcome":"hide","when":{"activityType":["tes"]}}]""", Added(Ev(type: "test")))); // exact, not substring
    }

    [Fact]
    public void EventEndedAtDetection()
    {
        const string rules = """[{"outcome":"hide","when":{"eventEnded":true}}]""";
        Assert.Equal(Hidden, O(rules, Changed("rooms", null, null, Ev(endDays: -1), Ev(endDays: -1))));
        Assert.Equal(Normal, O(rules, Changed("rooms", null, null, Ev(endDays: 1), Ev(endDays: 1))));
        // A time change that moves a past event into the future.
        Assert.Equal(Normal, O(rules, Changed("time", null, null, Ev(endDays: -1), Ev(endDays: 1))));
        Assert.Equal(Hidden, O(rules, Added(Ev(endDays: -1))));
        Assert.Equal(Hidden, O(rules, Removed(Ev(endDays: -1))));
        // Odd stored rows never match.
        Assert.Equal(Normal, O(rules, Added(new JsonObject())));
        Assert.Equal(Normal, O(rules, Changed("rooms", null, null, new JsonObject(), new JsonObject()) with { EventBefore = null, EventAfter = null }));
    }

    [Fact]
    public void StatusConditions()
    {
        const string rules = """[{"outcome":"lowPriority","when":{"statusFrom":"tentative","statusTo":"confirmed"}}]""";
        Assert.Equal(LowPriority, O(rules, Changed("status", "tentative", "confirmed")));
        Assert.Equal(Normal, O(rules, Changed("status", "confirmed", "tentative")));
        Assert.Equal(Normal, O(rules, Changed("name", "tentative", "confirmed")));
        Assert.Equal(Hidden, O("""[{"outcome":"hide","when":{"statusFrom":"none"}}]""", Changed("status", null, "confirmed")));
        Assert.Equal(Hidden, O("""[{"outcome":"hide","when":{"statusTo":"none"}}]""", Changed("status", "confirmed", null)));
        Assert.Equal(Normal, O("""[{"outcome":"hide","when":{"statusTo":"none"}}]""", Changed("status", "confirmed", "cancelled")));
    }

    // SC-001: the Default preset (US1).
    [Fact]
    public void DefaultPreset()
    {
        var d = FilterRulesTests.DefaultRules();
        Outcome D(ChangeView c, ActiveConditions? active = null) => FilterEngine.Outcome(d, c, active ?? Mt);
        var lesson = Ev(type: "lesson", groups: ["ES1"], rooms: ["R1"]);
        var holiday = Ev(type: "holiday");

        Assert.Equal(Hidden, D(Changed("rooms", null, null, Ev(endDays: -1), Ev(endDays: -1))));
        Assert.Equal(Normal, D(Changed("rooms", new JsonArray("R1"), new JsonArray("R2"), lesson, lesson)));
        Assert.Equal(Hidden, D(Changed("rooms", new JsonArray("R1"), new JsonArray("R2"), holiday, holiday)));
        Assert.Equal(Hidden, D(Changed("type", "lesson", "test", lesson, lesson)));
        Assert.Equal(Hidden, D(Changed("groups", new JsonArray("ES1"), new JsonArray("ES2"), lesson, lesson)));
        Assert.Equal(LowPriority, D(Changed("status", "tentative", "confirmed")));
        Assert.Equal(Normal, D(Changed("status", "confirmed", "tentative")));
        Assert.Equal(Normal, D(Changed("status", "confirmed", "cancelled")));
        Assert.Equal(Normal, D(Added(lesson)));

        // A generic calendar applies only the eventEnded and status rules.
        Assert.Equal([0, 3], Enumerable.Range(0, d.Count).Where(i => Generic.IsActive(d[i])));
        Assert.Equal(Normal, D(Changed("location", "a", "b", holiday, holiday), Generic));
        Assert.Equal(Hidden, D(Changed("location", "a", "b", Ev(endDays: -1), Ev(endDays: -1)), Generic));
        Assert.Equal(LowPriority, D(Changed("status", "tentative", "confirmed"), Generic));
    }

    // US3: show only one class, on top of the Default rules.
    [Fact]
    public void ShowOnlyOneGroup()
    {
        var withDefault = R("""[{"outcome":"showOnly","when":{"group":["ES1"]}}]""").Concat(FilterRulesTests.DefaultRules()).ToList();
        Outcome W(ChangeView c, ActiveConditions? active = null) => FilterEngine.Outcome(withDefault, c, active ?? Mt);

        Assert.Equal(Hidden, W(Added(Ev(groups: ["ES2"]))));
        Assert.Equal(Normal, W(Added(Ev(groups: ["ES1", "ES2"]))));
        Assert.Equal(Hidden, W(Changed("rooms", "a", "b", Ev(groups: ["ES1"], endDays: -1), Ev(groups: ["ES1"], endDays: -1))));
        // ES1 removed from the group list: passes the allow-list (matches before), then Default hides group changes.
        var dropped = Changed("groups", new JsonArray("ES1", "ES2"), new JsonArray("ES2"), Ev(groups: ["ES1", "ES2"]), Ev(groups: ["ES2"]));
        Assert.Equal(Hidden, W(dropped));
        Assert.Equal(Normal, O("""[{"outcome":"showOnly","when":{"group":["ES1"]}}]""", dropped));

        const string either = """[{"outcome":"showOnly","when":{"group":["ES1"]}},{"outcome":"showOnly","when":{"group":["ES2"]}}]""";
        Assert.Equal(Normal, O(either, Added(Ev(groups: ["ES2"]))));
        Assert.Equal(Hidden, O(either, Added(Ev(groups: ["ES3"]))));
        Assert.Equal(Hidden, O("""[{"outcome":"showOnly","when":{"group":["Nobody"]}}]""", Added(Ev(groups: ["ES1"]))));
        // On a generic calendar the show-only rule is inactive, so nothing is emptied.
        Assert.Equal(Normal, W(Added(Ev(groups: ["ES2"])), Generic));
    }

    // US4: MyTimetables conditions per calendar (research R5).
    [Fact]
    public void ActiveConditionsPerCalendar()
    {
        Assert.Equal(new ActiveConditions(true, true, true, true), Mt);
        Assert.Equal(new ActiveConditions(true, true, true, false), MtNoTeachers);
        Assert.Equal(new ActiveConditions(false, false, false, false), Generic);
        Assert.Equal(Generic, ActiveConditions.For(new Features(true, true, true, true, false, true))); // event details off
        Assert.Equal(["changeKind", "eventEnded", "status", "nameContains", "activityType", "group", "room", "teacher"], Mt.Keys);
        Assert.Equal(["changeKind", "eventEnded", "status", "nameContains"], Generic.Keys);

        Assert.True(Generic.IsActive(R("""[{"outcome":"hide","when":{"changeKind":["location","groups"]}}]""")[0]));
        Assert.False(Generic.IsActive(R("""[{"outcome":"hide","when":{"changeKind":["groups"]}}]""")[0]));
        Assert.False(MtNoTeachers.IsActive(R("""[{"outcome":"hide","when":{"changeKind":["teachers"]}}]""")[0]));
        Assert.True(Mt.IsActive(R("""[{"outcome":"hide","when":{"changeKind":["teachers"]}}]""")[0]));
    }

    // US4 scenario 4: hide location changes of one teacher's lessons only.
    [Fact]
    public void LocationChangesOfOneTeacher()
    {
        const string rules = """[{"outcome":"hide","when":{"changeKind":["location"],"teacher":["Docent1, Anna"]}}]""";
        var anna = Ev(teachers: ["Docent1, Anna"]);
        var cor = Ev(teachers: ["Docent3, Cor"]);
        Assert.Equal(Hidden, O(rules, Changed("rooms", "a", "b", anna, anna)));
        Assert.Equal(Normal, O(rules, Changed("rooms", "a", "b", cor, cor)));
        Assert.Equal(Normal, O(rules, Changed("time", "a", "b", anna, anna)));
        // Without MyTimetables data an activityType condition never matches.
        Assert.Equal(Normal, O("""[{"outcome":"hide","when":{"activityType":["lesson"]}}]""", Added(Ev())));
    }
}
