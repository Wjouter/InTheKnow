using System.Net.Mail;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
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

    static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    internal static int Count(string html, string needle) => Regex.Count(html, Regex.Escape(needle));

    // FR-008: the footer sits below the card, not in it.
    internal static void AssertFooterBelowCard(string html, string footer) =>
        Assert.Matches("</td></tr></table>\\s*<div style=\"max-width:600px[^>]*>\\s*<p[^>]*>" + Regex.Escape(Encoder.Encode(footer)), html);

    // WCAG 2.x relative luminance and contrast ratio.
    static double Luminance(string hex)
    {
        double Channel(int i) { var c = Convert.ToInt32(hex.Substring(1 + 2 * i, 2), 16) / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }

    static double Contrast(string fg, string bg)
    {
        var (a, b) = (Luminance(fg), Luminance(bg));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

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

        Assert.Contains("Rooms: AL00.A1.38 → AL00.A1.20", text);
        Assert.Contains("Status: tentative → confirmed", text);
        Assert.Contains("Tue 6 Oct 2026 08:30–10:00 → Tue 6 Oct 2026 09:30–11:00", text); // time: before → after
        Assert.Contains("detected at Thu 1 Oct 2026 14:00", text);
        Assert.Single(Regex.Matches(text, "low priority"));
        Assert.Contains("Robotics (low priority)", text);

        // The HTML part: heading, one calendar section, and per row a coloured label, bold name, date, old → new and detected-at.
        Assert.Matches("<h1[^>]*>3 timetable changes – Minor robotics</h1>", html);
        Assert.Equal(1, Count(html, "<h2"));
        Assert.Equal(3, Count(html, "<strong"));
        Assert.Equal(3, Count(html, "border-top:1px solid " + EmailChannel.Border));
        Assert.Contains($"color:{EmailChannel.FieldFg};background-color:{EmailChannel.FieldBg}\">Rooms</span>", html);
        Assert.Contains("AL00.A1.38 → AL00.A1.20</div>", html);
        Assert.Contains($"color:{EmailChannel.Muted};background-color:{EmailChannel.Page}\">Status</span>", html); // low priority is grey
        Assert.Contains("tentative → confirmed</div>", html);
        Assert.Contains(">Time</span>", html);
        Assert.DoesNotContain("Time</div>", html); // a time change has no "what" line
        Assert.Contains("Tue 6 Oct 2026 08:30–10:00 → Tue 6 Oct 2026 09:30–11:00</div>", html);
        Assert.Equal(1, Count(html, "(low priority)"));
        Assert.Equal(3, Count(html, "detected at Thu 1 Oct 2026 14:00</div>"));
        Assert.DoesNotContain("<script", html);
    }

    // FR-019: added and removed events show the event as it is or was.
    [Fact]
    public void AddedAndRemovedEvents()
    {
        JsonObject Event(string name, JsonObject fields) => new() { ["name"] = name, ["start"] = "2026-10-06T06:30:00Z", ["end"] = "2026-10-06T08:00:00Z", ["fields"] = fields };
        var added = new ChangeItem(View(7, "added", null, null, Event("New lesson", new JsonObject { ["rooms"] = new JsonArray("AL00.A1.20", "AL00.A1.23") }), name: "New lesson"), false);
        var removed = new ChangeItem(View(8, "removed", null, Event("Old lesson", new JsonObject { ["location"] = "Online" }), null, name: "Old lesson"), false);
        var (text, html, _) = Render(Changes("en", Amsterdam, 0, Group(1, "A", added, removed)));
        Assert.Contains("Added: AL00.A1.20, AL00.A1.23", text);
        Assert.Contains("Removed: Online", text);
        Assert.Contains("Tue 6 Oct 2026 08:30–10:00", text);

        Assert.Contains($"color:{EmailChannel.AddedFg};background-color:{EmailChannel.AddedBg}\">Added</span>", html);
        Assert.Contains($"color:{EmailChannel.RemovedFg};background-color:{EmailChannel.RemovedBg}\">Removed</span>", html);
        Assert.Contains("AL00.A1.20, AL00.A1.23</div>", html); // where it is, without the label prefix
        Assert.Contains("Online</div>", html);
        Assert.Contains("Tue 6 Oct 2026 08:30–10:00</div>", html);
    }

    [Fact]
    public void MultipleCalendarsSubject()
    {
        var items = Enumerable.Range(0, 5).Select(_ => Room).ToArray();
        var (_, _, mail) = Render(Changes("en", Amsterdam, 5, Group(1, "A", items), Group(2, "B", Status, Time)));
        Assert.Equal("12 timetable changes in 2 calendars", mail.Subject);
        Assert.Equal("1 timetable change – A", Render(Changes("en", Amsterdam, 0, Group(1, "A", Room))).Mail.Subject);

        // One section per calendar, in order, each with its settings link.
        var html = Render(Changes("en", Amsterdam, 0, Group(1, "A", Room), Group(2, "B", Status))).Html;
        Assert.Matches($"<h2[^>]*>A</h2><p[^>]*><a href=\"{Regex.Escape(Frontend)}/calendars/1/notifications\"[^>]*>[^<]*</a></p>(?s:.*)<h2[^>]*>B</h2><p[^>]*><a href=\"{Regex.Escape(Frontend)}/calendars/2/notifications\"", html);
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

    // SC-005: every kind encodes every inserted value (text, URLs) in the HTML part.
    [Fact]
    public void ValuesAreEncodedInEveryKind()
    {
        const string evil = "<b>x</b> & \"q\" 'a'", encoded = "&lt;b&gt;x&lt;/b&gt; &amp; &quot;q&quot; &#x27;a&#x27;";
        var url = "http://localhost/x?a=" + evil;
        var messages = new[]
        {
            new Message(MessageKind.Confirmation, "en", Amsterdam, url, url, null, null, [], 0),
            new Message(MessageKind.Test, "en", Amsterdam, null, null, null, url, [], 0),
            Changes("en", Amsterdam, 0, new CalendarGroup(1, evil, url, [new ChangeItem(View(9, "changed", "name", evil, evil, name: evil), false)])),
        };
        foreach (var m in messages)
        {
            var html = Render(m).Html;
            Assert.DoesNotContain("<b>", html);
            Assert.Contains(encoded, html);
        }
    }

    // FR-002, FR-004, FR-006, FR-019, FR-020, SC-003: the same shell and the same palette in every kind and language.
    [Fact]
    public void ShellAndPalette()
    {
        foreach (var language in new[] { "en", "nl" })
            foreach (var (name, message) in EmailFixtures(language))
            {
                var html = Render(message).Html;
                Assert.Contains($"<html lang=\"{language}\">", html);
                Assert.Contains("name=\"format-detection\" content=\"telephone=no,date=no,address=no,email=no\"", html);
                Assert.Contains("name=\"color-scheme\" content=\"light\"", html);
                Assert.Contains("name=\"supported-color-schemes\" content=\"light\"", html);
                Assert.Contains("max-width:600px", html);
                Assert.Contains("<table role=\"presentation\" width=\"600\" align=\"center\">", html);
                Assert.Contains("<h1 ", html);
                Assert.DoesNotContain("<script", html);
                Assert.DoesNotContain("<img", html);
                Assert.All(Regex.Matches(html, "#[0-9a-fA-F]{6}\\b"), c => Assert.True(EmailChannel.Palette.Contains(c.Value), $"{name}.{language}: {c.Value}"));
            }
    }

    // FR-021, SC-003.
    [Fact]
    public void TextColoursHaveContrast() =>
        Assert.All(EmailChannel.TextPairs, p => Assert.True(Contrast(p.Fg, p.Bg) >= 4.5, $"{p.Fg} on {p.Bg}: {Contrast(p.Fg, p.Bg):0.00}"));

    // FR-022: each calendar value is at most 200 bytes once encoded, whole characters only; the text part keeps it all.
    [Theory]
    [InlineData("x", "x")]
    [InlineData("\"&<>'", "(?:&quot;|&amp;|&lt;|&gt;|&#x27;)")]
    [InlineData("😀", "&#x1F600;")]
    [InlineData("漢", "漢")]
    public void LongValuesAreShortenedInHtml(string unit, string element)
    {
        var full = string.Concat(Enumerable.Repeat(unit, 1000 / unit.Length));
        var added = new JsonObject { ["name"] = full, ["start"] = "2026-10-06T06:30:00Z", ["end"] = "2026-10-06T08:00:00Z", ["fields"] = new JsonObject { ["rooms"] = new JsonArray(full) } };
        var fits = new string('f', 66);
        var items = new[]
        {
            new ChangeItem(View(1, "changed", "name", full, full, name: full), false),
            new ChangeItem(View(2, "added", null, null, added, name: full), false),
            new ChangeItem(View(3, "changed", "name", "old", "new", name: fits), false),
        };
        var (text, html, _) = Render(Changes("en", Amsterdam, 0, Group(1, full, items)));
        Assert.Contains(full, text);
        // calendar name, then event name, old and new value, event name and where
        var runs = Regex.Matches(html, $"(?:{element})+…");
        Assert.Equal(6, runs.Count);
        foreach (Match run in runs)
        {
            var bytes = Encoding.UTF8.GetByteCount(run.Value);
            Assert.InRange(bytes, 190, 200);
            Assert.StartsWith(WebUtility.HtmlDecode(run.Value[..^1]), full);
        }
        if (unit == "x") Assert.All(runs, r => Assert.Equal(198, r.Length)); // 197 characters and "…"
        Assert.Contains($">{fits}</strong>", html); // a value that fits is untouched
    }

    // SC-007: the worst case stays under 100 KB.
    [Fact]
    public void ChangesHtmlStaysUnder100KB()
    {
        var mixed = string.Concat(Enumerable.Repeat("\"&<>'😀漢字", 112));
        JsonObject Range(string day) => new() { ["start"] = $"2026-10-{day}T06:30:00Z", ["end"] = $"2026-10-{day}T08:00:00Z", ["fields"] = new JsonObject { ["rooms"] = new JsonArray(mixed), ["location"] = mixed } };
        ChangeItem Item(int i) => (i % 5) switch
        {
            0 => new(View(i, "added", null, null, Range("06"), name: mixed, calendarId: i), i % 3 == 0),
            1 => new(View(i, "removed", null, Range("06"), null, name: mixed, calendarId: i), i % 3 == 0),
            2 => new(View(i, "changed", "time", Range("06"), Range("07"), name: mixed, calendarId: i), i % 3 == 0),
            3 => new(View(i, "changed", "rooms", new JsonArray(mixed), new JsonArray(mixed), name: mixed, calendarId: i), i % 3 == 0),
            _ => new(View(i, "changed", "description", mixed, mixed, name: mixed, calendarId: i), i % 3 == 0),
        };
        var groups = Enumerable.Range(1, Notifier.Listed).Select(i => Group(i, mixed, Item(i))).ToArray();
        var html = Render(Changes("nl", Amsterdam, 7, groups)).Html;
        Assert.InRange(Encoding.UTF8.GetByteCount(html), 1, 99_999);
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
        Assert.Equal(1, Count(html, "mso-padding-alt"));
        Assert.Contains($"<a href=\"{Public}/n/confirm/TOKEN\" style=\"display:inline-block;padding:12px 24px;line-height:20px;color:{EmailChannel.Card};font-weight:700;text-decoration:none\">{Texts.Get("en", "confirmation.confirm")}</a>", html);
        Assert.Contains($"<a href=\"{Public}/n/decline/TOKEN\" style=\"color:{EmailChannel.LinkColor};text-decoration:underline\">", html); // a text link, not a button
        Assert.Contains($"<p style=\"margin:0 0 16px;color:{EmailChannel.Muted}\">", html); // the expiry line
        Assert.DoesNotContain("max-width:600px;margin:0 auto;padding:16px 4px 0", html); // no footer
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
        AssertFooterBelowCard(html, Texts.Get("en", "changes.footer"));
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
        Assert.Contains("Lokalen: AL00.A1.38 → AL00.A1.20", text);
        Assert.Contains("Status: tentative → confirmed", text);
        Assert.Contains("lage prioriteit", text);
        foreach (var part in new[] { text, html })
        {
            Assert.Contains("Robotics", part);
            Assert.Contains("di 6 okt 2026 08:30", part);
            Assert.Contains("gevonden op do 1 okt 2026 14:00", part);
        }
        Assert.Contains(">Lokalen</span>", html);
        Assert.Contains("AL00.A1.38 → AL00.A1.20</div>", html);
        Assert.Contains("tentative → confirmed</div>", html);
        Assert.Contains("(lage prioriteit)", html);
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
        Assert.Matches($"<td style=\"padding:12px 16px\">{more}</td>", html);
        Assert.Contains($"bgcolor=\"{EmailChannel.Box}\"", html);
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
        Assert.Matches("<h1[^>]*>" + Regex.Escape(subject) + "</h1>", html);
        Assert.Contains("InThe<span", html); // the wordmark
        Assert.Contains($"<a href=\"{Frontend}/notifications\" style=\"color:{EmailChannel.LinkColor};text-decoration:underline\">", html);
        Assert.DoesNotContain("mso-padding-alt", html); // a text link, not a button
        Assert.DoesNotContain("max-width:600px;margin:0 auto;padding:16px 4px 0", html); // no footer
    }

    static JsonObject RangeOf(string start, string end) => new() { ["start"] = start, ["end"] = end };

    // Every email shape in one language, for the snapshot test (SC-004) and the opt-in Mailpit preview.
    internal static IEnumerable<(string Name, Message Message)> EmailFixtures(string language)
    {
        JsonObject Event(string name, JsonObject fields) => new() { ["name"] = name, ["start"] = "2026-10-06T06:30:00Z", ["end"] = "2026-10-06T08:00:00Z", ["fields"] = fields };
        var added = new ChangeItem(View(7, "added", null, null, Event("New lesson", new JsonObject { ["rooms"] = new JsonArray("AL00.A1.20", "AL00.A1.23") }), name: "New lesson"), false);
        var removed = new ChangeItem(View(8, "removed", null, Event("Old lesson", new JsonObject { ["location"] = "Online" }), null, name: "Old lesson"), false);
        var acrossDays = new ChangeItem(View(10, "changed", "time",
            RangeOf("2026-10-06T06:30:00Z", "2026-10-06T08:00:00Z"), RangeOf("2026-10-07T06:30:00Z", "2026-10-07T08:00:00Z"), name: "Physics"), false);
        var lowRooms = new ChangeItem(View(11, "changed", "rooms", new JsonArray("AL00.A1.38"), new JsonArray("AL00.A1.20"), name: "Elective", calendarId: 2), true);
        yield return ("changes", Changes(language, Amsterdam, 7,
            Group(1, "Minor robotics", Room, Status, Time, acrossDays, added, removed), Group(2, "Electives", lowRooms)));
        yield return ("confirmation", Confirmation(language));
        yield return ("test", Test(language));
        foreach (var kind in AccountEmailTests.Kinds) yield return ($"account-{kind}", AccountEmailTests.Account(kind, language));
        yield return ("account-roleChanged-user", AccountEmailTests.Account("roleChanged", language, new Dictionary<string, string> { ["role"] = "user" }));
        yield return ("account-invite-admin", AccountEmailTests.Account("invite", language,
            new Dictionary<string, string> { ["admin"] = "true", ["expires"] = "Tue 6 Oct 2026 08:30" }));
    }

    static string SnapshotDir([CallerFilePath] string file = "") => Path.Combine(Path.GetDirectoryName(file)!, "EmailSnapshots");

    // SC-004: the text part, subject and headers do not change with the HTML layout. Set INTHEKNOW_UPDATE_SNAPSHOTS=1 to (re)write them.
    [Fact]
    public void TextPartMatchesSnapshot()
    {
        var update = Environment.GetEnvironmentVariable("INTHEKNOW_UPDATE_SNAPSHOTS") == "1";
        Directory.CreateDirectory(SnapshotDir());
        var problems = new List<string>();
        foreach (var language in new[] { "en", "nl" })
            foreach (var (name, message) in EmailFixtures(language))
            {
                var (text, _, mail) = Render(message);
                var actual = string.Join("\n", new[]
                {
                    "Subject: " + mail.Subject, "To: " + mail.To, "From: " + mail.From,
                    "List-Unsubscribe: " + mail.Headers["List-Unsubscribe"], "List-Unsubscribe-Post: " + mail.Headers["List-Unsubscribe-Post"],
                    "", text,
                });
                var path = Path.Combine(SnapshotDir(), $"{name}.{language}.txt");
                if (update) File.WriteAllBytes(path, Encoding.UTF8.GetBytes(actual));
                else if (!File.Exists(path)) problems.Add($"{path} is missing");
                else if (!File.ReadAllBytes(path).SequenceEqual(Encoding.UTF8.GetBytes(actual))) problems.Add($"{name}.{language} differs");
            }
        Assert.Empty(problems);
    }
}
