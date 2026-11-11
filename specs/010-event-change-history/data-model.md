# Data model: Event change history

## Schema change: migration `006_event_history.sql`

```sql
-- 010: a lesson's history is one index range scan (research R3).
CREATE INDEX ON detected_change (calendar_id, event_key, id);

-- 010: when the current snapshot's baseline was taken, so history can say where it starts (research R4).
-- NULL for calendars baselined before this migration: the start is unknown.
ALTER TABLE calendar ADD COLUMN baseline_at timestamptz NULL;
```

Writes to `baseline_at`:

| Where | When | Value |
|---|---|---|
| `Subscriptions` add-link, `INSERT INTO calendar … baseline_features …` | a new calendar's first snapshot | the insert's `now` |
| `Checker.CheckAsync` success `UPDATE calendar …` | only on row 3 (`fence.BaselineFeatures != canonical`) | `claim.StartedAt` |

No other path writes the snapshot wholesale (checked: `baseline_features` and `DELETE FROM event_snapshot` occur only
there and in Differ's per-key deletes). Regroup moves links, not snapshots. A calendar regroup creates has no
`baseline_features`, so its first check is a row 3 baseline and sets `baseline_at`.

**Personal data**: none added. `baseline_at` describes a calendar check, not a person (FR-017).

## Existing data read (unchanged)

| Table | Used for |
|---|---|
| `detected_change` | every kept row of `(calendar_id, event_key)`, read through `ChangeView` |
| `event_snapshot` | the lesson's current details when it is in the timetable |
| `calendar` | `baseline_at`; the name is resolved as in `/api/me/calendars` (`coalesce(label, feed_name, host)`) |
| `subscription` (view) + `subscription_filter` + `preset` | access check and outcomes, through `Presets.LoadFiltersAsync(user, ct, calendarId)` |

Rows expire by `detected_at < now − ChangeRetention` (existing `RunRetentionAsync`). Nothing in this feature
extends that period.

## Entities

### Lesson (event)

The key is `(calendar_id, event_key)` (001). It is never put in a URL. The view reaches it through any of its
change ids.

`current` in the API, built per research R5:

| Field | Type | Notes |
|---|---|---|
| `state` | `"present" \| "removed" \| "gone"` | `present`: an `event_snapshot` row exists. `removed`: the newest row is `removed`. `gone`: neither. |
| `event` | object | the event object form from `Differ.EventObject`: `name`, `start`, `end`, `status`, `fields` (`location`, `rooms`, `teachers`, `groups`, `type`, `description`, `details`, …). From the `event_after` source, `fields.details` is missing. |
| `removedAt` | ISO time \| null | only for `removed` |

### Detected change (row)

The existing table, unchanged. The API returns the same per-row shape as `GET /api/me/changes` items, including
`outcome`.

### History entry (frontend, derived)

All rows with the same `detectedAt` (one check), in id order (research R6).

| Field | Notes |
|---|---|
| `key` | `detectedAt` + first row id |
| `ids` | the row ids. The entry is "opened" when it contains `openedId`. |
| `kind` | `added` / `removed` / `changed` |
| `found` | `{ text, exact, iso }` from `fmt.found`; the history entry shows `text` and `exact` visibly |
| `name` | `eventName` of the first row |
| `outcome` | the mildest row outcome: normal, then lowPriority, then hidden. `hidden` means every row is hidden. |
| `summary` | for added: `addedAt(range)`; for removed: `removed` |
| `rows` | for changed: `{ label, before, after, note, wrap, outcome }[]`. One **Time** line: hours only plus a date `note` when the date stays, dates in both values when it moved. Sets are joined, `details.X` keeps label X, and free text has `wrap`. |

### History view (frontend, derived, `historyView`)

Inputs: entries, `all: boolean | null` from the URL, `openedId`, `cap: number | null`.

| Output | Meaning |
|---|---|
| `showAll` | the effective toggle state (research R7 table) |
| `toggle` | `false` when every entry is hidden (no toggle offered) |
| `shown` | entries after the toggle filter and the cap. With the toggle off, hidden rows are dropped and an entry with no rows left is left out. |
| `more` | how many entries the cap removed (card only) |
| `hiddenCount` | rows left out by the filter (FR-015) |
| `openedHidden` | the opened entry is hidden and currently left out (FR-014b message) |
| `allHidden` | every entry is hidden (FR-014b message) |
| `openedBeyondCap` | the opened entry is cut by the card cap (edge case) |
| `startsBeforeHistory` | the oldest kept entry is not `added` (FR-011 message, research R4) |

### Subscriber filters

These are unchanged. The outcome per row is computed by `FilterEngine.Outcome` with the student's current filter
for that calendar, at request time.

## State transitions

None new. A lesson's `current.state` follows from the existing snapshot and change rows:

```text
present ──(removed by a check)──▶ removed ──(appears again)──▶ present
present ──(re-baseline drops it, or it leaves the horizon)──▶ gone
```
