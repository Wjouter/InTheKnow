# Implementation Plan: Check the password-reset link before the new password is typed

**Branch**: `009-reset-link-precheck` | **Date**: 2026-10-21 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/009-reset-link-precheck/spec.md`

## Summary

The reset page's `load` asks a new, side-effect-free `POST /api/auth/password-reset/check { token }` whether the
link is still usable, and shows the existing "this link no longer works" panel instead of the form when it is not.
The backend answers with the same lookup `CompleteResetAsync` already uses (moved into one shared helper, so both
apply exactly the same rules, FR-002): `204` usable, `400 token_invalid` otherwise. Sending the form still checks
again (FR-006). No schema, text or dependency changes.

## Technical Context

**Language/Version**: C# on .NET 10 (backend); TypeScript, Svelte 5 / SvelteKit 2 (frontend)

**Primary Dependencies**: existing only (ASP.NET Core minimal APIs, Npgsql; SvelteKit); no new package

**Storage**: PostgreSQL, read-only for this feature: `account_message` joined to `account` (no schema change)

**Testing**: xUnit v3 (`backend/InTheKnow.Tests`), vitest (`frontend`)

**Target Platform**: Linux server (Docker stack from 006); any browser

**Project Type**: web application (backend API + SvelteKit frontend)

**Performance Goals**: one indexed lookup per page open (`token_hash` is already looked up this way on submit), SC-003

**Constraints**: the check changes nothing (FR-005, link scanners); token travels in the POST body, never in a URL
the API logs; the answer is only usable / not usable (FR-007)

**Scale/Scope**: 1 new API route, 1 page load, 0 new texts

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle / rule | Status |
|---|---|
| I. Simplicity: smallest implementation, no speculative code | PASS: one route reusing the existing lookup; page reuses the existing `token_invalid` panel and texts; other emailed links stay out of scope |
| I. New dependency or layer justified | PASS: none added |
| II. No new personal data | PASS: nothing stored; the answer carries no account detail (FR-007) |
| II. No secrets or deployment details in repo | PASS: tests and quickstart use `localhost`/Mailpit and test accounts |
| II. Trust boundary validated server-side | PASS: the API decides; the frontend only renders its answer; submit re-checks (FR-006) |
| II. State change only on deliberate action | PASS: the check is a pure SELECT; tests assert no row in `account_message`, `security_event`, `session` or the mail queue changes (SC-004) |
| II. Output encoded by default | PASS: no new output; existing Svelte-escaped texts |
| II. Auth via established libraries | PASS: reuses `SignIn.Hash` and the existing token rows; no new crypto |
| Tech: GPL-compatible, lockfiles | PASS: no new package |
| Workflow: non-trivial logic has an automated check | PASS: backend test for usable/used/expired/unknown/disabled/empty + no side effects; frontend `load` test for no token / usable / invalid / unavailable |

Post-design re-check: PASS (no dependency, layer, stored data or text added; the lookup is shared, not copied).

## Project Structure

### Documentation (this feature)

```text
specs/009-reset-link-precheck/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/reset-check.md
└── tasks.md             # /speckit-tasks
```

### Source Code (repository root)

```text
backend/InTheKnow/
├── Accounts.cs          # reset-token lookup moved into one helper; new ResetUsableAsync(token, ct); CompleteResetAsync uses the helper
└── AccountApi.cs        # + POST /api/auth/password-reset/check (AllowAnonymous, "public" rate limit)

backend/InTheKnow.Tests/
├── ApiTests.cs          # PublicRoutes += "/api/auth/password-reset/check"
└── SignInTests.cs       # US3 section: check answers, repeated checks keep the link usable, no side effects

frontend/src/routes/account/reset/
├── +page.server.ts      # load: no token → usable false (no call); after a submit (POST or data request) → true (no call, the action decides); ok → true; token_invalid → false; else must() → error page
├── +page.svelte         # `!data.hasToken` → `!data.usable`; the `'reset' in form` branch stays first
└── reset.test.ts        # new: the four load outcomes

specs/004-accounts-login/contracts/api.md   # Public routes: the new route
specs/005-sveltekit-frontend/contracts/pages.md  # API → page table: the new route on /account/reset
```

**Structure Decision**: the existing web-application layout (`backend/`, `frontend/`); no new projects or folders
beyond the one test file.

## Complexity Tracking

No violations.
