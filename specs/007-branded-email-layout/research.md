# Research: Branded email layout

All decisions keep the hand-written renderer in `backend/InTheKnow/EmailChannel.cs`. No template engine, MJML or
CSS inliner is added (Principle I): the email has four shapes, and inline styles written once per element are
fewer lines than a build step.

## R1. Layout technique for classic Outlook (Word engine)

- **Decision**: Nested `<table role="presentation">` layout. The outer table is 100% wide with `bgcolor="#f0f0f0"`;
  the card is a table with `width="100%"` and `style="max-width:600px"`, wrapped in an MSO-only ghost table
  (`<!--[if mso]><table width="600" align="center">…<![endif]-->`) because classic Outlook ignores `max-width`.
  Colours are set both as `bgcolor` attributes and inline `background-color` (Outlook reads the attribute).
- **Rationale**: tables are the only layout classic Outlook 2016+ renders reliably; the ghost table is the
  standard way to cap width there while other apps shrink with the window (FR-006, FR-017, FR-018).
- **Alternatives**: `<div>` layout with `max-width` (breaks in classic Outlook: full width, no centring);
  MJML (new dependency and build step for four shapes).

## R2. Card top bar, border and corners

- **Decision**: the magenta bar is a 4px-tall first row (`<td height="4" bgcolor="#e20e80" style="font-size:0;line-height:0">`);
  the card has `border:1px solid #e3e3e3;border-radius:2px`. Classic Outlook drops the radius (square corners).
- **Rationale**: a coloured row renders everywhere; `border-top` on a rounded box renders inconsistently.

## R3. Button (FR-015)

- **Decision**: a one-cell table: `<td bgcolor="#e20e80" style="border-radius:4px">` containing
  `<a style="display:inline-block;padding:12px 24px;line-height:20px;color:#ffffff;font-weight:700;text-decoration:none">`.
  12 + 20 + 12 = 44px tall. Classic Outlook ignores padding on `<a>`, so the cell carries
  `style="mso-padding-alt:12px 24px"`; there the whole cell is coloured but only the text is clickable.
- **Rationale**: a padded table cell is the "bulletproof button" without VML; VML would add a second copy of
  every button for one app.
- **Alternatives**: VML `<v:roundrect>` (rounded corners in Outlook, ~15 lines of conditional markup per button,
  rejected: corners may fall back per FR-017).
- **Trap**: the confirmation email gets a button in HTML but its plain text must stay `label: url`
  (the current `Link` format). The current `Button` writes `label:\nurl`. So the HTML button markup is one
  helper, and the text side keeps whichever format each call site has today.

## R4. Phone-only styling, light mode, iOS links (FR-009, FR-007, FR-019, FR-020)

- **Decision**: one small `<style>` block in `<head>`:
  - `@media (max-width:480px)` setting card padding to 20px and the heading to 24px through two classes;
  - `:root{color-scheme:light;supported-color-schemes:light}`;
  - `a[x-apple-data-detectors]{color:inherit!important;text-decoration:none!important;font:inherit!important}`.
  Plus `<meta name="color-scheme" content="light">`, `<meta name="supported-color-schemes" content="light">`,
  `<meta name="format-detection" content="telephone=no,date=no,address=no,email=no">`,
  `<meta name="x-apple-disable-message-reformatting">` and the viewport meta.
- An MSO-only `<style>` (`<!--[if mso]>`) sets `font-family:Arial,sans-serif` on `body, table, td, a`, because
  classic Outlook falls back to Times New Roman when the first font in the stack (Lato) is missing.
- **Rationale**: FR-002 allows one optional phone-only block; everything that must work without it is inline.
  Apps that drop `<style>` (Gmail with non-Gmail accounts) keep the 32px padding, which still fits at 320px
  (320 − 2 × 32 = 256px of text) as long as nothing has a fixed width.
- **Alternatives**: fixed 20px padding everywhere (rejected: spec asks for 32px on desktop).

## R5. Long values and narrow screens (Edge cases, FR-018)

- **Decision**: no element below the card has a fixed width. Text cells get
  `word-break:break-word;overflow-wrap:anywhere`. Dates sit in their own line, so a before → after on different
  days wraps at the arrow. Every calendar value in a change row is shortened in HTML (R8).
- **Rationale**: one inline rule covers long URLs in descriptions and long room lists.

## R6. Fonts

- **Decision**: body `font-family:Lato,Helvetica,Arial,sans-serif`; wordmark the same stack with
  `font-weight:900`; dates `font-family:Menlo,Consolas,'Courier New',monospace`. No `@font-face` (FR-002).

## R7. Text part stays byte-for-byte (SC-004, FR-003)

- **Decision**: the `Renderer` keeps writing the text part with exactly the calls it makes today; only the HTML
  side of each method changes. The footer (`changes.footer`, `account.footer`) is already the last paragraph
  in text, so a `Footer` method writes it to text as before and to a separate HTML builder that is placed below
  the card. The heading (subject) is HTML-only.
- **Proof**: before the renderer changes, a test snapshots the current text part (and subject and headers) of
  every email shape into checked-in files: a changes email with every change kind, low priority and
  `NotListed > 0`; confirmation; test; every account kind including both `roleChanged` and `invite` variants;
  each in `en` and `nl`. The snapshot test then guards the refactor.

## R8. Size (FR-022, SC-007)

- **Decision**: the HTML side shortens each calendar value in a change row (event name, old/new value, where)
  and each calendar name so its *encoded* form is at most 200 UTF-8 bytes, the "…" (3 bytes) included (clarified
  2026-10-20 after analysis). A character cap does not bound size: the encoder turns `"` and `'` into 6 bytes,
  `&` into 5 and `<`/`>` into 4, and emoji or non-Latin scripts take 2–3 bytes per `char`. The helper walks the
  string by text element (`StringInfo`), adds each element's encoded byte count, and stops before the budget is
  passed, so no character or entity is cut. Row markup keeps inline styles short (no repeated font stack on every
  cell: it is set on the card cell and inherited, except where Outlook needs it via the MSO style).
- **Rejected**: 300 encoded bytes per value. Worst case (50 changes, one calendar each, three values per row) is
  about 50 × (0.55 KB markup + 0.9 KB values) + 50 × (0.4 KB calendar header + 0.3 KB name) ≈ 108 KB.
- **Measure**: `Encoding.UTF8.GetByteCount(html)` of the decoded HTML string, not the transfer-encoded MIME part.
  The test builds `Notifier.Listed` changes (currently 50; the constant is made `internal` so the test uses it)
  in Dutch, each in its own calendar with a 1000-character name, mixed kinds and low priority, every calendar
  value 1000 characters of text that grows when encoded (a mix of `"&<>'`, emoji and CJK), and asserts
  < 100 000 bytes.
- **Estimate**: ~0.55 KB markup + ≤ 0.6 KB values per row, ≤ 0.4 KB per calendar header → about 80 KB worst
  case. The test is the authority: if it fails, slim the markup; do not raise the cap.

## R9. Contrast (FR-021, SC-003)

Measured with the WCAG 2.x formula:

| Text | Background | Ratio |
|---|---|---|
| ink #1d1d1b (heading) | #ffffff | 16.9 |
| body #3d3d3d | #ffffff / #f7f7f7 | 10.9 / 10.1 |
| link #c70b70 | #ffffff | 5.7 |
| button #ffffff | #e20e80 | 4.6 |
| added #16673a | #e3f4ea | 6.1 |
| removed #a50d26 | #fbe3e7 | 6.4 |
| field change #7a4a00 | #fdf0d9 | 6.6 |
| muted / low priority #5a5a5a | #ffffff / #f0f0f0 / #f7f7f7 | 6.9 / 6.0 / 6.4 |
| wordmark "Know" #e20e80 | #f0f0f0 | 4.0 (logotype, exempt) |

- **Decision**: the colours are named constants in `EmailChannel`. A test asserts ≥ 4.5:1 for every declared
  text/background pair, and a second check asserts that every `#rrggbb` in the rendered HTML of all fixtures
  belongs to the declared palette (text pairs plus the border #e3e3e3), so a colour added later cannot skip the
  contrast check.

## R10. Verification in email apps (SC-001)

- **Decision**: Mailpit's HTML Check (`GET /api/v1/message/{ID}/html-check`, verify the path against the running
  Mailpit) per email type, plus screenshots of the HTML part alone at 320, 375, 768 and 1440px and 200% zoom
  (a 640px window at 200% browser zoom). The HTML part is opened on its own (Mailpit's raw HTML view, or the
  file the preview test writes), because Mailpit's own UI takes width.
- Email types reach Mailpit through an opt-in preview test that sends every fixture through
  `EmailChannel.SendAsync`; it is skipped unless an SMTP port is given in the environment. No app flow has to be
  clicked through to produce a changes email.
- **Expected flags and their fallbacks** (anything flagged that is not in this table fails SC-001):

| Flagged feature | Clients | Fallback |
|---|---|---|
| `border-radius` | classic Outlook | square corners |
| `<style>` / `@media` | Gmail with non-Gmail accounts, some Outlook | desktop padding (32px) and 28px heading |
| `padding` on `<a>` | classic Outlook | `mso-padding-alt` on the button cell |
| `max-width` | classic Outlook | MSO ghost table at 600px |
| `display:inline-block` | classic Outlook | text link inside a coloured cell |
| `text-transform` | none expected; if flagged | label shows the kind name in its normal case |
| `color-scheme` / `supported-color-schemes` | Gmail, Outlook | app recolours; readability checked by eye |
| `word-break` / `overflow-wrap` | classic Outlook | long unbroken values may widen the card there (FR-018 exception) |
| `font-weight:900` | apps without heavy Lato | bold of the fallback font |
| `x-apple-data-detectors` selector | non-Apple apps | not needed there |
| MSO conditional comments | non-Outlook | ignored by design |
| `<body>` styles | Gmail, Outlook mobile/web (partial/none) | page colour also on the outer table (`bgcolor` + inline) |
| `border`, `border-radius`, `height`, `width`, `padding`, `line-height`, `letter-spacing`, `font-weight`, `text-decoration` | classic Outlook (partial, Word engine) | square corners and slightly looser spacing; layout stays in tables, button cell carries `mso-padding-alt`, bar and colours use `bgcolor` |
| `margin`, `text-align`, `display` | some Gmail/Outlook mobile versions (partial) | `align="center"` and table cells carry the centring and spacing that matter |

Mailpit's HTML Check flags every property the email uses, not only the unsupported ones (checked 2026-10-20 over every
fixture in both languages): all flags in the lists above and nothing else for Outlook, Gmail and Apple Mail families.
