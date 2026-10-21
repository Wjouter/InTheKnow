# Data model: reset-link pre-check

No schema change and no new stored data.

The check only reads what `CompleteResetAsync` already reads:

| Table | Columns read | Rule (FR-002) |
|---|---|---|
| `account_message` | `token_hash`, `kind`, `used_at`, `token_expires_at`, `account_id` | `token_hash = hash(token)`, `kind = 'reset'`, `used_at IS NULL`, `token_expires_at > now` |
| `account` | `disabled_at` | `disabled_at IS NULL` |

Nothing is written: `used_at` and `token_expires_at` stay as they are, and no `session`, `security_event` or queued
`account_message` row is added or removed (FR-005, SC-004). Token lifetime (1 h) and single use are unchanged.
