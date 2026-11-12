# Research: Event change history

The Technical Context had no open NEEDS CLARIFICATION items: stack, storage and test tools are fixed by the
constitution and earlier specs. The decisions below are the design choices the spec leaves open.

## R1. Found time in the feed (FR-001 to FR-003)

**Decision**: `formatter` gets `found(iso)`, which returns `{ text, exact }`:

| Age | `text` |
|---|---|
| under 60 s | `Intl.RelativeTimeFormat(locale, { numeric: 'always' })` in seconds ("30 seconds ago") |
| under 60 min | the same, in whole minutes (floor) |
| under 24 h | the same, in whole hours (floor), so 23 h 59 min reads "23 hours ago" |
| 24 h or more | `fmt.dateTime(iso)` ("Tue 10 Nov 09:19", with the year when it is not the current year) |

`exact` is always `fmt.fullDateTime(iso)`, a new formatter function that always includes the year (`fmt.dateTime` leaves out the current year). `ChangeGroup` prints
`<time datetime={detectedAt} title={exact}>{text}</time>`. The title gives the full value on hover and to
assistive tech. On touch screens a long-press on the link shows the browser's link menu, not the title. So the
history view shows the exact time visibly next to the relative time in every entry, one tap away. This is a known
limit of US1-4 on phones (FR-003 itself asks only that the value is on the same element).

`numeric: 'always'` is the setting that never produces "yesterday" or "today" (FR-002). The text is computed on
the server with the formatter's `now`. A tab left open shows "2 hours ago" until the next navigation or reload.
That is accepted: the feed has no live updates either, and a ticking client clock would add code for little gain.

The cut-off between relative and exact uses `now - detectedAt`, not calendar days.

**Year**: under 24 hours the year is never needed. From 24 hours on, `fmt.dateTime` follows 005 research R12 and
drops the year when it is the current year. Retention is one year, so a date from last year always shows its
year. The spec's "Tue 10 Nov 2026 09:19" is an example of the form, not of the year rule. Consistency with every
other date in the app decides it.

**Alternatives**: a client-side ticking component was rejected (Principle I). "just now" for very recent changes
was rejected because it is one more text for no gain over "30 seconds ago". `numeric: 'auto'` was rejected
because it produces "yesterday".

## R2. One history route that returns every row (FR-009, FR-009a, FR-013 to FR-016)

**Decision**: `GET /api/me/changes/{id:long}/history` (contract in [contracts/api.md](./contracts/api.md)).

1. Read `calendar_id, event_key` of change `id`.
2. `presets.LoadFiltersAsync(user, ct, calendarId)`. No entry means the student does not follow the timetable.
3. When the change is missing, or the student does not follow the timetable, return `Problem(404, "not_found")`.
   It is the same call in both cases, so the response body is byte-identical (SC-005).
4. Read every `detected_change` row with that `(calendar_id, event_key)`, newest first, using `ChangeView.Columns`
   and `ChangeView.Read`. Each row gets `outcome = FilterEngine.Name(FilterEngine.Outcome(...))`, exactly as in
   `/api/me/changes`.
5. Read the lesson's current details (R5), the calendar's display name, `baseline_at` (R4) and the retention period
   (R11).

The route has no `limit` and no `includeHidden`. The card cap (FR-009a) counts entries after grouping and after the
toggle. A row-level cap or filter on the server could not give that count, nor the FR-014b states ("are all
changes hidden?"). Rows per lesson are bounded by how often one lesson changes within a year, which is at most a
few hundred, so returning them all is cheap (R3). The SvelteKit side then groups, filters and caps the list (R6,
R7, R10), and the toggle needs no second request.

The route stays read-only. `user.AccountId()` is the only identity input. The id is bound as `long`, so a
non-numeric id is a routing 404 and never reaches the database.

**Alternatives**:

- Keying the route by `(calendarId, eventKey)` was rejected. It puts an up-to-1024-character key in the URL
  (FR-005d) and needs a second id format.
- Server-side grouping and paging was rejected: it duplicates logic `changes.ts` already has, and spec FR-009a
  says the full page shows everything without paging anyway.

## R3. Index for the history read (SC-003)

**Decision**: `CREATE INDEX ON detected_change (calendar_id, event_key, id);` in migration
`006_event_history.sql`.

The existing `(calendar_id, id)` index would make a history read scan every kept change of the calendar. With the
new index it is a range scan over one lesson's rows. Migrations run inside a transaction (`Db.MigrateAsync`), so
`CONCURRENTLY` is not possible. A plain `CREATE INDEX` blocks writes to `detected_change` while it builds, once, at
migrate time, before the server starts. That is acceptable: the migrate command runs while the checker is stopped
(006 deploy flow).

The index costs some write throughput on the checker's COPY. Change volume is small next to the event snapshot, so
this is acceptable.

**Alternatives**: no index was rejected because a large timetable with a year of changes would make every card
open scan thousands of rows. An index on `(event_key)` alone was rejected because an event key is unique only
within its calendar.

## R4. When a lesson's recorded history starts (FR-011, edge case "already there")

**Decision**: add `calendar.baseline_at timestamptz NULL`, written at both places a baseline is taken:

- `Checker.CheckAsync` row 3 (`baseline_features` differs, snapshot replaced): `baseline_at = claim.StartedAt`,
  in the same `UPDATE calendar` statement;
- `Subscriptions` add-link for a new calendar (the `INSERT INTO calendar … baseline_features …` that precedes
  `CopySnapshotAsync`): `baseline_at = now`.

The history shows the start as `max(baseline_at, now − retention)`, because changes before the retention cut-off
are gone too. Existing calendars get `NULL`. For them the page says the lesson was already in the timetable before
its recorded history starts, without a date. The spec allows this ("when that is known").

The FR-011 message appears whenever the **oldest kept entry** is not an `added` entry. That covers four cases:

- the lesson was there at the baseline;
- its `added` row has expired;
- it entered the expansion horizon of a repeating series, where Differ records no `added` row (001 research R7);
- it was in the baseline, was removed and later reappeared. The `added` entry of the reappearance is not the
  oldest entry, so the message still shows and never presents the reappearance as the first appearance.

**Alternatives**:

- `calendar.created_at` was rejected. After a re-baseline (a features change, Checker row 3) it names a date before
  which the history is in fact empty, which is the false claim FR-011 forbids.
- Showing no date at all was rejected because it costs only one nullable column and two assignments.
- Storing a per-event "first seen" was rejected: it needs a snapshot column and a backfill, for the same message.

## R5. The lesson's current or last known details (FR-007, FR-008)

**Decision**: in this order:

1. **In the timetable now.** If there is an `event_snapshot` row `(calendar_id, event_key)`, use its name, start,
   end, status and `fields` as `current` with `state: "present"`.
2. **Removed.** Otherwise, if the newest kept row is `removed`, use its `old_value` (the full event object Differ
   stores) as the last known details, with `state: "removed"` and `removedAt = detected_at`.
3. **Gone without a removal.** Otherwise, use the newest row that holds an event object: `event_after`, or
   `new_value` of an `added` row, or `old_value` of a `removed` row. The state is `"gone"`, with no removal time.
   This happens when a re-baseline dropped the lesson, or the lesson left the horizon. The page then says it is no
   longer in the timetable, without a time.

Step 1 also covers "removed and later appeared again": the snapshot row is back.

The event objects are already in the form Differ writes (`EventObject`: `name`, `start`, `end`, `status`,
`fields`, plus any extras). The API passes them through as JSON (`current.event`).

The frontend shows them as a calendar event (FR-007, layout in [contracts/pages.md](./contracts/pages.md)):

- A date tile.
- An event block with name, time · location, and teachers · groups · type.
- Everything else (status, description, `details.*`, the timetable name) behind a collapsed "More details". Unknown
  keys and `details.*` labels print with their raw label there.

This keeps the block small above the change list. It follows design review on 2026-11-11: the full nine-row list was
too tall, especially in the card and on phones.

**Alternatives**: always taking the newest row's `event_after` was rejected. Only `changed` rows have it, and it
would show stale data while a snapshot row exists.

## R6. Entries and the before/after list (FR-009, FR-010)

**Decision**: one **entry** is all rows of the lesson with the same `detected_at`. The checker writes every change
of one check with the same `claim.StartedAt`, so this is the feed's grouping key (`changes.ts` `group`, 001
"Grouping") restricted to one event. Rows within an entry keep their id order.

An entry has:

- **kind**: `added` if it contains an added row, else `removed`, else `changed`. One check never has both for the
  same key.
- **found time**: `found(detected_at)` plus the exact time.
- **name**: `event_name` at that moment.
- **outcome**: the entry's mildest outcome. `normal` if any row is normal, else `lowPriority` if any row is low
  priority, else `hidden`. It is used only to decide whether a whole entry is hidden (FR-014b states, the hidden
  count and the cap). Within an entry each row keeps its own outcome and badge, as in the feed.
- **rows** for a `changed` entry: one line per changed detail, `{ label, before, after, note, wrap, outcome }`.

Building the rows:

- **`time`**: one stored row `{start, end}` becomes one **Time** line. When the date stays the same, before and
  after are only the hours (`13:30–15:00` → `09:00–10:30`), and `note` gives the date once ("Same day: Wed 6 Jan
  2027"). When the date moved, both values carry the date (`Wed 6 Jan 13:30–15:00` → `Thu 7 Jan 09:00–10:30`),
  with the year only when it is not the current year (`fmt.date`). An event spanning days uses `fmt.range`.
- **`rooms`, `teachers`, `groups`**: the values are joined with ", ". An empty value prints as the existing "none"
  text.
- **`status`, `type`**: the existing translated words.
- **`name`, `description`, `details.X`**: free text, `wrap: true`, so the value may break inside itself.
- **`location`**: shown as text, like rooms.
- **`details.X`**: label `X` as-is, the value as text.

`added` and `removed` entries have no list. They show the lesson's start and end at that time, from `new_value` or
`old_value`, using the feed's existing `addedAt` sentence and the `removed` sentence.

Each line is `label · before → after`, as a `<ul>`:

- The label column has a fixed width (about 84 px) and the values fill the rest.
- **Before** is a struck-through chip on light red, **after** is a bold chip on light green, and an arrow sits
  between them (`aria-hidden`). The red/green follows the issue 23 reference.
  Visually hidden "Before:" and "After:" labels go in front of each value. Together these tell the two apart by
  more than colour (FR-010).
- Values that are not `wrap` get `white-space: nowrap`. The value pair is a `flex-wrap` row, so on a narrow
  screen the after value drops to the next line whole instead of splitting a time (FR-010, FR-019).
- `wrap` values use `overflow-wrap: anywhere`.

Nothing scrolls sideways, so no scroll wrapper is needed.

**Alternatives**:

- The Field / Before / After table from the issue 23 screenshots was tried in the design and rejected. At phone
  width, the full dates wrapped mid-value and the three columns were hard to read.
- Stacking before and after under each other on phones was rejected because it doubles the height.
- Coloured diff chips were rejected because they lean more on colour.
- The feed sentences were rejected because they don't line up details for comparison.

## R7. Filter toggle state and the FR-014a/b rules

**Decision**: the URL query `all` has three states. When it is absent the default rule applies, `all=1` forces
"show all", and `all=0` forces filtered. The page is built from all rows with their outcomes. The pure function
`historyView(rows, { all, openedId, cap })` in `frontend/src/lib/history.ts` returns the entries to show and the
messages:

| Situation | Shown | Toggle | Message |
|---|---|---|---|
| Opened change not hidden, `all` absent or `0` | non-hidden rows (entries with none left out) | off | "N changes hidden by your filters" when N > 0 (FR-015) |
| Opened change not hidden, `all=1` | all, hidden ones badged | on | none |
| Opened change hidden, some entries not hidden, `all` absent or `1` | all, hidden badged | on | none |
| Opened change hidden, some entries not hidden, `all=0` | non-hidden rows (entries with none left out) | off | "The change you opened is hidden by your filters" (FR-014b) |
| Every entry hidden | all, hidden badged | none | "All changes of this lesson are hidden by your filters" (FR-014b) |

Filtering is per row, as in the feed (FR-013). With the toggle off, hidden rows are dropped, and an entry is
left out only when no rows remain, which means its entry outcome is `hidden`. A mixed entry stays, without its
hidden rows. "Hidden entry" in the table and in the counts uses the entry outcome from R6. FR-014b is evaluated when the view is rendered, with the
student's current filters, because the API computes outcomes per request.

The toggle is a compact "Show all" checkbox in the "Changes, newest first" heading row, with the table's message
as one muted line beside it. It is not a separate panel: design review on 2026-11-11 found the panel too large for
a secondary setting (FR-021).

The feed's link adds `all=1` when the feed has `showHidden=1` (FR-014a). Inside the view, the toggle writes
`all=1` or `all=0` with `replaceState`, so it adds no history step:

- In the card, it uses `$app/navigation`'s shallow `replaceState`, which keeps the card's data and only updates
  the URL and `page.state.all`.
- On the full page it does the same, so it makes no new request.

The feed's own `showHidden` is a different parameter on a different URL, so it is never touched (FR-014a,
scenario 9).

**Alternatives**: a two-state `all` (on or absent) was rejected. Then "toggle off after opening a hidden change"
cannot survive a reload, because absent would mean "show all" again under FR-014b. Storing the toggle in a cookie
was rejected because FR-014a wants it in the address.

## R8. Card over the feed with shallow routing (FR-004, FR-005 to FR-005c)

**Decision**: `ChangeGroup` renders as `<a href={group.href}>` around the notice. `Feed.svelte` handles `onclick`
on the feed, once for both `/` and `/changes`:

- **Let the browser handle it** (new tab or window, the full page opens) when any modifier key is held (Ctrl,
  Meta, Shift or Alt), the button is not the primary one, or `defaultPrevented` is set.
- **Otherwise** call `preventDefault()`, then `const r = await preloadData(href)`. If
  `r.type === 'loaded' && r.status === 200`, call `pushState(href, { history: r.data })`, as in the SvelteKit
  shallow-routing example. Otherwise fall back to `goto(href)`, so a 404, an API failure or an expired session
  shows the normal error or sign-in page instead of a card with error data.

`Feed` renders `<HistoryCard>` while `page.state.history` is set. **The card is open exactly when `page.state.history`
is set**, so there is no separate `open` flag that can disagree with the history stack:

- **Close.** The close button, Esc and a backdrop click all call `history.back()`. Esc goes through the dialog's
  `cancel` event with `preventDefault()`, then `history.back()`. Back then clears `page.state.history`, and an
  `$effect` closes the `<dialog>`.
- **No double back.** The effect closes the dialog with `el.close()`, which fires `close`. The `onclose` handler
  only calls `history.back()` when `page.state.history` is still set. So a close that popstate caused never steps
  back twice.
- **Forward** restores `page.state.history`, so the card opens again (FR-005b).
- **Backdrop.** A click whose target is the `<dialog>` element itself. On a phone the card fills the screen, so
  there is no backdrop to click.
- **Focus.** `showModal()` keeps focus inside the card. On close, focus returns to the link that opened it: `Feed`
  remembers the element and calls `.focus()` in the close effect (FR-005a).
- **Scroll.** `pushState` does not navigate, so the feed keeps its scroll position (FR-005).

The page component `routes/changes/[id]/+page.svelte` and the card both render `<History data=… />`. The card frame
adds a heading row with **Open as page** (a plain `<a href>` to the same URL, which loads the full route) and the
close button (FR-005c). The existing `Dialog.svelte` is not reused: it binds `open` directly and is capped at 480 px.
`HistoryCard` is a separate, small native `<dialog>`. It is at most about 720 px wide on desktop, and at phone
width it has `width: 100%; height: 100%; max-height: none` (FR-005). It shares the existing tokens.

**Alternatives**:

- A client-side-only modal with no URL change was rejected, because FR-005b needs back and forward.
- A nested route with a layout modal was rejected, because a reload then shows the card, not the page (FR-005b
  says the page).
- Extending `Dialog.svelte` with modes was rejected: two different ownership models of `open` in one component.

## R9. Which change id a feed group links to (FR-006, FR-014b)

**Decision**: a feed group can hold several rows. `group()` sets `href = /changes/{id}` from the first row of the
group whose outcome is not hidden, or the first row when all are hidden. It adds `?all=1` when the feed shows
hidden changes.

Linking to a non-hidden row means FR-014b only applies when the whole group is hidden, which is what the student
saw as hidden. All rows of the group fall in one history entry, so the marked entry is the same either way. The
history view marks the entry that contains `openedId` and scrolls it into view on the full page (FR-006).

**Alternatives**: always the first row was rejected. A group with one hidden and one visible row would then force
"show all" for a change the student saw as visible.

## R10. Card cap of 20 and "further back" (FR-009a, edge case "opened from an older change")

**Decision**: `historyView` returns `entries` (after the toggle filter), `shown = entries.slice(0, cap)` and
`more = entries.length − shown.length`. The card passes `cap = 20`, the page passes no cap.

- When `more > 0`, the card ends its list with "N older changes not shown" and a link to the full page with the
  same query.
- When the opened entry is among the hidden-by-cap part, the card also says near the top that "the change you
  opened is further back", with the same link.
- The FR-012 retention note always closes the list, after "more", on both card and page.

**Alternatives**: "load more" in the card was rejected, because the spec chose a link to the full page.

## R11. Retention note (FR-012)

**Decision**: the API returns `retentionDays = (int)ChangeRetention.TotalDays`, since retention can be configured.
`format.retention(days)` gives "1 year" when `days` is a whole multiple of 365 (`days / 365` years), otherwise
`Intl.NumberFormat` days ("90 days"). The note is `history.retention(period)`: "Changes older than {period} are not
kept." / "Wijzigingen ouder dan {period} worden niet bewaard."

**Alternatives**: hard-coding "1 year" was rejected because it would lie on any installation that changes
`ChangeRetention`.

## R12. Testing approach (constitution: non-trivial logic has a check)

**Decision**:

- **Backend xUnit** (`HistoryTests`, real database):
  - 404 parity: an unknown id, an expired or deleted change, and a change in an unfollowed timetable all give the
    same status and the same body. A student who stops following loses access.
  - Rows are newest first, with outcomes under a hide rule and a low-priority rule.
  - A student with two links to the same timetable sees each row once.
  - The current-details sources follow R5: snapshot, removed, gone.
  - `baseline_at` is set by add-link and by a features-change re-baseline, and is left alone by a normal check.
  - `retentionDays` comes from the setting.
- **Frontend vitest**:
  - `found()` at 59 s, 59 min, 23 h 59 min, 24 h and an older date in another year, in en and nl, and the result
    never contains "yesterday" or "gisteren".
  - `group().href`: hidden rows, mixed rows, and `showHidden`.
  - `historyView` covers every row of the R7 table, a mixed entry losing only its hidden row with the toggle off,
    the cap and `more`, the opened entry beyond the cap, and the history-start rule (oldest entry not `added`).
  - Entry rows: a same-day time change gives hours only plus the date note, a moved date gives dates in both values,
    then sets, details labels, `wrap` for free text, and missing values.
  - `all` and id parsing in `+page.server.ts`, where anything that is not `^[1-9]\d{0,17}$` gives a 404.
  - The existing i18n key-parity test covers the new texts (SC-006).
- **Manual** (in quickstart): card open and close through back, Esc, the button and the backdrop; forward reopens
  it; focus return; Ctrl-click and middle-click; reload; phone width.

**Alternatives**: adding Playwright for the interaction checks was rejected under Principle I. It is a new
dependency with a browser download and CI setup, for checks that a short manual pass covers. It can be reconsidered
if a later feature leans on more client-side behaviour.
