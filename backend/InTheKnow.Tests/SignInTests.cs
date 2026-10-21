using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace InTheKnow.Tests;

// Counts verifications, to show that an unknown address still costs one hash (SC-003).
sealed class CountingHasher(IPasswordHasher<Account> inner) : IPasswordHasher<Account>
{
    public int Verifies;
    public string HashPassword(Account user, string password) => inner.HashPassword(user, password);
    public PasswordVerificationResult VerifyHashedPassword(Account user, string hashedPassword, string providedPassword)
    {
        Interlocked.Increment(ref Verifies);
        return inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}

// specs/004 US1 (sign-in, sessions, sign-out), FR-011 to FR-014, SC-003, SC-006, SC-009; theories over password and test.
public class SignInTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    public static TheoryData<string> Methods => ["password", "test"];

    internal static Task<ApiHost> HostAsync(Action<IServiceCollection>? services = null, params (string Key, string Value)[] settings) =>
        ApiHost.CreateAsync(s => { TestSignIn.Register(s); services?.Invoke(s); }, settings);

    static async Task Expect(HttpResponseMessage r, HttpStatusCode status, string? reason = null)
    {
        Assert.Equal(status, r.StatusCode);
        if (reason is not null) Assert.Equal(reason, await TestHost.ReasonAsync(r));
    }

    static Task<HttpResponseMessage> Me(HttpClient client) => client.GetAsync("/api/me", Ct);

    internal static async Task<HttpResponseMessage> PasswordSignInAsync(ApiHost h, string email, string password, string? source = null, string? userAgent = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/sign-in") { Content = JsonContent.Create(new { email, password }) };
        if (source is not null) request.Headers.Add("X-Test-Source", source);
        if (userAgent is not null) request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return await h.Client.SendAsync(request, Ct);
    }

    [Theory, MemberData(nameof(Methods))]
    public async Task SignInAndOut(string method)
    {
        await using var h = await HostAsync();
        var a = await h.SignInAsync(method: method);

        var r = method == "password"
            ? await PasswordSignInAsync(h, a.Email.ToUpperInvariant(), TestHost.Password)
            : await h.Client.PostAsJsonAsync("/api/test-auth/sign-in", new { key = a.Email.ToUpperInvariant() }, Ct);
        var body = JsonNode.Parse(await r.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal(["token", "secondFactorRequired"], body.AsObject().Select(kv => kv.Key));
        Assert.Equal(43, body["token"]!.GetValue<string>().Length);
        Assert.False(body["secondFactorRequired"]!.GetValue<bool>());
        var client = h.ClientFor(body["token"]!.GetValue<string>());
        var me = JsonNode.Parse(await (await Me(client)).Content.ReadAsStringAsync(Ct))!;
        Assert.Equal(["email", "role", "language", "twoFactor", "recoveryCodesLeft", "hasPassword", "pendingEmail", "createdAt", "homePage"], me.AsObject().Select(kv => kv.Key));
        Assert.Equal((a.Email, "user", method == "password"), (me["email"]!.GetValue<string>(), me["role"]!.GetValue<string>(), me["hasPassword"]!.GetValue<bool>()));

        // Two devices: signing out of one leaves the other (US1-6).
        var other = await h.SignInViaAsync(method, a.Email);
        await Expect(await client.PostAsync("/api/auth/sign-out", null, Ct), HttpStatusCode.NoContent);
        await Expect(await Me(client), HttpStatusCode.Unauthorized, "not_signed_in");
        await Expect(await Me(other.Client), HttpStatusCode.OK);

        // Sign out everywhere, this session included.
        var third = await h.SignInViaAsync(method, a.Email);
        await Expect(await third.Client.PostAsync("/api/auth/sign-out-everywhere", null, Ct), HttpStatusCode.NoContent);
        foreach (var c in new[] { other.Client, third.Client, a.Client }) await Expect(await Me(c), HttpStatusCode.Unauthorized, "not_signed_in");
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'signedOut' AND account_id = $1", a.AccountId));
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'signedOutEverywhere' AND account_id = $1", a.AccountId));
    }

    // FR-013: idle expiry, and last_used_at written at most every 5 minutes (research R2).
    [Theory, MemberData(nameof(Methods))]
    public async Task IdleExpiry(string method)
    {
        await using var h = await HostAsync();
        var a = await h.SignInViaAsync(method, (await h.SignInAsync(method: method)).Email);
        Task<object?> LastUsed() => h.Db.RowsAsync("SELECT last_used_at FROM session WHERE id = $1", a.SessionId).ContinueWith(t => t.Result.Single()[0]);
        var start = await LastUsed();
        h.Time.Advance(TimeSpan.FromMinutes(4));
        await Expect(await Me(a.Client), HttpStatusCode.OK);
        Assert.Equal(start, await LastUsed());
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await Expect(await Me(a.Client), HttpStatusCode.OK);
        Assert.Equal(h.Time.GetUtcNow().UtcDateTime, (DateTime)(await LastUsed())!);
        h.Time.Advance(TimeSpan.FromDays(30) + TimeSpan.FromSeconds(1));
        await Expect(await Me(a.Client), HttpStatusCode.Unauthorized, "not_signed_in");
    }

    [Fact]
    public async Task DeviceLabel()
    {
        await using var h = await HostAsync();
        var a = await h.SignInAsync();
        await Expect(await PasswordSignInAsync(h, a.Email, TestHost.Password,
            userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:130.0) Gecko/20100101 Firefox/130.0"), HttpStatusCode.OK);
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM session WHERE account_id = $1 AND device = 'Firefox on Windows'", a.AccountId));

        foreach (var (ua, device) in new[]
        {
            ("Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/128.0 Safari/537.36 Edg/128.0", "Edge on Windows"),
            ("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 Chrome/128.0 Safari/537.36 OPR/112.0", "Opera on Linux"),
            ("Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 Chrome/128.0 Mobile Safari/537.36", "Chrome on Android"),
            ("Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 Version/17.5 Mobile/15E148 Safari/604.1", "Safari on iOS"),
            ("Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 Version/17.5 Safari/604.1", "Safari on iOS"),
            ("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/605.1.15 Version/17.5 Safari/605.1.15", "Safari on macOS"),
            ("Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0", "Firefox on Linux"),
            ("curl/8.9.1", "Unknown browser"),
            ("", "Unknown browser"),
        })
            Assert.Equal(device, SignIn.Device(ua));
        Assert.Equal("Unknown browser", SignIn.Device(null));
        Assert.True(SignIn.Device(new string('x', 5000) + "Firefox/1 Windows").Length <= 60);
    }

    static async Task<string> WithoutTraceIdAsync(HttpResponseMessage r)
    {
        var json = JsonNode.Parse(await r.Content.ReadAsStringAsync(Ct))!.AsObject();
        json.Remove("traceId");
        return $"{(int)r.StatusCode} {json.ToJsonString()}";
    }

    // US1-5, SC-003: unknown address, wrong password, a too short one and an account without a password look the same,
    // and every one costs a hash.
    [Fact]
    public async Task NoEnumeration()
    {
        var counting = new CountingHasher(new PasswordHasher<Account>(Options.Create(new PasswordHasherOptions { IterationCount = 1 })));
        await using var h = await HostAsync(s => { s.RemoveAll<IPasswordHasher<Account>>(); s.AddSingleton<IPasswordHasher<Account>>(counting); });
        var a = await h.SignInAsync();
        var keyOnly = await h.SignInAsync(method: "test");
        var responses = new List<string>();
        foreach (var (email, password) in new[]
        {
            ("nobody@example.nl", TestHost.Password), (a.Email, "wrong-password-0123"), (a.Email, "short123"), (keyOnly.Email, TestHost.Password),
        })
        {
            var before = counting.Verifies;
            responses.Add(await WithoutTraceIdAsync(await PasswordSignInAsync(h, email, password)));
            Assert.Equal(before + 1, counting.Verifies);
        }
        Assert.Single(responses.Distinct());
        Assert.StartsWith("401 ", responses[0]);
        Assert.Contains("invalid_credentials", responses[0]);
    }

    // FR-012, research R8: failures per (address, source), the same for unknown addresses.
    [Fact]
    public async Task ThrottlingThroughTheRoute()
    {
        await using var h = await HostAsync();
        var a = await h.SignInAsync();
        foreach (var email in new[] { a.Email, "unknown@example.nl" })
        {
            for (var i = 0; i < 5; i++) await Expect(await PasswordSignInAsync(h, email, "wrong-password-0123", "192.0.2.1"), HttpStatusCode.Unauthorized);
            var refused = await PasswordSignInAsync(h, email, "wrong-password-0123", "192.0.2.1");
            await Expect(refused, HttpStatusCode.TooManyRequests, "too_many_attempts");
            Assert.Equal("30", refused.Headers.RetryAfter!.ToString());
        }
        await Expect(await PasswordSignInAsync(h, a.Email, TestHost.Password, "192.0.2.1"), HttpStatusCode.TooManyRequests, "too_many_attempts");
        await Expect(await PasswordSignInAsync(h, a.Email, TestHost.Password, "198.51.100.7"), HttpStatusCode.OK);
        // The first refusal of each wait is logged, once (FR-035).
        Assert.Equal(2, await h.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'throttled' AND source = '192.0.2.1'::inet"));
    }

    // A hash made with older parameters is replaced in place at sign-in, so the method's sessions survive.
    [Fact]
    public async Task RehashInPlace()
    {
        await using var h = await HostAsync(s => s.Configure<PasswordHasherOptions>(o => o.IterationCount = 2));
        var a = await h.SignInAsync();
        var (id, secret) = (await h.Db.RowsAsync("SELECT id, secret FROM sign_in_method WHERE account_id = $1", a.AccountId)).Single() is var row
            ? ((long)row[0]!, (string)row[1]!) : default;
        await Expect(await PasswordSignInAsync(h, a.Email, TestHost.Password), HttpStatusCode.OK);
        var after = (await h.Db.RowsAsync("SELECT id, secret FROM sign_in_method WHERE account_id = $1", a.AccountId)).Single();
        Assert.Equal(id, (long)after[0]!);
        Assert.NotEqual(secret, (string)after[1]!);
        await Expect(await Me(a.Client), HttpStatusCode.OK);
        await Expect(await PasswordSignInAsync(h, a.Email, TestHost.Password), HttpStatusCode.OK);
    }

    // FR-035: sign-ins and failures are logged with their source, never with the address.
    [Fact]
    public async Task SecurityEvents()
    {
        await using var h = await HostAsync();
        var a = await h.SignInAsync();
        await PasswordSignInAsync(h, a.Email, TestHost.Password, "192.0.2.1");
        await PasswordSignInAsync(h, a.Email, "wrong-password-0123", "192.0.2.1");
        await PasswordSignInAsync(h, "nobody@example.nl", "wrong-password-0123", "2001:db8::1");
        var rows = await h.Db.RowsAsync("SELECT type, account_id, host(source), detail::text FROM security_event ORDER BY id");
        Assert.Equal([["signIn", a.AccountId, "192.0.2.1"], ["signInFailed", a.AccountId, "192.0.2.1"], ["signInFailed", null, "2001:db8::1"]],
            rows.Select(r => r[..3]).ToList());
        Assert.Equal("""{"methodType": "password"}""", rows[0][3]);
        Assert.All(rows, r => Assert.DoesNotContain("@", (string)r[3]!));
    }

    // Research R3: X-Forwarded-For is believed only from a trusted proxy. With none configured, the peer is the client.
    [Fact]
    public async Task ForwardedForOnlyFromTrustedProxies()
    {
        async Task<string> SourceAsync(ApiHost h, string peer = "192.0.2.1")
        {
            var a = await h.SignInAsync();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/sign-in") { Content = JsonContent.Create(new { email = a.Email, password = TestHost.Password }) };
            request.Headers.Add("X-Test-Source", peer);
            request.Headers.Add("X-Forwarded-For", "203.0.113.9");
            await Expect(await h.Client.SendAsync(request, Ct), HttpStatusCode.OK);
            return (string)(await h.Db.RowsAsync("SELECT host(source) FROM security_event WHERE type = 'signIn'")).Single()[0]!;
        }
        await using (var h = await HostAsync()) Assert.Equal("192.0.2.1", await SourceAsync(h));
        await using (var h = await HostAsync(null, ("InTheKnow:Accounts:TrustedProxies:0", "192.0.2.0/24"))) Assert.Equal("203.0.113.9", await SourceAsync(h));
        // 006 R7: Kestrel listens dual-stack, so the stack's IPv4 peer can arrive IPv4-mapped.
        await using (var h = await HostAsync(null, ("InTheKnow:Accounts:TrustedProxies:0", "192.0.2.0/24"))) Assert.Equal("203.0.113.9", await SourceAsync(h, "::ffff:192.0.2.1"));
    }

    // --- US3: password reset (FR-009, FR-010) ---------------------------------------------------------------------

    static Task<HttpResponseMessage> ResetAsync(DeliveryHost d, string email) => SignUpTests.PostAsync(d, "/api/auth/password-reset", new { email });

    static Task<HttpResponseMessage> CompleteAsync(DeliveryHost d, string token, string password) =>
        SignUpTests.PostAsync(d, "/api/auth/password-reset/complete", new { token, password });

    static async Task<string> ResetTokenAsync(DeliveryHost d, string email)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await ResetAsync(d, email)).StatusCode);
        await d.NotifyAsync(2);
        return SignUpTests.TokenOf(SignUpTests.Mails(d, "reset").Last(m => m.Target == email).Message);
    }

    // US3-1, FR-012a: the same answer for every address; at most 3 links a day.
    [Fact]
    public async Task ResetRequests()
    {
        await using var d = await SignUpTests.HostAsync();
        await d.Db.ExecAsync("UPDATE account SET disabled_at = $2 WHERE id = $1", d.S2.AccountId, d.Now);
        var bodies = new List<string>();
        foreach (var email in new[] { d.S1.Email, "unknown@example.nl", d.S2.Email })
        {
            var r = await ResetAsync(d, email);
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
            bodies.Add(await r.Content.ReadAsStringAsync(Ct));
        }
        Assert.Single(bodies.Distinct());
        Assert.Equal([[d.S1.AccountId]], await d.Db.RowsAsync("SELECT account_id FROM account_message WHERE kind = 'reset'"));
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Accepted, (await ResetAsync(d, d.S1.Email)).StatusCode);
        Assert.Equal(3, await d.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'reset'"));
    }

    // US3-2, US3-3, SC-009.
    [Fact]
    public async Task CompleteReset()
    {
        await using var d = await SignUpTests.HostAsync();
        var a = d.S1;
        var h = d.H;
        var other = await h.SignInViaAsync("password", a.Email);
        var methodId = await d.Db.ScalarAsync("SELECT id FROM sign_in_method WHERE account_id = $1", a.AccountId);
        for (var i = 0; i < 5; i++) await PasswordSignInAsync(h, a.Email, "wrong-password-0123", "192.0.2.1");
        var throttle = h.Factory.Services.GetRequiredService<Throttle>();
        for (var i = 0; i < 20; i++) throttle.Fail($"account:{a.AccountId}", $"s{i}");
        Assert.NotNull(throttle.Wait($"account:{a.AccountId}", "any"));

        var older = await ResetTokenAsync(d, a.Email);
        var token = await ResetTokenAsync(d, a.Email);
        foreach (var (password, reason) in new[] { ("short", "password_length"), (await SignUpTests.CommonPasswordAsync(), "password_common"), (a.Email, "password_is_email") })
        {
            var r = await CompleteAsync(d, token, password);
            Assert.Equal((HttpStatusCode.BadRequest, reason), (r.StatusCode, await TestHost.ReasonAsync(r)));
        }
        var olderResult = await CompleteAsync(d, older, "new-password-4567");
        Assert.Equal((HttpStatusCode.BadRequest, "token_invalid"), (olderResult.StatusCode, await TestHost.ReasonAsync(olderResult)));

        await Expect(await CompleteAsync(d, token, "new-password-4567"), HttpStatusCode.NoContent);
        await Expect(await PasswordSignInAsync(h, a.Email, TestHost.Password, "198.51.100.1"), HttpStatusCode.Unauthorized, "invalid_credentials");
        await Expect(await PasswordSignInAsync(h, a.Email, "new-password-4567", "198.51.100.1"), HttpStatusCode.OK);
        foreach (var c in new[] { a.Client, other.Client }) await Expect(await Me(c), HttpStatusCode.Unauthorized, "not_signed_in");
        Assert.Equal(methodId, await d.Db.ScalarAsync("SELECT id FROM sign_in_method WHERE account_id = $1", a.AccountId)); // in place
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'passwordChanged' AND account_id = $1", a.AccountId));
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'passwordReset' AND account_id = $1", a.AccountId));

        // The throttle for the address and the account is cleared: a 6th failure from the same source is free again.
        Assert.Null(throttle.Wait($"account:{a.AccountId}", "any"));
        await Expect(await PasswordSignInAsync(h, a.Email, "wrong-password-0123", "192.0.2.1"), HttpStatusCode.Unauthorized, "invalid_credentials");

        // US3-3: single use, and 1 hour.
        await Expect(await CompleteAsync(d, token, "another-password-89"), HttpStatusCode.BadRequest, "token_invalid");
        var late = await ResetTokenAsync(d, a.Email);
        d.H.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await Expect(await CompleteAsync(d, late, "another-password-89"), HttpStatusCode.BadRequest, "token_invalid");
        await Expect(await PasswordSignInAsync(h, a.Email, "new-password-4567", "198.51.100.1"), HttpStatusCode.OK);
    }

    // US3-5: a reset confirms an unconfirmed account; FR-009: an account without a password gets one.
    [Fact]
    public async Task ResetConfirmsAndAddsAPassword()
    {
        await using var d = await SignUpTests.HostAsync();
        await SignUpTests.SignUpAsync(d, "pending@example.nl");
        await Expect(await CompleteAsync(d, await ResetTokenAsync(d, "pending@example.nl"), "chosen-password-12"), HttpStatusCode.NoContent);
        var pending = await d.H.SignInViaAsync("password", "pending@example.nl", "chosen-password-12");
        Assert.True((await pending.JsonAsync("/api/me/channels"))["items"]!.AsArray().Single()!["accountAddress"]!.GetValue<bool>());

        var keyOnly = await d.H.SignInAsync(method: "test");
        await Expect(await CompleteAsync(d, await ResetTokenAsync(d, keyOnly.Email), "chosen-password-34"), HttpStatusCode.NoContent);
        Assert.Equal(["password", "test"], (await d.Db.RowsAsync("SELECT type FROM sign_in_method WHERE account_id = $1 ORDER BY type", keyOnly.AccountId)).Select(r => r[0]));
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'methodAdded' AND account_id = $1", keyOnly.AccountId));
        await d.H.SignInViaAsync("password", keyOnly.Email, "chosen-password-34");
    }

    // --- 009: the reset link is checked before a password is typed -------------------------------------------------

    static Task<HttpResponseMessage> CheckAsync(DeliveryHost d, string? token) =>
        SignUpTests.PostAsync(d, "/api/auth/password-reset/check", new { token });

    // 009 US1, FR-002, FR-007
    [Fact]
    public async Task ResetCheckRefusesDeadLinks()
    {
        await using var d = await SignUpTests.HostAsync();
        var used = await ResetTokenAsync(d, d.S1.Email);
        await Expect(await CompleteAsync(d, used, "new-password-4567"), HttpStatusCode.NoContent);
        await Expect(await CheckAsync(d, used), HttpStatusCode.BadRequest, "token_invalid");

        var replaced = await ResetTokenAsync(d, d.S1.Email);
        var current = await ResetTokenAsync(d, d.S1.Email);
        await Expect(await CheckAsync(d, current), HttpStatusCode.NoContent);

        await SignUpTests.SignUpAsync(d, "pending@example.nl");
        await d.NotifyAsync(2);
        var confirmation = SignUpTests.TokenOf(SignUpTests.Mails(d, "confirm").Last(m => m.Target == "pending@example.nl").Message);

        var disabled = await ResetTokenAsync(d, d.S2.Email);
        await d.Db.ExecAsync("UPDATE account SET disabled_at = $2 WHERE id = $1", d.S2.AccountId, d.Now);

        foreach (var dead in new[] { "nonsense", "", confirmation, disabled })
            await Expect(await CheckAsync(d, dead), HttpStatusCode.BadRequest, "token_invalid");
        await Expect(await SignUpTests.PostAsync(d, "/api/auth/password-reset/check", new { }), HttpStatusCode.BadRequest, "token_invalid");

        d.H.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await Expect(await CheckAsync(d, current), HttpStatusCode.BadRequest, "token_invalid");
        Assert.NotEqual(replaced, current);
    }

    // 009 US2, FR-005, SC-002, SC-004
    [Fact]
    public async Task ResetCheckChangesNothing()
    {
        await using var d = await SignUpTests.HostAsync();
        var a = d.S1;
        var token = await ResetTokenAsync(d, a.Email);
        async Task<string> SnapshotAsync() => string.Join("|",
            (await d.Db.RowsAsync("SELECT id, used_at, token_expires_at FROM account_message WHERE account_id = $1 ORDER BY id", a.AccountId)).Select(r => string.Join(",", r)),
            await d.Db.ScalarAsync("SELECT count(*) FROM session WHERE account_id = $1", a.AccountId),
            await d.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE account_id = $1", a.AccountId),
            SignUpTests.Mails(d, "reset").Count);
        var before = await SnapshotAsync();
        for (var i = 0; i < 3; i++) await Expect(await CheckAsync(d, token), HttpStatusCode.NoContent);
        Assert.Equal(before, await SnapshotAsync());

        await Expect(await CompleteAsync(d, token, "new-password-4567"), HttpStatusCode.NoContent);
        await d.H.SignInViaAsync("password", a.Email, "new-password-4567");
    }
}
