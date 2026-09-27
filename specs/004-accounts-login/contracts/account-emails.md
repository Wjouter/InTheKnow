# Contract: Account emails

Every account email is an `account_message` row ([data-model](../data-model.md#account_message), research R9). The
email type of 003 sends it, with the same `From`, one recipient, `multipart/alternative`, encoding rules and rate
limit ([003 messages.md](../../003-notification-channels/contracts/messages.md)). Differences from 003's emails:
- There are **no `List-Unsubscribe` headers.** These emails are about the account, and they are sent even when
  every channel is paused or unsubscribed (spec Assumptions).
- The fixed text is in the row's `language`, which is the account's, or the invite's for an invite (FR-025,
  SC-010). `Texts` holds every key in `en` and `nl`, and a test fails on a missing key.
- Link kinds get their token at send time. The link is `{FrontendBaseUrl}/account/{page}?token={token}`.
- The body never contains a password, a code, a key, another person's address, or the network address of whoever
  triggered it (FR-034).

| Kind | To | Trigger | Link page, validity | Sent while disabled |
|---|---|---|---|---|
| `confirm` | the account's address | sign-up (new, or replacing an unconfirmed account), confirmation request | `confirm`, 24 h | no |
| `signUpAttempt` | the account's address | sign-up with the address of a confirmed account (FR-006) | none | no |
| `reset` | the account's address | reset request | `reset`, 1 h | no |
| `passwordChanged` | the account's address | password changed, reset completed, or password added | none | no |
| `emailChange` | **the new address** | email change request | `email`, 24 h | no |
| `emailChangeNotice` | the old address | email change request (FR-022) | none | no |
| `twoFactorOn`, `twoFactorOff` | the account's address | 2FA turned on or off by the owner | none | no |
| `twoFactorOffByAdmin` | the account's address | FR-019 | none | yes |
| `recoveryCodeUsed` | the account's address | a recovery code was used; `args.remaining` | none | no |
| `methodAdded`, `methodRemoved` | the account's address | FR-027a; `args.type` | none | no |
| `roleChanged` | the account's address | promotion or demotion, including from the server; `args.role` | none | yes |
| `disabled`, `enabled` | the account's address | FR-033c | none | yes |
| `deleted` | the former address (`account_id` NULL) | the owner or an admin deleted the account | none | yes |
| `invite` | the invited address (`invite_id`) | a named invite is created or resent, including from the server; `args.admin` | `invite`, the invite's own expiry | not applicable |

Rules:
- **Caps** (FR-004, FR-012a): at most 3 `confirm` and 3 `reset` rows per account, and 3 `signUpAttempt` rows per
  address, requested in the last 24 h. A request over a cap queues nothing and returns the normal response.
- **Invalidation**: sending a `confirm`, `reset` or `emailChange` sets `used_at` on the account's earlier unused
  rows of the same kind that have a token, and `expired_at` on older ones still unsent. Resending an invite replaces `invite.token_hash`.
- **Using a token**: `UPDATE account_message SET used_at = now() WHERE token_hash = $1 AND used_at IS NULL AND
  token_expires_at > now() RETURNING …`. It is single-use, even when two requests race.
- **Subjects**, English / Dutch, for example: "Confirm your InTheKnow account" / "Bevestig je InTheKnow-account",
  and "Your InTheKnow password was changed" / "Je InTheKnow-wachtwoord is gewijzigd". The exact text is in `Texts`.
- Every notice ends with one sentence saying what to do if it was not the owner: reset the password, or reply to the
  operator. There is no link that changes anything.
