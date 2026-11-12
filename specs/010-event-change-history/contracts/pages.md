# Contract: Pages, card and feed changes

Extends [005 contracts/pages.md](../../005-sveltekit-frontend/contracts/pages.md). It replaces 005 research R11 and
R18 on one point only: change notices are now links (FR-004).

## Addresses

| Address | Sign-in | Shows |
|---|---|---|
| `/changes/{id}` | required (as `/changes`) | the full history page of the lesson of change `{id}` |
| `/changes/{id}?all=1` | required | the same, toggle on: all changes shown, hidden ones badged |
| `/changes/{id}?all=0` | required | the same, toggle off: filtered, even when opened from a hidden change |

- `{id}` must match `^[1-9]\d{0,17}$`. Anything else gives the 404 page.
- `all` accepts only `0` or `1`. Any other value counts as absent, which means the default rule in research R7.
- An API 404 renders the existing `+error.svelte` with the standard "not found" text, the same for every cause
  (FR-016).
- The address holds only the change id, which the feed already uses, plus `all` (FR-005d).
- When the session has expired, the page redirects to sign-in as other student pages do, with `next` set to this
  address.

## Feed changes (`/` and `/changes`)

`ChangeGroup` changes as follows:

- **The notice is a link**: `<a class="change …" href={group.href}>`, so it is focusable and opens with Enter
  (FR-004). Its heading and sentences stay inside it.
- **`group.href`**: `/changes/{id}` (research R9), plus `?all=1` when the feed URL has `showHidden=1`. It is built
  in `group()` on the server, so `/` and `/changes` agree. `/` has no `showHidden`, so it never adds `all`.
- **Corner time**: `<time class="when" datetime={detectedAt} title={found.exact}>{found.text}</time>` replaces
  `fmt.dateTime(eventStartsAt)` (FR-001 to FR-003, research R1).
- **No change to the sentence**: it keeps the lesson's own time, for example "New event: Wed 6 Jan 2027
  16:00–17:30".

`Feed` handles clicks. A plain primary click opens the card. Any modifier key, middle-click or other button is left
to the browser (research R8).

## Card (shallow route over the feed)

- **Open**: `preloadData(href)` returns `type: 'loaded'` with `status: 200`, then
  `pushState(href, { history: data })`. Any other result falls back to `goto(href)`. While `page.state.history` is set, `Feed` renders `HistoryCard`.
- **Frame**: a native modal `<dialog>` with an `aria-labelledby` heading (the lesson name), an **Open as page**
  link (`href` = the current card URL, a full load) and a close button (`aria-label` = the existing "Close" text).
  - Wide screens: centred, at most about 720 px wide, at most 90 vh high, scrolling inside.
  - Below 600 px: fills the viewport.
- **Close**: the button, Esc (`cancel`) and a backdrop click (wide only) all call `history.back()`. Popstate clears
  the state and an effect closes the dialog. `onclose` steps back only while the state is still set, so it never
  steps back twice. Focus returns to the opening link (FR-005a).
- **Forward**: forward reopens the card. A reload of the card URL loads the full page (FR-005b).
- **Toggle**: shallow `replaceState` with the new `all`. It makes no request, adds no history step, and does not
  touch the feed's `showHidden` (FR-014a, scenario 9).
- **Cap**: the card shows at most 20 entries, plus "N older changes not shown" with a link to the page. When the
  opened entry falls beyond the cap, it also shows "The change you opened is further back" (FR-009a).

## Full page `/changes/{id}`

- **Loading**: `+page.server.ts` loads `GET /api/me/changes/{id}/history` and builds the entries on the server
  (texts, rows, found times), like `loadFeed`. The `historyView` filter and cap logic runs in the component, so the
  toggle needs no reload.
- **Layout**: `PageIntro` (the lesson name, with the timetable name under it), a "Back to changes" link to
  `/changes`, then `<History>` with no cap.
- **Opened entry**: the opened entry is marked as under § Shared content › Entries (`aria-current="true"`), and
  scrolled into view after load (FR-006).
- **Toggle**: same rule as the card. It uses shallow `replaceState` on the page URL.

## Shared content `<History>`

From top to bottom:

1. **Current details** (FR-007, FR-008), shaped like a calendar event:
   - **Date tile**, about 52 px wide. It shows the start date: the month in a brand-coloured strip, then the day
     number and the short weekday. A visually hidden full date is added for screen readers. A lesson that spans
     several days shows its start date, and the time line shows the range (`fmt.range`).
   - **Event block**, next to the tile: a soft-tinted block with three lines:
     1. the name;
     2. start–end time · location;
     3. teachers · groups · type.

     A part with no value is left out of its line.
   - **More details**: a disclosure button, collapsed at first, with a target of at least 44 px. It has
     `aria-expanded`, and a chevron that turns when open. Its label reads "More details" while collapsed and "Fewer
     details" while open. It holds a
     compact label/value list: status word, description, rooms when they differ from the location, every `details`
     label and value, and the timetable name. A detail with no value is left out.
   - **States**:
     - `present`: a tinted block, a brand date strip, no badge.
     - `removed`: a grey date strip, a dashed grey block, the name struck through, a "Removed" badge, and a third
       line "Removed {found time}".
     - `gone`: the same dimmed look, with a "No longer in the timetable" badge and no time.
2. **Changes heading row**, by research R7. There is no separate panel: the "Changes, newest first" heading has
   the controls on its right, and they wrap under it at phone width (FR-021).
   - A checkbox labelled "Show all" (FR-014). The checkbox is about 18 px, but the whole label is a target of at
     least 44 px.
   - Next to it, one short muted line, when it applies:
     - "N hidden by your filters" while rows are left out (FR-015);
     - "The change you opened is hidden by your filters" (FR-014b).
   - When every entry is hidden: only "All changes of this lesson are hidden by your filters", with no checkbox.
3. **Entries**, newest first, on a light grey ground (`--surface-subtle`). Each entry is a card (FR-021):
   - **Card**: 8 px rounded corners, a 1 px `--border-default` border, `--shadow-1`, and a 4 px bar on the leading
     edge:
     - blue (`--status-info`) for changed;
     - green (`--status-new`) for new;
     - red (`--status-cancelled`) for removed;
     - grey (`--grey-400`) for low priority.
   - **Hidden entry**: `--grey-100` background, a dashed `--border-strong` border, a grey bar and no shadow.
   - **Opened entry** (FR-006):
     - Normally: a `--brand-soft` background, a 2 px `--brand` border with a 6 px leading bar, and the label "You
       opened this change" at the end of the header line.
     - Removed and opened: it keeps the removed look. The background is `--grey-100`, the name is greyed and struck
       through, and the bar is red. Only the brand-coloured label marks it as opened.
   - Inside each card:
   - A kind badge (New / Removed / Changed, using the existing tone colours) with the found time, relative and
     exact both visible (research R1).
   - The lesson name at that moment.
   - Low-priority and hidden badges per row, as in the feed. With the toggle off, hidden rows are left out of a
     mixed entry.
   - Added and removed entries: the start–end sentence.
   - Changed entries: one line per changed detail, `label · before → after` (research R6):
     - **Before** is a chip struck through on `--red-100` with `--red-500` text. **After** is a bold chip on
       `--green-100` with `--green-700` text. Both pairs pass 4.5:1.
     - Visually hidden "Before:" and "After:" labels go in front of the values.
     - Short values never break; on a narrow screen the after value moves to the next line whole.
     - A same-day time change shows only the hours, with the date once below.
4. **History start note** when the oldest kept entry is not an `added` entry (FR-011, research R4):
   - "This lesson was already in the timetable before its recorded history starts", plus " ({date})" when
     `historyStartsAt` is known.
   - The note never names the oldest entry as the first appearance.
5. **More** (card only): see Cap.
6. **Retention note** (FR-012), always last: "Changes older than {period} are not kept."

All colours come from the existing tokens. Badges always carry text, so meaning never depends on
colour alone. At 360 px the page has no sideways scroll (FR-019).

## New texts (en and nl, FR-018)

`changes.found` (relative and exact are built with `Intl`, so no text keys for the units), plus the `history.*`
texts:

- title / back link;
- open as page;
- current: "Now in the timetable", "Last known details", "More details" / "Fewer details", removed (with time) and no longer in the
  timetable;
- the detail labels: time, status, location, rooms, teachers, groups, type, description, name;
- the kinds: new, removed, changed;
- the hidden value labels "Before:" and "After:", and the time note "Same day: {date}";
- show all;
- the messages: N hidden, opened hidden, all hidden, and "You opened this change";
- history start, with and without a date;
- N older not shown, opened further back;
- the retention note.

The existing i18n parity test enforces that both languages have every key (SC-006).
