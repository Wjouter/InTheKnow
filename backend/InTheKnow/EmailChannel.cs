using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
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

    // One email as contracts/messages.md describes it: multipart/alternative, text first, HTML with inline styles only.
    internal static MailMessage Build(string target, Message m, EmailOptions o)
    {
        var (text, html) = (new StringBuilder(), new StringBuilder());
        var r = new Renderer(m, text, html);
        switch (m.Kind)
        {
            case MessageKind.Confirmation:
                r.Paragraph(r.T("confirmation.intro"));
                r.Link(r.T("confirmation.confirm"), m.ConfirmUrl!);
                r.Link(r.T("confirmation.decline"), m.DeclineUrl!);
                r.Paragraph(r.T("confirmation.expiry"));
                break;
            case MessageKind.Changes:
                foreach (var g in m.Calendars) r.Calendar(g);
                if (m.NotListed > 0) r.Paragraph(Texts.Format(m.Language, "changes.more", m.NotListed));
                r.Paragraph(r.T("changes.footer"));
                break;
            case MessageKind.Test:
                r.Paragraph(r.T("test.body"));
                r.Link(r.T("test.settings"), m.SettingsUrl!);
                break;
            case MessageKind.Account:
                r.Paragraph(Texts.Format(m.Language, AccountBody(m), AccountValue(m)));
                if (m.Link is not null) r.Button(r.T($"account.{m.AccountKind}.action"), m.Link);
                r.Paragraph(r.T("account.footer"));
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
            $"<!DOCTYPE html><html lang=\"{m.Language}\"><body style=\"font-family:sans-serif;font-size:14px;color:#222\">{html}</body></html>",
            Encoding.UTF8, "text/html"));
        // Account emails are about the account, not optional news: no unsubscribe (specs/004 contracts/account-emails.md).
        if (m.Kind is not (MessageKind.Confirmation or MessageKind.Account))
        {
            mail.Headers.Add("List-Unsubscribe", $"<{m.UnsubscribeUrl}>");
            mail.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
        }
        return mail;
    }

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
    sealed class Renderer(Message m, StringBuilder text, StringBuilder html)
    {
        // Every character range allowed, so Dutch text and arrows stay readable; markup characters are still encoded.
        static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);
        static string E(string s) => Encoder.Encode(s);

        public string T(string key) => Texts.Get(m.Language, key);

        public void Paragraph(string s)
        {
            text.Append(s).Append("\n\n");
            html.Append("<p style=\"margin:0 0 12px\">").Append(E(s)).Append("</p>");
        }

        public void Link(string label, string url)
        {
            text.Append(label).Append(": ").Append(url).Append("\n\n");
            html.Append("<p style=\"margin:0 0 12px\"><a href=\"").Append(E(url)).Append("\">").Append(E(label)).Append("</a></p>");
        }

        // The link as a button in HTML, and on a line of its own in text.
        public void Button(string label, string url)
        {
            text.Append(label).Append(":\n").Append(url).Append("\n\n");
            html.Append("<p style=\"margin:16px 0\"><a href=\"").Append(E(url))
                .Append("\" style=\"display:inline-block;padding:10px 16px;background:#2a6;color:#fff;text-decoration:none;border-radius:4px\">")
                .Append(E(label)).Append("</a></p>");
        }

        public void Calendar(CalendarGroup g)
        {
            text.Append(g.Name).Append('\n').Append(T("changes.settings")).Append(": ").Append(g.SettingsUrl).Append("\n\n");
            html.Append("<h2 style=\"font-size:16px;margin:16px 0 4px\">").Append(E(g.Name)).Append("</h2>")
                .Append("<p style=\"margin:0 0 8px;font-size:12px\"><a href=\"").Append(E(g.SettingsUrl)).Append("\">")
                .Append(E(T("changes.settings"))).Append("</a></p>");
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
            html.Append("<div style=\"margin:0 0 10px;padding-left:8px;border-left:3px solid ")
                .Append(item.LowPriority ? "#bbb" : "#2a6").Append("\"><strong>").Append(E(c.EventName)).Append("</strong>");
            if (item.LowPriority) html.Append(" <em style=\"color:#777\">(").Append(E(T("changes.lowPriority"))).Append(")</em>");
            foreach (var l in lines) html.Append("<br>").Append(E(l));
            html.Append("</div>");
        }

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
            "added" or "removed" => (c.Kind == "added" ? c.NewValue : c.OldValue)?["fields"] is JsonObject f && Where(f) is { } w
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
