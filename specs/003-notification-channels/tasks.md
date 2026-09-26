---

description: "Task list for Notification Channels (003)"
---

# Tasks: Notification Channels

**Input**: Design documents from `/specs/003-notification-channels/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: Included. The constitution requires an automated check for non-trivial logic, and the plan lists a failing-if-broken test per component (plan § Constitution Check, quickstart § 1). Write each story's tests first. They should fail until that story's implementation tasks are done (a compile failure counts).

**Organization**: grouped by user story, in priority order: US1 (P1), US2 (P1), US3 (P2), US4 (P2), US5 (P2), US6 (P2), US7 (P3). **US1 and US2 ship together** (spec US2: "This must ship together with US1"; FR-010).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (a different file, and no dependency on an unfinished task)
- **[Story]**: the user story the task belongs to (US1 to US7)

## Path Conventions

- App: `backend/InTheKnow/`. Tests: `backend/InTheKnow.Tests/` (plan § Project Structure). Paths below are relative to `backend/`, except `specs/…` and `compose.yaml`, which are relative to the repository root.
- Migrations are embedded by the `Migrations/*.sql` glob in `InTheKnow/InTheKnow.csproj`, so the csproj needs no change.
- Test databases come from `TestDb` (`InTheKnow.Tests/TestDb.cs`). App tests use the `Harness` in `InTheKnow.Tests/ApiTests.cs`, which wraps `WebApplicationFactory`. It removes every `IHostedService`, replaces `TimeProvider` with a `FakeTimeProvider` and `Fetcher` with `FakeHttp`, and drives checks with `Checker.RunDueChecksAsync`. Read both files before writing tests. Drive the notifier the same way, with `Notifier.RunDueAsync(now)`.
- Changes are recorded **only through a real check** (`Checker.RunDueChecksAsync` against an edited fixture, for example `Fixtures/mt-nl.ics` → `Fixtures/mt-nl-edited.ics`). Rows inserted directly into `detected_change` get no deliveries (research R1).
- Every time decision uses the `now` or `TimeProvider` passed in, never `DateTimeOffset.UtcNow` or the database `now()` (research R4).

## Shared signatures (so tasks written separately compile together)

```csharp
// ChannelType.cs: contracts/channel-type.md, verbatim
interface IChannelType
{
    string Name { get; }                  // "email"; matches channel.type (^[a-z][a-z0-9-]{0,30}$)
    bool Configured { get; }
    int MaxMessagesPerMinute { get; }
    string? Normalize(string target);     // the stored target, or null when invalid
    Task<SendResult> SendAsync(string target, Message message, CancellationToken ct);
}
enum SendStatus { Sent, TargetFailed, TargetRejected, TypeUnavailable }
sealed record SendResult(SendStatus Status, string? Reason = null);   // Reason: a code such as "smtp_550", never server text
enum MessageKind { Confirmation, Changes, Test }
sealed record Message(MessageKind Kind, string Language, TimeZoneInfo TimeZone, string? ConfirmUrl, string? DeclineUrl,
                      string? UnsubscribeUrl, string? SettingsUrl, IReadOnlyList<CalendarGroup> Calendars, int NotListed);
sealed record CalendarGroup(long CalendarId, string Name, string SettingsUrl, IReadOnlyList<ChangeItem> Changes);
sealed record ChangeItem(ChangeView Change, bool LowPriority);

// FilterEngine.cs: ChangeView gains one shared reader, extracted from Api.cs's changes route (T013)
sealed record ChangeView(/* unchanged */)
{
    // "c.id, c.calendar_id, c.event_key, c.kind, c.field, c.old_value::text, c.new_value::text, c.event_name,
    //  c.event_starts_at, c.detected_at, c.event_before::text, c.event_after::text"
    internal const string Columns = "…";
    internal static ChangeView Read(NpgsqlDataReader r, int start = 0);
}

// Texts.cs
static class Texts
{
    static string Get(string language, string key);                       // throws KeyNotFoundException on an unknown key
    static string Format(string language, string key, params object[] args);
    static string Date(string language, TimeZoneInfo zone, DateTimeOffset t); // "ddd d MMM yyyy HH:mm", en-GB / nl-NL
    static TimeZoneInfo? FindTimeZone(string id);                         // IANA ids only (HasIanaId); null otherwise
    static IReadOnlyCollection<string> Keys(string language);             // for the parity test (SC-009)
}

// Settings.cs
sealed class NotificationsOptions
{
    TimeSpan CollectionWindow = 5 min; TimeSpan RetryWindow = 1 day; string DefaultTimeZone = "Europe/Amsterdam";
    string PublicBaseUrl = ""; string FrontendBaseUrl = ""; EmailOptions Email = new();
}
sealed class EmailOptions
{
    string Host = ""; int Port = 587; bool StartTls = true; string Username = ""; string Password = "";
    string FromAddress = ""; string FromName = "InTheKnow"; int MaxMessagesPerMinute = 60;
}
// InTheKnowOptions gains `NotificationsOptions Notifications`. Settings gains `TimeZoneInfo DefaultTimeZone` (resolved at load).

// Channels.cs: storage and lifecycle
sealed record ChannelRow(long Id, string Subscriber, string Type, string Target, string Status, string? FailureReason,
                         DateTimeOffset? LastMessageAt, DateTimeOffset CreatedAt);
sealed record ChannelResult(ChannelRow? Channel, string? Error);   // Error is the api.md `reason`; "not_found" means 404
sealed record SubscriptionNotifications(bool Notify, string? Label, string Name);
sealed record NotificationSettings(string Language, string? TimeZone, string EffectiveTimeZone);
enum PageOutcome { Done, AlreadyDone, Expired, Declined, Unavailable, NotFound }
sealed record PageResult(PageOutcome Outcome, string Language);    // Language picks the page text (FR-017)
sealed class Channels(NpgsqlDataSource db, IEnumerable<IChannelType> types, Settings settings, TimeProvider time)
{ /* methods named in the tasks below */ }

// Notifier.cs
sealed record TypeStatus(string Type, bool Configured, DateTimeOffset? LastAttemptAt, bool? LastOk, string? LastReason,
                         int ConsecutiveFailures, DateTimeOffset? FailingSince, long Waiting);
sealed class Notifier(NpgsqlDataSource db, IEnumerable<IChannelType> types, Presets presets, Channels channels,
                      Settings settings, TimeProvider time, ILogger<Notifier> log) : BackgroundService
{
    public Task RunDueAsync(DateTimeOffset now, CancellationToken ct = default);   // one pass; the test seam
    public Task<IReadOnlyList<TypeStatus>> StatusAsync(CancellationToken ct);
}

// InTheKnow.Tests: the delivery-suite harness (SC-008)
sealed class FakeSmtp : IAsyncDisposable
{
    static Task<FakeSmtp> StartAsync();                       // TcpListener on 127.0.0.1:0, no TLS
    int Port { get; }
    IReadOnlyList<CapturedMail> Mails { get; }
    void Recipient(string address, int? code);               // RCPT reply for this address: 450, 550…; null = 250
    bool Refuse { get; set; }                                 // close connections without a greeting
    int? DataReply { get; set; }                              // reply to DATA's end, e.g. 451; null = 250
    void DataReplyFor(string address, int? code);            // DATA-end reply only when this address is the recipient
}
sealed record CapturedMail(string From, IReadOnlyList<string> Recipients, string Data);
sealed class RecordingChannel : IChannelType                  // Name = "test"
{
    bool Configured { get; set; } = true; int MaxMessagesPerMinute { get; set; } = 60;
    List<(string Target, Message Message)> Sent { get; }     // only calls that returned Sent
    void Script(string target, SendStatus status, int times, string reason = "scripted");
    bool Outage { get; set; }                                 // every call → TypeUnavailable("unreachable")
}
interface IChannelUnderTest : IAsyncDisposable               // DeliveryTests.cs: one per type
{
    string Type { get; }                                     // "email" | "test"
    void Register(IServiceCollection services);              // adds its IChannelType (and, for email, its settings)
    IReadOnlyList<(string Target, Message Message)> Delivered { get; }
    void FailTemporarily(string target, int times);          // email: RCPT 450; test: TargetFailed
    void RejectPermanently(string target);                   // email: RCPT 550; test: TargetRejected
    void PoisonMessage(string target);                       // email: DATA 554 for this recipient; test: Script(target, TypeUnavailable, int.MaxValue)
    bool Outage { get; set; }                                 // email: FakeSmtp.Refuse; test: RecordingChannel.Outage
}
```

For email, `Delivered` comes from a decorator around `EmailChannel` that records the `Message` only when `SendAsync`
returned `Sent`. The harness also asserts that `FakeSmtp.Mails.Count` equals `Delivered.Count`. The delivery suite
checks the shared logic. `EmailChannelTests` checks what the email looks like.

---

## Phase 1: Setup

**Purpose**: the schema, so every later task can run against the test database.

- [ ] T001 Create `InTheKnow/Migrations/003_notifications.sql` per data-model.md § Migration. All CHECKs are verbatim from data-model.md.
  - `ALTER TABLE calendar ADD COLUMN feed_name text NULL CHECK (feed_name IS NULL OR (length(feed_name) BETWEEN 1 AND 100 AND feed_name !~ '[[:cntrl:]]'))`.
  - `ALTER TABLE detected_change DROP COLUMN notified_at`.
  - `channel`:
    - `id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY`;
    - `subscriber text NOT NULL CHECK (subscriber ~ '^[A-Za-z0-9._@-]{1,100}$')`;
    - `type text NOT NULL CHECK (type ~ '^[a-z][a-z0-9-]{0,30}$')`;
    - `target text NOT NULL CHECK (length(target) BETWEEN 1 AND 254)`;
    - `status text NOT NULL CHECK (status IN ('awaitingConfirmation','active','paused','unsubscribed','failing'))`;
    - `status_since timestamptz NOT NULL`;
    - `failure_reason text NULL`, with `CHECK ((status = 'failing') = (failure_reason IS NOT NULL))`;
    - `activated_at timestamptz NULL`;
    - `CHECK (status <> 'active' OR activated_at IS NOT NULL)`;
    - `last_message_at timestamptz NULL`;
    - `unsubscribe_token text NOT NULL UNIQUE CHECK (length(unsubscribe_token) = 43)`;
    - `failed_attempts integer NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0)`;
    - `next_attempt_at timestamptz NULL`;
    - `created_at timestamptz NOT NULL`;
    - a unique index `(subscriber, type, lower(target))`, and an index `(subscriber)`.
  - `channel_message`:
    - `id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY`;
    - `channel_id bigint NOT NULL REFERENCES channel(id) ON DELETE CASCADE`;
    - `kind text NOT NULL CHECK (kind IN ('confirmation','test'))`;
    - `requested_at timestamptz NOT NULL`;
    - `sent_at timestamptz NULL`;
    - `language text NULL CHECK (language IN ('en','nl'))`;
    - `token_hash bytea NULL UNIQUE CHECK (token_hash IS NULL OR (kind = 'confirmation' AND length(token_hash) = 32))`;
    - `expires_at timestamptz NULL`;
    - `used_at timestamptz NULL`;
    - `CHECK ((sent_at IS NULL) = (language IS NULL))`;
    - `CHECK (kind = 'confirmation' OR (expires_at IS NULL AND used_at IS NULL))`;
    - `CHECK (kind <> 'confirmation' OR ((sent_at IS NULL) = (token_hash IS NULL) AND (sent_at IS NULL) = (expires_at IS NULL)))`;
    - an index `(channel_id, requested_at)`, and a partial index `(requested_at) WHERE sent_at IS NULL`.
  - `delivery`:
    - `channel_id bigint NOT NULL REFERENCES channel(id) ON DELETE CASCADE`;
    - `change_id bigint NOT NULL REFERENCES detected_change(id) ON DELETE CASCADE`;
    - `status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','sent','skipped','expired'))`;
    - `reason text NULL CHECK (reason IN ('hidden','channelNotActive','notificationsOff','subscriptionRemoved'))`, with `CHECK ((status = 'skipped') = (reason IS NOT NULL))`;
    - `status_at timestamptz NULL`;
    - `PRIMARY KEY (channel_id, change_id)`;
    - an index `(change_id)`, and a partial index `(channel_id, change_id) WHERE status = 'pending'`.
  - `subscriber_setting`:
    - `subscriber text PRIMARY KEY` with the subscriber pattern CHECK;
    - `language text NOT NULL DEFAULT 'en' CHECK (language IN ('en','nl'))`;
    - `time_zone text NULL CHECK (time_zone IS NULL OR length(time_zone) BETWEEN 1 AND 100)`.
  - `subscription_setting`:
    - `calendar_id bigint NOT NULL REFERENCES calendar(id) ON DELETE CASCADE`;
    - `subscriber text NOT NULL` with the subscriber pattern CHECK;
    - `notify boolean NOT NULL DEFAULT true`;
    - `label text NULL CHECK (label IS NULL OR (length(label) BETWEEN 1 AND 100 AND label !~ '[[:cntrl:]]'))`;
    - `PRIMARY KEY (calendar_id, subscriber)`.
  - `declined_address`:
    - `subscriber text NOT NULL` with the subscriber pattern CHECK;
    - `address_hash bytea NOT NULL CHECK (length(address_hash) = 32)`;
    - `created_at timestamptz NOT NULL`;
    - `PRIMARY KEY (subscriber, address_hash)`.
  - Then `subscription_setting_check()` with its `DEFERRABLE INITIALLY DEFERRED` constraint trigger, `channel_limit_check()` with its `AFTER INSERT` trigger, and `CREATE OR REPLACE FUNCTION calendar_link_drop_filter()`, all exactly as in data-model.md § Cross-table rules. Keep 002's `CREATE TRIGGER` as it is.
- [ ] T002 Extend `InTheKnow.Tests/DbTests.cs`, and fix the `notified_at` use in `InTheKnow.Tests/ApiTests.cs`.
  - **The fix comes first.** `ApiTests.cs:198` counts `detected_change WHERE notified_at IS NOT NULL`, and that column is dropped. Replace the assertion with `SELECT count(*) FROM delivery` = 0: the changes route never creates a delivery.
  - In `DbTests.cs`, after migrating, assert that each of these is rejected:
    - a `failing` channel without `failure_reason`, and an `active` one with it;
    - a second channel of one subscriber with the same address in other case;
    - an unknown `status`;
    - an `unsubscribe_token` that is not 43 characters long;
    - a `test` `channel_message` with a `token_hash`;
    - a `channel_message` with `sent_at` but no `language`;
    - a sent `confirmation` without `token_hash` or without `expires_at`, and an unsent one with either;
    - an `active` channel without `activated_at`;
    - a `delivery` that is `skipped` without a `reason`, or `sent` with one;
    - a `label` with a newline;
    - a `subscription_setting` row with no link. It fails at commit.
    - a 6th `channel` row for one subscriber, with SQLSTATE `23514` and constraint `channel_limit`. 5 channels of another subscriber are unaffected.
  - Also assert:
    - two subscribers can each add the same address;
    - deleting a subscriber's last link to a calendar deletes their `subscription_setting` row and sets their pending `delivery` rows for that calendar to `skipped`/`subscriptionRemoved`;
    - pending rows of other calendars and of other subscribers are untouched;
    - deleting one of two links keeps both.
  - **Then extend `Harness`** in `InTheKnow.Tests/ApiTests.cs` with optional configuration overrides and an optional `Action<IServiceCollection>`/`Action<ILoggingBuilder>` callback, applied in its existing `WithWebHostBuilder`/`ConfigureTestServices`. T017 (delivery harness), T018/T019 (email configured and unconfigured), T029 (log capture) and T062 (both types in one app) use it. Existing `ApiTests` stay unchanged.

---

## Phase 2: Foundational (blocking prerequisites)

**Purpose**: the pieces every story uses:
- the channel-type seam;
- text and time zones;
- configuration;
- the feed name;
- fan-out at record time;
- the shared change reader;
- the test harness.

**⚠️ CRITICAL**: no user story work can begin until this phase is complete.

### Tests first

- [ ] T003 [P] Extend `InTheKnow.Tests/SettingsTests.cs` with each startup rule of contracts/configuration.md. Each case fails startup with a message naming the key.
  - `CollectionWindow` outside 1 to 60 minutes;
  - `RetryWindow` < `CollectionWindow` + 1 h (with the 5-minute default window, `01:00:00` fails and `01:05:00` succeeds);
  - `DefaultTimeZone` `Nowhere/City` or a Windows id (`W. Europe Standard Time`);
  - with `Email:Host` set:
    - an empty `PublicBaseUrl`, `FrontendBaseUrl` or `FromAddress`;
    - an `http://` base URL on a host other than `localhost`;
  - `Port` 0 or 65536;
  - `StartTls=false` with a host other than `localhost`, `127.0.0.1` or `::1`;
  - `Username` without `Password`, or the reverse;
  - a `FromName` that is empty, over 100 characters or contains a control character;
  - `MaxMessagesPerMinute` 0.

  With the shipped defaults and an empty `Email:Host`, startup succeeds and email counts as not configured.
- [ ] T004 [P] Extend `InTheKnow.Tests/FeedParserTests.cs`:
  - `Fixtures/mt-nl.ics` yields its `X-WR-CALNAME` as `FeedName`;
  - `Fixtures/generic.ics` yields `null`.
- [ ] T005 [P] Extend `InTheKnow.Tests/CheckerTests.cs`, following its existing detection tests. For the fan-out, insert `channel` rows directly with `status = 'active'`, `activated_at` and a 43-character token (FR-011).
  - A check that records changes creates one `pending` `delivery` per change for each channel that is active, of a subscriber linked to the calendar, and without `subscription_setting.notify = false`.
  - Channels that are `paused`, `awaitingConfirmation`, `unsubscribed` or `failing`, and channels of other subscribers or of a subscriber whose `notify` is false, get no row.
  - **The watermark**: two checks that record changes at the same fake instant give a channel activated between them only the second check's changes.
  - A baseline creates no deliveries.
  - `calendar.feed_name` is set on every successful check, including the baseline. It has control characters removed, is trimmed, is cut to 100 characters, and is `null` when empty.
  - A feed whose only edit is a new `X-WR-CALNAME` records no change but updates `feed_name`.
- [ ] T006 [P] Create `InTheKnow.Tests/FakeSmtp.cs` per the shared signature.
  - A `TcpListener` on `127.0.0.1:0` that answers `220`, then `EHLO`/`HELO` → `250`, `MAIL FROM` → `250`, `RCPT TO` → `250` or the scripted code for that address, `DATA` → `354`, then reads until `\r\n.\r\n` and replies `250` or `DataReply`, `RSET`/`NOOP` → `250`, and `QUIT` → `221`.
  - The `EHLO` reply offers no `STARTTLS` and no `AUTH`.
  - Captured mails keep the raw DATA, with dot-unstuffing.
  - `DataReplyFor` takes precedence over `DataReply` for its recipient.
  - One connection is handled at a time on a background task, and `DisposeAsync` stops it.
- [ ] T007 [P] Create `InTheKnow.Tests/RecordingChannel.cs` per the shared signature.
  - `Normalize` trims the input and accepts it when it is 1 to 254 characters, else it returns `null`.
  - `SendAsync` checks, in this order: `Outage` → `TypeUnavailable("unreachable")`; a scripted status for the target (decrementing `times`) → that status with its reason; otherwise `Sent`, and the call is recorded in `Sent`.
### Implementation

- [ ] T008 [P] Create `InTheKnow/ChannelType.cs` with `IChannelType`, `SendStatus`, `SendResult`, `MessageKind`, `Message`, `CalendarGroup` and `ChangeItem`, exactly as in the shared signatures and contracts/channel-type.md. No logic.
- [ ] T009 [P] Create `InTheKnow/Texts.cs` per research R9.
  - `static class Texts` holds one `Dictionary<string, Dictionary<string, string>>`, keyed `"en"`, with every key messages.md needs:
    - the subjects;
    - the confirmation, changes and test bodies;
    - the footer;
    - the "and N more changes" line;
    - "low priority" and "detected at";
    - each change-kind name: `added, removed, name, time, status, location, description, activityType, rooms, teachers, groups`;
    - the public-page titles, sentences, buttons and results: confirm, decline, unsubscribe, done, already done, expired, declined, unavailable, and not found.
  - Only English here. Dutch is added in T046.
  - `Get` throws on an unknown key. `Format` uses `string.Format` with the language's culture.
  - `Date` converts with `TimeZoneInfo.ConvertTime` and formats `ddd d MMM yyyy HH:mm` with `CultureInfo.GetCultureInfo(language == "nl" ? "nl-NL" : "en-GB")`.
  - `FindTimeZone` returns the zone only when `TimeZoneInfo.TryFindSystemTimeZoneById(id, out var z) && z.HasIanaId`, else `null`. This rejects Windows ids.
- [ ] T010 In `InTheKnow/Settings.cs`, add `NotificationsOptions` and `EmailOptions` per the shared signatures, and `InTheKnowOptions.Notifications`.
  - In `Settings.Load`, validate each rule of contracts/configuration.md § Settings with the existing `Require`. Use the rule wording as the message, prefixed with the key, for example `InTheKnow:Notifications:CollectionWindow must be between 1 and 60 minutes`.
  - `RetryWindow`: `InTheKnow:Notifications:RetryWindow must be at least CollectionWindow + 1 hour`.
  - Resolve `DefaultTimeZone` with `Texts.FindTimeZone` into `Settings.DefaultTimeZone`.
  - The base-URL rule: an absolute `https` URL, or `http` with host `localhost`. It applies only when `Email:Host` is set, and to `PublicBaseUrl`, `FrontendBaseUrl` and `FromAddress` (valid by `MailAddress.TryCreate`).
  - Add the `Notifications` section of contracts/configuration.md § Shape to `InTheKnow/appsettings.json` with its empty placeholders. Keep `Logging:LogLevel:Microsoft.AspNetCore` at `Warning`.
  - Makes T003 pass.
- [ ] T011 [P] In `InTheKnow/FeedParser.cs`, change `ParseResult` to `(IReadOnlyList<Occurrence>? Events, string? Error, string? FeedName = null)`. Set `FeedName` from `calendar.Properties["X-WR-CALNAME"]?.Value as string` on success. Add `static string? CleanName(string? s)` in the same class: remove `char.IsControl` characters, trim, cut to 100 characters, and return `null` when empty (research R9). Makes T004 pass.
- [ ] T012 In `InTheKnow/Checker.cs`, implement the fan-out and the feed name (research R1, R12).
  - **(a) The feed name**: in the success branch of `CheckAsync`, add `feed_name = $10` to the `UPDATE calendar SET status = 'ok', …` with `FeedParser.CleanName(parsed.FeedName)`. That branch covers both the baseline and the diff path.
  - **(b) The watermark**: in `ApplyDiffAsync`, after the `if (diff.Changes.Count == 0) return;` and before `BeginBinaryImportAsync`, read `var watermark = (long)(await ScalarAsync(conn, tx, "SELECT coalesce(max(id), 0) FROM detected_change WHERE calendar_id = $1", ct, claim.Id))!;`.
  - **(c) The fan-out**: after `w.CompleteAsync(ct)`, dispose the importer first, then run:
    ```sql
    INSERT INTO delivery (channel_id, change_id)
    SELECT ch.id, c.id
    FROM detected_change c
    JOIN subscription s ON s.calendar_id = c.calendar_id
    JOIN channel ch ON ch.subscriber = s.subscriber AND ch.status = 'active'
    LEFT JOIN subscription_setting ss ON ss.calendar_id = c.calendar_id AND ss.subscriber = s.subscriber
    WHERE c.calendar_id = $1 AND c.id > $2 AND coalesce(ss.notify, true)
    ```
    Pass `claim.Id` and `watermark`. The filter is **not** applied here (research R1). Above it, add `ponytail:` delivery rows kept for the change's retention: add a shorter retention for finished deliveries if the table size matters (research R10).
  - Makes T005 pass.
- [ ] T013 [P] In `InTheKnow/FilterEngine.cs`, add to `ChangeView` the `internal const string Columns` and `internal static ChangeView Read(NpgsqlDataReader r, int start = 0)` from the shared signatures. Move the column list and the `new ChangeView(...)` construction from the changes route in `InTheKnow/Api.cs` (~line 97–110) here. Make the route use `ChangeView.Columns` (aliasing `detected_change` as `c`) and `ChangeView.Read(r)`. The route's behaviour and existing `ApiTests` stay unchanged. The notifier reuses this reader. Do not copy the SQL.
- [ ] T014 In `InTheKnow/Subscriptions.cs` `AddLinkAsync`, add `feed_name` to the new-calendar `INSERT INTO calendar (…)` (~line 63) with `FeedParser.CleanName(parsed.FeedName)`. A joining link stores nothing, as before.

**Checkpoint**: `dotnet build && dotnet test` passes. Checks fan out pending deliveries and store feed names, and `FakeSmtp` and `RecordingChannel` compile.

---

## Phase 3: User Story 1 - Get an email when a lesson changes (Priority: P1) 🎯 MVP (with US2)

**Goal**: a confirmed email address receives, in one message per collection window, every recorded change that the subscriber's filter does not hide, with calendars named by label, then feed name, then host.

**Independent Test**: register and confirm an address, record hidden, low-priority and normal changes, advance past the window, and verify one captured email with exactly the normal and low-priority changes.

### Tests for User Story 1

- [ ] T015 [P] [US1] Create `InTheKnow.Tests/ChannelsTests.cs` (database, via `TestDb`, with `Channels` built directly and a `RecordingChannel` plus an `EmailChannel`).
  - `AddAsync` rejects, each with its reason:
    - an unknown type → `invalid_type`;
    - an invalid address → `invalid_target`: `a`, `Name <a@b.nl>`, a 255-character address, whitespace only;
    - a 6th channel → `channel_limit`;
    - the same address in other case → `target_taken`;
    - an unconfigured type → `type_unavailable`.
  - Nothing is stored after a rejection.
  - On success, the target is trimmed, the channel is `awaitingConfirmation` with a 43-character base64url `unsubscribe_token`, and one unsent `confirmation` `channel_message` is queued.
  - Two subscribers can add the same address.
  - `ConfirmAsync(token)`:
    - from `awaitingConfirmation` → `Done`, the channel is `active` with `activated_at`, and `used_at` is set;
    - the same token again → `AlreadyDone`;
    - a token 48 h + 1 s after `sent_at` → `Expired`, and the status is unchanged;
    - an unknown token → `NotFound`;
    - with the type unconfigured → `Unavailable`, unchanged.
  - `SetSubscriptionAsync(label)`:
    - accepts 1 to 100 characters;
    - rejects `""`, 101 characters or a `\n` with `invalid_label`, and the previous label stays;
    - with `null`, clears the label.
  - `name` falls back from the label to `feed_name`, then to `calendar.host`.
- [ ] T016 [P] [US1] Create `InTheKnow.Tests/EmailChannelTests.cs`.
  - **Rendering** through an `internal static MailMessage Build(string target, Message m, EmailOptions o)`:
    - `multipart/alternative` with `text/plain` first and `text/html` second;
    - From is `FromName <FromAddress>`, and To holds only the target, with no Cc or Bcc;
    - a Confirmation has no `List-Unsubscribe` header, and its text holds both `ConfirmUrl` and `DeclineUrl` and the 48-hour note;
    - a Changes message has `List-Unsubscribe: <UnsubscribeUrl>` and `List-Unsubscribe-Post: List-Unsubscribe=One-Click`;
    - each change shows its event name, date and time (before → after for `time`), kind name, old and new value, the low-priority marker only when `LowPriority`, and the detection time;
    - an event name `<a href="x">Hi</a> & <b>` appears HTML-encoded in the HTML part, with no `<a`/`<b` added (FR-020);
    - the subject is `3 timetable changes – Minor robotics` for one calendar and `12 timetable changes in 2 calendars` for two.
  - **Against `FakeSmtp`**, assert the thrown exception type and the mapped `SendResult` of each case (research R2):
    - RCPT `550` → `TargetRejected("smtp_550")`;
    - RCPT `450` → `TargetFailed("smtp_450")`;
    - refused connection → `TypeUnavailable("unreachable")`;
    - DATA `451` → `TypeUnavailable("smtp_451")`;
    - a captured mail has exactly one recipient.
  - A `MailMessage` whose subject contains `\r\n` throws when sent. This confirms research R9's reason for cleaning names.
  - `Normalize`: trims, rejects a display-name form and anything over 254 characters.
- [ ] T017 [P] [US1] Create `InTheKnow.Tests/DeliveryTests.cs` with the harness, then the US1 theories over `email` and `test`.
  - **The harness** (it references `EmailChannel` and `Notifier`, so it lives here, not in Phase 2):
    - `IChannelUnderTest` has two implementations:
      - `EmailUnderTest` starts `FakeSmtp`, sets `InTheKnow:Notifications:Email:{Host=127.0.0.1, Port, StartTls=false, FromAddress=intheknow@localhost}`, `PublicBaseUrl=http://localhost:5000` and `FrontendBaseUrl=http://localhost:5173`, and wraps the registered `EmailChannel` in the recording decorator;
      - `TestTypeUnderTest` registers `RecordingChannel` as `IChannelType` through `ConfigureTestServices`, and sets the same `PublicBaseUrl` and `FrontendBaseUrl`. They do not depend on `Email:Host`, and both theories must build identical `Message`s (SC-008).
    - The xunit theory data is `["email", "test"]`.
    - Both use the `Harness` hook from T002.
    - Helpers, built on `ApiTests.Harness`:
      - `SubscribeAsync(subscriber, fixture)`: a link through `POST /api/subscribers/{s}/links` and `FakeHttp`;
      - `AddActiveChannelAsync(subscriber, target)`: inserts `channel` directly as `active` with `activated_at = now`, so the stories that do not test consent stay short;
      - `RecordChangesAsync(editedFixture)`: swaps the fixture and runs `RunDueChecksAsync`;
      - `AdvanceAndNotifyAsync(TimeSpan)`: advances `FakeTimeProvider` and calls `Notifier.RunDueAsync`;
      - `DeliveryStatusesAsync(channelId)`: `change_id → (status, reason)`.
  - **The US1 theories**:
    - **SC-001, US1-3, US1-4**: with the Default filter, record a hidden change (an ended event), a low-priority one (tentative → confirmed) and a normal one (a future room change). After `CollectionWindow`, exactly one message lists the normal and low-priority ones, and only the low-priority one has `LowPriority`. Delivery statuses: `sent`, `sent`, `skipped`/`hidden`.
    - **US1-5**: a filter `PUT` that hides a pending change before the window ends makes it `skipped`/`hidden`, and it is not sent.
    - **US1-6**: a channel confirmed after changes were recorded never receives them.
    - **Subscribing after a change** gets nothing for it.
    - **US1-7**: two subscribers on one shared calendar, each with their own filter, each get their own message. Neither `Message` nor the captured email names the other's target.
    - **US1-8**: three changes recorded in two checks within one window give one message. A change after that message was sent starts a new window.
    - **US1-9**: two active channels of one subscriber both receive it.
    - **US1-10**: a labelled calendar is named by its label, an unlabelled MyTimetables calendar by its feed name, and a generic fixture calendar by its host.
    - **SC-002**: an `awaitingConfirmation` channel receives only its confirmation. Its `ConfirmUrl` and `DeclineUrl` start with `PublicBaseUrl` + `/n/confirm/` and `/n/decline/`.
    - **No window before its time**: nothing is sent at `CollectionWindow − 1 s`.
- [ ] T018 [P] [US1] Create `InTheKnow.Tests/PublicPagesTests.cs` (the in-memory app via `ApiTests.Harness`, without the operator key).
  - `GET /n/confirm/{token}` returns `200` `text/html; charset=utf-8` with one `<form method="post">`, and the channel is still `awaitingConfirmation`.
  - `POST` makes it `active` and shows the done page. A second `POST` shows "already confirmed".
  - An expired token shows the expired page and changes nothing.
  - An unknown or malformed token returns `404` with the English "not valid" text (the Dutch half is asserted in T043).
  - With email unconfigured, the "not available right now" page is shown and nothing changes.
  - No page contains the address.
- [ ] T019 [P] [US1] Create `InTheKnow.Tests/NotificationApiTests.cs` (the in-memory app via `ApiTests.Harness`).
  - `POST /api/subscribers/{s}/channels` returns `201` with the `Channel` JSON of contracts/api.md, and `400 invalid_type`/`invalid_target`, `409 target_taken`/`channel_limit`, `503 type_unavailable`.
  - `GET …/channels` returns `{ items }` ordered by `id`.
  - An invalid subscriber id returns `400 invalid_subscriber`.
  - `GET/PUT …/calendars/{id}/notifications` returns `{ notify, label, name }`, with:
    - `400 invalid_label`;
    - `400 empty_body` for `{}`;
    - `null` clearing the label;
    - `404` for a calendar the subscriber has no link to.
  - `GET /api/subscribers/{s}/calendars` gains `notify`, `label` and `name`.
  - `/n/confirm/{token}` answers without the key, while `/api/…/channels` without it returns `401`.
  - No response contains a calendar URL or feed token (SC-010).

### Implementation for User Story 1

- [ ] T020 [US1] Create `InTheKnow/Channels.cs` with `Channels` and the records of the shared signatures.
  - `ListAsync(subscriber)`.
  - `AddAsync(subscriber, type, target)`. The checks run in this order:
    1. `invalid_type`: not a registered `IChannelType.Name`;
    2. `invalid_target`: `Normalize` returns `null`;
    3. `type_unavailable`: `!Configured`;
    4. `target_declined`: a `declined_address` row with `DeclinedHash(subscriber, target)` exists;
    5. `channel_limit`: a `PostgresException` `23514` with `ConstraintName == "channel_limit"` from the insert (the database trigger). The unique index fails before the trigger, so a 6th channel with a duplicate address gives `target_taken`;
    6. `target_taken`: a unique violation `23505`.

    Insert the channel as `awaitingConfirmation` with `status_since = created_at = now` and `unsubscribe_token = Base64Url(RandomNumberGenerator.GetBytes(32))`. Queue `INSERT INTO channel_message (channel_id, kind, requested_at) VALUES ($1, 'confirmation', $now)` in the same transaction.
  - `static byte[] DeclinedHash(string subscriber, string address) => SHA256.HashData(UTF8(subscriber + "\n" + address.ToLowerInvariant()))` (research R7).
  - `ConfirmAsync(string token)` → `PageResult`:
    - a token not matching `^[A-Za-z0-9_-]{43}$`, or with no `channel_message` whose `token_hash = SHA256(token)` → `NotFound`;
    - otherwise, lock the channel `FOR UPDATE`, then:
      - `used_at` is set or the channel is `active`/`paused` → `AlreadyDone`;
      - `expires_at < now` → `Expired`;
      - declined → `Declined`;
      - the type is unconfigured → `Unavailable`;
      - otherwise set `status = 'active', status_since = activated_at = now, failure_reason = NULL, failed_attempts = 0, next_attempt_at = NULL` and `used_at = now` → `Done`.
    - The result language is the `channel_message.language`.
  - `GetSubscriptionAsync(subscriber, calendarId)`: `null` when there is no link. The name comes from `coalesce(ss.label, c.feed_name, c.host)`.
  - `SetSubscriptionAsync(subscriber, calendarId, JsonObject body)`: in this feature phase it handles `label` only. Validate 1 to 100 characters with no `char.IsControl` → `invalid_label`, then `INSERT … ON CONFLICT (calendar_id, subscriber) DO UPDATE SET label = excluded.label`. `notify` is added in T039.
  - Makes T015 pass for US1.
- [ ] T021 [P] [US1] Create `InTheKnow/EmailChannel.cs` with `sealed class EmailChannel(Settings settings) : IChannelType`.
  - `Name = "email"`, `Configured = Host != ""`, and `MaxMessagesPerMinute` from `EmailOptions`.
  - `Normalize` per contracts/channel-type.md § Implementations.
  - `internal static MailMessage Build(...)` renders per contracts/messages.md:
    - every fixed text comes from `Texts`, and every date from `Texts.Date(m.Language, m.TimeZone, …)`;
    - calendar values are `HtmlEncoder.Default.Encode`d in the HTML part;
    - the HTML uses inline styles only;
    - the text and HTML parts are UTF-8 `AlternateView`s, text first;
    - `List-Unsubscribe`/`List-Unsubscribe-Post` go on every kind except `Confirmation`.
  - The US2 settings links and footer come in T040. The Test body comes in T065.
  - `SendAsync` uses a new `SmtpClient(Host, Port) { EnableSsl = StartTls, Timeout = 30_000 }`, with `NetworkCredential` when `Username` is set, and awaits `SendMailAsync(msg, ct)`. It maps errors by status code only and never uses `ex.Message`:
    - `SmtpFailedRecipientException` with a 5xx status → `TargetRejected($"smtp_{code}")`;
    - with a 4xx status → `TargetFailed($"smtp_{code}")`;
    - a `SocketException` in the chain → `TypeUnavailable("unreachable")`;
    - a timeout → `"timeout"`;
    - any other `SmtpException` → `TypeUnavailable($"smtp_{code}")`.

    Adjust the mapping to what T016 observes.
  - Makes T016 pass.
- [ ] T022 [US1] Create `InTheKnow/Notifier.cs` with `Notifier : BackgroundService` per the shared signature (research R4, R5, R6).
  - `ExecuteAsync` loops `RunDueAsync(time.GetUtcNow())` then `Task.Delay(1 s, time)`, catching and logging exceptions like `Checker.ExecuteAsync`.
  - **Each pass**, for each configured type:
    1. **The queue**: unsent `channel_message` rows of that type whose channel has `(ch.next_attempt_at IS NULL OR ch.next_attempt_at <= $now)`, fewest `ch.failed_attempts` first, then oldest `requested_at`, 100 per query. Each one runs in one transaction:
       - lock the channel `FOR NO KEY UPDATE` and the row `FOR UPDATE`;
       - a confirmation for an `active` or `paused` channel, or a test for a channel that is not `active`, is deleted without sending;
       - set `sent_at = now` and `language` on every kind (the CHECK requires both). For a confirmation, also create a token and set `token_hash = SHA256(token)` and `expires_at = now + 48 h`;
       - build the `Message` with `ConfirmUrl`/`DeclineUrl` = `{PublicBaseUrl}/n/confirm|decline/{token}`;
       - send it, and on `Sent` set `last_message_at` and commit.
    2. **Due digests**:
       ```sql
       SELECT d.channel_id FROM delivery d JOIN detected_change c ON c.id = d.change_id JOIN channel ch ON ch.id = d.channel_id
       WHERE d.status = 'pending' AND ch.type = $1 AND (ch.next_attempt_at IS NULL OR ch.next_attempt_at <= $2)
       GROUP BY d.channel_id, ch.failed_attempts HAVING min(c.detected_at) <= $3 ORDER BY ch.failed_attempts, min(c.detected_at) LIMIT 100
       ```
       with `$3 = now − CollectionWindow`. Add a `ponytail:` comment for the due query grouping every pending row (research R6). Keep the batch in memory across passes until it is worked through.

       For each channel, one transaction:
       - `SELECT … FROM channel WHERE id = $1 FOR NO KEY UPDATE`;
       - `SELECT {ChangeView.Columns} FROM delivery d JOIN detected_change c ON c.id = d.change_id WHERE d.channel_id = $1 AND d.status = 'pending' ORDER BY d.change_id FOR UPDATE OF d`;
       - `presets.LoadFiltersAsync(subscriber)`;
       - the names and switches, from `coalesce(ss.label, c.feed_name, c.host)` and `coalesce(ss.notify, true)` over `subscription`;
       - the language and time zone from `subscriber_setting`, with no row meaning `en` and `settings.DefaultTimeZone`.

       Then evaluate each change, in this order (research R5, the send-time re-check):
       - the channel's `status` is not `active` → `skipped`/`channelNotActive`;
       - its calendar's `notify` is false → `skipped`/`notificationsOff`;
       - its calendar is missing from the filters → `skipped`/`subscriptionRemoved`;
       - `Outcome.Hidden` → `skipped`/`hidden`;
       - the rest are listed in change-id (detection) order.

       Group them into `CalendarGroup`s ordered by each calendar's first change, with `SettingsUrl = {FrontendBaseUrl}/calendars/{id}/notifications`, and set `UnsubscribeUrl = {PublicBaseUrl}/n/unsubscribe/{unsubscribe_token}`.
       - If nothing remains, write the skips and commit without sending.
       - Otherwise send. On `Sent`, set the selected ids to `sent` and the skipped ids to `skipped` with their reasons, both with `status_at = now`, set `last_message_at = now`, and commit.
  - For any other `SendResult`, roll back and leave the rows `pending`. Retries come in T053.
  - Database exceptions roll back the one channel and the pass continues (research R5).
  - Add the `ponytail:` comment for the row lock held across one send (research R5).
  - Makes T017 pass for US1.
- [ ] T023 [US1] Create `InTheKnow/NotificationApi.cs` with `MapNotificationApi(this WebApplication app)`.
  - Under `app.MapGroup("/api")`, add `GET`/`POST /subscribers/{subscriber}/channels` and `GET`/`PUT /subscribers/{subscriber}/calendars/{calendarId:long}/notifications`, per contracts/api.md:
    - validate the subscriber with `Subscriptions.SubscriberPattern()`;
    - map `ChannelResult.Error` to its status;
    - parse the PUT body as `JsonObject`, so that a missing field differs from `null`; neither field → `400 empty_body`;
    - use `Api.Problem` for errors.
  - Serialise `Channel` as in api.md (`status` camelCase, as stored).
  - Outside `/api`, add `MapGet` and `MapPost("/n/confirm/{token}")`:
    - `GET` renders the button page and changes nothing;
    - `POST` binds **no body**, calls `ConfirmAsync`, renders the result page and ends with `.DisableAntiforgery()` (research R8);
    - pages are minimal HTML with `<html lang>` from `PageResult.Language`, every inserted value `HtmlEncoder`-encoded, and no address;
    - `NotFound` → `404` with the English text. T047 adds the Dutch text.
  - Makes T018 and T019 pass for US1.
- [ ] T024 [US1] In `InTheKnow/Api.cs`, extend the `GET /subscribers/{subscriber}/calendars` query with `LEFT JOIN subscription_setting ss ON ss.calendar_id = c.id AND ss.subscriber = l.subscriber` and `c.host, c.feed_name`. Add `notify` (`coalesce(ss.notify, true)`), `label` and `name` (`label ?? feed_name ?? host`) to each calendar object (FR-028).
- [ ] T025 [US1] In `InTheKnow/Program.cs`:
  - `builder.Services.AddSingleton<IChannelType, EmailChannel>();`
  - `builder.Services.AddSingleton<Channels>();`
  - `builder.Services.AddSingleton<Notifier>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<Notifier>());`, the pattern `Checker` uses;
  - `app.MapNotificationApi();` after `app.MapFilterApi();`.

**Checkpoint**: US1 works alone: add, confirm, and receive one digest per window. Do not deploy before US2.

---

## Phase 4: User Story 2 - Stop notifications for a calendar or an address (Priority: P1)

**Goal**: every way out works: per-calendar switch, pause/resume, remove, mail-client unsubscribe, "this wasn't me". Each skips pending changes with its reason, and no email link changes anything without login.

**Independent Test**: with one active channel and two calendars, check that the email links each calendar's settings page. Then switch a calendar off and on, pause, resume, remove, one-click unsubscribe and decline. After each step, record changes and check what is sent and which reason is recorded.

### Tests for User Story 2

- [ ] T026 [P] [US2] In `InTheKnow.Tests/DeliveryTests.cs`, add the US2 theories over `email` and `test`:
  - **US2-2**: switching calendar X off makes X's pending changes `skipped`/`notificationsOff` on every channel of the subscriber. Later X changes get no row, Y still arrives, and `GET …/changes` still lists X's changes.
  - **US2-5**: switching X back on sends only changes recorded afterwards.
  - **US2-7**: pausing makes the pending changes `skipped`/`channelNotActive`. After a resume, only later changes are sent.
  - **US2-8**: removing the channel deletes it and its `delivery` rows, and nothing is sent.
  - **US2-3**: after a one-click unsubscribe, nothing more is sent.
  - **US2-6**: after an unsubscribe, a new confirmation plus confirm resumes sending with no backfill.
  - **Edge**: removing the subscription (`DELETE …/calendars/{id}`) makes the pending changes `skipped`/`subscriptionRemoved`.
  - Every message lists changes of only the calendars that are switched on.
  - **Race backstop (FR-011, SC-003)**: record changes while the channel is active, so the rows are `pending`. Then, **bypassing `Channels`**, run `UPDATE channel SET status = 'paused'` directly. After the window, nothing is delivered and every row is `skipped`/`channelNotActive`. Do the same with a direct `INSERT INTO subscription_setting (…, notify) VALUES (…, false)` → `skipped`/`notificationsOff`.
- [ ] T027 [P] [US2] In `InTheKnow.Tests/EmailChannelTests.cs`, add:
  - a Changes email over calendars X and Y has, per calendar, a link to `{FrontendBaseUrl}/calendars/{id}/notifications` in both parts (US2-1, FR-009);
  - the footer text;
  - **SC-003**: every URL in a Changes email is a settings link, and the only `/n/` URL is the one in the `List-Unsubscribe` header, never in the body;
  - in a Confirmation, the only `/n/` URLs are the confirm and decline links.
- [ ] T028 [P] [US2] In `InTheKnow.Tests/ChannelsTests.cs`, add:
  - `PauseAsync` is allowed only from `active`, and `ResumeAsync` only from `paused`. Other statuses give `invalid_status`.
  - `RemoveAsync` deletes the channel. Another subscriber's channel id → `not_found`.
  - `RequestConfirmationAsync` is allowed from `awaitingConfirmation`, `unsubscribed` and `failing`. From `active` or `paused` it gives `invalid_status`.
  - The 4th confirmation within 24 h, **counting the initial one**, → `confirmation_limit`. It is allowed again 24 h + 1 s after the first.
  - `DeclineAsync(token)`: from any status → `unsubscribed`, with a `declined_address` row. A repeat changes nothing more. Afterwards:
    - `RequestConfirmationAsync` → `target_declined`;
    - `ConfirmAsync` with an older unexpired token → `Declined`;
    - re-adding the address (other case too), after `RemoveAsync` and after the cleanup, → `target_declined`;
    - another subscriber can still add it;
    - a confirmation queued before the decline is deleted, and the next `RunDueAsync` sends nothing to the channel.
  - `UnsubscribeAsync(token)`: from `active`, `paused` or `failing` → `unsubscribed`; from other statuses nothing changes.
  - **FR-029a** with `CleanupAsync(now)`:
    - an `awaitingConfirmation` channel whose `status_since` and latest confirmation `sent_at` are both over 30 days ago is deleted;
    - one with a confirmation email sent 29 days ago is kept;
    - `active`, `paused` and `failing` channels are kept;
    - `channel_message` rows older than 30 days are deleted.
  - **FR-029**: removing the last subscription keeps the channels, the `subscriber_setting` and `declined_address`.
- [ ] T029 [P] [US2] In `InTheKnow.Tests/PublicPagesTests.cs`, add:
  - `GET` of `/n/decline/{token}` and `/n/unsubscribe/{token}` changes nothing (US2-4);
  - `POST /n/decline/{token}` → `unsubscribed`;
  - `POST /n/unsubscribe/{token}` with no body, and with `Content-Type: application/x-www-form-urlencoded` body `List-Unsubscribe=One-Click` → `200` and `unsubscribed` (RFC 8058);
  - confirming after a decline shows the declined page;
  - one channel's token never affects another channel of the same subscriber;
  - **FR-031, SC-010**: capture logs with a test `ILoggerProvider` added through `ConfigureLogging` in the harness. Across add, confirm, decline, unsubscribe, a sent digest and a failed send, no log line contains the address, any token, `/n/`, or a calendar URL.
- [ ] T030 [P] [US2] In `InTheKnow.Tests/NotificationApiTests.cs`, add:
  - `DELETE …/channels/{id}` → `204`;
  - `POST …/pause` and `…/resume` → `200` with the `Channel`, or `409 invalid_status`;
  - `POST …/confirmation` → `202`, or `409 invalid_status`/`target_declined`, `429 confirmation_limit`, `503 type_unavailable`;
  - a `channelId` of another subscriber → `404` on every channel route;
  - `PUT …/notifications` with `{ "notify": "yes" }` → `400 invalid_notify`;
  - `{ "notify": false }` → `200` with `notify: false`, and the calendars list shows it.
- [ ] T031 [P] [US2] In `InTheKnow.Tests/CheckerTests.cs`, add to the regroup tests: a subscriber's `subscription_setting` (`notify = false`, a label) moves with a link that regroups to another calendar. A row that the target already has for that subscriber wins. Other subscribers' rows on the old calendar are untouched.

### Implementation for User Story 2

- [ ] T032 [US2] In `InTheKnow/Channels.cs`, add the lifecycle methods. Each runs in one transaction, locks the channel `FOR UPDATE`, sets `status_since = now` and returns `ChannelResult` with `invalid_status` or `not_found`, per data-model.md § Channel lifecycle.
  - `PauseAsync`: `active` → `paused`, then `UPDATE delivery SET status = 'skipped', reason = 'channelNotActive', status_at = $now WHERE channel_id = $1 AND status = 'pending'`.
  - `ResumeAsync`: `paused` → `active`, `activated_at = now`.
  - `RemoveAsync`: `DELETE FROM channel WHERE id = $1 AND subscriber = $2`. The cascade removes its deliveries and messages.
  - `RequestConfirmationAsync`: allowed from `awaitingConfirmation`, `unsubscribed` and `failing`. Checks, in order:
    1. `type_unavailable`;
    2. `target_declined`;
    3. `confirmation_limit`: `count(*) FROM channel_message WHERE channel_id = $1 AND kind = 'confirmation' AND requested_at > $now - 24h` ≥ 3.

    Then insert a queued confirmation.
- [ ] T033 [US2] Same file: add `DeclineAsync(token)` and `UnsubscribeAsync(token)` → `PageResult`.
  - `DeclineAsync` looks the token up as `ConfirmAsync` does. It works for as long as the row exists, not only 48 h. It sets `unsubscribed` if not already, skips the pending rows with `channelNotActive`, deletes the channel's unsent `channel_message` rows (a queued confirmation must not follow a decline, FR-006), and runs `INSERT INTO declined_address … ON CONFLICT DO NOTHING`. It never re-activates.
  - `UnsubscribeAsync` looks the channel up by `unsubscribe_token` (same shape check). From `active`, `paused` or `failing` → `unsubscribed`, with the skip. The language is the subscriber's current `subscriber_setting.language` or `en`.
- [ ] T034 [US2] Same file: add `CleanupAsync(DateTimeOffset now)` (research R10).
  ```sql
  DELETE FROM channel ch WHERE ch.status IN ('awaitingConfirmation','unsubscribed') AND ch.status_since < $1
    AND NOT EXISTS (SELECT 1 FROM channel_message m WHERE m.channel_id = ch.id AND m.kind = 'confirmation' AND m.sent_at >= $1)
  ```
  with `$1 = now − 30 d`. Then `DELETE FROM channel_message WHERE requested_at < $1`, and `DELETE FROM channel_message WHERE sent_at IS NULL AND requested_at < now − RetryWindow`. Makes T028 pass.
- [ ] T035 [US2] In `InTheKnow/Notifier.cs`, call `channels.CleanupAsync(now)` from `RunDueAsync` when 10 minutes have passed since the last cleanup, using the pass's `now`, as `Checker` does with `RetentionEvery`. `Channels` is already a constructor parameter (shared signatures).
- [ ] T036 [US2] In `InTheKnow/NotificationApi.cs`, add:
  - `DELETE /subscribers/{subscriber}/channels/{channelId:long}`, `POST …/pause`, `POST …/resume` and `POST …/confirmation` (`202`), per contracts/api.md;
  - `MapGet`/`MapPost` for `/n/decline/{token}` and `/n/unsubscribe/{token}`. The `POST` handlers bind no body, so the one-click body `List-Unsubscribe=One-Click` is accepted without being parsed, and each ends with `.DisableAntiforgery()`. Every `POST` answers `200` with the result page, also when nothing changed.
  - Makes T029 and T030 pass.
- [ ] T037 [US2] In `InTheKnow/Checker.cs` `RegroupAsync`, next to the existing `subscription_filter` copy (~line 397) and **before** the link `UPDATE`/`DELETE`, add the same copy for `subscription_setting`:
  ```sql
  INSERT INTO subscription_setting (calendar_id, subscriber, notify, label)
  SELECT $1, subscriber, notify, label FROM subscription_setting WHERE calendar_id = $2 AND subscriber = $3
  ON CONFLICT (calendar_id, subscriber) DO NOTHING
  ```
  Makes T031 pass.
- [ ] T038 [US2] Makes T026 pass. It needs no code beyond T012, T032, T033 and the extended trigger from T001. Fix any gap it shows in `InTheKnow/Checker.cs`, `InTheKnow/Channels.cs` or `InTheKnow/Migrations/003_notifications.sql`.
- [ ] T039 [US2] In `InTheKnow/Channels.cs` `SetSubscriptionAsync`, add `notify`.
  - A non-boolean value → `invalid_notify`. Upsert it together with `label` in one statement.
  - When it becomes `false`, in the same transaction run:
    ```sql
    UPDATE delivery d SET status = 'skipped', reason = 'notificationsOff', status_at = $now
    FROM channel ch, detected_change c
    WHERE d.status = 'pending' AND ch.id = d.channel_id AND ch.subscriber = $s AND c.id = d.change_id AND c.calendar_id = $cal
    ```
  - Turning it on creates nothing backwards.
- [ ] T040 [US2] In `InTheKnow/EmailChannel.cs`, render each `CalendarGroup`'s heading with its `SettingsUrl` link, in text and HTML, and the Changes footer from `Texts` (contracts/messages.md). Makes T027 pass.

**Checkpoint**: US1 + US2 form the shippable MVP. `dotnet test` passes.

---

## Phase 5: User Story 3 - Emails in English or Dutch (Priority: P2)

**Goal**: language and time zone per subscriber, set through the API and applied at send time to every message and linked page.

**Independent Test**: one subscriber on Dutch and New York time, another on the defaults, each getting the same change. Every fixed text, date and page is in the chosen language and zone.

### Tests for User Story 3

- [ ] T041 [P] [US3] In `InTheKnow.Tests/EmailChannelTests.cs`, add:
  - **SC-009**: `Texts.Keys("en")` and `Texts.Keys("nl")` are equal sets.
  - `Texts.Date("nl", Amsterdam, 2026-10-06T06:30Z)` = `di 6 okt 2026 08:30`, and in English `Tue 6 Oct 2026 08:30`. This fails without ICU or tzdata (research R9).
  - A Dutch Changes email has the Dutch subject (`3 roosterwijzigingen – Minor robotics`), kind names and "en nog N wijzigingen", while event names, rooms and statuses appear as recorded (US3-2).
  - **US3-6**: the same `time` change in `Europe/Amsterdam` and in `America/New_York` shows the times 6 hours apart for an event at 2026-10-20T10:00Z, **5 hours** apart at 2026-10-27T10:00Z (Europe changes its clocks on 25 Oct, the US on 1 Nov), and 6 hours apart again at 2026-11-02T10:00Z.
  - A Dutch Confirmation has the Dutch subject `Bevestig je InTheKnow-meldingen`.
- [ ] T042 [P] [US3] In `InTheKnow.Tests/NotificationApiTests.cs`, add:
  - `GET …/notification-settings` for a new subscriber → `{ "language": "en", "timeZone": null, "effectiveTimeZone": "Europe/Amsterdam" }`;
  - `PUT` with `{ "language": "nl" }` and with `{ "timeZone": "America/New_York" }` → `200`;
  - `{ "timeZone": null }` clears it;
  - `{ "language": "de" }` → `400 invalid_language` (US3-5), and `{ "timeZone": "W. Europe Standard Time" }` or `"Mars/Base"` → `400 invalid_time_zone`, with the previous values kept;
  - `{}` → `400 empty_body`.
- [ ] T043 [P] [US3] In `InTheKnow.Tests/PublicPagesTests.cs`, add:
  - **US3-4**: a confirmation sent while the subscriber was Dutch gives Dutch confirm and decline pages (`<html lang="nl">`), even after the subscriber has switched back to English;
  - the unsubscribe page follows the subscriber's current language;
  - an unknown token's `404` page holds both the English and the Dutch "not valid" text.
- [ ] T044 [P] [US3] In `InTheKnow.Tests/DeliveryTests.cs`, add over both types:
  - **US3-1**: no setting → `Message.Language == "en"` and `TimeZone` = the default.
  - **US3-3 and the edge case "Language changed while waiting"**: a switch to Dutch and New York during a collection window makes the digest `nl`/`America/New_York`. A confirmation requested afterwards is `nl`.

### Implementation for User Story 3

- [ ] T045 [US3] In `InTheKnow/Channels.cs`, add `GetSettingsAsync(subscriber)` and `SetSettingsAsync(subscriber, JsonObject body)`.
  - `language` ∈ {`en`,`nl`}, else `invalid_language`.
  - `timeZone`: a string for which `Texts.FindTimeZone` returns a zone, stored as `zone.Id`, or `null`; otherwise `invalid_time_zone`.
  - Neither field → `empty_body`.
  - Upsert `subscriber_setting`. `EffectiveTimeZone` = `time_zone ?? settings.DefaultTimeZone.Id`.
- [ ] T046 [US3] In `InTheKnow/Texts.cs`, add the `"nl"` dictionary with every English key translated. Use the subjects of contracts/messages.md verbatim: "Bevestig je InTheKnow-meldingen", "N roosterwijzigingen – {name}", "N roosterwijzigingen in N roosters", "en nog N wijzigingen" and "InTheKnow-testmelding". Makes T041's parity and Dutch cases pass.
- [ ] T047 [US3] In `InTheKnow/NotificationApi.cs`, add `GET`/`PUT /subscribers/{subscriber}/notification-settings` per contracts/api.md, with the body `{ language, timeZone, effectiveTimeZone }`, and make the unknown-token `404` page show the English and the Dutch text. Makes T042 and T043's `404` case pass.
- [ ] T048 [US3] Makes T043 and T044 pass. `Notifier` (T022) already reads `subscriber_setting` at send time and stores `channel_message.language`, and `Channels` returns it in `PageResult`. Fix any gap it shows in `Notifier.cs` or `Channels.cs`.

**Checkpoint**: both languages and any IANA time zone work end to end.

---

## Phase 6: User Story 4 - Reliable delivery without duplicates (Priority: P2)

**Goal**: temporary failures retry with backoff and then expire. Permanent rejections fail only their channel, type-wide outages fail no channel, restarts never duplicate, and the operator sees the type's status.

**Independent Test**: a type outage and then recovery delivers once. A permanent reject fails one channel. A restart mid-way gives no duplicates. `GET /api/channel-types` reflects the failures and then the success.

### Tests for User Story 4

- [ ] T049 [P] [US4] In `InTheKnow.Tests/DeliveryTests.cs`, add over both types:
  - **US4-1, SC-005**: with `Outage = true`, passes over 30 minutes send nothing. Channel statuses stay `active` (US4-6). Only channels whose send hit the outage have `failed_attempts > 0`. After `Outage = false`, the next pass past the type backoff delivers every pending change exactly once.
  - **Type backoff**: during an outage, the attempts are spaced 1, 2, 4… minutes apart, capped at 1 h, and each attempt counts once for the whole type, not once per channel.
  - **Channel backoff**: `FailTemporarily(target, 2)` → the channel's `next_attempt_at` is at 1 min and then 2 min, and the third attempt delivers. Another channel of the same type is not delayed.
  - **Bad message**: channel A (the oldest due) gets `PoisonMessage(a)`, and channels B and C are healthy. Within 3 minutes B and C receive their messages. A stays `active`, and its changes become `expired` after `RetryWindow`.
  - **US4-2**: pending changes older than `RetryWindow` become `expired` and are never sent afterwards. This also happens with the type unconfigured (edge case "Channel type configured off").
  - **US4-3**: `RejectPermanently(a)` → channel A is `failing` with `failure_reason` (`smtp_550` for email, `scripted` for test), its pending rows are `expired`, and its queued messages are deleted. Channel B of the same subscriber keeps receiving.
  - **US4-4**: a failing channel → request confirmation → confirm → `active`, and changes recorded while it was failing are not sent.
  - **US4-5, SC-004**: every change is sent exactly once per channel when:
    - `RunDueAsync` is cancelled mid-pass by a token cancelled from inside `SendAsync` of the second of three channels, **before** the inner send (for email: in the decorator, before `EmailChannel.SendAsync`), so the test never lands in FR-025's accepted-then-crash window;
    - a fresh `Notifier` then runs;
    - both of the above are repeated three times.
- [ ] T050 [P] [US4] In `InTheKnow.Tests/NotificationApiTests.cs`, add `GET /api/channel-types` (operator key; `401` without).
  - Before any attempt, the attempt fields are `null`.
  - During an email outage: `configured: true`, `lastOk: false`, `lastReason: "unreachable"`, a growing `consecutiveFailures`, `failingSince` set, and `waiting` = channels with pending deliveries + unsent messages.
  - After recovery: `lastOk: true` and `consecutiveFailures: 0` (US4-7).
  - The body holds no address or subscriber id.
- [ ] T051 [P] [US4] In `InTheKnow.Tests/ChannelsTests.cs`, add:
  - `ConfirmAsync` from `failing` clears `failure_reason` (DB constraint);
  - `RequestConfirmationAsync` from `failing` is allowed;
  - `UnsubscribeAsync` from `failing` → `unsubscribed`.

### Implementation for User Story 4

- [ ] T052 [US4] In `InTheKnow/Notifier.cs`, add expiry. Every minute of pass time, **for every registered type, configured or not**, run:
  ```sql
  UPDATE delivery d SET status = 'expired', status_at = $now FROM detected_change c
  WHERE d.status = 'pending' AND c.id = d.change_id AND c.detected_at < $now - RetryWindow
  ```
  The queued messages are covered by T034.
- [ ] T053 [US4] In `InTheKnow/Notifier.cs`, handle each non-`Sent` result, the same for queue messages and digests (contracts/channel-type.md table).
  - **`TargetFailed`**: roll back the send transaction. In a new statement set `failed_attempts = failed_attempts + 1, next_attempt_at = now + min(1 min × 2^(n−1), 1 h)`.
  - **`TargetRejected`**: roll back. In a new transaction:
    - lock the channel;
    - set `status = 'failing', status_since = now, failure_reason = coalesce(Reason, 'rejected')`;
    - set its pending deliveries to `expired`;
    - delete its unsent `channel_message` rows.
  - **`TypeUnavailable`**: roll back. In the in-memory state per type, increase `failures` and set `nextTry = now + min(1 min × 2^(n−1), 1 h)`. While `now < nextTry`, the pass skips that type's queue and digests, and drops its batch of due channels, so the next query orders them again.
    - Also give the channel whose send hit it a channel backoff, as for `TargetFailed`. Its status never changes (FR-027, US4-6).
    - Because the due and queue queries order by `failed_attempts`, the next attempt goes to another channel. A real outage fails that one too, and the type keeps waiting. A single bad message only delays its own channel until its changes expire (research R4).
  - **An exception from `SendAsync`**, other than cancellation or an `Npgsql`/`PostgresException`, counts as `TargetFailed("exception")`. Only the exception type name is logged, never its message (FR-031).
  - **`Sent`**: resets the channel's `failed_attempts`/`next_attempt_at` and the type's backoff.
  - Log every non-`Sent` result as `"Channel {ChannelId} ({Type}): send failed with {Reason}"`, and nothing else (FR-030a, FR-031).
  - A `Npgsql`/`PostgresException` (including `40P01`) rolls back that one channel, is logged at Warning without data, and counts as no attempt.
  - Makes T049 pass, including its "bad message" case.
- [ ] T054 [US4] In `InTheKnow/Notifier.cs`, track the operator status per type in memory: `LastAttemptAt`, `LastOk`, `LastReason`, `ConsecutiveFailures` and `FailingSince` (set on the first failure after a success). `StatusAsync` adds `Configured` and `Waiting`:
  ```sql
  SELECT (SELECT count(DISTINCT d.channel_id) FROM delivery d JOIN channel ch ON ch.id = d.channel_id WHERE d.status = 'pending' AND ch.type = $1)
       + (SELECT count(*) FROM channel_message m JOIN channel ch ON ch.id = m.channel_id WHERE m.sent_at IS NULL AND ch.type = $1)
  ```
- [ ] T055 [US4] In `InTheKnow/NotificationApi.cs`, add `GET /api/channel-types` (operator) returning `{ items: [TypeStatus] }` per contracts/api.md. Makes T050 pass.
- [ ] T056 [US4] Makes T051 pass. The code comes from T020 and T033. Fix any gap it shows in `Channels.cs`.

**Checkpoint**: outages, rejections and restarts behave per FR-025 to FR-027, and the operator status is visible.

---

## Phase 7: User Story 5 - Mass changes stay readable and within limits (Priority: P2)

**Goal**: one message per channel per batch, at most 50 changes listed with an "N more" line, and never more than the configured messages per minute.

**Independent Test**: 300 changes × 1,000 subscribers: one email each, at most 50 listed, and the rate never exceeded.

### Tests for User Story 5

- [ ] T057 [P] [US5] In `InTheKnow.Tests/EmailChannelTests.cs`, add: a `Message` with 50 listed changes and `NotListed = 7` renders all 50 and the closing line `and 7 more changes`. With `Language = "nl"` it is `en nog 7 wijzigingen` (needs T046). The subject counts 57.
- [ ] T058 [P] [US5] In `InTheKnow.Tests/DeliveryTests.cs`, add over both types:
  - **US5-2, FR-015**: 60 due changes → one message with the first 50 in detection order and `NotListed = 10`. All 60 rows are `sent`.
  - **US5-3, FR-016**: with `MaxMessagesPerMinute = 6` and 20 due channels, passes every 1 s of fake time for 60 s send at most 6 messages per rolling minute and at least 5 in total. The rest stay `pending`, not dropped, and follow in later minutes.
  - A queued confirmation takes its slot before due digests.
- [ ] T059 [US5] In `InTheKnow.Tests/DeliveryTests.cs`, add `[Fact(Explicit = true)] MassChange` (SC-007), for the `test` type only, so it needs no SMTP:
  - 1,000 subscribers with one active channel each, on one calendar;
  - one check records 300 changes. Build an edited fixture in code or use a generated feed through `FakeHttp`;
  - **log the fan-out duration** of that check through `TestContext.Current.SendDiagnosticMessage` (research R1);
  - run passes with a fake clock advancing 1 s at `MaxMessagesPerMinute = 60`;
  - assert exactly 1,000 messages, each listing 50 with `NotListed = 250`, and never more than 60 within any 60 s.

  Record the measured fan-out time in this task when it runs, as 002 T040 did.

### Implementation for User Story 5

- [ ] T060 [US5] In `InTheKnow/Notifier.cs`, cap each digest at 50: list the first 50 remaining changes in detection order, set `NotListed` to the rest, and mark all of them `sent` (FR-015). Makes T057 and T058's cap case pass.
- [ ] T061 [US5] In `InTheKnow/Notifier.cs`, add rate slots.
  - Per type, keep an in-memory `nextFree`. Before each send:
    - if `nextFree > now`, stop that type's sending for this pass;
    - otherwise set `nextFree = max(nextFree, now) + 1 min / MaxMessagesPerMinute`.
  - Queue messages run before digests within a pass (research R6).
  - Add `ponytail:` single sequential sender, one connection per message: add per-type workers or MailKit when the limit exceeds what the loop reaches (research R6).
  - Makes T058 and T059 pass.

**Checkpoint**: semester re-publishes produce one capped email per channel within the configured rate.

---

## Phase 8: User Story 6 - New channel types plug in without rework (Priority: P2)

**Goal**: prove the seam: email and the test-only type get identical behaviour side by side, and only configured production types are offered.

**Independent Test**: one subscriber with an email and a test-type channel. Both receive the same changes, and a failure on one does not touch the other. The channel-types list shows only email in production.

### Tests for User Story 6

- [ ] T062 [P] [US6] In `InTheKnow.Tests/DeliveryTests.cs`, add a non-theory test with **both** `EmailUnderTest` and `TestTypeUnderTest` registered in one app:
  - **US6-1**: one subscriber with an active email channel and an active `test` channel gets the same changes, in the same `CalendarGroup`s, in both messages.
  - **US6-2**: `FailTemporarily` and then `RejectPermanently` on the test channel give the same `failed_attempts`/`next_attempt_at` sequence and the same `failing` status that the email theory produced, while the email channel keeps receiving.
  - An outage of the test type alone does not back off email.
- [ ] T063 [P] [US6] In `InTheKnow.Tests/NotificationApiTests.cs`, add `GET /api/subscribers/{s}/channel-types` (US6-3):
  - the production registration with email configured → `{ "items": [ { "type": "email" } ] }`;
  - email unconfigured → `{ "items": [] }`;
  - with `RecordingChannel` added in the test → both are listed;
  - `POST …/channels` with `type: "test"` in the production registration → `400 invalid_type`.

### Implementation for User Story 6

- [ ] T064 [US6] In `InTheKnow/NotificationApi.cs`, add `GET /subscribers/{subscriber}/channel-types` returning the registered types where `Configured`, ordered by name. Makes T063 pass. T062 needs no production code. If it fails, the shared logic in `Notifier.cs` is type-specific somewhere, so fix it there, not in a type.

**Checkpoint**: SC-008. The delivery suite passes unchanged for both types.

---

## Phase 9: User Story 7 - Send a test notification (Priority: P3)

**Goal**: a subscriber sends a labelled test message to an active channel, at most 5 per channel per 24 h.

**Independent Test**: request a test for an active channel and see it arrive. The 6th request that day is rejected, and so is a request for a paused channel.

### Tests for User Story 7

- [ ] T065 [P] [US7] In `InTheKnow.Tests/EmailChannelTests.cs`, add: a Test email has the subject `InTheKnow test notification`, or `InTheKnow-testmelding` in Dutch (needs T046), the "this is a test" text, a link to `{FrontendBaseUrl}/notifications`, the `List-Unsubscribe` headers and no changes (FR-010, FR-023).
- [ ] T066 [P] [US7] In `InTheKnow.Tests/ChannelsTests.cs`, add `RequestTestAsync`:
  - an `active` channel → queued;
  - `paused` → `channel_not_active`, with the status in the result;
  - the 6th request within 24 h → `test_limit`;
  - an unconfigured type → `type_unavailable`.
- [ ] T067 [P] [US7] In `InTheKnow.Tests/NotificationApiTests.cs`, add `POST …/channels/{id}/test`:
  - `202`;
  - `409 channel_not_active` with the extra field `"status": "paused"` (US7-2);
  - `429 test_limit` (US7-3);
  - `503 type_unavailable`.
- [ ] T068 [P] [US7] In `InTheKnow.Tests/DeliveryTests.cs`, add over both types (US7-1): a requested test delivers one `MessageKind.Test` message in the subscriber's language, with `UnsubscribeUrl` and `SettingsUrl = {FrontendBaseUrl}/notifications` and no calendars. A test requested and then followed by a pause before the pass is dropped unsent. After a sending pass, the `test` row has `sent_at` and `language` set, and a second pass sends nothing more.

### Implementation for User Story 7

- [ ] T069 [US7] In `InTheKnow/Channels.cs`, add `RequestTestAsync(subscriber, channelId)`:
  - the channel must be `active`, else `channel_not_active` (return the status for the API);
  - `type_unavailable`;
  - `test_limit`: `count(*) … kind = 'test' AND requested_at > now − 24 h` ≥ 5;
  - then insert a queued `test` row.
- [ ] T070 [US7] In `InTheKnow/Notifier.cs`, build `Test` messages in the queue step, with `UnsubscribeUrl`, `SettingsUrl = {FrontendBaseUrl}/notifications` and an empty `Calendars`. In `InTheKnow/EmailChannel.cs`, render the Test body per contracts/messages.md § Test.
- [ ] T071 [US7] In `InTheKnow/NotificationApi.cs`, add `POST /subscribers/{subscriber}/channels/{channelId:long}/test` → `202`. `channel_not_active` returns `Results.Problem` with the extensions `reason` and `status`: add an `Api.Problem` overload with extra fields rather than a second helper. Makes T065–T068 pass.

**Checkpoint**: all seven stories are functional.

---

## Phase 10: Polish & Cross-Cutting

- [ ] T072 [P] Update `specs/001-ical-change-detection/contracts/detected-change.md`: `notified_at` is gone, and the delivery status per change and channel lives in `delivery` (link `specs/003-notification-channels/data-model.md`).
- [ ] T073 [P] Update `specs/001-ical-change-detection/contracts/api.md`:
  - the calendars list gains `notify`, `label` and `name`;
  - the changes route no longer mentions `notified_at`;
  - link `specs/003-notification-channels/contracts/api.md`.
- [ ] T074 [P] Update `specs/001-ical-change-detection/contracts/configuration.md` with a pointer to `specs/003-notification-channels/contracts/configuration.md`, and the `Microsoft.AspNetCore` ≥ `Warning` rule.
- [ ] T075 [P] In `InTheKnow/Api.cs`, replace the comment "Read-only: never sets notified_at, which belongs to the notification feature." with "Read-only: delivery status lives in `delivery` (003)."
- [ ] T076 [P] Add a `mailpit` service to `compose.yaml` (`image: axllent/mailpit`, ports `1025:1025` and `8025:8025`). The automated tests do not use it (research R11).
- [ ] T077 Run `dotnet build && dotnet test` from `backend/`, and fix warnings or failures.
  - Confirm that no package was added and that both `packages.lock.json` files are unchanged.
  - Grep for `ponytail:` in `Notifier.cs` and `Checker.cs`, and confirm the four comments of research R5, R6 and R10 are present.
- [ ] T078 Run the manual walkthrough of quickstart.md § 2 against Mailpit and a dev database, including `grep -E '@|/n/' app.log` finding nothing from the app. Run T059 (`MassChange`) and record its fan-out time. If the fan-out threatens the check's 2-minute limit, apply research R1's named fallback in a follow-up, not here.

---

## Dependencies & Execution Order

### Phase dependencies

- **Setup (1)** → **Foundational (2)** → user stories → **Polish (10)**.
- Within Setup, T002 depends on T001.
- Within Foundational:
  - the tests T003–T007 are parallel. The delivery-suite harness is written in US1 (T017), because it needs `EmailChannel` and `Notifier`;
  - T010 needs T009 (`Texts.FindTimeZone`);
  - T012 needs T001 and T011;
  - T013 and T014 are independent of the rest (T014 needs T011).

### User story dependencies

- **US1** needs Phase 2. It creates `Channels.cs`, `EmailChannel.cs`, `Notifier.cs` and `NotificationApi.cs`, which every later story extends.
- **US2** needs US1. **US1 and US2 are released together.**
- **US3** needs US1. It is independent of US2 apart from the same-file order below.
- **US4** needs US1. T051 and T056 use US2's `RequestConfirmationAsync` and `UnsubscribeAsync`.
- **US5** needs US1, and T046 (Dutch texts) for T057's Dutch case. It is independent of US4.
- **US6** needs US1 and US4 (T062 compares failure behaviour).
- **US7** needs US1, and T046 (Dutch texts) for T065's Dutch subject.

### Same-file ordering (never [P] against each other)

| File | Order |
|---|---|
| `Channels.cs` | T020 → T032 → T033 → T034 → T039 → T045 → T069 |
| `Notifier.cs` | T022 → T035 → T052 → T053 → T054 → T060 → T061 → T070 |
| `NotificationApi.cs` | T023 → T036 → T047 → T055 → T064 → T071 |
| `EmailChannel.cs` | T021 → T040 → T070 |
| `Texts.cs` | T009 → T046 |
| `Checker.cs` | T012 → T037 |
| `Api.cs` | T013 → T024 → T071 (the `Problem` overload) → T075 |
| `DeliveryTests.cs` | T017 → T026 → T044 → T049 → T058 → T059 → T062 → T068 |
| `EmailChannelTests.cs` | T016 → T027 → T041 → T057 → T065 |
| `ChannelsTests.cs` | T015 → T028 → T051 → T066 |
| `PublicPagesTests.cs` | T018 → T029 → T043 |
| `NotificationApiTests.cs` | T019 → T030 → T042 → T050 → T063 → T067 |
| `CheckerTests.cs` | T005 → T031 |
| `ApiTests.cs` | T002 (the fix, then the `Harness` hook) |

The [P] marks apply within one phase and across different files only. Two [P] tasks of different stories that touch the same test file run in story order.

## Parallel example: Foundational

```text
Task: "SettingsTests extension in InTheKnow.Tests/SettingsTests.cs"        (T003)
Task: "FeedParserTests extension in InTheKnow.Tests/FeedParserTests.cs"    (T004)
Task: "CheckerTests fan-out in InTheKnow.Tests/CheckerTests.cs"            (T005)
Task: "FakeSmtp in InTheKnow.Tests/FakeSmtp.cs"                            (T006)
Task: "RecordingChannel in InTheKnow.Tests/RecordingChannel.cs"            (T007)
Task: "IChannelType and Message in InTheKnow/ChannelType.cs"               (T008)
Task: "Texts (English) in InTheKnow/Texts.cs"                              (T009)
```

## Parallel example: User Story 1

```text
Task: "ChannelsTests in InTheKnow.Tests/ChannelsTests.cs"                  (T015)
Task: "EmailChannelTests in InTheKnow.Tests/EmailChannelTests.cs"          (T016)
Task: "US1 delivery theories in InTheKnow.Tests/DeliveryTests.cs"          (T017)
Task: "PublicPagesTests in InTheKnow.Tests/PublicPagesTests.cs"            (T018)
Task: "NotificationApiTests in InTheKnow.Tests/NotificationApiTests.cs"    (T019)
# then
Task: "EmailChannel in InTheKnow/EmailChannel.cs"                          (T021)  alongside T020 Channels.cs
```

## Implementation Strategy

### MVP first (US1 + US2)

1. Phase 1 and 2.
2. Phase 3 (US1). **Validate** with a local run against Mailpit: add, confirm, one digest. **Do not deploy yet**: without US2, the mail client's unsubscribe button does nothing (FR-010).
3. Phase 4 (US2). **Stop and validate**: every way out works and SC-003 holds. This is the first deployable increment.

### Incremental delivery

After the MVP, each of these is independently testable, and each only extends `Channels.cs`, `Notifier.cs`, `NotificationApi.cs`, `EmailChannel.cs` and tests:
- US3, language and time zone;
- US4, reliability and operator status. **Needed before whole-school rollout**;
- US5, caps and rate limit. **Needed before whole-school rollout**;
- US6, the seam proof;
- US7, test notifications.

## Notes

- Tests for a story are written first and should fail before its implementation.
- Constraints are stated verbatim in T001, T010 and T020. Do not loosen them in code.
- Never log or store SMTP server text, addresses, tokens or email content (FR-031, research R10).
- Commit after each task or logical group.
