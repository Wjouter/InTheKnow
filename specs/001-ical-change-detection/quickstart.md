# Quickstart: Timetable Change Detection

This guide checks that the feature works from start to finish. The interfaces are described in
[contracts/](./contracts/) and the tables in [data-model.md](./data-model.md).

## Prerequisites

- .NET SDK 10.0
- Docker or Podman with Compose, for PostgreSQL
- Optional: real MyTimetables feeds saved as `.ics` files in `backend/InTheKnow.Tests/Fixtures/private/`
  (gitignored). Tests that need them are skipped when the folder is empty.

## Setup

The commands below use bash or zsh syntax. In fish, use `set -x NAME value` instead of `export`, and
replace `read -rs LINK` with `read -s LINK`. The `$(…)` substitution also works in fish.

```bash
docker compose up -d db
```

```bash
export ConnectionStrings__Default="Host=localhost;Username=intheknow;Password=intheknow;Database=intheknow"
```

```bash
export InTheKnow__ApiKey="$(openssl rand -base64 48)"
```

```bash
export INTHEKNOW_TEST_DB="Host=localhost;Username=intheknow;Password=intheknow;Database=postgres"
```

## 1. Automated checks (the quality gate)

```bash
dotnet test
```

Expected: every test passes. This covers:

| Scenario | Covers |
|---|---|
| Two anonymised MyTimetables fixtures with known edits (room, time, teacher, name, draft marker, added, removed) record exactly those changes | US1-1…5, SC-001 |
| The same feed with only `DTSTAMP`/`LAST-MODIFIED`/sync lines changed records 0 changes | US1-6, SC-002 |
| The first check records a baseline only, and a second worker instance using the same database afterwards records nothing twice | US1-7, US1-8, SC-008 |
| NL and EN fixtures of the same timetable extract identical values, and a language switch records 0 changes | SC-006, the language edge case |
| Real feeds (private fixtures, when present) extract name, times, rooms, teachers, type and groups for every event | SC-006 |
| Duplicate links, a synthetic classmate link, a different group and a personal timetable lead to the expected number of calendars. A fake clock advances through several intervals with `RunDueChecksAsync`, and a counting fake HTTP handler sees 1 scheduled request per interval plus 1 per validation. | US2-1…7, US3-1, SC-003 |
| An empty feed after events gives no removals, flags the link and falls back to another link. With all links flagged the calendar becomes `failing`. When a link returns the **same** feed as before the failure (the hash no-op path), the link is un-flagged and the calendar returns to `ok` and the normal interval. | SC-004, FR-008a, US3-4 |
| A generic feed with RRULE, EXDATE, a RECURRENCE-ID override, a floating time and a UID-less event: an edit to one occurrence records one change, a horizon shift records nothing, and a DST-only representation change records nothing | SC-007, FR-015a |
| The same feed from an unlisted domain, a fully enabled domain and a partly enabled domain: the features that apply match the configuration, and switching them re-baselines with 0 changes | US4-1…5, SC-009 |
| The address classifier rejects loopback, private, link-local and CGNAT addresses (both IPv4 and IPv6-mapped forms), and the `file:` and plain `http:` schemes. A real `SocketsHttpHandler` refuses to connect to `localhost` through the connect-time callback, which also covers redirects. A body over 10 MB fails with `too_large`, and a network error with `network`. | FR-001, FR-014 |
| Adding a link that creates a calendar stores its baseline from the validation fetch, with 0 changes and the first check one interval later. A link that joins stores nothing. | FR-023, research R11 |
| An interval below the minimum is clamped with a warning, and the defaults apply when nothing is configured | US3-2, US3-3 |
| With the per-host budget set to 10/min and 50 calendars on one host all due at the same instant (as after a restart), `FakeTimeProvider` shows consecutive requests exactly 6 s apart, in longest-waiting-first order. None is skipped, all 50 are checked within 5 minutes, and their next checks are one interval after each actual check, so they stay 6 s apart. An add-link validation during the backlog waits at most one slot and succeeds. | FR-012a, FR-012b, SC-013 |
| A default budget of 10/min and an override of 60/min for one configured domain: requests to that domain are 1 s apart, and requests to an unlisted host are 6 s apart. Restarting with the override raised to 120/min changes the domain's spacing to 0.5 s, and no calendar is skipped or re-baselined. | FR-012a, US3-5 |
| With `MaxConcurrentChecks` = 3 and 10 due calendars whose fake fetches block, exactly 3 run at once, and the next one starts as soon as one finishes. A value of 0 or 65 fails at startup. | FR-014a, US3-6 |
| A feed whose server never responds, and a check stuck in non-cancellable work (a test hook that blocks): each is recorded as failed with `timeout` within 2 minutes of fake time, commits nothing, and releases its worker slot. Meanwhile every other due calendar is checked on schedule. | FR-014a, SC-012 |
| A process that stops mid-check: the calendar is checked again once its lease expires, and nothing is recorded twice | SC-008 |
| A stuck check that resumes after its timeout was recorded (the test hook releases it) commits no snapshot rows and no changes, and the `timeout` outcome stays. The same holds when it resumes after the calendar was claimed again. | FR-014a, lease fence |
| An unchanged feed checked twice writes no `event_snapshot` rows (row counts and `xmin` unchanged), and a one-room edit writes exactly one row | FR-019a, SC-010 |
| API tests (`WebApplicationFactory`): a missing or wrong key gives `401` on every route; add, list, remove and paged changes follow [contracts/api.md](./contracts/api.md); subscriber S never sees another subscriber's calendars or changes; no response body contains a link | US5-1…5, SC-011, FR-032 |

## 2. Manual check against the live source

1. Start the app, which runs the API and the checker in one process:

   ```bash
   dotnet run --project backend/InTheKnow
   ```

2. In a second shell, add a link. The link is read without echo and piped into the request body, so it never appears in a command line. Replace the
   port with the one the app printed at startup:

   ```bash
   read -rs LINK && printf '{"url":"%s"}' "$LINK" | curl -s -X POST localhost:5000/api/subscribers/me/links -H "Authorization: Bearer $InTheKnow__ApiKey" -H 'Content-Type: application/json' --data-binary @-
   ```

   Expected: `201` with `"calendarIsNew": true`, and no link in the response.

3. Add a second link for the same timetable. It must differ only in the leading hex parameter.

   Expected: `201` with the same `calendarId` and `"calendarIsNew": false` (US2-1).

4. Repeat step 2 without the `Authorization` header.

   Expected: `401` (SC-011).

5. Look at the calendars:

   ```bash
   curl -s localhost:5000/api/subscribers/me/calendars -H "Authorization: Bearer $InTheKnow__ApiKey"
   ```

   Expected: `lastOk: true` straight away, because the baseline came from the validation fetch. There should be no
   link in the output, and no link anywhere in the app's log (FR-032).

6. Leave the app running for 24 hours, then run:

   ```bash
   curl -s "localhost:5000/api/subscribers/me/changes" -H "Authorization: Bearer $InTheKnow__ApiKey"
   ```

   Expected: `items: []` unless the timetable really changed (SC-002).

7. Remove the subscription:

   ```bash
   curl -s -X DELETE localhost:5000/api/subscribers/me/calendars/<calendarId> -H "Authorization: Bearer $InTheKnow__ApiKey"
   ```

   Expected: `204`, and the calendar, its links, snapshots and changes are gone (FR-008).

## 3. Operator configuration check

Set `InTheKnow__CheckInterval=00:01:00` and start the app.

Expected: a warning that the interval was clamped to 5 minutes (US3-2). Starting without `InTheKnow__ApiKey`, or
with a key shorter than 32 characters, fails at startup.

## 4. Performance check

Run this once against the live source with a real link, and note the numbers in research R19. The plan's goals are
not asserted in tests, because wall-clock assertions are flaky. The `dotnet test` selection options below are
unverified until the test project exists; T076 confirms them.

1. **Compression**: check whether the source compresses its feed, without printing the link:

   ```bash
   read -rs LINK && printf 'url = "%s"\n' "$LINK" | curl -s -o /dev/null -D - -H 'Accept-Encoding: gzip, br' -K - | grep -i -E '^(content-encoding|content-length|expires):'
   ```

   The link goes to curl through a config on stdin, never on its command line (FR-028). Expected: the headers only.
   A `Content-Encoding` means the bandwidth figures in R19 are an upper bound.

2. **Parse cost**: with real feeds in `Fixtures/private/`, run the explicit parse benchmark. Explicit tests never
   run in the normal gate:

   ```bash
   dotnet test -- --filter-method "*ParseCost*" --explicit only
   ```

   Expected: it logs the average time of parse + extraction + hash per feed, well under the 1 s goal.

3. **Throughput and API reads**: run the explicit 5,000-calendar test. It seeds 5,000 calendars on one configured
   domain with a budget high enough not to limit, serves `mt-nl.ics` from fake HTTP, and runs a baseline pass and an
   unchanged pass, one calendar per fake-clock step (so the numbers are a sequential upper bound). It then seeds
   2,000 changes and times `GET /api/calendars?limit=500` and one subscriber's `GET …/changes?limit=500` through the
   in-memory host:

   ```bash
   dotnet test -- --filter-method "*Throughput5000*" --explicit only
   ```

   Expected: the unchanged pass writes 0 `event_snapshot` rows and its total wall time is well under the 10-minute
   interval (SC-010); both API reads take under 0.2 s.
