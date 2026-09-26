# Contract: Operator configuration additions

Extends [001 contracts/configuration.md](../../001-ical-change-detection/contracts/configuration.md). The same
sources and precedence apply, and configuration is read at startup.

## Shape (added to the shipped `appsettings.json`)

```json
{
  "InTheKnow": {
    "Notifications": {
      "CollectionWindow": "00:05:00",
      "RetryWindow": "1.00:00:00",
      "DefaultTimeZone": "Europe/Amsterdam",
      "PublicBaseUrl": "",
      "FrontendBaseUrl": "",
      "Email": {
        "Host": "",
        "Port": 587,
        "StartTls": true,
        "Username": "",
        "Password": "",
        "FromAddress": "",
        "FromName": "InTheKnow",
        "MaxMessagesPerMinute": 60
      }
    }
  }
}
```

## Settings (all under `InTheKnow:Notifications:`)

| Key | Type | Default | Rule | FR |
|---|---|---|---|---|
| `CollectionWindow` | TimeSpan | `00:05:00` | Must be between 1 and 60 minutes, otherwise startup fails | FR-014 |
| `RetryWindow` | TimeSpan | `1.00:00:00` | Must be at least `CollectionWindow` + 1 hour, otherwise startup fails. A shorter window expires changes as soon as they become due, or while they wait for a send slot (FR-016). | FR-026 |
| `DefaultTimeZone` | IANA id | `Europe/Amsterdam` | Must resolve to a time zone with an IANA id, otherwise startup fails | FR-018a |
| `PublicBaseUrl` | absolute URL | none | Where the `/n/` pages are reachable from the internet (the reverse proxy). `https`, except `http://localhost` for development. **Required when email is configured.** | FR-007, FR-010 |
| `FrontendBaseUrl` | absolute URL | none | The frontend. Emails link to `{FrontendBaseUrl}/calendars/{id}/notifications` and `{FrontendBaseUrl}/notifications`. Same `https` rule. **Required when email is configured.** | FR-009 |
| `Email:Host` | string | empty | **Empty means email is not configured.** Then channels cannot be added or confirmed, and pending deliveries expire (spec edge case). | FR-002 |
| `Email:Port` | int | `587` | 1 to 65535. STARTTLS submission. Implicit TLS on port 465 is **not supported** (research R2). | |
| `Email:StartTls` | bool | `true` | `false` is allowed only when `Host` is `localhost`, `127.0.0.1` or `::1` (the local capture), otherwise startup fails (Constitution II) | |
| `Email:Username`, `Email:Password` | string | empty | Both or neither. **The password is secret**: set it through the environment (`InTheKnow__Notifications__Email__Password`) or a secret store, never in a committed file. | FR-032 |
| `Email:FromAddress` | email address | none | **Required when email is configured.** Sender domain setup (SPF, DKIM, DMARC) is the operator's task. | FR-022 |
| `Email:FromName` | string | `InTheKnow` | 1 to 100 characters, no control characters | FR-022 |
| `Email:MaxMessagesPerMinute` | int | `60` | Must be at least 1. Confirmation, test and change emails all count. | FR-016 |

## Rules that follow from this feature

- **`Logging:LogLevel:Microsoft.AspNetCore` MUST stay at `Warning` or higher.** At `Information`, ASP.NET Core logs
  request paths, and the `/n/` paths contain tokens (FR-031, research R8). The shipped `appsettings.json` already
  sets `Warning`.
- The reverse proxy exposes `/n/` publicly. `/api` stays reachable only by the frontend's server side and the
  operator (001 contracts/api.md § Transport).
- The container image (later feature) needs ICU and tzdata for Dutch dates and IANA time zones. A test fails
  without them (research R9).

## Constants (not configurable)

| Constant | Value | Source |
|---|---|---|
| Channels per subscriber | 5 | FR-001 |
| Confirmation emails per channel per 24 h (initial included) | 3 | FR-006, research R10 |
| Confirmation link validity | 48 h | FR-005 |
| Test messages per channel per 24 h | 5 | FR-023 |
| Changes listed per message | 50 | FR-015 |
| Unused channel deletion | 30 days | FR-029a |
| Backoff | `min(1 min × 2^(n−1), 1 h)` | FR-026, research R4 |
| Send timeout | 30 s | research R5 |
| Notifier idle sleep | 1 s. Expiry every minute, cleanup every 10 minutes. | research R4, R10 |

## Local development

`compose.yaml` gains a Mailpit service for the manual walkthrough (research R11):

```bash
InTheKnow__Notifications__Email__Host=localhost
InTheKnow__Notifications__Email__Port=1025
InTheKnow__Notifications__Email__StartTls=false
InTheKnow__Notifications__Email__FromAddress=intheknow@localhost
InTheKnow__Notifications__PublicBaseUrl=http://localhost:5000
InTheKnow__Notifications__FrontendBaseUrl=http://localhost:5173
```

You can then read the emails at `http://localhost:8025`.
