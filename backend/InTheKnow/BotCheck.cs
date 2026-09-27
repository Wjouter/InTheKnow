using System.Text.Json;
using System.Text.Json.Nodes;

namespace InTheKnow;

// A bot check provider is data: its name and siteverify URL (specs/004 contracts/bot-check.md, research R11).
sealed record BotCheckProvider(string Name, Uri VerifyUrl);

enum BotCheckOutcome { Passed, Failed, Unavailable }

// One siteverify client for every provider. Anything unexpected counts as unavailable, never as passed. Logs the
// provider, the outcome and the error codes, never the response token or the secret (FR-034, FR-040).
sealed class BotCheck(BotCheckProvider? provider, string siteKey, string secretKey, HttpClient http, ILogger<BotCheck> log, TimeProvider? time = null)
{
    public static readonly Dictionary<string, Uri> Providers = new()
    {
        ["turnstile"] = new("https://challenges.cloudflare.com/turnstile/v0/siteverify"),
        ["recaptcha"] = new("https://www.google.com/recaptcha/api/siteverify"),
    };
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public string? ProviderName => provider?.Name;
    public string? SiteKey => provider is null ? null : siteKey;
    public Uri? VerifyUrl => provider?.VerifyUrl;

    public async Task<BotCheckOutcome> VerifyAsync(string? response, CancellationToken ct)
    {
        if (provider is null) throw new InvalidOperationException("no bot check provider is configured");
        if (string.IsNullOrEmpty(response)) return Logged(BotCheckOutcome.Failed, ["missing-input-response"]);
        using var timeout = new CancellationTokenSource(Timeout, time ?? TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var reply = await http.PostAsync(provider.VerifyUrl,
                new FormUrlEncodedContent([new("secret", secretKey), new("response", response)]), linked.Token);
            if (!reply.IsSuccessStatusCode) return Logged(BotCheckOutcome.Unavailable, [$"http-{(int)reply.StatusCode}"]);
            if (JsonNode.Parse(await reply.Content.ReadAsStringAsync(linked.Token)) is not JsonObject json
                || json["success"] is not JsonValue success || success.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                return Logged(BotCheckOutcome.Unavailable, ["unparsable"]);
            if (success.GetValue<bool>()) return BotCheckOutcome.Passed;
            string[] codes = json["error-codes"] is JsonArray a ? [.. a.Select(c => c?.GetValueKind() == JsonValueKind.String ? c.GetValue<string>() : "?")] : [];
            return Logged(codes.Contains("internal-error") ? BotCheckOutcome.Unavailable : BotCheckOutcome.Failed, codes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Logged(BotCheckOutcome.Unavailable, ["timeout"]); }
        catch (Exception e) when (e is HttpRequestException or JsonException) { return Logged(BotCheckOutcome.Unavailable, [e.GetType().Name]); }
    }

    BotCheckOutcome Logged(BotCheckOutcome outcome, string[] codes)
    {
        log.LogWarning("Bot check ({Provider}): {Outcome} {Codes}", provider!.Name, outcome, string.Join(",", codes));
        return outcome;
    }
}
