# Contract: HTTP API additions (FR-045)

Extends [001](../../001-ical-change-detection/contracts/api.md), [002](../../002-subscriber-change-filters/contracts/api.md),
[003](../../003-notification-channels/contracts/api.md) and [004](../../004-accounts-login/contracts/api.md). Their
conventions apply unchanged:
- camelCase JSON and ISO 8601 UTC;
- Problem Details with a `reason`;
- keyset paging (`items`, `next`);
- a signed-in session on every `/api` route.

Everything here is **additive**: every existing route, parameter, response and reason behaves exactly as before.
The spec names these four additions in FR-045. There are no others.

## 1. Newest-first changes: `GET /api/me/changes` (FR-025)

Two new query parameters:

| Parameter | Values | Default | Meaning |
|---|---|---|---|
| `order` | `oldest` \| `newest` | `oldest` | `newest` returns items by `id` **descending** |
| `before` | change id | none (start at the newest) | Only with `order=newest`: return changes with `id < before` |

- `after` still works only with `order=oldest` (001).
- `limit`, `includeHidden`, `outcome` and the visibility rules are unchanged. The server keeps reading batches until
  it has `limit` visible items or runs out, so a page is never short while older visible changes remain (the 002 rule,
  in the other direction).
- `next` is the id of the last (oldest) item returned when more remain, and `null` otherwise. The client passes it
  as `before`.

| Status | `reason` | When |
|---|---|---|
| `400` | `invalid_order` | `order` is not `oldest` or `newest` |
| `400` | `invalid_cursor` | `before` with `order=oldest` (or without `order`), or `after` with `order=newest` |

Without `order`, a request gets exactly the 001/002 response (SC: additive).

**Backend tests** (`ApiTests`):
- newest-first order across two calendars;
- `before` paging reaches the oldest change with no gaps or repeats;
- a page is filled past hidden changes and is never short while older visible ones exist;
- `includeHidden` with `newest`;
- the two new reasons;
- the unchanged oldest-first response.

## 2. User settings: `GET /api/me` and `PUT /api/me/settings` (FR-046)

`GET /api/me` gains one field:

```json
{ "email": "…", "role": "user", "language": "nl", …, "homePage": "changes" }
```

`homePage` is `changes` (the default) or `combined`.

`PUT /api/me/settings`:

```json
{ "homePage": "combined" }
```

`200` returns `{ "homePage": "combined" }`.
- At least one known field is required. An unknown field is refused, so a later setting can't be silently dropped
  by an older API.
- No step-up is needed, because this is a display preference.

| Status | `reason` |
|---|---|
| `400` | `empty_body` (no known field), `invalid_settings` (an unknown field), `invalid_home_page` (not `changes`/`combined`) |

**Backend tests** (`AccountTests`):
- the default `changes` for new and existing accounts;
- set and read back;
- the three reasons;
- another account's value is untouched;
- the DB `CHECK` refuses other values.

## 3. Rule position on rule validation errors (FR-032)

Every `400` whose `reason` is one of `invalid_outcome`, `empty_conditions`, `invalid_condition`, or
`invalid_rules` caused by one rule gains a `rule` member:

```json
{ "status": 400, "reason": "invalid_condition", "rule": 2 }
```

- `rule` is the 0-based index of the first rule that failed, in the array that was sent.
- It is **absent** when the fault is the array itself: not an array, or more than 50 rules.
- It applies to the routes that take rules:
  - `PUT /api/me/calendars/{id}/filter`;
  - `POST`/`PUT /api/me/presets[/{id}]`;
  - `POST`/`PUT /api/admin/presets[/{id}]`.

**Backend tests** (`FilterRulesTests`, `PresetsTests`):
- the index for a fault in the first, a middle and the last rule;
- no `rule` for array-level faults;
- the member appears on each of the routes above.

## 4. Preset followers: `GET /api/me/presets` (FR-031, US3-6)

Each **personal** `Preset` in the list gains `followers`: the number of the signed-in user's calendars whose filter
is linked to that preset.

```json
{ "id": 3, "name": "Minor", "scope": "personal", "rules": [ … ], "followers": 2 }
```

- Global presets (and `GET /api/admin/presets`) do not get the field. A count across accounts is not a student's
  data.
- A calendar with no stored filter follows Default, so it is not a follower of any personal preset.

**Backend tests** (`PresetsTests`):
- the count follows apply, customise and remove;
- it counts only the user's own calendars;
- global presets have no `followers`.

## Reasons the frontend must translate

The `Reason` union in `frontend/src/lib/codes.ts` holds:
- every reason in 001 to 004 `contracts/api.md` that a route the frontend calls can return;
- plus `invalid_order`, `invalid_cursor`, `invalid_home_page` and `invalid_settings` from this file.

`svelte-check` fails when a dictionary misses one (SC-009, research R10).
