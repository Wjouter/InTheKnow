# Data Model: Accounts and Login

**Spec**: [spec.md](./spec.md) | **Research**: [research.md](./research.md) | **Builds on**:
[001](../001-ical-change-detection/data-model.md), [002](../002-subscriber-change-filters/data-model.md),
[003](../003-notification-channels/data-model.md)

Every integrity rule that the database can express is a constraint or a trigger. The migration is
`004_accounts.sql`. Enumerated values are camelCase, the same in the database and the API, as in 002 and 003.
Timestamps come from the app clock (`TimeProvider`), as in 003.

## Spec entity → storage mapping

| Spec entity | Storage |
|---|---|
| Account | table `account` (also takes over 003's `subscriber_setting`) |
| Sign-in method | table `sign_in_method` |
| Session | table `session` |
| Two-factor setting | tables `two_factor` and `recovery_code` |
| Account email token, pending email change | table `account_message` (a queue entry that is also the token and the cap log, research R9) |
| Invite | table `invite` |
| Sign-up settings | table `signup_settings` (a single row) |
| Security event | table `security_event` |
| Failed-attempt counters (FR-012) | not stored. They are held in memory by `Throttle` (research R8). |
| The account's own email channel (FR-028) | new column `channel.is_account_address` |

## Tables

### `account`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `bigint` | PK, identity | Replaces 001's subscriber id as the owner of all data (R1) |
| `email` | `text` | NOT NULL, `CHECK (length(email) BETWEEN 3 AND 254)` | Stored as entered, trimmed (edge case "Address case"). **Personal data.** |
| `confirmed_at` | `timestamptz` | NULL | NULL means unconfirmed (FR-003) |
| `disabled_at` | `timestamptz` | NULL, `CHECK (disabled_at IS NULL OR confirmed_at IS NOT NULL)` | Set by an admin (FR-033c) |
| `role` | `text` | NOT NULL, default `'user'`, `CHECK (role IN ('user','admin'))`, `CHECK (role = 'user' OR confirmed_at IS NOT NULL)`, `CHECK (role = 'user' OR disabled_at IS NULL)` | FR-032. An admin is confirmed and never disabled. |
| `language` | `text` | NOT NULL, `CHECK (language IN ('en','nl'))` | Chosen at sign-up (FR-031). Moved from `subscriber_setting`. |
| `time_zone` | `text` | NULL, `CHECK (time_zone IS NULL OR length(time_zone) BETWEEN 1 AND 100)` | NULL means the operator default (003 FR-018a). Moved from `subscriber_setting`. |
| `created_at` | `timestamptz` | NOT NULL | For the 30-day deletion of unconfirmed accounts (FR-005) |

Indexes:
- `UNIQUE (lower(email))` (FR-002). Used by sign-in, sign-up and admin search.
- `(created_at) WHERE confirmed_at IS NULL`, for cleanup.

### `sign_in_method`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `bigint` | PK, identity | Shown to the owner as the method's reference |
| `account_id` | `bigint` | NOT NULL, FK → `account(id)` ON DELETE CASCADE | |
| `type` | `text` | NOT NULL, `CHECK (type ~ '^[a-z][a-z0-9-]{0,30}$')` | `password`, or `test` in the test suite. A pattern, so later types need no schema change (FR-026). |
| `key` | `text` | NULL, `CHECK (key IS NULL OR length(key) BETWEEN 1 AND 255)` | An external provider's stable subject. NULL for `password`. |
| `secret` | `text` | NULL, `CHECK (type <> 'password' OR (secret IS NOT NULL AND key IS NULL))` | The `PasswordHasher` v3 hash (R4). Never returned or logged. |
| `created_at` | `timestamptz` | NOT NULL | Listed to the owner (FR-027a) |
| `last_used_at` | `timestamptz` | NULL | Set by every successful sign-in or re-authentication with it |

Indexes:
- `UNIQUE (account_id) WHERE type = 'password'`: at most one password.
- `UNIQUE (type, key) WHERE key IS NOT NULL`: one external identity belongs to one account.
- `(account_id)`.

### `session`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `bigint` | PK, identity | The non-secret reference of FR-014a |
| `account_id` | `bigint` | NOT NULL, FK → `account(id)` ON DELETE CASCADE | |
| `method_id` | `bigint` | NOT NULL, FK → `sign_in_method(id)` ON DELETE CASCADE | Removing a method ends its sessions (FR-027a, SC-014) |
| `token_hash` | `bytea` | NOT NULL, UNIQUE, `CHECK (length(token_hash) = 32)` | SHA-256 of the bearer token (R2) |
| `device` | `text` | NOT NULL, `CHECK (length(device) BETWEEN 1 AND 60)` | "Firefox on Windows" (R3). Never the raw `User-Agent`. |
| `awaiting_code` | `boolean` | NOT NULL, default `false` | Password accepted, 2FA code pending (R6) |
| `second_factor` | `boolean` | NOT NULL, default `false`, `CHECK (NOT (awaiting_code AND second_factor))` | Proved a code at sign-in or turned 2FA on in this session (FR-032a) |
| `reauthenticated_at` | `timestamptz` | NULL | Step-up, valid 10 minutes (R6) |
| `created_at` | `timestamptz` | NOT NULL | |
| `last_used_at` | `timestamptz` | NOT NULL | Written at most every 5 minutes (R2). Idle expiry is `last_used_at + SessionIdleLifetime`. |

Indexes: `(account_id)`, and `(last_used_at)` for cleanup.

### `two_factor`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `account_id` | `bigint` | PK, FK → `account(id)` ON DELETE CASCADE | |
| `secret` | `bytea` | NOT NULL, `CHECK (length(secret) = 20)` | TOTP key (R5). **Secret.** Returned only once, in the setup response. |
| `enabled_at` | `timestamptz` | NULL | NULL means setup was started but not confirmed (FR-015). 2FA is "on" exactly when this is set. |
| `last_step` | `bigint` | NOT NULL, default `0` | The last accepted TOTP step. Codes of this step or earlier are refused (FR-017). |

### `recovery_code`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `account_id` | `bigint` | NOT NULL, FK → `two_factor(account_id)` ON DELETE CASCADE | Turning 2FA off deletes the codes with the setting |
| `code_hash` | `bytea` | NOT NULL, `CHECK (length(code_hash) = 32)` | SHA-256 of the normalised 80-bit code (R5) |
| `used_at` | `timestamptz` | NULL | Set once (US4-5) |

PK `(account_id, code_hash)`. Generating new codes deletes all rows of the account in the same transaction
(FR-016).

### `account_message`

A queue entry until `sent_at` is set. For link kinds it is also the single-use token, and every row is the log that
the per-address caps count (R8, R9). The kinds are listed in [account-emails.md](./contracts/account-emails.md).

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `bigint` | PK, identity | |
| `kind` | `text` | NOT NULL, `CHECK (kind IN (…the kinds of account-emails.md…))` | |
| `account_id` | `bigint` | NULL, FK → `account(id)` ON DELETE CASCADE, `CHECK (account_id IS NOT NULL OR kind IN ('deleted','invite'))` | NULL for the deletion notice and for invites |
| `invite_id` | `bigint` | NULL, FK → `invite(id)` ON DELETE CASCADE, `CHECK ((kind = 'invite') = (invite_id IS NOT NULL))` | A revoked invite drops its unsent email |
| `to_address` | `text` | NOT NULL, `CHECK (length(to_address) BETWEEN 3 AND 254)` | Fixed when queued. For `emailChange`, the pending new address. **Personal data.** |
| `language` | `text` | NOT NULL, `CHECK (language IN ('en','nl'))` | |
| `args` | `jsonb` | NOT NULL, default `'{}'`, `CHECK (jsonb_typeof(args) = 'object')` | Non-secret values, e.g. `{"remaining": 7}` or `{"role": "admin"}` |
| `requested_at` | `timestamptz` | NOT NULL | The caps count rows with `requested_at > now − 24 h` |
| `sent_at` | `timestamptz` | NULL | |
| `expired_at` | `timestamptz` | NULL, `CHECK (sent_at IS NULL OR expired_at IS NULL)` | Unsent after `RetryWindow` |
| `token_hash` | `bytea` | NULL, UNIQUE, `CHECK (token_hash IS NULL OR (kind IN ('confirm','reset','emailChange') AND length(token_hash) = 32))`, `CHECK (kind NOT IN ('confirm','reset','emailChange') OR (sent_at IS NULL) = (token_hash IS NULL))` | Minted at send time (R9) |
| `token_expires_at` | `timestamptz` | NULL, `CHECK ((token_hash IS NULL) = (token_expires_at IS NULL))` | 24 h for `confirm` and `emailChange`, 1 h for `reset` |
| `used_at` | `timestamptz` | NULL, `CHECK (used_at IS NULL OR token_hash IS NOT NULL)` | Also set, without use, on the earlier tokens that a newer one invalidates |

Indexes:
- `(requested_at) WHERE sent_at IS NULL AND expired_at IS NULL`, for the queue;
- `(account_id, kind, requested_at)`, for caps and invalidation;
- `(lower(to_address), kind, requested_at)`, for the per-address caps of sign-up notices.

### `invite`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `bigint` | PK, identity | |
| `kind` | `text` | NOT NULL, `CHECK (kind IN ('named','shareable'))` | FR-001c |
| `email` | `text` | NULL, `CHECK ((kind = 'named') = (email IS NOT NULL))`, `CHECK (email IS NULL OR length(email) BETWEEN 3 AND 254)` | **Personal data**, needed to send and to create the account |
| `language` | `text` | NOT NULL, `CHECK (language IN ('en','nl'))` | The email's language and the new account's default |
| `grants_admin` | `boolean` | NOT NULL, default `false`, `CHECK (NOT grants_admin OR kind = 'named')` | US8-8 |
| `max_uses` | `integer` | NOT NULL, `CHECK (max_uses BETWEEN 1 AND 1000)`, `CHECK (kind = 'shareable' OR max_uses = 1)` | |
| `used` | `integer` | NOT NULL, default `0`, `CHECK (used BETWEEN 0 AND max_uses)` | |
| `token_hash` | `bytea` | NULL, UNIQUE, `CHECK (token_hash IS NULL OR length(token_hash) = 32)`, `CHECK (kind = 'named' OR token_hash IS NOT NULL)` | Shareable: set at creation. Named: set when its email is sent, and replaced by a resend (R10). |
| `created_by` | `bigint` | NULL, FK → `account(id)` ON DELETE SET NULL | NULL when made from the server (R13) |
| `created_at` | `timestamptz` | NOT NULL | |
| `expires_at` | `timestamptz` | NOT NULL, `CHECK (expires_at > created_at AND expires_at <= created_at + interval '90 days')` | 1 to 90 days, default 7 |

Index: `UNIQUE (lower(email)) WHERE kind = 'named'`. A second invite to the same address while the first is
pending is refused with `invite_exists`, and the admin resends or revokes the first. An expired named invite is
deleted when a new one is created for its address, and a resend renews `created_at` and `expires_at` with the same
validity, so an expired invite never blocks the address (R10, R13).

### `signup_settings`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `boolean` | PK, default `true`, `CHECK (id)` | Exactly one row |
| `open` | `boolean` | NOT NULL | FR-001a |
| `allowed_domains` | `text[]` | NOT NULL, `CHECK (cardinality(allowed_domains) <= 100)`, `CHECK (allowed_domains = lower(allowed_domains::text)::text[])` | Host names. The API validates their syntax and deduplicates them. |

The migration inserts `(true, false, '{}')`: invite-only (FR-001a).

### `security_event`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `bigint` | PK, identity | |
| `occurred_at` | `timestamptz` | NOT NULL | |
| `type` | `text` | NOT NULL, `CHECK (type ~ '^[a-z][a-zA-Z]{1,40}$')` | See § Security event types |
| `account_id` | `bigint` | NULL, no FK | Outlives the account (FR-035). NULL for an unknown address. |
| `actor_id` | `bigint` | NULL, no FK | The acting admin, for admin actions |
| `source` | `inet` | NULL | The client address, after trusted proxies (R3). NULL from the server command. **Personal data.** |
| `detail` | `jsonb` | NOT NULL, default `'{}'` | e.g. `{"methodType":"password"}` or `{"via":"server"}`. Never an FR-034 item. |

Index: `(occurred_at)`, for the 90-day cleanup. The operator's queries filter by `account_id` or `source`, and a
sequential scan over 90 days is acceptable for an operator's query.

Security event types: `signIn`, `signInFailed`, `throttled`, `codeFailed`, `reauthenticated`, `signedOut`,
`signedOutEverywhere`, `sessionEnded`, `twoFactorOn`, `twoFactorOff`, `recoveryCodeUsed`, `recoveryCodesRenewed`,
`passwordChanged`, `passwordReset`, `emailChangeRequested`, `emailChanged`, `methodAdded`, `methodRemoved`,
`accountConfirmed`, `accountDeleted`, `roleChanged`, `accountDisabled`, `accountEnabled`, `twoFactorRemovedByAdmin`,
`signupSettingsChanged`, `inviteCreated`, `inviteRevoked`, `inviteUsed`, `adminMadeFromServer`.

### `channel` (extended, 003)

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `is_account_address` | `boolean` | NOT NULL, default `false`, `CHECK (NOT is_account_address OR type = 'email')` | R15 |

Index: `UNIQUE (subscriber) WHERE is_account_address`.

### `delivery` (changed, 003)

The `reason` check gains `accountDisabled` (R12).

### Owner columns (changed, 001–003)

The `subscriber` column of `calendar_link`, `subscription_filter`, `channel`, `subscription_setting` and
`declined_address` becomes `bigint NOT NULL REFERENCES account (id) ON DELETE CASCADE`. `preset.owner` becomes the
same, but nullable, since NULL means global. The regex checks are dropped, and the existing indexes and unique keys
stay as they are (R1).

`subscription_filter.preset_id`: `ON DELETE RESTRICT` → `NO ACTION` (R1).

`subscriber_setting`: dropped. Its columns are now on `account`.

## Cross-table rules (triggers)

- **`sign_in_method_keep_one`**: a deferred constraint trigger, `AFTER DELETE ON sign_in_method`. At commit, if the
  account still exists and has no method, it raises `23514`. It takes `pg_advisory_xact_lock(hashtext('method:' ||
  account_id))` first, so concurrent removals queue behind each other (FR-027a, SC-014). Taking over an unconfirmed
  account (delete, then insert, in one transaction) passes.
- **A password change, reset or rehash updates `sign_in_method.secret` in place**, and never replaces the row:
  `session.method_id` cascades, so replacing the row would end the session that made the change (FR-013).
- **`account_keep_admin`**: `AFTER UPDATE OF role OR DELETE ON account`, for rows where `OLD.role = 'admin'`. It
  takes `pg_advisory_xact_lock(hashtext('admins'))`, then raises `23514` with constraint `last_admin` if no account
  has role `admin` any more (FR-033a, R12). A deployment that never had an admin is unaffected, since the trigger
  only fires when an admin row changes.
- **003's `channel_limit_check`**, `subscription_filter_check`, `subscription_setting_check` and
  `calendar_link_drop_filter` keep working unchanged on the `bigint` columns. `hashtext('channel:' || subscriber)`
  casts implicitly.

## Migration `004_accounts.sql`

1. Delete the rows the spec does not carry over (R1): `delivery`, `channel_message`, `channel`, `declined_address`,
   `subscription_setting`, `subscription_filter`, `calendar_link`, `calendar` (its snapshots and changes cascade),
   `subscriber_setting`, and `preset WHERE owner IS NOT NULL`.
2. Create `account`, `sign_in_method`, `session`, `two_factor`, `recovery_code`, `invite`, `account_message`,
   `signup_settings` (and insert its row), `security_event`.
3. Drop 001's view `subscription`, because PostgreSQL refuses to change the type of a column that a view uses. Drop the `subscriber`/`owner` regex checks. Change those columns to `bigint USING NULL`, which is safe because
   the tables are empty, and add `NOT NULL` where it applies and the foreign keys.
4. Recreate the view `subscription` unchanged. Change `subscription_filter.preset_id` to `NO ACTION`. Add `channel.is_account_address` and the
   `accountDisabled` delivery reason. Drop `subscriber_setting`.
5. Create the two triggers above.

## Lifecycles

### Account

```text
            sign-up (FR-001) ──────────────► unconfirmed ──30 days──► deleted (FR-005)
                                                 │ confirm (link + password), or reset, or named invite
                                                 ▼
 named invite accepted ───────────────────► confirmed ◄──── re-enable (admin) ────┐
                                            │   │   └── disable (admin) ──► disabled ──► deleted (admin)
                                            │   └── promote/demote (admin; 2FA on, not last admin)
                                            └── delete (self, step-up; not last admin) ──► deleted
```

- Confirming creates the account's email channel (R15).
- Deleting runs `Accounts.DeleteAsync` (R1) and queues a `deleted` notice with `account_id = NULL`.

### Session

```text
password ok, no 2FA ─────────────────────────────► active
password ok, 2FA on ──► awaiting_code ──code ok──► active (second_factor)
                            └── 10 min ──► deleted
active ──sign-out / ended from another session / idle past lifetime / password change or reset (others) /
         2FA off / sign out everywhere / method removed / account disabled or deleted──► deleted
```

### Account message

```text
queued ──send ok──► sent [link kinds: token_hash, token_expires_at]
   │                 └── used (the explicit POST) / invalidated by a newer token / token expired
   └── RetryWindow passed ──► expired
all rows: `deleted` notice at once when sent or expired; deleted 24 h after sent (other notices), after used or token expiry (link kinds), or after expiry (unsent)
```

### Invite

```text
created [shareable: token now] ──► pending ──use── (used = max_uses) ──► deleted
   [named: token at send]            ├── revoked ──► deleted
                                     └── expires ──30 days──► deleted
```

## Personal and sensitive data (Principle II)

| Field | Why it is needed | Protection |
|---|---|---|
| `account.email` | Confirming, signing in, resets, security notices and the notification channel (spec, Account entity) | Deleted with the account, and unconfirmed accounts after 30 days. Never logged in full (FR-034). Shown only to its owner and to admins (FR-033c). |
| `account.language`, `.time_zone` | Emails the owner can read, in their local time | A choice, not identifying |
| `sign_in_method.secret` | Checking the password (FR-008) | PBKDF2-HMAC-SHA512, 210,000 iterations, salted (R4). Never returned or logged. |
| `sign_in_method.key` | Later external providers' subject (FR-027) | Not used by this feature, and NULL for passwords |
| `session.token_hash` | Recognising a signed-in device | SHA-256 only. The token exists only at the client. |
| `session.device` | Letting the owner recognise a device (FR-014a) | A coarse "browser on OS" label, not the `User-Agent` and not an address |
| `two_factor.secret` | Checking codes (FR-015) | Returned once. Stored as is (R5, with a `ponytail:` upgrade path). Deleted when 2FA is turned off. |
| `recovery_code.code_hash` | Signing in without the app (FR-016) | SHA-256 of 80 random bits. The codes are shown once. |
| `account_message.to_address` | Sending the email, and holding a pending email change (R9) | Deleted within 24 h of sending, use or expiry. The `deleted` notice is deleted as soon as it is sent or expires. Never logged. |
| `account_message.token_hash` | Single-use links (FR-003, FR-009, FR-022) | SHA-256 only, minted at send time |
| `invite.email` | Sending the invite and creating the account (FR-001c) | Deleted when the invite is used or revoked, or 30 days after it expires |
| `invite.token_hash` | Single-use or counted links | SHA-256 only. The shareable link is shown once. |
| `security_event.source` | Investigating abuse (FR-035) | Readable only in the database, never through the API. Deleted after 90 days. |
| In-memory throttle keys (addresses, network addresses) | FR-012 | Never persisted. Forgotten one hour after the last failure. |
