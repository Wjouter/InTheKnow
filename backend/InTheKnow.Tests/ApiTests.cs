using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
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

    public static async Task<ApiHost> CreateAsync(params (string Key, string Value)[] settings)
    {
        var db = await TestDb.CreateAsync();
        var time = new FakeTimeProvider(Harness.T0);
        var http = new FakeHttp(time);
        var logs = new CapturingLogs();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Default", db.ConnectionString);
            b.UseSetting("InTheKnow:ApiKey", TestDb.ApiKey);
            foreach (var (k, v) in settings) b.UseSetting(k, v);
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<Fetcher>();
                s.AddSingleton(new Fetcher(http, FakeHttp.Resolve));
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(time);
                s.RemoveAll<IHostedService>();
                s.AddSingleton<ILoggerProvider>(logs);
            });
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestDb.ApiKey);
        return new ApiHost { Db = db, Time = time, Http = http, Logs = logs, Factory = factory, Client = client };
    }

    // Moves the fake clock a second on first, so the validation never waits for a budget slot.
    public Task<HttpResponseMessage> AddAsync(string subscriber, string url, string fixture = "mt-nl.ics")
    {
        Http.Map.TryAdd(new Uri(url).AbsoluteUri, fixture);
        Time.Advance(TimeSpan.FromSeconds(1));
        return Client.PostAsJsonAsync($"/api/subscribers/{subscriber}/links", new { url }, TestContext.Current.CancellationToken);
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

public class ApiTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<string> ReasonAsync(HttpResponseMessage r) =>
        JsonNode.Parse(await r.Content.ReadAsStringAsync(Ct))!["reason"]!.GetValue<string>();

    [Fact]
    public async Task EveryRouteNeedsTheKey()
    {
        await using var h = await ApiHost.CreateAsync();
        var url = MtLink();
        h.Http.Map[new Uri(url).AbsoluteUri] = "mt-nl.ics";
        using var bare = h.Factory.CreateClient(); // no default Authorization header
        foreach (var auth in new[] { null, new AuthenticationHeaderValue("Bearer", "wrong-key-wrong-key-wrong-key-wrong-key-00"), new AuthenticationHeaderValue("Bearer", "") })
        {
            HttpRequestMessage[] requests =
            [
                new(HttpMethod.Post, "/api/subscribers/s1/links") { Content = JsonContent.Create(new { url }) },
                new(HttpMethod.Get, "/api/subscribers/s1/calendars"),
                new(HttpMethod.Delete, "/api/subscribers/s1/calendars/1"),
                new(HttpMethod.Get, "/api/subscribers/s1/changes"),
                new(HttpMethod.Get, "/api/calendars"),
            ];
            foreach (var request in requests)
            {
                request.Headers.Authorization = auth;
                var response = await bare.SendAsync(request, Ct);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal("", await response.Content.ReadAsStringAsync(Ct));
            }
        }
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM calendar"));
        Assert.Equal(0, h.Http.Count(new Uri(url).AbsoluteUri));
    }

    [Fact]
    public async Task AddListChangesAndRemove()
    {
        await using var h = await ApiHost.CreateAsync();
        var url = MtLink(hex: "01");

        // Add (US5-1)
        var created = await h.AddAsync("s1", url);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = JsonNode.Parse(await created.Content.ReadAsStringAsync(Ct))!;
        var calendarId = body["calendarId"]!.GetValue<long>();
        Assert.True(body["calendarIsNew"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await h.AddAsync("s1", url)).StatusCode);
        var variant = await h.AddAsync("s1", MtLink(hex: "02"));
        Assert.Equal(HttpStatusCode.Created, variant.StatusCode);
        Assert.False(JsonNode.Parse(await variant.Content.ReadAsStringAsync(Ct))!["calendarIsNew"]!.GetValue<bool>());

        // List (baseline from validation)
        var calendars = (await h.JsonAsync("/api/subscribers/s1/calendars")).AsArray();
        var calendar = Assert.Single(calendars)!;
        Assert.Equal(calendarId, calendar["calendarId"]!.GetValue<long>());
        Assert.True(calendar["lastOk"]!.GetValue<bool>());
        Assert.Equal(["linkId", "flagged", "createdAt"], calendar["links"]![0]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(2, calendar["links"]!.AsArray().Count);
        Assert.Empty((await h.JsonAsync("/api/subscribers/nobody/calendars")).AsArray());

        // Changes (US5-3): only this subscriber's, in id order, paged.
        await h.AddAsync("s2", MtLink(id: "ALET26VT4A"));
        h.Http.Map[new Uri(url).AbsoluteUri] = "mt-nl-edited.ics";
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await h.Checker.RunDueChecksAsync(h.Time.GetUtcNow(), Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var all = (await h.JsonAsync("/api/subscribers/s1/changes"))["items"]!.AsArray();
        Assert.Equal(8, all.Count);
        Assert.Equal(all.Select(i => i!["id"]!.GetValue<long>()).Order(), all.Select(i => i!["id"]!.GetValue<long>()));
        Assert.All(all, i => Assert.Equal(calendarId, i!["calendarId"]!.GetValue<long>()));
        var rooms = all.Single(i => i!["field"]?.GetValue<string>() == "rooms")!;
        Assert.Equal("[\"AL00.A1.20\"]", rooms["newValue"]!.ToJsonString());
        Assert.Empty((await h.JsonAsync("/api/subscribers/s2/changes"))["items"]!.AsArray());

        var page = await h.JsonAsync("/api/subscribers/s1/changes?limit=2");
        Assert.Equal(2, page["items"]!.AsArray().Count);
        var next = page["next"]!.GetValue<long>();
        var page2 = await h.JsonAsync($"/api/subscribers/s1/changes?limit=2&after={next}");
        Assert.Equal(all[2]!["id"]!.GetValue<long>(), page2["items"]![0]!["id"]!.GetValue<long>());
        var last = await h.JsonAsync($"/api/subscribers/s1/changes?after={all[6]!["id"]}&limit=2");
        Assert.Null(last["next"]);
        foreach (var bad in new[] { 0, 501 })
        {
            var r = await h.Client.GetAsync($"/api/subscribers/s1/changes?limit={bad}", Ct);
            Assert.Equal((HttpStatusCode.BadRequest, "invalid_limit"), (r.StatusCode, await ReasonAsync(r)));
        }
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM detected_change WHERE notified_at IS NOT NULL"));

        // Remove (US5-4)
        var removed = await h.Client.DeleteAsync($"/api/subscribers/s1/calendars/{calendarId}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        foreach (var table in new[] { "calendar_link", "event_snapshot", "detected_change" })
            Assert.Equal(0, await h.Db.ScalarAsync($"SELECT count(*) FROM {table} WHERE calendar_id = $1", calendarId));
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM calendar WHERE id = $1", calendarId));
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.DeleteAsync($"/api/subscribers/s1/calendars/{calendarId}", Ct)).StatusCode);

        // No link anywhere (FR-032, SC-011).
        var token = new Uri(url).Query.Split('&').Single(p => p.StartsWith("h=", StringComparison.Ordinal))[2..];
        Assert.DoesNotContain(h.Logs.Lines, l => l.Contains(token) || l.Contains("/ical?"));
        Assert.DoesNotContain(token, (await h.JsonAsync("/api/calendars")).ToJsonString());
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
        await Expect(await h.AddAsync("a%20b", "https://example.com/a.ics"), HttpStatusCode.BadRequest, "invalid_subscriber");
        await Expect(await h.AddAsync("s1", "ftp://example.com/a.ics"), HttpStatusCode.BadRequest, "unsupported_scheme");
        await Expect(await h.AddAsync("s1", "http://example.com/a.ics"), HttpStatusCode.BadRequest, "unsupported_scheme");
        await Expect(await h.AddAsync("s1", MtLink(), "mt-empty.ics"), HttpStatusCode.UnprocessableEntity, "validation_failed:empty_feed");

        var budget = h.Factory.Services.GetRequiredService<HostBudget>();
        for (var i = 0; i < 12; i++) budget.TryReserve(Harness.Mt, h.Time.GetUtcNow() + TimeSpan.FromSeconds(1), TimeSpan.MaxValue);
        var busy = await h.AddAsync("s1", MtLink(id: "OTHER"));
        await Expect(busy, HttpStatusCode.TooManyRequests, "host_budget_exhausted");
        Assert.Equal("5", busy.Headers.RetryAfter!.ToString());
    }

    [Fact]
    public async Task OperatorOverview()
    {
        await using var h = await ApiHost.CreateAsync();
        var ok1 = (await h.Db.SeedCalendarAsync("a.example", ["https://a.example/1", "https://a.example/2"], "s1")).CalendarId;
        await h.Db.ExecAsync("INSERT INTO calendar_link (calendar_id, subscriber, url, flagged_at) VALUES ($1, 's2', 'https://a.example/3', now())", ok1);
        var failing = (await h.Db.SeedCalendarAsync("b.example", ["https://b.example/1"], "s1", h.Time.GetUtcNow().AddHours(1))).CalendarId;
        await h.Db.ExecAsync("UPDATE calendar SET status = 'failing' WHERE id = $1", failing);
        var ok2 = (await h.Db.SeedCalendarAsync("c.example", ["https://c.example/1"], "s3", h.Time.GetUtcNow().AddSeconds(-30))).CalendarId;

        var f = Assert.Single((await h.JsonAsync("/api/calendars?status=failing"))["items"]!.AsArray())!;
        Assert.Equal((failing, 1L, 1L, 1L), (f["calendarId"]!.GetValue<long>(), f["subscribers"]!.GetValue<long>(), f["validLinks"]!.GetValue<long>(), f["totalLinks"]!.GetValue<long>()));
        Assert.Null(f["links"]);

        var overdue = (await h.JsonAsync("/api/calendars?overdue=true"))["items"]!.AsArray();
        var o = Assert.Single(overdue)!; // ok1 is due since the epoch; ok2 only 30 s late; failing is in the future
        Assert.Equal((ok1, 2L, 2L, 3L), (o["calendarId"]!.GetValue<long>(), o["subscribers"]!.GetValue<long>(), o["validLinks"]!.GetValue<long>(), o["totalLinks"]!.GetValue<long>()));

        var page = await h.JsonAsync("/api/calendars?limit=2");
        Assert.Equal([ok1, failing], page["items"]!.AsArray().Select(i => i!["calendarId"]!.GetValue<long>()));
        var rest = await h.JsonAsync($"/api/calendars?limit=2&after={page["next"]}");
        Assert.Equal([ok2], rest["items"]!.AsArray().Select(i => i!["calendarId"]!.GetValue<long>()));
        Assert.Null(rest["next"]);

        var badStatus = await h.Client.GetAsync("/api/calendars?status=x", Ct);
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_status"), (badStatus.StatusCode, await ReasonAsync(badStatus)));
        var badLimit = await h.Client.GetAsync("/api/calendars?limit=0", Ct);
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_limit"), (badLimit.StatusCode, await ReasonAsync(badLimit)));
        Assert.DoesNotContain("https://", (await h.JsonAsync("/api/calendars")).ToJsonString());
    }
}
