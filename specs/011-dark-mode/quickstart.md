# Quickstart: checking dark mode

Details of the cookie, the endpoint and the markup are in [contracts/theme.md](./contracts/theme.md) and
[data-model.md](./data-model.md).

## Prerequisites

- The app runs locally as in the repository's `QUICKSTART.md` (backend, database, frontend dev server).
- One account to sign in with, and an inbox for the sign-up check.
- A browser with dark emulation (DevTools › Rendering › "Emulate CSS media feature prefers-color-scheme").

## Automated checks

```bash
cd frontend && npm test && npm run check
```

Expected: all tests pass, among them the theme cookie, `POST /theme`, the equal dark token blocks and the dark
contrast pairs. `check` reports no missing dictionary key.

Browser tests (once per machine: `npx playwright install chromium`):

```bash
cd frontend && npm run test:e2e
```

Expected: all pass. They need no backend or database; they start a stub API and the built frontend themselves
(research R8). They cover scenarios 3, 4, 8, 9, 10, 12, 14, 15 and 16 below, and the "is dark" half of scenario 1.
Those scenarios stay in the table for a check by eye.

## Pages to walk through

The list of FR-002: home (signed out), sign-in (both steps), sign-up, the account pages (confirm, confirmation,
email change, forgot password, reset, invite), changes feed, history card and history page, calendars, a
calendar's overview, filter and notification pages, presets and a preset's edit page, all settings pages with
two-factor setup, notifications, the admin pages, about, and an error page (open an address that does not exist).

## Scenarios

| # | Do | Expect | Covers |
|---|---|---|---|
| 1 | Emulate dark, clear the site's cookies, open each page of the list at 390px and 1280px, in English and Dutch | Every page is dark and matches its dark screen in the design; selects, radio buttons, checkboxes and scrollbars are dark | US1, SC-001, SC-005 |
| 2 | Same state, throttle to "Slow 3G", reload a few pages with the performance recorder on | No frame with a light background | SC-004 |
| 3 | With a page open, switch the emulation between light and dark | The page follows without a reload | FR-001 |
| 4 | In dark, run the browser's accessibility audit (Lighthouse or the Issues panel) on each page of the list | No contrast failures | US2, SC-002 |
| 5 | In dark, tab through the sign-in form and one settings form | The focus ring is visible on every control | FR-004 |
| 6 | In dark, look at: a switch on and off, the phone bottom bar, a dark button under the pointer, a toast (save preferences), a hidden change, the badges, the two-factor QR code | All legible; the knob is white; the QR code is on a white tile and a phone scans it | FR-009, FR-010 |
| 7 | Emulate light, no cookie. Take screenshots of each page on `main` and on this branch with the page list of `e2e/contrast.spec.ts`, on the same machine, and compare | No difference | US3, SC-003 |
| 8 | In dark, open print preview | Dark text on white | FR-012 |
| 9 | Signed out, light device: press the toggle in the header | Dark at once, no reload; cookie `itk_theme=dark`; the toggle now shows a sun | US5, SC-010 |
| 10 | Reload; close and reopen the browser | Still dark, from the first paint | SC-007, SC-009 |
| 11 | With dark chosen, sign up, confirm the email, reach the app, open Preferences | Every page on the way is dark; "Dark" is selected | SC-011 |
| 12 | On Preferences choose "Light", then "Same as this device" | Each applies at once; after the last one the cookie is gone and the app follows the device | US4, SC-006 |
| 13 | Sign out | The signed-out pages keep the chosen theme | US4-5 |
| 14 | Open the app in two tabs (one on Preferences), change the theme in the other | The first tab changes within a second and its radio button matches | SC-012 |
| 15 | Turn JavaScript off. Press the toggle; on Preferences pick a theme and press Save | One reload each, in the new theme; it is remembered | FR-021 |
| 16 | Set the cookie by hand to `itk_theme=purple` and reload | The app follows the device; nothing breaks | FR-016 |
| 17 | Sign in to the same account in another browser | It follows that device; `GET /api/me` has no theme field | SC-008 |
| 18 | Signed out at 390px | Wordmark, toggle, language, sign in and sign up fit without sideways scrolling | edge case |
| 19 | Open sign-up in dark | The bot check widget is dark when the provider offers it | edge case |
