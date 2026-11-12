# Quickstart: validating event change history

Contracts: [api.md](./contracts/api.md), [pages.md](./contracts/pages.md). Shapes: [data-model.md](./data-model.md).

## Automated

```bash
dotnet test backend/InTheKnow.Tests --filter "FullyQualifiedName~HistoryTests|FullyQualifiedName~ApiTests|FullyQualifiedName~CheckerTests|FullyQualifiedName~SubscriptionsTests"
```

```bash
cd frontend && npm test && npm run check
```

Expected: all pass.

- **Backend**: covers the cases listed under "Backend tests" in [contracts/api.md](./contracts/api.md). These
  include identical 404 responses for "unknown" and "not followed" (SC-005), and `baseline_at` on both baseline
  paths.
- **Frontend**: covers found-time boundaries (59 s, 59 min, 23 h 59 min, 24 h, an earlier year; never
  "yesterday"/"gisteren"), `group().href`, every row of the research R7 toggle table, the card cap with the opened
  entry beyond it, the one-line time change (hours only plus the date note, or dates when the date moved), and i18n
  key parity (SC-006).

Then run the full suites once (`dotnet test backend/InTheKnow.Tests`, `npm test`), because the feed's corner time
changed for every existing feed test.

## Manual walkthrough (dev stack from the root `QUICKSTART.md`)

**Getting real changes.** Changes must come from real checks:

1. Serve an editable copy of `backend/InTheKnow.Tests/Fixtures/mt-nl.ics`, as in the 003 walkthrough notes. The
   SSRF guard refuses local addresses, so swap the fetch transport in a scratch host outside the repository.
2. Add the link as a test student.
3. Edit the file between checks.
4. Trigger each check with `UPDATE calendar SET next_check_at = now() WHERE id = <id>;`.

Use these edits on one lesson ("lesson A"), each in its own check:

1. **Location**: change its location.
2. **Time**: move it from 13:30–15:00 to 09:00–10:30.
3. **Description**: change its description. The default preset doesn't hide this, so add a custom filter rule that
   hides `description` changes for this timetable.

Then remove a second lesson ("lesson B") in one check and put it back in the next.

**Feed time (US1)**

1. Open `/changes`. Each notice's corner shows "N minutes ago", not the lesson time, and the sentence below still
   gives the lesson time (US1-1).
2. Hover a corner time. The title shows the full date and time, with the year (US1-4). On a phone, a long-press
   on the link shows the link menu instead, and the exact time appears in the history view (research R1).
3. Run `UPDATE detected_change SET detected_at = detected_at - interval '23 hours'` on one row and reload. It reads
   "23 hours ago". With `25 hours` it shows an exact date and time (US1-2, US1-3).

**Card and page (US2)**

4. Scroll the feed down a little, then click lesson A's newest change.
   - The card opens over the feed.
   - The current details appear as a calendar event: a date tile, and a block with the name, the new time · location,
     and teachers · groups · type.
   - **More details** opens to show the status, description and timetable name.
   - Entries are newest first, and the time entry shows one line, "Time 13:30–15:00 → 09:00–10:30", with "Same
     day: …" under it (US2-1, US2-3, US2-4).
   - The clicked entry is marked (US2-6).
5. Close the card, once with each method: Esc, the close button, a click outside the card, and the browser back
   button. Each time the card closes, the feed is at the same scroll position, and focus is on the change you
   clicked (US2-7).
6. After closing with back, press forward. The card opens again.
7. Open the card and press **Open as page**. The full page shows the same history (US2-8).
8. Ctrl-click and middle-click a change. A new tab opens the full page (US2-9).
9. Reload while a card is open. The full page loads (US2-10).
10. Open lesson B's history.
    - The removal and the re-appearance are separate entries.
    - The current details have no "Removed" badge.
    - With the newest entry being the removal (open it before putting B back), the page shows the last known
      details as a dimmed event: the name struck through, a "Removed" badge and the removal time.
11. Lesson A's oldest entry is not a "new" entry, because it was in the baseline. The page says it was already in the timetable
    before its recorded history starts, and gives the date the link was added (FR-011).
12. Every history ends with "Changes older than 1 year are not kept" (FR-012).

**Filters (US3)**

13. Open lesson A from the feed with hidden changes off.
    - The description change is not listed.
    - The page says 1 change is hidden by your filters.
    - Tick **Show all** next to the "Changes, newest first" heading. The change appears with a "Hidden" badge, and the URL now ends in `?all=1`
      (US3-1, US3-2).
14. Turn on "show hidden changes" in the feed, then click the hidden description change.
    - The view starts with all changes shown, the toggle on, and that entry marked (US3-4, US3-5).
    - Switch the toggle off. Only the visible entries remain, and it says the opened change is hidden (US3-6).
    - Close the card. The feed still shows hidden changes (US3-9).
15. Reload `/changes/{id}?all=0` and `/changes/{id}?all=1`. The state survives (US3-8).
16. Hide every change kind for the timetable, then open a change from the feed with hidden changes shown. There is
    no toggle, and the view says all changes of this lesson are hidden (US3-7).

**Access (SC-005)**

17. As a second test student who does not follow the timetable, open `/changes/{id}` and `/changes/999999999`.
    Both show the same "not found" page. In the browser's network tab, the two API responses are identical.

**Presentation**

18. Repeat steps 4 and 13 in Dutch, at 360 px width (the card fills the screen, no sideways scroll, and no time
    value breaks in the middle) (FR-018, FR-019).
19. Seed one lesson with 25 checks and open the card. It shows 20 entries and "5 older changes not shown" with a
    link to the page. Opened from the oldest change, the card says that change is further back, and the page
    scrolls to it (FR-009a). Seeding with direct `INSERT`s into `detected_change` is fine here, because only display
    is being tested.

**Performance (SC-003)**

20. With a lesson of 100 rows in a database seeded to whole-school size, time
    `GET /api/me/changes/{id}/history` (for example with `curl -w '%{time_total}'` and the session cookie). Expect
    well under 1 s. Then open that lesson's full page in the browser and read the load time from the network tab:
    it must be ready in under 1 s, and the card shows 20 entries and "80 older changes not shown". Check that
    `EXPLAIN ANALYZE` on the row query uses the `(calendar_id, event_key, id)` index.

Record the results as walkthrough notes here, as earlier specs did.
