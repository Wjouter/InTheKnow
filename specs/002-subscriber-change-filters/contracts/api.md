# Contract: HTTP API additions

Extends [001 contracts/api.md](../../001-ical-change-detection/contracts/api.md). Everything there applies:
base path `/api`, camelCase JSON, ISO 8601 UTC, Problem Details with a `reason`. Since 004, a signed-in session on
every request, and the user routes under `/api/me/…` act for that account
([004 contracts/api.md](../../004-accounts-login/contracts/api.md) § Moved routes). Rule shape and validation reasons:
[rules.md](./rules.md).

`Preset` in responses:

```json
{ "id": 3, "name": "Minor", "scope": "personal", "rules": [ … ] }
```

`scope` is `personal` or `global`; the Default preset also has `"isDefault": true`.

## Subscription filter

A subscription is a calendar the subscriber has a link to. An unknown `{calendarId}` for that subscriber
returns `404`.

### `GET /api/me/calendars/{calendarId}/filter` (FR-013, FR-014)

```json
{
  "source": { "type": "preset", "presetId": 1, "name": "Default", "scope": "global" },
  "rules": [ … ],
  "inactiveRules": [1, 2],
  "activeConditions": ["changeKind", "eventEnded", "status", "nameContains", "activityType", "group", "room"],
  "values": { "activityType": ["lesson"], "group": ["ALTI26VT4ES1"], "room": ["AL00.A1.38"] }
}
```

- `source` is `{ "type": "preset", … }` when linked (no stored row means Default) or `{ "type": "custom" }`.
- `rules` is the effective rule list, in the format of [rules.md](./rules.md), so it can be sent back unchanged.
- `inactiveRules` holds the indexes of rules ignored on this calendar (FR-006).
- `activeConditions` lists the condition keys usable on this calendar. `status` stands for the `statusFrom` and
  `statusTo` rule keys, and `changeKind` values `activityType`/`groups` and `teachers` are usable when `activityType`
  and `teacher` are listed. `values` holds the distinct activity
  types, groups, rooms and teachers of the calendar's current events, for the conditions that are active
  (FR-013); inactive ones are omitted.

### `PUT /api/me/calendars/{calendarId}/filter` (US2, FR-014, FR-017)

Body: `{ "rules": [ … ] }`. Replaces the filter and makes the subscription customised. `200` returns the same
body as `GET`. `400` with a rules reason leaves the previous filter in place.

### `PUT /api/me/calendars/{calendarId}/filter/preset` (FR-017, SC-007)

Body: `{ "presetId": 3 }`. Links the subscription to the preset, which must be one of the subscriber's own
or a global preset (otherwise `404`). `200` returns the same body as `GET`. Applying a preset again re-links it.

## Personal presets

Since 005, personal presets in `GET /api/me/presets` carry `followers`, and rule refusals carry the index of the
rule at fault: see [005 contracts/api.md](../../005-sveltekit-frontend/contracts/api.md).

### `GET /api/me/presets`

Returns `{ "items": [Preset] }`: the subscriber's own presets, then the global presets (FR-015, FR-016).

### `POST /api/me/presets`

Body: `{ "name": "Minor", "rules": [ … ] }`. `201` returns the `Preset`. Saving a subscription's filter as a
preset, or copying a global preset, is a `GET` of its rules followed by this call.

| Status | `reason` |
|---|---|
| `400` | `invalid_name` (empty or over 100 characters), or a rules reason |
| `409` | `name_taken`, `preset_limit` (50 personal presets) |

### `PUT /api/me/presets/{id}`

Body: `{ "name"?: …, "rules"?: … }` (at least one, else `400` `invalid_rules`). Linked subscriptions follow the new rules (FR-017).
`200` returns the `Preset`. Errors: as `POST`, `403` `preset_is_global` for a global preset (FR-022), `404` for a
preset that does not exist or belongs to another subscriber (FR-016).

### `DELETE /api/me/presets/{id}`

`204`. Subscriptions linked to it become customised with its last rules (FR-018). `403`/`404` as `PUT`.

## Global presets (admins only, FR-021, FR-026; admin-managed since 004)

Subscribers reach global presets through the routes above (list and apply). Management is separate:

| Route | Meaning |
|---|---|
| `GET /api/admin/presets` | `{ "items": [Preset] }`, global presets only |
| `POST /api/admin/presets` | Body `{ "name", "rules" }`. `201`. `409` `name_taken`, `preset_limit` (100). |
| `PUT /api/admin/presets/{id}` | Body `{ "name"?, "rules"? }`. Also edits Default's rules and name (FR-012). `404` for a personal preset. |
| `DELETE /api/admin/presets/{id}` | `204`. `403` `preset_is_default` for Default (FR-012). Linked subscriptions become customised (FR-018). |

Since 004 these routes are under `/api/admin` and need an admin with a second factor; the path split keeps global
management out of the account's own routes.

## Changes: `GET /api/me/changes` (FR-008)

Since 005, `order=newest` with a `before` cursor pages newest first: see [005 contracts/api.md](../../005-sveltekit-frontend/contracts/api.md).

Adds one query parameter and one response field to the 001 route:

- `includeHidden=true`: also return hidden changes. Default `false`. Any other value returns `400` with reason `invalid_include_hidden`.
- Each item gains `"outcome": "normal" | "lowPriority" | "hidden"`. Without `includeHidden`, `hidden` never
  appears.

Paging is unchanged (`after`, `limit`, `next`). The server keeps reading until it has `limit` visible items or
runs out, so a page is never empty while later visible changes exist. `next` is the id of the last returned
item when more remain.

## Errors summary

| Status | `reason` |
|---|---|
| `400` | `invalid_include_hidden`, `invalid_name`, `invalid_rules`, `invalid_outcome`, `empty_conditions`, `invalid_condition` |
| `403` | `preset_is_global`, `preset_is_default` |
| `404` | no such subscription, or no such (visible) preset |
| `409` | `name_taken`, `preset_limit` |
