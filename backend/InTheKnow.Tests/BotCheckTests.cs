using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace InTheKnow.Tests;

// specs/004 US9, FR-037 to FR-040, SC-015, contracts/bot-check.md § Test provider.
public class BotCheckTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    const string Secret = "s3cr3t-bot-check-key";
    static readonly BotCheckProvider Test = new("test", new Uri("https://bot.test/verify"));

    // The test provider: answers per posted token, and remembers every form it was sent.
    internal sealed class Provider
    {
        public readonly FakeHttp Http;
        public readonly ConcurrentQueue<Dictionary<string, string>> Forms = new();
        readonly ConcurrentDictionary<string, int> seen = new();

        public Provider(TimeProvider? time = null)
        {
            Http = new FakeHttp(time) { Respond = RespondAsync };
        }

        async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            var form = (await request.Content.ReadAsStringAsync(ct)).Split('&').Select(p => p.Split('='))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
            Forms.Enqueue(form);
            var token = form["response"];
            var uses = seen.AddOrUpdate(token, 1, (_, n) => n + 1);
            HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
            switch (token)
            {
                case "tok-hang": await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException();
                case "tok-500": return new(HttpStatusCode.InternalServerError);
                case "tok-junk": return Json("<html>");
                case "tok-internal": return Json("""{"success":false,"error-codes":["internal-error"]}""");
                case var t when t.StartsWith("tok-ok", StringComparison.Ordinal) && uses == 1: return Json("""{"success":true}""");
                case var t when t.StartsWith("tok-ok", StringComparison.Ordinal): return Json("""{"success":false,"error-codes":["timeout-or-duplicate"]}""");
                default: return Json("""{"success":false,"error-codes":["invalid-input-response"]}""");
            }
        }
    }

    internal static BotCheck New(Provider p, ILogger<BotCheck> log, TimeProvider? time = null) => new(Test, "site", Secret, new HttpClient(p.Http), log, time);

    [Fact]
    public async Task Outcomes()
    {
        var time = new FakeTimeProvider(Harness.T0);
        var p = new Provider(time);
        var logs = new CapturingLogs();
        var bot = New(p, logs.CreateLogger<BotCheck>(), time);
        Assert.Equal(BotCheckOutcome.Passed, await bot.VerifyAsync("tok-ok-1", Ct));
        Assert.Equal(BotCheckOutcome.Failed, await bot.VerifyAsync("tok-ok-1", Ct)); // replayed
        Assert.Equal(BotCheckOutcome.Failed, await bot.VerifyAsync("tok-bad", Ct));
        Assert.Equal(BotCheckOutcome.Unavailable, await bot.VerifyAsync("tok-internal", Ct));
        Assert.Equal(BotCheckOutcome.Unavailable, await bot.VerifyAsync("tok-500", Ct));
        Assert.Equal(BotCheckOutcome.Unavailable, await bot.VerifyAsync("tok-junk", Ct));
        var sent = p.Forms.Count;
        Assert.Equal(BotCheckOutcome.Failed, await bot.VerifyAsync(null, Ct));
        Assert.Equal(BotCheckOutcome.Failed, await bot.VerifyAsync("", Ct));
        Assert.Equal(sent, p.Forms.Count); // no request for a missing result

        var hanging = bot.VerifyAsync("tok-hang", Ct);
        while (p.Forms.Count == sent) await Task.Delay(10, Ct);
        Assert.False(hanging.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(BotCheckOutcome.Unavailable, await hanging);

        Assert.All(p.Forms, f => Assert.Equal(["secret", "response"], f.Keys));
        Assert.All(p.Forms, f => Assert.Equal(Secret, f["secret"]));
        Assert.Contains(logs.Lines, l => l.Contains("test") && l.Contains("Failed") && l.Contains("invalid-input-response"));
        Assert.Contains(logs.Lines, l => l.Contains("Unavailable") && l.Contains("internal-error"));
        Assert.DoesNotContain(logs.Lines, l => l.Contains("tok-") || l.Contains(Secret));
    }

    static async Task<(ApiHost H, Provider P, CountingHasher Hasher)> HostAsync()
    {
        var p = new Provider();
        var hasher = new CountingHasher(new PasswordHasher<Account>(Options.Create(new PasswordHasherOptions { IterationCount = 1 })));
        var h = await SignInTests.HostAsync(s =>
        {
            s.RemoveAll<BotCheck>();
            s.AddSingleton(sp => New(p, sp.GetRequiredService<ILogger<BotCheck>>()));
            s.RemoveAll<IPasswordHasher<Account>>();
            s.AddSingleton<IPasswordHasher<Account>>(hasher);
        });
        await h.Db.ExecAsync("UPDATE signup_settings SET open = true");
        return (h, p, hasher);
    }

    static Task<HttpResponseMessage> Post(ApiHost h, string path, object body) => h.Client.PostAsJsonAsync(path, body, Ct);

    // The four protected routes, with a bot check result (or none).
    static IEnumerable<(string Path, Func<string?, object> Body)> Protected(string email, string password, string? invite = null) =>
    [
        ("/api/auth/sign-in", b => new { email, password, botCheck = b }),
        ("/api/auth/sign-up", b => new { email, password, language = "en", botCheck = b }),
        ("/api/auth/sign-up", b => new { email, password, language = "en", invite = invite ?? "unknown-invite", botCheck = b }),
        ("/api/auth/confirmation", b => new { email, botCheck = b }),
        ("/api/auth/password-reset", b => new { email, botCheck = b }),
    ];

    // US9-2, SC-015: missing, invalid or replayed results are refused first, for every address alike.
    [Fact]
    public async Task ProtectedRoutesCheckFirst()
    {
        var (h, p, hasher) = await HostAsync();
        await using var _ = h;
        var known = await h.SignInAsync();
        await h.Db.ExecAsync("DELETE FROM security_event");
        foreach (var (email, password) in new[] { (known.Email, TestHost.Password), ("unknown@example.nl", TestHost.Password), ("not an address", "short") })
            foreach (var (path, body) in Protected(email, password))
                foreach (var result in new[] { null, "tok-bad", "tok-ok-replayed" })
                {
                    if (result == "tok-ok-replayed") await p.Http.Respond!(new HttpRequestMessage(HttpMethod.Post, Test.VerifyUrl)
                        { Content = new FormUrlEncodedContent([new("secret", Secret), new("response", result)]) }, Ct);
                    var r = await Post(h, path, body(result));
                    Assert.Equal((HttpStatusCode.BadRequest, "bot_check_failed"), (r.StatusCode, await TestHost.ReasonAsync(r)));
                }
        Assert.Equal(0, hasher.Verifies);
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM security_event"));
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM account_message"));

        // A valid result: the route's normal answer.
        var n = 0;
        string Ok() => $"tok-ok-{n++}";
        Assert.Equal(HttpStatusCode.OK, (await Post(h, "/api/auth/sign-in", new { email = known.Email, password = TestHost.Password, botCheck = Ok() })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(h, "/api/auth/sign-up", new { email = "new@example.nl", password = TestHost.Password, language = "en", botCheck = Ok() })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(h, "/api/auth/confirmation", new { email = "new@example.nl", botCheck = Ok() })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(h, "/api/auth/password-reset", new { email = known.Email, botCheck = Ok() })).StatusCode);
        var invalidInvite = await Post(h, "/api/auth/sign-up", new { email = "x@example.nl", password = TestHost.Password, language = "en", invite = "nope", botCheck = Ok() });
        Assert.Equal((HttpStatusCode.Gone, "invite_invalid"), (invalidInvite.StatusCode, await TestHost.ReasonAsync(invalidInvite)));
    }

    // FR-040: when the provider cannot be reached, sign-in continues (still throttled); the other actions wait.
    [Fact]
    public async Task ProviderUnavailable()
    {
        var (h, _, _) = await HostAsync();
        await using var _h = h;
        var known = await h.SignInAsync();
        foreach (var (path, body) in Protected(known.Email, TestHost.Password))
        {
            var r = await Post(h, path, body("tok-internal"));
            if (path == "/api/auth/sign-in") Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            else Assert.Equal((HttpStatusCode.ServiceUnavailable, "try_again_later"), (r.StatusCode, await TestHost.ReasonAsync(r)));
        }
        for (var i = 0; i < 5; i++) await Post(h, "/api/auth/sign-in", new { email = known.Email, password = "wrong-password-0123", botCheck = "tok-500" });
        var throttled = await Post(h, "/api/auth/sign-in", new { email = known.Email, password = TestHost.Password, botCheck = "tok-500" });
        Assert.Equal((HttpStatusCode.TooManyRequests, "too_many_attempts"), (throttled.StatusCode, await TestHost.ReasonAsync(throttled)));
    }

    // The code step, the link completions and the signed-in routes never read a bot check.
    [Fact]
    public async Task UnprotectedRoutesIgnoreIt()
    {
        var (h, p, _) = await HostAsync();
        await using var _h = h;
        var a = await h.SignInAsync();
        var token = SignIn.NewToken().Token;
        foreach (var (path, body, status) in new (string, object, HttpStatusCode)[]
        {
            ("/api/auth/confirm", new { token, password = TestHost.Password, botCheck = "tok-bad" }, HttpStatusCode.BadRequest),
            ("/api/auth/password-reset/complete", new { token, password = TestHost.Password, botCheck = "tok-bad" }, HttpStatusCode.BadRequest),
            ("/api/auth/email-change/confirm", new { token, botCheck = "tok-bad" }, HttpStatusCode.BadRequest),
            ("/api/auth/invite/accept", new { token, password = TestHost.Password, botCheck = "tok-bad" }, HttpStatusCode.Gone),
        })
        {
            var r = await Post(h, path, body);
            Assert.Equal(status, r.StatusCode);
            Assert.NotEqual("bot_check_failed", await TestHost.ReasonAsync(r));
        }
        Assert.Equal(HttpStatusCode.OK, (await a.Client.GetAsync("/api/me", Ct)).StatusCode);
        var code = await a.Client.PostAsJsonAsync("/api/auth/sign-in/code", new { code = "000000", botCheck = "tok-bad" }, Ct);
        Assert.Equal("code_invalid", await TestHost.ReasonAsync(code));
        Assert.Empty(p.Forms);
    }

    // US9-1, US9-3, US9-4: off by default; the public settings; the providers' URLs from configuration.
    [Fact]
    public async Task Configuration()
    {
        await using (var h = await SignInTests.HostAsync())
        {
            await h.Db.ExecAsync("UPDATE signup_settings SET open = true");
            Assert.Equal("""{"provider":null,"siteKey":null}""", await (await h.Client.GetAsync("/api/auth/bot-check", Ct)).Content.ReadAsStringAsync(Ct));
            var a = await h.SignInAsync();
            Assert.Equal(HttpStatusCode.OK, (await Post(h, "/api/auth/sign-in", new { email = a.Email, password = TestHost.Password })).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await Post(h, "/api/auth/sign-up", new { email = "n@example.nl", password = TestHost.Password, language = "en" })).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await Post(h, "/api/auth/password-reset", new { email = a.Email })).StatusCode);
        }
        var (withTest, _, _) = await HostAsync();
        await using (withTest)
        {
            var body = await (await withTest.Client.GetAsync("/api/auth/bot-check", Ct)).Content.ReadAsStringAsync(Ct);
            Assert.Equal("""{"provider":"test","siteKey":"site"}""", body);
            Assert.DoesNotContain(Secret, body);
        }
        foreach (var (provider, url) in new[] { ("recaptcha", "https://www.google.com/recaptcha/api/siteverify"),
                                                ("turnstile", "https://challenges.cloudflare.com/turnstile/v0/siteverify") })
        {
            await using var h = await ApiHost.CreateAsync(("InTheKnow:Accounts:BotCheck:Provider", provider),
                ("InTheKnow:Accounts:BotCheck:SiteKey", "site"), ("InTheKnow:Accounts:BotCheck:SecretKey", Secret));
            var bot = h.Factory.Services.GetRequiredService<BotCheck>();
            Assert.Equal((provider, url), (bot.ProviderName, bot.VerifyUrl!.AbsoluteUri));
        }
    }
}
