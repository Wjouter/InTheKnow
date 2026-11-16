# Data model: Dark mode

No database table, column or API field is added or changed. The feature stores one value, in the browser.

## Theme choice

| Value | Meaning | How it is stored |
|---|---|---|
| "Same as this device" | follow the device's light or dark setting (default) | no `itk_theme` cookie |
| "Light" | light theme, whatever the device says | `itk_theme=light` |
| "Dark" | dark theme, whatever the device says | `itk_theme=dark` |

- **Belongs to**: the browser. Not the account, not the session (FR-014, FR-020).
- **Validation**: read through an allow-list. Any other value, also an empty one, counts as "same as this device"
  (FR-016).
- **State changes**: any value to any other value, by `POST /theme` or by Save on Preferences. Nothing else writes
  or deletes it: not sign-in, sign-up, sign-out or a password reset.

## Cookie

Added to the cookie table of [005 data-model.md](../005-sveltekit-frontend/data-model.md).

| Name | Purpose | Value | Attributes | Set by | Removed | Why it may be stored |
|---|---|---|---|---|---|---|
| `itk_theme` | Theme choice | `light` or `dark` | `Secure; SameSite=Lax; Path=/; Max-Age=31536000` | `POST /theme`, Preferences Save | When "Same as this device" is chosen | FR-014: remembers the choice on this device. It is not personal data and identifies nobody (FR-017). Strictly functional, so no consent prompt. |

It is not `HttpOnly`, like `itk_lang`: it holds nothing secret. It is never sent to the backend API.

## Page data

| Field | Where | Type | Source |
|---|---|---|---|
| `locals.theme` | `App.Locals` | `'light' \| 'dark' \| null` | `pickTheme(cookies.get('itk_theme'))` in `hooks.server.ts` |
| `data.theme` | `App.PageData` (root layout) | same | `locals.theme` |
