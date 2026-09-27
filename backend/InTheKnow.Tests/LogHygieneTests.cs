using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InTheKnow.Tests;

// specs/004 SC-007, FR-034: every flow once, then no secret in any log line, and none in a response except where it is
// handed out (the setup secret, the recovery codes, the session tokens and a shareable invite's link).
public class LogHygieneTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Records every response body with its request path.
    sealed class Recorder(ConcurrentQueue<(string Path, string Body)> into) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var r = await base.SendAsync(request, ct);
            into.Enqueue((request.RequestUri!.AbsolutePath, await r.Content.ReadAsStringAsync(ct)));
            return r;
        }
    }

    [Fact]
    public async Task NoSecretLeaks()
    {
        var bot = new BotCheckTests.Provider();
        await using var d = await DeliveryHost.CreateWithAsync([await EmailUnderTest.CreateAsync()], s =>
        {
            TestSignIn.Register(s);
            s.RemoveAll<BotCheck>();
            s.AddSingleton(sp => BotCheckTests.New(bot, sp.GetRequiredService<ILogger<BotCheck>>()));
        });
        var h = d.H;
        await d.Db.ExecAsync("UPDATE signup_settings SET open = true");
        var responses = new ConcurrentQueue<(string Path, string Body)>();
        var secrets = new Dictionary<string, HashSet<string>>
        {
            ["password"] = [], ["totp"] = [], ["code"] = [], ["recovery"] = [], ["session"] = [], ["link"] = [], ["address"] = [], ["bot"] = [],
        };
        HttpClient Client(string? token = null)
        {
            var c = h.Factory.CreateDefaultClient(new Recorder(responses));
            if (token is not null)
            {
                secrets["session"].Add(token);
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            return c;
        }
        var anonymous = Client();
        var n = 0;
        string Bot() { var b = $"tok-ok-hygiene-{n++}"; secrets["bot"].Add(b); return b; }
        async Task<JsonNode?> Post(HttpClient c, string path, object? body, HttpStatusCode expected)
        {
            var r = await c.PostAsJsonAsync(path, body ?? new { }, Ct);
            Assert.True(r.StatusCode == expected, $"{path}: {r.StatusCode}");
            var text = await r.Content.ReadAsStringAsync(Ct);
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
        async Task<string> LinkAsync(string kind, string to)
        {
            await d.NotifyAsync(20); // one send per pass at the test rate
            var link = SignUpTests.Mails(d, kind).Last(m => m.Target.Equals(to, StringComparison.OrdinalIgnoreCase)).Message.Link!;
            var token = link[(link.IndexOf("?token=", StringComparison.Ordinal) + 7)..];
            secrets["link"].UnionWith([link, token]);
            return token;
        }
        string Code(string secret, TimeSpan later = default)
        {
            h.Time.Advance(TimeSpan.FromSeconds(30) + later);
            var code = TwoFactorTests.Code(secret, h.Time.GetUtcNow());
            secrets["code"].Add(code);
            return code;
        }
        async Task<HttpClient> SignInWithCodeAsync(string email, string password, string secret)
        {
            secrets["password"].Add(password);
            var token = (await Post(anonymous, "/api/auth/sign-in", new { email, password, botCheck = Bot() }, HttpStatusCode.OK))!["token"]!.GetValue<string>();
            var c = Client(token);
            await Post(c, "/api/auth/sign-in/code", new { code = Code(secret) }, HttpStatusCode.OK);
            return c;
        }

        // Sign-up and confirmation.
        const string user = "hygiene-user@example.nl", moved = "hygiene-moved@example.nl", admin = "hygiene-admin@example.nl", guest = "hygiene-guest@example.nl";
        secrets["address"].UnionWith([user, moved, admin, guest]);
        const string p1 = "first-hygiene-pass", p2 = "second-hygiene-pass", p3 = "third-hygiene-pass", pa = "admin-hygiene-pass";
        secrets["password"].UnionWith([p1, p2, p3, pa]);
        await Post(anonymous, "/api/auth/sign-up", new { email = user, password = p1, language = "en", botCheck = Bot() }, HttpStatusCode.Accepted);
        await Post(anonymous, "/api/auth/confirmation", new { email = user, botCheck = Bot() }, HttpStatusCode.Accepted);
        await Post(anonymous, "/api/auth/confirm", new { token = await LinkAsync("confirm", user), password = p1 }, HttpStatusCode.NoContent);

        // Sign-in, 2FA, the code step and a recovery code.
        var c1 = Client((await Post(anonymous, "/api/auth/sign-in", new { email = user, password = p1, botCheck = Bot() }, HttpStatusCode.OK))!["token"]!.GetValue<string>());
        var secret = (await Post(c1, "/api/me/2fa/setup", null, HttpStatusCode.OK))!["secret"]!.GetValue<string>();
        secrets["totp"].Add(secret);
        var codes = (await Post(c1, "/api/me/2fa/enable", new { code = Code(secret) }, HttpStatusCode.OK))!["recoveryCodes"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        secrets["recovery"].UnionWith(codes);
        await Post(c1, "/api/auth/sign-out", null, HttpStatusCode.NoContent);
        await SignInWithCodeAsync(user, p1, secret);
        var viaRecovery = Client((await Post(anonymous, "/api/auth/sign-in", new { email = user, password = p1, botCheck = Bot() }, HttpStatusCode.OK))!["token"]!.GetValue<string>());
        await Post(viaRecovery, "/api/auth/sign-in/code", new { code = codes[0] }, HttpStatusCode.OK);

        // Reset, password change, email change.
        await Post(anonymous, "/api/auth/password-reset", new { email = user, botCheck = Bot() }, HttpStatusCode.Accepted);
        await Post(anonymous, "/api/auth/password-reset/complete", new { token = await LinkAsync("reset", user), password = p2 }, HttpStatusCode.NoContent);
        var c2 = await SignInWithCodeAsync(user, p2, secret);
        await Post(c2, "/api/me/reauthenticate", new { password = p2, code = Code(secret) }, HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NoContent, (await c2.PutAsJsonAsync("/api/me/password", new { password = p3 }, Ct)).StatusCode);
        await Post(c2, "/api/me/email", new { email = moved }, HttpStatusCode.Accepted);
        await Post(anonymous, "/api/auth/email-change/confirm", new { token = await LinkAsync("emailChange", moved) }, HttpStatusCode.NoContent);

        // Sessions and methods.
        var c3 = await SignInWithCodeAsync(moved, p3, secret);
        var sessions = JsonNode.Parse(await c3.GetStringAsync("/api/me/sessions", Ct))!["items"]!.AsArray();
        var other = sessions.First(s => !s!["current"]!.GetValue<bool>())!["id"]!.GetValue<long>(); // c2's
        Assert.Equal(HttpStatusCode.NoContent, (await c3.DeleteAsync($"/api/me/sessions/{other}", Ct)).StatusCode);
        await Post(c3, "/api/me/reauthenticate", new { password = p3, code = Code(secret) }, HttpStatusCode.NoContent);
        await Post(c3, "/api/test-auth/add", new { key = "hygiene-key@example.nl" }, HttpStatusCode.NoContent);
        secrets["address"].Add("hygiene-key@example.nl");
        var methods = JsonNode.Parse(await c3.GetStringAsync("/api/me/methods", Ct))!["items"]!.AsArray();
        Assert.Equal(HttpStatusCode.NoContent,
            (await c3.DeleteAsync($"/api/me/methods/{methods.Single(m => m!["type"]!.GetValue<string>() == "test")!["id"]}", Ct)).StatusCode);
        var userId = await d.Db.ScalarAsync("SELECT id FROM account WHERE email = $1", moved);

        // An admin from the server, and every admin action.
        await Accounts.MakeAdminAsync(d.Db.DataSource, admin, "en", h.Time.GetUtcNow());
        var ca = Client((await Post(anonymous, "/api/auth/invite/accept", new { token = await LinkAsync("invite", admin), password = pa }, HttpStatusCode.OK))!["token"]!.GetValue<string>());
        var adminSecret = (await Post(ca, "/api/me/2fa/setup", null, HttpStatusCode.OK))!["secret"]!.GetValue<string>();
        secrets["totp"].Add(adminSecret);
        secrets["recovery"].UnionWith((await Post(ca, "/api/me/2fa/enable", new { code = Code(adminSecret) }, HttpStatusCode.OK))!["recoveryCodes"]!.AsArray().Select(c => c!.GetValue<string>()));
        secrets["recovery"].UnionWith((await Post(ca, "/api/me/2fa/recovery-codes", new { code = Code(adminSecret) }, HttpStatusCode.OK))!["recoveryCodes"]!.AsArray().Select(c => c!.GetValue<string>()));
        await ca.GetStringAsync("/api/admin/accounts?q=hygiene", Ct);
        Assert.Equal(HttpStatusCode.OK, (await ca.PutAsJsonAsync($"/api/admin/accounts/{userId}/role", new { role = "admin" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ca.PutAsJsonAsync($"/api/admin/accounts/{userId}/role", new { role = "user" }, Ct)).StatusCode);
        await Post(ca, $"/api/admin/accounts/{userId}/disable", null, HttpStatusCode.OK);
        await Post(ca, $"/api/admin/accounts/{userId}/enable", null, HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await ca.PutAsJsonAsync("/api/admin/signup-settings", new { open = false, allowedDomains = new[] { "example.nl" } }, Ct)).StatusCode);
        var named = (await Post(ca, "/api/admin/invites", new { kind = "named", email = guest, language = "nl" }, HttpStatusCode.Created))!["id"]!.GetValue<long>();
        await LinkAsync("invite", guest);
        await Post(ca, $"/api/admin/invites/{named}/resend", null, HttpStatusCode.Accepted);
        await LinkAsync("invite", guest);
        var link = (await Post(ca, "/api/admin/invites", new { kind = "shareable", language = "en", maxUses = 5 }, HttpStatusCode.Created))!["link"]!.GetValue<string>();
        secrets["link"].UnionWith([link, link[(link.IndexOf("?invite=", StringComparison.Ordinal) + 8)..]]);
        await ca.GetStringAsync("/api/admin/invites", Ct);
        Assert.Equal(HttpStatusCode.NoContent, (await ca.DeleteAsync($"/api/admin/invites/{named}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ca.DeleteAsync($"/api/admin/accounts/{userId}/2fa", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ca.DeleteAsync($"/api/admin/accounts/{userId}", Ct)).StatusCode);
        await d.NotifyAsync(10);
        secrets["link"].UnionWith(d.C.Delivered.Select(m => m.Message.Link).OfType<string>());

        var all = secrets.Values.SelectMany(s => s).Where(s => s.Length > 0).ToList();
        foreach (var line in h.Logs.Lines)
            foreach (var value in all) Assert.False(line.Contains(value, StringComparison.OrdinalIgnoreCase), $"log line holds a secret: {line}");

        // Addresses are the owner's and the admin's to see; everything else only where it is handed out.
        static bool SignIn(string p) => p is "/api/auth/sign-in" or "/api/auth/invite/accept" or "/api/test-auth/sign-in";
        foreach (var (path, body) in responses)
            foreach (var (category, values) in secrets)
            {
                if (category == "address" || category == "session" && SignIn(path) || category == "totp" && path == "/api/me/2fa/setup"
                    || category == "recovery" && path is "/api/me/2fa/enable" or "/api/me/2fa/recovery-codes" || category == "link" && path == "/api/admin/invites")
                    continue;
                foreach (var value in values) Assert.False(body.Contains(value), $"{path} returned a {category}");
            }

        foreach (var detail in (await d.Db.RowsAsync("SELECT detail::text FROM security_event")).Select(r => (string)r[0]!))
            Assert.DoesNotContain("@", detail);
    }
}
