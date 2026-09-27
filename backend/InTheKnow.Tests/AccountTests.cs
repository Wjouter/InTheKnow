using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static InTheKnow.Tests.LinkIdentityTests;

namespace InTheKnow.Tests;

// specs/004 US5 (and US6), FR-013, FR-014a, FR-021 to FR-024, SC-008, SC-009; theories over password and test.
public class AccountTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    public static TheoryData<string> Methods => SignInTests.Methods;

    static async Task Expect(HttpResponseMessage r, HttpStatusCode status, string? reason = null)
    {
        Assert.Equal(status, r.StatusCode);
        if (reason is not null) Assert.Equal(reason, await TestHost.ReasonAsync(r));
    }

    static async Task Reauth(ApiHost h, SignedIn who, string method)
    {
        var (status, _) = await TwoFactorTests.ReauthAsync(who, method);
        Assert.Equal(HttpStatusCode.NoContent, status);
    }

    static Task<HttpResponseMessage> Put(SignedIn who, string path, object body) => who.Client.PutAsJsonAsync(path, body, Ct);
    static Task<HttpResponseMessage> Post(SignedIn who, string path, object body) => who.Client.PostAsJsonAsync(path, body, Ct);

    // A signed-in account of this method with its own address channel, as a confirmed account has (FR-028).
    static async Task<SignedIn> AccountAsync(ApiHost h, string method)
    {
        var a = await h.SignInViaAsync(method, (await h.SignInAsync(method: method)).Email);
        await h.Db.ExecAsync("""
            INSERT INTO channel (subscriber, type, target, status, status_since, activated_at, unsubscribe_token, created_at, is_account_address)
            VALUES ($1, 'email', $2, 'active', $3, $3, $4, $3, true)
            """, a.AccountId, a.Email, h.Time.GetUtcNow(), Channels.NewToken());
        return a;
    }

    // Research R6: the step-up routes need a re-authentication from the last 10 minutes, in this session.
    [Theory, MemberData(nameof(Methods))]
    public async Task StepUp(string method)
    {
        await using var h = await SignInTests.HostAsync();
        var a = await AccountAsync(h, method);
        var other = await h.SignInViaAsync(method, a.Email);
        Func<SignedIn, Task<HttpResponseMessage>>[] routes =
        [
            w => Put(w, "/api/me/password", new { password = "short" }),
            w => Post(w, "/api/me/email", new { email = "not an address" }),
            w => w.Client.DeleteAsync("/api/me/2fa", Ct),
        ];
        foreach (var route in routes) await Expect(await route(a), HttpStatusCode.Forbidden, "reauthentication_required");
        await Expect(await a.Client.DeleteAsync("/api/me", Ct), HttpStatusCode.Forbidden, "reauthentication_required");

        await Reauth(h, a, method);
        await Expect(await routes[0](a), HttpStatusCode.BadRequest, "password_length");
        await Expect(await routes[1](a), HttpStatusCode.BadRequest, "invalid_email");
        await Expect(await routes[2](a), HttpStatusCode.Conflict, "two_factor_off");
        foreach (var route in routes) await Expect(await route(other), HttpStatusCode.Forbidden, "reauthentication_required"); // per session

        h.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        foreach (var route in routes) await Expect(await route(a), HttpStatusCode.Forbidden, "reauthentication_required");
    }

    [Fact]
    public async Task StepUpWithAWrongPasswordIsThrottled()
    {
        await using var h = await SignInTests.HostAsync();
        var a = await h.SignInAsync();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await TwoFactorTests.ReauthAsync(a, "password", password: "wrong-password-0123")).Item1);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await TwoFactorTests.ReauthAsync(a, "password", password: "wrong-password-0123")).Item1);
        var keyOnly = await h.SignInAsync(method: "test");
        Assert.Equal(HttpStatusCode.Unauthorized, (await TwoFactorTests.ReauthAsync(keyOnly, "password")).Item1); // no password (US6-5)
    }

    // US5-1, SC-009.
    [Fact]
    public async Task ChangePassword()
    {
        await using var d = await SignUpTests.HostAsync();
        var h = d.H;
        var a = await h.SignInViaAsync("password", d.S1.Email);
        var other = await h.SignInViaAsync("password", d.S1.Email);
        var method = await d.Db.ScalarAsync("SELECT id FROM sign_in_method WHERE account_id = $1", a.AccountId);
        await Reauth(h, a, "password");
        foreach (var (password, reason) in new[] { ("short", "password_length"), (await SignUpTests.CommonPasswordAsync(), "password_common"), (a.Email, "password_is_email") })
            await Expect(await Put(a, "/api/me/password", new { password }), HttpStatusCode.BadRequest, reason);
        await Expect(await Put(a, "/api/me/password", new { password = "changed-password-1" }), HttpStatusCode.NoContent);
        await Expect(await SignInTests.PasswordSignInAsync(h, a.Email, TestHost.Password), HttpStatusCode.Unauthorized);
        await h.SignInViaAsync("password", a.Email, "changed-password-1");
        await Expect(await other.Client.GetAsync("/api/me", Ct), HttpStatusCode.Unauthorized, "not_signed_in");
        await Expect(await a.Client.GetAsync("/api/me", Ct), HttpStatusCode.OK);
        Assert.Equal(method, await d.Db.ScalarAsync("SELECT id FROM sign_in_method WHERE account_id = $1", a.AccountId));
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'passwordChanged' AND account_id = $1", a.AccountId));
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'passwordChanged' AND account_id = $1", a.AccountId));
    }

    static async Task<string> EmailChangeTokenAsync(DeliveryHost d, string to)
    {
        await d.NotifyAsync(3);
        return SignUpTests.TokenOf(SignUpTests.Mails(d, "emailChange").Last(m => m.Target == to).Message);
    }

    static Task<HttpResponseMessage> ConfirmAsync(DeliveryHost d, string token) => SignUpTests.PostAsync(d, "/api/auth/email-change/confirm", new { token });

    // US5-2, US5-3, FR-022, FR-023.
    [Theory, MemberData(nameof(Methods))]
    public async Task ChangeEmail(string method)
    {
        await using var d = await SignUpTests.HostAsync();
        var h = d.H;
        var a = await AccountAsync(h, method);
        var old = a.Email;
        await CheckerTests.ChannelAsync(d.Db, a.AccountId, "active", d.Now, "new@example.nl");
        await CheckerTests.ChannelAsync(d.Db, a.AccountId, "active", d.Now, "keep@example.nl");
        await Reauth(h, a, method);
        await Expect(await Post(a, "/api/me/email", new { email = "x" }), HttpStatusCode.BadRequest, "invalid_email");
        await Expect(await Post(a, "/api/me/email", new { email = old.ToUpperInvariant() }), HttpStatusCode.BadRequest, "same_email");

        await Expect(await Post(a, "/api/me/email", new { email = "new@example.nl" }), HttpStatusCode.Accepted);
        Assert.Equal(old, (string)(await d.Db.RowsAsync("SELECT email FROM account WHERE id = $1", a.AccountId)).Single()[0]!);
        var token = await EmailChangeTokenAsync(d, "new@example.nl");
        Assert.Contains(SignUpTests.Mails(d, "emailChangeNotice"), m => m.Target == old);
        Assert.Equal("new@example.nl", (await a.JsonAsync("/api/me"))["pendingEmail"]!.GetValue<string>());

        var mailsToOld = d.C.Delivered.Count(m => m.Target == old);
        await Expect(await ConfirmAsync(d, token), HttpStatusCode.NoContent);
        Assert.Equal("new@example.nl", (string)(await d.Db.RowsAsync("SELECT email FROM account WHERE id = $1", a.AccountId)).Single()[0]!);
        Assert.Equal(["keep@example.nl", "new@example.nl"],
            (await d.Db.RowsAsync("SELECT target FROM channel WHERE subscriber = $1 ORDER BY target", a.AccountId)).Select(r => r[0]));
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM channel WHERE subscriber = $1 AND is_account_address AND target = 'new@example.nl'", a.AccountId));
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'emailChanged' AND account_id = $1", a.AccountId));
        await Expect(await ConfirmAsync(d, token), HttpStatusCode.BadRequest, "token_invalid");
        await d.NotifyAsync(3);
        Assert.Equal(mailsToOld, d.C.Delivered.Count(m => m.Target == old));
        Assert.Null((await a.JsonAsync("/api/me"))["pendingEmail"]);
    }

    [Fact]
    public async Task EmailChangeEdgeCases()
    {
        await using var d = await SignUpTests.HostAsync();
        var h = d.H;
        var a = await AccountAsync(h, "password");
        await Reauth(h, a, "password");

        // A link expires after 24 h; a newer request supersedes an older one once it is sent.
        await Post(a, "/api/me/email", new { email = "one@example.nl" });
        var first = await EmailChangeTokenAsync(d, "one@example.nl");
        await Post(a, "/api/me/email", new { email = "two@example.nl" });
        var second = await EmailChangeTokenAsync(d, "two@example.nl");
        await Expect(await ConfirmAsync(d, first), HttpStatusCode.BadRequest, "token_invalid");
        d.H.Time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        await Expect(await ConfirmAsync(d, second), HttpStatusCode.BadRequest, "token_invalid");
        Assert.Equal(a.Email, (string)(await d.Db.RowsAsync("SELECT email FROM account WHERE id = $1", a.AccountId)).Single()[0]!);

        // An address another account has: the same answer, and nothing queued or changed.
        await Reauth(h, a, "password");
        var queued = await d.Db.ScalarAsync("SELECT count(*) FROM account_message");
        await Expect(await Post(a, "/api/me/email", new { email = d.S2.Email.ToUpperInvariant() }), HttpStatusCode.Accepted);
        Assert.Equal(queued, await d.Db.ScalarAsync("SELECT count(*) FROM account_message"));

        // Taken between the request and the confirmation.
        await Post(a, "/api/me/email", new { email = "race@example.nl" });
        var race = await EmailChangeTokenAsync(d, "race@example.nl");
        await TestHost.AccountAsync(d.Db.DataSource, "Race@example.nl");
        await Expect(await ConfirmAsync(d, race), HttpStatusCode.Conflict, "address_taken");

        // Without the flagged channel (removed earlier), nothing moves and the change still succeeds (R15).
        await d.Db.ExecAsync("DELETE FROM channel WHERE subscriber = $1 AND is_account_address", a.AccountId);
        await Post(a, "/api/me/email", new { email = "moved@example.nl" });
        await Expect(await ConfirmAsync(d, await EmailChangeTokenAsync(d, "moved@example.nl")), HttpStatusCode.NoContent);
        Assert.Equal(0, await d.Db.ScalarAsync("SELECT count(*) FROM channel WHERE subscriber = $1", a.AccountId));
    }

    // US5-6, US5-7.
    [Theory, MemberData(nameof(Methods))]
    public async Task Sessions(string method)
    {
        await using var h = await SignInTests.HostAsync();
        var a = await AccountAsync(h, method);
        var other = await h.SignInViaAsync(method, a.Email);
        await h.Db.ExecAsync("""
            INSERT INTO session (account_id, method_id, token_hash, device, awaiting_code, created_at, last_used_at)
            SELECT account_id, method_id, $2, 'awaiting', true, created_at, last_used_at FROM session WHERE id = $1
            """, a.SessionId, RandomNumberGenerator.GetBytes(32));
        var b = await h.SignInAsync();

        var list = await a.JsonAsync("/api/me/sessions");
        var items = list["items"]!.AsArray();
        Assert.Equal(["id", "device", "createdAt", "lastUsedAt", "current"], items[0]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(await h.Db.ScalarAsync("SELECT count(*) FROM session WHERE account_id = $1 AND NOT awaiting_code", a.AccountId), items.Count);
        Assert.Equal(a.SessionId, Assert.Single(items, i => i!["current"]!.GetValue<bool>())!["id"]!.GetValue<long>());
        Assert.DoesNotContain(a.Token, list.ToJsonString());
        Assert.DoesNotContain(other.Token, list.ToJsonString());

        await Expect(await a.Client.DeleteAsync($"/api/me/sessions/{a.SessionId}", Ct), HttpStatusCode.Conflict, "use_sign_out");
        var theirs = await a.Client.DeleteAsync($"/api/me/sessions/{b.SessionId}", Ct);
        var missing = await a.Client.DeleteAsync("/api/me/sessions/999999999", Ct);
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (theirs.StatusCode, missing.StatusCode));
        Assert.Equal(await missing.Content.ReadAsStringAsync(Ct), await theirs.Content.ReadAsStringAsync(Ct));
        await Expect(await a.Client.DeleteAsync($"/api/me/sessions/{other.SessionId}", Ct), HttpStatusCode.NoContent);
        await Expect(await other.Client.GetAsync("/api/me", Ct), HttpStatusCode.Unauthorized, "not_signed_in");
        await Expect(await b.Client.GetAsync("/api/me", Ct), HttpStatusCode.OK);
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'sessionEnded' AND account_id = $1", a.AccountId));

        // US5-4: sign out everywhere ends this session too.
        await Expect(await a.Client.PostAsync("/api/auth/sign-out-everywhere", null, Ct), HttpStatusCode.NoContent);
        await Expect(await a.Client.GetAsync("/api/me", Ct), HttpStatusCode.Unauthorized, "not_signed_in");
    }

    // US5-5, SC-008: deleting an account leaves no row that is its own, and no copy of its address but the final notice.
    [Theory, MemberData(nameof(Methods))]
    public async Task DeleteAccount(string method)
    {
        await using var d = await SignUpTests.HostAsync();
        var h = d.H;
        var a = await AccountAsync(h, method);
        var b = d.S2;
        var shared = await d.SubscribeAsync(a, MtLink());
        Assert.Equal(shared, await d.SubscribeAsync(b, MtLink(user: "2002")));
        var own = await d.SubscribeAsync(a, MtLink(id: "ALET26VT4B", user: "3003"));
        var preset = JsonNode.Parse(await (await Post(a, "/api/me/presets", new { name = "Mine", rules = Array.Empty<object>() })).Content.ReadAsStringAsync(Ct))!["id"]!;
        await Expect(await Put(a, $"/api/me/calendars/{own}/filter/preset", new { presetId = preset.GetValue<long>() }), HttpStatusCode.OK);
        var channel = await CheckerTests.ChannelAsync(d.Db, a.AccountId, "active", d.Now, "second@example.nl");
        await d.RecordChangesAsync(MtLink(), "mt-nl-edited.ics");
        Assert.True(await d.Db.ScalarAsync("SELECT count(*) FROM delivery WHERE channel_id = $1", channel) > 0);
        await d.Db.ExecAsync("INSERT INTO declined_address (subscriber, address_hash, created_at) VALUES ($1, $2, now())", a.AccountId, new byte[32]);
        var (secret, _) = await TwoFactorTests.EnableAsync(h, a);
        await h.SignInViaAsync(method, a.Email); // a second session (awaiting a code)
        await d.Db.ExecAsync("INSERT INTO account_message (kind, account_id, to_address, language, requested_at) VALUES ('twoFactorOn', $1, $2, 'en', $3)",
            a.AccountId, a.Email, d.Now);
        var changesOfShared = await d.Db.ScalarAsync("SELECT count(*) FROM detected_change WHERE calendar_id = $1", shared);

        h.Time.Advance(TimeSpan.FromSeconds(30));
        var (status, _) = await TwoFactorTests.ReauthAsync(a, method, TwoFactorTests.Code(secret, h.Time.GetUtcNow()));
        Assert.Equal(HttpStatusCode.NoContent, status);
        await Expect(await a.Client.DeleteAsync("/api/me", Ct), HttpStatusCode.NoContent);

        foreach (var row in await d.Db.RowsAsync("""
            SELECT c.table_name, c.column_name FROM information_schema.columns c JOIN information_schema.tables t USING (table_schema, table_name)
            WHERE c.table_schema = 'public' AND t.table_type = 'BASE TABLE' AND c.column_name IN ('subscriber', 'owner', 'account_id', 'created_by')
              AND c.table_name <> 'security_event'
            """))
            Assert.True(await d.Db.ScalarAsync($"SELECT count(*) FROM {row[0]} WHERE {row[1]} = $1", a.AccountId) == 0, $"{row[0]}.{row[1]}");

        async Task<long> AddressCopiesAsync()
        {
            long n = 0;
            foreach (var row in await d.Db.RowsAsync("""
                SELECT c.table_name, c.column_name FROM information_schema.columns c JOIN information_schema.tables t USING (table_schema, table_name)
                WHERE c.table_schema = 'public' AND t.table_type = 'BASE TABLE' AND c.data_type = 'text'
                """))
                n += await d.Db.ScalarAsync($"SELECT count(*) FROM {row[0]} WHERE strpos(lower({row[1]}), lower($1)) > 0", a.Email);
            return n;
        }
        Assert.Equal(1, await AddressCopiesAsync());
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'deleted' AND account_id IS NULL AND sent_at IS NULL"));
        await d.NotifyAsync(3);
        Assert.Contains(SignUpTests.Mails(d, "deleted"), m => m.Target == a.Email);
        Assert.Equal(0, await AddressCopiesAsync());

        Assert.Equal(0, await d.Db.ScalarAsync("SELECT count(*) FROM calendar WHERE id = $1", own));
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM calendar WHERE id = $1", shared));
        Assert.Equal(changesOfShared, await d.Db.ScalarAsync("SELECT count(*) FROM detected_change WHERE calendar_id = $1", shared));
        Assert.NotEmpty((await b.JsonAsync("/api/me/calendars")).AsArray());
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'accountDeleted' AND account_id = $1", a.AccountId));
    }

    // --- US6: sign-in methods (FR-026, FR-027a, SC-014) ------------------------------------------------------------

    static Task<HttpResponseMessage> AddKeyAsync(SignedIn who, string key) => Post(who, "/api/test-auth/add", new { key });

    [Fact]
    public async Task AddAndListMethods()
    {
        await using var h = await SignInTests.HostAsync();
        var a = await h.SignInViaAsync("password", (await h.SignInAsync()).Email);
        var b = await h.SignInAsync(method: "test");
        await h.AddAsync(a, MtLink());

        await Expect(await AddKeyAsync(a, "a-key@example.nl"), HttpStatusCode.Forbidden, "reauthentication_required");
        await Reauth(h, a, "password");
        await Expect(await AddKeyAsync(a, b.Email.ToUpperInvariant()), HttpStatusCode.Conflict, "method_in_use");
        await Expect(await AddKeyAsync(a, "a-key@example.nl"), HttpStatusCode.NoContent);
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'methodAdded' AND account_id = $1 AND args->>'type' = 'test'", a.AccountId));
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'methodAdded' AND account_id = $1", a.AccountId));

        // Both methods reach the same account and its calendars (US6-2).
        var viaKey = await h.SignInViaAsync("test", "a-key@example.nl");
        Assert.Equal(a.AccountId, viaKey.AccountId);
        Assert.Single((await viaKey.JsonAsync("/api/me/calendars")).AsArray());

        // US6-1: the list, without secrets; last use follows sign-ins and step-ups.
        var list = await a.JsonAsync("/api/me/methods");
        var items = list["items"]!.AsArray();
        Assert.Equal(["id", "type", "createdAt", "lastUsedAt"], items[0]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(["password", "test"], items.Select(i => i!["type"]!.GetValue<string>()));
        Assert.DoesNotContain("AQAAAA", list.ToJsonString()); // no v3 hash
        var before = items[1]!["lastUsedAt"]!.GetValue<DateTime>();
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await TwoFactorTests.ReauthAsync(viaKey, "test");
        Assert.True((await a.JsonAsync("/api/me/methods"))["items"]![1]!["lastUsedAt"]!.GetValue<DateTime>() > before);
    }

    // US6-3, US6-4, US6-6.
    [Fact]
    public async Task RemoveMethods()
    {
        await using var h = await SignInTests.HostAsync();
        var a = await h.SignInViaAsync("password", (await h.SignInAsync()).Email);
        await Reauth(h, a, "password");
        await Expect(await AddKeyAsync(a, "a-key@example.nl"), HttpStatusCode.NoContent);
        var viaKey = await h.SignInViaAsync("test", "a-key@example.nl");
        var other = await h.SignInAsync();
        var (password, key) = ((long)(await h.Db.RowsAsync("SELECT id FROM sign_in_method WHERE account_id = $1 AND type = 'password'", a.AccountId)).Single()[0]!,
                               (long)(await h.Db.RowsAsync("SELECT id FROM sign_in_method WHERE account_id = $1 AND type = 'test'", a.AccountId)).Single()[0]!);
        var otherMethod = await h.Db.ScalarAsync("SELECT id FROM sign_in_method WHERE account_id = $1", other.AccountId);

        await Expect(await viaKey.Client.DeleteAsync($"/api/me/methods/{password}", Ct), HttpStatusCode.Forbidden, "reauthentication_required");
        await TwoFactorTests.ReauthAsync(viaKey, "test");
        await Expect(await viaKey.Client.DeleteAsync($"/api/me/methods/{otherMethod}", Ct), HttpStatusCode.NotFound);
        await Expect(await viaKey.Client.DeleteAsync($"/api/me/methods/{password}", Ct), HttpStatusCode.NoContent);
        await Expect(await a.Client.GetAsync("/api/me", Ct), HttpStatusCode.Unauthorized, "not_signed_in"); // opened with the password
        await Expect(await viaKey.Client.GetAsync("/api/me", Ct), HttpStatusCode.OK);
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'methodRemoved' AND account_id = $1", a.AccountId));
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'methodRemoved' AND account_id = $1", a.AccountId));

        // US6-6: the last method stays.
        await Expect(await viaKey.Client.DeleteAsync($"/api/me/methods/{key}", Ct), HttpStatusCode.Conflict, "last_method");
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM sign_in_method WHERE id = $1", key));

        // US6-4: the password comes back through PUT /api/me/password.
        await Expect(await Put(viaKey, "/api/me/password", new { password = "back-again-pass-1" }), HttpStatusCode.NoContent);
        await h.SignInViaAsync("password", a.Email, "back-again-pass-1");
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'methodAdded' AND account_id = $1 AND args->>'type' = 'password'", a.AccountId));
    }

    // US6-5: an account with only the test-only method uses everything, with step-up through that method.
    [Fact]
    public async Task AccountWithoutPassword()
    {
        await using var d = await SignUpTests.HostAsync();
        var h = d.H;
        var a = await h.SignInViaAsync("test", (await h.SignInAsync(method: "test")).Email);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TwoFactorTests.ReauthAsync(a, "password")).Item1);
        var (secret, _) = await TwoFactorTests.EnableAsync(h, a);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.NoContent, (await TwoFactorTests.ReauthAsync(a, "test", TwoFactorTests.Code(secret, h.Time.GetUtcNow()))).Item1);
        await Expect(await a.Client.DeleteAsync("/api/me/2fa", Ct), HttpStatusCode.NoContent);
        await Expect(await Post(a, "/api/me/email", new { email = "moved-away@example.nl" }), HttpStatusCode.Accepted);
        await Expect(await a.Client.DeleteAsync("/api/me", Ct), HttpStatusCode.NoContent);
        Assert.Equal(0, await d.Db.ScalarAsync("SELECT count(*) FROM account WHERE id = $1", a.AccountId));
    }

    // SC-014: concurrent removals leave one method, and a random run of adds and removes never leaves none.
    [Fact]
    public async Task MethodsNeverRunOut()
    {
        await using var h = await SignInTests.HostAsync();
        var a = await h.SignInViaAsync("password", (await h.SignInAsync()).Email);
        await Reauth(h, a, "password");
        await Expect(await AddKeyAsync(a, "a-key@example.nl"), HttpStatusCode.NoContent);
        var viaKey = await h.SignInViaAsync("test", "a-key@example.nl");
        await TwoFactorTests.ReauthAsync(viaKey, "test");
        var ids = (await h.Db.RowsAsync("SELECT id FROM sign_in_method WHERE account_id = $1 ORDER BY id", a.AccountId)).Select(r => (long)r[0]!).ToArray();
        var results = await Task.WhenAll(
            Task.Run(() => a.Client.DeleteAsync($"/api/me/methods/{ids[1]}", Ct), Ct),
            Task.Run(() => viaKey.Client.DeleteAsync($"/api/me/methods/{ids[0]}", Ct), Ct));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.NoContent);
        // The other is refused by the rule, or finds its own session already ended with the removed method.
        var loser = results.Single(r => r.StatusCode != HttpStatusCode.NoContent);
        Assert.Contains((loser.StatusCode, await TestHost.ReasonAsync(loser)),
            new[] { (HttpStatusCode.Conflict, (string?)"last_method"), (HttpStatusCode.Unauthorized, "not_signed_in") });
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM sign_in_method WHERE account_id = $1", a.AccountId));

        var accounts = h.Factory.Services.GetRequiredService<Accounts>();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var random = new Random(4);
        for (var i = 0; i < 20; i++)
        {
            var existing = (await h.Db.RowsAsync("SELECT id FROM sign_in_method WHERE account_id = $1", a.AccountId)).Select(r => (long)r[0]!).ToList();
            if (random.Next(2) == 0) await accounts.AddMethodAsync(a.AccountId, "test", $"k{i}@example.nl", null, http);
            else await accounts.RemoveMethodAsync(a.AccountId, existing[random.Next(existing.Count)], http);
            Assert.True(await h.Db.ScalarAsync("SELECT count(*) FROM sign_in_method WHERE account_id = $1", a.AccountId) >= 1);
            Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM session s WHERE NOT EXISTS (SELECT 1 FROM sign_in_method m WHERE m.id = s.method_id)"));
        }
    }
}
