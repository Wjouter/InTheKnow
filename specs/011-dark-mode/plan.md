# Implementation Plan: Dark mode

**Branch**: `011-dark-mode` | **Date**: 2026-11-16 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/011-dark-mode/spec.md`

## Summary

The app gets a dark theme by declaring the design's dark colour set a second time on `:root`. Every component
already takes its colours from the tokens in `tokens.css`, and the design's dark set turns the neutral scale
around, so most components need no change.

- **Following the device** is pure CSS: `@media (prefers-color-scheme: dark)`. No script (FR-001, FR-021).
- **A chosen theme** is one cookie, `itk_theme` (`light` or `dark`; absent means "same as this device"), set by the
  server like `itk_lang`. `hooks.server.ts` writes it into `<html data-theme="…">` while it renders, so the first
  byte of every page already has the right theme (FR-006). There is no inline script and the CSP stays as it is.
- **Changing it** is a `POST /theme`. Without script it is a normal form post that redirects back. With script a
  small `setTheme()` sets the attribute at once, posts in the background, and tells the other tabs over a
  `BroadcastChannel` (FR-021, FR-022).
- **Controls**: two submit buttons in the signed-out header (moon and sun, CSS shows the one that applies), and
  three radio buttons on Settings › Preferences.
- **Five spots** where the design's dark twin is not legible get a fix: switch knob, phone bar active item, dark
  button hover, toast, QR tile (FR-009, FR-010).

No backend change and no database change. Two new development dependencies, for the browser tests (research R8):
`@playwright/test` and `@axe-core/playwright`. Nothing new ships to users.

## Technical Context

**Language/Version**: TypeScript, Svelte 5 / SvelteKit 2 (frontend only). The .NET backend is not touched.

**Primary Dependencies**: none new at run time. Uses SvelteKit, `@lucide/svelte` (moon and sun icons), and browser
features: `prefers-color-scheme`, `color-scheme`, `BroadcastChannel`. New development dependencies:
`@playwright/test` (Apache-2.0) and `@axe-core/playwright` (MPL-2.0), both GPL-3.0-compatible.

**Storage**: one browser cookie, `itk_theme`. Nothing in PostgreSQL, nothing on the account (FR-014, FR-020).

**Testing**:

- Vitest (`npm test`) for the cookie reading, the `/theme` endpoint, the `%theme%` replacement in the hook, the two
  dark token blocks being equal, and the contrast of the dark token pairs (this covers borders and the focus ring,
  which axe does not measure).
- Playwright with axe (`npm run test:e2e`, new) in Chromium, against the built frontend and a stub API with fixed
  answers: a contrast check of every page of FR-002 in dark, and the theme behaviour (first paint, toggle,
  Preferences, reload, two tabs, no script, print). Runs in CI.
- `npm run check` for types and the dictionary keys.
- By hand ([quickstart.md](./quickstart.md)): the comparison with the design, the light screenshots against
  `main`, the sign-up walk, and the QR scan.

**Target Platform**: the browsers the app supports today. Dark needs `prefers-color-scheme` (all browsers since
2020); a browser without it shows light (US3-2).

**Project Type**: web application (SvelteKit frontend, .NET API). This feature is in `frontend/` only.

**Performance Goals**: no extra request on page load and no script before first paint. A theme change shows in the
same tab at once, and in other tabs within one second (SC-012).

**Constraints**: light must not change (FR-007); CSP `script-src 'self'` stays, so no inline theme script; works
without script (FR-021); WCAG AA contrast in dark (FR-004); print stays light (FR-012).

**Scale/Scope**: 2 stylesheets, about 12 components and pages with a colour fix or a control, 1 new endpoint, 1 new
small client module, 2 dictionaries. All pages of FR-002 are covered through the tokens. The browser tests add a
config file, a stub API, a fixture file, 2 spec files and one CI job.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Check | Result |
|---|---|---|
| I. Simplicity First | Reuses the token stylesheet, the `itk_lang` cookie pattern and `safeNext`. Native CSS and `BroadcastChannel` do the work. No run-time dependency, no build step, no theme store. | PASS |
| I. New dependency | Two development dependencies for browser tests, asked for by the maintainer (2026-11-16). Justified in Complexity Tracking. | PASS (justified) |
| I. No speculative code | No per-account theme, no third "device" state on the visitor toggle, no email theme (all out of scope in the spec). | PASS |
| II. Minimal data | One cookie with `light` or `dark`. Not personal data, not sent to the API, not stored with the account. Named in [data-model.md](./data-model.md). | PASS |
| II. Validate at trust boundary | `POST /theme` accepts only `light`, `dark`, `device`; anything else changes nothing. `next` goes through the existing `safeNext`. The cookie is read through an allow-list, so its value can never reach the HTML unchecked. | PASS |
| II. Escaped output | No raw HTML. The attribute value is one of three fixed strings chosen by the server. | PASS |
| II. CSRF | `/theme` is a POST and falls under the existing `Sec-Fetch-Site` check in `hooks.server.ts`. | PASS |
| Technology constraints | Svelte/SvelteKit only. The two new packages are Apache-2.0 and MPL-2.0, both GPL-3.0-compatible, and are pinned in `package-lock.json`. | PASS |
| Workflow: automated check for non-trivial logic | Unit and browser tests listed under Testing above; both run in CI. | PASS |

**Post-design re-check (after Phase 1)**: still PASS. The design added no run-time dependency, layer or stored
data. The spec assumes automated accessibility checks already exist to run a second time (SC-002); the repository
had none that open a browser. The maintainer chose to add them (research R8), which is the one justified deviation
below.

## Project Structure

### Documentation (this feature)

```text
specs/011-dark-mode/
├── plan.md              # This file
├── research.md          # Phase 0: decisions R1 to R10
├── data-model.md        # Phase 1: the theme choice and its cookie
├── quickstart.md        # Phase 1: how to check the feature by hand
├── contracts/
│   └── theme.md         # Phase 1: cookie, POST /theme, markup, tokens
└── tasks.md             # Phase 2 (/speckit-tasks, not made here)
```

### Source Code (repository root)

```text
frontend/src/
├── app.html                                   # data-theme="%theme%", <meta name="color-scheme">
├── app.d.ts                                   # Locals.theme, PageData.theme
├── hooks.server.ts                            # read itk_theme, fill %theme% and %scheme%
├── hooks.server.test.ts                       # + theme cases
├── lib/
│   ├── theme.ts                               # NEW: pickTheme(), setTheme(), listen for other tabs
│   ├── theme.test.ts                          # NEW: pickTheme, token blocks equal, dark contrast pairs
│   ├── paths.ts                               # /theme is public
│   ├── styles/tokens.css                      # dark colour set, twice (device, chosen); 3 new aliases
│   ├── styles/base.css                        # link hover alias, moon/sun visibility
│   ├── i18n/en.ts, nl.ts                      # theme texts
│   └── components/
│       ├── Header.svelte                      # theme toggle for visitors
│       ├── Switch.svelte                      # knob stays white
│       ├── Toast.svelte                       # text and icon stay light
│       ├── PhoneNav.svelte                    # active item colour
│       ├── Button.svelte                      # dark button hover
│       └── BotCheck.svelte                    # pass the theme to the widget
└── routes/
    ├── +layout.server.ts                      # theme in page data
    ├── +layout.svelte                         # keep data-theme in step with page data, listen to other tabs
    ├── theme/+server.ts                       # NEW: POST /theme
    ├── theme/server.test.ts                   # NEW
    └── (settings)/settings/
        ├── preferences/+page.svelte           # theme radio buttons
        ├── preferences/+page.server.ts        # save also stores the theme (the no-script path)
        └── security/two-factor/+page.svelte   # QR on a fixed white tile

frontend/
├── package.json, package-lock.json            # 2 dev dependencies, script test:e2e
├── playwright.config.ts                       # NEW: Chromium, starts the stub API and the built frontend
└── e2e/
    ├── api-stub.ts                            # NEW: node:http server with fixed answers for the API's GET routes
    ├── fixtures.ts                            # NEW: the answers, typed with the frontend's own types
    ├── contrast.spec.ts                       # NEW: axe colour contrast, every page of FR-002, in dark
    └── theme.spec.ts                          # NEW: first paint, toggle, Preferences, tabs, no script, print

.github/workflows/ci.yml                       # frontend job: install Chromium, run test:e2e

specs/010-event-change-history/spec.md         # FR-019: drop "light theme only" (spec assumption, last line)
```

**Structure Decision**: frontend only, inside the existing SvelteKit layout. One new route folder (`theme/`) next to
`language/`, and one new module (`lib/theme.ts`). The browser tests live in a new `frontend/e2e/` folder, outside
`src/`, so Vitest (`src/**/*.test.ts`) does not pick them up. Everything else is an edit to an existing file.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| 2 new dev dependencies: `@playwright/test`, `@axe-core/playwright` | SC-002 asks for an automated accessibility check of every page in dark. Contrast depends on which colour ends up on which background in the rendered page, and first paint, tabs and no-script behaviour only exist in a browser. | The token-pair unit test alone checks the pairs someone thought to list, not the pages. A manual audit is not repeated on later changes, and dark is applied to everyone with a dark device. |
| A stub API (`e2e/api-stub.ts`) | The frontend calls the API from the server, so the browser cannot intercept those calls. The pages need data to show their components. | The real backend needs PostgreSQL, a confirmed account and detected timetable changes, which need a calendar feed that changes between two checks. That is a far larger and slower setup for a colour test. |
