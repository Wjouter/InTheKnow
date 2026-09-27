using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InTheKnow.Tests;

// specs/004 US1 (sign-up and confirmation), FR-001 to FR-007, FR-012a, SC-003.
public class SignUpTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static async Task<DeliveryHost> HostAsync(bool open = true, params (string Key, string Value)[] extra)
    {
        var d = await DeliveryHost.CreateWithAsync([await EmailUnderTest.CreateAsync()], TestSignIn.Register, extra);
        if (open) await d.Db.ExecAsync("UPDATE signup_settings SET open = true");
        return d;
    }

    internal static async Task<HttpResponseMessage> PostAsync(DeliveryHost d, string path, object body, string? source = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (source is not null) request.Headers.Add("X-Test-Source", source);
        return await d.H.Client.SendAsync(request, Ct);
    }

    internal static Task<HttpResponseMessage> SignUpAsync(DeliveryHost d, string email, string password = TestHost.Password, string language = "en",
                                                          string? source = null) =>
        PostAsync(d, "/api/auth/sign-up", new { email, password, language }, source);

    internal static List<(string Target, Message Message)> Mails(DeliveryHost d, string kind) =>
        [.. d.C.Delivered.Where(x => x.Message.AccountKind == kind)];

    internal static string TokenOf(Message m) => m.Link![(m.Link!.IndexOf("?token=", StringComparison.Ordinal) + 7)..];

    static async Task Expect(HttpResponseMessage r, HttpStatusCode status, string? reason = null)
    {
        Assert.Equal(status, r.StatusCode);
        if (reason is not null) Assert.Equal(reason, await TestHost.ReasonAsync(r));
    }

    static Task<long> CountAsync(DeliveryHost d, string sql, params object[] args) => d.Db.ScalarAsync(sql, args);

    [Fact]
    public async Task SignUpAndConfirm()
    {
        await using var d = await HostAsync();
        const string email = "student@example.nl";

        // US1-1
        var r = await SignUpAsync(d, email, language: "nl");
        await Expect(r, HttpStatusCode.Accepted);
        Assert.Equal("", await r.Content.ReadAsStringAsync(Ct));
        Assert.Equal([["nl", null, null]], await d.Db.RowsAsync("SELECT language, time_zone, confirmed_at FROM account WHERE email = $1", email));
        await d.NotifyAsync(2);
        var (to, mail) = Assert.Single(Mails(d, "confirm"));
        Assert.Equal((email, "nl"), (to, mail.Language));
        var token = TokenOf(mail);

        // US1-2: opening the link does nothing; the explicit POST with the password confirms.
        Assert.False((await d.H.Client.GetAsync($"/account/confirm?token={token}", Ct)).IsSuccessStatusCode); // a frontend page, not the API
        await Expect(await PostAsync(d, "/api/auth/confirm", new { token, password = "wrong-password-0123" }), HttpStatusCode.BadRequest, "password_incorrect");
        Assert.Equal(0, await CountAsync(d, "SELECT count(*) FROM account WHERE email = $1 AND confirmed_at IS NOT NULL", email));

        // US1-3: signing in before confirming.
        await Expect(await PostAsync(d, "/api/auth/sign-in", new { email, password = TestHost.Password }), HttpStatusCode.Forbidden, "not_confirmed");
        await Expect(await PostAsync(d, "/api/auth/sign-in", new { email, password = "wrong-password-0123" }), HttpStatusCode.Unauthorized, "invalid_credentials");

        await Expect(await PostAsync(d, "/api/auth/confirm", new { token, password = TestHost.Password }), HttpStatusCode.NoContent);
        var id = await CountAsync(d, "SELECT id FROM account WHERE email = $1 AND confirmed_at IS NOT NULL", email);
        Assert.Equal(1, await CountAsync(d, "SELECT count(*) FROM security_event WHERE type = 'accountConfirmed' AND account_id = $1", id));
        await Expect(await PostAsync(d, "/api/auth/confirm", new { token, password = TestHost.Password }), HttpStatusCode.BadRequest, "token_invalid");
        await Expect(await PostAsync(d, "/api/auth/sign-in", new { email, password = TestHost.Password }), HttpStatusCode.OK);

        // A link 24 h + 1 s after sending has expired.
        await SignUpAsync(d, "late@example.nl");
        await d.NotifyAsync(2);
        var late = TokenOf(Mails(d, "confirm").Last().Message);
        d.H.Time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        await Expect(await PostAsync(d, "/api/auth/confirm", new { token = late, password = TestHost.Password }), HttpStatusCode.BadRequest, "token_invalid");
    }

    // FR-004: the same answer for every address; only an unconfirmed one gets a link, at most 3 per 24 h.
    [Fact]
    public async Task ConfirmationRequests()
    {
        await using var d = await HostAsync();
        await SignUpAsync(d, "new@example.nl");
        await d.NotifyAsync(2);
        var first = TokenOf(Mails(d, "confirm").Single().Message);
        foreach (var email in new[] { "unknown@example.nl", d.S1.Email, "NEW@example.nl" })
            await Expect(await PostAsync(d, "/api/auth/confirmation", new { email }), HttpStatusCode.Accepted);
        await d.NotifyAsync(3);
        Assert.Equal(2, Mails(d, "confirm").Count);
        await Expect(await PostAsync(d, "/api/auth/confirm", new { token = first, password = TestHost.Password }), HttpStatusCode.BadRequest, "token_invalid");

        await Expect(await PostAsync(d, "/api/auth/confirmation", new { email = "new@example.nl" }), HttpStatusCode.Accepted); // the 3rd
        await Expect(await PostAsync(d, "/api/auth/confirmation", new { email = "new@example.nl" }), HttpStatusCode.Accepted); // over the cap
        Assert.Equal(3, await CountAsync(d, "SELECT count(*) FROM account_message WHERE kind = 'confirm'"));
    }

    // US1-7, SC-003: a confirmed address gets the same answer as a new one, and its owner a notice (at most 3 a day).
    [Fact]
    public async Task ExistingConfirmedAddress()
    {
        await using var d = await HostAsync();
        var fresh = await SignUpAsync(d, "brand-new@example.nl");
        var owner = d.S1;
        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 4; i++) responses.Add(await SignUpAsync(d, owner.Email.ToUpperInvariant()));
        static string Shape(HttpResponseMessage r) =>
            $"{(int)r.StatusCode}|{string.Join(";", r.Headers.Concat(r.Content.Headers).Where(h => h.Key != "Date").Select(h => $"{h.Key}={string.Join(",", h.Value)}"))}";
        foreach (var r in responses)
        {
            Assert.Equal(Shape(fresh), Shape(r));
            Assert.Equal(await fresh.Content.ReadAsStringAsync(Ct), await r.Content.ReadAsStringAsync(Ct));
        }
        Assert.Equal(1, await CountAsync(d, "SELECT count(*) FROM account WHERE lower(email) = lower($1)", owner.Email));
        Assert.Equal(3, await CountAsync(d, "SELECT count(*) FROM account_message WHERE kind = 'signUpAttempt' AND account_id = $1", owner.AccountId));
        await d.NotifyAsync(5);
        Assert.Equal(3, Mails(d, "signUpAttempt").Count(m => m.Target == owner.Email));
    }

    // FR-006: an unconfirmed address takes the new password in place and gets a new link; a disabled one changes nothing.
    [Fact]
    public async Task ExistingUnconfirmedOrDisabledAddress()
    {
        await using var d = await HostAsync();
        await SignUpAsync(d, "pending@example.nl");
        await d.NotifyAsync(2);
        var old = TokenOf(Mails(d, "confirm").Single().Message);
        var (methodId, secret) = await d.Db.RowsAsync("SELECT m.id, m.secret FROM sign_in_method m JOIN account a ON a.id = m.account_id WHERE a.email = 'pending@example.nl'")
            is [[long i, string s]] ? (i, s) : throw new InvalidOperationException();
        await Expect(await SignUpAsync(d, "PENDING@example.nl", "another-password-456"), HttpStatusCode.Accepted);
        var after = (await d.Db.RowsAsync("SELECT id, secret FROM sign_in_method WHERE id = $1", methodId)).Single();
        Assert.NotEqual(secret, after[1]);
        await d.NotifyAsync(2);
        var fresh = TokenOf(Mails(d, "confirm").Last().Message);
        await Expect(await PostAsync(d, "/api/auth/confirm", new { token = old, password = "another-password-456" }), HttpStatusCode.BadRequest, "token_invalid");
        await Expect(await PostAsync(d, "/api/auth/confirm", new { token = fresh, password = "another-password-456" }), HttpStatusCode.NoContent);

        await d.Db.ExecAsync("UPDATE account SET disabled_at = $2 WHERE id = $1", d.S2.AccountId, d.Now);
        var queued = await CountAsync(d, "SELECT count(*) FROM account_message");
        var secrets = await d.Db.RowsAsync("SELECT secret FROM sign_in_method WHERE account_id = $1", d.S2.AccountId);
        await Expect(await SignUpAsync(d, d.S2.Email, "yet-another-pass-789"), HttpStatusCode.Accepted);
        Assert.Equal(queued, await CountAsync(d, "SELECT count(*) FROM account_message"));
        Assert.Equal(secrets, await d.Db.RowsAsync("SELECT secret FROM sign_in_method WHERE account_id = $1", d.S2.AccountId));
    }

    // FR-007, FR-002.
    [Fact]
    public async Task Rules()
    {
        await using var d = await HostAsync();
        var common = await CommonPasswordAsync();
        foreach (var (email, password, language, reason) in new[]
        {
            ("x@example.nl", new string('p', 11), "en", "password_length"),
            ("x@example.nl", new string('p', 129), "en", "password_length"),
            ("x@example.nl", common.ToUpperInvariant(), "en", "password_common"),
            ("Long.Address@example.nl", "long.address@EXAMPLE.nl", "en", "password_is_email"),
            ("x@example.nl", TestHost.Password, "de", "invalid_language"),
            ("a", TestHost.Password, "en", "invalid_email"),
            ("Name <a@b.nl>", TestHost.Password, "en", "invalid_email"),
            (new string('a', 244) + "@example.nl", TestHost.Password, "en", "invalid_email"),
        })
            await Expect(await SignUpAsync(d, email, password, language), HttpStatusCode.BadRequest, reason);
        Assert.Equal(3, await CountAsync(d, "SELECT count(*) FROM account")); // S1 to S3

        await Expect(await SignUpAsync(d, " Student@Example.nl ", "twelve chars"), HttpStatusCode.Accepted);
        await Expect(await SignUpAsync(d, "student@example.nl"), HttpStatusCode.Accepted);
        Assert.Equal(["Student@Example.nl"], (await d.Db.RowsAsync("SELECT email FROM account WHERE lower(email) = 'student@example.nl'")).Select(r => r[0]));
    }

    internal static async Task<string> CommonPasswordAsync()
    {
        var asm = typeof(Passwords).Assembly;
        using var reader = new StreamReader(asm.GetManifestResourceStream(asm.GetManifestResourceNames().Single(n => n.EndsWith("common-passwords.txt")))!);
        while (await reader.ReadLineAsync(Ct) is { } line)
            if (line.Length >= 12 && !line.StartsWith('#')) return line;
        throw new InvalidOperationException("no common password");
    }

    // FR-001a, FR-001b: without invites, only the settings decide, from the address alone.
    [Fact]
    public async Task SignUpSettings()
    {
        await using var d = await HostAsync(open: false);
        await Expect(await SignUpAsync(d, "x@example.nl"), HttpStatusCode.Forbidden, "sign_up_by_invitation");
        await d.Db.ExecAsync("UPDATE signup_settings SET allowed_domains = '{example.nl}'");
        await Expect(await SignUpAsync(d, "x@example.nl"), HttpStatusCode.Accepted);
        await Expect(await SignUpAsync(d, "x@EXAMPLE.nl"), HttpStatusCode.Accepted);
        await Expect(await SignUpAsync(d, "x@gmail.com"), HttpStatusCode.Forbidden, "sign_up_by_invitation");
        await Expect(await SignUpAsync(d, "x@student.example.nl"), HttpStatusCode.Forbidden, "sign_up_by_invitation");
        await TestHost.AccountAsync(d.Db.DataSource, "y@gmail.com");
        await Expect(await SignUpAsync(d, "y@gmail.com"), HttpStatusCode.Forbidden, "sign_up_by_invitation");
    }

    // FR-005: unconfirmed accounts go after 30 days.
    [Fact]
    public async Task UnconfirmedAccountsExpire()
    {
        await using var d = await HostAsync();
        await SignUpAsync(d, "old@example.nl");
        d.H.Time.Advance(TimeSpan.FromDays(1));
        await SignUpAsync(d, "newer@example.nl");
        var accounts = d.H.Factory.Services.GetRequiredService<Accounts>();
        await accounts.CleanupAsync(d.Now + TimeSpan.FromDays(29) + TimeSpan.FromSeconds(1), Ct);
        Assert.Equal(["newer@example.nl"], (await d.Db.RowsAsync("SELECT email FROM account WHERE confirmed_at IS NULL")).Select(r => r[0]));
        await accounts.CleanupAsync(d.Now + TimeSpan.FromDays(30) + TimeSpan.FromSeconds(1), Ct);
        Assert.Empty(await d.Db.RowsAsync("SELECT email FROM account WHERE confirmed_at IS NULL"));
    }

    // FR-012a: sign-up requests per source.
    [Fact]
    public async Task RequestsPerSource()
    {
        await using var d = await HostAsync(true, ("InTheKnow:Accounts:SourceRequestsPerHour", "10"));
        for (var i = 0; i < 10; i++) await Expect(await SignUpAsync(d, $"u{i}@example.nl", source: "192.0.2.1"), HttpStatusCode.Accepted);
        var refused = await SignUpAsync(d, "u10@example.nl", source: "192.0.2.1");
        await Expect(refused, HttpStatusCode.TooManyRequests, "too_many_requests");
        Assert.NotNull(refused.Headers.RetryAfter);
        await Expect(await SignUpAsync(d, "u10@example.nl", source: "192.0.2.2"), HttpStatusCode.Accepted);
    }

    // --- US8: invites (FR-001a to FR-001e, SC-012) -----------------------------------------------------------------

    internal static async Task<(string? Token, long Id)> InviteAsync(DeliveryHost d, SignedIn op, object body)
    {
        var r = await op.Client.PostAsJsonAsync("/api/admin/invites", body, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var invite = System.Text.Json.Nodes.JsonNode.Parse(await r.Content.ReadAsStringAsync(Ct))!;
        var link = invite["link"]?.GetValue<string>();
        return (link?[(link.IndexOf("?invite=", StringComparison.Ordinal) + 8)..], invite["id"]!.GetValue<long>());
    }

    internal static async Task<string> NamedTokenAsync(DeliveryHost d, string email)
    {
        await d.NotifyAsync(3);
        return TokenOf(Mails(d, "invite").Last(m => m.Target.Equals(email, StringComparison.OrdinalIgnoreCase)).Message);
    }

    internal static Task<HttpResponseMessage> AcceptAsync(DeliveryHost d, string token, string password = TestHost.Password, string? language = null) =>
        PostAsync(d, "/api/auth/invite/accept", new { token, password, language });

    static Task<HttpResponseMessage> SignUpWithInviteAsync(DeliveryHost d, string email, string invite) =>
        PostAsync(d, "/api/auth/sign-up", new { email, password = TestHost.Password, language = "en", invite });

    // SC-012: sign-up is allowed exactly when it is open, the domain matches, or a valid invite is used.
    [Fact]
    public async Task SignUpMatrix()
    {
        await using var d = await HostAsync(open: false);
        var op = await d.H.SignInAsync(admin: true, secondFactor: true);
        const string email = "x@example.nl";
        foreach (var open in new[] { true, false })
            foreach (var domains in new[] { "{}", "{example.nl}", "{other.nl}" })
                foreach (var invite in new[] { "none", "named", "shareable" })
                {
                    await d.Db.ExecAsync("DELETE FROM account WHERE lower(email) = $1", email);
                    await d.Db.ExecAsync("DELETE FROM invite");
                    await d.Db.ExecAsync("UPDATE signup_settings SET open = $1, allowed_domains = $2::text[]", open, domains);
                    var allowed = open || domains == "{example.nl}" || invite != "none";
                    var r = invite switch
                    {
                        "named" => await AcceptAsync(d, await NamedAsync()),
                        "shareable" => await SignUpWithInviteAsync(d, email, (await InviteAsync(d, op, new { kind = "shareable", language = "en" })).Token!),
                        _ => await SignUpAsync(d, email),
                    };
                    var what = $"open={open} domains={domains} invite={invite}";
                    Assert.True(r.IsSuccessStatusCode == allowed, $"{what}: {r.StatusCode}");
                    var state = await d.Db.RowsAsync("SELECT confirmed_at IS NOT NULL FROM account WHERE lower(email) = $1", email);
                    Assert.True(allowed ? state.Single()[0] is bool c && c == (invite == "named") : state.Count == 0, what);
                    if (!allowed) Assert.Equal("sign_up_by_invitation", await TestHost.ReasonAsync(r));
                }

        async Task<string> NamedAsync()
        {
            await InviteAsync(d, op, new { kind = "named", email, language = "en" });
            return await NamedTokenAsync(d, email);
        }
    }

    // US8-7: a shareable invite counts its uses, atomically.
    [Fact]
    public async Task ShareableInviteUses()
    {
        await using var d = await HostAsync(open: false);
        var op = await d.H.SignInAsync(admin: true, secondFactor: true);
        var (token, _) = await InviteAsync(d, op, new { kind = "shareable", language = "en", maxUses = 30 });
        for (var i = 0; i < 30; i++) await Expect(await SignUpWithInviteAsync(d, $"guest{i}@gmail.com", token!), HttpStatusCode.Accepted);
        Assert.Equal(30, await d.Db.ScalarAsync("SELECT count(*) FROM account WHERE email LIKE 'guest%' AND confirmed_at IS NULL"));
        await Expect(await SignUpWithInviteAsync(d, "guest30@gmail.com", token!), HttpStatusCode.Gone, "invite_invalid");

        var (race, _) = await InviteAsync(d, op, new { kind = "shareable", language = "en", maxUses = 29 });
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(() => SignUpWithInviteAsync(d, $"race{i}@gmail.com", race!), Ct)));
        Assert.Equal(29, results.Count(r => r.StatusCode == HttpStatusCode.Accepted));

        // Edge case "Shareable invite and existing addresses": the same answer, and a use is counted.
        var (one, id) = await InviteAsync(d, op, new { kind = "shareable", language = "en", maxUses = 2 });
        await Expect(await SignUpWithInviteAsync(d, d.S1.Email, one!), HttpStatusCode.Accepted);
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT used FROM invite WHERE id = $1", id));
    }

    // R10: each route refuses the other kind's token, and revoked, expired or unknown ones.
    [Fact]
    public async Task InviteRouteSplit()
    {
        await using var d = await HostAsync(open: false);
        var op = await d.H.SignInAsync(admin: true, secondFactor: true);
        var (shareable, shareableId) = await InviteAsync(d, op, new { kind = "shareable", language = "en" });
        await InviteAsync(d, op, new { kind = "named", email = "named@gmail.com", language = "en" });
        var named = await NamedTokenAsync(d, "named@gmail.com");
        await Expect(await AcceptAsync(d, shareable!), HttpStatusCode.Gone, "invite_invalid");
        await Expect(await SignUpWithInviteAsync(d, "named@gmail.com", named), HttpStatusCode.Gone, "invite_invalid");
        await Expect(await SignUpWithInviteAsync(d, "x@gmail.com", SignIn.NewToken().Token), HttpStatusCode.Gone, "invite_invalid");
        await Expect(await AcceptAsync(d, SignIn.NewToken().Token), HttpStatusCode.Gone, "invite_invalid");
        Assert.Equal(HttpStatusCode.NoContent, (await op.Client.DeleteAsync($"/api/admin/invites/{shareableId}", Ct)).StatusCode);
        await Expect(await SignUpWithInviteAsync(d, "x@gmail.com", shareable!), HttpStatusCode.Gone, "invite_invalid");
        d.H.Time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        await Expect(await AcceptAsync(d, named), HttpStatusCode.Gone, "invite_invalid");
    }

    // Edge case: the address got a confirmed account after the named invite was made.
    [Fact]
    public async Task NamedInviteForAnAddressConfirmedSince()
    {
        await using var d = await HostAsync(open: false);
        var op = await d.H.SignInAsync(admin: true, secondFactor: true);
        await InviteAsync(d, op, new { kind = "named", email = "x@example.nl", language = "en" });
        var token = await NamedTokenAsync(d, "x@example.nl");
        await d.Db.ExecAsync("UPDATE signup_settings SET allowed_domains = '{example.nl}'");
        await SignUpAsync(d, "x@example.nl");
        await d.NotifyAsync(3);
        await Expect(await PostAsync(d, "/api/auth/confirm", new { token = TokenOf(Mails(d, "confirm").Single().Message), password = TestHost.Password }),
            HttpStatusCode.NoContent);
        var before = await d.Db.RowsAsync("SELECT row_to_json(a)::text FROM account a WHERE email = 'x@example.nl'");
        await Expect(await AcceptAsync(d, token, "another-password-1"), HttpStatusCode.Gone, "invite_invalid");
        Assert.Equal(0, await d.Db.ScalarAsync("SELECT count(*) FROM invite"));
        Assert.Equal(before, await d.Db.RowsAsync("SELECT row_to_json(a)::text FROM account a WHERE email = 'x@example.nl'"));
    }

    // R10: used-up invites, and invites 30 days past their expiry, are deleted.
    [Fact]
    public async Task InviteCleanup()
    {
        await using var d = await HostAsync();
        var accounts = d.H.Factory.Services.GetRequiredService<Accounts>();
        var t = d.Now;
        async Task<long> Insert(string kind, int maxUses, int used, DateTimeOffset expires) => await d.Db.ScalarAsync("""
            INSERT INTO invite (kind, email, language, max_uses, used, token_hash, created_at, expires_at)
            VALUES ($1, CASE WHEN $1 = 'named' THEN gen_random_uuid() || '@example.nl' END, 'en', $2, $3, CASE WHEN $1 = 'shareable' THEN $4 END, $5, $6) RETURNING id
            """, kind, maxUses, used, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), expires - TimeSpan.FromDays(1), expires);
        var usedUp = await Insert("shareable", 2, 2, t + TimeSpan.FromDays(5));
        var old = await Insert("named", 1, 0, t - TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1));
        var recent = await Insert("named", 1, 0, t - TimeSpan.FromDays(29));
        await accounts.CleanupAsync(t, Ct);
        Assert.Equal([recent], (await d.Db.RowsAsync("SELECT id FROM invite WHERE id = ANY($1)", (object)new[] { usedUp, old, recent })).Select(r => (long)r[0]!));
    }
}
