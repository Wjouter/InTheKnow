# Implementation Plan: Notification Channels

**Branch**: `003-notification-channels` | **Date**: 2026-09-26 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/003-notification-channels/spec.md`

## Summary

The system emails subscribers the recorded changes that their filter does not hide. Every piece except delivery is
shared, so later channel types (web push, webhooks) only add a delivery implementation.

- **Fan-out at record time** (research R1). The check transaction that records changes also inserts one `pending`
  `delivery` row per change for every channel that qualifies at that moment:
  - the channel is active;
  - its subscriber is subscribed to the calendar;
  - that subscription's notifications are on.

  This answers FR-011's "when recorded" conditions exactly, so nothing is ever backfilled. The changes are selected
  by an id watermark, because `COPY` returns no ids.
- **Send-time decisions** (research R4, R5). A background `Notifier` finds channels whose oldest pending change is
  older than the collection window. For each one, in a single transaction, it:
  - locks the channel and its pending rows;
  - applies 002's `FilterEngine.Outcome`;
  - builds one `Message` (grouped per calendar, at most 50 listed, low priority marked);
  - sends it;
  - marks exactly those rows as sent or skipped.

  State changes (pause, unsubscribe, notifications off, subscription removed) skip pending rows when they happen.
  The sender re-checks the channel status and each calendar's switch under its lock, which catches rows a
  concurrent check inserted (R5).
- **Provider seam** (research R3, [channel-type.md](./contracts/channel-type.md)). `IChannelType` holds only a name,
  whether it is configured, target validation and `SendAsync` → `Sent | TargetFailed | TargetRejected |
  TypeUnavailable`. Retries, backoff, expiry, the rate limit, language, time zone and statuses are shared. Email
  uses the BCL `SmtpClient` (research R2). A test-only `RecordingChannel` runs the same delivery suite (SC-008).
- **Consent and opting out** (research R7, R8):
  - Confirmation tokens are hashed, single-use and valid for 48 h.
  - "This wasn't me" stores a salted hash of the address.
  - Every other email carries RFC 8058 one-click unsubscribe.
  - The no-login pages live under `/n/` outside `/api`. `GET` shows a button, `POST` acts.
- **Language and time zone** (research R9). There is one `Texts` class with English and Dutch. Dates use the
  `en-GB`/`nl-NL` formats in the subscriber's IANA time zone.
- **API** ([api.md](./contracts/api.md)):
  - subscriber routes for channels, notification settings and per-subscription notify/label;
  - `notify`, `label` and `name` added to 001's calendars list;
  - an operator `GET /api/channel-types` status.

## Technical Context

**Language/Version**: C# 14 on .NET 10 (unchanged)

**Primary Dependencies**: none added. `System.Net.Mail.SmtpClient`, `System.Text.Encodings.Web`, `TimeZoneInfo`,
`CultureInfo` and `RandomNumberGenerator` from the BCL, plus Npgsql and ASP.NET Core from 001. The only new
container is **Mailpit**, added to `compose.yaml` for the manual walkthrough and not used by the product or its
tests (research R11).

**Storage**: PostgreSQL 18, with one new embedded migration `003_notifications.sql` ([data-model.md](./data-model.md))

**Testing**: xunit.v3, as in 001 and 002. The delivery suite is a theory over email (against an in-test fake SMTP
server) and the test-only channel type, on `FakeTimeProvider`. Routes and pages are tested through
`WebApplicationFactory` with captured logs ([quickstart.md](./quickstart.md)).

**Target Platform**: Linux server, the single 001 process. It now runs two background services: `Checker` and
`Notifier`.

**Project Type**: backend of a web application. The SvelteKit frontend, where the settings links lead, is a later
feature.

**Performance Goals**:
- SC-006: a change reaches the inbox within 10 minutes at the default 5-minute window.
- SC-007: 1,000 subscribers × 300 changes give one email per channel, at most 50 listed, with the rate limit never
  exceeded. The fan-out duration inside the check is measured by the SC-007 test (research R1).

**Constraints**:
- 5 channels per subscriber.
- 3 confirmation and 5 test emails per channel per 24 h.
- 48 h confirmation links.
- A retry window of 24 h by default.
- 60 emails per minute by default.
- At most once per channel except the FR-025 crash window.
- No addresses, tokens or email content in logs (FR-031).

**Scale/Scope**: a whole school, with tens of thousands of subscribers and a few channels each. Semester re-publishes
touch hundreds of events for thousands of students at once (US5).

No NEEDS CLARIFICATION items remain. All decisions are in [research.md](./research.md).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Rule | Status | How |
|---|---|---|
| I. Smallest implementation, no speculative code | PASS | Nothing is built for web push or webhooks beyond the interface that US6 and SC-008 require now, which has two implementations. No outbox, no message table, no persisted operator status (R5, R10). Due times, windows and expiry are derived from `detected_at`, not stored (R4). |
| I. Preference order | PASS | BCL `SmtpClient` rather than MailKit (R2). A C# dictionary for two languages rather than `.resx` (R9). The existing trigger is extended rather than adding a new one (data-model). |
| I. Deviations justified | PASS | See Complexity Tracking. |
| II. Store only required data, name each personal field | PASS | [data-model.md § Personal and sensitive data](./data-model.md#personal-and-sensitive-data-principle-ii). Only the address is stored per channel. Declined addresses are kept only as a salted hash. Unused channels are deleted after 30 days. |
| II. No committed secrets | PASS | The SMTP password comes from the environment or a secret store. The shipped config has empty placeholders ([configuration.md](./contracts/configuration.md)). |
| II. Validate input at trust boundaries | PASS | Address, label, language, time zone, ids and tokens are validated server-side, with constraints where the database can express them. SMTP replies are classified by status code only (R2, R10). |
| II. Escape browser output | PASS | The `/n/` pages and the HTML emails encode every inserted value (R8, R9). The known limit of mail clients auto-linking URL-like text is recorded (R9). |
| II. Established auth | PASS | Reuses 001's operator key. The public routes use random 256-bit tokens with a stored SHA-256, compared by hash lookup, and no crypto is hand-rolled (R7). |
| II. HTTPS outside development | PASS | `PublicBaseUrl` and `FrontendBaseUrl` must be `https` outside localhost. SMTP requires STARTTLS except to a loopback capture ([configuration.md](./contracts/configuration.md)). |
| Tech: .NET, PostgreSQL, constraints in the database | PASS | Statuses, reasons, the consistency of failing and reason, token and label shape, uniqueness per subscriber, cascades and the link requirement of `subscription_setting` are constraints or triggers. The channel cap (5) is a trigger with a per-subscriber advisory lock. The 3 and 5 per 24 h send caps are rate limits on the app clock and stay application checks, as in 002 R7. |
| Tech: GPL-3.0-compatible licences, lockfile | PASS | No package change. Mailpit (MIT) is a compose dev service, not a dependency. |
| Workflow: checks as the gate; tests for non-trivial logic | PASS (planned) | Fan-out, sending, the lifecycle, consent, rendering, error mapping and log hygiene each get a test that fails if they break ([quickstart § 1](./quickstart.md)). |

**Post-design re-check (after Phase 1)**: PASS. The design adds six tables, one column, one dropped column, one
background service and one interface, and no dependency. Each addition is justified below or follows directly
from an FR.

## Project Structure

### Documentation (this feature)

```text
specs/003-notification-channels/
├── spec.md
├── plan.md              # This file
├── research.md          # Phase 0
├── data-model.md        # Phase 1
├── quickstart.md        # Phase 1
├── contracts/           # Phase 1
│   ├── api.md
│   ├── channel-type.md
│   ├── messages.md
│   └── configuration.md
├── checklists/
└── tasks.md             # Phase 2 (/speckit-tasks)
```

### Source Code (repository root)

```text
backend/
├── InTheKnow/
│   ├── Migrations/003_notifications.sql  # NEW: channel, channel_message, delivery, subscriber_setting,
│   │                                     #      subscription_setting, declined_address; calendar.feed_name;
│   │                                     #      drop notified_at; triggers
│   ├── ChannelType.cs         # NEW: IChannelType, SendStatus/SendResult, Message, CalendarGroup, ChangeItem
│   ├── EmailChannel.cs        # NEW: address normalisation, rendering (text + HTML, headers), SmtpClient send + error mapping
│   ├── Texts.cs               # NEW: en/nl fixed text, date formatting, time zone validation
│   ├── Channels.cs            # NEW: channel lifecycle, caps, tokens, declined hashes, settings, subscription notify/label
│   ├── Notifier.cs            # NEW: BackgroundService: queue (confirmation/test), digests, rate slots, backoff,
│   │                          #      expiry, cleanup, in-memory operator status; RunDueAsync(now) test seam
│   ├── NotificationApi.cs     # NEW: MapNotificationApi(): /api routes and the public /n/ pages
│   ├── Settings.cs            # CHANGED: NotificationsOptions + startup validation
│   ├── FeedParser.cs          # CHANGED: ParseResult carries X-WR-CALNAME
│   ├── Checker.cs             # CHANGED: feed_name on success; id watermark + fan-out in ApplyDiffAsync;
│   │                          #          regroup copies subscription_setting
│   ├── Subscriptions.cs       # CHANGED: feed_name on a new calendar's baseline
│   ├── Api.cs                 # CHANGED: calendars list gains notify/label/name; notified_at comment updated
│   ├── Program.cs             # CHANGED: register EmailChannel, Channels, Notifier; MapNotificationApi()
│   └── appsettings.json       # CHANGED: Notifications section
└── InTheKnow.Tests/
    ├── FakeSmtp.cs            # NEW: scriptable in-test SMTP server
    ├── RecordingChannel.cs    # NEW: the test-only channel type
    ├── DeliveryTests.cs       # NEW: theory over email and test (SC-008)
    ├── EmailChannelTests.cs, ChannelsTests.cs, PublicPagesTests.cs, NotificationApiTests.cs   # NEW
    └── CheckerTests.cs, DbTests.cs, ApiTests.cs                                              # EXTENDED
compose.yaml                   # CHANGED: mailpit service (manual walkthrough only)
```

Other documents updated during implementation:
- 001's `contracts/detected-change.md`: `notified_at` is gone, and delivery status lives in `delivery`.
- 001's `contracts/api.md`: the calendars list gains fields, and the changes route no longer mentions
  `notified_at`.
- 001's `contracts/configuration.md`: a pointer to this feature's configuration additions.

**Structure Decision**: the same single backend project and single test project as 001 and 002. Files are split
one per concern, as in 002:
- the seam (`ChannelType.cs`) and its one production implementation (`EmailChannel.cs`);
- shared text (`Texts.cs`);
- storage and lifecycle (`Channels.cs`);
- the background loop (`Notifier.cs`);
- routes (`NotificationApi.cs`).

## Complexity Tracking

| Addition / deviation | Why needed | Simpler alternative rejected because |
|---|---|---|
| `IChannelType` interface | FR-003, US6 and SC-008 require one delivery suite over email and a second type. It has two implementations now. | A `switch` on the type name: the test-only type would then have to exist in production code (US6-3). |
| A second background service (`Notifier`) | Windows, retries, rate limiting and expiry all run on time, not on requests | Sending inside the check or the API request: that blocks checks on SMTP, and no retry or rate limit could survive a request |
| Fan-out inside the check transaction (up to ~300k rows for SC-007) | Gives FR-011's "when recorded" conditions exactly, with no timestamp reconstruction (R1) | Asynchronous fan-out: it needs timestamp comparisons and loses the `skipped` record for early pauses. It is the named fallback if the SC-007 measurement is too slow. |
| Delivery rows for hidden changes, kept for the change's retention | FR-024 records `skipped (hidden)`. The filter is applied at send time (002 FR-007). | Filtering at fan-out: that breaks "edits before sending count" (US1-5). A shorter retention for finished rows is the named upgrade (R10). |
| A row lock held across one SMTP send (`FOR NO KEY UPDATE`, so fan-out inserts are not blocked) | Makes a pause or switch-off either precede a message or follow it, with no third case (R5) | A claim/commit outbox: more states and code for the same guarantee |
| Unsubscribe token stored as is | Asynchronous sends must embed it in every email (R7) | HMAC-derived tokens: a new operator secret and key rotation |
