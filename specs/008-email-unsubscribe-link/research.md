# Research: Unsubscribe link in the email footer

No open questions remained after reading the spec and the code; each item below records a decision.

## R1. Which URL the footer links to

- **Decision**: `Message.UnsubscribeUrl`, the value already put in `List-Unsubscribe` (`Notifier.Unsubscribe(token)`
  → `{PublicBaseUrl}/n/unsubscribe/{token}`).
- **Rationale**: FR-002 asks for the same page and channel; one value for both guarantees it.
- **Alternatives**: a new footer-only token or page (rejected: FR-002 forbids it, and it adds storage).

## R2. Link scanners (FR-003)

- **Decision**: no change. `GET /n/unsubscribe/{token}` only calls `Channels.PageLanguageAsync` and renders a page with
  a POST form; only `POST` calls `UnsubscribeAsync` (`NotificationApi.cs`).
- **Rationale**: the split already exists for the mail client's button (003 research R8).
- **Alternatives**: none needed. No test pins "GET leaves the channel active" for unsubscribe yet; one is added (SC-003).

## R3. Already unsubscribed / removed channel

- **Decision**: no change. A repeat POST skips `StopAsync` when the status is already `unsubscribed`
  (`Channels.cs`), and an unknown token renders `NotFoundPage()`.

## R4. Footer link style (FR-005)

- **Decision**: `Muted` (#5a5a5a) text, `text-decoration:underline`, in the footer's 12px block, on its own `<p>`
  after the footer sentence.
- **Rationale**: the footer sits on `Page` (#f0f0f0); `(Muted, Page)` is already in `TextPairs`, so the existing
  contrast test covers it. The existing `Anchor()` uses `LinkColor`, whose pair with `Page` is not declared.
- **Alternatives**: `LinkColor` and add `(LinkColor, Page)` to `TextPairs` (rejected: FR-005 asks for grey footer
  text); a button (rejected by the spec's assumptions).

## R5. Text part

- **Decision**: `{label}: {url}`, the format of `Renderer.Link`, after the footer sentence.
- **Rationale**: the plain-text part shows the full address (FR-001) in the house format.

## R6. Texts

- **Decision**: rewrite `changes.footer` (en, nl) to mention the link and the mail client's option; reuse
  `page.unsubscribe.title` ("Unsubscribe" / "Afmelden") as the link label. The test email uses `changes.footer` too,
  since its reason is the same confirmed address.
- **Proposed text**:
  - en: "You receive this email because this address was confirmed for InTheKnow timetable notifications. To stop
    these notifications to this address, use the link below or your mail client's unsubscribe option."
  - nl: "Je krijgt deze e-mail omdat dit adres is bevestigd voor roostermeldingen van InTheKnow. Gebruik de link
    hieronder of de afmeldoptie van je mailprogramma om deze meldingen naar dit adres te stoppen."
- **Alternatives**: a new `changes.unsubscribe` label key (rejected: same words as the page title it leads to).

## R7. Logs (FR-008)

- **Decision**: no change. No new log call; the URL lives only in the message, as the header value already does.
