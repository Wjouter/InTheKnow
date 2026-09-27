---

description: "Task list for Accounts and Login (004)"
---

# Tasks: Accounts and Login

**Input**: Design documents from `/specs/004-accounts-login/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: Included. The constitution requires an automated check for non-trivial logic, and quickstart.md § 1 names a failing-if-broken test file per mechanism. Write each story's tests first. They should fail until that story's implementation tasks are done (a compile failure counts).

**Organization**: grouped by user story, in priority order: US1 (P1), US2 (P1), US3 to US8 (P2), US9 (P3). **US1 and US2 ship together**: without US2 the API cannot be opened to students (spec US2, "Why this priority").

**Phase 2 is large on purpose.** Migration 004 turns every owner column into an account foreign key and drops `subscriber_setting` (T003). From T003 on, every existing route and test is broken, so Phase 2 carries the whole cut-over: owner types, routes under `/api/me` and `/api/admin`, the session handler that replaces the operator key, and the rewrite of the 001–003 tests. US2 is then the proof of ownership (SC-002), not the switch itself.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (a different file, and no dependency on an unfinished task)
- **[Story]**: the user story the task belongs to (US1 to US9)

## Path Conventions

- App: `backend/InTheKnow/`. Tests: `backend/InTheKnow.Tests/` (plan § Project Structure). Paths below are relative to `backend/`, except `specs/…`, which are relative to the repository root.
- Migrations are embedded by the `Migrations/*.sql` glob in `InTheKnow/InTheKnow.csproj`. `Resources/*` is added to it in T001.
- Test databases come from `TestDb` (`InTheKnow.Tests/TestDb.cs`). App tests use `ApiHost` in `InTheKnow.Tests/ApiTests.cs`, which wraps `WebApplicationFactory`. It removes every `IHostedService`, replaces `TimeProvider` with a `FakeTimeProvider` and `Fetcher` with `FakeHttp`, and takes an `Action<IServiceCollection>` plus configuration overrides. Read both files, and `DeliveryTests.cs`'s email harness around `FakeSmtp`, before writing tests. Drive the notifier with `Notifier.RunDueAsync(now)`.
- Every time decision uses the `now` or `TimeProvider` passed in, never `DateTimeOffset.UtcNow` or the database `now()` (003 research R4). Where a contract shows SQL with `now()`, pass the app clock as a parameter instead.
- Every route refusal is `Api.Problem(status, reason)`, with the `reason` strings of contracts/api.md, verbatim.
- Never log an address, token, code, password, key, link or bot check result (FR-034). Log account ids and reason codes only.

## Shared signatures (so tasks written separately compile together)

```csharp
// Settings.cs
sealed class AccountsOptions
{
    TimeSpan SessionIdleLifetime = 30 days; int SourceFailuresPerHour = 3000; int SourceRequestsPerHour = 1000;
    List<string> TrustedProxies = [];   // IP addresses or CIDR networks
    BotCheckOptions BotCheck = new();
}
sealed class BotCheckOptions { string Provider = ""; string SiteKey = ""; string SecretKey = ""; }
// InTheKnowOptions gains `AccountsOptions Accounts`. `ApiKey` stays as a property only so Load can warn that it is ignored.

// Accounts.cs
// One account row. Also the T of PasswordHasher<Account>, which never reads it.
sealed record Account(long Id, string Email, string Role, string Language, string? TimeZone,
                      DateTimeOffset? ConfirmedAt, DateTimeOffset? DisabledAt, DateTimeOffset CreatedAt);
sealed class Accounts(NpgsqlDataSource db, IPasswordHasher<Account> hasher, Throttle throttle, Channels channels,
                      Settings settings, TimeProvider time, ILogger<Accounts> log)
{
    // Queues one account_message row (contracts/account-emails.md). args: non-secret values only.
    public static Task QueueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string kind, long? accountId, string to,
                                  string language, DateTimeOffset now, object? args = null, long? inviteId = null,
                                  CancellationToken ct = default);
    public Task CleanupAsync(DateTimeOffset now, CancellationToken ct);   // research R16
    // The other methods are named in the tasks below. They return the api.md `reason` on refusal and null on success,
    // or a tuple (T? Value, string? Error). "not_found" means 404.
}

// SignIn.cs
static class Claims
{
    const string Account = "itk:account", Session = "itk:session", Method = "itk:method",
                 SecondFactor = "itk:2fa", Reauthenticated = "itk:reauth", Awaiting = "itk:awaiting";
    static long AccountId(this ClaimsPrincipal user); static long SessionId(this ClaimsPrincipal user);
}
sealed class AllowAwaitingCode;   // endpoint metadata: this route also accepts an awaiting-code session (R6)
sealed record ReasonRequirement(string Reason, Func<ClaimsPrincipal, DateTimeOffset, bool> Holds) : IAuthorizationRequirement;
sealed class SignIn(NpgsqlDataSource db, Throttle throttle, Settings settings, TimeProvider time, ILogger<SignIn> log)
{
    // contracts/sign-in-method.md, verbatim, plus the optional throttle key that a success clears.
    public Task<IResult> CompleteAsync(long accountId, long methodId, HttpContext http, bool secondFactor = false, string? throttleKey = null);
    public Task FailAsync(string throttleKey, HttpContext http, long? accountId = null);
    public Task<TimeSpan?> CheckAsync(string throttleKey, HttpContext http);
    public Task ReauthenticatedAsync(long sessionId, long methodId);
    public static (string Token, byte[] Hash) NewToken();   // 32 bytes from RandomNumberGenerator, base64url (43 chars), SHA-256
    public static byte[] Hash(string token);
    public static string Device(string? userAgent);         // research R3: "Firefox on Windows", "Unknown browser"
}
sealed class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>;   // scheme "session"

// Throttle.cs (research R8). Account key: the lowercased address, or "account:{id}" for the code step and step-up.
sealed class Throttle(TimeProvider time, Settings settings)
{
    public TimeSpan? Wait(string accountKey, string source);   // null = this attempt may be evaluated
    public void Fail(string accountKey, string source);
    public void Succeed(string accountKey, string source);     // clears (key, source) and the key's all-source wait
    public void Reset(string accountKey);                      // a completed password reset: every source of this key
    public void Forget(DateTimeOffset now);                    // drops entries whose last failure is over 1 h old
    public static string SourceKey(IPAddress? address);        // IPv4 as is, IPv6 as its /64, "unknown" for null
}

// SecurityLog.cs (research R14). detail: non-secret values only.
static class SecurityLog
{
    static Task WriteAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string type, long? accountId, DateTimeOffset now,
                           IPAddress? source, long? actorId = null, object? detail = null, CancellationToken ct = default);
}

// TwoFactor.cs (research R5)
sealed record CodeCheck(bool Ok, bool RecoveryCode, int RecoveryCodesLeft);
static class TwoFactor
{
    static byte[] NewSecret();                                     // 20 bytes
    static string Uri(string address, byte[] secret);              // otpauth://totp/InTheKnow:{address}?secret=…&issuer=InTheKnow&algorithm=SHA1&digits=6&period=30
    static IReadOnlyList<string> NewRecoveryCodes();               // 10 × "xxxx-xxxx-xxxx-xxxx", base32, 80 bit
    static byte[] HashRecoveryCode(string code);                   // SHA-256 of the code without '-' and ' ', uppercased
    static Task<CodeCheck> VerifyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, string code, DateTimeOffset now, CancellationToken ct);
}

// BotCheck.cs: contracts/bot-check.md, verbatim
sealed record BotCheckProvider(string Name, Uri VerifyUrl);
enum BotCheckOutcome { Passed, Failed, Unavailable }
sealed class BotCheck(BotCheckProvider? provider, string siteKey, string secretKey, HttpClient http, ILogger<BotCheck> log)
{
    public string? ProviderName { get; } public string? SiteKey { get; }
    public Task<BotCheckOutcome> VerifyAsync(string? response, CancellationToken ct);
}

// ChannelType.cs: one kind and three optional trailing parameters, so every 003 constructor call still compiles
enum MessageKind { Confirmation, Changes, Test, Account }
sealed record Message(/* 003's parameters, unchanged */ …, int NotListed,
                      string? AccountKind = null, string? Link = null, IReadOnlyDictionary<string, string>? Args = null);

// InTheKnow.Tests/TestHost.cs
sealed record SignedIn(long AccountId, long SessionId, string Token, string Email, HttpClient Client);
static class TestHost
{
    public const string Password = "test-password-0123";   // valid: 18 characters, not on the common list, not an address
    // Database level, for tests that build Channels/Presets/Subscriptions directly: a confirmed account with a password
    // method hashed at a low iteration count, and NO address channel (003's 5-channel tests stay valid).
    public static Task<long> AccountAsync(NpgsqlDataSource db, string? email = null, string language = "en", string role = "user");
    // Direct insert of account, method (password, or `test` with key = lowercased address) and an active session.
    public static Task<SignedIn> SignInAsync(this ApiHost h, string? email = null, string method = "password",
                                             bool admin = false, bool secondFactor = false);
    // Through the method's own route: POST /api/auth/sign-in, or POST /api/test-auth/sign-in (T032).
    public static Task<SignedIn> SignInViaAsync(this ApiHost h, string method, string email, string? password = null);
}
```

---

## Phase 1: Setup

**Purpose**: the package, the password list and the schema, so every later task can run against the test database.

- [X] T001 In `InTheKnow/InTheKnow.csproj`, add `<PackageReference Include="Otp.NET" Version="1.4.1" />` and `<EmbeddedResource Include="Resources/*" />` beside the migrations glob. Run `dotnet restore` so `InTheKnow/packages.lock.json` records Otp.NET (and the test project's lock file, if it changes). No other package (plan § Technical Context).
- [X] T002 [P] Create `InTheKnow/Resources/common-passwords.txt` (research R4). Take SecLists' `Passwords/Common-Credentials/100k-most-used-passwords-NCSC.txt` or its "top 100,000" equivalent (MIT). Keep only entries 12 to 128 characters long, lowercase them, deduplicate them, and write one per line. Put the SecLists commit hash and licence in a first line starting with `#`, which the loader skips.
- [X] T003 Create `InTheKnow/Migrations/004_accounts.sql` per data-model.md § Migration. Every CHECK, default and index is copied **verbatim** from data-model.md § Tables. In order:
  1. **Delete the rows the spec does not carry over** (R1): `DELETE FROM delivery; DELETE FROM channel_message; DELETE FROM channel; DELETE FROM declined_address; DELETE FROM subscription_setting; DELETE FROM subscription_filter; DELETE FROM calendar_link; DELETE FROM calendar; DELETE FROM subscriber_setting; DELETE FROM preset WHERE owner IS NOT NULL;`. `subscription_filter` goes before `preset` because of its `RESTRICT`. The global presets, including `Default`, stay.
  2. **Create the tables**, in FK order: `account`, `sign_in_method`, `session`, `two_factor`, `recovery_code`, `invite`, `account_message`, `signup_settings`, `security_event`. Specifics:
     - `account`: the `lower(email)` unique index and the partial `(created_at) WHERE confirmed_at IS NULL` index.
     - `sign_in_method`: `UNIQUE (account_id) WHERE type = 'password'`, `UNIQUE (type, key) WHERE key IS NOT NULL`, and an index on `(account_id)`.
     - `session`: `method_id … REFERENCES sign_in_method (id) ON DELETE CASCADE`, plus indexes `(account_id)` and `(last_used_at)`.
     - `account_message.kind`: `CHECK (kind IN ('confirm','signUpAttempt','reset','passwordChanged','emailChange','emailChangeNotice','twoFactorOn','twoFactorOff','twoFactorOffByAdmin','recoveryCodeUsed','methodAdded','methodRemoved','roleChanged','disabled','enabled','deleted','invite'))`. These are the kinds of contracts/account-emails.md. Add its three indexes.
     - `invite`: `UNIQUE (lower(email)) WHERE kind = 'named'`.
     - `signup_settings`: then `INSERT INTO signup_settings (id, open, allowed_domains) VALUES (true, false, '{}')`.
     - `security_event`: the `(occurred_at)` index, and no FK on `account_id` or `actor_id`.
  3. **Change the owner columns** (R1):
     - Drop 001's view: `DROP VIEW subscription`.
     - Drop the two triggers whose `UPDATE OF` column list names an owner column, because PostgreSQL refuses `ALTER COLUMN … TYPE` on such a column: `DROP TRIGGER calendar_link_drop_filter ON calendar_link; DROP TRIGGER preset_owner_fixed ON preset;`. Their functions stay.
     - Drop the regex CHECKs by PostgreSQL's default names for inline column checks: `calendar_link_subscriber_check`, `subscription_filter_subscriber_check`, `channel_subscriber_check`, `subscription_setting_subscriber_check`, `declined_address_subscriber_check` and `preset_owner_check`. Confirm each name against the inline CHECKs in `001_init.sql`, `002_filters.sql` and `003_notifications.sql` if a `DROP CONSTRAINT` fails.
     - For each of `calendar_link`, `subscription_filter`, `channel`, `subscription_setting` and `declined_address`: `ALTER COLUMN subscriber TYPE bigint USING NULL`, which is safe on the emptied tables, keep `NOT NULL`, and `ADD FOREIGN KEY (subscriber) REFERENCES account (id) ON DELETE CASCADE`.
     - `preset`: `ALTER COLUMN owner TYPE bigint USING NULL`. The remaining global rows are NULL already. Then `ADD FOREIGN KEY (owner) REFERENCES account (id) ON DELETE CASCADE`, still nullable.
  4. **Recreate and extend**:
     - Recreate `CREATE VIEW subscription …` exactly as in `001_init.sql:69`.
     - Recreate both triggers exactly as in `002_filters.sql:60` and `:69`.
     - `subscription_filter`: `DROP CONSTRAINT subscription_filter_preset_id_fkey`, then `ADD FOREIGN KEY (preset_id) REFERENCES preset (id)`. That is `NO ACTION`, per R1.
     - `ALTER TABLE channel ADD COLUMN is_account_address boolean NOT NULL DEFAULT false CHECK (NOT is_account_address OR type = 'email')`, then `CREATE UNIQUE INDEX ON channel (subscriber) WHERE is_account_address`.
     - `delivery`: `DROP CONSTRAINT delivery_reason_check`, then `ADD CHECK (reason IN ('hidden','channelNotActive','notificationsOff','subscriptionRemoved','accountDisabled'))`.
     - `DROP TABLE subscriber_setting`.
  5. **Add the triggers** of data-model.md § Cross-table rules:
     - `sign_in_method_keep_one()`: `PERFORM pg_advisory_xact_lock(hashtext('method:' || OLD.account_id))`. Then, if `EXISTS (SELECT 1 FROM account WHERE id = OLD.account_id) AND NOT EXISTS (SELECT 1 FROM sign_in_method WHERE account_id = OLD.account_id)`, `RAISE EXCEPTION … USING ERRCODE = '23514', CONSTRAINT = 'last_method'`. Attach it with `CREATE CONSTRAINT TRIGGER sign_in_method_keep_one AFTER DELETE ON sign_in_method DEFERRABLE INITIALLY DEFERRED FOR EACH ROW`.
     - `account_keep_admin()`: `PERFORM pg_advisory_xact_lock(hashtext('admins'))`. Then, if `NOT EXISTS (SELECT 1 FROM account WHERE role = 'admin')`, `RAISE … USING ERRCODE = '23514', CONSTRAINT = 'last_admin'`. Attach it with `CREATE TRIGGER account_keep_admin AFTER UPDATE OF role OR DELETE ON account FOR EACH ROW WHEN (OLD.role = 'admin')`.

  From this task until T030, the existing suite is red: the C# code still passes `string` owners.
- [X] T004 Create `InTheKnow.Tests/TestHost.cs` with `SignedIn`, `TestHost.Password` and the database-level `TestHost.AccountAsync` from the shared signatures. It hashes with `new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = 1 }))`, because `Account` does not exist until T015. The v3 hash format does not depend on the type parameter, so the hash verifies later under `PasswordHasher<Account>`. and its default email is `$"a{Guid.NewGuid():N}@example.nl"`. Then rewrite and extend `InTheKnow.Tests/DbTests.cs`.
  - **The rewrite comes first.** Replace every subscriber string (18 uses) with an account id from `TestHost.AccountAsync`. The existing assertions stay unchanged.
  - Then assert that each of these is rejected:
    - **`account`**:
      - a second account whose address differs only in case;
      - `role = 'admin'` while unconfirmed;
      - `disabled_at` while unconfirmed;
      - an admin with `disabled_at`;
      - `role = 'owner'`;
      - `language = 'de'`.
    - **`sign_in_method`**:
      - a second `password` row for one account;
      - a `password` row without `secret`, or with a `key`;
      - `type = 'Pass word'`;
      - two rows with the same `(type, key)`.
    - **`session`**:
      - a `token_hash` of 31 bytes;
      - `awaiting_code AND second_factor`.
    - **`two_factor`**: a 19-byte `secret`.
    - **`account_message`**:
      - an unknown `kind`;
      - `confirm` with a NULL `account_id`;
      - `invite` without `invite_id`, and `reset` with one;
      - a `token_hash` on `passwordChanged`;
      - a sent `confirm` without `token_hash`, and an unsent one with it;
      - `token_hash` without `token_expires_at`;
      - `used_at` without `token_hash`;
      - both `sent_at` and `expired_at` set.
    - **`invite`**:
      - a shareable invite with `grants_admin`;
      - a named invite with `max_uses = 2`;
      - a named invite without `email`, and a shareable one with it;
      - a shareable invite without `token_hash`;
      - `expires_at = created_at + 91 days`;
      - `used > max_uses`;
      - a second named invite for the same address in other case.
    - **`signup_settings`**:
      - a second row;
      - `allowed_domains = '{Inholland.nl}'`;
      - 101 domains.
    - **`security_event`**: `type = 'Sign-in'`.
    - **`channel`**:
      - `is_account_address` on a non-`email` channel;
      - two flagged channels for one account.
  - Also assert:
    - The migration left one `signup_settings` row `(true, false, '{}')`.
    - `delivery.reason = 'accountDisabled'` is accepted.
    - **`sign_in_method_keep_one`**:
      - deleting an account's only method fails at commit with SQLSTATE `23514` and constraint `last_method`;
      - deleting one of two methods succeeds;
      - deleting the account (cascading its methods) succeeds;
      - deleting the only method and inserting another in one transaction succeeds;
      - two connections that each delete a different one of an account's two methods, and commit concurrently: exactly one fails.
    - **`account_keep_admin`**:
      - demoting or deleting the only admin fails with `23514` and constraint `last_admin`;
      - with two admins, demoting one succeeds;
      - two admins demoting each other concurrently: exactly one fails;
      - with no admin ever, updating a user's role to `user` succeeds.
    - **Cascades.** Delete an account that has:
      - a link;
      - a filter using its own personal preset, which shows that `NO ACTION` holds;
      - a subscription setting;
      - a channel with a delivery and a `channel_message`;
      - a declined address;
      - a session, a method, `two_factor` with recovery codes, and an `account_message` row.

      Every one of those rows is gone, an `invite.created_by` pointing at the account is NULL, and a `security_event` for it remains. Deleting a preset that a filter uses, on its own, still fails.

**Checkpoint**: `dotnet test --filter DbTests` passes. Every other test may be red until T030.

---

## Phase 2: Foundational (blocking prerequisites)

**Purpose**: everything every story uses:
- configuration and the security log;
- the throttle and the session pipeline, which replaces the operator key;
- step-up re-authentication;
- the account email queue and its rendering in both languages;
- the owner-type switch and the moved routes;
- the test host and the rewritten 001–003 tests.

**⚠️ CRITICAL**: no user story work can begin until this phase is complete.

### Tests first

- [X] T005 [P] Extend `InTheKnow.Tests/SettingsTests.cs` with each rule of contracts/configuration.md § Settings. Each case fails startup with a message naming the key.
  - `SessionIdleLifetime` `00:59:00` or `366.00:00:00`;
  - `SourceFailuresPerHour` 19;
  - `SourceRequestsPerHour` 9;
  - a `TrustedProxies` entry `not-an-ip` or `10.0.0.0/33`;
  - `BotCheck:Provider` `hcaptcha`;
  - `Provider=turnstile` with an empty `SiteKey` or an empty `SecretKey`.

  Also:
  - the shipped defaults start, with empty `TrustedProxies` logging a warning that all users share one throttling source;
  - `10.0.0.1` and `fd00::/8` are accepted.
  - **Flip the `ApiKey` case**: startup no longer needs `InTheKnow:ApiKey`, and when it is set a warning says it is ignored. Remove `ApiKey` from every other case in the file.
- [X] T006 [P] Create `InTheKnow.Tests/ThrottleTests.cs` (unit, `FakeTimeProvider`, a `Settings` from configuration) per research R8.
  - **Per (key, source)**: failures 1–5 are free. After the 5th, `Wait` returns 30 s, then after each further failure 1, 2, 4, 8, 15, 15 min, each measured from that failure. A refused attempt is not counted. Another source for the same key gets `null`.
  - **Per key, all sources**: 20 failures spread over 5 sources give a 15-minute wait for every source, including a new one. When it ends, one attempt is allowed. If it fails while the hourly count is ≥ 20, another 15 minutes follow. Across all sources, a single wait is never longer than 15 minutes.
  - **SC-005 worst case**: an attacker who fails every allowed attempt from fresh sources gets exactly 23 evaluated failures in any rolling hour.
  - **Per source across keys**: at `SourceFailuresPerHour` (set to 20 in this test) failures over 20 keys, a 21st key from that source waits until the oldest failure is an hour old.
  - `Succeed` clears the (key, source) counter and the all-source wait. `Reset(key)` clears every source of the key.
  - `Forget(now)` drops entries whose last failure is over 1 h old.
  - Known and unknown addresses are the same kind of string key, so they behave identically.
  - An address key and an `account:{id}` key are independent: failures on one never make the other wait. SC-005 states its bound per key.
  - `SourceKey`:
    - `2001:db8:1:2:3:4:5:6` and `2001:db8:1:2:ffff::1` give the same key, and `2001:db8:1:3::1` a different one;
    - `192.0.2.1` stays as it is;
    - an IPv4-mapped IPv6 address maps to its IPv4 form;
    - `null` gives `"unknown"`.
- [X] T007 [P] Create `InTheKnow.Tests/AccountEmailTests.cs` (`FakeSmtp`, `ApiHost` configured for email the way `DeliveryTests.cs`'s email harness does, with rows queued through `Accounts.QueueAsync` in a transaction on `h.Db.DataSource`).
  - **SC-010**: every kind of contracts/account-emails.md renders in `en` and `nl`, with:
    - no missing `Texts` key;
    - no `List-Unsubscribe` or `List-Unsubscribe-Post` header;
    - `text/plain` then `text/html`;
    - the footer sentence of account-emails.md § Rules;
    - `args` values HTML-encoded. Queue `methodAdded` with `args.type = "<b>x</b>"`.

    Extend the existing `Texts` key-parity test so every `account.*` key exists in both languages.
  - **Tokens are minted at send** (R9):
    - before a pass, a queued `confirm` row has no `token_hash`;
    - after it, the row has `token_hash`, `token_expires_at = sent_at + 24 h` (`reset`: 1 h), and the captured email's link is `{FrontendBaseUrl}/account/confirm?token=<43 chars>`, whose SHA-256 equals the stored hash;
    - the pages are `confirm`, `reset`, `email` (for `emailChange`) and `invite`;
    - an `invite` row sets `invite.token_hash`, and its link uses `/account/invite`.
  - **Invalidation**: sending a second `confirm` for the same account sets `used_at` on the first row. An older unsent `confirm` row gets `expired_at` and is never sent.
  - **Recipients**: `emailChange` goes to its `to_address` (the new address), and `deleted` to the former address with `account_id` NULL. The `deleted` row is deleted right after it is sent.
  - **Disabled accounts**: with `account.disabled_at` set, `confirm`, `reset` and `passwordChanged` rows are marked `expired_at` and not sent. `disabled`, `enabled`, `roleChanged`, `twoFactorOffByAdmin` and `deleted` are sent (FR-033c).
  - **Failures**:
    - RCPT `450` keeps the row queued for the next pass;
    - after `RetryWindow` it gets `expired_at`;
    - RCPT `550` sets `expired_at` at once;
    - a refused connection backs the email type off as 003 does, and the row is sent on a later pass.
  - **Ordering**: with `MaxMessagesPerMinute = 1`, a queued account email, a queued 003 channel confirmation and a due digest all waiting, the account email is sent first.
  - **Security notices ignore channels**: with every channel of the account paused, `passwordChanged` is still sent.
  - **Retention** through `Accounts.CleanupAsync` (R9, R16; the cleanup of T015):
    - a sent notice is deleted 24 h after `sent_at`;
    - a used or expired token row, 24 h after use or expiry;
    - an expired unsent row, 24 h after `expired_at`;
    - a session idle for `SessionIdleLifetime` + 1 s and an awaiting session 10 min + 1 s old are deleted, while an active session and an awaiting one 9 min old are kept;
    - a `security_event` 90 days + 1 s old is deleted, also when its account no longer exists, and one 89 days old is kept (FR-035, SC-008).
- [X] T008 [P] Create `InTheKnow.Tests/TestSignIn.cs`, the test-only `test` method of contracts/sign-in-method.md.
  - `public static void Register(IServiceCollection s)` adds an `IStartupFilter`. Before `next(app)`, its `Configure` calls `app.Map("/api/test-auth", branch => { branch.UseRouting(); branch.UseAuthentication(); branch.UseAuthorization(); branch.UseEndpoints(…); })`. `Map` strips the `/api/test-auth` prefix, so even if the branch's endpoints appear in the shared `EndpointDataSource`, their patterns (`/sign-in`, …) do not start with `/api`, and the route enumerations of T021, T038 and T057, which filter by prefix, skip them.
  - `POST /sign-in {key}` (anonymous):
    - find `sign_in_method WHERE type = 'test' AND key = lower($key)`;
    - on a miss, `SignIn.FailAsync(lowerKey, http)` and `401 invalid_credentials`;
    - on a hit, first `SignIn.CheckAsync(lowerKey)` (`429 too_many_attempts` with `Retry-After`), then `return SignIn.CompleteAsync(accountId, methodId, http, throttleKey: lowerKey)`.
  - `POST /reauthenticate {key}` (`RequireAuthorization()`): the key must belong to a `test` method of the signed-in account, else `401 invalid_credentials`. Then `SignIn.ReauthenticatedAsync(sessionId, methodId)` → `204`. T048 adds the code branch.
  - `POST /add` is added in T056.
  - Every `ApiHost` in the suites that are theories over methods (`SignInTests`, `AccountTests`, `OwnershipTests`) passes `TestSignIn.Register` in its services callback.

### Implementation

- [X] T009 In `InTheKnow/Settings.cs`, add `AccountsOptions` and `BotCheckOptions` per the shared signatures, and `InTheKnowOptions.Accounts`.
  - Remove the `ApiKey` length requirement. When `ApiKey` is non-empty, `log.LogWarning("InTheKnow:ApiKey is retired and ignored")`.
  - Validate each rule of contracts/configuration.md § Settings with `Require`, with the message prefixed by the key, e.g. `InTheKnow:Accounts:SessionIdleLifetime must be between 1 hour and 365 days`.
  - Parse each `TrustedProxies` entry with `IPAddress.TryParse` or `IPNetwork.TryParse`. Keep the parsed lists on `Settings` as `IReadOnlyList<IPAddress> TrustedProxyAddresses` and `IReadOnlyList<IPNetwork> TrustedProxyNetworks`. When both are empty, warn.
  - In `InTheKnow/appsettings.json`, remove `"ApiKey"` and add the `Accounts` section of contracts/configuration.md § Shape, with its empty placeholders.
  - Makes T005 pass.
- [X] T010 [P] Create `InTheKnow/SecurityLog.cs` per the shared signature.
  - It inserts into `security_event (occurred_at, type, account_id, actor_id, source, detail)`. `source` is `inet` (Npgsql maps `IPAddress`), and `detail` is serialized with `JsonSerializer` as `jsonb`, defaulting to `{}`.
  - With `tx == null` it runs on `conn` alone.
  - The type strings are data-model.md § Security event types.
- [X] T011 [P] Create `InTheKnow/Throttle.cs` per the shared signature and research R8.
  - One `Lock`-guarded pair of dictionaries: `(key, source) → (count, lastFailure, waitUntil)`, and `key → (Queue<DateTimeOffset> failures, waitUntil)`. The `source → Queue<DateTimeOffset>` window uses `SourceFailuresPerHour`.
  - The per-(key, source) wait schedule after the 5th failure is `[30 s, 1 min, 2 min, 4 min, 8 min]`, then 15 min.
  - `SourceKey` maps IPv4-mapped addresses with `MapToIPv4`, and masks IPv6 to its first 8 bytes, formatted as `x:x:x:x::/64`.
  - Add `ponytail:` in-memory counters; the instance lock guarantees one process, so move them to PostgreSQL only if the app ever runs as several instances (research R8).
  - Makes T006 pass.
- [X] T012 [P] In `InTheKnow/Texts.cs`, add every account-email key in `en` and `nl` (contracts/account-emails.md, FR-025):
  - `account.{kind}.subject` and `account.{kind}.body` for every kind;
  - `account.{kind}.action`, the link button text, for `confirm`, `reset`, `emailChange` and `invite`;
  - the variants `account.roleChanged.body.admin` / `.body.user` and `account.invite.body.admin` / `.body.user`, chosen by `args.role` / `args.admin`;
  - `account.footer`, the "if this was not you" sentence of account-emails.md § Rules.

  `args` values are the `string.Format` arguments `{0}`: `remaining` for `recoveryCodeUsed`, and `type` for `methodAdded`/`methodRemoved`. Bodies of link kinds state the validity (24 hours, 1 hour, or the invite's expiry date via `Texts.Date`). Use the example subjects of account-emails.md § Rules verbatim. No body contains a password, a code, a key, another person's address or a network address.
- [X] T013 In `InTheKnow/ChannelType.cs` and `InTheKnow/EmailChannel.cs`, add `MessageKind.Account` and the three optional trailing `Message` parameters per the shared signatures. Every existing `new Message(…)` call compiles unchanged.
  - In `EmailChannel.Build`, `Account` renders:
    - the subject `account.{AccountKind}.subject`;
    - the body key with its variant, formatted with `Args`;
    - when `Link` is set, the link as a button in HTML and as a bare line in text, with the `action` text;
    - then `account.footer`.
  - Every inserted value is HTML-encoded in the HTML part, as 003 does. There are **no `List-Unsubscribe` headers** for `Account`.
- [X] T014 Create `InTheKnow/SignIn.cs` per the shared signatures, research R2, R3 and R6, and contracts/sign-in-method.md.
  - **`NewToken`/`Hash`**: 32 bytes from `RandomNumberGenerator`, `Base64Url.EncodeToString`, and `SHA256.HashData(Encoding.ASCII.GetBytes(token))`.
  - **`Device`**: browser tokens checked in the order `Edg/` → Edge, `OPR/` → Opera, `Firefox/` → Firefox, `Chrome/` → Chrome, `Safari/` → Safari. Systems: `Windows`, `Android`, `iPhone`/`iPad` → iOS, `Mac OS X` → macOS, `Linux`. The result is `"{browser} on {os}"`, or `"Unknown browser"` when neither is known, and is at most 60 characters. Add a `ponytail:` comment: two token tables; use a UA parser only if owners cannot recognise their devices.
  - **`CompleteAsync`**, in one transaction:
    - load `account` and `two_factor.enabled_at`;
    - refuse `403 not_confirmed` when `confirmed_at IS NULL`, and `403 account_disabled` when `disabled_at` is set;
    - `throttle.Succeed(throttleKey, source)` when a key is given;
    - set `sign_in_method.last_used_at = now`;
    - insert a `session` with `token_hash`, `device = Device(UserAgent)`, `awaiting_code = (2FA on AND NOT secondFactor)`, `second_factor = secondFactor`, and `created_at = last_used_at = now`;
    - write `signIn` with `detail {methodType}`;
    - return `Results.Json(new { token, secondFactorRequired })`.
  - **`FailAsync`**: `throttle.Fail(key, source)`, and write `signInFailed` with `account_id` = the given id or NULL. **`CheckAsync`**: `throttle.Wait(key, source)`. On a wait, write `throttled` once per wait.
  - **`ReauthenticatedAsync`**: `UPDATE session SET reauthenticated_at = now`, `UPDATE sign_in_method SET last_used_at = now`, and write `reauthenticated`.
  - The source is always `Throttle.SourceKey(http.Connection.RemoteIpAddress)`, after forwarded headers.
  - **`SessionAuthenticationHandler`**:
    - read `Authorization: Bearer <token>` (no header → `NoResult`);
    - one query joins `session` and `account` by `token_hash`;
    - fail when the session is missing, idle (`last_used_at + SessionIdleLifetime <= now`), of an unconfirmed or disabled account, or awaiting a code while the endpoint has no `AllowAwaitingCode` metadata (`Context.GetEndpoint()`) or was created over 10 minutes ago;
    - write `last_used_at = now` when it is at least 5 minutes old;
    - otherwise return a principal with the claims of `Claims`, and `ClaimTypes.Role` = the role.
  - **Authorization pieces**:
    - `ReasonRequirement` and one `AuthorizationHandler<ReasonRequirement>` that takes `TimeProvider` and calls `Holds(user, now)`;
    - an `IAuthorizationMiddlewareResultHandler` that maps `Challenged` to `401 not_signed_in` and `Forbidden` to `403` with the first failed `ReasonRequirement`'s `Reason`, and otherwise defers to `AuthorizationMiddlewareResultHandler`.
  - **Policies**, registered in T020:
    - **fallback**: an authenticated user without the `Awaiting` claim;
    - **`admin`**: authenticated, then `ReasonRequirement("admin_required", role == admin)`, then `ReasonRequirement("two_factor_required", SecondFactor claim)`;
    - **`stepUp`**: authenticated and not awaiting, then `ReasonRequirement("reauthentication_required", Reauthenticated claim within 10 min of now)`;
    - **`awaitingOrSignedIn`**: authenticated only, for sign-out and the code step.
- [X] T015 Create `InTheKnow/Accounts.cs` with `Account`, the constructor of the shared signature, `QueueAsync` and the first part of `CleanupAsync`.
  - `QueueAsync` inserts an `account_message` row with `requested_at = now` and `args` as `jsonb`.
  - `CleanupAsync(now)` (R16, R9) deletes:
    - sessions idle past `SessionIdleLifetime`, and awaiting sessions older than 10 minutes;
    - sent non-token rows 24 h after `sent_at`;
    - token rows 24 h after `used_at`, or after `token_expires_at` when unused;
    - expired unsent rows 24 h after `expired_at`;
    - security events older than 90 days.

    Then it calls `throttle.Forget(now)`. US1 and US8 add their parts.
- [X] T016 In `InTheKnow/Notifier.cs`, drain `account_message` and replace `subscriber_setting` (R9, R12).
  - **(a) The switch**: the joins on `subscriber_setting ss ON ss.subscriber = ch.subscriber` (the queued-message query ~line 157 and the digest query ~line 209) become `JOIN account a ON a.id = ch.subscriber`, reading `a.language` and `a.time_zone`.
  - **(b) Constructor and cleanup**: the constructor gains `Accounts accounts`. The cleanup pass calls `accounts.CleanupAsync(now, ct)` right after `channels.CleanupAsync`.
  - **(c) The drain**: in `RunTypeAsync`, when `type.Name == "email"`, before the `channel_message` loop, send `SendAccountAsync` for each id of `SELECT id FROM account_message WHERE sent_at IS NULL AND expired_at IS NULL ORDER BY requested_at LIMIT 100`. Each send takes the same rate slot (`s.NextFree`) as other messages. Rows older than `RetryWindow` get `expired_at = now`, next to the existing expiry in `ExpireAsync`.
  - **(d) `SendAccountAsync`**, in one transaction with the row `FOR UPDATE`:
    - Re-read the account. If `disabled_at` is set and the kind is not one of `disabled`, `enabled`, `deleted`, `roleChanged` or `twoFactorOffByAdmin`, set `expired_at` and stop.
    - For `confirm`, `reset` and `emailChange`:
      - mint a token with `SignIn.NewToken()`, and set `token_hash` and `token_expires_at` (24 h, `reset` 1 h);
      - set `used_at = now` on the account's earlier rows of that kind with an unused token;
      - set `expired_at = now` on its older unsent rows of that kind.
    - For `invite`, mint the token into `invite.token_hash`. The link expiry is `invite.expires_at`.
    - Build `new Message(MessageKind.Account, language, settings.DefaultTimeZone, null, null, null, null, [], 0, kind, link, args)`, where `link = {FrontendBaseUrl}/account/{confirm|reset|email|invite}?token={token}`, and send it to `to_address`.
    - Map the result:
      - `Sent` sets `sent_at`, or deletes the row for `deleted`;
      - `TargetRejected` sets `expired_at`;
      - `TargetFailed` leaves the row for the next pass;
      - `TypeUnavailable` rolls back and applies the email type's outage backoff exactly as the channel-message path does, without touching any channel row, and ends this type's pass.
  - Logs carry the row id, kind and reason code only.
  - Makes T007 pass once T012, T013, T015 and T020 are done.
- [X] T017 Switch owners from `string` to `long` in `InTheKnow/Subscriptions.cs`, `InTheKnow/Presets.cs`, `InTheKnow/Channels.cs` and `InTheKnow/Checker.cs` (R1).
  - Every `string subscriber` / `string? owner` parameter becomes `long` / `long?`, including `Channels.DeclinedHash(long subscriber, string address)`, which hashes `subscriber.ToString(CultureInfo.InvariantCulture)`.
  - Delete `Subscriptions.SubscriberPattern()` and every `invalid_subscriber` check.
  - `Channels.GetSettingsAsync`/`SetSettingsAsync` read and write `account.language` and `account.time_zone` by `id` instead of `subscriber_setting`, and never insert.
  - In `Checker.cs`, the regroup reader of `SELECT id, calendar_id, subscriber, url FROM calendar_link` (~line 375) reads `subscriber` with `GetInt64`.
  - Every SQL string that names `subscriber`/`owner` stays as it is.
- [X] T018 Move the 001–003 routes in `InTheKnow/Api.cs`, `InTheKnow/FilterApi.cs` and `InTheKnow/NotificationApi.cs` per contracts/api.md § Moved routes.
  - Delete the operator-key middleware, `HasKey`, and the `Valid(subscriber)` helpers.
  - User routes are mapped on `app.MapGroup("/api/me")` with the `{subscriber}` segment removed. Each delegate takes `ClaimsPrincipal user` and uses `user.AccountId()`.
  - `GET /api/calendars`, the global `GET/POST /api/presets` and `PUT/DELETE /api/presets/{id}`, and `GET /api/channel-types` move to `app.MapGroup("/api/admin").RequireAuthorization("admin")` as `/calendars`, `/presets…` and `/channel-types`.
  - `GET /api/me/channel-types` keeps its body.
  - 003's `/n/{action}/{token}` routes get `.AllowAnonymous()` (FR-030).
  - Bodies, responses and reasons are otherwise unchanged. Update the header comment of `Api.cs`: sessions replace the operator key (004).
- [X] T019 Create `InTheKnow/AccountApi.cs` with `public static void MapAccountApi(this WebApplication app)`, a `static int StatusFor(string reason)` table built from contracts/api.md, and the first route.
  - `POST /api/me/reauthenticate {password, code?}` (R6):
    - `CheckAsync("account:{id}")` → `429 too_many_attempts` with `Retry-After`;
    - load the account's `password` method; if there is none, or `hasher.VerifyHashedPassword` fails, `FailAsync("account:{id}", http, id)` and `401 invalid_credentials`;
    - on success, `SignIn.ReauthenticatedAsync(sessionId, methodId)` → `204`.
  - T048 adds `code`.
- [X] T020 Wire `InTheKnow/Program.cs`:
  - **Services**:
    - `IPasswordHasher<Account>` as `PasswordHasher<Account>`, with `Configure<PasswordHasherOptions>(o => { o.CompatibilityMode = IdentityV3; o.IterationCount = 210_000; })`;
    - singletons `Throttle`, `SignIn` and `Accounts`;
    - `AddAuthentication("session").AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>("session", null)`;
    - `AddAuthorization` with the fallback and named policies of T014, the `ReasonRequirement` handler, and the result handler;
    - `Configure<ForwardedHeadersOptions>` with `ForwardedHeaders = XForwardedFor` and `ForwardLimit = null`. Clear the default known proxies and networks, then add `Settings.TrustedProxyAddresses`/`TrustedProxyNetworks`. Use the non-obsolete `KnownIPNetworks` on .NET 10. Resolve `Settings` through an `IConfigureOptions` so overrides stay visible.
  - **Middleware**, in this order after `Build()`: `UseForwardedHeaders()`, `UseAuthentication()`, `UseAuthorization()`. T037 adds `UseRateLimiter()` after them.
  - **Routes**: `app.MapAccountApi()` next to the existing `Map*` calls.
- [X] T021 Finish `InTheKnow.Tests/TestHost.cs` (`SignInAsync` per the shared signature), change `ApiHost`, and rewrite `InTheKnow.Tests/ApiTests.cs`.
  - **`SignInAsync`**:
    - insert a confirmed account, with role `admin` when asked;
    - a method: `password` hashed with `TestHost.Password`, or `test` with `key = lower(email)`;
    - when `secondFactor`, a `two_factor` row with `enabled_at`;
    - a session with `second_factor = secondFactor` and a token from `SignIn.NewToken()`;
    - return a new `h.Factory.CreateClient()` carrying `Authorization: Bearer <token>`.

    It creates no address channel.
  - **`ApiHost`**:
    - remove `UseSetting("InTheKnow:ApiKey", …)` and the default Authorization header;
    - add `s.Configure<PasswordHasherOptions>(o => o.IterationCount = 1)` before `services?.Invoke(s)`, so a test can override it;
    - add an `IStartupFilter` whose middleware runs before `next` and sets `http.Connection.RemoteIpAddress = IPAddress.Parse(value)` when the test-only header `X-Test-Source` is present. `TestServer` sets no peer address, so this is how every test chooses its source. Without the header the address stays null (source `unknown`);
    - change `AddAsync(string subscriber, …)` to `AddAsync(SignedIn who, string url, string fixture = "mt-nl.ics")`, posting `/api/me/links` with `who.Client`.
  - **Rewrite `ApiTests`**:
    - Replace every subscriber string with a `SignedIn`, and every `/api/subscribers/{s}/` with `/api/me/`.
    - The operator routes move to `/api/admin/…`, called with `SignInAsync(admin: true, secondFactor: true)`.
    - Delete the `invalid_subscriber` cases. Assertions are otherwise unchanged.
    - Replace `EveryRouteNeedsTheKey` with `EveryApiRouteNeedsASession`. It enumerates `h.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()` whose pattern starts with `/api`, minus the anonymous allowlist of contracts/api.md § Public routes. For each, with an invalid JSON body, it sends:
      - no header;
      - a random 43-character token;
      - `Bearer {TestDb.ApiKey}`;
      - the token of a session that was deleted.

      Each gets `401` with `reason = not_signed_in`. The list is non-empty, and it contains `/api/me/links` and `/api/admin/calendars`.
  - Keep `TestDb.ApiKey` only as the retired key these tests send.
- [X] T022 [P] Rewrite `InTheKnow.Tests/NotificationApiTests.cs` (46 uses) to signed-in accounts and `/api/me/…`, and the operator channel-types test to `/api/admin/channel-types` with an admin client. `notification-settings` reads and writes the account row. Assertions unchanged. Delete the `invalid_subscriber` cases.
- [X] T023 [P] Rewrite `InTheKnow.Tests/PresetsTests.cs` (8 uses): owners become `TestHost.AccountAsync` ids, and global presets keep `null`. Assertions unchanged.
- [X] T024 [P] Rewrite `InTheKnow.Tests/ChannelsTests.cs` (6 uses) to `TestHost.AccountAsync` ids. Settings tests read `account.language`/`time_zone`. Assertions unchanged.
- [X] T025 [P] Rewrite `InTheKnow.Tests/DeliveryTests.cs` (37 uses).
  - `SubscribeAsync`, `AddActiveChannelAsync` and the other helpers take a `SignedIn` (app) or an account id (SQL).
  - `AddActiveChannelAsync` inserts `channel` with the `bigint` subscriber.
  - Assertions unchanged. Both theory types still build identical `Message`s.
- [X] T026 [P] Rewrite `InTheKnow.Tests/CheckerTests.cs` (11 uses): links, filters, settings and channels inserted with account ids from `TestHost.AccountAsync`. The regroup tests still copy filters and settings.
- [X] T027 [P] Rewrite `InTheKnow.Tests/PublicPagesTests.cs` (6 uses): channels belong to an account. Add that every `/n/` page works with **no** Authorization header (FR-030).
- [X] T028 [P] Rewrite `InTheKnow.Tests/SubscriptionsTests.cs` (6 uses) to account ids, and delete its `invalid_subscriber` case.
- [X] T029 [P] Rewrite the one subscriber use in `InTheKnow.Tests/EmailChannelTests.cs` to an account id.
- [X] T030 Run `dotnet build && dotnet test` from `backend/`. All pre-existing tests and T004–T008 pass.

**Checkpoint**: the suite is green again. Every 001–003 route answers only to a session. Admin routes need `admin` plus a second factor. The operator key opens nothing. Account emails queue, render in both languages, and send.

---

## Phase 3: User Story 1 - Create an account and sign in with email and password (Priority: P1) 🎯 MVP (with US2)

**Goal**: sign up, confirm with the link and the password, sign in, sign out, with no account enumeration and throttled guessing.

**Independent Test**: set `signup_settings.open = true`, sign up, confirm through the emailed token with the password, sign in, call `GET /api/me`, sign out, and see the same call refused.

### Tests for User Story 1

- [X] T031 [P] [US1] Create `InTheKnow.Tests/SignUpTests.cs` (`ApiHost` with `FakeSmtp` email, as T007). Every test sets `UPDATE signup_settings SET open = true` unless stated otherwise.
  - **US1-1**: `POST /api/auth/sign-up {email, password, language: "nl"}` → `202`, empty body. An unconfirmed account exists with language `nl` and `time_zone` NULL. After a notifier pass, a Dutch `confirm` email is captured.
  - **US1-2**:
    - `GET` of the link's frontend URL is not an API route, and the account stays unconfirmed;
    - `POST /api/auth/confirm {token, password: wrong}` → `400 password_incorrect`, still unconfirmed, and the token is still usable;
    - with the right password → `204` and `confirmed_at` set, with a `accountConfirmed` security event;
    - the same token again → `400 token_invalid`;
    - a token 24 h + 1 s after sending → `token_invalid`.
  - **US1-3**: signing in to the unconfirmed account → `403 not_confirmed` with the right password only. A wrong one gets `401 invalid_credentials`.
  - **FR-004**: `POST /api/auth/confirmation {email}` → `202` for known, unknown and confirmed addresses alike. Only the unconfirmed one queues a `confirm`. The sign-up email counts toward the cap of 3: the 2nd request within 24 h of sign-up still queues, and the 3rd queues nothing and still answers `202`. After a new link is sent, the older one is `token_invalid`.
  - **US1-7 / SC-003**: sign-up with the address of a confirmed account → a `202` identical in status, headers (minus `Date`) and body to a new address's. No second account. The owner gets `signUpAttempt`. The 4th such notice within 24 h is not queued, and the response is the same.
  - **FR-006, unconfirmed**: sign-up with an unconfirmed account's address (other case) replaces its password method's `secret` in place, with the same row id. It queues a new `confirm`, and once that is sent the older link is `token_invalid`.
  - **FR-006, disabled**: sign-up for a disabled account's address → the same `202`, and nothing is queued or changed.
  - **FR-007**: each refusal gives `400` with its reason:
    - 11 characters or 129 → `password_length`;
    - a password taken from `common-passwords.txt` → `password_common`;
    - the address in other case → `password_is_email`;
    - `language: "de"` → `invalid_language`;
    - `a`, `Name <a@b.nl>`, or a 255-character address → `invalid_email`.

    A 12-character password with spaces is accepted.
  - **FR-002**: `Student@Example.nl` and `student@example.nl` are one account, and the address is stored as first entered, trimmed.
  - **FR-001a/b, without invites**: with `open = false` and `allowed_domains = '{example.nl}'`:
    - `x@example.nl` → `202`;
    - `x@gmail.com` → `403 sign_up_by_invitation`, also when `x@gmail.com` already has an account;
    - `x@student.example.nl` → `403`, since domains match exactly.

    With `open = false` and no domains, everything is `403`. Invites and the SC-012 matrix are in T065.
  - **FR-005**: an unconfirmed account 30 days + 1 s old is deleted by `Accounts.CleanupAsync`, and one 29 days old is kept.
- [X] T032 [P] [US1] Create `InTheKnow.Tests/SignInTests.cs`, with its tests as xunit theories over `"password"` and `"test"` (SC-006), and add `SignInViaAsync` to `InTheKnow.Tests/TestHost.cs`. For `password` it posts `/api/auth/sign-in`, and for `test` it posts `/api/test-auth/sign-in`. Accounts are created with `TestHost.SignInAsync(method: m)`, and the direct session is discarded.
  - Signing in returns `200 {token, secondFactorRequired: false}` with a 43-character token, and `GET /api/me` with it returns the account.
  - **US1-6**: `POST /api/auth/sign-out` → `204`, and the token then gets `401 not_signed_in`.
  - **Two devices**: two sign-ins give two working sessions, and signing out of one leaves the other.
  - **Sign out everywhere**: ends every session, this one included.
  - **Idle expiry**: after `SessionIdleLifetime` + 1 s on the fake clock → `401`. A request 4 minutes after the last write leaves `last_used_at` unchanged, and one 5 minutes after updates it.
  - **Device**: a session created with `User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) … Firefox/130.0` stores `Firefox on Windows`. A unit case covers each browser and system token of research R3, and garbage → `Unknown browser`.
  - **US1-5 / SC-003** (`password` only):
    - an unknown address, a wrong password and an 8-character password give identical `401` status and body, comparing the JSON with `traceId` removed if the Problem Details writer adds one;
    - the unknown-address path calls the hasher, asserted by registering a counting `IPasswordHasher<Account>` wrapper through the services callback;
    - an account without a password method (only `test`) gets the same `401`.
  - **Throttling through the route**: 5 wrong passwords for one address, then the 6th → `429 too_many_attempts` with `Retry-After: 30`, the same for an unknown address. A correct password from another source still signs in. Sources are chosen with the `X-Test-Source` header of T021.
  - **Rehash**: an account whose hash was made at a lower iteration count signs in. Its `sign_in_method.secret` changes but the row `id` stays, so sessions of that method survive (data-model § Cross-table rules).
  - **Security events**: `signIn` and `signInFailed` rows are written with `source`. No row's `detail` contains the address.

### Implementation for User Story 1

- [X] T033 [P] [US1] Create `InTheKnow/Passwords.cs` (research R4, FR-007):
  - `static string? Check(string? password, string email)` returns `password_length` unless `password.Length is >= 12 and <= 128`, `password_common` when `Common.Contains(password.ToLowerInvariant())`, `password_is_email` when it equals `email.Trim()` ignoring case, and null otherwise;
  - `Common` is a `FrozenSet<string>` loaded once from the embedded `Resources/common-passwords.txt`, skipping `#` lines.
- [X] T034 [US1] In `InTheKnow/Accounts.cs`, add the sign-up and confirmation flows (FR-001 to FR-006, research R10):
  - **`static string? NormalizeEmail(string? s)`**: trim, at most 254 characters, `MailAddress.TryCreate` succeeds and its `Address` equals the input (no display name), else null.
  - **`SignUpAsync(email, password, language, http)`**, in this order: `invalid_email`, `invalid_language` (`en`/`nl`), `Passwords.Check`, then the settings rule. The settings rule allows the address when `signup_settings.open`, or when the part after the last `@`, lowercased, is in `allowed_domains`; otherwise it returns `sign_up_by_invitation`, decided without reading `account`. Then:
    - `hasher.HashPassword` always, even when the result is discarded (SC-003);
    - lock `SELECT … FROM account WHERE lower(email) = lower($1) FOR UPDATE`;
    - **none**: insert the account (`language`, `created_at = now`), the `password` method and a `confirm` message. On a unique violation (a race), retry the lookup once;
    - **confirmed and not disabled**: queue `signUpAttempt` unless 3 rows of that kind for `lower(to_address)` exist with `requested_at > now − 24 h`;
    - **disabled**: nothing;
    - **unconfirmed**: `UPDATE sign_in_method SET secret = $hash WHERE account_id = $a AND type = 'password'`, then queue `confirm` unless 3 `confirm` rows exist for the account in 24 h.

    It returns null (→ `202`) in every non-refused case.
  - **`RequestConfirmationAsync(email)`**: an unconfirmed, not-disabled account under its cap gets a `confirm` queued. Always null (→ `202`).
  - **`ConfirmAsync(token, password, http)`**:
    - find the unused, unexpired `confirm` row by `SignIn.Hash(token)` whose account is unconfirmed and not disabled, else `token_invalid`;
    - `signIn.CheckAsync("account:{id}")` (→ `429`);
    - verify the password (wrong → `FailAsync` and `password_incorrect`, the token untouched);
    - then in one transaction: `UPDATE account_message SET used_at = $now WHERE id = $id AND used_at IS NULL` (0 rows → `token_invalid`), set `confirmed_at = now`, write `accountConfirmed`, and call `OnConfirmedAsync(conn, tx, account)`. That hook is a no-op here and gains the address channel in T040.
  - **`CleanupAsync`**: also delete unconfirmed accounts with `created_at < now − 30 days` (FR-005).
- [X] T035 [US1] In `InTheKnow/SignIn.cs`, add `PasswordAsync(string? email, string? password, HttpContext http)` (FR-011, R4, R8):
  - the key is `email.Trim().ToLowerInvariant()`;
  - a wait from `CheckAsync` → `429 too_many_attempts` with `Retry-After` in whole seconds, rounded up;
  - look up the account and its `password` method;
  - when either is missing, or the password is outside 12–128 characters, verify against a dummy hash made once at startup with the same options, `FailAsync(key)`, and return `401 invalid_credentials`;
  - `Failed` → `FailAsync(key, http, accountId)` and the same `401`;
  - `SuccessRehashNeeded` → `UPDATE sign_in_method SET secret = $new WHERE id = $id`, in place;
  - then `return CompleteAsync(accountId, methodId, http, throttleKey: key)`.

  `SignIn`'s constructor gains `IPasswordHasher<Account>`.
- [X] T036 [US1] In `InTheKnow/AccountApi.cs`, add the routes of contracts/api.md:
  - `POST /api/auth/sign-up`, `POST /api/auth/confirmation`, `POST /api/auth/confirm` and `POST /api/auth/sign-in`, each `.AllowAnonymous()`;
  - `POST /api/auth/sign-out`, which deletes this session → `204`, with `.RequireAuthorization("awaitingOrSignedIn").WithMetadata(new AllowAwaitingCode())`;
  - `POST /api/auth/sign-out-everywhere`, which deletes every session of the account → `204` and writes `signedOutEverywhere`;
  - `GET /api/me` with the exact JSON of api.md:
    - `twoFactor` = `two_factor.enabled_at IS NOT NULL`;
    - `recoveryCodesLeft` = the count of unused codes;
    - `hasPassword`;
    - `pendingEmail` = the `to_address` of the newest `emailChange` row that is sent, unused and unexpired;
    - `createdAt`.

  Bodies bind as nullable DTO records, so a missing field gives the field's reason rather than a framework `400`. Sign-out writes `signedOut`.
- [X] T037 [US1] In `InTheKnow/Program.cs`, add the per-source request limiter (FR-012a, R8).
  - `AddRateLimiter` with a policy `"public"`: `PartitionedRateLimiter` on `Throttle.SourceKey(RemoteIpAddress)`, with a `SlidingWindowLimiter` of `SourceRequestsPerHour` per hour in 6 segments and no queue.
  - `OnRejected` writes `429` Problem Details with `reason = too_many_requests` and `Retry-After` from the lease's `RetryAfter` metadata, or 600 s when absent.
  - Add `app.UseRateLimiter()` after `UseAuthorization()`.
  - In `AccountApi.cs`, add `.RequireRateLimiting("public")` to sign-up, confirmation and confirm. Later tasks add it to reset, reset completion, email-change confirmation and invite acceptance.
  - Add to `SignUpTests` one case with `SourceRequestsPerHour = 10`: the 11th sign-up from one `X-Test-Source` (T021) gets `429 too_many_requests`, and another source is unaffected.

**Checkpoint**: US1's acceptance scenarios pass. Accounts can be created and used, but US2's proof is still missing: do not open the API to students yet.

---

## Phase 4: User Story 2 - My calendars, filters and channels are mine (Priority: P1)

**Goal**: prove that every user route acts only for the signed-in account (SC-002), and give each confirmed account its own address as an active channel (FR-028).

**Independent Test**: accounts A and B. As A, add a calendar, a personal preset and a channel. As B, every user route with A's ids gets exactly the not-found response, and B's lists stay empty.

### Tests for User Story 2

- [X] T038 [P] [US2] Create `InTheKnow.Tests/OwnershipTests.cs`, a theory over `"password"` and `"test"` (SC-002, SC-006). Sign A and B in with `SignInViaAsync`.
  - **Enumeration**: list every `RouteEndpoint` whose pattern starts with `/api/me` from `EndpointDataSource`. Keep a hand-written table of each route's request, with `{calendarId}`, `{id}` and `{channelId}` filled from A's items (a calendar, a personal preset, a channel, and a session id for `/api/me/sessions/{id}`). **Fail if a route has no table entry**, so a new route cannot escape.
  - **As B**:
    - every call with A's ids gets the same status and body as the same call with id `999999999`;
    - A's rows are unchanged afterwards: compare a snapshot of `calendar_link`, `subscription_filter`, `preset`, `channel`, `subscription_setting` and `session` rows for A;
    - B's `GET` lists (calendars, changes, presets, channels) are empty.
  - **US2-3**: no session, a session idle past its lifetime, and an awaiting-code session each get `401 not_signed_in` on every enumerated route. For the awaiting-code session, insert `awaiting_code = true` directly. An unconfirmed account cannot hold a session, because `CompleteAsync` refuses `not_confirmed`, as asserted in T031.
  - **US2-1**: A's link, filter, preset and channel rows have `subscriber`/`owner` = A's id.
  - **US2-4**: an account confirmed through `POST /api/auth/confirm` lists exactly one channel: `type = email`, its address, `status = active`, `accountAddress: true`. The other confirmation paths are covered in T041 (reset) and T067 (invite).
  - **US2-5**: `GET /api/me/notification-settings` of a new account shows the sign-up language and the default time zone as the effective one. `PUT` changes `account.language` and `account.time_zone`.
  - **FR-028**: the address channel counts toward the limit, so 4 more channels are allowed and the 6th is `409 channel_limit`.

### Implementation for User Story 2

- [X] T039 [US2] In `InTheKnow/Channels.cs`, add `static Task AddAccountAddressAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, string email, DateTimeOffset now, CancellationToken ct)` (R15). It inserts `channel (subscriber, type, target, status, status_since, activated_at, unsubscribe_token, created_at, is_account_address)` as `email`, the trimmed address, `active`, `now`, `now`, a new 43-character token (the existing token helper) and `true`. Add `bool AccountAddress` to `ChannelRow`, read it in every channel query, and emit `"accountAddress"` in the `Channel` JSON in `InTheKnow/NotificationApi.cs`.
- [X] T040 [US2] In `InTheKnow/Accounts.cs`, make `OnConfirmedAsync` call `Channels.AddAccountAddressAsync(conn, tx, account.Id, account.Email, now, ct)`. Every confirmation path (T034, T042, T068) goes through `OnConfirmedAsync`. Makes T038 pass.

**Checkpoint**: US1 + US2 are the first deployable increment. Every user route is proven to be scoped to its account.

---

## Phase 5: User Story 3 - Recover a forgotten password (Priority: P2)

**Goal**: a reset link by email sets a new password, ends every session, confirms an unconfirmed account and never bypasses 2FA.

**Independent Test**: request a reset for a confirmed account, complete it from the emailed token, then check that the old password and every session fail and the new password signs in.

### Tests for User Story 3

- [X] T041 [P] [US3] Add a reset section to `InTheKnow.Tests/SignInTests.cs` (`password` theory rows, plus a `test`-only account for FR-009's "adds one"):
  - **US3-1**: `POST /api/auth/password-reset {email}` → `202` for known, unknown and disabled addresses alike, with identical bodies. Only the known, not-disabled account queues `reset`. The 4th request in 24 h queues nothing and still answers `202` (FR-012a).
  - **US3-2**: `POST /api/auth/password-reset/complete {token, password}` → `204`:
    - the old password → `401`;
    - the new one signs in;
    - every earlier session → `401` (SC-009);
    - a `passwordChanged` email is queued;
    - the throttle for the address is cleared: 5 failures, a reset, then a 6th failure is free. The `account:{id}` wait is cleared too, asserted through the `Throttle` singleton's `Wait`;
    - a `passwordReset` event is written.
  - **US3-3**: the same token again, a token 1 h + 1 s old, and the older of two sent reset tokens → `400 token_invalid`, with the password unchanged.
  - Password rules on completion → `400 password_length` / `password_common` / `password_is_email`.
  - **US3-5**: completing a reset for an unconfirmed account confirms it and creates its address channel.
  - **FR-009**: completing a reset for an account with only a `test` method adds a `password` method, and it then signs in with email and password.
  - The rehashed or reset `secret` is updated in place: the method row id is unchanged.

### Implementation for User Story 3

- [X] T042 [US3] In `InTheKnow/Accounts.cs`, add the reset flow (FR-009, FR-010):
  - `RequestResetAsync(email)` queues `reset` for a not-disabled account under its cap of 3 per 24 h. Always null.
  - `CompleteResetAsync(token, password, http)`:
    - `Passwords.Check(password, account.Email)`;
    - find the unused, unexpired `reset` row by hash, and set `used_at` with the single-use `UPDATE` (0 rows → `token_invalid`);
    - update the `password` method's `secret` in place, or insert one when the account has none (→ `methodAdded` message and event);
    - when unconfirmed, set `confirmed_at` and call `OnConfirmedAsync`;
    - `DELETE FROM session WHERE account_id = $a`;
    - `throttle.Reset(lower(email))` and `throttle.Reset($"account:{id}")`;
    - queue `passwordChanged` and write `passwordReset`.

    `two_factor` is never touched.
- [X] T043 [US3] In `InTheKnow/AccountApi.cs`, add `POST /api/auth/password-reset` and `POST /api/auth/password-reset/complete`, both `.AllowAnonymous().RequireRateLimiting("public")`. Makes T041 pass.

**Checkpoint**: a forgotten password no longer means a lost account.

---

## Phase 6: User Story 4 - Protect the account with an authenticator app (Priority: P2)

**Goal**: TOTP 2FA with recovery codes, a two-step sign-in, and step-up that includes a code.

**Independent Test**: turn 2FA on, sign out. The password alone does not sign in, the password plus a current code does, the same code twice is refused, and a recovery code works exactly once.

### Tests for User Story 4

- [X] T044 [P] [US4] Create `InTheKnow.Tests/TwoFactorTests.cs` (`ApiHost`, fake clock, codes computed with Otp.NET's `Totp` from the returned secret at `h.Time`).
  - **US4-1**:
    - `POST /api/me/2fa/setup` → `200 {secret, uri}`. The `uri` matches `^otpauth://totp/InTheKnow:.+\?secret=[A-Z2-7]+&issuer=InTheKnow&algorithm=SHA1&digits=6&period=30$`, and the secret decodes to 20 bytes;
    - `GET /api/me` still shows `twoFactor: false`;
    - a second setup replaces the unconfirmed secret.
  - **US4-2**: `POST /api/me/2fa/enable {code}`:
    - a wrong code → `400 code_invalid`;
    - a valid one → `200` with 10 distinct codes matching `^[A-Z2-7]{4}(-[A-Z2-7]{4}){3}$`, `twoFactor: true`, a `twoFactorOn` email queued and a `twoFactorOn` event;
    - setup or enable again → `409 two_factor_on`;
    - enable before setup → `409 setup_not_started`.
  - **RFC 6238**: `TwoFactor.VerifyAsync` accepts the RFC 6238 SHA-1 test vectors for secret `12345678901234567890` at T = 59, 1111111109 and 1111111111, truncated to 6 digits. Otp.NET supplies the digits. Set `last_step = 0` between cases.
  - **FR-017**:
    - codes of steps −1, 0 and +1 are accepted;
    - −2 and +2 are refused;
    - after step s is accepted, a code of step s or s−1 is refused;
    - two concurrent submissions of the same valid code: exactly one succeeds.
  - **US4-3 / SC-004**: with 2FA on, `POST /api/auth/sign-in` → `{token, secondFactorRequired: true}`. With that token:
    - `GET /api/me` → `401 not_signed_in`;
    - `POST /api/auth/sign-in/code {code}` → `200 {secondFactorRequired: false}`, and the same token now works, with `session.second_factor = true`.

    Right after a completed password reset, the password again gives only an awaiting session.
  - **Awaiting session**: expires 10 min after creation (the code step → `401`). `POST /api/auth/sign-out` works with it. Code failures give `400 code_invalid`, keep the awaiting session, and are throttled on `account:{id}`: the 6th failure → `429`.
  - **US4-4**: a code accepted at sign-in is refused at the next sign-in's code step within the same step.
  - **US4-5**:
    - a recovery code, given with or without hyphens and in any case, signs in once and is refused the second time;
    - `recoveryCodeUsed` is queued with `args.remaining = 9`;
    - a `recoveryCodeUsed` event is written;
    - `GET /api/me` shows `recoveryCodesLeft: 9`.
  - **US4-6**:
    - `DELETE /api/me/2fa` without step-up → `403 reauthentication_required`;
    - `POST /api/me/reauthenticate {password}` with 2FA on → `400 code_required`, and with a wrong code → `400 code_invalid`;
    - with password + code, then `DELETE /api/me/2fa` → `204`: `two_factor` and recovery codes are gone, other sessions → `401`, this one works, and `twoFactorOff` is queued;
    - with 2FA off → `409 two_factor_off`.
  - **Enable clears step-up**: reauthenticate with the password while 2FA is off, enable 2FA, then `DELETE /api/me/2fa` → `403 reauthentication_required`. This proves the confirmation included a code (FR-018).
  - **US4-7**: `POST /api/me/2fa/recovery-codes {code}` → 10 new codes, and every old code is refused. A wrong code → `400 code_invalid`. With 2FA off → `409 two_factor_off`.
  - Extend the route table of `InTheKnow.Tests/OwnershipTests.cs` (T038) with this phase's `/api/me/2fa/…` routes, so its enumeration stays complete.
  - **`test` method**: the same US4-3 and US4-6 flows pass for an account signed in through `/api/test-auth/sign-in`, whose step-up is `/api/test-auth/reauthenticate {key, code}`.

### Implementation for User Story 4

- [X] T045 [P] [US4] Create `InTheKnow/TwoFactor.cs` per the shared signature (research R5):
  - `NewSecret` returns `RandomNumberGenerator.GetBytes(20)`;
  - `Uri` uses `OtpNet.Base32Encoding.ToString(secret)` and `Uri.EscapeDataString(address)`;
  - `NewRecoveryCodes` builds each code from 10 random bytes as base32 (16 characters), formatted in 4 groups;
  - `VerifyAsync`:
    - a 6-digit code: `new Totp(secret).VerifyTotp(now.UtcDateTime, code, out var step, new VerificationWindow(1, 1))`, then `UPDATE two_factor SET last_step = $step WHERE account_id = $a AND last_step < $step AND enabled_at IS NOT NULL`. Ok only if 1 row changed;
    - any other code: `UPDATE recovery_code SET used_at = $now WHERE account_id = $a AND code_hash = $h AND used_at IS NULL`. Ok if 1 row, and then count the unused codes left.

    Enabling uses the same TOTP check without the `enabled_at` condition.
  - Add `ponytail:` plain key storage; encrypt with Data Protection if database backups leave the server (research R5).
- [X] T046 [US4] In `InTheKnow/Accounts.cs`, add the 2FA actions:
  - **`StartTwoFactorAsync(accountId)`**: `two_factor_on` when enabled. Otherwise upsert `two_factor (secret = NewSecret(), enabled_at = NULL, last_step = 0)`, and return the base32 secret and `TwoFactor.Uri(email, secret)`.
  - **`EnableTwoFactorAsync(accountId, sessionId, code)`**: `setup_not_started` / `two_factor_on`, and `code_invalid` when the TOTP fails. Then, in one transaction:
    - set `enabled_at` and `last_step`;
    - insert 10 recovery-code hashes;
    - `UPDATE session SET second_factor = true, reauthenticated_at = NULL WHERE id = $sessionId` (US7-11; the step-up clear per T044);
    - queue `twoFactorOn` and write `twoFactorOn`.

    Return the codes.
  - **`DisableTwoFactorAsync(accountId, sessionId)`**: `two_factor_off`, and `demote_first` when the account is an admin (US7-10). Delete `two_factor`, which cascades the codes, delete the other sessions, queue `twoFactorOff`, and write `twoFactorOff`.
  - **`RenewRecoveryCodesAsync(accountId, code)`**: `two_factor_off`, `code_invalid`, then replace all codes in one transaction and write `recoveryCodesRenewed`.
- [X] T047 [US4] In `InTheKnow/SignIn.cs`, add `CodeAsync(long sessionId, long accountId, string? code, HttpContext http)` for the code step:
  - `CheckAsync("account:{id}")` → `429`;
  - `TwoFactor.VerifyAsync` fails → `FailAsync("account:{id}", http, id)`, a `codeFailed` event and `400 code_invalid`;
  - success → `UPDATE session SET awaiting_code = false, second_factor = true`, `throttle.Succeed`, and a `signIn` event with `detail {secondFactor: true}`. A recovery code also queues `recoveryCodeUsed {remaining}` and writes `recoveryCodeUsed`.

  It returns `200 {secondFactorRequired: false}`.
- [X] T048 [US4] In `InTheKnow/AccountApi.cs`, add the 2FA routes and the code step, and extend both re-authentication routes.
  - `POST /api/auth/sign-in/code`: `.RequireAuthorization("awaitingOrSignedIn").WithMetadata(new AllowAwaitingCode())`. A session that is not awaiting gets `400 code_invalid`.
  - `POST /api/me/2fa/setup`, `POST /api/me/2fa/enable`, `DELETE /api/me/2fa` (`.RequireAuthorization("stepUp")`) and `POST /api/me/2fa/recovery-codes`.
  - In `POST /api/me/reauthenticate`, after the password is verified and 2FA is on:
    - a missing `code` → `400 code_required`, without counting a failure;
    - a failing code → `FailAsync` and `400 code_invalid`.
  - Apply the same code branch to `/api/test-auth/reauthenticate {key, code?}` in `InTheKnow.Tests/TestSignIn.cs`.
  - Makes T044 pass.

**Checkpoint**: 2FA works for both methods, and admins can now meet FR-032a.

---

## Phase 7: User Story 5 - Manage my account (Priority: P2)

**Goal**: change the password or email address, see and end sessions, and delete the account with everything it owns.

**Independent Test**: change the password, change the email and confirm it, end another session, sign out everywhere, and delete the account. After each step, check sessions, emails and stored rows.

### Tests for User Story 5

- [X] T049 [P] [US5] Create `InTheKnow.Tests/AccountTests.cs`, a theory over `"password"` and `"test"` (SC-006). A `Reauth(signedIn)` helper posts `/api/me/reauthenticate {password}` or `/api/test-auth/reauthenticate {key}`.
  - Extend the route table of `InTheKnow.Tests/OwnershipTests.cs` (T038) with `PUT /api/me/password`, `POST /api/me/email`, `GET /api/me/sessions`, `DELETE /api/me/sessions/{id}` and `DELETE /api/me`.
  - **Step-up (R6)**:
    - each ⬆ route that exists by now (`PUT /api/me/password`, `POST /api/me/email`, `DELETE /api/me`, `DELETE /api/me/2fa`) without step-up → `403 reauthentication_required`;
    - with it → allowed;
    - 10 min + 1 s later → refused again;
    - a step-up in session 1 does not count in session 2;
    - a wrong password → `401 invalid_credentials`, and the 6th → `429`.
  - **US5-1 / SC-009** (`password`):
    - `PUT /api/me/password {password}` → `204`: the new password signs in, the old does not, other sessions → `401`, and this session still works (FR-013);
    - `passwordChanged` is queued and a `passwordChanged` event is written;
    - the method row id is unchanged;
    - the rule reasons are `400`.
  - **US5-2**: `POST /api/me/email {email}` → `202`:
    - `emailChange` is queued to the new address and `emailChangeNotice` to the old;
    - `account.email` is unchanged;
    - after a send, `GET /api/me` shows `pendingEmail`;
    - `400 invalid_email`, and `400 same_email` for the current address in other case.
  - **US5-3**:
    - `POST /api/auth/email-change/confirm {token}` → `204`, and `account.email` is the new address;
    - the `accountAddress` channel's target is the new address;
    - a separate existing channel for the new address was deleted first;
    - other channels are untouched;
    - `emailChanged` is written;
    - the old address gets nothing more.
  - **Token validity**: 24 h + 1 s → `400 token_invalid`, and the old address is kept. A second request supersedes the first once sent: the first token is `token_invalid`.
  - **FR-022**: a request for an address taken by another account → `202`, nothing queued, nothing changed. Another account taking the address between request and confirmation → `409 address_taken`.
  - If the owner deleted the flagged channel earlier, confirming moves nothing and succeeds (R15).
  - **US5-4**: sign out everywhere, from T036, ends this session too.
  - **US5-6**:
    - `GET /api/me/sessions` lists `{id, device, createdAt, lastUsedAt, current}` for the active sessions only, with exactly one `current: true`;
    - awaiting sessions are not listed;
    - no value equals or contains a token.
  - **US5-7**:
    - `DELETE /api/me/sessions/{other}` → `204`, and that token → `401`;
    - the current session's id → `409 use_sign_out`;
    - another account's session id → `404`, with the same body as `999999999`.
  - **US5-5 / SC-008**, deletion. Set up an account with:
    - a calendar shared with account B, and one only its own;
    - a filter with its own personal preset, and a channel with deliveries;
    - a declined address, 2FA with codes, two sessions, and a pending `account_message`.

    `DELETE /api/me` with step-up → `204`. Afterwards:
    - no row in any table has `subscriber`, `owner`, `account_id` or `created_by` equal to its id, except `security_event`. Enumerate the columns from `information_schema.columns`, so a new table cannot escape;
    - no `text` column of any table contains its address, except one unsent `deleted` row. After a notifier pass that row is sent and gone;
    - the own calendar is gone, and the shared calendar and its history stay for B;
    - an `accountDeleted` event remains.

### Implementation for User Story 5

- [X] T050 [P] [US5] In `InTheKnow/Channels.cs`, add `static Task MoveAccountAddressAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, string newEmail, CancellationToken ct)` (R15, FR-023):
  - `DELETE FROM channel WHERE subscriber = $a AND NOT is_account_address AND type = 'email' AND lower(target) = lower($new)`;
  - then `UPDATE channel SET target = $new WHERE subscriber = $a AND is_account_address`.
- [X] T051 [US5] In `InTheKnow/Accounts.cs`, add:
  - **`ChangePasswordAsync(accountId, sessionId, password)`**: `Passwords.Check` against the account's address. Update the `password` method's `secret` in place, or insert one when there is none (US6-4: queue `methodAdded {type: password}` and write `methodAdded`). Delete the other sessions, queue `passwordChanged` and write `passwordChanged`.
  - **`RequestEmailChangeAsync(accountId, email)`**: `invalid_email`, and `same_email` when it equals the current address ignoring case. When another account has `lower(email)`, return null without changes. Otherwise queue `emailChange` (to the new address) and `emailChangeNotice` (to the old), and write `emailChangeRequested` with no address in `detail`.
  - **`ConfirmEmailChangeAsync(token)`**:
    - the single-use `UPDATE` on an `emailChange` row → `token_invalid`;
    - another account has the address → `address_taken`;
    - otherwise `UPDATE account SET email = $to`, `Channels.MoveAccountAddressAsync`, and write `emailChanged`, all in one transaction.
  - **`ListSessionsAsync(accountId, currentSessionId)`**: `WHERE NOT awaiting_code ORDER BY id`.
  - **`EndSessionAsync(accountId, currentSessionId, id)`**: `use_sign_out` when `id` is the current session, `not_found` when it is not the account's, else delete it and write `sessionEnded`.
  - **`DeleteAsync(accountId, actorId, source)`** (R1, FR-024), in one transaction:
    - read the email and language;
    - collect `SELECT DISTINCT calendar_id FROM calendar_link WHERE subscriber = $a`;
    - queue `deleted` with `account_id` NULL to the email;
    - write `accountDeleted` with `actor_id`;
    - `DELETE FROM account WHERE id = $a`, mapping `23514` with constraint `last_admin` to `last_admin`;
    - `DELETE FROM calendar WHERE id = ANY($ids) AND NOT EXISTS (SELECT 1 FROM calendar_link WHERE calendar_id = calendar.id)`.
- [X] T052 [US5] In `InTheKnow/AccountApi.cs`, add:
  - `PUT /api/me/password`, `POST /api/me/email` and `DELETE /api/me`, each `.RequireAuthorization("stepUp")`;
  - `GET /api/me/sessions` and `DELETE /api/me/sessions/{id:long}`;
  - `POST /api/auth/email-change/confirm`, `.AllowAnonymous().RequireRateLimiting("public")`.

  Makes T049 pass.

**Checkpoint**: an account can be kept safe after a leak, moved to a new address, and removed.

---

## Phase 8: User Story 6 - Use several sign-in methods, and add or remove them later (Priority: P2)

**Goal**: list, add and remove sign-in methods generically, never leaving an account without one, with removal ending that method's sessions.

**Independent Test**: add the test-only method to a password account and sign in with it. Remove the password, then re-add it. Removing the last method is refused.

### Tests for User Story 6

- [X] T053 [P] [US6] Add a methods section to `InTheKnow.Tests/AccountTests.cs` (SC-014). Add `DELETE /api/me/methods/{id}` to its step-up case, and `GET /api/me/methods` and `DELETE /api/me/methods/{id}` to the route table of `InTheKnow.Tests/OwnershipTests.cs`.
  - **US6-1**: `GET /api/me/methods` lists `{id, type, createdAt, lastUsedAt}`, and no value is a hash. `lastUsedAt` changes after a sign-in or step-up with that method.
  - **US6-2**:
    - with step-up, `POST /api/test-auth/add {key}` adds a `test` method to a password account → `204`;
    - both methods sign in to the same account, and its calendars are visible through both;
    - `methodAdded {type: test}` is queued and a `methodAdded` event is written;
    - without step-up → `403 reauthentication_required`;
    - a key already used by another account → `409 method_in_use`.
  - **US6-3**: `DELETE /api/me/methods/{id}` with step-up → `204`:
    - sessions opened with that method → `401`, including this one when it was;
    - sessions of the other method keep working;
    - `methodRemoved` is queued and a `methodRemoved` event is written;
    - another account's method id → `404`.
  - **US6-4**: after the password is removed, `PUT /api/me/password` (step-up through `test`) adds it again, and email + password signs in.
  - **US6-5**: an account with only a `test` method can:
    - use every `/api/me` route;
    - turn 2FA on and off, with step-up through `test` plus a code;
    - request an email change;
    - delete itself.

    `POST /api/me/reauthenticate {password}` gives `401 invalid_credentials`.
  - **US6-6**: removing the only method → `409 last_method`, and it is still there.
  - **SC-014, concurrency**: an account with two methods, and two concurrent `DELETE /api/me/methods/{id}` for different ids from two sessions with step-up. Exactly one gets `204`, the other `409 last_method`, and one method remains. Also run a random sequence of 20 adds and removes: after each step the account has ≥ 1 method, and no session references a removed method.
  - **US6-7**: email change and deletion behave as in US5 for an account with both methods.

### Implementation for User Story 6

- [X] T054 [US6] In `InTheKnow/Accounts.cs`, add, per contracts/sign-in-method.md:
  - **`AddMethodAsync(accountId, type, key, secret)`**: insert the row; a unique violation on `(type, key)` → `method_in_use`. Queue `methodAdded {type}` and write `methodAdded`. The caller has already checked step-up through its route's policy.
  - **`RemoveMethodAsync(accountId, methodId)`**:
    - `not_found` when it is not the account's;
    - a pre-check for the count gives `last_method` with a reason;
    - `DELETE`, then commit, mapping the deferred `23514` / `last_method` at commit to `last_method`. The cascade ends its sessions;
    - queue `methodRemoved {type}` and write `methodRemoved`.
- [X] T055 [US6] In `InTheKnow/AccountApi.cs`, add `GET /api/me/methods` and `DELETE /api/me/methods/{id:long}` (`.RequireAuthorization("stepUp")`).
- [X] T056 [US6] In `InTheKnow.Tests/TestSignIn.cs`, add `POST /add {key}` with `.RequireAuthorization("stepUp")`, calling `Accounts.AddMethodAsync(accountId, "test", lower(key), null)` → `204` or `409 method_in_use`. Makes T053 pass.

**Checkpoint**: a later method type is only new rows plus its own routes (FR-026), as the test type shows.

---

## Phase 9: User Story 7 - Admins manage what is shared by everyone (Priority: P2)

**Goal**: admin-only account management, the last-admin rule, disabling that also stops notifications, and making an admin from the server.

**Independent Test**: on an empty deployment, `make-admin` queues an admin invite. An admin with 2FA can list calendars and create a global preset, a regular account cannot, and an admin without 2FA gets `two_factor_required`.

### Tests for User Story 7

- [X] T057 [P] [US7] Create `InTheKnow.Tests/AdminTests.cs` (`ApiHost`, with `FakeSmtp` email for the notification cases).
  - **SC-013**: enumerate every `RouteEndpoint` under `/api/admin` from `EndpointDataSource`, and fail if the list lacks `/api/admin/calendars`, `/api/admin/presets`, `/api/admin/channel-types` or `/api/admin/accounts`. T066 adds `/api/admin/signup-settings` and `/api/admin/invites` to that list. For each route:
    - a regular user → `403 admin_required`;
    - an admin session without a second factor → `403 two_factor_required`;
    - `Bearer {TestDb.ApiKey}` → `401 not_signed_in` (US7-8).
  - **US7-1**: an admin with a second factor gets `200` for `GET /api/admin/calendars` and `201` for `POST /api/admin/presets`.
  - **US7-11**:
    - an admin (created with `secondFactor: false`) enables 2FA in session 1: session 1 now passes the admin routes;
    - their session 2, opened before with the password alone, still gets `two_factor_required`;
    - a new sign-in with a code passes.
  - **US7-12**: `GET /api/admin/accounts?q=EXAMPLE` matches addresses case-insensitively, with the JSON of api.md, keyset paging (`limit=1` gives `next`), and `limit` 0 or 501 → `400 invalid_limit`. No value is a hash, token or secret.
  - **Role changes (FR-033a)**, `PUT /api/admin/accounts/{id}/role`:
    - a user without 2FA → `409 two_factor_required` (US7-9);
    - with 2FA → `200` with role `admin`, `roleChanged {role: admin}` queued, and `roleChanged` written with `actor_id`;
    - an unconfirmed account → `409 not_confirmed`, a disabled one → `409 account_disabled`, and an unknown id → `404`;
    - demoting another admin → `200`;
    - the last admin demoting themselves → `409 last_admin` (US7-7), and `DELETE /api/me` by the last admin → `409 last_admin`.
  - **US7-10**: an admin's `DELETE /api/me/2fa` with step-up → `409 demote_first`, and so does `DELETE /api/admin/accounts/{own id}/2fa`.
  - **US7-15**: `disable` and `DELETE` on another admin, and on one's own id → `409 demote_first`.
  - **FR-032a, last sentence**: admin A removes admin B's 2FA with `DELETE /api/admin/accounts/{B}/2fa` → `204`. B is still an admin and B's sessions → `401`. After B signs in again with the password alone, `GET /api/admin/calendars` → `403 two_factor_required`, and it passes again once B has set up and enabled 2FA in that session.
  - **US7-13, disable**:
    - `POST /api/admin/accounts/{id}/disable` → `200`: the user's sessions → `401`, sign-in with the right password → `403 account_disabled`, and a wrong one → `401 invalid_credentials`;
    - `disabled` is queued and `accountDisabled` written with `actor_id`;
    - a reset request for the address answers `202` and queues nothing;
    - a `channel_message` queued for the user's channel before disabling is gone, and nothing is sent to that channel.
    - **Notifications stop**: with the user subscribed and an active channel, a check that records changes creates no `delivery` rows for their channel. A delivery that was `pending` before disabling is `skipped` with reason `accountDisabled` at the next notifier pass.
    - `enable` → `200`: sign-in works, `enabled` is queued and `accountEnabled` written. Later changes are delivered, earlier ones are not (no backfill).
  - **US7-14**: `DELETE /api/admin/accounts/{id}` → `204`, exactly as US5-5: the final `deleted` email and `accountDeleted` with `actor_id`.
  - **US7-4**: `DELETE /api/admin/accounts/{id}/2fa` → `204`:
    - `two_factor` and codes are gone, and every session of that account → `401`;
    - `twoFactorOffByAdmin` is queued, also while the account is disabled;
    - `twoFactorRemovedByAdmin` is written with `actor_id`;
    - with 2FA off → `409 two_factor_off`.
- [X] T058 [P] [US7] Create `InTheKnow.Tests/MakeAdminTests.cs` (database via `TestDb`, calling `Accounts.MakeAdminAsync` and `Db.SchemaCurrentAsync` directly).
  - **Empty database**: `MakeAdminAsync("admin@example.nl", "en")` returns `"invite queued"`. There is a named invite with `grants_admin`, `created_by` NULL and `expires_at = now + 7 days`, an `invite` `account_message`, and an `adminMadeFromServer` event with `source` NULL and `detail {via: "server"}`.
  - **Again for the same address**: the existing invite gets `grants_admin = true`, `expires_at = now + 7 days` and a new `invite` message, and no second invite is created.
  - **After the invite lapsed**: 8 days later on the fake clock, `MakeAdminAsync` for the same address → `"invite queued"`, without an exception. There is exactly one named invite for the address, with `expires_at = now + 7 days`, and after a notifier pass its new link can be accepted.
  - **A confirmed account**: `"promoted"`. Its role is `admin` even without 2FA, and `roleChanged` is queued. Its admin routes still give `two_factor_required`, asserted through an `ApiHost` in one case.
  - **A disabled confirmed account**: it is re-enabled and promoted, and both `enabled` and `roleChanged` are queued.
  - **An unconfirmed account**: an invite is queued, and the account is not promoted.
  - `Db.SchemaCurrentAsync` returns false for a database whose `schema_migration` lacks version 4.
  - It succeeds while another connection holds `Db.TakeInstanceLockAsync`.

### Implementation for User Story 7

- [X] T059 [US7] In `InTheKnow/Accounts.cs`, add the admin actions (FR-019, FR-033a, FR-033c, research R12). Each writes its security event with `actor_id` and queues the email of contracts/account-emails.md.
  - **`ListAccountsAsync(q, after, limit)`**: `WHERE lower(email) LIKE '%' || lower($q) || '%'`, with `%` and `_` escaped, and `id > $after ORDER BY id LIMIT $limit + 1`. It joins `two_factor` for `twoFactor`.
  - **`SetRoleAsync(actorId, id, role)`**:
    - `not_found`;
    - promotion: `not_confirmed`, `account_disabled`, then `two_factor_required` unless `two_factor.enabled_at IS NOT NULL`, all checked in the updating transaction;
    - demotion of the last admin → `last_admin`, a pre-check, with the trigger's `23514` mapped too;
    - queue `roleChanged {role}` and write `roleChanged`.
  - **`DisableAsync(actorId, id)` / `EnableAsync`**: `demote_first` for an admin or `id == actorId`. Set or clear `disabled_at`. On disable, delete all sessions and the account's unsent `channel_message` rows (`DELETE FROM channel_message m USING channel c WHERE m.channel_id = c.id AND c.subscriber = $a AND m.sent_at IS NULL`, FR-033c). queue `disabled`/`enabled`, and write `accountDisabled`/`accountEnabled`.
  - **`AdminDeleteAsync(actorId, id)`**: `demote_first` as above, then `DeleteAsync(id, actorId, source)`.
  - **`RemoveTwoFactorAsync(actorId, id)`**: `demote_first` when `id == actorId`, and `two_factor_off`. Delete `two_factor` and every session, queue `twoFactorOffByAdmin`, and write `twoFactorRemovedByAdmin`.
  - **`static Task<long> CreateNamedInviteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string email, string language, bool grantsAdmin, int validDays, long? createdBy, DateTimeOffset now, CancellationToken ct)`**: first delete an expired named invite for the address (`DELETE FROM invite WHERE kind = 'named' AND lower(email) = lower($e) AND expires_at <= $now`, which cascades its unsent message), then insert a `named` invite with `max_uses = 1` and `expires_at = now + validDays`, then queue an `invite` message with `invite_id` and `args {admin: grantsAdmin}`. The token is minted at send (T016). US8 reuses it.
  - **`static Task<string> MakeAdminAsync(NpgsqlDataSource db, string email, string language, DateTimeOffset now)`** (R13):
    - a confirmed account → when disabled, clear `disabled_at`, queue `enabled` and write `accountEnabled` with `actor_id` NULL; then set role `admin`, queue `roleChanged`, and return `"promoted"`;
    - a pending named invite for the address → set `grants_admin = true`, renew it (`created_at = now`, `expires_at = now + 7 days`), queue a new `invite` message, and return `"invite queued"`;
    - otherwise `CreateNamedInviteAsync(…, grantsAdmin: true, validDays: 7, createdBy: null)` → `"invite queued"`.

    Either way it writes `adminMadeFromServer` with `source` NULL and `detail {via: "server"}`.
- [X] T060 [P] [US7] In `InTheKnow/Db.cs`, add `static Task<bool> SchemaCurrentAsync(NpgsqlDataSource db)`: true when `max(version)` in `schema_migration` is at least the highest embedded migration version. Reuse the version parsing of `MigrateAsync` by extracting it into one private helper.
- [X] T061 [P] [US7] In `InTheKnow/Checker.cs`, add `JOIN account a ON a.id = s.subscriber AND a.disabled_at IS NULL` to the fan-out `INSERT INTO delivery … SELECT` (~line 327). Add a `CheckerTests` case: a disabled account's active channel gets no delivery rows, and an enabled account's channel on the same calendar does.
- [X] T062 [US7] In `InTheKnow/Notifier.cs`, in the digest step's per-change reason (~line 251), check first: when the channel's account has `disabled_at` set (join `account` in the channel query), the reason is `accountDisabled`. Those rows become `skipped` like the other reasons, and nothing is sent.
- [X] T063 [US7] In `InTheKnow/AccountApi.cs`, add the admin account routes of api.md § Account actions under `app.MapGroup("/api/admin").RequireAuthorization("admin")`:
  - `GET /accounts`;
  - `PUT /accounts/{id:long}/role`. A `role` other than `admin`/`user` → `400 invalid_role`;
  - `POST /accounts/{id:long}/disable` and `/enable` → `200` with the account JSON;
  - `DELETE /accounts/{id:long}` → `204`;
  - `DELETE /accounts/{id:long}/2fa` → `204`.
- [X] T064 [US7] In `InTheKnow/Program.cs`, before `WebApplication.CreateBuilder(args)`, add the verb: `if (args is ["make-admin", var address, ..])`.
  - Parse `--language en|nl` (default `en`), otherwise print the usage and `return 1`.
  - Build configuration with `WebApplication.CreateBuilder([])`, so the verb's arguments never reach the command-line provider, and load `Settings`.
  - `Db.Create`. When `!await Db.SchemaCurrentAsync(db)`, print "the database schema is behind this build; start the server once first" and `return 1`.
  - Print the result of `Accounts.MakeAdminAsync(db, address, language, TimeProvider.System.GetUtcNow())` and `return 0`.
  - It never takes the instance lock, never migrates, and never prints a token.

  `WebApplicationFactory` passes no such arguments, so tests are unaffected. Makes T057 and T058 pass.

**Checkpoint**: the operator key's duties all belong to admins, and a lost last admin can be replaced from the server.

---

## Phase 10: User Story 8 - Admins decide who may sign up (Priority: P2)

**Goal**: sign-up settings managed by admins, named and shareable invites, and invite acceptance.

**Independent Test**: with open sign-up off and `inholland.nl` allowed, an `inholland.nl` address can sign up, `gmail.com` cannot, the same `gmail.com` address can with an invite, and with open sign-up on it can without one.

### Tests for User Story 8

- [X] T065 [P] [US8] In `InTheKnow.Tests/SignUpTests.cs`, add:
  - **SC-012**, the full matrix: `open` ∈ {on, off} × `allowed_domains` ∈ {none, `{example.nl}`, `{other.nl}`} × invite ∈ {none, named, shareable} for the address `x@example.nl`. Sign-up is allowed exactly when `open`, the domain matches, or a valid invite is used. The named case goes through `POST /api/auth/invite/accept` and the shareable one through `POST /api/auth/sign-up {invite}`. Assert the account's existence and state per case.
  - A shareable invite with `max_uses = 30`:
    - 30 sign-ups with distinct addresses succeed and each account is unconfirmed;
    - the 31st → `410 invite_invalid`;
    - 30 concurrent sign-ups against `max_uses = 29` give exactly 29 `202`s.
  - **Edge case "Shareable invite and existing addresses"**: a sign-up with a confirmed account's address through a shareable invite answers `202` and uses one.
  - **Route split (R10)**: a shareable token to `invite/accept` → `410 invite_invalid`, and a named token to sign-up's `invite` → `410 invite_invalid`. So are a revoked, an expired and an unknown token.
  - **Named invite, address confirmed since (U1)**: create a named invite for `x@example.nl`, then sign `x@example.nl` up through the domain rule and confirm it. Accepting the invite → `410 invite_invalid`, the invite row is gone, and the existing account is unchanged.
  - **Cleanup (T068)**: `Accounts.CleanupAsync` deletes a used-up shareable invite and a named invite 30 days + 1 s past `expires_at`, and keeps one 29 days past it.
- [X] T066 [P] [US8] In `InTheKnow.Tests/AdminTests.cs`, add `/api/admin/signup-settings` and `/api/admin/invites` to the SC-013 required list of T057, then add the settings and invite routes:
  - **US8-10**:
    - `GET/PUT /api/admin/signup-settings` round-trips, with domains trimmed, lowercased and deduplicated;
    - `400 invalid_domain` for `exa mple.nl` and `-x.nl`, and `400 too_many_domains` for 101;
    - `signupSettingsChanged` is written with `actor_id`;
    - existing accounts and pending invites are unchanged (FR-001d).
  - **US8-5**:
    - `POST /api/admin/invites {kind: named, email, language: nl, validDays: 7}` → `201` with the `Invite` JSON and no `link`;
    - after a pass, a Dutch `invite` email links to `/account/invite?token=…`;
    - `inviteCreated` is written.
  - **Shareable**: `{kind: shareable, maxUses: 30, validDays: 14}` → `201` with `link = {FrontendBaseUrl}/account/sign-up?invite=<43 chars>`. `GET /api/admin/invites` never shows the link again.
  - **Reasons**:
    - `400 invalid_kind`, `invalid_email`, `invalid_language`;
    - `invalid_valid_days` for 0 and 91;
    - `invalid_max_uses` for 1001 and for a named invite with 2;
    - `admin_needs_named` (US8-8);
    - `409 account_exists` for a confirmed account's address;
    - `409 invite_exists` for a second named invite.
  - **US8-9**:
    - `GET /api/admin/invites` lists pending invites, newest first, with `used`/`left`;
    - `DELETE /api/admin/invites/{id}` → `204`, the token → `410 invite_invalid` at once, its unsent email is gone, and `inviteRevoked` is written;
    - `POST …/{id}/resend` → `202` for named; after the new send, the old token → `410`;
    - resend for a shareable invite → `409 not_named`;
    - resend renews the validity: 8 days after a 7-day named invite, resend → `202`, and after a pass the new link can be accepted;
    - a new named invite to an address whose earlier invite expired → `201`, and the expired invite is gone.
- [X] T067 [P] [US8] In `InTheKnow.Tests/MakeAdminTests.cs`, add (US7-5): after `MakeAdminAsync` on an empty database, a notifier pass and `POST /api/auth/invite/accept {token, password}` → `200 {token, secondFactorRequired: false}`. The new account is confirmed, `admin`, and has its address channel. `GET /api/admin/calendars` with the token → `403 two_factor_required`. After setup, enable → `200`.

### Implementation for User Story 8

- [X] T068 [US8] In `InTheKnow/Accounts.cs`, add (FR-001a to FR-001e, research R10):
  - **`GetSignupSettingsAsync()` / `SetSignupSettingsAsync(actorId, open, domains)`**:
    - each domain is trimmed and lowercased, and must be a valid host name: `Uri.CheckHostName(d) == UriHostNameType.Dns`, contains a `.`, and no label starts or ends with `-`. Otherwise `invalid_domain`;
    - deduplicate, then `too_many_domains` over 100;
    - write `signupSettingsChanged` with `detail {open, domainCount}`.
  - **`CreateInviteAsync(actorId, kind, email, language, grantsAdmin, maxUses, validDays)`**, with the reasons of api.md in its order:
    - named: `account_exists` when a confirmed account has the address, then `CreateNamedInviteAsync`, mapping its unique violation to `invite_exists` (only a pending invite can collide, since it deletes an expired one first);
    - shareable: `SignIn.NewToken()` stored as `token_hash`, with the raw token returned only in this call's `link`.

    Write `inviteCreated`.
  - **`ListInvitesAsync()`**: `WHERE used < max_uses AND expires_at > now ORDER BY id DESC`.
  - **`RevokeInviteAsync(actorId, id)`**: delete it, which cascades its unsent message, and write `inviteRevoked`.
  - **`ResendInviteAsync(id)`**: `not_named`, then renew it (`created_at = now`, `expires_at = now + (expires_at − created_at)`, so it keeps its validity) and queue a new `invite` message.
  - **In `SignUpAsync`**, when `invite` is given: after the password rules and before the settings rule, `UPDATE invite SET used = used + 1 WHERE token_hash = $h AND kind = 'shareable' AND used < max_uses AND expires_at > $now RETURNING id` (no row → `invite_invalid`). Then skip the settings rule, and continue with the FR-006 branch unchanged. Write `inviteUsed`. A fully used invite is deleted by cleanup.
  - **`AcceptInviteAsync(token, password, language?, http)`**:
    - `invalid_language`, then find the `named` invite by `token_hash` with `expires_at > now` (else `invite_invalid`), then `Passwords.Check` against the invite's address;
    - when a confirmed account now has the address, delete the invite and return `invite_invalid` (spec Edge Cases);
    - in one transaction: if an unconfirmed account has the address, take it over by replacing its password `secret` in place and setting `confirmed_at`. Otherwise insert a confirmed account with the chosen or the invite's language, and its password method;
    - apply `role = 'admin'` when `grants_admin`, call `OnConfirmedAsync`, delete the invite, and write `inviteUsed` and `accountConfirmed`;
    - then return `signIn.CompleteAsync(accountId, methodId, http)`.
  - **`CleanupAsync`**: also delete invites with `used >= max_uses`, or `expires_at < now − 30 days`.
- [X] T069 [US8] In `InTheKnow/AccountApi.cs`, add:
  - under the admin group: `GET/PUT /signup-settings`, `POST /invites` → `201`, `GET /invites`, `DELETE /invites/{id:long}` → `204` and `POST /invites/{id:long}/resend` → `202`;
  - public: `POST /api/auth/invite/accept`, `.AllowAnonymous().RequireRateLimiting("public")`;
  - the optional `invite` field on `POST /api/auth/sign-up`.

  Makes T065–T067 pass.

**Checkpoint**: a school deployment can limit accounts to its domain and still let invited guests in.

---

## Phase 11: User Story 9 - Keep bots out of sign-in and sign-up (Priority: P3)

**Goal**: an operator-chosen bot check (Turnstile or reCAPTCHA v2), verified server-side before anything else on the protected routes.

**Independent Test**: with the test provider registered, every protected route refuses a missing, invalid or replayed result and accepts a valid one. With no provider, nothing asks for one.

### Tests for User Story 9

- [X] T070 [P] [US9] Create `InTheKnow.Tests/BotCheckTests.cs` (SC-015, contracts/bot-check.md § Test provider).
  - **Unit, against `FakeHttp`**, with `new BotCheck(new("test", new Uri("https://bot.test/verify")), "site", "secret", httpClient, log)`:
    - a token that answers `{"success": true}` then `{"success": false, "error-codes": ["timeout-or-duplicate"]}` gives `Passed`, then `Failed`;
    - `invalid-input-response` → `Failed`;
    - `internal-error` → `Unavailable`;
    - a 500 → `Unavailable`;
    - an unparsable body → `Unavailable`;
    - no answer within 5 s on the fake clock → `Unavailable`;
    - `null` or `""` → `Failed` with no request made;
    - the request is a form POST with `secret` and `response` and no `remoteip`;
    - the logs hold the provider name, the outcome and the error codes, never the token or the secret.
  - **Routes**, with that instance registered through the services callback in place of the configured one:
    - every protected route of FR-037 (sign-in, sign-up with and without `invite`, confirmation request, reset request) refuses a missing, invalid or replayed `botCheck` with `400 bot_check_failed`;
    - the response is identical for a known and an unknown address, and no account lookup happened: the counting hasher wrapper of T032 is not called, and no security event exists;
    - it is refused even with an invalid email or a too-short password, which shows the check comes first;
    - a valid result → the route's normal response.
  - **Unavailable (FR-040)**: sign-in proceeds, still throttled. The others → `503 try_again_later`.
  - **Unprotected routes**: the code step, the confirm/reset/email-change/invite-accept completions and every signed-in route never read `botCheck`. Assert with the provider answering `Failed` for everything: those routes still succeed.
  - **Off (US9-1)**: with no provider, every protected route works without `botCheck`, and `VerifyAsync` is never called.
  - **US9-3**: `GET /api/auth/bot-check` → `{provider: "test", siteKey: "site"}` or `{provider: null, siteKey: null}`, and the body never contains the secret.
  - **US9-4**: `Provider=recaptcha` through configuration builds a `BotCheck` whose verify URL is Google's, and `turnstile` Cloudflare's.

### Implementation for User Story 9

- [X] T071 [P] [US9] Create `InTheKnow/BotCheck.cs` per contracts/bot-check.md.
  - `static readonly Dictionary<string, Uri> Providers` holds `turnstile` and `recaptcha` with their siteverify URLs.
  - `VerifyAsync`:
    - an empty response → `Failed`;
    - `http.PostAsync(provider.VerifyUrl, new FormUrlEncodedContent([secret, response]))` with a 5-second `CancellationTokenSource`;
    - map per the outcome table;
    - log `Failed` and `Unavailable` with the provider name and the error codes only.
  - In `InTheKnow/Program.cs`, register the singleton from `Settings.Options.Accounts.BotCheck`, with a null provider when `Provider` is empty, and an `HttpClient` from `IHttpClientFactory`.
- [X] T072 [US9] In `InTheKnow/AccountApi.cs`:
  - add `GET /api/auth/bot-check`, `.AllowAnonymous()`;
  - add `string? BotCheck` to the sign-in, sign-up, confirmation-request and reset-request DTOs;
  - add one helper, `static async Task<IResult?> BotGateAsync(BotCheck bot, string? response, bool isSignIn, CancellationToken ct)`, called as the **first statement** of those four handlers:
    - off → null;
    - `Failed` → `400 bot_check_failed`;
    - `Unavailable` → null for sign-in, else `503 try_again_later`.

  Makes T070 pass.

**Checkpoint**: all nine stories are functional.

---

## Phase 12: Polish & Cross-Cutting

- [X] T073 [P] Create `InTheKnow.Tests/LogHygieneTests.cs` (SC-007, FR-034). One `ApiHost` with `CapturingLogs`, `FakeSmtp`, the test bot check provider and `TestSignIn` runs every flow:
  - sign-up, confirm, sign-in with and without 2FA, code, recovery code;
  - reset, password change, email change, sessions, methods;
  - admin actions, invites, deletion.

  Collect every value that must not leak:
  - passwords, TOTP secrets, codes, recovery codes;
  - session tokens, link tokens and full links from the captured emails;
  - addresses, bot check results.

  Assert that no captured log line contains any of them, and that no response body contains any of them, except the secret in the setup response, the codes in the enable/renew responses and the tokens in the sign-in responses. Also assert that no `security_event.detail` contains an address.
- [X] T074 [P] Create `InTheKnow.Tests/SignInLoadTest.cs` (SC-011).
  - An `ApiHost` whose services callback sets `PasswordHasherOptions.IterationCount = 210_000`.
  - 500 confirmed accounts created with hashes made at 210,000 iterations.
  - Then 500 sign-ins spread over 60 s: a `Task.Delay`-paced loop on the real clock, with the fake clock only for session times.
  - Assert every sign-in completes in < 2 s, and log the p50 and p95.
  - Mark it `[Trait("Category", "Load")]`, and start it with `Assert.SkipUnless(Environment.GetEnvironmentVariable("ITK_LOAD") == "1", "set ITK_LOAD=1 to run the SC-011 load test")`, so a normal `dotnet test` reports it as skipped instead of spending about 2 minutes on it. T079 runs it explicitly.
- [X] T075 [P] Update `specs/001-ical-change-detection/contracts/api.md`:
  - § Authentication and § Subscriber id point to `specs/004-accounts-login/contracts/api.md`;
  - the paths move to `/api/me/…` and `/api/admin/calendars`;
  - `invalid_subscriber` is gone.
- [X] T076 [P] Update `specs/001-ical-change-detection/contracts/configuration.md`: `InTheKnow:ApiKey` is retired (link `specs/004-accounts-login/contracts/configuration.md`).
- [X] T077 [P] Update `specs/002-subscriber-change-filters/contracts/api.md`: the paths move to `/api/me/…` and `/api/admin/presets…`, and global presets are admin-managed (004).
- [X] T078 [P] Update `specs/003-notification-channels/contracts/api.md`, with the paths `/api/me/…` and `/api/admin/channel-types`, and `Channel.accountAddress`. Update `specs/003-notification-channels/contracts/configuration.md` with a pointer to 004's additions.
- [X] T079 Run `dotnet build && dotnet test` from `backend/`, and fix warnings and failures.
  - Run `env ITK_LOAD=1 dotnet test --filter Category=Load` once, and record the p50 and p95 (SC-011).
  - Confirm that the only added package is Otp.NET, and that both `packages.lock.json` files are committed.
  - Grep `InTheKnow/` for `ApiKey`: only the retirement warning in `Settings.cs` remains.
  - Grep for `ponytail:`: the comments of T011 (in-memory throttle), T014 (device tables) and T045 (plain TOTP key) are present.
- [X] T080 Run the manual walkthrough of quickstart.md § 2 against Mailpit and a throwaway database, including the security-log query and `grep -E '@|token=' app.log`. **Time step 3 → 4** (sign-up, confirm, sign-in) for one new address without counting email delivery, and record that it is under 3 minutes (SC-001). Record anything that differs from the acceptance scenarios in a follow-up, not here.

---

## Dependencies & Execution Order

### Phase dependencies

- **Setup (1)** → **Foundational (2)** → user stories → **Polish (12)**.
- Within Setup: T004 needs T003. T001 and T002 are independent.
- Within Foundational:
  - the tests T005–T008 are parallel. T008 needs the `SignIn` signatures to compile, and passes after T014 and T020;
  - T009 comes first among the implementation tasks, because everything reads `AccountsOptions`;
  - T010, T011 and T012 are parallel;
  - T014 needs T010 and T011;
  - T015 needs T014;
  - T016 needs T012, T013 and T015;
  - T017 is independent of T014–T016;
  - T018 needs T014 and T017;
  - T019 needs T014 and T015;
  - T020 needs T014–T019;
  - T021 needs T020;
  - T022–T029 need T021 and are parallel with each other;
  - T030 closes the phase.

### User story dependencies

- **US1** needs Phase 2.
- **US2** needs US1 (confirmation, and `SignInViaAsync` for the password theory row). **US1 and US2 are released together.**
- **US3** needs US1 and US2: a reset of an unconfirmed account creates the address channel through `OnConfirmedAsync` (T040).
- **US4** needs US1 (sign-in). It is independent of US3, apart from T044's "right after a reset" case, which needs T042.
- **US5** needs US1, US2 (the address channel moves) and US4 (step-up with a code, and deletion of 2FA rows in T049).
- **US6** needs US5 (`PUT /api/me/password` adds a password, and the step-up tests' helpers).
- **US7** needs US4 (`two_factor`, enable, self-2FA-off) and US5 (`DeleteAsync` of T051 for admin deletion, and `DELETE /api/me` in T057). Its make-admin writes a named invite through `CreateNamedInviteAsync` (T059). **Accepting that invite is US8** (T067, T068).
- **US8** needs US1 (sign-up), US2 (`OnConfirmedAsync`) and US7 (`CreateNamedInviteAsync`, and the admin group in `AccountApi.cs`).
- **US9** needs US1 and US3 (the four protected routes). US8's `invite` field on sign-up is covered once US8 is done. Run T070's shareable case after T069.

### Same-file ordering (never [P] against each other)

| File | Order |
|---|---|
| `Accounts.cs` | T015 → T034 → T040 → T042 → T046 → T051 → T054 → T059 → T068 |
| `SignIn.cs` | T014 → T035 → T047 |
| `AccountApi.cs` | T019 → T036 → T037 → T043 → T048 → T052 → T055 → T063 → T069 → T072 |
| `Program.cs` | T020 → T037 → T064 → T071 |
| `Notifier.cs` | T016 → T062 |
| `Checker.cs` | T017 → T061 |
| `Channels.cs` | T017 → T039 → T050 |
| `Settings.cs` | T009 |
| `Texts.cs` | T012 |
| `TestHost.cs` | T004 → T021 → T032 |
| `TestSignIn.cs` | T008 → T048 → T056 |
| `ApiTests.cs` | T021 |
| `SignUpTests.cs` | T031 → T037 → T065 |
| `SignInTests.cs` | T032 → T041 |
| `AccountTests.cs` | T049 → T053 |
| `OwnershipTests.cs` | T038 → T044 → T049 → T053 |
| `AdminTests.cs` | T057 → T066 |
| `MakeAdminTests.cs` | T058 → T067 |
| `CheckerTests.cs` | T026 → T061 |
| `NotificationApi.cs` | T018 → T039 |

The [P] marks apply within one phase, and across different files only.

## Parallel example: Foundational

```text
Task: "Accounts settings rules in InTheKnow.Tests/SettingsTests.cs"           (T005)
Task: "Throttle unit tests in InTheKnow.Tests/ThrottleTests.cs"                (T006)
Task: "Account email tests in InTheKnow.Tests/AccountEmailTests.cs"            (T007)
Task: "Test-only sign-in method in InTheKnow.Tests/TestSignIn.cs"              (T008)
# after T009:
Task: "SecurityLog in InTheKnow/SecurityLog.cs"                                (T010)
Task: "Throttle in InTheKnow/Throttle.cs"                                      (T011)
Task: "Account texts in InTheKnow/Texts.cs"                                    (T012)
# after T021:
Task: "Rewrite NotificationApiTests / PresetsTests / ChannelsTests / DeliveryTests / CheckerTests / PublicPagesTests / SubscriptionsTests / EmailChannelTests"   (T022–T029)
```

## Parallel example: User Story 1

```text
Task: "SignUpTests in InTheKnow.Tests/SignUpTests.cs"                          (T031)
Task: "SignInTests in InTheKnow.Tests/SignInTests.cs"                          (T032)
Task: "Password rules in InTheKnow/Passwords.cs"                               (T033)
```

## Parallel example: User Story 7

```text
Task: "AdminTests in InTheKnow.Tests/AdminTests.cs"                            (T057)
Task: "MakeAdminTests in InTheKnow.Tests/MakeAdminTests.cs"                    (T058)
Task: "SchemaCurrentAsync in InTheKnow/Db.cs"                                  (T060)
Task: "Fan-out skips disabled accounts in InTheKnow/Checker.cs"                (T061)
```

## Implementation Strategy

### MVP first (US1 + US2)

1. Phase 1 and Phase 2. The suite is red from T003 until T030. Work through Phase 2 in one sitting, and do not merge it half done.
2. Phase 3 (US1). **Validate** with a local run against Mailpit: sign up, confirm, sign in, sign out.
3. Phase 4 (US2). **Stop and validate**: SC-002 holds for both methods. This is the first deployable increment for students. No admin can exist until US7's `make-admin`.

### Incremental delivery

After the MVP, each story is independently testable:
- US3, reset;
- US4, 2FA;
- US5, account management;
- US6, methods;
- US7, admins and `make-admin`. **Needed before any real deployment**, since nothing else creates an admin;
- US8, sign-up control. **Needed before a school deployment**, because the default is invite-only;
- US9, bot checks. **Recommended before open sign-up.**

## Notes

- Tests for a story are written first and should fail before its implementation.
- Constraints are stated verbatim in T003, and quoted from data-model.md. Do not loosen them in code.
- Password secrets are always updated in place, never delete-and-insert, because `session.method_id` cascades (data-model § Cross-table rules).
- Never log or return an FR-034 item (T073 enforces it).
- Commit after each task or logical group, except inside Phase 2's red stretch (T003–T030), which lands as one commit.
