using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace InTheKnow.Tests;

// specs/004 SC-011: 500 sign-ins in a minute at the production hash cost, each within 2 s. About two minutes, so it runs
// only with ITK_LOAD=1: env ITK_LOAD=1 dotnet test --filter-trait "Category=Load"
public class SignInLoadTest
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Load")]
    public async Task FiveHundredSignInsInAMinute()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ITK_LOAD") == "1", "set ITK_LOAD=1 to run the SC-011 load test");
        await using var h = await ApiHost.CreateAsync(s => s.Configure<PasswordHasherOptions>(o => o.IterationCount = 210_000));
        // One production-cost hash serves every account: verifying it costs the same for each.
        var hash = new PasswordHasher<Account>(Options.Create(new PasswordHasherOptions { IterationCount = 210_000 })).HashPassword(null!, TestHost.Password);
        await h.Db.ExecAsync("""
            WITH a AS (INSERT INTO account (email, language, confirmed_at, created_at)
                       SELECT 'load' || g || '@example.nl', 'en', now(), now() FROM generate_series(1, 500) g RETURNING id)
            INSERT INTO sign_in_method (account_id, type, secret, created_at) SELECT id, 'password', $1, now() FROM a
            """, hash);

        var durations = new double[500];
        var start = Stopwatch.StartNew();
        var runs = new List<Task>();
        for (var i = 0; i < 500; i++)
        {
            var at = TimeSpan.FromMilliseconds(60_000.0 * i / 500);
            if (at > start.Elapsed) await Task.Delay(at - start.Elapsed, Ct);
            var n = i;
            runs.Add(Task.Run(async () =>
            {
                var watch = Stopwatch.StartNew();
                var r = await h.Client.PostAsJsonAsync("/api/auth/sign-in", new { email = $"load{n + 1}@example.nl", password = TestHost.Password }, Ct);
                durations[n] = watch.Elapsed.TotalMilliseconds;
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            }, Ct));
        }
        await Task.WhenAll(runs);
        var sorted = durations.Order().ToArray();
        TestContext.Current.TestOutputHelper!.WriteLine($"SC-011: 500 sign-ins in {start.Elapsed.TotalSeconds:F1} s; p50 {sorted[249]:F0} ms, p95 {sorted[474]:F0} ms, max {sorted[^1]:F0} ms");
        Assert.All(durations, ms => Assert.True(ms < 2000, $"{ms:F0} ms"));
    }
}
