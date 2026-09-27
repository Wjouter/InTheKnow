using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using OtpNet;
using Xunit;

namespace InTheKnow.Tests;

// specs/004 US4, FR-015 to FR-020, SC-004; the flows run for password and for the test-only method.
public class TwoFactorTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    public static TheoryData<string> Methods => SignInTests.Methods;

    internal static string Code(string secret, DateTimeOffset at) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(at.UtcDateTime);

    static async Task<(HttpStatusCode Status, JsonNode? Body)> PostAsync(HttpClient client, string path, object? body = null)
    {
        var r = body is null ? await client.PostAsync(path, null, Ct) : await client.PostAsJsonAsync(path, body, Ct);
        var text = await r.Content.ReadAsStringAsync(Ct);
        return (r.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    static async Task Expect(Task<(HttpStatusCode Status, JsonNode? Body)> call, HttpStatusCode status, string? reason = null)
    {
        var (s, body) = await call;
        Assert.Equal(status, s);
        if (reason is not null) Assert.Equal(reason, body?["reason"]?.GetValue<string>());
    }

    // Setup and enable through the routes; returns the secret and the recovery codes.
    internal static async Task<(string Secret, List<string> Codes)> EnableAsync(ApiHost h, SignedIn who)
    {
        var secret = (await PostAsync(who.Client, "/api/me/2fa/setup")).Body!["secret"]!.GetValue<string>();
        var (status, body) = await PostAsync(who.Client, "/api/me/2fa/enable", new { code = Code(secret, h.Time.GetUtcNow()) });
        Assert.Equal(HttpStatusCode.OK, status);
        return (secret, [.. body!["recoveryCodes"]!.AsArray().Select(c => c!.GetValue<string>())]);
    }

    // The method's sign-in route; returns the token and whether a code is required.
    internal static async Task<(string Token, bool Required)> SignInRawAsync(ApiHost h, string method, string email, string password = TestHost.Password)
    {
        var (status, body) = method == "password"
            ? await PostAsync(h.Client, "/api/auth/sign-in", new { email, password })
            : await PostAsync(h.Client, "/api/test-auth/sign-in", new { key = email });
        Assert.Equal(HttpStatusCode.OK, status);
        return (body!["token"]!.GetValue<string>(), body["secondFactorRequired"]!.GetValue<bool>());
    }

    internal static Task<(HttpStatusCode, JsonNode?)> ReauthAsync(SignedIn who, string method, string? code = null, string? password = null) =>
        method == "password"
            ? PostAsync(who.Client, "/api/me/reauthenticate", new { password = password ?? TestHost.Password, code })
            : PostAsync(who.Client, "/api/test-auth/reauthenticate", new { key = who.Email, code });

    // US4-1, US4-2.
    [Fact]
    public async Task SetupAndEnable()
    {
        await using var h = await SignInTests.HostAsync();
        var a = await h.SignInAsync();
        await Expect(PostAsync(a.Client, "/api/me/2fa/enable", new { code = "123456" }), HttpStatusCode.Conflict, "setup_not_started");
        var (_, setup) = await PostAsync(a.Client, "/api/me/2fa/setup");
        Assert.Equal(["secret", "uri"], setup!.AsObject().Select(kv => kv.Key));
        Assert.Matches(@"^otpauth://totp/InTheKnow:.+\?secret=[A-Z2-7]+&issuer=InTheKnow&algorithm=SHA1&digits=6&period=30$", setup["uri"]!.GetValue<string>());
        Assert.Equal(20, Base32Encoding.ToBytes(setup["secret"]!.GetValue<string>()).Length);
        Assert.False((await a.JsonAsync("/api/me"))["twoFactor"]!.GetValue<bool>());
        var secret = (await PostAsync(a.Client, "/api/me/2fa/setup")).Body!["secret"]!.GetValue<string>();
        Assert.NotEqual(setup["secret"]!.GetValue<string>(), secret);

        await Expect(PostAsync(a.Client, "/api/me/2fa/enable", new { code = Code(setup["secret"]!.GetValue<string>(), h.Time.GetUtcNow() + TimeSpan.FromHours(1)) }),
            HttpStatusCode.BadRequest, "code_invalid");
        var (status, enabled) = await PostAsync(a.Client, "/api/me/2fa/enable", new { code = Code(secret, h.Time.GetUtcNow()) });
        Assert.Equal(HttpStatusCode.OK, status);
        var codes = enabled!["recoveryCodes"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{4}){3}$", c));
        var me = await a.JsonAsync("/api/me");
        Assert.Equal((true, 10L), (me["twoFactor"]!.GetValue<bool>(), me["recoveryCodesLeft"]!.GetValue<long>()));
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'twoFactorOn' AND account_id = $1", a.AccountId));
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'twoFactorOn' AND account_id = $1", a.AccountId));
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM session WHERE id = $1 AND second_factor", a.SessionId));
        await Expect(PostAsync(a.Client, "/api/me/2fa/setup"), HttpStatusCode.Conflict, "two_factor_on");
        await Expect(PostAsync(a.Client, "/api/me/2fa/enable", new { code = Code(secret, h.Time.GetUtcNow()) }), HttpStatusCode.Conflict, "two_factor_on");
    }

    static async Task<CodeCheck> VerifyAsync(TestDb db, long account, string code, DateTimeOffset now)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var check = await TwoFactor.VerifyAsync(conn, tx, account, code, now, Ct);
        await tx.CommitAsync(Ct);
        return check;
    }

    // RFC 6238 appendix B (SHA-1), truncated to 6 digits.
    [Fact]
    public async Task RfcTestVectors()
    {
        await using var db = await TestDb.CreateAsync();
        var a = await TestHost.AccountAsync(db.DataSource);
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        await db.ExecAsync("INSERT INTO two_factor (account_id, secret, enabled_at) VALUES ($1, $2, now())", a, secret);
        foreach (var (t, expected) in new[] { (59L, "287082"), (1111111109L, "081804"), (1111111111L, "050471") })
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(t);
            Assert.Equal(expected, new Totp(secret).ComputeTotp(at.UtcDateTime));
            await db.ExecAsync("UPDATE two_factor SET last_step = 0");
            Assert.True((await VerifyAsync(db, a, expected, at)).Ok);
        }
    }

    // FR-017: ±1 step, never a step twice, and a race lets exactly one through.
    [Fact]
    public async Task StepsAndReplay()
    {
        await using var db = await TestDb.CreateAsync();
        var a = await TestHost.AccountAsync(db.DataSource);
        var secret = TwoFactor.NewSecret();
        var b32 = Base32Encoding.ToString(secret);
        await db.ExecAsync("INSERT INTO two_factor (account_id, secret, enabled_at) VALUES ($1, $2, now())", a, secret);
        var now = Harness.T0;
        var step = TimeSpan.FromSeconds(30);
        foreach (var offset in new[] { -2, 2 }) Assert.False((await VerifyAsync(db, a, Code(b32, now + offset * step), now)).Ok);
        foreach (var offset in new[] { -1, 0, 1 }) Assert.True((await VerifyAsync(db, a, Code(b32, now + offset * step), now)).Ok);
        foreach (var offset in new[] { 0, 1 }) Assert.False((await VerifyAsync(db, a, Code(b32, now + offset * step), now)).Ok);

        await db.ExecAsync("UPDATE two_factor SET last_step = 0");
        var code = Code(b32, now);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => VerifyAsync(db, a, code, now), Ct)));
        Assert.Single(results, r => r.Ok);
    }

    // US4-3, US4-4, SC-004: with 2FA on, the method alone gives only an awaiting session.
    [Theory, MemberData(nameof(Methods))]
    public async Task TwoStepSignIn(string method)
    {
        await using var h = await SignInTests.HostAsync();
        var a = await h.SignInViaAsync(method, (await h.SignInAsync(method: method)).Email);
        var (secret, _) = await EnableAsync(h, a);
        h.Time.Advance(TimeSpan.FromSeconds(30));

        var (token, required) = await SignInRawAsync(h, method, a.Email);
        Assert.True(required);
        var client = h.ClientFor(token);
        var me = await client.GetAsync("/api/me", Ct);
        Assert.Equal((HttpStatusCode.Unauthorized, "not_signed_in"), (me.StatusCode, await TestHost.ReasonAsync(me)));
        var code = Code(secret, h.Time.GetUtcNow());
        var (status, body) = await PostAsync(client, "/api/auth/sign-in/code", new { code });
        Assert.Equal((HttpStatusCode.OK, """{"secondFactorRequired":false}"""), (status, body!.ToJsonString()));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM session WHERE token_hash = $1 AND second_factor AND NOT awaiting_code", SignIn.Hash(token)));

        // US4-4: the same code at the next sign-in, within the same step, is refused.
        var (next, _) = await SignInRawAsync(h, method, a.Email);
        await Expect(PostAsync(h.ClientFor(next), "/api/auth/sign-in/code", new { code }), HttpStatusCode.BadRequest, "code_invalid");
        // A fully signed-in session has no code step.
        await Expect(PostAsync(client, "/api/auth/sign-in/code", new { code = Code(secret, h.Time.GetUtcNow() + TimeSpan.FromSeconds(30)) }),
            HttpStatusCode.BadRequest, "code_invalid");
    }

    // Right after a completed password reset, the password still gives only an awaiting session (FR-010).
    [Fact]
    public async Task ResetDoesNotBypassTwoFactor()
    {
        await using var h = await SignInTests.HostAsync();
        var a = await h.SignInAsync();
        await EnableAsync(h, a);
        var (token, hash) = SignIn.NewToken();
        await h.Db.ExecAsync("""
            INSERT INTO account_message (kind, account_id, to_address, language, requested_at, sent_at, token_hash, token_expires_at)
            VALUES ('reset', $1, $2, 'en', $3, $3, $4, $5)
            """, a.AccountId, a.Email, h.Time.GetUtcNow(), hash, h.Time.GetUtcNow() + TimeSpan.FromHours(1));
        var r = await h.Client.PostAsJsonAsync("/api/auth/password-reset/complete", new { token, password = "after-reset-pass-1" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.True((await SignInRawAsync(h, "password", a.Email, "after-reset-pass-1")).Required);
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM two_factor WHERE account_id = $1 AND enabled_at IS NOT NULL", a.AccountId));
    }

    [Fact]
    public async Task AwaitingSession()
    {
        await using var h = await SignInTests.HostAsync();
        var a = await h.SignInAsync();
        await EnableAsync(h, a);

        // Sign-out works while awaiting a code.
        var (first, _) = await SignInRawAsync(h, "password", a.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await h.ClientFor(first).PostAsync("/api/auth/sign-out", null, Ct)).StatusCode);
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM session WHERE token_hash = $1", SignIn.Hash(first)));

        // Failures keep the session and are throttled on the account: the 6th attempt waits.
        var (token, _) = await SignInRawAsync(h, "password", a.Email);
        var client = h.ClientFor(token);
        for (var i = 0; i < 5; i++) await Expect(PostAsync(client, "/api/auth/sign-in/code", new { code = "000000" }), HttpStatusCode.BadRequest, "code_invalid");
        await Expect(PostAsync(client, "/api/auth/sign-in/code", new { code = "000000" }), HttpStatusCode.TooManyRequests, "too_many_attempts");
        Assert.Equal(5, await h.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'codeFailed' AND account_id = $1", a.AccountId));

        // It expires 10 minutes after creation.
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await Expect(PostAsync(client, "/api/auth/sign-in/code", new { code = "000000" }), HttpStatusCode.Unauthorized, "not_signed_in");
    }

    // US4-5: a recovery code, with or without hyphens and in any case, works once.
    [Fact]
    public async Task RecoveryCodes()
    {
        await using var h = await SignInTests.HostAsync();
        var a = await h.SignInAsync();
        var (_, codes) = await EnableAsync(h, a);
        var (token, _) = await SignInRawAsync(h, "password", a.Email);
        var client = h.ClientFor(token);
        await Expect(PostAsync(client, "/api/auth/sign-in/code", new { code = codes[0].Replace("-", "").ToLowerInvariant() }), HttpStatusCode.OK);
        Assert.Equal(9L, (await new SignedIn(a.AccountId, 0, token, a.Email, client).JsonAsync("/api/me"))["recoveryCodesLeft"]!.GetValue<long>());
        var args = (string)(await h.Db.RowsAsync("SELECT args::text FROM account_message WHERE kind = 'recoveryCodeUsed' AND account_id = $1", a.AccountId)).Single()[0]!;
        Assert.Equal("""{"remaining": 9}""", args);
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'recoveryCodeUsed' AND account_id = $1", a.AccountId));

        var (again, _) = await SignInRawAsync(h, "password", a.Email);
        await Expect(PostAsync(h.ClientFor(again), "/api/auth/sign-in/code", new { code = codes[0] }), HttpStatusCode.BadRequest, "code_invalid");
        await Expect(PostAsync(h.ClientFor(again), "/api/auth/sign-in/code", new { code = " " + codes[1].ToLowerInvariant() + " " }), HttpStatusCode.OK);
    }

    // US4-6, FR-018: turning 2FA off needs a step-up that included a code.
    [Theory, MemberData(nameof(Methods))]
    public async Task TurnOff(string method)
    {
        await using var h = await SignInTests.HostAsync();
        var a = await h.SignInViaAsync(method, (await h.SignInAsync(method: method)).Email);
        var (secret, _) = await EnableAsync(h, a);
        var other = await h.SignInAsync(email: null); // unrelated account, unaffected
        var (second, _) = await SignInRawAsync(h, method, a.Email);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await Expect(PostAsync(h.ClientFor(second), "/api/auth/sign-in/code", new { code = Code(secret, h.Time.GetUtcNow()) }), HttpStatusCode.OK);

        var off = await a.Client.DeleteAsync("/api/me/2fa", Ct);
        Assert.Equal((HttpStatusCode.Forbidden, "reauthentication_required"), (off.StatusCode, await TestHost.ReasonAsync(off)));
        await Expect(ReauthAsync(a, method), HttpStatusCode.BadRequest, "code_required");
        await Expect(ReauthAsync(a, method, "000000"), HttpStatusCode.BadRequest, "code_invalid");
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await Expect(ReauthAsync(a, method, Code(secret, h.Time.GetUtcNow())), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.DeleteAsync("/api/me/2fa", Ct)).StatusCode);
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM two_factor WHERE account_id = $1", a.AccountId));
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM recovery_code WHERE account_id = $1", a.AccountId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.ClientFor(second).GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.Client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.Client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'twoFactorOff' AND account_id = $1", a.AccountId));
        var again = await a.Client.DeleteAsync("/api/me/2fa", Ct);
        Assert.Equal((HttpStatusCode.Conflict, "two_factor_off"), (again.StatusCode, await TestHost.ReasonAsync(again)));
    }

    // Enabling clears the step-up: the one before it had no code (FR-018).
    [Fact]
    public async Task EnableClearsStepUp()
    {
        await using var h = await SignInTests.HostAsync();
        var a = await h.SignInAsync();
        await Expect(ReauthAsync(a, "password"), HttpStatusCode.NoContent);
        await EnableAsync(h, a);
        var off = await a.Client.DeleteAsync("/api/me/2fa", Ct);
        Assert.Equal((HttpStatusCode.Forbidden, "reauthentication_required"), (off.StatusCode, await TestHost.ReasonAsync(off)));
    }

    // US4-7.
    [Fact]
    public async Task RenewRecoveryCodes()
    {
        await using var h = await SignInTests.HostAsync();
        var a = await h.SignInAsync();
        await Expect(PostAsync(a.Client, "/api/me/2fa/recovery-codes", new { code = "123456" }), HttpStatusCode.Conflict, "two_factor_off");
        var (secret, old) = await EnableAsync(h, a);
        await Expect(PostAsync(a.Client, "/api/me/2fa/recovery-codes", new { code = "000000" }), HttpStatusCode.BadRequest, "code_invalid");
        h.Time.Advance(TimeSpan.FromSeconds(30));
        var (status, body) = await PostAsync(a.Client, "/api/me/2fa/recovery-codes", new { code = Code(secret, h.Time.GetUtcNow()) });
        Assert.Equal(HttpStatusCode.OK, status);
        var fresh = body!["recoveryCodes"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.Equal(10, fresh.Count);
        Assert.Empty(fresh.Intersect(old));
        var (token, _) = await SignInRawAsync(h, "password", a.Email);
        await Expect(PostAsync(h.ClientFor(token), "/api/auth/sign-in/code", new { code = old[0] }), HttpStatusCode.BadRequest, "code_invalid");
        await Expect(PostAsync(h.ClientFor(token), "/api/auth/sign-in/code", new { code = fresh[0] }), HttpStatusCode.OK);
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'recoveryCodesRenewed' AND account_id = $1", a.AccountId));
    }
}
