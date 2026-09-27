using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace InTheKnow.Tests;

sealed record SignedIn(long AccountId, long SessionId, string Token, string Email, HttpClient Client);

// Accounts and sessions for tests (specs/004 tasks.md § Shared signatures).
static class TestHost
{
    public const string Password = "test-password-0123"; // valid: 18 characters, not on the common list, not an address

    // One iteration: the v3 format does not depend on the type parameter, so it verifies under PasswordHasher<Account>.
    public static readonly string PasswordHash =
        new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = 1 })).HashPassword(new object(), Password);

    public static string NewEmail() => $"a{Guid.NewGuid():N}@example.nl";

    // A confirmed account with a password method and no address channel, so 003's 5-channel tests stay valid.
    public static async Task<long> AccountAsync(NpgsqlDataSource db, string? email = null, string language = "en", string role = "user")
    {
        await using var cmd = db.CreateCommand("""
            WITH a AS (INSERT INTO account (email, language, role, confirmed_at, created_at) VALUES ($1, $2, $3, now(), now()) RETURNING id),
                 m AS (INSERT INTO sign_in_method (account_id, type, secret, created_at) SELECT id, 'password', $4, now() FROM a)
            SELECT id FROM a
            """);
        foreach (var v in new object[] { email ?? NewEmail(), language, role, PasswordHash }) cmd.Parameters.Add(new() { Value = v });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // The account service built by hand, for tests that construct the notifier themselves.
    public static Accounts Accounts(TestDb db, Settings settings, TimeProvider time)
    {
        var hasher = new PasswordHasher<Account>(Options.Create(new PasswordHasherOptions { IterationCount = 1 }));
        var throttle = new Throttle(time, settings);
        return new(db.DataSource, hasher, throttle, new SignIn(db.DataSource, hasher, throttle, time, NullLogger<SignIn>.Instance), settings, time);
    }

    // Three accounts for database-level tests: the subscribers s1, s2 and s3 of 001 to 003's stories.
    public static async Task<(long, long, long)> AccountsAsync(NpgsqlDataSource db) =>
        (await AccountAsync(db), await AccountAsync(db), await AccountAsync(db));

    // Direct insert of a confirmed account, its method (password, or `test` with key = lowercased address) and an active
    // session, on the fake clock. No address channel.
    public static async Task<SignedIn> SignInAsync(this ApiHost h, string? email = null, string method = "password",
                                                   bool admin = false, bool secondFactor = false)
    {
        email ??= NewEmail();
        var (token, hash) = SignIn.NewToken();
        await using var cmd = h.Db.DataSource.CreateCommand("""
            WITH a AS (INSERT INTO account (email, language, role, confirmed_at, created_at) VALUES ($1, 'en', $2, $3, $3) RETURNING id),
                 m AS (INSERT INTO sign_in_method (account_id, type, key, secret, created_at)
                       SELECT id, $4, $5, $6, $3 FROM a RETURNING id, account_id),
                 t AS (INSERT INTO two_factor (account_id, secret, enabled_at) SELECT id, $7, $3 FROM a WHERE $8),
                 s AS (INSERT INTO session (account_id, method_id, token_hash, device, second_factor, created_at, last_used_at)
                       SELECT account_id, id, $9, 'Test', $8, $3, $3 FROM m RETURNING id, account_id)
            SELECT account_id, id FROM s
            """);
        foreach (var v in new object[]
        {
            email, admin ? "admin" : "user", h.Time.GetUtcNow(), method, method == "password" ? DBNull.Value : email.ToLowerInvariant(),
            method == "password" ? PasswordHash : DBNull.Value, RandomNumberGenerator.GetBytes(20), secondFactor, hash,
        })
            cmd.Parameters.Add(new() { Value = v });
        long accountId, sessionId;
        await using (var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            await r.ReadAsync(TestContext.Current.CancellationToken);
            (accountId, sessionId) = (r.GetInt64(0), r.GetInt64(1));
        }
        return new(accountId, sessionId, token, email, h.ClientFor(token));
    }

    // Through the method's own route: POST /api/auth/sign-in, or the test-only POST /api/test-auth/sign-in.
    public static async Task<SignedIn> SignInViaAsync(this ApiHost h, string method, string email, string? password = null)
    {
        var r = method == "password"
            ? await h.Client.PostJsonAsync("/api/auth/sign-in", new { email, password = password ?? Password })
            : await h.Client.PostJsonAsync("/api/test-auth/sign-in", new { key = email });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var token = JsonNode.Parse(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["token"]!.GetValue<string>();
        var row = (await h.Db.RowsAsync("SELECT account_id, id FROM session WHERE token_hash = $1", SignIn.Hash(token))).Single();
        return new((long)row[0]!, (long)row[1]!, token, email, h.ClientFor(token));
    }

    public static HttpClient ClientFor(this ApiHost h, string token)
    {
        var client = h.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<JsonNode> JsonAsync(this SignedIn who, string path)
    {
        var response = await who.Client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string path, object body) =>
        client.PostAsJsonAsync(path, body, TestContext.Current.CancellationToken);

    public static async Task<string?> ReasonAsync(HttpResponseMessage r) =>
        JsonNode.Parse(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))?["reason"]?.GetValue<string>();
}
