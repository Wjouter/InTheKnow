using System.Net.Mail;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace InTheKnow.Tests;

public partial class EmailChannelTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    const string N = "InTheKnow:Notifications:";
    internal const string Public = "http://localhost:5000", Frontend = "http://localhost:5173";
    static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    internal static (string, string?)[] EmailSettings(int port) =>
    [
        (N + "Email:Host", "127.0.0.1"), (N + "Email:Port", port.ToString()), (N + "Email:StartTls", "false"),
        (N + "Email:FromAddress", "intheknow@localhost"), (N + "PublicBaseUrl", Public), (N + "FrontendBaseUrl", Frontend),
    ];

    static EmailOptions Options => TestDb.Offline(EmailSettings(25)).Options.Notifications.Email;

    static ChangeView View(long id, string kind, string? field, JsonNode? oldValue, JsonNode? newValue, string name = "Robotics",
                           DateTimeOffset? detected = null, long calendarId = 1)
    {
        var ev = new JsonObject { ["name"] = name, ["start"] = "2026-10-06T06:30:00Z", ["end"] = "2026-10-06T08:00:00Z", ["fields"] = new JsonObject() };
        return new(id, calendarId, "k" + id, kind, field, oldValue, newValue, name, new(2026, 10, 6, 6, 30, 0, TimeSpan.Zero),
            detected ?? new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), kind == "changed" ? ev : null, kind == "changed" ? ev.DeepClone() : null);
    }

    internal static Message Changes(string language, TimeZoneInfo zone, int notListed, params CalendarGroup[] groups) =>
        new(MessageKind.Changes, language, zone, null, null, $"{Public}/n/unsubscribe/UNSUB", null, groups, notListed);

    static CalendarGroup Group(long id, string name, params ChangeItem[] items) =>
        new(id, name, $"{Frontend}/calendars/{id}/notifications", items);

    static readonly ChangeItem Room = new(View(1, "changed", "rooms", new JsonArray("AL00.A1.38"), new JsonArray("AL00.A1.20")), false);
    static readonly ChangeItem Status = new(View(2, "changed", "status", "tentative", "confirmed"), true);
    static readonly ChangeItem Time = new(View(3, "changed", "time",
        new JsonObject { ["start"] = "2026-10-06T06:30:00Z", ["end"] = "2026-10-06T08:00:00Z" },
        new JsonObject { ["start"] = "2026-10-06T07:30:00Z", ["end"] = "2026-10-06T09:00:00Z" }), false);

    static Message Confirmation(string language = "en") =>
        new(MessageKind.Confirmation, language, Amsterdam, $"{Public}/n/confirm/TOKEN", $"{Public}/n/decline/TOKEN", null, null, [], 0);

    internal static Message Test(string language = "en") =>
        new(MessageKind.Test, language, Amsterdam, null, null, $"{Public}/n/unsubscribe/UNSUB", $"{Frontend}/notifications", [], 0);

    // The text and HTML parts of a built mail, and its raw headers.
    internal static (string Text, string Html, MailMessage Mail) Render(Message m, string target = "student@example.nl")
    {
        var mail = EmailChannel.Build(target, m, Options);
        string Part(int i)
        {
            using var reader = new StreamReader(mail.AlternateViews[i].ContentStream, Encoding.UTF8);
            var s = reader.ReadToEnd();
            mail.AlternateViews[i].ContentStream.Position = 0;
            return s;
        }
        return (Part(0), Part(1), mail);
    }

    [GeneratedRegex(@"https?://[^\s""<>]+")] private static partial Regex Url();

    [Fact]
    public void RendersParts()
    {
        var (text, html, mail) = Render(Changes("en", Amsterdam, 0, Group(1, "Minor robotics", Room, Status, Time)));
        Assert.Equal(["text/plain", "text/html"], mail.AlternateViews.Select(v => v.ContentType.MediaType));
        Assert.Equal("\"InTheKnow\" <intheknow@localhost>", mail.From!.ToString());
        Assert.Equal("student@example.nl", Assert.Single(mail.To).Address);
        Assert.Empty(mail.CC);
        Assert.Empty(mail.Bcc);
        Assert.Equal("3 timetable changes – Minor robotics", mail.Subject);
        Assert.Equal($"<{Public}/n/unsubscribe/UNSUB>", mail.Headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", mail.Headers["List-Unsubscribe-Post"]);
        Assert.Matches("^<[0-9a-f]{32}@localhost>$", mail.Headers["Message-ID"]);

        foreach (var part in new[] { text, html })
        {
            Assert.Contains("Robotics", part);
            Assert.Contains("Tue 6 Oct 2026 08:30", part); // event time in Amsterdam
            Assert.Contains("Rooms: AL00.A1.38 → AL00.A1.20", part);
            Assert.Contains("Status: tentative → confirmed", part);
            Assert.Contains("Tue 6 Oct 2026 08:30–10:00 → Tue 6 Oct 2026 09:30–11:00", part); // time: before → after
            Assert.Contains("detected at Thu 1 Oct 2026 14:00", part);
        }
        Assert.Single(Regex.Matches(text, "low priority"));
        Assert.Contains("Robotics (low priority)", text);
        Assert.DoesNotContain("<script", html);
    }

    // FR-019: added and removed events show the event as it is or was.
    [Fact]
    public void AddedAndRemovedEvents()
    {
        JsonObject Event(string name, JsonObject fields) => new() { ["name"] = name, ["start"] = "2026-10-06T06:30:00Z", ["end"] = "2026-10-06T08:00:00Z", ["fields"] = fields };
        var added = new ChangeItem(View(7, "added", null, null, Event("New lesson", new JsonObject { ["rooms"] = new JsonArray("AL00.A1.20", "AL00.A1.23") }), name: "New lesson"), false);
        var removed = new ChangeItem(View(8, "removed", null, Event("Old lesson", new JsonObject { ["location"] = "Online" }), null, name: "Old lesson"), false);
        var text = Render(Changes("en", Amsterdam, 0, Group(1, "A", added, removed))).Text;
        Assert.Contains("Added: AL00.A1.20, AL00.A1.23", text);
        Assert.Contains("Removed: Online", text);
        Assert.Contains("Tue 6 Oct 2026 08:30–10:00", text);
    }

    [Fact]
    public void MultipleCalendarsSubject()
    {
        var items = Enumerable.Range(0, 5).Select(_ => Room).ToArray();
        var (_, _, mail) = Render(Changes("en", Amsterdam, 5, Group(1, "A", items), Group(2, "B", Status, Time)));
        Assert.Equal("12 timetable changes in 2 calendars", mail.Subject);
        Assert.Equal("1 timetable change – A", Render(Changes("en", Amsterdam, 0, Group(1, "A", Room))).Mail.Subject);
    }

    [Fact]
    public void CalendarTextIsEncoded()
    {
        var evil = "<a href=\"x\">Hi</a> & <b>";
        var item = new ChangeItem(View(9, "changed", "name", "old", evil, name: evil), false);
        var (text, html, _) = Render(Changes("en", Amsterdam, 0, Group(1, evil, item)));
        Assert.Contains(evil, text);
        Assert.DoesNotContain("<a href=\"x\"", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;a href=&quot;x&quot;&gt;Hi&lt;/a&gt; &amp; &lt;b&gt;", html);
    }

    [Fact]
    public void ConfirmationEmail()
    {
        var (text, html, mail) = Render(Confirmation());
        Assert.Equal("Confirm your InTheKnow notifications", mail.Subject);
        Assert.Null(mail.Headers["List-Unsubscribe"]);
        Assert.Null(mail.Headers["List-Unsubscribe-Post"]);
        foreach (var part in new[] { text, html })
        {
            Assert.Contains($"{Public}/n/confirm/TOKEN", part);
            Assert.Contains($"{Public}/n/decline/TOKEN", part);
            Assert.Contains("48 hours", part);
        }
        // In a confirmation, the only /n/ URLs are the confirm and decline links.
        Assert.Equal([$"{Public}/n/confirm/TOKEN", $"{Public}/n/decline/TOKEN"], Url().Matches(text).Select(x => x.Value).Distinct());
    }

    [Fact]
    public void NormalizeAddresses()
    {
        var email = new EmailChannel(TestDb.Offline());
        Assert.Equal("a@b.nl", email.Normalize("  a@b.nl "));
        Assert.Null(email.Normalize("a"));
        Assert.Null(email.Normalize("Name <a@b.nl>"));
        Assert.Null(email.Normalize("   "));
        Assert.Null(email.Normalize(new string('a', 245) + "@example.nl"));
        Assert.Equal(254, email.Normalize(new string('a', 243) + "@example.nl")!.Length);
    }

    // Research R9: why feed names and labels never contain control characters.
    [Fact]
    public void SubjectWithLineBreakIsRejected()
    {
        using var mail = new MailMessage("intheknow@localhost", "a@b.nl");
        Assert.ThrowsAny<ArgumentException>(() => mail.Subject = "a\r\nBcc: x@y.nl");
    }

    // Research R2: the exception each SMTP failure produces, and its mapped result.
    [Fact]
    public async Task SmtpFailuresMapToResults()
    {
        await using var smtp = await FakeSmtp.StartAsync();
        var email = new EmailChannel(TestDb.Offline(EmailSettings(smtp.Port)));
        Task<SendResult> Send(string to) => email.SendAsync(to, Test(), Ct);

        Assert.Equal(new SendResult(SendStatus.Sent), await Send("ok@example.nl"));
        var mail = Assert.Single(smtp.Mails);
        Assert.Equal(["ok@example.nl"], mail.Recipients);
        Assert.Contains("Subject: InTheKnow test notification", mail.Data);

        smtp.Recipient("gone@example.nl", 550);
        Assert.Equal(new SendResult(SendStatus.TargetRejected, "smtp_550"), await Send("gone@example.nl"));
        smtp.Recipient("busy@example.nl", 450);
        Assert.Equal(new SendResult(SendStatus.TargetFailed, "smtp_450"), await Send("busy@example.nl"));
        smtp.DataReply = 451;
        Assert.Equal(new SendResult(SendStatus.TypeUnavailable, "smtp_451"), await Send("ok@example.nl"));
        smtp.DataReply = null;
        smtp.DataReplyFor("poison@example.nl", 554);
        Assert.Equal(new SendResult(SendStatus.TypeUnavailable, "smtp_554"), await Send("poison@example.nl"));
        smtp.Refuse = true;
        Assert.Equal(new SendResult(SendStatus.TypeUnavailable, "unreachable"), await Send("ok@example.nl"));
        Assert.Single(smtp.Mails);

        var closed = new EmailChannel(TestDb.Offline(EmailSettings(1)));
        Assert.Equal(new SendResult(SendStatus.TypeUnavailable, "unreachable"), await closed.SendAsync("ok@example.nl", Test(), Ct));
    }

    // US2-1, FR-009, SC-003: per-calendar settings links; the only /n/ URL is the List-Unsubscribe header.
    [Fact]
    public void SettingsLinksAndNoActionLinks()
    {
        var (text, html, mail) = Render(Changes("en", Amsterdam, 0, Group(1, "X", Room), Group(2, "Y", Status)));
        foreach (var part in new[] { text, html })
        {
            Assert.Contains($"{Frontend}/calendars/1/notifications", part);
            Assert.Contains($"{Frontend}/calendars/2/notifications", part);
            Assert.Contains("unsubscribe option to stop these notifications", part); // the footer
            Assert.All(Url().Matches(part), u => Assert.Matches($"^{Regex.Escape(Frontend)}/calendars/[12]/notifications$", u.Value));
        }
        Assert.Contains("/n/unsubscribe/", mail.Headers["List-Unsubscribe"]);
    }

    // SC-009, research R9.
    [Fact]
    public void LanguagesHaveTheSameKeys() => Assert.Equal(Texts.Keys("en").Order(), Texts.Keys("nl").Order());

    [Fact]
    public void DatesPerLanguage()
    {
        var t = new DateTimeOffset(2026, 10, 6, 6, 30, 0, TimeSpan.Zero);
        Assert.Equal("di 6 okt 2026 08:30", Texts.Date("nl", Amsterdam, t));
        Assert.Equal("Tue 6 Oct 2026 08:30", Texts.Date("en", Amsterdam, t));
        Assert.Throws<KeyNotFoundException>(() => Texts.Get("en", "no.such.key"));
    }

    // US3-2: fixed text in Dutch; calendar text as recorded.
    [Fact]
    public void DutchChangesEmail()
    {
        var (text, html, mail) = Render(Changes("nl", Amsterdam, 0, Group(1, "Minor robotics", Room, Status, Time)));
        Assert.Equal("3 roosterwijzigingen – Minor robotics", mail.Subject);
        foreach (var part in new[] { text, html })
        {
            Assert.Contains("Lokalen: AL00.A1.38 → AL00.A1.20", part);
            Assert.Contains("Status: tentative → confirmed", part);
            Assert.Contains("Robotics", part);
            Assert.Contains("di 6 okt 2026 08:30", part);
            Assert.Contains("gevonden op do 1 okt 2026 14:00", part);
            Assert.Contains("lage prioriteit", part);
        }
        Assert.Equal("12 roosterwijzigingen in 2 roosters", Render(Changes("nl", Amsterdam, 7, Group(1, "A", Room, Room, Room), Group(2, "B", Status, Time))).Mail.Subject);
        Assert.Equal("Bevestig je InTheKnow-meldingen", Render(Confirmation("nl")).Mail.Subject);
    }

    // US3-6: times in the subscriber's zone, across the two summer-time changes.
    [Theory]
    [InlineData("2026-10-20T10:00:00Z", "Tue 20 Oct 2026 12:00", "Tue 20 Oct 2026 06:00")] // 6 hours apart
    [InlineData("2026-10-27T10:00:00Z", "Tue 27 Oct 2026 11:00", "Tue 27 Oct 2026 06:00")] // Europe changed on 25 Oct: 5 hours
    [InlineData("2026-11-02T10:00:00Z", "Mon 2 Nov 2026 11:00", "Mon 2 Nov 2026 05:00")]  // the US on 1 Nov: 6 again
    public void TimeZones(string start, string amsterdam, string newYork)
    {
        var at = DateTimeOffset.Parse(start, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        JsonObject Range(DateTimeOffset s) => new() { ["start"] = FeedParser.Iso(s), ["end"] = FeedParser.Iso(s.AddHours(1)) };
        var item = new ChangeItem(View(5, "changed", "time", Range(at.AddHours(-1)), Range(at)), false);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.Contains($"→ {amsterdam}", Render(Changes("en", Amsterdam, 0, Group(1, "A", item))).Text);
        Assert.Contains($"→ {newYork}", Render(Changes("en", zone, 0, Group(1, "A", item))).Text);
    }

    // FR-015: at most 50 listed, and a line for the rest.
    [Theory]
    [InlineData("en", "and 7 more changes", "57 timetable changes – A")]
    [InlineData("nl", "en nog 7 wijzigingen", "57 roosterwijzigingen – A")]
    public void MoreThanFifty(string language, string more, string subject)
    {
        var items = Enumerable.Range(1, 50).Select(i => new ChangeItem(View(i, "changed", "name", "old", "new", name: $"Event {i:00}"), false)).ToArray();
        var (text, html, mail) = Render(Changes(language, Amsterdam, 7, Group(1, "A", items)));
        Assert.Equal(subject, mail.Subject);
        foreach (var part in new[] { text, html })
        {
            Assert.Equal(50, Regex.Matches(part, "Event [0-9]{2}").Count);
            Assert.Contains(more, part);
        }
    }

    // FR-010, FR-023: the test message.
    [Theory]
    [InlineData("en", "InTheKnow test notification")]
    [InlineData("nl", "InTheKnow-testmelding")]
    public void TestEmail(string language, string subject)
    {
        var (text, html, mail) = Render(Test(language));
        Assert.Equal(subject, mail.Subject);
        Assert.Equal($"<{Public}/n/unsubscribe/UNSUB>", mail.Headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", mail.Headers["List-Unsubscribe-Post"]);
        Assert.Matches("^<[0-9a-f]{32}@localhost>$", mail.Headers["Message-ID"]);
        foreach (var part in new[] { text, html })
        {
            Assert.Contains(Texts.Get(language, "test.body"), part);
            Assert.Contains($"{Frontend}/notifications", part);
        }
        Assert.Equal([$"{Frontend}/notifications"], Url().Matches(text).Select(u => u.Value));
    }
}
