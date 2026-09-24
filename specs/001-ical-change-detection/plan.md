# Implementation Plan: Timetable Change Detection

**Branch**: `001-ical-change-detection` | **Date**: 2026-09-24 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-ical-change-detection/spec.md`

## Summary

One ASP.NET Core (.NET 10) process serves an HTTP API and runs a background checker. The API is for the later
SvelteKit frontend. The checker periodically fetches iCalendar feeds, normalises every event, and records every
difference from the state stored in PostgreSQL. Differences are field changes, additions and removals.

- **Identity**: links are grouped into calendars by an identity key. The key is the normalised URL for generic
  feeds. For MyTimetables it is either the URL without the generation marker, or the host plus the student-group
  timetable. Each calendar is fetched once per interval, however many links point to it.
- **Parsing and expansion**: Ical.Net parses the feeds and expands recurrences up to a configurable horizon.
- **MyTimetables extraction**: for operator-listed domains, the description is parsed into type, rooms,
  teachers and groups. Dutch and English labels and values map to language-independent values.
- **Empty feeds**: an empty feed after events counts as a failed check, not as a mass cancellation.
- **Checking**: up to `MaxConcurrentChecks` calendars (default 16) are checked at once. Each check has a
  2-minute limit, so one stuck check never holds up the others.
- **Whole-school scale**:
  - Each source host has a request budget, with a global default that can be overridden per domain. Requests
    are spaced evenly, and a check that doesn't fit waits its turn rather than being skipped.
  - A new calendar's baseline comes from the validation fetch of the link that creates it (research R11).
  - Unchanged feeds are detected by hash and cost three small row updates (claim, calendar outcome, fetch link)
    and no event rows.
  - Changed feeds write only the affected rows, in one transaction.
  - The per-host budget, not the interval, sets the pace at scale: one check cycle is
    `max(CheckInterval, calendars on the host ÷ budget)` (SC-005, research R19).
- **API**: add a link, list a subscriber's calendars, remove a subscription, page through recorded changes, and an
  operator overview. Every call requires an operator key until the login feature ships.

## Technical Context

**Language/Version**: C# 14 on .NET 10 (LTS)

**Primary Dependencies**:
- The ASP.NET Core shared framework (`Microsoft.NET.Sdk.Web`), which includes hosting, config, logging and
  Problem Details
- Ical.Net 5.2.3 (and NodaTime 3.2.2 transitively)
- Npgsql 10
- Test only:
  - xunit.v3 on Microsoft.Testing.Platform, opted in through `global.json`, without Microsoft.NET.Test.Sdk
    (verified on the .NET 10 SDK)
  - Microsoft.AspNetCore.Mvc.Testing
  - Microsoft.Extensions.TimeProvider.Testing (`FakeTimeProvider`, which fires timers on fake time; verified)

**Storage**: PostgreSQL 18 (via `compose.yaml` for development and tests). Raw SQL migrations are embedded and
applied at startup (research R3). The reasons PostgreSQL fits a whole school are in
[research R19](./research.md#r19-scale-a-whole-school).

**Testing**:
- xunit.v3. Pure logic is unit-tested against in-memory and fixture feeds.
- The checker takes time from `TimeProvider` and exposes `RunDueChecksAsync(now)`, so scheduling, the per-host
  budget and the check time limit are tested with `FakeTimeProvider` (research R4).
- API tests use `WebApplicationFactory`.
- Database tests run against the compose PostgreSQL (`INTHEKNOW_TEST_DB`).
- HTTP to feeds is faked with an `HttpMessageHandler`.

**Target Platform**: Linux server. Exactly one process, behind a TLS-terminating reverse proxy on the same host. The
advisory lock refuses a second instance (research R1, R4).

**Project Type**: The backend of a web application: an HTTP API plus a background checker. The SvelteKit frontend
is a later feature.

**Performance Goals**:
- An unchanged check (fetch, parse, hash, three row updates) completes in < 1 s, excluding network time.
- 5,000 calendars fit within a 10-minute interval, as far as the per-host budget allows (SC-010).
- API reads complete in < 200 ms p95 at school scale.
- These goals are measured once by hand ([quickstart §4](./quickstart.md#4-performance-check), T075), not asserted in
  tests, because wall-clock assertions are flaky.

**Constraints**:
- Each source host gets at most its budget: the domain's `MaxRequestsPerMinute`, otherwise
  `MaxRequestsPerHostPerMinute` (default 120).
- At most one scheduled fetch per calendar per interval, with the interval ≥ 5 minutes.
- Fetches are bounded to 30 s and 10 MB.
- At most 50,000 occurrences per calendar.
- Links never appear in logs or API responses.
- Malformed feed events are normalised, so one bad event cannot block a calendar (research R8a).
- Exactly one instance runs (enforced with an advisory lock).
- Up to `MaxConcurrentChecks` checks (default 16, at most 64) run concurrently. Each whole check has a 2-minute
  limit, so a stuck check never blocks the others (FR-014a).
- Requests to a host are spaced evenly (one every `1 min / budget`). A budget-blocked check keeps its place in
  line and is never skipped (FR-012b).

**Scale/Scope**: A whole school: tens of thousands of subscribers, thousands of calendars and about 100–3,000
events per calendar, on one deployment with one PostgreSQL instance.

No NEEDS CLARIFICATION items remain. All unknowns are resolved in [research.md](./research.md).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Rule | Status | How |
|---|---|---|
| I. Smallest implementation, no speculative code | PASS | One app project and one test project. No ORM, no scheduler library, no config reload, no OpenAPI or CORS until a consumer needs them. Constants stay constants (R10). |
| I. Preference order (stdlib → installed → new) | PASS | The Web SDK's shared framework covers hosting, API, config and Problem Details, which **removes** a package. Scheduling, the host budget, SSRF guard, JSON and hashing use the BCL. Each new dependency is justified below. |
| I. Deviations justified | PASS | See Complexity Tracking |
| II. Store only required data, and name each personal field | PASS | [data-model.md § Personal and sensitive data](./data-model.md#personal-and-sensitive-data-principle-ii) |
| II. No committed secrets | PASS | The connection string and API key come from the environment. Links arrive only in request bodies and are stored only in the database. Real feed fixtures are gitignored, and the committed ones are anonymised. |
| II. Validate input crossing trust boundaries | PASS | API: operator key on every route, subscriber id pattern, body shape, and `limit` range. Links: scheme check and SSRF check at add time and at connect time. Feeds: size, time and occurrence bounds, and parse errors fail the check. |
| II. Escape browser output | N/A | The API returns JSON only. The frontend feature escapes what it renders. |
| II. Established auth libraries, no hand-rolled crypto | PASS | An operator key compared with BCL `SHA256` + `CryptographicOperations.FixedTimeEquals` (R15). User login is the next feature. |
| II. HTTPS outside local development | PASS | The API is served only behind a TLS-terminating proxy on the same host outside development ([contracts/api.md § Transport](./contracts/api.md#transport)). Feed links must be `https` (FR-001, R18). |
| Tech: .NET backend, PostgreSQL, constraints in the database | PASS | All integrity rules are SQL constraints ([data-model.md](./data-model.md)) |
| Tech: GPL-3.0-compatible licences | PASS | [research R17](./research.md#r17-dependency-licences-gpl-30-compatibility) |
| Tech: lockfile committed | PASS (planned) | `Directory.Build.props` sets `RestorePackagesWithLockFile=true`, and `packages.lock.json` is committed for both projects. Restore uses `--locked-mode`. |
| Workflow: checks as quality gate; tests for non-trivial logic | PASS (planned) | `dotnet build` with warnings as errors, and `dotnet test`. The parser, extraction, identity, URL guard, differ, horizon rule, host budget spacing, dispatcher (stuck check, bunch spreading, lease recovery), check transaction and each API route get a failing-if-broken test ([quickstart §1](./quickstart.md)). |

**Post-design re-check (after Phase 1)**: PASS. The API replaced the CLI without adding a project or a layer. The
dependency set changed from Hosting to Mvc.Testing and TimeProvider.Testing (both test only). There is no
constitution deviation: outbound `http` feeds were dropped from FR-001 (analysis, 2026-09-24).

## Project Structure

### Documentation (this feature)

```text
specs/001-ical-change-detection/
├── spec.md
├── investigation.md
├── plan.md              # This file
├── research.md          # Phase 0
├── data-model.md        # Phase 1
├── quickstart.md        # Phase 1
├── contracts/           # Phase 1
│   ├── api.md
│   ├── configuration.md
│   └── detected-change.md
├── checklists/
└── tasks.md             # Phase 2 (/speckit-tasks)
```

### Source Code (repository root)

```text
InTheKnow.slnx
global.json                     # {"test":{"runner":"Microsoft.Testing.Platform"}}
Directory.Build.props           # net10.0, nullable, warnings as errors, RestorePackagesWithLockFile
compose.yaml                    # postgres:18 for development and tests
backend/
├── InTheKnow/
│   ├── InTheKnow.csproj        # Microsoft.NET.Sdk.Web
│   ├── packages.lock.json
│   ├── appsettings.json        # defaults from contracts/configuration.md
│   ├── Program.cs              # builder, settings validation, migrations, checker registration, MapApi()
│   ├── Api.cs                  # /api route group, operator-key filter, endpoints from contracts/api.md
│   ├── Settings.cs             # options records, clamping and validation (intervals, budgets, concurrency 1–64)
│   ├── Db.cs                   # data source, migration runner, advisory lock
│   ├── Migrations/001_init.sql
│   ├── Fetcher.cs              # shared HttpClient, SSRF ConnectCallback, bounds, failure reasons
│   ├── HostBudget.cs           # per-host even request spacing (FR-012a, FR-012b)
│   ├── LinkIdentity.cs         # URL normalisation, MyTimetables link decoding, identity key
│   ├── FeedParser.cs           # Ical.Net → normalised occurrences (UIDs, horizon, times, R8a)
│   ├── MyTimetables.cs         # description blocks, NL/EN maps, teachers, draft marker
│   ├── Differ.cs               # snapshot vs new → changes (sets, horizon rule), snapshot hash
│   ├── Checker.cs              # dispatcher (claim + lease, worker slots, 2-min limit), check transaction, retention, regroup
│   └── Subscriptions.cs        # add link (validate, identity, attach, baseline a new calendar) and remove subscription, used by Api.cs
└── InTheKnow.Tests/
    ├── InTheKnow.Tests.csproj
    ├── packages.lock.json
    ├── Fixtures/               # anonymised MyTimetables NL/EN, empty feed, synthetic generic feed
    │   └── private/            # gitignored real feeds
    ├── FakeHttp.cs             # HttpMessageHandler that serves fixtures and counts requests
    ├── TestDb.cs               # per-test database from a migrated template, seed helper, Settings helper
    ├── DbTests.cs              # migrations, advisory lock, constraints live
    ├── SettingsTests.cs        # defaults, clamping, startup failures, domain lookup
    ├── SubscriptionsTests.cs   # add link (identity, validation, baseline), SC-003 request count
    ├── LinkIdentityTests.cs
    ├── FetcherTests.cs         # address classifier and the connect callback refusing localhost
    ├── FeedParserTests.cs
    ├── MyTimetablesTests.cs
    ├── DifferTests.cs
    ├── HostBudgetTests.cs      # spacing, per-domain override, validation queueing ahead of checks
    ├── CheckerTests.cs         # database: baseline, hash no-op, partial writes, failing/flagging, regroup, deletion,
    │                           #   stuck check vs others, restart bunch spreading, lease recovery
    └── ApiTests.cs             # WebApplicationFactory: key, routes, subscriber scoping, no links in responses
```

**Structure Decision**:
- The web-application layout uses `backend/` now and adds `frontend/` (SvelteKit) with the UI feature.
- There is one backend project; the API and the checker are files in it, not layers or assemblies.
- There is one test project, with the database and API tests in the same project. They run against the compose
  database.
- `.gitignore` gains `backend/InTheKnow.Tests/Fixtures/private/`.

## Complexity Tracking

| Addition / deviation | Why needed | Simpler alternative rejected because |
|---|---|---|
| Ical.Net (+ NodaTime) | Parsing, time-zone resolution and RRULE/EXDATE/RECURRENCE-ID expansion for generic feeds (FR-015, FR-015a, SC-007) | A hand-written parser works for MyTimetables but not for recurrence and time-zone rules. Getting it right would be far more code and risk than one mature MIT library. |
| Npgsql | The PostgreSQL driver the constitution requires | No alternative: the BCL has no PostgreSQL driver. An ORM or Dapper was rejected in favour of the bare driver (R3). |
| xunit.v3 (test only) | The constitution requires automated checks. `dotnet test` needs a test framework and runner. | The BCL has no test runner. An assert-only console app would give no discovery, isolation or reporting. |
| Microsoft.Extensions.TimeProvider.Testing (test only) | The time-limit, lease and spacing tests (SC-012, SC-013) need `TimeProvider` timers (`WaitAsync`, timed `CancellationTokenSource`, `Task.Delay`) to fire on fake time | A subclass overriding only `GetUtcNow()` never fires timers. A correct timer-aware fake means re-writing this package's timer queue. |
| Microsoft.AspNetCore.Mvc.Testing (test only) | Runs the real app in memory for the API tests (US5, SC-011) | Starting the app on a real port per test run is slower and flakier, and it re-creates what the platform's test host already does. |
| Hand-written `HostBudget` (about 20 lines, even spacing per host) instead of `System.Threading.RateLimiting` | FR-012a and FR-012b must be testable with a fake clock. Blocked checks must keep their place in the database queue rather than wait in an in-memory queue. | The built-in limiter has no `TimeProvider` hook (verified), so tests would need real waiting. Its in-memory queue would hold claimed calendars instead of leaving them in line. |
