# Research: Subscriber Change Filters

**Spec**: [spec.md](./spec.md) | **Builds on**: [001 research](../001-ical-change-detection/research.md)

No NEEDS CLARIFICATION items remain. No new dependency is needed, so there are no library evaluations. Each
decision below settles a place where the spec leaves the mechanism open.

## R1. Where a subscription's filter lives

**Decision**: a side table `subscription_filter(calendar_id, subscriber, preset_id, rules)`, primary key
`(calendar_id, subscriber)`. A row is either **linked** (`preset_id` set, `rules` NULL) or **customised**
(`rules` set, `preset_id` NULL). **No row means linked to the Default preset.**

**Rationale**:
- `subscription` is a view over `calendar_link` (001 data model), so it cannot carry columns or be a foreign
  key target. The side table leaves the view alone.
- "No row = Default" satisfies FR-011 for new subscriptions and for subscriptions that exist when the feature
  ships, with no backfill and no insert in `Subscriptions.AddLinkAsync`.
- The `calendar_link_drop_filter` trigger deletes the row when the last link of the (calendar, subscriber) goes,
  so re-subscribing starts on Default again (edge case "Subscriber removes a subscription"). Deleting the
  calendar cascades to its rows.

**Alternatives considered**:
- *Materialise a `subscription` table*: rewrites 001's link model and duplicates what the view derives.
- *A row per subscription, created on add*: needs a backfill migration and a second write in `AddLinkAsync`, for
  no behaviour a missing row does not already give.

## R2. What each change must record (the 001 extension)

**Decision**: migration `002` adds `detected_change.event_before jsonb NULL` and `event_after jsonb NULL`. For
kind `changed`, `Differ` fills them from `EventObject(old)` and `EventObject(now)` with `fields.details`
removed (no condition reads it). For `added` and `removed`, the existing `new_value` / `old_value` already hold
the event object, so both columns stay NULL and the reader takes the missing side from there.

**Rationale**: "event had ended" and the before-or-after conditions need the event's end time, name, type,
groups, rooms and teachers as they were at detection (spec Assumptions). `event_name` and `event_starts_at`
are not enough, and joining `event_snapshot` at read time would show the *current* state, not the state at
detection. Storing the objects once per change row costs about 1 KB per field change, inside the existing
retention window.

No changes were recorded before this migration (spec Assumptions), so every `changed` row has both columns.

**Alternatives considered**:
- *Store only `ends_at`*: covers one condition; type, groups, rooms and teachers would need a second column
  each. Two objects cover all of them and future conditions.
- *Compute an outcome at detection time*: rejected by FR-001 and FR-007 (filters edit later and apply to old
  changes).

## R3. Evaluate in C#, not SQL

**Decision**: one pure function, `FilterEngine.Outcome(rules, change, active) → hidden | lowPriority |
normal`. The changes endpoint loads the subscriber's filters once per request into a map
`calendar_id → (rules, active conditions)`, then scans `detected_change` in keyset batches of 500
(`calendar_id = ANY($1) AND id > $2 ORDER BY id`) until it has `take + 1` visible rows or runs out.

**Rationale**:
- FR-009: the notification feature must get the same outcome per change. One function is reused; a SQL
  version would duplicate the logic.
- The one-time filter load per request is what gives "each read uses one consistent version of each filter".
- There is **no scan cap**. A cap could return an empty page while visible changes still exist, which the spec
  forbids (edge case "very large number of recorded changes"). Retention bounds the worst case, and SC-009
  (10,000 changes, most hidden, under 1 s) is measured once by hand, as 001 did for its performance goals. The
  code carries a `ponytail:` comment naming the ceiling: a full scan per page, upgrade to an indexed
  pre-filter or a materialised outcome only if measurement fails.

**Alternatives considered**:
- *Compile rules to SQL*: the before-or-after semantics, inactive-rule handling and the ordered show-only /
  hide / low-priority logic would each become generated SQL over jsonb, hard to test and easy to get wrong.
- *Store the outcome per (change, subscriber)*: FR-007 says an edit must apply to older changes, so every edit
  would rewrite rows; the spec also lists the outcome as "derived when read, not stored".

## R4. Rule shape

**Decision**: a closed object per rule:

```json
{ "outcome": "hide", "when": { "changeKind": ["location"], "activityType": ["holiday"] } }
```

Each key in `when` is one condition, so "all conditions must match" (FR-003) is the AND of the keys. Unknown
keys, unknown values and an empty `when` are rejected (FR-024). The full grammar is in
[contracts/rules.md](./contracts/rules.md).

**Rationale**: a fixed set of at most eight conditions (FR-005) does not need a generic `{field, op, value}`
tree. A closed object validates with a few lines, round-trips through the API unchanged, and is stored as
`jsonb`.

**Alternatives considered**: a list of `{type, values}` conditions permits duplicate conditions of one type
(two `changeKind` entries) whose meaning the spec does not define.

## R5. Field to change-kind mapping and inactive conditions

Recorded `field` values (001 `contracts/detected-change.md`) map to the rule's `changeKind` values:

| `changeKind` | Matches |
|---|---|
| `added`, `removed` | `kind` = `added` / `removed` |
| `name`, `time`, `status` | `kind` = `changed` and the same `field` |
| `location` | `field` is `location` or `rooms` |
| `description` | `field` is `description`, `details.text`, or any `details.<label>` (this includes `details.teachers`, which exists only when teacher detection is off) |
| `activityType` | `field` = `type` |
| `groups` | `field` = `groups` |
| `teachers` | `field` = `teachers` |

**Active conditions** (FR-006) are computed per calendar from `settings.FeaturesFor(calendar.host)`, not from
`baseline_features`. Settings load at startup, which gives "active or inactive at the next start" for free:
- MyTimetables domain with event details on: `activityType`, `group`, `room` conditions and the `changeKind`
  values `activityType` and `groups`.
- Also teacher detection on: `teacher` condition and the `changeKind` value `teachers`.
- Elsewhere none of these.

A rule is **inactive** on a calendar when any of its conditions is inactive there. A `changeKind` condition
that mixes active and inactive values stays active: the inactive values simply never occur on that calendar.
It is inactive only when every value is inactive. Inactive show-only rules do not count for the allow-list
(edge case "Allow-list with nothing active").

## R6. Before-or-after matching and "event had ended"

- Event conditions (`nameContains`, `activityType`, `group`, `room`, `teacher`) match when the event matched in
  the *before* state or the *after* state. Added: only after. Removed: only before. Values compare exactly,
  ignoring letter case (`OrdinalIgnoreCase`); `nameContains` is a case-insensitive substring.
- `eventEnded` is true when the end time of every state that exists is earlier than `detected_at`. So a time
  change that moves a past event into the future does not match.
- `statusFrom` / `statusTo` match only status changes; a missing status counts as `none`.

## R7. Presets: storage, limits and deletion

- One table `preset` for personal and global presets; `owner IS NULL` means global. Default is the global
  preset with `is_default = true`, seeded by the migration.
- Uniqueness of names per owner uses `UNIQUE NULLS NOT DISTINCT (owner, name)` (PostgreSQL 15+; the project
  runs PostgreSQL 18), because plain `UNIQUE` would not stop two global presets sharing a name.
- **Count caps** (50 personal per subscriber, 100 global) are checked in the application inside the insert
  transaction. Two simultaneous creates can overshoot by one; that is accepted because the caps protect
  against abuse, not against a precise budget, and an advisory lock per owner would add code for nothing.
- **Deleting a preset (FR-018)**: in one transaction, `UPDATE subscription_filter SET rules = <preset rules>,
  preset_id = NULL WHERE preset_id = $1`, then `DELETE FROM preset`. The foreign key is `ON DELETE RESTRICT`,
  so this order is mandatory and cannot be skipped by mistake. Default cannot be deleted (checked in the
  application; also nothing links to it by row, so no row would need converting).
- **Editing a preset** needs no fan-out: linked subscriptions read the preset row, so they follow (FR-017).

## R8. API routes and who may call them

- Every route already needs the operator key (001 R15). Global preset management is separated **by path**:
  `/api/presets` is operator-only management; `/api/subscribers/{s}/…` is the subscriber's own scope. When
  login ships, the subscriber routes become user-scoped and `/api/presets` stays operator-only, as 001 planned
  for its own operator routes.
- A subscriber route that names another subscriber's preset returns `404` (it does not exist for them,
  FR-016); a global preset on a subscriber's edit or delete route returns `403` (FR-022).
- Saving a filter as a preset and copying a global preset are `GET` + `POST` by the client, because the rules
  round-trip unchanged. No dedicated "save as" or "copy" endpoint (YAGNI).
- The picker values of FR-013 come from `SELECT DISTINCT jsonb_array_elements_text(fields->'groups')` (and
  `rooms`, `teachers`; `fields->>'type'`) over `event_snapshot` for that calendar. They are the current events
  and are returned only for the conditions that are active.

## R9. Data stored (Principle II)

Preset names and rule text are subscriber-typed content and are stored as given, escaped by the frontend. Rule
values can contain teacher names or group codes; they are the subscriber's own input about their own timetable
and are deleted with the preset or subscription filter. The extra `event_before` / `event_after` columns hold the
same event data that `event_snapshot` and `detected_change` already hold, under the same retention. No new
personal field is introduced.
