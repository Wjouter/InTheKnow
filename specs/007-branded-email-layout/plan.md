# Implementation Plan: Branded email layout

**Branch**: `007-branded-email-layout` (work currently on `main`) | **Date**: 2026-10-20 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/007-branded-email-layout/spec.md`

## Summary

Every email InTheKnow sends gets one branded HTML layout: a type-only wordmark, a centred white card of at most
600px with a magenta top bar, the subject as heading, colour-coded change rows, a magenta button for the main
action and the footer below the card. The work is a rewrite of the HTML side of the existing hand-written
`Renderer` in `backend/InTheKnow/EmailChannel.cs`, using table layout with MSO fallbacks for classic Outlook. The
plain-text part, subjects and headers stay byte-for-byte the same, proven by snapshots taken before the change.

## Technical Context

**Language/Version**: C# on .NET 10 (`global.json`)

**Primary Dependencies**: BCL only (`System.Net.Mail`, `System.Text.Encodings.Web`); no new package

**Storage**: N/A (no schema change)

**Testing**: xUnit v3 (`backend/InTheKnow.Tests`), Mailpit (already in `compose.yaml`) for manual client checks

**Target Platform**: email apps: classic Outlook 2016+ and new Outlook (Windows), Outlook web/Mac, Gmail web/apps,
Apple Mail macOS/iOS

**Project Type**: web application (backend change only)

**Performance Goals**: N/A (render cost is trivial next to SMTP)

**Constraints**: HTML part < 100 KB at `Notifier.Listed` changes (each row value and calendar name ≤ 200 encoded bytes in HTML);
inline styles plus one optional `<style>` block; no images, scripts, remote resources or web fonts; ≥ 4.5:1
contrast; no sideways scroll from 320px and at 200% zoom

**Scale/Scope**: 4 message kinds, 17 account kinds, 2 languages

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle / rule | Status |
|---|---|
| I. Simplicity: smallest implementation, no speculative code | PASS: keeps the existing renderer; no template engine, MJML or inliner; no VML; one new text key |
| I. New dependency or layer justified | PASS: none added |
| II. No new personal data | PASS: nothing stored |
| II. No secrets or deployment details in repo | PASS: quickstart uses `localhost` and Mailpit only; no real mailbox or server named |
| II. Output encoded by default | PASS: every inserted value stays HTML-encoded (SC-005 test per kind) |
| Tech: GPL-compatible, lockfiles | PASS: no new package |
| Workflow: non-trivial logic has an automated check | PASS: snapshot, encoding, palette/contrast, size, truncation tests |

Post-design re-check: PASS (the design adds no dependency, layer or stored data).

## Project Structure

### Documentation (this feature)

```text
specs/007-branded-email-layout/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/email-html.md
└── tasks.md             # /speckit-tasks
```

### Source Code (repository root)

```text
backend/InTheKnow/
├── EmailChannel.cs      # Renderer: new HTML shell, card, rows, button, footer; palette constants; 200-byte shortening
├── Texts.cs             # + account.linkFallback (en, nl)
└── Notifier.cs          # Listed: private → internal (used by the size test)

backend/InTheKnow.Tests/
├── EmailChannelTests.cs # HTML assertions rewritten around structure; palette/contrast, size, truncation tests
├── AccountEmailTests.cs # button + fallback line; footer below the card
├── EmailSnapshots/      # pre-feature text part + subject + headers per fixture (SC-004)
└── EmailPreviewTests.cs # opt-in: sends every fixture to Mailpit (skipped without INTHEKNOW_PREVIEW_SMTP_PORT)

specs/003-notification-channels/contracts/messages.md   # Body row: allow the optional <style> block (link to 007 contract)
```

**Structure Decision**: backend-only change inside the existing `EmailChannel` renderer; the frontend and the
public `/n/…` pages are untouched.

## Ordering notes for tasks

1. Snapshot the current text part, subject and headers for every fixture **before** touching the renderer.
2. The confirmation button keeps the text format `label: url` (today's `Link`), not `Button`'s `label:\nurl`.
3. `EmailChannelTests.RendersParts` currently asserts strings like `Rooms: AL00.A1.38 → AL00.A1.20` in the HTML
   part; the split label/value layout breaks these, so the HTML assertions move to structure (label, values,
   encoding) while the text part is pinned by the snapshot.
4. Check `specs/004-accounts-login/contracts/account-emails.md` for HTML wording; amend only if it describes the
   HTML shape.

## Complexity Tracking

No violations.
