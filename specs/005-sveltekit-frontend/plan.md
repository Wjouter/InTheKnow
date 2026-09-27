# Implementation Plan: Web Frontend

**Branch**: `claude/sveltekit-frontend-32b635` | **Date**: 2026-09-27 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/005-sveltekit-frontend/spec.md`, and the user's design system
("InTheKnow Design System", claude.ai design project `c2ef9d00-a246-4489-b5c3-2a49171b9a17`). The spec decides what
the app does. The design system decides how it looks.

## Summary

A SvelteKit app in `frontend/`. It is the only thing browsers talk to, and it calls the existing API from its
server side.

- **Rendering** (research R2): every page is server-rendered. Every change is a `POST` form action with progressive
  enhancement. One server-only module calls the API, so the token and the API address can't reach browser code.
  Sentences, dates and messages are built on the server.
- **Session** (R3 to R6):
  - The browser session is the API token in an `HttpOnly; SameSite=Lax; Secure` cookie, with no server-side store.
  - A second, 10-minute cookie carries the awaiting-code token of two-step sign-in.
  - The sign-in gate lives in `hooks.server.ts`, so it also covers form actions.
  - Cross-site requests are stopped by a `Sec-Fetch-Site` check in `handle` plus `SameSite=Lax`. SvelteKit's
    `Origin` check is turned off, because `no-referrer` makes browsers send `Origin: null` on the app's own form
    posts (R4).
  - The browser's address and `User-Agent` are passed on to the API.
- **Security headers** (R15 to R17):
  - `no-store`, `no-referrer`, frame denial and a CSP on every response, data responses included.
  - The shareable invite token moves from the address into a short-lived cookie before any bot check script loads.
  - The access log has no query strings, addresses, users or tokens.
- **Language and messages** (R9, R10): English and Dutch as typed dictionaries. `svelte-check` fails on a missing
  translation, or on an API reason, check failure or channel failure code without a message.
- **Changes** (R11, R12): a newest-first feed grouped per event and check, one readable sentence per kind and field,
  dates in the user's zone.
- **Filters** (R14): one rule editor for calendars, personal presets and global presets, with `datalist` value
  pickers.
- **API additions** (R13, [contracts/api.md](./contracts/api.md)), the four named in FR-045:
  - newest-first paging of `/api/me/changes`;
  - `account.home_page` with `PUT /api/me/settings`;
  - a `rule` index on rule validation errors;
  - `followers` on personal presets.

  The last two were decided with the user during planning, and FR-045 was updated to name them.
- **Design** (R18):
  - The design system's tokens are copied verbatim.
  - Its React reference components are rebuilt as accessible Svelte components (native `<dialog>`, real checkbox
    switches, tab links).
  - Fonts are self-hosted (user decision), and Lucide comes from a package instead of a CDN.
  - Two darker text shades fix badge contrast.
  - Timetable components are not built, because there is no timetable view in the spec.

## Technical Context

**Language/Version**: TypeScript 5 on Node 24 LTS (frontend); C# 14 on .NET 10 (backend additions, unchanged
toolchain)

**Primary Dependencies**:
- Frontend (all pinned in `frontend/package-lock.json`):
  - `@sveltejs/kit` 2, `svelte` 5, `@sveltejs/adapter-node`, `vite`, `@sveltejs/vite-plugin-svelte`;
  - dev: `typescript`, `svelte-check`, `vitest`.
- **Added** beyond the SvelteKit starter:
  - `@lucide/svelte` (ISC), the design system's icon set;
  - `uqr` (MIT), the QR code for 2FA setup;
  - `@fontsource/lato` and `@fontsource/jetbrains-mono` (OFL-1.1), the self-hosted design system fonts.
- Backend: no new package.

**Storage**: none in the frontend (FR-002). Backend: migration `005_frontend.sql` adds `account.home_page`
([data-model.md](./data-model.md)).

**Testing**:
- Frontend: `svelte-check` (types and translation completeness) and `vitest` unit tests of the server modules and
  hooks (R19).
- Backend: xunit.v3, with the existing suites extended for the four additions.
- The manual walkthrough is in [quickstart.md](./quickstart.md).

**Target Platform**: a Node server next to the API on the same host, behind the TLS reverse proxy. Current mobile and
desktop browsers with scripting enabled.

**Project Type**: web application. It adds the frontend to the existing backend.

**Performance Goals**: SC-011, where 95% of pages show within 2 s. Per page:
- 2 API calls in parallel for the signed-in user (`/api/me`, `/api/me/notification-settings`);
- 1 to 3 in parallel for the page;
- all on loopback.

A changes page is 50 items.

**Constraints**:
- No token in any place the browser can see (SC-003).
- No `{@html}` (SC-010).
- 360 px without horizontal scrolling, and full keyboard use (SC-007).
- No personal data in the frontend's logs (FR-047).
- No third-party request, except the operator's optional bot check provider.

**Scale/Scope**:
- A whole school: tens of thousands of accounts, mostly on phones.
- About 30 pages ([contracts/pages.md](./contracts/pages.md)).
- Thousands of rows in the admin lists, always paged.

No NEEDS CLARIFICATION items remain. All decisions are in [research.md](./research.md).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Rule | Status | How |
|---|---|---|
| I. Smallest implementation, no speculative code | PASS | No server-side session store, no i18n library, no UI kit, no state library, no API proxy route, no client-side API calls (R2, R3, R10, R18). Only the design system components that pages use are built; timetable ones are skipped. No E2E framework until the manual walkthrough stops being enough (R19). API additions only where the spec names them (R13). |
| I. Preference order | PASS | The platform comes first: native `<dialog>`, `<datalist>`, `<select>` of `Intl.supportedValuesOf`, `Intl.DateTimeFormat`, `requestSubmit()`, a download link for recovery codes. Then the platform and framework: the browser's `Sec-Fetch-Site`, SvelteKit's CSP nonces, cookies and form actions (R4, R8, R12, R14, R16). The existing backend patterns are reused for the additions: keyset batching and `FilterRules.Parse`. |
| I. Deviations justified | PASS | See Complexity Tracking: four npm packages and one DB column. |
| II. Store only required data, name each personal field | PASS | [data-model.md](./data-model.md) names each cookie and why it exists. None holds readable personal data. `home_page` is a display preference. The frontend keeps no copy of API data beyond one page view (FR-002). |
| II. No committed secrets | PASS | The frontend has no secrets. Its config is addresses only ([contracts/configuration.md](./contracts/configuration.md)). |
| II. Validate input at trust boundaries | PASS | The API stays the validator of every field. The frontend server parses what it forwards: rules JSON, `next`, `itk_lang`, and query parameters for paging. It passes cookies on only as opaque strings (R6, R14). |
| II. Escape browser output | PASS | Svelte escapes by default. `{@html}` is banned by a test (SC-010). The QR code is an `<img>` data URL, not injected markup. |
| II. Established auth libraries, no hand-rolled crypto | PASS | Sessions and 2FA stay in the API (004). The frontend handles no crypto, only HttpOnly cookies through SvelteKit's `cookies` API. |
| II. HTTPS outside development | PASS | The cookies are `Secure`. The reverse proxy terminates TLS and sends HSTS ([configuration.md](./contracts/configuration.md)). |
| Tech: SvelteKit frontend, .NET backend, PostgreSQL | PASS | As fixed by the constitution. The new column has a `CHECK` constraint. |
| Tech: GPL-3.0-compatible licences, lockfile | PASS, with one note | MIT, ISC and Apache-2.0 dependencies are GPL-3.0-compatible. The two font packages are OFL-1.1: see Complexity Tracking. `package-lock.json` is committed. |
| Workflow: checks as the gate; tests for non-trivial logic | PASS (planned) | `npm run check`, `npm test`, `npm run build` and `dotnet test`. Each non-trivial module has a unit test (R19, [quickstart § 1](./quickstart.md)). |

**Post-design re-check (after Phase 1)**: PASS.
- The design adds one directory (`frontend/`), four npm packages beyond the starter, one DB column, one route
  (`PUT /api/me/settings`), two query parameters, and two response members.
- It adds no service, no store, and no background process.

## Project Structure

### Documentation (this feature)

```text
specs/005-sveltekit-frontend/
├── spec.md
├── plan.md              # This file
├── research.md          # Phase 0
├── data-model.md        # Phase 1
├── quickstart.md        # Phase 1
├── contracts/           # Phase 1
│   ├── api.md           # the four API additions
│   ├── pages.md         # addresses, actions, SC-002 coverage table
│   └── configuration.md # env, reverse proxy, headers, logs
├── checklists/
└── tasks.md             # Phase 2 (/speckit-tasks)
```

### Source Code (repository root)

```text
frontend/                                   # NEW
├── package.json, package-lock.json         # scripts: dev, build, check, test
├── svelte.config.js                        # adapter-node, kit.csp (R16)
├── vite.config.ts                          # vitest config
├── tsconfig.json
└── src/
    ├── app.html                            # <html lang="%lang%">
    ├── app.d.ts                            # Locals: token, me, tz, lang
    ├── hooks.server.ts                     # CSRF check, gate, me + tz load, lang, headers, access log, handleError (R3–R6, R9, R16, R17)
    ├── hooks.server.test.ts
    ├── no-raw-html.test.ts
    ├── lib/
    │   ├── styles/tokens.css               # design system tokens, verbatim + --amber-700/--green-700 (R18)
    │   ├── styles/base.css                 # design system base + forced-colours focus fix
    │   ├── components/                     # Button, Field, Switch, Badge, Card, Dialog, Toast, TabNav, Header,
    │   │                                   # PhoneNav, PageIntro, ChangeGroup, CalendarStatus, RuleEditor, StepUp,
    │   │                                   # BotCheck, PasswordRules
    │   ├── i18n/en.ts, i18n/nl.ts, i18n/index.ts   # typed dictionaries (R10)
    │   ├── codes.ts                        # Reason, CheckFailure unions; pattern-code mapping
    │   ├── enhance.ts                      # pending flag + step-up resubmit (R8)
    │   ├── codes.test.ts                   # contract reasons ⊆ Reason union (SC-009)
    │   ├── next.ts, next.test.ts           # safeNext (R6)
    │   └── server/
    │       ├── api.ts, api.test.ts         # API client, error mapping, cookies (R3, R5, R7)
    │       ├── actions.ts                  # shared: reauthenticate, fail() helpers, bot check field
    │       ├── changes.ts, changes.test.ts # sentences, grouping, tones (R11)
    │       ├── rules.ts                    # rule sentences, inactive reasons (R14)
    │       └── format.ts, format.test.ts   # dates, waits (R12)
    └── routes/
        ├── +layout.svelte, +layout.server.ts, +error.svelte, +page.svelte, +page.server.ts   # / (visitor or home)
        ├── sign-in/, sign-in/code/, sign-out/, language/
        ├── account/{sign-up,confirmation,confirm,forgot-password,reset,email,invite}/
        ├── changes/
        ├── calendars/, calendars/[id]/{+layout,+page,filter,notifications}/
        ├── presets/, presets/[id]/
        ├── (settings)/+layout.svelte, (settings)/settings/{account,security,security/two-factor,preferences}/,
        │   (settings)/notifications/
        └── admin/+layout.svelte, admin/{accounts,sign-up,presets,presets/[id],calendars,email}/

backend/
├── InTheKnow/
│   ├── Migrations/005_frontend.sql         # NEW: account.home_page
│   ├── Api.cs                              # CHANGED: /api/me/changes order=newest, before (R13.1)
│   ├── AccountApi.cs                       # CHANGED: homePage in GET /api/me; PUT /api/me/settings (R13.2)
│   ├── FilterRules.cs, FilterApi.cs        # CHANGED: Parse returns the rule index; Problem gains `rule` (R13.3)
│   └── Presets.cs                          # CHANGED: followers on personal presets (R13.4)
└── InTheKnow.Tests/
    └── ApiTests, AccountTests, FilterRulesTests, PresetsTests, DbTests   # CHANGED: tests for the additions
```

Other documents updated during implementation:
- `README.md`: the frontend in Features and Run it, and the removal of the "frontend doesn't exist yet" workaround.
- 001 and 002 `contracts/api.md`: a pointer to the additions here.
- `.gitignore`: already covers `node_modules/`, `.svelte-kit/` and `build/`.

**Structure Decision**:
- A web application with two top-level projects: the existing `backend/` and a new `frontend/`.
- In `frontend/`, server-only code lives in `$lib/server` (enforced by SvelteKit). Shared UI lives in
  `$lib/components`, with one file per design system component that a page uses.
- Routes mirror the addresses of [contracts/pages.md](./contracts/pages.md). A `(settings)` route group lets
  `/settings/*` and `/notifications` share the tab layout without changing their addresses (FR-012, FR-048).
- The backend additions stay in the files that own those routes, as in 002 to 004.

## Complexity Tracking

| Addition / deviation | Why needed | Simpler alternative rejected because |
|---|---|---|
| New `frontend/` project (Node process) | The constitution fixes SvelteKit, and FR-003 needs a server between the browser and the API | Serving pages from the .NET API: it contradicts the constitution's frontend choice |
| `@fontsource/lato`, `@fontsource/jetbrains-mono` (**OFL-1.1**) | The design system's typefaces, self-hosted so no visitor IP goes to Google (Principle II; user decision). **Licence note**: the FSF lists OFL as a free licence that is GPL-incompatible because of its bundling requirement, and says it is fine to use for fonts. The fonts are served files, aggregated with the program and not linked into it, so GPL-3.0 §5's aggregate clause applies. The user chose this over the alternatives. | Google Fonts CDN: every visitor's IP goes to Google. System fonts: the design's look is lost. Committing hand-downloaded `woff2` files: the same licence, without lockfile integrity or updates. |
| `@lucide/svelte` (ISC) | The design system's icon set, without its unpkg CDN (Principle II) | Copying about 20 SVG paths by hand: drift from the design's set, and a licence notice to keep anyway. The CDN masks: a third-party request per icon. |
| `uqr` (MIT, no dependencies) | FR-021 needs a scannable QR code of the `otpauth` URI | Hand-writing QR encoding (Reed–Solomon, masking) is a lot of subtle code. Text-only setup fails the "scannable code" requirement. |
| DB column `account.home_page` + `PUT /api/me/settings` | FR-045 and FR-046: the home page choice follows the account across devices | A cookie or `localStorage`: per browser, which the spec rejects |
| Newest-first paging, `rule` index, `followers` | FR-025, FR-032 and FR-031 can't be met with the current API. Each is named in FR-045. | Frontend-side workarounds: reading every page to find the newest (O(n) per view), only a whole-filter error, and one filter request per calendar to count followers |
