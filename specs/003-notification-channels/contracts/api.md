# Contract: HTTP API additions

Extends [001 contracts/api.md](../../001-ical-change-detection/contracts/api.md) and
[002 contracts/api.md](../../002-subscriber-change-filters/contracts/api.md). Everything there applies to `/api`:
camelCase JSON, ISO 8601 UTC, Problem Details with a `reason`, the operator key on every request, and the
subscriber id pattern (`invalid_subscriber`). The public routes under `/n/` (at the end of this file) are the only
exception (FR-030).

`Channel` in responses:

```json
{
  "id": 7,
  "type": "email",
  "target": "student@example.nl",
  "status": "failing",
  "failureReason": "smtp_550",
  "lastMessageAt": "2026-10-02T08:05:12Z",
  "createdAt": "2026-09-30T12:00:00Z"
}
```

- `status` is one of `awaitingConfirmation`, `active`, `paused`, `unsubscribed` or `failing`.
- `failureReason` is `null` unless the status is `failing`.
- `target` is returned only on this subscriber's own routes (FR-020).

## Notification settings (FR-017, FR-018a)

### `GET /api/subscribers/{s}/notification-settings`

```json
{ "language": "en", "timeZone": null, "effectiveTimeZone": "Europe/Amsterdam" }
```

`timeZone: null` means the operator default applies (`effectiveTimeZone`). A subscriber who has never set anything
gets this default body.

### `PUT /api/subscribers/{s}/notification-settings`

Body: `{ "language"?: "en" | "nl", "timeZone"?: string | null }`. At least one field is required. `null` clears the
time zone. `200` returns the same body as `GET`. If the request is rejected, the previous values stay.

| Status | `reason` |
|---|---|
| `400` | `invalid_language`, `invalid_time_zone` (not an IANA id, research R9), `empty_body` |

## Subscription notifications (FR-008, FR-019a)

### `GET /api/subscribers/{s}/calendars/{calendarId}/notifications`

```json
{ "notify": true, "label": "Minor robotics", "name": "Minor robotics" }
```

`name` is what emails show: the `label`, else the feed's calendar name, else the calendar's host. `404` if the
subscriber has no subscription to the calendar.

### `PUT /api/subscribers/{s}/calendars/{calendarId}/notifications`

Body: `{ "notify"?: boolean, "label"?: string | null }`. At least one field is required. `null` clears the label.

- Turning `notify` off skips the pending changes of that calendar on every channel of the subscriber
  (`notificationsOff`).
- Turning it on sends only changes recorded afterwards.

`200` returns the same body as `GET`.

| Status | `reason` |
|---|---|
| `400` | `invalid_label` (empty, over 100 characters, or containing control characters), `invalid_notify`, `empty_body` |
| `404` | no such subscription |

### `GET /api/subscribers/{s}/calendars` (001, extended)

Each calendar object gains `"notify": true, "label": null, "name": "Hogeschool Inholland - Rooster - ALTI26VT4ES1"`,
with the same meaning as above (FR-028).

## Channel types

### `GET /api/subscribers/{s}/channel-types` (US6-3)

`{ "items": [ { "type": "email" } ] }`: the types a subscriber can add now, which means registered and configured.
In production this is `email` only, or nothing when no mail server is configured. The test-only type exists only in
the tests.

### `GET /api/channel-types` (operator, FR-030a)

```json
{
  "items": [
    {
      "type": "email",
      "configured": true,
      "lastAttemptAt": "2026-10-02T08:05:12Z",
      "lastOk": false,
      "lastReason": "unreachable",
      "consecutiveFailures": 14,
      "failingSince": "2026-10-02T07:40:00Z",
      "waiting": 312
    }
  ]
}
```

- `waiting` is the number of messages owed now: channels of that type with pending deliveries, plus unsent
  confirmation and test messages.
- The attempt fields are `null` until the first attempt after startup (research R10).
- The response contains no addresses or subscriber data.

## Channels (FR-028)

A `{channelId}` that does not belong to `{s}` returns `404` on every route below.

### `GET /api/subscribers/{s}/channels`

`{ "items": [Channel] }`, ordered by `id`.

### `POST /api/subscribers/{s}/channels`

Body: `{ "type": "email", "target": "student@example.nl" }`. Creates the channel as `awaitingConfirmation` and
queues its confirmation email (FR-005). `201` returns the `Channel`.

| Status | `reason` |
|---|---|
| `400` | `invalid_type` (not a registered type), `invalid_target` (for email: not a bare address, or over 254 characters, FR-004) |
| `409` | `target_taken` (already one of this subscriber's channels), `channel_limit` (5), `target_declined` (its recipient chose "this wasn't me", FR-006) |
| `503` | `type_unavailable` (the type is not configured, edge case "Channel type configured off") |

### `DELETE /api/subscribers/{s}/channels/{channelId}`

`204`. Deletes the channel, its address and its delivery records (FR-029). Pending sends are dropped.

### `POST /api/subscribers/{s}/channels/{channelId}/pause` and `…/resume`

`200` returns the `Channel`.
- **Pause**: only from `active`. Pending changes become `skipped` (`channelNotActive`).
- **Resume**: only from `paused`. Only changes recorded afterwards are sent.

| Status | `reason` |
|---|---|
| `409` | `invalid_status` (the channel is in another status) |

### `POST /api/subscribers/{s}/channels/{channelId}/confirmation`

Queues a new confirmation email. This is allowed from `awaitingConfirmation`, `unsubscribed` and `failing`
(FR-006). `202` with no body.

| Status | `reason` |
|---|---|
| `409` | `invalid_status` (the channel is `active` or `paused`), `target_declined` |
| `429` | `confirmation_limit` (3 confirmation emails per channel per 24 h, **including the initial one**, research R10) |
| `503` | `type_unavailable` |

### `POST /api/subscribers/{s}/channels/{channelId}/test` (FR-023)

Queues a test message. The channel must be `active`. `202` with no body.

| Status | `reason` |
|---|---|
| `409` | `channel_not_active` with an extra field `"status": "<the channel's status>"` (US7-2) |
| `429` | `test_limit` (5 per channel per 24 h) |
| `503` | `type_unavailable` |

## Public routes (no operator key, FR-007, FR-010, FR-030)

These are served by the same app at `{PublicBaseUrl}/n/…`, outside `/api` (research R8). They return
`text/html; charset=utf-8`: a minimal page with one button, or a result page. The page language is the language of
the confirmation email the link came from; for unsubscribe it is the subscriber's current language. An unknown
token gets `404` with a page saying the link is not valid, in English and Dutch.

| Route | `GET` | `POST` result |
|---|---|---|
| `/n/confirm/{token}` | "Confirm notifications for this address" button. Nothing changes. | `active`. If the token was already used or the channel is already active: "already confirmed", and nothing changes. If the token has expired: "expired, request a new one", and nothing changes. If the address was declined: "not possible", and nothing changes. If the channel type is not configured: "not available right now", and nothing changes (edge case "Channel type configured off"). |
| `/n/decline/{token}` | "This wasn't me" button. Nothing changes. | `unsubscribed` plus the declined hash (FR-006). Repeating it changes nothing more. |
| `/n/unsubscribe/{token}` | "Stop these notifications to this address" button. Nothing changes. | `unsubscribed` (from `active`, `paused` or `failing`, otherwise no change). Also accepts the RFC 8058 one-click POST with the body `List-Unsubscribe=One-Click` and answers it with `200`. |

- Each action affects only the channel its token belongs to.
- `POST` returns `200` with the result page, including when nothing changed.
- There is no redirect, and no link on these pages changes anything.

## Errors summary (new reasons)

| Status | `reason` |
|---|---|
| `400` | `invalid_language`, `invalid_time_zone`, `invalid_label`, `invalid_notify`, `invalid_type`, `invalid_target`, `empty_body` |
| `404` | no such subscription or channel |
| `409` | `target_taken`, `channel_limit`, `target_declined`, `invalid_status`, `channel_not_active` |
| `429` | `confirmation_limit`, `test_limit` |
| `503` | `type_unavailable` |
