---

description: "Task list for 009 reset-link pre-check"
---

# Tasks: Check the password-reset link before the new password is typed

**Input**: Design documents from `/specs/009-reset-link-precheck/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/reset-check.md, quickstart.md

**Tests**: included; the plan's Constitution Check requires a backend test (usable/used/expired/unknown/disabled/empty
+ no side effects) and a frontend `load` test (no token / usable / invalid / unavailable).

**Organization**: US1 (dead link says so) and US2 (valid link still works) are both P1 and share one route and one
`load`; the route lives in Phase 2, each story owns its tests and page behaviour.

## Format: `[ID] [P?] [Story] Description`

---

## Phase 1: Setup

None: no new project, package, schema or text (plan: "no schema, text or dependency changes").

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: the side-effect-free check route both stories call.

- [X] T001 In `backend/InTheKnow/Accounts.cs`, move the reset-token `SELECT` out of `CompleteResetAsync` (lines ~217–226)
  into one private helper (e.g. `FindResetAsync(conn, token, now, ct)`) returning the row (`m.id, a.id, a.email,
  a.language, confirmed`) or null, keeping the WHERE clause verbatim: `m.token_hash = $1 AND m.kind = 'reset' AND
  m.used_at IS NULL AND m.token_expires_at > $2 AND a.disabled_at IS NULL` with `SignIn.Hash(token ?? "")`
  (research R2, FR-002). `CompleteResetAsync` calls the helper and still returns `"token_invalid"` on null, then
  keeps its password check and `UPDATE … WHERE used_at IS NULL` re-check unchanged (FR-006).
- [X] T002 In `backend/InTheKnow/Accounts.cs`, add `public async Task<bool> ResetUsableAsync(string? token,
  CancellationToken ct)`: opens a connection, calls the T001 helper with `time.GetUtcNow()`, returns `row != null`.
  Pure read: no transaction, no UPDATE, no `SecurityLog`, no queue (FR-005).
- [X] T003 In `backend/InTheKnow/AccountApi.cs`, after the `/password-reset/complete` route (line ~83), add
  `auth.MapPost("/password-reset/check", async (TokenPasswordRequest? body, HttpContext http, Accounts accounts) => await
  accounts.ResetUsableAsync(body?.Token, http.RequestAborted) ? Results.NoContent() : Refuse("token_invalid"))
  .AllowAnonymous().RequireRateLimiting("public");` (reuses `TokenPasswordRequest` from line 12; the password is
  ignored); no bot check (research R4). Answer only `204` or
  `400 token_invalid` (FR-007, contract).
- [X] T004 [P] In `backend/InTheKnow.Tests/ApiTests.cs`, add `"/api/auth/password-reset/check"` to `PublicRoutes`
  (line ~158) so the auth-required sweep stays green.

**Checkpoint**: `dotnet build backend/InTheKnow` succeeds; existing `CompleteReset` tests still pass.

---

## Phase 3: User Story 1 - A dead link says so straight away (Priority: P1) 🎯 MVP

**Goal**: an expired, used, unknown, disabled-account or missing token shows the "link no longer works" panel
before any password is typed.

**Independent Test**: request a reset, use the link, open it again → panel + `/account/forgot-password` link, no
password field.

### Tests for User Story 1

- [X] T005 [US1] In `backend/InTheKnow.Tests/SignInTests.cs`, after `ResetConfirmsAndAddsAPassword`, add a
  `// 009 US1, FR-002, FR-007` test `ResetCheckRefusesDeadLinks`: using `ResetTokenAsync`/`CompleteAsync` and a
  `CheckAsync(d, token)` helper (`SignUpTests.PostAsync(d, "/api/auth/password-reset/check", new { token })`), expect
  `400 token_invalid` for: a used token (after a `204` complete), a replaced token (request two resets, check the
  first), an expired token (advance the fake clock past 1 h,
  as `CompleteReset` does for `late`), `"nonsense"`, `""`, a missing body field, a non-reset token (e.g. a confirmation
  token if the fixtures expose one), and a token whose account has `disabled_at` set.
- [X] T006 [P] [US1] Create `frontend/src/routes/account/reset/reset.test.ts` in the style of
  `frontend/src/routes/sign-in/code/code.test.ts` (`vi.mock('$lib/server/api', …)`, `fakeEvent`), covering `load`:
  no `token` → `{ usable: false }` and `api` not called; a POST request, and a data request (`isDataRequest: true`), with a token → `{ usable: true }`
  and `api` not called; `api` returns `400 { reason: 'token_invalid' }` →
  `{ usable: false }`. Mock `must` with its real behaviour (`vi.importActual`) or keep it unmocked.

### Implementation for User Story 1

- [X] T007 [US1] In `frontend/src/routes/account/reset/+page.server.ts`, make `load` async: no `token` →
  `{ usable: false }` without an API call; after a submit → `{ usable: true }` without an API call, where "after a
  submit" is `event.request.method !== 'GET' || event.isDataRequest` (no-JS POST, or the data request `use:enhance`'s
  `update()` makes after a successful action; a failed action does not re-run `load`, and reset links are always a
  full page load). The action already checked the token and its result picks the page ("done", the `token_invalid`
  panel, or the form with a password error); a re-check here would find the token just used, or fail with
  `429`/`503` and hide "done". Otherwise `api(event, 'POST', '/api/auth/password-reset/check', { token })`;
  `r.ok` → `{ usable: true }`; `r.status === 400 && reason === 'token_invalid'` → `{ usable: false }`; anything
  else → `must(event, r)` (from `$lib/server/api`, line ~80) so the standard error page shows (FR-008, research R5). Leave
  `actions.default` unchanged.
- [X] T008 [US1] In `frontend/src/routes/account/reset/+page.svelte` line ~25, replace `!data.hasToken` with
  `!data.usable`; keep the `{#if form && 'reset' in form}` branch first so the success panel still shows after
  `load` re-runs on a used token (research R5).

**Checkpoint**: T005, T006 pass; quickstart manual steps 3–4 show the panel.

---

## Phase 4: User Story 2 - A valid link still works after the check (Priority: P1)

**Goal**: opening the page (any number of times, by a person or a link scanner) never uses up the link.

**Independent Test**: request a reset, check/open it several times, then set a new password and sign in with it.

### Tests for User Story 2

- [X] T009 [US2] In `backend/InTheKnow.Tests/SignInTests.cs`, add `// 009 US2, FR-005, SC-002, SC-004` test
  `ResetCheckChangesNothing`: get a fresh token, snapshot `account_message` (`used_at`, `token_expires_at` of the
  row), `count(*)` of `session` and `security_event` (the table `CompleteReset` queries) for the account, and the queued-mail count; call `CheckAsync`
  three times expecting `204` each; assert every snapshot value is unchanged; then `CompleteAsync` with a new
  password expects `204` and sign-in with it succeeds.
- [X] T010 [P] [US2] In `frontend/src/routes/account/reset/reset.test.ts`, add `load` cases: `api` ok (`204`) →
  `{ usable: true }` and the body sent is `{ token }` to `/api/auth/password-reset/check`; `api` returns `503` (or
  `429`) → `load` throws (the `must()` error), not `{ usable: … }` (FR-008).

### Implementation for User Story 2

No new code: T002/T003 are read-only and T001 keeps the submit re-check (FR-006). If T009 fails, fix it in
`backend/InTheKnow/Accounts.cs` (T002), not in the test.

**Checkpoint**: quickstart manual steps 1–2 and 5 pass.

---

## Phase 5: Polish & Cross-Cutting Concerns

- [X] T011 [P] In `specs/004-accounts-login/contracts/api.md`, add `POST /api/auth/password-reset/check` to the public
  routes with `204` / `400 token_invalid`, side-effect free (link to `specs/009-reset-link-precheck/contracts/reset-check.md`).
- [X] T012 [P] In `specs/005-sveltekit-frontend/contracts/pages.md`, add the check route to the API → page table row for
  `/account/reset` (load).
- [X] T013 Run the quickstart automated commands: `dotnet test backend/InTheKnow.Tests --filter
  "FullyQualifiedName~SignInTests|FullyQualifiedName~ApiTests|FullyQualifiedName~LogHygieneTests"` and
  `cd frontend && npm test && npm run check`; all green.
- [ ] T014 Walk `specs/009-reset-link-precheck/quickstart.md` manual steps 1–5 on the dev stack with Mailpit.

---

## Dependencies & Execution Order

- **Phase 2**: T001 → T002 → T003 (same two backend files, in order); T004 any time.
- **US1 (Phase 3)**: after Phase 2. T006 ‖ T005; T007 → T008.
- **US2 (Phase 4)**: after Phase 2; T010 extends the file from T006 (create it in T010 if US2 is done alone).
- **Polish**: T011 ‖ T012 any time; T013–T014 after both stories.

### Within Each User Story

- Tests first; T005/T009 fail until Phase 2 lands, T006/T010 until T007.
- T005 and T009 touch the same file (`SignInTests.cs`): run in order.

### Parallel Opportunities

- T004 (`ApiTests.cs`) ‖ T001–T003.
- Backend tests (T005, T009) ‖ frontend tests (T006, T010).
- T011 ‖ T012.

---

## Parallel Example: User Story 1

```bash
Task: "Add ResetCheckRefusesDeadLinks in backend/InTheKnow.Tests/SignInTests.cs"
Task: "Create frontend/src/routes/account/reset/reset.test.ts with the no-token and token_invalid load cases"
```

---

## Implementation Strategy

### MVP First

US1 and US2 are both P1 and ship together: US1 without US2's guarantee would be unsafe (a check that used up links
would break every reset opened by a mail scanner). Order: Phase 2 → US1 → US2 → Polish.

### Incremental Delivery

1. Phase 2 → route exists, submit unchanged.
2. US1 → dead links show the panel up front.
3. US2 → proven side-effect free.
4. Polish → contracts agree, quickstart passes, commit.

---

## Notes

- No schema, text, dependency or new reason added (plan, data-model.md).
- Token only in the POST body, never a URL (research R1, `LogHygieneTests`).
- Other emailed links (invite, confirmation, email change) are out of scope (spec Edge Cases).
