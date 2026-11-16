---

description: "Task list for 011 dark mode"
---

# Tasks: Dark mode

**Input**: Design documents from `/specs/011-dark-mode/`

**Prerequisites**: plan.md, spec.md, research.md (R1–R10), data-model.md, contracts/theme.md, quickstart.md

**Tests**: included. The plan's Testing section and research R8 name them: Vitest for the cookie, the endpoint, the
hook and the tokens; Playwright with axe for the pages. Checks that cannot be automated (SC-003, SC-005, SC-011, the
QR scan, the bot check widget) are quickstart steps in the last phase.

**Organization**: grouped by user story. The cookie plumbing, `POST /theme` and `setTheme()` are used by both US4
(Preferences) and US5 (toggle), so they are in Phase 2. US1 needs only the dark token set. `e2e/theme.spec.ts` and
`tokens.css` are each touched by more than one story, so those tasks are not marked [P].

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: US1 to US5 (setup, foundational and polish tasks have no label)
- Paths are from the repository root. All code is in `frontend/`; the backend is not touched.

---

## Phase 1: Setup (browser test runner)

**Purpose**: the Playwright runner and the stub API that the story phases write their browser tests against.

- [X] T001 In `frontend/`, run `npm install -D @playwright/test @axe-core/playwright` (Apache-2.0 and MPL-2.0, both GPL-3.0-compatible) so `frontend/package.json` and `frontend/package-lock.json` pin them; add the script `"test:e2e": "playwright test"` to `frontend/package.json`; add `test-results/` and `playwright-report/` to `frontend/.gitignore`
- [X] T002 [P] Create `frontend/e2e/fixtures.ts`: the fixed API answers, typed with the frontend's own types from `frontend/src/lib/` (so `npm run check` fails when a type and a fixture drift apart): an admin account, two calendars, a feed with one change of each kind (cancelled, moved, new, changed) and a hidden one, a history, presets, notification settings, and whatever the settings, two-factor setup and admin pages load. Use placeholder values only (`example.com`), no real domain or token
- [X] T003 Create `frontend/e2e/api-stub.ts`: a `node:http` server that answers every GET route the frontend calls on the API from `frontend/e2e/fixtures.ts` (find the routes in `frontend/src/lib/server/` and the `+page.server.ts` / `+layout.server.ts` loads), treats any `itk_session` cookie as signed in and no cookie as signed out, and answers POST/PUT/DELETE routes with a plain success. The `language` of the account in the `/api/me` and notification-settings answers is the value of the `itk_session` cookie when that is `en` or `nl`, so a test picks the language of a signed-in page. Port from an environment variable. No dependency beyond Node (depends on T002)
- [X] T004 Create `frontend/playwright.config.ts`: `testDir: 'e2e'`, Chromium only, two `webServer` entries: the stub (`e2e/api-stub.ts`) on its own port, and the built frontend (`npm run build && npm run preview`) with `API_URL` pointing at the stub; `baseURL` set to the preview address (depends on T001, T003)
- [X] T005 [P] In `.github/workflows/ci.yml`, frontend job: after `npm run build`, add the steps `npx playwright install --with-deps chromium` and `npm run test:e2e`

**Checkpoint**: `cd frontend && npm run test:e2e` starts both servers and reports "no tests found".

---

## Phase 2: Foundational (the remembered choice)

**Purpose**: read the cookie, put the theme in the first byte of the page, and one way to change it. US4 and US5
both build on this.

**⚠️ CRITICAL**: US4 and US5 cannot start before this phase is done. US1, US2 and US3 need only T009 and T010.

### Tests (write first, see them fail)

- [X] T006 [P] Create `frontend/src/lib/theme.test.ts` with cases for `pickTheme()`: `'light'` → `'light'`, `'dark'` → `'dark'`, and `undefined`, `''`, `'device'`, `'purple'`, `'DARK'` → `null` (FR-016)
- [X] T007 [P] Create `frontend/src/routes/theme/server.test.ts` for `POST /theme` (contracts/theme.md): `theme=light` and `theme=dark` set `itk_theme` to that value with `Secure; SameSite=Lax; Path=/; Max-Age=31536000`, not `HttpOnly`; `theme=device` deletes `itk_theme`; any other or a missing value sets and deletes nothing; the answer is always `303` to `next` when it is a path on this app and to `/` otherwise (`https://evil.example`, `//evil.example`); `GET /theme` redirects to `/`
- [X] T008 [P] Add cases to `frontend/src/hooks.server.test.ts`: cookie `itk_theme=dark` → the HTML has `data-theme="dark"` and `<meta name="color-scheme" content="dark">`; `light` → `light` and `light`; no cookie or `itk_theme=purple` → `data-theme=""` and content `light dark`, and the string `purple` is nowhere in the HTML; `locals.theme` is `'dark'`, `'light'` or `null`

### Implementation

- [X] T009 [P] In `frontend/src/app.html`: change `<html lang="%lang%">` to `<html lang="%lang%" data-theme="%theme%">` and add `<meta name="color-scheme" content="%scheme%" />` in `<head>` before `%sveltekit.head%`
- [X] T010 Create `frontend/src/lib/theme.ts` with `export type Theme = 'light' | 'dark'` and `pickTheme(value: string | undefined): Theme | null` (allow-list: `light`, `dark`, else `null`); add `theme: Theme | null` to `App.Locals` and to `App.PageData` in `frontend/src/app.d.ts`; in `frontend/src/hooks.server.ts` set `locals.theme = pickTheme(cookies.get('itk_theme'))` next to `locals.lang` and extend the existing `transformPageChunk` to also replace `%theme%` with `locals.theme ?? ''` and `%scheme%` with `locals.theme ?? 'light dark'`; return `theme: locals.theme` from the load in `frontend/src/routes/+layout.server.ts` (depends on T009; makes T006 and T008 pass)
- [X] T011 Add `storeTheme(cookies, choice: string | null)` to `frontend/src/lib/theme.ts` (type-only import of `Cookies` from `@sveltejs/kit`): `light` or `dark` → `cookies.set('itk_theme', choice, { httpOnly: false, secure: true, sameSite: 'lax', path: '/', maxAge: 31_536_000 })` (same attributes as `itk_lang` in `frontend/src/routes/language/+page.server.ts`); `device` → `cookies.delete('itk_theme', { path: '/' })`; anything else → nothing. Create `frontend/src/routes/theme/+server.ts`: `POST` reads the form fields `theme` and `next`, calls `storeTheme`, and redirects `303` to `safeNext(next, url.origin)`; `GET` redirects `303` to `/`. Add `path === '/theme'` to `isPublic` in `frontend/src/lib/paths.ts` (makes T007 pass)
- [X] T012 Add `setTheme(choice: 'light' | 'dark' | 'device')` to `frontend/src/lib/theme.ts` (research R4): (1) set `document.documentElement.dataset.theme` to `choice`, or to `''` for `device`, so the change shows at once; (2) `await fetch('/theme', { method: 'POST', body: new URLSearchParams({ theme: choice }), redirect: 'manual' })`; (3) after the answer, post one message on `new BroadcastChannel('itk_theme')` when `BroadcastChannel` exists; (4) `await invalidateAll()`. In `frontend/src/routes/+layout.svelte` add a `$effect` that keeps `document.documentElement.dataset.theme` equal to `data.theme ?? ''`, and an `onMount` that listens on the `itk_theme` channel, calls `invalidateAll()` on a message, and closes the channel on destroy (research R5)
- [X] T013 [P] Add the group `theme` to `frontend/src/lib/i18n/en.ts` and `frontend/src/lib/i18n/nl.ts` with the keys `label` ("Theme" / "Thema"), `hint` ("Saved on this device only." / "Alleen op dit apparaat opgeslagen."), `device` ("Same as this device" / "Zelfde als dit apparaat"), `light` ("Light" / "Licht"), `dark` ("Dark" / "Donker"), `toDark` ("Switch to dark theme" / "Naar donker thema"), `toLight` ("Switch to light theme" / "Naar licht thema")

**Checkpoint**: `npm test` and `npm run check` pass. A hand-set cookie `itk_theme=dark` gives `<html data-theme="dark">`; nothing looks different yet.

---

## Phase 3: User Story 1 - The app is dark when my device is dark (Priority: P1) 🎯 MVP

**Goal**: with the device set to dark and nothing chosen, every page uses the design's dark colour set from the first
paint, and follows the device when it switches.

**Independent Test**: emulate dark, clear the cookies, open the feed, a filter page, a settings page and sign-in;
each is dark, with dark selects, radio buttons and scrollbars. Switch the emulation; the page follows without a reload.

### Tests (write first, see them fail)

- [X] T014 [US1] Add a test to `frontend/src/lib/theme.test.ts` that reads `frontend/src/lib/styles/tokens.css` as text, takes the declarations of the block under `@media screen and (prefers-color-scheme: dark) { :root:not([data-theme='light'])` and of the block under `@media screen { :root[data-theme='dark']`, and fails when the two lists differ or when either block is missing (research R1, R8)
- [X] T015 [US1] Create `frontend/e2e/theme.spec.ts` with: (a) `colorScheme: 'dark'`, no cookie → the computed `background-color` of `body` on `/` and on `/sign-in` is `rgb(20, 20, 19)` (`#141413`), and after `page.emulateMedia({ colorScheme: 'light' })` it is `rgb(255, 255, 255)` with no navigation (FR-001); (b) the same with an `itk_session` cookie on the changes feed (FR-002); (c) `colorScheme: 'dark'`, no cookie → the response HTML of `/` has `content="light dark"` in the `color-scheme` meta tag (SC-004)

### Implementation

- [X] T016 [US1] In `frontend/src/lib/styles/tokens.css`: add `color-scheme: light` to the first `:root` block and change nothing else in it; after the light blocks add the design's `.dark{…}` declarations twice, with the same declarations in the same order, under `@media screen and (prefers-color-scheme: dark) { :root:not([data-theme='light']) { … } }` and `@media screen { :root[data-theme='dark'] { … } }`, each with `color-scheme: dark`. Take the values from the "Dark mode" section of the design's shared stylesheet (https://claude.ai/artifact/VwQA99wR7KWbd4gxngyCwm); where it and this list differ, the design wins (FR-003): page background `#141413`; raised surfaces (`--white`) `#1c1c1a`; the neutral scale turned around so `--grey-900` is `#f5f5f2`, body text `#d4d4cf`, quiet text `#a6a6a0`; magenta `#e20e80` unchanged for fills, the title band and the wordmark; magenta text and links `#ec51a7`; status text red `#ff8d9b`, amber `#f5b651`, green `#6fd39a`, blue `#8ab8f0` on deep tints of the same hue (also redeclare `--amber-700` and `--green-700`); heavier shadows; a darker scrim; the focus ring in the lighter magenta. Token names do not change (makes T014 and T015 pass)
- [X] T017 [P] [US1] In `frontend/src/lib/components/BotCheck.svelte`: add `theme?: string` to the `opts` type of `Widget.render` and pass `theme: getComputedStyle(document.documentElement).colorScheme` in the `w.render(container, { sitekey: siteKey })` call (research R9)

**Checkpoint**: the whole app is dark on a dark device. Some spots are not legible yet; that is US2.

---

## Phase 4: User Story 2 - Everything stays readable in dark (Priority: P1)

**Goal**: every text, border, control outline, meaningful icon and the focus ring meets WCAG AA in dark (FR-004), and
the spots where the design's dark twin fails are fixed (FR-008, FR-009, FR-010).

**Independent Test**: `npm run test:e2e` reports zero colour-contrast failures on every page of FR-002 in dark, and
the token-pair test passes.

### Tests (write first, see them fail)

- [X] T018 [US2] Add to `frontend/src/lib/theme.test.ts`: a small parser that resolves a token of the dark block of `frontend/src/lib/styles/tokens.css` to a hex colour (following `var(--…)`, falling back to the light `:root` for tokens the dark block does not redeclare), the WCAG contrast formula, and a table of pairs with their minimum. 4.5:1: `--text-strong`, `--text-body`, `--text-muted` and the link colour on `--surface-page`, on `--white` and on `--surface-subtle`; each status text colour on its `-soft` tint; `--text-inverse` on `--brand`, on the dark button surface and on `--surface-dark-hover`; `--brand-on-page` on `--surface-page`; `--text-link-hover` on `--surface-page`; the toast text and toast icon (`#e3f4ea`) on the toast background; `--text-inverse` on `--brand-hover` and on `--brand-press` (primary button hover and pressed); `--text-brand` on `--brand-soft` (secondary and ghost hover); `--status-cancelled` on `--white` (the field error text, and with it the `aria-invalid` border of `Field.svelte`, which needs only 3:1). 3:1: the field border (`--grey-500`), `--border-strong` and the focus ring on `--surface-page` and on `--white`; the switch knob (`--text-inverse`) on the off track (`--grey-500`) and on the on track (`--brand`)
- [X] T019 [US2] Create `frontend/e2e/contrast.spec.ts`: one exported list of the pages of FR-002 (signed-out home, sign-in both steps, sign-up, account confirm, confirmation, email change, forgot password, reset, invite, changes feed, history page, calendars, a calendar's overview, filter and notification pages, presets, a preset's edit page, every settings page including two-factor setup, notifications, every admin page, about, a not-found address) with whether each needs the `itk_session` cookie. Loop over page × viewport (390×844, 1280×800) × language (`en`, `nl`: the `itk_lang` cookie on signed-out pages, the value of the `itk_session` cookie on signed-in pages) with `colorScheme: 'dark'`, and run `new AxeBuilder({ page }).withRules(['color-contrast']).analyze()`; expect zero violations (SC-001, SC-002). On the feed also open the history card; on a page with a confirm dialog open it; on Preferences press Save so the toast shows, and run axe again each time. For the second sign-in step set an `itk_pending` cookie with path `/sign-in` and open `/sign-in/code`. Open the confirm, email change, reset and invite pages with `?token=placeholder`

### Implementation

- [X] T020 [US2] In `frontend/src/lib/styles/tokens.css` add three aliases (contracts/theme.md). In the light `:root` alias block: `--text-link-hover:var(--brand-press)`, `--brand-on-page:var(--brand)`, `--surface-dark-hover:var(--grey-700)` (today's values, so light does not change). In both dark blocks, identical: `--text-link-hover:var(--magenta-200)`, `--brand-on-page:var(--magenta-300)`, `--surface-dark-hover:#474743`
- [X] T021 [P] [US2] In `frontend/src/lib/styles/base.css` line 6: `a:hover{color:var(--brand-press)}` → `a:hover{color:var(--text-link-hover)}`
- [X] T022 [P] [US2] In `frontend/src/lib/components/Switch.svelte` (line 86): the knob's `background: var(--white)` → `background: var(--text-inverse)` so it stays white on both tracks
- [X] T023 [P] [US2] In `frontend/src/lib/components/Toast.svelte`: text `color: var(--white)` (line 44) → `var(--text-inverse)`; icon `color: var(--green-100)` (line 51) → the fixed `#e3f4ea`
- [X] T024 [P] [US2] In `frontend/src/lib/components/PhoneNav.svelte` (line 64): the active item's `color: var(--brand)` → `var(--brand-on-page)`
- [X] T025 [P] [US2] In `frontend/src/lib/components/Button.svelte` (lines 101–102): the dark button's hover `background` and `border-color` `var(--grey-700)` → `var(--surface-dark-hover)`; check that its pressed state still gives at least 4.5:1 for the label in dark, and use the same alias if not
- [X] T026 [P] [US2] In `frontend/src/routes/(settings)/settings/security/two-factor/+page.svelte` (line 90): the QR tile's `background: var(--white)` → the fixed `#ffffff`, and make sure the tile has padding of at least four QR modules as quiet zone (FR-010)
- [X] T027 [US2] Apply the rule of FR-008 to every other use of `var(--white)`: in `frontend/src/lib/components/Button.svelte`, `Dialog.svelte`, `Header.svelte`, `RuleEditor.svelte`, `HistoryCard.svelte`, `History.svelte` and `Field.svelte`, where `--white` is the colour of text or an icon on a coloured fill (magenta or a solid status colour) change it to `var(--text-inverse)`; where it is a surface leave it. Light must render the same (`--text-inverse` is white in light)
- [X] T028 [US2] In `frontend/src/lib/components/History.svelte` decide the open point of research R7: `--grey-400` on `--grey-100` (lines 216/220 and 338/341) is 2.61:1 in dark. If the mark carries meaning (it is the only sign of a state) change it to `var(--grey-500)`; if it is decoration next to text that says the same, leave it and say so in a one-line comment
- [X] T029 [US2] Run `cd frontend && npm test && npm run test:e2e` and fix every remaining pair or page that T018 or T019 reports (hover, pressed, disabled and error states, the hidden-change row with its dashed outline, the neutral badges "low priority" and "hidden"), in the component that owns the colour and with an existing token where one fits

**Checkpoint**: zero contrast failures in dark, both languages, both widths.

---

## Phase 5: User Story 3 - Light stays exactly as it is (Priority: P1)

**Goal**: a light device, or one with no preference, sees no change; print is light whatever the theme.

**Independent Test**: screenshots of the FR-002 pages in light, on `main` and on this branch, show no difference.

- [X] T030 [US3] Add to `frontend/e2e/theme.spec.ts`: (a) `colorScheme: 'light'` and `colorScheme: 'no-preference'`, no cookie → the `body` background is `rgb(255, 255, 255)` (US3-1, US3-2); (b) `page.emulateMedia({ media: 'print', colorScheme: 'dark' })`, once with no cookie and once with `itk_theme=dark` → the `body` background is white and the body text colour is the light theme's (FR-012)
- [X] T031 [US3] Check `git diff main -- frontend/src/lib/styles/tokens.css`: inside the existing light `:root` blocks the only added declarations are `color-scheme: light` and the three aliases of T020, and no existing value changed (FR-007). Fix it if anything else moved

**Checkpoint**: US1, US2 and US3 together are the roadmap item: dark by device, readable, light untouched.

---

## Phase 6: User Story 4 - I pick the theme myself, on this device (Priority: P2)

**Goal**: Settings › Preferences offers "Same as this device", "Light" and "Dark". The choice applies at once, wins
over the device on every page, and is remembered on this browser only.

**Independent Test**: on a light device choose "Dark" in Preferences; the app turns dark at once, stays dark after a
reload and after signing out; "Same as this device" removes the cookie.

### Tests (write first, see them fail)

- [X] T032 [US4] Add to `frontend/e2e/theme.spec.ts`: (a) cookie `itk_theme=dark` on a light device, and `itk_theme=light` on a dark device → the response HTML has `data-theme="dark"` / `"light"` and the `body` background is right (FR-006, FR-011); (b) cookie `itk_theme=purple` on a dark device → dark, `data-theme=""` (FR-016); (c) signed in on `/settings/preferences` with no cookie → "Same as this device" is checked; choosing "Dark" then "Light" changes the background each time with no navigation and sets the cookie; choosing "Same as this device" removes the cookie; after a reload the chosen option is still checked (US4); (d) with `itk_theme=dark`, a page without `itk_session` (`/sign-in`) is dark (US4-5); (e) two pages in one browser context, one on `/settings/preferences`: a change in the other shows in the first within one second and its checked radio button matches (FR-022, SC-012); (f) a context with `javaScriptEnabled: false`: pick "Dark" on Preferences and press Save → the page comes back with `data-theme="dark"` (FR-021)
- [X] T033 [P] [US4] Add a Vitest case for the `save` action of `frontend/src/routes/(settings)/settings/preferences/+page.server.ts` (next to it, or in the existing test file for that route if there is one): form field `theme=dark` sets `itk_theme`, `theme=device` deletes it, a missing `theme` field changes nothing, `event.locals.theme` is the new value after the action, and `theme` is not part of the body sent to the API (FR-020, SC-008)

### Implementation

- [X] T034 [US4] In `frontend/src/routes/(settings)/settings/preferences/+page.svelte`: after the "Home page" field, inside the preferences form, add `<fieldset aria-describedby="theme-hint">` with `<legend>{t.theme.label}</legend>`, `<p id="theme-hint" class="muted">{t.theme.hint}</p>` and three radio buttons `name="theme"` with the values `device`, `light`, `dark` and the labels `t.theme.device`, `t.theme.light`, `t.theme.dark`, using the existing `.choice` class of `frontend/src/lib/styles/base.css`. The checked one is `data.theme ?? 'device'` (from the root layout data, so it follows another tab). `onchange` calls `setTheme(value)` from `$lib/theme` (FR-011, FR-015)
- [X] T035 [US4] In the `save` action of `frontend/src/routes/(settings)/settings/preferences/+page.server.ts`: when the form has a `theme` field, before the API calls, call `storeTheme(cookies, field(data, 'theme'))` and set `event.locals.theme = pickTheme(field(data, 'theme'))`, both from `$lib/theme`. The action answers without a redirect, so the page is rendered in the same request and would otherwise come back in the old theme. The theme is stored even when the API save fails; it does not belong to the account. Do not add it to the values sent to the API (the no-script path, FR-021; makes T033 pass)

**Checkpoint**: a signed-in person can pick and keep a theme, with and without script, across tabs.

---

## Phase 7: User Story 5 - A visitor switches the theme, and it sticks through sign-up (Priority: P2)

**Goal**: every signed-out page has a toggle in the header. One press switches away from the theme shown, at once, and
remembers it; the same choice shows in Preferences after sign-up or sign-in.

**Independent Test**: signed out on a light device, press the toggle on the home page: dark at once, cookie
`itk_theme=dark`, the toggle shows a sun; still dark after a reload and on `/account/sign-up`.

### Tests (write first, see them fail)

- [X] T036 [US5] Add to `frontend/e2e/theme.spec.ts`: (a) signed out, light device, on `/`, `/sign-in`, `/about` and a not-found address: exactly one toggle button is visible, named "Switch to dark theme" (and its Dutch text with `itk_lang=nl`), at least 44×44px (FR-018); (b) pressing it gives the dark background with no navigation, sets `itk_theme=dark`, and the visible button is now "Switch to light theme"; after a reload and on a new page of the same context it is still dark (US5-2, US5-4); (c) dark device, no cookie: the visible button is "Switch to light theme" and a press gives light (edge case); (d) the toggle can be focused with Tab and pressed with Enter (US5-8); (e) with `itk_session`, the header has no toggle, and `/settings/preferences` shows "Dark" checked after a press made while signed out (FR-019); (f) `javaScriptEnabled: false`: a press loads the same path again with `data-theme="dark"` (FR-021); (g) at 390px, signed out, `document.documentElement.scrollWidth` is not larger than the viewport width (edge case)

### Implementation

- [X] T037 [P] [US5] In `frontend/src/lib/styles/base.css` add the visibility rules with the same two selectors as the dark token blocks (research R6): `.theme-dark-only{display:none}`; under `@media screen and (prefers-color-scheme: dark)`: `:root:not([data-theme='light']) .theme-light-only{display:none}` and `:root:not([data-theme='light']) .theme-dark-only{display:inline-flex}`; under `@media screen`: `:root[data-theme='dark'] .theme-light-only{display:none}` and `:root[data-theme='dark'] .theme-dark-only{display:inline-flex}`
- [X] T038 [US5] In `frontend/src/lib/components/Header.svelte`, in the signed-out branch, before the `/language` form: add `<form method="POST" action="/theme">` with a hidden `next` field holding the current path (the same value the language form sends) and two icon buttons, `<button name="theme" value="dark" class="plain theme-light-only" aria-label={t.theme.toDark}>` with the `Moon` icon and `<button name="theme" value="light" class="plain theme-dark-only" aria-label={t.theme.toLight}>` with the `Sun` icon, both from `@lucide/svelte`, each 44×44px. `onsubmit`: `event.preventDefault()` and `setTheme(event.submitter.value)` from `$lib/theme`. Not rendered when signed in. At 390px the wordmark, toggle, language switch, sign in and sign up must fit without sideways scrolling; reduce the header gap at phone width if they do not (depends on T037; makes T036 pass)

**Checkpoint**: all five stories work; the visitor toggle and Preferences write the same cookie.

---

## Phase 8: Polish & Cross-Cutting Concerns

- [X] T039 [P] In `specs/010-event-change-history/spec.md` FR-019 (line 259): remove the sentence "The app has a light theme only; a dark theme is out of scope."
- [X] T040 [P] Add the `itk_theme` row of `specs/011-dark-mode/data-model.md` to the cookie table of `specs/005-sveltekit-frontend/data-model.md`
- [X] T041 Run `cd frontend && npm run check && npm test && npm run build && npm run test:e2e`; all must pass with no warning
- [ ] T042 Walk the manual scenarios of `specs/011-dark-mode/quickstart.md` that the browser tests do not cover: 1 (each dark page against its dark screen in the design, SC-005), 2 (Slow 3G, no light frame), 5 (focus ring), 6 (switch, phone bar, dark button, toast, hidden change, badges, and scan the QR code with a phone), 11 (sign up with a real inbox, SC-011), 13, 17, 19 (bot check widget)
- [X] T043 Light regression check (quickstart scenario 7, SC-003): with the page list exported from `frontend/e2e/contrast.spec.ts` and `colorScheme: 'light'`, take full-page screenshots once on `main` and once on this branch on the same machine, compare them, and fix any difference. Do not commit the images or the script
- [X] T044 Before pushing: `git diff origin/main | grep -nE '[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+'` and a search for the real domain; the fixtures and specs must hold placeholders only (constitution, Principle II)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: no dependencies. Needed by every `e2e/` task.
- **Phase 2 (Foundational)**: T009 → T010 → T011 → T012 (the last three share `lib/theme.ts`). T013 is independent.
- **US1 (Phase 3)**: needs T009 and T010 (the `data-theme` attribute and the meta tag) and Phase 1.
- **US2 (Phase 4)**: needs US1 (T016); the fixes are fixes to the dark set.
- **US3 (Phase 5)**: needs US1 and T020; it checks what they left alone.
- **US4 (Phase 6)**: needs Phase 2 and US1. Independent of US5.
- **US5 (Phase 7)**: needs Phase 2 and US1. Independent of US4, except case (e) of T036, which opens the Preferences fieldset of T034.
- **Polish (Phase 8)**: after the stories that are being shipped.

### Shared files (never in parallel)

- `frontend/src/lib/styles/tokens.css`: T016 → T020
- `frontend/src/lib/theme.ts`: T010 → T011 → T012
- `frontend/src/lib/theme.test.ts`: T006 → T014 → T018
- `frontend/e2e/theme.spec.ts`: T015 → T030 → T032 → T036
- `frontend/src/lib/styles/base.css`: T021, T037
- `frontend/src/lib/components/Button.svelte`: T025 → T027

### Parallel Opportunities

- Phase 1: T002 and T005 next to T001.
- Phase 2: T006, T007, T008 together; T009 and T013 together.
- US2: T021 to T026 together, once T020 is in.
- Polish: T039 and T040 together.

## Parallel Example: User Story 2

```bash
# After T020 (the aliases), six one-file fixes at once:
Task: "a:hover uses --text-link-hover in frontend/src/lib/styles/base.css"
Task: "Switch knob uses --text-inverse in frontend/src/lib/components/Switch.svelte"
Task: "Toast text and icon stay light in frontend/src/lib/components/Toast.svelte"
Task: "Active item uses --brand-on-page in frontend/src/lib/components/PhoneNav.svelte"
Task: "Dark button hover uses --surface-dark-hover in frontend/src/lib/components/Button.svelte"
Task: "QR tile on fixed #ffffff in frontend/src/routes/(settings)/settings/security/two-factor/+page.svelte"
```

---

## Implementation Strategy

### MVP (the roadmap item)

US1 alone is dark but not safe to ship: it is applied to everyone with a dark device, and some spots are unreadable
until US2. The smallest shippable set is **Phases 1 to 5 (US1 + US2 + US3)**.

1. Phase 1 and Phase 2
2. US1 → the app is dark
3. US2 → it is readable; US3 → light and print are proven unchanged
4. **Stop and validate**: `npm run test:e2e`, quickstart scenarios 1 to 8

### Incremental Delivery

5. US4 → the Preferences choice (quickstart 12 to 15)
6. US5 → the visitor toggle (quickstart 9 to 11, 18)
7. Phase 8

## Notes

- Commit after each task or logical group; the dark token set (T016) and its test (T014) go in one commit.
- The dark values in T016 come from the design; the hex values listed there are the spec's summary (FR-003) and lose to the design where they differ.
- No backend, database or API change. If a task seems to need one, stop: the spec rules it out (FR-014, FR-020).
- `error.html` (SvelteKit's crash page) and emails stay as they are (research R10; emails are spec 012).
