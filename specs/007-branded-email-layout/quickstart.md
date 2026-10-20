# Quickstart: verify the branded email layout

## Automated checks

```bash
dotnet test backend/InTheKnow.Tests --filter "FullyQualifiedName~EmailChannelTests|FullyQualifiedName~AccountEmailTests"
```

Expected: all pass, including the text snapshot (SC-004), encoding (SC-005), palette/contrast (SC-003), size
(SC-007) and the new-key-in-both-languages check (SC-006).

## Send every email type to Mailpit

```bash
docker compose up -d mailpit
```

```bash
INTHEKNOW_PREVIEW_SMTP_PORT=1025 dotnet test backend/InTheKnow.Tests --filter "FullyQualifiedName~EmailPreview"
```

Expected: Mailpit at http://localhost:8025 shows one email per fixture (changes, confirmation, test, every
account kind) in English and Dutch. Without the variable, the preview test is skipped.

## Client support (SC-001)

For each email, open Mailpit's **HTML Check** tab (or `GET /api/v1/message/{ID}/html-check`; confirm the path in
the running Mailpit's API docs). Every flagged feature must be in the fallback table in
[research.md R10](./research.md#r10-verification-in-email-apps-sc-001); anything else is a failure.

## Layout (SC-001, FR-018)

Open the HTML part on its own (Mailpit's raw HTML view of the message, not the message UI, whose chrome takes
width). Take screenshots at 320, 375, 768 and 1440px wide, and at 200% zoom (640px window, browser zoom 200%).
Expected: wordmark, card, heading, labels and button visible; no sideways scrolling; long values wrap.

## Plain text

In Mailpit's **Text** tab the content is the same as before this feature (the snapshot test is the proof).
