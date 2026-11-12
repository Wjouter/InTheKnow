# Contract: HTTP API addition

Extends [001](../../001-ical-change-detection/contracts/api.md), [002](../../002-subscriber-change-filters/contracts/api.md)
and [005](../../005-sveltekit-frontend/contracts/api.md). Their conventions apply unchanged:

- camelCase JSON and ISO 8601 UTC;
- Problem Details with a `reason`;
- a signed-in session on every `/api` route.

The addition is one read-only route. Every existing route, parameter and response stays as it is. `GET
/api/me/changes` already returns `detectedAt`, which the feed now shows (FR-001), so it needs no change.

## `GET /api/me/changes/{id}/history`

Returns the history of the lesson that change `id` belongs to (research R2).

**Path**: `id` is a change id (`long`). A non-numeric id does not match the route and gives the framework's plain
404.

**Query**: none.

### 200

```json
{
  "changeId": 4711,
  "calendarId": 12,
  "calendarName": "Class 3B",
  "current": {
    "state": "present",
    "event": {
      "key": "…",
      "name": "Optische sensoren en actuatoren",
      "start": "2027-01-06T08:00:00Z",
      "end": "2027-01-06T09:30:00Z",
      "status": "confirmed",
      "fields": { "location": "B2.14", "rooms": ["B2.14"], "teachers": ["ABC"], "groups": ["3B"], "type": "lesson",
                  "description": "…", "details": { "Lecturer": "…" } }
    },
    "removedAt": null
  },
  "historyStartsAt": "2026-03-02T07:15:00Z",
  "retentionDays": 365,
  "items": [
    { "id": 4711, "calendarId": 12, "eventKey": "…", "kind": "changed", "field": "time",
      "oldValue": { "start": "…", "end": "…" }, "newValue": { "start": "…", "end": "…" },
      "eventName": "Optische sensoren en actuatoren", "eventStartsAt": "…", "detectedAt": "2026-11-11T07:19:03Z",
      "outcome": "normal" }
  ]
}
```

| Field | Meaning |
|---|---|
| `changeId` | the requested id, echoed back so the view can mark the entry it was opened from (FR-006) |
| `calendarId` | already exposed by the feed. It is not a new identifier (FR-005d). |
| `calendarName` | `coalesce(label, feed_name, host)` for this student, the same name `/api/me/calendars` gives (FR-007) |
| `current` | the lesson's current or last known details. See [data-model.md § Lesson](../data-model.md#lesson-event) and research R5. |
| `historyStartsAt` | `max(calendar.baseline_at, now − ChangeRetention)`, or `null` when `baseline_at` is null (research R4) |
| `retentionDays` | `ChangeRetention` in whole days (research R11) |
| `items` | **every** kept row of the lesson, newest first (`id DESC`), in the same item shape as `GET /api/me/changes`, hidden rows included, each with its `outcome` under the student's current filter |

`items` is never empty: the requested change is always one of its rows. The route has no paging, `limit` or
`includeHidden`. The frontend groups, filters and caps the list (research R2, R6, R7, R10).

### Errors

| Status | `reason` | When |
|---|---|---|
| `401` | (as every `/api` route) | no session |
| `404` | `not_found` | there is no change with this id (never existed, expired, or deleted with its timetable) **or** the signed-in student does not follow its timetable |

The two 404 cases come from the same `Problem(404, "not_found")` call, so their status, headers and body are
identical (FR-016, SC-005). The response never says which case applied.

### Behaviour

1. `SELECT calendar_id, event_key FROM detected_change WHERE id = $1`. No row gives a 404.
2. `presets.LoadFiltersAsync(user.AccountId(), ct, calendarId)`. No entry for the calendar gives a 404. A student
   with several links to one timetable is one row of the `subscription` view, so they get each row once.
3. Rows: `SELECT {ChangeView.Columns} FROM detected_change c WHERE c.calendar_id = $1 AND c.event_key = $2 ORDER BY
   c.id DESC`, using the index from migration 006. Each row's `outcome` comes from `FilterEngine`.
4. `current` per research R5. `calendarName`, `baseline_at` and the retention period are read in one query on
   `calendar` joined to `subscription_setting`.

Read-only: no table is written and no security event is logged.

### Backend tests (`HistoryTests`)

- 404 parity: an unknown id, a deleted change, and a change in a timetable the caller does not follow return
  identical status and body. After the caller removes their last link, the same change gives 404.
- Order and completeness: three checks on one lesson return all rows, newest first. Rows of another lesson in the
  same calendar are not included.
- Outcomes: a `hide` rule marks the matching row `hidden` and keeps it in `items`. A `lowPriority` rule gives
  `lowPriority`.
- Two links to one timetable return no duplicate rows.
- `current`:
  - `present`: from the snapshot.
  - `removed`: with `removedAt`, from the removed row.
  - `present` again after removal and reappearance.
  - `gone`: after a features-change re-baseline drops the lesson.
- `historyStartsAt`:
  - null for a calendar with no `baseline_at`;
  - the add-link baseline time for a new calendar;
  - the re-baseline time after a features change;
  - unchanged by a normal check;
  - `now − retention` when the baseline is older.
- `retentionDays` follows `ChangeRetention`.
