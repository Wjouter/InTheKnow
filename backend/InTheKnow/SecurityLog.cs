using System.Net;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace InTheKnow;

// The security log (specs/004 research R14): one row per event, written in the action's transaction where there is
// one. The types are data-model.md § Security event types. `detail` holds non-secret values only (FR-034).
static class SecurityLog
{
    public static async Task WriteAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string type, long? accountId, DateTimeOffset now,
                                        IPAddress? source, long? actorId = null, object? detail = null, CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO security_event (occurred_at, type, account_id, actor_id, source, detail) VALUES ($1, $2, $3, $4, $5, $6)", conn, tx);
        cmd.Parameters.Add(new() { Value = now });
        cmd.Parameters.Add(new() { Value = type });
        cmd.Parameters.Add(new() { Value = (object?)accountId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new() { Value = (object?)actorId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new() { Value = (object?)source ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Inet });
        cmd.Parameters.Add(new() { Value = JsonSerializer.Serialize(detail ?? new { }), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
