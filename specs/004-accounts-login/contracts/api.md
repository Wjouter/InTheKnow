# Contract: HTTP API changes and additions

Extends [001](../../001-ical-change-detection/contracts/api.md), [002](../../002-subscriber-change-filters/contracts/api.md)
and [003](../../003-notification-channels/contracts/api.md). Their conventions still apply:
- camelCase JSON and ISO 8601 UTC;
- Problem Details with a `reason`;
- keyset paging (`items`, `next`);
- `/api` is reached only by the frontend's server side (001 § Transport).

## Authentication (replaces 001 § Authentication and § Subscriber id)

- A signed-in request sends `Authorization: Bearer <session token>`. The token comes from sign-in (research R2).
  The operator key is gone: no route accepts it (FR-033).
- **Every `/api` route requires a session** of a confirmed, not-disabled account, **except** the public routes
  below. An awaiting-code session (R6) counts only for `POST /api/auth/sign-in/code` and `POST /api/auth/sign-out`.
- **Every `/api/admin/…` route also requires** role `admin` and a session that proved a second factor (FR-032,
  FR-032a).
- Refusals, decided before the body is read:

| Status | `reason` | When |
|---|---|---|
| `401` | `not_signed_in` | No token, an unknown or ended session, idle past the lifetime, or an awaiting-code session outside its two routes |
| `403` | `admin_required` | Admin route, role `user` |
| `403` | `two_factor_required` | Admin route, and the session has not proved a second factor (US7-3, US7-11) |
| `403` | `reauthentication_required` | A step-up route (marked ⬆ below) with no re-authentication in the last 10 minutes (R6) |

- Deny by default also covers paths that match no route: without a session they get `401 not_signed_in`, not `404`.

- The client address and `User-Agent` are taken from `X-Forwarded-For`, and only from trusted proxies
  ([configuration.md](./configuration.md), R3).
- **Bot check** (FR-037): when a provider is configured, the routes marked 🤖 need `"botCheck": "<provider
  response>"` in the body. It is verified first, before anything else in the request:

| Status | `reason` | When |
|---|---|---|
| `400` | `bot_check_failed` | Missing, invalid, expired or already used (US9-2) |
| `503` | `try_again_later` | Provider unreachable, on every 🤖 route **except** sign-in, which then proceeds (FR-040) |

- **Throttling** of failed sign-ins and codes gives `429 too_many_attempts` with `Retry-After` (seconds). The
  per-source request limit of the 🚦 routes gives `429 too_many_requests` with `Retry-After` (R8).

## Moved routes (FR-029, FR-032)

`/api/subscribers/{subscriber}/…` becomes **`/api/me/…`**. The rest of each path, the bodies, the responses and the
reasons are unchanged. The `invalid_subscriber` reason is gone.

- An item that exists but belongs to another account gets exactly the same `404` as one that does not exist
  (FR-029, SC-002). This was already how 001 to 003 answered for another subscriber id.

| Before | After |
|---|---|
| `POST /api/subscribers/{s}/links` | `POST /api/me/links` |
| `GET /api/subscribers/{s}/calendars`, `DELETE …/calendars/{id}` | `GET /api/me/calendars`, `DELETE /api/me/calendars/{id}` |
| `GET /api/subscribers/{s}/changes` | `GET /api/me/changes` |
| `…/calendars/{id}/filter`, `…/filter/preset` | `/api/me/calendars/{id}/filter`, `…/filter/preset` |
| `…/presets`, `…/presets/{id}` | `/api/me/presets`, `/api/me/presets/{id}` |
| `…/channels`, `…/channels/{id}`, `…/pause`, `…/resume`, `…/confirmation`, `…/test` | `/api/me/channels/…` |
| `…/calendars/{id}/notifications` | `/api/me/calendars/{id}/notifications` |
| `…/notification-settings` | `/api/me/notification-settings`: reads and writes `account.language` and `.time_zone` |
| `…/channel-types` | `/api/me/channel-types` |
| `GET /api/calendars` (operator) | `GET /api/admin/calendars` |
| `GET/POST /api/presets`, `PUT/DELETE /api/presets/{id}` (operator, global) | `/api/admin/presets`, `/api/admin/presets/{id}` |
| `GET /api/channel-types` (operator status) | `GET /api/admin/channel-types` |

003's `/n/` pages are unchanged and public (FR-030).

`Channel` gains `"accountAddress": true | false`, which is true for the account's own address channel (FR-028, R15).

## Public routes (no session)

All of them return the same response whether or not the address has an account (FR-006, SC-003). Their order of
checks is: bot check → per-source limit → body validation → rules that depend only on the request → account lookup.

### `GET /api/auth/bot-check` (US9-3)

```json
{ "provider": "turnstile", "siteKey": "0x4AAAA…" }
```

The response is `{ "provider": null, "siteKey": null }` when no provider is configured. The secret key is never
returned.

### `POST /api/auth/sign-up` 🤖 🚦 (FR-001 to FR-006, US1-1, US1-7, US8)

```json
{ "email": "student@inholland.nl", "password": "…", "language": "nl", "invite": "<shareable invite token>?", "botCheck": "…?" }
```

`202` with an empty body, **both** when an account was created **and** when the FR-006 branch ran:
- the address already has a confirmed account: its owner gets a notice;
- the address has an unconfirmed account: its password is replaced and a new link is sent;
- the address belongs to a disabled account: nothing happens.

Over a per-address email cap, the response is the same `202` and nothing is sent. With `invite`, one use is
counted, whatever the branch.

| Status | `reason` |
|---|---|
| `400` | `invalid_email`, `invalid_language`, `password_length` (not 12 to 128), `password_common`, `password_is_email` |
| `403` | `sign_up_by_invitation` (FR-001b, decided from the address and settings alone) |
| `410` | `invite_invalid` (unknown, revoked, expired, used up, or a named invite's token) |

### `POST /api/auth/confirmation` 🤖 🚦 (FR-004, US1-3)

`{ "email": "…", "botCheck": "…?" }` → `202` always. A new link is sent only if the address has an unconfirmed,
not-disabled account under its cap of 3 per 24 h.

### `POST /api/auth/confirm` 🚦 (FR-003, US1-2)

`{ "token": "…", "password": "…" }` → `204`. The account is confirmed and its email channel created.

| Status | `reason` |
|---|---|
| `400` | `token_invalid` (unknown, used, expired or superseded), `password_incorrect` |

A wrong password is throttled like sign-in, keyed by the token's account (R8).

### `POST /api/auth/sign-in` 🤖 (FR-011, US1-4, US1-5, US4-3)

```json
{ "email": "…", "password": "…", "botCheck": "…?" }
```

`200`:

```json
{ "token": "<43 chars>", "secondFactorRequired": false }
```

With 2FA on, `secondFactorRequired` is `true`, and the token is valid only for the code step, for 10 minutes.

| Status | `reason` |
|---|---|
| `401` | `invalid_credentials`: unknown address, wrong password, or a password that breaks the rules (US1-5). Also for an account without a password. |
| `403` | `not_confirmed` (only after a correct password, US1-3), `account_disabled` (only after a correct password) |
| `429` | `too_many_attempts` |

### `POST /api/auth/sign-in/code` (awaiting-code session; FR-011, FR-017, US4-3 to US4-5)

`{ "code": "123456" | "abcd-efgh-ijkl-mnop" }` → `200 { "secondFactorRequired": false }`. The same token is now
fully signed in. A used recovery code queues `recoveryCodeUsed` with the number remaining.

| Status | `reason` |
|---|---|
| `400` | `code_invalid` (wrong, reused, or a used recovery code) |
| `429` | `too_many_attempts` |

### `POST /api/auth/password-reset` 🤖 🚦 (FR-009, US3-1)

`{ "email": "…", "botCheck": "…?" }` → `202` always. A link is sent only if the address has a not-disabled account
under its cap of 3 per 24 h.

### `POST /api/auth/password-reset/check` 🚦 (009)

`{ "token": "…" }` → `204` when the link can still be used, `400 token_invalid` otherwise. Side-effect free; see
[009 contract](../../009-reset-link-precheck/contracts/reset-check.md).

### `POST /api/auth/password-reset/complete` 🚦 (FR-009, FR-010, US3-2 to US3-5)

`{ "token": "…", "password": "…" }` → `204`. The password is replaced or added, an unconfirmed account is confirmed,
every session ends, the throttle for the address is cleared, and a notice is queued. 2FA is untouched.

| Status | `reason` |
|---|---|
| `400` | `token_invalid`, `password_length`, `password_common`, `password_is_email` |

### `POST /api/auth/email-change/confirm` 🚦 (FR-022, FR-023, US5-3)

`{ "token": "…" }` → `204`. The account and its address channel move to the new address.

| Status | `reason` |
|---|---|
| `400` | `token_invalid` |
| `409` | `address_taken`: another account took the address in the meantime. Only the link holder sees this. |

### `POST /api/auth/invite/accept` 🚦 (named invites only; FR-001c, US8-6)

`{ "token": "…", "password": "…", "language": "en" | "nl"? }` → `200`, with the same body as sign-in with
`secondFactorRequired: false`. The account is confirmed at once and signed in. An admin invite gives role `admin`,
whose admin routes stay locked until 2FA is on. `language` defaults to the invite's.

| Status | `reason` |
|---|---|
| `400` | `password_length`, `password_common`, `password_is_email`, `invalid_language` |
| `410` | `invite_invalid` (also for a shareable token, and when the address has had a confirmed account since the invite was made; the invite is then deleted) |

## Signed-in routes

### `POST /api/auth/sign-out` → `204` (FR-014)

Ends this session. It also works with an awaiting-code session.

### `POST /api/auth/sign-out-everywhere` → `204` (FR-014, US5-4)

Ends every session of the account, this one included.

### `POST /api/me/reauthenticate` (R6)

`{ "password": "…", "code": "…?" }` → `204`. It sets the step-up for 10 minutes. `code` is required when 2FA is on.

| Status | `reason` |
|---|---|
| `400` | `code_required`, `code_invalid` |
| `401` | `invalid_credentials` (also when the account has no password) |
| `429` | `too_many_attempts` |

### `GET /api/me`

```json
{
  "email": "student@inholland.nl",
  "role": "user",
  "language": "nl",
  "twoFactor": true,
  "recoveryCodesLeft": 9,
  "hasPassword": true,
  "pendingEmail": null,
  "createdAt": "2026-09-27T10:00:00Z"
}
```

`pendingEmail` is the address of an email change that was sent and not yet used or expired.

### `DELETE /api/me` ⬆ (FR-024, US5-5)

`204`. The account and everything it owns are deleted (research R1). The final notice goes to the address.

| Status | `reason` |
|---|---|
| `409` | `last_admin` |

### `PUT /api/me/password` ⬆ (FR-021, US6-4)

`{ "password": "…" }` → `204`. It changes the password, or adds one to an account without. Other sessions end, a
notice is queued, and a password added is a method added (`methodAdded`).

| Status | `reason` |
|---|---|
| `400` | `password_length`, `password_common`, `password_is_email` |

### `POST /api/me/email` ⬆ (FR-022, US5-2)

`{ "email": "…" }` → `202`. A link goes to the new address and a notice to the old one. For an address used by
another account it is also `202`, and nothing changes. A newer request supersedes an earlier one.

| Status | `reason` |
|---|---|
| `400` | `invalid_email`, `same_email` |

### `GET /api/me/sessions` (FR-014a, US5-6)

```json
{ "items": [ { "id": 41, "device": "Firefox on Windows", "createdAt": "…", "lastUsedAt": "…", "current": true } ] }
```

Awaiting-code sessions are not listed. No secret is included.

### `DELETE /api/me/sessions/{id}` (US5-7)

`204`. `404` if the session is not one of this account's. `409 use_sign_out` for the current session.

### `GET /api/me/methods` (FR-027a, US6-1)

```json
{ "items": [ { "id": 3, "type": "password", "createdAt": "…", "lastUsedAt": "…" } ] }
```

### `DELETE /api/me/methods/{id}` ⬆ (FR-027a, US6-3, US6-6)

`204`. The sessions opened with the method end, which includes this one if it was opened with it. A notice is
queued. `404` if the method is not this account's.

| Status | `reason` |
|---|---|
| `409` | `last_method` |

### Two-factor authentication (FR-015 to FR-020, US4)

| Route | Body | Result |
|---|---|---|
| `POST /api/me/2fa/setup` | none | `200 { "secret": "BASE32…", "uri": "otpauth://totp/…" }`, `409 two_factor_on` |
| `POST /api/me/2fa/enable` | `{ "code" }` | `200 { "recoveryCodes": ["abcd-efgh-ijkl-mnop", … 10] }`. This session now counts as having proved a second factor (US7-11). A notice is queued. `400 code_invalid`, `409 setup_not_started`, `409 two_factor_on`. |
| `DELETE /api/me/2fa` ⬆ | none | `204`. The re-authentication must have included a code (FR-018). Other sessions end and a notice is queued. `409 demote_first` for admins (US7-10), `409 two_factor_off`. |
| `POST /api/me/2fa/recovery-codes` | `{ "code" }` | `200 { "recoveryCodes": [...] }`. The earlier codes stop working (US4-7). `400 code_invalid`, `409 two_factor_off`, `429 too_many_attempts` (failures are throttled like the code step). |

The setup `secret` and the `recoveryCodes` appear in these responses only (FR-034).

## Admin routes (`/api/admin`, admin with a second factor)

Every action here queues the email the spec names and writes a security event with `actor_id`.

### `GET /api/admin/accounts?q=&after=&limit=` (FR-033c, US7-12)

`q` is a case-insensitive substring of the address, and `limit` is 1 to 500, default 100.

```json
{ "items": [ { "id": 12, "email": "…", "role": "user", "confirmed": true, "disabled": false, "twoFactor": false, "createdAt": "…" } ], "next": null }
```

### Account actions

| Route | Body | Result and reasons |
|---|---|---|
| `PUT /api/admin/accounts/{id}/role` | `{ "role": "admin" \| "user" }` | `200` with the account. `400 invalid_role`, `409 two_factor_required` (US7-9), `409 not_confirmed`, `409 account_disabled`, `409 last_admin` (US7-7) |
| `POST /api/admin/accounts/{id}/disable` | none | `200`. Sessions end, and messages queued for its channels are dropped. `409 demote_first` for an admin or for oneself (US7-15), `409 not_confirmed` for an unconfirmed account |
| `POST /api/admin/accounts/{id}/enable` | none | `200` |
| `DELETE /api/admin/accounts/{id}` | none | `204`, as `DELETE /api/me`. `409 demote_first` for an admin or oneself |
| `DELETE /api/admin/accounts/{id}/2fa` | none | `204`. 2FA and recovery codes are removed and every session ends (FR-019). `409 two_factor_off`. `409 demote_first` for one's own id (FR-032a point 3) |

`404` for an unknown id.

### Sign-up settings (FR-001a, FR-001d, US8-10)

- `GET /api/admin/signup-settings` → `{ "open": false, "allowedDomains": ["inholland.nl"] }`.
- `PUT /api/admin/signup-settings` takes the same shape; both fields are required. Domains are trimmed,
  lowercased and deduplicated.

| Status | `reason` |
|---|---|
| `400` | `invalid_settings` (a field is missing), `invalid_domain` (not a host name), `too_many_domains` (over 100) |

### Invites (FR-001c, FR-001e, US8-5 to US8-9)

`POST /api/admin/invites`:

```json
{ "kind": "named", "email": "guest@gmail.com", "language": "en", "grantsAdmin": false, "validDays": 7 }
{ "kind": "shareable", "language": "nl", "maxUses": 30, "validDays": 14 }
```

`201` returns an `Invite`. For a shareable invite it also carries `"link": "{FrontendBaseUrl}/account/sign-up?invite=…"`,
**only in this response**.

| Status | `reason` |
|---|---|
| `400` | `invalid_kind`, `invalid_email`, `invalid_language`, `invalid_valid_days` (not 1–90), `invalid_max_uses` (not 1–1000, or not 1 for named), `admin_needs_named` (US8-8) |
| `409` | `account_exists` (named, confirmed account, edge case), `invite_exists` (a pending named invite for that address; an expired one is replaced) |

`Invite`:

```json
{ "id": 5, "kind": "shareable", "email": null, "language": "nl", "grantsAdmin": false,
  "maxUses": 30, "used": 4, "left": 26, "expiresAt": "…", "createdBy": 12, "createdAt": "…" }
```

- `GET /api/admin/invites` → `{ "items": [Invite…] }`, the pending invites, newest first.
- `DELETE /api/admin/invites/{id}` → `204`. The link stops working at once (US8-9).
- `POST /api/admin/invites/{id}/resend` → `202`, named invites only (`409 not_named`). The previous link stops
  working when the new email is sent, and the invite's validity starts again from now.
