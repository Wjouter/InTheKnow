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

## Walkthrough notes (2026-11-12)

Run on the local dev stack (`api` and `web-bun` from `.claude/launch.json`; the host's Node was broken, so `npm test`,
`npm run check` and `npm run build` ran in a `node:24-bookworm-slim` container). Migration 006 applied to the dev
database on API start.

**Data.** Seeded with direct `INSERT`s, not real checks: two test students, one timetable with a custom filter that
hides description changes, lesson A (location 50 h ago, time 3 h ago, description 20 min ago), lesson B (removed 2 h
ago, back 1 h ago), lesson C (25 room changes), and later lesson D (removed 5 min ago, no snapshot). The seed was
removed afterwards. The real-check path is covered by `HistoryTests.HistoryStartsAt` (add-link, a normal check and a
features re-baseline through the checker).

| Step | Result |
|---|---|
| 1–2 | Pass. Corners read "1 hour ago", "3 hours ago"; older rows an exact date. The title has the year ("Thu 12 Nov 2026 15:41") and `datetime` the ISO value. The sentence keeps the lesson time. |
| 3 | Pass (2026-11-12, real check data). Change 120 set to 23 h ago reads "23 hours ago"; at 25 h it shows "Wed 11 Nov 20:17" once. The real time was restored. The other boundaries are in `format.test.ts`. |
| 4 | Pass. Card over the feed, date tile, block, More details (status, description, `Lecturer`, timetable), "Time 13:30–15:00 → 09:00–10:30" with "Same day: Wed 18 Nov", opened entry marked. |
| 5 | Pass for Esc, close button, backdrop and Back: one step back each, feed scroll unchanged, focus on the clicked change. |
| 6 | Pass. Forward reopens the card. |
| 7 | Pass (2026-11-12, real check data). From the card of change 120, Open as page loads `/changes/120` as the full page with the same entry; the card is gone. |
| 8 | Pass. Ctrl-click and middle-click open the full page in a new tab and leave the feed as it was (checked by hand in Firefox). |
| 9 | Pass. Loading `/changes/{id}` gives the full page, scrolled to the opened entry. |
| 10 | Pass. Removal and re-appearance are separate entries, no Removed badge. Lesson D (removed newest): dimmed block, struck name, Removed badge, "Removed 5 minutes ago". |
| 11 | Pass. "…before its recorded history starts (Mon 9 Nov)" from `baseline_at`. |
| 12 | Pass. |
| 13 | Pass. Description change left out, "1 change hidden by your filters"; Show all shows it badged Hidden, URL `?all=1`, `history.length` unchanged. |
| 14 | Pass (in Dutch). From the feed with hidden changes shown: `?all=1`, toggle on, entry marked; off gives `?all=0` and "De wijziging die je opende is verborgen door je filters"; closing keeps `/changes?showHidden=1`. |
| 15 | Pass (real check data). Reloading change 120 keeps the toggle on with `?all=1` and off with `?all=0`. Change 158 (opened change hidden, lesson mixed), server-rendered: no `all` and `?all=1` give 5 entries, toggle on; `?all=0` gives 1 entry, toggle off, "The change you opened is hidden by your filters". |
| 16 | Pass (real check data). The Pasen holiday (change 166), where every change is hidden by the student's filters, opened from the feed with hidden changes shown: no toggle, both entries badged Hidden, "All changes of this lesson are hidden by your filters". |
| 17 | Pass. A non-follower gets byte-identical 404 pages for a real id and `999999999`; API parity is in `HistoryTests`. |
| 18 | Pass at 360 px in Dutch: the card fills the screen, no sideways scroll, time values move whole. |
| 19 | Pass. 20 entries, "5 older changes not shown" and "The change you opened is further back", both linking to `/changes/{oldest}`. |
| 20 | Pass on dev data, not whole-school size. One real lesson grown to 101 rows (99 copies, removed afterwards): the card shows 20 entries and "80 older changes not shown" (one row hidden by filters); the full page renders in 44–67 ms; `EXPLAIN ANALYZE` uses `detected_change_calendar_id_event_key_id_idx`, 0.19 ms. Earlier, with 100 000 extra rows on one calendar (rolled back), the same index: 0.08 ms. |

Found and fixed during the walkthrough: a `.wrap` class in `History.svelte` collided with the global `.wrap`
container (large padding in free-text rows), and entries older than 24 hours printed their date twice.
