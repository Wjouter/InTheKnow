# Data Model: Timetable Change Detection

**Date**: 2026-09-24 | **Spec**: [spec.md](./spec.md) | **Research**: [research.md](./research.md)

The data is stored in PostgreSQL. Every integrity rule below is a database constraint. The first migration is
`001_init.sql`.

## Spec entity → storage mapping

| Spec entity | Storage |
|---|---|
| Calendar | table `calendar` |
| Check Run (one per calendar, replaced each check) | outcome columns on `calendar`. The relationship is 1:1 and only the latest run is kept, so a separate table would add a join and nothing else. |
| Calendar Link | table `calendar_link` |
| Subscription | view `subscription`: the distinct `(calendar_id, subscriber)` pairs over `calendar_link` |
| Event Snapshot | table `event_snapshot` |
| Detected Change | table `detected_change` |
| MyTimetables Domain | operator configuration, not stored (see [contracts/configuration.md](./contracts/configuration.md)) |

## Tables

### `calendar`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `bigint` | PK, identity | |
| `identity_hash` | `bytea` | NOT NULL, UNIQUE, `CHECK (length(identity_hash) = 32)` | `sha256(identity key)` (research R12). This is the key with the secret token hashed. |
| `host` | `text` | NOT NULL, `CHECK (host = lower(host) AND length(host) BETWEEN 1 AND 253)` | The source host shared by all its links; identity rules never group links across hosts. Used to keep budget-blocked hosts out of the claim query (research R4). |
| `status` | `text` | NOT NULL, default `'ok'`, `CHECK (status IN ('ok','failing'))` | `failing` when every link is flagged (FR-008a) |
| `next_check_at` | `timestamptz` | NOT NULL | The scheduler's queue (R4). A claim moves it forward by the 5-minute lease; the end of the check sets it to check start + interval. Indexed. |
| `lease_id` | `uuid` | NULL | Set by a claim and cleared by the outcome write. Every write for a check is fenced on it: a check whose lease no longer matches writes nothing, so a timed-out or stale check can never commit (research R4, R14). |
| `baseline_features` | `text` | NULL | The canonical feature set the snapshot was built with. `NULL` means no baseline yet. A mismatch triggers a re-baseline (R13). |
| `horizon_end` | `timestamptz` | NULL | The recurrence horizon of the current snapshot (R7) |
| `snapshot_hash` | `bytea` | NULL, `CHECK (snapshot_hash IS NULL OR length(snapshot_hash) = 32)` | SHA-256 of the canonical snapshot. When a check produces the same hash under the same features, the snapshot is neither read nor written (R14, FR-019a). |
| `last_checked_at` | `timestamptz` | NULL | Check Run: time |
| `last_link_id` | `bigint` | NULL, FK → `calendar_link(id)` ON DELETE SET NULL | Check Run: the link used (an id, never the URL) |
| `last_ok` | `boolean` | NULL | Check Run: success or failure |
| `last_error` | `text` | NULL, `CHECK (last_ok IS DISTINCT FROM false OR last_error IS NOT NULL)` | A failure reason code: `network`, `timeout`, `http_<code>`, `too_large`, `unparseable`, `empty_feed`, `too_many_occurrences`, `blocked_address`. Never an exception message, because those can contain the URL. |
| `last_event_count` | `integer` | NULL, `CHECK (last_event_count >= 0)` | Check Run |
| `last_change_count` | `integer` | NULL, `CHECK (last_change_count >= 0)` | Check Run |
| `consecutive_failures` | `integer` | NOT NULL, default 0, `CHECK (consecutive_failures >= 0)` | Check Run |
| `failing_since` | `timestamptz` | NULL, `CHECK ((consecutive_failures = 0) = (failing_since IS NULL))` | Check Run |
| `created_at` | `timestamptz` | NOT NULL, default `now()` | |

### `calendar_link`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `bigint` | PK, identity | |
| `calendar_id` | `bigint` | NOT NULL, FK → `calendar(id)` ON DELETE CASCADE | |
| `subscriber` | `text` | NOT NULL, `CHECK (subscriber ~ '^[A-Za-z0-9._@-]{1,100}$')` | An operator-chosen subscriber id, used until the accounts feature exists |
| `url` | `text` | NOT NULL, `CHECK (url ~ '^https?://')`, `CHECK (length(url) <= 2048)` | The link as `Uri.AbsoluteUri`, which lower-cases the scheme so the CHECK holds for `HTTPS://…` input. **Secret** (FR-028). |
| `flagged_at` | `timestamptz` | NULL | `NULL` means valid. Set when the link returned an empty feed under empty-feed protection. Cleared when the link returns events (FR-008a). |
| `last_used_at` | `timestamptz` | NULL | Used to rotate through flagged links while the calendar is failing |
| `created_at` | `timestamptz` | NOT NULL, default `now()` | |
| | | UNIQUE `(calendar_id, subscriber, url)` | The same subscriber adding the same link again does nothing |

Index: `(subscriber, calendar_id)`, which serves every `/api/subscribers/{subscriber}/…` query.

**Fetch-link selection** (FR-008):
- **Calendar `ok`**: the link with the lowest `id` among those with `flagged_at IS NULL`. The lowest id is the
  oldest link, so the choice is deterministic and the feed language stays stable.
- **Calendar `failing`**: the flagged link with the oldest `last_used_at`, so every link gets retried in turn.

### `event_snapshot`

This table holds each calendar's latest successful state (FR-019). Only the affected rows are written on each
check, and a full replace happens only on a baseline (R14).

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `calendar_id` | `bigint` | NOT NULL, FK → `calendar(id)` ON DELETE CASCADE | |
| `event_key` | `text` | NOT NULL, `CHECK (length(event_key) BETWEEN 1 AND 1024)` | Stable identity (R6) |
| | | PK `(calendar_id, event_key)` | |
| `name` | `text` | NOT NULL | Draft marker removed when detection is on |
| `starts_at` | `timestamptz` | NOT NULL | UTC instant (R8) |
| `ends_at` | `timestamptz` | NOT NULL, `CHECK (ends_at >= starts_at)` | Effective end |
| `status` | `text` | NULL | Lower-cased `STATUS` (`confirmed`/`tentative`/`cancelled`/…). `tentative` when a draft marker is found. |
| `occurrence_start` | `timestamptz` | NULL | The original start. Set only for occurrences of recurring series (R7). |
| `fields` | `jsonb` | NOT NULL, default `'{}'`, `CHECK (jsonb_typeof(fields) = 'object')` | The other compared fields; they depend on the features (see below) |

`fields` contents, which follow FR-016a:
- **Event details off**: `{"location": string?, "description": string?}`. The description has volatile notes
  removed on configured domains.
- **Event details on**: `{"type": string?, "rooms": [..]?, "teachers": [..]?, "groups": [..]?, "details":
  {label: value}?}`. Lists are sorted and de-duplicated. `teachers` is present only when teacher detection is on;
  otherwise the teacher value lives in `details.teachers`. Unlabelled free text lives in `details.text` (research R9).
- Keys with empty values are omitted, so "absent" and "empty" compare as equal.

### `detected_change`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `bigint` | PK, identity | Also the processing order for the future notification feature |
| `calendar_id` | `bigint` | NOT NULL, FK → `calendar(id)` ON DELETE CASCADE | |
| `event_key` | `text` | NOT NULL | |
| `kind` | `text` | NOT NULL, `CHECK (kind IN ('added','removed','changed'))` | |
| `field` | `text` | NULL, `CHECK ((kind = 'changed') = (field IS NOT NULL))` | The field name. The list of values is in [contracts/detected-change.md](./contracts/detected-change.md). |
| `old_value` | `jsonb` | NULL | `NULL` for `added` |
| `new_value` | `jsonb` | NULL, `CHECK (kind <> 'changed' OR old_value IS NOT NULL OR new_value IS NOT NULL)` | `NULL` for `removed` |
| `event_name` | `text` | NOT NULL | Context: the new name, or the old one for `removed` |
| `event_starts_at` | `timestamptz` | NOT NULL | Context: the new start, or the old one for `removed` |
| `detected_at` | `timestamptz` | NOT NULL | The same value for every change of one check. Together with `event_key` it groups the changes of one event. |
| `notified_at` | `timestamptz` | NULL | The status for the later notification feature. `NULL` means pending. |

Indexes: `(detected_at)` for the retention delete (FR-025a), and `(calendar_id, id)`.

### `schema_migration`

`version integer PK`, `applied_at timestamptz NOT NULL default now()`.

## View

```sql
CREATE VIEW subscription AS
  SELECT DISTINCT calendar_id, subscriber FROM calendar_link;
```

This view records which subscribers belong to which calendar (FR-009). It is read-only and derived, so it can
never get out of sync with the links.

## State transitions

### Calendar status

```
                  all links flagged
        ok  ──────────────────────────▶  failing
         ▲                                  │
         └──── a fetched link returns ──────┘
                 events (un-flag it)
```

- `ok`: checked every `CheckInterval` through the oldest unflagged link.
- `failing`: checked every `FailingCheckInterval` through the flagged link that has waited longest.

### Link

`valid (flagged_at NULL)` → `flagged` when empty-feed protection is on for the domain and the feed returns 0
events while the calendar has events in its snapshot. A flagged link becomes valid again when a fetch through it
returns ≥ 1 event.

### One check (see research R14)

The rows are evaluated **in this order**. The first matching row wins.

| # | Outcome | Snapshot | Changes | Outcome columns |
|---|---|---|---|---|
| 1 | Fetch/parse error, a bound was exceeded, or the 2-minute check limit passed (`timeout`) | kept | none | `last_ok=false`, reason, `consecutive_failures+1` |
| 2 | Empty feed under protection, and the snapshot has events | kept | none | As row 1 with `empty_feed`, and the link is flagged. If another unflagged link exists, it is used next time. Otherwise status becomes `failing`. |
| 3 | Success, `baseline_features` is NULL or differs from the current features | replaced | **none** (a baseline) | `last_ok=true`, `consecutive_failures=0`. `baseline_features` and `horizon_end` are set. |
| 4 | Success, same features, `snapshot_hash` unchanged | untouched | none | `last_ok=true`, counts. This is the common case and costs one calendar `UPDATE` and one link `UPDATE`. |
| 5 | Success, same features, hash changed | only affected rows written | the diff (FR-021, and the horizon rule from R7) | `last_ok=true`, counts, new `snapshot_hash` |

Rows 3 to 5 (every success) also set `status='ok'`, `consecutive_failures=0` and `failing_since=NULL`, set
`horizon_end` to the current horizon, and set the fetch link's `flagged_at=NULL` and `last_used_at`. A recovered
link usually returns an unchanged feed (row 4), and it must still end the `failing` state (research R14).

Row 2 comes before row 3 on purpose. A feature switch combined with a revoked link must not store an empty
baseline over a good snapshot.

Feed data is normalised before any write (research R8a), so rows 3 to 5 cannot fail on a constraint.

## Lifecycle and deletion

- **Remove subscription** (`DELETE /api/subscribers/{s}/calendars/{id}`): the subscriber's links to that calendar
  are deleted. In the same transaction, the calendar is deleted if no links remain, and the cascade removes its
  snapshots and changes (FR-008).
- **Retention**: every 10 minutes the checker runs `DELETE FROM detected_change WHERE detected_at < @cutoff`, where
  `@cutoff = TimeProvider now - ChangeRetention` (FR-025a, research R4 clock).
- **Configuration regroup at startup**: see research R13.

## Personal and sensitive data (Principle II)

| Field | Why it is needed | Protection |
|---|---|---|
| `calendar_link.url` | Required to fetch the calendar (FR-008). It contains the MyTimetables token `h` and the user id `eu`. | Never logged or returned (FR-032). Logs and API responses use `calendar_link.id`. It reaches the API only in a POST body, which is never logged. Deleted when the subscription is removed. Calendar identity stores only a hash. |
| `calendar_link.subscriber` | Links a calendar to the person who gets its changes (FR-009) | An opaque id chosen by the client. No name or email is stored in this feature; the login feature will map it to an account. |
| Teacher names in `event_snapshot.fields` / `detected_change` values | A teacher change triggers a notification (FR-029) | Deleted with the calendar or after the retention period |
| Group codes, rooms, event names | Timetable content that needs change detection | Same as above |

Nothing else is stored. Raw feeds, HTTP headers and validation responses are never persisted.

## Validation rules (application side, on top of the constraints above)

- Link: the scheme is `http` or `https`, the host resolves only to public addresses (research R5), the URL is at
  most 2048 characters, and validation passes (research R11).
- Subscriber: matches the same pattern as the column CHECK. It is checked before any fetch so the error is clear.
- Configuration: intervals are clamped and warned about, other values fail at startup (see
  [contracts/configuration.md](./contracts/configuration.md)).
