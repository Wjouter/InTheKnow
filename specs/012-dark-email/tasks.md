---

description: "Task list for 012 Dark emails"
---

# Tasks: Dark emails

**Input**: Design documents from `/specs/012-dark-email/`

**Prerequisites**: plan.md, spec.md, research.md (R1–R10), data-model.md, contracts/email-theme.md, quickstart.md

**Tests**: Included. The constitution asks for an automated check of non-trivial logic, and plan.md / research R9 name
the tests per file.

**Organization**: by user story. US1 and US2 are both P1 and share the renderer; US2 is mostly the fallback checks on
top of US1. US3 (P2) adds the account setting and the two single-version themes.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: US1, US2, US3 from spec.md

---

## Phase 1: Setup

**Purpose**: none needed. No new project, dependency or tool (plan: "No new dependency, service or endpoint").

- [ ] T001 Confirm the baseline on `main`: `dotnet test --project backend/InTheKnow.Tests` and `cd frontend && npm test && npm run check` pass, and note the worst-case HTML size printed by the size test in backend/InTheKnow.Tests/EmailChannelTests.cs (expected 75 916 bytes, research R1). Save the HTML part of every `EmailFixtures` message in `en` and `nl` as rendered on `main` to backend/InTheKnow.Tests/Baseline/{name}.{language}.html (temporary, deleted in T031)

---

## Phase 2: Foundational (blocking prerequisites)

**Purpose**: the theme type, the palette record and the column that every story reads.

- [ ] T002 [P] Create backend/InTheKnow/Migrations/007_email_theme.sql exactly as data-model.md: header comment `-- 012: the account's email theme (specs/012 data-model.md).` then `ALTER TABLE account ADD COLUMN email_theme text NOT NULL DEFAULT 'device' CHECK (email_theme IN ('device', 'light', 'dark'));`. Check how existing migrations are picked up (embedded resource or copied folder) and register it the same way if needed
- [ ] T003 [P] In backend/InTheKnow/ChannelType.cs add `enum EmailTheme { Device, Light, Dark }` and a trailing optional parameter `EmailTheme Theme = EmailTheme.Device` on `sealed record Message(...)`, so every existing call site and test fixture compiles unchanged (research R5). Add a small helper (e.g. `static EmailTheme ParseEmailTheme(string? s)`) that maps `"light"`→Light, `"dark"`→Dark and anything else, `null` included, → Device (data-model "Code that reads it maps anything else to `device`")
- [ ] T004 In backend/InTheKnow/EmailChannel.cs replace the loose colour constants (`Ink`, `Magenta`, `LinkColor`, `Body`, `Muted`, `Page`, `Card`, `Border`, `Box`, `AddedFg/Bg`, `RemovedFg/Bg`, `FieldFg/Bg`) with an `EmailPalette` record holding one field per role plus four roles that today share a constant but differ in dark: `ButtonText` (today `Card`), `Wordmark` for "Know" (today `Magenta`; the bar and button stay `Magenta`), and `LowFg`/`LowBg` for the low-priority label (today `Muted` on `Page`, `EmailChannel.cs:280`). In `Light` they keep today's values. Two instances `EmailPalette.Light` (today's values, unchanged) and `EmailPalette.Dark` (research R4 table: page `#141413`, card `#1c1c1a`, ink `#f5f5f2`, body `#d4d4cf`, muted `#a6a6a0`, border `#343431`, box `#242422`, link and wordmark "Know" `#ec51a7`, magenta/bar `#e20e80`, button text `#ffffff`, added `#6fd39a` on `#11301e`, removed `#ff8d9b` on `#3a1519`, field/time `#f5b651` on `#33240a`, low priority `#a6a6a0` on `#2c2c2a`). Blue `#8ab8f0` is NOT used (R4). Thread the chosen palette (from `m.Theme`: Dark → Dark, else Light) through `Document` and the body writers instead of the constants. The button text uses `ButtonText`, not `Card`. With `Theme = Device` or `Light` the output colours must equal today's (FR-002). `Palette` becomes the union of both instances; `TextPairs` covers the text pairs of both palettes; a separate `LargeTextPairs` holds the wordmark pairs ("InThe" on `Page`, "Know" on `Page`) of both palettes (FR-005)
- [ ] T005 Update existing exact-markup assertions in backend/InTheKnow.Tests/EmailChannelTests.cs that refer to the old constants (`ConfirmationEmail` matching `color:{Card}` on the button, the expiry paragraph, the decline link; `TextColoursHaveContrast`) to use `EmailPalette.Light.*` and `ButtonText`, and run the suite: all green with no visible change. Add a test: for every fixture, the `Theme = Light` HTML with the ` d-xx` hooks stripped from `class` attributes (and empty `class=""` removed) equals the T001 baseline byte for byte (FR-002, SC-002); it keeps guarding T009–T010 and is removed with the baseline in T031 (depends on T001, T004)

**Checkpoint**: the renderer has two palettes and a theme on `Message`; output for the default is still today's email.

---

## Phase 3: User Story 1 – The email is dark when my mail app is dark (P1) 🎯 MVP

**Goal**: with the default theme every email carries both versions: light inline colours plus a dark `@media (prefers-color-scheme:dark)` block over class hooks, and `light dark` metas.

**Independent Test**: run the EmailPreviewTests against Mailpit, open a `device` email in a browser or Apple Mail set to dark: dark version; set to light: today's email (quickstart "Send every email in every theme to Mailpit").

### Tests for User Story 1

- [ ] T006 [P] [US1] In backend/InTheKnow.Tests/EmailChannelTests.cs add a theory over every fixture of `EmailFixtures(language)` in `en` and `nl` with the default theme asserting: `<meta name="color-scheme" content="light dark">` and `<meta name="supported-color-schemes" content="light dark">`; the first `<style>` contains `:root{color-scheme:light dark;supported-color-schemes:light dark}`; a separate second `<style>` element contains exactly one `@media (prefers-color-scheme:dark){` block; inline colours are the light palette (FR-001, FR-002)
- [ ] T007 [P] [US1] In backend/InTheKnow.Tests/EmailChannelTests.cs add the hook-to-rule check (research R9, contract "Guarantees"): collect every `d-…` class the renderer writes across all fixtures and every selector in the dark block; each written hook has a rule and each rule's hook is written somewhere; each rule sets only `color`, `background-color`/`background` and `border-color` with `!important`, using exactly the dark palette value of contracts/email-theme.md "Hooks" (FR-004)
- [ ] T008 [P] [US1] In backend/InTheKnow.Tests/EmailChannelTests.cs extend the "only palette colours" check (`ShellAndPalette`) to scan the inline styles AND both `<style>` blocks against `Palette` (light ∪ dark), and add a test that every pair in the dark `TextPairs` is ≥ 4.5:1 and every pair in `LargeTextPairs` (both palettes) is ≥ 3:1 (FR-005, SC-003)

### Implementation for User Story 1

- [ ] T009 [US1] In backend/InTheKnow/EmailChannel.cs write the class hooks of contracts/email-theme.md "Hooks" on every coloured element, in every theme (R3: one rendering path): `d-pg` on `<body>` and the outer table; `d-cd` on the card table; `d-bd` on the content cell; `d-ik` on wordmark "InThe", `<h1>`, `<h2>` and event names; `d-kn` on wordmark "Know"; `d-ln` on text links; `d-mt` on footer, muted paragraphs/lines, footer link and the low-priority row; `d-rw` on change rows (top border); `d-bx` on the "and N more" box; `d-ad`/`d-rm`/`d-fd`/`d-lp` on the change labels. Text that inherits its colour gets no hook (R1). The magenta bar and button cell get none. If an element already has `class` (e.g. `px`, `h1`), add to it
- [ ] T010 [US1] In backend/InTheKnow/EmailChannel.cs build the dark block from `EmailPalette.Dark` in code (not a hand-typed string), one rule per hook with `!important`, in its own `<style>` element placed after the existing `Css` element so Gmail dropping it keeps the 320px rules (R1). Make the metas and the `:root` rule of `Document` theme-dependent per contract §2: Light → `light`, no dark block; Device → `light dark` and emit the dark block; Dark → `dark`, no dark block (inline colours already come from `EmailPalette.Dark`, T004); keep `Css`'s other rules and the MSO `<style>` unchanged (contract §2) (depends on T009)
- [ ] T011 [US1] In backend/InTheKnow.Tests/EmailChannelTests.cs make the size test (comment "SC-007: the worst case stays under 100 KB") render the worst case with `Theme = Device` and assert `< 100_000` bytes (FR-012, R8)

**Checkpoint**: default-theme emails switch with the device in Apple Mail; the light rendering is today's email.

---

## Phase 4: User Story 2 – A mail app that goes its own way still shows a readable email (P1)

**Goal**: apps that ignore `<style>` or recolour on their own still show a readable email with no sideways scrolling.

**Independent Test**: the Gmail web, Gmail app (Android) and new Outlook rows of quickstart "Real mail apps", at 320px and desktop width.

### Tests for User Story 2

- [ ] T012 [P] [US2] In backend/InTheKnow.Tests/EmailChannelTests.cs assert for every fixture that removing the second `<style>` element (simulating Gmail dropping it) leaves the HTML byte-identical to the same message rendered with `Theme = Light` apart from the metas and `:root` rule — i.e. apps that ignore the block show today's email (US2-1, FR-006) — and that the first `<style>` still contains the `@media (max-width:480px)` padding rules in every theme (US2-3)

### Implementation for User Story 2

- [ ] T013 [US2] Review backend/InTheKnow/EmailChannel.cs for forced-dark readability (US2-2): every text colour is set on the element or its cell (no text relying on a default black that a recolouring app could put on its own dark background), the button keeps an explicit inline background and `ButtonText` colour, and no colour is carried only by a `<style>` rule. Fix any gap found; no Outlook-specific `[data-ogsc]` selectors (R1, Principle I)

**Checkpoint**: US1 + US2 together are the full "follow the device" behaviour.

---

## Phase 5: User Story 3 – I overrule it for my account (P2)

**Goal**: an account email theme (`device`/`light`/`dark`) on Settings › Preferences, read at send time, producing the single-version emails of contract §2.

**Independent Test**: quickstart "Preferences": set each of the three values, Send test, open on a light and a dark device.

### Tests for User Story 3

- [ ] T014 [P] [US3] In backend/InTheKnow.Tests/EmailChannelTests.cs add theories over every fixture in `en`/`nl` for `Theme = Light` (metas `light`, `:root{color-scheme:light;supported-color-schemes:light}`, no dark block, light inline palette) and `Theme = Dark` (metas `dark`, `:root{…:dark}`, no dark block, dark inline palette, button text `#ffffff`); assert the text part, subject and headers (`List-Unsubscribe`, `Message-ID` shape), recipients and part order are equal across the three themes (FR-007, FR-008, FR-011); run the worst-case size check in all three themes, each `< 100_000` bytes (FR-012)
- [ ] T015 [P] [US3] In backend/InTheKnow.Tests/NotificationApiTests.cs: `GET /api/me/notification-settings` returns `"emailTheme":"device"` for a new account; `PUT` with each of `"device"`, `"light"`, `"dark"` → 200 and GET returns it; `PUT` with `"blue"`, `1` and `null` → 400 `invalid_email_theme` and nothing saved, `language`/`timeZone` in the same body included; `emailTheme` absent leaves it unchanged; a body with none of `language`, `timeZone`, `emailTheme` → 400 `empty_body` (contract §1, FR-013)
- [ ] T016 [P] [US3] In backend/InTheKnow.Tests/DeliveryTests.cs: set the subscriber's `email_theme` to `dark` after a change is queued but before the digest is sent, and assert the sent digest has dark metas; same for a test notification via `SendQueuedAsync` (FR-010, US3-5, US3-6)
- [ ] T017 [P] [US3] In backend/InTheKnow.Tests/AccountEmailTests.cs: an account email for an account with `email_theme = 'light'` has light metas, also when sent to a new address (email change confirmation); an invite (no `account_id`) uses `light dark` (device) (FR-010, edge cases)
- [ ] T018 [P] [US3] In frontend/src/routes/(settings)/settings/preferences/page.server.test.ts: the save action sends `emailTheme` from the form field `emailTheme` in the `PUT /api/me/notification-settings` body with `language` and `timeZone`, and does NOT set or change the `itk_theme` cookie when only `emailTheme` changes (R7)

### Implementation for User Story 3

- [ ] T019 [US3] In backend/InTheKnow/Notifier.cs read `a.email_theme` at send time and pass it as `Theme` on the `Message`: in `SendQueuedAsync` (confirmation and test, via the existing account join), in `SendDigestAsync` (the `new Message(MessageKind.Changes, …)` around line 298), and in `SendAccountAsync` via the existing `LEFT JOIN account a` (the `new Message(MessageKind.Account, …)` around line 437), where `NULL` → Device. Map with the T003 helper (depends on T002, T003)
- [ ] T020 [US3] In backend/InTheKnow/Channels.cs add `string EmailTheme` to `sealed record NotificationSettings(...)`; `GetSettingsAsync` selects `email_theme` and maps any value other than `device`/`light`/`dark` to `"device"`; `SetSettingsAsync` treats `emailTheme` as a third optional key: the `empty_body` check becomes `!hasLanguage && !hasZone && !hasEmailTheme`, a present key whose value is not the string `"device"`, `"light"` or `"dark"` (including JSON `null`) returns `(null, "invalid_email_theme")` before anything is written, and a valid one is written in the same UPDATE (contract §1). Update the exact `new NotificationSettings(...)` assertions in backend/InTheKnow.Tests/ChannelsTests.cs (around lines 332–340) for the new field (`"device"` unless the test sets it) (depends on T002)
- [ ] T021 [P] [US3] Add `'invalid_email_theme'` to the known refusal codes in frontend/src/lib/codes.ts next to `'invalid_language'`
- [ ] T022 [P] [US3] In frontend/src/lib/i18n/en.ts and frontend/src/lib/i18n/nl.ts add under `preferences`: `emailTheme` "Email theme" / "E-mailthema", `emailThemeHint` "For every email to your account, on every device." / "Voor elke e-mail aan je account, op elk apparaat.", `emailThemeDevice` "Same as my device" / "Zelfde als mijn apparaat", `emailThemeLight` "Always light" / "Altijd licht", `emailThemeDark` "Always dark" / "Altijd donker"; and the error message for `invalid_email_theme` next to `invalid_language` (e.g. "Choose one of the three email themes." / "Kies een van de drie e-mailthema's."). `npm run check` must pass for both dictionaries
- [ ] T023 [US3] In frontend/src/routes/(settings)/settings/preferences/+page.server.ts add `emailTheme: 'device' | 'light' | 'dark'` to the exported `Settings` type, load it with the notification settings, and in the save action read form field `emailTheme` (never `theme`, which is 011's `storeTheme`) and include it in the `PUT /api/me/notification-settings` body; `invalid_email_theme` surfaces as the form error like `invalid_language` (depends on T021)
- [ ] T024 [US3] In frontend/src/routes/(settings)/settings/preferences/+page.svelte add the fieldset of contract §3 after the "Emails use this language and time zone too." note and before the home page choice: `<fieldset aria-describedby="email-theme-hint">`, legend `preferences.emailTheme`, `<p id="email-theme-hint">` with `preferences.emailThemeHint`, three radios `name="emailTheme"` values `device`/`light`/`dark` with the checked one from `data`, styled like the existing web theme radios (depends on T022, T023)
- [ ] T025 [P] [US3] In frontend/e2e/fixtures.ts add `emailTheme: 'device'` to `notificationSettings(language)` so the typed fixture compiles; `npm run test:e2e` keeps passing

**Checkpoint**: all three themes work end to end from the Preferences page.

---

## Phase 6: Polish & cross-cutting

- [ ] T026 [P] In backend/InTheKnow.Tests/EmailPreviewTests.cs send every fixture in `en` and `nl` in all three themes, addressed `{name}.{language}.{theme}@example.test` (quickstart)
- [ ] T027 [P] Amend specs/003-notification-channels/contracts/api.md: `/api/me/notification-settings` gains `emailTheme` and the `invalid_email_theme` refusal, "see specs/012"
- [ ] T028 [P] Amend specs/007-branded-email-layout/spec.md: FR-019 and the "no dark-mode version" assumption point to specs/012 (replaced for these emails)
- [ ] T029 [P] Amend specs/007-branded-email-layout/contracts/email-html.md: "light-mode metas in every kind" becomes "per theme, see specs/012 contracts/email-theme.md"
- [ ] T030 [P] Amend specs/011-dark-mode/spec.md: the "Emails are out of scope" lines point to specs/012
- [ ] T031 Run the automated checks of specs/012-dark-email/quickstart.md (`dotnet test` filters and `cd frontend && npm test && npm run check && npm run test:e2e`), then the Mailpit preview: a `device` email in a dark browser is dark and in a light browser matches the `light` email; a `dark` email in a light browser is dark; Mailpit HTML Check flags nothing beyond `prefers-color-scheme`/`color-scheme`. Then delete backend/InTheKnow.Tests/Baseline/ and the T005 baseline test
- [ ] T032 Manual mail-app checks of quickstart "Real mail apps" and "Preferences" (Apple Mail, Gmail web, Gmail app on Android, new Outlook / Outlook on the web; 320px and desktop width). Needs a person with those devices; record the outcome, which also closes 007's open T027

---

## Dependencies & execution order

- **Setup (T001)** → **Foundational (T002–T005)** → stories.
- **US1 (T006–T011)** depends on T003–T005.
- **US2 (T012–T013)** depends on US1's T009–T010 (it checks the dark block and hooks).
- **US3 (T014–T025)**: the backend API and frontend (T015, T018, T020–T025) depend only on T002; the per-theme rendering tests (T014) and send-path work (T016, T017, T019) depend on T003–T004 and, for exact metas, on T010.
- **Polish (T026–T032)** after the stories; T027–T030 are docs and can run any time.

### Within each story

Tests first and failing, then implementation. In EmailChannel.cs: hooks (T009) before the dark block (T010).

## Parallel opportunities

- T002 and T003 (different files).
- US1 tests T006–T008 are separate test methods in one file; write them together, then implement.
- In US3: T015, T016, T017, T018 (four test files); T021, T022, T025 (three frontend files); the frontend track (T021–T025) runs alongside the backend track (T019–T020).
- All doc amendments T027–T030.

```bash
# US3, two tracks at once:
Task: "T020 emailTheme in Channels.cs Get/SetSettingsAsync"
Task: "T021–T025 Preferences control, codes, dictionaries, e2e fixture"
```

## Implementation strategy

### MVP (US1 + US2, both P1)

1. T001–T005: palette record and theme type, no visible change.
2. T006–T011: default emails carry the dark version.
3. T012–T013: fallbacks stay readable.
4. Stop and check with the Mailpit preview (T026 can be pulled forward) and Apple Mail.

### Then US3

T014–T025: setting, API, send paths, Preferences. Then polish and the manual checks.

## Notes

- The light rendering must not change visibly (FR-002): the class hooks are the only addition in `light`.
- Never name the form field `theme` (R7).
- Commit after each task or logical group.
