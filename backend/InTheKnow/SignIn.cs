using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InTheKnow;

// The claims of a session principal (specs/004 research R2, R6).
static class Claims
{
    public const string Account = "itk:account", Session = "itk:session", Method = "itk:method",
                        SecondFactor = "itk:2fa", Reauthenticated = "itk:reauth", Awaiting = "itk:awaiting";

    public static long AccountId(this ClaimsPrincipal user) => long.Parse(user.FindFirstValue(Account)!);
    public static long SessionId(this ClaimsPrincipal user) => long.Parse(user.FindFirstValue(Session)!);
    public static long MethodId(this ClaimsPrincipal user) => long.Parse(user.FindFirstValue(Method)!);
}

// Endpoint metadata: this route also accepts an awaiting-code session (research R6).
sealed class AllowAwaitingCode;

// A requirement whose failure is refused with its own reason (contracts/api.md § Authentication).
sealed record ReasonRequirement(string Reason, Func<ClaimsPrincipal, DateTimeOffset, bool> Holds) : IAuthorizationRequirement;

sealed class ReasonHandler(TimeProvider time) : AuthorizationHandler<ReasonRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ReasonRequirement requirement)
    {
        if (requirement.Holds(context.User, time.GetUtcNow())) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

// 401 not_signed_in, or 403 with the first failed requirement's reason, in the policy's own order.
sealed class ReasonResultHandler : IAuthorizationMiddlewareResultHandler
{
    readonly AuthorizationMiddlewareResultHandler fallback = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext http, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Challenged || result.Forbidden)
        {
            var failed = result.AuthorizationFailure?.FailedRequirements.ToHashSet() ?? [];
            var reason = result.Forbidden ? policy.Requirements.OfType<ReasonRequirement>().FirstOrDefault(failed.Contains)?.Reason : null;
            await (reason is null ? Api.Problem(401, "not_signed_in") : Api.Problem(403, reason)).ExecuteAsync(http);
            return;
        }
        await fallback.HandleAsync(next, http, policy, result);
    }
}

// Sessions are opaque bearer tokens stored as SHA-256 (research R2). One indexed query per request.
sealed class SessionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
                                          NpgsqlDataSource db, Settings settings, TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "session";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();
        var now = time.GetUtcNow();
        await using var cmd = db.CreateCommand("""
            SELECT s.id, s.account_id, s.method_id, s.awaiting_code, s.second_factor, s.reauthenticated_at, s.created_at, s.last_used_at,
                   a.role, a.confirmed_at IS NOT NULL AND a.disabled_at IS NULL
            FROM session s JOIN account a ON a.id = s.account_id WHERE s.token_hash = $1
            """);
        cmd.Parameters.Add(new() { Value = SignIn.Hash(header["Bearer ".Length..]) });
        long id, account, method;
        bool awaiting, second, usable;
        DateTimeOffset? reauth;
        DateTimeOffset created, lastUsed;
        string role;
        await using (var r = await cmd.ExecuteReaderAsync(Context.RequestAborted))
        {
            if (!await r.ReadAsync(Context.RequestAborted)) return AuthenticateResult.Fail("no session");
            (id, account, method, awaiting, second) = (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetBoolean(3), r.GetBoolean(4));
            reauth = r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5);
            (created, lastUsed, role, usable) = (r.GetFieldValue<DateTimeOffset>(6), r.GetFieldValue<DateTimeOffset>(7), r.GetString(8), r.GetBoolean(9));
        }
        if (!usable || lastUsed + settings.Options.Accounts.SessionIdleLifetime <= now) return AuthenticateResult.Fail("no session");
        if (awaiting && (Context.GetEndpoint()?.Metadata.GetMetadata<AllowAwaitingCode>() is null || created + SignIn.AwaitingLifetime <= now))
            return AuthenticateResult.Fail("no session");
        if (now - lastUsed >= SignIn.TouchEvery)
        {
            await using var touch = db.CreateCommand("UPDATE session SET last_used_at = $2 WHERE id = $1");
            touch.Parameters.Add(new() { Value = id });
            touch.Parameters.Add(new() { Value = now });
            await touch.ExecuteNonQueryAsync(Context.RequestAborted);
        }

        var claims = new List<Claim>
        {
            new(Claims.Account, account.ToString()), new(Claims.Session, id.ToString()), new(Claims.Method, method.ToString()),
            new(ClaimTypes.Role, role),
        };
        if (second) claims.Add(new(Claims.SecondFactor, "true"));
        if (awaiting) claims.Add(new(Claims.Awaiting, "true"));
        if (reauth is { } at) claims.Add(new(Claims.Reauthenticated, at.ToUnixTimeMilliseconds().ToString()));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }
}

// The shared sign-in pipeline every method type calls (contracts/sign-in-method.md, research R6, R7).
// Logs account ids and reason codes only (FR-034).
sealed class SignIn(NpgsqlDataSource db, IPasswordHasher<Account> hasher, Throttle throttle, TimeProvider time, ILogger<SignIn> log)
{
    public static readonly TimeSpan AwaitingLifetime = TimeSpan.FromMinutes(10), StepUpValidity = TimeSpan.FromMinutes(10),
                                    TouchEvery = TimeSpan.FromMinutes(5);

    readonly ConcurrentDictionary<(string, string), DateTimeOffset> throttledLogged = new();

    // Verified when there is no account or no password, so an unknown address costs one hash too (SC-003).
    readonly string dummyHash = hasher.HashPassword(null!, Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));

    // Deny by default: every endpoint without its own policy needs a fully signed-in session (research R2).
    public static void Policies(AuthorizationOptions o)
    {
        static bool NotAwaiting(ClaimsPrincipal u) => !u.HasClaim(c => c.Type == Claims.Awaiting);
        o.FallbackPolicy = new AuthorizationPolicyBuilder(SessionAuthenticationHandler.SchemeName).RequireAuthenticatedUser()
            .RequireAssertion(c => NotAwaiting(c.User)).Build();
        o.AddPolicy("admin", p => p.AddAuthenticationSchemes(SessionAuthenticationHandler.SchemeName).RequireAuthenticatedUser()
            .AddRequirements(new ReasonRequirement("admin_required", (u, _) => u.IsInRole("admin") && NotAwaiting(u)),
                             new ReasonRequirement("two_factor_required", (u, _) => u.HasClaim(c => c.Type == Claims.SecondFactor))));
        o.AddPolicy("stepUp", p => p.AddAuthenticationSchemes(SessionAuthenticationHandler.SchemeName).RequireAuthenticatedUser()
            .RequireAssertion(c => NotAwaiting(c.User))
            .AddRequirements(new ReasonRequirement("reauthentication_required", (u, now) =>
                u.FindFirstValue(Claims.Reauthenticated) is { } at && DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(at)) > now - StepUpValidity)));
        o.AddPolicy("awaitingOrSignedIn", p => p.AddAuthenticationSchemes(SessionAuthenticationHandler.SchemeName).RequireAuthenticatedUser());
    }

    public async Task<IResult> CompleteAsync(long accountId, long methodId, HttpContext http, bool secondFactor = false, string? throttleKey = null)
    {
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        await using var tx = await conn.BeginTransactionAsync(http.RequestAborted);
        bool confirmed, disabled, twoFactor;
        string methodType;
        await using (var cmd = new NpgsqlCommand("""
            SELECT a.confirmed_at IS NOT NULL, a.disabled_at IS NOT NULL, t.enabled_at IS NOT NULL, m.type
            FROM account a JOIN sign_in_method m ON m.id = $2 AND m.account_id = a.id LEFT JOIN two_factor t ON t.account_id = a.id
            WHERE a.id = $1
            """, conn, tx) { Parameters = { new() { Value = accountId }, new() { Value = methodId } } })
        await using (var r = await cmd.ExecuteReaderAsync(http.RequestAborted))
        {
            if (!await r.ReadAsync(http.RequestAborted)) return Api.Problem(401, "invalid_credentials"); // removed meanwhile
            (confirmed, disabled, twoFactor, methodType) = (r.GetBoolean(0), r.GetBoolean(1), !r.IsDBNull(2) && r.GetBoolean(2), r.GetString(3));
        }
        if (!confirmed) return Api.Problem(403, "not_confirmed");
        if (disabled) return Api.Problem(403, "account_disabled");
        if (throttleKey is not null) throttle.Succeed(throttleKey, Source(http));

        await Exec(conn, tx, "UPDATE sign_in_method SET last_used_at = $2 WHERE id = $1", http.RequestAborted, methodId, now);
        var (token, hash) = NewToken();
        var awaiting = twoFactor && !secondFactor;
        await Exec(conn, tx, """
            INSERT INTO session (account_id, method_id, token_hash, device, awaiting_code, second_factor, created_at, last_used_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $7)
            """, http.RequestAborted, accountId, methodId, hash, Device(http.Request.Headers.UserAgent), awaiting, secondFactor, now);
        await SecurityLog.WriteAsync(conn, tx, "signIn", accountId, now, http.Connection.RemoteIpAddress, detail: new { methodType }, ct: http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
        return Results.Json(new { token, secondFactorRequired = awaiting });
    }

    // The password method's sign-in (FR-011, research R4, R8). Unknown address, wrong password, a password outside the
    // rules, and an account without a password all give the same 401.
    public async Task<IResult> PasswordAsync(string? email, string? password, HttpContext http)
    {
        var key = (email ?? "").Trim().ToLowerInvariant();
        if (await CheckAsync(key, http) is { } wait) return TooManyAttempts(http, wait);
        (long Account, long Method, string Secret)? found = null;
        await using (var cmd = db.CreateCommand("""
            SELECT a.id, m.id, m.secret FROM account a JOIN sign_in_method m ON m.account_id = a.id AND m.type = 'password'
            WHERE lower(a.email) = $1
            """))
        {
            cmd.Parameters.Add(new() { Value = key });
            await using var r = await cmd.ExecuteReaderAsync(http.RequestAborted);
            if (await r.ReadAsync(http.RequestAborted)) found = (r.GetInt64(0), r.GetInt64(1), r.GetString(2));
        }
        if (found is not { } f || password is not { Length: >= 12 and <= 128 })
        {
            hasher.VerifyHashedPassword(null!, dummyHash, password ?? "");
            await FailAsync(key, http);
            return Api.Problem(401, "invalid_credentials");
        }
        switch (hasher.VerifyHashedPassword(null!, f.Secret, password))
        {
            case PasswordVerificationResult.Failed:
                await FailAsync(key, http, f.Account);
                return Api.Problem(401, "invalid_credentials");
            case PasswordVerificationResult.SuccessRehashNeeded:
                await using (var update = db.CreateCommand("UPDATE sign_in_method SET secret = $2 WHERE id = $1")) // in place: its sessions stay
                {
                    update.Parameters.Add(new() { Value = f.Method });
                    update.Parameters.Add(new() { Value = hasher.HashPassword(null!, password) });
                    await update.ExecuteNonQueryAsync(http.RequestAborted);
                }
                break;
        }
        return await CompleteAsync(f.Account, f.Method, http, throttleKey: key);
    }

    public async Task FailAsync(string throttleKey, HttpContext http, long? accountId = null, string type = "signInFailed")
    {
        throttle.Fail(throttleKey, Source(http));
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        await SecurityLog.WriteAsync(conn, null, type, accountId, time.GetUtcNow(), http.Connection.RemoteIpAddress, ct: http.RequestAborted);
    }

    // The second step of a sign-in with 2FA on (research R6): the same session row becomes fully signed in.
    public async Task<IResult> CodeAsync(long sessionId, long accountId, string? code, HttpContext http)
    {
        var key = $"account:{accountId}";
        if (await CheckAsync(key, http) is { } wait) return TooManyAttempts(http, wait);
        if (!await VerifyCodeAsync(accountId, code, http, "UPDATE session SET awaiting_code = false, second_factor = true WHERE id = $1", sessionId))
        {
            await FailAsync(key, http, accountId, "codeFailed");
            return Api.Problem(400, "code_invalid");
        }
        throttle.Succeed(key, Source(http));
        return Results.Json(new { secondFactorRequired = false });
    }

    // Verifies an authenticator or recovery code and, in the same transaction, runs `then` (with $1 = its argument) and
    // writes the events. A used recovery code also queues its notice. False: wrong, reused or used up.
    public async Task<bool> VerifyCodeAsync(long accountId, string? code, HttpContext http, string? then = null, long thenArg = 0)
    {
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var check = await TwoFactor.VerifyAsync(conn, tx, accountId, code ?? "", now, ct);
        if (!check.Ok) return false;
        if (then is not null) await Exec(conn, tx, then, ct, thenArg);
        if (then is not null) await SecurityLog.WriteAsync(conn, tx, "signIn", accountId, now, http.Connection.RemoteIpAddress, detail: new { secondFactor = true }, ct: ct);
        if (check.RecoveryCode)
        {
            await Accounts.QueueNoticeAsync(conn, tx, "recoveryCodeUsed", accountId, now, ct, new { remaining = check.RecoveryCodesLeft });
            await SecurityLog.WriteAsync(conn, tx, "recoveryCodeUsed", accountId, now, http.Connection.RemoteIpAddress, ct: ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    // Whether the account has 2FA on, so a step-up must include a code.
    public async Task<bool> TwoFactorOnAsync(long accountId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT 1 FROM two_factor WHERE account_id = $1 AND enabled_at IS NOT NULL");
        cmd.Parameters.Add(new() { Value = accountId });
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    // The code part of a step-up re-authentication (research R6): null when it passes, else the refusal.
    public async Task<IResult?> StepUpCodeAsync(long accountId, string? code, HttpContext http)
    {
        if (!await TwoFactorOnAsync(accountId, http.RequestAborted)) return null;
        if (string.IsNullOrWhiteSpace(code)) return Api.Problem(400, "code_required");
        if (await VerifyCodeAsync(accountId, code, http)) return null;
        await FailAsync($"account:{accountId}", http, accountId, "codeFailed");
        return Api.Problem(400, "code_invalid");
    }

    // The wait, if the key or source must wait. The first refusal of each wait is logged.
    public async Task<TimeSpan?> CheckAsync(string throttleKey, HttpContext http)
    {
        var source = Source(http);
        if (throttle.Wait(throttleKey, source) is not { } wait) return null;
        var now = time.GetUtcNow();
        var until = now + wait;
        if (throttledLogged.TryGetValue((throttleKey, source), out var logged) && logged == until) return wait;
        if (throttledLogged.Count > 10_000)
            foreach (var (k, v) in throttledLogged) if (v <= now) throttledLogged.TryRemove(k, out _);
        throttledLogged[(throttleKey, source)] = until;
        long? accountId = throttleKey.StartsWith("account:", StringComparison.Ordinal) ? long.Parse(throttleKey["account:".Length..]) : null;
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        await SecurityLog.WriteAsync(conn, null, "throttled", accountId, now, http.Connection.RemoteIpAddress, ct: http.RequestAborted);
        log.LogInformation("Sign-in attempts throttled for {Seconds} s", (int)Math.Ceiling(wait.TotalSeconds));
        return wait;
    }

    public static IResult TooManyAttempts(HttpContext http, TimeSpan wait)
    {
        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
        return Api.Problem(429, "too_many_attempts");
    }

    // Step-up re-authentication, valid for 10 minutes in this session (research R6).
    public async Task ReauthenticatedAsync(long sessionId, long methodId, HttpContext? http = null)
    {
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand("UPDATE session SET reauthenticated_at = $2 WHERE id = $1 RETURNING account_id", conn, tx)
        { Parameters = { new() { Value = sessionId }, new() { Value = now } } };
        var accountId = (long?)await cmd.ExecuteScalarAsync();
        await Exec(conn, tx, "UPDATE sign_in_method SET last_used_at = $2 WHERE id = $1", default, methodId, now);
        await SecurityLog.WriteAsync(conn, tx, "reauthenticated", accountId, now, http?.Connection.RemoteIpAddress);
        await tx.CommitAsync();
    }

    public static string Source(HttpContext http) => Throttle.SourceKey(http.Connection.RemoteIpAddress);

    // 32 random bytes, base64url (43 characters), stored as SHA-256 (research R2).
    public static (string Token, byte[] Hash) NewToken()
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (token, Hash(token));
    }

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    // "Browser on system" (research R3). Only this label is stored, never the User-Agent.
    // ponytail: two token tables; use a UA parser only if owners cannot recognise their devices
    public static string Device(string? userAgent)
    {
        var ua = userAgent ?? "";
        string? browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("OPR/") ? "Opera" : ua.Contains("Firefox/") ? "Firefox"
            : ua.Contains("Chrome/") ? "Chrome" : ua.Contains("Safari/") ? "Safari" : null;
        string? system = ua.Contains("Windows") ? "Windows" : ua.Contains("Android") ? "Android" : ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS"
            : ua.Contains("Mac OS X") ? "macOS" : ua.Contains("Linux") ? "Linux" : null;
        return (browser, system) switch
        {
            (null, null) => "Unknown browser",
            (_, null) => browser!,
            _ => $"{browser ?? "Unknown browser"} on {system}",
        };
    }

    static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
