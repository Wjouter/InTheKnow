using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using static InTheKnow.Api;

namespace InTheKnow;

sealed record ReauthenticateRequest(string? Password, string? Code);
sealed record SignUpRequest(string? Email, string? Password, string? Language, string? Invite, string? BotCheck);
sealed record EmailRequest(string? Email, string? BotCheck);
sealed record TokenPasswordRequest(string? Token, string? Password);
sealed record SignInRequest(string? Email, string? Password, string? BotCheck);
sealed record CodeRequest(string? Code);
sealed record PasswordRequest(string? Password);
sealed record RoleRequest(string? Role);
sealed record SignupSettingsRequest(bool? Open, List<string>? AllowedDomains);
sealed record InviteRequest(string? Kind, string? Email, string? Language, bool? GrantsAdmin, int? MaxUses, int? ValidDays);
sealed record AcceptInviteRequest(string? Token, string? Password, string? Language);

// Account routes (specs/004 contracts/api.md): /api/auth, the account's own /api/me routes and /api/admin account routes.
// Bodies bind as nullable DTO records, so a missing field gives that field's reason rather than a framework 400.
static class AccountApi
{
    // The status of each reason, from contracts/api.md. Admin role changes map their own (all 409).
    static int StatusFor(string reason) => reason switch
    {
        "not_found" => 404,
        "invalid_credentials" or "not_signed_in" => 401,
        "sign_up_by_invitation" or "not_confirmed" or "account_disabled" or "admin_required" or "two_factor_required"
            or "reauthentication_required" => 403,
        "address_taken" or "last_admin" or "use_sign_out" or "last_method" or "two_factor_on" or "setup_not_started" or "two_factor_off"
            or "demote_first" or "account_exists" or "invite_exists" or "not_named" or "method_in_use" => 409,
        "invite_invalid" => 410,
        "too_many_attempts" or "too_many_requests" => 429,
        "try_again_later" => 503,
        _ => 400,
    };

    static IResult Refuse(string reason) => reason == "not_found" ? Results.NotFound() : Problem(StatusFor(reason), reason);

    public static void MapAccountApi(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");
        var me = Me(app);

        // Public routes: the same answer whether or not the address has an account (FR-006, SC-003).
        // FR-039, US9-3: what the frontend needs to show the check. Never the secret.
        auth.MapGet("/bot-check", (BotCheck bot) => Results.Json(new { provider = bot.ProviderName, siteKey = bot.SiteKey })).AllowAnonymous();

        auth.MapPost("/sign-up", async (SignUpRequest? body, HttpContext http, Accounts accounts, BotCheck bot) =>
        {
            if (await BotGateAsync(bot, body?.BotCheck, false, http.RequestAborted) is { } refused) return refused;
            return await accounts.SignUpAsync(body?.Email, body?.Password, body?.Language, body?.Invite, http) is { } error ? Refuse(error) : Results.StatusCode(202);
        }).AllowAnonymous().RequireRateLimiting("public");

        auth.MapPost("/confirmation", async (EmailRequest? body, HttpContext http, Accounts accounts, BotCheck bot) =>
        {
            if (await BotGateAsync(bot, body?.BotCheck, false, http.RequestAborted) is { } refused) return refused;
            await accounts.RequestConfirmationAsync(body?.Email, http.RequestAborted);
            return Results.StatusCode(202);
        }).AllowAnonymous().RequireRateLimiting("public");

        auth.MapPost("/confirm", async (TokenPasswordRequest? body, HttpContext http, Accounts accounts) =>
            await accounts.ConfirmAsync(body?.Token, body?.Password, http) switch
            {
                (null, _) => Results.NoContent(),
                (_, { } wait) => SignIn.TooManyAttempts(http, wait),
                var (error, _) => Refuse(error),
            }).AllowAnonymous().RequireRateLimiting("public");

        auth.MapPost("/sign-in", async (SignInRequest? body, HttpContext http, SignIn signIn, BotCheck bot) =>
            await BotGateAsync(bot, body?.BotCheck, true, http.RequestAborted) ?? await signIn.PasswordAsync(body?.Email, body?.Password, http))
            .AllowAnonymous();

        auth.MapPost("/password-reset", async (EmailRequest? body, HttpContext http, Accounts accounts, BotCheck bot) =>
        {
            if (await BotGateAsync(bot, body?.BotCheck, false, http.RequestAborted) is { } refused) return refused;
            await accounts.RequestResetAsync(body?.Email, http.RequestAborted);
            return Results.StatusCode(202);
        }).AllowAnonymous().RequireRateLimiting("public");

        auth.MapPost("/password-reset/complete", async (TokenPasswordRequest? body, HttpContext http, Accounts accounts) =>
            await accounts.CompleteResetAsync(body?.Token, body?.Password, http) is { } error ? Refuse(error) : Results.NoContent())
            .AllowAnonymous().RequireRateLimiting("public");

        // 009: side-effect free, so the reset page can show a dead link before a password is typed.
        auth.MapPost("/password-reset/check", async (TokenPasswordRequest? body, HttpContext http, Accounts accounts) =>
            await accounts.ResetUsableAsync(body?.Token, http.RequestAborted) ? Results.NoContent() : Refuse("token_invalid"))
            .AllowAnonymous().RequireRateLimiting("public");

        // The code step of a sign-in with 2FA on: only for an awaiting session (research R6).
        auth.MapPost("/sign-in/code", (ClaimsPrincipal user, CodeRequest? body, HttpContext http, SignIn signIn) =>
            user.HasClaim(c => c.Type == Claims.Awaiting) ? signIn.CodeAsync(user.SessionId(), user.AccountId(), body?.Code, http)
            : Task.FromResult(Problem(400, "code_invalid")))
            .RequireAuthorization("awaitingOrSignedIn").WithMetadata(new AllowAwaitingCode());

        auth.MapPost("/sign-out", async (ClaimsPrincipal user, HttpContext http, NpgsqlDataSource db, TimeProvider time) =>
        {
            await EndSessionsAsync(db, "DELETE FROM session WHERE id = $1", user.SessionId(), "signedOut", user.AccountId(), time, http);
            return Results.NoContent();
        }).RequireAuthorization("awaitingOrSignedIn").WithMetadata(new AllowAwaitingCode());

        auth.MapPost("/sign-out-everywhere", async (ClaimsPrincipal user, HttpContext http, NpgsqlDataSource db, TimeProvider time) =>
        {
            await EndSessionsAsync(db, "DELETE FROM session WHERE account_id = $1", user.AccountId(), "signedOutEverywhere", user.AccountId(), time, http);
            return Results.NoContent();
        });

        app.MapGet("/api/me", async (ClaimsPrincipal user, NpgsqlDataSource db, TimeProvider time, CancellationToken ct) =>
        {
            await using var cmd = db.CreateCommand("""
                SELECT a.email, a.role, a.language, coalesce(t.enabled_at IS NOT NULL, false),
                       (SELECT count(*) FROM recovery_code c WHERE c.account_id = a.id AND c.used_at IS NULL),
                       EXISTS (SELECT 1 FROM sign_in_method m WHERE m.account_id = a.id AND m.type = 'password'),
                       (SELECT to_address FROM account_message m WHERE m.account_id = a.id AND m.kind = 'emailChange' AND m.sent_at IS NOT NULL
                          AND m.used_at IS NULL AND m.token_expires_at > $2 ORDER BY m.id DESC LIMIT 1),
                       a.created_at, a.home_page
                FROM account a LEFT JOIN two_factor t ON t.account_id = a.id WHERE a.id = $1
                """);
            cmd.Parameters.Add(new() { Value = user.AccountId() });
            cmd.Parameters.Add(new() { Value = time.GetUtcNow() });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return Results.Json(new
            {
                email = r.GetString(0),
                role = r.GetString(1),
                language = r.GetString(2),
                twoFactor = r.GetBoolean(3),
                recoveryCodesLeft = r.GetInt64(4),
                hasPassword = r.GetBoolean(5),
                pendingEmail = r.IsDBNull(6) ? null : r.GetString(6),
                createdAt = r.GetFieldValue<DateTimeOffset>(7).UtcDateTime,
                homePage = r.GetString(8),
            });
        });

        // Display preferences that follow the account (005 contracts/api.md § 2). No step-up: nothing sensitive.
        me.MapPut("/settings", async (ClaimsPrincipal user, JsonNode? body, NpgsqlDataSource db, CancellationToken ct) =>
        {
            if (body is not JsonObject fields || !fields.ContainsKey("homePage")) return Problem(400, "empty_body");
            if (fields.Any(f => f.Key != "homePage")) return Problem(400, "invalid_settings");
            if (fields["homePage"] is not JsonValue v || !v.TryGetValue(out string? homePage) || homePage is not ("changes" or "combined"))
                return Problem(400, "invalid_home_page");
            await using var cmd = db.CreateCommand("UPDATE account SET home_page = $2 WHERE id = $1");
            cmd.Parameters.Add(new() { Value = user.AccountId() });
            cmd.Parameters.Add(new() { Value = homePage });
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.Json(new { homePage });
        });

        // Step-up re-authentication with the password, plus a code when 2FA is on (research R6).
        me.MapPost("/reauthenticate", async (ClaimsPrincipal user, ReauthenticateRequest? body, HttpContext http, NpgsqlDataSource db,
                                             IPasswordHasher<Account> hasher, SignIn signIn, Throttle throttle) =>
        {
            var id = user.AccountId();
            var key = $"account:{id}";
            if (await signIn.CheckAsync(key, http) is { } wait) return SignIn.TooManyAttempts(http, wait);
            await using var cmd = db.CreateCommand("SELECT id, secret FROM sign_in_method WHERE account_id = $1 AND type = 'password'");
            cmd.Parameters.Add(new() { Value = id });
            (long Id, string Secret)? method = null;
            await using (var r = await cmd.ExecuteReaderAsync(http.RequestAborted))
                if (await r.ReadAsync(http.RequestAborted)) method = (r.GetInt64(0), r.GetString(1));
            var verified = method is { } m && body?.Password is { } password
                ? hasher.VerifyHashedPassword(null!, m.Secret, password) : PasswordVerificationResult.Failed;
            if (verified == PasswordVerificationResult.Failed)
            {
                await signIn.FailAsync(key, http, id);
                return Problem(401, "invalid_credentials");
            }
            if (await signIn.StepUpCodeAsync(id, body?.Code, http) is { } refused) return refused;
            throttle.Succeed(key, SignIn.Source(http));
            await signIn.ReauthenticatedAsync(user.SessionId(), method!.Value.Id, http);
            return Results.NoContent();
        });

        // Managing the account (US5). The sensitive actions need a step-up re-authentication (research R6).
        me.MapPut("/password", async (ClaimsPrincipal user, PasswordRequest? body, HttpContext http, Accounts accounts) =>
            await accounts.ChangePasswordAsync(user.AccountId(), user.SessionId(), body?.Password, http) is { } error ? Refuse(error) : Results.NoContent())
            .RequireAuthorization("stepUp");

        me.MapPost("/email", async (ClaimsPrincipal user, EmailRequest? body, HttpContext http, Accounts accounts) =>
            await accounts.RequestEmailChangeAsync(user.AccountId(), body?.Email, http) is { } error ? Refuse(error) : Results.StatusCode(202))
            .RequireAuthorization("stepUp");

        app.MapDelete("/api/me", async (ClaimsPrincipal user, HttpContext http, Accounts accounts) =>
            await accounts.DeleteAsync(user.AccountId(), null, http.Connection.RemoteIpAddress, http.RequestAborted) is { } error
                ? Refuse(error) : Results.NoContent())
            .RequireAuthorization("stepUp");

        me.MapGet("/sessions", async (ClaimsPrincipal user, Accounts accounts, CancellationToken ct) =>
            Results.Json(new { items = await accounts.ListSessionsAsync(user.AccountId(), user.SessionId(), ct) }));

        me.MapDelete("/sessions/{id:long}", async (ClaimsPrincipal user, long id, HttpContext http, Accounts accounts) =>
            await accounts.EndSessionAsync(user.AccountId(), user.SessionId(), id, http) is { } error ? Refuse(error) : Results.NoContent());

        auth.MapPost("/email-change/confirm", async (TokenPasswordRequest? body, HttpContext http, Accounts accounts) =>
            await accounts.ConfirmEmailChangeAsync(body?.Token, http) is { } error ? Refuse(error) : Results.NoContent())
            .AllowAnonymous().RequireRateLimiting("public");

        // Sign-in methods (FR-027a): listing and removing work for every type.
        me.MapGet("/methods", async (ClaimsPrincipal user, Accounts accounts, CancellationToken ct) =>
            Results.Json(new { items = await accounts.ListMethodsAsync(user.AccountId(), ct) }));

        me.MapDelete("/methods/{id:long}", async (ClaimsPrincipal user, long id, HttpContext http, Accounts accounts) =>
            await accounts.RemoveMethodAsync(user.AccountId(), id, http) is { } error ? Refuse(error) : Results.NoContent())
            .RequireAuthorization("stepUp");

        // Admin account actions (FR-019, FR-033a, FR-033c). Each writes a security event with the acting admin.
        var admin = Admin(app);
        admin.MapGet("/accounts", async (string? q, long? after, int? limit, Accounts accounts, CancellationToken ct) =>
        {
            if (limit is < 1 or > 500) return Problem(400, "invalid_limit");
            var take = limit ?? 100;
            var (items, more) = await accounts.ListAccountsAsync(q, after ?? 0, take, ct);
            return Results.Json(new { items = items.Take(take).Select(i => i.Json), next = more ? items[take - 1].Id : (long?)null });
        });

        admin.MapPut("/accounts/{id:long}/role", async (ClaimsPrincipal user, long id, RoleRequest? body, HttpContext http, Accounts accounts) =>
        {
            if (body?.Role is not ("admin" or "user")) return Problem(400, "invalid_role");
            return await accounts.SetRoleAsync(user.AccountId(), id, body.Role, http) switch
            {
                null => Results.Json(await accounts.GetAccountAsync(id, http.RequestAborted)),
                "not_found" => Results.NotFound(),
                var error => Problem(409, error), // every refusal here is a conflict with the account's state
            };
        });

        foreach (var (action, disabled) in new[] { ("disable", true), ("enable", false) })
            admin.MapPost($"/accounts/{{id:long}}/{action}", async (ClaimsPrincipal user, long id, HttpContext http, Accounts accounts) =>
                await accounts.SetDisabledAsync(user.AccountId(), id, disabled, http) is { } error ? (error == "not_confirmed" ? Problem(409, error) : Refuse(error))
                : Results.Json(await accounts.GetAccountAsync(id, http.RequestAborted)));

        admin.MapDelete("/accounts/{id:long}", async (ClaimsPrincipal user, long id, HttpContext http, Accounts accounts) =>
            await accounts.AdminDeleteAsync(user.AccountId(), id, http) is { } error ? Refuse(error) : Results.NoContent());

        admin.MapDelete("/accounts/{id:long}/2fa", async (ClaimsPrincipal user, long id, HttpContext http, Accounts accounts) =>
            await accounts.RemoveTwoFactorAsync(user.AccountId(), id, http) is { } error ? Refuse(error) : Results.NoContent());

        // Who may sign up (FR-001a to FR-001e).
        admin.MapGet("/signup-settings", async (Accounts accounts, CancellationToken ct) => Results.Json(await accounts.GetSignupSettingsAsync(ct)));

        admin.MapPut("/signup-settings", async (ClaimsPrincipal user, SignupSettingsRequest? body, HttpContext http, Accounts accounts) =>
        {
            if (body?.Open is not { } open || body.AllowedDomains is not { } domains) return Problem(400, "invalid_settings");
            return await accounts.SetSignupSettingsAsync(user.AccountId(), open, domains, http) is { } error
                ? Refuse(error) : Results.Json(await accounts.GetSignupSettingsAsync(http.RequestAborted));
        });

        admin.MapPost("/invites", async (ClaimsPrincipal user, InviteRequest? body, HttpContext http, Accounts accounts) =>
        {
            var (invite, error) = await accounts.CreateInviteAsync(user.AccountId(), body?.Kind, body?.Email, body?.Language, body?.GrantsAdmin ?? false,
                body?.MaxUses, body?.ValidDays, http);
            return error is null ? Results.Json(invite, statusCode: 201) : Refuse(error);
        });

        admin.MapGet("/invites", async (Accounts accounts, CancellationToken ct) => Results.Json(new { items = await accounts.ListInvitesAsync(ct) }));

        admin.MapDelete("/invites/{id:long}", async (ClaimsPrincipal user, long id, HttpContext http, Accounts accounts) =>
            await accounts.RevokeInviteAsync(user.AccountId(), id, http) is { } error ? Refuse(error) : Results.NoContent());

        admin.MapPost("/invites/{id:long}/resend", async (long id, Accounts accounts, CancellationToken ct) =>
            await accounts.ResendInviteAsync(id, ct) is { } error ? Refuse(error) : Results.StatusCode(202));

        auth.MapPost("/invite/accept", (AcceptInviteRequest? body, HttpContext http, Accounts accounts) =>
            accounts.AcceptInviteAsync(body?.Token, body?.Password, body?.Language, http))
            .AllowAnonymous().RequireRateLimiting("public");

        // Two-factor authentication (FR-015 to FR-020). The secret and the codes appear in these responses only (FR-034).
        me.MapPost("/2fa/setup", async (ClaimsPrincipal user, Accounts accounts, CancellationToken ct) =>
        {
            var (secret, uri, error) = await accounts.StartTwoFactorAsync(user.AccountId(), ct);
            return error is null ? Results.Json(new { secret, uri }) : Refuse(error);
        });

        me.MapPost("/2fa/enable", async (ClaimsPrincipal user, CodeRequest? body, HttpContext http, Accounts accounts) =>
        {
            var (codes, error) = await accounts.EnableTwoFactorAsync(user.AccountId(), user.SessionId(), body?.Code, http);
            return error is null ? Results.Json(new { recoveryCodes = codes }) : Refuse(error);
        });

        me.MapDelete("/2fa", async (ClaimsPrincipal user, HttpContext http, Accounts accounts) =>
            await accounts.DisableTwoFactorAsync(user.AccountId(), user.SessionId(), http) is { } error ? Refuse(error) : Results.NoContent())
            .RequireAuthorization("stepUp");

        // A current code is guessable in 10^6, so failures are throttled like the code step.
        me.MapPost("/2fa/recovery-codes", async (ClaimsPrincipal user, CodeRequest? body, HttpContext http, Accounts accounts, SignIn signIn, Throttle throttle) =>
        {
            var key = $"account:{user.AccountId()}";
            if (await signIn.CheckAsync(key, http) is { } wait) return SignIn.TooManyAttempts(http, wait);
            var (codes, error) = await accounts.RenewRecoveryCodesAsync(user.AccountId(), body?.Code, http);
            if (error == "code_invalid") await signIn.FailAsync(key, http, user.AccountId(), "codeFailed");
            else if (error is null) throttle.Succeed(key, SignIn.Source(http));
            return error is null ? Results.Json(new { recoveryCodes = codes }) : Refuse(error);
        });
    }

    // FR-037, FR-040: the first step of the protected routes, before anything about the request is looked at. When the
    // provider cannot be reached, sign-in continues (still throttled) and the other actions are refused.
    static async Task<IResult?> BotGateAsync(BotCheck bot, string? response, bool isSignIn, CancellationToken ct) =>
        bot.ProviderName is null ? null
        : await bot.VerifyAsync(response, ct) switch
        {
            BotCheckOutcome.Passed => null,
            BotCheckOutcome.Failed => Problem(400, "bot_check_failed"),
            _ => isSignIn ? null : Problem(503, "try_again_later"),
        };

    static async Task EndSessionsAsync(NpgsqlDataSource db, string sql, long arg, string type, long accountId, TimeProvider time, HttpContext http)
    {
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        await using var tx = await conn.BeginTransactionAsync(http.RequestAborted);
        await using (var cmd = new NpgsqlCommand(sql, conn, tx) { Parameters = { new() { Value = arg } } })
            await cmd.ExecuteNonQueryAsync(http.RequestAborted);
        await SecurityLog.WriteAsync(conn, tx, type, accountId, time.GetUtcNow(), http.Connection.RemoteIpAddress, ct: http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
    }
}
