# Research: Dark mode

All decisions are for `frontend/`. The spec left no open questions; these are the technical choices.

## R1. Where the dark colours live

**Decision**: the design's `.dark{…}` block from its shared stylesheet goes into `tokens.css` twice, with the same
declarations:

```css
@media screen and (prefers-color-scheme: dark) { :root:not([data-theme='light']) { /* dark set */ } }
@media screen { :root[data-theme='dark'] { /* dark set */ } }
```

The block also sets `color-scheme: dark`. The plain `:root` gets `color-scheme: light`.

**Rationale**:

- The design's own note says the declarations go on `:root` in the app. The set turns the neutral scale around
  (`--white` becomes the raised surface, `--grey-900` the strongest text), so components that use `--white` or
  `--grey-*` follow without a change.
- The first rule follows the device with no script (FR-001, FR-021). The second is the chosen theme (FR-011).
  `:not([data-theme='light'])` lets a chosen "Light" win on a dark device.
- `@media screen` keeps print light (FR-012). Forced-colours mode overrides both by itself.
- `color-scheme` makes selects, checkboxes, radio buttons, autofill and scrollbars dark (FR-005), and tells the
  browser the page is already dark so it is not recoloured a second time (edge case).
- The light `:root` block is not edited, so light cannot shift (FR-007).

**The cost** is that the dark set is written twice. A unit test fails when the two copies differ (R8).

**Alternatives considered**:

- *`light-dark()` with `color-scheme`*: one definition per token and no second block. It needs Safari 17.5 /
  Chrome 123. An older browser would lose every colour, also in light, unless the light values are repeated in a
  fallback, which is the same duplication. Rejected for now.
- *A separate `dark.css` on a `<link>` whose `media` the server sets*: no duplication, but the file has to live
  outside the bundle and must load after the token file. More moving parts than a copied block.
- *A Sass or PostCSS mixin*: a new build dependency for 25 lines.

## R2. Where the choice is remembered

**Decision**: a cookie `itk_theme` with the value `light` or `dark`. No cookie means "same as this device". It has
the same attributes as `itk_lang`: `Secure; SameSite=Lax; Path=/; Max-Age=31536000`, not `HttpOnly`. Only the
server writes it.

**Rationale**:

- The server sees a cookie, so it can render `<html data-theme="dark">` in the first byte. That gives the right
  theme on first paint (FR-006, SC-009) with no inline script. The CSP (`script-src 'self'`) stays untouched.
- It works without script (FR-021), exactly as the language switch does.
- Sign-out clears only `itk_session` and the pending cookie, and sign-in and sign-up never touch `itk_theme`, so
  the choice survives all three (FR-014, FR-019, FR-020).
- Every response already has `Cache-Control: no-store`, so a cached page can never show an old theme.
- The server writes the cookie, also when script is running. Safari caps cookies written by script at seven days;
  a server-set cookie keeps its full year (SC-007).

**Alternatives considered**:

- *`localStorage`*: the server cannot read it, so it needs an inline script in `<head>` before first paint, a CSP
  hash or nonce for it, and it does nothing without script. Its one advantage, the `storage` event for other tabs,
  is covered by `BroadcastChannel` (R5).
- *A field on the account*: the spec rules it out (FR-014, FR-020).

## R3. Getting the theme into the page

**Decision**: `hooks.server.ts` reads the cookie through `pickTheme()` (allow-list: `light`, `dark`, else `null`),
stores it in `locals.theme`, and replaces two placeholders in `app.html` in the existing `transformPageChunk`:

- `data-theme="%theme%"` on `<html>`: `light`, `dark` or empty.
- `<meta name="color-scheme" content="%scheme%">`: `light`, `dark`, or `light dark` when nothing is chosen.

The root layout load passes `theme` on as page data.

**Rationale**: same mechanism as `%lang%`. The meta tag colours the empty canvas before the stylesheet has
arrived, so a slow connection shows no white frame (SC-004). An unknown cookie value becomes `null`, which is
"same as this device" (FR-016), and can never be written into the HTML.

## R4. Changing the theme

**Decision**: one endpoint, `POST /theme` (`routes/theme/+server.ts`), with form fields `theme` (`light`, `dark` or
`device`) and `next`. `light` and `dark` set the cookie, `device` deletes it, anything else changes nothing. It
answers `303` to `safeNext(next)`. `/theme` is added to `isPublic`.

The client function `setTheme(choice)` in `lib/theme.ts`:

1. sets or removes `data-theme` on `<html>`, so the change shows at once;
2. posts the same form to `/theme` with `fetch(…, { redirect: 'manual' })`;
3. posts the choice on the `BroadcastChannel`;
4. calls `invalidateAll()`, so `data.theme` is fresh.

**Rationale**:

- Without script the header form posts and the browser follows the redirect: one reload in the new theme
  (FR-021). With script the form's `onsubmit` calls `setTheme()` instead.
- An endpoint, not a form action as `/language` has, because the Preferences radio buttons sit inside the main
  preferences form (as in the design) and cannot be a second form. They call `setTheme()` on `change`. A plain
  `fetch` to an endpoint is simpler than calling a form action by hand.
- The existing CSRF check covers it: it is a POST, and `fetch` from the page is `same-origin`.

**Alternative considered**: writing `document.cookie` in the browser and skipping the request. Rejected because
of Safari's seven-day cap (R2).

## R5. Other tabs

**Decision**: `new BroadcastChannel('itk_theme')`. `setTheme()` posts after the server has answered. The root
layout listens and calls `invalidateAll()`. A root layout `$effect` keeps `data-theme` on `<html>` equal to
`data.theme`.

**Rationale**: the message arrives after the cookie is set, so the other tab's reload of its data reads the new
theme. That one path updates both the page colours and the selected radio button on an open Preferences page
(FR-022). A browser without `BroadcastChannel` skips this and takes the theme on its next page load, which is
what the spec allows for a tab without script.

**Alternatives considered**: the `storage` event needs `localStorage` (R2). The Cookie Store `change` event is not
in every browser yet.

## R6. The two controls

**Decision**:

- **Visitor toggle** (`Header.svelte`, signed out only, before the language switch): one form to `/theme` with two
  icon buttons, `<button name="theme" value="dark">` with a moon and `<button name="theme" value="light">` with a
  sun. CSS shows the moon when the page is light and the sun when it is dark, with the same two selectors as R1.
  Each is 44×44px and has its own `aria-label` ("Switch to dark theme" / "Switch to light theme").
- **Preferences** (`settings/preferences/+page.svelte`): a `<fieldset>` after "Home page" with legend "Theme", the
  hint "Saved on this device only." and three radio buttons named `theme` with the values `device`, `light`,
  `dark`, using the existing `.choice` class. With script, `change` calls `setTheme()`. Without script, the Save
  button submits them with the rest, and the `save` action stores the theme with the same server helper as
  `/theme`.

**Rationale**:

- The server does not know whether the device is dark when no theme is chosen. Two buttons with CSS picking one
  makes "switch away from what is shown" (FR-018) correct without script. The hidden button is `display: none`,
  so keyboard and screen reader meet only the one that applies.
- The radio buttons match the design and need no new component.

## R7. Spots where the design's dark twin is not right (FR-008 to FR-010)

Measured with the WCAG formula against the dark set.

| Spot | Problem with the plain twin | Fix | Result |
|---|---|---|---|
| Switch knob | `--white` turns dark; on the magenta track it is 3.75:1 and reads as a hole | knob uses `--text-inverse` (white in both themes) | 3.71:1 on the off track, 4.56:1 on the on track |
| Phone bar, active item | `--brand` text on the page is 4.05:1 | new alias `--brand-on-page`: `--brand` in light, `--magenta-300` in dark | 5.53:1 |
| Dark button, hover | `--grey-700` turns light; white text on it is 1.49:1 | new alias `--surface-dark-hover`: `--grey-700` in light, `#474743` in dark | 9.33:1 |
| Toast | text is `--white` (turns dark); icon `--green-100` turns into a deep tint, 1.26:1 | text uses `--text-inverse`; icon uses the fixed light green `#e3f4ea` | 11.41:1 and 10.0:1 |
| Link hover | `--brand-press` on the dark page is 2.52:1 | new alias `--text-link-hover`: `--brand-press` in light, `--magenta-200` in dark (as the design's `.dark a:hover`) | 10.11:1 |
| QR code | its tile uses `--white`, which turns dark | tile background is the fixed `#ffffff`, with padding as quiet zone | scannable |

Each new alias has today's value in light, so light does not change (FR-007).

To check while building, not yet decided: `History.svelte` uses `--grey-400` on `--grey-100` in two places
(2.61:1 in dark). If those marks carry meaning they need `--grey-500` in dark; if they are decoration they stay.
The inverse button on the title band (dark surface with `--magenta-300` text, 5.12:1) is left as the design has
it.

## R8. Checks

**Decision**: two layers, both in CI.

**Vitest** (`lib/theme.test.ts` and next to the changed server files):

- `pickTheme()` returns `null` for anything but `light` and `dark`.
- `POST /theme`: sets, deletes, ignores unknown values, and redirects only to a safe `next`.
- The hook fills `%theme%` and `%scheme%` from the cookie.
- The two dark blocks in `tokens.css` have the same declarations.
- A table of dark token pairs (text on surface, text on tint, border on surface, the R7 pairs) is resolved from
  `tokens.css` and each must reach 4.5:1 or 3:1.

**Playwright with axe** (`frontend/e2e/`, `npm run test:e2e`, Chromium only), chosen by the maintainer on
2026-11-16:

- `playwright.config.ts` starts two servers: the stub API on its own port, and the built frontend
  (`npm run build && npm run preview`) with `API_URL` pointing at the stub.
- `api-stub.ts` is a `node:http` server that answers the API's GET routes from `fixtures.ts`: an admin account,
  two calendars, a feed with one change of each kind and a hidden one, a history, presets, notification settings.
  Any `itk_session` cookie counts as signed in. POST routes answer with a plain success.
- `contrast.spec.ts` loops over the pages of FR-002 × phone (390px) and desktop (1280px) × English and Dutch, with
  `colorScheme: 'dark'`, and runs axe with only the `color-contrast` rule. Zero violations (SC-001, SC-002). The
  history card, a dialog and a toast are opened first where a page has them.
- `theme.spec.ts`:
  - device dark, no cookie: the page background is the dark page colour; switching the emulation to light changes
    it without a reload (FR-001);
  - cookie `dark` on a light device, and the reverse: the response HTML has the attribute and the background is
    right (FR-006, FR-011);
  - toggle press: dark with no navigation, cookie set, still dark after a reload and in a new page (US5);
  - Preferences: each radio button applies at once; "Same as this device" removes the cookie (US4);
  - two pages in one browser context: a change in one shows in the other within a second, and its radio button
    matches (FR-022);
  - a context with JavaScript off: the toggle and Preferences Save each give the new theme after one load
    (FR-021);
  - an unknown cookie value follows the device (FR-016);
  - print media in dark: the background is white (FR-012).

**Rationale**:

- axe measures text contrast on the rendered page, so it finds a component that puts a dark token on a dark
  surface, which the token table cannot. It does not measure borders, control outlines or the focus ring; the
  token-pair test covers those (FR-004), so both stay.
- Only the `color-contrast` rule runs. Other rules would report on things this feature does not touch.
- A stub API keeps the run to one `npm` command and a few seconds, with the same data every time. The fixtures use
  the frontend's own types, so `npm run check` fails when a type and a fixture drift apart. It does not prove the
  real API still matches those types; the backend tests and the frontend's `api.test.ts` own that.
- Chromium only: contrast is the same in every engine, and one browser keeps the CI job short.

**Not automated**:

- SC-003 (light is unchanged). Screenshots of every page are taken once from `main` and once from this branch
  with the same Playwright page list, and compared on the same machine. The images are not committed: stored
  screenshots differ between machines by font rendering and would fail in CI for no reason.
- SC-005 (matches the design), SC-011 (the sign-up walk, which needs real email confirmation), the QR scan, and
  the bot check widget. These are in [quickstart.md](./quickstart.md).

**Alternatives considered**:

- *The real backend in CI*: see Complexity Tracking in [plan.md](./plan.md).
- *Vitest browser mode*: tests components, not pages, and would still need Playwright underneath.
- *Lighthouse CI*: a second tool with its own server and config, and it cannot open a dialog or press a toggle.

## R9. Bot check widget

**Decision**: `BotCheck.svelte` passes `theme: getComputedStyle(document.documentElement).colorScheme` in the
options of `render()`.

**Rationale**: the computed `color-scheme` is `light` or `dark`, exactly what the page shows, whether it comes from
the device or from the choice. Both widgets the app supports take a `theme` option with those two values. The
widget keeps the theme it was rendered with if the theme changes afterwards, which the spec accepts.

## R10. Out of scope, on purpose

- `error.html`, SvelteKit's built-in page for a crash in the root layout: it stays as it is. The app's own error
  pages (`+error.svelte`) are dark through the tokens.
- Emails (spec 012).
- A way back to "same as this device" on the visitor toggle (spec edge case).
