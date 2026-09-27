using Npgsql;

namespace InTheKnow;

static class Db
{
    const long InstanceLockKey = 0x496E5468654B6E6F; // "InTheKno"

    // Persist Security Info keeps the password in db.ConnectionString, for the unpooled lock connection below.
    public static NpgsqlDataSource Create(string connectionString) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString) { PersistSecurityInfo = true });

    // The caller keeps the returned connection open for the process lifetime (research R4). It is unpooled,
    // because a pooled session would keep holding the lock after dispose.
    public static async Task<NpgsqlConnection> TakeInstanceLockAsync(NpgsqlDataSource db)
    {
        var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(db.ConnectionString) { Pooling = false }.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT pg_try_advisory_lock({InstanceLockKey})", conn);
        if ((bool)(await cmd.ExecuteScalarAsync())!) return conn;
        await conn.DisposeAsync();
        throw new InvalidOperationException("another InTheKnow instance holds the lock");
    }

    public static async Task MigrateAsync(NpgsqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using (var create = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS schema_migration (version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", conn))
            await create.ExecuteNonQueryAsync();

        var asm = typeof(Db).Assembly;
        foreach (var (version, name) in Migrations())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await using var check = new NpgsqlCommand("SELECT 1 FROM schema_migration WHERE version = $1 FOR UPDATE", conn, tx)
            { Parameters = { new() { Value = version } } };
            if (await check.ExecuteScalarAsync() is not null) continue;

            using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
            await using (var apply = new NpgsqlCommand(await reader.ReadToEndAsync(), conn, tx)) await apply.ExecuteNonQueryAsync();
            await using (var record = new NpgsqlCommand("INSERT INTO schema_migration (version) VALUES ($1)", conn, tx)
            { Parameters = { new() { Value = version } } })
                await record.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }
    }

    // The embedded migrations, in version order.
    static IEnumerable<(int Version, string Name)> Migrations()
    {
        const string prefix = "InTheKnow.Migrations.";
        return typeof(Db).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix) && n.EndsWith(".sql"))
            .Select(n => (Version: int.Parse(n[prefix.Length..].Split('_')[0]), Name: n))
            .OrderBy(m => m.Version);
    }

    // Whether the database has every embedded migration (004 research R13): the server command never migrates.
    public static async Task<bool> SchemaCurrentAsync(NpgsqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT max(version) FROM schema_migration", conn);
        try { return await cmd.ExecuteScalarAsync() is int version && version >= Migrations().Max(m => m.Version); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable) { return false; }
    }
}
