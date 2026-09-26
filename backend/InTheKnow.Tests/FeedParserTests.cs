using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InTheKnow.Tests;

public class FeedParserTests
{
    static readonly DateTimeOffset Horizon = new(2027, 9, 24, 0, 0, 0, TimeSpan.Zero);
    static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

    static Dictionary<string, Occurrence> Parse(string ics)
    {
        var r = FeedParser.Parse(ics, Horizon, NullLogger.Instance, 1, TestContext.Current.CancellationToken);
        Assert.Null(r.Error);
        return r.Events!.ToDictionary(e => e.Key);
    }

    static Dictionary<string, Occurrence> Generic() => Parse(File.ReadAllText(FakeHttp.FixturePath("generic.ics")));

    [Fact]
    public void ExpandsRecurrenceWithExdateAndOverride()
    {
        var e = Generic();
        var weekly = e.Keys.Where(k => k.StartsWith("weekly@test|")).Order().ToList();
        Assert.Equal(
        [
            "weekly@test|2026-10-15T07:00:00Z", "weekly@test|2026-10-22T07:00:00Z", "weekly@test|2026-11-05T08:00:00Z",
            "weekly@test|2026-11-12T08:00:00Z", "weekly@test|2026-11-19T08:00:00Z",
        ], weekly);
        var moved = e["weekly@test|2026-11-05T08:00:00Z"];
        Assert.Equal(Utc("2026-11-05T09:00:00Z"), moved.StartsAt);
        Assert.Equal(Utc("2026-11-05T08:00:00Z"), moved.OccurrenceStart);
        Assert.Equal(Utc("2026-10-15T07:00:00Z"), e["weekly@test|2026-10-15T07:00:00Z"].OccurrenceStart);
    }

    [Fact]
    public void TimeZones()
    {
        var e = Generic();
        Assert.Equal(Utc("2026-10-22T07:00:00Z"), e["weekly@test|2026-10-22T07:00:00Z"].StartsAt); // CEST
        Assert.Equal(Utc("2026-11-12T08:00:00Z"), e["weekly@test|2026-11-12T08:00:00Z"].StartsAt); // CET
        Assert.Equal(Utc("2026-12-01T08:00:00Z"), e["floating@test"].StartsAt);
        Assert.Equal(Utc("2026-03-31T22:00:00Z"), e["allday@test"].StartsAt);
        Assert.Equal(Utc("2026-04-01T22:00:00Z"), e["allday@test"].EndsAt);
        Assert.Null(e["allday@test"].OccurrenceStart);
    }

    [Fact]
    public void MissingUidGetsStableContentKey()
    {
        var a = Generic().Keys.Single(k => k.StartsWith("content:"));
        var b = Generic().Keys.Single(k => k.StartsWith("content:"));
        Assert.Equal(a, b);
        Assert.Equal("content:".Length + 64, a.Length);
    }

    [Fact]
    public void DuplicateUidsKeyedByStart()
    {
        var e = Generic();
        Assert.Equal("Duplicate UID one", e["dup@test|2026-12-03T08:00:00Z"].Name);
        Assert.Equal("Duplicate UID two", e["dup@test|2026-12-04T08:00:00Z"].Name);
        Assert.DoesNotContain("dup@test", e.Keys);
    }

    [Fact]
    public void MalformedEvents()
    {
        var e = Generic();
        Assert.Equal(e["badend@test"].StartsAt, e["badend@test"].EndsAt);
        Assert.DoesNotContain(e.Keys, k => k.StartsWith("nostart@test"));
        Assert.Equal("", e["nosummary@test"].Name);
        Assert.Equal("cancelled", e["cancelled@test"].Status);
        Assert.Null(e["single@test"].Status);
    }

    [Fact]
    public void Horizon_()
    {
        var e = Generic();
        Assert.Equal(Utc("2030-01-01T09:00:00Z"), e["future@test"].StartsAt);
        var daily = e.Values.Where(o => o.Key.StartsWith("daily@test|")).ToList();
        Assert.All(daily, o => Assert.True(o.StartsAt < Horizon));
        Assert.Equal((int)(Horizon - Utc("2026-09-01T10:00:00Z")).TotalDays + 1, daily.Count);
    }

    [Fact]
    public void Bounds()
    {
        var minutely = "BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:m@test\nDTSTART:20260901T000000Z\nDTEND:20260901T000100Z\nRRULE:FREQ=MINUTELY\nSUMMARY:x\nEND:VEVENT\nEND:VCALENDAR\n";
        Assert.Equal("too_many_occurrences", FeedParser.Parse(minutely, Horizon, NullLogger.Instance, 1, TestContext.Current.CancellationToken).Error);
        Assert.Equal("unparseable", FeedParser.Parse("this is not a calendar\n<html>", Horizon, NullLogger.Instance, 1, TestContext.Current.CancellationToken).Error);
    }

    [Fact]
    public void LongKeysAreHashed()
    {
        var uid = new string('u', 1100);
        var ics = $"BEGIN:VCALENDAR\nVERSION:2.0\nBEGIN:VEVENT\nUID:{uid}\nDTSTART:20260901T000000Z\nSUMMARY:x\nEND:VEVENT\nEND:VCALENDAR\n";
        var key = Assert.Single(Parse(ics).Keys);
        Assert.Equal("sha256:" + FeedParser.Sha256Hex(uid), key);
    }

    // 003 research R12: the feed's calendar name.
    [Fact]
    public void FeedName()
    {
        string? Name(string fixture) => FeedParser.Parse(File.ReadAllText(FakeHttp.FixturePath(fixture)), Horizon, NullLogger.Instance, 1,
            TestContext.Current.CancellationToken).FeedName;
        Assert.Equal("Hogeschool Inholland - Rooster - ALET26VT4A", Name("mt-nl.ics"));
        Assert.Null(Name("generic.ics"));
        Assert.Equal("a b", FeedParser.CleanName(" a\r\n b\u0007 "));
        Assert.Null(FeedParser.CleanName(" \t "));
        Assert.Null(FeedParser.CleanName(null));
        Assert.Equal(100, FeedParser.CleanName(new string('x', 150))!.Length);
    }
}
