---

description: "Task list for the branded email layout"
---

# Tasks: Branded email layout

**Input**: Design documents from `/specs/007-branded-email-layout/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/email-html.md, quickstart.md

**Tests**: Requested by the plan (snapshot, encoding, palette/contrast, size, truncation) and by SC-003–SC-007.

**Organization**: Grouped by user story. Almost all work is in one file (`backend/InTheKnow/EmailChannel.cs`,
class `Renderer`), so few tasks are parallel.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: US1 changes, US2 account and confirmation, US3 test notification

---

## Phase 1: Setup (pin today's behaviour)

**Purpose**: Freeze the plain-text part, subject and headers before the renderer is touched (SC-004, plan ordering note 1).

- [X] T001 Add a snapshot test in `backend/InTheKnow.Tests/EmailChannelTests.cs` (test `TextPartMatchesSnapshot`) that renders, through the same `EmailChannel` path the existing tests use, each fixture in `en` and `nl`: a changes email with an added, a removed, a time change (before and after on different days), a rooms change, a low priority change, two calendars and `NotListed > 0`; a confirmation email; a test email; every account kind listed in `backend/InTheKnow.Tests/AccountEmailTests.cs` (lines 18–19), with `roleChanged` for `role=admin` and `role=user` and `invite` for `admin=true` and `admin=false`. For each fixture it writes the decoded plain-text part, the subject and the headers (`List-Unsubscribe`, `List-Unsubscribe-Post`, `To`, `From`; skip `Message-ID`, it is random) to one file per fixture under `backend/InTheKnow.Tests/EmailSnapshots/{fixture}.{lang}.txt` when the file is missing, and compares byte-for-byte when it exists. Use fixed URLs, dates and time zone so the output is deterministic.
- [X] T002 Run the test once on the unchanged code to generate `backend/InTheKnow.Tests/EmailSnapshots/*.txt`, make sure the test project copies or reads them from the source folder (not `bin/`), inspect a few by eye, and commit them before any renderer change. After this, a missing snapshot file must fail the test instead of writing one (flip the write-if-missing behaviour off, or guard it behind an env var such as `INTHEKNOW_UPDATE_SNAPSHOTS=1`).

**Checkpoint**: the snapshot test passes on the old renderer; it guards every later task.

---

## Phase 2: Foundational (shared shell, palette, texts)

**Purpose**: The document shell, card and footer that every email kind uses (FR-002, FR-004–FR-009, FR-017–FR-021).

**⚠️ CRITICAL**: All user stories render inside this shell.

- [X] T003 [P] Add the text key `account.linkFallback` to `backend/InTheKnow/Texts.cs`: en "Button not working? Copy this link into your browser:", nl "Werkt de knop niet? Kopieer deze link naar je browser:" (FR-016, SC-006; `LanguagesHaveTheSameKeys` covers both).
- [X] T004 [P] Change `const int Listed = 50;` in `backend/InTheKnow/Notifier.cs` from private to `internal const int Listed = 50;` so the size test can use it (check `InternalsVisibleTo` already exposes internals to `InTheKnow.Tests`; add it to the project file if not).
- [X] T005 Add the palette as named constants in `backend/InTheKnow/EmailChannel.cs` (internal so tests can read them): `Ink #1d1d1b`, `Magenta #e20e80`, `LinkColor #c70b70`, `Body #3d3d3d`, `Muted #5a5a5a`, `Page #f0f0f0`, `Card #ffffff`, `Border #e3e3e3`, `Box #f7f7f7`, label pairs added `#16673a/#e3f4ea`, removed `#a50d26/#fbe3e7`, field change `#7a4a00/#fdf0d9`, low priority `#5a5a5a/#f0f0f0`; plus an internal list of the text/background pairs from research.md R9 (wordmark "Know" `#e20e80` on `#f0f0f0` marked exempt).
- [X] T006 Replace the one-line `<!DOCTYPE html><html …><body …>{html}</body></html>` wrapper in `EmailChannel.cs` (the `AlternateView` for `text/html`) with a `Document(subject, body, footer)` helper that writes the shell from contracts/email-html.md "Document": `<html lang="{en|nl}">`; head with meta charset utf-8, viewport, `x-apple-disable-message-reformatting`, `format-detection` `telephone=no,date=no,address=no,email=no`, `color-scheme` light, `supported-color-schemes` light; one `<style>` with `@media (max-width:480px)` (class for content cell padding 20px, class for h1 24px), `:root{color-scheme:light;supported-color-schemes:light}`, `a[x-apple-data-detectors]{color:inherit!important;text-decoration:none!important;font:inherit!important}`; an `<!--[if mso]><style>body,table,td,a{font-family:Arial,sans-serif}</style><![endif]-->`; `<body bgcolor="#f0f0f0" style="margin:0;padding:0;background-color:#f0f0f0">`; outer `<table role="presentation" width="100%" bgcolor="#f0f0f0">`; the wordmark row ("InThe" in ink + "Know" in magenta, `font-family:Lato,Helvetica,Arial,sans-serif;font-weight:900`, centred, no image); the MSO ghost table `<!--[if mso]><table role="presentation" width="600" align="center"><tr><td><![endif]-->`; the card table (`width="100%"`, `style="max-width:600px"`, `bgcolor="#ffffff"`, `border:1px solid #e3e3e3;border-radius:2px`, centred); a 4px bar row (`<td height="4" bgcolor="#e20e80" style="font-size:0;line-height:0">`); the content cell (padding 32px, phone class, `font-family:Lato,Helvetica,Arial,sans-serif;font-size:16px;line-height:1.5;color:#3d3d3d;word-break:break-word;overflow-wrap:anywhere`); `<h1>` with the encoded subject (`Subject(m)`), 28px, weight 900, ink, phone class; then the body; then, if a footer is given, a row below the card with the footer in 12px `#5a5a5a`. No element below the card has a fixed width (R5). Colours go both as `bgcolor` and inline `background-color` where Outlook needs it (R1).
- [X] T007 In the `Renderer` in `EmailChannel.cs`: restyle `Paragraph` HTML (`margin:0 0 16px`) and `Link` HTML (link `color:#c70b70;text-decoration:underline`), leaving both text sides exactly as they are. Add `Footer(string s)` that appends to `text` exactly what `Paragraph` appends today (`s + "\n\n"`) and writes the HTML to a separate footer `StringBuilder` passed to `Document` (R7). Add an HTML-only `ButtonHtml(label, url)` helper per research.md R3: one-cell `<table role="presentation">`, `<td bgcolor="#e20e80" style="border-radius:4px;mso-padding-alt:12px 24px">`, `<a href=… style="display:inline-block;padding:12px 24px;line-height:20px;color:#ffffff;font-weight:700;text-decoration:none">` (44px tall). Every inserted value stays encoded through `E(...)`.
- [X] T008 Add the encoding test in `backend/InTheKnow.Tests/EmailChannelTests.cs` (extend `CalendarTextIsEncoded` and `ArgsAreEncoded` in `backend/InTheKnow.Tests/AccountEmailTests.cs`): for every kind, a value containing `<b>x</b> & "q" 'a'` (calendar name, event name, values, account args) appears encoded in the HTML part and the raw `<b>` never appears (SC-005).
- [X] T009 Add the palette and shell tests in `backend/InTheKnow.Tests/EmailChannelTests.cs`: (a) every declared text/background pair from T005, except the exempt wordmark, has WCAG 2.x contrast ≥ 4.5 (compute relative luminance in the test, ~10 lines); (b) for every fixture from T001, every `#[0-9a-fA-F]{6}` found in the HTML part is one of the palette constants (SC-003, FR-021); (c) for every fixture from T001, the HTML part has `<html lang="{fixture language}">`, the `format-detection` meta with `telephone=no,date=no,address=no,email=no`, the `color-scheme` and `supported-color-schemes` light metas, `max-width:600px` on the card and the MSO ghost table at `width="600"` (FR-004, FR-006, FR-019, FR-020).

**Checkpoint**: shell in place; snapshot test still passes (text unchanged).

---

## Phase 3: User Story 1 - Timetable changes are easy to scan (Priority: P1) 🎯 MVP

**Goal**: Change notifications render as colour-coded rows grouped per calendar inside the card.

**Independent Test**: render a changes email with every kind across two calendars and `NotListed > 0`; check structure in tests and look at it in Mailpit.

### Tests for User Story 1

- [X] T010 [US1] Rewrite the HTML assertions in `RendersParts`, `AddedAndRemovedEvents`, `MultipleCalendarsSubject`, `DutchChangesEmail`, `MoreThanFifty`, `TimeZones` and `SettingsLinksAndNoActionLinks` in `backend/InTheKnow.Tests/EmailChannelTests.cs` so they assert structure instead of joined strings like `Rooms: AL00.A1.38 → AL00.A1.20` in HTML (plan ordering note 3): `<h1>` contains the subject; one `<h2>` per calendar in order followed by its settings link; each row has the label text with its colour pair (added green, removed red, field amber, low priority grey), the event name in `<strong>`, the date in a monospace element, old and new value present separately, "detected at" in muted text; time change rows have no "what" line; the "and N more changes" text sits in a `#f7f7f7` box; `changes.footer` comes after the card's closing. Keep the existing text-part assertions unchanged.
- [X] T011 [P] [US1] Add `LongValuesAreShortenedInHtml` in `backend/InTheKnow.Tests/EmailChannelTests.cs`: an event name, old/new value, room list and calendar name of 1000 characters each appear in the HTML as an encoded prefix ending in "…" whose UTF-8 byte count (prefix + "…") is ≤ 200, and in full in the text part (FR-022). Cases: plain ASCII (prefix is 197 characters), `"&<>'` repeated (no entity cut: the HTML around the value still parses, the prefix ends in a whole `&…;`), emoji and CJK (no surrogate pair cut); a value that already fits is not shortened.
- [X] T012 [P] [US1] Add `ChangesHtmlStaysUnder100KB` in `backend/InTheKnow.Tests/EmailChannelTests.cs`: `Notifier.Listed` changes in `nl`, each in its own calendar with a 1000-character name, mixed kinds (added, removed, time, rooms, description) and low priority, every calendar value 1000 characters of text that grows when encoded (a mix of `"&<>'`, emoji and CJK), `NotListed > 0`; assert `Encoding.UTF8.GetByteCount(html) < 100_000` on the decoded HTML string (SC-007, research.md R8). If it fails, slim the row markup; do not raise the 200-byte cap.

### Implementation for User Story 1

- [X] T013 [US1] Add an HTML-only `Short(string s)` helper in the `Renderer` in `EmailChannel.cs` that returns the *encoded* value: `E(s)` when its UTF-8 byte count is ≤ 200, else the encoded text elements of `s` (walk with `StringInfo.GetTextElementEnumerator`, sum each element's encoded byte count) up to 197 bytes followed by "…" — so no character or entity is cut (R8). Use it instead of `E(...)` for event name, old value, new value and where in `Change`, and for the calendar name in `Calendar`'s `<h2>` (T014).
- [X] T014 [US1] Rewrite `Calendar(CalendarGroup g)` HTML in `EmailChannel.cs`: `<h2>` with the calendar name through `Short` (T013) (ink, bold, ~20px, top margin), then the settings text link (`changes.settings`, magenta underlined, 14px). The text side stays exactly as it is.
- [X] T015 [US1] Rewrite `Change(ChangeItem item)` HTML in `EmailChannel.cs` per contracts/email-html.md "Change row", keeping the text side exactly as it is: split the HTML into parts computed alongside today's `When`/`What` strings (do not change those strings, they feed the text part): label = `kind.added` / `kind.removed` / `kind.time` / `KindName(field)` in small bold capitals (`text-transform:uppercase;font-size:12px;font-weight:700;letter-spacing`, padding 2px 6px) with the colour pair from T005 (low priority overrides to grey); event name shortened, bold, ink; when low priority, `changes.lowPriority` muted after the name; date line in `font-family:Menlo,Consolas,'Courier New',monospace` (for a time change the shortened before → after); what line = shortened old → new for a field change, shortened where for added/removed (omit the label prefix, the label already says it), omitted for a time change; "detected at" 12px `#5a5a5a`. Rows separated by `border-top:1px solid #e3e3e3`; a low priority row has all text in `#5a5a5a`. Use table cells or block elements, no fixed widths.
- [X] T016 [US1] In the `MessageKind.Changes` case of `EmailChannel.cs`: write `changes.more` as text exactly as today (`Paragraph` text side) but in HTML inside a `#f7f7f7` box (padding 12px 16px, `bgcolor` + background) after the last section; write `changes.footer` through `Footer(...)` so it lands below the card. Pass the body and footer builders to `Document`.

**Checkpoint**: US1 tests, snapshot, encoding and palette tests pass.

---

## Phase 4: User Story 2 - Account and confirmation emails have one clear action (Priority: P2)

**Goal**: The main action is a magenta button; account buttons get a copyable fallback link line.

**Independent Test**: render the confirmation and every account kind; check button, fallback line and footer in tests and Mailpit.

### Tests for User Story 2

- [X] T017 [P] [US2] Update `ConfirmationEmail` in `backend/InTheKnow.Tests/EmailChannelTests.cs`: the HTML has one button (cell `bgcolor="#e20e80"`) whose `href` is `ConfirmUrl` and text `confirmation.confirm`; `confirmation.decline` is a text link to `DeclineUrl` after it; `confirmation.expiry` in `#5a5a5a`; no footer.
- [X] T018 [P] [US2] Update `EveryKindRendersInBothLanguages` (and add assertions where needed) in `backend/InTheKnow.Tests/AccountEmailTests.cs`: a kind with `Link` has one button with `account.{kind}.action`, then `account.linkFallback` followed by an `<a>` whose text and `href` are the full link; a kind without `Link` has no button and no fallback text; `account.footer` appears after the card in every account email.

### Implementation for User Story 2

- [X] T019 [US2] In the `MessageKind.Confirmation` case of `EmailChannel.cs`: the confirm link keeps the text format `label: url` (today's `Link` text side) but renders `ButtonHtml` in HTML (plan ordering note 2, R3 trap) — e.g. a `Link(label, url, button: true)` overload or a `PrimaryLink` method that appends the `Link` text and the button HTML. Decline stays `Link`; expiry becomes a muted paragraph (HTML `color:#5a5a5a`, text unchanged).
- [X] T020 [US2] In the `Renderer.Button` and `MessageKind.Account` case of `EmailChannel.cs`: `Button` keeps its text `label:\nurl` and writes `ButtonHtml` plus, HTML-only, a muted 14px paragraph `account.linkFallback` + an encoded `<a>` with the full link as text (`word-break` inherited so it wraps). `account.footer` goes through `Footer(...)` so it lands below the card.
- [X] T021 [P] [US2] Check `specs/004-accounts-login/contracts/account-emails.md` for wording about the HTML shape (button, footer placement); amend only if it describes the HTML, linking to `specs/007-branded-email-layout/contracts/email-html.md` (plan ordering note 4).

**Checkpoint**: US1 and US2 tests pass; snapshot unchanged.

---

## Phase 5: User Story 3 - The test notification looks like the real thing (Priority: P3)

**Goal**: The test email uses the same shell.

**Independent Test**: render a test email in both languages.

- [X] T022 [US3] Update `TestEmail` in `backend/InTheKnow.Tests/EmailChannelTests.cs`: HTML has the wordmark, `<h1>` with the subject, `test.body`, and a magenta text link (not a button) `test.settings` → `SettingsUrl`; no footer.
- [X] T023 [US3] Confirm the `MessageKind.Test` case in `EmailChannel.cs` renders through `Document` with no footer; no other change should be needed after Phase 2.

**Checkpoint**: all kinds render in the new layout.

---

## Phase 6: Polish & Cross-Cutting Concerns

- [X] T024 [P] Amend the "Body" row of `specs/003-notification-channels/contracts/messages.md`: the HTML part may contain one optional `<style>` block and one MSO-only `<style>`; link to `specs/007-branded-email-layout/contracts/email-html.md`.
- [X] T025 [P] Add `backend/InTheKnow.Tests/EmailPreviewTests.cs`: one test that is skipped unless `INTHEKNOW_PREVIEW_SMTP_PORT` is set (xUnit v3 `Assert.Skip` / `SkipUnless`), and then sends every T001 fixture in `en` and `nl` through `EmailChannel.SendAsync` to `localhost:{port}` with a `@example.test` recipient; reuse the T001 fixture builder rather than copying it.
- [X] T026 Run the automated checks from `specs/007-branded-email-layout/quickstart.md` (`dotnet test backend/InTheKnow.Tests --filter "FullyQualifiedName~EmailChannelTests|FullyQualifiedName~AccountEmailTests"`) and the full test suite; all pass, snapshot files unchanged in `git diff`.
- [ ] T027 (done except the forced dark-mode check, which needs a real client) Run the Mailpit steps from `specs/007-branded-email-layout/quickstart.md`: `docker compose up -d mailpit`, run the preview test, then for every email type check Mailpit's HTML Check (verify the API path in the running Mailpit) — every flag must appear in the fallback table of research.md R10; take screenshots of the raw HTML at 320, 375, 768 and 1440px and at 200% zoom (640px, browser zoom 200%): wordmark, card, heading, labels and button visible, no sideways scroll, long values wrap (SC-001, FR-018). Then open one changes email and one account email with forced dark mode (e.g. Chromium's `--enable-features=WebContentsForceDark`, or the Gmail app) and confirm no text becomes unreadable on its background (dark-mode edge case).
- [X] T028 Fix whatever T026/T027 turned up in `backend/InTheKnow/EmailChannel.cs` (keeping the text snapshot unchanged) and re-run T026.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (T001–T002)**: first; snapshots must be committed before any change to `EmailChannel.cs`.
- **Foundational (T003–T009)**: after Setup; blocks all stories. T003 and T004 are in other files and can run in parallel with T005–T007.
- **US1 (T010–T016)**, **US2 (T017–T021)**, **US3 (T022–T023)**: after Foundational. They all edit `EmailChannel.cs`, so in practice run them in order P1 → P2 → P3.
- **Polish (T024–T028)**: T024, T025 any time after Foundational; T026–T028 last.

### User Story Dependencies

- **US1**: needs only Foundational.
- **US2**: needs only Foundational (`ButtonHtml`, `Footer`, `account.linkFallback`).
- **US3**: needs only Foundational.

### Within Each User Story

- Update the story's tests first and watch them fail, then change the renderer.
- The snapshot test (T001) must pass after every task.

### Parallel Opportunities

- T003 ∥ T004 ∥ T005 (different files).
- T011 ∥ T012 (new tests, no shared code beyond fixtures).
- T017 ∥ T018 (different test files); T021 ∥ any code task.
- T024 ∥ T025 ∥ story work.

---

## Parallel Example: User Story 1

```bash
Task: "Add LongValuesAreShortenedInHtml in backend/InTheKnow.Tests/EmailChannelTests.cs"
Task: "Add ChangesHtmlStaysUnder100KB in backend/InTheKnow.Tests/EmailChannelTests.cs"
```

(Both touch the same test file; parallel only if written as separate edits that do not overlap.)

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Phase 1: snapshot the text part and commit it.
2. Phase 2: shell, palette, footer, button helper.
3. Phase 3: change rows.
4. **STOP and VALIDATE**: run the tests, send a changes email to Mailpit and look at it.

### Incremental Delivery

1. Setup + Foundational → every email already sits in the branded shell.
2. US1 → change notifications scan well (MVP).
3. US2 → buttons and fallback lines.
4. US3 → test email checked.
5. Polish → contracts, preview test, Mailpit check, screenshots.

---

## Notes

- The text part, subjects and headers must not change: the snapshot test is the authority (SC-004).
- No new package, template engine, CSS inliner or VML (plan Constitution Check).
- Commit after each task or logical group.
