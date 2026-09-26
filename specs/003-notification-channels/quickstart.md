# Quickstart: Notification Channels

This is the validation guide. Setup, database and test commands are the same as in the
[001 quickstart](../001-ical-change-detection/quickstart.md).

- Contracts: [api.md](./contracts/api.md), [channel-type.md](./contracts/channel-type.md),
  [messages.md](./contracts/messages.md), [configuration.md](./contracts/configuration.md)
- Model: [data-model.md](./data-model.md)

## Prerequisites

- The compose PostgreSQL from 001 is running, and `INTHEKNOW_TEST_DB` is set.
- Migration `003_notifications.sql` applies at startup, embedded like `001` and `002`.
- The automated tests need no mail server. They start their own fake SMTP server (research R11).
- For the manual walkthrough only: `docker compose up -d mailpit`.

## 1. Automated checks

```bash
dotnet build && dotnet test
```

New or extended tests. Each one fails if the logic it covers breaks.

| Test file | Covers |
|---|---|
| `DeliveryTests` (database, **a theory over `email` and `test`**, SC-008) | See the list below. |
| `EmailChannelTests` | Rendering: multipart text and HTML; calendar markup encoded, so no injected tags or links (FR-020); unsubscribe headers on changes and test emails but not on confirmation emails; no link in the body that changes anything (SC-003); the list capped at 50 with an "N more" line; low-priority marker; label → feed name → host (US1-10); Dutch and English fixed text, and Dutch date output (research R9); `Texts` en/nl key parity (SC-009); a time change shown in a New York time zone versus Amsterdam (US3-6). Against `FakeSmtp`: the exception type each of RCPT 5xx, RCPT 4xx and refused connection produces, and its `SendStatus`; one recipient per message; a CR/LF subject throws (research R9). |
| `ChannelsTests` (database) | Address validation and every reason (FR-004); 5-channel cap; the same address twice; different subscribers with the same address; confirmation cap (3 per 24 h, including the initial one); test cap (5 per 24 h); declined hash blocks adding the address again and requesting confirmation, also after removal and after cleanup (FR-006); the 30-day cleanup and its "latest confirmation email" rule (FR-029a); illegal transitions give `invalid_status`; removing the last subscription keeps channels and settings (FR-029) |
| `PublicPagesTests` (in-memory app) | `GET` never changes state (US2-4); `POST` confirm, decline and unsubscribe, including one-click; expired, used and unknown tokens; confirm after decline is refused; a page's language follows its email (US3-4); **captured logs contain no token and no address across all these flows** (FR-031, SC-010) |
| `NotificationApiTests` (in-memory app) | Every route and reason in [api.md](./contracts/api.md); settings validation (language, IANA-only time zone); `notify`, `label` and `name` on 001's calendars list (FR-028); no calendar link in any response (SC-010); `/n/` works without the key and `/api` still needs it; operator status reflects failures and then a success (US4-7) |
| `CheckerTests` (extended) | Fan-out: rows only for channels active, subscribed and switched on at record time (FR-011); the id watermark with two checks at the same fake instant; `feed_name` stored and cleaned, and a name change records no change; regroup copies `subscription_setting` and an existing row on the target wins |
| `DbTests` (extended) | Constraints of the new tables; the extended `calendar_link_drop_filter` skips pending deliveries with `subscriptionRemoved` |

`DeliveryTests` covers, for both channel types:
- only normal and low-priority changes are sent (US1-3, US1-4, SC-001);
- a filter edit before sending counts (US1-5);
- no backfill after confirm, resume, switching on or subscribing (US1-6, US2-5);
- a shared calendar goes to two subscribers, each with their own filter (US1-7);
- one message per window (US1-8);
- two channels both receive (US1-9);
- switching off, pausing and removing skip with their reasons (US2-2, US2-7, US2-8);
- temporary failure, then recovery, delivers once (US4-1, SC-005);
- expiry after the retry window (US4-2);
- a permanent rejection fails only that channel (US4-3);
- a type-wide outage marks no channel failing (US4-6);
- a restart mid-way delivers exactly once (US4-5, SC-004);
- the rate limit is respected on the fake clock (US5-3);
- 1,000 subscribers × 300 changes give one message each, each listing at most 50 changes (SC-007). **The test
  logs the fan-out duration inside the check** (research R1).

## 2. Manual walkthrough (Mailpit)

Run the app with the local development settings from [configuration.md](./contracts/configuration.md#local-development)
against a development database with one MyTimetables calendar that `alice` subscribes to. Then:

1. `POST /api/subscribers/alice/channels` `{ "type": "email", "target": "alice@example.nl" }` returns `201` with
   `awaitingConfirmation`. Mailpit shows one confirmation email, with no `List-Unsubscribe` header.
2. Open its confirm link. The page has one button and nothing changes. Press it. `GET …/channels` shows `active`.
3. Record changes on alice's calendar **through a real check** against a feed you can edit, for example a local
   HTTPS file host serving an edited copy of `mt-nl.ics`. Rows inserted directly into `detected_change` get no
   deliveries, because the fan-out happens only in `Checker` (research R1). After the collection window, **one**
   email lists the changes, and a low-priority one is marked.
   - The email shows the feed's calendar name.
   - After `PUT …/calendars/{id}/notifications` `{ "label": "Minor robotics" }`, the next email shows the label.
4. `PUT …/notification-settings` `{ "language": "nl", "timeZone": "America/New_York" }`. The next email is in Dutch
   with New York times.
5. `POST …/channels/{id}/test`. The test email arrives with the unsubscribe header and a settings link.
6. Use Mailpit's view of the `List-Unsubscribe` URL: `POST` it with `List-Unsubscribe=One-Click`. The channel is
   `unsubscribed`, and further changes send nothing.
7. Stop Mailpit (`docker compose stop mailpit`), request a confirmation, and read `GET /api/channel-types`. Email
   shows `lastOk: false`, a reason, and a growing `consecutiveFailures`. Start Mailpit again, and the email arrives
   once.

Expected: each step matches the acceptance scenarios in [spec.md](./spec.md). Also, the app log has no address,
token or link (`grep -E '@|/n/' app.log` finds nothing from the app).

**Walkthrough notes (T078, 2026-09-26)**: run against Mailpit (v1.31.2) and a throwaway database, with
`CollectionWindow` at its 1-minute minimum. The real app listened on `http://localhost:5000` with its own Checker and
Notifier and real SMTP. Only the fetch transport was swapped, from a scratch host outside the repository built on
`WebApplicationFactory.UseKestrel`, to serve an editable local copy of `mt-nl.ics`, because the SSRF guard refuses
local addresses. Checks were triggered by setting `next_check_at` to now, so every change came from a real check. All
seven steps matched:
1. One confirmation email, `multipart/alternative`, no `List-Unsubscribe`.
2. `GET` showed one button and left the channel `awaitingConfirmation`; `POST` made it `active`.
3. The first check recorded 8 changes and 8 pending deliveries. One email followed the window, with the feed's name
   as heading, the low-priority change marked and added/removed events with their rooms. The result was 3 `sent` and
   5 `skipped`/`hidden`. After the label, the next email's subject was "3 timetable changes – Minor robotics".
4. After the switch to Dutch and New York: "3 roosterwijzigingen – Minor robotics". 9 Nov 08:30 Amsterdam showed as
   02:30 and 28 Oct 09:00 as 04:00 (the week between the two summer-time changes).
5. A Dutch test email arrived with the unsubscribe headers and the settings link.
6. The one-click POST answered `200` with a Dutch page, and the channel became `unsubscribed`. The next check
   recorded 8 changes with no deliveries, and no email was sent.
7. With Mailpit stopped, `GET /api/channel-types` showed `lastOk: false`, `unreachable`, `consecutiveFailures` 1 then
   2 a minute later, and a fixed `failingSince`. After Mailpit restarted, the confirmation arrived once at the 2-minute
   retry, and the status went to `lastOk: true`, `consecutiveFailures: 0`, `waiting: 0`.

The app log (15 lines) had no `@`, no `/n/` and no token. Its only warnings were the two lines
`Channel 1 (email): send failed with unreachable`.
