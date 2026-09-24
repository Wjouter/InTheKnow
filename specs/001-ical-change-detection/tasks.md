---

description: "Task list for Timetable Change Detection (001)"
---

# Tasks: Timetable Change Detection

**Input**: Design documents from `/specs/001-ical-change-detection/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: Included. The constitution requires an automated check for all non-trivial logic, and the plan lists a
failing-if-broken test for each component (plan § Constitution Check, quickstart §1). Write each story's tests
first. They should fail (a compile failure counts) until the implementation tasks for that story are done.

**Organization**: Tasks are grouped by user story. Phases follow priority order: US1 (P1), US2 (P2), US5 (P2), US3 (P3), US4 (P3).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different file, no dependency on an unfinished task)
- **[Story]**: the user story the task belongs to (US1 to US5)

## Path Conventions

- The app is in `backend/InTheKnow/` and the tests are in `backend/InTheKnow.Tests/` (plan § Project Structure).
- The plan's source tree has four test files added here: `TestDb.cs` (per-test database), `DbTests.cs`,
  `SettingsTests.cs` and `SubscriptionsTests.cs`. They are small additions to the plan tree, not new layers.

## Shared signatures (used across tasks, so that tasks written separately compile together)

```csharp
// Settings.cs
sealed class InTheKnowOptions   // bound from "InTheKnow"; property defaults = contracts/configuration.md defaults
sealed class DomainOptions      // Host, six feature bools (default true), int? MaxRequestsPerMinute
sealed record Features(bool DuplicateLinkMerging, bool ClassGroupSharing, bool EmptyFeedProtection,
                       bool TeacherDetection, bool EventDetails, bool DraftMarkerDetection) { string Canonical { get; } }
sealed class Settings { static Settings Load(IConfiguration c, ILogger log); InTheKnowOptions Options;
                        string ConnectionString; Features? FeaturesFor(string host); int BudgetFor(string host);
                        string CanonicalFeaturesFor(string host) /* "generic" when unlisted */ }
// Fetcher.cs
sealed record FetchResult(string? Body, string? Error);
sealed class Fetcher(HttpMessageHandler? handler = null, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
  { static bool IsBlocked(IPAddress a); Task<(Uri? Uri, string? Reason)> CheckUrlAsync(string url, CancellationToken ct);
    Task<FetchResult> FetchAsync(Uri url, CancellationToken ct); }
// HostBudget.cs
sealed class HostBudget(Settings s) { string[] BusyHosts(DateTimeOffset now); DateTimeOffset? EarliestFree(DateTimeOffset now);
                                      DateTimeOffset? TryReserve(string host, DateTimeOffset now, TimeSpan maxWait); }
// FeedParser.cs
sealed record Occurrence(string Key, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Status,
                         DateTimeOffset? OccurrenceStart, string? Location, string? Description);
sealed record ParseResult(IReadOnlyList<Occurrence>? Events, string? Error);
static class FeedParser { static ParseResult Parse(string ics, DateTimeOffset horizon, ILogger log, long calendarId, CancellationToken ct); }
// MyTimetables.cs
sealed record EventState(string Key, string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Status,
                         DateTimeOffset? OccurrenceStart, JsonObject Fields);
static class MyTimetables { static EventState ToState(Occurrence o, Features? f); }
// Differ.cs
sealed record ChangeRow(string EventKey, string Kind, string? Field, JsonNode? OldValue, JsonNode? NewValue,
                        string EventName, DateTimeOffset EventStartsAt);
sealed record DiffResult(IReadOnlyList<ChangeRow> Changes, IReadOnlyList<EventState> Upserts, IReadOnlyList<string> Deletes);
static class Differ { static DiffResult Diff(IReadOnlyDictionary<string, EventState> stored, IReadOnlyList<EventState> current,
                                             DateTimeOffset? previousHorizon, DateTimeOffset currentHorizon);
                      static byte[] SnapshotHash(IReadOnlyList<EventState> events); }
// LinkIdentity.cs
static class LinkIdentity { static string Normalise(Uri u); static (string Key, bool MyTimetablesDerived) KeyFor(Uri u, Settings s);
                            static byte[] Hash(string key) /* SHA-256 of UTF-8 */ }
// Checker.cs
sealed class Checker(NpgsqlDataSource db, Fetcher f, HostBudget b, Settings s, TimeProvider t, ILogger<Checker> log) : BackgroundService
  { Task RunDueChecksAsync(DateTimeOffset now, CancellationToken ct = default); Task RunRetentionAsync(DateTimeOffset now, CancellationToken ct = default);
    Task RegroupAsync(DateTimeOffset now, CancellationToken ct = default); internal Func<long, CancellationToken, Task>? BeforeWriteHook;
    internal static Task CopySnapshotAsync(NpgsqlConnection c, long calendarId, IReadOnlyList<EventState> states, CancellationToken ct); }
// Subscriptions.cs
sealed record AddLinkResult(long? CalendarId, long? LinkId, bool CalendarIsNew, bool AlreadyExisted, string? Error);
sealed class Subscriptions(NpgsqlDataSource db, Fetcher f, HostBudget b, Settings s, TimeProvider t)
  { Task<AddLinkResult> AddLinkAsync(string subscriber, string url, CancellationToken ct); Task<bool> RemoveAsync(string subscriber, long calendarId, CancellationToken ct); }
```

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Solution, build settings, database container and project files.

- [X] T001 [P] Create `global.json` at the repo root with exactly `{"test":{"runner":"Microsoft.Testing.Platform"}}` (research R16: this opts `dotnet test` into Microsoft.Testing.Platform, which xunit.v3 requires on the .NET 10 SDK)
- [X] T002 [P] Create `Directory.Build.props` at the repo root setting `TargetFramework` `net10.0`, `Nullable` `enable`, `ImplicitUsings` `enable`, `TreatWarningsAsErrors` `true` and `RestorePackagesWithLockFile` `true`
- [X] T003 [P] Create `compose.yaml` at the repo root with one service `db`: image `postgres:18`, `POSTGRES_USER`/`POSTGRES_PASSWORD`/`POSTGRES_DB` all `intheknow`, port `5432:5432`, and a named volume. These values must match the connection strings in quickstart.md § Setup.
- [X] T004 [P] Append `backend/InTheKnow.Tests/Fixtures/private/` to `.gitignore`. Real feeds go there; `mytimetable example links.md` is already ignored.
- [X] T005 [P] Create `backend/InTheKnow/InTheKnow.csproj` with `Sdk="Microsoft.NET.Sdk.Web"`. Add PackageReferences `Ical.Net` `5.2.3` and `Npgsql` `10.0.*` (pin the exact resolved version), and `<EmbeddedResource Include="Migrations/*.sql" />`. Add `<InternalsVisibleTo Include="InTheKnow.Tests" />` so tests can use `Checker.BeforeWriteHook`. Also create a placeholder `backend/InTheKnow/Program.cs` (`var app = WebApplication.CreateBuilder(args).Build(); app.Run(); public partial class Program;`) so the project builds; T018 replaces it.
- [X] T006 [P] Create `backend/InTheKnow.Tests/InTheKnow.Tests.csproj` (`Microsoft.NET.Sdk`) with `<OutputType>Exe</OutputType>`. Add PackageReferences `xunit.v3` `4.0.*`, `Microsoft.AspNetCore.Mvc.Testing` `10.0.*` and `Microsoft.Extensions.TimeProvider.Testing` `10.*`, and a ProjectReference to `../InTheKnow/InTheKnow.csproj`. Do **not** reference `Microsoft.NET.Test.Sdk`: with it, `dotnet test` fails on .NET 10 with "Testing with VSTest target is no longer supported" (research R16, verified). Copy `Fixtures/**` to the output directory (`CopyToOutputDirectory` `PreserveNewest`), and link `../InTheKnow/appsettings.json` into the output so `SettingsTests` can load the shipped defaults.
- [X] T007 [P] Create `backend/InTheKnow/appsettings.json` with exactly the shape in contracts/configuration.md § Shape. That means an empty `ConnectionStrings:Default` and `InTheKnow:ApiKey`, `CheckInterval` `"00:10:00"`, `MaxRequestsPerHostPerMinute` `120`, `MaxConcurrentChecks` `16`, `FailingCheckInterval` `"1.00:00:00"`, `ExpansionHorizonMonths` `12`, `ChangeRetention` `"365.00:00:00"`, and `MyTimetables:Domains` `[{ "Host": "mytimetables.inholland.nl" }]`. Keep the Logging section at the template defaults, and set nothing that enables HTTP request logging.
- [X] T008 Create `InTheKnow.slnx` at the repo root referencing `backend/InTheKnow/InTheKnow.csproj` and `backend/InTheKnow.Tests/InTheKnow.Tests.csproj` (depends on T005, T006)
- [X] T009 Run `dotnet restore` to generate `backend/InTheKnow/packages.lock.json` and `backend/InTheKnow.Tests/packages.lock.json`, and commit both. Then run `dotnet build` and confirm it succeeds with 0 warnings.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Configuration, schema, database access, fetching, the host budget and test infrastructure. Every story needs these.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [X] T010 Implement `backend/InTheKnow/Settings.cs` with `InTheKnowOptions`, `DomainOptions`, `Features` and `Settings` (see Shared signatures).
  - **Binding**: bind with `IConfiguration.GetSection("InTheKnow").Get<InTheKnowOptions>()`. Property initialisers hold the defaults from contracts/configuration.md: `CheckInterval` 10 min, `MaxRequestsPerHostPerMinute` 120, `MaxConcurrentChecks` 16, `FailingCheckInterval` 24 h, `ExpansionHorizonMonths` 12, `ChangeRetention` 365 d, and every `DomainOptions` feature bool `true`.
  - **Domain list default**: do **not** put a default domain list in code. The configuration binder appends to an initialised collection, so a code default would duplicate the Inholland entry from `appsettings.json`. The shipped `appsettings.json` is the only source of the Inholland default.
  - **Required values**: fail startup (throw `InvalidOperationException` with a message naming the key, never its value) when `ConnectionStrings:Default` is empty or when `InTheKnow:ApiKey` is shorter than 32 characters.
  - **Host lookup**: `FeaturesFor(host)` matches a configured `Host` exactly and case-insensitively, with no subdomains, and returns `null` for unlisted hosts. The returned `Features` has `TeacherDetection` forced to `false` when `EventDetails` is `false`, because teacher detection only works with event details on (contracts/configuration.md).
  - **Canonical feature string**: `Features.Canonical` is `"mt:"` followed by the effective flags, in the fixed order `dlm,cgs,efp,td,ed,dmd`, as `1`/`0`. `CanonicalFeaturesFor(host)` returns it, or `"generic"` when the host is unlisted.
  - **Budget**: `BudgetFor(host)` returns the domain's `MaxRequestsPerMinute` when the host is listed and sets one, otherwise `MaxRequestsPerHostPerMinute`.
  - Clamping and range validation are added in T064 (US3) and duplicate-host validation in T068 (US4).
- [X] T011 Create `backend/InTheKnow/Migrations/001_init.sql` with table `calendar`, using exactly these columns and constraints (data-model.md):
  - `id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY`
  - `identity_hash bytea NOT NULL UNIQUE CHECK (length(identity_hash) = 32)`
  - `host text NOT NULL CHECK (host = lower(host) AND length(host) BETWEEN 1 AND 253)`
  - `status text NOT NULL DEFAULT 'ok' CHECK (status IN ('ok','failing'))`
  - `next_check_at timestamptz NOT NULL`
  - `lease_id uuid NULL`
  - `baseline_features text NULL`
  - `horizon_end timestamptz NULL`
  - `snapshot_hash bytea NULL CHECK (snapshot_hash IS NULL OR length(snapshot_hash) = 32)`
  - `last_checked_at timestamptz NULL`
  - `last_link_id bigint NULL`. Its FK is added in T012 because `calendar_link` does not exist yet.
  - `last_ok boolean NULL`
  - `last_error text NULL CHECK (last_ok IS DISTINCT FROM false OR last_error IS NOT NULL)`
  - `last_event_count integer NULL CHECK (last_event_count >= 0)`
  - `last_change_count integer NULL CHECK (last_change_count >= 0)`
  - `consecutive_failures integer NOT NULL DEFAULT 0 CHECK (consecutive_failures >= 0)`
  - `failing_since timestamptz NULL CHECK ((consecutive_failures = 0) = (failing_since IS NULL))`
  - `created_at timestamptz NOT NULL DEFAULT now()`
  - Also add `CREATE INDEX ON calendar (next_check_at)`.
- [X] T012 In `backend/InTheKnow/Migrations/001_init.sql`, add table `calendar_link` with:
  - `id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY`
  - `calendar_id bigint NOT NULL REFERENCES calendar(id) ON DELETE CASCADE`
  - `subscriber text NOT NULL CHECK (subscriber ~ '^[A-Za-z0-9._@-]{1,100}$')`
  - `url text NOT NULL CHECK (url ~ '^https://') CHECK (length(url) <= 2048)` (only `https`, FR-001)
  - `flagged_at timestamptz NULL`
  - `last_used_at timestamptz NULL`
  - `created_at timestamptz NOT NULL DEFAULT now()`
  - `UNIQUE (calendar_id, subscriber, url)`
  - `CREATE INDEX ON calendar_link (subscriber, calendar_id)`
  - Then `ALTER TABLE calendar ADD FOREIGN KEY (last_link_id) REFERENCES calendar_link(id) ON DELETE SET NULL` and
    `CREATE INDEX ON calendar (last_link_id)`, so a link delete does not scan `calendar` for the `SET NULL`.
- [X] T013 In `backend/InTheKnow/Migrations/001_init.sql`, add table `event_snapshot` with:
  - `calendar_id bigint NOT NULL REFERENCES calendar(id) ON DELETE CASCADE`
  - `event_key text NOT NULL CHECK (length(event_key) BETWEEN 1 AND 1024)`
  - `name text NOT NULL`
  - `starts_at timestamptz NOT NULL`
  - `ends_at timestamptz NOT NULL`, with `CHECK (ends_at >= starts_at)`
  - `status text NULL`
  - `occurrence_start timestamptz NULL`
  - `fields jsonb NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(fields) = 'object')`
  - `PRIMARY KEY (calendar_id, event_key)`
- [X] T014 In `backend/InTheKnow/Migrations/001_init.sql`, add the remaining objects:
  - Table `detected_change`:
    - `id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY`
    - `calendar_id bigint NOT NULL REFERENCES calendar(id) ON DELETE CASCADE`
    - `event_key text NOT NULL`
    - `kind text NOT NULL CHECK (kind IN ('added','removed','changed'))`
    - `field text NULL CHECK ((kind = 'changed') = (field IS NOT NULL))`
    - `old_value jsonb NULL`
    - `new_value jsonb NULL CHECK (kind <> 'changed' OR old_value IS NOT NULL OR new_value IS NOT NULL)`
    - `CHECK (kind <> 'added' OR (old_value IS NULL AND new_value IS NOT NULL))`
    - `CHECK (kind <> 'removed' OR (old_value IS NOT NULL AND new_value IS NULL))`
    - `event_name text NOT NULL`
    - `event_starts_at timestamptz NOT NULL`
    - `detected_at timestamptz NOT NULL`
    - `notified_at timestamptz NULL`
  - Indexes `CREATE INDEX ON detected_change (detected_at)` and `CREATE INDEX ON detected_change (calendar_id, id)`.
  - The view `CREATE VIEW subscription AS SELECT DISTINCT calendar_id, subscriber FROM calendar_link;`.
  - `schema_migration` is **not** created here; the runner in T015 creates it.
- [X] T015 Implement `backend/InTheKnow/Db.cs`:
  - **Data source**: `static NpgsqlDataSource Create(string connectionString)`.
  - **Advisory lock**: `static async Task<NpgsqlConnection> TakeInstanceLockAsync(NpgsqlDataSource)` opens a dedicated connection and runs `SELECT pg_try_advisory_lock(<fixed bigint constant>)`. When the result is false, it throws `InvalidOperationException("another InTheKnow instance holds the lock")`. Otherwise it returns the connection, which the caller keeps open for the process lifetime (research R4).
  - **Migrations**: `static async Task MigrateAsync(NpgsqlDataSource)` is about 25 lines.
    1. Run `CREATE TABLE IF NOT EXISTS schema_migration (version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())`.
    2. Read the embedded `Migrations/NNN_*.sql` resources ordered by their numeric prefix.
    3. Apply each version not yet in `schema_migration` in its own transaction, and insert its version in the same transaction (research R3).
- [X] T016 [P] Implement `backend/InTheKnow/Fetcher.cs` (research R5).
  - **`IsBlocked(IPAddress)`**: first unwrap IPv4-mapped IPv6 with `MapToIPv4`. Then return true for:
    - loopback;
    - `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `fc00::/7`;
    - `169.254.0.0/16`, `fe80::/10`;
    - `100.64.0.0/10`;
    - unspecified (`0.0.0.0`, `::`);
    - multicast (`224.0.0.0/4`, `ff00::/8`);
    - broadcast `255.255.255.255`.
  - **Constructor**: with `handler == null`, build a `SocketsHttpHandler` with `AutomaticDecompression = All`, `MaxAutomaticRedirections = 5`, `PooledConnectionLifetime = 5 min` (so DNS changes at the source are picked up), and a `ConnectCallback`. The callback resolves the host with `resolve` (default `Dns.GetHostAddressesAsync`) and throws a private `BlockedAddressException` when any resolved address `IsBlocked`. Otherwise it connects a `Socket` to the first address. Create one `HttpClient` with `Timeout = 30 s`, `MaxResponseContentBufferSize = 10 MB` and a descriptive `User-Agent` such as `InTheKnow/1.0 (timetable change detection)`. Do not use `IHttpClientFactory`: it logs full URIs, which contain secrets (FR-028).
  - **`CheckUrlAsync`**: returns `(uri, null)` on success, otherwise a reason:
    - `invalid_url` when `Uri.TryCreate(Absolute)` fails;
    - `unsupported_scheme` when the scheme is not `https` (plain `http` included, FR-001);
    - `url_too_long` when `uri.AbsoluteUri.Length > 2048`;
    - `blocked_address` when `resolve` returns any blocked address or no address.
  - **`FetchAsync`**: returns `FetchResult(body, null)` for 2xx responses. Send with `HttpCompletionOption.ResponseHeadersRead`: the default buffers the whole body against `MaxResponseContentBufferSize` and throws `HttpRequestException`, which would map to `network` instead of `too_large`. Read the body as a stream, copy at most 10 MB + 1 byte, and decode it as UTF-8. Otherwise the error is a code only:
    - `http_<status>` for a non-2xx response;
    - `too_large` when the body exceeds 10 MB;
    - `timeout` when `HttpClient.Timeout` elapses (a `TaskCanceledException` while the caller's token is not cancelled);
    - `blocked_address` when the exception chain contains `BlockedAddressException`;
    - `network` for any other `HttpRequestException` or `IOException`.
  - When the **caller's** token is cancelled, rethrow `OperationCanceledException`. Never log or return exception messages or the URL. https→http redirects are not followed (the `HttpClient` default, research R18).
  - Add `// ponytail: worst case 10 MB body (~20 MB string) + Ical.Net graph per check × MaxConcurrentChecks; lower the bound or stream-parse if strangers can add links` next to the size constant (research R10).
- [X] T017 [P] Implement `backend/InTheKnow/HostBudget.cs` (about 20 lines, research R4).
  - **State**: a `lock`-guarded `Dictionary<string, DateTimeOffset> nextFree`, keyed by lower-cased host. The gap for a host is `TimeSpan.FromMinutes(1) / settings.BudgetFor(host)`.
  - **`BusyHosts(now)`**: returns the hosts with `nextFree > now`.
  - **`EarliestFree(now)`**: returns the smallest `nextFree` that is `> now`, or `null`.
  - **`TryReserve(host, now, maxWait)`**: computes `slot = max(now, nextFree[host])`. When `slot - now > maxWait`, it returns `null` and changes nothing. Otherwise it sets `nextFree[host] = slot + gap` and returns `slot`.
  - **Callers**: the dispatcher calls it with `TimeSpan.MaxValue`, and add-link validation with 5 s.
- [X] T018 Replace `backend/InTheKnow/Program.cs`:
  - Create `WebApplication.CreateBuilder(args)`.
  - Register these singletons **as DI factories**, so that `WebApplicationFactory` configuration overrides (applied at `Build()`) are visible to them:
    - `Settings`: `sp => Settings.Load(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("Settings"))`;
    - `NpgsqlDataSource`: `sp => Db.Create(sp.GetRequiredService<Settings>().ConnectionString)`;
    - `TimeProvider.System` as `TimeProvider`, `new Fetcher()`, and `HostBudget`.
  - After `builder.Build()` and before `app.Run()`:
    1. resolve `Settings` eagerly, so invalid configuration fails at startup;
    2. `await Db.TakeInstanceLockAsync(dataSource)`, keeping the returned connection referenced until shutdown;
    3. `await Db.MigrateAsync(dataSource)`.
  - End the file with `public partial class Program;` for `WebApplicationFactory`.
  - Leave the comment markers `// checker (T044)`, `// subscriptions (T049)`, `// api (T058)` and `// regroup (T070)` where later tasks add their lines.
- [X] T019 [P] Implement `backend/InTheKnow.Tests/TestDb.cs`, a per-test isolated database:
  - **Server**: read `INTHEKNOW_TEST_DB` (quickstart § Setup). When it is unset, throw with a message explaining how to start `compose.yaml`.
  - **Template, once per test run**: under a static `SemaphoreSlim`, create database `itk_template_<runGuid>` and run `Db.MigrateAsync` on it. Drop it at process exit through an xunit assembly fixture.
  - **Per test**: `static async Task<TestDb> CreateAsync()` runs `CREATE DATABASE itk_<guid> TEMPLATE itk_template_<runGuid>` and exposes `NpgsqlDataSource DataSource` and `string ConnectionString`. `DisposeAsync` drops the database `WITH (FORCE)`.
  - **Why per-test**: `RunDueChecksAsync` claims every due calendar in its database, so tests must never share one.
  - **Settings helper**: `Settings Settings(params (string Key, string? Value)[] overrides)` builds a `Settings` from the linked shipped `appsettings.json`, plus this database's connection string, a fixed 48-character `InTheKnow:ApiKey`, and the overrides (for example `("InTheKnow:CheckInterval", "00:30:00")`). Every test that needs a `Settings` uses it.
- [X] T020 [P] Implement `backend/InTheKnow.Tests/FakeHttp.cs`, an `HttpMessageHandler`:
  - **Configuration**: a map from absolute URL to a response. A response is a fixture file name, a status code, `Hang`, `Timeout`, `Bytes(n)` (a 200 response of `n` bytes, for the size bound) or `Throw(exception)` (for the `network` mapping).
    - `Hang` awaits `Task.Delay(Timeout.Infinite, cancellationToken)`, so it honours cancellation. It is for the 2-minute check-limit tests.
    - `Timeout` throws `TaskCanceledException` immediately while the caller's token is not cancelled. That is the condition `Fetcher` maps to `timeout`, so tests don't wait for the real 30 s `HttpClient.Timeout` (research R4: it runs on real time).
  - **The map is mutable** mid-test, so the same URL can serve a different fixture on the next check.
  - **Counters**: requests per URL and per host.
  - **Timestamps**: a list of `(host, TimeProvider.GetUtcNow())` for each request, taken from an injected `TimeProvider`.
  - **Concurrency tracking**: a counter of the current and maximum number of concurrent in-flight requests.
  - **Gate**: an optional `TaskCompletionSource`. While it is set, every request awaits it with the cancellation token before answering.
  - **Resolver**: provide a static `Resolve` delegate for `Fetcher` that returns `IPAddress.Parse("93.184.215.14")` for every host, so tests never use real DNS.
- [X] T021 [P] Create anonymised MyTimetables fixtures `backend/InTheKnow.Tests/Fixtures/mt-nl.ics` and `backend/InTheKnow.Tests/Fixtures/mt-en.ics` for the same timetable.
  - **Source**: fetch a real feed with a link from the gitignored `mytimetable example links.md`, and save the raw copy under `Fixtures/private/`. Also save the raw feed of the **other** example timetable, so both SC-006 timetables (ALTI26VT4ES1 and ALET26VT4A) are in `Fixtures/private/` for the real-data test in T031.
  - **Trim**: keep the VCALENDAR header, the VTIMEZONE and about 30 VEVENTs. Together they must cover:
    - types `Les`, `Toets`, `Vakantie` and `Afspraak`;
    - an event with several rooms (`LOCATION:AL00.A1.20\, AL00.A1.21`);
    - an event with several teachers (`Last1\, First1\, Last2\, First2`);
    - a `[CONCEPT] ` event;
    - a floating-time holiday;
    - the sync line and the footer.
  - **Anonymise**: replace every teacher name consistently (`Docent1, Anna`, …). Check that no `h=`, `eu=` or `timetable=` value, and no real person's name, appears in the committed files.
  - **English copy**: if the account currently returns only one language, derive the other file by applying the NL↔EN table in investigation.md §6. That covers the calendar name, the `[CONCEPT] `↔`[DRAFT] ` prefix, the labels, the type values, the footer and the sync line. Keep UIDs, times, rooms, teachers and groups identical.
- [X] T022 [P] Create `backend/InTheKnow.Tests/Fixtures/mt-empty.ics`: the invalid-link response from investigation.md §3. It is a VCALENDAR with the same header, `X-WR-CALNAME` and VTIMEZONE as `mt-nl.ics`, and zero VEVENTs.
- [X] T023 [P] Create `backend/InTheKnow.Tests/Fixtures/generic.ics`, a synthetic feed with `X-WR-TIMEZONE:Europe/Amsterdam` containing:
  - a weekly `RRULE` series (UID `weekly@test`, TZID Europe/Amsterdam, spanning a DST switch) with one `EXDATE` and one `RECURRENCE-ID` override that moves one occurrence by an hour;
  - an open-ended `FREQ=DAILY` series;
  - a floating-time event (no TZID, no `Z`);
  - an all-day `VALUE=DATE` event without `DTEND`;
  - a VEVENT with no UID;
  - two non-override VEVENTs sharing UID `dup@test` with different starts;
  - a VEVENT with `DTEND` before `DTSTART`;
  - a VEVENT without `DTSTART`;
  - a single event in 2030;
  - a `STATUS:CANCELLED` event;
  - an event without `SUMMARY`.
- [X] T024 [P] Write `backend/InTheKnow.Tests/FetcherTests.cs`:
  - A theory over `IsBlocked`: `127.0.0.1`, `::1`, `10.1.2.3`, `172.16.0.1`, `172.31.255.255`, `192.168.1.1`, `fc00::1`, `fd12::1`, `169.254.1.1`, `fe80::1`, `100.64.0.1`, `0.0.0.0`, `::`, `224.0.0.1`, `ff02::1`, `255.255.255.255`, `::ffff:10.0.0.1` and `::ffff:127.0.0.1` are blocked. `8.8.8.8`, `172.32.0.1`, `100.128.0.1` and `2001:4860:4860::8888` are allowed.
  - `CheckUrlAsync` returns `unsupported_scheme` for `file:///etc/passwd`, `ftp://x` and `http://example.com/cal.ics`, `invalid_url` for `not a url`, and `url_too_long` for a 2,049-character `https` URL.
  - With the **real** default handler (`new Fetcher()`), `FetchAsync(new Uri("https://localhost:<free port>/"))` returns error `blocked_address`. The callback refuses before any TLS handshake. This proves the `ConnectCallback`, which also covers redirects (research R16).
  - **Size bound**: `FakeHttp` serving a 200 response of 10 MB + 1 byte returns `too_large`, and one of exactly 10 MB returns the body.
  - **Network error**: a handler that throws `HttpRequestException` returns `network`, and one that throws `IOException` also returns `network`.
- [X] T025 [P] Write `backend/InTheKnow.Tests/HostBudgetTests.cs`:
  - With a default of 10/min, successive `TryReserve(host, now, MaxValue)` calls return slots exactly 6 s apart.
  - A listed domain with `MaxRequestsPerMinute` 60 gets a 1 s spacing, and an unlisted host keeps 6 s.
  - `BusyHosts` contains a host until its `nextFree` passes.
  - `TryReserve(host, now, 5 s)` returns `null` without changing state when the next slot is more than 5 s away.
  - A reservation pushes the next dispatcher slot back by one gap.
- [X] T026 [P] Write `backend/InTheKnow.Tests/SettingsTests.cs`, loading the linked shipped `appsettings.json` plus in-memory overrides:
  - With no overrides except the required values, the defaults are 10 min, 120, 16, 24 h, 12, 365 d, and `mytimetables.inholland.nl` with all features on (US3-3, US4-1).
  - An empty connection string fails, and an `ApiKey` of 31 characters fails.
  - `FeaturesFor("MyTimetables.Inholland.NL")` matches, `FeaturesFor("x.mytimetables.inholland.nl")` returns `null`, and `CanonicalFeaturesFor` returns `"generic"` for an unlisted host.
  - With `EventDetails=false`, `TeacherDetection` is effectively `false` in `Canonical`.
- [X] T027 [P] Write `backend/InTheKnow.Tests/DbTests.cs`:
  - `MigrateAsync` run twice on a `TestDb` applies version 1 once.
  - A second `TakeInstanceLockAsync` on the same database throws while the first connection is open, and succeeds after it is disposed.
  - Inserting a `calendar` with `status = 'x'` fails with a check violation. This proves the constraints are live.

**Checkpoint**: `dotnet build` and `dotnet test` pass (Fetcher, HostBudget, Settings and Db tests).

---

## Phase 3: User Story 1 - Detect changes in my timetable (Priority: P1) 🎯 MVP

**Goal**: A calendar is checked on schedule and compared with the snapshot stored in PostgreSQL. Every field change, addition and removal is recorded once. Failures record nothing.

**Independent Test**: Seed a calendar row and a link row directly in SQL. Serve `mt-nl.ics` and then `mt-nl-edited.ics` through `FakeHttp`, and call `RunDueChecksAsync` twice. Exactly the known edits must appear in `detected_change`, and nothing else (SC-001).

### Tests for User Story 1 ⚠️

- [ ] T028 [P] [US1] Create `backend/InTheKnow.Tests/Fixtures/mt-nl-edited.ics` from `mt-nl.ics` with exactly these edits, listed in a comment at the top of the test that uses them:
  - one lesson's `LOCATION` goes from `AL00.A1.38` to `AL00.A1.20` (US1-1);
  - one lesson's `DTEND` moves 30 min later (US1-2);
  - one lesson's second teacher is replaced (US1-3);
  - one `SUMMARY` is renamed (US1-4);
  - one event gains `[CONCEPT] ` and one loses it (US1-4);
  - one new VEVENT with a new UID (US1-5);
  - one VEVENT deleted (US1-5);
  - the Groep(en) order of one multi-group event is reversed, with no other change (FR-026).

  Also create `backend/InTheKnow.Tests/Fixtures/mt-nl-volatile.ics`, which is `mt-nl.ics` with only `DTSTAMP`, `CREATED`, `LAST-MODIFIED` and the `Laatst gesynchroniseerd op …` lines changed (US1-6).
- [ ] T029 [P] [US1] Create `backend/InTheKnow.Tests/Fixtures/generic-edited.ics` from `generic.ics`. The `RECURRENCE-ID` override gets a new `SUMMARY`, and one single event's `DTSTART`/`DTEND` is re-expressed from `TZID=Europe/Amsterdam` local time into the same instant in UTC (`…Z`). Nothing else changes.
- [ ] T030 [P] [US1] Write `backend/InTheKnow.Tests/FeedParserTests.cs` against `generic.ics` with a fixed horizon:
  - **Expansion**: weekly occurrences appear with keys `weekly@test|<original start UTC ISO>`. The EXDATE occurrence is absent. The override keeps the original-start key and has the moved time.
  - **Time zones**: occurrences on both sides of the DST switch resolve to the correct UTC instants. The floating event resolves in Europe/Amsterdam. The all-day event starts at local midnight (for example 1 April 2026 becomes `2026-03-31T22:00Z`) and ends the next day.
  - **Missing UID**: the UID-less event gets a `content:<sha256>` key, stable across two parses.
  - **Duplicate UIDs**: both `dup@test` events are present with `dup@test|<start>` keys.
  - **Bad end**: `DTEND < DTSTART` gives end = start, and the other events still parse.
  - **Other malformed events**: the event without `DTSTART` is skipped. The missing `SUMMARY` gives `""`. `STATUS:CANCELLED` gives `"cancelled"`.
  - **Horizon**: the 2030 single event is present even though the horizon is 12 months, and the daily series stops at the horizon.
  - **Bounds**: a synthetic `FREQ=MINUTELY` feed gives `too_many_occurrences`, and garbage text gives `unparseable`.
  - **Long keys**: a 1,100-character UID gives a `sha256:<hex>` key.
- [ ] T031 [P] [US1] Write `backend/InTheKnow.Tests/MyTimetablesTests.cs`.
  - **Parsing `mt-nl.ics` with all features on**, each event yields:
    - `type` mapped as `Les`→`lesson`, `Toets`→`test`, `Vakantie`→`holiday`, `Afspraak`→`appointment`;
    - `rooms` taken from `LOCATION`, split on `,`, sorted;
    - `teachers` paired as `"Last, First"` (FR-017), with an odd count kept as one entry;
    - `groups` sorted.
  - **Volatile data**: there is no `details` entry for the sync line or the footer.
  - **Draft marker**: the `[CONCEPT] ` event has its name without the prefix and status `tentative`.
  - **Languages**: parsing `mt-en.ics` gives `EventState`s equal to NL key by key (SC-006, the language edge case).
  - **Teacher detection off**: the teacher value is in `details.teachers` and there is no `teachers` key.
  - **Event details off**: `fields` = `{location, description}`, and the description has no sync or footer blocks.
  - **Unlisted host** (`Features` null): `fields` = the raw `{location, description}`, and the `[CONCEPT] ` prefix stays in the name.
  - **Unknown label**: an unknown `Foo: bar` block gives `details.Foo = "bar"`. An unlabelled free-text block gives `details.text`.
  - **Real data**: when `Fixtures/private/*.ics` exist, every event in them has a name, a start and an end, and every `Les` event has a type, rooms and groups. Skip this test when the folder is empty.
- [ ] T032 [P] [US1] Write `backend/InTheKnow.Tests/DifferTests.cs` with in-memory `EventState`s:
  - **Field changes**: one change per differing field, with field names and value shapes per contracts/detected-change.md. `time` covers start and end in one change, as `{"start","end"}`. Rooms, teachers and groups are compared as sets.
  - **Added and removed**: they carry the event object `{key,name,start,end,status,fields}`. The context is the new name and start, or the old ones for `removed`.
  - **Absent vs empty**: a key absent on one side and empty on the other is not a change.
  - **Horizon rule (FR-015a)**:
    - a recurring occurrence with `OccurrenceStart > previousHorizon` that is new is written to `Upserts` without a change;
    - a stored recurring occurrence with `OccurrenceStart > currentHorizon` that is missing now goes to `Deletes` without a change;
    - a single event is never horizon-exempt.
  - **Hash**: `SnapshotHash` is identical for the same events in a different order, and changes when one field changes.
- [ ] T033 [US1] Add a seed helper to `backend/InTheKnow.Tests/TestDb.cs`: `Task<(long CalendarId, long[] LinkIds)> SeedCalendarAsync(string host, string[] urls, string subscriber = "s1", DateTimeOffset? nextCheckAt = null)`. It inserts directly with SQL: one `calendar` with a random 32-byte `identity_hash` and `next_check_at` = `nextCheckAt` or epoch, and one `calendar_link` per URL (URLs must be `https://`, or the `url` CHECK rejects them). US1 tests therefore do not depend on the US2 add-link code.
- [ ] T034 [US1] Write `backend/InTheKnow.Tests/CheckerTests.cs` (part 1: detection). Build a `Checker` with a `TestDb`, `new Fetcher(fakeHttp, FakeHttp.Resolve)`, `FakeTimeProvider` and default `Settings`, and seed with T033. Cover:
  - **Baseline**: the first check records 0 changes and stores all events (US1-7).
  - **Known edits**: `mt-nl.ics` then `mt-nl-edited.ics` records exactly the T028 edits and nothing else. The Groep(en) reorder records nothing. The time and room changes of one event share `(event_key, detected_at)` (US1-1…5, SC-001).
  - **Volatile only**: `mt-nl.ics` then `mt-nl-volatile.ics` records 0 changes (US1-6).
  - **Language switch**: `mt-nl.ics` then `mt-en.ics` records 0 changes.
  - **Restart**: build a **new** `Checker` instance on the same database after the baseline, check the edited feed, and get the same changes. A further check then records nothing new (US1-8, SC-008).
  - **Generic feed**: on an unlisted host, `generic.ics` then `generic-edited.ics` records exactly one `name` change, for the override occurrence. After advancing fake time by 1 month with the unchanged `generic.ics`, the horizon shift records nothing (SC-007, FR-015a).
- [ ] T035 [US1] Add to `backend/InTheKnow.Tests/CheckerTests.cs` (part 2: outcomes and writes):
  - **No-op writes**: checking an unchanged feed twice leaves the `event_snapshot` row count and every row's `xmin` unchanged. A one-room edit changes the `xmin` of exactly one row (FR-019a, SC-010).
  - **Failures**: `http 500`, `Timeout` (fetch timeout) and an unparseable body each leave the snapshot unchanged and record no changes. They set `last_ok=false` and `last_error` to the code only, increment `consecutive_failures`, and set `failing_since` once (FR-024, FR-027).
  - **Empty feed (SC-004, FR-008a)**:
    - with two seeded links, the first returning `mt-empty.ics` after a baseline records no removals, flags link 1, and the next check uses link 2;
    - with every link empty, the calendar becomes `failing` and `next_check_at` = check start + `FailingCheckInterval`;
    - when a flagged link serves the **same** `mt-nl.ics` as before (the hash no-op path), the link is un-flagged, the calendar returns to `ok`, `consecutive_failures=0`, and `next_check_at` = check start + `CheckInterval`.
  - **Retention**: `RunRetentionAsync(now)` deletes changes with `detected_at < now - ChangeRetention` and keeps newer ones (FR-025a). Seed 25,000 expired changes so that more than two batches run, and check that all are gone.
- [ ] T036 [US1] Add to `backend/InTheKnow.Tests/CheckerTests.cs` (part 3: stuck checks and leases, SC-012, FR-014a):
  - **Stuck fetch**: one calendar whose URL is `Hang`, plus three normal calendars, are all due. Start `RunDueChecksAsync` without awaiting it, and advance `FakeTimeProvider` by 2 min. The three normal calendars are checked successfully before the advance. The stuck one is recorded `last_error='timeout'` with no changes, and its worker slot is released (a further due calendar gets checked).
  - **Stuck hook**: the same with `BeforeWriteHook` blocking on a `TaskCompletionSource` that ignores cancellation.
  - **Orphan resumes**: release the hook after the timeout was recorded. It commits no `event_snapshot` rows and no `detected_change` rows, and `last_error` stays `timeout`. Repeat after the calendar was claimed again by a later `RunDueChecksAsync`: still nothing is committed by the orphan.
  - **Timeout while the row is locked**: the same stuck fetch, but a separate test connection holds `SELECT … FROM calendar WHERE id=@id FOR UPDATE` when the 2 min pass. The worker slot is released immediately: a further due calendar is checked while the lock is still held. After the test connection releases the lock, `last_error='timeout'` is recorded and no changes are committed (FR-014a, SC-012).
  - **Lease recovery**: set `lease_id` and `next_check_at = now + 5 min` directly, as a claim by a process that then died. The calendar is not claimed before 5 min, is claimed after `Advance(5 min)`, and records its changes exactly once (SC-008).

### Implementation for User Story 1

- [ ] T037 [P] [US1] Implement `backend/InTheKnow/FeedParser.cs` `Parse` (research R2, R6, R7, R8, R8a, R10).
  - **1. Pre-scan**: scan the raw text and, in every `BEGIN:VEVENT` … `END:VEVENT` block with no line starting `UID`, insert the line `X-INTHEKNOW-NO-UID:1`. Ical.Net gives UID-less events a random GUID, so it cannot be relied on.
  - **2. Load**: `Calendar.Load(text)`. Any exception returns `unparseable`.
  - **3. Fix bad ends**: for each event with `DtEnd < DtStart`, set `DtEnd = DtStart` **before** any expansion. One such event otherwise makes the whole enumeration throw `Duration must be >= 0`.
  - **4. Skip missing starts**: skip events without `DtStart`, logging a warning with `calendarId` and the event's position only.
  - **5. Resolve the zone**: use `X-WR-TIMEZONE` (`calendar.Properties["X-WR-TIMEZONE"]?.Value`), else the TZID of the only VTIMEZONE when there is exactly one, else UTC. Floating times use `new CalDateTime(dt.Date, dt.Time, zone).AsUtc`. Date-only values use `new CalDateTime(date, new TimeOnly(0, 0), zone).AsUtc`; `new CalDateTime(date, null, zone)` ignores the zone.
  - **6. Expand**: take `calendar.GetOccurrences<CalendarEvent>().TakeWhileBefore(horizon)`, **plus** every single event whose start is ≥ horizon. `TakeWhileBefore` also drops single events. A single event has `RecurrenceRules.Count == 0`, no `RecurrenceDates` and `RecurrenceIdentifier == null`. Start and end come from `occurrence.Period.StartTime` and `EffectiveEndTime`.
  - **7. Keys**:
    - a single event is keyed by its UID;
    - a recurring occurrence is keyed by `UID|<original start UTC, ISO 8601>`, where the original start is `Source.RecurrenceIdentifier.StartTime` for overrides (not the obsolete `RecurrenceId`), otherwise the occurrence start, and `OccurrenceStart` is set to that original start;
    - a marked UID-less event is keyed by `content:<hex sha256(name|start|end|location)>`;
    - a UID on more than one non-override VEVENT gives each of them the key `UID|<start UTC>`, and remaining collisions keep the first in document order and log a warning with `calendarId`;
    - keys longer than 1,000 characters become `sha256:<hex>`.
  - **8. Fields**: `Name = Summary ?? ""`, `Status = Status?.ToLowerInvariant()`, and Location and Description taken raw.
  - **9. Bounds**: return `too_many_occurrences` once the count exceeds 50,000, and `unparseable` for any evaluation exception.
  - **Cancellation**: call `ct.ThrowIfCancellationRequested()` between stages.
- [ ] T038 [P] [US1] Implement `backend/InTheKnow/MyTimetables.cs` `ToState(Occurrence o, Features? f)` (research R9, FR-015 to FR-018, FR-016a).
  - **Unlisted host (`f == null`)**: `fields = {location, description}`, taken raw.
  - **Listed host, common steps**:
    1. With `f.DraftMarkerDetection`, a name starting with `[CONCEPT] ` or `[DRAFT] ` loses the prefix and gets status `tentative`.
    2. Split the description on blank lines (`\n\s*\n`).
    3. Drop volatile blocks by prefix, **before** label parsing: `Laatst gesynchroniseerd op`, `Last synchronised on`, `Deze afspraak wordt beheerd door MyTimetables.` and `This appointment is managed by MyTimetables.`.
  - **Event details off**: `fields = {location, description: the remaining blocks joined with "\n\n"}`.
  - **Event details on**:
    - A block is labelled when its first line matches `^([^\d:]+):(\s|$)`. Its value is the rest of the block, lines joined with `\n` and trimmed.
    - Map `Type` → `type`, using `Les`/`Lesson` → `lesson`, `Toets`/`Test` → `test`, `Vakantie`/`Holiday` → `holiday` and `Afspraak`/`Appointment` → `appointment`, and keeping unknown values verbatim.
    - `Ruimte(s)`/`Room(s)` is consumed and ignored; rooms come from `LOCATION` split on `,`.
    - `Docent(en)`/`Staff member(s)` → `teachers`. Split on `,`, trim, and pair parts as `"Last, First"`; an odd count keeps the whole trimmed value as one entry. Only with `f.TeacherDetection`; otherwise the raw value goes to `details.teachers`.
    - `Groep(en)`/`Student set(s)` → `groups`, split on `,`.
    - Any other label → `details[<label verbatim>]`. Unlabelled blocks are joined with `\n\n` into `details.text`.
  - **All paths**: lists are trimmed, empty entries removed, de-duplicated and sorted ordinally. Keys with null, empty-string or empty-list values are omitted. `fields` keys are written in sorted order.
- [ ] T039 [US1] Implement `backend/InTheKnow/Differ.cs` (depends on T038 for `EventState`; research R7, R14; contracts/detected-change.md).
  - **Changed events**: for each key in both `stored` and `current`, emit `changed` rows for:
    - `name`;
    - `time` (`{"start","end"}` ISO UTC; one change when either differs);
    - `status`;
    - every top-level `fields` key other than `details` whose values differ (compared with `JsonNode.DeepEquals`; absent equals empty);
    - each `details.<label>` that differs.

    The event goes in `Upserts` when anything differs.
  - **New keys**: `added`, with `new_value` = the event object `{key,name,start,end,status,fields}`, and in `Upserts`. The exception is a recurring occurrence (`OccurrenceStart != null`) whose `OccurrenceStart > previousHorizon`: it goes in `Upserts` only.
  - **Missing keys**: `removed`, with `old_value` = the event object, and in `Deletes`. The exception is a recurring occurrence whose `OccurrenceStart > currentHorizon`: it goes in `Deletes` only.
  - **Context**: `EventName`/`EventStartsAt` are the new values, or the old ones for `removed`.
  - **`SnapshotHash`**: SHA-256 over the events sorted by key (ordinal), serialised canonically as one JSON array of `[key,name,start,end,status,occurrenceStart,fields]` with ISO UTC times.
- [ ] T040 [US1] Implement the check input half of `backend/InTheKnow/Checker.cs`.
  - **Claim**: `ClaimAsync(now, busyHosts)` computes `leaseEnd = now + 5 min` and `lease = Guid.NewGuid()` in C#, then runs `UPDATE calendar SET next_check_at = @leaseEnd, lease_id = @lease WHERE id = (SELECT id FROM calendar WHERE next_check_at <= @now AND host <> ALL(@busyHosts) ORDER BY next_check_at LIMIT 1 FOR UPDATE SKIP LOCKED) RETURNING id, host, status` (research R4). All times come from `TimeProvider` parameters, never SQL `now()`.
  - **Link selection (FR-008)**: when `status='ok'`, use `SELECT id, url FROM calendar_link WHERE calendar_id=@id AND flagged_at IS NULL ORDER BY id LIMIT 1`. When the status is `failing`, or no unflagged link exists, use `… ORDER BY last_used_at NULLS FIRST, id LIMIT 1` over all links.
  - **Pipeline**: `Fetcher.FetchAsync`, then `FeedParser.Parse` with horizon `startedAt.AddMonths(ExpansionHorizonMonths)`, then `MyTimetables.ToState(o, settings.FeaturesFor(host))`, then `Differ.SnapshotHash`, then `await BeforeWriteHook?.Invoke(calendarId, ct)`.
  - **Logging**: log only calendar ids, link ids and reason codes, never URLs or exception messages. For an unexpected exception, log its type only.
- [ ] T041 [US1] Implement the outcome transaction in `backend/InTheKnow/Checker.cs` (data-model.md § One check, research R14). Every outcome runs in **one** transaction that starts with the lease fence `SELECT status, baseline_features, snapshot_hash, horizon_end, EXISTS(SELECT 1 FROM event_snapshot WHERE calendar_id=@id) FROM calendar WHERE id=@id AND lease_id=@lease FOR UPDATE`. When no row comes back, roll back and write **nothing**.

  Evaluate the rows **in this order**; the first match wins:
  1. **Fetch or parse error** (including `too_many_occurrences`): failure with that code.
  2. **Empty feed under protection**: 0 events, `FeaturesFor(host)?.EmptyFeedProtection == true`, and the snapshot has events. Record failure `empty_feed` and set the link's `flagged_at = startedAt`. When no unflagged link remains for the calendar, set `status='failing'`. This row comes **before** row 3, so a feature switch combined with a revoked link can never store an empty baseline over a good snapshot.
  3. **Baseline**: `baseline_features` is NULL or differs from `settings.CanonicalFeaturesFor(host)`. `DELETE FROM event_snapshot WHERE calendar_id=@id`, write all states with `CopySnapshotAsync` (below), record **no** changes, and set `baseline_features`.
  4. **Unchanged**: the hash equals `snapshot_hash`. Neither read nor write `event_snapshot`.
  5. **Changed**: load the snapshot rows, run `Differ.Diff(stored, states, horizon_end, currentHorizon)`, then:
     - `DELETE … WHERE calendar_id=@id AND event_key = ANY(@deletes)`;
     - upsert `Upserts` with `INSERT … SELECT FROM unnest(…) ON CONFLICT (calendar_id, event_key) DO UPDATE`;
     - binary-`COPY` the change rows into `detected_change`, all with `detected_at = startedAt`.

  `internal static CopySnapshotAsync(connection, calendarId, states, ct)` binary-`COPY`s the states into `event_snapshot`. It runs inside the caller's open transaction, and add-link (T048) reuses it for a new calendar's baseline.

  **Every success (rows 3–5), including row 4**, sets:
  - `status='ok'`, `consecutive_failures=0`, `failing_since=NULL`;
  - `last_ok=true`, `last_error=NULL`, `last_checked_at`, `last_link_id`, `last_event_count`, `last_change_count`;
  - `snapshot_hash`, and `horizon_end` = the current horizon;
  - `next_check_at = startedAt + CheckInterval`, `lease_id=NULL`;
  - on the fetch link: `flagged_at=NULL`, `last_used_at=startedAt`.

  **Every failure (rows 1–2)** sets:
  - `last_ok=false`, `last_error=<code>`, `last_checked_at`, `last_link_id`;
  - `consecutive_failures = consecutive_failures + 1`, `failing_since = COALESCE(failing_since, @startedAt)`;
  - `next_check_at = startedAt + (resulting status = 'failing' ? FailingCheckInterval : CheckInterval)`, `lease_id=NULL`;
  - the link's `last_used_at=startedAt`.
- [ ] T042 [US1] Implement the dispatcher in `backend/InTheKnow/Checker.cs` (research R4, FR-012b, FR-014a).
  - **Worker slots**: `SemaphoreSlim(MaxConcurrentChecks)`.
  - **One pass**:
    1. await a slot;
    2. claim with `budget.BusyHosts(now)`. Mark the claim query with `// ponytail: while the main host is busy, a pass that finds nothing walks its whole due backlog (host <> ALL can't use an index); ms at thousands of rows, add a per-host queue if it shows up in profiles`;
    3. when nothing was claimed, release the slot and return false;
    4. otherwise take `slot = budget.TryReserve(host, now, TimeSpan.MaxValue)` and start `RunGuardedAsync` **without awaiting it**.
  - **`RunGuardedAsync`**:
    1. When `slot > now`, `Task.Delay(slot - now, time)` (a validation reserved in between).
    2. Run the check under `new CancellationTokenSource(TimeSpan.FromMinutes(2), time)`, linked with the stopping token.
    3. Await it with `.WaitAsync(TimeSpan.FromMinutes(2), time)`.
    4. On `TimeoutException` or `OperationCanceledException` from the limit, write failure `timeout` in its own lease-fenced transaction (same failure columns as T041). The abandoned task is left to finish on its own; its later writes fail the fence.
       - Release the worker slot **before** this write, so waiting on a row lock never holds a slot.
       - This write takes **only the stopping token**, never the check's token, which is already cancelled and would make the write throw so the timeout is never recorded.
       - Its fence is the normal blocking `FOR UPDATE`. If the stuck check holds the row inside its outcome transaction, the cancellation rolls it back and the wrapper then records `timeout`; if the check committed first, the fence finds no lease and writes nothing (research R4).
    5. Release the worker slot in `finally` (once: step 4 may already have released it).
  - **`startedAt`**: the `now` of the pass that claimed the calendar, which is the argument to `RunDueChecksAsync` or `time.GetUtcNow()` in `ExecuteAsync`. Never read the clock again inside the check. T041 uses it for `next_check_at`, `detected_at`, `last_checked_at` and `last_used_at`, and the interval-count tests rely on it.
  - **`RunDueChecksAsync(now)`**: loop passes at the given `now` until one returns false, then await all started tasks.
  - **`ExecuteAsync`** loops until stopping:
    1. run a pass at `time.GetUtcNow()`;
    2. when nothing was claimable, `Task.Delay(min(1 s, EarliestFree(now) - now), time, stoppingToken)`;
    3. every 10 min of `TimeProvider` time, call `RunRetentionAsync`.
- [ ] T043 [US1] Implement `RunRetentionAsync(now)` in `backend/InTheKnow/Checker.cs` (FR-025a, data-model.md § Lifecycle): with `@cutoff = now - ChangeRetention`, repeat `DELETE FROM detected_change WHERE id IN (SELECT id FROM detected_change WHERE detected_at < @cutoff LIMIT 10000)` until it deletes fewer than 10,000 rows, each in its own statement, so an expiring burst never becomes one long transaction.
- [ ] T044 [US1] In `backend/InTheKnow/Program.cs`, at `// checker (T044)`, register `builder.Services.AddSingleton<Checker>()` and `builder.Services.AddHostedService(sp => sp.GetRequiredService<Checker>())`.

**Checkpoint**: US1 tests pass. A seeded calendar is checked on schedule and its changes are recorded (MVP).

---

## Phase 4: User Story 2 - Check each real calendar only once (Priority: P2)

**Goal**: Adding a link validates it once and attaches it to the right calendar under the identity rules, so each real calendar is fetched once per interval.

**Independent Test**: Add the duplicate, classmate, other-group and personal-timetable links through `Subscriptions.AddLinkAsync`. Check the number of calendars, then advance fake time over three intervals with `RunDueChecksAsync`. `FakeHttp` must count 1 scheduled request per calendar per interval, plus 1 per validation (SC-003).

### Tests for User Story 2 ⚠️

- [ ] T045 [P] [US2] Write `backend/InTheKnow.Tests/LinkIdentityTests.cs`. Add a helper that builds synthetic MyTimetables links as `https://<host>/ical?<hex>&timetable=<base64("Xedule!<kind>!<base32(id)>")>&eu=<base64(userId)>&h=<token>`. Test:
  - `Normalise` lower-cases scheme and host, drops `:443`/`:80`, and sorts query parameters (FR-004, US2-6).
  - Links differing only in the hex marker get equal keys (US2-1, FR-005).
  - Two users' `xedule_studentgroup` links for the same id and host get the key `mt|<host>|<decoded timetable>` (US2-2, FR-006).
  - Different group ids get different keys (US2-3).
  - A non-studentgroup kind with a different `eu` gets different keys (US2-4, FR-007).
  - The same links on an unlisted host get normalised-URL keys (US2-7).
  - A listed host whose link lacks `/ical`, `timetable`, `eu` or `h` falls back to the normalised URL.
  - With `ClassGroupSharing=false`, the classmates' keys differ. With `DuplicateLinkMerging=false`, the hex variants differ.
  - `Hash` is 32 bytes.
- [ ] T046 [P] [US2] Write `backend/InTheKnow.Tests/SubscriptionsTests.cs` with a `TestDb`, `FakeHttp` and `FakeTimeProvider`. Test:
  - **Duplicates and classmates**: a hex-variant link and a classmate link (both serving `mt-nl.ics`) attach to the first calendar, with `calendarIsNew=false` (US2-1, US2-2).
  - **Separate calendars**: a different group and a personal timetable each create a new calendar (US2-3, US2-4).
  - **Forged link**: a classmate-shaped link serving `mt-empty.ics` returns `validation_failed:empty_feed` and nothing is inserted (US2-5, research R11).
  - **Generic links**: the same generic link with a different host case and query order attaches to the same calendar (US2-6).
  - **Same link again**: the same subscriber adding the same exact link returns `AlreadyExisted=true`, with **no** fetch.
  - **Rejected inputs**: `invalid_subscriber` for `a b`, and `unsupported_scheme` for `ftp://…` and for an `http://` link (FR-001).
  - **Budget exhausted**: `host_budget_exhausted` when more than 5 s of reservations are already queued for the host.
  - **Baseline from validation (FR-023, research R11)**: a link that creates a calendar leaves `event_snapshot` rows equal to what a first check of `mt-nl.ics` stores, 0 `detected_change` rows, `baseline_features` = the host's canonical features, `snapshot_hash` and `horizon_end` set, `last_ok=true`, and `next_check_at = now + CheckInterval`. The first scheduled check of the unchanged feed then takes the hash no-op path and records 0 changes.
  - **Nothing stored from a join**: a link that joins an existing calendar writes no `event_snapshot` or `detected_change` rows and leaves the calendar's columns unchanged.
  - **SC-003 / US3-1**: with N=4 links to one class-group calendar, run `RunDueChecksAsync` after advancing through 3 intervals. `FakeHttp` counts exactly 3 scheduled requests plus 4 validation requests for that timetable.

### Implementation for User Story 2

- [ ] T047 [P] [US2] Implement `backend/InTheKnow/LinkIdentity.cs` (research R12, investigation.md §1).
  - **`Normalise`**: lower-case the scheme and host, drop the default port, keep the path unchanged, and sort the query parameters ordinally by name and then value, keeping valueless parameters as bare names.
  - **MyTimetables structure**:
    - the host is listed in `settings`;
    - the path is `/ical` (case-insensitive);
    - `timetable`, `eu` and `h` are present;
    - the optional generation marker is a bare parameter matching `^[0-9a-fA-F]+$` with no `=`.
  - **Decoding**: `timetable` is URL-decoded base64, tolerant of missing padding and of `-`/`_`, and reads `Source!kind!id`. `kind == "xedule_studentgroup"` marks a class group.
  - **Key rules, in order**:
    1. not a listed host, or no MyTimetables structure: the normalised URL, with `MyTimetablesDerived=false`;
    2. `ClassGroupSharing` on and the kind is `xedule_studentgroup`: `mt|<host>|<decoded timetable string>`, with `MyTimetablesDerived=true`;
    3. `DuplicateLinkMerging` on: the normalised URL without the hex marker parameter, with `MyTimetablesDerived=true`;
    4. otherwise rule 1.
  - **`Hash(key)`**: `SHA256.HashData(Encoding.UTF8.GetBytes(key))`. The key is never stored or logged, because rules 1 and 3 contain the token.
- [ ] T048 [US2] Implement `AddLinkAsync` in `backend/InTheKnow/Subscriptions.cs` (FR-001, FR-003 to FR-007, research R11).
  1. When `subscriber` does not match `^[A-Za-z0-9._@-]{1,100}$`, return `invalid_subscriber`.
  2. Run `Fetcher.CheckUrlAsync`, returning its reason on failure. Store the URL as `uri.AbsoluteUri`.
  3. Compute `LinkIdentity.KeyFor` and its `Hash`, and look up `calendar.id` by `identity_hash`.
  4. When that calendar already has a link with the same subscriber and URL, return it with `AlreadyExisted=true` and do **not** fetch.
  5. Take `slot = budget.TryReserve(host, now, 5 s)`. When it is `null`, return `host_budget_exhausted`. Otherwise `Task.Delay(slot - now, time)`.
  6. Run `FetchAsync`, then `FeedParser.Parse` with horizon `now.AddMonths(ExpansionHorizonMonths)`. On error, return `validation_failed:<code>`.
  7. Require at least 1 event when `FeaturesFor(host)?.EmptyFeedProtection == true`, **or** when the link would join an existing calendar with `MyTimetablesDerived`. Otherwise return `validation_failed:empty_feed`.
  8. Never diff the validation result.
  9. In one transaction:
     - `INSERT INTO calendar (identity_hash, host, next_check_at, baseline_features, snapshot_hash, horizon_end, last_checked_at, last_ok, last_event_count, last_change_count) VALUES (…, lower(host), @now + CheckInterval, CanonicalFeaturesFor(host), …, @now, true, @count, 0) ON CONFLICT (identity_hash) DO NOTHING RETURNING id`. The states come from `MyTimetables.ToState(o, settings.FeaturesFor(host))` and the hash from `Differ.SnapshotHash`, exactly as in a check (T040). When a concurrent add won, fall back to a `SELECT` and treat the link as joining;
     - `INSERT INTO calendar_link (calendar_id, subscriber, url) … ON CONFLICT (calendar_id, subscriber, url) DO NOTHING RETURNING id`;
     - only when this call created the calendar: set `calendar.last_link_id` and the link's `last_used_at = @now`, and write the states with `Checker.CopySnapshotAsync` (research R11: a new calendar is baselined from its validation fetch; a joining link stores nothing).
  10. Never log the URL.
- [ ] T049 [US2] In `backend/InTheKnow/Program.cs`, at `// subscriptions (T049)`, register `builder.Services.AddSingleton<Subscriptions>()`.

**Checkpoint**: US1 and US2 tests pass. Links are grouped correctly and each calendar is fetched once per interval.

---

## Phase 5: User Story 5 - Manage subscriptions through an API (Priority: P2)

**Goal**: The HTTP API from contracts/api.md, behind the operator key.

**Independent Test**: Through `WebApplicationFactory`: add a link, list the calendars, read the changes after a known edit, and remove the subscription. Requests without the key get 401. No response body or log line contains a link.

### Tests for User Story 5 ⚠️

- [ ] T050 [US5] Write `backend/InTheKnow.Tests/ApiTests.cs`.
  - **Test host**: a `WebApplicationFactory<Program>` per test with:
    - `builder.UseSetting` for `ConnectionStrings:Default` = `TestDb.ConnectionString` and a 48-character `InTheKnow:ApiKey`. `Settings` is a DI factory (T018), so these are visible when it is resolved;
    - `ConfigureTestServices` replacing `Fetcher` with `new Fetcher(fakeHttp, FakeHttp.Resolve)` and `TimeProvider` with a `FakeTimeProvider`;
    - `RemoveAll<IHostedService>()`, so the background checker does not run;
    - a capturing `ILoggerProvider` that stores every formatted log line.
  - **Assert**:
    - **Auth**: every route returns 401 with a missing key and with a wrong key, and nothing is changed (US5-2, SC-011).
    - **Add**: POST returns 201 `{calendarId, linkId, calendarIsNew:true}`; the same link again returns 200; a second hex variant returns 201 with `calendarIsNew:false` (US5-1). GET calendars right after the first add shows `lastOk: true` (baseline from validation).
    - **Error mapping**: 400 `invalid_subscriber`/`unsupported_scheme` (including an `http://` link), 422 `validation_failed:empty_feed`, and 429 `host_budget_exhausted` with a `Retry-After: 5` header, each as Problem Details with a `reason` field.
    - **List**: GET calendars shows `links: [{linkId, flagged, createdAt}]`, and an unknown subscriber gets `[]`.
    - **Changes**: the add stored the `mt-nl.ics` baseline. Switch the URL to `mt-nl-edited.ics`, advance the `FakeTimeProvider` by `CheckInterval`, and run `Checker.RunDueChecksAsync` (resolved from `factory.Services`). GET changes returns only that subscriber's changes, in id order. `limit=2` pages with `next`. `limit=0` and `limit=501` give 400 `invalid_limit`. A second subscriber sees none of them (US5-3).
    - **Remove**: DELETE returns 204. When it was the last subscriber, the `calendar`, `calendar_link`, `event_snapshot` and `detected_change` rows are gone. A repeat returns 404 (US5-4, FR-008).
    - **Overview**: `GET /api/calendars?status=failing` lists the failing calendars with `subscribers`, `validLinks` and `totalLinks` counts, and `overdue=true` filters `nextCheckAt < now - 1 min` (US5-5). `status=x` gives 400 `invalid_status`. With 3 calendars, `limit=2` returns 2 items and a `next`, and `after=<next>` returns the third with `next: null`; `limit=0` gives 400 `invalid_limit`.
    - **No links anywhere**: no response body and no captured log line contains any test URL or its `h` value (FR-032, SC-011).

### Implementation for User Story 5

- [ ] T051 [US5] Implement `RemoveAsync(subscriber, calendarId)` in `backend/InTheKnow/Subscriptions.cs`. In one transaction:
  1. `DELETE FROM calendar_link WHERE calendar_id=@c AND subscriber=@s`. When 0 rows are deleted, return `false`.
  2. `DELETE FROM calendar WHERE id=@c AND NOT EXISTS (SELECT 1 FROM calendar_link WHERE calendar_id=@c)`. The cascade removes snapshots and changes (FR-008).
  3. Return `true`.
- [ ] T052 [US5] Create `backend/InTheKnow/Api.cs` with `static void MapApi(this WebApplication app)`:
  - **Route group**: `app.MapGroup("/api")` with an endpoint filter that reads `Authorization: Bearer <key>`. The filter compares `SHA256.HashData(presented)` with `SHA256.HashData(configured)` using `CryptographicOperations.FixedTimeEquals`, and returns `Results.StatusCode(401)` with no body on a missing or wrong key, before any handler runs (FR-031, research R15).
  - **Error helper**: `Problem(int status, string reason)` returns `Results.Problem(statusCode: status, extensions: { ["reason"] = reason })`.
  - **Subscriber check**: validate `{subscriber}` against `^[A-Za-z0-9._@-]{1,100}$`, returning 400 `invalid_subscriber`.
  - **Clock**: all times come from `TimeProvider`.
- [ ] T053 [US5] In `backend/InTheKnow/Api.cs`, add `POST /api/subscribers/{subscriber}/links` with body `{ "url": string }`.
  - **Call**: `Subscriptions.AddLinkAsync`.
  - **Success**: 201, or 200 when `AlreadyExisted`, with `{calendarId, linkId, calendarIsNew}`.
  - **Errors**:
    - `invalid_subscriber`, `invalid_url`, `unsupported_scheme`, `url_too_long` and `blocked_address` → 400;
    - `validation_failed:*` → 422;
    - `host_budget_exhausted` → 429, with the header `Retry-After: 5` (the validation's longest wait).
  - **Privacy**: never echo the URL.
- [ ] T054 [US5] In `backend/InTheKnow/Api.cs`, add `GET /api/subscribers/{subscriber}/calendars`. It returns the calendars joined through `calendar_link` for that subscriber, as `[{calendarId, status, lastCheckedAt, lastOk, lastError, consecutiveFailures, nextCheckAt, links: [{linkId, flagged: flagged_at IS NOT NULL, createdAt}]}]` (links of that subscriber only, never the URL). An unknown subscriber gets `[]`.
- [ ] T055 [US5] In `backend/InTheKnow/Api.cs`, add `DELETE /api/subscribers/{subscriber}/calendars/{calendarId:long}`, calling `Subscriptions.RemoveAsync`. It returns 204, or 404 when the result is `false`.
- [ ] T056 [US5] In `backend/InTheKnow/Api.cs`, add `GET /api/subscribers/{subscriber}/changes?after={id}&limit={n}`.
  - **Parameters**: `after` defaults to 0, and `limit` defaults to 100. A limit outside 1–500 gets 400 `invalid_limit`.
  - **Query**: `SELECT c.* FROM detected_change c JOIN subscription s ON s.calendar_id = c.calendar_id WHERE s.subscriber=@s AND c.id > @after ORDER BY c.id LIMIT @limit + 1`.
  - **Response**: `{items: [{id, calendarId, eventKey, kind, field, oldValue, newValue, eventName, eventStartsAt, detectedAt}], next}`, with `oldValue`/`newValue` as raw JSON. `next` is the last item's id when an extra row existed, otherwise `null`.
  - **Read-only**: never set `notified_at`.
- [ ] T057 [US5] In `backend/InTheKnow/Api.cs`, add `GET /api/calendars?status={ok|failing}&overdue={true}&after={id}&limit={n}`.
  - **Filters**: `status` is optional, with any other value giving 400 `invalid_status`. `overdue=true` filters `next_check_at < @now - 1 min`.
  - **Paging**: keyset by calendar id, like T056: `after` defaults to 0, `limit` defaults to 100 and allows 1–500 (otherwise 400 `invalid_limit`). Query `… WHERE c.id > @after ORDER BY c.id LIMIT @limit + 1`, and count the links per calendar only for that page.
  - **Response**: `{items, next}`. Each item is the calendar object from T054, with `links` replaced by `subscribers` (the count of distinct subscribers), `validLinks` (links with `flagged_at IS NULL`) and `totalLinks`. No URLs and no subscriber ids.
- [ ] T058 [US5] In `backend/InTheKnow/Program.cs`, at `// api (T058)`, add `builder.Services.AddProblemDetails()` and `app.MapApi()`. Do not add HTTP logging, CORS or OpenAPI (contracts/api.md § Not in this feature).

**Checkpoint**: US1, US2 and US5 tests pass. The API can be used from start to finish (quickstart §2).

---

## Phase 6: User Story 3 - Configure how often calendars are checked (Priority: P3)

**Goal**: The operator controls the check interval, the failing interval, the per-host budgets and concurrency. Values are clamped or validated at startup.

**Independent Test**: Configure an interval of 30 min, a failing interval of 6 h, a budget override and a concurrency of 3, then advance `FakeTimeProvider`. Checks follow the configuration, and invalid values fail or are clamped with a warning.

### Tests for User Story 3 ⚠️

- [ ] T059 [P] [US3] Add to `backend/InTheKnow.Tests/SettingsTests.cs`, using a capturing logger:
  - **Clamped with a warning**: `CheckInterval=00:01:00` becomes 5 min with a warning (US3-2). `FailingCheckInterval=00:07:00` with `CheckInterval=00:10:00` becomes 10 min with a warning.
  - **Startup failures**: `MaxConcurrentChecks` 0 and 65, `MaxRequestsPerHostPerMinute` 0, a domain `MaxRequestsPerMinute` 0, `ExpansionHorizonMonths` 0, and `ChangeRetention` `00:00:00`.
  - **Accepted**: `MaxConcurrentChecks` 1 and 64.
- [ ] T060 [P] [US3] Add to `backend/InTheKnow.Tests/CheckerTests.cs` (scheduling):
  - **Interval**: with `CheckInterval=00:30:00`, a calendar is fetched once per 30 fake minutes over 3 hours (US3-1).
  - **Failing interval**: with `FailingCheckInterval=06:00:00` and every link serving `mt-empty.ics` after a baseline, the calendar is fetched every 6 h. After a link serves `mt-nl.ics` again, it returns to the 30-min interval (US3-4).
- [ ] T061 [US3] Add to `backend/InTheKnow.Tests/CheckerTests.cs` (budget spacing, SC-013, FR-012a, FR-012b). With `MaxRequestsPerHostPerMinute=10`, 50 calendars on one host all have the same `next_check_at` (as after a restart). Loop `RunDueChecksAsync(now)` and `Advance(1 s)` for 6 fake minutes. Assert:
  - consecutive `FakeHttp` timestamps are exactly 6 s apart;
  - the order is longest-waiting first (seed slightly different `next_check_at` values to make the order observable);
  - all 50 are fetched within 5 min and none is skipped;
  - every `next_check_at` = its actual check time + `CheckInterval`, so they stay 6 s apart.

  In a **separate** test with the same backlog: call `AddLinkAsync` for that host at a moment when 5 s or less remain until the host's next slot (the 5 s cap otherwise returns `host_budget_exhausted`). Do not await it straight away; advance `FakeTimeProvider` until it completes, because its `Task.Delay` runs on fake time. It succeeds, its request lands in the next slot, and the following check request comes one gap later.
- [ ] T062 [US3] Add to `backend/InTheKnow.Tests/CheckerTests.cs` (budget override, US3-5):
  - With a default of 10/min and `mytimetables.inholland.nl` `MaxRequestsPerMinute=60`, requests to that domain are 1 s apart and requests to an unlisted host 6 s apart.
  - Build a new `Settings`, `HostBudget` and `Checker` with the override raised to 120/min (a restart): the spacing becomes 0.5 s, no calendar is skipped, and 0 changes are recorded (the budget is not a feature, so no re-baseline).
- [ ] T063 [US3] Add to `backend/InTheKnow.Tests/CheckerTests.cs` (concurrency, US3-6, FR-014a). With `MaxConcurrentChecks=3`, 10 due calendars and the `FakeHttp` gate set, start `RunDueChecksAsync` without awaiting it:
  - `FakeHttp` max-in-flight is exactly 3;
  - completing one gated request lets exactly one more start;
  - after the gate opens, all 10 finish.

### Implementation for User Story 3

- [ ] T064 [US3] Add clamping and validation to `Settings.Load` in `backend/InTheKnow/Settings.cs` (contracts/configuration.md § Settings):
  - **Clamped with a warning** through the passed `ILogger`:
    - `CheckInterval < 00:05:00` → 5 min;
    - `FailingCheckInterval < CheckInterval` → `CheckInterval`.
  - **Startup failures** (throw `InvalidOperationException` naming the key):
    - `MaxRequestsPerHostPerMinute < 1`;
    - `MaxConcurrentChecks` outside 1–64;
    - `ExpansionHorizonMonths < 1`;
    - `ChangeRetention <= TimeSpan.Zero`;
    - any domain `MaxRequestsPerMinute < 1`.

**Checkpoint**: US3 tests pass. Scheduling follows the operator's configuration.

---

## Phase 7: User Story 4 - Configure where MyTimetables features apply (Priority: P3)

**Goal**: The configured domain list and its per-domain features decide how each link is grouped and parsed. Configuration changes regroup links at startup and re-baseline calendars without recording changes.

**Independent Test**: Serve the same MyTimetables feed from an unlisted host, a fully enabled domain and `rooster.example.nl` with `TeacherDetection=false`. Each gets exactly its features. Restarting with changed features re-baselines with 0 changes, and disabling class-group sharing splits a shared calendar.

### Tests for User Story 4 ⚠️

- [ ] T065 [P] [US4] Add to `backend/InTheKnow.Tests/SettingsTests.cs`: two domain entries `mytimetables.inholland.nl` and `MyTimetables.Inholland.nl` fail at startup as duplicate hosts.
- [ ] T066 [P] [US4] Add to `backend/InTheKnow.Tests/CheckerTests.cs` (features per domain, SC-009):
  - **Per-domain features**: `mt-nl.ics` is seeded on three hosts: unlisted, `mytimetables.inholland.nl` (all on), and `rooster.example.nl` (`TeacherDetection=false`). Their snapshot `fields` are, respectively, `{location, description}`, the parsed parts with `teachers`, and the parsed parts with `details.teachers` and no `teachers` (US4-2, US4-3).
  - **Feature switch**: after a baseline, rebuild `Settings` and `Checker` with `EventDetails=false` for the domain, and check the same feed again. It records 0 changes, and `baseline_features` and the snapshot `fields` now reflect the new features (US4-5, FR-026a).
  - **Revoked link during a switch**: the same switch combined with an `mt-empty.ics` response keeps the old snapshot and flags the link (row 2 before row 3).
- [ ] T067 [US4] Add to `backend/InTheKnow.Tests/CheckerTests.cs` (regroup, research R13):
  - **Split**: two subscribers' class-group links are added through `AddLinkAsync` into one calendar. `RegroupAsync` under settings with `ClassGroupSharing=false` gives two calendars, each with one link. The original calendar keeps its snapshot, and the new calendar has `baseline_features IS NULL` and records 0 changes on its first check (US4-4).
  - **Merge back**: `RegroupAsync` with sharing back on merges them, and the calendar left without links is deleted.
  - **Unlisted host**: removing the domain from the list regroups its links under normalised-URL keys.

### Implementation for User Story 4

- [ ] T068 [US4] In `backend/InTheKnow/Settings.cs`, fail startup when two `MyTimetables:Domains` entries have the same `Host` case-insensitively, or when a `Host` is empty.
- [ ] T069 [US4] Implement `RegroupAsync(now)` in `backend/InTheKnow/Checker.cs` (research R13), in one transaction, run before the checker starts.
  1. Load every link `(id, calendar_id, subscriber, url)` and every calendar `(id, identity_hash, host)`, and compute `LinkIdentity.Hash(KeyFor(url))` for each link.
  2. For each calendar whose links all map to one hash equal to its `identity_hash`, change nothing.
  3. For each other target hash, choose the target calendar:
     - an existing calendar with that `identity_hash`;
     - else the current calendar of the lowest-id link with that hash, whose `identity_hash` is then updated (after freeing it first when it conflicts);
     - else a new `calendar (identity_hash, host, next_check_at = now + random(0, CheckInterval))` with `baseline_features NULL`.
  4. Move links with `UPDATE calendar_link SET calendar_id`. Where the move would violate `UNIQUE (calendar_id, subscriber, url)`, delete the moved duplicate instead.
  5. Delete calendars left without links.
  6. Log only counts: calendars kept, moved, created and deleted.

  Known ceiling: the undelivered changes of a calendar deleted by a merge are lost (accepted in R13). Mark this with a `// ponytail:` comment.
- [ ] T070 [US4] In `backend/InTheKnow/Program.cs`, at `// regroup (T070)`, after `MigrateAsync` and before `app.Run()`, call `await app.Services.GetRequiredService<Checker>().RegroupAsync(app.Services.GetRequiredService<TimeProvider>().GetUtcNow())`, using the DI clock so test fakes apply.

**Checkpoint**: All user stories pass their tests independently.

---

## Phase 8: Polish & Cross-Cutting Concerns

- [ ] T071 [P] Add to `backend/InTheKnow.Tests/CheckerTests.cs` a test with a capturing `ILoggerProvider`. It runs a success, an `http 500`, a `Hang` timeout, an `empty_feed` and a duplicate-UID feed, and asserts that no log line contains any seeded URL or its `h` value (FR-028, SC-011).
- [ ] T072 [P] Update `README.md` with a short "Run it" section: `docker compose up -d db`, the required `ConnectionStrings__Default` and `InTheKnow__ApiKey` environment variables, `dotnet test`, `dotnet run --project backend/InTheKnow`, and links to `specs/001-ical-change-detection/quickstart.md` and `contracts/api.md`.
- [ ] T073 Run `dotnet list package --include-transitive` for both projects. Confirm every package in `backend/InTheKnow/packages.lock.json` and `backend/InTheKnow.Tests/packages.lock.json` is GPL-3.0-compatible, and update the table in `specs/001-ical-change-detection/research.md` § R17 with the resolved versions.
- [ ] T074 Run the quality gate from the repo root and fix everything it reports:
  - `dotnet restore --locked-mode`;
  - `dotnet build` (warnings are errors);
  - `dotnet format --verify-no-changes`;
  - `dotnet test` against `compose.yaml`.

  The gate covers `InTheKnow.slnx`.
- [ ] T075 Walk through `specs/001-ical-change-detection/quickstart.md` §2, §3 and §4 by hand against the live source with a real link. §2 includes the 24-hour unchanged run (SC-002). Confirm the expected results, including no link in any response or in the app log. Note any deviation in `specs/001-ical-change-detection/quickstart.md`, and record the §4 numbers (compression, parse cost, throughput, API reads) in `specs/001-ical-change-detection/research.md` § R19 (depends on T076).
- [ ] T076 [P] Add the two explicit performance tests from quickstart.md §4. Both use `[Fact(Explicit = true)]`, so they never run in the normal gate, and both **log** their timings rather than assert them (plan § Performance Goals):
  - `ParseCost` in `backend/InTheKnow.Tests/MyTimetablesTests.cs`: for each `Fixtures/private/*.ics` (skip when none), run `FeedParser.Parse` + `MyTimetables.ToState` + `Differ.SnapshotHash` 20 times after one warm-up and log the average per feed.
  - `Throughput5000` in `backend/InTheKnow.Tests/ApiTests.cs`, using the T050 host with `mytimetables.inholland.nl` `MaxRequestsPerMinute=60000` (a 1 ms gap): seed 5,000 calendars on that host with `SeedCalendarAsync` (one subscriber per 5 calendars), all serving `mt-nl.ics`.
    - Run a baseline pass, then `Advance(CheckInterval)` so every calendar is due again, then an unchanged pass. Each pass loops `RunDueChecksAsync(now)` + `Advance(1 ms)` while `SELECT count(*) FROM calendar WHERE next_check_at <= @now` is above 0 (a single `RunDueChecksAsync` stops as soon as the host is busy). Log each pass's wall time.
    - Assert that the unchanged pass leaves every `event_snapshot` row's `xmin` unchanged (SC-010).
    - Insert 2,000 `detected_change` rows spread over the timed subscriber's calendars with one `INSERT … SELECT FROM generate_series`, so the changes read is not an empty page. Then time `GET /api/calendars?limit=500` and that subscriber's `GET …/changes?limit=500`, and log both.
    - The test-selection commands in quickstart §4 (`--filter-method`, `--explicit only`, and whether .NET 10's `dotnet test` needs the `--` separator) are unverified: confirm them with `dotnet test --help` and fix quickstart §4 if they differ.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies. T008 needs T005 and T006; T009 needs T001–T008.
- **Foundational (Phase 2)**: needs Setup, and blocks every story.
  - T011–T014 edit one file in sequence.
  - T015 needs T011–T014.
  - T016, T017 and T018 need T010.
  - T019 needs T015.
  - T024–T027 need their subject tasks.
- **US1 (Phase 3)**: needs Foundational.
- **US2 (Phase 4)**: needs Foundational and US1. Its SC-003 test (in T046) runs the US1 `Checker`, and add-link (T048) baselines a new calendar with the US1 `MyTimetables.ToState`, `Differ.SnapshotHash` and `Checker.CopySnapshotAsync`.
- **US5 (Phase 5)**: **needs US2**, because the API calls `Subscriptions.AddLinkAsync`, and US1 for the changes test.
- **US3 (Phase 6)**: needs US1 (dispatcher) and US2 (the validation-during-backlog test in T061).
- **US4 (Phase 7)**: **needs US2** (`LinkIdentity` and `AddLinkAsync` for regroup) and US1 (re-baseline in the check transaction).
- **Polish (Phase 8)**: needs all stories.

### User Story Dependencies

- US1 is independent after Foundational. It is the MVP.
- US2, US3 and US4 depend on US1 for scheduled checks.
- US5 and US4 depend on US2 for identity and add-link.
- These dependencies are real. The stories share one checker and one add-link path, and duplicating them per story would break the constitution's smallest-implementation rule.

### Within Each User Story

- Tests are written first and fail (a compile failure counts) before implementation.
- `FeedParser`/`MyTimetables` come before `Differ`, and `Differ` before `Checker`. `LinkIdentity` comes before `Subscriptions`, and `Subscriptions` before `Api`.
- Tasks on the same file (`Checker.cs`, `CheckerTests.cs`, `Api.cs`, `001_init.sql`, `Program.cs`) run in ID order.

### Parallel Opportunities

- Setup: T001–T007.
- Foundational: T016 and T017 (after T010), then T019–T023, and the test files T024–T027.
- US1: fixtures T028 and T029 with the unit tests T030–T032, then `FeedParser` (T037) and `MyTimetables` (T038).
- US2: T045, T046 and T047.
- US3: T059 and T060. US4: T065 and T066.
- Polish: T071, T072 and T076.

---

## Parallel Example: User Story 1

```bash
# Fixtures and unit tests together:
Task: "T028 Create Fixtures/mt-nl-edited.ics and mt-nl-volatile.ics"
Task: "T029 Create Fixtures/generic-edited.ics"
Task: "T030 Write FeedParserTests.cs"
Task: "T031 Write MyTimetablesTests.cs"
Task: "T032 Write DifferTests.cs"

# Then the two independent parsers:
Task: "T037 Implement FeedParser.cs"
Task: "T038 Implement MyTimetables.cs"
```

## Parallel Example: User Story 2

```bash
Task: "T045 Write LinkIdentityTests.cs"
Task: "T046 Write SubscriptionsTests.cs"
Task: "T047 Implement LinkIdentity.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Phase 1: Setup.
2. Phase 2: Foundational. This blocks everything else.
3. Phase 3: US1.
4. **Stop and validate**: `dotnet test`. Seeded calendars are checked and changes are recorded correctly.

### Incremental Delivery

1. Setup + Foundational → the foundation is ready.
2. US1 → change detection works (MVP).
3. US2 → deduplicated calendars, and links can be added in code.
4. US5 → the HTTP API, and quickstart §2 is usable by hand.
5. US3 → operator scheduling knobs are validated and proven.
6. US4 → configurable domains, regroup and re-baseline.
7. Polish → the quality gate, the log audit and the manual quickstart run.

---

## Notes

- [P] tasks touch different files and have no pending dependency.
- Links are secrets everywhere. Log ids and reason codes only, and never commit a real `h`/`eu` token or a real name.
- Commit after each task or logical group, and stop at any checkpoint to validate.
