using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static InTheKnow.Tests.LinkIdentityTests;

namespace InTheKnow.Tests;

// specs/004 contracts/account-emails.md, research R9, R16, SC-010.
public partial class AccountEmailTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    internal static readonly string[] Kinds =
    [
        "confirm", "signUpAttempt", "reset", "passwordChanged", "emailChange", "emailChangeNotice", "twoFactorOn", "twoFactorOff",
        "twoFactorOffByAdmin", "recoveryCodeUsed", "methodAdded", "methodRemoved", "roleChanged", "disabled", "enabled", "deleted", "invite",
    ];
    static readonly HashSet<string> LinkKinds = ["confirm", "reset", "emailChange", "invite"];

    static Dictionary<string, string> Args(string kind) => kind switch
    {
        "recoveryCodeUsed" => new() { ["remaining"] = "9" },
        "methodAdded" or "methodRemoved" => new() { ["type"] = "password" },
        "roleChanged" => new() { ["role"] = "admin" },
        "invite" => new() { ["admin"] = "false", ["expires"] = "Tue 6 Oct 2026 08:30" },
        _ => [],
    };

    static Message Account(string kind, string language, IReadOnlyDictionary<string, string>? args = null) =>
        new(MessageKind.Account, language, Amsterdam, null, null, null, null, [], 0, kind,
            LinkKinds.Contains(kind) ? $"{EmailChannelTests.Frontend}/account/x?token=TOKEN" : null, args ?? Args(kind));

    // SC-010: every kind renders in both languages, with no unsubscribe header, text then HTML, and the footer.
    [Fact]
    public void EveryKindRendersInBothLanguages()
    {
        foreach (var language in new[] { "en", "nl" })
            foreach (var kind in Kinds)
            {
                var (text, html, mail) = EmailChannelTests.Render(Account(kind, language));
                Assert.Equal(["text/plain", "text/html"], mail.AlternateViews.Select(v => v.ContentType.MediaType));
                Assert.Null(mail.Headers["List-Unsubscribe"]);
                Assert.Null(mail.Headers["List-Unsubscribe-Post"]);
                Assert.Equal(Texts.Get(language, $"account.{kind}.subject"), mail.Subject);
                var footer = Texts.Get(language, "account.footer");
                Assert.Contains(footer, text);
                Assert.Contains(footer, html);
                if (LinkKinds.Contains(kind))
                    foreach (var part in new[] { text, html }) Assert.Contains("/account/x?token=TOKEN", part);
                Assert.DoesNotContain("{0}", text);
            }
        Assert.Contains("You have 9 recovery codes left.", EmailChannelTests.Render(Account("recoveryCodeUsed", "en")).Text);
        Assert.Contains("admin", EmailChannelTests.Render(Account("roleChanged", "en")).Text);
        Assert.Contains("geen beheerder meer", EmailChannelTests.Render(Account("roleChanged", "nl", new Dictionary<string, string> { ["role"] = "user" })).Text);
        Assert.Contains("Tue 6 Oct 2026 08:30", EmailChannelTests.Render(Account("invite", "en")).Text);
    }

    // Inserted values are encoded in the HTML part.
    [Fact]
    public void ArgsAreEncoded()
    {
        var (text, html, _) = EmailChannelTests.Render(Account("methodAdded", "en", new Dictionary<string, string> { ["type"] = "<b>x</b>" }));
        Assert.Contains("<b>x</b>", text);
        Assert.DoesNotContain("<b>x</b>", html);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", html);
    }

    // Both languages have every account key: a subject and a body (or its variants) per kind, and an action per link kind.
    [Fact]
    public void AccountKeysExistInBothLanguages()
    {
        foreach (var language in new[] { "en", "nl" })
        {
            var keys = Texts.Keys(language).ToHashSet();
            foreach (var kind in Kinds)
            {
                Assert.Contains($"account.{kind}.subject", keys);
                Assert.True(keys.Contains($"account.{kind}.body") || keys.Contains($"account.{kind}.body.admin") && keys.Contains($"account.{kind}.body.user"), kind);
                Assert.Equal(LinkKinds.Contains(kind), keys.Contains($"account.{kind}.action"));
            }
            Assert.Contains("account.footer", keys);
        }
    }

    static async Task<long> QueueAsync(DeliveryHost d, string kind, long? accountId, string to, object? args = null, long? inviteId = null,
                                       DateTimeOffset? at = null, string language = "en")
    {
        await using var conn = await d.Db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await InTheKnow.Accounts.QueueAsync(conn, tx, kind, accountId, to, language, at ?? d.Now, args, inviteId, Ct);
        await tx.CommitAsync(Ct);
        return await d.Db.ScalarAsync("SELECT max(id) FROM account_message");
    }

    static async Task<object?[]> RowAsync(DeliveryHost d, long id) =>
        (await d.Db.RowsAsync("SELECT sent_at, expired_at, token_hash, token_expires_at, used_at FROM account_message WHERE id = $1", id)).SingleOrDefault()
        ?? [null, null, null, null, null];

    static List<(string Target, Message Message)> Sent(DeliveryHost d) => [.. d.C.Delivered.Where(x => x.Message.Kind == MessageKind.Account)];

    [GeneratedRegex(@"\?token=([A-Za-z0-9_-]{43})$")] private static partial Regex Token();

    // R9: link tokens are minted at send, and only their hash is stored.
    [Fact]
    public async Task TokensAreMintedAtSend()
    {
        await using var d = await DeliveryHost.CreateAsync("email");
        var a = d.S1;
        foreach (var (kind, page, validity) in new[] { ("confirm", "confirm", TimeSpan.FromHours(24)), ("reset", "reset", TimeSpan.FromHours(1)), ("emailChange", "email", TimeSpan.FromHours(24)) })
        {
            var to = kind == "emailChange" ? "new@example.nl" : a.Email;
            var id = await QueueAsync(d, kind, a.AccountId, to, language: "nl");
            Assert.Null((await RowAsync(d, id))[2]);
            await d.NotifyAsync(2);
            var row = await RowAsync(d, id);
            var sent = (DateTime)row[0]!;
            Assert.Equal(sent + validity, (DateTime)row[3]!);
            var (target, message) = Sent(d).Last();
            Assert.Equal((to, kind, "nl"), (target, message.AccountKind, message.Language));
            Assert.StartsWith($"{EmailChannelTests.Frontend}/account/{page}?token=", message.Link);
            var token = Token().Match(message.Link!).Groups[1].Value;
            Assert.Equal((byte[])row[2]!, SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        }

        var invite = await d.Db.ScalarAsync("""
            INSERT INTO invite (kind, email, language, max_uses, created_at, expires_at) VALUES ('named', 'guest@example.nl', 'en', 1, $1, $2) RETURNING id
            """, d.Now, d.Now.AddDays(7));
        await QueueAsync(d, "invite", null, "guest@example.nl", new { admin = false }, invite);
        await d.NotifyAsync(2);
        var (guest, m) = Sent(d).Last();
        Assert.Equal("guest@example.nl", guest);
        Assert.StartsWith($"{EmailChannelTests.Frontend}/account/invite?token=", m.Link);
        Assert.Equal(Texts.Date("en", Amsterdam, d.Now.AddDays(7)), m.Args!["expires"]);
        var stored = (byte[])(await d.Db.RowsAsync("SELECT token_hash FROM invite WHERE id = $1", invite)).Single()[0]!;
        Assert.Equal(stored, SHA256.HashData(Encoding.UTF8.GetBytes(Token().Match(m.Link!).Groups[1].Value)));
    }

    // A newer link invalidates the earlier ones: a sent one is used, an older unsent one expires and is never sent.
    [Fact]
    public async Task NewerLinksInvalidateOlderOnes()
    {
        await using var d = await DeliveryHost.CreateAsync("email");
        var a = d.S1;
        var first = await QueueAsync(d, "confirm", a.AccountId, a.Email);
        await d.NotifyAsync(2);
        var second = await QueueAsync(d, "confirm", a.AccountId, a.Email);
        await d.NotifyAsync(2);
        Assert.NotNull((await RowAsync(d, first))[4]);
        Assert.Null((await RowAsync(d, second))[4]);

        // An older row (lower id) requested later: the newer one goes first, and the older one expires unsent.
        var older = await QueueAsync(d, "confirm", a.AccountId, a.Email, at: d.Now + TimeSpan.FromSeconds(1));
        var newer = await QueueAsync(d, "confirm", a.AccountId, a.Email);
        var sentBefore = Sent(d).Count;
        await d.NotifyAsync(5);
        Assert.NotNull((await RowAsync(d, newer))[0]);
        Assert.Equal((null, true), ((await RowAsync(d, older))[0], (await RowAsync(d, older))[1] is not null));
        Assert.Equal(sentBefore + 1, Sent(d).Count);
    }

    [Fact]
    public async Task Recipients()
    {
        await using var d = await DeliveryHost.CreateAsync("email");
        await QueueAsync(d, "emailChange", d.S1.AccountId, "new@example.nl");
        var deleted = await QueueAsync(d, "deleted", null, "former@example.nl");
        await d.NotifyAsync(3);
        Assert.Equal(["new@example.nl", "former@example.nl"], Sent(d).Select(x => x.Target));
        Assert.Equal(0, await d.Db.ScalarAsync("SELECT count(*) FROM account_message WHERE id = $1", deleted));
    }

    // FR-033c: a disabled account gets only the admin-requested emails.
    [Fact]
    public async Task DisabledAccounts()
    {
        await using var d = await DeliveryHost.CreateAsync("email");
        var a = d.S1;
        await d.Db.ExecAsync("UPDATE account SET disabled_at = $2 WHERE id = $1", a.AccountId, d.Now);
        var suppressed = new List<long>();
        foreach (var kind in new[] { "confirm", "reset", "passwordChanged" }) suppressed.Add(await QueueAsync(d, kind, a.AccountId, a.Email));
        foreach (var kind in new[] { "disabled", "enabled", "twoFactorOffByAdmin" }) await QueueAsync(d, kind, a.AccountId, a.Email);
        await QueueAsync(d, "roleChanged", a.AccountId, a.Email, new { role = "user" });
        await QueueAsync(d, "deleted", null, a.Email);
        await d.NotifyAsync(12);
        Assert.Equal(["disabled", "enabled", "twoFactorOffByAdmin", "roleChanged", "deleted"], Sent(d).Select(x => x.Message.AccountKind!));
        foreach (var id in suppressed) Assert.Equal((null, true), ((await RowAsync(d, id))[0], (await RowAsync(d, id))[1] is not null));
    }

    [Fact]
    public async Task FailuresRetryExpireAndBackOff()
    {
        var e = await EmailUnderTest.CreateAsync();
        await using var d = await DeliveryHost.CreateAsync([e]);
        var a = d.S1;

        // RCPT 450: queued for a later pass, then sent.
        e.FailTemporarily(a.Email, 1);
        var busy = await QueueAsync(d, "passwordChanged", a.AccountId, a.Email);
        await d.NotifyAsync(3);
        Assert.Equal((null, null), ((await RowAsync(d, busy))[0], (await RowAsync(d, busy))[1]));
        await d.AdvanceAndNotifyAsync(TimeSpan.FromMinutes(1), 3);
        Assert.NotNull((await RowAsync(d, busy))[0]);

        // RCPT 450 until the retry window has passed: expired.
        e.FailTemporarily(a.Email, 10_000);
        var stuck = await QueueAsync(d, "passwordChanged", a.AccountId, a.Email);
        await d.AdvanceAndNotifyAsync(d.H.Factory.Services.GetRequiredService<Settings>().Options.Notifications.RetryWindow + TimeSpan.FromMinutes(1), 3);
        Assert.Equal((null, true), ((await RowAsync(d, stuck))[0], (await RowAsync(d, stuck))[1] is not null));

        e.FailTemporarily(a.Email, 0);

        // RCPT 550: expired at once.
        e.RejectPermanently("gone@example.nl");
        var gone = await QueueAsync(d, "emailChange", a.AccountId, "gone@example.nl");
        await d.NotifyAsync(3);
        Assert.Equal((null, true), ((await RowAsync(d, gone))[0], (await RowAsync(d, gone))[1] is not null));

        // A refused connection backs the email type off, and the row is sent on a later pass.
        e.Outage = true;
        var later = await QueueAsync(d, "twoFactorOn", a.AccountId, a.Email);
        await d.NotifyAsync(3);
        Assert.Null((await RowAsync(d, later))[0]);
        e.Outage = false;
        await d.NotifyAsync(3);
        Assert.Null((await RowAsync(d, later))[0]); // still backing off
        await d.AdvanceAndNotifyAsync(TimeSpan.FromMinutes(2), 3);
        Assert.NotNull((await RowAsync(d, later))[0]);
    }

    // Account emails take the first rate slot, before 003's confirmations and digests.
    [Fact]
    public async Task AccountEmailsGoFirst()
    {
        var e = await EmailUnderTest.CreateAsync();
        e.MaxMessagesPerMinute = 1;
        await using var d = await DeliveryHost.CreateAsync([e]);
        var url = MtLink();
        await d.SubscribeAsync(d.S1, url);
        await d.AddActiveChannelAsync(d.S1, "digest@example.nl");
        await d.RecordChangesAsync(url, "mt-nl-edited.ics");
        await d.PostAsync(d.S1, "/api/me/channels", new { type = "email", target = "confirm@example.nl" });
        d.H.Time.Advance(d.Window);
        await QueueAsync(d, "passwordChanged", d.S1.AccountId, d.S1.Email, at: d.Now);
        await d.Notifier.RunDueAsync(d.Now, Ct);
        var (target, message) = Assert.Single(d.C.Delivered);
        Assert.Equal((d.S1.Email, MessageKind.Account), (target, message.Kind));
    }

    // Security notices are about the account: they ignore the account's channels.
    [Fact]
    public async Task NoticesIgnoreChannels()
    {
        await using var d = await DeliveryHost.CreateAsync("email");
        await CheckerTests.ChannelAsync(d.Db, d.S1.AccountId, "paused", d.Now, d.S1.Email);
        await QueueAsync(d, "passwordChanged", d.S1.AccountId, d.S1.Email);
        await d.NotifyAsync(2);
        Assert.Equal("passwordChanged", Assert.Single(Sent(d)).Message.AccountKind);
    }

    // R9, R16, FR-035, SC-008: what Accounts.CleanupAsync deletes, and when.
    [Fact]
    public async Task Retention()
    {
        await using var d = await DeliveryHost.CreateAsync("email");
        var accounts = d.H.Factory.Services.GetRequiredService<Accounts>();
        var a = d.S1;
        var t = d.Now;
        var day = TimeSpan.FromDays(1);
        async Task<bool> Exists(string table, long id) => await d.Db.ScalarAsync($"SELECT count(*) FROM {table} WHERE id = $1", id) == 1;

        var notice = await QueueAsync(d, "passwordChanged", a.AccountId, a.Email);
        var used = await QueueAsync(d, "confirm", a.AccountId, a.Email);
        await d.NotifyAsync(3);
        var unused = await QueueAsync(d, "reset", a.AccountId, a.Email);
        await d.NotifyAsync(3);
        await d.Db.ExecAsync("UPDATE account_message SET used_at = $2 WHERE id = $1", used, t);
        var expired = await QueueAsync(d, "twoFactorOn", a.AccountId, a.Email);
        await d.Db.ExecAsync("UPDATE account_message SET expired_at = $2 WHERE id = $1", expired, t);
        var sent = (DateTime)(await RowAsync(d, notice))[0]!;
        var resetExpires = (DateTime)(await RowAsync(d, unused))[3]!;

        await accounts.CleanupAsync(new DateTimeOffset(sent) + day - TimeSpan.FromSeconds(1), Ct);
        Assert.True(await Exists("account_message", notice));
        await accounts.CleanupAsync(new DateTimeOffset(sent) + day + TimeSpan.FromSeconds(1), Ct);
        Assert.False(await Exists("account_message", notice));
        Assert.False(await Exists("account_message", used)); // used at t, 24 h ago
        Assert.False(await Exists("account_message", expired));
        Assert.True(await Exists("account_message", unused)); // its token expires later
        await accounts.CleanupAsync(new DateTimeOffset(resetExpires) + day + TimeSpan.FromSeconds(1), Ct);
        Assert.False(await Exists("account_message", unused));

        // Sessions: idle past the lifetime, and awaiting sessions older than 10 minutes.
        var idle = d.H.Factory.Services.GetRequiredService<Settings>().Options.Accounts.SessionIdleLifetime;
        var method = await d.Db.ScalarAsync("SELECT id FROM sign_in_method WHERE account_id = $1", a.AccountId);
        async Task<long> Session(DateTimeOffset created, DateTimeOffset lastUsed, bool awaiting) => await d.Db.ScalarAsync("""
            INSERT INTO session (account_id, method_id, token_hash, device, awaiting_code, created_at, last_used_at)
            VALUES ($1, $2, $3, 'd', $4, $5, $6) RETURNING id
            """, a.AccountId, method, RandomNumberGenerator.GetBytes(32), awaiting, created, lastUsed);
        var now = t + TimeSpan.FromDays(400);
        var stale = await Session(now - idle - TimeSpan.FromSeconds(1), now - idle - TimeSpan.FromSeconds(1), false);
        var active = await Session(now - idle, now - idle + TimeSpan.FromSeconds(1), false);
        var oldAwaiting = await Session(now - TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1), now, true);
        var newAwaiting = await Session(now - TimeSpan.FromMinutes(9), now, true);

        // Security events: 90 days, also after the account is gone.
        async Task<long> Event(DateTimeOffset at, long account) => await d.Db.ScalarAsync(
            "INSERT INTO security_event (occurred_at, type, account_id) VALUES ($1, 'signIn', $2) RETURNING id", at, account);
        var oldEvent = await Event(now - TimeSpan.FromDays(90) - TimeSpan.FromSeconds(1), 999_999);
        var recentEvent = await Event(now - TimeSpan.FromDays(89), a.AccountId);

        await accounts.CleanupAsync(now, Ct);
        Assert.Equal(new[] { false, true, false, true }, new[] { await Exists("session", stale), await Exists("session", active),
                                                                await Exists("session", oldAwaiting), await Exists("session", newAwaiting) });
        Assert.Equal(new[] { false, true }, new[] { await Exists("security_event", oldEvent), await Exists("security_event", recentEvent) });
    }
}
