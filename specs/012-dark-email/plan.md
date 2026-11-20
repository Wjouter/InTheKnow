# Implementation Plan: Dark emails

**Branch**: `012-dark-email` | **Date**: 2026-11-19 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/012-dark-email/spec.md`

## Summary

Every email gets a dark version of its colours, and the account gets a setting that picks which versions an email
carries.

- **Following the device** (the default): the HTML keeps its light colours inline, exactly as today. Each coloured
  element also gets a short class hook, and a second `<style>` element holds one `@media
  (prefers-color-scheme:dark)` block that recolours the hooks. The `color-scheme` metas say `light dark`. Apps that
  honour it (Apple Mail) switch with the device; apps that ignore `<style>` (Gmail web) show today's email
  (research R1).
- **Always dark**: the same renderer with the dark palette inline, metas `dark`, no media block (R2).
- **Always light**: today's head and light palette (R3).
- **The dark palette** is the web app's dark set (spec 011). FR-003 is silent on four roles, which take the nearest
  web token. Blue is not used, because the light email has no blue label. All pairs pass 4.5:1 (R4).
- **The setting** is one column, `account.email_theme` (`device`, `light` or `dark`). The notifier reads it at send
  time in its three send paths. It is exposed as `emailTheme` on `/api/me/notification-settings` and set on Settings
  › Preferences with three radio buttons named `emailTheme` (R5 to R7).

No new dependency, service or endpoint.

## Technical Context

**Language/Version**: C# on .NET 10 (backend), TypeScript with Svelte 5 / SvelteKit 2 (frontend).

**Primary Dependencies**: none new. BCL `System.Net.Mail` for the email (as 003/007), Npgsql, SvelteKit.

**Storage**: PostgreSQL. One column on `account` (migration `007_email_theme.sql`). See [data-model.md](./data-model.md).

**Testing**:

- xUnit (`dotnet test`): `EmailChannelTests` (per-theme head, palette, dark contrast, hooks and dark block in step,
  equal text part and headers, size in each theme), `NotificationApiTests` (the field), `DeliveryTests` and
  `AccountEmailTests` (the theme is read at send time; an invite uses the default).
- Vitest (`npm test`): the Preferences save action sends `emailTheme` and does not touch the `itk_theme` cookie.
  `npm run check` for the dictionaries. The e2e stub fixture gets the field; `npm run test:e2e` keeps passing.
- By hand ([quickstart.md](./quickstart.md)): Mailpit preview of every fixture in three themes, then Apple Mail,
  Gmail web, the Gmail app on Android and new Outlook, per the clarification of 2026-11-19.

**Target Platform**: the server as deployed today (Docker, spec 006). Mail apps: those of 007 FR-017. Dark needs
`prefers-color-scheme` support in the mail app; elsewhere the email is light or recoloured by the app, and readable.

**Project Type**: web application (SvelteKit frontend, .NET API).

**Performance Goals**: none new. Rendering stays a string build per message.

**Constraints**:

- The light version must not change visibly (FR-002).
- The text part, subject and headers must not change (FR-011).
- The worst-case HTML must stay under 100 KB in each theme (FR-012). It is 75 916 bytes on `main` today; the
  hooks and the dark block add about 6 KB (R1), and the size test checks each theme.
- No images and no remote resources, as in 007.
- The setting may hold nothing about the reader's device (FR-014).

**Scale/Scope**: 1 migration, 4 backend files (`EmailChannel.cs`, `ChannelType.cs`, `Notifier.cs`, `Channels.cs`),
6 backend test files, 1 Preferences page with its server action and test, the refusal codes, 2 dictionaries,
1 e2e fixture file, and 4 spec documents amended.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Check | Result |
|---|---|---|
| I. Simplicity First | One renderer, two palettes, three heads. No template engine, no second copy of the body, no Outlook-specific selectors (R1). Reuses the notification-settings endpoint and the Preferences form. | PASS |
| I. No new dependency | None, backend or frontend. | PASS |
| I. No speculative code | No per-device or per-channel theme, no per-message theme stored, no theme for the web app on the account (spec scope). | PASS |
| II. Minimal data | One column with three values, named and justified in [data-model.md](./data-model.md). Nothing about devices or mail apps. Removed with the account. | PASS |
| II. Validate at trust boundary | `PUT /api/me/notification-settings` accepts only the three values; anything else, `null` included, is `400 invalid_email_theme` (R6). The database CHECK enforces the same. | PASS |
| II. Escaped output | No new inserted value in the HTML. The class names and colours are fixed strings in code. Every calendar and account value is still encoded (007 FR-020). | PASS |
| II. No deployment details committed | The quickstart uses `example.test` and placeholders for the test mailbox. | PASS |
| Technology constraints | .NET, PostgreSQL and SvelteKit only; the integrity rule is a database CHECK. | PASS |
| Workflow: automated check for non-trivial logic | Theme choice per send path, head per theme, palette and contrast, the hook-to-rule check, API validation: all have tests (R9). | PASS |

**Post-design re-check (after Phase 1)**: still PASS. The design adds no layer, dependency or stored personal data
beyond the one column. The one non-obvious coupling is the form field name. `theme` already means the per-device
web theme in the same form, so the new field is `emailTheme` (R7), and a test checks it.

## Project Structure

### Documentation (this feature)

```text
specs/012-dark-email/
├── plan.md              # This file
├── research.md          # Phase 0: decisions R1 to R10
├── data-model.md        # Phase 1: the email theme and its column
├── quickstart.md        # Phase 1: how to check the feature
├── contracts/
│   └── email-theme.md   # Phase 1: API field, HTML per theme and hooks, Preferences control
└── tasks.md             # Phase 2 (/speckit-tasks, not made here)
```

### Source Code (repository root)

```text
backend/InTheKnow/
├── Migrations/007_email_theme.sql      # NEW: account.email_theme
├── ChannelType.cs                      # enum EmailTheme; Message gets Theme = Device
├── EmailChannel.cs                     # EmailPalette Light/Dark, ButtonText role, class hooks, head per theme,
│                                       #   dark media block in its own <style>; Palette and TextPairs cover both
├── Notifier.cs                         # read a.email_theme in SendQueuedAsync, SendDigestAsync, SendAccountAsync
└── Channels.cs                         # NotificationSettings.EmailTheme; Get/SetSettingsAsync read and write it

backend/InTheKnow.Tests/
├── EmailChannelTests.cs                # per-theme shell, palette, dark contrast, hooks ↔ rules, size × 3, same text part;
│                                       #   existing exact-markup asserts (ConfirmationEmail, ShellAndPalette) rewritten
├── EmailPreviewTests.cs                # send every fixture in three themes
├── NotificationApiTests.cs             # emailTheme GET/PUT, invalid_email_theme
├── ChannelsTests.cs                    # NotificationSettings asserts gain EmailTheme
├── DeliveryTests.cs                    # digest and test message use the account's theme at send time
└── AccountEmailTests.cs                # account emails use it; an invite uses device

frontend/
├── src/routes/(settings)/settings/preferences/
│   ├── +page.svelte                    # email theme fieldset (name="emailTheme")
│   ├── +page.server.ts                 # Settings type, load emailTheme, send it on save
│   └── page.server.test.ts             # sends emailTheme, itk_theme untouched
├── src/lib/codes.ts                    # invalid_email_theme as a known refusal code
├── src/lib/i18n/en.ts, nl.ts           # preferences.emailTheme* texts, invalid_email_theme message
└── e2e/fixtures.ts                     # notificationSettings gets emailTheme

specs/003-notification-channels/contracts/api.md  # notification-settings: emailTheme, see 012
specs/007-branded-email-layout/spec.md  # FR-019 and the "no dark-mode version" assumption: point to 012
specs/007-branded-email-layout/contracts/email-html.md  # "light-mode metas in every kind" → per theme, see 012
specs/011-dark-mode/spec.md             # "Emails are out of scope": point to 012
```

**Structure Decision**: the existing backend and frontend layout. One new file (the migration); everything else is an
edit to an existing file. The renderer stays in `EmailChannel.cs`: the palettes and hooks are about 40 lines, which
is too small to move into a file of their own.

## Complexity Tracking

No violations. Nothing to justify.
