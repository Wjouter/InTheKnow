# Contract: Bot check providers

The seam of FR-038 (research R11). Only verification differs per provider, and it is data rather than code.

```csharp
sealed record BotCheckProvider(string Name, Uri VerifyUrl);

enum BotCheckOutcome { Passed, Failed, Unavailable }

// One instance, built from InTheKnow:Accounts:BotCheck. Null provider = off.
sealed class BotCheck(BotCheckProvider? provider, string siteKey, string secretKey, HttpClient http, ILogger<BotCheck> log)
{
    public string? ProviderName { get; }   // for GET /api/auth/bot-check
    public string? SiteKey { get; }
    public Task<BotCheckOutcome> VerifyAsync(string? response, CancellationToken ct);
}
```

- **Production providers**:
  - `turnstile` → `https://challenges.cloudflare.com/turnstile/v0/siteverify`;
  - `recaptcha` → `https://www.google.com/recaptcha/api/siteverify` (v2).
- **Request**: `POST application/x-www-form-urlencoded`, with `secret` and `response`. No `remoteip`.
- **Outcome mapping**:

| Reply | Outcome |
|---|---|
| 2xx, JSON `success: true` | `Passed` |
| 2xx, JSON `success: false`, `error-codes` without `internal-error` (incl. `timeout-or-duplicate`, `invalid-input-response`) | `Failed` |
| Missing or empty `response` field in our request | `Failed`, without calling the provider |
| Timeout (5 s), transport error, non-2xx, unparsable body, or `internal-error` | `Unavailable` |

## Shared rules (the same for every provider, verified with the test provider)

- The protected actions are sign-in (password step), sign-up (also with a shareable invite), confirmation request
  and password reset request (FR-037).
- The check runs first in the handler. `Failed` → `400 bot_check_failed`, for every address alike (edge case "Bot
  check failure and enumeration").
- `Unavailable` → sign-in continues, and throttling still applies. Every other protected action gets `503
  try_again_later` (FR-040).
- `Failed` and `Unavailable` log the provider name, the outcome and the error codes. They never log the response
  token or the secret (FR-040, FR-034).
- **Off** (no provider): `VerifyAsync` is never called and `botCheck` is ignored (US9-1).

## Test provider

`BotCheckTests` builds `new BotCheck(new("test", new Uri("https://bot.test/verify")), …, FakeHttp)`, and the
fake answers:
- one token as `Passed` the first time and `timeout-or-duplicate` the second time;
- one as `invalid-input-response`;
- one with a 500;
- one that never answers.

The route tests register this instance in the test host. A later provider is one more `BotCheckProvider` entry
plus its script in the frontend.
