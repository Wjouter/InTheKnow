using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using static InTheKnow.Api;

namespace InTheKnow;

// Notification routes (specs/003 contracts/api.md). The operator-key middleware of MapApi covers /api; the /n/ pages are
// public and act only through their path token (research R8). No response or page ever contains a calendar link.
static class NotificationApi
{
    static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    public static void MapNotificationApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        const string channels = "/subscribers/{subscriber}/channels";
        const string channel = channels + "/{channelId:long}";

        api.MapGet(channels, async (string subscriber, Channels c, CancellationToken ct) =>
            !Valid(subscriber) ? Problem(400, "invalid_subscriber") : Results.Json(new { items = (await c.ListAsync(subscriber, ct)).Select(ChannelJson) }));

        api.MapPost(channels, async (string subscriber, JsonNode? body, Channels c, CancellationToken ct) =>
        {
            if (!Valid(subscriber)) return Problem(400, "invalid_subscriber");
            var (type, target) = (FilterRules.Text(Field(body, "type")), FilterRules.Text(Field(body, "target")));
            if (type is null) return Problem(400, "invalid_type");
            if (target is null) return Problem(400, "invalid_target");
            var r = await c.AddAsync(subscriber, type, target, ct);
            return r.Error is null ? Results.Json(ChannelJson(r.Channel!), statusCode: 201) : Error(r);
        });

        api.MapDelete(channel, async (string subscriber, long channelId, Channels c, CancellationToken ct) =>
            !Valid(subscriber) ? Problem(400, "invalid_subscriber")
            : (await c.RemoveAsync(subscriber, channelId, ct)) is { Error: not null } r ? Error(r) : Results.NoContent());

        api.MapPost(channel + "/pause", (string subscriber, long channelId, Channels c, CancellationToken ct) =>
            Step(subscriber, () => c.PauseAsync(subscriber, channelId, ct), r => Results.Json(ChannelJson(r.Channel!))));
        api.MapPost(channel + "/resume", (string subscriber, long channelId, Channels c, CancellationToken ct) =>
            Step(subscriber, () => c.ResumeAsync(subscriber, channelId, ct), r => Results.Json(ChannelJson(r.Channel!))));
        api.MapPost(channel + "/confirmation", (string subscriber, long channelId, Channels c, CancellationToken ct) =>
            Step(subscriber, () => c.RequestConfirmationAsync(subscriber, channelId, ct), _ => Results.StatusCode(202)));
        api.MapPost(channel + "/test", (string subscriber, long channelId, Channels c, CancellationToken ct) =>
            Step(subscriber, () => c.RequestTestAsync(subscriber, channelId, ct), _ => Results.StatusCode(202)));

        const string notifications = "/subscribers/{subscriber}/calendars/{calendarId:long}/notifications";
        api.MapGet(notifications, async (string subscriber, long calendarId, Channels c, CancellationToken ct) =>
            !Valid(subscriber) ? Problem(400, "invalid_subscriber")
            : await c.GetSubscriptionAsync(subscriber, calendarId, ct) is { } s ? Results.Json(s) : Results.NotFound());

        api.MapPut(notifications, async (string subscriber, long calendarId, JsonNode? body, Channels c, CancellationToken ct) =>
        {
            if (!Valid(subscriber)) return Problem(400, "invalid_subscriber");
            var (value, error) = await c.SetSubscriptionAsync(subscriber, calendarId, body as JsonObject ?? [], ct);
            return error switch { null => Results.Json(value), "not_found" => Results.NotFound(), _ => Problem(400, error) };
        });

        const string settings = "/subscribers/{subscriber}/notification-settings";
        api.MapGet(settings, async (string subscriber, Channels c, CancellationToken ct) =>
            !Valid(subscriber) ? Problem(400, "invalid_subscriber") : Results.Json(await c.GetSettingsAsync(subscriber, ct)));

        api.MapPut(settings, async (string subscriber, JsonNode? body, Channels c, CancellationToken ct) =>
        {
            if (!Valid(subscriber)) return Problem(400, "invalid_subscriber");
            var (value, error) = await c.SetSettingsAsync(subscriber, body as JsonObject ?? [], ct);
            return error is null ? Results.Json(value) : Problem(400, error);
        });

        // The types a subscriber can add now (US6-3), and the operator's status per type (FR-030a).
        api.MapGet("/subscribers/{subscriber}/channel-types", (string subscriber, IEnumerable<IChannelType> types) =>
            !Valid(subscriber) ? Problem(400, "invalid_subscriber")
            : Results.Json(new { items = types.Where(t => t.Configured).OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new { type = t.Name }) }));

        api.MapGet("/channel-types", async (Notifier notifier, CancellationToken ct) => Results.Json(new
        {
            items = (await notifier.StatusAsync(ct)).Select(s => new
            {
                type = s.Type,
                configured = s.Configured,
                lastAttemptAt = s.LastAttemptAt?.UtcDateTime,
                lastOk = s.LastOk,
                lastReason = s.LastReason,
                consecutiveFailures = s.ConsecutiveFailures,
                failingSince = s.FailingSince?.UtcDateTime,
                waiting = s.Waiting,
            }),
        }));

        // Public pages: GET shows one button and changes nothing; POST acts. POST binds no body, so the RFC 8058
        // one-click body is accepted without being parsed (research R8).
        foreach (var (action, run) in new (string, Func<Channels, string, CancellationToken, Task<PageResult>>)[]
        {
            ("confirm", (c, t, ct) => c.ConfirmAsync(t, ct)),
            ("decline", (c, t, ct) => c.DeclineAsync(t, ct)),
            ("unsubscribe", (c, t, ct) => c.UnsubscribeAsync(t, ct)),
        })
        {
            app.MapGet($"/n/{action}/{{token}}", async (string token, Channels c, CancellationToken ct) =>
                await c.PageLanguageAsync(action == "unsubscribe", token, ct) is { } language ? Page(action, language, form: true) : NotFoundPage());
            app.MapPost($"/n/{action}/{{token}}", async (string token, Channels c, CancellationToken ct) =>
                await run(c, token, ct) is { Outcome: not PageOutcome.NotFound } r ? Page(action, r.Language, result: Result(action, r.Outcome)) : NotFoundPage())
                .DisableAntiforgery();
        }
    }

    static bool Valid(string subscriber) => Subscriptions.SubscriberPattern().IsMatch(subscriber);

    static JsonNode? Field(JsonNode? body, string key) => (body as JsonObject)?[key];

    static async Task<IResult> Step(string subscriber, Func<Task<ChannelResult>> run, Func<ChannelResult, IResult> ok)
    {
        if (!Valid(subscriber)) return Problem(400, "invalid_subscriber");
        var r = await run();
        return r.Error is null ? ok(r) : Error(r);
    }

    static IResult Error(ChannelResult r) => r.Error switch
    {
        "not_found" => Results.NotFound(),
        "invalid_type" or "invalid_target" => Problem(400, r.Error),
        "confirmation_limit" or "test_limit" => Problem(429, r.Error),
        "type_unavailable" => Problem(503, r.Error),
        // Not "status": Problem Details already uses that member for the HTTP status code.
        "channel_not_active" => Problem(409, r.Error, new() { ["channelStatus"] = r.Channel!.Status }),
        _ => Problem(409, r.Error!),
    };

    // contracts/api.md § Channel. The subscriber id is not part of it.
    static object ChannelJson(ChannelRow c) => new
    {
        id = c.Id,
        type = c.Type,
        target = c.Target,
        status = c.Status,
        failureReason = c.FailureReason,
        lastMessageAt = c.LastMessageAt?.UtcDateTime, // UtcDateTime: serialised with Z, as 001's timestamps are
        createdAt = c.CreatedAt.UtcDateTime,
    };

    static string? Result(string action, PageOutcome outcome) => outcome switch
    {
        PageOutcome.Done or PageOutcome.AlreadyDone when action != "confirm" => $"page.{action}.done",
        PageOutcome.Done => "page.confirm.done",
        PageOutcome.AlreadyDone => "page.alreadyDone",
        PageOutcome.Expired => "page.expired",
        PageOutcome.Declined => "page.declined",
        _ => "page.unavailable",
    };

    // A minimal page: a heading, one sentence and at most one button. No address, no link that changes anything.
    static IResult Page(string action, string language, bool form = false, string? result = null)
    {
        string T(string key) => Encoder.Encode(Texts.Get(language, key));
        var body = new StringBuilder($"<h1>{T($"page.{action}.title")}</h1>");
        if (form) body.Append($"<p>{T($"page.{action}.text")}</p><form method=\"post\"><button type=\"submit\">{T($"page.{action}.button")}</button></form>");
        else body.Append($"<p>{T(result!)}</p>");
        return Html(language, body.ToString(), 200);
    }

    // An unknown token: both languages, since there is no email to take the language from.
    static IResult NotFoundPage() => Html("en",
        string.Concat(new[] { "en", "nl" }.Select(l =>
            $"<section lang=\"{l}\"><h1>{Encoder.Encode(Texts.Get(l, "page.notFound.title"))}</h1><p>{Encoder.Encode(Texts.Get(l, "page.notFound.text"))}</p></section>")),
        404);

    static IResult Html(string language, string body, int status) => Results.Content(
        $"<!DOCTYPE html><html lang=\"{language}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>InTheKnow</title></head>" +
        $"<body style=\"font-family:sans-serif;max-width:32em;margin:2em auto;padding:0 1em\">{body}</body></html>",
        "text/html; charset=utf-8", Encoding.UTF8, status);
}
