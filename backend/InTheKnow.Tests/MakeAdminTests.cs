using System.Net;
using Xunit;

namespace InTheKnow.Tests;

// specs/004 FR-033b, research R13: the server command, called directly.
public class MakeAdminTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static readonly DateTimeOffset Now = Harness.T0;

    [Fact]
    public async Task EmptyDatabaseQueuesAnAdminInvite()
    {
        await using var db = await TestDb.CreateAsync();
        Assert.Equal("invite queued", await Accounts.MakeAdminAsync(db.DataSource, "admin@example.nl", "en", Now));
        Assert.Equal([["admin@example.nl", true, null, Now.AddDays(7).UtcDateTime]],
            await db.RowsAsync("SELECT email, grants_admin, created_by, expires_at FROM invite WHERE kind = 'named'"));
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'invite' AND args->>'admin' = 'true'"));
        Assert.Equal([[null, null, """{"via": "server"}"""]], await db.RowsAsync("SELECT account_id, source, detail::text FROM security_event WHERE type = 'adminMadeFromServer'"));

        // Again: the same invite, renewed, with a new email; no second invite.
        var later = Now.AddDays(2);
        Assert.Equal("invite queued", await Accounts.MakeAdminAsync(db.DataSource, "Admin@example.nl", "en", later));
        Assert.Equal([[later.AddDays(7).UtcDateTime]], await db.RowsAsync("SELECT expires_at FROM invite"));
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'invite'")); // the unsent one was replaced

        // After it lapsed: a new invite replaces the expired one, without an error.
        var lapsed = later.AddDays(8);
        Assert.Equal("invite queued", await Accounts.MakeAdminAsync(db.DataSource, "admin@example.nl", "en", lapsed));
        Assert.Equal([[lapsed.AddDays(7).UtcDateTime]], await db.RowsAsync("SELECT expires_at FROM invite WHERE lower(email) = 'admin@example.nl'"));
    }

    [Fact]
    public async Task ExistingAccounts()
    {
        await using var h = await SignInTests.HostAsync();
        var db = h.Db;
        var confirmed = await h.SignInAsync();
        Assert.Equal("promoted", await Accounts.MakeAdminAsync(db.DataSource, confirmed.Email.ToUpperInvariant(), "en", Now));
        Assert.Equal([["admin"]], await db.RowsAsync("SELECT role FROM account WHERE id = $1", confirmed.AccountId));
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'roleChanged' AND account_id = $1", confirmed.AccountId));
        var r = await confirmed.Client.GetAsync("/api/admin/calendars", Ct);
        Assert.Equal((HttpStatusCode.Forbidden, "two_factor_required"), (r.StatusCode, await TestHost.ReasonAsync(r)));

        var disabled = await h.SignInAsync();
        await db.ExecAsync("UPDATE account SET disabled_at = now() WHERE id = $1", disabled.AccountId);
        Assert.Equal("promoted", await Accounts.MakeAdminAsync(db.DataSource, disabled.Email, "en", Now));
        Assert.Equal([["admin", null]], await db.RowsAsync("SELECT role, disabled_at FROM account WHERE id = $1", disabled.AccountId));
        Assert.Equal(["enabled", "roleChanged"],
            (await db.RowsAsync("SELECT kind FROM account_message WHERE account_id = $1 ORDER BY id", disabled.AccountId)).Select(x => x[0]));

        var unconfirmed = await db.ScalarAsync("INSERT INTO account (email, language, created_at) VALUES ('pending@example.nl', 'en', now()) RETURNING id");
        Assert.Equal("invite queued", await Accounts.MakeAdminAsync(db.DataSource, "pending@example.nl", "nl", Now));
        Assert.Equal([["user"]], await db.RowsAsync("SELECT role FROM account WHERE id = $1", unconfirmed));
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM invite WHERE email = 'pending@example.nl' AND grants_admin AND language = 'nl'"));
    }

    [Fact]
    public async Task SchemaAndInstanceLock()
    {
        await using var db = await TestDb.CreateAsync();
        Assert.True(await Db.SchemaCurrentAsync(db.DataSource));
        await using (var held = await Db.TakeInstanceLockAsync(db.DataSource))
            Assert.Equal("invite queued", await Accounts.MakeAdminAsync(db.DataSource, "admin@example.nl", "en", Now));
        await db.ExecAsync("DELETE FROM schema_migration WHERE version = 4");
        Assert.False(await Db.SchemaCurrentAsync(db.DataSource));
    }

    // US7-5, US8-6: the admin invite from the server is accepted, and the admin routes open once 2FA is on.
    [Fact]
    public async Task AcceptTheServerInvite()
    {
        await using var d = await SignUpTests.HostAsync(open: false);
        Assert.Equal("invite queued", await Accounts.MakeAdminAsync(d.Db.DataSource, "admin@example.nl", "en", d.Now));
        var r = await SignUpTests.AcceptAsync(d, await SignUpTests.NamedTokenAsync(d, "admin@example.nl"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = System.Text.Json.Nodes.JsonNode.Parse(await r.Content.ReadAsStringAsync(Ct))!;
        Assert.False(body["secondFactorRequired"]!.GetValue<bool>());
        var admin = new SignedIn(0, 0, body["token"]!.GetValue<string>(), "admin@example.nl", d.H.ClientFor(body["token"]!.GetValue<string>()));
        Assert.Equal([["admin", true]], await d.Db.RowsAsync("SELECT role, confirmed_at IS NOT NULL FROM account WHERE email = 'admin@example.nl'"));
        Assert.True((await admin.JsonAsync("/api/me/channels"))["items"]!.AsArray().Single()!["accountAddress"]!.GetValue<bool>());
        var locked = await admin.Client.GetAsync("/api/admin/calendars", Ct);
        Assert.Equal((HttpStatusCode.Forbidden, "two_factor_required"), (locked.StatusCode, await TestHost.ReasonAsync(locked)));
        await TwoFactorTests.EnableAsync(d.H, admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync("/api/admin/calendars", Ct)).StatusCode);

        // A lapsed invite, renewed from the server, can be accepted with its new link.
        d.H.Time.Advance(TimeSpan.FromDays(8));
        Assert.Equal("invite queued", await Accounts.MakeAdminAsync(d.Db.DataSource, "second@example.nl", "en", d.Now - TimeSpan.FromDays(8)));
        Assert.Equal("invite queued", await Accounts.MakeAdminAsync(d.Db.DataSource, "second@example.nl", "en", d.Now));
        Assert.Equal(HttpStatusCode.OK, (await SignUpTests.AcceptAsync(d, await SignUpTests.NamedTokenAsync(d, "second@example.nl"))).StatusCode);
    }

    // FR-032a: a session that turned 2FA on and then off again proves no second factor, so promoting the account from
    // the server leaves its admin routes locked.
    [Fact]
    public async Task TwoFactorTurnedOffLeavesAdminRoutesLocked()
    {
        await using var h = await SignInTests.HostAsync();
        var a = await h.SignInViaAsync("password", (await h.SignInAsync()).Email);
        var (secret, _) = await TwoFactorTests.EnableAsync(h, a);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.NoContent, (await TwoFactorTests.ReauthAsync(a, "password", TwoFactorTests.Code(secret, h.Time.GetUtcNow()))).Item1);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.DeleteAsync("/api/me/2fa", Ct)).StatusCode);
        Assert.Equal("promoted", await Accounts.MakeAdminAsync(h.Db.DataSource, a.Email, "en", Now));
        var r = await a.Client.GetAsync("/api/admin/calendars", Ct);
        Assert.Equal((HttpStatusCode.Forbidden, "two_factor_required"), (r.StatusCode, await TestHost.ReasonAsync(r)));
    }
}
