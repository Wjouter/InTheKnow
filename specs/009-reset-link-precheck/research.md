# Research: reset-link pre-check

No NEEDS CLARIFICATION remained in the Technical Context; these are the design choices.

## R1. A POST route with the token in the body

- **Decision**: `POST /api/auth/password-reset/check { "token": "…" }`.
- **Rationale**: a `GET …?token=` or `GET …/{token}` puts a live reset token into request paths, which ASP.NET and
  the frontend's API logging record (008 FR-008, `LogHygieneTests`). The body keeps it out, like `/complete`.
  POST does not imply a change here; the route is documented and tested as side-effect free.
- **Alternatives**: GET with the token in the path (logged); folding the check into the page's own `load` by reading
  the database from SvelteKit (the frontend has no database access by design).

## R2. One shared lookup, not a copy

- **Decision**: move the reset-token `SELECT` (`kind = 'reset'`, unused, unexpired, account not disabled) out of
  `CompleteResetAsync` into one helper that both the check and the submit call.
- **Rationale**: FR-002 requires exactly the same rules; two copies of the WHERE clause would drift.
- **Alternatives**: a second SQL string (drift risk); a database view (more than one query needs).

## R3. Answer shape

- **Decision**: `204` when usable, `400 { reason: "token_invalid" }` for every unusable reason, including a missing or
  empty token.
- **Rationale**: reuses the existing reason, the existing frontend text and `refuse`/`must` handling; one reason for
  every cause satisfies FR-007 and tells a link holder nothing new.
- **Alternatives**: `200 { usable: bool }` (a second way to say the same thing).

## R4. Abuse limits

- **Decision**: `AllowAnonymous().RequireRateLimiting("public")`, no bot check, like `/complete`.
- **Rationale**: the check reveals no more than `/complete` with an empty password already does, and tokens are 256-bit
  random values; the same per-source limit applies.
- **Alternatives**: a bot check (would block link scanners and add a step before the page shows; not asked for).

## R5. Frontend behaviour

- **Decision**: the page `load` calls the check only when `token` is present; `token_invalid` → the existing "link
  no longer works" panel; any other failure → `must()` → the standard error page (FR-008).
- **Rationale**: after a submit SvelteKit re-runs `load`. `load` skips the check after a submit (a POST, or the data
  request `use:enhance` makes after a successful action), so a re-check that finds
  the token just used, or fails with `429`/`503`, cannot replace the "done" panel with the error page. The
  `'reset' in form` branch stays first in `+page.svelte`.
- **Alternatives**: a client-side fetch after render (shows the form first, the problem the issue describes).
