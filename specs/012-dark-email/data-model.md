# Data model: Dark emails

One column is added. Nothing about the reader's device or mail app is stored (FR-014).

## Email theme

| Value | Label (en) | Meaning |
|---|---|---|
| `device` | Same as my device | the email carries both versions; the mail app picks (default) |
| `light` | Always light | light version only, asks mail apps for light (today's email) |
| `dark` | Always dark | dark version only, inline |

- **Belongs to**: the account. Not a device, not a channel, not a message (FR-010, FR-014).
- **Personal data**: a display preference only; it identifies nobody and says nothing about the person beyond how
  they like their email to look. Justified by FR-007 (Principle II).
- **Default**: `device`, for every existing account (by the column default) and every new one.
- **Validation**: the API accepts the three values only (FR-013); the database enforces the same with a CHECK.
  Code that reads it maps anything else to `device`.
- **Lifetime**: removed with the account row (FR-014). Changing it affects emails sent afterwards only; emails
  already sent do not change (US3-5).

## Schema change

`backend/InTheKnow/Migrations/007_email_theme.sql`:

```sql
-- 012: the account's email theme (specs/012 data-model.md).
ALTER TABLE account ADD COLUMN email_theme text NOT NULL DEFAULT 'device'
    CHECK (email_theme IN ('device', 'light', 'dark'));
```

No other table changes. `account_message` and `channel_message` keep no theme: it is read from `account` when the
message is sent (research R5).

## Which theme an email uses

| Email | Read from | No account |
|---|---|---|
| Change notification | the channel's subscriber | not possible |
| Test notification, channel confirmation | the channel's subscriber | not possible |
| Account email (`account_message`) | `account_message.account_id`, also when sent to another address (new email address) | `device` (invite; "account deleted", whose account row is gone). A sign-up confirmation has a new account, still on `device`. |

## In code

- `enum EmailTheme { Device, Light, Dark }` and a trailing `Message` parameter `EmailTheme Theme = EmailTheme.Device`.
- `NotificationSettings` gains `EmailTheme` (the API string).
- `EmailPalette` record, `Light` and `Dark` instances (research R4). `EmailChannel.Palette` and `TextPairs` cover
  both.
