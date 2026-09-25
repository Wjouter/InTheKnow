using Xunit;
using static InTheKnow.Tests.LinkIdentityTests;

namespace InTheKnow.Tests;

public class SubscriptionsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Each add moves the fake clock one second on, so a validation never waits for a budget slot that
    // only fake time can free (0.5 s gaps at the default budget).
    internal static Task<AddLinkResult> AddAsync(Harness h, string subscriber, string url, string fixture = "mt-nl.ics")
    {
        h.Http.Map.TryAdd(new Uri(url).AbsoluteUri, fixture);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        return h.Subscriptions.AddLinkAsync(subscriber, url, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    [Fact]
    public async Task DuplicatesAndClassmatesJoin()
    {
        await using var h = await Harness.CreateAsync();
        var first = await AddAsync(h, "s1", MtLink(user: "1001", hex: "01"));
        Assert.True(first.CalendarIsNew);
        var hex = await AddAsync(h, "s1", MtLink(user: "1001", hex: "02"));
        var classmate = await AddAsync(h, "s2", MtLink(user: "2002"));
        Assert.Equal((first.CalendarId, false, false), (hex.CalendarId, hex.CalendarIsNew, hex.AlreadyExisted));
        Assert.Equal((first.CalendarId, false), (classmate.CalendarId, classmate.CalendarIsNew));
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM calendar"));
    }

    [Fact]
    public async Task OtherGroupsAndPersonalTimetablesAreSeparate()
    {
        await using var h = await Harness.CreateAsync();
        var group = await AddAsync(h, "s1", MtLink());
        var other = await AddAsync(h, "s1", MtLink(id: "ALET26VT4A"));
        var personal = await AddAsync(h, "s1", MtLink(kind: "xedule_personal"));
        Assert.True(other.CalendarIsNew);
        Assert.True(personal.CalendarIsNew);
        Assert.Equal(3, new[] { group.CalendarId, other.CalendarId, personal.CalendarId }.Distinct().Count());
    }

    [Fact]
    public async Task ForgedClassGroupLinkIsRejected()
    {
        await using var h = await Harness.CreateAsync(("InTheKnow:MyTimetables:Domains:0:EmptyFeedProtection", "false"));
        await AddAsync(h, "s1", MtLink(user: "1001"));
        var forged = await AddAsync(h, "s2", MtLink(user: "2002", token: "junk"), "mt-empty.ics");
        Assert.Equal("validation_failed:empty_feed", forged.Error);
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM calendar_link"));
        // Without protection, an empty feed may still create a calendar of its own.
        Assert.Null((await AddAsync(h, "s3", MtLink(id: "EMPTY"), "mt-empty.ics")).Error);
    }

    [Fact]
    public async Task EmptyFeedIsRejectedUnderProtection()
    {
        await using var h = await Harness.CreateAsync();
        Assert.Equal("validation_failed:empty_feed", (await AddAsync(h, "s1", MtLink(), "mt-empty.ics")).Error);
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM calendar"));
    }

    [Fact]
    public async Task GenericLinksNormalise()
    {
        await using var h = await Harness.CreateAsync();
        var a = await AddAsync(h, "s1", "https://Calendar.Example.com/a.ics?b=2&a=1", "generic.ics");
        var b = await AddAsync(h, "s2", "https://calendar.example.com/a.ics?a=1&b=2", "generic.ics");
        Assert.Equal((a.CalendarId, false), (b.CalendarId, b.CalendarIsNew));
    }

    [Fact]
    public async Task SameLinkAgainDoesNotFetch()
    {
        await using var h = await Harness.CreateAsync();
        var url = MtLink();
        var first = await AddAsync(h, "s1", url);
        var again = await AddAsync(h, "s1", url);
        Assert.Equal((first.CalendarId, first.LinkId, true), (again.CalendarId, again.LinkId, again.AlreadyExisted));
        Assert.Equal(1, h.Http.Count(new Uri(url).AbsoluteUri));
    }

    [Theory]
    [InlineData("a b", "https://example.com/a.ics", "invalid_subscriber")]
    [InlineData("s1", "ftp://example.com/a.ics", "unsupported_scheme")]
    [InlineData("s1", "http://example.com/a.ics", "unsupported_scheme")]
    public async Task RejectedInputs(string subscriber, string url, string reason)
    {
        await using var h = await Harness.CreateAsync();
        Assert.Equal(reason, (await AddAsync(h, subscriber, url)).Error);
        Assert.Empty(h.Http.PerUrl);
    }

    [Fact]
    public async Task BudgetExhausted()
    {
        await using var h = await Harness.CreateAsync();
        h.Time.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 11; i++) h.Budget.TryReserve(Harness.Mt, h.Now + TimeSpan.FromSeconds(1), TimeSpan.MaxValue); // 5.5 s queued
        Assert.Equal("host_budget_exhausted", (await AddAsync(h, "s1", MtLink())).Error);
    }

    [Fact]
    public async Task NewCalendarIsBaselinedFromValidation()
    {
        await using var h = await Harness.CreateAsync();
        var added = await AddAsync(h, "s1", MtLink());
        var addedAt = h.Now;
        var (seeded, _) = await h.SeedAsync("mt-nl.ics");
        h.Time.Advance(TimeSpan.FromSeconds(1)); // past the budget slot the validation took
        await h.RunAsync(); // baselines only the seeded calendar; the added one is not due

        const string rows = "SELECT event_key, name, starts_at, ends_at, status, occurrence_start, fields::text FROM event_snapshot WHERE calendar_id = $1 ORDER BY event_key";
        var expected = await h.Db.RowsAsync(rows, seeded);
        Assert.Equal(26, expected.Count);
        Assert.Equal(expected, await h.Db.RowsAsync(rows, added.CalendarId!.Value));
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM detected_change"));
        var c = (await h.Db.RowsAsync("""
            SELECT a.baseline_features, a.snapshot_hash = b.snapshot_hash, a.horizon_end IS NOT NULL, a.last_ok, a.next_check_at, a.last_link_id
            FROM calendar a, calendar b WHERE a.id = $1 AND b.id = $2
            """, added.CalendarId.Value, seeded)).Single();
        Assert.Equal(("mt:111111", true, true, true, added.LinkId), ((string)c[0]!, (bool)c[1]!, (bool)c[2]!, (bool)c[3]!, (long?)c[5]));
        Assert.Equal(addedAt + TimeSpan.FromMinutes(10), new DateTimeOffset((DateTime)c[4]!));

        var xmin = await h.XminAsync(added.CalendarId.Value);
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await h.RunAsync();
        Assert.Equal(xmin, await h.XminAsync(added.CalendarId.Value));
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM detected_change"));
    }

    [Fact]
    public async Task JoiningStoresNothing()
    {
        await using var h = await Harness.CreateAsync();
        var first = await AddAsync(h, "s1", MtLink(user: "1001"));
        var id = first.CalendarId!.Value;
        var xmin = await h.XminAsync(id);
        var calendar = await h.Db.RowsAsync("SELECT * FROM calendar WHERE id = $1", id);
        var joined = await AddAsync(h, "s2", MtLink(user: "2002"), "mt-nl-edited.ics");
        Assert.Equal(id, joined.CalendarId);
        Assert.Equal(xmin, await h.XminAsync(id));
        Assert.Equal(calendar, await h.Db.RowsAsync("SELECT * FROM calendar WHERE id = $1", id));
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM detected_change"));
    }

    // 002 edge case "subscriber removes a subscription": the filter goes, and re-subscribing starts on Default.
    [Fact]
    public async Task RemoveDeletesTheFilter()
    {
        await using var h = await Harness.CreateAsync();
        var presets = new Presets(h.Db.DataSource, h.Settings);
        var a = await AddAsync(h, "s1", MtLink(user: "1001"));
        await AddAsync(h, "s2", MtLink(user: "2002"));
        var calendar = a.CalendarId!.Value;
        foreach (var s in new[] { "s1", "s2" })
            Assert.True(await presets.ReplaceFilterAsync(s, calendar, FilterEngineTests.R(PresetsTests.HideZelf), Ct));

        Assert.True(await h.Subscriptions.RemoveAsync("s1", calendar, Ct));
        Assert.Equal([(calendar, "s2", null, 1L)], await PresetsTests.FiltersAsync(h.Db));
        await AddAsync(h, "s1", MtLink(user: "1001", hex: "05"));
        Assert.True((await presets.GetFilterAsync("s1", calendar, Ct))!.Filter.Preset!.IsDefault);
    }

    // SC-003, US3-1: one scheduled request per calendar per interval, plus one per validation.
    [Fact]
    public async Task OneRequestPerCalendarPerInterval()
    {
        await using var h = await Harness.CreateAsync();
        var urls = Enumerable.Range(1, 4).Select(i => MtLink(user: $"{i}00{i}")).ToList();
        foreach (var (url, i) in urls.Select((u, i) => (u, i))) await AddAsync(h, $"s{i}", url);
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM calendar"));
        for (var i = 0; i < 3; i++)
        {
            h.Time.Advance(TimeSpan.FromMinutes(10));
            await h.RunAsync();
        }
        Assert.Equal(3 + 4, urls.Sum(u => h.Http.Count(new Uri(u).AbsoluteUri)));
    }
}
