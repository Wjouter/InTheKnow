---

description: "Task list for Subscriber Change Filters (002)"
---

# Tasks: Subscriber Change Filters

**Input**: Design documents from `/specs/002-subscriber-change-filters/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: Included. The constitution requires an automated check for non-trivial logic, and the plan lists a failing-if-broken test per component (plan § Constitution Check, quickstart § 1). Write each story's tests first; they should fail (a compile failure counts) until that story's implementation tasks are done.

**Organization**: Grouped by user story. Phases follow priority order: US1 (P1), US2 (P1), US3 (P2), US4 (P2), US5 (P2), US6 (P3).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different file, no dependency on an unfinished task)
- **[Story]**: the user story the task belongs to (US1 to US6)

## Path Conventions

- App: `backend/InTheKnow/`. Tests: `backend/InTheKnow.Tests/` (plan § Project Structure). Paths below are relative to `backend/`.
- Migrations are embedded by the `Migrations/*.sql` glob in `InTheKnow/InTheKnow.csproj`, so no csproj change is needed.
- Test databases come from `TestDb` (`InTheKnow.Tests/TestDb.cs`); API tests use `WebApplicationFactory` as in the existing `ApiTests.cs`. Read both before writing tests.

## Shared signatures (so tasks written separately compile together)

```csharp
// FilterRules.cs
sealed record Rule(string Outcome /* "showOnly"|"hide"|"lowPriority" */, JsonObject When);
static class FilterRules {
    // Parse + validate a JSON array of rules (contracts/rules.md). Error is the `reason`; Rules is null on error.
    static (IReadOnlyList<Rule>? Rules, string? Error) Parse(JsonNode? json);
    static JsonArray ToJson(IReadOnlyList<Rule> rules);
}
sealed record ActiveConditions(bool ActivityType, bool Group, bool Room, bool Teacher /* also gates changeKind values */) {
    static ActiveConditions For(Features? f);                    // research R5: null/generic => all false
    bool IsActive(Rule r);                                       // false when any condition is inactive (changeKind: only if every value is)
    string[] Keys { get; }                                       // the activeConditions list of api.md
}
// what FilterEngine reads from one detected_change row
sealed record ChangeView(long Id, long CalendarId, string EventKey, string Kind, string? Field, JsonNode? OldValue, JsonNode? NewValue,
                         string EventName, DateTimeOffset EventStartsAt, DateTimeOffset DetectedAt, JsonNode? EventBefore, JsonNode? EventAfter);
enum Outcome { Normal, LowPriority, Hidden }
static class FilterEngine { static Outcome Outcome(IReadOnlyList<Rule> rules, ChangeView change, ActiveConditions active); }
// Presets.cs
sealed record PresetRow(long Id, string? Owner, string Name, IReadOnlyList<Rule> Rules, bool IsDefault);
sealed record EffectiveFilter(string SourceType /* "preset"|"custom" */, PresetRow? Preset, IReadOnlyList<Rule> Rules);
sealed class Presets(NpgsqlDataSource db, Settings settings) { /* methods named in the tasks below */ }
```

`FilterEngine.Outcome` is the single evaluation function (FR-009). Nothing else may re-implement rule matching.

---

## Phase 1: Setup

**Purpose**: the schema, so every later task can run against the test database.

- [ ] T001 Create `InTheKnow/Migrations/002_filters.sql` per data-model.md § Migration. `preset`: `id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY`; `owner text NULL CHECK (owner IS NULL OR owner ~ '^[A-Za-z0-9._@-]{1,100}$')`; `name text NOT NULL CHECK (length(name) BETWEEN 1 AND 100)`; `rules jsonb NOT NULL CHECK (jsonb_typeof(rules) = 'array' AND jsonb_array_length(rules) <= 50)`; `is_default boolean NOT NULL DEFAULT false CHECK (NOT is_default OR owner IS NULL)`; `created_at timestamptz NOT NULL DEFAULT now()`; `UNIQUE NULLS NOT DISTINCT (owner, name)`; unique partial index `ON preset ((true)) WHERE is_default`; index `(owner)`. `subscription_filter`: `calendar_id bigint NOT NULL REFERENCES calendar(id) ON DELETE CASCADE`; `subscriber text NOT NULL` with the same pattern CHECK; `preset_id bigint NULL REFERENCES preset(id) ON DELETE RESTRICT`; `rules jsonb NULL CHECK (rules IS NULL OR (jsonb_typeof(rules) = 'array' AND jsonb_array_length(rules) <= 50))`; `PRIMARY KEY (calendar_id, subscriber)`; `CHECK ((preset_id IS NULL) <> (rules IS NULL))`; index `(preset_id)`. Seed the Default preset (`owner` NULL, `name` 'Default', `is_default` true) with the four rules from contracts/rules.md § Default preset. Then `ALTER TABLE detected_change ADD COLUMN event_before jsonb NULL, ADD COLUMN event_after jsonb NULL` and `ADD CHECK (kind = 'changed' OR (event_before IS NULL AND event_after IS NULL))`. Then create the three triggers of data-model.md § Cross-table rules.
- [ ] T002 [P] Extend `InTheKnow.Tests/DbTests.cs`: after migrating, assert the Default preset exists exactly once, a second `is_default` row is rejected, a `subscription_filter` row with both or neither of `preset_id`/`rules` is rejected, two global presets with the same name are rejected (`NULLS NOT DISTINCT`), and `event_before` on an `added` change is rejected. For the triggers: a filter row pointing at another subscriber's personal preset fails at commit; a filter row with no link fails at commit; deleting a subscriber's last link to a calendar removes their filter row, while deleting one of two links keeps it; updating `preset.owner` is rejected.

---

## Phase 2: Foundational (blocking prerequisites)

**Purpose**: the pieces every story uses: what a change records, the rule model, and the evaluation function.

**⚠️ CRITICAL**: no user story work can begin until this phase is complete.

### Tests first

- [ ] T003 [P] Create `InTheKnow.Tests/FilterRulesTests.cs` covering every reason and limit in contracts/rules.md § Validation (SC-008): not an array / more than 50 rules / non-object rule / unknown key at either level → `invalid_rules`; missing or unknown `outcome` → `invalid_outcome`; missing or empty `when` → `empty_conditions`; wrong type, unknown `changeKind` or status value, `eventEnded` not `true`, empty array, more than 50 values, text or value outside 1–100 characters → `invalid_condition`. Also: a valid rule with MyTimetables conditions is accepted (FR-020, FR-024), and the Default rules parse.
- [ ] T004 [P] Create `InTheKnow.Tests/FilterEngineTests.cs` covering: outcome order (show-only miss → hidden, then hide, then lowPriority, then normal); two show-only rules are OR; inactive show-only rules do not count for the allow-list and inactive rules are ignored (SC-006); `changeKind` mapping of R5 (`location` = `location`/`rooms`, `description` = `description`/`details.*`); before-or-after matching for `nameContains`/`activityType`/`group`/`room`/`teacher` including `added` (after only) and `removed` (before only); `eventEnded` at detection time incl. a time change moving a past event into the future (not matched); `statusFrom`/`statusTo` with a missing status as `none`. Default-preset cases of SC-001 are added in US1 (T012).
- [ ] T005 [P] Extend `InTheKnow.Tests/DifferTests.cs`: `changed` rows carry `EventBefore`/`EventAfter` as `EventObject(old)`/`EventObject(now)` with `fields.details` removed; `added` and `removed` rows carry neither.
- [ ] T006 [P] Extend `InTheKnow.Tests/CheckerTests.cs` (following its existing detection tests): after a check that produces a field change, the `detected_change` row has non-null `event_before`/`event_after`, and none for an added event.

### Implementation

- [ ] T007 In `InTheKnow/Differ.cs`, add `JsonNode? EventBefore, JsonNode? EventAfter` to the end of the `ChangeRow` record (null default). In the `Changed(...)` local function set them to `EventObject(old)` / `EventObject(now)` each with `["fields"]["details"]` removed (clone first; `fields.details` is never read by a condition). `added`/`removed` rows leave them null. Update `ChangeRow` constructions in `Differ.cs` and any in `InTheKnow.Tests` so the solution compiles.
- [ ] T008 In `InTheKnow/Checker.cs`, extend the `COPY detected_change (...)` at ~line 297 with `event_before, event_after` (jsonb, via `WriteNullableAsync(..., NpgsqlDbType.Jsonb, ...)` like `old_value`).
- [ ] T009 [P] Create `InTheKnow/FilterRules.cs` with `Rule`, `FilterRules.Parse` and `ToJson` per contracts/rules.md. The rule object is closed. Reasons: `invalid_rules`, `invalid_outcome`, `empty_conditions`, `invalid_condition`. Limits: at most 50 rules, at most 50 values per array condition, texts and values 1–100 characters. Allowed `changeKind` values: `added, removed, name, time, status, location, description, activityType, groups, teachers`. Allowed `statusFrom`/`statusTo`: `confirmed, tentative, cancelled, none`. `eventEnded` must be the JSON literal `true`. MyTimetables conditions are valid on any calendar (FR-020). Makes T003 pass.
- [ ] T010 Same file: add `ActiveConditions.For(Features? f)` per research R5: all false when `f` is null or `EventDetails` is false; `ActivityType`/`Group`/`Room` true with event details; `Teacher` additionally needs `TeacherDetection`. Add `IsActive(Rule)` (a rule is inactive when any of its conditions is inactive; a `changeKind` condition is inactive only when every value is inactive, where `activityType`/`groups` need event details and `teachers` needs `Teacher`) and `Keys` (`changeKind`, `eventEnded`, `status`, `nameContains` always; `activityType`, `group`, `room`, `teacher` when active — the values shown in contracts/api.md).
- [ ] T011 Create `InTheKnow/FilterEngine.cs`: `ChangeView`, `Outcome` enum and `FilterEngine.Outcome(rules, change, active)` per contracts/rules.md § Outcome and research R5/R6. Only active rules are used; the event before/after states come from `EventBefore`/`EventAfter`, else `OldValue`/`NewValue` for `removed`/`added`, else missing (no match). `eventEnded` = the `end` of every existing state is earlier than `DetectedAt`, and at least one state exists. Pure, no I/O. Makes T004 pass.

**Checkpoint**: `dotnet build && dotnet test` passes; changes are recorded with before/after events; the engine is usable.

---

## Phase 3: User Story 1 - Only see the changes that matter, out of the box (Priority: P1) 🎯 MVP

**Goal**: with no action, a subscriber's change list hides noise and marks low priority, per the Default preset. Hidden changes remain retrievable.

**Independent Test**: record changes covering each Default rule plus ordinary ones; `GET /api/subscribers/{s}/changes` returns exactly the expected outcomes; `includeHidden=true` returns everything.

### Tests for User Story 1

- [ ] T012 [P] [US1] In `InTheKnow.Tests/FilterEngineTests.cs` add the Default-preset cases (SC-001): ended event hidden; future room change normal; holiday room-list change hidden; activity-type and groups change hidden; tentative → confirmed lowPriority; confirmed → tentative and → cancelled normal; a generic calendar (`ActiveConditions.For(null)`) only applies the `eventEnded` and status rules.
- [ ] T013 [P] [US1] In `InTheKnow.Tests/ApiTests.cs` add: a subscriber with no filter row gets Default outcomes; `outcome` is on every item and never `hidden` without `includeHidden`; `includeHidden=true` returns 100% of recorded changes with hidden ones marked (SC-004); paging: a subscriber with many hidden changes before one visible change gets that change on the first page, and `next` is the id of the last returned item only when more visible changes remain (empty-page rule); `includeHidden=yes` returns 400 with reason `invalid_include_hidden`.

### Implementation for User Story 1

- [ ] T014 [US1] Create `InTheKnow/Presets.cs` with class `Presets(NpgsqlDataSource db, Settings settings)` and `LoadFiltersAsync(string subscriber, CancellationToken ct)` returning `Dictionary<long, (EffectiveFilter Filter, ActiveConditions Active)>` keyed by calendar id, using one query joining `calendar_link`, `calendar`, `subscription_filter` and `preset` (data-model § Outcome evaluation inputs): rules = `subscription_filter.rules`, else its preset's rules, else the Default preset's rules (no row = Default, research R1). Active conditions from `ActiveConditions.For(settings.FeaturesFor(calendar host))` — check `Settings.cs` and the `calendar` table for how the host is stored. Rule JSON parsed with `FilterRules.Parse`.
- [ ] T015 [US1] In `InTheKnow/Program.cs`, register `builder.Services.AddSingleton<Presets>();` next to `Subscriptions`.
- [ ] T016 [US1] In `InTheKnow/Api.cs` replace the body of the changes route (research R3): validate `subscriber`, `limit`, and a new `string? includeHidden` parameter (missing or `"false"` → false, `"true"` → true, anything else → `Problem(400, "invalid_include_hidden")`; a `bool?` parameter would fail in model binding with a 400 that has no `reason`); call `presets.LoadFiltersAsync` once; then scan `detected_change` in keyset batches of 500 (`WHERE calendar_id = ANY($1) AND id > $2 ORDER BY id LIMIT 500`, also selecting `event_before::text`, `event_after::text`) building `ChangeView`s, evaluating `FilterEngine.Outcome`, and keeping non-hidden (or all when `includeHidden`) items until `take + 1` are collected or rows run out. No scan cap. Add `outcome` (`"normal"|"lowPriority"|"hidden"`) to each item; keep the existing fields and `Page(...)` behaviour. Put a `ponytail:` comment naming the ceiling (a full keyset scan per page; upgrade to an indexed pre-filter or materialised outcome only if SC-009 measurement fails).

**Checkpoint**: User Story 1 works alone: Default filtering on the existing changes route.

---

## Phase 4: User Story 2 - Adjust the filter of one calendar (Priority: P1)

**Goal**: a subscriber views and replaces the filter of one subscription; other subscribers are unaffected; edits apply to old changes.

**Independent Test**: two subscribers share a calendar; one PUTs a filter; their list changes, the other's does not; invalid filters are rejected leaving the old one.

### Tests for User Story 2

- [ ] T017 [P] [US2] In `InTheKnow.Tests/PresetsTests.cs` (new; database tests via `TestDb`) add: `ReplaceFilterAsync` from "no row" creates a customised row holding the submitted rules (not Default's); replacing again overwrites; a failed validation leaves the previous row unchanged; `GetFilterAsync` for a subscription without a row reports `source.type = "preset"` with the Default preset.
- [ ] T018 [P] [US2] In `InTheKnow.Tests/ApiTests.cs` add: `GET .../calendars/{id}/filter` shape per contracts/api.md; `PUT` with a `hide` rule on `nameContains: "Zelfstudie"` hides those changes for A and not for B (SC-002), applies to changes recorded before the edit (SC-003); `PUT` with `[]` makes every change normal; each invalid case of rules.md returns 400 with its `reason` and the old filter stays; an unknown calendar or one the subscriber has no link to returns 404; invalid subscriber id → 400 `invalid_subscriber`.
- [ ] T019 [P] [US2] In `InTheKnow.Tests/SubscriptionsTests.cs` add: `RemoveAsync` deletes the subscriber's `subscription_filter` row, other subscribers' rows stay, and re-subscribing starts on Default.
- [ ] T020 [P] [US2] In `InTheKnow.Tests/CheckerTests.cs` (following its existing regroup tests) add: a subscriber with a customised filter and one with a preset-linked filter on a calendar whose link regroups to another calendar keep those filters on the new calendar. If the target already has a filter row for that subscriber, it is kept. A filter row whose subscriber has no link left on a calendar is removed. The other subscribers' filters on the old calendar are untouched.

### Implementation for User Story 2

- [ ] T021 [US2] In `InTheKnow/Presets.cs` add `GetFilterAsync(subscriber, calendarId, ct)` (null when the subscriber has no link to the calendar → 404) returning the effective filter, `inactiveRules` (indexes where `!active.IsActive(rule)`), `activeConditions` (`ActiveConditions.Keys`) and `values` as an empty object for now (filled in T026). Add `ReplaceFilterAsync(subscriber, calendarId, IReadOnlyList<Rule> rules, ct)`: upsert `subscription_filter` with `preset_id = NULL, rules = <json>` (`INSERT ... ON CONFLICT (calendar_id, subscriber) DO UPDATE`), after checking the subscription exists.
- [ ] T022 [US2] Create `InTheKnow/FilterApi.cs` with `MapFilterApi(this WebApplication app)` using `app.MapGroup("/api")` (the operator-key middleware in `Api.cs` already covers `/api`). Add `GET` and `PUT /subscribers/{subscriber}/calendars/{calendarId:long}/filter` per contracts/api.md: validate `subscriber` with `Subscriptions.SubscriberPattern()` (`400 invalid_subscriber`), parse `{ "rules": [...] }` with `FilterRules.Parse` (`400` with its reason), `404` when there is no subscription, `200` with the GET body. Reuse a shared problem helper: make `Api.Problem` `internal` rather than copying it. Call `app.MapFilterApi();` in `InTheKnow/Program.cs` after `app.MapApi();`.
- [ ] T023 [US2] In `InTheKnow/Checker.cs`, in the regroup loop that moves links (~line 390), keep the subscription filter with the subscriber. When a link moves from calendar A to calendar B (or is dropped as a duplicate of one already on B), run in the same transaction, **before** the `UPDATE`/`DELETE` of the link: `INSERT INTO subscription_filter (calendar_id, subscriber, preset_id, rules) SELECT $B, subscriber, preset_id, rules FROM subscription_filter WHERE calendar_id = $A AND subscriber = $S ON CONFLICT (calendar_id, subscriber) DO NOTHING`. B's existing row wins. It is a copy, not a move, because the subscriber may still have other links on A. The copy must come first, because moving the last link off A fires `calendar_link_drop_filter` and removes A's row. The ownership and link checks are deferred to commit, so the copy can be written before the link reaches B. No separate cleanup statement is needed. Makes T020 pass.

**Checkpoint**: US1 and US2 work; a subscriber can view and replace filters.

---

## Phase 5: User Story 3 - Show only one class from a timetable (Priority: P2)

**Goal**: show-only rules narrow a timetable to one group or course, with values picked from what the calendar contains.

**Independent Test**: a calendar with events for two groups; a show-only group rule hides the other group's changes and keeps the Default rules working; the group list is offered by the filter endpoint.

### Tests for User Story 3

- [ ] T024 [P] [US3] In `InTheKnow.Tests/FilterEngineTests.cs` add the US3 scenarios: show-only group ES1 hides ES2-only changes; an ended ES1 event stays hidden by the Default rule; removing ES1 from a group list passes the allow-list (matches before) and, without the Default groups rule, is shown; two show-only rules match either; a show-only rule that never matches hides everything on that calendar; a show-only group rule on a generic calendar is inactive so nothing is emptied.
- [ ] T025 [P] [US3] In `InTheKnow.Tests/PresetsTests.cs` add: `GetFilterAsync` returns the distinct `activityType`, `group`, `room`, `teacher` values of the calendar's `event_snapshot` rows only for active conditions, and omits inactive ones (FR-013, SC-006); a generic calendar returns no `values` entries.

### Implementation for User Story 3

- [ ] T026 [US3] In `InTheKnow/Presets.cs` fill `values` in `GetFilterAsync` (research R8): for each active condition query `event_snapshot` for that calendar — `SELECT DISTINCT jsonb_array_elements_text(fields->'groups')` (same for `rooms`, `teachers`) and `SELECT DISTINCT fields->>'type'` — ordered, keyed `activityType`, `group`, `room`, `teacher`. Inactive ones are omitted.

**Checkpoint**: allow-list workflows work on MyTimetables calendars.

---

## Phase 6: User Story 4 - Filter on MyTimetables details (Priority: P2)

**Goal**: MyTimetables conditions are active per calendar from the domain settings and reported as inactive elsewhere, without editing stored filters.

**Independent Test**: one subscriber, one MyTimetables calendar (event details on) and one generic calendar, same rules: MyTimetables conditions work on the first and are reported inactive with no effect on the second.

### Tests for User Story 4

- [ ] T027 [P] [US4] In `InTheKnow.Tests/FilterEngineTests.cs` add `ActiveConditions.For` cases: MyTimetables + event details → activityType/group/room active, teacher inactive; + teacher detection → teacher active; event details off or generic (`null`) → none; a `changeKind` mixing `location` and `groups` stays active on a generic calendar, while `["groups"]` alone is inactive. Add engine cases for spec US4 scenario 4 (hide location changes of teacher X's lessons only) and for a rule with `activityType` not matching events without MyTimetables data.
- [ ] T028 [P] [US4] In `InTheKnow.Tests/ApiTests.cs` add: `GET filter` on a MyTimetables calendar lists `activityType`, `group`, `room` in `activeConditions` (and `teacher` only with teacher detection) and on a generic calendar lists only the generic keys; the same custom rules (a `showOnly` rule on `activityType: ["test"]`) put on both calendars give `inactiveRules: []` on the first and `[0]` on the second, and on the second the change list is filtered by the other rules only (SC-006); toggling the domain's `EventDetails` in the test's settings makes the rules inactive without a PUT (US4 scenario 5).

No new implementation task: US4 behaviour comes from `ActiveConditions.For` (T010) and `Presets.LoadFiltersAsync` / `GetFilterAsync` (T014, T021). If T028 shows a gap (for example host normalisation differing from `Settings.FeaturesFor`), fix it in `Presets.cs`.

**Checkpoint**: MyTimetables conditions behave per calendar.

---

## Phase 7: User Story 5 - Save a filter as a preset and reuse it (Priority: P2)

**Goal**: personal presets: create, rename, edit, delete, apply to subscriptions; linked until customised; private.

**Independent Test**: save a preset, apply to two calendars, both follow edits; customise one, only the other follows; deleting the preset keeps both outcomes; another subscriber cannot see or apply it.

### Tests for User Story 5

- [ ] T029 [P] [US5] In `InTheKnow.Tests/PresetsTests.cs` add: create/list/rename/edit/delete; names unique per subscriber (`name_taken`) but the same name is allowed for two subscribers; 50 personal presets then `preset_limit`; apply links (`preset_id` set, `rules` NULL); editing the preset changes the effective rules of linked subscriptions only (SC-003); replacing a linked subscription's filter customises it and later preset edits do not affect it; applying again re-links; deleting a preset converts linked rows to customised with the preset's last rules and outcomes do not change (FR-018); another subscriber's preset cannot be listed, edited, deleted or applied (FR-016); presets survive removal of the subscriber's last subscription (FR-019).
- [ ] T030 [P] [US5] In `InTheKnow.Tests/ApiTests.cs` add the routes of contracts/api.md § Personal presets: `GET/POST /api/subscribers/{s}/presets` (`201`, `400 invalid_name`/rules reasons, `409 name_taken`/`preset_limit`); `PUT`/`DELETE .../presets/{id}` (`200`/`204`, `404` for another subscriber's or unknown id, neither `name` nor `rules` → `400 invalid_rules`); `PUT .../calendars/{id}/filter/preset` (`200` with the GET body, `404` for an unknown or foreign preset or subscription); `GET .../presets` returns own presets first then global ones; the GET-then-POST "save as" flow works with the rules returned unchanged; after each rejected `POST` or `PUT`, `GET .../presets` returns the same list as before (SC-008).

### Implementation for User Story 5

- [ ] T031 [US5] In `InTheKnow/Presets.cs` add: `ListForSubscriberAsync` (own presets by name, then global by name), `CreateAsync(owner, name, rules)` (name 1–100 characters → `invalid_name`; count check `50` inside the insert transaction → `preset_limit`; unique violation `23505` → `name_taken`), `UpdateAsync(owner, id, name?, rules?)`, `DeleteAsync(owner, id)` and `ApplyAsync(subscriber, calendarId, presetId)`. Ownership: a preset with another `owner` or a missing id → not found; a global preset on the subscriber routes → `preset_is_global`. `DeleteAsync` runs one transaction: `UPDATE subscription_filter SET rules = <preset rules>, preset_id = NULL WHERE preset_id = $1`, then `DELETE FROM preset WHERE id = $1` (research R7; the FK is RESTRICT so the order matters). `ApplyAsync` upserts `preset_id = P, rules = NULL` after checking the subscription exists and the preset is the subscriber's own or global. Return small result records with an `Error` reason string like `AddLinkResult` in `Subscriptions.cs`.
- [ ] T032 [US5] In `InTheKnow/FilterApi.cs` add `PUT .../calendars/{calendarId:long}/filter/preset` and the four personal preset routes (`GET`/`POST /subscribers/{subscriber}/presets`, `PUT`/`DELETE /subscribers/{subscriber}/presets/{id:long}`), mapping results to the statuses in contracts/api.md (`Preset` JSON: `id`, `name`, `scope`, `rules`, and `isDefault: true` only for Default). Extend the `GET filter` body's `source` object to include `presetId`, `name`, `scope` for a linked subscription.

**Checkpoint**: US1–US5 work; presets are reusable per subscriber.

---

## Phase 8: User Story 6 - Admins offer global presets to everyone (Priority: P3)

**Goal**: the operator manages global presets and edits (but never deletes) Default; subscribers apply or copy them but cannot edit them.

**Independent Test**: as operator create a global preset; two subscribers list and apply it; edit Default and see linked subscriptions follow while customised ones do not.

### Tests for User Story 6

- [ ] T033 [P] [US6] In `InTheKnow.Tests/PresetsTests.cs` add: global create/rename/edit/delete; name unique among global presets; 100 global presets then `preset_limit`; Default rules and name editable, Default deletion → `preset_is_default`; deleting a global preset converts linked subscriptions to customised (FR-018, US6 scenario 6); a new subscription is on Default and no global-preset action changes existing choices (FR-023); a subscriber edit or delete of a global preset → `preset_is_global`, and copying (GET + create) yields a personal preset with the same rules.
- [ ] T034 [P] [US6] In `InTheKnow.Tests/ApiTests.cs` add the routes of contracts/api.md § Global presets: `GET/POST /api/presets`, `PUT/DELETE /api/presets/{id}` (`404` for a personal preset, `403 preset_is_default`, `409 name_taken`/`preset_limit`); all require the operator key (`401` without); subscriber `PUT/DELETE .../presets/{globalId}` → `403 preset_is_global`; a subscriber can list and apply a global preset; editing Default changes a linked subscriber's outcomes while a customised one is unaffected (US6 scenario 4).

### Implementation for User Story 6

- [ ] T035 [US6] In `InTheKnow/Presets.cs` add global variants: `ListGlobalAsync`, `CreateGlobalAsync` (limit `100`), `UpdateGlobalAsync` (allowed on Default), `DeleteGlobalAsync` (`preset_is_default` when `is_default`; otherwise the same convert-then-delete transaction as T031). Share the SQL and validation with the personal methods by passing `owner: null`, not by duplicating them.
- [ ] T036 [US6] In `InTheKnow/FilterApi.cs` add `GET/POST /api/presets` and `PUT/DELETE /api/presets/{id:long}` (operator-only by being under `/api` behind the existing key middleware; no subscriber id). A personal preset id on these routes returns `404`.

**Checkpoint**: all six stories are functional.

---

## Phase 9: Polish & Cross-Cutting

- [ ] T037 [P] Update `specs/001-ical-change-detection/contracts/detected-change.md`: document `event_before` / `event_after` (data-model.md § detected_change), and change its "no change is filtered out" line to say *recording* filters nothing while presentation and notification use the outcome (plan § Source Code).
- [ ] T038 [P] Update `specs/001-ical-change-detection/contracts/api.md` (or add a pointer) so the changes route documents `includeHidden` and `outcome`, linking to `specs/002-subscriber-change-filters/contracts/api.md`.
- [ ] T039 Run `dotnet build && dotnet test` from `backend/` and fix warnings or failures; confirm no new package was added and `packages.lock.json` is unchanged.
- [ ] T040 Run the manual scenario of quickstart.md § 2 against a dev database, then the SC-009 timing of quickstart.md § 3 (10,000 changes, most hidden, `limit=100` under 1 s). If it fails, optimise at the `ponytail:` comment from T016 and record the result in the PR description.

---

## Dependencies & Execution Order

### Phase dependencies

- **Setup (1)** → **Foundational (2)** → user stories → **Polish (9)**.
- Within Foundational: T007–T008 (recording) are independent of T009–T011 (rules and engine); T010 depends on T009, T011 on T009–T010. Tests T003–T006 come first and are all parallel.

### User story dependencies

- **US1** needs Phase 2 only. It creates `Presets.cs` (T014) that all later stories extend.
- **US2** needs US1 (`Presets.cs`, `Program.cs` registration). T023 needs T001.
- **US3** needs US2 (`GetFilterAsync`).
- **US4** needs US2 (filter routes) and is mostly verification of Phase 2 logic.
- **US5** needs US2 (replace/customise semantics); independent of US3 and US4.
- **US6** needs US5 (shares preset SQL and validation).

### Same-file ordering

`Presets.cs`: T014 → T021 → T026 → T031 → T035. `FilterApi.cs`: T022 → T032 → T036. `Checker.cs`: T008 → T023. `Api.cs`: T016 only (plus T022's `internal` change). `PresetsTests.cs`: T017 → T025 → T029 → T033. `ApiTests.cs`: T013 → T018 → T028 → T030 → T034. Tasks touching one file are not marked [P] against each other. The [P] marks above are across different files only, so where two [P] tasks write the same file within one story (for example T017 and T025 both edit `PresetsTests.cs` but in different stories) run them in story order.

## Parallel example: Foundational tests

```text
Task: "FilterRulesTests in InTheKnow.Tests/FilterRulesTests.cs"      (T003)
Task: "FilterEngineTests in InTheKnow.Tests/FilterEngineTests.cs"    (T004)
Task: "DifferTests extension in InTheKnow.Tests/DifferTests.cs"      (T005)
Task: "CheckerTests extension in InTheKnow.Tests/CheckerTests.cs"    (T006)
```

## Implementation Strategy

### MVP first (US1)

1. Phase 1 and 2, then Phase 3. **Stop and validate**: Default filtering with `includeHidden` on the changes route works. This alone delivers spec P1 value and unblocks the notification feature (FR-009).
2. Add US2 (per-calendar editing, the second P1) and ship.

### Incremental delivery

US3 → US4 → US5 → US6, each independently testable, each only adding to `Presets.cs`, `FilterApi.cs` and tests.

## Notes

- Tests for a story are written first and should fail before its implementation.
- Constraints are stated verbatim in the tasks (T001, T009, T031); do not loosen them in code.
- Commit after each task or logical group.
