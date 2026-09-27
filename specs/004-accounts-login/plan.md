# Implementation Plan: Accounts and Login

**Branch**: `004-accounts-login` | **Date**: 2026-09-27 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/004-accounts-login/spec.md`

## Summary

Accounts replace 001's subscriber id as the owner of every calendar, filter, preset and channel. Sessions replace
the operator key. Admins replace the operator inside the app.

- **Ownership** (research R1). Every `subscriber`/`owner` column becomes an `account` foreign key with
  `ON DELETE CASCADE`. The column names are kept, so 001 to 003's SQL and triggers stay valid, and only C# types
  change. `subscriber_setting` merges into `account`. Deletion is one transaction that also drops calendars left
  without subscribers. User routes move from `/api/subscribers/{s}/…` to `/api/me/…`.
- **Sessions** (R2, R3). A session is an opaque 256-bit bearer token, stored as SHA-256. The frontend's server
  sends it, since `/api` is server-to-server per 001 § Transport. It is checked by an ASP.NET Core authentication
  handler.
  - A fallback policy makes every route need a session. Public routes opt out.
  - `/api/admin/…` also needs the admin role and a second factor.
  - The client address comes from `X-Forwarded-For`, and only from configured trusted proxies.
- **Credentials** (R4, R5, R6):
  - Passwords use `PasswordHasher<T>` from the shared framework: PBKDF2-HMAC-SHA512 at 210k iterations, 12 to 128
    characters, and a bundled common-password list.
  - Authenticator codes use **Otp.NET**: ±1 step and replay-proof.
  - Recovery codes are 80-bit and stored as SHA-256.
  - With 2FA on, sign-in has two steps on one session row. Sensitive actions need a step-up re-authentication from
    the last 10 minutes.
- **Modularity** (R7, [sign-in-method.md](./contracts/sign-in-method.md)). `sign_in_method` is generic by `type`.
  Every type calls a shared `SignIn.CompleteAsync`, so sessions, 2FA, throttling and events are written once.
  - Sessions cascade from their method, and a trigger keeps at least one method.
  - A test-only `test` type runs the same suites.
  - There is no interface, because nothing calls types polymorphically.
- **Abuse limits** (R8, R11):
  - Failure throttling is in memory, which the single-instance lock makes complete. It is keyed by normalised
    address, so unknown addresses behave identically, and by source.
  - Per-source request limits use ASP.NET rate limiting. Per-address email caps are counted from the email queue.
  - The bot check is one siteverify client, where a provider is only a name and a URL: Turnstile and reCAPTCHA v2.
- **Account emails** (R9, [account-emails.md](./contracts/account-emails.md)). An `account_message` queue, drained
  first by 003's notifier through the same email rate slot. Link tokens are minted at send time, so no raw token is
  ever stored.
- **Sign-up control and admins** (R10, R12, R13):
  - Sign-up settings are a single row. Invites are named (emailed, can grant admin) or shareable (counted uses,
    atomic).
  - A trigger keeps the last admin, and disabling an account also stops its notifications.
  - A `make-admin` command creates the first admin and recovers a lost one.
- **Security log** (R14): a `security_event` table with no API, deleted after 90 days.

R17 records three places where the spec's requirements conflicted. They are resolved: SC-005 now states the true
bound of 23 per rolling hour, and FR-018, FR-021, FR-022 and FR-024 now refer to FR-027a's step-up confirmation.

## Technical Context

**Language/Version**: C# 14 on .NET 10 (unchanged)

**Primary Dependencies**:
- **Added: Otp.NET 1.4.1** (MIT, no dependencies) for TOTP (research R5).
- From the `Microsoft.AspNetCore.App` shared framework, with no package: `PasswordHasher<T>`
  (Microsoft.Extensions.Identity.Core), authentication and authorization, `ForwardedHeadersMiddleware`, and rate
  limiting.
- From the BCL: `RandomNumberGenerator`, `SHA256` and `FrozenSet`.
- The common-password list is an embedded resource taken from SecLists (MIT).

**Storage**: PostgreSQL 18, with one new embedded migration, `004_accounts.sql` ([data-model.md](./data-model.md)).
It creates 9 tables, rewrites 6 owner columns, adds 1 column, drops 1 table, and adds 2 triggers.

**Testing**: xunit.v3, as before.
- The account, session and ownership suites are theories over `password` and the test-only `test` method
  (SC-006).
- Bot checks run against a test provider on `FakeHttp`, and emails against `FakeSmtp`.
- Every wait and expiry runs on `FakeTimeProvider`.
- SC-002 and SC-013 enumerate the routes from `EndpointDataSource`, so a new route cannot escape them
  ([quickstart.md](./quickstart.md)).

**Target Platform**: Linux server, the single 001 process. It gains one CLI verb (`make-admin`) and no new
background service. Cleanup joins the notifier's existing pass.

**Project Type**: backend of a web application. The SvelteKit frontend, which hosts the pages that email links
open, is a later feature.

**Performance Goals**:
- SC-011: 500 sign-ins per minute, each within 2 s. At about 60–120 ms of PBKDF2 per sign-in that is about one
  core, and a load test measures it (R4).
- Per-request session lookup is one indexed query, and `last_used_at` is written at most every 5 minutes.

**Constraints**:
- No account enumeration through responses or timing (SC-003). An unknown address still costs one hash.
- No secret in logs or responses (SC-007).
- The operator key is gone (SC-013).
- The bot check comes before any lookup (FR-037).
- Deleting an account leaves no personal record except its security events (SC-008).

**Scale/Scope**: a whole school, with tens of thousands of accounts. Semester-start bursts come from shared campus
addresses (R8 defaults).

No NEEDS CLARIFICATION items remain. All decisions are in [research.md](./research.md).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Rule | Status | How |
|---|---|---|
| I. Smallest implementation, no speculative code | PASS | Nothing is built for passkeys, SURFconext or OAuth beyond what FR-026, FR-027 and SC-006 require now: a generic method table and one shared completion call, with no interface (R7). The throttle is in memory, not in the database (R8). No Data Protection keyring, no JWT and no ASP.NET Identity stores (R2). No separate challenge-token table (R6). Bot check providers are data, not classes (R11). |
| I. Preference order | PASS | The platform is used before packages: the password hasher, authentication, forwarded headers and rate limiting all come from the shared framework (R2, R3, R4, R8). 003's queue, notifier and email rendering are reused for account emails (R9). The existing `FakeHttp` serves as the test bot provider. |
| I. Deviations justified | PASS | See Complexity Tracking: one package, two triggers, the in-memory throttle and a CLI verb. |
| II. Store only required data, name each personal field | PASS | [data-model.md § Personal and sensitive data](./data-model.md#personal-and-sensitive-data-principle-ii). No name or phone number. Sessions store a coarse device label, never the `User-Agent` or an address. The pending new address lives only in its email row, for at most 24 h. The security log keeps the source address for 90 days, because FR-035 requires it. |
| II. No committed secrets | PASS | The bot check secret and SMTP password come from the environment. Shipped config has empty placeholders. The only keys in docs are Cloudflare's and Google's published test keys ([configuration.md](./contracts/configuration.md)). |
| II. Validate input at trust boundaries | PASS | Every body field is validated server-side with a reason ([api.md](./contracts/api.md)). Forwarded headers are believed only from trusted proxies (R3). Provider replies are mapped by structure, and anything unexpected counts as unavailable, never as passed (R11). |
| II. Escape browser output | PASS | The API returns JSON only. Account emails encode every inserted value, as 003 does (R9). |
| II. Established auth libraries, no hand-rolled crypto | PASS | `PasswordHasher<T>` (R4), Otp.NET (R5), and ASP.NET Core authentication and authorization (R2). Tokens and codes are `RandomNumberGenerator` output stored as SHA-256, the same scheme that 003 passed with (R7 there). |
| II. HTTPS outside development | PASS | 001's Transport rule is unchanged: TLS at the reverse proxy, and `/api` is not public. `FrontendBaseUrl` must already be `https` outside localhost (003). |
| Tech: .NET, PostgreSQL, integrity as constraints | PASS | Uniqueness (address, one password, `(type, key)`, token hashes, the one named invite per address), role and state consistency, invite shape, the single-row settings, and cascades are all constraints. "At least one method" and "the last admin stays" are triggers with advisory locks. Rate caps on the app clock stay application checks, as in 002 R7 and 003. |
| Tech: GPL-3.0-compatible licences, lockfile | PASS | Otp.NET and SecLists are MIT. `packages.lock.json` is updated with the package. |
| Workflow: checks as the gate; tests for non-trivial logic | PASS (planned) | Every mechanism has a test that fails if it breaks, including the SC-002 and SC-013 route enumerations and the SC-011 load test (opt-in with `ITK_LOAD=1`) ([quickstart § 1](./quickstart.md)). |

**Post-design re-check (after Phase 1)**: PASS.
- The design adds 9 tables, 1 column, 2 triggers, 1 package and 1 CLI verb. It adds no background service and no
  interface.
- It removes the operator key and `subscriber_setting`.
- Each addition either follows from an FR or is justified below.

## Project Structure

### Documentation (this feature)

```text
specs/004-accounts-login/
├── spec.md
├── plan.md              # This file
├── research.md          # Phase 0
├── data-model.md        # Phase 1
├── quickstart.md        # Phase 1
├── contracts/           # Phase 1
│   ├── api.md
│   ├── configuration.md
│   ├── account-emails.md
│   ├── sign-in-method.md
│   └── bot-check.md
├── checklists/
└── tasks.md             # Phase 2 (/speckit-tasks)
```

### Source Code (repository root)

```text
backend/
├── InTheKnow/
│   ├── Migrations/004_accounts.sql  # NEW: account, sign_in_method, session, two_factor, recovery_code, invite,
│   │                                #      account_message, signup_settings, security_event; owner columns → FK;
│   │                                #      channel.is_account_address; delivery reason; triggers; drop subscriber_setting
│   ├── Resources/common-passwords.txt  # NEW: embedded, SecLists-derived (R4)
│   ├── Accounts.cs            # NEW: sign-up rules, confirm, reset, email change, delete (R1), methods, invites,
│   │                          #      sign-up settings, admin actions, MakeAdminAsync (R13), CleanupAsync (R16)
│   ├── SignIn.cs              # NEW: password verify + dummy hash, CompleteAsync pipeline, sessions, step-up,
│   │                          #      SessionAuthenticationHandler, policies + reason result handler, device label
│   ├── TwoFactor.cs           # NEW: Otp.NET setup/verify, last_step, recovery codes
│   ├── Throttle.cs            # NEW: in-memory failure counters (R8)
│   ├── BotCheck.cs            # NEW: siteverify client, providers (R11)
│   ├── Passwords.cs           # NEW: length/common/email rules, PasswordHasher options
│   ├── SecurityLog.cs         # NEW: insert helper for security_event (R14)
│   ├── AccountApi.cs          # NEW: MapAccountApi(): /api/auth, /api/me account routes, /api/admin account routes
│   ├── Api.cs                 # CHANGED: operator-key middleware removed; /api/me and /api/admin groups; subscriber → account id
│   ├── FilterApi.cs, NotificationApi.cs  # CHANGED: /api/me and /api/admin paths; subscriber → account id
│   ├── Subscriptions.cs, Presets.cs, Channels.cs  # CHANGED: long owner; settings from account; account address channel (R15)
│   ├── Checker.cs             # CHANGED: fan-out skips disabled accounts (R12)
│   ├── Db.cs                  # CHANGED: SchemaCurrentAsync for make-admin (R13)
│   ├── Notifier.cs            # CHANGED: drains account_message first; accountDisabled skip; Accounts.CleanupAsync
│   ├── ChannelType.cs, EmailChannel.cs, Texts.cs  # CHANGED: MessageKind.Account rendering; en/nl account texts
│   ├── Settings.cs            # CHANGED: AccountsOptions + validation; ApiKey retired
│   ├── Program.cs             # CHANGED: make-admin verb; auth, forwarded headers, rate limiter, new services
│   ├── InTheKnow.csproj, packages.lock.json  # CHANGED: Otp.NET; embedded Resources/*
│   └── appsettings.json       # CHANGED: Accounts section; ApiKey removed
└── InTheKnow.Tests/
    ├── TestHost.cs            # NEW: SignInAsync helper (account + session), low hash iterations
    ├── TestSignIn.cs          # NEW: the test-only `test` sign-in method (R7)
    ├── SignUpTests.cs, SignInTests.cs, ThrottleTests.cs, TwoFactorTests.cs, AccountTests.cs, OwnershipTests.cs,
    │   AdminTests.cs, MakeAdminTests.cs, BotCheckTests.cs, AccountEmailTests.cs, LogHygieneTests.cs,
    │   SignInLoadTest.cs     # NEW
    └── ApiTests.cs, NotificationApiTests.cs, PresetsTests.cs, ChannelsTests.cs, DeliveryTests.cs, CheckerTests.cs,
        PublicPagesTests.cs, DbTests.cs, SettingsTests.cs, SubscriptionsTests.cs,
        EmailChannelTests.cs   # CHANGED: signed-in accounts instead of subscriber ids
```

Other documents updated during implementation:
- 001 `contracts/api.md`: § Authentication and § Subscriber id point to this feature, and the paths move.
- 001 `contracts/configuration.md`: `ApiKey` is retired.
- 002 `contracts/api.md` and 003 `contracts/api.md`: the paths move.
- 003 `contracts/configuration.md`: a pointer to this feature's additions.

**Structure Decision**: the same single backend project and single test project as 001 to 003. Files are split one
per concern, as in 002 and 003:
- account lifecycle (`Accounts.cs`);
- signing in and sessions (`SignIn.cs`);
- the three credential helpers (`Passwords.cs`, `TwoFactor.cs`);
- the two abuse guards (`Throttle.cs`, `BotCheck.cs`);
- the log (`SecurityLog.cs`);
- routes (`AccountApi.cs`).

## Complexity Tracking

| Addition / deviation | Why needed | Simpler alternative rejected because |
|---|---|---|
| Package **Otp.NET** | TOTP is authentication code, and Principle II forbids hand-rolling it. ASP.NET Identity's TOTP is `internal` and bound to `UserManager` (R5). | 20 lines of RFC 6238 on `HMACSHA1`: hand-rolled authentication code. The full ASP.NET Identity stack needs EF Core or about ten custom store interfaces (R2). |
| In-memory `Throttle` state | FR-012 counts failures, not requests, with an increasing wait, keyed by address and source. The instance lock guarantees one process (R8). | Database counters: they are keyed by normalised address, unknown addresses included, and the database deliberately stores no address outside `account` and short-lived email rows (R14). They also need a cleanup. The rate-limiting middleware counts requests and has no increasing wait (it is still used for FR-012a). |
| Two triggers (`sign_in_method_keep_one`, `account_keep_admin`) | "At least one method" (FR-027a, SC-014) and "the last admin stays" (FR-033a) must hold under concurrency, and the constitution puts integrity rules in the database | Application checks alone: two concurrent removals or demotions can both pass the check |
| `account_message` as a new queue beside `channel_message` | Invites, new-address confirmations and the deletion notice go to addresses with no channel, or with no account (R9) | Reusing `channel_message`: it would need nullable channels, addresses and kinds that break its constraints and 003's caps |
| `make-admin` CLI verb | FR-033b: the first and the recovery admin come from the server, without signing in (R13) | A bootstrap secret in configuration: long-lived, usable by anyone who finds it, and it still needs a route |
| Step-up re-authentication instead of a password field per route | FR-027a's "an existing method", SC-006's test-only method, and password-less accounts deleting themselves (US6-5) all need one mechanism (R6) | A password field per route: it hard-wires the password type into every sensitive route |
| Migration deletes 001–003 subscriber data | Owner columns become foreign keys, and no real deployment exists (spec Assumptions) | Converting subscriber ids into accounts: there are no addresses to create accounts from |
