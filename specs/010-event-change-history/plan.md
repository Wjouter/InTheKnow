# Implementation Plan: Event change history

**Branch**: `010-event-change-history` | **Date**: 2026-11-11 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/010-event-change-history/spec.md`

## Summary

The time in the top corner of each feed change now shows when the change was found (`detectedAt`, which the API
already returns). It is relative below 24 hours ("2 hours ago") and an exact date and time after that. The full
value is in a `<time datetime title>` element.

Each feed change becomes a link to `/changes/{id}`.

- **Full page.** `/changes/{id}` is a real SvelteKit route. A reload, a new tab, Ctrl-click or middle-click opens
  it.
- **Card.** A plain click is intercepted. The feed calls `preloadData` and `pushState` (SvelteKit shallow routing),
  and the same content opens in a native modal `<dialog>` over the feed. Back closes it.

One new read-only backend route, `GET /api/me/changes/{id}/history`, provides the content. It works like this:

- **Lookup.** It resolves the change to its lesson `(calendar_id, event_key)`.
- **Access.** It checks that the student follows that timetable, using the same filter lookup the feed uses. Every
  failure returns the same 404 response.
- **Response.** It returns every kept row of that lesson with its filter outcome, the lesson's current or last
  known details, and the retention period.

The SvelteKit server side does the rest: grouping per check, the filter toggle, the 20-entry cap on the card, the
opened entry, and the FR-011, FR-014b and FR-015 messages. Switching the toggle only rewrites the URL and needs no
second API call.

One migration adds:

- an index on `detected_change (calendar_id, event_key, id)`, so a history read is an index range scan (SC-003);
- a nullable `calendar.baseline_at`, so the page can say when a lesson's recorded history starts (FR-011, edge
  case).

## Technical Context

**Language/Version**: C# on .NET 10 (backend); TypeScript, Svelte 5 / SvelteKit 2 (frontend)

**Primary Dependencies**: existing only. Backend: ASP.NET Core minimal APIs, Npgsql. Frontend: SvelteKit (shallow
routing: `pushState`, `preloadData`), native `<dialog>`, `Intl.RelativeTimeFormat`. No new package.

**Storage**: PostgreSQL. Migration `006_event_history.sql` adds one index and one nullable column. The history is
read from `detected_change`, `event_snapshot` and `calendar`.

**Testing**: xUnit v3 (`backend/InTheKnow.Tests`, real PostgreSQL via `TestDb`) and vitest (`frontend`). There is no
browser test runner. Interaction checks (back, Esc, focus, Ctrl-click, phone width) are manual steps in
[quickstart.md](./quickstart.md).

**Target Platform**: Linux server (the Docker stack from 006); current evergreen browsers, phone and desktop

**Project Type**: web application (backend API + SvelteKit frontend)

**Performance Goals**: SC-003: history ready within 1 s at p95 for up to 100 kept changes, at whole-school scale.
One request costs two primary-key lookups, one index range scan over the lesson's rows, and one filter load for one
calendar.

**Constraints**:

- The 404 response for "not found" and for "not allowed" must be identical (FR-016, SC-005).
- The URL holds only the change id, which the feed already exposes, plus `all=0|1` (FR-005d).
- No new personal data (FR-017).
- Both themes must work, at phone width (FR-019).

**Scale/Scope**:

- Backend: 1 API route, 1 migration, 2 one-line writes of `baseline_at`.
- Frontend: 1 route, 1 card component, changes to `ChangeGroup` and `Feed`, about 30 new texts in en and nl.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle / rule | Status |
|---|---|
| I. Simplicity: smallest implementation, no speculative code | PASS. The design adds one read route and reuses `ChangeView`, `FilterEngine`, `LoadFiltersAsync`, `describe`/`tone`/`icon` and the native `<dialog>`. The card and the page share one component. Paging, a "list all lessons" page and email changes stay out (spec Assumptions). |
| I. New dependency or layer justified | PASS. Nothing is added. Shallow routing, `<dialog>` and `Intl.RelativeTimeFormat` are platform and framework features. A Playwright suite was rejected (research R12). |
| I. Schema additions justified | PASS. The index is needed for SC-003 (R3). `baseline_at` is the only way to give a true "history starts" date after a re-baseline. `calendar.created_at` would be wrong (R4). |
| II. No new personal data | PASS. `baseline_at` is a calendar timestamp, not personal data. The route returns only rows that already exist (FR-017). |
| II. No secrets or deployment details in repo | PASS. Tests and quickstart use `localhost` and test accounts. |
| II. Trust boundary validated server-side | PASS. The API parses the id (`{id:long}`), resolves it and checks access. The frontend accepts `all` only as `0` or `1`, and ids only as a digit string. |
| II. Output encoded by default | PASS. Svelte escapes everything, and lesson details print as text. The existing `no-raw-html` test keeps covering this. |
| II. Authorization via established mechanisms | PASS. The route sits behind the existing session fallback policy, and access uses the existing subscription lookup. The 404 is the same for every reason. |
| Tech: GPL-compatible, lockfiles, constraints in DB | PASS. No package is added. The index and the column are plain DDL. |
| Workflow: non-trivial logic has an automated check | PASS. Backend: access and 404 parity, rows and outcomes, current details and their fallbacks, `baseline_at` set on both baseline paths. Frontend: found-time boundaries, grouping, toggle and FR-014b states, the 20 cap with "further back", the before/after rows, URL parsing, i18n parity. |

Post-design re-check: PASS. The design added nothing beyond the items listed above. The contracts keep every
decision in the existing API and page patterns.

## Project Structure

### Documentation (this feature)

```text
specs/010-event-change-history/
├── plan.md              # This file
├── research.md          # Phase 0: decisions R1–R12
├── data-model.md        # Phase 1: schema change, API and view shapes
├── quickstart.md        # Phase 1: validation scenarios
├── contracts/
│   ├── api.md           # GET /api/me/changes/{id}/history
│   └── pages.md         # /changes/{id}, the card, the feed link and found time
├── checklists/
│   └── requirements.md  # from /speckit-specify
└── tasks.md             # Phase 2 (/speckit-tasks, not created here)
```

### Source Code (repository root)

```text
backend/
├── InTheKnow/
│   ├── Migrations/006_event_history.sql   # NEW: index + calendar.baseline_at
│   ├── History.cs                         # NEW: route + history query (or a block in Api.cs, see R2)
│   ├── Api.cs                             # map the route next to /changes
│   ├── Checker.cs                         # row 3 baseline sets baseline_at
│   └── Subscriptions.cs                   # new calendar's baseline sets baseline_at
└── InTheKnow.Tests/
    └── HistoryTests.cs                    # NEW

frontend/src/
├── lib/
│   ├── server/
│   │   ├── changes.ts        # Change.id kept; group gets `href` + found time; history view builder
│   │   ├── changes.test.ts   # grouping/href/found-time cases
│   │   ├── history.ts        # NEW: load + build the history view (entries, rows, toggle state)
│   │   ├── history.test.ts   # NEW
│   │   ├── format.ts         # `found(iso)` + `retention(days)`
│   │   └── format.test.ts
│   ├── components/
│   │   ├── ChangeGroup.svelte    # becomes a link; corner shows found time in <time>
│   │   ├── Feed.svelte           # click interception + card host (shallow routing)
│   │   ├── History.svelte        # NEW: the shared content (details, toggle, entries, notes)
│   │   └── HistoryCard.svelte    # NEW: modal <dialog> frame around History
│   └── i18n/{en,nl}.ts           # new `history` texts + `changes.found*`
└── routes/changes/[id]/
    ├── +page.server.ts       # NEW: full page load
    └── +page.svelte          # NEW: full page frame around History
```

**Structure Decision**: the existing two-project layout (`backend/` ASP.NET Core API, `frontend/` SvelteKit). The
feature adds one backend file and one frontend route. Everything else changes existing files in place.

## Complexity Tracking

No constitution violations. The table is not needed.
