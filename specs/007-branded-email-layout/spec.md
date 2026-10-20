# Feature Specification: Branded email layout

**Feature Branch**: `007-branded-email-layout`

**Created**: 2026-10-20

**Status**: Draft

**Input**: User description: "Branded HTML email layout for all emails InTheKnow sends (timetable change notifications, notification-address confirmation, test notification, and all account emails), following the InTheKnow design system and the approved design canvas. Readable in Outlook for Windows, Gmail and Apple Mail, from 320px phones to wide desktop windows and at 200% zoom; the plain-text part and the existing message contract stay as they are."

Design references: the InTheKnow Design System (colours, type, wordmark) and the approved design canvas
"InTheKnow Email Layout" (artboards: changes, confirmation, account, changes on a phone). Where this spec and the
canvas differ, this spec wins.

## Clarifications

### Session 2026-10-20

- Q: Does "Outlook for Windows" include classic desktop Outlook (Word rendering engine) or only new Outlook? → A: Both: classic desktop Outlook (2016 and later) and new Outlook for Windows must render as designed; rounded corners and Lato may fall back.
- Q: Must the HTML part stay under Gmail's clipping limit (~102 KB) at the maximum listed changes? → A: Yes: under 100 KB at the maximum listed changes, checked by an automated test.
- Q: The wordmark's magenta "Know" on #f0f0f0 is ~4.0:1; how is it treated under the 4.5:1 rule? → A: The wordmark is a logotype, exempt from the contrast rule; it keeps #e20e80 on #f0f0f0.
- Q: What counts as proof of rendering in the supported apps before shipping? → A: Mailpit's HTML Check (per-client support report) for every email type, plus browser screenshots at the listed widths and zoom, plus the automated checks; no paid preview service.
- Q: Calendar values (descriptions especially) have no length cap, so 50 long changes can exceed 100 KB. How does FR-022 hold? → A: The HTML part shortens each calendar value in a change row to at most 300 characters, ending in "…"; the plain-text part keeps the full values. (Cap superseded by the next answer.)
- Q (analysis): 300 characters does not hold the limit, because encoding can make one character 6 bytes (`"` → `&quot;`) and 50 changes in 50 calendars at 300 bytes per value add up to about 108 KB. → A: The cap is measured on the encoded HTML: each calendar value in a change row, and each calendar name, takes at most 200 bytes (UTF-8) including the "…"; this replaces the 300-character cap.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Timetable changes are easy to scan (Priority: P1)

A student opens a change notification on their phone or laptop. They see at a glance that it comes from
InTheKnow, how many changes there are, which calendar each change belongs to, and per change what happened
(removed, added, moved room, new time), when, and what it was before and after.

**Why this priority**: change notifications are the email students get most often and the reason InTheKnow
exists; today they are plain text with a green bar and are hard to scan.

**Independent Test**: send a change notification with changes of every kind (added, removed, time, rooms, a low
priority change) across two calendars, plus more than the listed maximum, and open it in each supported email app.

**Acceptance Scenarios**:

1. **Given** a notification with changes in two calendars, **When** the student opens it, **Then** they see the
   wordmark, a heading with the same text as the subject, and one section per calendar with its name and its
   "notification settings for this calendar" link.
2. **Given** a removed, an added, a room change and a time change, **When** the student reads them, **Then** each
   row shows a coloured label with the change kind (red for removed, green for added, amber for field changes),
   the event name in bold, the date and time, the old → new value (or where the event is/was for added and
   removed), and when it was detected.
3. **Given** a low priority change, **When** the student reads it, **Then** its label is grey, the whole row is
   muted and it says "low priority".
4. **Given** more changes than are listed, **When** the student reaches the end, **Then** they see "and N more
   changes" set apart in a light grey box, followed by the existing footer text below the card.

---

### User Story 2 - Account and confirmation emails have one clear action (Priority: P2)

Someone receives an email that asks them to do one thing (confirm notifications for this address, reset a
password, confirm a new email address, accept an invitation, confirm an account). The main action is a clear
button; secondary actions stay text links.

**Why this priority**: these emails are rarer but each one blocks a task (signing up, getting back into an
account) until the person acts on it.

**Independent Test**: trigger a confirmation email and each account email that has a link, and open them in each
supported email app.

**Acceptance Scenarios**:

1. **Given** a confirmation email, **When** it is opened, **Then** "confirm notifications for this address" is a
   magenta button, "this wasn't me" is a text link under it, and the expiry note follows in muted text.
2. **Given** an account email with an action (e.g. password reset), **When** it is opened, **Then** the action is a
   magenta button, and under it a short line offers the full link to copy for when the button does not work.
3. **Given** an account email without an action (e.g. password changed), **When** it is opened, **Then** it shows
   the same layout with no button and no fallback line.
4. **Given** any account email, **When** it is opened, **Then** the existing account footer text is shown below
   the card.

---

### User Story 3 - The test notification looks like the real thing (Priority: P3)

A student sends themselves a test notification from their settings and sees the same layout as a real
notification, so they know what to expect.

**Why this priority**: low volume, but it is the first InTheKnow email many students see.

**Independent Test**: send a test notification and open it.

**Acceptance Scenarios**:

1. **Given** a test notification, **When** it is opened, **Then** it shows the wordmark, the subject as heading,
   the test text and the notification settings link in the same layout as the other emails.

---

### Edge Cases

- A very long event name, calendar name, room list or a long unbroken value (e.g. a URL in a description) wraps
  inside the card and never causes sideways scrolling (classic desktop Outlook: see FR-018).
- Calendar text containing markup characters (`<`, `>`, `&`, quotes) is shown literally, never as markup.
- A time change with a before and after on different days shows both on their own line without overflowing a
  320px wide screen.
- An email app that ignores the phone-only styling (e.g. Gmail with a non-Gmail account) still shows the email
  without sideways scrolling at 320px; it only keeps the wider desktop padding.
- An email app that forces dark mode may recolour the email; the text must stay readable (no text made invisible
  on its background). Checked by eye in one app or browser that forces dark mode.
- An email app that does not have the Lato font shows the fallback font without layout breaking.
- iOS must not turn dates, times or room numbers in change rows into tappable links.
- Both languages (Dutch and English) fit the layout, including the longer Dutch labels ("TOEGEVOEGD",
  "SOORT ACTIVITEIT").

## Requirements *(mandatory)*

### Functional Requirements

**Scope and contract**

- **FR-001**: The new layout MUST apply to every email InTheKnow sends: change notifications, the notification
  address confirmation, the test notification and every account email.
- **FR-002**: The existing message contract MUST stay as it is: a plain-text part first and an HTML part second;
  the HTML uses inline styles only (a small optional block of phone-only styling is allowed as an enhancement) and
  has no images, scripts or remote resources (including web fonts); every inserted value is HTML-encoded.
- **FR-003**: The plain-text part, subjects, headers (including the unsubscribe headers) and recipients MUST NOT
  change.
- **FR-004**: All texts MUST come from the existing translated texts (plus the one new text in FR-016), in the
  recipient's language (Dutch or English). Calendar text is shown as recorded and never translated. The HTML declares the email's language.

**Shared layout**

- **FR-005**: Every email MUST show, from top to bottom: a light grey page background (#f0f0f0); the type-only
  wordmark "InThe" in ink (#1d1d1b) followed by "Know" in magenta (#e20e80), set in heavy Lato with a sans-serif
  fallback, no image; a white card with a thin grey border (#e3e3e3), almost square corners (2px) and a 4px
  magenta bar along its top; the existing footer text for that email type, if it has one, small (12px) and grey
  (#5a5a5a) below the card.
- **FR-006**: The card MUST be at most 600px wide, centred in the window, and shrink with narrower windows.
- **FR-007**: The card MUST open with a heading whose text is the email's subject (heavy, 28px; 24px on phones).
- **FR-008**: Body text MUST be 16px with 1.5 line height in grey (#3d3d3d), in Lato with a Helvetica/Arial
  fallback; links MUST be magenta (#c70b70) and underlined.
- **FR-009**: Inside the card the side padding MUST be 32px, reduced to 20px on phones where the email app
  supports phone-only styling.

**Change notifications**

- **FR-010**: Changes MUST be grouped per calendar, in the existing order: the calendar name as a section heading
  and under it the "notification settings for this calendar" link.
- **FR-011**: Each change MUST be its own row, separated from the next by a thin grey line, showing: a label with
  the change kind; the event name in bold; the event's date and time in a monospaced font (before → after for a
  time change); what changed (old → new value, or where the event is/was for added and removed; omitted for a time
  change, whose label already says "time"); and "detected at" with the time, small and muted.
- **FR-012**: The change label MUST use the existing translated kind name in small bold capitals, coloured by
  kind: added = green text #16673a on #e3f4ea; removed = red text #a50d26 on #fbe3e7; any field change (time,
  rooms, location, teachers, name, …) = amber text #7a4a00 on #fdf0d9.
- **FR-013**: A low priority change MUST have a grey label (#5a5a5a on #f0f0f0), muted text throughout the row and
  the "low priority" text next to the event name.
- **FR-014**: When not all changes are listed, the "and N more changes" text MUST appear after the last section,
  in a light grey (#f7f7f7) box inside the card.

**Actions**

- **FR-015**: The main action of an email MUST be a button: magenta background (#e20e80), white bold text, 4px
  corners, at least 44px tall. This applies to the confirmation email's confirm link and to every account email
  that has an action link. "This wasn't me" in the confirmation email and the settings links in change and test
  emails stay text links.
- **FR-016**: Account emails with an action button MUST show, under the button, a short muted line with the full
  link as text, so it can be copied when the button does not work. This line is a new translated text in Dutch
  and English, in the HTML part only; the plain-text part already shows the full link and stays unchanged.

**Email-app behaviour**

- **FR-017**: The email MUST use only HTML and CSS that these apps support or that has a listed fallback, so each
  shows the card, wordmark, labels and button, centred: Outlook for Windows (both classic desktop Outlook 2016 and
  later, which uses Word's rendering engine, and new Outlook), Outlook on the web/Mac, Gmail (web and apps) and
  Apple Mail (macOS and iOS). Where an app cannot show a detail (rounded corners, label padding, the Lato font) it
  MUST fall back without breaking the layout or readability. Proof is the check in SC-001, not a render in each app.
- **FR-018**: The email MUST fit without sideways scrolling at any window width from 320px up, and at 200% text
  zoom. Exception: in classic desktop Outlook the card is a fixed 600px and a long unbroken value may widen it,
  because Word's engine supports neither `max-width` nor word breaking; it is a desktop app, so phone widths do not
  apply there.
- **FR-019**: The email MUST ask email apps to show it in light mode.
- **FR-020**: The email MUST stop iOS from turning dates, times, addresses and numbers into links.
- **FR-021**: Every text and label colour MUST have at least 4.5:1 contrast against its background. The wordmark
  is a logotype and is exempt (it keeps #e20e80 on #f0f0f0, about 4.0:1).
- **FR-022**: The HTML part of a change notification MUST stay under 100 KB (below Gmail's ~102 KB clipping
  limit) when the maximum number of changes is listed, including with long values, Dutch texts, one calendar per
  change, and text whose characters grow when encoded (`"`, `&`, `<`, emoji, non-Latin scripts). To make this hold
  for any calendar text, the HTML part shortens each calendar value in a change row (event name, old and new value,
  where the event is/was) and each calendar name so that its encoded HTML is at most 200 bytes (UTF-8), the "…"
  included, without cutting a character or an entity in half; the plain-text part keeps the full values (FR-003).

### Key Entities

No new data is stored. The email is rendered from the existing message (kind, language, time zone, calendar
groups with their changes, links), as described in the notification channels message contract.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Every email type shows the wordmark, the card, the heading and (where it has one) the button, with no
  sideways scrolling at 320px, 375px, 768px and 1440px wide and at 200% zoom, shown by screenshots; and Mailpit's
  HTML Check reports no unsupported feature for the clients in FR-017 that is not covered by an intended
  fallback (FR-017).
- **SC-002**: In a change notification, a reader can tell the kind of each change (added, removed, field change,
  low priority) from its label alone, without reading the row text.
- **SC-003**: 100% of text/background colour pairs in the email, except the wordmark (logotype), meet 4.5:1
  contrast.
- **SC-004**: The plain-text part of every email type is byte-for-byte the same as before this feature, for the
  same message.
- **SC-005**: Every value from the calendar or user input that contains markup characters is shown literally in
  the HTML part (no injected markup), for every email type.
- **SC-006**: Every new or changed text exists in both Dutch and English.
- **SC-007**: An automated check confirms the HTML part of a change notification with the maximum listed changes
  (long values that grow when encoded, one calendar per change, Dutch) is under 100 KB.

## Assumptions

- The public `/n/…` pages (confirm/decline/unsubscribe in the browser) keep their current minimal look; restyling
  them is a separate change.
- No dark-mode version is designed; the email asks for light mode, and apps that force dark mode (Gmail app,
  Outlook) recolour it themselves. Readability there is checked, exact colours are not.
- Lato is not embedded (no remote resources); readers without Lato installed see Helvetica or Arial.
- Client support is verified with Mailpit (already in the dev stack): its HTML Check reports per client which used
  HTML/CSS features are unsupported, based on caniemail data. It does not render as each client, so layout is
  verified with screenshots at the listed widths and zoom; no paid preview service is used. The automated checks
  cover structure, encoding, contrast, size and the unchanged plain-text part.
- The colours are taken from the InTheKnow design system; the label text colours are darker than the system's
  status colours because the system's ones do not reach 4.5:1 at label size. The design system may be updated to
  match later.
- The sample events, rooms and names on the design canvas are illustrative only.
