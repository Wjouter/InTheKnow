using Npgsql;
using Xunit;

namespace InTheKnow.Tests;

public class DbTests
{
    [Fact]
    public async Task MigrateTwiceAppliesOnce()
    {
        await using var db = await TestDb.CreateAsync();
        await Db.MigrateAsync(db.DataSource);
        await Db.MigrateAsync(db.DataSource);
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM schema_migration WHERE version = 1"));
    }

    [Fact]
    public async Task SecondInstanceIsRefused()
    {
        await using var db = await TestDb.CreateAsync();
        var first = await Db.TakeInstanceLockAsync(db.DataSource);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Db.TakeInstanceLockAsync(db.DataSource));
        await first.DisposeAsync();
        await using var second = await Db.TakeInstanceLockAsync(db.DataSource);
    }

    [Fact]
    public async Task ConstraintsAreLive()
    {
        await using var db = await TestDb.CreateAsync();
        var e = await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync(
            "INSERT INTO calendar (identity_hash, host, next_check_at, status) VALUES ($1, 'x', now(), 'x')", new byte[32]));
        Assert.Equal(PostgresErrorCodes.CheckViolation, e.SqlState);
    }
}
