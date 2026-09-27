# Research: Accounts and Login

**Spec**: [spec.md](./spec.md) | **Builds on**: [001 research](../001-ical-change-detection/research.md),
[002 research](../002-subscriber-change-filters/research.md), [003 research](../003-notification-channels/research.md)

No NEEDS CLARIFICATION items remain after the spec's clarification session. Each decision below settles a mechanism
the spec leaves open. Two library questions were evaluated:
- password hashing (R4): no package, since it is in the shared framework;
- authenticator codes (R5): one small MIT package.

R17 lists three places where the spec's requirements pulled against each other. The spec has since been amended for two of them (SC-005; FR-018, FR-022, FR-024, FR-027a).

## R1. Ownership in the schema: `subscriber` becomes an account foreign key

**Decision**: migration `004_accounts.sql` creates `account`, then turns every `subscriber` column into
`bigint NOT NULL REFERENCES account (id) ON DELETE CASCADE`. The affected tables are `calendar_link`,
`subscription_filter`, `channel`, `subscription_setting` and `declined_address`, plus `preset.owner`, which stays
nullable because `NULL` means global.
- **The column names stay** (`subscriber`, `owner`). Their meaning ("the account that subscribes or owns") is
  unchanged, and keeping the names keeps every existing SQL string and trigger valid. Only the C# parameter types
  change, from `string` to `long`.
- **001's view `subscription` is dropped and recreated around the type change**, because PostgreSQL refuses
  `ALTER COLUMN … TYPE` on a column that a view uses.
- **The regex CHECKs are dropped first.** They are the auto-named `*_subscriber_check` and `preset_owner_check`
  constraints, and `bigint` has no `~` operator.
- **Existing rows are deleted, not converted** (spec Assumptions, "No data to migrate"). The migration deletes all
  rows from the subscriber-owned tables and `calendar`, and personal presets. The global presets, including the
  default one, stay.
- **`subscriber_setting` is merged into `account`** (`language`, `time_zone`) and dropped. 003's settings routes
  now read and write the account row.
- **`subscription_filter.preset_id` changes from `ON DELETE RESTRICT` to `NO ACTION`.** Deleting an account
  cascades to both `preset` (its personal presets) and `subscription_filter`, and PostgreSQL does not fix the order
  of the two cascades. RESTRICT is checked at once, so the deletion could fail. NO ACTION is checked at the end of
  the statement, after both cascades have run. For 002's routes nothing changes: a single `DELETE FROM preset` of a
  preset still in use still fails.
- **Account deletion is still explicit about calendars.** FR-024 removes calendars that lose their last
  subscriber, and no cascade does that. `Accounts.DeleteAsync` collects the account's calendar ids, deletes the
  account (the cascades remove links, filters, settings, channels, deliveries and presets), then deletes those
  calendars that now have no links, all in one transaction. This is the same rule as 001's
  `Subscriptions.RemoveAsync`.

**Alternatives considered**:
- *Rename the columns to `account_id`*: clearer to read, but it touches every query and trigger of 001 to 003 for
  no behavioural gain.
- *Keep `subscriber` as text holding the account id*: then there is no foreign key and no cascade. The constitution
  requires integrity rules in the database, and FR-024 and SC-008 would depend on application code finding every
  table.

## R2. Sessions: opaque bearer tokens through the platform's authentication

**Decision**:
- A session secret is 32 random bytes from `RandomNumberGenerator`, base64url encoded (43 characters). Only its
  SHA-256 is stored, in `session.token_hash`, the same scheme as 003's tokens.
- The frontend's server sends it as `Authorization: Bearer <token>`. 001's Transport rule already limits `/api` to
  the frontend's server side, so the browser never holds the API token. How the frontend keeps the browser signed
  in (e.g. its own `HttpOnly` cookie) belongs to the frontend feature.
- **ASP.NET Core authentication and authorization** (platform features, Principle II) replace the operator-key
  middleware:
  - `SessionAuthenticationHandler` hashes the bearer token, loads the session with its account in one indexed
    query, and rejects the session if it is idle past `SessionIdleLifetime` or awaits a code. Otherwise it builds a
    `ClaimsPrincipal` with the account id, session id, role, and whether the session proved a second factor.
  - `last_used_at` is written at most once per 5 minutes per session, so reads do not become writes. It is shown
    with minute precision (FR-014a), and 30 days of idle time is not sensitive to 5 minutes.
  - Authorization runs before the endpoint delegate binds its body, so a request without a session gets 401 before
    any 400. This keeps the property that 001's middleware was written for.
- **Deny by default**: a fallback authorization policy requires a signed-in, confirmed, not-disabled account for
  every endpoint. Only the routes in [api.md § Public](./contracts/api.md#public-routes-no-session) and 003's `/n/`
  pages opt out with `AllowAnonymous()`. A route added later without a thought is therefore closed, not open.
- **Admin routes share the prefix `/api/admin`**. That group requires the `admin` policy: role `admin` and a
  session that proved a second factor. The three former operator routes move into it: `/calendars`, the global
  `/presets` and `/channel-types`. That way SC-013 can enumerate every admin route from the endpoint data source.
- **Refusals carry their reason** (spec: "responses tell them why"). An `IAuthorizationMiddlewareResultHandler`
  maps a failed requirement to a Problem Details response with `reason`:
  - `401 not_signed_in`: no session, an unknown one, or an expired one;
  - `403 admin_required`;
  - `403 two_factor_required`.
- The operator key is gone (FR-033). `Settings.Load` no longer requires `InTheKnow:ApiKey`, and if it is still set
  a startup warning says it is ignored. Nothing reads it, so a request that carries it is simply unauthenticated.

**Alternatives considered**:
- *ASP.NET Core Identity with its cookie and `UserManager`*: it needs EF Core stores (the project uses Npgsql
  directly) or a custom `IUserStore` with about ten interfaces. Its API endpoints cannot express invites, domain
  rules, the throttling of FR-012 or the no-enumeration rules. Only its password hasher is used (R4).
- *Cookie authentication with an `ITicketStore`*: the Data Protection keyring becomes another operator secret and
  volume, cookies across the frontend server boundary need CSRF handling, and a table is still needed to list and
  end sessions (FR-014a).
- *JWTs*: they cannot end at once (FR-013, SC-009) without a server-side lookup, and with that lookup they add
  nothing over an opaque token.

## R3. The client's address and device behind the frontend's server

**Decision**:
- Behind the frontend's server and the reverse proxy, `RemoteIpAddress` would always be the frontend's server.
  That would make FR-012's per-address throttling one shared bucket.
- ASP.NET Core's `ForwardedHeadersMiddleware` (platform) is enabled for `X-Forwarded-For` only. It trusts only the
  addresses and networks in `InTheKnow:Accounts:TrustedProxies`. The frontend's server and the reverse proxy
  forward the browser's address, and the frontend's server also forwards the browser's `User-Agent`.
- The default is empty, which trusts nobody, and startup logs a warning then. Throttling still works, but per
  proxy.
- **IPv6 sources are grouped per /64** for every source-keyed limit, because one household or attacker usually has
  a whole /64. IPv4 addresses are used as they are.
- **Device description** (FR-014a): a small function maps `User-Agent` to "Browser on OS". The browsers are Edge,
  Opera, Firefox, Chrome and Safari, in that order of token precedence. The systems are Windows, Android, iOS,
  macOS and Linux. Anything else is "Unknown browser". Only the derived description is stored, never the raw
  `User-Agent`, and no address is stored with a session (spec, Session entity). No user-agent parsing package is
  used: two short token tables give what the owner needs to recognise a device.

## R4. Passwords: `PasswordHasher<T>` from the shared framework

**Decision**:
- `Microsoft.AspNetCore.Identity.PasswordHasher<Account>` is in `Microsoft.Extensions.Identity.Core.dll`, part of
  the `Microsoft.AspNetCore.App` shared framework that the project already uses. It needs **no package**.
- It uses format v3: PBKDF2-HMAC-SHA512, a 128-bit salt, and a versioned header.
- `PasswordHasherOptions.IterationCount = 210_000`, OWASP's current figure for PBKDF2-HMAC-SHA512. The framework
  default is 100,000.
- `VerifyHashedPassword` returns `SuccessRehashNeeded` for older parameters, and sign-in then stores a fresh hash.
  Raising the count later needs no migration.
- Tests set a low count through `IOptions<PasswordHasherOptions>`. The SC-011 measurement uses the production
  count.

**Cost** (SC-011): one verification is about 60–120 ms of CPU. 500 sign-ins per minute is about 8 per second, or
roughly one core. ASP.NET runs requests in parallel, so each sign-in stays well under 2 s.

**Rules** (FR-007):
- A password is 12 to 128 characters, counted as UTF-16 code units after no normalisation. It is compared as
  entered.
- It is rejected with `password_common` when its lowercased form is on the bundled list, and with
  `password_is_email` when it equals the account's address without regard to case.
- The list is `Resources/common-passwords.txt`, an embedded resource: the entries of SecLists' "top 100,000 common
  passwords" (MIT) that are 12 to 128 characters long, lowercased and deduplicated. That is a few thousand lines,
  loaded once into a `FrozenSet<string>`. Shorter entries would be refused by length anyway. The implementation
  records the SecLists commit the file came from.

**No account enumeration by timing** (SC-003):
- A sign-in for an unknown address verifies the password against a fixed dummy hash, generated at startup with the
  same parameters.
- A sign-up for an address that already has a confirmed account still hashes the submitted password.
- The response therefore costs one hash in every case.

**Alternatives considered**:
- *Argon2id* (Konscious, Isopoh): stronger against GPUs, but a new dependency for a gain that the password-length
  rule and throttling already cover.
- *`Rfc2898DeriveBytes.Pbkdf2` directly*: the same primitive, but with a storage format, versioning and rehashing
  written by hand. Principle II asks for the established library.
- *An online breach check (HIBP k-anonymity)*: the spec rules it out, since it sends data about users' passwords
  to a third party (Assumptions).

## R5. Authenticator codes: Otp.NET

**Decision**: TOTP (RFC 6238) uses **Otp.NET 1.4.1** (MIT, no dependencies, targets `net10.0`, released
2025-12-01, by the author of Bitwarden).
- The ASP.NET Identity TOTP code is `internal` and only reachable through `UserManager` (R2).
- Principle II forbids hand-rolled authentication crypto, even though TOTP is only an HMAC with a truncation.

How it is used:
- **Key**: 20 random bytes (160 bit, RFC 4226's recommendation), stored in `two_factor.secret`. It is shown once as
  base32 (Otp.NET's `Base32Encoding`), and in an
  `otpauth://totp/InTheKnow:{address}?secret=…&issuer=InTheKnow&algorithm=SHA1&digits=6&period=30` URI. The
  frontend turns that URI into a QR code.
  - SHA-1, 6 digits and a 30-second period are the only settings Google Authenticator and most other apps honour.
  - The address in the label is the account's own address, returned only to its owner, in the setup response.
- **Verification**: `Totp.VerifyTotp(code, out step, new VerificationWindow(1, 1))` accepts the previous, current
  and next step (FR-017, clock drift).
  - It is accepted only if `step > two_factor.last_step`, and `last_step` is then set to `step`. The update is one
    statement, `UPDATE … SET last_step = $s WHERE account_id = $a AND last_step < $s`. Two concurrent submissions
    of the same code therefore cannot both succeed (US4-4).
- **Setup**: starting setup writes a new secret with `enabled_at = NULL`. Only a valid code sets `enabled_at`
  (FR-015). Starting again replaces an unconfirmed secret. Once 2FA is on, setup is refused.
- **Recovery codes**: 10 codes. Each is 16 characters from the RFC 4648 base32 alphabet (80 bit), shown as
  `xxxx-xxxx-xxxx-xxxx`, and compared after removing hyphens and spaces and uppercasing. Only the SHA-256 is
  stored. 80 bits make a slow hash unnecessary, and a fast one lets a submitted code be looked up by hash instead
  of being verified against ten slow hashes. Using one sets `used_at` with an `UPDATE … WHERE used_at IS NULL`, so
  it succeeds once.
- **The key is stored as it is**, as ASP.NET Identity stores its authenticator key. Reading it needs database
  access, and signing in needs the password as well. Encrypting it would bring the Data Protection keyring
  (another operator secret and volume, R2).
  `ponytail:` plain storage; encrypt with Data Protection if database backups leave the server.

**Alternatives considered**:
- *Writing RFC 6238 on `HMACSHA1`*: about 20 lines, but it is hand-rolled authentication code, which the
  constitution forbids.
- *GoogleAuthenticator (BrandonPotter)*: it bundles QR image generation that the API does not need, since the
  frontend renders the URI.

## R6. Two-step sign-in and confirming who you are

**Decision**:
- **The password step** (`POST /api/auth/sign-in`) checks the bot check, then throttling, then the password. It
  then applies the shared checks of [sign-in-method.md](./contracts/sign-in-method.md): confirmed, not disabled,
  2FA.
  - Without 2FA, it creates a session and returns its token.
  - With 2FA, it creates a session with `awaiting_code = true` and returns its token with
    `secondFactorRequired: true`.
- **An awaiting session** is accepted only by `POST /api/auth/sign-in/code` and `POST /api/auth/sign-out`. The
  authentication handler treats it as signed out everywhere else. It expires 10 minutes after creation.
  - A valid code or recovery code clears `awaiting_code` and sets `second_factor = true` on the same row, so the
    token does not change.
  - Code failures are throttled on the same keys as the password (FR-012), and the pending session survives them.
- **Confirming who you are** (FR-027a, and the "entering the current password" of FR-021, FR-022, FR-024 and
  FR-018) uses **one mechanism, step-up re-authentication**. `POST /api/me/reauthenticate` takes the password, plus
  a code when 2FA is on, and sets `session.reauthenticated_at`.
  - These routes require it within the last **10 minutes**: change or add a password, change the email address,
    delete the account, add or remove a sign-in method, and turn 2FA off.
  - Without it they return `403 reauthentication_required`.
  - A later sign-in method offers its own re-authentication route that sets the same column. The test-only method
    does so already (R7). That is what lets the account suites run unchanged over both methods (SC-006).
  - Generating new recovery codes needs only a current code (FR-016, US4-7), so it takes `code` in its body.

  A client can still offer "enter your current password" on one screen: the frontend calls reauthenticate, then
  the action.

**Alternatives considered**:
- *A password field in each sensitive request*: it hard-wires "password" into every route, and an account without
  a password (US6-5) could then never delete itself.
- *A separate short-lived challenge token for the code step*: a second token table and lifecycle, for what one
  boolean on the session row gives.

## R7. Sign-in methods: one table and a shared pipeline, no interface

**Decision**:
- `sign_in_method` holds each method generically, with `account_id`, `type`, and two optional columns:
  - `key`: the provider's stable subject for external types, unique per `(type, key)`;
  - `secret`: the password hash.
- One password per account is enforced by a partial unique index.
- `session.method_id` references it with `ON DELETE CASCADE`. Removing a method therefore ends its sessions in
  the database (FR-027a, SC-014), with no application code to forget.
- **Shared pipeline**: a method type is its own verification route, which calls the shared
  `SignIn.CompleteAsync(account, method, http)` ([sign-in-method.md](./contracts/sign-in-method.md)). Everything
  the spec says must not change per type lives there or below: confirmed and disabled checks, 2FA, session
  creation, `last_used_at`, security events, and step-up.
- Listing and removing methods, and the "at least one remains" rule, work on rows of any type.
- **At least one method** is enforced in the database: a deferred constraint trigger on `sign_in_method` refuses a
  commit that leaves an existing account with none. It uses a per-account advisory lock, like 003's channel limit.
  Two concurrent removals therefore cannot both succeed.
- **The test-only method** (type `test`, in `InTheKnow.Tests/TestSignIn.cs`) adds two routes, through an
  `IStartupFilter` registered only by the test host: sign in with a key, and re-authenticate with it. The account,
  session and ownership suites are xunit theories over `password` and `test` (SC-006, SC-014).

**Why no `ISignInMethod` interface**: every shared step already sits behind `CompleteAsync`. What differs between
types is the request shape (email and password, a redirect callback, a WebAuthn assertion), which an interface
method cannot unify. So the interface would have one production implementation and no caller. 003's
`IChannelType` was different: the notifier had to call each type's `SendAsync`. The type name is still constrained
like `channel.type` (`^[a-z][a-z0-9-]{0,30}$`), so a later type is only new rows.

## R8. Throttling: in memory, keyed so that unknown addresses behave the same

**Decision**: one singleton `Throttle` holds all failure counters in memory. `Db.TakeInstanceLockAsync` guarantees
a single process (001 R4), so the counters are complete. A restart resets them. That is accepted: an attacker
cannot trigger a restart, and the worst case is one fresh allowance.

**Keys** (FR-012):
- **Account key = the normalised address** (lowercased), not the account id. Unknown addresses therefore count,
  wait and respond exactly like known ones (SC-003).
  - The code step and step-up re-authentication key on the account id instead. Only a signed-in or password-proven
    person reaches them, so there is nothing to enumerate.
- **Source key** = the IPv4 address, or the IPv6 /64 (R3).

**Rules**:
- **Per (account key, source)**: failures 1 to 5 are free. After the 5th, each further attempt from that source for
  that account waits 30 s, 1, 2, 4, 8 minutes, then 15 minutes for each further failure. The counter is forgotten
  after a success from that source, after a completed password reset (for every source of that account), or one
  hour after its last failure. Other sources are unaffected (US clarification).
- **Per account key, all sources**: failure times from the last hour are kept. The failure that brings the count to
  20 starts a 15-minute wait for every source. When the wait ends, one attempt is evaluated, and if it fails while
  the hourly count is still at or above 20, another 15-minute wait starts. A success or a completed reset clears
  it.
- **Per source, across accounts**: at `SourceFailuresPerHour` failures in the sliding hour (default 3,000, operator
  configurable), that source waits until its count drops below the limit.
  - 500 sign-ins per minute from one campus address with a 10% typo rate give about 3,000 failures per hour, which
    is the reason for this default.
- A refused attempt is not evaluated and does not count as a failure. It returns `429 too_many_attempts` with
  `Retry-After`, the same for known and unknown addresses. No account is ever locked beyond these waits.

**Emails triggered without signing in** (FR-012a, FR-004):
- **Per-address caps** (3 resets, 3 "someone tried to sign up" notices and 3 confirmations per 24 h) are counted
  from `account_message` rows. The rows are kept at least 24 h for this (data-model). A request over a cap gets the
  normal response and sends nothing.
- **Per-source request throttling** of sign-up, reset request, confirmation request and invite acceptance uses
  ASP.NET Core's rate limiting middleware (platform). It is a `SlidingWindowLimiter` partitioned by source key,
  `SourceRequestsPerHour` (default 1,000), and gives `429 too_many_requests`. It is per source, so it reveals
  nothing about any address.

**Alternatives considered**:
- *Counters in PostgreSQL*: they survive restarts, but every failed attempt becomes a write and a cleanup, for a
  single-process app.
- *The rate limiting middleware for failures too*: it counts requests, not failures, and has no increasing wait.
  It is used where it fits, for per-source request volume.

## R9. Account emails: a queue of their own, drained by 003's notifier

**Decision**: a new table `account_message` is the queue and, for link emails, also the token. The
[account emails contract](./contracts/account-emails.md) lists every kind.

**Why not 003's `channel_message`**: invites, the confirmation of a new address, the notice to the old address and
the "account deleted" notice go to addresses that have no channel, and the last one to an account that no longer
exists. Security notices must also go out when every channel is paused or unsubscribed (spec Assumptions).

How it works:
- A row holds:
  - `kind`;
  - `to_address` and `language`, fixed when queued;
  - `account_id` (`ON DELETE CASCADE`, NULL for the deletion notice and for invites);
  - `invite_id` (`ON DELETE CASCADE`, so a revoked invite's unsent email is dropped);
  - `args` for small values such as the number of remaining recovery codes.
- **Tokens are minted at send time**, as 003 does. When the notifier sends a link kind (`confirm`, `reset`,
  `emailChange`, or an `invite` for a named address), it generates the token and stores its SHA-256 and expiry in
  that row (for an invite, in `invite.token_hash`), sends, and commits. The raw token only ever exists in the
  email.
  - The same transaction invalidates earlier unused tokens of the same account and purpose (`used_at` on rows that
    have a token, `expired_at` on older rows of that kind still unsent) (FR-004, FR-009, and
    the edge case "Sign-up with an address of an unconfirmed account").
- **The pending email change** is the `emailChange` row itself: its `to_address` is the new address (spec,
  "Account email token" entity). Confirming it moves the account, and its own channel (R15), to that address.
- **Sending**: `Notifier.RunTypeAsync` for `email` drains `account_message` before 003's queued channel messages
  and digests. It uses the same rate slot, backoff and retry window (FR-025, and the edge case "Mail server down").
  An unsent row older than `RetryWindow` is marked expired, and the user can request a new email.
  - The send transaction re-reads the account. If the account is disabled, only the admin-requested kinds are
    sent (FR-033c): `disabled`, `enabled`, `deleted`, `roleChanged` and `twoFactorOffByAdmin`.
- **Rendering**: `EmailChannel` gains `MessageKind.Account`. The message holds a text key, one optional link and
  `args`. Each message has a text and an HTML part, fixed text from `Texts` in the row's language (SC-010), and
  every inserted value encoded, as in 003. It carries **no `List-Unsubscribe`**, because these emails are about the
  account, not optional news.
- **Links** point to `{FrontendBaseUrl}/account/{page}?token={token}`, with the pages `confirm`, `reset`, `email`
  and `invite` (FR-036, as 003 FR-009). The frontend feature must serve these pages with
  `Referrer-Policy: no-referrer` and act only on an explicit button (FR-003, FR-009, and the edge case "Links opened
  by mail scanners"). The API side already needs the explicit POST.
- **Retention**:
  - The `deleted` notice row is deleted as soon as it is sent or expires. Its `to_address` is the only personal
    data that outlives the account, for at most `RetryWindow` (SC-008's one bounded exception).
  - Any other notice row is deleted 24 h after it was sent.
  - A token row is deleted 24 h after it was used or expired.
  - An expired unsent row is deleted after 24 h.

  The 24 h minimum is what the per-address caps count (R8).

**Alternatives considered**:
- *Sending inline in the request*: an SMTP outage would fail sign-up, and there would be no retry and no shared
  rate limit.
- *Minting the token at request time and storing it raw until sent*: this puts secrets in the database, which
  003 R7 already avoided.

## R10. Who may sign up, and invites

**Decision**:
- **Sign-up settings** are a single row (`signup_settings`, primary key `id boolean DEFAULT true CHECK (id)`). It
  holds `open` and `allowed_domains text[]`, lowercased, at most 100 entries, each a valid host name. The migration
  inserts `open = false, allowed_domains = '{}'` (FR-001a).
- **Order in `POST /api/auth/sign-up`**:
  1. the bot check (FR-037);
  2. the per-source rate limit;
  3. the password rules (they do not depend on the address's account);
  4. the invite or the sign-up settings, decided from the address alone (`sign_up_by_invitation`, FR-001b);
  5. only then the existing-account branch of FR-006.

  Steps 1 to 4 never look at `account`.
- **Invite kind follows from the route, not from a lookup.** FR-037 requires the bot check before anything about
  the request is looked at, but only shareable invites need one.
  - A shareable invite is used through **sign-up** with an `invite` field, so it gets the bot check like any
    sign-up.
  - A named invite is used through **`POST /api/auth/invite/accept`**, which never asks for one.
  - Each route refuses the other kind's token as `invite_invalid`.
- **Using up a shareable invite** is atomic: `UPDATE invite SET used = used + 1 WHERE token_hash = $1 AND used <
  max_uses AND expires_at > now() RETURNING …`. The 31st of 30 gets no row back and is refused with
  `invite_invalid` (US8-7). It runs before the FR-006 branch, so every submitted sign-up uses one (edge case
  "Shareable invite and existing addresses").
- **Named invites**:
  - Creating one for an address with a confirmed account gives `409 account_exists`.
  - Accepting one:
    - creates the account confirmed, with the admin role if the invite grants it;
    - or, if the address has an unconfirmed account, takes over that row: it replaces its password method,
      confirms it, and applies the role. An unconfirmed account holds no data;
    - then deletes the invite.
  - Resending queues a new `invite` email, whose send replaces `invite.token_hash`, so the old link stops working.
- **Shareable invites** get their token at creation. It is returned once, in the `link` of the creation response
  (FR-001e), and they cannot grant admin (a database check).
- **Cleanup**: an invite is deleted when it is used up or revoked, or 30 days after it expires (Invite entity).

## R11. Bot checks: one siteverify client, the provider is data

**Decision**: Turnstile and reCAPTCHA verify in the same way:
- a form POST of `secret` and `response` to a siteverify URL;
- a JSON reply with `success` and `error-codes`.

So one `BotCheck` class holds all shared logic, and a provider is a record `(Name, VerifyUrl)`. There are two in
production:
- `turnstile` → `https://challenges.cloudflare.com/turnstile/v0/siteverify`;
- `recaptcha` → `https://www.google.com/recaptcha/api/siteverify`.

Tests construct `BotCheck` with a `test` provider whose URL is answered by the existing `FakeHttp` handler (FR-038:
the bot-check suite runs against a test-only provider). hCaptcha uses the same protocol, so a later provider is one
more record. [bot-check.md](./contracts/bot-check.md) describes the seam.

- **reCAPTCHA v2** (checkbox or invisible), not v3. v3 returns a score and an action, which would need a
  threshold and action names as extra configuration and a policy for borderline scores. v2 gives the same yes/no
  as Turnstile, so the providers stay interchangeable (US9-4).
- **Replays** (US9-2, and the edge case "A bot check result used twice") are refused by both providers
  (`timeout-or-duplicate`), and the code maps that to `bot_check_failed`. The test provider simulates it.
- **No `remoteip`** is sent. Both providers make it optional, and leaving it out sends one item less to a third
  party (Principle II, and the spec's bot check privacy assumption).
- **Outcomes**:
  - `Passed`.
  - `Failed`: `success: false`, except the provider's own `internal-error`.
  - `Unavailable`: a timeout (5 s), a transport error, a non-2xx status, an unparsable body, or `internal-error`.
- For FR-040, `Unavailable` lets sign-in continue and refuses the other actions with `503 try_again_later`. Every
  `Failed` and `Unavailable` is logged with the provider and the error codes, never the response token.
- The request carries the result as `botCheck` in the JSON body. It is verified as the first step of the handler,
  before any other field is validated (US9-2).
- **No hostname check** of the provider's reply. Turnstile and reCAPTCHA return the hostname, but a mismatch there
  would mean a stolen site key, which is outside what US9 asks for.

## R12. Admins: enforcement points and the last admin

**Decision**:
- `account.role` is `'user'` or `'admin'`.
- **The three enforcement points of FR-032a**:
  - the `admin` policy (R2) requires `session.second_factor`;
  - promotion checks `two_factor.enabled_at IS NOT NULL` in the same transaction that changes the role;
  - `DELETE /api/me/2fa` refuses admins with `409 demote_first`.
- **The last admin**: a trigger on `account` refuses any update or delete that leaves no admin when there was one
  before. It holds an advisory lock on "admins", so two admins demoting each other at once cannot both succeed.
  This covers self-demotion, self-deletion and anything run by hand in `psql`. The routes check first, so they can
  return `409 last_admin` with a reason. The trigger is the guarantee.
- **Disable, re-enable and delete** apply to regular accounts only. An admin gets `409 demote_first`, and so does
  their own account through these routes (US7-15).
- **Disabling**: sets `disabled_at` and deletes all sessions. Then:
  - `SignIn.CompleteAsync` refuses `403 account_disabled` after a correct password;
  - account emails are suppressed, except admin-requested ones (R9);
  - **notifications stop**: 003's fan-out in `Checker.ApplyDiffAsync` joins `account` and skips disabled
    accounts, and the notifier's send-time re-check skips pending rows of a disabled account with the new delivery
    reason `accountDisabled`.
  - Re-enabling resumes future changes only, which is 003's "no backfill" rule.
- **Removing 2FA from an account** (FR-019) deletes its `two_factor` and recovery-code rows and all its sessions,
  queues `twoFactorOffByAdmin`, and logs a security event with the acting admin's id.

## R13. Making an admin from the server

**Decision**: the same executable takes a command:
`dotnet InTheKnow.dll make-admin <address> [--language en|nl]`. In the container that becomes
`docker compose exec app dotnet InTheKnow.dll make-admin …`.
- `Program.cs` checks `args` before building the web host, loads `Settings`, opens the database and runs
  `Accounts.MakeAdminAsync`, then exits with 0 or with 1 and a message.
- **It does not take the instance lock or migrate.** It must work next to the running server, which holds the lock.
  It refuses to run when `schema_migration` is behind the embedded migrations ("start the server once first"), so
  it never writes to an old schema.
- **What it does** (FR-033b):
  - if a confirmed account has the address, it becomes admin, and a `roleChanged` email is queued. A disabled
    account is re-enabled first, because an admin is never disabled (data-model `account` checks). Promotion
    from the server skips the 2FA precondition, and the account's admin routes stay locked until 2FA is on
    (FR-032a);
  - if a pending named invite exists for the address, it sets `grants_admin = true` on it and resends it (the
    recovery path, which the one-named-invite-per-address index would otherwise block);
  - otherwise it creates a named invite with `grants_admin = true`, `created_by = NULL` and a validity of 7 days,
    and queues its email.

  Either way it logs a security event with source `server`.
- **Sending** is left to the running server's notifier, which drains `account_message` (R9). The command prints
  whether the account was promoted or an invite was queued, never a token.

**Alternatives considered**:
- *An admin bootstrap token in configuration*: a long-lived secret that works for anyone who finds it, and it
  still needs a route.
- *A direct SQL recipe*: it skips the invite, the email and the security event.

## R14. The security log

**Decision**: a `security_event` table with `id`, `occurred_at`, `type`, `account_id` and `actor_id`, `source`, and
`detail`.
- `account_id` and `actor_id` are plain `bigint` values without a foreign key, so events outlive the account
  (FR-035). `actor_id` is the acting admin.
- `source` is `inet`, or NULL when the command runs from the server. Then `detail` says `server`.
- `detail` is a small `jsonb` for non-secret context such as `method_type` and `reason`.
- It is written in the same transaction as the action it records, where there is one.
- There is **no route** that reads it. The operator reads it with `psql`, and the quickstart gives the query.
- A cleanup deletes rows older than 90 days.
- Sign-in failures for unknown addresses have `account_id = NULL`, and no address is stored anywhere in the log
  (FR-034).

**Alternatives considered**: *writing the events only to `ILogger`*: the app cannot enforce the 90-day deletion of
log files it does not own, and the stdout log must not hold addresses.

## R15. The account's own email channel

**Decision**:
- `channel.is_account_address boolean NOT NULL DEFAULT false`, with a partial unique index `(subscriber) WHERE
  is_account_address`.
- **On confirmation** (FR-028, US2-4) the account gets an `active` email channel for its address, with this flag
  set. There are three confirmation paths, and all of them do this: link plus password, a completed reset of an
  unconfirmed account (US3-5), and accepting a named invite.
- **On an email change** (FR-023) the flagged channel's `target` becomes the new address. If the account already has
  a separate channel for the new address, that channel is deleted first, so the unique target index holds, and its
  pending deliveries go with it.
- If the owner removed the flagged channel earlier, the change moves nothing, and the owner can add the address as
  a normal channel (003 FR-005).
- The channel counts toward the limit of 5 (FR-028). Confirming an account always has room, because an unconfirmed
  account can own no channels.

## R16. Periodic cleanup

**Decision**: `Accounts.CleanupAsync(now)` runs from the notifier's existing 10-minute cleanup pass, next to
`Channels.CleanupAsync`. It deletes:
- unconfirmed accounts 30 days after sign-up (FR-005);
- sessions idle past `SessionIdleLifetime`, and awaiting sessions older than 10 minutes;
- invites as in R10;
- `account_message` rows as in R9;
- security events older than 90 days;
- in-memory throttle entries older than one hour. This pass is the only thing that touches them outside requests.

## R17. Tensions in the spec, now resolved in the spec (clarifications of 2026-09-27)

1. **SC-005 and FR-012**: "No more than 20 wrong passwords or codes per account per hour are evaluated" cannot
   hold together with "no account is kept from signing in from every address for more than 15 minutes at a time".
   With 20 failures in the first minute, a strict hourly cap would keep every address waiting for 59 minutes. This
   plan implements FR-012 as written (R8): a 15-minute wait for all sources, then one attempt per wait.
   - **The true worst case is 23 evaluated failures in a rolling hour**: 20, plus one at each of the 15-, 30- and
     45-minute marks.
   - In steady state it is at most 20.
   - **Resolved**: SC-005 now reads "No more than 23 wrong passwords or codes per account per rolling hour are
     evaluated (20, then one per 15-minute wait)".
   - **Amended after /speckit-analyze**: the throttle keeps two keys per account (the address at sign-in, and
     `account:{id}` for the code step, re-authentication and confirmation), so SC-005 now states the bound per key.
2. **FR-037 and invite kinds**: the bot check must come before anything about the request is looked at, but only
   shareable invites need it. Resolved by the route split of R10.
3. **"Entering the current password" (FR-018, FR-021, FR-022, FR-024) and "confirming who they are with an existing
   method" (FR-027a)**: resolved by step-up re-authentication (R6). With a password it is exactly "entering the
   current password". It is also the only form in which the test-only method passes the same suites (SC-006), and
   in which an account without a password can still delete itself (US6-5, US6-7). **Resolved**: FR-018, FR-022
   and FR-024 now say "after confirming who they are (FR-027a)", and FR-027a fixes the 10-minute validity per
   session. FR-021 first kept "entering the current one". After /speckit-analyze it refers to FR-027a too: any method can
   already remove and re-add the password, so requiring the current password specifically protected nothing.
