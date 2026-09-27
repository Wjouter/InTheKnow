using System.Security.Cryptography;
using System.Text;
using Npgsql;
using OtpNet;

namespace InTheKnow;

sealed record CodeCheck(bool Ok, bool RecoveryCode, int RecoveryCodesLeft);

// Authenticator codes and recovery codes (specs/004 research R5): TOTP SHA-1, 6 digits, 30 s, ±1 step, never the same
// step twice; 10 recovery codes of 80 bit, stored as SHA-256.
// ponytail: plain key storage; encrypt with Data Protection if database backups leave the server (research R5)
static class TwoFactor
{
    static readonly VerificationWindow Window = new(previous: 1, future: 1);

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static string Uri(string address, byte[] secret) =>
        $"otpauth://totp/InTheKnow:{System.Uri.EscapeDataString(address)}?secret={Base32Encoding.ToString(secret)}&issuer=InTheKnow&algorithm=SHA1&digits=6&period=30";

    public static IReadOnlyList<string> NewRecoveryCodes() =>
        [.. Enumerable.Range(0, 10).Select(_ => Base32Encoding.ToString(RandomNumberGenerator.GetBytes(10))).Select(c => $"{c[..4]}-{c[4..8]}-{c[8..12]}-{c[12..]}")];

    public static byte[] HashRecoveryCode(string code) =>
        SHA256.HashData(Encoding.ASCII.GetBytes(code.Replace("-", "").Replace(" ", "").ToUpperInvariant()));

    // The step a 6-digit code matches within ±1 step, or null.
    public static long? Step(byte[] secret, string code, DateTimeOffset now) =>
        code.Length == 6 && code.All(char.IsAsciiDigit) && new Totp(secret).VerifyTotp(now.UtcDateTime, code, out var step, Window) ? step : null;

    // A current code (each step once, even when two requests race) or an unused recovery code (once).
    public static async Task<CodeCheck> VerifyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, string code, DateTimeOffset now, CancellationToken ct)
    {
        var c = code.Trim();
        if (c.Length == 6 && c.All(char.IsAsciiDigit))
        {
            await using var find = new NpgsqlCommand("SELECT secret FROM two_factor WHERE account_id = $1 AND enabled_at IS NOT NULL", conn, tx)
            { Parameters = { new() { Value = accountId } } };
            if (await find.ExecuteScalarAsync(ct) is not byte[] secret || Step(secret, c, now) is not { } step) return new(false, false, 0);
            await using var update = new NpgsqlCommand(
                "UPDATE two_factor SET last_step = $2 WHERE account_id = $1 AND last_step < $2 AND enabled_at IS NOT NULL", conn, tx)
            { Parameters = { new() { Value = accountId }, new() { Value = step } } };
            return new(await update.ExecuteNonQueryAsync(ct) == 1, false, 0);
        }
        await using var use = new NpgsqlCommand("""
            WITH u AS (UPDATE recovery_code SET used_at = $3 WHERE account_id = $1 AND code_hash = $2 AND used_at IS NULL RETURNING 1)
            SELECT (SELECT count(*) FROM u), (SELECT count(*) FROM recovery_code WHERE account_id = $1 AND used_at IS NULL)
            """, conn, tx)
        { Parameters = { new() { Value = accountId }, new() { Value = HashRecoveryCode(c) }, new() { Value = now } } };
        await using var r = await use.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        // The count in the same statement sees the codes before the update.
        return r.GetInt64(0) == 1 ? new(true, true, (int)r.GetInt64(1) - 1) : new(false, false, 0);
    }
}
