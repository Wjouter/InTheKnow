using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace InTheKnow.Tests;

// A per-test database, fake HTTP and a fake clock, with a Checker built from them.
sealed class Harness : IAsyncDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    public const string Mt = "mytimetables.inholland.nl";

    public required TestDb Db { get; init; }
    // Three accounts (004): the subscribers s1, s2 and s3 of 001 to 003's stories.
    public long S1 { get; private set; }
    public long S2 { get; private set; }
    public long S3 { get; private set; }
    public FakeTimeProvider Time { get; } = new(T0);
    public FakeHttp Http { get; }
    public Settings Settings { get; private set; } = null!;
    public HostBudget Budget { get; private set; } = null!;
    public Checker Checker { get; private set; } = null!;
    public ILoggerFactory Logs { get; init; } = NullLoggerFactory.Instance;

    Harness() => Http = new FakeHttp(Time);

    public static async Task<Harness> CreateAsync(params (string Key, string? Value)[] overrides) =>
        await CreateAsync(NullLoggerFactory.Instance, overrides);

    public static async Task<Harness> CreateAsync(ILoggerFactory logs, params (string Key, string? Value)[] overrides)
    {
        var h = new Harness { Db = await TestDb.CreateAsync(), Logs = logs };
        (h.S1, h.S2, h.S3) = await TestHost.AccountsAsync(h.Db.DataSource);
        h.Restart(overrides);
        return h;
    }

    // A new Settings, HostBudget and Checker on the same database: what a process restart does.
    public Checker Restart(params (string Key, string? Value)[] overrides)
    {
        Settings = Db.Settings(overrides);
        Budget = new HostBudget(Settings);
        return Checker = new Checker(Db.DataSource, Fetcher, Budget, Settings, Time, Logs.CreateLogger<Checker>());
    }

    public Fetcher Fetcher => field ??= new Fetcher(Http, FakeHttp.Resolve);

    public Subscriptions Subscriptions => new(Db.DataSource, Fetcher, Budget, Settings, Time);

    public DateTimeOffset Now => Time.GetUtcNow();

    public Task RunAsync() => Checker.RunDueChecksAsync(Now).WaitAsync(TimeSpan.FromSeconds(30));

    public async Task<(long Id, string Url)> SeedAsync(string fixture, string host = Mt, DateTimeOffset? nextCheckAt = null, long? subscriber = null)
    {
        var url = $"https://{host}/ical?{Guid.NewGuid():N}";
        Http.Map[url] = fixture;
        var (id, _) = await Db.SeedCalendarAsync(host, [url], subscriber ?? S1, nextCheckAt);
        return (id, url);
    }

    public async Task<List<(string Key, string Kind, string? Field)>> ChangesAsync(long calendarId) =>
        [.. (await Db.RowsAsync("SELECT event_key, kind, field FROM detected_change WHERE calendar_id = $1 ORDER BY id", calendarId))
            .Select(r => ((string)r[0]!, (string)r[1]!, (string?)r[2]))];

    public async Task<object?[]> CalendarAsync(long id) => (await Db.RowsAsync(
        "SELECT status, last_ok, last_error, consecutive_failures, failing_since, next_check_at, baseline_features, lease_id FROM calendar WHERE id = $1", id)).Single();

    public async Task<Dictionary<string, string>> XminAsync(long calendarId) =>
        (await Db.RowsAsync("SELECT event_key, xmin::text FROM event_snapshot WHERE calendar_id = $1", calendarId))
            .ToDictionary(r => (string)r[0]!, r => (string)r[1]!);

    // Waits in real time for something another task does (never for fake-time timers).
    public static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    public static Task UntilAsync(Func<bool> condition) => UntilAsync(() => Task.FromResult(condition()));

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

public class CheckerTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    const string Room = "Xedule!GI3TINRWGY3Q@schedule.inholland.nl";

    // The edits in mt-nl-edited.ics, relative to mt-nl.ics:
    // - GY3Q: LOCATION AL00.A1.38 -> AL00.A1.20 and DTEND 30 min later (US1-1, US1-2)
    // - GUZQ: second teacher replaced (US1-3)
    // - HAZQ: SUMMARY renamed (US1-4)
    // - HAYQ: gains [CONCEPT]; HEYQ: loses [CONCEPT] (US1-4)
    // - NEWEVENT added; GIZA deleted (US1-5)
    // - GQZA: Groep(en) order reversed, which is not a change (FR-026)
    static readonly (string, string, string?)[] KnownEdits =
    [
        (Room, "changed", "rooms"),
        (Room, "changed", "time"),
        ("Xedule!GI3TINRWGUZQ@schedule.inholland.nl", "changed", "teachers"),
        ("Xedule!GI3TINRWHAYQ@schedule.inholland.nl", "changed", "status"),
        ("Xedule!GI4TANBYHAZQ@schedule.inholland.nl", "changed", "name"),
        ("Xedule!GI4TANRRGIZA@schedule.inholland.nl", "removed", null),
        ("Xedule!GI4TGNBVHEYQ@schedule.inholland.nl", "changed", "status"),
        ("Xedule!NEWEVENT@schedule.inholland.nl", "added", null),
    ];

    static IEnumerable<(string, string, string?)> Sorted(IEnumerable<(string, string, string?)> c) => c.Order();

    async Task<(Harness H, long Id, string Url)> BaselineAsync(string fixture = "mt-nl.ics", string host = Harness.Mt, params (string, string?)[] overrides)
    {
        var h = await Harness.CreateAsync(overrides);
        var (id, url) = await h.SeedAsync(fixture, host);
        await h.RunAsync();
        return (h, id, url);
    }

    async Task NextIntervalAsync(Harness h)
    {
        h.Time.Advance(h.Settings.Options.CheckInterval);
        await h.RunAsync();
    }

    [Fact]
    public async Task BaselineRecordsNothing()
    {
        var (h, id, _) = await BaselineAsync();
        await using var _h = h;
        Assert.Empty(await h.ChangesAsync(id));
        Assert.Equal(26, await h.Db.ScalarAsync("SELECT count(*) FROM event_snapshot WHERE calendar_id = $1", id));
        var c = await h.CalendarAsync(id);
        Assert.Equal(("ok", true, "mt:111111", null), ((string)c[0]!, (bool)c[1]!, (string)c[6]!, c[7]));
        Assert.Equal(Harness.T0 + TimeSpan.FromMinutes(10), new DateTimeOffset((DateTime)c[5]!));
    }

    [Fact]
    public async Task KnownEditsAreRecordedExactly()
    {
        var (h, id, url) = await BaselineAsync();
        await using var _h = h;
        h.Http.Map[url] = "mt-nl-edited.ics";
        await NextIntervalAsync(h);
        Assert.Equal(Sorted(KnownEdits), Sorted(await h.ChangesAsync(id)));
        var grouped = await h.Db.ScalarAsync("SELECT count(DISTINCT detected_at) FROM detected_change WHERE event_key = $1", Room);
        Assert.Equal(1, grouped);
        var rooms = (await h.Db.RowsAsync("SELECT old_value::text, new_value::text FROM detected_change WHERE field = 'rooms'")).Single();
        Assert.Equal(("[\"AL00.A1.38\"]", "[\"AL00.A1.20\"]"), ((string)rooms[0]!, (string)rooms[1]!));

        // Research R2: field changes carry the event before and after; added and removed rows do not.
        var events = (await h.Db.RowsAsync("""
            SELECT event_before->'fields'->>'rooms', event_after->'fields'->>'rooms', event_after->'fields' ? 'details'
            FROM detected_change WHERE field = 'rooms'
            """)).Single();
        Assert.Equal(("[\"AL00.A1.38\"]", "[\"AL00.A1.20\"]", false), ((string)events[0]!, (string)events[1]!, (bool)events[2]!));
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM detected_change WHERE kind = 'changed' AND (event_before IS NULL OR event_after IS NULL)"));
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM detected_change WHERE kind <> 'changed' AND (event_before IS NOT NULL OR event_after IS NOT NULL)"));
    }

    [Theory]
    [InlineData("mt-nl-volatile.ics")]
    [InlineData("mt-en.ics")]
    public async Task VolatileAndLanguageChangesRecordNothing(string next)
    {
        var (h, id, url) = await BaselineAsync();
        await using var _h = h;
        h.Http.Map[url] = next;
        await NextIntervalAsync(h);
        Assert.Empty(await h.ChangesAsync(id));
        Assert.Equal(true, (await h.CalendarAsync(id))[1]);
    }

    [Fact]
    public async Task RestartRecordsEachChangeOnce()
    {
        var (h, id, url) = await BaselineAsync();
        await using var _h = h;
        h.Restart();
        h.Http.Map[url] = "mt-nl-edited.ics";
        await NextIntervalAsync(h);
        Assert.Equal(Sorted(KnownEdits), Sorted(await h.ChangesAsync(id)));
        await NextIntervalAsync(h);
        Assert.Equal(KnownEdits.Length, (await h.ChangesAsync(id)).Count);
    }

    [Fact]
    public async Task GenericFeed()
    {
        var (h, id, url) = await BaselineAsync("generic.ics", "calendar.example.com");
        await using var _h = h;
        Assert.Equal("generic", (await h.CalendarAsync(id))[6]);
        h.Http.Map[url] = "generic-edited.ics";
        await NextIntervalAsync(h);
        Assert.Equal([("weekly@test|2026-11-05T08:00:00Z", "changed", "name")], await h.ChangesAsync(id));

        var before = await h.Db.ScalarAsync("SELECT count(*) FROM event_snapshot WHERE calendar_id = $1", id);
        h.Time.Advance(TimeSpan.FromDays(31));
        await h.RunAsync();
        Assert.Single(await h.ChangesAsync(id));
        Assert.True(await h.Db.ScalarAsync("SELECT count(*) FROM event_snapshot WHERE calendar_id = $1", id) > before);
    }

    // Google Calendar's shape: X-WR-TIMEZONE, all-day VALUE=DATE, UTC times, and a moved occurrence as a RECURRENCE-ID override.
    [Fact]
    public async Task GoogleFeedTimeMovesAreRecordedAndFannedOut()
    {
        var (h, id, url) = await BaselineAsync("google.ics", "calendar.google.com");
        await using var _h = h;
        Assert.Equal(6, await h.Db.ScalarAsync("SELECT count(*) FROM event_snapshot WHERE calendar_id = $1", id));
        var channel = await ChannelAsync(h.Db, h.S1);

        h.Http.Map[url] = "google-edited.ics";
        await NextIntervalAsync(h);
        Assert.Equal(
        [
            ("allday@google.com", "changed", "time"),
            ("single@google.com", "changed", "time"),
            ("weekly@google.com|2026-11-09T08:00:00Z", "changed", "time"),
        ], Sorted(await h.ChangesAsync(id)));
        var single = await h.Db.RowsAsync("SELECT old_value::text, new_value::text FROM detected_change WHERE event_key = 'single@google.com'");
        Assert.Equal([["""{"end": "2026-11-12T16:30:00Z", "start": "2026-11-12T16:00:00Z"}""",
                       """{"end": "2026-11-12T17:30:00Z", "start": "2026-11-12T17:00:00Z"}"""]], single);
        Assert.Equal([[channel, 3L]], await h.Db.RowsAsync("SELECT channel_id, count(*) FROM delivery WHERE status = 'pending' GROUP BY channel_id"));
    }

    [Fact]
    public async Task UnchangedFeedWritesNoSnapshotRows()
    {
        var (h, id, url) = await BaselineAsync();
        await using var _h = h;
        var xmin = await h.XminAsync(id);
        await NextIntervalAsync(h);
        await NextIntervalAsync(h);
        Assert.Equal(xmin, await h.XminAsync(id));

        var edited = File.ReadAllText(FakeHttp.FixturePath("mt-nl.ics")).Replace("LOCATION:AL00.A1.38", "LOCATION:AL00.A1.99");
        var first = edited.IndexOf("LOCATION:AL00.A1.99", StringComparison.Ordinal);
        var oneRoom = File.ReadAllText(FakeHttp.FixturePath("mt-nl.ics")).Remove(first, "LOCATION:AL00.A1.38".Length).Insert(first, "LOCATION:AL00.A1.99");
        var path = Path.Combine(Path.GetTempPath(), $"itk-{Guid.NewGuid():N}.ics");
        File.WriteAllText(path, oneRoom);
        try
        {
            h.Http.Map[url] = path;
            await NextIntervalAsync(h);
        }
        finally { File.Delete(path); }
        var after = await h.XminAsync(id);
        Assert.Single(after, kv => xmin[kv.Key] != kv.Value);
    }

    [Theory]
    [InlineData("500", "http_500")]
    [InlineData("timeout", "timeout")]
    [InlineData("garbage", "unparseable")]
    public async Task FailuresKeepTheSnapshot(string reply, string code)
    {
        var (h, id, url) = await BaselineAsync();
        await using var _h = h;
        var xmin = await h.XminAsync(id);
        h.Http.Map[url] = reply switch { "500" => HttpStatusCode.InternalServerError, "timeout" => Reply.Timeout, _ => Reply.Bytes(1000) };
        await NextIntervalAsync(h);
        var firstFailure = h.Now;
        await NextIntervalAsync(h);

        Assert.Equal(xmin, await h.XminAsync(id));
        Assert.Empty(await h.ChangesAsync(id));
        var c = await h.CalendarAsync(id);
        Assert.Equal(("ok", false, code, 2), ((string)c[0]!, (bool)c[1]!, (string)c[2]!, (int)c[3]!));
        Assert.Equal(firstFailure, new DateTimeOffset((DateTime)c[4]!));
    }

    [Fact]
    public async Task EmptyFeedFlagsLinkAndFallsBack()
    {
        await using var h = await Harness.CreateAsync();
        string[] urls = [$"https://{Harness.Mt}/ical?one", $"https://{Harness.Mt}/ical?two"];
        foreach (var u in urls) h.Http.Map[u] = "mt-nl.ics";
        var (id, links) = await h.Db.SeedCalendarAsync(Harness.Mt, urls, h.S1);
        await h.RunAsync();
        Assert.Equal(1, h.Http.Count(urls[0]));

        // Link 1 revoked: no removals, link 1 flagged, next check through link 2.
        h.Http.Map[urls[0]] = "mt-empty.ics";
        await NextIntervalAsync(h);
        Assert.Empty(await h.ChangesAsync(id));
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM calendar_link WHERE id = $1 AND flagged_at IS NOT NULL", links[0]));
        Assert.Equal("empty_feed", (await h.CalendarAsync(id))[2]);
        await NextIntervalAsync(h);
        Assert.Equal(1, h.Http.Count(urls[1]));
        Assert.Equal(true, (await h.CalendarAsync(id))[1]);

        // Every link empty: the calendar is failing and waits the failing interval.
        h.Http.Map[urls[1]] = "mt-empty.ics";
        await NextIntervalAsync(h);
        var start = h.Now;
        var c = await h.CalendarAsync(id);
        Assert.Equal("failing", c[0]);
        Assert.Equal(start + TimeSpan.FromHours(24), new DateTimeOffset((DateTime)c[5]!));
        Assert.Empty(await h.ChangesAsync(id));

        // A flagged link serves the same feed as before (the hash no-op path): back to ok.
        foreach (var u in urls) h.Http.Map[u] = "mt-nl.ics";
        h.Time.Advance(TimeSpan.FromHours(24));
        await h.RunAsync();
        start = h.Now;
        c = await h.CalendarAsync(id);
        Assert.Equal(("ok", true, 0), ((string)c[0]!, (bool)c[1]!, (int)c[3]!));
        Assert.Null(c[4]);
        Assert.Equal(start + TimeSpan.FromMinutes(10), new DateTimeOffset((DateTime)c[5]!));
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM calendar_link WHERE calendar_id = $1 AND flagged_at IS NULL", id));
        Assert.Empty(await h.ChangesAsync(id));
    }

    [Fact]
    public async Task RetentionDeletesExpiredChangesInBatches()
    {
        await using var h = await Harness.CreateAsync();
        var (id, _) = await h.Db.SeedCalendarAsync("x.example", ["https://x.example/a"], h.S1);
        const string insert = """
            INSERT INTO detected_change (calendar_id, event_key, kind, new_value, event_name, event_starts_at, detected_at)
            SELECT $1, 'k' || g, 'added', '{}', 'n', $2, $2 FROM generate_series(1, $3) g
            """;
        await h.Db.ExecAsync(insert, id, h.Now - TimeSpan.FromDays(366), 25_000);
        await h.Db.ExecAsync(insert, id, h.Now - TimeSpan.FromDays(364), 3);
        await h.Checker.RunRetentionAsync(h.Now, Ct);
        Assert.Equal(3, await h.Db.ScalarAsync("SELECT count(*) FROM detected_change"));
    }

    // SC-012, FR-014a. Calendars are on distinct hosts, because the budget allows one claim per host per pass.
    [Fact]
    public async Task StuckFetchTimesOutAndReleasesItsSlot()
    {
        await using var h = await Harness.CreateAsync(("InTheKnow:MaxConcurrentChecks", "1"));
        var normal = new List<long>();
        for (var i = 0; i < 3; i++) normal.Add((await h.SeedAsync("mt-nl.ics", $"h{i}.example", Harness.T0.AddMinutes(-10 + i))).Id);
        var (stuck, stuckUrl) = await h.SeedAsync("mt-nl.ics", "stuck.example", Harness.T0.AddMinutes(-1));
        h.Http.Map[stuckUrl] = Reply.Hang;

        var run = h.Checker.RunDueChecksAsync(h.Now, Ct);
        await Harness.UntilAsync(() => h.Http.Count(stuckUrl) == 1);
        foreach (var id in normal) Assert.Equal(true, (await h.CalendarAsync(id))[1]);
        var (later, _) = await h.SeedAsync("mt-nl.ics", "later.example");

        h.Time.Advance(TimeSpan.FromMinutes(2));
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        var c = await h.CalendarAsync(stuck);
        Assert.Equal((false, "timeout"), ((bool)c[1]!, (string)c[2]!));
        Assert.Equal(true, (await h.CalendarAsync(later))[1]); // the slot was released
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM event_snapshot WHERE calendar_id = $1", stuck));
    }

    [Fact]
    public async Task StuckHookTimesOutAndTheOrphanCommitsNothing()
    {
        await using var h = await Harness.CreateAsync(("InTheKnow:MaxConcurrentChecks", "1"));
        var (stuck, url) = await h.SeedAsync("mt-nl.ics", "stuck.example");
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var blockOnce = 1;
        h.Checker.BeforeWriteHook = async (id, _) =>
        {
            if (id != stuck || Interlocked.Exchange(ref blockOnce, 0) == 0) return;
            entered.SetResult();
            await release.Task; // ignores cancellation, like CPU-bound work
        };

        var run = h.Checker.RunDueChecksAsync(h.Now, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        var (later, _) = await h.SeedAsync("mt-nl.ics", "later.example");
        h.Time.Advance(TimeSpan.FromMinutes(2));
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal("timeout", (await h.CalendarAsync(stuck))[2]);
        Assert.Equal(true, (await h.CalendarAsync(later))[1]);

        // Claimed again by a later pass (this time without blocking) ...
        h.Http.Map[url] = "mt-nl-edited.ics";
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await h.RunAsync();
        var snapshot = await h.XminAsync(stuck);
        var calendar = await h.CalendarAsync(stuck);

        // ... then the orphan resumes: it commits nothing.
        release.SetResult();
        await Task.Delay(200, Ct);
        Assert.Equal(snapshot, await h.XminAsync(stuck));
        Assert.Equal(calendar, await h.CalendarAsync(stuck));
        Assert.Empty(await h.ChangesAsync(stuck));
    }

    [Fact]
    public async Task TimeoutWhileTheRowIsLockedStillReleasesTheSlot()
    {
        await using var h = await Harness.CreateAsync(("InTheKnow:MaxConcurrentChecks", "1"));
        var (stuck, url) = await h.SeedAsync("mt-nl.ics", "stuck.example");
        h.Http.Map[url] = Reply.Hang;
        var run = h.Checker.RunDueChecksAsync(h.Now, Ct);
        await Harness.UntilAsync(() => h.Http.Count(url) == 1);

        await using var locker = await h.Db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await locker.BeginTransactionAsync(Ct);
        await using (var cmd = new Npgsql.NpgsqlCommand("SELECT id FROM calendar WHERE id = $1 FOR UPDATE", locker, tx) { Parameters = { new() { Value = stuck } } })
            await cmd.ExecuteScalarAsync(Ct);
        var (later, _) = await h.SeedAsync("mt-nl.ics", "later.example");

        h.Time.Advance(TimeSpan.FromMinutes(2));
        await Harness.UntilAsync(async () => (await h.CalendarAsync(later))[1] is true); // checked while the lock is held
        Assert.False(run.IsCompleted);
        await tx.RollbackAsync(Ct);

        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal("timeout", (await h.CalendarAsync(stuck))[2]);
        Assert.Empty(await h.ChangesAsync(stuck));
    }

    [Fact]
    public async Task ExpiredLeaseIsRecoveredOnce()
    {
        var (h, id, url) = await BaselineAsync();
        await using var _h = h;
        h.Http.Map[url] = "mt-nl-edited.ics";
        h.Time.Advance(TimeSpan.FromMinutes(10));
        // A claim by a process that then died.
        await h.Db.ExecAsync("UPDATE calendar SET lease_id = $1, next_check_at = $2 WHERE id = $3", Guid.NewGuid(), h.Now + TimeSpan.FromMinutes(5), id);
        await h.RunAsync();
        Assert.Equal(1, h.Http.Count(url));
        h.Time.Advance(TimeSpan.FromMinutes(5));
        await h.RunAsync();
        Assert.Equal(2, h.Http.Count(url));
        Assert.Equal(KnownEdits.Length, (await h.ChangesAsync(id)).Count);
        await NextIntervalAsync(h);
        Assert.Equal(KnownEdits.Length, (await h.ChangesAsync(id)).Count);
    }

    // Runs a pass, then moves the fake clock one step, until `span` has passed.
    static async Task RunForAsync(Harness h, TimeSpan span, TimeSpan step)
    {
        for (var end = h.Now + span; h.Now < end; h.Time.Advance(step)) await h.RunAsync();
    }

    static List<DateTimeOffset> Times(Harness h, string host) => [.. h.Http.Requests.Where(r => r.Host == host).Select(r => r.At)];

    static List<TimeSpan> Gaps(List<DateTimeOffset> times) => [.. times.Zip(times.Skip(1), (a, b) => b - a)];

    [Fact]
    public async Task ChecksFollowTheInterval()
    {
        await using var h = await Harness.CreateAsync(("InTheKnow:CheckInterval", "00:30:00"));
        await h.SeedAsync("mt-nl.ics");
        await RunForAsync(h, TimeSpan.FromHours(3), TimeSpan.FromMinutes(5));
        var times = Times(h, Harness.Mt);
        Assert.Equal(6, times.Count);
        Assert.All(Gaps(times), g => Assert.Equal(TimeSpan.FromMinutes(30), g));
    }

    [Fact]
    public async Task FailingCalendarsUseTheFailingInterval()
    {
        await using var h = await Harness.CreateAsync(("InTheKnow:CheckInterval", "00:30:00"), ("InTheKnow:FailingCheckInterval", "06:00:00"));
        var (id, url) = await h.SeedAsync("mt-nl.ics");
        await h.RunAsync();
        h.Http.Map[url] = "mt-empty.ics";
        await RunForAsync(h, TimeSpan.FromHours(13), TimeSpan.FromMinutes(5));
        Assert.Equal("failing", (await h.CalendarAsync(id))[0]);
        var failing = Times(h, Harness.Mt).Skip(1).ToList(); // after the baseline: 00:30, 06:30, 12:30
        Assert.Equal(3, failing.Count);
        Assert.All(Gaps(failing), g => Assert.Equal(TimeSpan.FromHours(6), g));

        h.Http.Map[url] = "mt-nl.ics";
        var before = Times(h, Harness.Mt).Count;
        await RunForAsync(h, TimeSpan.FromHours(7), TimeSpan.FromMinutes(5));
        var recovered = Times(h, Harness.Mt).Skip(before).ToList();
        Assert.Equal("ok", (await h.CalendarAsync(id))[0]);
        Assert.All(Gaps(recovered), g => Assert.Equal(TimeSpan.FromMinutes(30), g));
        Assert.True(recovered.Count > 2);
    }

    // SC-013, FR-012a, FR-012b: 50 calendars due at once on one host at 10 requests a minute.
    [Fact]
    public async Task BudgetSpacesABacklogEvenly()
    {
        await using var h = await Harness.CreateAsync(("InTheKnow:MaxRequestsPerHostPerMinute", "10"));
        var seeded = new List<long>();
        for (var i = 0; i < 50; i++) seeded.Add((await h.SeedAsync("mt-nl.ics", nextCheckAt: Harness.T0.AddMinutes(-60).AddMilliseconds(50 - i))).Id);
        await RunForAsync(h, TimeSpan.FromMinutes(6), TimeSpan.FromSeconds(1));

        var times = Times(h, Harness.Mt);
        Assert.Equal(50, times.Count);
        Assert.All(Gaps(times), g => Assert.Equal(TimeSpan.FromSeconds(6), g));
        Assert.True(times[^1] - Harness.T0 < TimeSpan.FromMinutes(5));
        var rows = await h.Db.RowsAsync("SELECT id, last_checked_at, next_check_at FROM calendar ORDER BY last_checked_at");
        Assert.Equal(Enumerable.Reverse(seeded), rows.Select(r => (long)r[0]!)); // longest-waiting first
        Assert.All(rows, r => Assert.Equal((DateTime)r[1]! + TimeSpan.FromMinutes(10), (DateTime)r[2]!));
    }

    [Fact]
    public async Task ValidationDuringABacklogTakesTheNextSlot()
    {
        await using var h = await Harness.CreateAsync(("InTheKnow:MaxRequestsPerHostPerMinute", "10"));
        for (var i = 0; i < 5; i++) await h.SeedAsync("mt-nl.ics");
        await h.RunAsync(); // one check at T0; the host is free again at T0 + 6 s
        h.Time.Advance(TimeSpan.FromSeconds(1));

        var link = LinkIdentityTests.MtLink(id: "NEWGROUP");
        h.Http.Map[new Uri(link).AbsoluteUri] = "mt-nl.ics";
        var add = h.Subscriptions.AddLinkAsync(await TestHost.AccountAsync(h.Db.DataSource), link, Ct);
        await Harness.UntilAsync(() => h.Budget.EarliestFree(h.Now) == Harness.T0.AddSeconds(12));
        while (!add.IsCompleted || h.Now < Harness.T0.AddSeconds(13))
        {
            await h.RunAsync();
            h.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
        Assert.Null((await add).Error);
        Assert.Equal(1, h.Http.Count(new Uri(link).AbsoluteUri));
        Assert.Equal([Harness.T0, Harness.T0.AddSeconds(6), Harness.T0.AddSeconds(12)], Times(h, Harness.Mt));
    }

    [Fact]
    public async Task DomainBudgetOverride()
    {
        (string, string?)[] settings = [("InTheKnow:MaxRequestsPerHostPerMinute", "10"), ("InTheKnow:MyTimetables:Domains:0:MaxRequestsPerMinute", "60")];
        await using var h = await Harness.CreateAsync(settings);
        var mt = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            mt.Add((await h.SeedAsync("mt-nl.ics")).Id);
            await h.SeedAsync("mt-nl.ics", "other.example");
        }
        await RunForAsync(h, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        Assert.All(Gaps(Times(h, Harness.Mt)), g => Assert.Equal(TimeSpan.FromSeconds(1), g));
        Assert.All(Gaps(Times(h, "other.example")), g => Assert.Equal(TimeSpan.FromSeconds(6), g));
        Assert.Equal(5, Times(h, "other.example").Count);

        // Restart with the override raised: the new spacing applies, nothing is skipped or re-baselined.
        h.Restart([settings[0], ("InTheKnow:MyTimetables:Domains:0:MaxRequestsPerMinute", "120")]);
        h.Time.Advance(TimeSpan.FromMinutes(10));
        var before = Times(h, Harness.Mt).Count;
        await RunForAsync(h, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500));
        var after = Times(h, Harness.Mt).Skip(before).ToList();
        Assert.Equal(5, after.Count);
        Assert.All(Gaps(after), g => Assert.Equal(TimeSpan.FromMilliseconds(500), g));
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM detected_change"));
    }

    // US3-6, FR-014a. Distinct hosts, so the budget never limits the claims.
    [Fact]
    public async Task AtMostMaxConcurrentChecksRunAtOnce()
    {
        await using var h = await Harness.CreateAsync(("InTheKnow:MaxConcurrentChecks", "3"));
        for (var i = 0; i < 10; i++) await h.SeedAsync("mt-nl.ics", $"h{i}.example");
        h.Http.Gate = new SemaphoreSlim(0);
        var run = h.Checker.RunDueChecksAsync(h.Now, Ct);

        await Harness.UntilAsync(() => h.Http.InFlight == 3);
        await Task.Delay(100, Ct);
        Assert.Equal((3, 3), (h.Http.InFlight, h.Http.MaxInFlight));

        h.Http.Gate.Release();
        await Harness.UntilAsync(() => h.Http.PerUrl.Count == 4);
        await Harness.UntilAsync(() => h.Http.InFlight == 3);
        await Task.Delay(100, Ct);
        Assert.Equal((4, 3), (h.Http.PerUrl.Count, h.Http.MaxInFlight));

        h.Http.Gate.Release(100);
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(10, await h.Db.ScalarAsync("SELECT count(*) FROM calendar WHERE last_ok"));
        Assert.Equal(3, h.Http.MaxInFlight);
    }

    const string Rooster = "rooster.example.nl";
    static readonly (string, string?)[] TwoDomains = [("InTheKnow:MyTimetables:Domains:1:Host", Rooster), ("InTheKnow:MyTimetables:Domains:1:TeacherDetection", "false")];
    const string Lesson = "Xedule!GI3TINRWGUZQ@schedule.inholland.nl";

    static async Task<string[]> FieldKeysAsync(Harness h, long id) =>
        [.. (await h.Db.RowsAsync("SELECT jsonb_object_keys(fields) FROM event_snapshot WHERE calendar_id = $1 AND event_key = $2 ORDER BY 1", id, Lesson))
            .Select(r => (string)r[0]!)];

    // SC-009, US4-2, US4-3
    [Fact]
    public async Task FeaturesApplyPerDomain()
    {
        await using var h = await Harness.CreateAsync(TwoDomains);
        var plain = (await h.SeedAsync("mt-nl.ics", "plain.example")).Id;
        var full = (await h.SeedAsync("mt-nl.ics")).Id;
        var partial = (await h.SeedAsync("mt-nl.ics", Rooster)).Id;
        await h.RunAsync();
        Assert.Equal(["description", "location"], await FieldKeysAsync(h, plain));
        Assert.Equal(["groups", "rooms", "teachers", "type"], await FieldKeysAsync(h, full));
        Assert.Equal(["details", "groups", "rooms", "type"], await FieldKeysAsync(h, partial));
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM event_snapshot WHERE calendar_id = $1 AND event_key = $2 AND fields->'details' ? 'teachers'", partial, Lesson));
        Assert.Equal(["generic", "mt:111111", "mt:111011"], (await h.Db.RowsAsync("SELECT baseline_features FROM calendar ORDER BY id")).Select(r => (string)r[0]!));
    }

    // US4-5, FR-026a
    [Fact]
    public async Task FeatureSwitchRebaselinesWithoutChanges()
    {
        var (h, id, _) = await BaselineAsync();
        await using var _h = h;
        h.Restart(("InTheKnow:MyTimetables:Domains:0:EventDetails", "false"));
        await NextIntervalAsync(h);
        Assert.Empty(await h.ChangesAsync(id));
        Assert.Equal("mt:111001", (await h.CalendarAsync(id))[6]);
        Assert.Equal(["description", "location"], await FieldKeysAsync(h, id));
    }

    [Fact]
    public async Task RevokedLinkDuringAFeatureSwitchKeepsTheSnapshot()
    {
        var (h, id, url) = await BaselineAsync();
        await using var _h = h;
        var xmin = await h.XminAsync(id);
        h.Restart(("InTheKnow:MyTimetables:Domains:0:EventDetails", "false"));
        h.Http.Map[url] = "mt-empty.ics";
        await NextIntervalAsync(h);
        Assert.Equal(xmin, await h.XminAsync(id));
        var c = await h.CalendarAsync(id);
        Assert.Equal(("empty_feed", "mt:111111"), ((string)c[2]!, (string)c[6]!));
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM calendar_link WHERE flagged_at IS NOT NULL"));
    }

    // Research R13, US4-4
    [Fact]
    public async Task RegroupSplitsAndMerges()
    {
        await using var h = await Harness.CreateAsync();
        var a = await SubscriptionsTests.AddAsync(h, h.S1, LinkIdentityTests.MtLink(user: "1001"));
        var b = await SubscriptionsTests.AddAsync(h, h.S2, LinkIdentityTests.MtLink(user: "2002"));
        Assert.Equal(a.CalendarId, b.CalendarId);
        var original = a.CalendarId!.Value;

        // Split
        h.Restart(("InTheKnow:MyTimetables:Domains:0:ClassGroupSharing", "false"));
        await h.Checker.RegroupAsync(h.Now, Ct);
        var calendars = await h.Db.RowsAsync("SELECT c.id, c.baseline_features, count(l.id) FROM calendar c JOIN calendar_link l ON l.calendar_id = c.id GROUP BY c.id ORDER BY c.id");
        Assert.Equal(2, calendars.Count);
        Assert.All(calendars, c => Assert.Equal(1L, c[2]));
        Assert.Equal(original, calendars[0][0]);
        Assert.Equal(26, await h.Db.ScalarAsync("SELECT count(*) FROM event_snapshot WHERE calendar_id = $1", original));
        var split = (long)calendars[1][0]!;
        Assert.Null(calendars[1][1]);
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM calendar_link WHERE calendar_id = $1 AND id = $2", split, b.LinkId!.Value));
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await h.RunAsync();
        Assert.Equal(26, await h.Db.ScalarAsync("SELECT count(*) FROM event_snapshot WHERE calendar_id = $1", split));
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM detected_change"));

        // Merge back
        h.Restart();
        await h.Checker.RegroupAsync(h.Now, Ct);
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM calendar"));
        Assert.Equal(2, await h.Db.ScalarAsync("SELECT count(*) FROM calendar_link"));
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM calendar WHERE id = $1", split));
    }

    // 002 T023: a subscription filter follows its link through a regroup.
    [Fact]
    public async Task RegroupKeepsSubscriptionFilters()
    {
        await using var h = await Harness.CreateAsync();
        var presets = new Presets(h.Db.DataSource, h.Settings);
        var a = await SubscriptionsTests.AddAsync(h, h.S1, LinkIdentityTests.MtLink(user: "1001"));
        var b = await SubscriptionsTests.AddAsync(h, h.S2, LinkIdentityTests.MtLink(user: "2002"));
        await SubscriptionsTests.AddAsync(h, h.S2, LinkIdentityTests.MtLink(user: "1001", hex: "03")); // stays with s1's link on a split
        var c = await SubscriptionsTests.AddAsync(h, h.S3, LinkIdentityTests.MtLink(user: "3003"));
        var original = a.CalendarId!.Value;
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM calendar"));

        var personal = (await presets.CreateAsync(h.S2, "Mine", FilterEngineTests.R(PresetsTests.HideZelf), Ct)).Preset!.Id;
        Assert.True(await presets.ReplaceFilterAsync(h.S1, original, FilterEngineTests.R(PresetsTests.HideZelf), Ct));
        Assert.True(await presets.ApplyAsync(h.S2, original, personal, Ct));
        Assert.True(await presets.ReplaceFilterAsync(h.S3, original, FilterEngineTests.R("[]"), Ct));

        // Split: s2 keeps its filter on both calendars; s3 has no link left on the original, so its row moves.
        h.Restart(("InTheKnow:MyTimetables:Domains:0:ClassGroupSharing", "false"));
        await h.Checker.RegroupAsync(h.Now, Ct);
        async Task<long> CalendarOf(long linkId) => await h.Db.ScalarAsync("SELECT calendar_id FROM calendar_link WHERE id = $1", linkId);
        var (split2, split3) = (await CalendarOf(b.LinkId!.Value), await CalendarOf(c.LinkId!.Value));
        Assert.Equal(original, await CalendarOf(a.LinkId!.Value));
        Assert.Equal(3, new[] { original, split2, split3 }.Distinct().Count());
        Assert.Equal(new List<(long, long, long?, long?)>
            {
                (original, h.S1, null, 1), (original, h.S2, personal, null), (split2, h.S2, personal, null), (split3, h.S3, null, 0),
            }.Order(), (await PresetsTests.FiltersAsync(h.Db)).Order());

        // Merge back: the target's own row wins over the moved link's.
        Assert.True(await presets.ReplaceFilterAsync(h.S2, split2, FilterEngineTests.R("[]"), Ct));
        h.Restart();
        await h.Checker.RegroupAsync(h.Now, Ct);
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM calendar"));
        Assert.Equal([(original, h.S1, null, 1L), (original, h.S2, personal, null), (original, h.S3, null, 0L)], await PresetsTests.FiltersAsync(h.Db));
    }

    [Fact]
    public async Task RegroupUnderAnUnlistedHostUsesNormalisedUrls()
    {
        await using var h = await Harness.CreateAsync();
        string[] urls = [LinkIdentityTests.MtLink(user: "1001"), LinkIdentityTests.MtLink(user: "2002")];
        foreach (var u in urls) await SubscriptionsTests.AddAsync(h, await TestHost.AccountAsync(h.Db.DataSource), u);
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM calendar"));

        h.Restart(("InTheKnow:MyTimetables:Domains:0:Host", "elsewhere.example"));
        await h.Checker.RegroupAsync(h.Now, Ct);
        var hashes = (await h.Db.RowsAsync("SELECT identity_hash FROM calendar ORDER BY id")).Select(r => Convert.ToHexString((byte[])r[0]!));
        Assert.Equal(urls.Select(u => Convert.ToHexString(LinkIdentity.Hash(LinkIdentity.Normalise(new Uri(u))))).Order(), hashes.Order());
    }

    // FR-028, SC-011: no outcome ever logs a link or its token.
    [Fact]
    public async Task NoLinkIsEverLogged()
    {
        var logs = new CapturingLogs();
        await using var h = await Harness.CreateAsync(logs);
        var outcomes = new[] { "success", "http500", "hang", "empty", "duplicates" };
        var urls = outcomes.Select((o, i) => LinkIdentityTests.MtLink(host: $"{o}.example", token: $"secret-token-{i}")).ToList();
        for (var i = 0; i < urls.Count; i++)
        {
            h.Http.Map[new Uri(urls[i]).AbsoluteUri] = i == 4 ? "generic.ics" : "mt-nl.ics";
            await h.Db.SeedCalendarAsync(i == 3 ? Harness.Mt : $"{outcomes[i]}.example", [new Uri(urls[i]).AbsoluteUri], h.S1);
        }
        await h.RunAsync();
        h.Http.Map[new Uri(urls[1]).AbsoluteUri] = System.Net.HttpStatusCode.InternalServerError;
        h.Http.Map[new Uri(urls[2]).AbsoluteUri] = Reply.Hang;
        h.Http.Map[new Uri(urls[3]).AbsoluteUri] = "mt-empty.ics";
        h.Time.Advance(TimeSpan.FromMinutes(10));
        var run = h.Checker.RunDueChecksAsync(h.Now, Ct);
        await Harness.UntilAsync(() => h.Http.Count(new Uri(urls[2]).AbsoluteUri) == 2);
        // The other four must have finished this pass (not merely be unclaimed) before fake time jumps 2 minutes.
        await Harness.UntilAsync(async () => await h.Db.ScalarAsync("SELECT count(*) FROM calendar WHERE lease_id IS NULL AND last_checked_at = $1", h.Now) == 4);
        h.Time.Advance(TimeSpan.FromMinutes(2));
        await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(["empty_feed", "http_500", "timeout"],
            (await h.Db.RowsAsync("SELECT last_error FROM calendar WHERE last_error IS NOT NULL ORDER BY 1")).Select(r => (string)r[0]!));
        Assert.NotEmpty(logs.Lines);
        Assert.DoesNotContain(logs.Lines, l => l.Contains("secret-token") || l.Contains("/ical") || l.Contains("https://"));
    }

    // 003 research R1: fan-out at record time. Channels are inserted directly as active (FR-011).
    internal static Task<long> ChannelAsync(TestDb db, long subscriber, string status = "active", DateTimeOffset? at = null, string? target = null,
                                            string type = "email") =>
        db.ScalarAsync("""
            INSERT INTO channel (subscriber, type, target, status, status_since, failure_reason, activated_at, unsubscribe_token, created_at)
            VALUES ($1, $6, $2, $3, $4, CASE WHEN $3 = 'failing' THEN 'smtp_550' END, CASE WHEN $3 = 'active' THEN $4 END, $5, $4)
            RETURNING id
            """, subscriber, target ?? $"{Guid.NewGuid():N}@example.nl", status, at ?? Harness.T0, Channels.NewToken(), type);

    [Fact]
    public async Task ChecksFanOutPendingDeliveries()
    {
        var (h, id, url) = await BaselineAsync();
        await using var _h = h;
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM delivery")); // a baseline creates none
        await h.Db.ExecAsync("INSERT INTO calendar_link (calendar_id, subscriber, url) VALUES ($1, $3, $2)", id, url + "&s2", h.S2);
        await h.Db.ExecAsync("INSERT INTO calendar_link (calendar_id, subscriber, url) VALUES ($1, $3, $2)", id, url + "&s3", h.S3);
        var active = await ChannelAsync(h.Db, h.S1);
        var second = await ChannelAsync(h.Db, h.S1);
        var s2 = await ChannelAsync(h.Db, h.S2);
        foreach (var status in new[] { "paused", "awaitingConfirmation" }) await ChannelAsync(h.Db, h.S1, status);
        foreach (var status in new[] { "unsubscribed", "failing" }) await ChannelAsync(h.Db, h.S2, status);
        await ChannelAsync(h.Db, h.S3);
        await ChannelAsync(h.Db, await TestHost.AccountAsync(h.Db.DataSource));
        await h.Db.ExecAsync("INSERT INTO subscription_setting (calendar_id, subscriber, notify) VALUES ($1, $2, false)", id, h.S3);
        await h.Db.ExecAsync("INSERT INTO subscription_setting (calendar_id, subscriber, label) VALUES ($1, $2, 'Mine')", id, h.S2);

        h.Http.Map[url] = "mt-nl-edited.ics";
        await NextIntervalAsync(h);
        var changes = await h.Db.ScalarAsync("SELECT count(*) FROM detected_change");
        Assert.Equal(KnownEdits.Length, changes);
        var rows = await h.Db.RowsAsync("SELECT channel_id, count(*), bool_and(status = 'pending') FROM delivery GROUP BY channel_id ORDER BY channel_id");
        Assert.Equal([[active, changes, true], [second, changes, true], [s2, changes, true]], rows);
    }

    // 004 FR-033c, research R12: a disabled account's channels get no deliveries; an enabled one on the same calendar does.
    [Fact]
    public async Task FanOutSkipsDisabledAccounts()
    {
        var (h, id, url) = await BaselineAsync();
        await using var _h = h;
        await h.Db.ExecAsync("INSERT INTO calendar_link (calendar_id, subscriber, url) VALUES ($1, $3, $2)", id, url + "&s2", h.S2);
        var enabled = await ChannelAsync(h.Db, h.S1);
        await ChannelAsync(h.Db, h.S2);
        await h.Db.ExecAsync("UPDATE account SET disabled_at = now() WHERE id = $1", h.S2);
        h.Http.Map[url] = "mt-nl-edited.ics";
        await NextIntervalAsync(h);
        Assert.Equal([[enabled]], await h.Db.RowsAsync("SELECT DISTINCT channel_id FROM delivery"));
    }

    [Fact]
    public async Task FanOutUsesTheIdWatermark()
    {
        var (h, id, url) = await BaselineAsync();
        await using var _h = h;
        var early = await ChannelAsync(h.Db, h.S1);
        h.Http.Map[url] = "mt-nl-edited.ics";
        h.Time.Advance(h.Settings.Options.CheckInterval);
        await h.RunAsync();
        var first = await h.Db.ScalarAsync("SELECT count(*) FROM detected_change");

        // A second check at the same fake instant: the channel activated in between gets only its changes.
        var late = await ChannelAsync(h.Db, h.S1);
        await h.Db.ExecAsync("UPDATE calendar SET next_check_at = $1 WHERE id = $2", h.Now, id);
        h.Restart(); // a fresh host budget, so the host is not busy at this instant
        h.Http.Map[url] = "mt-nl.ics";
        await h.RunAsync();
        var second = await h.Db.ScalarAsync("SELECT count(*) FROM detected_change") - first;
        Assert.True(second > 0);
        Assert.Equal(first + second, await h.Db.ScalarAsync("SELECT count(*) FROM delivery WHERE channel_id = $1", early));
        Assert.Equal(second, await h.Db.ScalarAsync("SELECT count(*) FROM delivery WHERE channel_id = $1", late));
        Assert.Equal(0, await h.Db.ScalarAsync("""
            SELECT count(*) FROM delivery d JOIN detected_change c ON c.id = d.change_id
            WHERE d.channel_id = $1 AND c.id <= (SELECT max(id) - $2 FROM detected_change)
            """, late, second));
    }

    [Fact]
    public async Task FeedNameIsStoredOnEverySuccessfulCheck()
    {
        var (h, id, url) = await BaselineAsync();
        await using var _h = h;
        async Task<string?> FeedName() => (string?)(await h.Db.RowsAsync("SELECT feed_name FROM calendar WHERE id = $1", id)).Single()[0];
        Assert.Equal("Hogeschool Inholland - Rooster - ALET26VT4A", await FeedName());

        // Only the name changes: no change is recorded, but the name is updated, cleaned and cut to 100 characters.
        var renamed = Path.Combine(Path.GetTempPath(), $"itk-{Guid.NewGuid():N}.ics");
        var ics = await File.ReadAllTextAsync(FakeHttp.FixturePath("mt-nl.ics"), Ct);
        await File.WriteAllTextAsync(renamed, ics.Replace("X-WR-CALNAME:Hogeschool Inholland - Rooster - ALET26VT4A",
            "X-WR-CALNAME:  Renamed\t" + new string('x', 120)), Ct);
        try
        {
            h.Http.Map[url] = renamed;
            await NextIntervalAsync(h);
            Assert.Empty(await h.ChangesAsync(id));
            Assert.Equal("Renamed" + new string('x', 93), await FeedName());
        }
        finally { File.Delete(renamed); }

        await ChannelAsync(h.Db, h.S1);
        var (generic, _) = await h.SeedAsync("generic.ics", "calendar.example.com");
        await h.RunAsync();
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM delivery")); // a baseline records nothing, so fans out nothing
        Assert.Null((await h.Db.RowsAsync("SELECT feed_name FROM calendar WHERE id = $1", generic)).Single()[0]);
    }

    // 003 T031: notification settings follow a link through a regroup, like filters.
    [Fact]
    public async Task RegroupKeepsSubscriptionSettings()
    {
        await using var h = await Harness.CreateAsync();
        var a = await SubscriptionsTests.AddAsync(h, h.S1, LinkIdentityTests.MtLink(user: "1001"));
        var b = await SubscriptionsTests.AddAsync(h, h.S2, LinkIdentityTests.MtLink(user: "2002"));
        await SubscriptionsTests.AddAsync(h, h.S2, LinkIdentityTests.MtLink(user: "1001", hex: "03")); // stays with s1's link on a split
        var original = a.CalendarId!.Value;
        await h.Db.ExecAsync("INSERT INTO subscription_setting (calendar_id, subscriber, notify, label) VALUES ($1, $2, true, 'One'), ($1, $3, false, 'Two')", original, h.S1, h.S2);
        Task<List<object?[]>> Rows() => h.Db.RowsAsync("SELECT calendar_id, subscriber, notify, label FROM subscription_setting ORDER BY subscriber, calendar_id");

        h.Restart(("InTheKnow:MyTimetables:Domains:0:ClassGroupSharing", "false"));
        await h.Checker.RegroupAsync(h.Now, Ct);
        var split = await h.Db.ScalarAsync("SELECT calendar_id FROM calendar_link WHERE id = $1", b.LinkId!.Value);
        Assert.NotEqual(original, split);
        Assert.Equal([[original, h.S1, true, "One"], [original, h.S2, false, "Two"], [split, h.S2, false, "Two"]], await Rows());

        // Merge back: the target's own row wins over the moved link's.
        await h.Db.ExecAsync("UPDATE subscription_setting SET notify = true, label = 'Other' WHERE calendar_id = $1", split);
        h.Restart();
        await h.Checker.RegroupAsync(h.Now, Ct);
        Assert.Equal([[original, h.S1, true, "One"], [original, h.S2, false, "Two"]], await Rows());
    }
}
