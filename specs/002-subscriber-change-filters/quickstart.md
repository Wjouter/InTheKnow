# Quickstart: Subscriber Change Filters

Validation guide. Setup, database and test commands are the same as
[001 quickstart](../001-ical-change-detection/quickstart.md). Contracts: [api.md](./contracts/api.md),
[rules.md](./contracts/rules.md). Model: [data-model.md](./data-model.md).

## Prerequisites

- The compose PostgreSQL from 001 running, `INTHEKNOW_TEST_DB` set.
- Migration `002_filters.sql` applies at startup (it is embedded like `001`).

## 1. Automated checks

```bash
dotnet build && dotnet test
```

New or extended tests (each fails if its logic breaks):

| Test file | Covers |
|---|---|
| `FilterEngineTests` | Each Default rule and ordinary changes (SC-001); rule order; show-only with two rules (OR) and with none active; before-or-after matching; `eventEnded` at detection time incl. time change into the future; inactive rules on a generic calendar (SC-006) |
| `FilterRulesTests` | Every validation reason and limit in [rules.md](./contracts/rules.md#validation) (SC-008) |
| `DifferTests` (extended) | `event_before` / `event_after` on `changed`, none on `added` / `removed`, `details` removed |
| `PresetsTests` (database) | Link, customise, re-link; edit fan-out (SC-003); delete converts linked filters (FR-018); Default not deletable; names unique per owner incl. global; caps; subscription removal deletes the filter row and re-subscribing starts on Default |
| `ApiTests` (extended) | Filter and preset routes, private presets (FR-016), global routes, 403/404 cases, `includeHidden`, `outcome`, empty-page rule (paging past many hidden changes), two subscribers of one calendar (SC-002) |

## 2. Manual scenario: defaults, edit, preset

With the app running against a dev database and a fixture calendar with several recorded changes:

1. `GET /api/subscribers/alice/changes`: hidden changes are absent; a tentative → confirmed change has
   `outcome: lowPriority`.
2. `GET …/changes?includeHidden=true`: every recorded change is present, hidden ones marked (SC-004).
3. `PUT …/calendars/{id}/filter` with a `showOnly` rule on `group`: the next `changes` read reflects it,
   including older changes (SC-003, SC-005).
4. `POST /api/subscribers/alice/presets` with the same rules, then `PUT …/calendars/{other}/filter/preset`:
   both calendars follow a later `PUT …/presets/{id}`; a customised one does not.
5. As operator, `POST /api/presets` "Everything" (`[]`): bob lists it and applies it (US6).

Expected: each step matches the acceptance scenarios in [spec.md](./spec.md).

**Walkthrough notes (T040, 2026-09-26)**: run against a throwaway database with two MyTimetables calendars
(alice on both, bob on the first), seeded after startup with `next_check_at` in 2100 so nothing was fetched, and
eight recorded changes covering each Default rule. All five steps matched. Also seen: `includeHidden=yes` → `400`,
an invalid `PUT` left the filter in place, a duplicate preset name → `409 name_taken`, bob could neither list nor
apply alice's preset (`404`), `preset_is_global` and `preset_is_default` as specified, deleting "Everything" left
bob's subscription customised with `[]`, and alice and bob saw different outcomes on the shared calendar (SC-002).
The app log had no errors and no links.

## 3. Performance check (SC-009, by hand)

Seed 10,000 `detected_change` rows on one calendar, about 95% of them hidden by the Default preset, then time
`GET …/changes?limit=100` for a subscriber and for a subscriber whose first 9,000 rows are all hidden.
Expected: under 1 s. If it is not, the `ponytail:` ceiling in the changes endpoint (a full keyset scan per
page) is where to optimise (research R3).

The explicit test `FilteredChanges10000` in `ApiTests` does this seeding and logs the timings.

**Result (T040, 2026-09-26)**: 160 ms (95% hidden) and 434 ms (first 9,000 hidden), both under 1 s, so the
`ponytail:` ceiling stays.
