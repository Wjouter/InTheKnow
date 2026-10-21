using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text.Json.Nodes;

namespace InTheKnow;

// The email channel type (research R2, contracts/messages.md): BCL SmtpClient, one recipient and one connection per message.
// Failures map to codes by SMTP status only; server text is never used (FR-031).
sealed class EmailChannel(Settings settings) : IChannelType
{
    static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);
    EmailOptions O => settings.Options.Notifications.Email;

    public string Name => "email";
    public bool Configured => O.Host != "";
    public int MaxMessagesPerMinute => O.MaxMessagesPerMinute;

    // A bare address only: no display name, at most 254 characters (FR-004).
    public string? Normalize(string target)
    {
        var t = target.Trim();
        return t.Length <= 254 && MailAddress.TryCreate(t, out var a) && a.Address == t && a.DisplayName == "" ? t : null;
    }

    public async Task<SendResult> SendAsync(string target, Message message, CancellationToken ct)
    {
        using var mail = Build(target, message, O);
        using var client = new SmtpClient(O.Host, O.Port) { EnableSsl = O.StartTls, Timeout = (int)SendTimeout.TotalMilliseconds };
        if (O.Username != "") client.Credentials = new NetworkCredential(O.Username, O.Password);
        // SmtpClient.Timeout covers only the synchronous Send.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SendTimeout);
        try
        {
            await client.SendMailAsync(mail, timeout.Token);
            return new(SendStatus.Sent);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(SendStatus.TypeUnavailable, "timeout"); }
        catch (SmtpFailedRecipientException e) when ((int)e.StatusCode is >= 400 and < 600)
        {
            return new((int)e.StatusCode >= 500 ? SendStatus.TargetRejected : SendStatus.TargetFailed, $"smtp_{(int)e.StatusCode}");
        }
        catch (SmtpException e)
        {
            return new(SendStatus.TypeUnavailable, (int)e.StatusCode is >= 400 and < 600 ? $"smtp_{(int)e.StatusCode}" : "unreachable");
        }
        catch (Exception e) when (e is SocketException or IOException) { return new(SendStatus.TypeUnavailable, "unreachable"); }
    }

    // The palette (specs/007 research R9): the only colours the HTML part uses. Tests check the contrast of TextPairs.
    internal const string Ink = "#1d1d1b", Magenta = "#e20e80", LinkColor = "#c70b70", Body = "#3d3d3d", Muted = "#5a5a5a",
        Page = "#f0f0f0", Card = "#ffffff", Border = "#e3e3e3", Box = "#f7f7f7",
        AddedFg = "#16673a", AddedBg = "#e3f4ea", RemovedFg = "#a50d26", RemovedBg = "#fbe3e7", FieldFg = "#7a4a00", FieldBg = "#fdf0d9";

    // Text colour on background; the wordmark ("Know" on Page) is a logotype and exempt.
    internal static readonly (string Fg, string Bg)[] TextPairs =
    [
        (Ink, Card), (Body, Card), (Body, Box), (LinkColor, Card), (Card, Magenta), (AddedFg, AddedBg), (RemovedFg, RemovedBg),
        (FieldFg, FieldBg), (Muted, Card), (Muted, Page), (Muted, Box),
    ];
    internal static readonly HashSet<string> Palette = [Ink, Magenta, LinkColor, Body, Muted, Page, Card, Border, Box, AddedFg, AddedBg, RemovedFg, RemovedBg, FieldFg, FieldBg];

    const string Font = "Lato,Helvetica,Arial,sans-serif", Mono = "Menlo,Consolas,'Courier New',monospace";
    const int MaxValueBytes = 200; // FR-022: each calendar value in the HTML, encoded, "…" included

    // One email as contracts/messages.md describes it: multipart/alternative, text first, HTML with inline styles and one small <style> block.
    internal static MailMessage Build(string target, Message m, EmailOptions o)
    {
        var (text, html, footer) = (new StringBuilder(), new StringBuilder(), new StringBuilder());
        var r = new Renderer(m, text, html, footer);
        switch (m.Kind)
        {
            case MessageKind.Confirmation:
                r.Paragraph(r.T("confirmation.intro"));
                r.Link(r.T("confirmation.confirm"), m.ConfirmUrl!, button: true);
                r.Link(r.T("confirmation.decline"), m.DeclineUrl!);
                r.Paragraph(r.T("confirmation.expiry"), muted: true);
                break;
            case MessageKind.Changes:
                foreach (var g in m.Calendars) r.Calendar(g);
                if (m.NotListed > 0) r.Box(Texts.Format(m.Language, "changes.more", m.NotListed));
                r.Footer(r.T("changes.footer"));
                r.FooterLink(r.T("page.unsubscribe.title"), m.UnsubscribeUrl!);
                break;
            case MessageKind.Test:
                r.Paragraph(r.T("test.body"));
                r.Link(r.T("test.settings"), m.SettingsUrl!);
                r.Footer(r.T("changes.footer"));
                r.FooterLink(r.T("page.unsubscribe.title"), m.UnsubscribeUrl!);
                break;
            case MessageKind.Account:
                r.Paragraph(Texts.Format(m.Language, AccountBody(m), AccountValue(m)));
                if (m.Link is not null) r.Button(r.T($"account.{m.AccountKind}.action"), m.Link);
                r.Footer(r.T("account.footer"));
                break;
        }

        var mail = new MailMessage
        {
            From = new MailAddress(o.FromAddress, o.FromName, Encoding.UTF8),
            Subject = Subject(m),
            SubjectEncoding = Encoding.UTF8,
            HeadersEncoding = Encoding.UTF8,
        };
        mail.To.Add(new MailAddress(target));
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(text.ToString(), Encoding.UTF8, "text/plain"));
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            Document(m, mail.Subject, html.ToString(), footer.ToString()), Encoding.UTF8, "text/html"));
        // System.Net.Mail adds no Message-ID, and Gmail rejects mail without one.
        mail.Headers.Add("Message-ID", $"<{Guid.NewGuid():N}@{mail.From!.Host}>");
        // Account emails are about the account, not optional news: no unsubscribe (specs/004 contracts/account-emails.md).
        if (m.Kind is not (MessageKind.Confirmation or MessageKind.Account))
        {
            mail.Headers.Add("List-Unsubscribe", $"<{m.UnsubscribeUrl}>");
            mail.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
        }
        return mail;
    }

    // The shell every kind shares (contracts/email-html.md "Document"): table layout, a 600px card, the footer below it.
    const string Css = "<style>@media (max-width:480px){.px{padding:20px!important}.h1{font-size:24px!important}}:root{color-scheme:light;supported-color-schemes:light}a[x-apple-data-detectors]{color:inherit!important;text-decoration:none!important;font:inherit!important}</style>";

    static string Document(Message m, string subject, string body, string footer)
    {
        if (footer != "") footer = $"<div style=\"max-width:600px;margin:0 auto;padding:16px 4px 0;text-align:left;font-size:12px;line-height:1.5;color:{Muted};word-break:break-word;overflow-wrap:anywhere\">{footer}</div>";
        return $$"""
        <!DOCTYPE html><html lang="{{m.Language}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="x-apple-disable-message-reformatting"><meta name="format-detection" content="telephone=no,date=no,address=no,email=no">
        <meta name="color-scheme" content="light"><meta name="supported-color-schemes" content="light">
        {{Css}}
        <!--[if mso]><style>body,table,td,a{font-family:Arial,sans-serif}</style><![endif]--></head>
        <body bgcolor="{{Page}}" style="margin:0;padding:0;background-color:{{Page}}">
        <table role="presentation" width="100%" bgcolor="{{Page}}" cellspacing="0" cellpadding="0" border="0" style="background-color:{{Page}}"><tr><td align="center" style="padding:24px 12px;font-family:{{Font}}">
        <div style="padding:0 0 16px;text-align:center;font-size:28px;line-height:1;font-weight:900;color:{{Ink}}">InThe<span style="color:{{Magenta}}">Know</span></div>
        <!--[if mso]><table role="presentation" width="600" align="center"><tr><td><![endif]-->
        <table role="presentation" width="100%" align="center" bgcolor="{{Card}}" cellspacing="0" cellpadding="0" border="0" style="max-width:600px;margin:0 auto;background-color:{{Card}};border:1px solid {{Border}};border-radius:2px">
        <tr><td height="4" bgcolor="{{Magenta}}" style="height:4px;font-size:0;line-height:0;background-color:{{Magenta}}">&nbsp;</td></tr>
        <tr><td class="px" style="padding:32px;font-family:{{Font}};font-size:16px;line-height:1.5;color:{{Body}};text-align:left;word-break:break-word;overflow-wrap:anywhere">
        <h1 class="h1" style="margin:0 0 24px;font-size:28px;line-height:1.25;font-weight:900;color:{{Ink}}">{{Encoder.Encode(subject)}}</h1>{{body}}</td></tr></table>
        {{footer}}
        <!--[if mso]></td></tr></table><![endif]-->
        </td></tr></table></body></html>
        """;
    }

    // The HTML encoder, shared by the shell and the renderer.
    static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    // roleChanged and invite have one body per role.
    static string AccountBody(Message m) => m.AccountKind switch
    {
        "roleChanged" => $"account.roleChanged.body.{(Arg(m, "role") == "admin" ? "admin" : "user")}",
        "invite" => $"account.invite.body.{(Arg(m, "admin") == "true" ? "admin" : "user")}",
        _ => $"account.{m.AccountKind}.body",
    };

    // The body's one inserted value, if it has one.
    static string AccountValue(Message m) => m.AccountKind switch
    {
        "recoveryCodeUsed" => Arg(m, "remaining"),
        "methodAdded" or "methodRemoved" => Arg(m, "type"),
        "invite" => Arg(m, "expires"),
        _ => "",
    };

    static string Arg(Message m, string key) => m.Args?.GetValueOrDefault(key) ?? "";

    static string Subject(Message m) => m.Kind switch
    {
        MessageKind.Confirmation => Texts.Get(m.Language, "subject.confirmation"),
        MessageKind.Account => Texts.Get(m.Language, $"account.{m.AccountKind}.subject"),
        MessageKind.Test => Texts.Get(m.Language, "subject.test"),
        _ when m.Calendars.Count != 1 => Texts.Format(m.Language, "subject.changesCalendars", Count(m), m.Calendars.Count),
        _ when Count(m) == 1 => Texts.Format(m.Language, "subject.change", m.Calendars[0].Name),
        _ => Texts.Format(m.Language, "subject.changes", Count(m), m.Calendars[0].Name),
    };

    static int Count(Message m) => m.Calendars.Sum(g => g.Changes.Count) + m.NotListed;

    // Writes the text and HTML parts side by side. Every inserted value is HTML-encoded in the HTML part (FR-020).
    sealed class Renderer(Message m, StringBuilder text, StringBuilder html, StringBuilder footer)
    {
        // Every character range allowed, so Dutch text and arrows stay readable; markup characters are still encoded.
        static string E(string s) => Encoder.Encode(s);

        // E(s) cut to MaxValueBytes encoded, whole text elements only, so no character or entity is split (R8).
        static string Short(string s)
        {
            var e = E(s);
            if (Encoding.UTF8.GetByteCount(e) <= MaxValueBytes) return e;
            var (cut, bytes, parts) = (new StringBuilder(), 0, StringInfo.GetTextElementEnumerator(s));
            while (parts.MoveNext())
            {
                var part = E((string)parts.Current);
                var n = Encoding.UTF8.GetByteCount(part);
                if (bytes + n > MaxValueBytes - 3) break;
                bytes += n;
                cut.Append(part);
            }
            return cut.Append('…').ToString();
        }

        public string T(string key) => Texts.Get(m.Language, key);

        public void Paragraph(string s, bool muted = false)
        {
            text.Append(s).Append("\n\n");
            html.Append("<p style=\"margin:0 0 16px").Append(muted ? $";color:{Muted}" : "").Append("\">").Append(E(s)).Append("</p>");
        }

        // After the card, not inside it (FR-008).
        public void Footer(string s)
        {
            text.Append(s).Append("\n\n");
            footer.Append("<p style=\"margin:0 0 12px\">").Append(E(s)).Append("</p>");
        }

        // Muted, not LinkColor: (Muted, Page) is a checked TextPairs pair (specs/008 research R4).
        public void FooterLink(string label, string url)
        {
            text.Append(label).Append(": ").Append(url).Append("\n\n");
            footer.Append($"<p style=\"margin:0 0 12px\"><a href=\"{E(url)}\" style=\"color:{Muted};text-decoration:underline\">{E(label)}</a></p>");
        }

        // A note in a grey box.
        public void Box(string s)
        {
            text.Append(s).Append("\n\n");
            html.Append($"<table role=\"presentation\" width=\"100%\" bgcolor=\"{EmailChannel.Box}\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"margin:16px 0;background-color:{EmailChannel.Box}\"><tr><td style=\"padding:12px 16px\">")
                .Append(E(s)).Append("</td></tr></table>");
        }

        // The text part is always "label: url"; the HTML part is a text link, or the main action's button.
        public void Link(string label, string url, bool button = false)
        {
            text.Append(label).Append(": ").Append(url).Append("\n\n");
            if (button) ButtonHtml(label, url);
            else html.Append("<p style=\"margin:0 0 16px\">").Append(Anchor(label, url)).Append("</p>");
        }

        // The link on a line of its own in text; a button and a copyable fallback line in HTML.
        public void Button(string label, string url)
        {
            text.Append(label).Append(":\n").Append(url).Append("\n\n");
            ButtonHtml(label, url);
            html.Append($"<p style=\"margin:0 0 16px;font-size:14px;color:{Muted}\">").Append(E(T("account.linkFallback"))).Append("<br>").Append(Anchor(url, url)).Append("</p>");
        }

        static string Anchor(string label, string url) =>
            $"<a href=\"{E(url)}\" style=\"color:{LinkColor};text-decoration:underline\">{E(label)}</a>";

        // A bulletproof button: a coloured cell around a padded link (research R3).
        void ButtonHtml(string label, string url) => html.Append($"<table role=\"presentation\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"margin:8px 0 16px\"><tr><td bgcolor=\"{Magenta}\" style=\"background-color:{Magenta};border-radius:4px;mso-padding-alt:12px 24px\">")
            .Append($"<a href=\"{E(url)}\" style=\"display:inline-block;padding:12px 24px;line-height:20px;color:{Card};font-weight:700;text-decoration:none\">{E(label)}</a></td></tr></table>");

        public void Calendar(CalendarGroup g)
        {
            text.Append(g.Name).Append('\n').Append(T("changes.settings")).Append(": ").Append(g.SettingsUrl).Append("\n\n");
            html.Append($"<h2 style=\"margin:24px 0 4px;font-size:20px;line-height:1.3;color:{Ink}\">").Append(Short(g.Name)).Append("</h2>")
                .Append("<p style=\"margin:0 0 8px;font-size:14px\">").Append(Anchor(T("changes.settings"), g.SettingsUrl)).Append("</p>");
            foreach (var item in g.Changes) Change(item);
            text.Append('\n');
        }

        void Change(ChangeItem item)
        {
            var c = item.Change;
            var title = c.EventName + (item.LowPriority ? $" ({T("changes.lowPriority")})" : "");
            string[] lines = [When(c), What(c), Texts.Format(m.Language, "changes.detectedAt", Date(c.DetectedAt))];
            text.Append("- ").Append(title).Append('\n');
            foreach (var l in lines) text.Append("  ").Append(l).Append('\n');

            var (label, fg, bg) = (c.Kind, item.LowPriority) switch
            {
                (_, true) => (LabelText(c), Muted, Page),
                ("added", _) => (LabelText(c), AddedFg, AddedBg),
                ("removed", _) => (LabelText(c), RemovedFg, RemovedBg),
                _ => (LabelText(c), FieldFg, FieldBg),
            };
            var what = c.Kind switch
            {
                "added" or "removed" => WhereOf(c) is { } w ? Short(w) : null,
                _ when c.Field == "time" => null,
                _ => $"{Short(Value(c.OldValue))} → {Short(Value(c.NewValue))}",
            };
            html.Append($"<div style=\"border-top:1px solid {Border};padding:12px 0{(item.LowPriority ? $";color:{Muted}" : "")}\">")
                .Append($"<span style=\"display:inline-block;padding:2px 6px;font-size:12px;font-weight:700;letter-spacing:.04em;text-transform:uppercase;color:{fg};background-color:{bg}\">").Append(E(label)).Append("</span> ")
                .Append("<strong").Append(item.LowPriority ? "" : $" style=\"color:{Ink}\"").Append('>').Append(Short(c.EventName)).Append("</strong>");
            if (item.LowPriority) html.Append(" <span>(").Append(E(T("changes.lowPriority"))).Append(")</span>");
            html.Append($"<div style=\"margin-top:6px;font-family:{Mono};font-size:14px\">").Append(Short(lines[0])).Append("</div>");
            if (what is not null) html.Append("<div style=\"margin-top:4px\">").Append(what).Append("</div>");
            html.Append($"<div style=\"margin-top:4px;font-size:12px;color:{Muted}\">").Append(E(lines[2])).Append("</div></div>");
        }

        string LabelText(ChangeView c) => c.Kind switch
        {
            "added" or "removed" => T("kind." + c.Kind),
            _ when c.Field == "time" => T("kind.time"),
            _ => KindName(c.Field!),
        };

        // Where an added or removed event is or was (FR-019).
        string? WhereOf(ChangeView c) => (c.Kind == "added" ? c.NewValue : c.OldValue)?["fields"] is JsonObject f ? Where(f) : null;

        // The event's date and time; before → after for a time change.
        string When(ChangeView c) => c is { Kind: "changed", Field: "time" }
            ? $"{Value(c.OldValue)} → {Value(c.NewValue)}"
            : (c.EventAfter ?? (c.Kind == "added" ? c.NewValue : null) ?? c.EventBefore ?? c.OldValue) is JsonObject s && Range(s) is { } r
                ? r
                : Date(c.EventStartsAt);

        // What changed: the kind name, with its old and new value for a field change.
        string What(ChangeView c) => c.Kind switch
        {
            // FR-019: an added or removed event shows where it is or was.
            "added" or "removed" => WhereOf(c) is { } w
                ? $"{T("kind." + c.Kind)}: {w}"
                : T("kind." + c.Kind),
            _ when c.Field == "time" => T("kind.time"),
            _ => $"{KindName(c.Field!)}: {Value(c.OldValue)} → {Value(c.NewValue)}",
        };

        // The rooms, else the location, as recorded.
        string? Where(JsonObject fields) => fields["rooms"] is JsonArray { Count: > 0 } ? Value(fields["rooms"]) : FilterRules.Text(fields["location"]);

        string KindName(string field) => field switch
        {
            "type" => T("kind.activityType"),
            "name" or "status" or "location" or "description" or "rooms" or "teachers" or "groups" => T("kind." + field),
            _ when field.StartsWith("details.", StringComparison.Ordinal) => field["details.".Length..],
            _ => field,
        };

        string Value(JsonNode? v) => v switch
        {
            JsonArray a when a.Count > 0 => string.Join(", ", a.Select(x => FilterRules.Text(x) ?? x?.ToJsonString() ?? "")),
            JsonObject o => Range(o) ?? o.ToJsonString(),
            JsonValue s when FilterRules.Text(s) is { Length: > 0 } t => t,
            JsonValue s when s.GetValueKind() != System.Text.Json.JsonValueKind.String => s.ToJsonString(),
            _ => "–",
        };

        string? Range(JsonObject s)
        {
            if (!DateTimeOffset.TryParse(FilterRules.Text(s["start"]), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var start)) return null;
            if (!DateTimeOffset.TryParse(FilterRules.Text(s["end"]), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var end)) return Date(start);
            var (a, b) = (TimeZoneInfo.ConvertTime(start, m.TimeZone), TimeZoneInfo.ConvertTime(end, m.TimeZone));
            return a.Date == b.Date ? $"{Date(start)}–{b:HH:mm}" : $"{Date(start)} – {Date(end)}";
        }

        string Date(DateTimeOffset t) => Texts.Date(m.Language, m.TimeZone, t);
    }
}
