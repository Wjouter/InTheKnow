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

    public async Task<(long Id, string Url)> SeedAsync(string fixture, string host = Mt, DateTimeOffset? nextCheckAt = null, string subscriber = "s1")
    {
        var url = $"https://{host}/ical?{Guid.NewGuid():N}";
        Http.Map[url] = fixture;
        var (id, _) = await Db.SeedCalendarAsync(host, [url], subscriber, nextCheckAt);
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
        var (id, links) = await h.Db.SeedCalendarAsync(Harness.Mt, urls);
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
        var (id, _) = await h.Db.SeedCalendarAsync("x.example", ["https://x.example/a"]);
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
}
