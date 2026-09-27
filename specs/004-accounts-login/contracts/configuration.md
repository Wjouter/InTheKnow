# Contract: Operator configuration changes

Extends [001](../../001-ical-change-detection/contracts/configuration.md) and
[003](../../003-notification-channels/contracts/configuration.md). The same sources and precedence apply, and
configuration is read at startup.

## Removed

| Key | Change |
|---|---|
| `InTheKnow:ApiKey` | **Retired** (FR-033). It is no longer required. If it is set, startup logs a warning that it is ignored. |

## Shape (added to the shipped `appsettings.json`)

```json
{
  "InTheKnow": {
    "Accounts": {
      "SessionIdleLifetime": "30.00:00:00",
      "SourceFailuresPerHour": 3000,
      "SourceRequestsPerHour": 1000,
      "TrustedProxies": [],
      "BotCheck": {
        "Provider": "",
        "SiteKey": "",
        "SecretKey": ""
      }
    }
  }
}
```

## Settings (all under `InTheKnow:Accounts:`)

| Key | Type | Default | Rule | FR |
|---|---|---|---|---|
| `SessionIdleLifetime` | TimeSpan | `30.00:00:00` | 1 hour to 365 days, otherwise startup fails | FR-013 |
| `SourceFailuresPerHour` | int | `3000` | At least 20. The failed sign-ins and codes from one source (IPv4 address or IPv6 /64) across all accounts before that source waits (research R8). Sized for one campus address. | FR-012 |
| `SourceRequestsPerHour` | int | `1000` | At least 10. The sign-up, confirmation, reset and invite requests per source (research R8). | FR-012a |
| `TrustedProxies` | list of IP addresses or CIDR networks | empty | The frontend's server and the reverse proxy. Only their `X-Forwarded-For` is believed (research R3). **Empty means the direct peer is the client**, and startup warns that all users then share one throttling source. An invalid entry fails startup. | FR-012 |
| `BotCheck:Provider` | string | empty | Empty (off), `turnstile` or `recaptcha` (reCAPTCHA v2, research R11). Anything else fails startup. | FR-037 to FR-039 |
| `BotCheck:SiteKey` | string | empty | Public. **Required when a provider is set.** Returned by `GET /api/auth/bot-check`. | FR-039 |
| `BotCheck:SecretKey` | string | empty | **Secret, required when a provider is set.** Set it through the environment (`InTheKnow__Accounts__BotCheck__SecretKey`) or a secret store, never in a committed file. | FR-039 |

The account emails use 003's `Notifications:Email:*`, `RetryWindow` and `FrontendBaseUrl`, with no new key
(FR-025, FR-036). Startup logs a warning when email is not configured: sign-up still records accounts, but
confirmation, reset and invite emails wait until it is.

## Server command

```bash
dotnet InTheKnow.dll make-admin <address> [--language en|nl]
```

In the container: `docker compose exec app dotnet InTheKnow.dll make-admin <address>`.

- It uses the same configuration, including `ConnectionStrings:Default`.
- It exits with `1` if the database schema is behind this build. Start the server once to migrate it.
- It prints `promoted` or `invite queued`. The running server sends the email (research R13).

## Constants (not configurable)

| Constant | Value | Source |
|---|---|---|
| Password length | 12 to 128 characters | FR-007 |
| Password hashing | `PasswordHasher` v3, PBKDF2-HMAC-SHA512, 210,000 iterations | FR-008, research R4 |
| Confirmation link and email-change link validity | 24 h | FR-003, FR-022 |
| Reset link validity | 1 h | FR-009 |
| Invite validity | 1 to 90 days, default 7 (chosen per invite) | FR-001c |
| Uses per shareable invite | 1 to 1,000, default 1 | FR-001c |
| Unconfirmed account deletion | 30 days after sign-up | FR-005 |
| Per-address email caps | 3 confirmations, 3 resets, 3 "someone tried to sign up" notices per 24 h | FR-004, FR-012a |
| Throttle per (address, source) | 5 free, then 30 s, 1, 2, 4, 8, 15, 15, … min. Forgotten 1 h after the last failure. | FR-012, research R8 |
| Throttle per address, all sources | 20 failures in a rolling hour → 15 min wait, then one attempt per wait | FR-012, research R8, R17 |
| Awaiting-code session lifetime | 10 min | research R6 |
| Step-up re-authentication validity | 10 min | research R6 |
| `last_used_at` write interval | 5 min | research R2 |
| TOTP | SHA-1, 6 digits, 30 s, ±1 step | FR-017, research R5 |
| Recovery codes | 10, each 80 bit | FR-016, research R5 |
| Bot check verification timeout | 5 s | FR-040, research R11 |
| Security event retention | 90 days | FR-035 |
| Allowed domains | at most 100 | research R10 |

## Rules that follow from this feature

- **`Logging:LogLevel:Microsoft.AspNetCore` MUST stay at `Warning` or higher** (003 rule). At `Information`, the
  authentication middleware logs details that may include the path, and ASP.NET logs request lines.
- The reverse proxy and the frontend's server MUST set `X-Forwarded-For` and be listed in `TrustedProxies`. The
  frontend's server MUST also forward the browser's `User-Agent`.
- The frontend's `/account/confirm`, `/account/reset`, `/account/email`, `/account/invite` and `/account/sign-up`
  pages hold tokens in their query string. They MUST send `Referrer-Policy: no-referrer` and act only on an
  explicit button (research R9). That is the frontend feature's contract.
- **Bot check privacy**: turning a provider on sends visitors' browser signals to that provider. The operator names
  it in the privacy notice (spec Assumptions).

## Local development

```bash
InTheKnow__Accounts__BotCheck__Provider=turnstile
InTheKnow__Accounts__BotCheck__SiteKey=1x00000000000000000000AA
InTheKnow__Accounts__BotCheck__SecretKey=1x0000000000000000000000000000000AA
```

These are Cloudflare's published always-pass test keys. The secret `2x0000000000000000000000000000000AA` always
fails, and `3x0000000000000000000000000000000AA` answers "already spent". Google's published reCAPTCHA v2 test keys
work the same way with `Provider=recaptcha`.
