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
| `src/lib/server/changes.test.ts` | **SC-006**: one fixture change per `kind`/`field` row of 001's detected-change table. Each has a sentence in `en` and `nl` with no raw field name, `{`, `[`, `null` or code. "event confirmed" for tentative → confirmed, and "cancelled". Grouping by `(calendarId, eventKey, detectedAt)`. Tones (FR-026, research R11). |
| `src/lib/server/format.test.ts` | DST: 26 and 27 October in Europe/Amsterdam show their own offsets. Year only when not the current year in the user's zone. The 24-hour format (FR-028). |
| `src/lib/server/api.test.ts` | Mocked `fetch`: `Authorization`, one `X-Forwarded-For` address and the browser's `User-Agent` are sent (FR-007). `401 not_signed_in` deletes `itk_session` and redirects with `next` (FR-006). `Retry-After` is parsed (FR-042). A timeout or `5xx` maps to "unavailable" with no details. Session cookies are set `HttpOnly`, `SameSite=Lax` and `Path=/` (FR-003, FR-004). |
| `src/hooks.server.test.ts` | Signed-out request to `/changes` → `303 /sign-in?next=%2Fchanges`. Public paths pass, also with an expired cookie (the `/api/me` 401 clears it and the page still renders). A `POST` with `Sec-Fetch-Site: cross-site` → `403`. A `POST` with `Sec-Fetch-Site: same-origin` and `Origin: null` passes (FR-005). `X-Frame-Options`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store` and `nosniff` are on a page and on a `__data.json` response (FR-008, FR-009, FR-013). The access log line for `/account/reset?token=SECRET` with a cookie and `X-Forwarded-For` contains none of them (**FR-047**). |
| `src/no-raw-html.test.ts` | No `{@html` in `src/` (FR-010, **SC-010**) |
| `src/lib/codes.test.ts` | **SC-009**: every reason code in the reason tables of `specs/00{1..5}*/contracts/api.md` is in the `Reason` union, apart from the listed exclusions |
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
