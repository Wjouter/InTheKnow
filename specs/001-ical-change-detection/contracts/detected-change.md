# Contract: Detected changes (for the future notification feature)

The later notification feature reads recorded changes from the database. This file fixes the shape it can rely on.
The table definitions are in [data-model.md](../data-model.md).

## Reading pending changes per subscriber

```sql
SELECT s.subscriber, c.*
FROM detected_change c
JOIN subscription s ON s.calendar_id = c.calendar_id
WHERE c.notified_at IS NULL
ORDER BY c.id;
```

- Every change is recorded once (FR-025). Marking it delivered means setting `notified_at`. Changes are never
  re-inserted on later checks.
- The changes of one event in one check share `(calendar_id, event_key, detected_at)`. This is the grouping
  required by the "multiple changes to one event" edge case.
- Recording filters nothing out (FR-021). Presentation and notification use each subscriber's filter outcome
  from feature 002: `FilterEngine.Outcome(rules, change, active)`, skipping `hidden` changes
  ([002 contracts/rules.md](../../002-subscriber-change-filters/contracts/rules.md#for-the-notification-feature-fr-009)).

## `kind` / `field` values

| `kind` | `field` | `old_value` / `new_value` (jsonb) | Applies when |
|---|---|---|---|
| `added` | NULL | NULL / *event object* | always |
| `removed` | NULL | *event object* / NULL | always |
| `changed` | `name` | string / string | always |
| `changed` | `time` | `{"start": iso, "end": iso}` / same | start or end changed. One change covers both. |
| `changed` | `status` | string or null / string or null | always. Includes the draft marker being added or removed (`tentative` ↔ `confirmed`). |
| `changed` | `location` | string or null / string or null | event details off |
| `changed` | `description` | string or null / string or null | event details off |
| `changed` | `type` | string or null / string or null | event details on. Values are `lesson`, `test`, `holiday`, `appointment`, or the raw value if unknown. |
| `changed` | `rooms` | string[] / string[] | event details on. Sorted sets. |
| `changed` | `teachers` | string[] / string[] | event details and teacher detection both on. Each element is `"Last, First"`. |
| `changed` | `groups` | string[] / string[] | event details on |
| `changed` | `details.<label>` | string or null / string or null | event details on. Covers unknown labelled parts, and `details.teachers` when teacher detection is off. |
| `changed` | `details.text` | string or null / string or null | event details on. Unlabelled free text in the description, with volatile notes removed. |

## Event before and after (`event_before` / `event_after`, feature 002)

For `kind = changed`, `event_before` and `event_after` hold the whole event object (below) as it was before and
after the check, with `fields.details` removed. They are `NULL` for `added` and `removed`, whose event is already
in `new_value` / `old_value`. Filters read them to evaluate event conditions as they were at detection
(002 research R2).

## Event object (used by `added` / `removed`)

```json
{
  "key": "Xedule!GI3TINRXGY2Q@schedule.inholland.nl",
  "name": "ROS2 basics",
  "start": "2026-09-01T06:30:00Z",
  "end": "2026-09-01T08:15:00Z",
  "status": "confirmed",
  "fields": { "type": "lesson", "rooms": ["AL00.A1.38"], "teachers": ["Last, First"], "groups": ["ALTI26VT4ES1"] }
}
```

`fields` has the same content as `event_snapshot.fields`.

## Stability

- Adding a new `field` value is a backwards-compatible change.
- Renaming or removing a value, or changing the shape of a value, requires updating this contract.
