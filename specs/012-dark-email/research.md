# Research: Dark emails

Phase 0 of [plan.md](./plan.md). Each decision names the requirement it serves. The Technical Context had no open
"NEEDS CLARIFICATION"; these are the design choices it left.

## R1. How one email carries both versions (FR-001, FR-004, FR-012)

**Decision**: the HTML keeps its light colours inline, exactly as today. Every coloured element also gets a short
class hook (`d-…`). A second `<style>` element holds one `@media (prefers-color-scheme:dark)` block that sets the
dark colour of each hook with `!important`. The `color-scheme` and `supported-color-schemes` metas and the `:root`
rule say `light dark`.

- The media block is a **separate** `<style>` element from today's phone/data-detector block. A mail app that
  drops a style block it does not like (Gmail drops a whole block on one rule it rejects) then still keeps the
  320px padding rules (US2-3, FR-006).
- Text that inherits its colour (paragraphs in the card, the footer paragraphs) needs no hook: the hook sits on
  the cell or `<div>` that sets it.
- Measured on `main` (2026-11-19): the worst-case changes email of 007 SC-007 is **75 916 bytes**. That case
  has 50 calendars with one change each, so the hooks repeat on 50 headings, settings links and change rows: about
  90 bytes each, 4.5 KB in all, plus about 1.5 KB of style block. The limit stays met with about 18 KB to spare,
  and the size test checks it in each theme (FR-012, SC-007, R8).

**Rationale**: inline light colours are what Gmail web and every app that ignores `<style>` show, so those apps
show today's email unchanged (US2-1, FR-002). The media query is how Apple Mail and other apps that honour an
email's own dark colours pick the version; declaring `light dark` is what stops Apple Mail from recolouring the
email itself.

**Alternatives considered**:

- *Two copies of the body, one hidden*: doubles the size (breaks FR-012 at the worst case) and the spec rules it
  out ("adds colours only, not a second copy").
- *Attribute selectors on the inline style* (`[style*="color:#3d3d3d"]`), no classes: no extra bytes per row, but
  `color:#…` also matches inside `background-color:#…`, and a later colour change silently breaks the match.
- *Outlook.com's `[data-ogsc]`/`[data-ogsb]` selectors* to push our dark colours into new Outlook: not needed for
  the spec. Outlook recolours on its own and only has to stay readable (US2-2). Rejected under Principle I; can be
  added if the manual check shows a problem.

## R2. "Always dark" (FR-008, SC-006)

**Decision**: the same renderer with the dark palette inline. The metas and `:root` say `dark`, and there is no
media block. The class hooks stay (see R3); without a media block nothing uses them.

**Rationale**: only inline colours reach every mail app. A light email with a "force dark" style block would still
show light in Gmail web, which ignores the block, and in Apple Mail on a light device.

**Alternatives considered**: declaring `light dark` with dark inline colours. Some apps then treat the email as
light-capable and may lighten it on a light device. `dark` says what the email is.

## R3. "Always light" (FR-007)

**Decision**: today's head (`light` metas, `:root{color-scheme:light;…}`, no media block) with the light palette
inline. The class hooks are written in every mode, so there is one rendering path; the only things a mode changes
are the inline palette and the head.

**Rationale**: FR-007 asks for "light version only, asking mail apps for light". Unused class attributes do not
change what any mail app shows. Leaving them out in two modes would put a branch into every element writer for no
visible gain.

**Alternatives considered**: byte-identical output to today. It would let a snapshot compare against `main`, but
the text-part snapshots (the only snapshots there are) do not change anyway (FR-011), and the light HTML is checked
by its inline colours (R9).

## R4. The dark palette (FR-003, FR-005)

**Decision**: one `EmailPalette` record with a `Light` and a `Dark` instance. The dark values come from FR-003 and
the web app's dark tokens (`frontend/src/lib/styles/tokens.css`) where FR-003 is silent:

| Role (light value) | Dark | Source |
|---|---|---|
| Page `#f0f0f0` | `#141413` | FR-003 |
| Card `#ffffff` | `#1c1c1a` | FR-003 |
| Ink, headings and wordmark "InThe" `#1d1d1b` | `#f5f5f2` | FR-003 |
| Body `#3d3d3d` | `#d4d4cf` | FR-003 |
| Muted `#5a5a5a` (footer, expiry, detected-at, low-priority row) | `#a6a6a0` | FR-003 |
| Border `#e3e3e3` (card and row lines) | `#343431` | FR-003 |
| Box `#f7f7f7` ("and N more") | `#242422` | not in FR-003: web `--surface-subtle` |
| Link `#c70b70`, wordmark "Know" `#e20e80` | `#ec51a7` | FR-003 |
| Button and 4px bar `#e20e80` | `#e20e80` | FR-003 (bar: unchanged brand stripe) |
| Button text (today `Card`, `#ffffff`) | `#ffffff` | FR-003; its own constant, see below |
| Added `#16673a` on `#e3f4ea` | `#6fd39a` on `#11301e` | FR-003, web green |
| Removed `#a50d26` on `#fbe3e7` | `#ff8d9b` on `#3a1519` | FR-003, web red |
| Field and time change `#7a4a00` on `#fdf0d9` | `#f5b651` on `#33240a` | FR-003, web amber |
| Low-priority label `#5a5a5a` on `#f0f0f0` | `#a6a6a0` on `#2c2c2a` | FR-003 "light grey on dark grey"; web grey-100 |

- **Button text gets its own constant.** Today the button text is written as `Card`. Swapping palettes would turn
  it into `#1c1c1a` on magenta. A `ButtonText` role, white in both palettes, avoids that.
- **Blue `#8ab8f0` is not used.** The web app's dark set has it, but the light email has three label hues (FR-003 amended to match):
  time changes share amber with field changes. A blue label only in dark would make dark differ from light in
  meaning, which FR-004 forbids. The four kinds (added, removed, field/time change, low priority) differ by their
  words in both versions (US1-5).
- **Contrast** (WCAG, computed 2026-11-19): ink on card 15.6, body on card 11.5, body on box 10.4, link on card
  5.1, white on magenta 4.56, green 7.8, red 7.3, amber 8.4, low-priority label 5.7, muted on card / page / box
  7.0 / 7.5 / 6.4, wordmark "Know" on page 5.5. All pass 4.5:1 (FR-005, SC-003).

**Alternatives considered**: deriving dark values by inverting light ones. That gives colours nobody designed and
does not match the web app (spec assumption: "email and app look alike").

## R5. Where the theme is stored and read (FR-010, FR-014, edge cases)

**Decision**: one column `account.email_theme text NOT NULL DEFAULT 'device' CHECK (email_theme IN ('device',
'light', 'dark'))`, added by migration `007_email_theme.sql`. The notifier reads it **at send time** in the three
places that already join `account`:

- channel confirmation and test (`SendQueuedAsync`): `a.email_theme`;
- change digest (`SendDigestAsync`): `a.email_theme`;
- account emails (`SendAccountAsync`): the existing `LEFT JOIN account a`; `NULL` (invite, a deleted account, no
  account yet) means device (FR-010).

`Message` gets a trailing optional parameter `EmailTheme Theme = EmailTheme.Device`, so the existing call sites
and test fixtures stay as they are.

**Rationale**: reading at send time makes the setting hold "for every email sent after it was saved" (FR-010),
including a digest whose changes were collected before the change. The confirmation of a new email address is an
`account_message` with `account_id` set, so it uses the account's setting (edge case). The column goes with the
account row on deletion (FR-014). The CHECK constraint enforces the three values in the database (constitution:
data integrity as constraints).

**Alternatives considered**: storing the theme on `account_message` / `channel_message` when the message is
queued. That freezes an older value for queued messages and stores the setting in a second place, against FR-014.

## R6. API (FR-009, FR-013)

**Decision**: extend `GET`/`PUT /api/me/notification-settings` (`Channels.GetSettingsAsync`/`SetSettingsAsync`)
with `emailTheme`: `"device" | "light" | "dark"`.

- `GET` always returns it.
- `PUT`: an absent key leaves it unchanged, the same as `language` and `timeZone`. Any other value, `null`
  included, is refused with `400 invalid_email_theme` and nothing is saved. A body with none of the three keys is
  still `empty_body`.
- A stored value that is not one of the three (not possible under the CHECK, but FR-013 asks for it) reads as
  `device`.

**Rationale**: this endpoint already holds the settings that emails follow (language, time zone), and the
Preferences page already saves to it. `PUT /api/me/settings` rejects unknown keys and holds web-app display
preferences only.

**Alternatives considered**: a new endpoint `/api/me/email-theme`: one more route and one more request on save for
the same page.

## R7. The Preferences control (FR-009, US3-1, US3-7)

**Decision**: a fieldset with three radio buttons named **`emailTheme`**, between the "emails follow" note and the
home page choice, with a legend and hint that say it is for emails and for the whole account. The save action
sends `emailTheme` to the API with `language` and `timeZone`.

- The field **must not** be named `theme`: the save action already treats a `theme` field as the per-device web
  theme (`storeTheme`, 011) with the same three values. Reusing the name would overwrite the device cookie.
- The web theme's hint already says "Saved on this device only." (011), so the email theme's hint only needs the
  other half ("for every email to your account, on every device"); together the two read apart (US3-7).

**Alternatives considered**: a select. Three short choices read faster as radios, as the web theme already does.

## R8. Size, both languages, all kinds (FR-012, SC-007)

**Decision**: the size test runs the worst case in all three modes and asserts each stays under 100 000 bytes.
Device mode is the largest.

## R9. Tests (constitution: automated check for non-trivial logic)

**Decision**:

- `EmailChannelTests`: per mode, for every fixture in both languages: the metas and `:root` rule; the media block
  only in device mode, in its own `<style>`; only palette colours used; dark palette inline in dark mode, light in
  the other two; button text white in all three; the dark `TextPairs` all ≥ 4.5:1; the text part, subject and
  headers equal across the three modes (FR-011).
- A check that the media block names a dark colour for every hook the renderer writes, so a new hook cannot be
  forgotten.
- Existing 007 assertions that match exact markup are **rewritten**, not just added to. `ConfirmationEmail` matches
  the button anchor with `color:{Card}`, the expiry paragraph and the decline link's style. `ShellAndPalette`
  expects `color-scheme` `light` in every email. Both change once class hooks are written and the colours move
  into the palette record.
- `NotificationApiTests`: `emailTheme` in GET, PUT with each value, `invalid_email_theme` for others and `null`,
  absent key unchanged.
- `DeliveryTests` and `AccountEmailTests`: one send per path picks the account's theme at send time; an invite
  uses device.
- Frontend: `page.server.test.ts` sends `emailTheme` and leaves the `itk_theme` cookie alone; the e2e stub's
  notification-settings fixture gets the field (011's typed fixture would fail to compile otherwise).
- `EmailPreviewTests` sends every fixture in the three themes, for the manual checks in [quickstart.md](./quickstart.md).

## R10. Manual checks (SC-001, SC-004, Clarifications)

**Decision**: per the clarification of 2026-11-19: Apple Mail (iOS or macOS) for an app that honours the dark
colours; the Gmail app on Android and new Outlook / Outlook on the web for apps that recolour; Gmail web for an app
that ignores them. Each email type is sent to the test mailbox through the preview test or a real send. Steps are
in [quickstart.md](./quickstart.md).
