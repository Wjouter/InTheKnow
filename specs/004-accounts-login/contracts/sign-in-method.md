# Contract: Adding a sign-in method type

This is what a later feature (passkeys, SURFconext, Google, GitHub) writes, and what it must not change (FR-026,
FR-027). There is no interface to implement (research R7). A method type consists of:
- **rows in `sign_in_method`** with its `type` name (`^[a-z][a-z0-9-]{0,30}$`), and in `key` the provider's stable
  subject. Its own data goes in its own table, keyed by `sign_in_method.id`;
- **its own routes**: sign in, add it to the signed-in account, and re-authenticate. These routes verify the
  credential in the type's own way, then call the shared code below.

## Shared code a type calls

| Call | Does | The type must not |
|---|---|---|
| `SignIn.CompleteAsync(accountId, methodId, http)` | Refuses `not_confirmed` and `account_disabled`. Throttling success resets. Updates `last_used_at` and writes a `signIn` event. Creates the session: awaiting a code if 2FA is on (research R6), otherwise active. Returns the sign-in response of [api.md](./api.md). | create sessions or write events itself |
| `SignIn.FailAsync(throttleKey, http)` | Counts a failure (research R8) and writes a `signInFailed` event | keep its own counters |
| `SignIn.CheckAsync(throttleKey, http)` | Returns the wait, if the key or source must wait | |
| `SignIn.ReauthenticatedAsync(sessionId, methodId)` | Sets the step-up and `last_used_at`, and writes a `reauthenticated` event | |
| `Accounts.AddMethodAsync(accountId, type, key, secret)` | Needs a step-up. Inserts the row, queues `methodAdded` and writes `methodAdded`. `409 method_in_use` when `(type, key)` belongs to another account. | link identities to accounts by matching addresses without its own decision (spec Assumptions) |
| `DELETE /api/me/methods/{id}` | Generic. It already works for any type, and the last-method trigger applies. | |

## Invariants a type gets for free

- The account's email address is an attribute. A type that supplies no address must ask for one at sign-up,
  confirm it through FR-003, and never identify the account by it (FR-027).
- Removing its row ends its sessions (FK cascade), and the last method cannot be removed (trigger).
- 2FA, admin rules, deletion, ownership of data and sessions behave exactly as for `password`. Whether a type
  already counts as two factors is that feature's decision (spec Assumptions): it would pass `secondFactor: true` to
  `CompleteAsync`.

## The test-only type (`test`)

`InTheKnow.Tests/TestSignIn.cs` registers these routes only in the test host:
- `POST /api/test-auth/sign-in {key}`;
- `POST /api/test-auth/add {key}`, which needs a session and a step-up;
- `POST /api/test-auth/reauthenticate {key}`.

The account, session and ownership suites are theories over `password` and `test` (SC-006, SC-014).
