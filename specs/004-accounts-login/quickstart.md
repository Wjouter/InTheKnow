# Quickstart: Accounts and Login

This is the validation guide. Setup, database and test commands are the same as in the
[001 quickstart](../001-ical-change-detection/quickstart.md).

- Contracts: [api.md](./contracts/api.md), [configuration.md](./contracts/configuration.md),
  [account-emails.md](./contracts/account-emails.md), [sign-in-method.md](./contracts/sign-in-method.md),
  [bot-check.md](./contracts/bot-check.md)
- Model: [data-model.md](./data-model.md)

## Prerequisites

- The compose PostgreSQL from 001 is running, and `INTHEKNOW_TEST_DB` is set.
- Migration `004_accounts.sql` applies at startup. **It deletes all subscriber data from 001 to 003** (research
  R1), so do not point it at a database you want to keep.
- The automated tests need no mail server, no network and no bot check provider: `FakeSmtp`, `FakeHttp` and the
  test-only bot check provider stand in for them. `FakeTimeProvider` drives every expiry and wait.
- The test host sets `PasswordHasherOptions.IterationCount` low, except in the SC-011 measurement.
- For the manual walkthrough only: `docker compose up -d mailpit`.

## 1. Automated checks

```bash
dotnet build && dotnet test
```

Each test below fails if the logic it covers breaks.

| Test file | Covers |
|---|---|
| `SignUpTests` (in-memory app, database) | Sign-up → confirm email → confirm with link **and** password. A `GET` of the link does nothing, and a wrong password confirms nothing (US1-1, US1-2). Unconfirmed sign-in → `not_confirmed` after a correct password only (US1-3). A confirmed address gets a `202` identical in status, headers and body to a new one, plus an owner notice (US1-7). Unconfirmed address → password replaced, old link dead. Resend cap 3/24 h and notice cap 3/24 h send nothing more yet answer the same. Password rules and each reason (FR-007). Case-insensitive uniqueness. Unconfirmed accounts deleted at 30 days (FR-005). **SC-012**: the full matrix of open × domains (none, matching, non-matching) × invite (none, named, shareable), plus a shareable invite with N uses accepting exactly N, including concurrent use. |
| `SignInTests` (theory over `password` and `test`) | Sign-in, sign-out, sign-out everywhere, two devices at once. Idle expiry on the fake clock. Awaiting-code session refused outside its two routes, and expires at 10 min. A password change (and a rehash on sign-in) keeps this session working and ends the others (SC-009, FR-013). A reset ends all. Disabled → `account_disabled` only after a correct password. **Enumeration** (SC-003): unknown address, wrong password and invalid password give byte-identical bodies. The unknown-address path calls the hasher, asserted through a counting `IPasswordHasher` wrapper. |
| `ThrottleTests` (unit, fake clock) | 5 free, then 30 s … 15 min per (address, source). Another source unaffected (US clarification). 20 per hour across sources → 15 min for all, then one attempt per wait. **The worst case of 23 in a rolling hour** asserted (SC-005, research R17), and a continuous wait never over 15 min (SC-005). Unknown and known addresses throttle identically. IPv6 /64 grouping. Per-source limit. Reset clears. Entries forgotten after 1 h. |
| `TwoFactorTests` (database, fake clock) | Setup is off until a code is given (US4-1). 10 recovery codes (US4-2). Password alone never signs in, also right after a reset (SC-004, US3-4). ±1 step accepted, ±2 refused. The same code twice refused, also concurrently (US4-4). A recovery code works once and notifies with the count (US4-5). Regenerating kills the old codes (US4-7). Turning off needs step-up with a code, and ends the other sessions (US4-6). The otpauth URI shape. Codes checked against Otp.NET's RFC 6238 test vectors. |
| `AccountTests` (theory over `password` and `test`) | Step-up required and expiring at 10 min (R6). Email change: link to the new address, notice to the old, the old address kept until confirmed, 24 h expiry, the address channel moves, and a taken address looks successful (US5-2, US5-3, FR-022). Session list without secrets, ending another session, `use_sign_out` for this one, another account's session is 404 (US5-6, US5-7). Methods: list, add, remove, `last_method`, and removal ending that method's sessions, also concurrently (US6, **SC-014**). An account without a password can do everything except change password (US6-5). **Deletion** (US5-5): afterwards no row in any table references the account and no text column contains its address, except the unsent `deleted` notice until it is sent; only its security events remain, orphaned calendars are gone and shared ones stay (**SC-008**, edge case "Deleting an account that shares calendars"). |
| `OwnershipTests` (theory over `password` and `test`, **SC-002**, SC-006) | It enumerates every `/api/me/…` endpoint from `EndpointDataSource`, and fails if one is not covered. As B, it calls each with A's calendar, preset, channel and session ids. Every call gets the same status and body as for a non-existent id, and A's data is unchanged. B's lists stay empty. No session, an expired one and an awaiting-code one → 401 on all of them (US2-3). A new account's address channel is active, and its language and time zone come from sign-up and the default (US2-4, US2-5). |
| `AdminTests` (in-memory app, database) | It enumerates every `/api/admin/…` endpoint (**SC-013**). A regular user → `admin_required`. An admin without a second factor → `two_factor_required`. An admin who enables 2FA in this session passes, while their other password-only sessions do not (US7-11). The old operator key opens nothing (US7-8). Promotion needs 2FA (US7-9). Demote and `last_admin`, also two admins demoting each other concurrently (trigger). `demote_first` on self-2FA-off, through both `/api/me/2fa` and `/api/admin/accounts/{own id}/2fa`, and on disabling or deleting an admin or oneself (US7-10, US7-15). Account list and search without secrets (US7-12). Disable → sessions gone, sign-in refused, no account emails except admin ones, **no notifications**: the fan-out skips the account and a pending delivery is skipped `accountDisabled`. Re-enable (US7-13). Admin delete (US7-14). Admin removal of 2FA (US7-4). Sign-up settings and invites, every reason (US8). |
| `MakeAdminTests` (database) | `make-admin` on an empty database queues an admin invite. Accepting it gives a locked admin until 2FA (US7-5). An existing confirmed account is promoted. It refuses an outdated schema. It runs while another connection holds the instance lock. |
| `BotCheckTests` (unit + in-memory app, **SC-015**) | The outcome mapping of [bot-check.md](./contracts/bot-check.md) against `FakeHttp`: pass, replay, invalid, 500, timeout. Every protected route refuses a missing, invalid or replayed result **before** the address is looked at (asserted: no database query for the address, and identical responses for known and unknown addresses). Unavailable → sign-in proceeds, the others give `503`. No unprotected route asks for a result. Off → nothing asked. `GET /api/auth/bot-check` never contains the secret. |
| `AccountEmailTests` (`FakeSmtp`) | Every kind of [account-emails.md](./contracts/account-emails.md) renders in `en` and `nl` with no missing `Texts` key (**SC-010**), has no `List-Unsubscribe`, and encodes inserted values. Tokens are minted at send: before sending the row has no hash, and after sending the link works once. A newer link kills the older one. Retry after an SMTP outage, then expiry (edge case "Mail server down"). Account emails take the rate slot before digests. Security notices are sent while every channel is paused. |
| `LogHygieneTests` (in-memory app, captured logs and responses, **SC-007**) | It runs every flow above and asserts that no log line contains a password, code, TOTP key, recovery code, session token, confirmation or reset link, full address or bot check result. It also asserts that no response contains them outside the setup and enable responses. |
| `SignInLoadTest` (database, production iteration count, **SC-011**) | 500 sign-ins over 60 s against the in-memory app on a real database. It asserts p100 < 2 s and logs the p50 and p95. |
| `SettingsTests` (extended) | Every new configuration rule. `ApiKey` no longer required, with a warning if set. |
| `DbTests` (extended) | The constraints of the new tables. `sign_in_method_keep_one` and `account_keep_admin`. The owner columns cascade. `NO ACTION` on `subscription_filter.preset_id` lets an account with personal presets in use be deleted, and still blocks deleting a used preset by itself. |
| `ApiTests`, `NotificationApiTests`, `PresetsTests`, `ChannelsTests`, `DeliveryTests`, `CheckerTests`, `PublicPagesTests` (existing) | Rewritten from subscriber ids to signed-in accounts (`TestHost.SignInAsync`), with unchanged assertions. `/n/` pages still work without a session (FR-030). |

## 2. Manual walkthrough (Mailpit)

Run the app with 003's local email settings and `TrustedProxies` empty, against a throwaway database. Emails appear
at `http://localhost:8025`. `curl` stands in for the frontend's server.

1. Start the server once so it migrates the database. Then, with the server still running,
   `dotnet run -- make-admin admin@example.nl`. The output says `invite queued`, and Mailpit shows the invite. `POST /api/auth/invite/accept` with its token and a password returns a token.
   `GET /api/admin/calendars` with it → `403 two_factor_required`.
2. `POST /api/me/2fa/setup`, then add the `uri` to an authenticator app (paste it into a QR generator, or type
   `secret`). `POST /api/me/2fa/enable` with a code → 10 recovery codes and a `twoFactorOn` email.
   `GET /api/admin/calendars` → `200`.
3. `PUT /api/admin/signup-settings` `{ "open": false, "allowedDomains": ["example.nl"] }`.
   `POST /api/auth/sign-up` for `student@example.nl` → `202` and a confirmation email. `student@gmail.com` →
   `403 sign_up_by_invitation`.
4. Open the confirm link's token with `POST /api/auth/confirm` and the password → `204`. Sign in.
   `GET /api/me/channels` shows the address, active, `accountAddress: true`. Add a calendar link and a channel as
   in 003.
5. Sign up again with `student@example.nl` → the same `202`, and Mailpit shows a "someone tried to sign up" email.
6. Sign in with a wrong password 6 times → the 6th gets `429` with `Retry-After: 30`.
7. `POST /api/auth/password-reset`, then complete it from the email. The old token is now `401`, and a
   `passwordChanged` email arrives.
8. As the admin: disable the student. The student's token → `401`, and sign-in → `account_disabled`. Re-enable.
   Delete the student: a `deleted` email arrives, and `SELECT count(*) FROM calendar_link` is `0`.
9. Set the Turnstile test keys from [configuration.md](./contracts/configuration.md#local-development) and restart.
   Sign-up without `botCheck` → `400 bot_check_failed`. With `"botCheck": "XXXX.DUMMY.TOKEN.XXXX"` (Cloudflare's
   dummy response) → `202`. With secret `2x…AA` → `400`.
10. Read the security log as the operator:

    ```sql
    SELECT occurred_at, type, account_id, actor_id, source, detail FROM security_event ORDER BY id;
    ```

    Every step above has its event, and no address appears.

Expected: each step matches the acceptance scenarios in [spec.md](./spec.md). Also, the app log has no address,
token or code (`grep -E '@|token=' app.log` finds nothing from the app).
