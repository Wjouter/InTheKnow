# Contract: Pages and actions

This contract lists the addresses the web interface serves, what each one reads and which form actions it offers.
The [coverage table](#coverage-every-api-action-and-the-page-that-offers-it-sc-002) is the checklist for SC-002.

**Rules for every page**:
- `GET` only reads. Every change is a `POST` form action, named `?/<action>` below (FR-005, SC-004).
- The headers of research R16 are on every response, data responses included: `no-store`, `no-referrer`, frame
  denial, and the CSP.
- Pages behind sign-in redirect to `/sign-in?next=…` without a session. **Public** pages don't (research R6).
- Every action returns `fail(status, { reason, values, rule? })` on a refusal. The page shows the message for the
  reason next to its field (FR-041), keeps the other values filled in, and never echoes passwords or codes.
- Every destructive action is behind a confirmation `Dialog` (US2-10, US5-8, US8-4).

## Addresses

### Visitors and account links (public)

| Address | Shows | Actions |
|---|---|---|
| `/` | **Visitor**: a short explanation, with sign-in and sign-up. **Signed in**: the home page, as the changes feed or the combined view per `homePage`, or the empty state (explanation + add-calendar form) with no calendars (FR-046) | `?/addLink` (empty state) |
| `/sign-in` | Email, password, bot check. Messages: `invalid_credentials` (generic), `not_confirmed` (with a link to `/account/confirmation`), `account_disabled`, `too_many_attempts` + wait, `bot_check_failed` | `?/default` → sets `itk_session` and `303 next`, or sets `itk_pending` and `303 /sign-in/code?next=…` |
| `/sign-in/code` | Authenticator or recovery code. Without `itk_pending` (expired), `303 /sign-in` with "the code step timed out" | `?/default` → moves the token to `itk_session`, `303 next` |
| `/sign-out` | (no page) | `?/default` → `POST /api/auth/sign-out`, clears the cookies, `303 /` |
| `/language` | (no page) | `?/default` → sets `itk_lang`, `303` back (visitors, US9-1) |
| `/account/sign-up` | Email, password (rules shown up front, FR-017), language (defaults to the page language), bot check. With `itk_invite`: "signing up with an invite". Result: the same "check your email" page for every address (SC-005) | `?/default` |
| `/account/sign-up?invite=…` | (no page) Sets `itk_invite`, `303 /account/sign-up` (research R15) | none |
| `/account/confirmation` | Email + bot check: "send a new confirmation email". The same result for every address | `?/default` |
| `/account/confirm?token=…` | Password field + "Confirm my account" button. Opening it changes nothing | `?/default` → `POST /api/auth/confirm`. `token_invalid` offers `/account/confirmation` |
| `/account/forgot-password` | Email + bot check. The same result for every address | `?/default` |
| `/account/reset?token=…` | New password + button | `?/default` → complete. Result: "every device was signed out; sign in". `token_invalid` offers `/account/forgot-password` |
| `/account/email?token=…` | "Confirm the new address" button | `?/default`. `address_taken` and `token_invalid` offer a new change on `/settings/account` |
| `/account/invite?token=…` | Password, a language choice whose first option "the invite's language" sends no `language` (the API then uses the invite's, 004), and a button | `?/default` → sets `itk_session`. An admin invite ends with "admin pages unlock after two-step verification" and a link to `/settings/security/two-factor` (US7-2). `invite_invalid` → "this invite no longer works" |

### Student pages (sign-in required)

| Address | Shows | Actions |
|---|---|---|
| `/changes` | The changes feed, newest first, 50 per page. `?before=<id>` shows older pages, and `?showHidden=1` shows hidden changes (US2-5 to US2-9) | none |
| `/calendars` | The calendar list (name, status, last/next check, failures), each linking to its page. The add-link form ("Checking…" while pending, US2-1). A link to `/presets` | `?/addLink` |
| `/calendars/[id]` | **Overview** tab: name, status, last/next check, failures | `?/remove` (confirm) → `303 /calendars` |
| `/calendars/[id]/filter` | **Filter** tab: the source (preset name or "customised"), the rules as sentences, inactive rules with the reason, the rule editor, preset picker, "save as preset" | `?/saveRules`, `?/applyPreset`, `?/saveAsPreset` |
| `/calendars/[id]/notifications` | **Notifications** tab: the notify switch and label, plus a note linking to `/notifications` for where emails go (US4-11) | `?/save` |
| `/presets` | Personal presets (with `followers`), then global presets (apply to a calendar or copy only, US3-7) | `?/create`, `?/copy` |
| `/presets/[id]` | A personal preset: rename, edit the rules (same editor), delete. The confirmation states `followers` and what happens to them (US3-6) | `?/save`, `?/delete` |
| `/settings` | `303 /settings/account` | none |
| `/settings/account` | **Account** tab: address, pending email change, change the address, delete the account (the confirmation lists what is deleted; `last_admin` is explained) | `?/changeEmail` ⬆, `?/deleteAccount` ⬆, `?/reauthenticate` |
| `/settings/security` | **Sign-in & security** tab: password (change, or add when `hasPassword` is false), 2FA status, recovery codes left + generate new, turn 2FA off, sign-in methods with last use (remove), devices with last use and the current one marked (sign out one, sign out everywhere) | `?/setPassword` ⬆, `?/newRecoveryCodes`, `?/disable2fa` ⬆, `?/removeMethod` ⬆, `?/endSession`, `?/signOutEverywhere`, `?/reauthenticate` |
| `/settings/security/two-factor` | Setup: QR code (from `uri`) + the secret as text, a code field. After enabling, the 10 recovery codes shown once, with copy, download and a warning before leaving (US6-2, edge case "Recovery codes not saved") | `?/start`, `?/enable` |
| `/settings/preferences` | **Preferences** tab: language, time zone (or "Default (zone)"), home page | `?/save` |
| `/notifications` | **Notifications** tab: channels with status and allowed actions, the add form (or "not available right now"), a note that the language and time zone on Preferences apply to emails, and a note linking to `/calendars` for the per-calendar settings (US4-10, US4-11) | `?/add`, `?/pause`, `?/resume`, `?/test`, `?/resend`, `?/remove` |

⬆ = the API may answer `reauthentication_required`. The page then opens the step-up dialog and resubmits (research
R8).

The four settings tabs share one `(settings)` layout with `TabNav`. The three calendar tabs share
`/calendars/[id]/+layout`. A `404` from the API on any `/calendars/[id]/…` page shows "this calendar is not in the
account `<email>`" and changes nothing (edge case).

### Admin pages (role `admin` and a second factor)

`/admin/*` shares one layout with `TabNav`. Non-admins get "not allowed" (US8-2), and an admin session without a
second factor gets the "locked" page with a way forward (US8-1). Both come from the API's `403` reason (research R7).

| Address | Shows | Actions |
|---|---|---|
| `/admin` | `303 /admin/accounts` | none |
| `/admin/accounts` | Search by part of the address (`?q=`), paged with `?after=`. Role, confirmed, disabled, 2FA per row | `?/setRole`, `?/disable`, `?/enable`, `?/delete`, `?/remove2fa` (each destructive one behind a confirmation) |
| `/admin/sign-up` | Sign-up settings (open, allowed domains). Pending invites with uses left and expiry. The create form (named or shareable). A new shareable link is shown once, with copy and a warning before leaving | `?/saveSettings`, `?/createInvite`, `?/resend`, `?/revoke` |
| `/admin/presets` | Global presets. Default is marked and can't be deleted | `?/create` |
| `/admin/presets/[id]` | Edit the name and rules with the same `RuleEditor` | `?/save`, `?/delete` (not for Default) |
| `/admin/calendars` | `?status=failing` and `?overdue=true` filters, paged. Status, last error (as a message), subscriber and link counts. No links or addresses | none |
| `/admin/email` | Per channel type: configured, last attempt and result, consecutive failures, failing since, waiting | none |

## Coverage: every API action and the page that offers it (SC-002)

| API route (001 to 004 + additions) | Page / action | Walkthrough 2026-09-27 |
|---|---|---|
| `GET /api/auth/bot-check` | `load` of `/sign-in`, `/account/sign-up`, `/account/confirmation`, `/account/forgot-password` | ✓ |
| `POST /api/auth/sign-up` | `/account/sign-up` | ✓ |
| `POST /api/auth/confirmation` | `/account/confirmation` (also linked from `not_confirmed` on `/sign-in`) | — |
| `POST /api/auth/confirm` | `/account/confirm` | — |
| `POST /api/auth/sign-in` | `/sign-in` | ✓ |
| `POST /api/auth/sign-in/code` | `/sign-in/code` | ✓ (code and recovery code) |
| `POST /api/auth/password-reset` | `/account/forgot-password` | ✓ |
| `POST /api/auth/password-reset/complete` | `/account/reset` | — |
| `POST /api/auth/email-change/confirm` | `/account/email` | — |
| `POST /api/auth/invite/accept` | `/account/invite` | ✓ |
| `POST /api/auth/sign-out` | `/sign-out` (header and phone nav) | ✓ |
| `POST /api/auth/sign-out-everywhere` | `/settings/security` `?/signOutEverywhere` | — |
| `POST /api/me/reauthenticate` | Step-up dialog, `?/reauthenticate` on `/settings/account` and `/settings/security` | ✓ |
| `GET /api/me` | Every signed-in request (`hooks.server.ts`) | ✓ |
| `DELETE /api/me` | `/settings/account` `?/deleteAccount` | — |
| `PUT /api/me/password` | `/settings/security` `?/setPassword` | — |
| `POST /api/me/email` | `/settings/account` `?/changeEmail` | ✓ (after step-up) |
| `GET /api/me/sessions`, `DELETE …/{id}` | `/settings/security` (list, `?/endSession`) | partial (list) |
| `GET /api/me/methods`, `DELETE …/{id}` | `/settings/security` (list, `?/removeMethod`) | partial (list) |
| `POST /api/me/2fa/setup`, `…/enable` | `/settings/security/two-factor` | ✓ |
| `DELETE /api/me/2fa` | `/settings/security` `?/disable2fa` | — |
| `POST /api/me/2fa/recovery-codes` | `/settings/security` `?/newRecoveryCodes` | — |
| `POST /api/me/links` | `/calendars` `?/addLink`, `/` empty state | partial (refusal only) |
| `GET /api/me/calendars` | `/calendars`, `/calendars/[id]/*`, `/changes`, `/` | ✓ |
| `DELETE /api/me/calendars/{id}` | `/calendars/[id]` `?/remove` | — |
| `GET /api/me/changes` (+ `order=newest`, `before`) | `/changes`, `/` | ✓ (newest, hidden) |
| `GET`/`PUT /api/me/calendars/{id}/filter` | `/calendars/[id]/filter` (`?/saveRules`) | ✓ |
| `PUT /api/me/calendars/{id}/filter/preset` | `/calendars/[id]/filter` `?/applyPreset` | — |
| `GET /api/me/presets` | `/presets`, `/calendars/[id]/filter` (picker) | ✓ |
| `POST /api/me/presets` | `/presets` `?/create`, `?/copy`; `/calendars/[id]/filter` `?/saveAsPreset` | — |
| `PUT`/`DELETE /api/me/presets/{id}` | `/presets/[id]` | — |
| `GET`/`PUT /api/me/notification-settings` | `/settings/preferences`; `effectiveTimeZone` read on every signed-in request | ✓ |
| `GET`/`PUT /api/me/calendars/{id}/notifications` | `/calendars/[id]/notifications` | ✓ |
| `GET /api/me/channel-types` | `/notifications` (add form availability) | ✓ |
| `GET`/`POST /api/me/channels`, `DELETE …/{id}` | `/notifications` | ✓ |
| `POST /api/me/channels/{id}/pause`, `…/resume`, `…/confirmation`, `…/test` | `/notifications` | partial (not confirmation) |
| `PUT /api/me/settings` (addition) | `/settings/preferences` `?/save` (home page) | ✓ |
| `GET /api/admin/accounts`, role, disable, enable, delete, `DELETE …/2fa` | `/admin/accounts` | partial (list) |
| `GET`/`PUT /api/admin/signup-settings` | `/admin/sign-up` | partial (read) |
| `POST`/`GET /api/admin/invites`, `DELETE …/{id}`, `POST …/{id}/resend` | `/admin/sign-up` | partial (create, list) |
| `GET`/`POST /api/admin/presets`, `PUT`/`DELETE …/{id}` | `/admin/presets`, `/admin/presets/[id]` | partial (list, open Default) |
| `GET /api/admin/calendars` | `/admin/calendars` | ✓ |
| `GET /api/admin/channel-types` | `/admin/email` | ✓ |
| `/n/confirm`, `/n/decline`, `/n/unsubscribe` | **Out of scope**: served by the backend (spec Assumptions) | n/a |

## Email link addresses (FR-012)

The five `/account/…` addresses, `/notifications` and `/calendars/{id}/notifications` above are fixed by 003's and
004's emails. Renaming any of them breaks links that were already sent.
