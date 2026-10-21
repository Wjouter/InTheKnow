# Quickstart: validating the reset-link pre-check

## Automated

```bash
dotnet test backend/InTheKnow.Tests --filter "FullyQualifiedName~SignInTests|FullyQualifiedName~ApiTests"
```

```bash
cd frontend && npm test && npm run check
```

Expected: all pass. The backend tests cover a usable token answering `204` repeatedly and still completing a reset,
`token_invalid` for used, replaced, expired, unknown, disabled-account and empty tokens, and unchanged `account_message`,
`session` and `security_event` rows after checks. The frontend `reset.test.ts` covers the `load` outcomes from
[contracts/reset-check.md](./contracts/reset-check.md).

## Manual (dev stack + Mailpit)

1. Open `/account/forgot-password`, request a reset for a test account, and open the link from Mailpit.
   Expect the new-password form.
2. Reload the page a few times, then set a new password. Expect the "done" panel, and signing in with the new
   password works (US2-1).
3. Open the same link again. Expect "This link no longer works." and the new-link link, with no password field (US1-2).
4. Open `/account/reset` with no token, and with `?token=nonsense`. Expect the same panel (US1-3, US1-5).
5. With a fresh link's page open, stop the backend and reload. Expect the standard error page, not the form (FR-008).
