---

description: "Task list for Web Frontend (005)"
---

# Tasks: Web Frontend

**Input**: Design documents from `/specs/005-sveltekit-frontend/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: Included. The constitution requires an automated check for non-trivial logic, and quickstart.md § 1 names one test file per mechanism (research R19). Write each phase's tests first. They should fail until that phase's implementation tasks are done (a missing module or a compile failure counts). Page rendering and navigation have no automated tests: the quickstart § 2 walkthrough covers them (R19). Route actions that carry auth logic (which cookie a token goes into, the invite cookie) do get a test, with `$lib/server/api` mocked through `vi.mock`. Name those files without a `+` (for example `sign-in.test.ts`), because SvelteKit reserves `+` names in `routes/`.

**Organization**: grouped by user story, in priority order: US1 and US2 (P1), US3 to US7 (P2), US8 and US9 (P3). US1 and US2 together are the MVP.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (a different file, and no dependency on an unfinished task). **Accepted exception**: most UI tasks also add keys to `frontend/src/lib/i18n/en.ts` and `nl.ts`. Such key-only additions don't remove the [P] marker, and they merge trivially.
- **[Story]**: the user story the task belongs to (US1 to US9)

## Path Conventions

- Frontend: `frontend/`. Backend: `backend/InTheKnow/`, with its tests in `backend/InTheKnow.Tests/`. Paths below are from the repository root.
- Every route directory holds `+page.server.ts` (a `load` that only reads, plus `actions`) and `+page.svelte`, unless a task says otherwise. `GET` and `load` never change anything (SC-004, research R4).
- **Text**: every fixed string goes into **both** `frontend/src/lib/i18n/en.ts` and `nl.ts` in the same task. Otherwise `npm run check` fails (SC-008). Dutch copy uses informal *je/jij*, sentence case and imperative buttons (R9).
- **Server-built text**: change sentences, rule sentences, dates, waits and reason messages are built in `load` and actions (R2). Components only print strings. No `{@html}` anywhere (SC-010).
- **Refusals**: every action returns `refuse(event, result, values)` (T019) on an API refusal. It never echoes passwords, codes or tokens. Every destructive action sits behind a `Dialog` confirmation.
- **Forms**: every form uses `use:enhance` through the wrapper from T024, and every submit button is `disabled={pending}` (FR-044).
- **Styling**: only the tokens in `tokens.css`, never hard-coded colours or sizes (R18). Target the phone first: 360 px wide with no horizontal scroll, and at least `--hit-min` for hit targets (FR-043).
- **Backend**: routes return `Api.Problem(status, reason, extra)` (`backend/InTheKnow/Api.cs:158`) with the `reason` strings of contracts/api.md, verbatim. Tests use `ApiHost` (`backend/InTheKnow.Tests/ApiTests.cs`) and `TestHost.SignInAsync` (`backend/InTheKnow.Tests/TestHost.cs`). Read both before writing a backend test. `Migrations/*.sql` is embedded by the csproj glob, so a new migration needs no csproj change.
- **Logs**: the frontend never logs a query string, header, cookie, address, user, token, password or email address (FR-047).

## Shared signatures (so tasks written separately fit together)

```ts
// frontend/src/lib/i18n/index.ts
export type Lang = 'en' | 'nl';
export type Dict = Widen<typeof en>;              // every string leaf → string, every function leaf keeps its signature
export const dicts: Record<Lang, Dict>;
export function pickLang(cookie: string | undefined, acceptLanguage: string | null): Lang;
// itk_lang if it is 'en'/'nl', else the first of nl/en in Accept-Language by q-value, else 'en' (R9)
// Components read the dictionary as dicts[page.data.lang] (page from $app/state). Dicts hold functions, so they are
// never returned from a load.

// frontend/src/lib/codes.ts
export type Reason = 'invalid_credentials' | 'not_confirmed' | …;      // complete, see T021
export type CheckFailure = 'network' | 'timeout' | 'too_large' | …;    // 001 data-model last_error
export function checkFailureMessage(code: string, t: Dict): string;    // CheckFailure, http_<n>, validation_failed:<code>
export function channelFailureMessage(code: string, t: Dict): string;  // smtp_<n>, unreachable, …
export function reasonMessage(reason: string, t: Dict): string;        // unknown → t.reasons.unknown ("Something went wrong")

// frontend/src/app.d.ts
type Me = { email: string; role: 'user' | 'admin'; language: Lang; twoFactor: boolean; recoveryCodesLeft: number;
            hasPassword: boolean; pendingEmail: string | null; homePage: 'changes' | 'combined' };
namespace App {
  interface Locals { token: string | null; me: Me | null; tz: string; lang: Lang; t: Dict; unavailable: boolean }
  interface Error { message: string; kind?: 'locked' | 'notAllowed' | 'notInAccount' | 'unavailable' | 'notFound'; email?: string }
}

// frontend/src/lib/server/api.ts
export type ApiFailure = { ok: false; status: number; reason: string; retryAfter?: number; extra: Record<string, unknown> };
export type ApiResult<T> = { ok: true; status: number; data: T } | ApiFailure;
export function api<T = unknown>(event: RequestEvent, method: string, path: string, body?: unknown,
                                 opts?: { token?: string | null; on401?: 'redirect' | 'return' }): Promise<ApiResult<T>>;
// Network error, timeout (30 s), or 5xx without a reason → { ok: false, status: 503, reason: 'unavailable' }.
// 401 not_signed_in → deletes itk_session, then throws redirect(303, `/sign-in?next=${path+query}`) unless on401 is 'return'.
export function must<T>(event: RequestEvent, r: ApiResult<T>, opts?: { notFound?: 'notFound' | 'notInAccount' }): T;
// For loads: throws error() with kind 'locked' (403 two_factor_required), 'notAllowed' (403 admin_required),
// opts.notFound ?? 'notFound' on 404 ('notInAccount' adds email: locals.me.email), 'unavailable' (503),
// or the status with a generic message. A 409 two_factor_required (admin role change) is a refusal, not 'locked'.
export function setSession(event: RequestEvent, token: string): void;    // itk_session
export function clearSession(event: RequestEvent): void;
export function setPending(event: RequestEvent, token: string): void;    // itk_pending
export function clearPending(event: RequestEvent): void;

// frontend/src/lib/server/actions.ts
export type Refusal = { reason: string; message: string; rule?: number; stepUp?: true; values?: Record<string, string> };
export function refuse(event: RequestEvent, r: ApiFailure, values?: Record<string, string>): ActionFailure<Refusal>;
// message = reasonMessage + " " + t.wait(format.wait(retryAfter)) when retryAfter is set (FR-042).
// 403 reauthentication_required → fail(403, { stepUp: true, … }). rule = extra.rule when it is a number (R13).
export function formValues(data: FormData, keep: string[]): Record<string, string>;   // only the listed fields; never password, newPassword, code or token
export function reauthenticate(event: RequestEvent): Promise<ActionFailure<Refusal> | { reauthenticated: true }>;  // US5
export function loadBotCheck(event: RequestEvent): Promise<{ provider: 'turnstile' | 'recaptcha'; siteKey: string } | null>;  // US1
export function botCheckValue(data: FormData): string | undefined;   // cf-turnstile-response or g-recaptcha-response

// frontend/src/lib/server/format.ts
export function formatter(lang: Lang, timeZone: string, now?: Date): {
  dateTime(iso: string): string;   // "Wed 1 Oct 08:30", year added when not the current year in that zone
  date(iso: string): string;
  time(iso: string): string;       // 24-hour
  range(startIso: string, endIso: string): string;   // en dash
  wait(seconds: number): string;   // "2 minutes", "30 seconds"
};

// frontend/src/lib/enhance.svelte.ts  (runes need the .svelte.ts suffix; the plan's enhance.ts)
export function submitter(opts?: { onResult?: (r: ActionResult) => void }): { readonly pending: boolean; enhance: SubmitFunction };
```

---

## Phase 1: Setup

**Purpose**: the SvelteKit project, its config and the design tokens.

- [ ] T001 Create the `frontend/` SvelteKit project (research R1). Use `npx sv create frontend` with the minimal template, TypeScript, and no add-ons, or write the same files by hand. Then in `frontend/package.json`:
  - `"type": "module"`, `"private": true`, `"license": "GPL-3.0-only"`, `"engines": { "node": ">=24" }`;
  - scripts: `dev` (`vite dev`), `build` (`vite build`), `preview`, `check` (`svelte-kit sync && svelte-check --tsconfig ./tsconfig.json --fail-on-warnings`), `test` (`vitest run`);
  - dependencies: `@lucide/svelte`, `uqr`, `@fontsource/lato`, `@fontsource/jetbrains-mono`;
  - devDependencies: `@sveltejs/kit` 2, `svelte` 5, `@sveltejs/adapter-node`, `@sveltejs/vite-plugin-svelte`, `vite`, `typescript`, `svelte-check`, `vitest`.

  No other package (plan § Technical Context). Run `npm install` so `frontend/package-lock.json` pins them, and commit it. Delete the starter's demo route and any `static/` favicon that the design system doesn't provide.
- [ ] T002 Write `frontend/svelte.config.js`:
  - `adapter: adapter-node()`;
  - `kit.csp`: `mode: 'auto'`, with directives giving exactly this policy (research R16): `default-src 'self'; script-src 'self' https://challenges.cloudflare.com https://www.google.com https://www.gstatic.com; frame-src https://challenges.cloudflare.com https://www.google.com; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'; object-src 'none'`;
  - SvelteKit's own origin check turned off (research R4). In Kit 2 that is `kit.csrf.checkOrigin: false`. **Check the installed `@sveltejs/kit` types** (`node_modules/@sveltejs/kit/types/index.d.ts`, `KitConfig.csrf`) for the current option name, and use the replacement if that one is deprecated. T010 proves it with the `Origin: null` case.
- [ ] T003 [P] Write `frontend/vite.config.ts` with `sveltekit()` and a vitest `test` block (`environment: 'node'`, `include: ['src/**/*.test.ts']`), and `frontend/tsconfig.json` extending `./.svelte-kit/tsconfig.json` with `strict: true`.
- [ ] T004 [P] Write `frontend/src/app.html` with `<html lang="%lang%">`, `%sveltekit.head%` and `%sveltekit.body%`, and no external link or script. Write `frontend/src/app.d.ts` with `Me`, `App.Locals` and `App.Error` exactly as in Shared signatures.
- [ ] T005 [P] Port the design system tokens (research R18) into `frontend/src/lib/styles/tokens.css` and `frontend/src/lib/styles/base.css`.
  - **Source**: the claude.ai design project "InTheKnow Design System" (`c2ef9d00-a246-4489-b5c3-2a49171b9a17`), its `tokens/*.css` and `base.css`. Fetch them with the `DesignSync` tool (load it through ToolSearch). **If it can't be reached, stop and ask the user to export those files. Never invent token values.**
  - Copy colours, typography, spacing, radii, shadows and motion **verbatim**. Drop the Google Fonts `@import` from `fonts.css`. Keep the font-family names (`Lato`, `JetBrains Mono`).
  - Add `--amber-700: #8a5200;` and `--green-700: #17693a;` to `tokens.css`, with a comment that they are the 4.5:1 text shades for badges and notices (R18).
  - In `base.css`, replace the `:focus-visible` box-shadow ring with `outline: 2px solid var(--border-focus); outline-offset: 2px;`. Keep the translucent ring only as an extra halo on inputs. Muted text on `--surface-subtle` uses `--grey-600`.

**Checkpoint**: `npm run check` and `npm run build` pass on the empty project.

---

## Phase 2: Foundational (blocking prerequisites)

**Purpose**: the pieces every page needs: codes and dictionaries, the API client, the hooks (gate, CSRF, headers, log, language), formatting, the submit wrapper, the base components and the layouts.

**⚠️ CRITICAL**: no user story can start before this phase is done.

### Tests for Phase 2 (write first)

- [ ] T006 [P] `frontend/src/lib/next.test.ts`: `safeNext` keeps `/calendars/1`, `/calendars/1/notifications?x=1` and `/`. It turns each of these into `/`: `//evil.example`, `/\evil.example`, `https://evil.example`, `https:evil.example`, `javascript:alert(1)`, `%2F%2Fevil.example`, `/%5Cevil.example`, an empty string and `null` (FR-011, research R6).
- [ ] T007 [P] `frontend/src/lib/server/format.test.ts` (research R12):
  - Europe/Amsterdam: `2026-10-24T06:30:00Z` shows 08:30 and `2026-10-26T07:30:00Z` shows 08:30, across the DST change;
  - with `now` in 2026, a 2026 date has no year and a 2027 date has one;
  - a zone where the year boundary differs from UTC (`Pacific/Auckland`, `2026-12-31T12:00:00Z` is 2027 there);
  - times are 24-hour in `en` and `nl`, and `range` uses an en dash;
  - `wait(90)` and `wait(30)` in both languages.
- [ ] T008 [P] `frontend/src/lib/server/api.test.ts` with a mocked `fetch` and a fake `RequestEvent` (cookies, `getClientAddress`, `request.headers`, `url`):
  - the request carries `Authorization: Bearer <itk_session>`, `X-Forwarded-For: <getClientAddress()>` (one address), and the browser's `User-Agent` (FR-007);
  - `401 { reason: 'not_signed_in' }` deletes `itk_session` and throws a `303` to `/sign-in?next=%2Fchanges%3FshowHidden%3D1`; with `on401: 'return'` it deletes the cookie and returns the failure (FR-006);
  - the same `401` during a form action on `/calendars/1?/remove` redirects with `next=%2Fcalendars%2F1`: query keys starting with `/` (action names) are dropped;
  - `429` with `Retry-After: 120` gives `retryAfter: 120` (FR-042);
  - an aborted or failed fetch and a `502` without a body give `{ status: 503, reason: 'unavailable' }` with no details;
  - `extra` carries other Problem members (for example `rule`);
  - a failing `GET /api/admin/accounts/7?q=alice@example.org` logs one line (spy on `console.log`/`console.error`) that contains `/api/admin/accounts/{id}` and none of `?`, `alice` or `example.org` (FR-047);
  - `setSession` sets `itk_session` with `httpOnly: true, secure: true, sameSite: 'lax', path: '/', maxAge: 34560000`, and `setPending` sets `itk_pending` with `httpOnly: true, secure: true, sameSite: 'lax', path: '/sign-in', maxAge: 600` (FR-003, FR-004).
- [ ] T009 [P] `frontend/src/lib/i18n/index.test.ts`: `pickLang` gives `nl` for `nl-NL,en;q=0.8`, `en` for `en-US,nl;q=0.5`, `nl` for `de,nl;q=0.9,en;q=0.8`, `en` for `de` and for `null`. `itk_lang=nl` beats `Accept-Language: en`, and `itk_lang=de` is ignored (R9).
- [ ] T010 [P] `frontend/src/hooks.server.test.ts`. Call the exported `handle` with a stub `resolve` and a mocked API `fetch` (quickstart § 1):
  - **Gate**: signed out, `GET /changes` → `303 /sign-in?next=%2Fchanges`. `/`, `/sign-in`, `/sign-in/code`, `/account/reset`, `/language` and `/_app/x.js` pass. `GET /account/reset?token=T` with an `itk_session` that the API answers `401` for: the cookie is deleted and the page resolves (R3).
  - **CSRF**: `POST` with `Sec-Fetch-Site: cross-site` → `403`. `Sec-Fetch-Site: same-site` → `403`. `Sec-Fetch-Site: same-origin` with `Origin: null` passes. No `Sec-Fetch-Site`, with `Origin` equal to the app origin, passes, and with another `Origin` → `403` (FR-005, R4).
  - **Headers**: a page response and a `/changes/__data.json` response both carry `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `X-Content-Type-Options: nosniff` and `Cache-Control: no-store` (FR-008, FR-009, FR-013).
  - **Log** (FR-047): spy on `console.log`. For `GET /account/reset?token=SECRET` with `Cookie: itk_session=TOKEN`, `X-Forwarded-For: 203.0.113.9` and `User-Agent: UA-X`, the single line matches `^\S+Z GET /account/reset \d{3} \d+ms$` and contains none of `SECRET`, `TOKEN`, `203.0.113.9` or `UA-X`.
  - **Language**: a visitor with `Accept-Language: nl` gets `<html lang="nl">` via `transformPageChunk`. A signed-in user whose `/api/me` says `en` gets `en`.
- [ ] T011 [P] `frontend/src/no-raw-html.test.ts`: walk `src/` recursively with `node:fs`, and fail when any `.svelte` or `.ts` file other than this test contains `{@html` (FR-010, SC-010).
- [ ] T012 [P] `frontend/src/lib/codes.test.ts` (SC-009, research R10):
  - Read `../specs/001-ical-change-detection/contracts/api.md`, and the `contracts/api.md` of 002, 003, 004 and 005, with paths relative to `frontend/`.
  - The contracts write reasons in three shapes: a `reason` column (`` `invalid_email`, `invalid_language` ``, several per cell), a status inside the backticks (`` `409 demote_first` `` in 004's 2FA and Account actions tables, whose headers have no reason column), and inline or prose (`` `reason`: `invalid_url` `` in 001, `` `429 too_many_requests` `` in 004 § Authentication). So: in every table row or bullet line that mentions a 4xx or 5xx status, take **every** backticked token, strip a leading `\d{3} `, and keep those matching `^[a-z]+(_[a-z0-9]+)+$` (plus the `validation_failed:<code>` form, mapped to its `CheckFailure`). Assert that each of the five files yields a non-zero count.
  - Assert each is in the runtime list `reasons` exported from `codes.ts`, apart from an explicit, commented `excluded` list (for example `invalid_subscriber`, gone since 004; reasons of `/n/…` pages and of routes the frontend doesn't call).
  - Assert that `dicts.en.reasons` and `dicts.nl.reasons` have a non-empty message for every entry of `reasons`, and for every `checkFailures` entry.
  - The pattern codes (research R10), in `en`: `checkFailureMessage` gives the "no longer exists" message for `http_404` and `http_410`, "refused the link" for `http_401` and `http_403`, the generic server-error message for `http_500`, and the `CheckFailure` message for `validation_failed:empty_feed`. `channelFailureMessage` gives "rejected this address" for `smtp_550`, "refused it for now" for `smtp_421`, and "can't be reached" for `unreachable`. An unknown code (`bogus`) gives the generic message in all three functions.
- [ ] T013 [P] `frontend/src/lib/server/actions.test.ts`, with a fake `RequestEvent` whose `locals.t` is `dicts.en`:
  - `refuse()` on `429 too_many_attempts` with `retryAfter: 120` gives a message ending in the "2 minutes" wait; without `retryAfter` it has no wait;
  - `403 reauthentication_required` gives `stepUp: true`;
  - `extra.rule: 2` gives `rule: 2`, and `extra.rule: '2'` or no `rule` gives none;
  - an unknown reason gives `t.reasons.unknown`, never the raw code;
  - `formValues(data, ['email', 'password', 'code', 'token'])` on a form holding all four returns only `email`: `password`, `newPassword`, `code` and `token` are never returned, even when listed.

### Implementation for Phase 2

- [ ] T014 Write `frontend/src/lib/codes.ts`:
  - `export const reasons = [...] as const; export type Reason = typeof reasons[number];` Start it with the frontend's own `unavailable` and `unknown`. T021 fills in the rest.
  - `export const checkFailures = [...] as const; export type CheckFailure` from 001 data-model `last_error` and the `validation_failed:<code>` codes: `network`, `timeout`, `too_large`, `unparseable`, `empty_feed`, `too_many_occurrences`, `blocked_address`, and the rest listed there.
  - `checkFailureMessage`, `channelFailureMessage` and `reasonMessage` from Shared signatures, with the pattern codes of research R10: `http_404`/`http_410` → "the link no longer exists", `http_401`/`http_403` → "the calendar server refused the link", other `http_*` → "the calendar server answered with an error", `smtp_5xx` → "the mail server rejected this address", `smtp_4xx` → "the mail server refused it for now", `unreachable` → "the mail server can't be reached". An unknown code gives the generic message, and the caller logs only the code.
- [ ] T015 Write `frontend/src/lib/i18n/en.ts`, `frontend/src/lib/i18n/nl.ts` and `frontend/src/lib/i18n/index.ts` (research R10):
  - `en.ts` is `export const en = { … }`. It holds `reasons: Record<Reason, string>`, `checkFailures: Record<CheckFailure, string>`, the pattern-code messages, and the shared UI strings: nav (Changes, Calendars, Settings, Admin, Sign out), wordmark aria label, `close`, `back`, `loading`, the `wait` phrase, and the error page texts for `locked`, `notAllowed`, `notInAccount` (a function taking the email), `unavailable` and `notFound`.
  - `nl.ts` is `export const nl: Dict = { … }`, so a missing or extra key fails `svelte-check`.
  - `index.ts` defines `Widen<T>`, `Dict`, `Lang`, `dicts` and `pickLang` per Shared signatures.
  - Plain-language wording, no status codes (FR-041).
- [ ] T016 [P] Write `frontend/src/lib/next.ts`: `export function safeNext(next: string | null | undefined, origin: string): string`. Keep `next` only when it starts with `/`, doesn't start with `//` or `/\`, and `new URL(next, origin).origin === origin`. Otherwise return `/` (R6).
- [ ] T017 [P] Write `frontend/src/lib/server/format.ts`: `formatter()` per Shared signatures, using `Intl.DateTimeFormat` with `timeZone` and locale `en-GB` or `nl-NL`, `hourCycle: 'h23'`. Compare the year of the date and of `now` *in that zone* (R12).
- [ ] T018 Write `frontend/src/lib/server/api.ts` per Shared signatures and research R7:
  - the base URL from `$env/dynamic/private` `API_URL`, default `http://localhost:5000`;
  - headers: `Authorization` from `opts.token`, else the `itk_session` cookie; `X-Forwarded-For: event.getClientAddress()`; `User-Agent: event.request.headers.get('user-agent') ?? ''`; `Content-Type: application/json` when there's a body;
  - `signal: AbortSignal.timeout(30_000)`;
  - parse Problem Details: `reason`, the rest into `extra`, and `Retry-After` seconds into `retryAfter`;
  - `401` with `not_signed_in` handling as in Shared signatures. Use `event.url.pathname` plus `event.url.search` for `next`, without the query keys that start with `/` (form action names such as `?/remove`);
  - on failure, log only `method`, the path template, `status` and `reason` (FR-047). The template is the path **with everything from `?` removed** (queries can hold part of an email address, for example the admin search `q`), and numeric segments replaced by `{id}`;
  - the cookie helpers with the exact attributes of data-model.md: `itk_session` → `httpOnly: true, secure: true, sameSite: 'lax', path: '/', maxAge: 34560000`; `itk_pending` → `httpOnly: true, secure: true, sameSite: 'lax', path: '/sign-in', maxAge: 600`. SvelteKit drops `secure` itself on `http://localhost`.
- [ ] T019 [P] Write `frontend/src/lib/server/actions.ts` with `Refusal`, `refuse` and `formValues` per Shared signatures (`reauthenticate`, `loadBotCheck` and `botCheckValue` come in US5 and US1). `refuse` uses `event.locals.t`, `reasonMessage`, and `formatter(...).wait` for `retryAfter`. `formValues` always drops `password`, `newPassword`, `code` and `token`, even when they are listed. Make T013 pass.
- [ ] T020 Write `frontend/src/hooks.server.ts` (research R3 to R6, R9, R16, R17). One exported `handle`, in this order:
  1. Start a timer.
  2. **CSRF**: for `POST`/`PUT`/`PATCH`/`DELETE`, answer `403` unless `Sec-Fetch-Site === 'same-origin'`, or `Sec-Fetch-Site` is absent and `Origin === event.url.origin`.
  3. **Me**: with an `itk_session` cookie, call `GET /api/me` and `GET /api/me/notification-settings` in parallel through `api(…, { on401: 'return' })`. On success, set `locals.me` (`homePage` included) and `locals.tz = effectiveTimeZone`. On `401`, `locals.me = null` (the cookie is already deleted). On `unavailable`, set `locals.unavailable = true` and keep `locals.me = null`. Set `locals.token`. **Don't throw `error()` from `handle`**: SvelteKit then renders the static fallback `src/error.html` (which prints the status), not `+error.svelte`. Check this against the installed Kit's docs.
  4. **Gate**: skip it when `locals.unavailable` (the root layout shows the unavailable view instead, T029). Public paths are `/`, `/sign-in`, `/sign-in/code`, `/account` and everything under it, `/language`, and `/_app/`. Any other path without `locals.me` → `303 /sign-in?next=<encodeURIComponent(pathname + search)>`.
  5. **Language**: `locals.lang = me?.language ?? pickLang(cookies.get('itk_lang'), request.headers.get('accept-language'))`, and `locals.t = dicts[locals.lang]`. Visitors have no notification settings to read, so their `locals.tz` is `'UTC'`, with a comment that visitor pages show no dates. Don't copy the server's default zone here: the API owns it.
  6. `resolve(event, { transformPageChunk: ({ html }) => html.replace('%lang%', locals.lang) })`.
  7. **Headers** on the response: `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `X-Content-Type-Options: nosniff`, `Cache-Control: no-store`.
  8. **Log**: `console.log(`${new Date().toISOString()} ${method} ${event.url.pathname} ${status} ${ms}ms`)`. Nothing else.

  The early exits (the CSRF `403`, the gate `303`) are built as `Response`s that also go through steps 7 and 8, so every response gets the headers and one log line.

  Also export `handleError`: `console.error(`${iso} ${event.url.pathname} ${error.name}: ${error.message}`)`, and return `{ message: t.errors.generic }` without details.
- [ ] T021 Complete the code sets. Make `npm test` pass `codes.test.ts` (T012): add to `reasons` every code the test extracts (using the same three-shape rule as T012, so 004's `` `409 demote_first` `` rows and 001's inline `` `reason`: `` rows are included), and give every `reasons` and `checkFailures` entry a message in both dictionaries. Every story after this only adds UI strings. Mark any code that the tables list but the frontend never receives in the test's `excluded` list, with a comment naming the route.
- [ ] T022 Add `must()` to `frontend/src/lib/server/api.ts` per Shared signatures: `403 two_factor_required` → `error(403, { kind: 'locked', message })`, `403 admin_required` → `kind: 'notAllowed'`, `404` → `kind: opts.notFound ?? 'notFound'` (with `email: locals.me?.email` for `notInAccount`), `unavailable` → `error(503, { kind: 'unavailable' })`, anything else → `error(status, { message: generic })`.
- [ ] T023 [P] Write the base components in `frontend/src/lib/components/` (research R18), each keeping the design system's look and states (hover, press, focus-visible, disabled):
  - `Button.svelte`: `variant` primary/secondary/dark/ghost/inverse, `size` sm/md/lg; renders `<a>` when `href` is set, else `<button>` (default `type="submit"`); a trailing Lucide `ArrowRight` only on `<a>` links; `disabled` passes through.
  - `Card.svelte` and `Badge.svelte` (`tone` neutral/green/amber/red/blue/magenta). Amber and green badge text uses `--amber-700` and `--green-700`.
  - `PageIntro.svelte`: the magenta title band of the web kit, with `title` and an optional intro line.
- [ ] T024 [P] Write `frontend/src/lib/components/Field.svelte` and `frontend/src/lib/components/Switch.svelte`:
  - `Field`: a `<label for>`, the input or a slotted `<select>`/`<textarea>`, a hint and an error, both linked with `aria-describedby`, `aria-invalid` when there's an error, and a 1px `--grey-500` border;
  - `Switch`: a native `<input type="checkbox" role="switch">` with a label, styled as the design's switch. It posts with forms.
- [ ] T025 [P] Write `frontend/src/lib/components/Dialog.svelte` and `frontend/src/lib/components/Toast.svelte`:
  - `Dialog`: a native `<dialog>` opened with `showModal()` through a bound `open` prop, closed on Esc (native), with a close button labelled from the dictionary. Title, body and actions go in slots or snippets.
  - `Toast`: `role="status"`, shows the `message` prop and fades out after a few seconds.
- [ ] T026 [P] Write `frontend/src/lib/components/TabNav.svelte`: a `<nav aria-label>` of links, `aria-current="page"` on the active one (from `page.url.pathname`), a side list at ≥ 768 px, wrapping below that and never `overflow-x: auto` (FR-043, FR-048).
- [ ] T027 Write `frontend/src/lib/enhance.svelte.ts`: `submitter()` per Shared signatures. `pending` is `$state`. `enhance` sets `pending = true` on submit, calls SvelteKit's `update()` (default reset and invalidation behaviour), sets `pending = false`, then calls `opts.onResult`. The step-up handling is added in US5 (T072).
- [ ] T028 Write `frontend/src/lib/components/Header.svelte` and `frontend/src/lib/components/PhoneNav.svelte` (research R18):
  - `Header`: the wordmark ("InThe" + magenta "Know") linking to `/`. For signed-in users: nav links Changes (`/changes`), Calendars (`/calendars`), Settings (`/settings/account`), and Admin (`/admin/accounts`) only when `me.role === 'admin'` (US8-2), plus a sign-out `<form method="POST" action="/sign-out">` (route in T036). For visitors: sign in and sign up links, and a slot for the language switch (US9).
  - `PhoneNav`: the same signed-in items as a fixed bottom bar below 768 px, using Lucide icons with visible labels. `Header` hides its nav links below 768 px.
- [ ] T029 Write the root layout and error page:
  - `frontend/src/routes/+layout.server.ts`: returns `{ lang: locals.lang, me: locals.me, unavailable }` (no tokens, no functions), where `unavailable` is `locals.unavailable` on a non-public path. **Don't throw `error()` here**: an error in the root layout's load has no `+error.svelte` above it, so SvelteKit renders the static `src/error.html` (the same trap as in T020; check it against the installed Kit's docs);
  - `frontend/src/routes/+layout.svelte`: imports `@fontsource/lato/400.css`, `700.css`, `900.css`, `@fontsource/jetbrains-mono/600.css`, `$lib/styles/tokens.css` and `base.css`. Renders `Header`, `<main>`, and `PhoneNav` for signed-in users. When `data.unavailable` is set, `<main>` shows the "try again later" view instead of the child page, and the response status stays 200 (only the text matters to the user);
  - `frontend/src/routes/+error.svelte`: one view per `page.error.kind`. `locked`: why admin pages are locked, a link to `/settings/security/two-factor`, and a sign-out-and-sign-in-with-code link (US8-1). `notAllowed` (US8-2). `notInAccount`: "this calendar is not in the account `<email>`". `unavailable`: try again later. Otherwise a generic message and a link home. Never print the status or technical details (FR-041).
- [ ] T030 Write the settings tab layout and redirect (FR-048, plan § Structure Decision):
  - `frontend/src/routes/(settings)/+layout.svelte`: a `PageIntro` ("Settings") and `TabNav` with Account (`/settings/account`), Sign-in & security (`/settings/security`), Preferences (`/settings/preferences`) and Notifications (`/notifications`), then the child page;
  - `frontend/src/routes/(settings)/settings/+page.server.ts`: `load` throws `redirect(303, '/settings/account')`.

**Checkpoint**: `npm run check`, `npm test` and `npm run build` pass. T006 to T013 are green.

---

## Phase 3: User Story 1 - Create an account and sign in (Priority: P1) 🎯 MVP

**Goal**: a visitor signs up, confirms from the email, signs in and signs out (FR-011, FR-015 to FR-018).

**Independent Test**: with Mailpit, sign up with a new address, open the confirmation link, confirm, sign in, see the signed-in home page, sign out, and check that `/changes` now sends you to sign-in.

### Tests for User Story 1 (write first)

- [ ] T031 [P] [US1] `frontend/src/routes/sign-in/sign-in.test.ts`: call the `default` action of `./+page.server` with `vi.mock('$lib/server/api')` and a fake event (cookies, `url`, `locals.t`):
  - `{ token: 'A', secondFactorRequired: false }` → `setSession(A)`, no `setPending`, `303` to `next`;
  - `{ token: 'B', secondFactorRequired: true }` → `setPending(B)`, **never** `setSession`, `303 /sign-in/code?next=…`;
  - `next=//evil.example` → the redirect goes to `/`;
  - `401 invalid_credentials` → a `fail` whose `values` hold `email` and no `password`.

### Implementation for User Story 1

- [ ] T032 [P] [US1] Write `frontend/src/lib/components/PasswordRules.svelte`: the three rules (12 to 128 characters, not a common password, not the email address) listed next to a password field before submitting, with an `id` for `aria-describedby`. The failed rule is highlighted when the page's refusal is `password_length`, `password_common` or `password_is_email` (004 contracts/api.md) (FR-017, US1-3).
- [ ] T033 [P] [US1] Add `loadBotCheck` and `botCheckValue` to `frontend/src/lib/server/actions.ts`. `loadBotCheck` calls `GET /api/auth/bot-check` (004 contracts/api.md § `GET /api/auth/bot-check`) and returns the provider and site key, or `null`. `botCheckValue` reads `cf-turnstile-response`, else `g-recaptcha-response`.
- [ ] T034 [US1] Write `frontend/src/lib/components/BotCheck.svelte` (research R15):
  - props `provider`, `siteKey`;
  - loads the provider script once per page session, with explicit rendering: Turnstile `https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit`, reCAPTCHA `https://www.google.com/recaptcha/api.js?onload=<global callback>&render=explicit`. Add the CSP nonce if SvelteKit exposes one; otherwise the host list in `kit.csp` covers it;
  - calls `render()` on mount into its container, and `remove()` (Turnstile) or `reset()` (reCAPTCHA) on destroy;
  - exports `reset()`. Each form's `onResult` calls it after every action result, because the result is single-use;
  - on the script's `onerror`, or when nothing has rendered after 10 s, shows "the check couldn't load; allow challenges.cloudflare.com (or google.com) or try again". It never disables the submit button.
- [ ] T035 [US1] Write `frontend/src/routes/sign-in/` (pages.md, 004 contracts/api.md § `POST /api/auth/sign-in`):
  - `load`: a signed-in user is sent to `safeNext(next)`. Return `loadBotCheck()`, `next`, and an `expired` notice when `?expired=code` (the code step timed out, spec edge case).
  - `default` action: `POST /api/auth/sign-in { email, password, botCheck }` with `on401: 'return'` (the API's `401` here is `invalid_credentials`, not `not_signed_in`).
    - `secondFactorRequired: false` → `setSession(token)`, `303 safeNext(next)`.
    - `secondFactorRequired: true` → `setPending(token)`, `303 /sign-in/code?next=…`. **Never put this token in `itk_session`**, because the gate would treat it as signed in. The code page arrives in US6 (T079). Until then that address is a 404.
    - Refusals through `refuse()`, keeping `email`: `invalid_credentials` (one generic message, US1-6), `not_confirmed` (with a link to `/account/confirmation`, US1-5), `account_disabled`, `too_many_attempts` with the wait (US1-7), `bot_check_failed`.
  - Page: email and password `Field`s (`autocomplete="username"` / `current-password`), `BotCheck` when configured, a link to `/account/forgot-password` (built in US5) and to sign-up. The sign-in layout from the design system uses the solid magenta block without a photo.
- [ ] T036 [P] [US1] Write `frontend/src/routes/sign-out/+page.server.ts`: no `load` page (a `GET` gets `303 /`). The `default` action calls `POST /api/auth/sign-out` with `on401: 'return'`, then `clearSession` and `clearPending` even when the call fails, and `303 /` (FR-006, US1-8).
- [ ] T037 [US1] Write `frontend/src/routes/account/sign-up/` (004 contracts/api.md § `POST /api/auth/sign-up`):
  - `load`: `loadBotCheck()`, and `lang` as the default language (US9-2);
  - `default` action: `POST /api/auth/sign-up { email, password, language, botCheck }`. On `202`, return `{ done: true }`. The page then shows one "check your email" view, **identical for every address**: no email echoed, no difference between existing and new (SC-005). Refusals keep `email` and `language`: the password rules, `invalid_email`, `invalid_language`, `sign_up_by_invitation` ("sign-up is by invitation only", US7-5), `bot_check_failed`, `try_again_later` (503: try again later), `too_many_requests` with the wait;
  - Page: email, password with `PasswordRules`, a language `<select>` (English, Nederlands), `BotCheck`.
- [ ] T038 [P] [US1] Write `frontend/src/routes/account/confirmation/`: an email field and `BotCheck`. The `default` action calls `POST /api/auth/confirmation { email, botCheck }` and always shows the same "if the address has an unconfirmed account, we sent an email" result (FR-016, SC-005). `try_again_later` and waits are the only other messages.
- [ ] T039 [P] [US1] Write `frontend/src/routes/account/confirm/`. `load` reads `token` from the query and returns only whether it is present. It calls no API (SC-004). The page has a password field and a "Confirm my account" button. The `default` action posts `POST /api/auth/confirm { token, password }`, with the token from `url.searchParams`. On success: "your account is ready", with a sign-in link. `token_invalid` → "this link no longer works", with a link to `/account/confirmation` (US5-4). `password_incorrect` → "the password is incorrect" (US1-4).
- [ ] T040 [US1] Write `frontend/src/routes/+page.server.ts` and `+page.svelte` for visitors: a short explanation of InTheKnow with sign-in and sign-up buttons (spec Assumptions, "a public landing page beyond … is out of scope"). For a signed-in user, the page shows a minimal heading and a link to `/calendars`. US2 (T054) replaces that branch with the real home page.

**Checkpoint**: the US1 independent test passes. `npm run check` and `npm test` stay green.

---

## Phase 4: User Story 2 - Add my calendar and see what changed (Priority: P1)

**Goal**: a student adds a calendar link, sees its status, and reads their changes newest first as sentences. The home page shows the feed or the combined view (FR-023 to FR-028, FR-046, FR-049 Overview tab).

**Independent Test**: sign in, add a valid link, see it checked OK, let the checker record changes (a test feed you edit), and read them on `/changes` in the account's time zone. Switch the home page to the combined view on Preferences, sign in on a second browser, and see the same view.

### Tests for User Story 2 (write first)

- [ ] T041 [P] [US2] In `backend/InTheKnow.Tests/ApiTests.cs`, add the newest-first tests of contracts/api.md § 1:
  - newest-first order across two calendars;
  - `before` paging with a small `limit` reaches the oldest change with no gaps or repeats;
  - a page is filled past hidden changes and is never short while older visible changes exist;
  - `includeHidden=true` with `order=newest`;
  - `order=sideways` → `400 invalid_order`; `before` without `order`, `before` with `order=oldest`, and `after` with `order=newest` → `400 invalid_cursor`;
  - a request without `order` returns exactly what it did before (compare with an existing oldest-first assertion).
- [ ] T042 [P] [US2] In `backend/InTheKnow.Tests/AccountTests.cs`, add the home page tests of contracts/api.md § 2: a new account reads `homePage: "changes"`; `PUT /api/me/settings { "homePage": "combined" }` → `200 { "homePage": "combined" }` and `GET /api/me` reads it back; `{}` → `400 empty_body`; `{ "homePage": "combined", "x": 1 }` → `400 invalid_settings`; `{ "homePage": "grid" }` → `400 invalid_home_page`; another account's value is untouched. In `backend/InTheKnow.Tests/DbTests.cs`, assert that `UPDATE account SET home_page = 'grid'` is refused by the `CHECK`, and that an account inserted without `home_page` gets `changes`.
- [ ] T043 [P] [US2] Write `frontend/src/lib/server/changes.test.ts` (SC-006, research R11):
  - a fixture with one change per `kind`/`field` row of `specs/001-ical-change-detection/contracts/detected-change.md`: `added`, `removed`, and `changed` on `name`, `time`, `status`, `location`, `description`, `type`, `rooms`, `teachers`, `groups`, `details.<label>` and `details.text`;
  - each gets a non-empty sentence in `en` and `nl`, containing none of the raw field names (`details.`, `eventKey`, `oldValue`), `{`, `[`, `null` or `undefined`;
  - status tentative → confirmed reads "event confirmed" (en) and its Dutch equivalent; → cancelled reads "cancelled";
  - rooms `["AL00.A1.38"]` → `["AL00.A1.20"]` names both, and a group added names only that group;
  - grouping: three consecutive items with the same `(calendarId, eventKey, detectedAt)` form one group, and a fourth with another `detectedAt` starts a new one;
  - tones: a group with a `location` and a `removed` change is `cancelled`; `added` alone is `new`; `time` alone is `changed`; outcomes `lowPriority` and `hidden` are kept per item;
  - `calendarName` is set only when there is more than one calendar (US2-9);
  - `calendarView` gives each state of data-model.md § Calendar (checked OK, last check failed, failing with its count, not checked yet), `stateText` from `checkFailureMessage` for a `lastError`, the label as `name` when set, else the feed name. `JSON.stringify` of the result contains no `links`, no `url` and no `https://` from a fixture calendar whose links carry a URL (FR-023).

### Implementation for User Story 2 (backend, FR-045)

- [ ] T044 [US2] Create `backend/InTheKnow/Migrations/005_frontend.sql` with exactly (data-model.md):
  ```sql
  ALTER TABLE account ADD COLUMN home_page text NOT NULL DEFAULT 'changes'
      CHECK (home_page IN ('changes', 'combined'));
  ```
- [ ] T045 [US2] In `backend/InTheKnow/Api.cs`, extend `me.MapGet("/changes", …)` (line 77) per contracts/api.md § 1:
  - add `string? order` and `long? before` parameters;
  - before the loop: `order` not null/`oldest`/`newest` → `Problem(400, "invalid_order")`; `before` with `order != "newest"`, or `after` with `order == "newest"` → `Problem(400, "invalid_cursor")`;
  - for `newest`, the same batch loop with `from = before ?? long.MaxValue`, `c.id < $2 ORDER BY c.id DESC`, the same `take + 1` condition, the same item shape, and the **same `Page()` helper** (line 162). Don't write a second pager. `next` is then the oldest id returned;
  - without `order`, the code path is unchanged.
- [ ] T046 [US2] In `backend/InTheKnow/AccountApi.cs`:
  - `GET /api/me` (line 104): append `a.home_page` to the SELECT as ordinal 8, so the existing ordinals don't shift, and add `homePage = r.GetString(8)` to the JSON;
  - add `me.MapPut("/settings", …)` taking `JsonNode? body`: not an object, or no known field → `Problem(400, "empty_body")`; any key other than `homePage` → `Problem(400, "invalid_settings")`; `homePage` not the string `changes` or `combined` → `Problem(400, "invalid_home_page")`. Then `UPDATE account SET home_page = $2 WHERE id = $1`, and return `{ homePage }`. No step-up (contracts/api.md § 2).

### Implementation for User Story 2 (frontend)

- [ ] T047 [US2] Write `frontend/src/lib/server/changes.ts` (research R11, data-model.md § ChangeGroup):
  - `describe(change, t, fmt): string` for every kind and field. Set fields name what was added and removed. Status per R11. `details.<label>` uses the label as text;
  - `tone(change): 'new' | 'cancelled' | 'moved' | 'changed'` and `icon(change)` per R11's table (`plus-circle`, `x-circle`, `map-pin`, `clock`, `check-circle`, `pencil`);
  - `group(items, calendars, t, fmt): ChangeGroup[]` over consecutive items sharing `(calendarId, eventKey, detectedAt)`. Heading: `eventName`, `fmt.dateTime(eventStartsAt)`, and `calendarName` only when `calendars.length > 1`. Tone: cancelled > moved > new > changed. `items[]`: `{ sentence, outcome, icon }`;
  - `calendarView(calendar, t, fmt)`, built from `GET /api/me/calendars`: `{ calendarId, name, state, stateText, lastChecked, nextCheck, failures, notify, label }`, with the states of data-model.md § Calendar and `checkFailureMessage(lastError)`. It **never** carries `links[]` or a URL, only `linkCount` (FR-023).
- [ ] T048 [P] [US2] Write `frontend/src/lib/components/ChangeGroup.svelte`: the design's ChangeNotice look with the group's tone colour and icon, the heading, and one line per item. `lowPriority`: muted text, a grey icon, and a neutral "Low priority" `Badge`. `hidden`: a dashed border, reduced emphasis, and a "Hidden" `Badge` (FR-027). Not clickable, and no unread dot (R18).
- [ ] T049 [P] [US2] Write `frontend/src/lib/components/CalendarStatus.svelte`: prints a `calendarView`: name, a `Badge` for the state (Checked OK green, Last check failed amber, Failing red, Not checked yet neutral), the reason text, last and next check, and the failure count when failing (US2-4).
- [ ] T050 [US2] Add the shared add-link action and form:
  - `addLink(event)` in `frontend/src/lib/server/actions.ts`: `POST /api/me/links { url }` (001 contracts/api.md § `POST /api/me/links`). Success (`201` added, `200` already added) → `{ added: true, name }`. Refusals via `refuse()` keep `url`: `invalid_url`, `unsupported_scheme` ("this is not an https link"), `url_too_long`, `blocked_address`, each `validation_failed:<code>` through `checkFailureMessage`, and `host_budget_exhausted` with the wait (FR-024, US2-2, US2-3);
  - `frontend/src/lib/components/AddCalendar.svelte`: a URL `Field` and a submit button that reads "Checking…" and is disabled while `pending` (US2-1), with a hint that checking can take several seconds. Posts to `?/addLink` on the page that includes it.
- [ ] T051 [US2] Write `frontend/src/routes/changes/`:
  - `load`: in parallel, `GET /api/me/changes?order=newest&limit=50[&before=<id>][&includeHidden=true]` and `GET /api/me/calendars`. Accept `before` only as a positive integer and `showHidden` only as `1`. Return the groups from `group()`, `next`, and `showHidden`;
  - Page: a `PageIntro`; a "show hidden changes" switch as a `GET` form (`showHidden=1`) that submits on change and has a visible submit button without JavaScript; the groups; "Older changes" (`?before=<next>`, keeping `showHidden`) only when `next` is set, and "Newest" when `before` is set (US2-5 to US2-9, FR-025). An empty page says there are no changes yet.
- [ ] T052 [US2] Write `frontend/src/routes/calendars/`:
  - `load`: `GET /api/me/calendars` → `calendarView[]`;
  - action `addLink` (T050);
  - Page: the list of `CalendarStatus` cards, each linking to `/calendars/{id}`; `AddCalendar`; a link to `/presets` (R18). With no calendars, a short explanation above the form.
- [ ] T053 [US2] Write the calendar page layout and Overview tab (FR-049, pages.md):
  - `frontend/src/routes/calendars/[id]/+layout.server.ts`: validate `id` as a positive integer (else `error(404, { kind: 'notInAccount', email })`). Find it in `GET /api/me/calendars`. Absent → `error(404, { kind: 'notInAccount', email: locals.me.email })` (spec edge case). Return its `calendarView`;
  - `frontend/src/routes/calendars/[id]/+layout.svelte`: a `PageIntro` with the calendar name, and `TabNav` with Overview (`/calendars/{id}`), Filter (`/calendars/{id}/filter`) and Notifications (`/calendars/{id}/notifications`);
  - `frontend/src/routes/calendars/[id]/+page.server.ts` and `+page.svelte`: `CalendarStatus`, and a remove button behind a `Dialog` ("remove this calendar; its changes will no longer show"). Action `remove`: `DELETE /api/me/calendars/{id}`, then `303 /calendars`. A `404` from the API → the `notInAccount` refusal (US2-10).
- [ ] T054 [US2] Replace the signed-in branch of `frontend/src/routes/+page.server.ts` and `+page.svelte` (FR-046, US2-11, US2-12):
  - `load` for signed-in users: `GET /api/me/calendars`. With none, return `{ view: 'empty' }`. Otherwise load the first changes page as in T051 (no `before`, no hidden) and return `{ view: locals.me.homePage, groups, calendars }`;
  - action `addLink` (T050);
  - Page: `changes` → the feed with a link to `/changes` for older ones. `combined` → the `CalendarStatus` list on top and the latest changes below. `empty` → a short explanation and `AddCalendar`.
- [ ] T055 [US2] Write `frontend/src/routes/(settings)/settings/preferences/` with the **home page** setting only. US4 (T068) adds language and time zone to this same page.
  - `load`: returns `homePage` from `locals.me`;
  - action `save`: read `homePage` (only `changes` or `combined`, else `refuse` with `invalid_home_page`), `PUT /api/me/settings { homePage }`, and return `{ saved: true }`;
  - Page: two radio buttons (Changes feed, Calendars and changes), a Save button, and a `Toast` on success.

**Checkpoint**: US1 and US2 work on their own. This is the MVP. `dotnet test`, `npm run check` and `npm test` are green.

---

## Phase 5: User Story 3 - Choose which changes matter (Priority: P2)

**Goal**: filters in words, the rule editor with the calendar's own values, presets applied, saved, edited and deleted with follower counts (FR-029 to FR-032).

**Independent Test**: on a calendar's Filter tab, see Default; add a rule hiding one group picked from the list; see those changes disappear from `/changes`; save the rules as a preset; apply it to a second calendar; edit the preset and see "2 calendars follow this preset".

### Tests for User Story 3 (write first)

- [ ] T056 [P] [US3] In `backend/InTheKnow.Tests/FilterRulesTests.cs`, assert that `FilterRules.Parse` returns the 0-based index of the failing rule for a fault in the first, a middle and the last rule (`invalid_outcome`, `empty_conditions`, `invalid_condition`, and `invalid_rules` for an unknown key inside one rule), and a null index for a non-array and for 51 rules (contracts/api.md § 3).
- [ ] T057 [P] [US3] In `backend/InTheKnow.Tests/PresetsTests.cs` (or `ApiTests.cs` where the route tests live), assert:
  - the `rule` member is on the `400` of `PUT /api/me/calendars/{id}/filter`, `POST` and `PUT /api/me/presets[/{id}]`, and `POST` and `PUT /api/admin/presets[/{id}]`, and is absent for an array-level fault;
  - `followers` on `GET /api/me/presets` counts the user's calendars that apply the preset, drops when one is customised or removed, ignores another account's calendars, and is absent on global presets and on every row of `GET /api/admin/presets` (contracts/api.md § 4);
  - **shared calendar**: accounts A and B both add the same link, and A applies its preset. When A removes the calendar, it still exists for B, and A's `subscription_filter` row survives (`Subscriptions.RemoveAsync` deletes only `calendar_link`). A's `followers` must still drop to 0.
- [ ] T058 [P] [US3] Write `frontend/src/lib/server/rules.test.ts`: every outcome (`showOnly`, `hide`, `lowPriority`) and every condition of `specs/002-subscriber-change-filters/contracts/rules.md` (`changeKind`, `statusFrom`, `statusTo`, `eventEnded`, `nameContains`, `activityType`, `group`, `room`, `teacher`) gets a sentence in `en` and `nl` with no raw key, `{`, `[` or `null`. An inactive rule gets its reason. `parseRules` on a `FormData` whose `rules` field is `not json`, and on one whose `rules` is `{}`, gives `invalid_rules`.

### Implementation for User Story 3 (backend, FR-045)

- [ ] T059 [US3] In `backend/InTheKnow/FilterRules.cs`, change `Parse` to return `(IReadOnlyList<Rule>? Rules, string? Error, int? Rule)`: the loop index for per-rule faults, `null` for the array-level `invalid_rules` (not an array, or more than `MaxRules`). Update all call sites:
  - `backend/InTheKnow/FilterApi.cs:21` (calendar filter PUT), `:53` (`CreateAsync`) and `:64` (`UpdateAsync`'s `is var (parsed, error)` pattern): on an error, return `Problem(400, error, rule is int i ? new() { ["rule"] = i } : null)`, using `Api.Problem`'s existing `extra` parameter;
  - the two-value deconstructions in the tests: `backend/InTheKnow.Tests/FilterRulesTests.cs:67` and `:79`, and `backend/InTheKnow.Tests/FilterEngineTests.cs:40`. Change them to `var (rules, error, _) =`;
  - `backend/InTheKnow/Presets.cs:202` and `FilterRulesTests.cs:9` use `.Rules`/`.Error` and need no change.

  Grep `FilterRules.Parse(` across `backend/` (excluding `obj/`) afterwards, to be sure no call site was missed.
- [ ] T060 [US3] Add `followers` to personal presets on `GET /api/me/presets` only (contracts/api.md § 4). In `backend/InTheKnow/Presets.cs`, add `Task<Dictionary<long, int>> FollowersAsync(long owner, CancellationToken ct)`: `SELECT f.preset_id, count(*) FROM subscription_filter f WHERE f.subscriber = $1 AND f.preset_id IS NOT NULL AND EXISTS (SELECT 1 FROM calendar_link l WHERE l.calendar_id = f.calendar_id AND l.subscriber = f.subscriber) GROUP BY f.preset_id`. The `EXISTS` skips filter rows left behind when the user removed a calendar that other accounts still have. It is `EXISTS` and not a `JOIN` because one account can have several links to one calendar (check the column names against `002_filters.sql` and `004_accounts.sql`). In `backend/InTheKnow/FilterApi.cs`, the `me.MapGet("/presets", …)` handler passes that lookup to a variant of `Items`/`PresetJson` that sets `followers` (0 when absent) on rows whose `Owner` is not null. The admin route and global rows stay as they are. Don't change `PresetRow` or `ReadPreset`.

### Implementation for User Story 3 (frontend)

- [ ] T061 [US3] Write `frontend/src/lib/server/rules.ts` (research R14):
  - `ruleSentence(rule, t): string`, for example "Hide changes to holidays when the location changes";
  - `inactiveReason(ruleIndex, inactiveRules, activeConditions, t)`: "group, room, teacher and activity type rules only work on MyTimetables calendars", or the matching reason;
  - `parseRules(data: FormData): { rules: unknown[] } | { reason: 'invalid_rules' }`, which `JSON.parse`s the hidden `rules` field and requires an array. The API stays the validator;
  - `editorRules(rules, inactiveRules, activeConditions, error, t)`: each rule plus `sentence`, `inactive`, `inactiveReason`, and `error` when `error.rule` is its index.
- [ ] T062 [US3] Write `frontend/src/lib/components/RuleEditor.svelte` (research R14):
  - props: `rules` (initial), `activeConditions`, `values` (per key, from the filter response, empty for presets), `errors` (per index), `formError`;
  - state: the rules array in `$state`, serialised into a hidden `<input name="rules">` (JSON) that the enclosing form submits;
  - per rule: the outcome `<select>` (Show only, Hide, Mark low priority); conditions added and removed with buttons; `changeKind` as checkboxes limited to `activeConditions`; `statusFrom`/`statusTo` as `<select>`s; `eventEnded` as one checkbox; `nameContains` as text with `maxlength="100"`; `activityType`, `group`, `room` and `teacher` as multi-value text entries with a `<datalist>` of `values[key]`, which still accept typed values (US3-2);
  - move up, move down and remove per rule, and "add rule";
  - an error under rule *n* when `errors[n]` is set, and `formError` above the editor. The user's edits stay after a refusal (FR-032);
  - every control is labelled and keyboard operable (FR-043).
- [ ] T063 [US3] Write `frontend/src/routes/calendars/[id]/filter/` (FR-029 to FR-031, US3-1 to US3-5):
  - `load`: in parallel `GET /api/me/calendars/{id}/filter` and `GET /api/me/presets` (`must(…, { notFound: 'notInAccount' })`). Return the source (preset name, or "customised"), the rules via `editorRules`, and the preset list;
  - action `saveRules`: `parseRules`, then `PUT /api/me/calendars/{id}/filter { rules }`. On a refusal, `refuse()` carries `rule`. Also return the submitted rules so the editor keeps them;
  - action `applyPreset`: `PUT /api/me/calendars/{id}/filter/preset { presetId }` (US3-4);
  - action `saveAsPreset`: `POST /api/me/presets { name, rules }`. `name_taken` and `preset_limit` get their messages (US3-5);
  - Page: the source line; the rules as sentences, inactive ones marked with their reason; the `RuleEditor` in a form posting to `?/saveRules`; a preset `<select>` with an Apply button; a name field with "Save as preset"; a link to `/presets`.
- [ ] T064 [US3] Write `frontend/src/routes/presets/` (US3-7, pages.md):
  - `load`: `GET /api/me/presets`, split into personal (with `followers` and the rule sentences) and global (Default marked);
  - action `create`: `POST /api/me/presets { name, rules: [] }` or with the rules of a new `RuleEditor`, then `303 /presets/{id}`;
  - action `copy`: `POST /api/me/presets { name, rules }` with a global preset's rules and a name from the form ("Copy of …" by default);
  - Page: personal presets linking to `/presets/{id}`; global presets with their sentences and a Copy button only (no edit, no delete); a create form.
- [ ] T065 [US3] Write `frontend/src/routes/presets/[id]/` (US3-6):
  - `load`: find the preset in `GET /api/me/presets`. A global or missing one → `error(404, { kind: 'notFound' })`;
  - action `save`: `PUT /api/me/presets/{id} { name, rules }`, with `rule` errors under their rule;
  - action `delete`: `DELETE /api/me/presets/{id}`, then `303 /presets`;
  - Page: a name field, `RuleEditor`, Save, and Delete. The Save confirmation `Dialog` (shown only when `followers > 0`) says "N calendars follow this preset; they will use the new rules". The Delete `Dialog` says "N calendars follow this preset; they keep its last rules as a custom filter" (FR-031).

**Checkpoint**: US3 works on its own. `dotnet test`, `npm run check` and `npm test` are green.

---

## Phase 6: User Story 4 - Control my notifications (Priority: P2)

**Goal**: per-calendar notifications and label, email channels, and the language and time zone (FR-033, FR-034, FR-048, FR-049).

**Independent Test**: from a change email, open the calendar's notification link while signed out, sign in, land on that tab, and turn notifications off. On `/notifications`, add a second address, confirm it from Mailpit, send a test, pause it and remove it.

- [ ] T066 [P] [US4] Write `frontend/src/routes/calendars/[id]/notifications/` (FR-014, US4-1 to US4-3, US4-11):
  - `load`: `GET /api/me/calendars/{id}/notifications` (003 contracts/api.md) through `must(…, { notFound: 'notInAccount' })`. Opening the page changes nothing;
  - action `save`: `PUT /api/me/calendars/{id}/notifications { notify, label }`, where an empty label is sent as `null` (clear). Label 1 to 100 characters: `maxlength="100"`, and the API's refusal is shown next to the field;
  - Page: a `Switch` for notifications, a label `Field`, Save, a `Toast`, and a note: "where notifications are sent (email addresses) is on the Notifications tab of your settings", linking to `/notifications`.
- [ ] T067 [US4] Write `frontend/src/routes/(settings)/notifications/` (FR-034, US4-4 to US4-7, US4-10, US4-11, data-model.md § Channel):
  - `load`: in parallel `GET /api/me/channels` and `GET /api/me/channel-types`. For each channel: `target` as text, the status label, `channelFailureMessage(failureReason)` when failing, `lastMessageAt` formatted, `accountAddress`, and the allowed actions from data-model.md's table (`awaitingConfirmation`: resend, remove; `active`: pause, test, remove; `paused`: resume, remove; `failing`: resend, remove; `unsubscribed`: resend, remove). `emailAvailable` = the types include `email`;
  - actions `add` (`POST /api/me/channels { type: 'email', target }`, then "open the email we sent to that address"), `pause`, `resume`, `test`, `resend` (`POST …/{id}/confirmation`) and `remove` (`DELETE`, behind a `Dialog`), each taking a `channelId` field. Refusals go through `refuse()`: `target_taken`, `channel_limit`, `target_declined`, `invalid_status`, `channel_not_active`, and `confirmation_limit` and `test_limit` with their wait (003 contracts/api.md § Errors summary);
  - Page: the channel list, with "Account address" marked (US4-7) and only the allowed action buttons per row; the add form, or "adding addresses isn't available right now" when `!emailAvailable`; a note that language and time zone on Preferences apply to emails, linking to `/settings/preferences`; a note that each calendar's notifications and label are on its page, linking to `/calendars`.
- [ ] T068 [US4] Extend `frontend/src/routes/(settings)/settings/preferences/` (from T055) with language and time zone (FR-033, US4-8 to US4-10):
  - `load`: also `GET /api/me/notification-settings` → `language`, `timeZone` (null = default), `defaultTimeZone`, `effectiveTimeZone`; plus `Intl.supportedValuesOf('timeZone')`;
  - action `save`: send the home page to `PUT /api/me/settings` as now, and language and time zone to `PUT /api/me/notification-settings { language, timeZone }`, where the "Default (…)" option sends `timeZone: null`. Refusals via `refuse()`. After a language change, the next render uses the new language (`invalidateAll` through `update()`);
  - Page: a language `<select>`; a time zone `<select>` headed by "Default (<defaultTimeZone>)"; the home page radios; a note that emails follow the language and time zone.

**Checkpoint**: US4 works on its own.

---

## Phase 7: User Story 5 - Recover and manage my account (Priority: P2)

**Goal**: password reset, email change, sessions and methods, sign out everywhere, account deletion, and the step-up flow (FR-019, FR-020).

**Independent Test**: request a reset, open the link, set a new password, sign in with it. Change the email address (the step-up dialog appears and the change continues without retyping), confirm from the new address, sign out another device, and delete the account.

### Tests for User Story 5 (write first)

- [ ] T069 [P] [US5] `frontend/src/lib/enhance.test.ts`: `isStepUp(result)` (exported from `enhance.svelte.ts`) is true for `{ type: 'failure', status: 403, data: { stepUp: true } }` and false for a failure without `stepUp`, for `success`, `redirect` and `error` results, and for `data: null`.

### Implementation for User Story 5

- [ ] T070 [US5] Add `reauthenticate(event)` to `frontend/src/lib/server/actions.ts`: reads `password` and optional `code`, calls `POST /api/me/reauthenticate` (004 contracts/api.md § `POST /api/me/reauthenticate`), and returns `{ reauthenticated: true }` or `refuse()` without echoing either field (research R8).
- [ ] T071 [US5] Write `frontend/src/lib/components/StepUp.svelte`: a `Dialog` with a password field, plus a code field when `me.twoFactor`, posting to `?/reauthenticate` with `use:enhance`. On `{ reauthenticated: true }`, it calls an `onConfirmed()` prop and closes. It shows the refusal message otherwise.
- [ ] T072 [US5] Extend `frontend/src/lib/enhance.svelte.ts` with step-up (research R8). Export the pure `isStepUp(result: ActionResult): boolean` that T069 tests. When it is true, keep a reference to the submitted `HTMLFormElement` and its submitter, expose `stepUpOpen = true`, and do not reset the form. `StepUp`'s `onConfirmed` then calls `form.requestSubmit(submitter)`. The original fields are still filled in, so the user doesn't retype anything (FR-020, spec edge case "Step-up expires").
- [ ] T073 [P] [US5] Write `frontend/src/routes/account/forgot-password/`: an email field and `BotCheck`. The `default` action calls `POST /api/auth/password-reset { email, botCheck }` and always shows the same "if the address has an account, we sent an email" result (FR-016, US5-1, SC-005). `try_again_later` and waits are the only other messages.
- [ ] T074 [P] [US5] Write `frontend/src/routes/account/reset/`. `load` calls no API. The page has a new password field with `PasswordRules` and a button. The `default` action calls `POST /api/auth/password-reset/complete { token, password }`, with the token from the query. Success: "your password is changed and every device was signed out; sign in", and `clearSession` (US5-2). `token_invalid` → "this link no longer works", with a link to `/account/forgot-password`. Password rule refusals show next to the field.
- [ ] T075 [P] [US5] Write `frontend/src/routes/account/email/`. `load` calls no API. The page has one "Confirm the new address" button. The `default` action calls `POST /api/auth/email-change/confirm { token }`. Success: "your address is changed". `address_taken` and `token_invalid` each explain and link to `/settings/account` for a new change (US5-3, US5-4).
- [ ] T076 [US5] Write `frontend/src/routes/(settings)/settings/account/` (FR-048 Account tab, US5-5, US5-6, US5-8):
  - `load`: `email` and `pendingEmail` from `locals.me`;
  - action `changeEmail`: `POST /api/me/email { email }`. `reauthentication_required` → step-up; `invalid_email` and `same_email` are explained. Success (`202`, the same for a taken address): "open the email we sent to the new address";
  - action `deleteAccount`: `DELETE /api/me`. `reauthentication_required` → step-up. `last_admin` → "you're the last admin; you can't delete your account". Success → `clearSession`, `303 /`;
  - action `reauthenticate` (T070);
  - Page: the address and pending change; the change form; a delete button behind a `Dialog` listing what is deleted (calendars, filters, presets, channels, the account); `StepUp`.
- [ ] T077 [US5] Write `frontend/src/routes/(settings)/settings/security/` with password, sign-in methods, devices and sign out everywhere (FR-019, US5-6, US5-7). The 2FA section comes in US6 (T081).
  - `load`: in parallel `GET /api/me/sessions` and `GET /api/me/methods`, with each last use formatted and the current session marked;
  - actions: `setPassword` (`PUT /api/me/password { password }`; a "change" or "add" label per `hasPassword`; password rules; step-up), `removeMethod` (`DELETE /api/me/methods/{id}`, `Dialog`, step-up, `last_method` explained), `endSession` (`DELETE /api/me/sessions/{id}`; the current session isn't offered), `signOutEverywhere` (`POST /api/auth/sign-out-everywhere`, `Dialog`, then `clearSession` and `303 /sign-in`), and `reauthenticate` (T070);
  - Page: a password section with `PasswordRules`; the methods list; the devices list with "this device" marked; "Sign out everywhere"; `StepUp`.

**Checkpoint**: US5 works on its own.

---

## Phase 8: User Story 6 - Protect my account with an authenticator app (Priority: P2)

**Goal**: 2FA setup with a QR code, recovery codes shown once, sign-in with a code, and turning 2FA off (FR-021).

**Depends on**: US5, for `StepUp` (T071, T072) and the security tab (T077).

**Independent Test**: turn on 2FA by scanning the code, save the recovery codes, sign out, sign in with password plus code, then sign in once with a recovery code and see the count left drop.

### Tests for User Story 6 (write first)

- [ ] T078 [P] [US6] `frontend/src/routes/sign-in/code/code.test.ts`, in the style of T031:
  - `load` without `itk_pending` → `303 /sign-in?…&expired=code`;
  - the action sends the `itk_pending` value as `opts.token`. On `200` it calls `setSession` with **that same value**, then `clearPending`, then `303 safeNext(next)`;
  - `401` → `clearPending`, no `setSession`, `303 /sign-in?…&expired=code`;
  - `400 code_invalid` → a `fail` without the code in `values`, and `itk_pending` kept.

### Implementation for User Story 6

- [ ] T079 [US6] Write `frontend/src/routes/sign-in/code/` (US6-3, 004 contracts/api.md § `POST /api/auth/sign-in/code`):
  - `load`: without `itk_pending`, `303 /sign-in?next=…&expired=code`;
  - `default` action: `POST /api/auth/sign-in/code { code }` with `opts.token` = the `itk_pending` value and `on401: 'return'`. The API answers `200 { secondFactorRequired: false }` and returns no new token: the same token is now fully signed in. So success → `setSession(<the itk_pending value>)`, `clearPending`, `303 safeNext(next)`. `401` → `clearPending`, `303 /sign-in?next=<next>&expired=code`. `code_invalid` and `too_many_attempts` with the wait via `refuse()`;
  - Page: one code field (`autocomplete="one-time-code"`, `inputmode="numeric"`) that also accepts a recovery code, with a hint saying so.
- [ ] T080 [US6] Write `frontend/src/routes/(settings)/settings/security/two-factor/` (US6-1, US6-2, pages.md):
  - action `start`: `POST /api/me/2fa/setup` → `{ secret, uri }`. Return the secret and a QR data URL made on the server with `uqr` (`renderSVG(uri)`, encoded as `data:image/svg+xml;base64,…`) (plan § Constraints: no `{@html}`);
  - action `enable`: `POST /api/me/2fa/enable { code }` → the 10 recovery codes, returned once in the action result and never stored;
  - Page: a "Start" button. Then the QR as `<img alt>`, the secret as text (grouped for reading), and a code field. After enabling: the codes in a list, a warning that they can't be shown again, a Copy button (`navigator.clipboard.writeText`), and a Download link (`data:text/plain` with `download="intheknow-recovery-codes.txt"`). While the codes are shown, `beforeNavigate` asks for confirmation and `beforeunload` is set (spec edge case "Recovery codes not saved"). An admin who just enabled 2FA sees a link to `/admin/accounts`.
- [ ] T081 [US6] Add the 2FA section to `frontend/src/routes/(settings)/settings/security/` (from T077) (US6-4, US6-5):
  - the status (on or off), a link to `/settings/security/two-factor` when off, and the recovery codes left from `locals.me`;
  - action `newRecoveryCodes`: `POST /api/me/2fa/recovery-codes { code }` → the new codes, shown once with the same copy, download and leave warning as T080. Reuse that markup as `frontend/src/lib/components/RecoveryCodes.svelte`, used by both pages;
  - action `disable2fa`: `DELETE /api/me/2fa` with no body, behind a `Dialog`. The API needs a step-up that included a code, which `StepUp` asks for when 2FA is on. `demote_first` → "give up the admin role first"; `two_factor_off` is explained.

**Checkpoint**: US6 works together with US5.

---

## Phase 9: User Story 7 - Join through an invite (Priority: P2)

**Goal**: named invites accepted on their page, and shareable invite links on sign-up (FR-012, FR-022).

**Independent Test**: as admin (with the API, until US8), create a named invite; open the emailed link, choose a password, and land signed in. Create a shareable invite and sign up through its link with an address outside the allowed domains.

### Tests for User Story 7 (write first)

- [ ] T082 [P] [US7] `frontend/src/routes/account/sign-up/sign-up.test.ts`, in the style of T031:
  - `load` with `?invite=T` sets `itk_invite=T` with `httpOnly: true, secure: true, sameSite: 'lax', path: '/account/sign-up', maxAge: 3600`, calls no API, and throws `303 /account/sign-up`;
  - the action sends `invite: 'T'` from the cookie, and deletes the cookie after a `202`. After a refusal the cookie stays;
  - the `202` result is the same object for every address (SC-005).

### Implementation for User Story 7

- [ ] T083 [US7] Extend `frontend/src/routes/account/sign-up/` (from T037) with shareable invites (research R15, data-model.md):
  - `+page.server.ts` `load`: when `url.searchParams.get('invite')` is set, set the `itk_invite` cookie with `httpOnly: true, secure: true, sameSite: 'lax', path: '/account/sign-up', maxAge: 3600`, and `throw redirect(303, '/account/sign-up')`. This runs whether or not a bot check is configured, and changes no data (SC-004). Return `invite: Boolean(cookies.get('itk_invite'))`;
  - the `default` action sends `invite` from the cookie, and deletes the cookie after a successful sign-up. `invite_invalid` → "this invite no longer works" (US7-4);
  - Page: "you're signing up with an invite" when `invite` is set (US7-3).
- [ ] T084 [US7] Write `frontend/src/routes/account/invite/` (US7-1, US7-2, pages.md). `load` calls no API. The page has a password field with `PasswordRules`, a language `<select>` whose first option, "The invite's language", sends no `language`, and a button. The `default` action calls `POST /api/auth/invite/accept { token, password, language? }`. The response is the sign-in body (`{ token, secondFactorRequired: false }`) with no role. So success → `setSession(token)`, then `GET /api/me` with `opts.token` = that token to read `role`. When it is `admin`, show "admin pages unlock once you turn on two-step verification", with a link to `/settings/security/two-factor`; otherwise `303 /`. `invite_invalid` → "this invite no longer works".

**Checkpoint**: US7 works on its own.

---

## Phase 10: User Story 8 - Admins run the app (Priority: P3)

**Goal**: accounts, sign-up settings and invites, global presets, the calendars overview and the email status (FR-035 to FR-039).

**Depends on**: US3, for `RuleEditor` and `rules.ts`. To exercise it you need an admin with 2FA (US6).

**Independent Test**: as an admin with 2FA, search for an account and promote it (refused without 2FA, then allowed), invite a named address, revoke a shareable invite, edit Default's rules, and list failing calendars.

- [ ] T085 [US8] Write the admin layout (FR-035, US8-1, US8-2):
  - `frontend/src/routes/admin/+layout.server.ts`: `locals.me.role !== 'admin'` → `error(403, { kind: 'notAllowed' })`. Otherwise call `GET /api/admin/channel-types` through `must()`, so a session without a second factor gets the `locked` page from the API's `two_factor_required`. Return the channel types (reused by `/admin/email`);
  - `frontend/src/routes/admin/+layout.svelte`: a `PageIntro` ("Admin") and `TabNav` with Accounts, Sign-up, Presets, Calendars, Email;
  - `frontend/src/routes/admin/+page.server.ts`: `303 /admin/accounts`.

  Admin actions don't run the layout load. Their API refusals `admin_required` and `two_factor_required` go through `refuse()` with a message, and a demoted or disabled admin gets that message or the sign-in redirect (spec edge case).
- [ ] T086 [P] [US8] Write `frontend/src/routes/admin/accounts/` (FR-036, US8-3, US8-4):
  - `load`: `GET /api/admin/accounts?q=&after=&limit=50`, with `q` passed through and `after` only as a positive integer. Rows: email, role, confirmed, disabled, 2FA, created;
  - actions `setRole` (body `{ role }`), `disable`, `enable`, `delete` and `remove2fa`, each taking `accountId`, the destructive ones (`disable`, `delete`, `remove2fa`, demote) behind a `Dialog`. Routes (004 contracts/api.md § Account actions): `PUT /api/admin/accounts/{id}/role`, `POST …/{id}/disable`, `POST …/{id}/enable`, `DELETE …/{id}`, `DELETE …/{id}/2fa`. The refusals `invalid_role`, `two_factor_required` (a `409` here: "turn on 2FA before becoming admin", not the locked page), `not_confirmed`, `account_disabled`, `last_admin`, `demote_first` and `two_factor_off` go through `refuse()`;
  - Page: a search `GET` form, the list (cards on phones, never a wide table), "Next page" when `next` is set.
- [ ] T087 [P] [US8] Write `frontend/src/routes/admin/sign-up/` (FR-037, US8-5, US8-6):
  - `load`: in parallel `GET /api/admin/signup-settings` and `GET /api/admin/invites`;
  - actions `saveSettings` (`PUT /api/admin/signup-settings { open, allowedDomains }`, with domains from a textarea, one per line; an invalid domain is reported), `createInvite` (`POST /api/admin/invites` with kind `named` + email + role + language, or `shareable` + uses + expiry, per 004 contracts/api.md), `resend` (`POST …/{id}/resend`) and `revoke` (`DELETE …/{id}`, `Dialog`);
  - a new shareable invite's link, built as `${url.origin}/account/sign-up?invite=${token}`, is returned once in the action result: shown with a Copy button and a "can't be shown again" warning, with `beforeNavigate`/`beforeunload` confirmation while shown (spec edge case);
  - Page: the settings form, the create form, and the pending invites with uses left and expiry.
- [ ] T088 [P] [US8] Write `frontend/src/routes/admin/presets/` and `frontend/src/routes/admin/presets/[id]/` (FR-038, US8-7), mirroring T064 and T065 on `GET`/`POST /api/admin/presets` and `PUT`/`DELETE /api/admin/presets/{id}`, with the same `RuleEditor` and `rule` errors. Default is marked and has no Delete button. There is no followers text.
- [ ] T089 [P] [US8] Write `frontend/src/routes/admin/calendars/` (FR-039, US8-8): `load` calls `GET /api/admin/calendars?status=&overdue=&after=&limit=50`, with `status` only `ok`/`failing` and `overdue` only `true`. Rows: name, status `Badge`, last error via `checkFailureMessage`, subscriber count and link counts. **No links or addresses.** A filter `GET` form and "Next page".
- [ ] T090 [P] [US8] Write `frontend/src/routes/admin/email/+page.svelte` (and a `+page.server.ts` that formats dates) (FR-039, US8-9): per channel type from the layout data, show configured or not, the last attempt and its result (`channelFailureMessage`), consecutive failures, failing since, and messages waiting.

**Checkpoint**: US8 works together with US3 and US6.

---

## Phase 11: User Story 9 - Use the app in English or Dutch (Priority: P3)

**Goal**: visitors see their browser's language and can switch. Signed-in users see their account's language (FR-040). Resolving the language is in Phase 2 (T020), and the signed-in switch is in Preferences (T068).

**Independent Test**: visit `/sign-in` with a Dutch browser and see Dutch; switch to English; sign in to an account set to Dutch and see Dutch.

- [ ] T091 [US9] Write `frontend/src/routes/language/+page.server.ts`: a `GET` gets `303 /`. The `default` action reads `lang` (only `en`/`nl`, else ignored) and `next`, sets `itk_lang` with `httpOnly: false, secure: true, sameSite: 'lax', path: '/', maxAge: 31536000` (**not** `HttpOnly`; data-model.md), and answers `303 safeNext(next)`.
- [ ] T092 [US9] Add the visitor language switch to `frontend/src/lib/components/Header.svelte`: a small `<form method="POST" action="/language">` with a hidden `next` (the current path and query) and one button for the other language ("English" / "Nederlands", each with `lang` set on the button text). It is shown only to visitors.

**Checkpoint**: all stories work.

---

## Phase 12: Polish & cross-cutting concerns

- [ ] T093 [P] Update `README.md` (plan § Project Structure): add the frontend to Features and to "Run it" (`cd frontend`, `npm ci`, `npm run dev`, the environment of contracts/configuration.md for production, and the reverse proxy rules), and remove the "frontend doesn't exist yet" workaround.
- [ ] T094 [P] Add a one-line pointer to `specs/005-sveltekit-frontend/contracts/api.md` in `specs/001-ical-change-detection/contracts/api.md` (under `GET /api/me/changes`) and in `specs/002-subscriber-change-filters/contracts/api.md` (under the changes and presets sections).
- [ ] T095 Run every check from `frontend/` and the repository root, and fix what fails: `npm run check`, `npm test`, `npm run build` (in `frontend/`) and `dotnet test`. Then `grep -rn "{@html" frontend/src` finds nothing besides the test file.
- [ ] T096 Run the quickstart § 2 manual walkthrough (`specs/005-sveltekit-frontend/quickstart.md`), steps 1 to 14, against the running API, Mailpit and `npm run dev`. That includes the 360 px and keyboard-only pass over every address in pages.md (SC-007), the token checks in devtools (SC-003), and the log check (FR-047). Tick each row of pages.md's coverage table as it is exercised (SC-002). Record the results and any fixes in `specs/005-sveltekit-frontend/quickstart.md` under a dated "Walkthrough results" note.

---

## Dependencies & Execution Order

### Phase dependencies

- **Setup (Phase 1)**: none.
- **Foundational (Phase 2)**: after Setup. **Blocks every story.** Inside it, T014 and T015 come before T021, and T018 comes before T020 and T022.
- **US1 (Phase 3)** and **US2 (Phase 4)**: after Phase 2. They are independent of each other, but US2's pages are only reachable once someone can sign in (US1).
- **US3, US4, US5, US7**: after Phase 2 (and US1 to sign in).
- **US6**: after **US5** (`StepUp`, the enhance step-up, and `/settings/security`).
- **US8**: after **US3** (`RuleEditor`, `rules.ts`). To exercise it you need US6.
- **US9**: after Phase 2. It edits `Header.svelte` (from T028).
- **Polish**: after the stories you ship.

### Shared files between stories (do these in order)

- `frontend/src/routes/(settings)/settings/preferences/`: T055 (US2, home page) → T068 (US4, language and time zone).
- `frontend/src/routes/(settings)/settings/security/`: T077 (US5) → T081 (US6).
- `frontend/src/routes/account/sign-up/`: T037 (US1) → T083 (US7).
- `frontend/src/routes/+page.server.ts` and `+page.svelte`: T040 (US1) → T054 (US2).
- `frontend/src/lib/server/actions.ts`: T019 → T033 (US1) → T050 (US2) → T070 (US5).
- `frontend/src/lib/enhance.svelte.ts`: T027 → T072 (US5).
- `frontend/src/lib/components/Header.svelte`: T028 → T092 (US9).
- `backend/InTheKnow/FilterApi.cs`: T059 → T060 (both US3).

### Within each story

- Tests first, and they fail before the implementation.
- Backend before the frontend pages that call it (US2: T044 to T046 before T051 and T055; US3: T059 and T060 before T063 to T065).
- Server modules (`changes.ts`, `rules.ts`) before the components and pages that print their output.

---

## Parallel examples

### Phase 2

```text
Task: "T006 next.test.ts"          Task: "T007 format.test.ts"      Task: "T008 api.test.ts"
Task: "T009 i18n index.test.ts"    Task: "T011 no-raw-html.test.ts" Task: "T012 codes.test.ts"
Task: "T013 actions.test.ts"
then
Task: "T016 next.ts"   Task: "T017 format.ts"   Task: "T023 Button/Card/Badge/PageIntro"
Task: "T024 Field/Switch"   Task: "T025 Dialog/Toast"   Task: "T026 TabNav"
```

### User Story 2

```text
Task: "T041 ApiTests newest-first"   Task: "T042 AccountTests + DbTests home page"   Task: "T043 changes.test.ts"
then backend T044 → T045, T046, alongside frontend T047 → T048, T049 in parallel
```

### User Story 3

```text
Task: "T056 FilterRulesTests rule index"   Task: "T057 PresetsTests rule member + followers"   Task: "T058 rules.test.ts"
```

### User Story 8

```text
After T085: Task: "T086 admin/accounts"   Task: "T087 admin/sign-up"   Task: "T088 admin/presets"
            Task: "T089 admin/calendars"  Task: "T090 admin/email"
```

---

## Implementation Strategy

### MVP first (US1 + US2)

1. Phase 1 and Phase 2. All foundational tests are green.
2. US1: sign up, confirm, sign in, sign out.
3. US2: the backend additions (newest-first paging, the home page setting), then the calendars, the changes feed and the home page.
4. **Stop and validate**: the US1 and US2 independent tests, and quickstart § 2 steps 1 to 4.

### Incremental delivery

1. MVP (US1 + US2).
2. US3 (filters and presets) and US4 (notifications). Either order.
3. US5, then US6. They share the security tab and the step-up flow.
4. US7 (invites).
5. US8 (admin). It needs US3's editor, and US6 to use it.
6. US9 (the visitor language switch).
7. Polish: README, the contract pointers, all checks, and the full walkthrough.

---

## Notes

- [P] tasks touch different files and have no dependency on an unfinished task. Dictionary key additions are the accepted exception (see Format).
- The `Reason` union and every code message are complete from T021 on. A story that finds a reason missing adds it to `codes.ts` and both dictionaries, and `codes.test.ts` keeps them honest.
- Where a task says "use the exact reason names from … contracts/api.md", copy them from that file. Don't guess a spelling.
- Commit after each task or logical group. Stop at any checkpoint to validate that story on its own.
