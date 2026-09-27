using System.Security.Cryptography;
using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

[assembly: AssemblyFixture(typeof(InTheKnow.Tests.TemplateDb))]

namespace InTheKnow.Tests;

// Drops the migrated template database once the test run ends.
public sealed class TemplateDb : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        if (TestDb.TemplateName is { } name) await TestDb.AdminAsync($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
    }
}

// A per-test database copied from a migrated template: RunDueChecksAsync claims every due calendar, so tests never share one.
sealed class TestDb : IAsyncDisposable
{
    // The retired operator key (004): tests send it to show that it opens nothing.
    public const string ApiKey = "test-api-key-0123456789-abcdefghijklmnopqrstuvw";
    static readonly SemaphoreSlim gate = new(1, 1);
    internal static string? TemplateName;

    public required string Name { get; init; }
    public required string ConnectionString { get; init; }
    public required NpgsqlDataSource DataSource { get; init; }

    static string Server => Environment.GetEnvironmentVariable("INTHEKNOW_TEST_DB")
        ?? throw new InvalidOperationException(
            "Set INTHEKNOW_TEST_DB, for example \"Host=localhost;Username=intheknow;Password=intheknow;Database=postgres\", after `docker compose up -d db`.");

    static string ConnectionStringFor(string database) => new NpgsqlConnectionStringBuilder(Server) { Database = database }.ConnectionString;

    internal static async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Server);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<TestDb> CreateAsync()
    {
        var name = $"itk_{Guid.NewGuid():N}";
        await gate.WaitAsync();
        try
        {
            if (TemplateName is null)
            {
                var template = $"itk_template_{Guid.NewGuid():N}";
                await AdminAsync($"CREATE DATABASE {template}");
                await using (var ds = Db.Create(ConnectionStringFor(template))) await Db.MigrateAsync(ds);
                TemplateName = template;
            }
            await AdminAsync($"CREATE DATABASE {name} TEMPLATE {TemplateName}");
        }
        finally { gate.Release(); }

        var cs = ConnectionStringFor(name);
        return new TestDb { Name = name, ConnectionString = cs, DataSource = Db.Create(cs) };
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await AdminAsync($"DROP DATABASE IF EXISTS {Name} WITH (FORCE)");
    }

    public Settings Settings(params (string Key, string? Value)[] overrides) => Settings(NullLogger.Instance, overrides);

    public Settings Settings(ILogger log, params (string Key, string? Value)[] overrides) =>
        LoadSettings(log, [("ConnectionStrings:Default", ConnectionString), .. overrides]);

    // Settings for tests without a database.
    public static Settings Offline(params (string Key, string? Value)[] overrides) =>
        LoadSettings(NullLogger.Instance, [("ConnectionStrings:Default", "Host=unused"), .. overrides]);

    public static Settings LoadSettings(ILogger log, params (string Key, string? Value)[] values) =>
        InTheKnow.Settings.Load(new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build(), log);

    public async Task<long> ScalarAsync(string sql, params object[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<List<object?[]>> RowsAsync(string sql, params object[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a });
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await r.ReadAsync())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public async Task ExecAsync(string sql, params object[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a });
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<(long CalendarId, long[] LinkIds)> SeedCalendarAsync(
        string host, string[] urls, long? subscriber = null, DateTimeOffset? nextCheckAt = null)
    {
        var owner = subscriber ?? await TestHost.AccountAsync(DataSource);
        var calendarId = await ScalarAsync(
            "INSERT INTO calendar (identity_hash, host, next_check_at) VALUES ($1, $2, $3) RETURNING id",
            RandomNumberGenerator.GetBytes(32), host, nextCheckAt ?? DateTimeOffset.UnixEpoch);
        var links = new long[urls.Length];
        for (var i = 0; i < urls.Length; i++)
            links[i] = await ScalarAsync(
                "INSERT INTO calendar_link (calendar_id, subscriber, url) VALUES ($1, $2, $3) RETURNING id",
                calendarId, owner, urls[i]);
        return (calendarId, links);
    }
}
