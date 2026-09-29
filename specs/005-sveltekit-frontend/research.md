# Research: Web Frontend

Phase 0 of [plan.md](./plan.md). Each entry gives the decision, why it was chosen and what else was considered.
There were no open questions left in the spec. During planning, the user decided three things: how fonts are
handled (R18), and two more API additions (R13).

## R1. Project setup and toolchain

**Decision**:
- A new `frontend/` directory beside `backend/`.
- SvelteKit 2 with Svelte 5 (runes), TypeScript, Vite, and `@sveltejs/adapter-node`.
- npm with a committed `package-lock.json`, and `"engines": { "node": ">=24" }` (Node 24 is the current LTS).
- Checks:
  - `svelte-check` does type checking. It is also the translation check (R10).
  - `vitest` runs the unit tests (R19).
  - `vite build` is the build.
- No ESLint and no Prettier.

**Rationale**:
- The constitution fixes Svelte with SvelteKit.
- adapter-node gives one plain Node process. It runs next to the API behind the same proxy (spec Assumptions) and
  fits in a container later.
- TypeScript lets `svelte-check` catch mismatches with the API shapes and missing translations at build time.
- The one lint rule that matters here, "no raw HTML" (FR-010), is a one-line check in the test suite (R19). A
  linter stack isn't needed for it.

**Alternatives considered**:
- JavaScript with JSDoc: this loses the compile-time translation check (R10).
- pnpm: one more tool to install, and npm ships with Node.
- adapter-static with client-side API calls: this breaks FR-003, because the browser would hold the token.
- ESLint with `svelte/no-at-html-tags`: three more dev dependencies to enforce one rule.

## R2. Rendering and data flow

**Decision**:
- Every page is rendered on the server.
- Data comes from `+page.server.ts` `load` functions.
- Every change is a **form action** (`POST`), progressively enhanced with `use:enhance`.
- The browser calls only these pages and actions. There are no `+server.ts` JSON endpoints.
- One server-only module, `$lib/server/api.ts`, calls the API.
- Text built from API data (change sentences, dates, rule sentences, messages for reason codes) is built in `load`
  and actions on the server. Components only print strings.

**Rationale**:
- FR-003: `$lib/server` can't be imported into browser code, because SvelteKit refuses to build that. So the token
  and the API URL never reach the browser.
- Form actions give CSRF protection (R4) and double-submit prevention (R8) in one place.
- Formatting on the server means one ICU version is used. A browser with a different ICU version can't produce a
  hydration mismatch in dates.
- FR-002: nothing is cached between requests. A page reads what it needs from the API on every load.

**Alternatives considered**:
- A client-side SPA that calls a proxy route (`/api/*` → API): the whole API surface would be exposed through a
  generic proxy, and the proxy would need its own CSRF handling.
- Formatting in components: this risks hydration mismatches, and it would ship all the text logic to the browser.

## R3. Browser session (FR-003, FR-004, FR-006)

**Decision**: the browser session is the API token itself, in an HttpOnly cookie. It has no server-side store.

| Cookie | Holds | Attributes | Life |
|---|---|---|---|
| `itk_session` | The API session token (43 chars) | `HttpOnly; Secure; SameSite=Lax; Path=/` | `Max-Age` 400 days. The API's idle lifetime decides validity. |
| `itk_pending` | The awaiting-code token between the password step and the code step | `HttpOnly; Secure; SameSite=Lax; Path=/sign-in` | `Max-Age` 600 s, the API's awaiting-code lifetime |

- `Secure` is SvelteKit's `cookies.set` default. The one exception is plain `http://localhost` in development.
- **Sign-in**:
  - When the API answers `secondFactorRequired: false`, the token goes into `itk_session`.
  - When it answers `true`, the token goes into `itk_pending`, and the code step moves it to `itk_session`.
  - Named-invite acceptance returns a signed-in token and sets `itk_session` directly.
- **Sign-out**: `POST /api/auth/sign-out` with the token, then the cookie is deleted. It is deleted even when the
  API call fails, so the browser is always signed out.
- **API says the session is gone** (FR-006, edge case "Session ends"): every `401 not_signed_in` from the API deletes
  `itk_session`. What happens next depends on where it came from:
  - the `/api/me` lookup in `handle`: the request continues as a visitor's, and the gate (R6) redirects only on a
    gated path. So a public page such as `/account/reset?token=…` still works with an expired cookie;
  - a page `load` or action on a gated path: `303 /sign-in?next=<current path>`;
  - `POST /api/auth/sign-in/code`: the code step timed out. It deletes `itk_pending` and answers
    `303 /sign-in?next=<the original next>&expired=code`, with the spec's explanation.

**Rationale**:
- **Script-proof**: `HttpOnly` means page scripts can't read the token (FR-003, SC-003).
- **Usable only through our server**: `/api` is not exposed by the reverse proxy
  ([contracts/configuration.md](./contracts/configuration.md)). A copied cookie gives exactly what any stolen session
  cookie gives, and no more.
- **Cross-site rules**: `SameSite=Lax` keeps the cookie off cross-site `POST`s and background requests. It is still
  sent on a top-level navigation from an email link, so the user stays signed in (FR-004). `Strict` would sign out
  every user who opens an email link.
- **No state**: the server stores nothing, and a restart of the frontend signs nobody out. A deployment can run more
  than one process.

**Alternatives considered**:
- A random session id in the cookie that maps to the token in memory: a restart signs everyone out, and a shared
  store (Redis) is a new service.
- Encrypting the token in the cookie: it needs a key to manage, and it changes nothing. The cookie is the credential
  whether it is encrypted or not.
- The `__Host-` cookie prefix: it doesn't work on `http://localhost`, so the cookie name would have to differ between
  development and production. It only guards against a sibling subdomain setting the cookie. Add it when the app
  shares a parent domain with untrusted hosts.

## R4. Cross-site request forgery (FR-005)

**Decision**: one check in `hooks.server.ts` `handle`, with `SameSite=Lax` on the session cookie as a second defence.
- Every change is a `POST` form action.
- No `GET` and no `load` function changes anything. Loads only read, which SC-004 also requires.
- `handle` refuses every request with an unsafe method (`POST`, `PUT`, `PATCH`, `DELETE`) with `403`, unless:
  - `Sec-Fetch-Site` is `same-origin`; or
  - `Sec-Fetch-Site` is absent and `Origin` equals the app's origin.
- SvelteKit's own origin check is turned off (`kit.csrf.checkOrigin: false`, or its current equivalent).

**Rationale**:
- The pages must send `Referrer-Policy: no-referrer` (FR-013, 004 configuration).
- Under that policy, the Fetch standard's "append a request `Origin` header" step sends `Origin: null` on non-GET
  requests that are not CORS-mode, which includes a same-origin form post without JavaScript.
- SvelteKit's `checkOrigin` compares `Origin`, so it would reject the app's own form posts.
- `Sec-Fetch-Site` is set by the browser from the request's initiator, not from the referrer policy, and every
  current browser sends it.
- Cross-origin JSON would need a CORS preflight, and nothing allows one.

**Consequence**: behind the proxy, adapter-node must know its public origin (`ORIGIN`). Without it, `url.origin`
and the `Origin` fallback are wrong ([contracts/configuration.md](./contracts/configuration.md)).

**Alternatives considered**:
- Keeping `checkOrigin` and relaxing the referrer policy to `same-origin`: it breaks 004's `no-referrer` contract
  for the `/account/…` pages.
- Synchronizer tokens in every form: more code for what the browser already reports.

## R5. Passing on the client address and device (FR-007)

**Decision**:
- adapter-node resolves the browser's address with `ADDRESS_HEADER=X-Forwarded-For` and `XFF_DEPTH=1`, which trusts
  exactly the reverse proxy in front of it. `event.getClientAddress()` then returns that address.
- The API client sends:
  - `X-Forwarded-For: <that one address>`;
  - `User-Agent: <the browser's User-Agent>`, which overrides Node fetch's own.
- The operator lists the frontend's address in the API's `TrustedProxies` (004 configuration).

**Rationale**:
- The backend uses `ForwardedHeadersOptions` with `ForwardLimit = null` and only the configured `KnownProxies` and
  networks. It walks `X-Forwarded-For` from the right while each hop is trusted.
- One resolved address from a trusted peer is the simplest input that gives the right answer.
- Appending to the chain the proxy sent would also work. But it relies on the proxy's list being listed in the
  API's `TrustedProxies` too, which is one more thing an operator can get wrong.
- The browser's `User-Agent` is what 004 turns into the device label.

**Alternatives considered**: passing on the incoming `X-Forwarded-For` unchanged. A client that isn't behind the
proxy, such as local development, could then spoof it.

## R6. Sign-in gate and return address (FR-011, FR-014, edge case "Return address")

**Decision**:
- The gate lives in `hooks.server.ts` `handle`. A `+layout.server.ts` would not do: layout loads don't run for form
  actions.
- **Public paths**:
  - `/` (visitor intro, or the signed-in home);
  - `/sign-in` and `/sign-in/code`;
  - `/account/*`;
  - `/language` (the visitor's language switch action);
  - SvelteKit's `/_app/*` assets.
- **Every other path**: without `itk_session`, the answer is `303 /sign-in?next=<path + query>`.
- **`next` is accepted only when all of these hold**:
  - it starts with `/`;
  - it doesn't start with `//` or `/\`;
  - `new URL(next, url.origin).origin === url.origin`.

  Otherwise the target is `/`. One function, `safeNext()`, does this, and it is unit tested with the usual bypasses
  (`//evil`, `/\evil`, `https:evil`, encoded variants, `javascript:`).
- The API stays the real gate. A stale cookie passes this check and then gets `401` from the API (R3).

**Rationale**: every action runs through `handle`, so a check there can't be forgotten on a new route.

## R7. API client and error mapping

**Decision**: `$lib/server/api.ts` exports `api(event, method, path, body?)`. It:
- adds `Authorization`, `X-Forwarded-For` and `User-Agent`;
- sets a 30 s timeout with `AbortSignal.timeout`, because link validation can take seconds;
- returns `{ ok: true, status, data }` or `{ ok: false, status, reason, retryAfter?, extra }`.

Answers the whole app handles in one place:

| API answer | Frontend behaviour |
|---|---|
| `401 not_signed_in` | Delete `itk_session`, then continue as a visitor, redirect to sign-in, or restart sign-in, depending on the caller (R3) |
| `403 two_factor_required` | Admin "locked" page with a link to 2FA setup, or to sign in again with a code (FR-035, US8-1) |
| `403 admin_required` | "Not allowed" page (US8-2) |
| `403 reauthentication_required` | The action returns `fail(403, { stepUp: true })`, and the page opens the step-up dialog (R8) |
| `404` on a calendar route | "This calendar is not in the account `<email>`" (edge case "Notification link for a calendar…") |
| `429` + `Retry-After` | The message for the reason, plus "try again in N minutes/seconds" (FR-042) |
| Network error, timeout, or `5xx` without a known reason | "Temporarily unavailable, try again". Form values are kept, and no details are shown (edge case "Backend unavailable") |
| Any other refusal | `fail(status, { reason, values })`. The page shows `t.reasons[reason]` next to the field (FR-041) |

Values for `fail` go back without passwords, codes or tokens. The other fields stay filled in (US1-3).

## R8. Step-up confirmation and double submit (FR-020, FR-044, edge case "Step-up expires")

**Decision**:
- **Double submit**: one shared `enhance` wrapper (`$lib/enhance.ts`) sets a `pending` flag while a submission is
  in flight. Every submit button is `disabled={pending}`, and the add-calendar button reads "Checking…" (US2-1).
- **Step-up**: when an action fails with `stepUp: true`, the page opens one shared `StepUp.svelte` dialog. It asks
  for the password, plus a code when `/api/me` says 2FA is on, and posts to that page's `?/reauthenticate` action.
  On success, the wrapper calls `requestSubmit()` on the original form, whose fields are still filled in. The user
  doesn't retype anything.
- One helper, `reauthenticate(event)`, backs every page's `reauthenticate` action.

**Rationale**:
- The original fields never leave the browser's form. Hidden fields that echoed a new password or address back into
  the page are not needed.
- A step-up that expires between two submissions is handled the same way: the next action gets
  `reauthentication_required` again.

**Alternatives considered**:
- A password field on every sensitive form: this asks for the password even within the 10-minute window, and the
  spec says to ask only when needed.
- Server-side replay of the stored action: it would need server state.

## R9. Language (FR-040, US9)

**Decision**:
- **Signed in**: the account's `language` from `GET /api/me`.
- **Visitor**:
  - the `itk_lang` cookie when set (`Path=/`, 1 year, not `HttpOnly`, and not personal data per the spec's Language
    choice entity);
  - else the first of `nl`/`en` in `Accept-Language` by q-value;
  - else `en`.
- **Switch for visitors**: a small form posting to `/language`. It sets `itk_lang` and returns to the page.
- **Switch for signed-in users**: on the Preferences tab, `PUT /api/me/notification-settings { language }`. It
  changes the app and the emails, because 003 FR-017 made it one setting.
- `<html lang>` is set per request through `transformPageChunk` (a `%lang%` placeholder in `app.html`).
- **Sign-up**: the language field defaults to the page's language (US9-2).
- **Dutch copy** follows the design system's voice: informal *je/jij*, sentence case and imperative buttons.

## R10. Text dictionaries and machine codes (FR-041, SC-008, SC-009)

**Decision**:
- `$lib/i18n/en.ts` is the source dictionary.
- `$lib/i18n/nl.ts` is declared `const nl: Dict = { … }`, where `type Dict = typeof en` with every leaf widened to
  `string` or to the same function signature. **A missing or extra key fails `svelte-check`** (SC-008).
- Machine codes are typed unions in `$lib/codes.ts`, and each dictionary holds `Record<Code, string>` for them. A
  missing message is a type error (SC-009).

| Code set | Source | Examples |
|---|---|---|
| `Reason` | Every `reason` in 001 to 004 `contracts/api.md`, and in [contracts/api.md](./contracts/api.md) here, for the routes the frontend calls | `invalid_credentials`, `password_common`, `host_budget_exhausted`, `channel_limit`, `name_taken`, `last_admin` |
| `CheckFailure` | 001 data-model `last_error` and `validation_failed:<code>` | `network`, `timeout`, `too_large`, `unparseable`, `empty_feed`, `too_many_occurrences`, `blocked_address` |
| Pattern codes | `http_<n>` (001), `smtp_<n>` (003) | `http_404`/`http_410` → "the link no longer exists"; `http_401`/`http_403` → "the calendar server refused the link"; other `http_*` → "the calendar server answered with an error"; `smtp_5xx` → "the mail server rejected this address"; `smtp_4xx` → "the mail server refused it for now"; `unreachable` → "the mail server can't be reached" |

- **The union is complete**: a vitest test (`codes.test.ts`) reads the reason tables of `specs/00{1..5}*/contracts/api.md`
  (the backticked codes in each `reason` column), and fails when one is missing from `Reason`. Reasons for routes
  the frontend doesn't call, such as `invalid_subscriber`, which is gone since 004, are listed in the test as an
  explicit exclusion.
- A code the frontend doesn't know, from a newer API, gets the generic "Something went wrong" message. The server
  logs the code (never any values), per FR-047.
- No status code or reason code is ever printed (FR-041).

**Alternatives considered**:
- An i18n library such as Paraglide or svelte-i18n: a dependency plus a build step, for two languages and a flat set
  of strings.
- A runtime completeness test: the type check already fails earlier, in the editor and in the build.

## R11. Change sentences, grouping and tones (FR-026, FR-027, SC-006)

**Decision**:
- `$lib/server/changes.ts` exports `describe(change, t, fmt)`. It returns a sentence for every `kind`/`field` in
  001's [detected-change.md](../001-ical-change-detection/contracts/detected-change.md): `added`, `removed`, and
  `changed` on `name`, `time`, `status`, `location`, `description`, `type`, `rooms`, `teachers`, `groups`,
  `details.<label>` and `details.text`.
- **Set fields** (rooms, teachers, groups): the sentence names what was added and what was removed, for example
  "Room AL00.A1.38 → AL00.A1.20" and "Group X added". The whole arrays are not printed.
- **Status**: `tentative → confirmed` reads "event confirmed", and `→ cancelled` reads "cancelled".
- **Grouping**: consecutive items on a page that share `(calendarId, eventKey, detectedAt)` form one group. The
  group heading is the event name, its start in the user's zone and the calendar name (US2-9). A group split across
  two pages shows its heading on both. This is accepted: each page is complete on its own.
- **Tones** map 001's values to the design system's four change types:

| Change | Tone (design system `ChangeNotice` type) | Icon |
|---|---|---|
| `added` | `new` (green) | `plus-circle` |
| `removed`, status → `cancelled` | `cancelled` (red) | `x-circle` |
| `location`, `rooms` | `moved` (amber) | `map-pin` |
| `time` | `changed` (blue) | `clock` |
| status → `confirmed` ("event confirmed") | `changed` (blue) | `check-circle` |
| everything else (`name`, `description`, `type`, `teachers`, `groups`, `details.*`, other status) | `changed` (blue) | `pencil` |

- A group takes the tone of its most severe change, in the order cancelled > moved > new > changed.
- **Low priority** (FR-027): muted text (`--text-muted`), a grey icon instead of the tone, and a neutral "Low
  priority" badge.
- **Hidden** (only with "show hidden changes"): a dashed border, reduced emphasis, and a "Hidden" badge.
- The badges carry the meaning in words, so colour is never the only signal.

**Test**: a vitest fixture holds one change per `kind`/`field` row of 001's table. It asserts that each has a
sentence in both languages, and that no sentence contains a raw field name, `{`, `[` or `null` (SC-006).

## R12. Dates and time zones (FR-028, edge cases "another year", "Daylight-saving")

**Decision**:
- `$lib/server/format.ts` builds `Intl.DateTimeFormat` with the `timeZone` set to the user's `effectiveTimeZone`
  (from `GET /api/me/notification-settings`) and the locale `en-GB` or `nl-NL`.
- The year is added when the date's year *in that zone* differs from the current year in that zone.
- Formatting each instant with its own zone rules gives that moment's offset across daylight-saving changes. That is
  what `Intl` does.
- Times are 24-hour, and ranges use an en dash (design system).
- Times are shown as dates, never as relative times like "5 min ago". FR-028 asks for dates, and one format is less
  code.
- The time zone picker is a native `<select>` of `Intl.supportedValuesOf('timeZone')`, headed by
  "Default (Europe/Amsterdam)". The API stays the judge of what is valid (spec Assumptions).

**Test**: the unit tests cover:
- an instant on each side of the October DST change in Europe/Amsterdam, which shows different offsets;
- the year shown only for another year;
- a zone where the year boundary differs from UTC.

## R13. API additions (FR-045)

The four additions named in the spec. Their details are in [contracts/api.md](./contracts/api.md) and
[data-model.md](./data-model.md).

1. **Newest-first changes**: `GET /api/me/changes?order=newest&before={id}`.
   - It uses the same keyset batching as today, run descending on the existing `(calendar_id, id)` index. So a page
     is never short while older visible changes remain.
   - Without `order`, the behaviour is exactly 001's.
   - *Alternatives*: reversing oldest-first pages in the frontend. It must read every page to find the newest, which
     is O(n) per view.
2. **Home page setting**: a new `account.home_page` column (`'changes' | 'combined'`, default `'changes'`, with a DB
   `CHECK`).
   - Read through a new `homePage` field on `GET /api/me`, which every signed-in request already calls. So there's no
     extra round trip.
   - Written with a new `PUT /api/me/settings`, a general settings route that later settings can join (spec: "Later
     settings get their own tab").
   - *Alternatives*: putting it in `notification-settings`. The name would be wrong, and that route writes the email
     language.
3. **Rule position** (user decision): rule validation refusals gain a `rule` member with the 0-based index of the
   rule that failed.
   - It is present only when a single rule is at fault. Array-level errors (`invalid_rules` for a non-array or more
     than 50 rules) have no `rule`.
   - `FilterRules.Parse` already loops over the rules, so it returns the index with the reason.
4. **Preset followers** (user decision): each personal `Preset` in `GET /api/me/presets` gains `followers`, the
   number of the user's calendars linked to it (`subscription_filter.preset_id`).
   - Global presets don't get the field. Their count would be across all accounts, which isn't a student's data.

## R14. Rule editor (FR-029 to FR-032, FR-038)

**Decision**:
- One `RuleEditor.svelte` is used by the calendar Filter tab, personal presets and admin global presets.
- Its rules live in `$state`, and are serialised to a hidden `rules` field (JSON) on submit. The action
  `JSON.parse`s it (a parse failure maps to `invalid_rules`) and passes it to the API, which validates it.
- **Fields per condition**:
  - `changeKind`: checkboxes, limited to `activeConditions`;
  - `statusFrom`/`statusTo`: selects;
  - `eventEnded`: one checkbox;
  - `nameContains`: text, `maxlength=100`;
  - `activityType`, `group`, `room`, `teacher`: a multi-value text input with a `<datalist>` of `values[key]`.
    Students pick existing values and can still type new ones (US3-2). This is native HTML, with no combobox
    library.
- **Rules in words**: a rule is shown as a sentence ("Hide changes to holidays when the location changes") from the
  same dictionary.
- **Inactive rules**: rules at the `inactiveRules` indexes are marked "doesn't apply to this calendar", with the
  reason ("group, room, teacher and activity type rules only work on MyTimetables calendars").
- **Errors**: an API refusal with `rule: n` is shown under rule *n* (R13). Without it, the error shows above the
  editor. The saved filter is unchanged (FR-032), and the editor keeps the user's edits.

## R15. Bot check widget and invite token (FR-018, edge cases "Token pages and referrers", "Bot check …")

**Decision**:
- The four forms (sign-in, sign-up, confirmation request, reset request) load `GET /api/auth/bot-check` in their
  `load`.
- When a provider is set, the page adds:
  - the provider's script (Turnstile `https://challenges.cloudflare.com/turnstile/v0/api.js`, or reCAPTCHA v2
    `https://www.google.com/recaptcha/api.js`);
  - a `BotCheck.svelte` component that loads the script once (`?render=explicit`, or reCAPTCHA's `onload` +
    `render=explicit`), then calls the provider's `render()` on mount and `remove()`/`reset()` on destroy.

  The widget adds its own hidden field (`cf-turnstile-response` or `g-recaptcha-response`), and the action sends
  that field as `botCheck`.
- **Explicit rendering, not implicit**:
  - the implicit mode scans the page only once, when the script loads, so a client-side navigation to another of
    the four forms would show no widget;
  - the result is single-use, so after every action result (a wrong password, a password rule) the enhance
    callback calls `reset()`. Otherwise the next try would send a spent result and get `bot_check_failed`.
- With no provider, the page has no script.
- **Script fails to load** (a content blocker): the script tag's `onerror`, or a 10 s timer without the widget
  rendering, shows "the check couldn't load; allow challenges.cloudflare.com or try again". The submit button stays
  usable for sign-in, where the API proceeds when the provider is down (004 FR-040).
- **`503 try_again_later`**: sign-up and the request forms show "try again later".
- **Invite token and third-party scripts**: `Referrer-Policy` doesn't stop a script from reading `location.href`.
  So `GET /account/sign-up?invite=X`:
  - stores `X` in an `itk_invite` cookie (`HttpOnly; Secure; SameSite=Lax; Path=/account/sign-up; Max-Age=3600`);
  - answers `303 /account/sign-up`.

  The page then shows "you're signing up with an invite" and the action reads the cookie. This runs whether or not a
  bot check is on, so there is one code path. Setting a cookie changes no data (SC-004).
- The other token pages (`/account/confirm`, `/account/reset`, `/account/email`, `/account/invite`) have no bot
  check and no third-party content. `no-referrer` is enough there (R16).

## R16. Security headers and CSP (FR-008, FR-009, FR-013)

**Decision**:
- `handle` sets these on every response:
  - `X-Frame-Options: DENY`;
  - `Referrer-Policy: no-referrer` on every page, since nothing needs a referrer, so one rule covers FR-013;
  - `X-Content-Type-Options: nosniff`;
  - `Cache-Control: no-store` on every response that goes through `handle`: pages, the `__data.json` of client-side
    navigation, and action results. The last two carry the user's data too (FR-008).
- `no-store` on everything (not only signed-in pages) is one rule instead of two. Public pages are cheap to render.
  The hashed `/_app/immutable/*` assets are served by adapter-node without `handle`, and keep their long cache.
  It also keeps a signed-out user's back button from showing data (US1-8), because browsers don't keep `no-store`
  pages in the back/forward cache once cookies change.
- **CSP** comes from SvelteKit's `kit.csp` (mode `auto`, which gives nonces for its inline scripts):

```text
default-src 'self'; script-src 'self' https://challenges.cloudflare.com https://www.google.com https://www.gstatic.com;
frame-src https://challenges.cloudflare.com https://www.google.com; style-src 'self' 'unsafe-inline';
img-src 'self' data:; font-src 'self'; connect-src 'self'; form-action 'self'; frame-ancestors 'none';
base-uri 'none'; object-src 'none'
```

- The provider hosts are always listed. Listing them while no provider is configured is harmless, and it saves
  building the header per request.
- `style-src 'unsafe-inline'` is needed for Svelte's transition styles.
- HSTS is the reverse proxy's job ([contracts/configuration.md](./contracts/configuration.md)).

## R17. Server logs (FR-047)

**Decision**:
- `handle` writes one line per request:
  - `ISO-time METHOD pathname status durationMs`;
  - `event.url.pathname` only, never `search` or headers.
- `handleError` writes `ISO-time pathname error-name: message` and returns a generic error to the page. The API
  client's errors carry only the method, the API path template, the status and the reason. Never bodies,
  parameters, tokens or addresses.
- adapter-node writes no access log of its own.

**Test**: a unit test runs `handle` on a request with a query string, a cookie and `X-Forwarded-For`. It asserts
that the logged line contains none of them.

## R18. Porting the design system (user direction: spec for behaviour, design system for looks)

**Source**: the claude.ai design project "InTheKnow Design System" (`c2ef9d00-…`). It has tokens (`tokens/*.css`),
React reference components with inline styles, a web and an app UI kit, and guidelines in `readme.md`.

**Decision**:
- **Tokens are copied verbatim** into `frontend/src/lib/styles/tokens.css`: colours, typography, spacing, radii,
  shadows, motion and `base.css`. They are the single source of every colour, size and radius. No hard-coded
  values.
- **Components are rebuilt as semantic Svelte**, keeping each component's look and states (hover, press, focus,
  disabled). The React files are a visual reference only.

| Design component | Svelte | Changes for the spec |
|---|---|---|
| Button (primary/secondary/dark/ghost/inverse, sm/md/lg) | `Button.svelte` (`<button>` or `<a>`) | The trailing arrow icon appears only on navigational links, not on form submits |
| Input, Select, Checkbox, Radio | `Field.svelte` plus native `<select>`/`<input>` | A real `<label for>`, and `aria-describedby` for hint and error (FR-043). A 1px `--grey-500` border reaches 3:1 non-text contrast; `--grey-300` is 1.6:1 |
| Switch | `Switch.svelte` | A native `<input type="checkbox" role="switch">`, styled, so it posts with forms |
| Badge, Tag, Card | `Badge.svelte`, `Card.svelte` | Badge text uses darker shades where needed (below) |
| Tabs | `TabNav.svelte` | `<nav>` with links and `aria-current="page"`: each tab is its own address (FR-048, FR-049), so it isn't an ARIA tab widget. A side list at ≥ 768 px. Below that it wraps, never `overflow-x: auto` (FR-043) |
| TopBar, web `SiteHeader` | `Header.svelte` | Wordmark ("InThe" + magenta "Know"), nav (Changes, Calendars, Settings, and Admin for admins), language switch for visitors, sign out. Presets are reached from each calendar's Filter tab and from the calendar list, which keeps the phone bar at 3 or 4 items |
| TabBar (app kit) | `PhoneNav.svelte` | The same nav items as a fixed bottom bar below 768 px, where students mostly are |
| Web `PageIntro` (magenta band) | `PageIntro.svelte` | Page title band, as in the web kit |
| Dialog | `Dialog.svelte` | A native `<dialog>` with `showModal()`. It gives the focus trap, Esc and the scrim for free. Used for confirmations and step-up |
| Toast | `Toast.svelte` | `role="status"` for the result of an action ("Saved") |
| ChangeNotice | `ChangeGroup.svelte` | A group of changes (R11). It isn't clickable, so it has no `div` click handler. There is no "unread" dot, because the API has no read state and FR-002 forbids keeping one |
| Icon (Lucide through unpkg masks) | `@lucide/svelte` components | Inline SVGs, with no request to unpkg |
| LessonCard, week grid, "Rooster" screens | **not built** | Out of scope: no timetable view (spec Assumptions) |

- **Where the spec overrides the design system**:
  - **Language**: English by default, Dutch when the browser prefers it (FR-040). The design says "Dutch first".
  - **Times**: absolute dates and times, never relative (R12).
  - **Coverage**: no photos, because none are provided. The sign-in page uses the solid magenta block without a
    photo.
  - **Aria labels**: the design's hard-coded Dutch aria-labels ("Sluiten", "Terug", "Ongelezen") come from the
    dictionaries.
- **Accessibility fixes (FR-043)**:
  - Measured contrast on the soft backgrounds for 12 px badge text: amber `#b86e00` on `#fdf0d9` is **3.54:1** and
    green `#1e8a4c` on `#e3f4ea` is **3.84:1**. Both are below 4.5:1.
  - Two local text tokens are added: `--amber-700: #8a5200` (5.67:1) and `--green-700: #17693a` (5.90:1). Badges
    and notice text use them. Red (4.83:1) and blue (5.39:1) pass as they are.
  - Muted text on `--surface-subtle` is 4.24:1, so muted text on grey sections uses `--grey-600` (6.44:1).
  - `base.css`'s focus ring is a translucent magenta box-shadow. It is about 1.8:1 against white, and it disappears in
    forced-colours mode. The design readme itself says "2px magenta" for focus, so `:focus-visible` becomes
    `outline: 2px solid var(--border-focus); outline-offset: 2px`. That is 4.56:1, and forced-colours mode keeps it.
    The translucent ring stays as an extra halo on inputs.
  - All hit targets are at least `--hit-min` (44 px).
- **Fonts** (user decision: self-host):
  - Lato 400/700/900 and JetBrains Mono 600 come from `@fontsource/lato` and `@fontsource/jetbrains-mono`, imported
    in the root layout. Vite bundles the `woff2` files, so no visitor request goes to Google (Principle II).
  - The design's `fonts.css` `@import` of Google Fonts is dropped.
  - Both are OFL-1.1. See the plan's Complexity Tracking for the licence note.
- **Icons**: `@lucide/svelte` (ISC), the same icon set the design system chose, without the unpkg CDN.

**Alternatives considered**:
- Porting the React components' inline styles one to one: the result has no labels, `div` click handlers and a
  scrolling tab strip, which break FR-043.
- A Svelte UI library (Skeleton, shadcn-svelte): a second design language to override.
- Committing `woff2` files by hand: Fontsource gives the same files with lockfile integrity hashes and updates.
- Keeping the Google Fonts and unpkg CDNs: every visitor's IP goes to two third parties.

## R19. Testing strategy

**Decision**: the constitution requires an automated check for non-trivial logic. The logic of this feature is in
pure server modules, so those are unit tested with vitest. Page flows are covered by the quickstart walkthrough.

| Check | Covers |
|---|---|
| `svelte-check` | Types and API shapes, **translation completeness** (SC-008), **message for every code** (SC-009) |
| `next.test.ts` | Return-address validation bypasses (FR-011) |
| `changes.test.ts` | Every kind and field gets a sentence in both languages, grouping, "event confirmed", tones (FR-026, SC-006) |
| `format.test.ts` | DST, year display, zones (FR-028) |
| `api.test.ts` | Mocked `fetch`: the forwarded headers, `401` clears the cookie and redirects, `Retry-After` is parsed, a timeout maps to unavailable, cookie attributes (FR-003 to FR-007) |
| `hooks.test.ts` | Security headers on page and `__data.json` responses, the sign-in gate, the CSRF check (`Sec-Fetch-Site`, `Origin: null`), an expired cookie on a public page, the access log line without query, cookies or addresses (FR-005, FR-008, FR-009, FR-047) |
| `no-raw-html.test.ts` | No `{@html` anywhere in `src/` (FR-010, SC-010) |
| `codes.test.ts` | Every reason documented in the contracts is in the `Reason` union (SC-009) |
| Backend xunit | The four API additions ([contracts/api.md](./contracts/api.md)), in the existing test project |

**Alternatives considered**: Playwright end-to-end tests against a running backend. They need a browser download,
a seeded database and a mail catcher in CI, and the 004 walkthroughs were manual too. Add them when the manual
walkthrough becomes a burden.

## R20. Running and deployment

**Decision**:
- **Development**: `npm run dev` serves on `http://localhost:5173`, which matches the README's `FrontendBaseUrl`.
  `API_URL` defaults to `http://localhost:5000`.
- **Production**: `node build`, with `HOST=127.0.0.1` (or the container network), `PORT`, `ORIGIN`, `API_URL`,
  `ADDRESS_HEADER` and `XFF_DEPTH` ([contracts/configuration.md](./contracts/configuration.md)).
- There is no compose service for it yet. Docker deployment stays out of scope (spec Assumptions), and nothing here
  prevents it.
