---

description: "Task list for 008 Unsubscribe link in the email footer"
---

# Tasks: Unsubscribe link in the email footer

**Input**: Design documents from `/specs/008-email-unsubscribe-link/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/email-footer.md, quickstart.md

**Tests**: Included. The constitution requires an automated check for non-trivial logic, and the plan lists the
test changes (contracts/email-footer.md "Guarantees").

**Organization**: Tasks are grouped by user story. Backend-only; paths are relative to the repository root.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1, US2)

---

## Phase 1: Setup

None. No new project, package, schema or configuration (plan.md Technical Context).

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: the footer text and the renderer's footer-link line, used by both stories.

- [X] T001 [P] Rewrite `changes.footer` in `backend/InTheKnow/Texts.cs` (en block line ~27, nl block line ~123) to the
  research R6 text, verbatim:
  - en: "You receive this email because this address was confirmed for InTheKnow timetable notifications. To stop
    these notifications to this address, use the link below or your mail client's unsubscribe option."
  - nl: "Je krijgt deze e-mail omdat dit adres is bevestigd voor roostermeldingen van InTheKnow. Gebruik de link
    hieronder of de afmeldoptie van je mailprogramma om deze meldingen naar dit adres te stoppen."
  Do not add a new label key; the link label reuses `page.unsubscribe.title` ("Unsubscribe" / "Afmelden").
- [X] T002 [P] Add `public void FooterLink(string label, string url)` to `Renderer` in
  `backend/InTheKnow/EmailChannel.cs`, next to `Footer(string s)`. Text part: `{label}: {url}\n\n` (same format as
  `Renderer.Link`, research R5). HTML part, appended to the `footer` builder (not `html`), exactly per
  contracts/email-footer.md:
  `<p style="margin:0 0 12px"><a href="{E(url)}" style="color:{Muted};text-decoration:underline">{E(label)}</a></p>`.
  Do **not** call `Anchor()`: it hardcodes `LinkColor`, and `(LinkColor, Page)` is not in `TextPairs`; `(Muted, Page)`
  is (plan ordering note 2, FR-005). No logging of the URL (FR-008).

**Checkpoint**: footer text and footer-link renderer exist; no email uses the link yet.

---

## Phase 3: User Story 1 - Unsubscribe from the bottom of a change email (Priority: P1) 🎯 MVP

**Goal**: every change email has the footer sentence followed by an unsubscribe link to `Message.UnsubscribeUrl`,
in both parts and both languages.

**Independent Test**: render a Changes message; the footer link's `href` equals the `List-Unsubscribe` value
without `<` `>`; following it with GET leaves the channel active, POST unsubscribes it (quickstart.md manual
steps 3–5 for the change email).

### Tests for User Story 1

- [X] T003 [US1] Rewrite `SettingsLinksAndNoActionLinks` in `backend/InTheKnow.Tests/EmailChannelTests.cs` (do not
  delete it): replace the comment "the only /n/ URL is the List-Unsubscribe header" with the amended 003 SC-003 rule
  "the only /n/ URL in either part is exactly UnsubscribeUrl"; replace the
  `"unsubscribe option to stop these notifications"` check with a check for `Texts.Get("en", "changes.footer")`
  (HTML: encoded) and for the link; change the `Url().Matches` assertion so every URL is either a
  `/calendars/[12]/notifications` settings URL or exactly the unsubscribe URL; assert neither part contains
  `/n/confirm` or `/n/decline`. Keep `AssertFooterBelowCard(html, Texts.Get("en", "changes.footer"))` unchanged
  (it reads the first footer `<p>`, which stays the sentence).
- [X] T004 [US1] Add a `[Theory]` `FooterLinkMatchesListUnsubscribe` with `[InlineData("en")]`/`[InlineData("nl")]`
  for `MessageKind.Changes` in `backend/InTheKnow.Tests/EmailChannelTests.cs`: the HTML part contains
  `<a href="{header without < >}" style="color:{EmailChannel.Muted};text-decoration:underline">{page.unsubscribe.title}</a>`
  after the footer sentence, and the text part contains `{page.unsubscribe.title}: {header without < >}` after
  `changes.footer` (FR-001, FR-002, FR-004). Write it so US2 can add `MessageKind.Test` rows (T008).
- [X] T005 [P] [US1] In `DeclineAndUnsubscribePages` in `backend/InTheKnow.Tests/PublicPagesTests.cs`, confirm the
  existing assertion that GET on the unsubscribe URL leaves channel B `active` (line ~125); add `SC-003 (008)` and
  `FR-003 (008)` to the test's leading comment. Only add a new assertion if that GET check is missing.

### Implementation for User Story 1

- [X] T006 [US1] In `EmailChannel.Build` (`backend/InTheKnow/EmailChannel.cs`), `case MessageKind.Changes:` add
  `r.FooterLink(r.T("page.unsubscribe.title"), m.UnsubscribeUrl!);` directly after
  `r.Footer(r.T("changes.footer"));` (sentence first, plan ordering note 1). Leave the `List-Unsubscribe` header code
  unchanged (FR-006).
- [X] T007 [US1] Regenerate snapshots for Changes:
  `cd backend && INTHEKNOW_UPDATE_SNAPSHOTS=1 dotnet test --filter TextPartMatchesSnapshot`; check that
  `backend/InTheKnow.Tests/EmailSnapshots/changes.en.txt` and `changes.nl.txt` end with the new footer sentence, a
  blank line, and `Unsubscribe: …/n/unsubscribe/…` / `Afmelden: …`. Then `dotnet test` green.

**Checkpoint**: change emails carry the footer link; US1 is shippable on its own.

---

## Phase 4: User Story 2 - The same link in the test email (Priority: P2)

**Goal**: the test email gets the same footer sentence and unsubscribe link below the card.

**Independent Test**: render a Test message in en and nl; footer below the card, link `href` equals the
`List-Unsubscribe` value (quickstart.md manual step 2).

### Tests for User Story 2

- [X] T008 [US2] In `backend/InTheKnow.Tests/EmailChannelTests.cs`: add `MessageKind.Test` rows (en, nl) to
  `FooterLinkMatchesListUnsubscribe` (T004), and rewrite `TestEmail`: the text-part URL list becomes
  `[$"{Frontend}/notifications", $"{Public}/n/unsubscribe/UNSUB"]`; replace the
  `DoesNotContain("max-width:600px;margin:0 auto;padding:16px 4px 0", html); // no footer` line with
  `AssertFooterBelowCard(html, Texts.Get(language, "changes.footer"))`; keep the `DoesNotContain("mso-padding-alt")`
  check. Keep the Confirmation "no footer" assertion elsewhere unchanged (FR-007, SC-005).

### Implementation for User Story 2

- [X] T009 [US2] In `EmailChannel.Build` (`backend/InTheKnow/EmailChannel.cs`), `case MessageKind.Test:` add, after
  the settings link, `r.Footer(r.T("changes.footer"));` then
  `r.FooterLink(r.T("page.unsubscribe.title"), m.UnsubscribeUrl!);` (contracts/email-footer.md "Which emails").
- [X] T010 [US2] Regenerate snapshots as in T007, then run
  `git diff --stat backend/InTheKnow.Tests/EmailSnapshots/`: it must list exactly `changes.en.txt`, `changes.nl.txt`,
  `test.en.txt`, `test.nl.txt` (FR-007: no confirmation or account snapshot changed). `dotnet test` green, including
  the `TextPairs` contrast test.

**Checkpoint**: both change and test emails carry the footer link.

---

## Phase 5: Polish & Cross-Cutting Concerns

**Purpose**: amend the 003/007 documents (spec "Amendments to earlier specs"), then validate.

- [X] T011 [P] Amend `specs/003-notification-channels/spec.md`: clarification "Unsubscribing?" (a login-free
  unsubscribe page the email links to, next to the mail client's button), FR-030 (the footer unsubscribe link works
  without login, only for its own channel), SC-003 (the footer unsubscribe link is the one other email link allowed
  to change something without login, only through the page's button). Reference 008.
- [X] T012 [P] Amend `specs/003-notification-channels/contracts/messages.md`: Changes footer mentions the link and
  shows the `{page.unsubscribe.title}: {UnsubscribeUrl}` line; Test gets the same footer; the "Links" line lists the
  unsubscribe link as the one exception. Reference 008.
- [X] T013 [P] Amend `specs/007-branded-email-layout/contracts/email-html.md`: Changes/Test footer rows (footer may
  contain the Muted underlined link; Test now has a footer), and carve the "text part unchanged / byte-for-byte equal
  to the snapshots" guarantee out for this footer change. Reference 008.
- [X] T014 Run `cd backend && dotnet test` (all green) and the quickstart.md manual Mailpit steps 1–6, including the
  Dutch check and the "press again via back button" repeat.
- [X] T015 Commit the feature without the unrelated `.gitignore` change (plan ordering note 6).

---

## Dependencies & Execution Order

### Phase Dependencies

- **Foundational (Phase 2)**: T001 and T002 block both stories.
- **US1 (Phase 3)**: after Phase 2. **US2 (Phase 4)**: after Phase 2; T008 extends the theory from T004, so do US1
  first or create the theory in T008 if US2 is done alone.
- **Polish (Phase 5)**: T011–T013 can be written any time; T014–T015 after both stories.

### Within Each User Story

- Tests (T003–T005, T008) first; they fail until T006/T009 land.
- Snapshot regeneration (T007, T010) last in each story.
- T003, T004, T006, T008, T009 touch the same two files (`EmailChannelTests.cs`, `EmailChannel.cs`): run them in
  order, not in parallel.

### Parallel Opportunities

- T001 (`Texts.cs`) ‖ T002 (`EmailChannel.cs`).
- T005 (`PublicPagesTests.cs`) ‖ T003/T004 (`EmailChannelTests.cs`).
- T011 ‖ T012 ‖ T013 (three different docs).

---

## Parallel Example: User Story 1

```bash
Task: "Rewrite SettingsLinksAndNoActionLinks + add FooterLinkMatchesListUnsubscribe in backend/InTheKnow.Tests/EmailChannelTests.cs"
Task: "Reference SC-003/FR-003 on the GET-leaves-active check in backend/InTheKnow.Tests/PublicPagesTests.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Phase 2 (T001, T002).
2. Phase 3 (T003–T007).
3. **Validate**: `dotnet test` green; Mailpit change email shows the link; GET leaves active, POST unsubscribes.

### Incremental Delivery

1. Phase 2 + US1 → change emails have the link (MVP).
2. US2 → test email gets the footer.
3. Phase 5 → docs agree, quickstart passes, commit.

---

## Notes

- No new page, token, key, dependency or log call (FR-002, FR-008, research R1/R7).
- Account and Confirmation branches of `Build` are not touched (FR-007).
