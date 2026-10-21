using System.Net.Mail;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using NpgsqlTypes;
using OtpNet;

namespace InTheKnow;

// One account row. Also the T of PasswordHasher<Account>, which never reads it.
sealed record Account(long Id, string Email, string Role, string Language, string? TimeZone,
                      DateTimeOffset? ConfirmedAt, DateTimeOffset? DisabledAt, DateTimeOffset CreatedAt);

// The account lifecycle (specs/004 research R1, R9, R10, R12, R13, R16). Methods return the contracts/api.md `reason` on
// refusal and null on success, or a tuple (T? Value, string? Error); "not_found" means 404. Logs account ids and reason
// codes only (FR-034).
sealed class Accounts(NpgsqlDataSource db, IPasswordHasher<Account> hasher, Throttle throttle, SignIn signIn, Settings settings,
                      TimeProvider time)
{
    static readonly TimeSpan Day = TimeSpan.FromDays(1), SecurityRetention = TimeSpan.FromDays(90), Unconfirmed = TimeSpan.FromDays(30);
    const int EmailCap = 3; // per 24 h: confirmations and resets per account, sign-up notices per address (FR-004, FR-012a)

    // A bare address of at most 254 characters, trimmed: no display name (FR-002).
    public static string? NormalizeEmail(string? s)
    {
        var t = s?.Trim();
        return t is { Length: >= 3 and <= 254 } && MailAddress.TryCreate(t, out var a) && a.Address == t && a.DisplayName == "" ? t : null;
    }

    // FR-001 to FR-006, research R10. Every accepted request answers the same (null → 202), whatever the address's account.
    public async Task<string?> SignUpAsync(string? email, string? password, string? language, string? invite, HttpContext http)
    {
        if (NormalizeEmail(email) is not { } address) return "invalid_email";
        if (language is not ("en" or "nl")) return "invalid_language";
        if (Passwords.Check(password, address) is { } rule) return rule;
        if (invite is not null)
        {
            // A shareable invite: one use per submitted sign-up, whatever the address's account (R10, US8-7).
            if (await UseShareableInviteAsync(invite, http) is not { }) return "invite_invalid";
        }
        else if (!await MaySignUpAsync(address, http.RequestAborted)) return "sign_up_by_invitation";

        var hash = hasher.HashPassword(null!, password!); // always, so every branch costs one hash (SC-003)
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await SignUpBranchAsync(address, hash, language, http.RequestAborted);
                return null;
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && attempt == 0) { } // a concurrent sign-up won: look again
        }
    }

    async Task<long?> UseShareableInviteAsync(string token, HttpContext http)
    {
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);
        await using var tx = await conn.BeginTransactionAsync(http.RequestAborted);
        var id = (long?)await ScalarAsync(conn, tx, """
            UPDATE invite SET used = used + 1 WHERE token_hash = $1 AND kind = 'shareable' AND used < max_uses AND expires_at > $2 RETURNING id
            """, http.RequestAborted, SignIn.Hash(token), now);
        if (id is null) return null;
        await SecurityLog.WriteAsync(conn, tx, "inviteUsed", null, now, http.Connection.RemoteIpAddress, detail: new { inviteId = id }, ct: http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
        return id;
    }

    // Decided from the address and the settings alone, without reading `account` (FR-001b).
    async Task<bool> MaySignUpAsync(string address, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT open OR $1 = ANY(allowed_domains) FROM signup_settings");
        cmd.Parameters.Add(new() { Value = address[(address.LastIndexOf('@') + 1)..].ToLowerInvariant() });
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    async Task SignUpBranchAsync(string address, string hash, string language, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var existing = await FindAsync(conn, tx, address, ct);
        switch (existing)
        {
            case null:
                var id = (long)(await ScalarAsync(conn, tx, "INSERT INTO account (email, language, created_at) VALUES ($1, $2, $3) RETURNING id",
                    ct, address, language, now))!;
                await ExecAsync(conn, tx, "INSERT INTO sign_in_method (account_id, type, secret, created_at) VALUES ($1, 'password', $2, $3)", ct, id, hash, now);
                await QueueAsync(conn, tx, "confirm", id, address, language, now, ct: ct);
                break;
            case { Disabled: true }:
                break; // nothing, and the same answer
            case { Confirmed: true } a:
                if (await CountAsync(conn, tx, "SELECT count(*) FROM account_message WHERE lower(to_address) = lower($1) AND kind = 'signUpAttempt' AND requested_at > $2",
                        ct, a.Email, now - Day) < EmailCap)
                    await QueueAsync(conn, tx, "signUpAttempt", a.Id, a.Email, a.Language, now, ct: ct);
                break;
            case var a:
                // Unconfirmed: the new password replaces the old one in place, and a new link is sent (FR-006).
                await ExecAsync(conn, tx, "UPDATE sign_in_method SET secret = $2 WHERE account_id = $1 AND type = 'password'", ct, a.Id, hash);
                await QueueConfirmAsync(conn, tx, a, now, ct);
                break;
        }
        await tx.CommitAsync(ct);
    }

    sealed record Found(long Id, string Email, string Language, bool Confirmed, bool Disabled);

    static async Task<Found?> FindAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string address, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id, email, language, confirmed_at IS NOT NULL, disabled_at IS NOT NULL FROM account WHERE lower(email) = lower($1) FOR UPDATE
            """, conn, tx) { Parameters = { new() { Value = address } } };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4)) : null;
    }

    // A confirmation link, unless 3 were requested for the account in the last 24 h (the sign-up email included).
    static async Task QueueConfirmAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Found a, DateTimeOffset now, CancellationToken ct)
    {
        if (await CountAsync(conn, tx, "SELECT count(*) FROM account_message WHERE account_id = $1 AND kind = 'confirm' AND requested_at > $2",
                ct, a.Id, now - Day) < EmailCap)
            await QueueAsync(conn, tx, "confirm", a.Id, a.Email, a.Language, now, ct: ct);
    }

    // FR-004: always the same answer; only an unconfirmed, not-disabled account under its cap gets a link.
    public async Task RequestConfirmationAsync(string? email, CancellationToken ct)
    {
        if (NormalizeEmail(email) is not { } address) return;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await FindAsync(conn, tx, address, ct) is { Confirmed: false } a) await QueueConfirmAsync(conn, tx, a, now, ct);
        await tx.CommitAsync(ct);
    }

    // FR-003: the link plus the account's password confirms it. A wrong password leaves the token usable, and is
    // throttled on the token's account (research R8).
    public async Task<(string? Error, TimeSpan? Wait)> ConfirmAsync(string? token, string? password, HttpContext http)
    {
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        long messageId, accountId;
        string email;
        (long Id, string Secret)? method = null;
        await using (var cmd = new NpgsqlCommand("""
            SELECT m.id, a.id, a.email, s.id, s.secret
            FROM account_message m JOIN account a ON a.id = m.account_id
            LEFT JOIN sign_in_method s ON s.account_id = a.id AND s.type = 'password'
            WHERE m.token_hash = $1 AND m.kind = 'confirm' AND m.used_at IS NULL AND m.token_expires_at > $2
              AND a.confirmed_at IS NULL AND a.disabled_at IS NULL
            """, conn) { Parameters = { new() { Value = SignIn.Hash(token ?? "") }, new() { Value = now } } })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return ("token_invalid", null);
            (messageId, accountId, email) = (r.GetInt64(0), r.GetInt64(1), r.GetString(2));
            if (!r.IsDBNull(3)) method = (r.GetInt64(3), r.GetString(4));
        }
        var key = $"account:{accountId}";
        if (await signIn.CheckAsync(key, http) is { } wait) return ("too_many_attempts", wait);
        if (!await VerifyAsync(conn, method, password, ct))
        {
            await signIn.FailAsync(key, http, accountId);
            return ("password_incorrect", null);
        }
        throttle.Succeed(key, SignIn.Source(http));

        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await CountAsync(conn, tx, "WITH u AS (UPDATE account_message SET used_at = $2 WHERE id = $1 AND used_at IS NULL RETURNING 1) SELECT count(*) FROM u",
                ct, messageId, now) == 0)
            return ("token_invalid", null); // used by a concurrent request
        await ExecAsync(conn, tx, "UPDATE account SET confirmed_at = $2 WHERE id = $1", ct, accountId, now);
        await SecurityLog.WriteAsync(conn, tx, "accountConfirmed", accountId, now, http.Connection.RemoteIpAddress, ct: ct);
        await OnConfirmedAsync(conn, tx, accountId, email, now, ct);
        await tx.CommitAsync(ct);
        return (null, null);
    }

    // Verifies a password against the account's password method, rehashing it in place when the parameters changed.
    async Task<bool> VerifyAsync(NpgsqlConnection conn, (long Id, string Secret)? method, string? password, CancellationToken ct)
    {
        if (method is not { } m || password is null) return false;
        switch (hasher.VerifyHashedPassword(null!, m.Secret, password))
        {
            case PasswordVerificationResult.Failed: return false;
            case PasswordVerificationResult.SuccessRehashNeeded:
                await ExecAsync(conn, null, "UPDATE sign_in_method SET secret = $2 WHERE id = $1", ct, m.Id, hasher.HashPassword(null!, password));
                break;
        }
        return true;
    }

    // FR-009: always the same answer; a not-disabled account under its cap of 3 a day gets a link.
    public async Task RequestResetAsync(string? email, CancellationToken ct)
    {
        if (NormalizeEmail(email) is not { } address) return;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await FindAsync(conn, tx, address, ct) is { Disabled: false } a
            && await CountAsync(conn, tx, "SELECT count(*) FROM account_message WHERE account_id = $1 AND kind = 'reset' AND requested_at > $2", ct, a.Id, now - Day) < EmailCap)
            await QueueAsync(conn, tx, "reset", a.Id, a.Email, a.Language, now, ct: ct);
        await tx.CommitAsync(ct);
    }

    // The one rule for "this reset link can still be used", shared by the check and the submit (009 FR-002).
    static async Task<(long MessageId, long AccountId, string Email, string Language, bool Confirmed)?> FindResetAsync(
        NpgsqlConnection conn, string? token, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT m.id, a.id, a.email, a.language, a.confirmed_at IS NOT NULL
            FROM account_message m JOIN account a ON a.id = m.account_id
            WHERE m.token_hash = $1 AND m.kind = 'reset' AND m.used_at IS NULL AND m.token_expires_at > $2 AND a.disabled_at IS NULL
            """, conn) { Parameters = { new() { Value = SignIn.Hash(token ?? "") }, new() { Value = now } } };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetBoolean(4)) : null;
    }

    // 009: a pure read, so a link scanner or a page open never uses the link up.
    public async Task<bool> ResetUsableAsync(string? token, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await FindResetAsync(conn, token, time.GetUtcNow(), ct) is not null;
    }

    // FR-009, FR-010: a new password (added when there was none), every session ends, the throttle is cleared, and an
    // unconfirmed account is confirmed. 2FA is untouched.
    public async Task<string?> CompleteResetAsync(string? token, string? password, HttpContext http)
    {
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        if (await FindResetAsync(conn, token, now, ct) is not var (messageId, accountId, email, language, confirmed)) return "token_invalid";
        if (Passwords.Check(password, email) is { } rule) return rule;

        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await CountAsync(conn, tx, "WITH u AS (UPDATE account_message SET used_at = $2 WHERE id = $1 AND used_at IS NULL RETURNING 1) SELECT count(*) FROM u",
                ct, messageId, now) == 0)
            return "token_invalid";
        await SetPasswordAsync(conn, tx, accountId, email, language, password!, now, http, ct);
        if (!confirmed)
        {
            await ExecAsync(conn, tx, "UPDATE account SET confirmed_at = $2 WHERE id = $1", ct, accountId, now);
            await SecurityLog.WriteAsync(conn, tx, "accountConfirmed", accountId, now, http.Connection.RemoteIpAddress, ct: ct);
            await OnConfirmedAsync(conn, tx, accountId, email, now, ct);
        }
        await ExecAsync(conn, tx, "DELETE FROM session WHERE account_id = $1", ct, accountId);
        await QueueAsync(conn, tx, "passwordChanged", accountId, email, language, now, ct: ct);
        await SecurityLog.WriteAsync(conn, tx, "passwordReset", accountId, now, http.Connection.RemoteIpAddress, ct: ct);
        await tx.CommitAsync(ct);
        throttle.Reset(email.ToLowerInvariant());
        throttle.Reset($"account:{accountId}");
        return null;
    }

    // The password method's secret, updated in place (its sessions cascade from the row), or a new method.
    async Task SetPasswordAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, string email, string language, string password,
                                DateTimeOffset now, HttpContext http, CancellationToken ct)
    {
        var hash = hasher.HashPassword(null!, password);
        if (await CountAsync(conn, tx, "WITH u AS (UPDATE sign_in_method SET secret = $2 WHERE account_id = $1 AND type = 'password' RETURNING 1) SELECT count(*) FROM u",
                ct, accountId, hash) > 0)
            return;
        await ExecAsync(conn, tx, "INSERT INTO sign_in_method (account_id, type, secret, created_at) VALUES ($1, 'password', $2, $3)", ct, accountId, hash, now);
        await QueueAsync(conn, tx, "methodAdded", accountId, email, language, now, new { type = "password" }, ct: ct);
        await SecurityLog.WriteAsync(conn, tx, "methodAdded", accountId, now, http.Connection.RemoteIpAddress, detail: new { methodType = "password" }, ct: ct);
    }

    // --- Managing the account (US5) ---

    // FR-021, FR-013: a new password (or a first one, US6-4); every other session ends.
    public async Task<string?> ChangePasswordAsync(long accountId, long sessionId, string? password, HttpContext http)
    {
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        var (email, language) = await EmailAndLanguageAsync(conn, accountId, ct);
        if (Passwords.Check(password, email) is { } rule) return rule;
        await using var tx = await conn.BeginTransactionAsync(ct);
        await SetPasswordAsync(conn, tx, accountId, email, language, password!, now, http, ct);
        await ExecAsync(conn, tx, "DELETE FROM session WHERE account_id = $1 AND id <> $2", ct, accountId, sessionId);
        await QueueAsync(conn, tx, "passwordChanged", accountId, email, language, now, ct: ct);
        await SecurityLog.WriteAsync(conn, tx, "passwordChanged", accountId, now, http.Connection.RemoteIpAddress, ct: ct);
        await tx.CommitAsync(ct);
        return null;
    }

    // FR-022: a link to the new address and a notice to the old one. An address that another account has gets the
    // same answer and nothing happens. The pending address lives only in the email row (research R9).
    public async Task<string?> RequestEmailChangeAsync(long accountId, string? email, HttpContext http)
    {
        var ct = http.RequestAborted;
        if (NormalizeEmail(email) is not { } address) return "invalid_email";
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        var (current, language) = await EmailAndLanguageAsync(conn, accountId, ct);
        if (string.Equals(address, current, StringComparison.OrdinalIgnoreCase)) return "same_email";
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await ScalarAsync(conn, tx, "SELECT 1 FROM account WHERE lower(email) = lower($1)", ct, address) is null)
        {
            await QueueAsync(conn, tx, "emailChange", accountId, address, language, now, ct: ct);
            await QueueAsync(conn, tx, "emailChangeNotice", accountId, current, language, now, ct: ct);
            await SecurityLog.WriteAsync(conn, tx, "emailChangeRequested", accountId, now, http.Connection.RemoteIpAddress, ct: ct);
        }
        await tx.CommitAsync(ct);
        return null;
    }

    // FR-023: the link moves the account and its own channel to the new address, once.
    public async Task<string?> ConfirmEmailChangeAsync(string? token, HttpContext http)
    {
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        long accountId;
        string address;
        await using (var use = new NpgsqlCommand("""
            UPDATE account_message SET used_at = $2
            WHERE token_hash = $1 AND kind = 'emailChange' AND used_at IS NULL AND token_expires_at > $2
            RETURNING account_id, to_address
            """, conn, tx) { Parameters = { new() { Value = SignIn.Hash(token ?? "") }, new() { Value = now } } })
        await using (var r = await use.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return "token_invalid";
            (accountId, address) = (r.GetInt64(0), r.GetString(1));
        }
        if (await ScalarAsync(conn, tx, "SELECT 1 FROM account WHERE lower(email) = lower($1) AND id <> $2", ct, address, accountId) is not null)
            return "address_taken"; // rolled back: the link stays unused
        try
        {
            await ExecAsync(conn, tx, "UPDATE account SET email = $2 WHERE id = $1", ct, accountId, address);
            await Channels.MoveAccountAddressAsync(conn, tx, accountId, address, ct);
            await SecurityLog.WriteAsync(conn, tx, "emailChanged", accountId, now, http.Connection.RemoteIpAddress, ct: ct);
            await tx.CommitAsync(ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return "address_taken"; }
        return null;
    }

    // FR-014a: the active sessions; awaiting-code ones are not sessions yet. No token or address is included.
    public async Task<List<object>> ListSessionsAsync(long accountId, long currentSessionId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT id, device, created_at, last_used_at FROM session WHERE account_id = $1 AND NOT awaiting_code ORDER BY id");
        cmd.Parameters.Add(new() { Value = accountId });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var items = new List<object>();
        while (await r.ReadAsync(ct))
            items.Add(new
            {
                id = r.GetInt64(0),
                device = r.GetString(1),
                createdAt = r.GetFieldValue<DateTimeOffset>(2).UtcDateTime,
                lastUsedAt = r.GetFieldValue<DateTimeOffset>(3).UtcDateTime,
                current = r.GetInt64(0) == currentSessionId,
            });
        return items;
    }

    // US5-7: another session of this account ends; the current one signs out instead.
    public async Task<string?> EndSessionAsync(long accountId, long currentSessionId, long id, HttpContext http)
    {
        if (id == currentSessionId) return "use_sign_out";
        var ct = http.RequestAborted;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await CountAsync(conn, tx, "WITH d AS (DELETE FROM session WHERE id = $1 AND account_id = $2 RETURNING 1) SELECT count(*) FROM d", ct, id, accountId) == 0)
            return "not_found";
        await SecurityLog.WriteAsync(conn, tx, "sessionEnded", accountId, time.GetUtcNow(), http.Connection.RemoteIpAddress, ct: ct);
        await tx.CommitAsync(ct);
        return null;
    }

    // FR-024, research R1: the account and everything it owns, in one transaction; the cascades remove its rows, and
    // calendars left without subscribers go too. The final notice is the only personal data left, until it is sent.
    public async Task<string?> DeleteAsync(long accountId, long? actorId, System.Net.IPAddress? source, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var (email, language) = await EmailAndLanguageAsync(conn, accountId, ct, tx);
        var calendars = new List<long>();
        await using (var cmd = new NpgsqlCommand("SELECT DISTINCT calendar_id FROM calendar_link WHERE subscriber = $1", conn, tx)
        { Parameters = { new() { Value = accountId } } })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) calendars.Add(r.GetInt64(0));
        await QueueAsync(conn, tx, "deleted", null, email, language, now, ct: ct);
        await SecurityLog.WriteAsync(conn, tx, "accountDeleted", accountId, now, source, actorId, ct: ct);
        try { await ExecAsync(conn, tx, "DELETE FROM account WHERE id = $1", ct, accountId); }
        catch (PostgresException e) when (e is { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: "last_admin" }) { return "last_admin"; }
        await ExecAsync(conn, tx, "DELETE FROM calendar c WHERE c.id = ANY($1) AND NOT EXISTS (SELECT 1 FROM calendar_link WHERE calendar_id = c.id)",
            ct, calendars.ToArray());
        await tx.CommitAsync(ct);
        return null;
    }

    static async Task<(string Email, string Language)> EmailAndLanguageAsync(NpgsqlConnection conn, long accountId, CancellationToken ct,
                                                                               NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand("SELECT email, language FROM account WHERE id = $1", conn, tx) { Parameters = { new() { Value = accountId } } };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetString(0), r.GetString(1));
    }

    // --- Admins (FR-019, FR-032 to FR-033c, research R12, R13). Each action queues its email and logs the acting admin. ---

    const string AccountColumns = "a.id, a.email, a.role, a.confirmed_at IS NOT NULL, a.disabled_at IS NOT NULL, coalesce(t.enabled_at IS NOT NULL, false), a.created_at";

    static object AccountJson(NpgsqlDataReader r) => new
    {
        id = r.GetInt64(0),
        email = r.GetString(1),
        role = r.GetString(2),
        confirmed = r.GetBoolean(3),
        disabled = r.GetBoolean(4),
        twoFactor = r.GetBoolean(5),
        createdAt = r.GetFieldValue<DateTimeOffset>(6).UtcDateTime,
    };

    // FR-033c, US7-12: a case-insensitive substring of the address, keyset paged.
    public async Task<(List<(long Id, object Json)> Items, bool More)> ListAccountsAsync(string? q, long after, int limit, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT {AccountColumns} FROM account a LEFT JOIN two_factor t ON t.account_id = a.id
            WHERE a.id > $1 AND strpos(lower(a.email), lower($2)) > 0 ORDER BY a.id LIMIT $3
            """);
        cmd.Parameters.Add(new() { Value = after });
        cmd.Parameters.Add(new() { Value = q ?? "" });
        cmd.Parameters.Add(new() { Value = limit + 1 });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var items = new List<(long, object)>();
        while (await r.ReadAsync(ct)) items.Add((r.GetInt64(0), AccountJson(r)));
        return (items, items.Count > limit);
    }

    public async Task<object?> GetAccountAsync(long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT {AccountColumns} FROM account a LEFT JOIN two_factor t ON t.account_id = a.id WHERE a.id = $1");
        cmd.Parameters.Add(new() { Value = id });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? AccountJson(r) : null;
    }

    sealed record Target(string Role, bool Confirmed, bool Disabled, bool TwoFactor);

    static async Task<Target?> LockTargetAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT a.role, a.confirmed_at IS NOT NULL, a.disabled_at IS NOT NULL, coalesce(t.enabled_at IS NOT NULL, false)
            FROM account a LEFT JOIN two_factor t ON t.account_id = a.id WHERE a.id = $1 FOR UPDATE OF a
            """, conn, tx) { Parameters = { new() { Value = id } } };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3)) : null;
    }

    // FR-033a: promotion needs a confirmed, enabled account with 2FA on (FR-032a); the last admin stays (US7-7).
    public async Task<string?> SetRoleAsync(long actorId, long id, string role, HttpContext http)
    {
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await LockTargetAsync(conn, tx, id, ct) is not { } target) return "not_found";
        if (role == "admin")
        {
            if (!target.Confirmed) return "not_confirmed";
            if (target.Disabled) return "account_disabled";
            if (!target.TwoFactor) return "two_factor_required";
        }
        else if (target.Role == "admin" && await CountAsync(conn, tx, "SELECT count(*) FROM account WHERE role = 'admin'", ct) <= 1) return "last_admin";
        if (target.Role == role) return null;
        try { await ExecAsync(conn, tx, "UPDATE account SET role = $2 WHERE id = $1", ct, id, role); }
        catch (PostgresException e) when (e is { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: "last_admin" }) { return "last_admin"; }
        await QueueNoticeAsync(conn, tx, "roleChanged", id, now, ct, new { role });
        await SecurityLog.WriteAsync(conn, tx, "roleChanged", id, now, http.Connection.RemoteIpAddress, actorId, new { role }, ct);
        await tx.CommitAsync(ct);
        return null;
    }

    // FR-033c: disabling ends the sessions, drops the account's queued channel messages, and stops its notifications.
    // Disable, enable and delete apply to regular accounts other than one's own (US7-15).
    public async Task<string?> SetDisabledAsync(long actorId, long id, bool disabled, HttpContext http)
    {
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await LockTargetAsync(conn, tx, id, ct) is not { } target) return "not_found";
        if (target.Role == "admin" || id == actorId) return "demote_first";
        if (disabled && !target.Confirmed) return "not_confirmed";
        if (target.Disabled == disabled) return null;
        await ExecAsync(conn, tx, "UPDATE account SET disabled_at = $2 WHERE id = $1", ct, id, disabled ? now : DBNull.Value);
        if (disabled)
        {
            await ExecAsync(conn, tx, "DELETE FROM session WHERE account_id = $1", ct, id);
            await ExecAsync(conn, tx, "DELETE FROM channel_message m USING channel c WHERE m.channel_id = c.id AND c.subscriber = $1 AND m.sent_at IS NULL", ct, id);
        }
        await QueueNoticeAsync(conn, tx, disabled ? "disabled" : "enabled", id, now, ct);
        await SecurityLog.WriteAsync(conn, tx, disabled ? "accountDisabled" : "accountEnabled", id, now, http.Connection.RemoteIpAddress, actorId, ct: ct);
        await tx.CommitAsync(ct);
        return null;
    }

    public async Task<string?> AdminDeleteAsync(long actorId, long id, HttpContext http)
    {
        await using (var conn = await db.OpenConnectionAsync(http.RequestAborted))
        {
            var role = await ScalarAsync(conn, null, "SELECT role FROM account WHERE id = $1", http.RequestAborted, id);
            if (role is null) return "not_found";
            if (role is "admin" || id == actorId) return "demote_first";
        }
        return await DeleteAsync(id, actorId, http.Connection.RemoteIpAddress, http.RequestAborted);
    }

    // FR-019: an admin removes an account's 2FA; every session of it ends. Not one's own (FR-032a).
    public async Task<string?> RemoveTwoFactorAsync(long actorId, long id, HttpContext http)
    {
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await LockTargetAsync(conn, tx, id, ct) is not { } target) return "not_found";
        if (id == actorId) return "demote_first";
        if (!target.TwoFactor) return "two_factor_off";
        await ExecAsync(conn, tx, "DELETE FROM two_factor WHERE account_id = $1", ct, id);
        await ExecAsync(conn, tx, "DELETE FROM session WHERE account_id = $1", ct, id);
        await QueueNoticeAsync(conn, tx, "twoFactorOffByAdmin", id, now, ct);
        await SecurityLog.WriteAsync(conn, tx, "twoFactorRemovedByAdmin", id, now, http.Connection.RemoteIpAddress, actorId, ct: ct);
        await tx.CommitAsync(ct);
        return null;
    }

    // A named invite (FR-001c). An expired one for the address is replaced; the token is minted when the email is sent.
    public static async Task<long> CreateNamedInviteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string email, string language, bool grantsAdmin,
                                                          int validDays, long? createdBy, DateTimeOffset now, CancellationToken ct)
    {
        await ExecAsync(conn, tx, "DELETE FROM invite WHERE kind = 'named' AND lower(email) = lower($1) AND expires_at <= $2", ct, email, now);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO invite (kind, email, language, grants_admin, max_uses, created_by, created_at, expires_at)
            VALUES ('named', $1, $2, $3, 1, $4, $5, $6) RETURNING id
            """, conn, tx);
        foreach (var v in new object[] { email, language, grantsAdmin }) insert.Parameters.Add(new() { Value = v });
        insert.Parameters.Add(new() { Value = (object?)createdBy ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        insert.Parameters.Add(new() { Value = now });
        insert.Parameters.Add(new() { Value = now + TimeSpan.FromDays(validDays) });
        var id = (long)(await insert.ExecuteScalarAsync(ct))!;
        await QueueAsync(conn, tx, "invite", null, email, language, now, new { admin = grantsAdmin }, id, ct);
        return id;
    }

    // Queues a named invite's email again, from now on for the same validity; its send replaces the token (R10, R13).
    static async Task RenewInviteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long id, TimeSpan? validity, DateTimeOffset now, CancellationToken ct)
    {
        await ExecAsync(conn, tx, "DELETE FROM account_message WHERE invite_id = $1 AND sent_at IS NULL", ct, id);
        await using var renew = new NpgsqlCommand("""
            UPDATE invite SET created_at = $2, expires_at = $2 + coalesce($3, expires_at - created_at) WHERE id = $1 RETURNING email, language, grants_admin
            """, conn, tx);
        renew.Parameters.Add(new() { Value = id });
        renew.Parameters.Add(new() { Value = now });
        renew.Parameters.Add(new() { Value = (object?)validity ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Interval });
        string email, language;
        bool admin;
        await using (var r = await renew.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            (email, language, admin) = (r.GetString(0), r.GetString(1), r.GetBoolean(2));
        }
        await QueueAsync(conn, tx, "invite", null, email, language, now, new { admin }, id, ct);
    }

    // FR-033b, research R13: the server command. Never takes the instance lock, never prints a token.
    public static async Task<string> MakeAdminAsync(NpgsqlDataSource db, string email, string language, DateTimeOffset now)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var ct = CancellationToken.None;
        string result;
        long? accountId = null;
        if (await FindAsync(conn, tx, email, ct) is { Confirmed: true } a)
        {
            accountId = a.Id;
            if (a.Disabled)
            {
                await ExecAsync(conn, tx, "UPDATE account SET disabled_at = NULL WHERE id = $1", ct, a.Id);
                await QueueAsync(conn, tx, "enabled", a.Id, a.Email, a.Language, now, ct: ct);
                await SecurityLog.WriteAsync(conn, tx, "accountEnabled", a.Id, now, null, ct: ct);
            }
            if (await CountAsync(conn, tx, "WITH u AS (UPDATE account SET role = 'admin' WHERE id = $1 AND role <> 'admin' RETURNING 1) SELECT count(*) FROM u", ct, a.Id) > 0)
                await QueueAsync(conn, tx, "roleChanged", a.Id, a.Email, a.Language, now, new { role = "admin" }, ct: ct);
            result = "promoted";
        }
        else
        {
            if (await ScalarAsync(conn, tx, "SELECT id FROM invite WHERE kind = 'named' AND lower(email) = lower($1) AND expires_at > $2", ct, email, now) is long pending)
            {
                await ExecAsync(conn, tx, "UPDATE invite SET grants_admin = true WHERE id = $1", ct, pending);
                await RenewInviteAsync(conn, tx, pending, TimeSpan.FromDays(7), now, ct);
            }
            else await CreateNamedInviteAsync(conn, tx, email, language, true, 7, null, now, ct);
            result = "invite queued";
        }
        await SecurityLog.WriteAsync(conn, tx, "adminMadeFromServer", accountId, now, null, detail: new { via = "server" }, ct: ct);
        await tx.CommitAsync();
        return result;
    }

    // --- Who may sign up (FR-001a to FR-001e, research R10) ---

    public async Task<object> GetSignupSettingsAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT open, allowed_domains FROM signup_settings");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new { open = r.GetBoolean(0), allowedDomains = r.GetFieldValue<string[]>(1) };
    }

    public async Task<string?> SetSignupSettingsAsync(long actorId, bool open, IReadOnlyList<string> domains, HttpContext http)
    {
        var clean = new List<string>();
        foreach (var raw in domains)
        {
            var d = raw.Trim().ToLowerInvariant();
            if (Uri.CheckHostName(d) != UriHostNameType.Dns || !d.Contains('.') || d.Split('.').Any(l => l.Length == 0 || l.StartsWith('-') || l.EndsWith('-')))
                return "invalid_domain";
            if (!clean.Contains(d)) clean.Add(d);
        }
        if (clean.Count > 100) return "too_many_domains";
        var ct = http.RequestAborted;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ExecAsync(conn, tx, "UPDATE signup_settings SET open = $1, allowed_domains = $2", ct, open, clean.ToArray());
        await SecurityLog.WriteAsync(conn, tx, "signupSettingsChanged", null, time.GetUtcNow(), http.Connection.RemoteIpAddress, actorId,
            new { open, domainCount = clean.Count }, ct);
        await tx.CommitAsync(ct);
        return null;
    }

    const string InviteColumns = "id, kind, email, language, grants_admin, max_uses, used, expires_at, created_by, created_at";

    static Dictionary<string, object?> InviteJson(NpgsqlDataReader r) => new()
    {
        ["id"] = r.GetInt64(0),
        ["kind"] = r.GetString(1),
        ["email"] = r.IsDBNull(2) ? null : r.GetString(2),
        ["language"] = r.GetString(3),
        ["grantsAdmin"] = r.GetBoolean(4),
        ["maxUses"] = r.GetInt32(5),
        ["used"] = r.GetInt32(6),
        ["left"] = r.GetInt32(5) - r.GetInt32(6),
        ["expiresAt"] = r.GetFieldValue<DateTimeOffset>(7).UtcDateTime,
        ["createdBy"] = r.IsDBNull(8) ? null : r.GetInt64(8),
        ["createdAt"] = r.GetFieldValue<DateTimeOffset>(9).UtcDateTime,
    };

    // FR-001c, FR-001e. A shareable invite's link is returned in this response only.
    public async Task<(Dictionary<string, object?>? Invite, string? Error)> CreateInviteAsync(long actorId, string? kind, string? email, string? language,
        bool grantsAdmin, int? maxUses, int? validDays, HttpContext http)
    {
        if (kind is not ("named" or "shareable")) return (null, "invalid_kind");
        var address = kind == "named" ? NormalizeEmail(email) : null;
        if (kind == "named" && address is null) return (null, "invalid_email");
        if (language is not ("en" or "nl")) return (null, "invalid_language");
        if (validDays is not (null or (>= 1 and <= 90))) return (null, "invalid_valid_days");
        if (maxUses is not (null or (>= 1 and <= 1000)) || kind == "named" && maxUses is not (null or 1)) return (null, "invalid_max_uses");
        if (grantsAdmin && kind != "named") return (null, "admin_needs_named");

        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        long id;
        string? link = null;
        if (kind == "named")
        {
            if (await ScalarAsync(conn, tx, "SELECT 1 FROM account WHERE lower(email) = lower($1) AND confirmed_at IS NOT NULL", ct, address!) is not null)
                return (null, "account_exists");
            try { id = await CreateNamedInviteAsync(conn, tx, address!, language, grantsAdmin, validDays ?? 7, actorId, now, ct); }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return (null, "invite_exists"); }
        }
        else
        {
            var (token, hash) = SignIn.NewToken();
            id = (long)(await ScalarAsync(conn, tx, """
                INSERT INTO invite (kind, language, max_uses, token_hash, created_by, created_at, expires_at) VALUES ('shareable', $1, $2, $3, $4, $5, $6) RETURNING id
                """, ct, language, maxUses ?? 1, hash, actorId, now, now + TimeSpan.FromDays(validDays ?? 7)))!;
            link = $"{settings.Options.Notifications.FrontendBaseUrl}/account/sign-up?invite={token}";
        }
        await SecurityLog.WriteAsync(conn, tx, "inviteCreated", null, now, http.Connection.RemoteIpAddress, actorId, new { inviteId = id, kind, grantsAdmin }, ct);
        await tx.CommitAsync(ct);
        var json = (await InvitesAsync(conn, null, "id = $1", ct, id)).Single();
        if (link is not null) json["link"] = link;
        return (json, null);
    }

    // US8-9: the pending invites, newest first. Never a link.
    public async Task<List<Dictionary<string, object?>>> ListInvitesAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await InvitesAsync(conn, null, "used < max_uses AND expires_at > $1", ct, time.GetUtcNow());
    }

    static async Task<List<Dictionary<string, object?>>> InvitesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string where, CancellationToken ct, object arg)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {InviteColumns} FROM invite WHERE {where} ORDER BY id DESC", conn, tx) { Parameters = { new() { Value = arg } } };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var items = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync(ct)) items.Add(InviteJson(r));
        return items;
    }

    // Revoking deletes the invite: its link stops working at once, and its unsent email goes with it (cascade).
    public async Task<string?> RevokeInviteAsync(long actorId, long id, HttpContext http)
    {
        var ct = http.RequestAborted;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await CountAsync(conn, tx, "WITH d AS (DELETE FROM invite WHERE id = $1 RETURNING 1) SELECT count(*) FROM d", ct, id) == 0) return "not_found";
        await SecurityLog.WriteAsync(conn, tx, "inviteRevoked", null, time.GetUtcNow(), http.Connection.RemoteIpAddress, actorId, new { inviteId = id }, ct);
        await tx.CommitAsync(ct);
        return null;
    }

    // A new email for a named invite, valid again for its own length from now; the old link stops when it is sent.
    public async Task<string?> ResendInviteAsync(long id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        switch (await ScalarAsync(conn, tx, "SELECT kind FROM invite WHERE id = $1 FOR UPDATE", ct, id))
        {
            case null: return "not_found";
            case "shareable": return "not_named";
        }
        await RenewInviteAsync(conn, tx, id, null, time.GetUtcNow(), ct);
        await tx.CommitAsync(ct);
        return null;
    }

    // US8-6: a named invite creates a confirmed account (taking over an unconfirmed one), with admin if it grants it,
    // and signs it in. The invite goes; an address confirmed meanwhile makes it invalid.
    public async Task<IResult> AcceptInviteAsync(string? token, string? password, string? language, HttpContext http)
    {
        if (language is not (null or "en" or "nl")) return Api.Problem(400, "invalid_language");
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        long inviteId;
        string email, inviteLanguage;
        bool grantsAdmin;
        await using (var find = new NpgsqlCommand("""
            SELECT id, email, language, grants_admin FROM invite WHERE token_hash = $1 AND kind = 'named' AND expires_at > $2
            """, conn) { Parameters = { new() { Value = SignIn.Hash(token ?? "") }, new() { Value = now } } })
        await using (var r = await find.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return Api.Problem(410, "invite_invalid");
            (inviteId, email, inviteLanguage, grantsAdmin) = (r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetBoolean(3));
        }
        if (Passwords.Check(password, email) is { } rule) return Api.Problem(400, rule);
        var hash = hasher.HashPassword(null!, password!);

        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await CountAsync(conn, tx, "WITH d AS (DELETE FROM invite WHERE id = $1 RETURNING 1) SELECT count(*) FROM d", ct, inviteId) == 0)
            return Api.Problem(410, "invite_invalid"); // used by a concurrent request
        var existing = await FindAsync(conn, tx, email, ct);
        if (existing is { Confirmed: true })
        {
            await tx.CommitAsync(ct); // the invite stays deleted
            return Api.Problem(410, "invite_invalid");
        }
        long accountId;
        if (existing is { } a)
        {
            // Taking over an unconfirmed account: it holds no data, and its password is replaced in place.
            accountId = a.Id;
            await ExecAsync(conn, tx, "UPDATE account SET confirmed_at = $2, language = coalesce($3, language) WHERE id = $1",
                ct, accountId, now, (object?)language ?? DBNull.Value);
        }
        else
            accountId = (long)(await ScalarAsync(conn, tx, "INSERT INTO account (email, language, confirmed_at, created_at) VALUES ($1, $2, $3, $3) RETURNING id",
                ct, email, language ?? inviteLanguage, now))!;
        var methodId = (long?)await ScalarAsync(conn, tx, "UPDATE sign_in_method SET secret = $2 WHERE account_id = $1 AND type = 'password' RETURNING id",
                           ct, accountId, hash)
            ?? (long)(await ScalarAsync(conn, tx, "INSERT INTO sign_in_method (account_id, type, secret, created_at) VALUES ($1, 'password', $2, $3) RETURNING id",
                ct, accountId, hash, now))!;
        if (grantsAdmin) await ExecAsync(conn, tx, "UPDATE account SET role = 'admin' WHERE id = $1", ct, accountId);
        await OnConfirmedAsync(conn, tx, accountId, email, now, ct);
        await SecurityLog.WriteAsync(conn, tx, "inviteUsed", accountId, now, http.Connection.RemoteIpAddress, detail: new { inviteId }, ct: ct);
        await SecurityLog.WriteAsync(conn, tx, "accountConfirmed", accountId, now, http.Connection.RemoteIpAddress, ct: ct);
        await tx.CommitAsync(ct);
        return await signIn.CompleteAsync(accountId, methodId, http);
    }

    // --- Sign-in methods (FR-026, FR-027a, contracts/sign-in-method.md) ---

    // Adds a method of a type to the account. The caller's route has already required a step-up.
    public async Task<string?> AddMethodAsync(long accountId, string type, string? key, string? secret, HttpContext http)
    {
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using var insert = new NpgsqlCommand("INSERT INTO sign_in_method (account_id, type, key, secret, created_at) VALUES ($1, $2, $3, $4, $5)", conn, tx);
            insert.Parameters.Add(new() { Value = accountId });
            insert.Parameters.Add(new() { Value = type });
            insert.Parameters.Add(new() { Value = (object?)key ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            insert.Parameters.Add(new() { Value = (object?)secret ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            insert.Parameters.Add(new() { Value = now });
            await insert.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return "method_in_use"; }
        await QueueNoticeAsync(conn, tx, "methodAdded", accountId, now, ct, new { type });
        await SecurityLog.WriteAsync(conn, tx, "methodAdded", accountId, now, http.Connection.RemoteIpAddress, detail: new { methodType = type }, ct: ct);
        await tx.CommitAsync(ct);
        return null;
    }

    // Removes a method; its sessions end with it (FK cascade). The last one stays (a trigger, checked at commit).
    public async Task<string?> RemoveMethodAsync(long accountId, long methodId, HttpContext http)
    {
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await ScalarAsync(conn, tx, "SELECT type FROM sign_in_method WHERE id = $1 AND account_id = $2", ct, methodId, accountId) is not string type)
            return "not_found";
        if (await CountAsync(conn, tx, "SELECT count(*) FROM sign_in_method WHERE account_id = $1", ct, accountId) <= 1) return "last_method";
        await ExecAsync(conn, tx, "DELETE FROM sign_in_method WHERE id = $1", ct, methodId);
        await QueueNoticeAsync(conn, tx, "methodRemoved", accountId, now, ct, new { type });
        await SecurityLog.WriteAsync(conn, tx, "methodRemoved", accountId, now, http.Connection.RemoteIpAddress, detail: new { methodType = type }, ct: ct);
        try { await tx.CommitAsync(ct); }
        catch (PostgresException e) when (e is { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: "last_method" }) { return "last_method"; }
        return null;
    }

    public async Task<List<object>> ListMethodsAsync(long accountId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT id, type, created_at, last_used_at FROM sign_in_method WHERE account_id = $1 ORDER BY id");
        cmd.Parameters.Add(new() { Value = accountId });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var items = new List<object>();
        while (await r.ReadAsync(ct))
            items.Add(new
            {
                id = r.GetInt64(0),
                type = r.GetString(1),
                createdAt = r.GetFieldValue<DateTimeOffset>(2).UtcDateTime,
                lastUsedAt = r.IsDBNull(3) ? (DateTime?)null : r.GetFieldValue<DateTimeOffset>(3).UtcDateTime,
            });
        return items;
    }

    // --- Two-factor authentication (FR-015 to FR-020, research R5) ---

    // A new, unconfirmed secret; starting again replaces it. Returned only here (FR-034).
    public async Task<(string? Secret, string? Uri, string? Error)> StartTwoFactorAsync(long accountId, CancellationToken ct)
    {
        var secret = TwoFactor.NewSecret();
        await using var conn = await db.OpenConnectionAsync(ct);
        var email = (string)(await ScalarAsync(conn, null, "SELECT email FROM account WHERE id = $1", ct, accountId))!;
        var started = await ScalarAsync(conn, null, """
            INSERT INTO two_factor (account_id, secret) VALUES ($1, $2)
            ON CONFLICT (account_id) DO UPDATE SET secret = excluded.secret, last_step = 0 WHERE two_factor.enabled_at IS NULL RETURNING 1
            """, ct, accountId, secret);
        return started is null ? (null, null, "two_factor_on") : (Base32Encoding.ToString(secret), TwoFactor.Uri(email, secret), null);
    }

    // A valid code turns 2FA on. This session then counts as having proved a second factor (US7-11), and its step-up is
    // cleared, so turning 2FA off again needs a re-authentication that includes a code (FR-018).
    public async Task<(IReadOnlyList<string>? Codes, string? Error)> EnableTwoFactorAsync(long accountId, long sessionId, string? code, HttpContext http)
    {
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        byte[] secret;
        await using (var find = new NpgsqlCommand("SELECT secret, enabled_at IS NOT NULL FROM two_factor WHERE account_id = $1 FOR UPDATE", conn, tx)
        { Parameters = { new() { Value = accountId } } })
        await using (var r = await find.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return (null, "setup_not_started");
            if (r.GetBoolean(1)) return (null, "two_factor_on");
            secret = (byte[])r[0];
        }
        if (TwoFactor.Step(secret, code?.Trim() ?? "", now) is not { } step) return (null, "code_invalid");
        var codes = TwoFactor.NewRecoveryCodes();
        await ExecAsync(conn, tx, "UPDATE two_factor SET enabled_at = $2, last_step = $3 WHERE account_id = $1", ct, accountId, now, step);
        await InsertRecoveryCodesAsync(conn, tx, accountId, codes, ct);
        await ExecAsync(conn, tx, "UPDATE session SET second_factor = true, reauthenticated_at = NULL WHERE id = $1", ct, sessionId);
        await QueueNoticeAsync(conn, tx, "twoFactorOn", accountId, now, ct);
        await SecurityLog.WriteAsync(conn, tx, "twoFactorOn", accountId, now, http.Connection.RemoteIpAddress, ct: ct);
        await tx.CommitAsync(ct);
        return (codes, null);
    }

    // The owner turns 2FA off (after a step-up that included a code). Admins must stay protected (US7-10).
    public async Task<string?> DisableTwoFactorAsync(long accountId, long sessionId, HttpContext http)
    {
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        switch (await ScalarAsync(conn, tx, "SELECT a.role FROM account a JOIN two_factor t ON t.account_id = a.id AND t.enabled_at IS NOT NULL WHERE a.id = $1 FOR UPDATE OF t",
                    ct, accountId))
        {
            case null: return "two_factor_off";
            case "admin": return "demote_first";
        }
        await ExecAsync(conn, tx, "DELETE FROM two_factor WHERE account_id = $1", ct, accountId);
        await ExecAsync(conn, tx, "DELETE FROM session WHERE account_id = $1 AND id <> $2", ct, accountId, sessionId);
        // This session no longer proves a second factor, so a later promotion cannot open the admin routes (FR-032a).
        await ExecAsync(conn, tx, "UPDATE session SET second_factor = false WHERE id = $1", ct, sessionId);
        await QueueNoticeAsync(conn, tx, "twoFactorOff", accountId, now, ct);
        await SecurityLog.WriteAsync(conn, tx, "twoFactorOff", accountId, now, http.Connection.RemoteIpAddress, ct: ct);
        await tx.CommitAsync(ct);
        return null;
    }

    // FR-016, US4-7: a current authenticator code replaces every recovery code.
    public async Task<(IReadOnlyList<string>? Codes, string? Error)> RenewRecoveryCodesAsync(long accountId, string? code, HttpContext http)
    {
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (await ScalarAsync(conn, tx, "SELECT 1 FROM two_factor WHERE account_id = $1 AND enabled_at IS NOT NULL", ct, accountId) is null)
            return (null, "two_factor_off");
        var c = code?.Trim() ?? "";
        if (c.Length != 6 || !(await TwoFactor.VerifyAsync(conn, tx, accountId, c, now, ct)).Ok) return (null, "code_invalid");
        var codes = TwoFactor.NewRecoveryCodes();
        await ExecAsync(conn, tx, "DELETE FROM recovery_code WHERE account_id = $1", ct, accountId);
        await InsertRecoveryCodesAsync(conn, tx, accountId, codes, ct);
        await SecurityLog.WriteAsync(conn, tx, "recoveryCodesRenewed", accountId, now, http.Connection.RemoteIpAddress, ct: ct);
        await tx.CommitAsync(ct);
        return (codes, null);
    }

    static async Task InsertRecoveryCodesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, IReadOnlyList<string> codes, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO recovery_code (account_id, code_hash) SELECT $1, unnest($2::bytea[])", conn, tx)
        { Parameters = { new() { Value = accountId }, new() { Value = codes.Select(TwoFactor.HashRecoveryCode).ToArray() } } };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // A notice to the account's current address, in its language.
    internal static async Task QueueNoticeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string kind, long accountId, DateTimeOffset now,
                                                CancellationToken ct, object? args = null)
    {
        await using var cmd = new NpgsqlCommand("SELECT email, language FROM account WHERE id = $1", conn, tx) { Parameters = { new() { Value = accountId } } };
        string email, language;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            (email, language) = (r.GetString(0), r.GetString(1));
        }
        await QueueAsync(conn, tx, kind, accountId, email, language, now, args, ct: ct);
    }

    // Every confirmation path (link, reset, named invite) ends here: the account's own address channel (FR-028, R15).
    static Task OnConfirmedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, string email, DateTimeOffset now, CancellationToken ct) =>
        Channels.AddAccountAddressAsync(conn, tx, accountId, email, now, ct);

    // Queues one account_message row (contracts/account-emails.md). args: non-secret values only.
    public static async Task QueueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string kind, long? accountId, string to,
                                        string language, DateTimeOffset now, object? args = null, long? inviteId = null,
                                        CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO account_message (kind, account_id, invite_id, to_address, language, args, requested_at) VALUES ($1, $2, $3, $4, $5, $6, $7)
            """, conn, tx);
        cmd.Parameters.Add(new() { Value = kind });
        cmd.Parameters.Add(new() { Value = (object?)accountId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new() { Value = (object?)inviteId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new() { Value = to });
        cmd.Parameters.Add(new() { Value = language });
        cmd.Parameters.Add(new() { Value = JsonSerializer.Serialize(args ?? new { }), NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new() { Value = now });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // research R16, from the notifier's cleanup pass.
    public async Task CleanupAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (sql, arg) in new (string, object)[]
        {
            ("DELETE FROM session WHERE last_used_at <= $1", now - settings.Options.Accounts.SessionIdleLifetime),
            ("DELETE FROM session WHERE awaiting_code AND created_at <= $1", now - SignIn.AwaitingLifetime),
            // research R9: sent notices, used or expired tokens, and expired unsent rows, each after 24 h.
            ("DELETE FROM account_message WHERE token_hash IS NULL AND sent_at < $1", now - Day),
            ("DELETE FROM account_message WHERE token_hash IS NOT NULL AND coalesce(used_at, token_expires_at) < $1", now - Day),
            ("DELETE FROM account_message WHERE expired_at < $1", now - Day),
            ("DELETE FROM security_event WHERE occurred_at < $1", now - SecurityRetention),
            ("DELETE FROM account WHERE confirmed_at IS NULL AND created_at < $1", now - Unconfirmed), // FR-005
            ("DELETE FROM invite WHERE used >= max_uses OR expires_at < $1", now - Unconfirmed),      // R10: used up, or expired 30 days ago
        })
        {
            await using var cmd = db.CreateCommand(sql);
            cmd.Parameters.Add(new() { Value = arg });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        throttle.Forget(now);
    }

    static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    static async Task<long> CountAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct, params object[] args) =>
        (long)(await ScalarAsync(conn, tx, sql, ct, args))!;

    static async Task<object?> ScalarAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a });
        return await cmd.ExecuteScalarAsync(ct);
    }
}
