# Contract: HTTP API

This is the ASP.NET Core minimal API served by the same process as the checker. The operator uses it now, and the
SvelteKit frontend will use it later, **from its server side only**.

- **Base path**: `/api`
- **Format**: JSON (`application/json`), with camelCase field names.
- **Timestamps**: ISO 8601 UTC.
- **Errors**: RFC 9457 Problem Details (`application/problem+json`), with an extra `reason` field that holds a
  machine-readable code.

## Authentication and the account (changed by 004)

The operator key is retired. Every `/api` request now needs a session of a signed-in account, and the user routes act
for that account: see [004 contracts/api.md](../../004-accounts-login/contracts/api.md) § Authentication and § Moved
routes.
- `/api/me/…` became **`/api/me/…`**, with the same bodies and responses.
- The operator overview moved to **`GET /api/admin/calendars`**, which needs an admin with a second factor.
- The `invalid_subscriber` reason is gone, because there is no subscriber id in the path any more.

## Endpoints

### `POST /api/me/links`: add a link (US5-1, FR-001, FR-003 to FR-007)

Request body:

```json
{ "url": "https://mytimetables.example/ical?..." }
```

The link travels only in the body, never in the path or query string (FR-032). The server then:
1. checks the scheme (`https` only) and address;
2. validates the link with one fetch (research R11);
3. attaches the link to the matching calendar, or creates one.

When the link creates a calendar, the validation response becomes that calendar's baseline (FR-023), so
`GET …/calendars` shows `lastOk: true` straight away. When it joins an existing calendar, nothing from the
validation is stored.

| Status | Body |
|---|---|
| `201` | `{ "calendarId": 12, "linkId": 40, "calendarIsNew": false }` |
| `200` | The same shape. The subscriber already had this exact link, so nothing changed. |
| `400` | `reason`: `invalid_url`, `unsupported_scheme` (anything other than `https`, including `http`), `url_too_long`, `blocked_address` |
| `422` | `reason`: `validation_failed:<code>`, where `<code>` is one of the check failure codes in [data-model.md](../data-model.md), for example `validation_failed:empty_feed` |
| `429` | `reason`: `host_budget_exhausted`. The validation waits up to 5 s for the host's next free request slot (FR-012a). This status means more than 5 s of requests were already queued for that host. The response carries `Retry-After: 5`; the client retries after that many seconds. |

### `GET /api/me/calendars`: the subscriber's calendars (FR-030)

`200`:

```json
[
  {
    "calendarId": 12,
    "status": "ok",
    "lastCheckedAt": "2026-09-24T19:30:00Z",
    "lastOk": true,
    "lastError": null,
    "consecutiveFailures": 0,
    "nextCheckAt": "2026-09-24T19:40:00Z",
    "notify": true,
    "label": null,
    "name": "Hogeschool Inholland - Rooster - ALTI26VT4ES1",
    "links": [ { "linkId": 40, "flagged": false, "createdAt": "2026-09-24T18:00:00Z" } ]
  }
]
```

Links are listed by id only, never by URL (FR-032). An unknown subscriber gets `[]`. Since feature 003, each calendar
also has its notification switch (`notify`), its `label` and the `name` emails use (label, else the feed's calendar
name, else the host); see [003 contracts/api.md](../../003-notification-channels/contracts/api.md).

### `DELETE /api/me/calendars/{calendarId}`: remove a subscription (US5-4, FR-008)

This deletes the subscriber's links to that calendar. If no links remain, the calendar and all its data are
deleted in the same transaction.

| Status | Meaning |
|---|---|
| `204` | Removed |
| `404` | The subscriber has no subscription to this calendar |

### `GET /api/me/changes?after={id}&limit={n}`: recorded changes (US5-3)

Since 005, `order=newest` with a `before` cursor pages newest first: see [005 contracts/api.md](../../005-sveltekit-frontend/contracts/api.md).

Returns the changes of the calendars this subscriber currently subscribes to, ordered by `id` ascending.
Pagination is keyset-based:
- `after` defaults to 0.
- `limit` defaults to 100 and allows 1 to 500. Values outside that range get `400` with the reason
  `invalid_limit`.

`200`:

```json
{
  "items": [
    {
      "id": 981,
      "calendarId": 12,
      "eventKey": "Xedule!GI3TINRXGY2Q@schedule.inholland.nl",
      "kind": "changed",
      "field": "rooms",
      "oldValue": ["AL00.A1.38"],
      "newValue": ["AL00.A1.20"],
      "eventName": "ROS2 basics",
      "eventStartsAt": "2026-10-01T06:30:00Z",
      "detectedAt": "2026-09-24T19:30:02Z"
    }
  ],
  "next": 981
}
```

- `next` is `null` when there are no more items.
- The values of `kind`, `field`, `oldValue` and `newValue` follow [detected-change.md](./detected-change.md).
- This endpoint only reads. Delivery status lives in `delivery` (feature 003).
- Since feature 002, each item also has `"outcome": "normal" | "lowPriority" | "hidden"` under the subscriber's
  filter, and hidden changes are left out unless `includeHidden=true` (any other value than `true`/`false` gets
  `400` `invalid_include_hidden`). A page is only short when no visible changes remain. See
  [002 contracts/api.md](../../002-subscriber-change-filters/contracts/api.md) § Changes.

### `GET /api/admin/calendars?status={ok|failing}&overdue={true}&after={id}&limit={n}`: operator overview (US5-5, FR-027)

`status` and `overdue` are both optional filters. `status` other than `ok` or `failing` gets `400` with the reason
`invalid_status`. `overdue=true` returns only calendars with `nextCheckAt < now - 1 min`, which shows when the
per-host budget is making checks fall behind.

The list is paged by calendar id like the changes endpoint, because at school scale it holds thousands of
calendars: `after` defaults to 0, and `limit` defaults to 100 and allows 1 to 500 (otherwise `400`
`invalid_limit`).

`200`: `{ "items": [...], "next": <id or null> }`. Each item is a calendar object from `GET …/calendars` above,
ordered by `calendarId`, where `links` is replaced by `"subscribers": <n>, "validLinks": <n>, "totalLinks": <n>`.
No links or subscriber ids are included.

## Not in this feature

- CORS: not configured, because the browser never calls the API directly; the frontend's server sends the session (004).
- OpenAPI document generation and a health endpoint: to be added when the frontend or deployment needs them.
- User login: feature 004.

## Transport

- The app listens on plain HTTP on the same host only: loopback (the ASP.NET Core default `http://localhost:5000`)
  or a host-internal container network. It never listens on an address reachable across a physical network.
- **Outside local development it MUST run behind a TLS-terminating reverse proxy on the same host.** The
  constitution's HTTPS rule applies, and session tokens must never travel unencrypted.
- The API does not log request bodies. ASP.NET Core does not log them by default, and HTTP logging middleware is
  not enabled.
