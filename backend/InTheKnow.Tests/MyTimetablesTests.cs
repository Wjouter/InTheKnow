using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InTheKnow.Tests;

public class MyTimetablesTests
{
    static readonly DateTimeOffset Horizon = new(2027, 9, 24, 0, 0, 0, TimeSpan.Zero);
    static readonly Features All = new(true, true, true, true, true, true);

    internal static IReadOnlyList<Occurrence> ParseFile(string path) =>
        FeedParser.Parse(File.ReadAllText(path), Horizon, NullLogger.Instance, 1, TestContext.Current.CancellationToken).Events!;

    static Dictionary<string, EventState> States(string fixture, Features? f) =>
        ParseFile(FakeHttp.FixturePath(fixture)).Select(o => MyTimetables.ToState(o, f)).ToDictionary(s => s.Key);

    static EventState One(string description, string? location = "R2, R1", Features? f = null) =>
        MyTimetables.ToState(new("k", "[CONCEPT] Name", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "confirmed", null, location, description), f ?? All);

    static string[] Strings(JsonNode? n) => [.. n!.AsArray().Select(x => x!.GetValue<string>())];

    [Fact]
    public void ExtractsParts()
    {
        var s = States("mt-nl.ics", All);
        var test = s["Xedule!GI4TANRRGIZA@schedule.inholland.nl"];
        Assert.Equal("test", test.Fields["type"]!.GetValue<string>());
        Assert.Equal(["AL00.A1.23", "AL00.A1.30"], Strings(test.Fields["rooms"]));
        Assert.Equal(["ALET26DT3A", "ALET26DT4A", "ALET26VT3A", "ALET26VT4A"], Strings(test.Fields["groups"]));
        Assert.Null(test.Fields["teachers"]);

        var lesson = s["Xedule!GI3TINRWGUZQ@schedule.inholland.nl"];
        Assert.Equal("lesson", lesson.Fields["type"]!.GetValue<string>());
        Assert.Equal(["Docent1, Anna", "Docent2, Bram", "Docent3, Cor"], Strings(lesson.Fields["teachers"]));

        var types = s.Values.Select(e => e.Fields["type"]!.GetValue<string>()).ToHashSet();
        Assert.Equal(["appointment", "holiday", "lesson", "test"], types.Order());
        Assert.All(s.Values, e => Assert.Null(e.Fields["details"])); // sync line and footer dropped
        Assert.All(s.Values, e => Assert.Equal(new[] { "details", "groups", "rooms", "teachers", "type" }.Where(k => e.Fields.ContainsKey(k)), e.Fields.Select(kv => kv.Key)));
    }

    [Fact]
    public void OddTeacherCountIsOneEntry()
    {
        Assert.Equal(["Solo"], Strings(One("Docent(en): Solo").Fields["teachers"]));
        Assert.Equal(["A, B, C"], Strings(One("Docent(en): A, B, C").Fields["teachers"]));
    }

    [Fact]
    public void DraftMarker()
    {
        var e = States("mt-nl.ics", All)["Xedule!GI4TGNBVHEYQ@schedule.inholland.nl"];
        Assert.Equal("Project Smart Building", e.Name);
        Assert.Equal("tentative", e.Status);
        Assert.Equal("confirmed", States("mt-nl.ics", All)["Xedule!GI3TINRWGUZQ@schedule.inholland.nl"].Status);
    }

    [Fact]
    public void DutchAndEnglishAreEqual()
    {
        var nl = States("mt-nl.ics", All);
        var en = States("mt-en.ics", All);
        Assert.Equal(nl.Keys.Order(), en.Keys.Order());
        foreach (var (key, n) in nl)
        {
            var e = en[key];
            Assert.Equal((n.Name, n.StartsAt, n.EndsAt, n.Status, n.OccurrenceStart), (e.Name, e.StartsAt, e.EndsAt, e.Status, e.OccurrenceStart));
            Assert.True(JsonNode.DeepEquals(n.Fields, e.Fields), $"{key}: {n.Fields.ToJsonString()} vs {e.Fields.ToJsonString()}");
        }
    }

    [Fact]
    public void TeacherDetectionOff()
    {
        var e = States("mt-nl.ics", All with { TeacherDetection = false })["Xedule!GI3TINRWGUZQ@schedule.inholland.nl"];
        Assert.Null(e.Fields["teachers"]);
        Assert.Equal("Docent1, Anna, Docent2, Bram, Docent3, Cor", e.Fields["details"]!["teachers"]!.GetValue<string>());
    }

    [Fact]
    public void EventDetailsOff()
    {
        var e = States("mt-nl.ics", All with { EventDetails = false, TeacherDetection = false })["Xedule!GI3TINRWGUZQ@schedule.inholland.nl"];
        Assert.Equal(["description", "location"], e.Fields.Select(kv => kv.Key));
        var description = e.Fields["description"]!.GetValue<string>();
        Assert.StartsWith("Type: Les\n\nRuimte(s):", description);
        Assert.DoesNotContain("gesynchroniseerd", description);
        Assert.DoesNotContain("beheerd door", description);
    }

    [Fact]
    public void UnlistedHostKeepsRawValues()
    {
        var e = MyTimetables.ToState(ParseFile(FakeHttp.FixturePath("mt-nl.ics")).Single(o => o.Key == "Xedule!GI4TGNBVHEYQ@schedule.inholland.nl"), null);
        Assert.Equal("[CONCEPT] Project Smart Building", e.Name);
        Assert.Equal("confirmed", e.Status);
        Assert.Contains("gesynchroniseerd", e.Fields["description"]!.GetValue<string>());
        Assert.Equal("AL00.L0.30b", e.Fields["location"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownLabelsAndFreeText()
    {
        var d = One("Type: Werkcollege\n\nFoo: bar\n\nSome free text\nover two lines\n\nLast synchronised on 2026-09-24 at 20:47 CEST").Fields;
        Assert.Equal("Werkcollege", d["type"]!.GetValue<string>());
        Assert.Equal("bar", d["details"]!["Foo"]!.GetValue<string>());
        Assert.Equal("Some free text\nover two lines", d["details"]!["text"]!.GetValue<string>());
        Assert.Equal(["R1", "R2"], Strings(d["rooms"]));
        Assert.Equal(2, d["details"]!.AsObject().Count);
    }

    [Fact]
    public void RealFeeds()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "private");
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.ics") : [];
        Assert.SkipWhen(files.Length == 0, "no private feeds in Fixtures/private");
        foreach (var file in files)
        {
            var occurrences = ParseFile(file);
            Assert.NotEmpty(occurrences);
            foreach (var o in occurrences)
            {
                var s = MyTimetables.ToState(o, All);
                Assert.NotEqual("", s.Name);
                Assert.True(s.EndsAt >= s.StartsAt);
                if (o.Description?.Contains("Type: Les") != true) continue;
                Assert.Equal("lesson", s.Fields["type"]!.GetValue<string>());
                Assert.NotEmpty(Strings(s.Fields["rooms"]));
                Assert.NotEmpty(Strings(s.Fields["groups"]));
            }
        }
    }

    // Quickstart §4: logs parse + extraction + hash time per private feed. Never runs in the normal gate.
    [Fact(Explicit = true)]
    public void ParseCost()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "private");
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.ics") : [];
        Assert.SkipWhen(files.Length == 0, "no private feeds in Fixtures/private");
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            void Once() => Differ.SnapshotHash([.. FeedParser.Parse(text, Horizon, NullLogger.Instance, 1, TestContext.Current.CancellationToken)
                .Events!.Select(o => MyTimetables.ToState(o, All))]);
            Once();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 20; i++) Once();
            TestContext.Current.TestOutputHelper!.WriteLine($"{Path.GetFileName(file)} ({text.Length / 1024} KB): {watch.Elapsed.TotalMilliseconds / 20:F1} ms per check");
        }
    }
}
