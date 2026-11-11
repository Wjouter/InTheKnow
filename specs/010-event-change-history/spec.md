# Feature Specification: Event change history

**Feature Branch**: `010-event-change-history`

**Created**: 2026-11-11

**Status**: Draft

**Input**: User description: "Event change history" (InTheKnow roadmap,
[Wjouter/InTheKnow#23](https://github.com/Wjouter/InTheKnow/issues/23), the event-history part only): "I want to be
able to see the full history of the event, and show only filtered (default) or all changes. This allows you to see
when the event was first found in the system or when every change occurred." Also: the time on a change in the feed
should say when the change was detected, instead of repeating the event's start time that the sentence below it
already gives.

## Clarifications

### Session 2026-11-11

- Q: When should the history view say that older changes are no longer kept? → A: Always: every history list ends
  with one short note that changes older than the retention period are not kept.
- Q: When a lesson has many changes, should the history view show all of them at once, or load them in parts? →
  A: The card shows only the newest 20 entries. When there are more, it says so and links to the full page, which
  shows every kept entry at once.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - See when a change was found (Priority: P1)

A student opens their changes feed. Each change shows a time in its top corner, but today that is the lesson's own
start time, which the sentence below it ("New event: Wed 6 Jan 2027 16:00–17:30") already says. The student cannot
tell whether a change is from this morning or from three weeks ago. With this feature the corner time says when
InTheKnow found the change.

**Why this priority**: it is small, it fixes information that is repeated today, and it already helps the student
judge how recent each change is, even without the history view.

**Independent Test**: make a change in a watched timetable, let InTheKnow check it, and open the feed: the corner
time is the time of that check, not the lesson's start time.

**Acceptance Scenarios**:

1. **Given** a change to a lesson on 6 January that was found 2 hours ago, **When** the student opens the feed,
   **Then** that change's time reads "2 hours ago", and the lesson date appears only in the sentence below.
2. **Given** a change found 23 hours ago, **When** the student opens the feed, **Then** its time still reads
   relatively ("23 hours ago"), never "yesterday".
3. **Given** a change found 24 hours ago or longer, **When** the student opens the feed, **Then** its time shows the
   exact date and time it was found (for example "Tue 10 Nov 2026 09:19").
4. **Given** the student pauses on (hovers over or long-presses) a relative time, **When** it is shown, **Then** the
   exact date and time it was found is available.

---

### User Story 2 - Open the full history of one event (Priority: P1)

A student sees that "Optische sensoren en actuatoren" moved. They want to know what the lesson looks like now and
whether this is the first time it moved. They click the change and a history card for that lesson opens over the
feed (filling the screen on a phone): at the top how the
lesson is now (name, start, end, location and the other details InTheKnow keeps), and below it every change
InTheKnow found for that lesson, newest first. Each change shows when it was found, what kind of change it was
(new, removed or modified) and, for modifications, a list of the changed details with the value before and after.
Several changes close together in time make it obvious that the lesson changed quickly.

**Why this priority**: this is the core of issue 23. Without it the feed only shows one change at a time, and the
student cannot see the full story of a lesson.

**Independent Test**: let one lesson change two or three times across separate checks, click any of those changes
in the feed, and check that the page shows the lesson's current details and all of its changes with their times
and before/after values.

**Acceptance Scenarios**:

1. **Given** a change in the feed on a wide screen, **When** the student clicks it, **Then** the history card for
   that lesson opens over the feed, and the feed stays where it was underneath.
2. **Given** the same on a phone-width screen, **When** the student taps it, **Then** the history card fills the
   screen.
3. **Given** a lesson that moved from 13:30–15:00 to 09:00–10:30, **When** its history view is open, **Then** the
   entry for that change shows one "Time" line, "13:30–15:00 → 09:00–10:30", with the old value marked as before
   and the new value marked as after, the date once (as it did not change), and the time the change was found.
4. **Given** a lesson whose location changed and later whose time changed, **When** its history view is open,
   **Then** both changes are listed as separate entries, newest first, each with its own time.
5. **Given** a lesson that InTheKnow first saw appear after it started watching the timetable, **When** its history
   page is open, **Then** the oldest entry is the "new" entry with the time it was found.
6. **Given** the history view was opened from a specific change, **When** it is shown, **Then** the entry for that
   change is easy to find (for example highlighted or scrolled into view).
7. **Given** the history card is open, **When** the student uses the browser's or phone's back button, presses Esc,
   uses the close button, or (on a wide screen) clicks outside the card, **Then** the card closes and the feed is
   where it was.
8. **Given** the history card is open, **When** the student chooses "open as page", **Then** the same history is
   shown as a full page of its own.
9. **Given** a change in the feed, **When** the student opens it in a new tab (for example Ctrl-click or
   middle-click), **Then** the new tab shows the full history page.
10. **Given** the address of an open history card, **When** it is reloaded or opened as a link, **Then** the full
   history page for that lesson is shown.

---

### User Story 3 - Filtered by default, everything on request (Priority: P2)

The student's filters hide changes they do not care about (for example description edits). In the history view they
see the same picture as in the feed: hidden changes are left out and low-priority changes are marked as such. A
toggle shows every change InTheKnow found for that lesson, with hidden ones marked as hidden, so the student can
check the complete record when they want to.

**Why this priority**: it keeps the history consistent with the feed, but the history is already useful without the
toggle.

**Independent Test**: set a filter that hides one kind of change, cause one hidden and one visible change to the
same lesson, and open its history: only the visible one shows; switch the toggle and both show, the hidden one
marked as hidden.

**Acceptance Scenarios**:

1. **Given** a lesson with one change the student's filters hide, **When** its history view opens, **Then** that
   change is not listed and the page tells the student that some changes are hidden by their filters.
2. **Given** the same page, **When** the student turns on "show all changes", **Then** the hidden change appears,
   marked as hidden.
3. **Given** a change the student's filters mark as low priority, **When** the history view is shown, **Then** that
   change is listed and marked as low priority, as in the feed.
4. **Given** the student has turned on "show hidden changes" in the feed, **When** they open a change's history,
   **Then** the history view starts with all changes shown, hidden ones marked as hidden, and its toggle on.
5. **Given** the feed shows hidden changes and the student opens the history from a hidden change, **When** the view
   is shown, **Then** all changes are shown and that change is listed and marked as the one it was opened from.
6. **Given** the history was opened from a hidden change and the lesson also has changes that are not hidden,
   **When** the student switches the toggle off, **Then** only the changes that are not hidden remain, and the view
   says that the change they opened is hidden by their filters.
7. **Given** the history was opened from a hidden change and every change of the lesson is hidden, **When** the
   view is shown, **Then** there is no toggle, and the view says that all changes of this lesson are hidden by
   their filters.
8. **Given** the history was opened with all changes shown, **When** it is reloaded, opened in a new tab or opened
   as a page, **Then** it still shows all changes.
9. **Given** the student switches the toggle inside the history card, **When** they close the card, **Then** the
   feed is unchanged and keeps its own "show hidden changes" setting.

---

### Edge Cases

- **Lesson that was already there when InTheKnow started watching**: when a timetable is first added, or read again
  from scratch, InTheKnow records what is there without counting it as new. Such a lesson has no "new" entry. The
  page MUST NOT claim a first-seen time it does not know; it says the lesson was already in the timetable before its
  recorded history starts, and gives the date the history starts from when that is known.
- **Removed lesson**: a lesson that was removed has no current details anymore. The page shows its last known
  details, clearly marked as removed, with the time it was removed.
- **Lesson that was removed and later appears again**: the history shows the removal and the new appearance as
  separate entries in time order.
- **Older history deleted**: changes are kept for a limited time (one year by default). The page MUST NOT suggest the
  list is complete beyond that; every history list ends with a note that older changes are not kept (FR-012).
- **Repeating lessons**: each occurrence of a repeating lesson that InTheKnow tracks on its own has its own history.
- **Many changes**: a lesson that changed very often (for example a hundred times) still opens quickly. The card
  shows the newest 20 entries and links to the full page, where the student can see all of them (FR-009a).
- **Opened from an older change**: when the change the card was opened from is not among the newest 20 entries, the
  card still lists the newest 20, says the opened change is further back, and links to the full page, where that
  entry is marked and scrolled into view (FR-006).
- **No access**: a student who does not (or no longer) follows the timetable of that lesson, or who opens a link to a
  change that does not exist or was deleted, gets a "not found" message (in the card or as the page) that does not
  reveal whether the lesson or change exists.
- **Every change hidden**: when the student's filters hide all of a lesson's changes, the view can only have been
  opened from a hidden change. All changes are shown, marked as hidden, and there is no toggle (FR-014b), because
  switching it off would leave nothing.
- **Timetable followed through more than one link**: the student sees the lesson's history once, not once per link.

## Requirements *(mandatory)*

### Functional Requirements

**Change time in the feed**

- **FR-001**: Each change in the feed MUST show the time InTheKnow found it, instead of the lesson's start time.
- **FR-002**: The found time MUST be shown relative to now (for example "5 minutes ago", "23 hours ago") while the
  change was found less than 24 hours ago, and as an exact date and time (for example "Tue 10 Nov 2026 09:19") from
  24 hours on. It MUST NOT use calendar-day words such as "today" or "yesterday". It follows the student's chosen
  language and is in local time.
- **FR-003**: When the found time is shown in a short or relative form, the full date and time MUST also be
  available on the same element.

**Opening the history**

- **FR-004**: Each change in the feed MUST be clickable (and reachable and usable by keyboard) and open the history
  view of the lesson it belongs to. This replaces spec 005's decision that change notices are not clickable
  (005 research R11 and R18).
- **FR-005**: The history view comes in two forms with the same content (except that the card shortens long
  lists, FR-009a): a **card** that opens over the feed, and a
  **full page**. A plain click opens the card. On a wide screen the card floats over the feed; at phone width it
  fills the screen. The feed underneath keeps its scroll position.
- **FR-005a**: The card MUST close with a close button, the Esc key, the browser's or phone's back button, and (on a
  wide screen) a click outside it. While open, keyboard focus stays inside the card; on closing it returns to the
  change that opened it.
- **FR-005b**: An open card MUST be reflected in the address, so back closes it and forward reopens it. Opening
  that address directly (reload, bookmark, shared link, new tab, Ctrl-click or middle-click on a change) MUST show
  the full page.
- **FR-005c**: The card MUST offer an "open as page" action that shows the full page for the same lesson.
- **FR-005d**: The address MUST NOT contain the timetable's or lesson's internal identifiers beyond what the feed
  already exposes, and MUST stay short.
- **FR-006**: The history view MUST mark the entry for the change it was opened from.

**What the history view shows**

- **FR-007**: The page MUST show the lesson's current details: name, start, end, location and every other detail
  InTheKnow keeps for lessons, plus the name of the timetable it belongs to. They MUST be shown compactly, the way a
  calendar app shows an event:
  - a small date tile (month, day, weekday);
  - next to it, an event block with the name, the start–end time and location, and then the teachers, groups and
    type;
  - the remaining details (status, description, the other kept details and the timetable name) behind a control
    that starts collapsed and reads "More details", or "Fewer details" while open.
- **FR-008**: For a removed lesson the page MUST show its last known details in the same event form, marked as
  removed (a "Removed" badge, the name struck through and a dimmed block), with the time of the removal.
- **FR-009**: The page MUST list every kept change to that lesson, newest first. Each entry MUST show the kind of
  change (new, removed or modified), the time InTheKnow found it, and the lesson name at that moment.
- **FR-009a**: The card MUST list at most the newest 20 entries (counted after FR-010 grouping, among the entries
  the current filter toggle shows). When there are more, the card MUST say how many entries are not shown and offer
  a link to the full page. The full page MUST list every kept entry at once, without paging.
- **FR-010**: Changes found in the same check MUST appear as one entry. A modified entry MUST list each changed
  detail on one line: the detail's name, its value before and its value after (for example
  "Time 13:30–15:00 → 09:00–10:30"). The before and after values MUST be told apart by more than colour alone (for
  example by strike-through, an arrow and labels for screen readers). A value MUST show only what changed: a time
  change on the same day shows the times without repeating the date. Short values (times, rooms, statuses) MUST NOT
  break in the middle; when before and after do not fit side by side, the after value moves to the next line as a
  whole.
- **FR-011**: When the lesson has no "new" entry, the page MUST say that the lesson was already in the timetable
  before its recorded history starts, and MUST NOT present the oldest listed change as the moment the lesson first
  appeared.
- **FR-012**: Every history list MUST end with one short note that changes older than the retention period are not
  kept, naming that period (for example "Changes older than 1 year are not kept"). The note is always shown, for
  every lesson.

**Filters**

- **FR-013**: Unless FR-014a says otherwise, the page MUST apply the student's current filters for that timetable in the same way the
  feed does: hidden changes are left out, low-priority changes are listed and marked.
- **FR-014**: The page MUST offer a toggle that shows all changes, with the ones the filters hide marked as hidden.
- **FR-014a**: The toggle MUST start in the same state as the feed's "show hidden changes" setting at the moment the
  history is opened: on when the feed shows hidden changes, off otherwise. The state MUST be part of the history
  view's address, so a reload, a new tab or "open as page" keeps it. Switching the toggle inside the history view
  MUST NOT change the feed's setting.
- **FR-014b**: When the history view is opened from a change the student's filters hide, it MUST start with all
  changes shown, also when the address does not ask for all changes, so the change it was opened for is visible.
  - If the lesson also has changes the filters do not hide, the toggle stays available. Switching it off leaves
    only those changes and tells the student that the change they opened is hidden by their filters.
  - If every change of the lesson is hidden, the view MUST NOT offer the toggle, because switching it off would
    leave no changes. Instead of the toggle it says that all changes of this lesson are hidden by their filters.
  - This holds for the card and the full page, and is judged with the student's filters at the moment the view
    opens.
- **FR-015**: When changes are left out by filters, the page MUST say so.

**Access and privacy**

- **FR-016**: Only a signed-in student who currently follows the lesson's timetable MAY see its history. Anyone else
  MUST get the same "not found" response as for a change that does not exist.
- **FR-017**: The history view MUST show only what InTheKnow already stores about the lesson and its changes; this
  feature MUST NOT start storing new personal data.

**Presentation**

- **FR-018**: All new texts MUST be available in English and Dutch and follow the student's language choice.
- **FR-019**: The feed change and the history view MUST work at phone width without sideways scrolling of the page, and MUST work in light and dark theme.
- **FR-020**: The history view MUST follow the existing design system; the reference screenshots in issue 23 set
  the layout (kind badge with the found time, lesson name, the changed details with before and after), not the
  colours. The before/after table in those screenshots is replaced by the one-line list of FR-010, which stays
  readable at phone width.
- **FR-021**: The history view's look follows the design review of 2026-11-11 (canvas "InTheKnow Frontend", row
  "Change history (spec 010)"):
  - **Cards.** The history sits on a light grey ground. Each entry is a rounded card with a visible border and a
    coloured bar on its leading edge that matches its kind (changed, new, removed). Low-priority and hidden entries
    get a grey bar, and hidden entries also a dashed border.
  - **Before/after values.** Before values are struck through on a light red chip. After values are bold on a
    light green chip. Colour is never the only cue (FR-010).
  - **Opened entry (FR-006).** It is marked with a brand-coloured border and a "You opened this change" label. A
    removed entry keeps its muted look even when it is the opened one, and only the label marks it, so that it
    still reads as gone.
  - **Toggle.** "Show all" (FR-014) is a compact control next to the "Changes, newest first" heading, not a
    separate panel. The FR-014b and FR-015 messages sit beside it as one short line.

### Key Entities

- **Lesson (event)**: one item in a watched timetable, identified within its timetable. Has current details (name,
  start, end, location, other kept details) while it exists.
- **Detected change**: one difference InTheKnow found for a lesson in one check: its kind (new, removed, modified),
  the detail that changed with its value before and after, the lesson name at that time, and when it was found.
  Changes are kept for the retention period and then deleted.
- **Lesson history**: all kept detected changes for one lesson, grouped per check, newest first, together with the
  lesson's current or last known details.
- **Subscriber filters**: the student's existing filter rules per timetable, which give each change an outcome
  (shown, low priority or hidden).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For any change in the feed, a student can tell when it was found without opening anything, and the
  corner time no longer repeats the lesson time from the sentence below it.
- **SC-002**: From the feed, a student reaches the full history of a lesson in one click or tap.
- **SC-003**: For a lesson with up to 100 kept changes, the history view is ready to read within 1 second for 95%
  of openings, at whole-school scale (thousands of watched timetables).
- **SC-004**: In a walkthrough, a student can answer "what does this lesson look like now", "when did it last
  change" and "has it changed more than once" from the history view alone, without help.
- **SC-005**: A student who does not follow a timetable cannot learn anything about its lessons through the history
  page (verified by an automated check).
- **SC-006**: Every new text is present in both English and Dutch (verified by the existing translation check).

## Assumptions

- The card and the full page show the same content, except that the card lists only the newest 20 entries
  (FR-009a); otherwise only the frame around it differs. The card suits a quick look, the full page suits long
  histories and opening in a new tab.
- The view is opened through one of the lesson's changes, so a lesson without any kept change has no history view;
  a list of all lessons in a timetable is out of scope.
- "Changes found in the same check form one entry" reuses the grouping the feed already uses.
- A lesson's identity is the one InTheKnow's change detection already uses; this feature does not try to link a
  lesson to a different lesson the school re-created under another identity.
- The student sees all kept changes of a followed timetable, including changes from before they started following
  it, in the same way the feed already does.
- The broader redesign in issue 23 (other screens restyled after the reference screenshots) is out of scope.
- Email notifications are unchanged.
