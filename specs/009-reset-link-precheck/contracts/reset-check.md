# Contract: reset-link check

## `POST /api/auth/password-reset/check` 🚦 (FR-001 to FR-007)

Public (no session), `"public"` per-source rate limit, no bot check.

`{ "token": "…" }` → `204` when the token can still complete a reset under the same rules as
`POST /api/auth/password-reset/complete`.

| Status | `reason` | When |
|---|---|---|
| `400` | `token_invalid` | missing, empty or unknown token; not a reset token; used; expired; account disabled |
| `429` | (rate limit) | too many requests from the source |

- Side-effect free: the token is not used, changed or extended; no session ends, no email is queued, no security-log
  entry is written. Repeated calls give the same answer until the token is used or expires.
- The answer never says which rule failed and carries no account detail.
- `/complete` keeps checking the token itself; a `204` here is not a promise.

## Page `/account/reset?token=…`

| Situation | Shown |
|---|---|
| no `token` | "link no longer works" panel + `/account/forgot-password` link (no API call) |
| check `204` | the new-password form, as before |
| check `400 token_invalid` | "link no longer works" panel + `/account/forgot-password` link, no password field |
| check fails otherwise (API down, 429, 5xx) | the standard error page (`must()`) |
| form sent (any result) | `load` skips the check after a submit (POST, or the `use:enhance` data request), so the action's result decides the page |
| form sent, reset done | the "done" panel |
| form sent, `token_invalid` | "link no longer works" panel, as before |
