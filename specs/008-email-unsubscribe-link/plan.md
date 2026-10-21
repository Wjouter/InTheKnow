# Implementation Plan: Unsubscribe link in the email footer

**Branch**: `008-email-unsubscribe-link` (work currently on `main`) | **Date**: 2026-10-21 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/008-email-unsubscribe-link/spec.md`

## Summary

Change and test emails get a footer link to the existing login-free unsubscribe page, the same URL as their
`List-Unsubscribe` header (`Message.UnsubscribeUrl`). The page, its GET-shows/POST-acts split, the token and the
channel states already exist (003); this feature only adds one footer line in `EmailChannel.Build`, rewrites the
`changes.footer` text in English and Dutch, and updates the tests, snapshots and the 003/007 documents that said
there was no such link.

## Technical Context

**Language/Version**: C# on .NET 10 (`global.json`)

**Primary Dependencies**: BCL only; no new package

**Storage**: N/A (no schema change; `channel.unsubscribe_token` is reused)

**Testing**: xUnit v3 (`backend/InTheKnow.Tests`), text-part snapshots in `EmailSnapshots/`, Mailpit for a manual look

**Target Platform**: the email apps from 007; the public `/n/unsubscribe/{token}` page in any browser

**Project Type**: web application (backend change only)

**Performance Goals**: N/A

**Constraints**: the footer link uses `Muted` (#5a5a5a) on `Page` (#f0f0f0), a pair already checked ≥ 4.5:1 in
`TextPairs`; GET on the page must stay side-effect free (FR-003, link scanners); no token or URL in logs (FR-008)

**Scale/Scope**: 2 message kinds (Changes, Test), 2 languages, 1 changed text key

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle / rule | Status |
|---|---|
| I. Simplicity: smallest implementation, no speculative code | PASS: reuses the existing page, token and `UnsubscribeUrl`; one footer line in the renderer; link label reuses `page.unsubscribe.title` |
| I. New dependency or layer justified | PASS: none added |
| II. No new personal data | PASS: nothing stored; the token already exists per channel |
| II. No secrets or deployment details in repo | PASS: tests and quickstart use `example.com`/`localhost` and Mailpit |
| II. Trust boundary / state change | PASS: GET `/n/unsubscribe/{token}` only looks up the language; only POST unsubscribes (unchanged, `NotificationApi.cs`) |
| II. Output encoded by default | PASS: label and URL go through the renderer's `E()` like every other link |
| Tech: GPL-compatible, lockfiles | PASS: no new package |
| Workflow: non-trivial logic has an automated check | PASS: per-kind footer/href assertions, unchanged account/confirmation snapshots, new PublicPagesTests assertion that GET on the unsubscribe page leaves the channel active |

Post-design re-check: PASS (no dependency, layer, page, token or stored data added).

## Project Structure

### Documentation (this feature)

```text
specs/008-email-unsubscribe-link/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/email-footer.md
└── tasks.md             # /speckit-tasks
```

### Source Code (repository root)

```text
backend/InTheKnow/
├── EmailChannel.cs      # Build: Changes and Test add footer + unsubscribe link; Renderer: footer link line (Muted, underlined)
└── Texts.cs             # changes.footer rewritten (en, nl) to mention the link

backend/InTheKnow.Tests/
├── EmailChannelTests.cs # SettingsLinksAndNoActionLinks, TestEmail: footer + link; new href == List-Unsubscribe check
├── PublicPagesTests.cs  # + GET /n/unsubscribe/{token} leaves the channel active (SC-003, link scanners)
└── EmailSnapshots/      # only changes.{en,nl}.txt and test.{en,nl}.txt change

specs/003-notification-channels/spec.md                 # clarification "Unsubscribing?", FR-030, SC-003
specs/003-notification-channels/contracts/messages.md   # Changes footer + Links line; Test gets the footer
specs/007-branded-email-layout/contracts/email-html.md  # Changes/Test footer rows; text-part guarantee carve-out
```

**Structure Decision**: backend-only change inside `EmailChannel`; the public page, `Channels`, `Notifier`, the
frontend and the database are untouched.

## Ordering notes for tasks

1. Footer sentence and link are separate `<p>`s, sentence first, so `AssertFooterBelowCard` (regex on the first
   footer `<p>`) keeps working unchanged.
2. Do **not** reuse `Anchor()` for the footer link: it hardcodes `LinkColor`, and `(LinkColor, Page)` is not in
   `TextPairs`, so the contrast test would pass while an unchecked pair ships. Use `Muted` + underline.
3. Tests that break and must be rewritten, not deleted:
   - `SettingsLinksAndNoActionLinks`: the comment "the only /n/ URL is the List-Unsubscribe header" and the
     `"unsubscribe option to stop these notifications"` check. New rule (amended 003 SC-003): the only `/n/` URL in
     either part is exactly `UnsubscribeUrl`; no `/n/confirm` or `/n/decline`.
   - `TestEmail`: the URL list of the text part gains the unsubscribe URL; the "no footer" line flips to "footer
     below the card".
   - The Confirmation "no footer" assertion stays as it is (SC-005).
4. Regenerate snapshots with `INTHEKNOW_UPDATE_SNAPSHOTS=1`; `git diff --stat backend/InTheKnow.Tests/EmailSnapshots/`
   must show exactly the 4 changes/test files (proof of FR-007).
5. Doc amendments last, from the spec's "Amendments to earlier specs" list.
6. Leave the unrelated `.gitignore` change out of the feature's commits.

## Complexity Tracking

No violations.
