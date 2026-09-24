# Research: Timetable Change Detection

**Date**: 2026-09-24 | **Spec**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md)

Each entry uses Decision / Rationale / Alternatives considered. Everything marked "verified" was run in a
throw-away spike against Ical.Net 5.2.3 on .NET 10, using one live MyTimetables feed (English, 73 events) and a
synthetic generic feed.

---

## R1. Runtime and hosting model

- **Decision**: .NET 10 (LTS, SDK 10.0.1xx installed). One ASP.NET Core project (`Microsoft.NET.Sdk.Web`,
  `WebApplication.CreateBuilder`). It serves the minimal-API endpoints ([contracts/api.md](./contracts/api.md)) and
  runs the checker as a `BackgroundService` in the same process.
- **Rationale**:
  - The owner wants an API that the later SvelteKit frontend builds on (spec FR-030).
  - The ASP.NET Core shared framework already contains hosting, configuration, logging and Problem Details. The Web SDK therefore **removes** the separate
    `Microsoft.Extensions.Hosting` package rather than adding anything.
  - One process is enough at school scale (R19), and the deployment stays a single container or service.
  - **Exactly one instance is deployed.** `HostBudget` lives in memory, migrations and the regroup run at startup,
    and the checker is in-process. All of these assume one instance. The advisory lock (R4) only guards against
    an accidental second one.
- **Alternatives considered**:
  - A separate worker process next to the API. That would mean two deployables sharing one database. Split them
    only if API traffic and checking ever need to scale independently. That would also require moving the budget
    into the database, and running migrations and the regroup under the lock.
  - Keeping the operator CLI next to the API. That would be two ways of doing the same thing, so the API replaces
    it (R15).

## R2. iCalendar parsing and recurrence expansion

- **Decision**: **Ical.Net 5.2.3** (MIT). Its only runtime dependency is **NodaTime 3.2.2** (Apache-2.0).
- **Rationale**: Getting RRULE, EXDATE and RECURRENCE-ID overrides, VTIMEZONE/TZID resolution and DST right by
  hand is a large, bug-prone task. Ical.Net is the maintained .NET library for this.
- **Verified API usage**:
  - `Calendar.Load(string)` parses the real feed: 73 events and `X-WR-TIMEZONE=Europe/Brussels`, read via
    `calendar.Properties["X-WR-TIMEZONE"].Value`.
  - `CalDateTime.AsUtc` resolves `TZID=Europe/Brussels` correctly across DST: 1 March 08:30 is 07:30Z and
    1 April 08:30 is 06:30Z.
  - `calendar.GetOccurrences<CalendarEvent>().TakeWhileBefore(horizon)` expands a weekly RRULE, drops the EXDATE,
    and replaces the overridden occurrence with the RECURRENCE-ID component (`occurrence.Source`, whose
    `RecurrenceIdentifier` holds the original start). The occurrence start and end come from
    `occurrence.Period.StartTime` / `EffectiveEndTime`. Note that `RecurrenceId` is obsolete in 5.x; use
    `RecurrenceIdentifier`.
  - For floating times, `CalDateTime.IsFloating == true`. Resolve them with
    `new CalDateTime(dt.Date, dt.Time, <X-WR-TIMEZONE>).AsUtc`. Verified: 30 March 09:00 in Amsterdam is 07:00Z.
  - Date-only (all-day) values have `HasTime == false` and `IsFloating == true`.
    - `new CalDateTime(date, null, tz)` **ignores the zone** and gives UTC midnight.
    - Use `new CalDateTime(date, new TimeOnly(0, 0), tz).AsUtc` for local midnight. Verified: 1 April 2026 gives
      2026-03-31T22:00Z.
  - An all-day event without `DTEND` gets `EffectiveEndTime` = the next day (verified).
  - An override occurrence exposes its original start through `RecurrenceIdentifier.StartTime` (verified).
    Single events have `RecurrenceRules.Count == 0`, no `RecurrenceDates` and `RecurrenceIdentifier == null`.
  - A missing `SUMMARY` gives an empty summary. Store it as `""`.
  - An open-ended `FREQ=DAILY` series that started in 2020 produced 2,251 occurrences up to the horizon. This is
    manageable, but it means the occurrence count must be capped (R10).
- **Gotcha, verified**: Ical.Net assigns a **random GUID** as `Uid` to a VEVENT without a UID, and the property is
  then indistinguishable from a real one. Handling is covered in R6.
- **Gotcha, verified**: a single event with `DTEND < DTSTART` makes the **whole** calendar-wide occurrence
  enumeration throw (`ArgumentException: Duration must be >= 0`). Before expanding, every such event has its
  `DtEnd` set to its `DtStart` on the parsed object (R8a). Evaluation exceptions that remain fail the check as
  `unparseable`.
- **Gotcha, verified**: `TakeWhileBefore` on the calendar-wide enumeration cuts off *all* events, including
  non-recurring ones, at the horizon. A single event in 2030 did not appear with a 2027 horizon. The spec only
  bounds recurring expansion (FR-015a), so single events that start at or after the horizon are added separately
  (R7).
- **Alternatives considered**: A hand-written parser. That would be fine for MyTimetables, which has no RRULE in
  events (the only 2 RRULEs are inside VTIMEZONE), but not for generic feeds (SC-007). The other libraries
  (DDay.iCal, the ancestor of Ical.Net, and ical.net forks) are unmaintained.

## R3. Database access and migrations

- **Decision**: **Npgsql 10** (PostgreSQL License) directly, with no ORM or micro-ORM. Schema changes are numbered
  `.sql` files embedded in the assembly. A roughly 25-line runner applies them in order, in one transaction each,
  and records them in `schema_migration`. Bulk writes (snapshots, changes) use Npgsql binary `COPY`.
- **Rationale**: There are about 14 queries in total:
  - checker: due calendars, links, snapshot, snapshot upsert/delete, change insert, outcome update, retention
    delete;
  - API: add link, list calendars, delete subscription, page changes, operator overview.

  Mapping these readers by hand is less code than setting up and learning an ORM. Constraints live in SQL, where
  the constitution wants them. Revisit Dapper or EF Core with the login feature: ASP.NET Core Identity's stores
  are EF Core-based, and if that feature adopts them, EF Core arrives anyway.
- **Alternatives considered**:
  - EF Core with its migrations: generated migration code, change tracking we don't need, and a larger dependency
    tree.
  - Dapper: would save about 40 lines of reader mapping. Not worth a dependency at this size; revisit if the query
    count grows.
  - DbUp: a dependency for what a 25-line loop does.

## R4. Scheduling: concurrency, time limits, the per-host budget and the single-instance guard

- **Decision**:
  - **A dispatcher with a worker limit** replaces tick-and-batch processing. It is a `BackgroundService` loop:
    1. Wait for a free slot. At most `MaxConcurrentChecks` checks run at once (default 16, `SemaphoreSlim`).
    2. Claim the longest-waiting due calendar whose host has a free budget slot, in one statement:
       `UPDATE calendar SET next_check_at = @now + lease, lease_id = @newLease WHERE id = (SELECT id FROM
       calendar WHERE next_check_at <= @now AND host <> ALL(@busyHosts) ORDER BY next_check_at LIMIT 1
       FOR UPDATE SKIP LOCKED) RETURNING …`. `@newLease` is a fresh `Guid`. The lease is 5 minutes (constant)
       and longer than the check time limit, so a calendar can't be claimed twice. If the process crashes
       mid-check, the calendar comes back after the lease expires.
    3. Take the host's budget slot, start the check as its own task, and go back to step 1 **without waiting for
       it**. When the check ends, it releases the worker slot.
    4. When nothing is claimable, sleep until the earliest of: the next host slot becomes free, or 1 s
       (constant). Idle cost is one indexed query per second.
  - **After each check**: `next_check_at = check_started_at + interval`, where interval is the check interval, or
    the failing-calendar interval when status is `failing`. A calendar that was delayed therefore keeps its new,
    later slot, and a bunch of calendars that fell due together stays spread out afterwards (FR-012b).
  - **New calendars** added through a link are baselined from the validation fetch and get
    `next_check_at = now + interval` (R11); adds happen at user-chosen moments, so they are naturally spread.
    Calendars created by a regroup (R13) have no baseline and get `next_check_at = now + random(0, interval)`.
  - **Check time limit (FR-014a)**: 2 minutes per check (constant), covering fetch, parse, diff and database
    write. It is enforced two ways:
    - The check runs under a `CancellationTokenSource(limit, timeProvider)`. The fetch has its own 30 s timeout
      (R5), and Npgsql commands take the token and have a 30 s command timeout. The token is checked between
      stages. Checking a token is not a guarantee, though; the fence below is.
    - The dispatcher's wrapper awaits the check with `WaitAsync(limit)`. If the limit passes, it records the
      failure `timeout` in its own short transaction, schedules the next check normally and **releases the
      worker slot**, even if the check's code is stuck somewhere that ignores cancellation, such as CPU-bound
      parsing inside Ical.Net.
    - The wrapper **releases the worker slot first**, then writes `timeout`. That write uses only the stopping
      token, never the check's own token, which is already cancelled at that point and would make the write fail.
      Its fence is a plain blocking `FOR UPDATE`: if the stuck check is inside its outcome transaction, the
      cancellation rolls that transaction back and the wrapper then records `timeout`; if the check committed
      first, its lease is cleared and the wrapper writes nothing. Waiting on the lock costs no worker slot.
    - **Lease fence**: every write for a check runs in a transaction that begins with
      `SELECT … FROM calendar WHERE id = @id AND lease_id = @lease FOR UPDATE`. If no row comes back, the check
      has lost its lease and writes **nothing**. This applies to the success transaction (R14) and to the
      wrapper's `timeout` write. Every outcome write also sets `lease_id = NULL`.
      - Whichever of the two takes the row lock first wins, and the other becomes a no-op. A timed-out check can
        never commit changes, and an orphan that resumes after the calendar was claimed again can't commit
        stale data.
      - A dedicated `uuid` column is used rather than comparing `next_check_at`, because .NET ticks are finer
        than PostgreSQL's microseconds, so a round-tripped timestamp would not match.
    - The abandoned task ends when its current operation returns. Parsing is bounded by the 10 MB and
      50,000-occurrence limits (R10).
    - One stuck check therefore costs at most one worker slot for 2 minutes and never blocks the dispatcher or
      other calendars (SC-012).
  - **Per-host budget (FR-012a, FR-012b)**: `HostBudget` is about 20 lines, in memory, and takes `now` as a
    parameter:
    - It keeps a lock-guarded dictionary from host to `nextFree` (an instant). The spacing is
      `gap(host) = 1 minute / budget(host)`, for example 0.5 s at 120/min.
    - `budget(host)` is the domain entry's `MaxRequestsPerMinute` when the host is a configured domain that
      sets one, and otherwise `MaxRequestsPerHostPerMinute`. Both are read at startup. After a restart with new
      values, spacing follows the new gap straight away (`nextFree` is in memory and starts empty). Existing
      calendars keep their place in line, and because each next check counts from when the check actually ran,
      the schedule settles at the new pace without any migration.
    - **Checker**: a host is claimable only when `nextFree <= now`. Taking a slot sets
      `nextFree = max(now, nextFree) + gap`. Hosts with `nextFree > now` are passed as `@busyHosts`, so their due
      calendars keep their place in line and aren't claimed and put back.
    - Known ceiling: while the main host is busy, a claim pass that finds nothing walks that host's whole due
      backlog in the `next_check_at` index (`host <> ALL` cannot use an index). That is about two scans a second
      over a few thousand entries, milliseconds each. Marked with a `ponytail:` comment in `Checker.cs`.
    - **Validation** (adding a link): reserves the next slot, `slot = max(now, nextFree)`, and waits for it
      (`Task.Delay` on the `TimeProvider`) if that is at most 5 s away. Otherwise it returns `429`
      (`host_budget_exhausted`). A reserved slot pushes `nextFree` forward, so validations queue ahead of the
      next checks and are never starved, and the host never gets more than the budget.
  - **Why this spreads bunches**: suppose 300 calendars on one host fall due in the same second, for example
    after a restart, and the budget is 120/min. They go out one every 0.5 s, longest-waiting first, and all are
    done in 2.5 minutes. None is skipped. Their next checks land 0.5 s apart, one interval later. Without budget
    pressure, the worker limit and the spread-out first checks do the same job.
  - **Single-instance guard**: at startup the app takes `pg_try_advisory_lock(<constant>)` on a dedicated
    connection that it holds for its lifetime. If the lock is held, it exits with an error. One instance is the
    deployment model (R1). The lock stops an accidental second instance from recording every change twice
    (FR-025) and from racing migrations.
- **Clock**: every time decision takes its time from the BCL `TimeProvider`, passed as a parameter to SQL rather
  than read from `now()` in SQL. That covers claiming, the lease, `next_check_at`, the retention cutoff,
  `detected_at`, the first check of a new calendar, the horizon and `HostBudget`. The checker exposes
  `RunDueChecksAsync(DateTimeOffset now)`. It runs dispatch passes at `now` until nothing is claimable, then
  awaits the checks it started. The background loop runs the same passes without awaiting the checks.
  - **Timers use the same provider**: `Task.WaitAsync(limit, timeProvider)`,
    `new CancellationTokenSource(limit, timeProvider)`, and `Task.Delay(…, timeProvider)` for validation waits and
    the dispatcher sleep.
  - **Tests use `FakeTimeProvider`** from **Microsoft.Extensions.TimeProvider.Testing** (MIT, test only).
    Verified on net10: after `Advance(5 s)` the delay has completed, and after `Advance(2 min)` the
    `CancellationTokenSource` is cancelled and `WaitAsync` has thrown `TimeoutException`. A hand-written subclass
    that only overrides `GetUtcNow()` would never fire these timers.
  - `HttpClient.Timeout` and Npgsql's command timeout run on real time. Test fakes (the fake HTTP handler, the
    stuck-check hook) therefore honour the cancellation token rather than rely on those timeouts.
  - Scheduling tests (SC-003, SC-012, SC-013, US3-1, US3-4) run without waiting in real time.
- **Rationale**:
  - A continuous dispatcher means a slow check occupies one slot, not a whole batch. With tick-and-batch, the
    next batch waited for the slowest check in the previous one.
  - Claiming with `SKIP LOCKED` and a lease is one statement. It prevents double checks, and a crash is
    recovered by the lease expiring.
  - Spacing, rather than a per-minute window, removes the burst at the start of each minute and gives a hard
    ceiling on what any one source server receives.
  - Leaving budget-blocked calendars untouched preserves fairness (oldest first) without extra state.
  - Persisting `next_check_at` gives FR-013 (at most one scheduled fetch per interval) and SC-003, even across
    restarts.
- **Alternatives considered**:
  - The earlier tick every 30 s with `Parallel.ForEachAsync` per batch: one slow check held back the next batch,
    and a budget-blocked calendar waited a whole tick.
  - The earlier fixed per-minute window: it allowed a whole minute's budget in one burst.
  - The earlier "skip and retry next tick" behaviour: technically not skipped forever, but it retried at tick
    granularity rather than at the host's next free slot.
  - A startup step that spreads overdue calendars randomly over the interval: removed, because spacing already
    spreads a post-restart bunch at the budget's pace, oldest first.
  - Deferring a blocked calendar by moving its `next_check_at` forward: that loses its place in line.
  - Quartz.NET or Hangfire: dependencies, and they solve distributed scheduling that we don't need.
  - `System.Threading.RateLimiting` (in the shared framework): verified to have no `TimeProvider` hook, so tests
    would need real waiting.
  - A per-host concurrency cap instead of a rate: it doesn't bound the request rate when fetches are fast.
## R5. Fetching: bounds and SSRF protection

- **Decision**:
  - One shared `HttpClient` over a `SocketsHttpHandler`, with no `IHttpClientFactory`.
  - Client settings:
    - `Timeout = 30 s`
    - `MaxResponseContentBufferSize = 10 MB`
    - automatic decompression
    - `MaxAutomaticRedirections = 5`
    - a descriptive `User-Agent`
  - A `ConnectCallback` resolves DNS and refuses to connect to any address that is:
    - loopback;
    - private (10/8, 172.16/12, 192.168/16, fc00::/7);
    - link-local (169.254/16, fe80::/10);
    - CGNAT (100.64/10);
    - unspecified, multicast or broadcast;
    - an IPv4-mapped IPv6 form of any of the above.
  - At link-add time the scheme (`https` only) and the resolved addresses are also checked, so the user gets an
    immediate, specific rejection (FR-001).
  - `PooledConnectionLifetime = 5 min`, so pooled connections are replaced and DNS changes at the source are
    picked up. Every new connection passes the `ConnectCallback` again.
- **Rationale**:
  - Checking at connect time also covers redirects and DNS rebinding, which checking only at add time would miss.
  - A plain `HttpClient` avoids the factory's default logging of full request URIs at Information level, which
    would break FR-028.
  - Both bounds are built into the handler (FR-014).
- **Alternatives considered**:
  - Checking only at add time (bypassed by redirects and rebinding).
  - `IHttpClientFactory` with log filters (more configuration to get the same privacy result).

## R6. Event identity

- **Decision**:
  - A **non-recurring event** is keyed by its `UID`.
  - A **recurring occurrence** is keyed by `UID|<original start in UTC>`. The original start is
    `RecurrenceIdentifier` for overrides, otherwise the occurrence start. A moved occurrence therefore keeps its key
    and shows up as a time change, not as a removal plus an addition.
  - Keys longer than 1,000 characters are hashed (R8a).
  - **Missing UID**: before `Calendar.Load`, a cheap pre-scan of the raw text inserts a marker line
    `X-INTHEKNOW-NO-UID:1` into every `VEVENT` block that has no `UID` line. Marked events are keyed by
    `content:<sha256(name|start|end|location)>`. This follows the spec: "identified by their content; any change
    then shows up as a removal plus an addition".
  - **Duplicate UIDs**: when a UID appears on more than one non-override VEVENT, which some Outlook and Google
    exports do, those events are keyed `UID|<start UTC>`. Any rows that still collide (same UID and same start) keep
    the first in document order, and a warning with the calendar id is logged. Keys are therefore unique before the
    insert, and the `(calendar_id, event_key)` primary key can never fail a check.
- **Rationale**: The spec requires stable identity (FR-015, FR-020). MyTimetables UIDs are stable and global
  (investigation §2).
- **Alternatives considered**:
  - Relying on the Ical.Net `Uid` (random for events without a UID, which would record every such event as
    removed and re-added on every check).
  - Failing the check on duplicate UIDs (one bad event would permanently break a calendar).

## R7. Recurrence horizon (FR-015a)

- **Decision**:
  - Occurrences are the calendar-wide enumeration `TakeWhileBefore(horizon)`, plus every single, non-recurring
    event that starts at or after the horizon. A single event is one with no RRULE, no RDATE and no
    RECURRENCE-ID.
  - `horizon = check time + ExpansionHorizonMonths`.
  - Each snapshot row of a recurring occurrence stores its `occurrence_start` (original start). The calendar row
    stores the `horizon_end` used for the snapshot.
  - Diff rule:
    - an added recurring occurrence with `occurrence_start > previous horizon_end` is **not** recorded;
    - a removed recurring occurrence with `occurrence_start > current horizon` is **not** recorded.
- **Rationale**: This implements FR-015a literally. Single events are not horizon-bound.
- **Alternatives considered**: Applying the horizon to all events. That is simpler, but it would silently drop
  real additions of far-future single events, contrary to the spec.

## R8. Time normalisation

- **Decision**:
  - All times are stored as UTC instants (`timestamptz`).
  - `TZID` values are resolved by Ical.Net.
  - Floating times are resolved in the calendar's `X-WR-TIMEZONE`. If that is absent, the single `VTIMEZONE` is
    used when there is exactly one; otherwise UTC.
  - Date-only values (all-day events) are stored as local midnight at the start of that date, resolved in the
    same zone as floating times, using the explicit `TimeOnly(0, 0)` form (R2).
  - A missing `DTEND` uses Ical.Net's `EffectiveEndTime` (DURATION or the RFC 5545 default).
- **Rationale**: Covers the spec's DST and floating-time edge case. A pure representation change (TZID vs UTC)
  yields the same instant, so nothing is recorded.
- **Alternatives considered**: Storing the local time plus the zone (more to compare, and the output is the same).

## R8a. Malformed events (normalised so that a snapshot write can never fail)

- **Decision**: A database constraint violation during the snapshot `COPY` would fail the calendar on every check.
  Feed data is therefore normalised before it reaches the database:

  | Input | Handling |
  |---|---|
  | `DTEND < DTSTART` | Set end = start, **before** expansion (R2 gotcha) |
  | Missing `SUMMARY` | Name `""` |
  | Missing `DTSTART` | Event skipped, and a warning is logged with the calendar id and the event's position |
  | Event key longer than 1,000 characters | Replaced by `sha256:<hex>` of the key |
  | Duplicate keys | R6 |
  | Missing UID | R6 |

- **Rationale**: Without this, one bad event in a third-party feed blocks the whole calendar permanently.

## R9. MyTimetables extraction (NL/EN)

- **Decision**:
  - **Description format (verified on a live EN feed)**: blocks separated by blank lines. Each block is
    `Label:` followed by a value, either on the same line (`Type: Lesson`) or on the following lines
    (`Room(s):\nAL00.A1.38`).
  - **Parser**:
    1. Split the description on blank lines.
    2. **First** drop the volatile blocks by prefix match on the raw block (list below). The sync line contains a
       time (`21:22`), so treating it as a label would create a detail that changes every minute.
    3. Then a block is *labelled* when its first line matches `^([^\d:]+):(\s|$)`: the label contains no digits,
       and the colon ends the line or is followed by whitespace. The value is the rest of the block, joined and
       trimmed.
    4. Unlabelled, non-volatile blocks are joined in order and kept as the detail `text`. A changed free-text
       remark is therefore still recorded, even though the raw description is not compared when event details
       are on.
  - **Label map**, from both languages to canonical keys:

    | Labels (Dutch / English) | Canonical key |
    |---|---|
    | `Type:` | `type` |
    | `Ruimte(s):` / `Room(s):` | `rooms`: consumed and not used, because rooms come from `LOCATION` |
    | `Docent(en):` / `Staff member(s):` | `teachers` |
    | `Groep(en):` / `Student set(s):` | `groups` |

  - **Type map**: `Les`/`Lesson` → `lesson`, `Toets`/`Test` → `test`, `Vakantie`/`Holiday` → `holiday`,
    `Afspraak`/`Appointment` → `appointment`. Unknown types are kept verbatim.
  - **Teachers**: split on `,`, trim, and pair the parts into `Last, First`. An odd count keeps the whole value as
    a single entry (FR-017).
  - **Rooms**: `LOCATION` split on `,`. **Groups**: split on `,`. Lists are sorted and de-duplicated, so they are
    compared as sets (FR-026).
  - **With teacher detection off**, the teacher value is kept as the additional detail keyed `teachers`. Unknown
    labels are kept as details keyed by their label, verbatim. Canonical keys for known labels mean a language
    switch changes nothing, even with teacher detection off.
  - **Volatile blocks, always stripped on configured domains (FR-018)**: `Laatst gesynchroniseerd op …`,
    `Last synchronised on …`, `Deze afspraak wordt beheerd door MyTimetables.` and
    `This appointment is managed by MyTimetables.`
  - **Draft marker** (with draft-marker detection on): a leading `[CONCEPT] ` or `[DRAFT] ` is removed from the
    name and the status is set to `tentative`.
- **Rationale**: Taken directly from investigation §4 and §6 and the live sample. It meets FR-016 to FR-018 and
  SC-006.
- **Alternatives considered**: Line-based `Label: value` parsing. It breaks on the multi-line `Room(s):` block
  seen live.

## R10. Untrusted-feed limits

- **Decision**:
  - Constants: maximum feed size 10 MB (enforced by the handler), fetch timeout 30 s, and at most 50,000
    occurrences per calendar. Exceeding the occurrence cap fails the check with the reason `too_many_occurrences`.
  - Parse errors fail the check with `unparseable`.
- **Rationale**: Feeds are third-party input (Principle II). A single `FREQ=MINUTELY` rule would otherwise expand
  to millions of rows. Real feeds are about 250 KB and fewer than 3,000 occurrences.
- **Known ceilings** (accepted, marked with `ponytail:` comments):
  - Memory: the worst case per check is a 10 MB body (about 20 MB as a .NET string) plus Ical.Net's object graph,
    times `MaxConcurrentChecks` (up to 64). Only links added with the operator key can reach it today. Lower the
    size bound or stream-parse if user-added links ever make this reachable by strangers.
  - Recurring series are expanded from their start (FR-015a). A generic calendar with many long-running daily
    series (roughly 14 daily series over 10 years) passes 50,000 occurrences and fails every check with
    `too_many_occurrences`. This is rare and visible in the operator overview.
- **Alternatives considered**: Making these operator-configurable. That would be configuration for values that
  never need to change (Principle I).

## R11. Link validation and joining calendars (FR-003 vs US2-5)

- **Decision**:
  - A new link is validated by one fetch, with the same bounds and SSRF protection as a check.
  - The response must parse. It must also contain ≥ 1 event when **either** empty-feed protection is enabled for
    the domain **or** the link would join an *existing* calendar through a MyTimetables-derived identity (duplicate
    link merging or class-group sharing).
  - The validation response is **never** diffed. When the link **creates** a calendar, the validation result is
    stored as its baseline in the same transaction that inserts the calendar (FR-023), with the calendar's first
    check one `CheckInterval` later. When the link **joins** an existing calendar, nothing from the validation is
    stored.
- **Rationale**:
  - FR-003 alone would let a forged class-group link (right `timetable`, junk `h`) join a calendar on a domain
    with protection off. When it later becomes the fetch link, its empty feed would look like a mass removal.
  - US2-5 rejects such a link without conditions, so requiring events on MyTimetables-derived joins satisfies
    both FR-003 and US2-5.
  - Storing nothing from a joining link means a link in the other language, or fetched at another moment, cannot
    record changes outside the schedule.
  - Baselining a new calendar from its validation fetch means it is watched from the moment it is added. With a
    random first check instead, a new calendar under budget pressure waited behind the whole overdue backlog
    (about one check cycle, R19), and anything that changed at the source in that window was absorbed into the
    late baseline without being recorded. It also saves one request per new calendar. The baseline is exactly
    what the first check would store, because the fetch link of a new calendar is the link that created it.
- **Alternatives considered**: Taking the baseline from the first scheduled check (the earlier design): simpler by
  one shared helper, but it loses changes under budget pressure as described above.

## R12. Calendar identity keys

- **Decision**: The identity key is computed per link:
  1. The host is not a configured MyTimetables domain, or the link lacks the MyTimetables structure (`/ical` with
     `timetable`, `eu` and `h`): key = the **normalised URL**. Normalisation lower-cases the scheme and host,
     drops the default port and sorts the query parameters.
  2. Class-group sharing is on and the decoded timetable kind is `xedule_studentgroup`: key =
     `mt|<host>|<decoded timetable>`.
  3. Duplicate link merging is on: key = normalised URL **without** the valueless hex generation-marker parameter.
  4. Otherwise: rule 1.

  The database stores `sha256(key)`, never the key itself, because keys in rules 1 and 3 contain the link token.
- **Rationale**: Taken directly from investigation §1, §2 and §7.2 and FR-004 to FR-007. Hashing keeps secrets out
  of the calendar table and the unique index.
- **Alternatives considered**: Merging generic links by content hash. The spec explicitly excludes it.

## R13. Configuration changes, regrouping and re-baselining

- **Decision**:
  - Configuration is read **at startup**. We interpret "takes effect from the next check" as "the next check
    after the restart that applied the new configuration", which is how environment-variable and appsettings
    deployments behave anyway.
  - At startup, identity keys are recomputed for all links and links are regrouped. A calendar keeps its row
    (snapshot, pending changes) when its links still share one key. Links whose key changed move to the calendar
    with that key, or to a new one. Calendars left without links are deleted.
  - Each calendar stores `baseline_features`, the canonical feature set used for its snapshot. A check whose
    applicable features differ from it (or where it is `NULL`, meaning a new calendar) stores the result as the
    new baseline and records no changes. This covers FR-023, FR-026a and SC-009.
- **Rationale**: This is the smallest mechanism that meets the edge cases. Reloading config at runtime
  (`IOptionsMonitor`) would need regrouping while the worker is running, for no user-visible gain.
- **Known ceiling**: when a configuration change *merges* two calendars, the calendar that is deleted loses its
  undelivered changes. This only happens on an operator configuration change and is accepted.

## R14. Check atomicity and write volume (FR-019a, FR-024, FR-025, SC-008, SC-010)

- **Decision**:
  1. The fetch and parse happen outside any transaction.
  2. The parsed occurrences are serialised canonically (sorted by key) and hashed (SHA-256). If the hash equals
     `calendar.snapshot_hash` and the features are unchanged, the check is a **no-op success**. No snapshot rows are
     read or written. The calendar and link rows are still updated (step 5).
  3. Otherwise, in **one** transaction:
     1. lock the calendar row with `SELECT … WHERE id = @id AND lease_id = @lease FOR UPDATE`. If no row comes
        back, the calendar was deleted or this check lost its lease (R4 fence), so write nothing;
     2. load the snapshot and diff;
     3. write **only the affected rows**:
        - `DELETE … WHERE event_key = ANY(@removed)`;
        - upsert the added and changed rows with `INSERT … SELECT FROM unnest(…) ON CONFLICT (calendar_id,
          event_key) DO UPDATE`;
        - rows that were silently added or removed (the R7 horizon rule) are written the same way;
        - on a baseline (R13), delete all rows and `COPY` the new set;
     4. `COPY` the change rows;
     5. update `snapshot_hash`, the outcome columns and `next_check_at`, and clear `lease_id`;
     6. commit.

  **Every successful check, including the no-op**, makes these updates:
  - on the calendar: `status = 'ok'`, `horizon_end` set to the current horizon, `consecutive_failures = 0`;
  - on the fetch link: `flagged_at = NULL` and `last_used_at = now`.

  This matters because a link that recovers usually returns exactly the events it had before, so its hash
  matches. If only the outcome columns were updated, the calendar would stay `failing` and on the 24-hour interval
  (FR-008a), and a stale `horizon_end` would break the horizon rule (FR-015a).
  4. A failed check writes only the outcome columns and `next_check_at`, also in one transaction behind the
     same lease fence. The no-op success in step 2 is fenced the same way.
- **Rationale**:
  - A crash before commit leaves the old state, so the next check redoes the same diff. A crash after commit has
    already recorded everything. Nothing is lost or recorded twice (SC-008). No network I/O happens while a lock
    is held.
  - Almost every check of a timetable finds nothing new. With thousands of calendars, replacing every row each
    time would rewrite millions of rows every interval and keep autovacuum busy for nothing. With the hash
    shortcut, an unchanged calendar costs three small row updates (the claim, the calendar outcome and the fetch
    link) and no event rows (SC-010).
- **Alternatives considered**:
  - Always replacing the full snapshot: simpler, but writes grow with events rather than changes.
  - Hashing the raw feed: useless, because `DTSTAMP` and the sync line change on every fetch.

## R15. API instead of an operator CLI (FR-030 to FR-032)

- **Decision**:
  - ASP.NET Core minimal APIs, with the endpoints in [contracts/api.md](./contracts/api.md).
  - All routes are in one `/api` route group with an endpoint filter that checks
    `Authorization: Bearer <operator key>`. The comparison is constant-time: `FixedTimeEquals` on SHA-256
    digests, both BCL.
  - Errors use the built-in Problem Details, with a `reason` code.
  - Subscribers are addressed as `{subscriber}` in the path. The login feature will later bind that to the
    signed-in user.
  - Links travel only in POST bodies, and responses never contain them.
- **Rationale**:
  - The owner chose "API now, login next". A single operator key is the smallest safe gate until then.
  - Checking it takes one filter using platform crypto primitives, not hand-rolled crypto, which satisfies
    Principle II.
  - Links in bodies stay out of access logs and proxy logs.
- **Alternatives considered**:
  - An ASP.NET Core `AuthenticationHandler` for the key: more code now, and it gets replaced by real login next
    anyway.
  - Leaving the API unauthenticated on localhost: one misconfigured proxy would expose every link and timetable.
  - Generating an OpenAPI document now: the `Microsoft.AspNetCore.OpenApi` package would be added when the
    frontend actually consumes it.

## R16. Testing

- **Decision**:
  - **xunit.v3** (Apache-2.0) on **Microsoft.Testing.Platform**. A root `global.json` contains
    `{"test":{"runner":"Microsoft.Testing.Platform"}}` and the test project has `OutputType Exe`.
  - **No** `Microsoft.NET.Test.Sdk`. Verified in a scratch project: on the .NET 10 SDK, xunit.v3 with the VSTest
    Test SDK fails `dotnet test` with "Testing with VSTest target is no longer supported". With the `global.json`
    opt-in and without the Test SDK, `dotnet test` discovers and passes the test.
  - **SSRF tests**: the address classifier is unit-tested directly. The `ConnectCallback` is tested by connecting
    a real `SocketsHttpHandler` to `localhost`, which must be refused. The redirect case uses the same callback
    (every connection passes through it), so it has no separate test. A fake outer handler would bypass the
    callback.
  - Pure logic (URL guard, identity, parser, MyTimetables extraction, differ) is unit-tested with in-memory
    fixtures.
  - Checks against the database run against PostgreSQL from `compose.yaml`, located through the
    `INTHEKNOW_TEST_DB` connection string. Each test run creates and drops its own database.
  - HTTP is faked with a custom `HttpMessageHandler` that serves fixture files and counts requests (for SC-003).
  - API tests use `WebApplicationFactory` from **Microsoft.AspNetCore.Mvc.Testing** (MIT), which runs the app
    in memory against the test database. They cover the key check (SC-011), link-free responses, subscriber
    scoping of changes, and subscription removal.
- **Fixtures**:
  - Committed: *anonymised* MyTimetables NL and EN feeds (teacher names replaced, structure kept), an empty
    MyTimetables feed and a synthetic generic feed with recurrence.
  - Real feeds go in `backend/InTheKnow.Tests/Fixtures/private/` (gitignored). Tests that use them (SC-006 on
    real data) are skipped when the directory is empty.
- **Alternatives considered**:
  - Starting the app on a real port for API tests: slower and flakier, and the test host is the platform's
    standard way.
  - Testcontainers (a dependency. The compose file is needed for local runs anyway, so
  reusing it is simpler). An in-memory database (hides the constraints the constitution requires in the database).

## R17. Dependency licences (GPL-3.0 compatibility)

Checked from the nuspec files and `dotnet list package --include-transitive`.

| Package | Version | Licence | Compatible |
|---|---|---|---|
| Ical.Net | 5.2.3 | MIT | yes |
| NodaTime (transitive) | 3.2.2 | Apache-2.0 | yes (GPLv3 accepts Apache-2.0) |
| Npgsql | 10.0.x | PostgreSQL License (permissive) | yes |
| Microsoft.AspNetCore.Mvc.Testing (test) | 10.0.x | MIT | yes |
| xunit.v3 (test, brings Microsoft.Testing.Platform, MIT) | 4.0.x | Apache-2.0 | yes |
| Microsoft.Extensions.TimeProvider.Testing (test) | 10.x | MIT (its only dependency, Microsoft.Bcl.TimeProvider, is MIT) | yes |

ASP.NET Core itself comes from the shared framework (MIT), not from NuGet. The transitive trees of Npgsql and
Mvc.Testing (Microsoft.AspNetCore.TestHost and Microsoft.Extensions.*, all MIT) are re-checked with
`dotnet list package --include-transitive` when `packages.lock.json` is first committed.

## R18. HTTPS outside local development vs FR-001

- **Decision** (revised after analysis, 2026-09-24): only `https` feed links are accepted. `http` links are
  rejected at add time with `unsupported_scheme`, and the database CHECK on `calendar_link.url` allows only
  `https://`. A redirect from https to http is never followed (the `HttpClient` default), so no feed is ever read
  over plain http. The API's own hop from the TLS-terminating proxy to the app stays on the same host (loopback or
  a host-internal container network), never across a physical network, so the operator key never travels
  unencrypted ([contracts/api.md § Transport](./contracts/api.md#transport)).
- **Rationale**: The constitution's "Traffic MUST use HTTPS outside local development" has no deviation path;
  Complexity Tracking only covers Principle I. The spec owner chose to drop `http` from FR-001 rather than amend
  the constitution. Real MyTimetables links are https.
- **Alternatives considered**: Accepting `http` with an `insecure` flag in the add-link response (the earlier
  design): it violated Principle II. Amending the constitution to scope the rule to traffic the app serves:
  possible later, if an http-only calendar source is ever needed.

## R19. Scale: a whole school

- **Decision**: The design targets tens of thousands of subscribers and thousands of calendars on one deployment,
  with one PostgreSQL instance.
- **Where the load actually is**:
  - **Fetching**: class-group links share one calendar (FR-006), but personal timetables do not.
    5,000 calendars at a 10-minute interval is about 8 requests/s and about 2 MB/s of downloads. The per-host
    budget (R4) caps what MyTimetables receives. With the default of 120/min, one host supports about 1,200
    calendars per 10 minutes, fewer while many links are being added. Beyond that, checks are delayed, and the
    operator raises that domain's budget (for example after agreeing a bigger allowance with the institution)
    or the interval.
    Inholland's tolerance is unknown, so the default is deliberately cautious.
  - **The budget, not the interval, sets the pace.** Nearly every calendar is on one host, so one check cycle is
    `max(CheckInterval, calendars on the host ÷ budget)` minutes. SC-005 is phrased in cycles for this reason.

    | Calendars on one host | Cycle at 120/min, 10 min | Requests to the source per day |
    |---|---|---|
    | 1,200 | 10 min | 172,800 |
    | 5,000 | about 42 min | 172,800 (budget cap) |
    | 20,000 | about 167 min | 172,800 (budget cap) |

    How many calendars one host carries is **unverified**: class-group links merge, personal timetable links do
    not, so "thousands" could be tens of thousands. The operator overview's `overdue` filter shows when the budget
    is the limit. Each MyTimetables response appears to be generated per request (`Last-Modified` and `DTSTAMP` are
    the request time, investigation §5), so the budget protects real work at the source. The source's
    `Expires` of 1 hour is close to the cycle at 5,000 calendars.
  - **Bandwidth figures are uncompressed and unverified**: about 250 KB per response, so about 43 GB a day at the
    default budget's cap. Whether the server compresses (`Content-Encoding`) was not recorded; the fetcher asks for
    compression, and quickstart §4 records the header.
  - **Database**:
    - The hash shortcut (R14) makes unchanged checks almost free.
    - `event_snapshot` holds about 5,000 × 400 = 2 million small rows, which is comfortable for PostgreSQL.
    - `detected_change` grows with real changes only and is capped by retention.
    - The indexes are on `calendar(next_check_at)`, `calendar(last_link_id)`, `detected_change(detected_at)` and
      `detected_change(calendar_id, id)`. The `detected_change(calendar_id, id)` index serves the paged changes
      endpoint through the `subscription` join. The `last_link_id` index keeps each link delete from scanning
      `calendar` for its `ON DELETE SET NULL`.
    - Retention deletes in batches of 10,000 rows, because a timetable-publication day (every event of every
      calendar `added`) expires as one burst a year later.
  - **CPU**: unmeasured. The spike parsed a 73-event feed without timing it; real feeds have about 390 events.
    The margin is large: at the default budget about 2 checks start per second, against a goal of < 1 s per
    unchanged check. Quickstart §4 measures it once.
  - **Throughput limit of the workers**: at about 1 s per check (mostly network), the default 16 workers manage
    about 16 checks/s (960/min), well above the default budget of 120/min. With a much bigger per-domain
    budget, the workers can become the limit. `MaxConcurrentChecks` (up to 64) is then raised as well; see
    [contracts/configuration.md](./contracts/configuration.md).
- **Why PostgreSQL** (the constitution already requires it, and the owner asked for an opinion):
  - The data is relational, with integrity rules the database enforces.
  - `jsonb` covers the fields that vary by feature.
  - Row-level locking and `unnest` upserts suit this workload.
  - A single instance handles this scale by a wide margin.
- **Alternatives considered**:
  - SQLite: a single writer, which is awkward with an API and a checker writing concurrently.
  - A document database: gives up the constraints for no gain.
  - Sharding or read replicas: unnecessary at this size.
