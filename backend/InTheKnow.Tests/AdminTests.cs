using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using Xunit;
using static InTheKnow.Tests.LinkIdentityTests;

namespace InTheKnow.Tests;

// specs/004 US7, FR-019, FR-032 to FR-033c, SC-013.
public class AdminTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task Expect(HttpResponseMessage r, HttpStatusCode status, string? reason = null)
    {
        Assert.Equal(status, r.StatusCode);
        if (reason is not null) Assert.Equal(reason, await TestHost.ReasonAsync(r));
    }

    // Every /api/admin route: this list must be there, and T066 adds sign-up settings and invites.
    internal static readonly List<string> Required =
        ["/api/admin/calendars", "/api/admin/presets", "/api/admin/channel-types", "/api/admin/accounts", "/api/admin/signup-settings", "/api/admin/invites"];

    static Task<HttpResponseMessage> Send(HttpClient client, string method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request, Ct);
    }

    // The current authenticator code of an account whose 2FA was inserted directly.
    static async Task<string> CodeAsync(ApiHost h, long account, TimeSpan? later = null) =>
        TwoFactorTests.Code(Base32Encoding.ToString((byte[])(await h.Db.RowsAsync("SELECT secret FROM two_factor WHERE account_id = $1", account)).Single()[0]!),
            h.Time.GetUtcNow() + (later ?? TimeSpan.Zero));

    // SC-013, US7-2, US7-3, US7-8.
    [Fact]
    public async Task EveryAdminRouteNeedsAnAdminWithASecondFactor()
    {
        await using var h = await SignInTests.HostAsync();
        var routes = h.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/admin", StringComparison.Ordinal))
            .SelectMany(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Select(m => (Method: m, Path: ApiTests.Fill(e.RoutePattern.RawText!))))
            .ToList();
        foreach (var path in Required) Assert.Contains(routes, r => r.Path == path);
        var user = await h.SignInAsync();
        var adminWithout2fa = await h.SignInAsync(admin: true);
        using var withKey = h.Factory.CreateClient();
        withKey.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestDb.ApiKey);
        foreach (var (method, path) in routes)
        {
            await Expect(await Send(user.Client, method, path, new { }), HttpStatusCode.Forbidden, "admin_required");
            await Expect(await Send(adminWithout2fa.Client, method, path, new { }), HttpStatusCode.Forbidden, "two_factor_required");
            await Expect(await Send(withKey, method, path, new { }), HttpStatusCode.Unauthorized, "not_signed_in");
        }

        // US7-1
        var op = await h.SignInAsync(admin: true, secondFactor: true);
        await Expect(await op.Client.GetAsync("/api/admin/calendars", Ct), HttpStatusCode.OK);
        await Expect(await Send(op.Client, "POST", "/api/admin/presets", new { name = "Everything", rules = Array.Empty<object>() }), HttpStatusCode.Created);
    }

    // US7-11: a second factor counts per session.
    [Fact]
    public async Task SecondFactorIsPerSession()
    {
        await using var h = await SignInTests.HostAsync();
        var admin = await h.SignInViaAsync("password", (await h.SignInAsync(admin: true)).Email);
        var before = await h.SignInViaAsync("password", admin.Email);
        var (secret, _) = await TwoFactorTests.EnableAsync(h, admin);
        await Expect(await admin.Client.GetAsync("/api/admin/calendars", Ct), HttpStatusCode.OK);
        await Expect(await before.Client.GetAsync("/api/admin/calendars", Ct), HttpStatusCode.Forbidden, "two_factor_required");
        h.Time.Advance(TimeSpan.FromSeconds(30));
        var (token, required) = await TwoFactorTests.SignInRawAsync(h, "password", admin.Email);
        Assert.True(required);
        var fresh = h.ClientFor(token);
        await Expect(await fresh.PostAsJsonAsync("/api/auth/sign-in/code", new { code = TwoFactorTests.Code(secret, h.Time.GetUtcNow()) }, Ct), HttpStatusCode.OK);
        await Expect(await fresh.GetAsync("/api/admin/calendars", Ct), HttpStatusCode.OK);
    }

    // US7-12.
    [Fact]
    public async Task ListAccounts()
    {
        await using var h = await SignInTests.HostAsync();
        var op = await h.SignInAsync(admin: true, secondFactor: true);
        await h.SignInAsync("Someone@Example.nl");
        await h.SignInAsync("other@elsewhere.org");
        var page = await op.JsonAsync("/api/admin/accounts?q=EXAMPLE");
        var items = page["items"]!.AsArray();
        Assert.Equal(["id", "email", "role", "confirmed", "disabled", "twoFactor", "createdAt"], items[0]!.AsObject().Select(kv => kv.Key));
        Assert.Contains(items, i => i!["email"]!.GetValue<string>() == "Someone@Example.nl");
        Assert.DoesNotContain(items, i => i!["email"]!.GetValue<string>() == "other@elsewhere.org");
        Assert.DoesNotContain("AQAAAA", page.ToJsonString());
        var first = await op.JsonAsync("/api/admin/accounts?limit=1");
        Assert.Single(first["items"]!.AsArray());
        var next = first["next"]!.GetValue<long>();
        Assert.NotEqual(next, (await op.JsonAsync($"/api/admin/accounts?limit=1&after={next}"))["items"]![0]!["id"]!.GetValue<long>());
        foreach (var limit in new[] { 0, 501 })
            await Expect(await op.Client.GetAsync($"/api/admin/accounts?limit={limit}", Ct), HttpStatusCode.BadRequest, "invalid_limit");
    }

    static Task<HttpResponseMessage> RoleAsync(SignedIn op, long id, string role) => Send(op.Client, "PUT", $"/api/admin/accounts/{id}/role", new { role });

    // FR-033a, US7-7, US7-9.
    [Fact]
    public async Task RoleChanges()
    {
        await using var h = await SignInTests.HostAsync();
        var op = await h.SignInAsync(admin: true, secondFactor: true);
        var plain = await h.SignInAsync();
        var with2fa = await h.SignInAsync(secondFactor: true);
        var unconfirmed = await h.Db.ScalarAsync("INSERT INTO account (email, language, created_at) VALUES ('u@example.nl', 'en', now()) RETURNING id");
        var disabled = await h.SignInAsync(secondFactor: true);
        await h.Db.ExecAsync("UPDATE account SET disabled_at = now() WHERE id = $1", disabled.AccountId);

        await Expect(await RoleAsync(op, plain.AccountId, "owner"), HttpStatusCode.BadRequest, "invalid_role");
        await Expect(await RoleAsync(op, plain.AccountId, "admin"), HttpStatusCode.Conflict, "two_factor_required");
        await Expect(await RoleAsync(op, unconfirmed, "admin"), HttpStatusCode.Conflict, "not_confirmed");
        await Expect(await RoleAsync(op, disabled.AccountId, "admin"), HttpStatusCode.Conflict, "account_disabled");
        await Expect(await RoleAsync(op, 999_999_999, "admin"), HttpStatusCode.NotFound);
        var promoted = await RoleAsync(op, with2fa.AccountId, "admin");
        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        Assert.Equal("admin", JsonNode.Parse(await promoted.Content.ReadAsStringAsync(Ct))!["role"]!.GetValue<string>());
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'roleChanged' AND account_id = $1 AND args->>'role' = 'admin'", with2fa.AccountId));
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'roleChanged' AND account_id = $1 AND actor_id = $2", with2fa.AccountId, op.AccountId));

        await Expect(await RoleAsync(op, with2fa.AccountId, "user"), HttpStatusCode.OK); // demoting another admin
        await Expect(await RoleAsync(op, op.AccountId, "user"), HttpStatusCode.Conflict, "last_admin");
        var (status, _) = await TwoFactorTests.ReauthAsync(op, "password", await CodeAsync(h, op.AccountId));
        Assert.Equal(HttpStatusCode.NoContent, status);
        await Expect(await op.Client.DeleteAsync("/api/me", Ct), HttpStatusCode.Conflict, "last_admin");

        // US7-10: an admin cannot turn 2FA off, by the owner's route or the admin route.
        await Expect(await op.Client.DeleteAsync("/api/me/2fa", Ct), HttpStatusCode.Conflict, "demote_first");
        await Expect(await op.Client.DeleteAsync($"/api/admin/accounts/{op.AccountId}/2fa", Ct), HttpStatusCode.Conflict, "demote_first");

        // US7-15: disable and delete only apply to other, regular accounts.
        var other = await h.SignInAsync(admin: true, secondFactor: true);
        foreach (var id in new[] { other.AccountId, op.AccountId })
        {
            await Expect(await Send(op.Client, "POST", $"/api/admin/accounts/{id}/disable"), HttpStatusCode.Conflict, "demote_first");
            await Expect(await op.Client.DeleteAsync($"/api/admin/accounts/{id}", Ct), HttpStatusCode.Conflict, "demote_first");
        }
    }

    // FR-019, FR-032a: an admin removes another admin's 2FA; the admin routes lock until it is on again.
    [Fact]
    public async Task RemoveAnotherAdminsTwoFactor()
    {
        await using var h = await SignInTests.HostAsync();
        var a = await h.SignInAsync(admin: true, secondFactor: true);
        var b = await h.SignInAsync(admin: true, secondFactor: true);
        await h.Db.ExecAsync("INSERT INTO recovery_code (account_id, code_hash) VALUES ($1, $2)", b.AccountId, new byte[32]);
        await Expect(await a.Client.DeleteAsync($"/api/admin/accounts/{b.AccountId}/2fa", Ct), HttpStatusCode.NoContent);
        Assert.Equal([["admin"]], await h.Db.RowsAsync("SELECT role FROM account WHERE id = $1", b.AccountId));
        Assert.Equal(0, await h.Db.ScalarAsync("SELECT count(*) FROM recovery_code WHERE account_id = $1", b.AccountId));
        await Expect(await b.Client.GetAsync("/api/me", Ct), HttpStatusCode.Unauthorized, "not_signed_in");
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'twoFactorOffByAdmin' AND account_id = $1", b.AccountId));
        Assert.Equal(1, await h.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'twoFactorRemovedByAdmin' AND account_id = $1 AND actor_id = $2", b.AccountId, a.AccountId));
        await Expect(await a.Client.DeleteAsync($"/api/admin/accounts/{b.AccountId}/2fa", Ct), HttpStatusCode.Conflict, "two_factor_off");

        var again = await h.SignInViaAsync("password", b.Email);
        await Expect(await again.Client.GetAsync("/api/admin/calendars", Ct), HttpStatusCode.Forbidden, "two_factor_required");
        await TwoFactorTests.EnableAsync(h, again);
        await Expect(await again.Client.GetAsync("/api/admin/calendars", Ct), HttpStatusCode.OK);
    }

    // US7-13, US7-4, US7-14, FR-033c.
    [Fact]
    public async Task DisableEnableAndDelete()
    {
        await using var d = await SignUpTests.HostAsync();
        var h = d.H;
        var op = await h.SignInAsync(admin: true, secondFactor: true);
        var user = await h.SignInViaAsync("password", d.S1.Email);
        var url = MtLink();
        await d.SubscribeAsync(user, url);
        var channel = await d.AddActiveChannelAsync(user, "notify@example.nl");
        await d.Db.ExecAsync("INSERT INTO channel_message (channel_id, kind, requested_at) VALUES ($1, 'test', $2)", channel, d.Now);
        await d.RecordChangesAsync(url, "mt-nl-edited.ics"); // pending, inside the collection window
        Assert.True(await d.Db.ScalarAsync("SELECT count(*) FROM delivery WHERE channel_id = $1 AND status = 'pending'", channel) > 0);

        var r = await Send(op.Client, "POST", $"/api/admin/accounts/{user.AccountId}/disable");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(JsonNode.Parse(await r.Content.ReadAsStringAsync(Ct))!["disabled"]!.GetValue<bool>());
        await Expect(await user.Client.GetAsync("/api/me", Ct), HttpStatusCode.Unauthorized, "not_signed_in");
        await Expect(await SignInTests.PasswordSignInAsync(h, user.Email, TestHost.Password), HttpStatusCode.Forbidden, "account_disabled");
        await Expect(await SignInTests.PasswordSignInAsync(h, user.Email, "wrong-password-0123"), HttpStatusCode.Unauthorized, "invalid_credentials");
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'accountDisabled' AND account_id = $1 AND actor_id = $2", user.AccountId, op.AccountId));
        Assert.Equal(0, await d.Db.ScalarAsync("SELECT count(*) FROM channel_message WHERE channel_id = $1", channel));
        var queued = await d.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'reset'");
        await Expect(await SignUpTests.PostAsync(d, "/api/auth/password-reset", new { email = user.Email }), HttpStatusCode.Accepted);
        Assert.Equal(queued, await d.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'reset'"));

        // Notifications stop: pending deliveries are skipped, and new changes create none.
        await d.AdvanceAndNotifyAsync(d.Window);
        Assert.Empty(d.To("notify@example.nl"));
        Assert.Equal(0, await d.Db.ScalarAsync("SELECT count(*) FROM delivery WHERE channel_id = $1 AND (status <> 'skipped' OR reason <> 'accountDisabled')", channel));
        var deliveries = await d.Db.ScalarAsync("SELECT count(*) FROM delivery WHERE channel_id = $1", channel);
        await d.RecordChangesAsync(url, "mt-nl.ics");
        Assert.Equal(deliveries, await d.Db.ScalarAsync("SELECT count(*) FROM delivery WHERE channel_id = $1", channel));
        Assert.Contains(SignUpTests.Mails(d, "disabled"), m => m.Target == user.Email);

        // US7-4 while disabled: the notice still goes.
        var with2fa = await h.SignInAsync(secondFactor: true);
        await Send(op.Client, "POST", $"/api/admin/accounts/{with2fa.AccountId}/disable");
        await Expect(await op.Client.DeleteAsync($"/api/admin/accounts/{with2fa.AccountId}/2fa", Ct), HttpStatusCode.NoContent);
        await d.NotifyAsync(5);
        Assert.Contains(SignUpTests.Mails(d, "twoFactorOffByAdmin"), m => m.Target == with2fa.Email);

        // Enable: signing in works again, and later changes are delivered; earlier ones are not.
        await Expect(await Send(op.Client, "POST", $"/api/admin/accounts/{user.AccountId}/enable"), HttpStatusCode.OK);
        await h.SignInViaAsync("password", user.Email);
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'accountEnabled' AND account_id = $1", user.AccountId));
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'enabled' AND account_id = $1", user.AccountId));
        await d.RecordChangesAsync(url, "mt-nl-edited.ics");
        Assert.True(await d.Db.ScalarAsync("SELECT count(*) FROM delivery WHERE channel_id = $1 AND status = 'pending'", channel) > 0);

        // US7-14: an admin deletes a regular account as the owner would.
        await Expect(await op.Client.DeleteAsync($"/api/admin/accounts/{user.AccountId}", Ct), HttpStatusCode.NoContent);
        Assert.Equal(0, await d.Db.ScalarAsync("SELECT count(*) FROM account WHERE id = $1", user.AccountId));
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'deleted' AND to_address = $1", user.Email));
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'accountDeleted' AND account_id = $1 AND actor_id = $2", user.AccountId, op.AccountId));
        await Expect(await op.Client.DeleteAsync($"/api/admin/accounts/{user.AccountId}", Ct), HttpStatusCode.NotFound);
    }

    // --- US8: sign-up settings and invites -------------------------------------------------------------------------

    // US8-10, FR-001d.
    [Fact]
    public async Task SignupSettings()
    {
        await using var d = await SignUpTests.HostAsync(open: false);
        var op = await d.H.SignInAsync(admin: true, secondFactor: true);
        await SignUpTests.InviteAsync(d, op, new { kind = "named", email = "pending@gmail.com", language = "en" });
        var accounts = await d.Db.RowsAsync("SELECT row_to_json(a)::text FROM account a ORDER BY id");
        Assert.Equal("""{"open":false,"allowedDomains":[]}""", (await op.JsonAsync("/api/admin/signup-settings")).ToJsonString());
        var put = await op.Client.PutAsJsonAsync("/api/admin/signup-settings", new { open = true, allowedDomains = new[] { " Inholland.NL ", "inholland.nl", "student.inholland.nl" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("""{"open":true,"allowedDomains":["inholland.nl","student.inholland.nl"]}""", (await op.JsonAsync("/api/admin/signup-settings")).ToJsonString());
        foreach (var (domains, reason) in new[] { (new[] { "exa mple.nl" }, "invalid_domain"), (new[] { "-x.nl" }, "invalid_domain"),
                                                  (Enumerable.Range(0, 101).Select(i => $"d{i}.nl").ToArray(), "too_many_domains") })
            await Expect(await op.Client.PutAsJsonAsync("/api/admin/signup-settings", new { open = false, allowedDomains = domains }, Ct), HttpStatusCode.BadRequest, reason);
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'signupSettingsChanged' AND actor_id = $1", op.AccountId));
        Assert.Equal(accounts, await d.Db.RowsAsync("SELECT row_to_json(a)::text FROM account a ORDER BY id"));
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM invite"));
    }

    // US8-5, US8-8, FR-001e.
    [Fact]
    public async Task CreateInvites()
    {
        await using var d = await SignUpTests.HostAsync(open: false);
        var op = await d.H.SignInAsync(admin: true, secondFactor: true);
        var r = await op.Client.PostAsJsonAsync("/api/admin/invites", new { kind = "named", email = "guest@gmail.com", language = "nl", validDays = 7 }, Ct);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var named = JsonNode.Parse(await r.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal(["id", "kind", "email", "language", "grantsAdmin", "maxUses", "used", "left", "expiresAt", "createdBy", "createdAt"],
            named.AsObject().Select(kv => kv.Key));
        Assert.Null(named["link"]);
        await d.NotifyAsync(3);
        var (to, mail) = SignUpTests.Mails(d, "invite").Single();
        Assert.Equal(("guest@gmail.com", "nl"), (to, mail.Language));
        Assert.StartsWith($"{EmailChannelTests.Frontend}/account/invite?token=", mail.Link);
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'inviteCreated' AND actor_id = $1", op.AccountId));

        var s = await op.Client.PostAsJsonAsync("/api/admin/invites", new { kind = "shareable", language = "en", maxUses = 30, validDays = 14 }, Ct);
        Assert.Equal(HttpStatusCode.Created, s.StatusCode);
        Assert.Matches($"^{System.Text.RegularExpressions.Regex.Escape(EmailChannelTests.Frontend)}/account/sign-up\\?invite=[A-Za-z0-9_-]{{43}}$",
            JsonNode.Parse(await s.Content.ReadAsStringAsync(Ct))!["link"]!.GetValue<string>());
        Assert.DoesNotContain("link", (await op.JsonAsync("/api/admin/invites")).ToJsonString());

        foreach (var (body, status, reason) in new (object, HttpStatusCode, string)[]
        {
            (new { kind = "group", language = "en" }, HttpStatusCode.BadRequest, "invalid_kind"),
            (new { kind = "named", email = "nope", language = "en" }, HttpStatusCode.BadRequest, "invalid_email"),
            (new { kind = "shareable", language = "de" }, HttpStatusCode.BadRequest, "invalid_language"),
            (new { kind = "shareable", language = "en", validDays = 0 }, HttpStatusCode.BadRequest, "invalid_valid_days"),
            (new { kind = "shareable", language = "en", validDays = 91 }, HttpStatusCode.BadRequest, "invalid_valid_days"),
            (new { kind = "shareable", language = "en", maxUses = 1001 }, HttpStatusCode.BadRequest, "invalid_max_uses"),
            (new { kind = "named", email = "n@gmail.com", language = "en", maxUses = 2 }, HttpStatusCode.BadRequest, "invalid_max_uses"),
            (new { kind = "shareable", language = "en", grantsAdmin = true }, HttpStatusCode.BadRequest, "admin_needs_named"),
            (new { kind = "named", email = d.S1.Email, language = "en" }, HttpStatusCode.Conflict, "account_exists"),
            (new { kind = "named", email = "GUEST@gmail.com", language = "en" }, HttpStatusCode.Conflict, "invite_exists"),
        })
            await Expect(await op.Client.PostAsJsonAsync("/api/admin/invites", body, Ct), status, reason);
    }

    // US8-9: list, revoke, resend.
    [Fact]
    public async Task ManageInvites()
    {
        await using var d = await SignUpTests.HostAsync(open: false);
        var op = await d.H.SignInAsync(admin: true, secondFactor: true);
        var (_, named) = await SignUpTests.InviteAsync(d, op, new { kind = "named", email = "a@gmail.com", language = "en" });
        var (_, shareable) = await SignUpTests.InviteAsync(d, op, new { kind = "shareable", language = "en", maxUses = 3 });
        var list = (await op.JsonAsync("/api/admin/invites"))["items"]!.AsArray();
        Assert.Equal([shareable, named], list.Select(i => i!["id"]!.GetValue<long>()));
        Assert.Equal((0, 3), (list[0]!["used"]!.GetValue<int>(), list[0]!["left"]!.GetValue<int>()));

        // Resend: a new link; the old one stops once it is sent. Shareable invites have no email.
        var first = await SignUpTests.NamedTokenAsync(d, "a@gmail.com");
        await Expect(await op.Client.PostAsync($"/api/admin/invites/{named}/resend", null, Ct), HttpStatusCode.Accepted);
        var second = await SignUpTests.NamedTokenAsync(d, "a@gmail.com");
        await Expect(await SignUpTests.AcceptAsync(d, first), HttpStatusCode.Gone, "invite_invalid");
        await Expect(await op.Client.PostAsync($"/api/admin/invites/{shareable}/resend", null, Ct), HttpStatusCode.Conflict, "not_named");

        // Revoke: the link stops at once, and an unsent email goes with the invite.
        await Expect(await op.Client.PostAsync($"/api/admin/invites/{named}/resend", null, Ct), HttpStatusCode.Accepted);
        await Expect(await op.Client.DeleteAsync($"/api/admin/invites/{named}", Ct), HttpStatusCode.NoContent);
        await Expect(await SignUpTests.AcceptAsync(d, second), HttpStatusCode.Gone, "invite_invalid");
        Assert.Equal(0, await d.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE kind = 'invite' AND sent_at IS NULL"));
        Assert.Equal(1, await d.Db.ScalarAsync("SELECT count(*) FROM security_event WHERE type = 'inviteRevoked' AND actor_id = $1", op.AccountId));
        await Expect(await op.Client.DeleteAsync($"/api/admin/invites/{named}", Ct), HttpStatusCode.NotFound);

        // A resend renews the validity: 8 days after a 7-day invite.
        var (_, late) = await SignUpTests.InviteAsync(d, op, new { kind = "named", email = "late@gmail.com", language = "en", validDays = 7 });
        d.H.Time.Advance(TimeSpan.FromDays(8));
        await Expect(await op.Client.PostAsync($"/api/admin/invites/{late}/resend", null, Ct), HttpStatusCode.Accepted);
        await Expect(await SignUpTests.AcceptAsync(d, await SignUpTests.NamedTokenAsync(d, "late@gmail.com")), HttpStatusCode.OK);

        // A new invite replaces an expired one for the same address.
        var (_, expired) = await SignUpTests.InviteAsync(d, op, new { kind = "named", email = "again@gmail.com", language = "en", validDays = 1 });
        d.H.Time.Advance(TimeSpan.FromDays(2));
        await SignUpTests.InviteAsync(d, op, new { kind = "named", email = "again@gmail.com", language = "en" });
        Assert.Equal(0, await d.Db.ScalarAsync("SELECT count(*) FROM invite WHERE id = $1", expired));
    }
}
