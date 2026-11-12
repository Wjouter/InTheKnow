---

description: "Task list for 010 event change history"
---

# Tasks: Event change history

**Input**: Design documents from `/specs/010-event-change-history/`

**Prerequisites**: plan.md, spec.md, research.md (R1–R12), data-model.md, contracts/api.md, contracts/pages.md, quickstart.md

**Tests**: included. The plan's Constitution Check ("non-trivial logic has an automated check") lists the backend
(`HistoryTests`) and frontend (vitest) cases. There is no browser test runner (research R12), so interaction checks are
the manual quickstart steps in Phase 6.

**Organization**: grouped by user story. US1 (found time) is frontend only and does not need the backend. US2 (card and
page) and US3 (filters) share one backend route and one `History.svelte`, so the route lives in Phase 2 and the shared
content in US2; US3 adds the toggle logic on top.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: US1, US2, US3 (setup, foundational and polish tasks have no label)
- Paths: `backend/InTheKnow/`, `backend/InTheKnow.Tests/`, `frontend/src/`

---

## Phase 1: Setup

None: no new project, package or dependency (plan: "No new package"). Shallow routing, `<dialog>` and
`Intl.RelativeTimeFormat` are platform and framework features.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: the migration and the read-only history route that US2 and US3 both read. US1 does not depend on this
phase and may start in parallel with it.

**CRITICAL**: no US2 or US3 task can start before this phase is complete.

- [ ] T001 Create `backend/InTheKnow/Migrations/006_event_history.sql` (the next number after `005_frontend.sql`) with
  exactly the two statements of data-model.md: `CREATE INDEX ON detected_change (calendar_id, event_key, id);` and
  `ALTER TABLE calendar ADD COLUMN baseline_at timestamptz NULL;`, each with the one-line "010:" comment from
  data-model.md. Migrations run in a transaction, so no `CONCURRENTLY` (research R3). `baseline_at` is nullable: "NULL
  for calendars baselined before this migration: the start is unknown".
- [ ] T002 In `backend/InTheKnow/Subscriptions.cs`, extend the add-link `INSERT INTO calendar (…, baseline_features, …)`
  (line ~59) to also write `baseline_at` = that insert's `now` (data-model.md "Writes to `baseline_at`", research R4).
  Depends on T001.
- [ ] T003 In `backend/InTheKnow/Checker.cs`, in the success `UPDATE calendar … baseline_features = $8 …` (line ~205),
  set `baseline_at = claim.StartedAt` **only** on the row 3 path (`fence.BaselineFeatures != canonical`, line ~189).
  A normal check MUST leave `baseline_at` unchanged. Depends on T001. Not parallel with T002 only if the same helper is
  shared; otherwise [P].
- [ ] T004 Create `backend/InTheKnow/History.cs` (research R2: a new file, not a block in `Api.cs`) with
  `GET /api/me/changes/{id:long}/history`, mapped in `backend/InTheKnow/Api.cs` next to `me.MapGet("/changes", …)`
  (line ~78). Behaviour, in order (contracts/api.md "Behaviour"):
  1. `SELECT calendar_id, event_key FROM detected_change WHERE id = $1`; no row → `Problem(404, "not_found")`.
  2. `presets.LoadFiltersAsync(user.AccountId(), ct, calendarId)`; no entry for the calendar → the **same**
     `Problem(404, "not_found")` call, so status, headers and body are identical (FR-016, SC-005).
  3. Rows: `SELECT {ChangeView.Columns} FROM detected_change c WHERE c.calendar_id = $1 AND c.event_key = $2 ORDER BY
     c.id DESC`, each item in the same shape as `GET /api/me/changes` items (`id, calendarId, eventKey, kind, field,
     oldValue, newValue, eventName, eventStartsAt, detectedAt, outcome`), `outcome` from
     `FilterEngine.Name(FilterEngine.Outcome(filter.Rules, c, active))`, hidden rows included. No `limit`, no
     `includeHidden`, no paging.
  4. One query on `calendar` joined to `subscription_setting` for `calendarName` =
     `coalesce(label, feed_name, host)` (same as `/api/me/calendars`) and `baseline_at`.
  5. `current` per research R5: `state: "present"` from the `event_snapshot` row `(calendar_id, event_key)` (name,
     start, end, status, `fields`); else `"removed"` when the newest row is `removed`, using its `old_value` and
     `removedAt = detected_at`; else `"gone"` from the newest row holding an event object (`event_after`, `new_value` of
     `added`, or `old_value` of `removed`), no `removedAt`.
  6. `historyStartsAt` = `max(baseline_at, now − O.ChangeRetention)`, or `null` when `baseline_at` is null (research
     R4). `retentionDays = (int)O.ChangeRetention.TotalDays` (research R11). `changeId` echoes the requested id.
  Read-only: no write, no `SecurityLog`. Depends on T001.
- [ ] T005 [P] Create `backend/InTheKnow.Tests/HistoryTests.cs` (real PostgreSQL via `TestDb`, patterns from
  `ApiTests.cs`) covering the "Backend tests" list of contracts/api.md:
  - 404 parity: an unknown id, a deleted change, and a change in a timetable the caller does not follow return
    identical status and body; after the caller removes their last link, the same change gives 404.
  - Order and completeness: three checks on one lesson return all rows newest first; rows of another lesson in the same
    calendar are absent.
  - Outcomes: a `hide` rule marks the matching row `hidden` and it stays in `items`; a `lowPriority` rule gives
    `lowPriority`.
  - Two links to one timetable return no duplicate rows.
  - `current`: `present` from the snapshot; `removed` with `removedAt`; `present` again after removal and
    reappearance; `gone` after a features-change re-baseline drops the lesson.
  - `historyStartsAt`: null with no `baseline_at`; the add-link baseline time for a new calendar; the re-baseline time
    after a features change (`CheckerTests` helpers); unchanged by a normal check; `now − retention` when the baseline
    is older.
  - `retentionDays` follows `ChangeRetention`.
  - Size: a lesson with 100 rows returns all 100, newest first (SC-003 correctness; the timing check is quickstart
    step 20).

**Checkpoint**: `dotnet test backend/InTheKnow.Tests --filter "FullyQualifiedName~HistoryTests|FullyQualifiedName~ApiTests|FullyQualifiedName~CheckerTests|FullyQualifiedName~SubscriptionsTests"` passes. The route serves everything the frontend needs.

---

## Phase 3: User Story 1 - See when a change was found (Priority: P1) 🎯 MVP

**Goal**: the corner time of each feed change says when InTheKnow found it (relative under 24 h, exact from 24 h), not
the lesson's start time.

**Independent Test**: make a change, let a check run, open the feed: the corner shows "N minutes ago" (or the exact
date and time after 24 h), with the full value on the element (US1-1 to US1-4).

### Tests for User Story 1

- [ ] T006 [P] [US1] In `frontend/src/lib/server/format.test.ts`, add cases for `fmt.found(iso)` and
  `fmt.fullDateTime(iso)` in en and nl: 59 s → seconds, 59 min → minutes, 23 h 59 min → "23 hours ago", exactly 24 h →
  `fmt.dateTime` form, a date in another year includes the year, and the text never contains "yesterday", "today",
  "gisteren" or "vandaag" (FR-002, research R1, R12). `fullDateTime` always includes the year.
- [ ] T007 [P] [US1] In `frontend/src/lib/server/changes.test.ts`, update the existing `group()` expectations: the
  group's `when` is now `found(detectedAt).text`, and the group exposes `found.exact` and `detectedAt` (iso) for the
  `<time>` element. The corner time no longer equals `fmt.dateTime(eventStartsAt)` (FR-001).

### Implementation for User Story 1

- [ ] T008 [US1] In `frontend/src/lib/server/format.ts`, add to `formatter` (using the existing `now`): `fullDateTime(iso)`
  (date with the year always, plus time) and `found(iso)` returning `{ text, exact }` per research R1: `now − iso`
  under 60 s → `Intl.RelativeTimeFormat(locale, { numeric: 'always' })` in seconds; under 60 min → whole minutes
  (floor); under 24 h → whole hours (floor); 24 h or more → `dateTime(iso)`. `exact` = `fullDateTime(iso)`. Never
  `numeric: 'auto'` (it yields "yesterday"). Depends on T006.
- [ ] T009 [US1] In `frontend/src/lib/server/changes.ts`, change `ChangeGroup.when: string` to carry the found time:
  replace `when` with `found: { text: string; exact: string; iso: string }` built from the group's first row
  `detectedAt` via `fmt.found` in `group()` (line ~129); drop the `fmt.dateTime(eventStartsAt)` corner value. The
  sentences from `describe()` stay unchanged (they keep the lesson's own time). Depends on T008, T007.
- [ ] T010 [US1] In `frontend/src/lib/components/ChangeGroup.svelte`, print
  `<time class="when" datetime={group.found.iso} title={group.found.exact}>{group.found.text}</time>` in the corner in
  place of the old `when` text (FR-001 to FR-003, contracts/pages.md "Corner time"). Depends on T009.
- [ ] T011 [US1] Run `cd frontend && npm test && npm run check`; fix any other test that read the old `when` (for
  example feed page or `fake.ts` fixtures under `frontend/src`). Depends on T010.

**Checkpoint**: US1 works and is demoable on its own: the feed shows found times.

---

## Phase 4: User Story 2 - Open the full history of one event (Priority: P1)

**Goal**: clicking a feed change opens a history card over the feed (full screen on a phone) with the lesson's current
details and every kept change, newest first, with before/after lines; Ctrl-click, reload and "open as page" give the
full page at `/changes/{id}`.

**Independent Test**: let one lesson change two or three times, click a change in the feed, and check the card shows
the current details and all its changes with times and before/after values; reload shows the page (US2-1 to US2-10).

### Tests for User Story 2

- [ ] T012 [P] [US2] Create `frontend/src/lib/server/history.test.ts` for the entry builder (research R6): rows with the
  same `detectedAt` become one entry in id order; `kind` is `added` if any added row, else `removed`, else `changed`;
  `outcome` is the mildest row outcome; a same-day time change gives one **Time** row with hours only and a `note`
  "Same day: {date}"; a moved date gives dates in both values (year only when not the current year); `rooms`,
  `teachers`, `groups` joined with ", " and an empty value prints the existing "none" text; `status`/`type` use the
  translated words; `name`, `description`, `details.X` are `wrap: true` and `details.X` keeps label `X`; added and
  removed entries show start–end from `new_value`/`old_value` with `addedAt`/`removed` summaries. (The history-start
  rule is tested once, in `historyView`, T014a.)
- [ ] T013 [P] [US2] Create `frontend/src/routes/changes/[id]/page.server.test.ts` (or alongside the load as the repo's
  pattern in `frontend/src/lib/server/actions.test.ts` suggests) for id and `all` parsing: `{id}` must match
  `^[1-9]\d{0,17}$` otherwise a 404; `all` accepts only `0` or `1`, anything else counts as absent (contracts/pages.md
  "Addresses"); an API 404 surfaces as the standard not-found error, identical for every cause (FR-016).
- [ ] T014 [P] [US2] In `frontend/src/lib/server/changes.test.ts`, add `group().href` cases (research R9): `/changes/{id}`
  of the first non-hidden row, the first row when all are hidden, never `?all=1` for `/`, and `?all=1` when the feed
  has `showHidden=1`.
- [ ] T014a [P] [US2] Create `frontend/src/lib/history.test.ts` for the US2 part of `historyView` (research R7, first
  table row): opened not hidden + `all` absent or `0` → hidden rows dropped, entries with no rows left are left out, a
  mixed entry loses only its hidden rows, `hiddenCount` counts the dropped rows, toggle off; the cap counts **after**
  that filter (`shown` ≤ cap of the filtered entries, `more` = filtered − shown, FR-009a); `openedBeyondCap`; and
  `startsBeforeHistory` (FR-011): true whenever the oldest kept entry is not `added`, including "was in the baseline,
  removed, then reappeared", judged over all kept entries, so a hidden oldest `added` entry with the toggle off still
  gives false.

### Implementation for User Story 2

- [ ] T015 [P] [US2] Add the `history.*` texts (and `changes.found*` if any text key is needed; units come from `Intl`)
  to `frontend/src/lib/i18n/en.ts` and `frontend/src/lib/i18n/nl.ts` (FR-018), listed in contracts/pages.md "New
  texts": title and back link; open as page; current ("Now in the timetable", "Last known details", "More details" /
  "Fewer details", removed with time, no longer in the timetable); detail labels (time, status, location, rooms,
  teachers, groups, type, description, name); kinds (new, removed, changed); hidden labels "Before:" / "After:" and the
  time note "Same day: {date}"; "Show all"; the messages (N hidden by your filters, the change you opened is hidden,
  all changes hidden, "You opened this change"); history start with and without a date; "N older changes not shown";
  "the change you opened is further back"; and the retention note `history.retention(period)` = "Changes older than
  {period} are not kept." / "Wijzigingen ouder dan {period} worden niet bewaard." The existing parity test in
  `frontend/src/lib/i18n/index.test.ts` enforces both languages (SC-006).
- [ ] T016 [US2] In `frontend/src/lib/server/format.ts`, add `retention(days)` (research R11): whole multiples of 365 →
  "{n} year(s)" via `Intl`, otherwise `Intl.NumberFormat` days ("90 days"); add its case to
  `frontend/src/lib/server/format.test.ts`.
- [ ] T017 [US2] In `frontend/src/lib/server/changes.ts`, add `href: string` to `ChangeGroup` and build it in `group()`:
  `/changes/{id}` from the first row of the group whose outcome is not `hidden` (the first row when all are hidden),
  plus `?all=1` when the feed URL has `showHidden=1` (research R9, contracts/pages.md "Feed changes"). Keep the
  `Change.id` field. `group()` needs the `showHidden` flag as a parameter, passed by `loadFeed` (line ~189) for both
  `/` and `/changes`. Depends on T014, T009.
- [ ] T018 [US2] Create `frontend/src/lib/server/history.ts`: types `HistoryData` (the API body) and `HistoryEntry`
  (data-model.md "History entry": `key`, `ids`, `kind`, `found {text, exact, iso}`, `name`, `outcome`, `summary`,
  `rows {label, before, after, note, wrap, outcome}[]`), and `loadHistory(event, id)` that calls
  `GET /api/me/changes/{id}/history` through `api`/`must` in `frontend/src/lib/server/api.ts`, groups rows per
  `detectedAt` (research R6), builds the rows using `describe`/`tone`/`icon` helpers and the translated words from
  `changes.ts` (export the small helpers it needs, e.g. `text`, `statusWord`, `typeWord`), formats the current details,
  calendar name, `historyStartsAt` and `retention(retentionDays)`, and returns everything the components print.
  Depends on T012, T016, T015.
- [ ] T019 [P] [US2] Create `frontend/src/lib/history.ts` (client-safe, outside `$lib/server`, research R7) with the pure
  `historyView(entries, { all, openedId, cap })` returning `showAll`, `toggle`, `shown`, `more`, `hiddenCount`,
  `openedHidden`, `allHidden`, `openedBeyondCap`, `startsBeforeHistory` per data-model.md "History view". For this
  story implement the `all` absent or `0` path for an opened change that is not hidden: drop hidden rows, leave out
  entries with no rows left and count `hiddenCount` (FR-013, FR-015); then cap what remains (`shown =
  filtered.slice(0, cap)`, `more = filtered.length − shown.length`, `openedBeyondCap`; FR-009a counts after the
  filter); and `startsBeforeHistory` over all kept entries. T029 adds `all=1`, the toggle state and the FR-014b rows.
  Export the entry types from here so `server/history.ts` imports them. Depends on T014a.
- [ ] T020 [US2] Create `frontend/src/routes/changes/[id]/+page.server.ts`: validate `id` against `^[1-9]\d{0,17}$`
  (else `error(404)`), parse `all` (`'1'`→true, `'0'`→false, otherwise null), call `loadHistory`, and return
  `{ history, openedId, all }`. A non-200 API result is rethrown as the standard error so `+error.svelte` renders the
  same "not found" text for every cause; an expired session redirects to sign-in with `next` set to this address like
  the other student pages (use `frontend/src/lib/next.ts`). Depends on T018, T013.
- [ ] T021 [US2] Create `frontend/src/lib/components/History.svelte`, the shared content (contracts/pages.md "Shared
  content"), taking `{ history, openedId, all, cap }`:
  1. Current details as a calendar event: ~52 px date tile (brand-coloured month strip, day number, short weekday,
     visually hidden full date), soft-tinted event block (name; start–end · location; teachers · groups · type; empty
     parts omitted), a "More details" disclosure with `aria-expanded`, ≥44 px target and turning chevron holding
     status, description, rooms when different from location, every `details` label/value and the timetable name.
     States: `present`, `removed` (grey strip, dashed block, struck-through name, "Removed" badge, third line
     "Removed {found time}"), `gone` ("No longer in the timetable" badge, no time) (FR-007, FR-008).
  2. Heading row "Changes, newest first" (the toggle itself comes in T030).
  3. Entries newest first on `--surface-subtle`: rounded 8 px card, 1 px `--border-default`, `--shadow-1`, 4 px leading
     bar by kind (`--status-info` changed, `--status-new` new, `--status-cancelled` removed); kind badge with relative
     and exact found time both visible; the name at that moment; added/removed sentence; changed entries as a `<ul>` of
     `label · before → after` rows with before a struck-through chip (`--red-100`/`--red-500`), after a bold chip
     (`--green-100`/`--green-700`), visually hidden "Before:"/"After:" labels, `aria-hidden` arrow, non-wrap values
     `white-space: nowrap` in a `flex-wrap` pair so the after value drops whole, `wrap` values
     `overflow-wrap: anywhere` (FR-009, FR-010, FR-021).
  4. Opened entry (FR-006): `aria-current="true"`, `--brand-soft` background, 2 px `--brand` border, 6 px bar and the
     label "You opened this change"; a removed opened entry keeps the removed look and only the label marks it.
  5. History-start note when `startsBeforeHistory`, with " ({date})" when `historyStartsAt` is known; never names the
     oldest entry as the first appearance (FR-011).
  6. Card only: "N older changes not shown" link to the page, and "The change you opened is further back" when
     `openedBeyondCap` (FR-009a).
  7. Retention note, always last (FR-012).
  All colours from existing tokens, no sideways scroll at 360 px (FR-019). All text via `dicts[lang]`.
  Depends on T019, T018, T015.
- [ ] T022 [US2] Create `frontend/src/routes/changes/[id]/+page.svelte`: `PageIntro` (lesson name with the timetable name
  under it), a "Back to changes" link to `/changes`, then `<History>` with no cap; after load, scroll the opened entry
  into view (FR-006). Depends on T021, T020.
- [ ] T023 [US2] Create `frontend/src/lib/components/HistoryCard.svelte`: a native modal `<dialog>` frame around
  `<History>` with an `aria-labelledby` heading (the lesson name), an "Open as page" `<a href>` to the card's own URL
  (full load, FR-005c) and a close button (`aria-label` = existing "Close" text). Wide: centred, max about 720 px wide,
  max 90 vh, scrolls inside. Below 600 px: `width: 100%; height: 100%; max-height: none`. Close wiring (research R8):
  close button, Esc (`cancel` with `preventDefault()`) and backdrop click (target is the `<dialog>` itself) all call
  `history.back()`; an `$effect` calls `el.close()` when `page.state.history` clears; `onclose` calls `history.back()`
  only while `page.state.history` is still set, so no double back. `showModal()` keeps focus inside (FR-005a). Do not
  reuse `Dialog.svelte`. Depends on T021.
- [ ] T024 [US2] In `frontend/src/lib/components/ChangeGroup.svelte`, make the notice `<a class="change …"
  href={group.href}>` so it is focusable and opens with Enter, heading and sentences inside it (FR-004,
  contracts/pages.md); keep existing visual styling and focus ring. Depends on T017, T010.
- [ ] T024a [US2] In `frontend/src/lib/history.ts`, export a pure `opensCard(e: { button: number; ctrlKey: boolean;
  metaKey: boolean; shiftKey: boolean; altKey: boolean; defaultPrevented: boolean }): boolean` that is true only for a
  primary-button click with no modifier key and not `defaultPrevented`. In `frontend/src/lib/history.test.ts`, add one
  case per condition: plain click → true; Ctrl, Meta, Shift, Alt, middle button, `defaultPrevented` → false (FR-005b,
  constitution "non-trivial logic has an automated check"). Depends on T019 (same files).
- [ ] T025 [US2] In `frontend/src/lib/components/Feed.svelte`, add the card host (research R8): an `onclick` on the feed
  that leaves the browser alone unless `opensCard(event)` (T024a); otherwise `preventDefault()`, remember the clicked link, `const r = await preloadData(href)` and,
  when `r.type === 'loaded' && r.status === 200`, `pushState(href, { history: r.data })`, else `goto(href)`. Render
  `<HistoryCard>` while `page.state.history` is set; on close return focus to the remembered link. Declare
  `App.PageState` (`history?: …`) in `frontend/src/app.d.ts`. Depends on T023, T024, T024a.
- [ ] T026 [US2] Run `cd frontend && npm test && npm run check`; make the new tests T012–T014a and T024a pass and keep
  `no-raw-html` and the i18n parity test green. Depends on T025, T022.

**Checkpoint**: US2 works on its own with default filters: card and page show current details and every change.

---

## Phase 5: User Story 3 - Filtered by default, everything on request (Priority: P2)

**Goal**: the history applies the student's filters like the feed (hidden left out, low priority marked) and offers a
"Show all" toggle that shows hidden changes badged; the FR-014a/b rules decide the starting state.

**Independent Test**: set a filter that hides one kind, cause one hidden and one visible change to one lesson, open
its history: only the visible one shows; toggle shows both, the hidden one badged (US3-1 to US3-9).

### Tests for User Story 3

- [ ] T027 [P] [US3] In `frontend/src/lib/history.test.ts` (created in T014a), add the remaining rows of the research
  R7 table: opened not hidden + `all=1` (all shown, toggle on); opened hidden + some visible + `all` absent/`1` (all
  shown, toggle on, no message); opened hidden + some visible + `all=0` (filtered, `openedHidden` true); every entry
  hidden (all shown, `toggle: false`, `allHidden` true); and the cap with the toggle on (hidden entries count toward
  it).
- [ ] T028 [P] [US3] In `frontend/src/lib/server/history.test.ts`, add outcome cases: low-priority and hidden rows keep
  their own outcome inside a mixed entry, and the entry `outcome` is the mildest row outcome (hidden only when every row
  is hidden).

### Implementation for User Story 3

- [ ] T029 [US3] In `frontend/src/lib/history.ts`, complete `historyView` with the remaining R7 rows: three-state `all`
  (null / true / false) including `all=1`, `openedId` lookup of the entry containing it, `showAll`, `toggle`,
  `openedHidden`, `allHidden` (FR-014, FR-014a, FR-014b). Per-row hiding and `hiddenCount` already exist from T019.
  Depends on T027, T019.
- [ ] T030 [US3] In `frontend/src/lib/components/History.svelte`, add to the heading row a compact "Show all" checkbox
  (about 18 px box, whole label ≥44 px target) with one short muted line beside it when it applies: "N hidden by your
  filters", "The change you opened is hidden by your filters", or, when every entry is hidden, only "All changes of this
  lesson are hidden by your filters" with no checkbox. Render hidden rows with a "Hidden" badge and dashed border/grey
  bar, low-priority with a grey bar and "Low priority" badge (reuse `Badge.svelte`, FR-021). Switching the checkbox
  calls shallow `replaceState` from `$app/navigation` with `?all=1`/`?all=0` and updates `page.state`, with no request,
  no new history step and no change to the feed's `showHidden` (FR-014a, research R7). Depends on T029, T021.
- [ ] T031 [US3] Wire `all` through both forms: in `HistoryCard.svelte` and `routes/changes/[id]/+page.svelte` read the
  effective `all` from the URL (`page.state` in the card, `page.url`/load data on the page) and pass it to `<History>`;
  "Open as page" in the card keeps the current query (`?all=…`). Depends on T030, T023, T022.
- [ ] T032 [US3] Run `cd frontend && npm test && npm run check`. Depends on T031, T028.

**Checkpoint**: all three stories work independently and together.

---

## Phase 6: Polish & Cross-Cutting Concerns

- [ ] T033 Run the full suites once, because the feed's corner time changed for every existing feed test:
  `dotnet test backend/InTheKnow.Tests` and `cd frontend && npm test && npm run check`.
- [ ] T034 Run the manual walkthrough of `specs/010-event-change-history/quickstart.md` steps 1–20 on the dev stack
  (card open/close via back, Esc, button and backdrop; forward reopens; focus return; Ctrl-click and middle-click;
  reload; Dutch and 360 px; 25-check lesson; 100-row lesson with API and page load times recorded (SC-003); access
  parity; `EXPLAIN ANALYZE` uses the new index), and record the
  results as walkthrough notes at the end of `quickstart.md`, as earlier specs did.
- [ ] T035 Check the diff for committed deployment details before any push (constitution Principle II): `git diff
  origin/main | grep -nE '[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+'` and confirm tests and docs use `localhost`/placeholders only.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 2 (Foundational)**: T001 first; T002, T003 and T004 then depend on it; T005 [P] can be written alongside T004.
  Blocks US2 and US3.
- **US1 (Phase 3)**: frontend only; independent of Phase 2 and can run in parallel with it.
- **US2 (Phase 4)**: needs Phase 2 and the T008–T010 corner-time work (T024 edits `ChangeGroup.svelte` after T010).
- **US3 (Phase 5)**: needs US2 (`History.svelte`, `HistoryCard.svelte`, the page).
- **Polish (Phase 6)**: after the desired stories.

### Task-level dependencies

- T008 → T009 → T010 → T011; T009 → T017 → T024 → T025.
- T014a → T019 → T021 → T022, T023 → T025; T019 → T024a → T025; T018 → T020 → T022; T018 and T019 share entry types (declare them in T019).
- T029 → T030 → T031 → T032.

### Parallel Opportunities

- Backend (T001–T005) and US1 (T006–T011) can proceed together.
- Tests marked [P] in a story (T006/T007; T012/T013/T014/T014a; T027/T028) touch different files and can be written together.
- T015 (i18n) and T019 (`history.ts`) are independent of each other.

## Parallel Example: User Story 2 tests

```text
Task: "Entry builder tests in frontend/src/lib/server/history.test.ts"      (T012)
Task: "id/all parsing tests in frontend/src/routes/changes/[id]"            (T013)
Task: "group().href cases in frontend/src/lib/server/changes.test.ts"       (T014)
Task: "historyView filter, cap and history-start tests in frontend/src/lib/history.test.ts" (T014a)
Task: "history.* texts in frontend/src/lib/i18n/en.ts and nl.ts"            (T015)
```

## Implementation Strategy

### MVP first (User Story 1)

1. Phase 3 (US1) needs no backend work: found time in the feed. Validate with T011 and quickstart steps 1–3.
2. Ship if wanted: it already helps students judge how recent a change is.

### Incremental delivery

1. Phase 2 (migration, `baseline_at`, history route) → US2 (card and page, default filters) → validate quickstart
   steps 4–12, 17–19.
2. US3 (toggle and FR-014b states) → validate steps 13–16.
3. Polish: full suites, manual walkthrough, diff check.

## Notes

- Research listed `historyView` in `frontend/src/lib/history.ts` while plan.md lists a `lib/server/history.ts` for the
  loader. Both are kept: the loader and entry builder are server code (`lib/server/history.ts`); the pure toggle/cap
  logic runs in the component, so it is client-safe in `lib/history.ts`.
- `HistoryCard` must never hold its own `open` flag: the card is open exactly when `page.state.history` is set.
- The 404 for "unknown" and "not followed" MUST come from the same `Problem(404, "not_found")` call.
- Commit after each task or logical group.
