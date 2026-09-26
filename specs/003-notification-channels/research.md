# Research: Notification Channels

**Spec**: [spec.md](./spec.md) | **Builds on**: [001 research](../001-ical-change-detection/research.md),
[002 research](../002-subscriber-change-filters/research.md)

No NEEDS CLARIFICATION items remain after the spec's clarification session. Each decision below settles a mechanism
the spec leaves open. One library question (R2) was evaluated; everything else uses code that already exists.

## R1. When a change becomes a delivery: fan-out at record time

**Decision**: the check transaction that records changes also creates one `delivery` row (status `pending`) per
recorded change and per channel that should get it. That means every channel that is `active` when the change is
recorded, of a subscriber who has a link to the calendar at that moment, with that subscription's notifications on.
It is one `INSERT … SELECT` in `Checker.ApplyDiffAsync`, right after the `COPY` into `detected_change`.

`COPY` returns no ids. So before the `COPY`, the transaction reads the calendar's highest change id
(`SELECT coalesce(max(id), 0) FROM detected_change WHERE calendar_id = $1`, served by the existing
`(calendar_id, id)` index), and the fan-out selects `calendar_id = $1 AND id > $watermark`. This is safe because the
transaction holds the calendar row `FOR UPDATE` (the lease fence), so no other writer records changes for that
calendar in between. It does **not** key on `detected_at = StartedAt`: tests on a fake clock can record twice at the
same instant, and that would give a later channel the earlier changes.

**Rationale**:
- FR-011 asks what held *when the change was recorded*: the subscription existed, notifications were on, the channel
  was active. Evaluating those inside the recording transaction answers exactly that, with no timestamps to compare.
  This gives "no backfill" for confirm, resume, re-enable and subscribe for free.
- The filter is **not** applied here. FR-011/FR-012 decide the outcome at send time (002 FR-007), so hidden changes
  also get a row, which later becomes `skipped (hidden)` (FR-024 records it).
- Only `Checker` records changes, so this is the only place that needs the fan-out.

**Cost** (Complexity Tracking): SC-007 (300 changes × 1,000 subscribers) inserts 300,000 narrow rows in one
statement inside a check that has a 2-minute limit. Expect low seconds. The SC-007 test measures it.
**Fallback if it is too slow**: move the fan-out out of the check into the notifier, which reads a per-calendar id
watermark. The "when recorded" conditions then need `activated_at`/`notify_since`/link `created_at` comparisons.

**Alternatives considered**:
- *Fan-out in the notifier, asynchronously*: keeps the check transaction unchanged, but "active/on/subscribed when
  recorded" must then be reconstructed from timestamps. A pause that lands before the fan-out would also leave no
  `skipped` record (US2-7).
- *No delivery rows. Derive due changes from `detected_change` plus a per-channel watermark*: FR-024 needs a status
  per change and channel (sent, skipped with reason, expired), so the rows are needed anyway.
- *Keep 001's `detected_change.notified_at`*: a single flag cannot track many channels. It is dropped (spec
  Assumptions, "Replaces 001's per-change notification status").

## R2. Sending email: `System.Net.Mail.SmtpClient`

**Decision**: the BCL `SmtpClient` with `SendMailAsync(MailMessage, CancellationToken)`, one recipient per message,
STARTTLS (`EnableSsl = true`) on a submission port (587 by default), and username/password authentication.

**Rationale**:
- Constitution I: the standard library covers everything this feature needs. That is STARTTLS, AUTH, a multipart
  text + HTML body (`AlternateViews`), custom headers (`List-Unsubscribe`, `List-Unsubscribe-Post`), and a sender
  display name.
- The failure classes FR-026/FR-027 need are visible:
  - `SmtpFailedRecipientException` with a 5xx `StatusCode` → the target is rejected permanently.
  - `SmtpFailedRecipientException` with a 4xx code → this target failed temporarily.
  - Any other `SmtpException`, a socket error or a timeout → the channel type is unavailable (temporary, type-wide).

  The exact exception types these three cases produce are **asserted in tests** against the fake SMTP server (R11),
  not assumed.
- The type is not marked `[Obsolete]`, so it builds with `TreatWarningsAsErrors`.

**Limits, recorded**: Microsoft's docs discourage `SmtpClient` for new development because it lacks modern
protocols. What matters here: no implicit TLS (port 465), no OAuth2 authentication, and one connection per message.
The configuration contract says so.

**Alternatives considered**: *MailKit* (MIT, GPL-compatible). It is the better client, but it is a new dependency
for features this feature does not need. **Switch to it when** the mail provider requires port 465 or OAuth2, or
when one connection per message limits the send rate (R6).

## R3. The channel-type seam

**Decision**: one internal interface, `IChannelType`, with exactly what differs per provider: its name, whether it is
configured, how a target is validated, and how a `Message` is delivered. Everything else lives once in the
`Notifier` and `Channels` code: which changes, filters, switches, combining, language, time zone, rate limit,
retries, expiry, tracking and statuses. The contract is [contracts/channel-type.md](./contracts/channel-type.md).

Confirming a target is, for email, delivering a `Confirmation` message whose links prove ownership. A future web
push type confirms differently: the browser's subscription is the proof. It will extend the interface then.
Nothing is added for it now.

**Rationale**: FR-003 and US6 require it. It has two implementations now: `EmailChannel`, and `RecordingChannel`,
which exists only in tests. SC-008 runs one delivery test suite over both, so the interface is not a speculative
abstraction.

**Alternatives considered**: *a `switch` on the type name*: that is fine for one type, but the test-only type cannot
then be added without production code knowing about it (US6-3).

## R4. Collection window, "due", retries and expiry, all derived from existing times

**Decision**: no per-channel window state is stored.
- A channel is **due** when its oldest `pending` delivery's change was detected at least `CollectionWindow` ago,
  counted as `min(detected_change.detected_at) + window <= now`. Changes that arrive during the window join the
  same message. A change that arrives after a message was assembled starts the next window with its own
  `detected_at`.
- **Expiry**: a `pending` delivery whose change was detected more than `RetryWindow` ago becomes `expired`
  (FR-026). This runs every minute, **whether or not the channel type is configured** (edge case "Channel type
  configured off"). Waiting for a rate-limit slot counts toward it (FR-016).
- **Per-channel backoff** (4xx for one target): `channel.failed_attempts` and `channel.next_attempt_at`. The delay is
  `min(1 min × 2^(n−1), 1 h)`. A success resets both.
- **Type-wide backoff** (server unreachable, timeout, any non-recipient SMTP error): kept in memory per type, with
  the same delay curve. It pauses that type's sending, so 1,000 channels do not each retry against a dead server,
  and it marks no channel as failing (FR-027).
- **A bad message** (one message that always fails with a type-wide error, such as a 554 after DATA) must not stall
  the type. The channel that hit the error also gets a channel backoff, and due channels are ordered by
  `failed_attempts` first, so the next attempt tests the server with another channel.
- **Permanent rejection**: the channel becomes `failing` with a reason code, and its pending deliveries become
  `expired` (FR-027).

**Every time decision uses the app clock** (`TimeProvider`, passed as a parameter as `Checker` does). This covers the
window end, expiry, backoff, the 3 and 5 per 24 h caps, the 48 h confirmation expiry and the 30-day cleanup. The
database `now()` only stamps informational times: `delivery.status_at` when a trigger writes it. The tests run on
`FakeTimeProvider`.

**Alternatives considered**: *a `window_ends_at` column set during fan-out*: an extra update of up to 1,000 channel
rows inside the check transaction, for a value `min(detected_at)` already gives.

## R5. Exactly-once sending and state changes during a send

**Decision**: the notifier handles one channel at a time, in one transaction:
1. Lock the channel row with `FOR NO KEY UPDATE`, and its pending deliveries with `FOR UPDATE` in
   `(channel_id, change_id)` order. All locks are taken before sending.
   - **Not `FOR UPDATE` on the channel.** Inserting a `delivery` row takes `FOR KEY SHARE` on its channel through
     the foreign key check. Under `FOR UPDATE`, every check that fans out to that channel would wait for the send,
     and that wait counts against the check's 2-minute limit.
   - `FOR NO KEY UPDATE` still conflicts with pause, unsubscribe and remove, which update or delete the channel row,
     so the guarantee below holds.
2. Evaluate them and build the message.
3. Send it.
4. Mark exactly the selected ids `sent` or `skipped`, set `last_message_at`, and commit.

The only duplicate possible is a message the mail server accepted right before the process stopped, before the
commit. FR-025 allows exactly that one.

Every state change that drops pending changes does so **when it happens**, as an `UPDATE … WHERE status = 'pending'`:
- pause and unsubscribe → `skipped (channel_not_active)`;
- notifications off → `skipped (notifications_off)`;
- last link removed → `skipped (subscription_removed)`, from 002's `calendar_link_drop_filter` trigger, extended.
  That one place covers `RemoveAsync`, regroup moves and cascades;
- permanent rejection → `expired`.

These updates touch rows the sender has locked, so they wait for an in-flight send and then find the rows already
`sent`.

One race remains. A check that records changes can read a channel as `active` (or `notify` as on) just before a
pause, unsubscribe or switch-off commits, and then insert `pending` rows after that state change's skip has run: the
delivery FK takes `FOR KEY SHARE`, which does not conflict with `FOR NO KEY UPDATE`. So the sender **re-checks at
send time**, under its channel lock, in this order:
- a channel that is not `active` → all its pending rows become `skipped (channelNotActive)`;
- a calendar whose `subscription_setting.notify` is false → `skipped (notificationsOff)`;
- a calendar that is no longer in the subscriber's `LoadFiltersAsync` result → `skipped (subscriptionRemoved)`;
- the filter → `skipped (hidden)`.

This is what FR-011's "still … when it is sent" and FR-012 require. A pause therefore lands either before a message
(and nothing is sent) or after it.

**Database errors** in the send transaction, including a deadlock (`40P01`) between switching notifications off
and the sender's row locks, roll back the transaction and are retried on the next pass.
- They are never counted as `TypeUnavailable`.
- Every lock is taken before sending, so a deadlock always hits before an email leaves.

**Cost**: a row lock and a pooled connection are held across one SMTP send (at most the 30 s send timeout). An API
pause or switch-off can wait that long. Accepted: `ponytail:` lock across send, and **split into claim/commit** if
API latency during outages matters.

**Alternatives considered**:
- *An outbox table with claim and commit steps*: more states and code for the same guarantee.
- *Re-checking the channel state after the send*: that detects the race but cannot undo a sent email.

## R6. Rate limit and sender concurrency

**Decision**: one notifier loop sends sequentially. Each channel type has an in-memory next-free slot, spaced
`1 min / MaxMessagesPerMinute` apart (default 60/min, one per second), the same scheme as 001's `HostBudget`.
Confirmation and test messages take slots too, and go before change digests. Nothing is dropped: a message over
the limit waits for its slot (FR-016).

**Finding due channels**: one query returns up to 100 due channel ids of a type, oldest first. The loop works
through that batch before it queries again.
- It is not one query per message, because the pending set is large exactly when it matters: about 300k rows during
  SC-007, and a day of changes × channels during an outage.
- `ponytail:` the due query groups every pending row. **Add** a stored per-channel due time if it shows up in
  profiles.

**Rationale**: at 60/min, one sequential sender with one SMTP connection per message is fast enough. SC-007's
1,000 emails take about 17 minutes by design (the limit is reached), and SC-006 assumes the limit is not reached.

**Ceiling**: sequential sending with one connection per message tops out at a few hundred messages per minute.
`ponytail:` single sequential sender. **Add** per-type workers or connection reuse (MailKit, R2) when an operator
raises the limit past what the loop reaches.

`HostBudget` is not reused: it is keyed and configured per source host. The spacing it does is three lines.

## R7. Consent tokens and the unsubscribe token

**Decisions**:
- **Confirmation tokens**: 32 random bytes (`RandomNumberGenerator`), base64url, one per confirmation email. Only
  the SHA-256 is stored, on the `channel_message` row. The token is created by the sender inside the send
  transaction, right before sending. If the send fails, the transaction rolls back and the next attempt creates a
  new token. A crash between sending and committing leaves one emailed link that no longer works, and the email is
  sent again with a fresh link (the FR-025 exception). A token expires 48 h after its email was sent (FR-005). A
  newer confirmation email does not invalidate older unexpired ones. Confirming a used token or an already active
  channel changes nothing and says so.
- **"This wasn't me"** uses the same token on a different path. It works for as long as the token row exists
  (30 days, R10), not only for 48 h, because opting out must stay possible. It sets the channel to `unsubscribed` and
  stores the declined hash.
- **Declined address**: `SHA-256(subscriber + "\n" + lowercase(address))`, stored per subscriber (FR-006). Salting
  with the subscriber id means equal addresses of different subscribers do not correlate. This is not a secret
  keyed hash: the spec asks only that the address itself is not kept.
- **Unsubscribe token**: 32 random bytes per channel, stored **as is** (`channel.unsubscribe_token`, unique),
  because every non-confirmation email must carry it and the sender builds emails asynchronously. It is a
  capability for one action only, opting that one channel out, so leaking it is low-harm. The data model names
  this.

**Alternatives considered**: *HMAC-signed stateless tokens*. They need a new operator secret and key rotation, and
single use still needs state.

## R8. The routes that work without login

**Decision**: they live **outside `/api`**, under `/n/`, so 001's operator-key middleware (a prefix match on `/api`)
does not apply:

| Path | `GET` | `POST` |
|---|---|---|
| `/n/confirm/{token}` | Page with one "Confirm" button | Confirms (FR-007) |
| `/n/decline/{token}` | Page with one "This wasn't me" button | Declines |
| `/n/unsubscribe/{token}` | Page with one "Unsubscribe" button | Unsubscribes. Also accepts the RFC 8058 one-click body `List-Unsubscribe=One-Click` |

`GET` never changes anything (US2-4, link scanners). Pages are minimal server-rendered HTML with every inserted
value HTML-encoded. Their language is the language of the email they came from. For unsubscribe it is the
subscriber's current language. Links in emails use the operator setting `PublicBaseUrl`, which must be `https`
except on localhost. The reverse proxy exposes `/n/` publicly and nothing else from the backend.

**Logs**: tokens sit in URL paths. ASP.NET Core logs request paths only at `Information` for `Microsoft.AspNetCore`,
which `appsettings.json` already keeps at `Warning`. The configuration contract makes that a rule, and a
captured-log test runs the confirm, decline and unsubscribe flows and asserts no token or address appears
(FR-031, SC-010).

**No CSRF token**: the path token is the only credential, so a forged POST needs the token anyway. The three `POST`
routes bind **no form data**. The action does not depend on the body, and the one-click body
`List-Unsubscribe=One-Click` is accepted without being parsed. They are also marked `.DisableAntiforgery()`. Minimal
API endpoints that bind form data get antiforgery metadata, and without the antiforgery middleware that throws at
runtime. A test posts the one-click form body to prove it works.

## R9. Text, languages and time zones

**Decisions**:
- **Fixed text**: one static C# class `Texts` with an English and a Dutch dictionary. It holds email text, subjects,
  change kinds, outcomes and page text. A unit test asserts that both have the same keys (SC-009). No
  `.resx`: satellite assemblies and `ResourceManager` add ceremony for two languages.
- **Dates**: format `ddd d MMM yyyy HH:mm` with culture `en-GB` or `nl-NL` (for example "Tue 6 Oct 2026 08:30" and
  "di 6 okt 2026 08:30"). This needs ICU, which .NET on Linux uses by default. One test asserts the Dutch output, so a
  container image without ICU fails loudly (Docker later).
- **Time zones**: `TimeZoneInfo.TryFindSystemTimeZoneById`, accepted only if `HasIanaId` is true. On Linux .NET also
  resolves Windows ids, so Windows ids are rejected. The canonical id is stored. `null` means the operator default
  (`DefaultTimeZone`, validated at startup). Conversion with `TimeZoneInfo.ConvertTime` handles summer and winter
  time. This needs tzdata in the image, which the same test catches.
- **Plain text from the calendar**: the HTML part encodes every calendar value (`HtmlEncoder.Default`), so it cannot
  add markup or links (FR-020). **Known limit**: mail clients may still auto-link text that looks like a URL. That
  cannot be prevented from the sender side, and it is accepted.
- **Labels** (FR-019a): 1 to 100 characters, **and no control characters**. 002's name check tests only the length,
  and labels reach email text and subjects. **Feed names** (`X-WR-CALNAME`) have control characters removed, are
  trimmed, and are cut to 100 characters when stored. So no subject can contain CR/LF, which `MailMessage` would
  reject at send time (a message that could only fail until it expired). A test asserts that a CR/LF subject throws,
  to confirm the reason for the check.

## R10. Limits, cleanup and small decisions

- **Channel cap (5)** is a trigger (data-model § Cross-table rules): an advisory lock per subscriber, then a count,
  so concurrent adds cannot reach 6. The **send caps** (3 and 5 per 24 h) are rate limits on the app clock, not
  integrity rules, and stay application checks as in 002 R7.
- **Confirmation cap**: every confirmation email counts, **including the initial one**, at most 3 per channel per
  24 h. That allows 2 re-requests on the first day. It satisfies "at most 3" (FR-006) with the simplest count.
- **Test cap**: 5 per channel per 24 h (FR-023), counted in `channel_message`.
- **Cleanup** (every 10 minutes, app clock):
  - delete channels that have been `awaiting_confirmation` or `unsubscribed` since before `now − 30 d` and have no
    confirmation email sent within 30 days (FR-029a);
  - delete `channel_message` rows older than 30 days;
  - delete unsent `channel_message` rows older than `RetryWindow` (their email expired).
- **Growth**: hidden changes also get delivery rows, so delivery rows grow with changes × subscribers × channels,
  and they are removed with their change (retention, one year by default). Rough ceiling: tens of millions of narrow
  rows per year at whole-school scale. `ponytail:` delivery rows kept for the change's retention. **Add** a shorter
  retention for finished deliveries (anything not `pending`) if the table size matters. FR-025 only needs rows while
  a change can still be sent.
- **Operator status** (FR-030a) is kept **in memory** per type (latest attempt, outcome, reason, consecutive
  failures and since when). "Waiting" is counted from the database. After a restart the status shows no attempts
  until the next send. Accepted: the spec asks for no persistence.
- **Failure reasons** are codes, never server text: `unreachable`, `timeout`, `smtp_<code>` (for example
  `smtp_550`). SMTP replies often echo the address, so `exception.Message` is never logged or stored (FR-031).
- **One recipient per message**: each message goes to one channel. No To/Cc list ever reveals another subscriber
  (FR-020, US1-7).

## R11. Testing the email channel without a real server

**Decision**: a small fake SMTP server in the test project (`FakeSmtp`, a `TcpListener` that speaks EHLO, MAIL, RCPT,
DATA and QUIT without TLS). It captures messages and can be scripted to refuse connections, answer RCPT with a
chosen 4xx or 5xx per address, or fail at DATA. The delivery tests run as a theory over a "channel under test"
harness with two implementations: email over `FakeSmtp`, and the test-only `RecordingChannel`. Both capture messages
and inject temporary, permanent and type-wide failures (SC-008).

For the manual walkthrough, `compose.yaml` gains a **Mailpit** service (MIT) to view real rendered emails. It is
dev tooling only. No automated test depends on it.

**Alternatives considered**: *Mailpit for the automated tests*: its failure injection is not per address, and it
would make the tests depend on a second container.

## R12. What changes in 001 and 002 code

- `FeedParser` returns the feed's `X-WR-CALNAME`. `Checker` stores it as `calendar.feed_name` on every successful
  check, and `Subscriptions.AddLinkAsync` stores it on a new calendar's baseline. It is not part of the snapshot
  hash, so a new name never records a change (FR-019a, 001 FR-018).
- `Checker.ApplyDiffAsync` does the fan-out (R1). `Checker.RegroupAsync` copies the `subscription_setting` row
  before moving a link, exactly like the filter copy, and an existing row on the target wins.
- `GET /api/subscribers/{s}/calendars` (001) gains `notify`, `label` and `name` (FR-028).
- `detected_change.notified_at` is dropped. 001's `contracts/detected-change.md` and the comment on the changes
  route in `Api.cs` are updated.
