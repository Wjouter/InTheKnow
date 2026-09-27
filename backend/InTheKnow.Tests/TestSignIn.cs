using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace InTheKnow.Tests;

sealed record TestKeyRequest(string? Key, string? Code);

// The test-only sign-in method `test` (specs/004 contracts/sign-in-method.md): a key instead of a password, through the
// same shared calls as a real type. Mapped in its own branch under /api/test-auth, so its patterns do not start with /api.
static class TestSignIn
{
    public static void Register(IServiceCollection s) => s.AddSingleton<IStartupFilter, Filter>();

    sealed class Filter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map("/api/test-auth", branch =>
            {
                branch.UseRouting();
                branch.UseAuthentication();
                branch.UseAuthorization();
                branch.UseEndpoints(Map);
            });
            next(app);
        };
    }

    static void Map(IEndpointRouteBuilder e)
    {
        e.MapPost("/sign-in", async (TestKeyRequest? body, HttpContext http, NpgsqlDataSource db, SignIn signIn) =>
        {
            var key = (body?.Key ?? "").Trim().ToLowerInvariant();
            if (await FindAsync(db, key, null) is not var (accountId, methodId))
            {
                await signIn.FailAsync(key, http);
                return Api.Problem(401, "invalid_credentials");
            }
            if (await signIn.CheckAsync(key, http) is { } wait) return SignIn.TooManyAttempts(http, wait);
            return await signIn.CompleteAsync(accountId, methodId, http, throttleKey: key);
        }).AllowAnonymous();

        e.MapPost("/reauthenticate", async (ClaimsPrincipal user, TestKeyRequest? body, HttpContext http, NpgsqlDataSource db, SignIn signIn) =>
        {
            if (await FindAsync(db, (body?.Key ?? "").Trim().ToLowerInvariant(), user.AccountId()) is not var (_, methodId))
                return Api.Problem(401, "invalid_credentials");
            if (await signIn.StepUpCodeAsync(user.AccountId(), body?.Code, http) is { } refused) return refused;
            await signIn.ReauthenticatedAsync(user.SessionId(), methodId, http);
            return Results.NoContent();
        }).RequireAuthorization();

        e.MapPost("/add", async (ClaimsPrincipal user, TestKeyRequest? body, HttpContext http, Accounts accounts) =>
            await accounts.AddMethodAsync(user.AccountId(), "test", (body?.Key ?? "").Trim().ToLowerInvariant(), null, http) is { } error
                ? Api.Problem(409, error) : Results.NoContent())
            .RequireAuthorization("stepUp");
    }

    static async Task<(long AccountId, long MethodId)?> FindAsync(NpgsqlDataSource db, string key, long? accountId)
    {
        await using var cmd = db.CreateCommand("SELECT account_id, id FROM sign_in_method WHERE type = 'test' AND key = $1 AND ($2::bigint IS NULL OR account_id = $2)");
        cmd.Parameters.Add(new() { Value = key });
        cmd.Parameters.Add(new() { Value = (object?)accountId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? (r.GetInt64(0), r.GetInt64(1)) : null;
    }
}
