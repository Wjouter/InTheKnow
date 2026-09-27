using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static InTheKnow.Tests.LinkIdentityTests;

namespace InTheKnow.Tests;

// Stores every formatted log line, so tests can check that no link is ever logged.
sealed class CapturingLogs : ILoggerProvider, ILoggerFactory
{
    public readonly ConcurrentQueue<string> Lines = new();
    public ILogger CreateLogger(string category) => new Logger(this, category);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    sealed class Logger(CapturingLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e, Func<TState, Exception?, string> format) =>
            owner.Lines.Enqueue($"{category}: {format(state, e)} {e}");
    }
}

// The real app in memory against a per-test database, with fake HTTP and a fake clock, and no background checker.
sealed class ApiHost : IAsyncDisposable
{
    public required TestDb Db { get; init; }
    public required FakeTimeProvider Time { get; init; }
    public required FakeHttp Http { get; init; }
    public required CapturingLogs Logs { get; init; }
    public required WebApplicationFactory<Program> Factory { get; init; }
    public required HttpClient Client { get; init; }
    public Checker Checker => Factory.Services.GetRequiredService<Checker>();

    public static Task<ApiHost> CreateAsync(params (string Key, string Value)[] settings) => CreateAsync(null, settings);

    // `services` runs after the fakes are in place, so a test can add or replace registrations (003 T002).
    public static async Task<ApiHost> CreateAsync(Action<IServiceCollection>? services, params (string Key, string Value)[] settings)
    {
        var db = await TestDb.CreateAsync();
        var time = new FakeTimeProvider(Harness.T0);
        var http = new FakeHttp(time);
        var logs = new CapturingLogs();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Default", db.ConnectionString);
            foreach (var (k, v) in settings) b.UseSetting(k, v);
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<Fetcher>();
                s.AddSingleton(new Fetcher(http, FakeHttp.Resolve));
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(time);
                s.RemoveAll<IHostedService>();
                s.AddSingleton<ILoggerProvider>(logs);
                s.Configure<PasswordHasherOptions>(o => o.IterationCount = 1);
                // First in the pipeline, so every branch sees it: TestServer sets no peer address (004 T021).
                s.AddSingleton<IStartupFilter, TestSource>();
                services?.Invoke(s);
            });
        });
        var client = factory.CreateClient(); // anonymous; signed-in clients come from TestHost.SignInAsync
        return new ApiHost { Db = db, Time = time, Http = http, Logs = logs, Factory = factory, Client = client };
    }

    // Moves the fake clock a second on first, so the validation never waits for a budget slot.
    public Task<HttpResponseMessage> AddAsync(SignedIn who, string url, string fixture = "mt-nl.ics")
    {
        Http.Map.TryAdd(new Uri(url).AbsoluteUri, fixture);
        Time.Advance(TimeSpan.FromSeconds(1));
        return who.Client.PostAsJsonAsync("/api/me/links", new { url }, TestContext.Current.CancellationToken);
    }

    public async Task<JsonNode> JsonAsync(string path)
    {
        var response = await Client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
    }
}

// The test-only header X-Test-Source sets the peer address, which is how a test chooses its throttling source.
sealed class TestSource : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((http, n) =>
        {
            if (http.Request.Headers["X-Test-Source"].ToString() is { Length: > 0 } source) http.Connection.RemoteIpAddress = IPAddress.Parse(source);
            return n(http);
        });
        next(app);
    };
}

public class ApiTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<string> ReasonAsync(HttpResponseMessage r) =>
        JsonNode.Parse(await r.Content.ReadAsStringAsync(Ct))!["reason"]!.GetValue<string>();

    // 004 FR-033, contracts/api.md § Authentication: every /api route outside the public list needs a session, decided
    // before the body or route values are bound. The operator key opens nothing.
    [Fact]
    public async Task EveryApiRouteNeedsASession()
    {
        await using var h = await ApiHost.CreateAsync();
        var gone = await h.SignInAsync();
        await h.Db.ExecAsync("DELETE FROM session WHERE id = $1", gone.SessionId);
        var routes = h.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api", StringComparison.Ordinal) && !PublicRoutes.Contains(e.RoutePattern.RawText))
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => (Method: m, Path: Fill(e.RoutePattern.RawText!))))
            .ToList();
        Assert.Contains(routes, r => r.Path == "/api/me/links");
        Assert.Contains(routes, r => r.Path == "/api/admin/calendars");
        using var bare = h.Factory.CreateClient();
        foreach (var auth in new AuthenticationHeaderValue?[]
        {
            null, new("Bearer", SignIn.NewToken().Token), new("Bearer", TestDb.ApiKey), new("Bearer", gone.Token),
        })
            foreach (var (method, path) in routes)
            {
                var request = new HttpRequestMessage(new HttpMethod(method), path)
                { Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json") };
                request.Headers.Authorization = auth;
                var response = await bare.SendAsync(request, Ct);
                Assert.Equal((HttpStatusCode.Unauthorized, "not_signed_in"), (response.StatusCode, await ReasonAsync(response)));
            }
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM calendar"));
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM preset WHERE name = 'Default'"));
    }

    // contracts/api.md § Public routes (and the test-only method, mapped outside /api by its own branch).
    internal static readonly HashSet<string> PublicRoutes =
    [
        "/api/auth/bot-check", "/api/auth/sign-up", "/api/auth/confirmation", "/api/auth/confirm", "/api/auth/sign-in",
        "/api/auth/password-reset", "/api/auth/password-reset/complete", "/api/auth/email-change/confirm", "/api/auth/invite/accept",
    ];

    // A route pattern with its parameters filled with 1.
    internal static string Fill(string pattern) => System.Text.RegularExpressions.Regex.Replace(pattern, "{[^}]+}", "1");

    [Fact]
    public async Task AddListChangesAndRemove()
    {
        await using var h = await ApiHost.CreateAsync();
        var (s1, s2, nobody, op) = (await h.SignInAsync(), await h.SignInAsync(), await h.SignInAsync(), await h.SignInAsync(admin: true, secondFactor: true));
        var url = MtLink(hex: "01");

        // Add (US5-1)
        var created = await h.AddAsync(s1, url);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = JsonNode.Parse(await created.Content.ReadAsStringAsync(Ct))!;
        var calendarId = body["calendarId"]!.GetValue<long>();
        Assert.True(body["calendarIsNew"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await h.AddAsync(s1, url)).StatusCode);
        var variant = await h.AddAsync(s1, MtLink(hex: "02"));
        Assert.Equal(HttpStatusCode.Created, variant.StatusCode);
        Assert.False(JsonNode.Parse(await variant.Content.ReadAsStringAsync(Ct))!["calendarIsNew"]!.GetValue<bool>());

        // List (baseline from validation)
        var calendars = (await s1.JsonAsync("/api/me/calendars")).AsArray();
        var calendar = Assert.Single(calendars)!;
        Assert.Equal(calendarId, calendar["calendarId"]!.GetValue<long>());
        Assert.True(calendar["lastOk"]!.GetValue<bool>());
        Assert.Equal(["linkId", "flagged", "createdAt"], calendar["links"]![0]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(2, calendar["links"]!.AsArray().Count);
        Assert.Empty((await nobody.JsonAsync("/api/me/calendars")).AsArray());

        // Changes (US5-3): only this subscriber's, in id order, paged. includeHidden: every recorded change, as in 001.
        await h.AddAsync(s2, MtLink(id: "ALET26VT4A"));
        h.Http.Map[new Uri(url).AbsoluteUri] = "mt-nl-edited.ics";
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await h.Checker.RunDueChecksAsync(h.Time.GetUtcNow(), Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var all = (await s1.JsonAsync("/api/me/changes?includeHidden=true"))["items"]!.AsArray();
        Assert.Equal(8, all.Count);
        Assert.Equal(all.Select(i => i!["id"]!.GetValue<long>()).Order(), all.Select(i => i!["id"]!.GetValue<long>()));
        Assert.All(all, i => Assert.Equal(calendarId, i!["calendarId"]!.GetValue<long>()));
        var rooms = all.Single(i => i!["field"]?.GetValue<string>() == "rooms")!;
        Assert.Equal("[\"AL00.A1.20\"]", rooms["newValue"]!.ToJsonString());
        Assert.Empty((await s2.JsonAsync("/api/me/changes?includeHidden=true"))["items"]!.AsArray());

        var page = await s1.JsonAsync("/api/me/changes?includeHidden=true&limit=2");
        Assert.Equal(2, page["items"]!.AsArray().Count);
        var next = page["next"]!.GetValue<long>();
        var page2 = await s1.JsonAsync($"/api/me/changes?includeHidden=true&limit=2&after={next}");
        Assert.Equal(all[2]!["id"]!.GetValue<long>(), page2["items"]![0]!["id"]!.GetValue<long>());
        var last = await s1.JsonAsync($"/api/me/changes?includeHidden=true&after={all[6]!["id"]}&limit=2");
        Assert.Null(last["next"]);
        foreach (var bad in new[] { 0, 501 })
        {
            var r = await s1.Client.GetAsync($"/api/me/changes?limit={bad}", Ct);
            Assert.Equal((HttpStatusCode.BadRequest, "invalid_limit"), (r.StatusCode, await ReasonAsync(r)));
        }
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM delivery")); // the changes route never creates a delivery

        // Remove (US5-4)
        var removed = await s1.Client.DeleteAsync($"/api/me/calendars/{calendarId}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        foreach (var table in new[] { "calendar_link", "event_snapshot", "detected_change" })
            Assert.Equal(0, await h.Db.ScalarAsync($"SELECT count(*) FROM {table} WHERE calendar_id = $1", calendarId));
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM calendar WHERE id = $1", calendarId));
        Assert.Equal(HttpStatusCode.NotFound, (await s1.Client.DeleteAsync($"/api/me/calendars/{calendarId}", Ct)).StatusCode);

        // No link anywhere (FR-032, SC-011).
        var token = new Uri(url).Query.Split('&').Single(p => p.StartsWith("h=", StringComparison.Ordinal))[2..];
        Assert.DoesNotContain(h.Logs.Lines, l => l.Contains(token) || l.Contains("/ical?"));
        Assert.DoesNotContain(token, (await op.JsonAsync("/api/admin/calendars")).ToJsonString());
    }

    [Fact]
    public async Task ErrorMapping()
    {
        await using var h = await ApiHost.CreateAsync();
        async Task Expect(HttpResponseMessage r, HttpStatusCode status, string reason)
        {
            Assert.Equal(status, r.StatusCode);
            Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
            Assert.Equal(reason, await ReasonAsync(r));
        }
        var s1 = await h.SignInAsync();
        await Expect(await h.AddAsync(s1, "ftp://example.com/a.ics"), HttpStatusCode.BadRequest, "unsupported_scheme");
        await Expect(await h.AddAsync(s1, "http://example.com/a.ics"), HttpStatusCode.BadRequest, "unsupported_scheme");
        await Expect(await h.AddAsync(s1, MtLink(), "mt-empty.ics"), HttpStatusCode.UnprocessableEntity, "validation_failed:empty_feed");

        var budget = h.Factory.Services.GetRequiredService<HostBudget>();
        for (var i = 0; i < 12; i++) budget.TryReserve(Harness.Mt, h.Time.GetUtcNow() + TimeSpan.FromSeconds(1), TimeSpan.MaxValue);
        var busy = await h.AddAsync(s1, MtLink(id: "OTHER"));
        await Expect(busy, HttpStatusCode.TooManyRequests, "host_budget_exhausted");
        Assert.Equal("5", busy.Headers.RetryAfter!.ToString());
    }

    [Fact]
    public async Task OperatorOverview()
    {
        await using var h = await ApiHost.CreateAsync();
        var op = await h.SignInAsync(admin: true, secondFactor: true);
        var (s1, s2, s3) = (await TestHost.AccountAsync(h.Db.DataSource), await TestHost.AccountAsync(h.Db.DataSource), await TestHost.AccountAsync(h.Db.DataSource));
        var ok1 = (await h.Db.SeedCalendarAsync("a.example", ["https://a.example/1", "https://a.example/2"], s1)).CalendarId;
        await h.Db.ExecAsync("INSERT INTO calendar_link (calendar_id, subscriber, url, flagged_at) VALUES ($1, $2, 'https://a.example/3', now())", ok1, s2);
        var failing = (await h.Db.SeedCalendarAsync("b.example", ["https://b.example/1"], s1, h.Time.GetUtcNow().AddHours(1))).CalendarId;
        await h.Db.ExecAsync("UPDATE calendar SET status = 'failing' WHERE id = $1", failing);
        var ok2 = (await h.Db.SeedCalendarAsync("c.example", ["https://c.example/1"], s3, h.Time.GetUtcNow().AddSeconds(-30))).CalendarId;

        var f = Assert.Single((await op.JsonAsync("/api/admin/calendars?status=failing"))["items"]!.AsArray())!;
        Assert.Equal((failing, 1L, 1L, 1L), (f["calendarId"]!.GetValue<long>(), f["subscribers"]!.GetValue<long>(), f["validLinks"]!.GetValue<long>(), f["totalLinks"]!.GetValue<long>()));
        Assert.Null(f["links"]);

        var overdue = (await op.JsonAsync("/api/admin/calendars?overdue=true"))["items"]!.AsArray();
        var o = Assert.Single(overdue)!; // ok1 is due since the epoch; ok2 only 30 s late; failing is in the future
        Assert.Equal((ok1, 2L, 2L, 3L), (o["calendarId"]!.GetValue<long>(), o["subscribers"]!.GetValue<long>(), o["validLinks"]!.GetValue<long>(), o["totalLinks"]!.GetValue<long>()));

        var page = await op.JsonAsync("/api/admin/calendars?limit=2");
        Assert.Equal([ok1, failing], page["items"]!.AsArray().Select(i => i!["calendarId"]!.GetValue<long>()));
        var rest = await op.JsonAsync($"/api/admin/calendars?limit=2&after={page["next"]}");
        Assert.Equal([ok2], rest["items"]!.AsArray().Select(i => i!["calendarId"]!.GetValue<long>()));
        Assert.Null(rest["next"]);

        var badStatus = await op.Client.GetAsync("/api/admin/calendars?status=x", Ct);
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_status"), (badStatus.StatusCode, await ReasonAsync(badStatus)));
        var badLimit = await op.Client.GetAsync("/api/admin/calendars?limit=0", Ct);
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_limit"), (badLimit.StatusCode, await ReasonAsync(badLimit)));
        Assert.DoesNotContain("https://", (await op.JsonAsync("/api/admin/calendars")).ToJsonString());
    }

    // Quickstart §4: 5,000 calendars on one domain, a baseline pass and an unchanged pass, then API reads.
    // Logs timings rather than asserting them. Never runs in the normal gate.
    [Fact(Explicit = true)]
    public async Task Throughput5000()
    {
        await using var h = await ApiHost.CreateAsync(("InTheKnow:MyTimetables:Domains:0:MaxRequestsPerMinute", "60000"));
        var output = TestContext.Current.TestOutputHelper!;
        await h.Db.ExecAsync("""
            INSERT INTO calendar (identity_hash, host, next_check_at)
            SELECT sha256(g::text::bytea), 'mytimetables.inholland.nl', 'epoch' FROM generate_series(1, 5000) g
            """);
        var (s0, op) = (await h.SignInAsync(), await h.SignInAsync(admin: true, secondFactor: true));
        await h.Db.ExecAsync("""
            INSERT INTO account (email, language, confirmed_at, created_at) SELECT 'u' || g || '@example.nl', 'en', now(), now() FROM generate_series(1, 999) g
            """);
        await h.Db.ExecAsync("""
            INSERT INTO calendar_link (calendar_id, subscriber, url)
            SELECT id, CASE WHEN id <= 5 THEN $1 ELSE (SELECT min(id) - 1 FROM account WHERE email LIKE 'u%') + (id - 1) / 5 END,
                   'https://mytimetables.inholland.nl/ical?c=' || id
            FROM calendar
            """, s0.AccountId);
        foreach (var id in Enumerable.Range(1, 5000)) h.Http.Map[$"https://mytimetables.inholland.nl/ical?c={id}"] = "mt-nl.ics";

        async Task<TimeSpan> PassAsync()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (await h.Db.ScalarAsync("SELECT count(*) FROM calendar WHERE next_check_at <= $1", h.Time.GetUtcNow()) > 0)
            {
                await h.Checker.RunDueChecksAsync(h.Time.GetUtcNow(), Ct);
                h.Time.Advance(TimeSpan.FromMilliseconds(1));
            }
            return watch.Elapsed;
        }
        const string xmin = "SELECT md5(string_agg(xmin::text, ',' ORDER BY calendar_id, event_key)) FROM event_snapshot";
        output.WriteLine($"baseline pass: {await PassAsync()}");
        var before = (await h.Db.RowsAsync(xmin)).Single()[0];
        h.Time.Advance(TimeSpan.FromMinutes(10));
        output.WriteLine($"unchanged pass: {await PassAsync()}");
        Assert.Equal(before, (await h.Db.RowsAsync(xmin)).Single()[0]);

        await h.Db.ExecAsync("""
            INSERT INTO detected_change (calendar_id, event_key, kind, new_value, event_name, event_starts_at, detected_at)
            SELECT 1 + g % 5, 'k' || g, 'added', '{}', 'n', now(), now() FROM generate_series(1, 2000) g
            """);
        foreach (var (who, path) in new[] { (op, "/api/admin/calendars?limit=500"), (s0, "/api/me/changes?limit=500") })
        {
            await who.JsonAsync(path);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var json = await who.JsonAsync(path);
            output.WriteLine($"GET {path}: {watch.Elapsed.TotalMilliseconds:F0} ms, {json["items"]!.AsArray().Count} items");
        }
    }

    // --- 002: filters -------------------------------------------------------------------------------------------

    static JsonObject Ev(string name = "Lesson", int endDays = 1, string? status = "confirmed", string? type = null, string[]? groups = null) =>
        FilterEngineTests.Ev(name, endDays, status, type, groups);

    // Records a change directly, controlling the events: added/removed carry the event in new/old value.
    internal static async Task<long> RecordAsync(TestDb db, long calendarId, string kind, JsonObject? before = null, JsonObject? after = null,
                                                 string? field = null, JsonNode? oldValue = null, JsonNode? newValue = null)
    {
        var (o, n, b, a) = kind switch
        {
            "added" => (null, after, null, null),
            "removed" => (before, null, null, null),
            _ => (oldValue ?? "a", newValue ?? "b", before, after),
        };
        await using var cmd = db.DataSource.CreateCommand("""
            INSERT INTO detected_change (calendar_id, event_key, kind, field, old_value, new_value, event_name, event_starts_at, detected_at,
                                         event_before, event_after)
            VALUES ($1, 'k', $2, $3, $4, $5, $6, $7, $7, $8, $9) RETURNING id
            """);
        cmd.Parameters.Add(new() { Value = calendarId });
        cmd.Parameters.Add(new() { Value = kind });
        cmd.Parameters.Add(new() { Value = (object?)field ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        foreach (var j in new[] { o, n })
            cmd.Parameters.Add(new() { Value = (object?)j?.ToJsonString() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new() { Value = (after ?? before)?["name"]?.GetValue<string>() ?? "Lesson" });
        cmd.Parameters.Add(new() { Value = FilterEngineTests.Detected });
        foreach (var j in new[] { b, a })
            cmd.Parameters.Add(new() { Value = (object?)j?.ToJsonString() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb });
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    // Hidden by Default's eventEnded rule.
    static Task HiddenAsync(TestDb db, long calendarId, int count) => db.ExecAsync("""
        INSERT INTO detected_change (calendar_id, event_key, kind, new_value, event_name, event_starts_at, detected_at)
        SELECT $1, 'h' || g, 'added', '{"name":"Old","end":"2026-09-01T00:00:00Z","fields":{}}', 'Old', '2026-09-01', $2
        FROM generate_series(1, $3) g
        """, calendarId, FilterEngineTests.Detected, count);

    static List<(long Id, string Outcome)> Outcomes(JsonNode page) =>
        [.. page["items"]!.AsArray().Select(i => (i!["id"]!.GetValue<long>(), i["outcome"]!.GetValue<string>()))];

    static async Task<long> SeedAsync(ApiHost h, SignedIn who, string host = Harness.Mt) =>
        (await h.Db.SeedCalendarAsync(host, [$"https://{host}/ical?{Guid.NewGuid():N}"], who.AccountId)).CalendarId;

    async Task Expect(HttpResponseMessage r, HttpStatusCode status, string? reason = null)
    {
        Assert.Equal(status, r.StatusCode);
        if (reason is not null) Assert.Equal(reason, await ReasonAsync(r));
    }

    // US1, SC-001, SC-004: with no filter row, Default applies; hidden changes only with includeHidden.
    [Fact]
    public async Task ChangesCarryDefaultOutcomes()
    {
        await using var h = await ApiHost.CreateAsync();
        var s1 = await h.SignInAsync();
        var calendar = await SeedAsync(h, s1);
        var ended = await RecordAsync(h.Db, calendar, "changed", Ev(endDays: -1), Ev(endDays: -1), "rooms");
        var normal = await RecordAsync(h.Db, calendar, "changed", Ev(type: "lesson"), Ev(type: "lesson"), "rooms");
        var low = await RecordAsync(h.Db, calendar, "changed", Ev(status: "tentative"), Ev(), "status", "tentative", "confirmed");
        var holiday = await RecordAsync(h.Db, calendar, "changed", Ev(type: "holiday"), Ev(type: "holiday"), "rooms");

        Assert.Equal([(normal, "normal"), (low, "lowPriority")], Outcomes(await s1.JsonAsync("/api/me/changes")));
        Assert.Equal([(normal, "normal"), (low, "lowPriority")], Outcomes(await s1.JsonAsync("/api/me/changes?includeHidden=false")));
        Assert.Equal([(ended, "hidden"), (normal, "normal"), (low, "lowPriority"), (holiday, "hidden")],
            Outcomes(await s1.JsonAsync("/api/me/changes?includeHidden=true")));
        await Expect(await s1.Client.GetAsync("/api/me/changes?includeHidden=yes", Ct), HttpStatusCode.BadRequest, "invalid_include_hidden");
    }

    // Edge case "very large number of recorded changes": a page is never empty while visible changes remain.
    [Fact]
    public async Task PagingReadsPastHiddenChanges()
    {
        await using var h = await ApiHost.CreateAsync();
        var s1 = await h.SignInAsync();
        var calendar = await SeedAsync(h, s1);
        await HiddenAsync(h.Db, calendar, 1200);
        var first = await RecordAsync(h.Db, calendar, "added", after: Ev("First"));
        await HiddenAsync(h.Db, calendar, 700);
        var second = await RecordAsync(h.Db, calendar, "added", after: Ev("Second"));
        await HiddenAsync(h.Db, calendar, 600);

        var page = await s1.JsonAsync("/api/me/changes?limit=1");
        Assert.Equal([(first, "normal")], Outcomes(page));
        Assert.Equal(first, page["next"]!.GetValue<long>());
        page = await s1.JsonAsync($"/api/me/changes?limit=1&after={first}");
        Assert.Equal([(second, "normal")], Outcomes(page));
        Assert.Null(page["next"]);
        Assert.Empty(Outcomes(await s1.JsonAsync($"/api/me/changes?after={second}")));
        Assert.Equal(2502, await h.Db.ScalarAsync("SELECT count(*) FROM detected_change"));
        var all = await s1.JsonAsync("/api/me/changes?includeHidden=true&limit=500");
        Assert.Equal(500, Outcomes(all).Count);
        Assert.NotNull(all["next"]);
    }

    static Task<HttpResponseMessage> PutAsync(SignedIn who, string path, string json) =>
        who.Client.PutAsync(path, new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

    static Task<HttpResponseMessage> PostAsync(SignedIn who, string path, string json) =>
        who.Client.PostAsync(path, new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

    static async Task<JsonNode> BodyAsync(HttpResponseMessage r, HttpStatusCode status = HttpStatusCode.OK)
    {
        Assert.Equal(status, r.StatusCode);
        return JsonNode.Parse(await r.Content.ReadAsStringAsync(Ct))!;
    }

    // US2, SC-002, SC-003: one subscriber's filter edit changes only their list, including older changes.
    [Fact]
    public async Task ReplaceFilter()
    {
        await using var h = await ApiHost.CreateAsync();
        var (s1, s2, s3) = (await h.SignInAsync(), await h.SignInAsync(), await h.SignInAsync());
        var calendar = await SeedAsync(h, s1);
        await h.Db.ExecAsync("INSERT INTO calendar_link (calendar_id, subscriber, url) VALUES ($1, $2, 'https://mytimetables.inholland.nl/ical?s2')", calendar, s2.AccountId);
        var zelf = await RecordAsync(h.Db, calendar, "added", after: Ev("Zelfstudie"));
        var lesson = await RecordAsync(h.Db, calendar, "added", after: Ev("Lesson"));
        var ended = await RecordAsync(h.Db, calendar, "added", after: Ev("Old", endDays: -1));
        var filterPath = $"/api/me/calendars/{calendar}/filter";

        var initial = await s1.JsonAsync(filterPath);
        Assert.Equal(["source", "rules", "inactiveRules", "activeConditions", "values"], initial.AsObject().Select(kv => kv.Key));
        Assert.Equal("""{"type":"preset","presetId":1,"name":"Default","scope":"global"}""", initial["source"]!.ToJsonString());
        Assert.Equal(4, initial["rules"]!.AsArray().Count);

        var put = await BodyAsync(await PutAsync(s1, filterPath, $$"""{"rules":{{PresetsTests.HideZelf}}}"""));
        Assert.Equal("""{"type":"custom"}""", put["source"]!.ToJsonString());
        Assert.Equal(PresetsTests.HideZelf, put["rules"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(put, await s1.JsonAsync(filterPath)));
        Assert.Equal([(lesson, "normal"), (ended, "normal")], Outcomes(await s1.JsonAsync("/api/me/changes")));
        Assert.Equal([(zelf, "normal"), (lesson, "normal")], Outcomes(await s2.JsonAsync("/api/me/changes")));

        // An empty filter makes every change normal.
        await BodyAsync(await PutAsync(s1, filterPath, """{"rules":[]}"""));
        Assert.Equal([(zelf, "normal"), (lesson, "normal"), (ended, "normal")], Outcomes(await s1.JsonAsync("/api/me/changes")));

        // Rejected filters leave the stored one in place (SC-008).
        var before = await s1.JsonAsync(filterPath);
        foreach (var (body, reason) in new[]
        {
            ("{}", "invalid_rules"), ("""{"rules":{}}""", "invalid_rules"), ("[]", "invalid_rules"),
            ("""{"rules":[{"when":{"eventEnded":true}}]}""", "invalid_outcome"),
            ("""{"rules":[{"outcome":"hide","when":{}}]}""", "empty_conditions"),
            ("""{"rules":[{"outcome":"hide","when":{"changeKind":["moved"]}}]}""", "invalid_condition"),
        })
            await Expect(await PutAsync(s1, filterPath, body), HttpStatusCode.BadRequest, reason);
        Assert.True(JsonNode.DeepEquals(before, await s1.JsonAsync(filterPath)));

        await Expect(await s3.Client.GetAsync($"/api/me/calendars/{calendar}/filter", Ct), HttpStatusCode.NotFound);
        await Expect(await s1.Client.GetAsync($"/api/me/calendars/{calendar + 1}/filter", Ct), HttpStatusCode.NotFound);
        await Expect(await PutAsync(s3, $"/api/me/calendars/{calendar}/filter", """{"rules":[]}"""), HttpStatusCode.NotFound);
    }

    // US4, SC-006: the same rules on a MyTimetables and a generic calendar; inactive rules are reported and ignored.
    [Fact]
    public async Task MyTimetablesConditionsPerCalendar()
    {
        await using var h = await ApiHost.CreateAsync(("InTheKnow:MyTimetables:Domains:0:TeacherDetection", "false"));
        var s1 = await h.SignInAsync();
        var mt = await SeedAsync(h, s1);
        var generic = await SeedAsync(h, s1, host: "example.com");
        const string rules = """[{"outcome":"showOnly","when":{"activityType":["test"]}},{"outcome":"hide","when":{"nameContains":"Zelf"}}]""";
        var changes = new List<(long, string)>();
        foreach (var calendar in new[] { mt, generic })
        {
            var filter = await BodyAsync(await PutAsync(s1, $"/api/me/calendars/{calendar}/filter", $$"""{"rules":{{rules}}}"""));
            var isMt = calendar == mt;
            Assert.Equal(isMt ? "[]" : "[0]", filter["inactiveRules"]!.ToJsonString());
            Assert.Equal(isMt ? """["changeKind","eventEnded","status","nameContains","activityType","group","room"]"""
                              : """["changeKind","eventEnded","status","nameContains"]""", filter["activeConditions"]!.ToJsonString());
            var test = await RecordAsync(h.Db, calendar, "added", after: Ev("Toets", type: "test"));
            var lesson = await RecordAsync(h.Db, calendar, "added", after: Ev("Les", type: "lesson"));
            await RecordAsync(h.Db, calendar, "added", after: Ev("Zelfstudie", type: "test"));
            changes.AddRange(isMt ? [(test, "normal")] : [(test, "normal"), (lesson, "normal")]);
        }
        Assert.Equal(changes, Outcomes(await s1.JsonAsync("/api/me/changes")));
    }

    static Task<HttpResponseMessage> DeleteAsync(SignedIn who, string path) => who.Client.DeleteAsync(path, Ct);

    // US5, contracts/api.md § Personal presets.
    [Fact]
    public async Task PersonalPresets()
    {
        await using var h = await ApiHost.CreateAsync();
        var (s1, s2) = (await h.SignInAsync(), await h.SignInAsync());
        var (cal1, cal2) = (await SeedAsync(h, s1), await SeedAsync(h, s1));
        const string presets = "/api/me/presets";

        // "Save as": the filter's rules go back unchanged.
        var rules = (await s1.JsonAsync($"/api/me/calendars/{cal1}/filter"))["rules"]!;
        var created = await BodyAsync(await PostAsync(s1, presets, $$"""{"name":"Mine","rules":{{rules.ToJsonString()}}}"""), HttpStatusCode.Created);
        var id = created["id"]!.GetValue<long>();
        Assert.Equal($$"""{"id":{{id}},"name":"Mine","scope":"personal","rules":{{rules.ToJsonString()}}}""", created.ToJsonString());
        var list = await s1.JsonAsync(presets);
        Assert.Equal(["Mine", "Default"], list["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()));
        Assert.True(list["items"]![1]!["isDefault"]!.GetValue<bool>());
        Assert.Equal("global", list["items"]![1]!["scope"]!.GetValue<string>());

        // Rejected changes leave the list as it was (SC-008).
        foreach (var (task, status, reason) in new (Func<Task<HttpResponseMessage>>, HttpStatusCode, string)[]
        {
            (() => PostAsync(s1, presets, """{"name":"","rules":[]}"""), HttpStatusCode.BadRequest, "invalid_name"),
            (() => PostAsync(s1, presets, """{"rules":[]}"""), HttpStatusCode.BadRequest, "invalid_name"),
            (() => PostAsync(s1, presets, """{"name":"x","rules":[{"outcome":"hide"}]}"""), HttpStatusCode.BadRequest, "empty_conditions"),
            (() => PostAsync(s1, presets, """{"name":"Mine","rules":[]}"""), HttpStatusCode.Conflict, "name_taken"),
            (() => PutAsync(s1, $"{presets}/{id}", "{}"), HttpStatusCode.BadRequest, "invalid_rules"),
            (() => PutAsync(s1, $"{presets}/{id}", """{"name":5}"""), HttpStatusCode.BadRequest, "invalid_name"),
            (() => PutAsync(s1, $"{presets}/{id}", """{"rules":{}}"""), HttpStatusCode.BadRequest, "invalid_rules"),
            (() => PutAsync(s1, $"{presets}/1", """{"name":"x"}"""), HttpStatusCode.Forbidden, "preset_is_global"),
            (() => DeleteAsync(s1, $"{presets}/1"), HttpStatusCode.Forbidden, "preset_is_global"),
        })
        {
            await Expect(await task(), status, reason);
            Assert.True(JsonNode.DeepEquals(list, await s1.JsonAsync(presets)));
        }

        // Apply to two calendars; edits follow; the response is the GET filter body.
        foreach (var calendar in new[] { cal1, cal2 })
        {
            var applied = await BodyAsync(await PutAsync(s1, $"/api/me/calendars/{calendar}/filter/preset", $$"""{"presetId":{{id}}}"""));
            Assert.Equal($$"""{"type":"preset","presetId":{{id}},"name":"Mine","scope":"personal"}""", applied["source"]!.ToJsonString());
        }
        var ended = await RecordAsync(h.Db, cal2, "added", after: Ev("Old", endDays: -1));
        Assert.Empty(Outcomes(await s1.JsonAsync("/api/me/changes")));
        var edited = await BodyAsync(await PutAsync(s1, $"{presets}/{id}", """{"name":"Everything","rules":[]}"""));
        Assert.Equal(("Everything", "[]"), (edited["name"]!.GetValue<string>(), edited["rules"]!.ToJsonString()));
        Assert.Equal([(ended, "normal")], Outcomes(await s1.JsonAsync("/api/me/changes")));

        // Another subscriber's preset, an unknown one, or no subscription: 404 (FR-016).
        var theirs = (await BodyAsync(await PostAsync(s2, "/api/me/presets", """{"name":"Theirs","rules":[]}"""), HttpStatusCode.Created))["id"]!;
        await Expect(await PutAsync(s1, $"{presets}/{theirs}", """{"name":"x"}"""), HttpStatusCode.NotFound);
        await Expect(await DeleteAsync(s1, $"{presets}/{theirs}"), HttpStatusCode.NotFound);
        await Expect(await PutAsync(s1, $"{presets}/999999", """{"name":"x"}"""), HttpStatusCode.NotFound);
        await Expect(await PutAsync(s1, $"/api/me/calendars/{cal1}/filter/preset", $$"""{"presetId":{{theirs}}}"""), HttpStatusCode.NotFound);
        await Expect(await PutAsync(s1, $"/api/me/calendars/{cal1}/filter/preset", """{"presetId":999999}"""), HttpStatusCode.NotFound);
        await Expect(await PutAsync(s2, $"/api/me/calendars/{cal1}/filter/preset", """{"presetId":1}"""), HttpStatusCode.NotFound);

        // Delete: linked filters keep their outcome as customised ones (FR-018).
        await Expect(await DeleteAsync(s1, $"{presets}/{id}"), HttpStatusCode.NoContent);
        Assert.Equal("""{"type":"custom"}""", (await s1.JsonAsync($"/api/me/calendars/{cal2}/filter"))["source"]!.ToJsonString());
        Assert.Equal([(ended, "normal")], Outcomes(await s1.JsonAsync("/api/me/changes")));

        await h.Db.ExecAsync("INSERT INTO preset (owner, name, rules) SELECT $1, 'p' || g, '[]' FROM generate_series(1, 50) g", s1.AccountId);
        await Expect(await PostAsync(s1, presets, """{"name":"x","rules":[]}"""), HttpStatusCode.Conflict, "preset_limit");
    }

    // US6, contracts/api.md § Global presets.
    [Fact]
    public async Task GlobalPresets()
    {
        await using var h = await ApiHost.CreateAsync();
        var (s1, s2, op) = (await h.SignInAsync(), await h.SignInAsync(), await h.SignInAsync(admin: true, secondFactor: true));
        var (cal1, cal2) = (await SeedAsync(h, s1), await SeedAsync(h, s2));
        var ended = await RecordAsync(h.Db, cal1, "added", after: Ev("Old", endDays: -1));
        var ended2 = await RecordAsync(h.Db, cal2, "added", after: Ev("Old", endDays: -1));
        await PutAsync(s2, $"/api/me/calendars/{cal2}/filter", """{"rules":[{"outcome":"hide","when":{"nameContains":"Old"}}]}""");

        var everything = await BodyAsync(await PostAsync(op, "/api/admin/presets", """{"name":"Everything","rules":[]}"""), HttpStatusCode.Created);
        var id = everything["id"]!.GetValue<long>();
        Assert.Equal("global", everything["scope"]!.GetValue<string>());
        Assert.Equal(["Default", "Everything"], (await op.JsonAsync("/api/admin/presets"))["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()));
        await Expect(await PostAsync(op, "/api/admin/presets", """{"name":"Default","rules":[]}"""), HttpStatusCode.Conflict, "name_taken");
        await Expect(await PutAsync(op, $"/api/admin/presets/{id}", """{"name":"Default"}"""), HttpStatusCode.Conflict, "name_taken");
        await Expect(await DeleteAsync(op, "/api/admin/presets/1"), HttpStatusCode.Forbidden, "preset_is_default");
        var personal = (await BodyAsync(await PostAsync(s1, "/api/me/presets", """{"name":"Mine","rules":[]}"""), HttpStatusCode.Created))["id"]!;
        await Expect(await PutAsync(op, $"/api/admin/presets/{personal}", """{"name":"x"}"""), HttpStatusCode.NotFound);
        await Expect(await DeleteAsync(op, $"/api/admin/presets/{personal}"), HttpStatusCode.NotFound);

        // Subscribers list and apply it, but cannot edit or delete it.
        Assert.Contains("Everything", (await s2.JsonAsync("/api/me/presets"))["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()));
        await Expect(await PutAsync(s2, $"/api/me/presets/{id}", """{"name":"x"}"""), HttpStatusCode.Forbidden, "preset_is_global");
        await Expect(await PutAsync(s1, $"/api/me/calendars/{cal1}/filter/preset", $$"""{"presetId":{{id}}}"""), HttpStatusCode.OK);
        Assert.Equal([(ended, "normal")], Outcomes(await s1.JsonAsync("/api/me/changes")));
        await Expect(await DeleteAsync(op, $"/api/admin/presets/{id}"), HttpStatusCode.NoContent);
        Assert.Equal("""{"type":"custom"}""", (await s1.JsonAsync($"/api/me/calendars/{cal1}/filter"))["source"]!.ToJsonString());

        // Editing Default changes a linked subscriber's outcomes; a customised one is unaffected (US6-4).
        await PutAsync(s1, $"/api/me/calendars/{cal1}/filter/preset", """{"presetId":1}""");
        Assert.Empty(Outcomes(await s1.JsonAsync("/api/me/changes")));
        var standard = await BodyAsync(await PutAsync(op, "/api/admin/presets/1", """{"name":"Standard","rules":[]}"""));
        Assert.Equal("""{"id":1,"name":"Standard","scope":"global","rules":[],"isDefault":true}""", standard.ToJsonString());
        Assert.Equal([(ended, "normal")], Outcomes(await s1.JsonAsync("/api/me/changes")));
        Assert.Empty(Outcomes(await s2.JsonAsync("/api/me/changes")));
        Assert.Equal([(ended2, "hidden")], Outcomes(await s2.JsonAsync("/api/me/changes?includeHidden=true")));

        await h.Db.ExecAsync("INSERT INTO preset (name, rules) SELECT 'g' || g, '[]' FROM generate_series(1, 99) g");
        await Expect(await PostAsync(op, "/api/admin/presets", """{"name":"x","rules":[]}"""), HttpStatusCode.Conflict, "preset_limit");
    }

    // Quickstart 002 §3 (SC-009): 10,000 changes, most hidden, a limit=100 page under 1 s. Logs timings rather than
    // asserting them. Never runs in the normal gate.
    [Fact(Explicit = true)]
    public async Task FilteredChanges10000()
    {
        await using var h = await ApiHost.CreateAsync();
        var output = TestContext.Current.TestOutputHelper!;
        var (s1, s2) = (await h.SignInAsync(), await h.SignInAsync());
        var (mixed, late) = (await SeedAsync(h, s1), await SeedAsync(h, s2));
        // Field changes with realistic before/after events; "ended" ones end before detection (Default hides them).
        const string insert = """
            INSERT INTO detected_change (calendar_id, event_key, kind, field, old_value, new_value, event_name, event_starts_at, detected_at,
                                         event_before, event_after)
            SELECT $1, 'k' || g, 'changed', 'rooms', '["R1"]', '["R2"]', 'Lesson', $2, $2, e, e
            FROM generate_series(1, 10000) g,
                 LATERAL (SELECT jsonb_build_object('key', 'k' || g, 'name', 'Lesson ' || g, 'status', 'confirmed',
                     'start', '2026-09-30T08:00:00Z', 'end', CASE WHEN $3(g) THEN '2026-09-30T09:00:00Z' ELSE '2026-10-02T09:00:00Z' END,
                     'fields', jsonb_build_object('type', 'lesson', 'rooms', jsonb_build_array('R1'), 'groups', jsonb_build_array('ES1', 'ES2'),
                                                  'teachers', jsonb_build_array('Docent1, Anna'))) e) x
            """;
        await h.Db.ExecAsync(insert.Replace("$3(g)", "g % 20 <> 0"), mixed, FilterEngineTests.Detected); // 95% hidden
        await h.Db.ExecAsync(insert.Replace("$3(g)", "g <= 9000"), late, FilterEngineTests.Detected);    // first 9,000 hidden
        foreach (var who in new[] { s1, s2 })
        {
            var path = "/api/me/changes?limit=100";
            await who.JsonAsync(path);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var json = await who.JsonAsync(path);
            output.WriteLine($"GET {path}: {watch.Elapsed.TotalMilliseconds:F0} ms, {json["items"]!.AsArray().Count} items");
        }
    }
}
