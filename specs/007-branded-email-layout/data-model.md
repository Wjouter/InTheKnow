# Data model: Branded email layout

No new data is stored, and no table, column or migration changes. No personal field is added (Principle II).

## Input (unchanged)

The email is rendered from the existing `Message` (`backend/InTheKnow/ChannelType.cs`, contract
[003 channel-type.md](../003-notification-channels/contracts/channel-type.md#message)): kind, language, time zone,
confirm/decline/unsubscribe/settings URLs, calendar groups with their `ChangeItem`s, `NotListed`, and for account
emails the account kind, link and args.

## Derived per change row (HTML part only)

| Part | Source | Rule |
|---|---|---|
| Label text | `kind.added` / `kind.removed` / the field's kind name (as in the text part) | shown in capitals |
| Label colour | change kind and `LowPriority` | low priority → grey; added → green; removed → red; any `changed` field → amber |
| Event name | `ChangeView.EventName` | bold; "low priority" text after it when `LowPriority`; ≤ 200 encoded bytes incl. "…" |
| When | same value as the text part's date line | monospaced |
| What | added/removed: where it is/was; changed: old → new; time: omitted | each value ≤ 200 encoded bytes incl. "…" |
| Detected | `changes.detectedAt` | small, muted |

## Texts

One new key in both languages (FR-016, SC-006), HTML part only:

| Key | en | nl |
|---|---|---|
| `account.linkFallback` | Button not working? Copy this link into your browser: | Werkt de knop niet? Kopieer deze link naar je browser: |

The link itself follows the text as an encoded, selectable `<a>` (so it stays copyable). The existing
`LanguagesHaveTheSameKeys` test covers both languages.

## Palette (named constants, R9)

`ink #1d1d1b`, `magenta #e20e80` (bar, button, wordmark), `link #c70b70`, `body #3d3d3d`, `muted #5a5a5a`,
`page #f0f0f0`, `card #ffffff`, `border #e3e3e3`, `box #f7f7f7`, label pairs `#16673a/#e3f4ea`,
`#a50d26/#fbe3e7`, `#7a4a00/#fdf0d9`, `#5a5a5a/#f0f0f0`.
