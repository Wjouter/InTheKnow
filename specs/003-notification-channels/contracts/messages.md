# Contract: Email messages

How `EmailChannel` renders a [`Message`](./channel-type.md#message). All fixed text comes from `Texts`, in the
subscriber's language (FR-017, FR-018). Text from the calendar (event names, rooms, teachers, groups, activity
types, descriptions, statuses as recorded) is shown as it is and never translated. In the HTML part it is always
HTML-encoded (FR-020).

## Common to every email

| Item | Value |
|---|---|
| From | `Notifications:Email:FromName` `<Notifications:Email:FromAddress>` (FR-022) |
| To | the channel's address only. One recipient per message, no Cc or Bcc (FR-020). |
| Body | `multipart/alternative`: `text/plain` first, then `text/html` (FR-021). The HTML uses inline styles only, with no images, scripts or remote resources. |
| Dates | `ddd d MMM yyyy HH:mm` in the message's time zone, formatted with `en-GB` or `nl-NL` (research R9) |
| `List-Unsubscribe` | `<{UnsubscribeUrl}>` on every kind **except** Confirmation (FR-010) |
| `List-Unsubscribe-Post` | `List-Unsubscribe=One-Click`, next to the header above |

## Confirmation (FR-005, FR-007)

- **Subject**: "Confirm your InTheKnow notifications" / "Bevestig je InTheKnow-meldingen".
- **Body**: who sends this and why (a student added this address for timetable notifications), then two links:
  the **confirm** link (`ConfirmUrl`) and the **"this wasn't me"** link (`DeclineUrl`), and a note that the confirm
  link expires after 48 hours.
- It has no unsubscribe header and no changes. This is the only email an unconfirmed address can receive (SC-002).

## Changes (FR-013 to FR-015, FR-019)

- **Subject**: the number of listed and not-listed changes, and the calendar name when there is exactly one
  calendar. For example "3 timetable changes – Minor robotics" / "3 roosterwijzigingen – Minor robotics", or
  "12 timetable changes in 2 calendars" / "12 roosterwijzigingen in 2 roosters".
- **Body**: one section per `CalendarGroup`, in order:
  - the calendar's `Name` as a heading, and under it a link to its notification settings (`SettingsUrl`, FR-009);
  - each `ChangeItem` in detection order, showing:
    - the event name;
    - the event's date and time (before → after for a `time` change);
    - what changed, with its old and new value. `added` and `removed` show the event as it is or was, and a
      `changed` item shows the field's translated kind name with its values;
    - a "low priority" marker when `LowPriority` is true (FR-013);
    - "detected at" and the detection time.
- **Beyond 50**: when `NotListed > 0`, a closing line says "and N more changes" / "en nog N wijzigingen" (FR-015).
- **Footer**: why the recipient gets this, and that the mail client's unsubscribe option stops these notifications to this
  address.
- **Links**: none that change anything. The settings links lead to the frontend, which requires login (SC-003).

Change kinds shown (translated): added, removed, name, time, status, location, description, activity type, rooms,
teachers, groups, and `details.<label>` (shown as its label). Values for status and activity type are recorded
calendar text and are shown as recorded.

## Test (FR-023)

- **Subject**: "InTheKnow test notification" / "InTheKnow-testmelding".
- **Body**: states that this is a test and contains no changes, then gives a link to the notification settings
  (`SettingsUrl`, FR-010).
- It has the unsubscribe headers.

## Public pages (`/n/…`)

These are minimal HTML with `<html lang="en|nl">`, a heading, one sentence and at most one button (a `<form
method="post">`). Every inserted value is HTML-encoded. The texts are in `Texts` under the same keys for both
languages (SC-009). They show no address, so a forwarded link reveals nothing.
