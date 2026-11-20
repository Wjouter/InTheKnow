# Contract: Email theme

Three parts: the API field, the HTML each theme produces, and the Preferences control. Amends
[007 contracts/email-html.md](../../007-branded-email-layout/contracts/email-html.md) and
[003 contracts/api.md](../../003-notification-channels/contracts/api.md) for `/api/me/notification-settings`.

## 1. API: `/api/me/notification-settings`

`GET` (signed in) returns one more field:

```json
{ "language": "en", "timeZone": null, "effectiveTimeZone": "Europe/Amsterdam", "emailTheme": "device" }
```

`PUT` accepts it next to `language` and `timeZone`:

| Body | Result |
|---|---|
| `{ "emailTheme": "device" \| "light" \| "dark" }` | 200, the settings as `GET` returns them |
| `emailTheme` absent | unchanged |
| `emailTheme` anything else, `null` included | 400 `invalid_email_theme`, nothing saved (also not `language` or `timeZone`) |
| none of `language`, `timeZone`, `emailTheme` | 400 `empty_body` (as today) |

No step-up: a display preference, like `language`.

## 2. Email HTML per theme

Only the HTML part differs. Text part, subject, headers (`List-Unsubscribe`, `Message-ID` shape), recipients and
part order are the same in all three themes (FR-011).

| | `light` | `device` (default) | `dark` |
|---|---|---|---|
| `<meta name="color-scheme">` / `supported-color-schemes` | `light` | `light dark` | `dark` |
| `:root` rule in the first `<style>` | `color-scheme:light;supported-color-schemes:light` | `…:light dark` | `…:dark` |
| Inline colours | light palette | light palette | dark palette |
| Second `<style>`: `@media (prefers-color-scheme:dark){…}` | absent | present | absent |
| Class hooks on coloured elements | present, unused | present | present, unused |

- Light palette: unchanged from 007. Dark palette: research R4. Button text is white in every theme.
- The dark block sets, per hook, exactly the dark palette colour of the role the light inline style has, with
  `!important`. It sets colours only: no size, spacing or layout (FR-004).
- The first `<style>` (phone sizes, `:root`, Apple data detectors) and the MSO `<style>` are otherwise unchanged.

### Hooks

One class per colour role, short to save bytes. The names are part of this contract so the tests and the block
agree.

| Hook | On | Dark (from R4) |
|---|---|---|
| `d-pg` | `<body>`, outer table | background `#141413` |
| `d-cd` | card table | background `#1c1c1a`, border `#343431` |
| `d-bd` | content cell (body text) | colour `#d4d4cf` |
| `d-ik` | wordmark "InThe", `<h1>`, `<h2>`, event name | colour `#f5f5f2` |
| `d-kn` | wordmark "Know" | colour `#ec51a7` |
| `d-ln` | text links | colour `#ec51a7` |
| `d-mt` | footer, muted paragraphs and lines, footer link, low-priority row | colour `#a6a6a0` |
| `d-rw` | change row (top line) | border `#343431` |
| `d-bx` | "and N more" box | background `#242422` |
| `d-ad` / `d-rm` / `d-fd` / `d-lp` | change labels: added, removed, field/time, low priority | colour and background per R4 |

The magenta bar and button cell have no hook: their colour is the same in both palettes. In `dark` the inline
colours already are the dark ones.

### Guarantees (checked by tests)

- Every colour in the HTML, inline or in a `<style>`, is in the light or dark palette.
- Every light and every dark text pair is at least 4.5:1; the wordmark and `<h1>` at least 3:1 as large text
  (FR-005).
- Every hook the renderer writes has a rule in the dark block, and every rule's hook is written somewhere.
- The worst-case changes email of 007 SC-007 is under 100 000 bytes in each theme (FR-012).
- The text part, subject and headers are identical across the three themes.

## 3. Preferences control

On Settings › Preferences, after "Emails use this language and time zone too." and before the home page choice:

```
<fieldset aria-describedby="email-theme-hint">
  <legend>Email theme</legend>
  <p id="email-theme-hint">For every email to your account, on every device.</p>
  (•) Same as my device   ( ) Always light   ( ) Always dark      name="emailTheme" value=device|light|dark
</fieldset>
```

- The field name is `emailTheme`, never `theme` (that is 011's per-device web theme; research R7).
- Saved with the page's Save button, in the same `PUT /api/me/notification-settings` as language and time zone.
  `invalid_email_theme` shows as the form's error, like `invalid_language`.
- Dutch texts in `nl.ts`, under `preferences`: "E-mailthema", "Voor elke e-mail aan je account, op elk apparaat.",
  "Zelfde als mijn apparaat", "Altijd licht", "Altijd donker".
