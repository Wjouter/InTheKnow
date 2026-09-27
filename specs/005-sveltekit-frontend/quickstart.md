# Quickstart: Web Frontend

The validation guide for this feature. The backend's setup, database and test commands are the same as in the
[004 quickstart](../004-accounts-login/quickstart.md).

- Contracts: [api.md](./contracts/api.md) (the four API additions), [pages.md](./contracts/pages.md) (addresses,
  actions and the SC-002 coverage table), [configuration.md](./contracts/configuration.md)
- Model: [data-model.md](./data-model.md). Decisions: [research.md](./research.md)

## Prerequisites

- Node 24 or later, and npm.
- The 004 prerequisites: the .NET 10 SDK, and the compose PostgreSQL with `INTHEKNOW_TEST_DB` set.
- For the walkthrough: `docker compose up -d db mailpit`, and the API started with the README's local email
  settings. That includes `InTheKnow__Notifications__FrontendBaseUrl=http://localhost:5173`, so email links open the
  dev frontend.

```bash
cd frontend
npm ci
```

## 1. Automated checks

All must pass. The constitution makes them the gate.

```bash
cd frontend
npm run check
```

```bash
cd frontend
npm test
```

```bash
cd frontend
npm run build
```

```bash
dotnet test
```

| Check | Covers |
|---|---|
| `npm run check` (`svelte-check`) | Types against the API shapes. **SC-008**: `nl` misses no key of `en` and adds none. **SC-009**: every `Reason`, `CheckFailure` and pattern code has a message in both languages (research R10). |
| `src/lib/next.test.ts` | Return addresses: `/calendars/1` kept; `//evil.example`, `/\evil.example`, `https://evil.example`, `javascript:…` and encoded variants → `/` (FR-011) |
| `src/lib/server/changes.test.ts` | **SC-006**: one fixture change per `kind`/`field` row of 001's detected-change table. Each has a sentence in `en` and `nl` with no raw field name, `{`, `[`, `null` or code. "event confirmed" for tentative → confirmed, and "cancelled". Grouping by `(calendarId, eventKey, detectedAt)`. Tones (FR-026, research R11). `calendarView` states, with no link or URL in its output (FR-023). |
| `src/lib/server/format.test.ts` | DST: 26 and 27 October in Europe/Amsterdam show their own offsets. Year only when not the current year in the user's zone. The 24-hour format (FR-028). |
| `src/lib/server/api.test.ts` | Mocked `fetch`: `Authorization`, one `X-Forwarded-For` address and the browser's `User-Agent` are sent (FR-007). `401 not_signed_in` deletes `itk_session` and redirects with `next` (FR-006). `Retry-After` is parsed (FR-042). A timeout or `5xx` maps to "unavailable" with no details. Session cookies are set `HttpOnly`, `Secure`, `SameSite=Lax` and `Path=/` (FR-003, FR-004). The failure log has no query string (FR-047). |
| `src/hooks.server.test.ts` | Signed-out request to `/changes` → `303 /sign-in?next=%2Fchanges`. Public paths pass, also with an expired cookie (the `/api/me` 401 clears it and the page still renders). A `POST` with `Sec-Fetch-Site: cross-site` → `403`. A `POST` with `Sec-Fetch-Site: same-origin` and `Origin: null` passes (FR-005). `X-Frame-Options`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store` and `nosniff` are on a page and on a `__data.json` response (FR-008, FR-009, FR-013). The access log line for `/account/reset?token=SECRET` with a cookie and `X-Forwarded-For` contains none of them (**FR-047**). |
| `src/lib/server/actions.test.ts` | `refuse()`: the wait from `Retry-After`, `stepUp` on `reauthentication_required`, `rule` only when it is a number, no raw codes. `formValues()` never returns a password, code or token (FR-041, FR-042) |
| `src/lib/enhance.test.ts` | `isStepUp()` is true only for a failure with `stepUp` (FR-020) |
| `src/routes/sign-in/sign-in.test.ts`, `sign-in/code/code.test.ts` | The awaiting-code token goes to `itk_pending`, never `itk_session`, and is promoted only after the code step. A `401` on the code step clears it. `next` stays on the app (FR-011, US6-3) |
| `src/routes/account/sign-up/sign-up.test.ts` | `?invite=` moves the token into `itk_invite` and redirects without calling the API. The cookie is sent with sign-up and deleted after it (SC-004, SC-005) |
| `src/no-raw-html.test.ts` | No `{@html` in `src/` (FR-010, **SC-010**) |
| `src/lib/codes.test.ts` | **SC-009**: every reason code in the reason tables of `specs/00{1..5}*/contracts/api.md` is in the `Reason` union, apart from the listed exclusions. The `http_*` and `smtp_*` pattern codes map to their messages |
| Backend `ApiTests`, `AccountTests`, `FilterRulesTests`, `PresetsTests` (extended) | The four API additions ([api.md](./contracts/api.md)): newest-first paging, the home page setting, the rule index, followers. The existing tests still pass unchanged, which shows the additions are additive. |

## 2. Manual walkthrough

Start the frontend against the running API:

```bash
cd frontend
npm run dev
```

Open `http://localhost:5173` and use Mailpit at `http://localhost:8025`. Each step names what it proves.

1. **Sign-up and sign-in** (US1, SC-001, SC-005). Start a timer.
   1. Sign up with a new address. Then sign up again with that address.
   2. Save both result pages (browser "Save page") and `diff` them. Only the nonce may differ (**SC-005**). Do the
      same for `/account/confirmation` and `/account/forgot-password`.
   3. Open the confirmation link. The page asks for the password, and the account stays unconfirmed until you press
      the button (**SC-004**).
   4. Confirm, then sign in.
   5. Sign in with a wrong password: one generic message.
   6. With a bot check configured (the Turnstile test keys of 004), fail a sign-in once and retry: the widget
      resets, and the second try isn't refused as `bot_check_failed`. Navigate from `/sign-in` to
      `/account/forgot-password` without a reload: the widget appears.
2. **First calendar** (US2, SC-001).
   1. Paste a MyTimetables link. The button shows "Checking…" and can't be pressed twice.
   2. The calendar appears as checked OK. Stop the timer: under 5 minutes, not counting the email wait (**SC-001**).
   3. Paste `http://…`, a non-calendar URL and an empty feed. Each gives its own plain message, and nothing is added.
3. **Changes** (US2, FR-026 to FR-028).
   1. Let the checker record changes, using a test feed you edit.
   2. The feed is newest first, grouped per event, in the account's time zone, with "event confirmed" for a
      tentative → confirmed change.
   3. "Older changes" pages back.
   4. Hidden changes appear only with the switch, and are marked.
   5. Rename the calendar label to `<b>x</b>`. It shows literally (**SC-010**).
4. **Session hygiene** (FR-003 to FR-009, SC-003).
   1. In devtools:
      - `document.cookie` shows no `itk_session`;
      - Application → Storage holds no token;
      - Network responses contain no token;
      - the page source contains no token (**SC-003**).
   2. Sign out, then press Back: no data is shown.
   3. Embed the app in an `<iframe>` on another origin: it is refused.
   4. From another origin, post a form to `/calendars?/addLink`: `403`. Then, with JavaScript off, submit
      `/account/forgot-password`: it works despite `no-referrer` (research R4).
5. **Email links** (US4-1, US4-2, SC-004).
   1. Sign out, and open a change email's calendar settings link. After sign-in you land on that calendar's
      Notifications tab.
   2. Turn notifications off. The next change sends no email.
   3. While signed in as another account, open the link: "not in this account", and nothing changes.
6. **Filters and presets** (US3).
   1. On the Filter tab, add a hide rule for a group picked from the list.
   2. Save a rule with an empty value. The error shows under that rule, and the saved filter is unchanged
      (FR-032).
   3. Save as a preset, and apply it to a second calendar.
   4. Edit the preset. The confirmation says "2 calendars follow this preset".
7. **Notifications tab** (US4). Add a second address, then confirm it from Mailpit. Send a test (the sixth test in
   a day is refused with a message), pause it and remove it. Only the valid actions appear per status.
8. **Account management** (US5, US6).
   1. Reset your password from the email. Every device is signed out.
   2. Change your address. The step-up dialog appears (confirm), then the change continues without retyping.
   3. Turn on 2FA: scan the QR code, save the recovery codes (leaving the page warns first).
   4. Sign out, then sign in with a code. Then sign in once with a recovery code: the count left drops.
   5. Sign out another device. Delete the account.
9. **Invites and admin** (US7, US8).
   1. `make-admin` → accept the invite on `/account/invite`. The admin pages say they are locked until 2FA, then
      unlock after setup.
   2. Create a shareable invite. The link is shown once. Open it in a private window: the address bar drops the
      token, and the page says an invite is used.
   3. Search accounts, promote one (refused without 2FA), and list failing calendars. The email status shows.
10. **Language** (US9). With a Dutch-preferring browser, the sign-in page is in Dutch. The switch changes it. An
    account set to English shows English after sign-in.
11. **Phone and keyboard** (FR-043, **SC-007**).
    1. At 360 px width (devtools device mode), visit every address in [pages.md](./contracts/pages.md). None scrolls
       horizontally, and the settings and calendar tabs wrap.
    2. Repeat steps 1, 2, 6 and 8 with the keyboard only: every control is reachable, the focus is visible, and
       dialogs trap focus and close with Esc.
12. **Coverage** (**SC-002**): tick each row of the [coverage table](./contracts/pages.md#coverage-every-api-action-and-the-page-that-offers-it-sc-002)
    as it is exercised in steps 1 to 9.
13. **Speed** (SC-011): with the API on the same host, the Network panel shows each page's document within 2 s on
    "Fast 4G" throttling.
14. **Logs** (FR-047): the frontend terminal shows `time METHOD path status ms` lines, with no query strings,
    addresses, tokens or emails.

## Walkthrough results (2026-09-27)

Run by Claude against `npm run dev`, the API on a separate database (`intheknow_005`) and Mailpit, in the desktop
app's built-in browser. What was exercised, and what wasn't:

| Step | Result |
|---|---|
| 1. Sign-up and sign-in | Done: a password-rule refusal (rule marked, email kept), "sign-up is by invitation only", sign-up through a shareable invite ("check your email"), a wrong password (one generic message), sign-in with password + code and with a recovery code. **Not done**: the two-address page diff (SC-005), opening a confirmation link, the bot check (no provider configured). |
| 2. First calendar | Partly: the `http://` refusal shows its message. **Not done**: adding a real feed. The walkthrough calendars were seeded in the database, so no outside feed was fetched on purpose. The API's background checker still fetched the seeded placeholder links once, before their next check was pushed back. |
| 3. Changes | Done on seeded changes: newest first, grouped per event, "Event confirmed" + "Low priority", the hidden change only with the switch, a year only on the 2027 date, and the time zone applied after a change on Preferences. The label `<b>x</b>` shows literally (SC-010). **Not done**: "Older changes" paging (fewer than 50 changes). |
| 4. Session hygiene | Done: `document.cookie` is empty, no storage holds a token, and neither the page nor a `__data.json` response mentions a bearer token or `itk_session`. Back after sign-out lands on sign-in. A cross-site `POST /calendars?/addLink` gets `403`, and a no-script `POST /account/forgot-password` with `Origin: null` works. Frame denial is checked on the response headers (`X-Frame-Options: DENY`, `frame-ancestors 'none'`), not with a real iframe. |
| 5. Email links | Done: signed out, `/calendars/3/notifications` leads to sign-in and back to that tab after the code step. A calendar of another account shows "not in the account". **Not done**: the next change sending no email. |
| 6. Filters and presets | Partly: a rule without conditions is refused under that rule and the edits stay. A group rule is saved, and the calendar then says it has its own rules. **Not done**: picking from the list (the seed has no current events), save as preset, apply to a second calendar, the followers confirmation. |
| 7. Notifications tab | Done: add a second address (waiting for confirmation, only resend and remove), pause, resume, send a test, and remove through the dialog. **Not done**: confirming from Mailpit, and the sixth-test refusal. |
| 8. Account management | Partly: an email change asks for step-up (password + code) and then continues without retyping. 2FA turned on with the QR code, the key and a wrong code first; the recovery codes show once, and a recovery-code sign-in drops the count to 9. **Not done**: password reset, signing out another device, deleting the account. |
| 9. Invites and admin | Partly: `make-admin` → "Accept your invite" → "admin pages unlock after two-step verification". A shareable invite link is shown once, and opening it drops the token from the address and shows the invite notice. Accounts, calendars, email status and presets (Default without Delete) load. **Not done**: the locked page before 2FA, promoting an account. |
| 10. Language | Done with direct requests: `Accept-Language: nl` gives `lang="nl"` and "Inloggen"; the switch sets `itk_lang=en` and returns to the page. **Not done**: an account set to English after sign-in. |
| 11. Phone and keyboard | Done at 360 × 740: every signed-in, admin and `/account/…` page has `scrollWidth` 360 (no horizontal scroll), and the tabs wrap. Keyboard: a spot check only (visible magenta focus outline, a dialog opened from the keyboard with focus inside, Esc closes it and focus returns). **Not done**: the full keyboard-only pass of steps 1, 2, 6 and 8. |
| 12. Coverage | Marked in the [coverage table](./contracts/pages.md#coverage-every-api-action-and-the-page-that-offers-it-sc-002). |
| 13. Speed | **Not measured** with throttling. The access log shows server times of 1 to 170 ms per page on loopback. |
| 14. Logs | Done: lines are `time METHOD path status ms`, with no query strings, addresses, tokens or emails. The API client logs refusals as `api POST /api/auth/sign-in 401 invalid_credentials`. |

Fixes made during the walkthrough:
- A global `.notice` style leaked into the change groups. The component class is now `change`.
- The dashed "hidden" border also applied to list items. It is now scoped to the group.
- A group took its icon from its first normal item. It now takes the icon of the change that sets its tone.
- The settings tabs marked nothing on `/settings/security/two-factor`. The longest matching tab is now current.
- The browser pane asks for `/favicon.ico` on every page. The gate sent it to sign-in, which rendered the sign-in page each time. `handle` now answers it with a plain `404`.
- After a session ended, client-side navigation reached sign-in, but the header kept the signed-in nav. The root layout load skipped reading `url` (a short-circuited `&&`), so it never reran. It now always reads the path.
- The gate's `next` kept form action keys (`?/addLink`). It now uses the same `returnPath()` as the API client.

## Walkthrough results, second run (2026-09-27 to 2026-09-28)

The steps the first run left open, on the same setup. The API ran with `CollectionWindow` at 1 minute so digests
arrive quickly. Real changes came from the checker: one event was removed from a stored snapshot (with the snapshot
hash cleared) before a check, so the next check recorded it as added and wrote the delivery rows itself.

| Step | Result |
|---|---|
| 1. Sign-up and sign-in | Done. **SC-005**: sign-up with a new address, the same address again and an existing account's address give pages that differ only in the nonce; so do confirmation and forgot-password for an existing and an unknown address. **SC-004**: opening the confirmation link asks for the password and the account stays unconfirmed (`confirmed_at` checked) until the button. Bot check with Cloudflare's always-pass test keys: after a wrong password the widget was reset (one `turnstile.reset` call), and the retry reached the code step, not `bot_check_failed`. Going from `/sign-in` to `/account/forgot-password` without a reload shows the widget. |
| 2. First calendar | Done with a public Google holiday feed (the user chose not to use a private MyTimetables link). "Checking…" shows with the button disabled, a second Enter adds nothing, and the calendar appears as checked OK within seconds. **SC-001**: from the sign-up post (20:08:13) to the calendar added (20:11:19) took about 3 minutes, including the email wait and the SC-005 diffs. `https://example.org/` gives "This link doesn't give a calendar InTheKnow can read." **Not done**: the empty-feed refusal (needs a hosted empty calendar). |
| 3. Changes | Done: with 66 changes, the first page has 50 and "Older changes" gives the other 16 with no further link, with no overlap or gap. |
| 5. Email links | Done: with notifications on for one calendar and off for another, a change on each gave one digest email (the first) and no delivery row or email for the second. |
| 6. Filters and presets | Done: a group picked from the current events' list (the list held the three seeded groups; the pane doesn't draw the browser's own suggestion popup, so the value was typed and added with Enter), save as preset, apply to a second calendar, and the edit confirmation "2 calendars follow this preset". Also create, copy and delete on `/presets`. |
| 7. Notifications tab | Done: resend confirmation, confirm from Mailpit (then Pause, Send a test and Remove appear), and the sixth test in a day is refused. |
| 8. Account management | Done: password reset from the email signs out the other device, sign out another device, change password (other devices signed out), sign out everywhere, remove the only sign-in method (refused, after step-up), 2FA on with the leave warning (stubbed `confirm` returned "stay" and the page kept the codes), new recovery codes, 2FA off (step-up with password and code), email change through `/account/email`, delete the account. |
| 9. Invites and admin | Done: `make-admin` → accept → admin pages locked → unlocked in the same session after 2FA setup. Search, make admin refused for an unconfirmed account and for one without 2FA, then promote, demote, disable, enable, remove 2FA and delete. Named invite create, send again and revoke. Global preset create, edit and delete. `?status=failing` lists only the failing calendar. `/calendars/[id]` remove. |
| 10. Language | Done: signed out with `Accept-Language: nl` the page is Dutch; after signing in, an account set to English gets `lang="en"`. |
| 11. Keyboard | Done for steps 1, 2, 6 and 8 with real Tab, Shift+Tab, Enter, arrow and Esc keys. Flows done by keyboard: confirm, sign out, sign in, add a calendar, the Filter tab, save as and apply a preset, the preset edit dialog, and on Sign-in & security: remove method, sign out a device, 2FA off. A Tab sweep (every visible control in `main` reached) also covered `/account/sign-up`, `/account/forgot-password`, `/account/reset`, `/account/email`, `/sign-in/code`, `/settings/account`, `/presets` and the two-factor page in its "already on" state. **Not swept**: the two-factor setup form (Start, code). Every control was reachable with the magenta focus ring, dialogs kept focus inside (Tab cycles), and Esc closed them with focus back on the opener. After a save on the Filter tab and Apply preset, focus dropped to `<body>` (fixed below). |
| 12. Coverage | Every row of the [coverage table](./contracts/pages.md#coverage-every-api-action-and-the-page-that-offers-it-sc-002) is now exercised. |
| 13. Speed | **Estimated, not throttled** (neither browser here can throttle): dev-server documents are 25 to 101 KB, uncompressed, and take 9 to 66 ms on the server. At 1.6 Mbit/s with a 150 ms round trip, the largest (`/`) works out to about 1 s, under the 2 s target. |
| 14. Logs | Done: no query strings, tokens or addresses after reset, confirm, email-change and invite links. |

Fixes made during this run:
- The tab title kept the previous page's title after client-side navigation to `/` or to an error page. Svelte 5 sets
  `document.title` from `<svelte:head>`, and the root layout's title ran only once. The home and error pages now set it.
- A success notice stayed on screen for good when a refusal followed within 4 seconds (the sixth test email). The
  toast's timer was cleared with the previous effect run while the new run returned early. A result without a
  message now hides the old one.
- Admin role and disable refusals said "Your account isn't confirmed" and "Admin pages need two-step verification"
  about someone else's account. A `409` on `/admin/accounts` now says "This account isn't confirmed yet." and "This
  account needs two-step verification before it can be an admin."
- "0 calendars follow this preset." now reads "No calendar follows this preset.", like the delete dialog, and the
  admin calendar list says "1 account" instead of "1 accounts".

Fixed after the run (each checked again in the browser):
- Focus dropped to `<body>` after Save rules, Apply preset and every other enhanced form. The button is disabled while
  the request runs, and a disabled button loses focus. The shared `submitter()` now gives focus back when the result
  stays on the page.
- In the rule editor, removing a rule dropped focus, and moving one to the top or bottom left focus on a disabled
  button. Focus now goes to the next rule's remove button, or to the other move button.
- Enter in a preset's name field did nothing. The save button is now a submit button, and any submit asks for the
  followers confirmation first. Without JavaScript the form now saves at all.
- The unavailable view kept the previous page's title. Its "Try again" also did nothing: a client-side visit to the
  same address reruns no load. It now has its own title, and "Try again" is a full page load.
- Removing a calendar, deleting a preset and deleting the account now show a notice ("Calendar removed.") on the page
  they land on. The notice goes with the navigation state and is cleared from history, so Back doesn't repeat it.
  Without JavaScript these still redirect with no notice.
