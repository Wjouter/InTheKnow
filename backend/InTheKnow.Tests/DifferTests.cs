using System.Text.Json.Nodes;
using Xunit;

namespace InTheKnow.Tests;

public class DifferTests
{
    static readonly DateTimeOffset T = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset H = new(2027, 10, 1, 0, 0, 0, TimeSpan.Zero);

    static EventState E(string key, string name = "Lesson", int startHour = 8, string? status = "confirmed",
                        JsonObject? fields = null, DateTimeOffset? occurrence = null) =>
        new(key, name, T.AddHours(startHour - 8), T.AddHours(startHour - 7), status, occurrence, fields ?? new JsonObject());

    static DiffResult Diff(EventState[] stored, EventState[] current, DateTimeOffset? previous = null) =>
        Differ.Diff(stored.ToDictionary(s => s.Key), current, previous ?? H, H);

    [Fact]
    public void OneChangePerField()
    {
        var old = E("a", fields: new() { ["rooms"] = new JsonArray("R1", "R2"), ["type"] = "lesson", ["details"] = new JsonObject { ["Foo"] = "x" } });
        var now = E("a", name: "Renamed", startHour: 9, status: "tentative",
                    fields: new() { ["rooms"] = new JsonArray("R1", "R3"), ["type"] = "lesson", ["details"] = new JsonObject { ["Foo"] = "y", ["text"] = "t" } });
        var d = Diff([old], [now]);

        Assert.Equal(["details.Foo", "details.text", "name", "rooms", "status", "time"], d.Changes.Select(c => c.Field!).Order());
        Assert.All(d.Changes, c => Assert.Equal(("a", "changed", "Renamed", T.AddHours(1)), (c.EventKey, c.Kind, c.EventName, c.EventStartsAt)));
        var time = d.Changes.Single(c => c.Field == "time");
        Assert.Equal("""{"start":"2026-10-01T08:00:00Z","end":"2026-10-01T09:00:00Z"}""", time.OldValue!.ToJsonString());
        Assert.Equal("""{"start":"2026-10-01T09:00:00Z","end":"2026-10-01T10:00:00Z"}""", time.NewValue!.ToJsonString());
        Assert.Equal("""["R1","R3"]""", d.Changes.Single(c => c.Field == "rooms").NewValue!.ToJsonString());
        Assert.Null(d.Changes.Single(c => c.Field == "details.text").OldValue);
        Assert.Equal([now], d.Upserts);
        Assert.Empty(d.Deletes);
    }

    // Research R2: changed rows carry the event before and after without details; added and removed carry neither.
    [Fact]
    public void ChangedRowsCarryTheEventBeforeAndAfter()
    {
        var old = E("a", fields: new() { ["rooms"] = new JsonArray("R1"), ["details"] = new JsonObject { ["Foo"] = "x" } });
        var now = E("a", name: "Renamed", fields: new() { ["rooms"] = new JsonArray("R2"), ["details"] = new JsonObject { ["Foo"] = "x" } });
        var d = Diff([old, E("gone")], [now, E("new")]);
        var changed = d.Changes.Where(c => c.Kind == "changed").ToList();
        Assert.Equal(["name", "rooms"], changed.Select(c => c.Field!).Order());
        Assert.All(changed, c =>
        {
            Assert.Equal("""{"key":"a","name":"Lesson","start":"2026-10-01T08:00:00Z","end":"2026-10-01T09:00:00Z","status":"confirmed","fields":{"rooms":["R1"]}}""",
                c.EventBefore!.ToJsonString());
            Assert.Equal("""{"key":"a","name":"Renamed","start":"2026-10-01T08:00:00Z","end":"2026-10-01T09:00:00Z","status":"confirmed","fields":{"rooms":["R2"]}}""",
                c.EventAfter!.ToJsonString());
        });
        Assert.All(d.Changes.Where(c => c.Kind != "changed"), c => Assert.Equal((null, null), (c.EventBefore, c.EventAfter)));
        Assert.NotNull(old.Fields["details"]); // the snapshot state is untouched
    }

    [Fact]
    public void EndOnlyIsOneTimeChange()
    {
        var old = E("a");
        var now = old with { EndsAt = old.EndsAt.AddMinutes(30) };
        Assert.Equal("time", Assert.Single(Diff([old], [now]).Changes).Field);
    }

    [Fact]
    public void AddedAndRemovedCarryTheEvent()
    {
        var d = Diff([E("gone", name: "Old", startHour: 10)], [E("new", fields: new() { ["type"] = "test" })]);
        var added = d.Changes.Single(c => c.Kind == "added");
        Assert.Null(added.OldValue);
        Assert.Equal("""{"key":"new","name":"Lesson","start":"2026-10-01T08:00:00Z","end":"2026-10-01T09:00:00Z","status":"confirmed","fields":{"type":"test"}}""",
            added.NewValue!.ToJsonString());
        var removed = d.Changes.Single(c => c.Kind == "removed");
        Assert.Null(removed.NewValue);
        Assert.Equal(("gone", "Old", T.AddHours(2)), (removed.EventKey, removed.EventName, removed.EventStartsAt));
        Assert.Equal(["new"], d.Upserts.Select(u => u.Key));
        Assert.Equal(["gone"], d.Deletes);
    }

    [Fact]
    public void AbsentEqualsEmpty()
    {
        var d = Diff([E("a", fields: new() { ["rooms"] = new JsonArray() })], [E("a", fields: new() { ["details"] = new JsonObject() })]);
        Assert.Empty(d.Changes);
        Assert.Empty(d.Upserts);
    }

    [Fact]
    public void SetsIgnoreOrderBecauseListsAreSorted()
    {
        var a = E("a", fields: new() { ["groups"] = new JsonArray("A", "B") });
        Assert.Empty(Diff([a], [E("a", fields: new() { ["groups"] = new JsonArray("A", "B") })]).Changes);
    }

    [Fact]
    public void HorizonRule()
    {
        var previous = H.AddMonths(-1);
        var beyondOld = E("r|new", occurrence: previous.AddDays(1));
        var beyondNow = E("r|gone", occurrence: H.AddDays(1));
        var single = E("single-new");
        var d = Diff([beyondNow, E("single-gone")], [beyondOld, single], previous);

        Assert.Equal(["single-gone|removed", "single-new|added"], d.Changes.Select(c => $"{c.EventKey}|{c.Kind}").Order());
        Assert.Contains(beyondOld, d.Upserts);
        Assert.Contains("r|gone", d.Deletes);
        Assert.Contains("single-gone", d.Deletes);
    }

    [Fact]
    public void RecurringInsideHorizonIsRecorded()
    {
        var d = Diff([E("r|gone", occurrence: H.AddDays(-1))], [E("r|new", occurrence: H.AddMonths(-2))], H.AddMonths(-1));
        Assert.Equal(["r|gone|removed", "r|new|added"], d.Changes.Select(c => $"{c.EventKey}|{c.Kind}").Order());
    }

    [Fact]
    public void SnapshotHash()
    {
        EventState[] events = [E("a", fields: new() { ["type"] = "lesson" }), E("b")];
        var hash = Differ.SnapshotHash(events);
        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, Differ.SnapshotHash([events[1], events[0]]));
        Assert.NotEqual(hash, Differ.SnapshotHash([E("a", fields: new() { ["type"] = "test" }), E("b")]));
    }
}
