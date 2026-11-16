# Contract: Theme

Extends [005 contracts/pages.md](../../005-sveltekit-frontend/contracts/pages.md). Frontend only; the backend API
does not change.

## `POST /theme`

Public (no sign-in). Under the app's CSRF check like every POST.

| Form field | Values | Effect |
|---|---|---|
| `theme` | `light`, `dark` | sets `itk_theme` to that value ([data-model.md](../data-model.md)) |
| | `device` | deletes `itk_theme` |
| | anything else, or missing | no change |
| `next` | a path on this app | where to go afterwards; anything else becomes `/` (`safeNext`) |

Response: always `303` with `Location` set to the safe `next`. `GET /theme` redirects to `/`.

## HTML document

| Place | Value |
|---|---|
| `<html data-theme="…">` | `light`, `dark`, or empty when nothing is chosen |
| `<meta name="color-scheme" content="…">` | `light`, `dark`, or `light dark` when nothing is chosen |

Both are written by the server from the cookie. In the browser, `data-theme` follows `data.theme` after a change.

## Which theme shows

| `data-theme` | Device asks for dark | Theme |
|---|---|---|
| `dark` | either | dark |
| `light` | either | light |
| empty | yes | dark |
| empty | no, or unknown | light |

Print is always light. Forced-colours mode keeps the system colours.

## Tokens

The dark set redeclares the colour, shadow, focus-ring and scrim tokens of `tokens.css` with the values of the
design's `.dark` block (spec FR-003). Token names do not change. Three aliases are new:

| Alias | Light | Dark | Used by |
|---|---|---|---|
| `--text-link-hover` | `var(--brand-press)` | `var(--magenta-200)` | `a:hover` |
| `--brand-on-page` | `var(--brand)` | `var(--magenta-300)` | phone bar, active item |
| `--surface-dark-hover` | `var(--grey-700)` | `#474743` | dark button, hover |
| `--text-brand-on-section` | `var(--text-brand)` | `var(--magenta-200)` | history, "opened" label on a removed lesson (`--grey-100`); found by T029 |

Rule for components (FR-008): text or an icon on a coloured fill uses `--text-inverse`, never `--white`. `--white`
is a surface.

## Visitor toggle (signed-out header)

```html
<form method="POST" action="/theme">
  <input type="hidden" name="next" value="{current path}" />
  <button name="theme" value="dark" class="theme-light-only" aria-label="{t.theme.toDark}">moon icon</button>
  <button name="theme" value="light" class="theme-dark-only" aria-label="{t.theme.toLight}">sun icon</button>
</form>
```

- Placed before the language switch. Each button is 44×44px.
- Exactly one button is displayed: the moon in light, the sun in dark.
- With script, submit is intercepted: the theme changes at once, with no navigation.
- Not shown when signed in.

## Preferences (Settings › Preferences)

A fieldset after "Home page", inside the preferences form:

- legend: `t.theme.label` ("Theme"); hint: `t.theme.hint` ("Saved on this device only."), linked with
  `aria-describedby`;
- radio buttons `name="theme"`: `device` ("Same as this device"), `light` ("Light"), `dark` ("Dark"); the one
  matching `data.theme` is checked, `device` when it is `null`.

With script, choosing an option applies and stores it at once. Without script, Save stores it with the other
preferences and the page comes back in that theme.

## Other tabs

`BroadcastChannel` named `itk_theme`. One message per change, sent after the server has stored the choice. A tab
that receives it reloads its page data; its `data-theme` and its Preferences radio buttons follow.

## Texts (both dictionaries)

`theme.label`, `theme.hint`, `theme.device`, `theme.light`, `theme.dark`, `theme.toDark`, `theme.toLight`.
