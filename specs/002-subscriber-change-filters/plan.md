# Implementation Plan: Subscriber Change Filters

**Branch**: `002-subscriber-change-filters` | **Date**: 2026-09-25 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/002-subscriber-change-filters/spec.md`

## Summary

Filters decide how each recorded change is *presented* to one subscriber; detection still records everything.

- **Storage**: a `preset` table (personal and global, with a seeded undeletable Default) and a
  `subscription_filter` side table. A row is linked to a preset or holds its own rules; **no row means Default**,
  so no backfill is needed (research R1).
- **001 extension**: `detected_change` gains `event_before` / `event_after` (research R2), because "event had
  ended" and the before-or-after conditions need the event as it was at detection.
- **Evaluation**: one pure C# function, `FilterEngine.Outcome`, evaluated when changes are read. The changes
  endpoint loads the subscriber's filters once, then scans keyset batches until a page of visible changes is
  full (research R3). The notification feature reuses the same function (FR-009).
- **Rules**: a closed JSON object per rule, `{outcome, when{…}}`, validated server-side ([rules.md](./contracts/rules.md)).
  MyTimetables conditions are active per calendar from the same settings 001 uses (research R5).
- **API**: filter view/replace/apply per subscription, personal presets, operator-only global presets, and
  `includeHidden` plus `outcome` on the existing changes route ([api.md](./contracts/api.md)).

## Technical Context

**Language/Version**: C# 14 on .NET 10 (unchanged from 001)

**Primary Dependencies**: none added. The BCL (`System.Text.Json`), Npgsql and the ASP.NET Core framework from 001
cover it.

**Storage**: PostgreSQL 18, one new embedded migration `002_filters.sql` ([data-model.md](./data-model.md))

**Testing**: xunit.v3 as in 001. `FilterEngine` and rule validation are pure and unit-tested; presets and the
filter lifecycle run against the compose database; routes through `WebApplicationFactory`
([quickstart.md](./quickstart.md)).

**Target Platform**: Linux server, the single 001 process

**Project Type**: backend of a web application; the SvelteKit frontend is a later feature

**Performance Goals**: a filtered changes page in under 1 s for a subscriber with 10,000 recorded changes, most
hidden (SC-009). Measured once by hand, as in 001, not asserted in a test.

**Constraints**: at most 50 rules per filter, 50 values per condition, 50 personal and 100 global presets;
names and texts 1–100 characters (FR-025). Filtering never changes what is recorded (FR-001).

**Scale/Scope**: a whole school: tens of thousands of subscribers, a few filters each. Filters and presets are
small `jsonb` values; the largest cost is the read-time scan (R3).

No NEEDS CLARIFICATION items remain. All decisions are in [research.md](./research.md).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Rule | Status | How |
|---|---|---|
| I. Smallest implementation, no speculative code | PASS | No new project, layer or dependency. No "save as" or "copy" endpoint (the client composes GET + POST). Outcomes are derived, not stored. Preset caps have no advisory lock (R7). |
| I. Preference order | PASS | `System.Text.Json` and the existing Npgsql code cover parsing and storage. |
| I. Deviations justified | PASS | One noted below in Complexity Tracking |
| II. Store only required data, name each personal field | PASS | [data-model.md § Personal and sensitive data](./data-model.md#personal-and-sensitive-data-principle-ii) |
| II. No committed secrets | PASS | No new secrets. |
| II. Validate input at trust boundaries | PASS | Rules, names, ids and query parameters are validated server-side ([rules.md § Validation](./contracts/rules.md#validation)); limits are constraints where the database can express them. |
| II. Escape browser output | N/A | JSON only; the frontend escapes preset names and rule text. |
| II. Established auth | PASS | Reuses 001's operator-key check; global routes are path-separated ([api.md](./contracts/api.md)). |
| II. HTTPS outside development | PASS | Unchanged from 001. |
| Tech: .NET, PostgreSQL, constraints in the database | PASS | Uniqueness, link-or-customise, one Default and size limits are constraints ([data-model.md](./data-model.md)). The two cross-table rules, preset ownership (FR-016) and "a filter row needs a link", are enforced by triggers in `002_filters.sql` ([data-model.md § Cross-table rules](./data-model.md#cross-table-rules-triggers)). Rule shape and the preset count caps are application checks because SQL cannot express them cheaply. |
| Tech: GPL-3.0-compatible licences, lockfile | PASS | No dependency change, so no lockfile change. |
| Workflow: checks as the gate; tests for non-trivial logic | PASS (planned) | Engine, validation, differ extension, preset lifecycle and routes each get a failing-if-broken test ([quickstart § 1](./quickstart.md)). |

**Post-design re-check (after Phase 1)**: PASS. The design added two tables and two columns, no dependency, layer or
service.

## Project Structure

### Documentation (this feature)

```text
specs/002-subscriber-change-filters/
├── spec.md
├── plan.md              # This file
├── research.md          # Phase 0
├── data-model.md        # Phase 1
├── quickstart.md        # Phase 1
├── contracts/           # Phase 1
│   ├── rules.md
│   └── api.md
├── checklists/
└── tasks.md             # Phase 2 (/speckit-tasks)
```

### Source Code (repository root)

```text
backend/
├── InTheKnow/
│   ├── Migrations/002_filters.sql   # NEW: preset, subscription_filter, Default seed, detected_change columns, triggers
│   ├── FilterRules.cs               # NEW: rule model, parse + validation, active conditions
│   ├── FilterEngine.cs              # NEW: ChangeView, Outcome, FilterEngine.Outcome (pure)
│   ├── Presets.cs                   # NEW: preset CRUD and caps, filter lookup/replace/apply, picker values, delete-conversion
│   ├── FilterApi.cs                 # NEW: MapFilterApi(): filter, preset and global-preset routes
│   ├── Differ.cs                    # CHANGED: ChangeRow carries before/after; details removed
│   ├── Checker.cs                   # CHANGED: COPY writes event_before / event_after; regroup copies subscription_filter rows
│   ├── Api.cs                       # CHANGED: changes route gains includeHidden, outcome and the batch scan; Problem made internal
│   └── Program.cs                   # CHANGED: register Presets, call MapFilterApi()
└── InTheKnow.Tests/
    ├── FilterEngineTests.cs         # NEW
    ├── FilterRulesTests.cs          # NEW
    ├── PresetsTests.cs              # NEW
    ├── DifferTests.cs, ApiTests.cs, DbTests.cs   # EXTENDED
```

001's `specs/001-ical-change-detection/contracts/detected-change.md` is updated as part of implementation: the
two new columns, and its "no change is filtered out" line now reads that *recording* filters nothing while
presentation and notification use the outcome.

**Structure Decision**: same single backend project and single test project as 001. The three new files are
one per concern (rules and engine, storage, routes), matching how 001 splits `Api.cs` and `Subscriptions.cs`.

## Complexity Tracking

| Addition / deviation | Why needed | Simpler alternative rejected because |
|---|---|---|
| Read-time C# evaluation with a batched keyset scan and no scan cap | FR-007 (edits apply to old changes), FR-009 (one function for notifications), and the rule that a page is never empty while later visible changes exist | Compiling rules to SQL duplicates the logic and is hard to test. Storing outcomes per (change, subscriber) needs a rewrite on every filter edit. See research R3. |
