# InTheKnow Constitution

## Core Principles

### I. Simplicity First (YAGNI)

- Every feature MUST start as the smallest implementation that satisfies its spec.
- Code, abstractions, dependencies, services, or config MUST NOT be added for speculative
  future needs; they are added when a current requirement demands them.
- Prefer, in order: removing the need, reusing existing code, the language standard library,
  native web platform features (HTML/CSS/browser APIs), already-installed dependencies, and
  only then new code or a new dependency.
- Any deviation (a new dependency, an extra layer, a new service) MUST be justified in the
  plan's Complexity Tracking table with the simpler alternative that was rejected and why.

Rationale: a solo-maintained project cannot afford code it does not need; every line and
dependency is future maintenance and attack surface.

### II. Privacy & Security by Default

- Collect and store only the user data a feature strictly requires; each stored personal
  field MUST be named and justified in the feature's data model.
- Secrets (API keys, tokens, credentials) MUST NOT be committed; they are supplied via
  environment or a secret store, and example config uses placeholders only.
- All input crossing a trust boundary (HTTP requests, forms, query params, third-party API
  responses) MUST be validated server-side; client-side validation is a convenience only.
- Output rendered in the browser MUST be escaped/encoded by default; raw HTML injection
  requires an explicit, reviewed justification.
- Authentication, session, and authorization code MUST use established, maintained libraries
  or platform features, never hand-rolled crypto.
- Traffic MUST use HTTPS outside local development.

Rationale: a web app is exposed to untrusted input by design; data never collected cannot
leak, and defaults are what actually ships.

## Technology Constraints

- InTheKnow is a web application (browser frontend plus server/API).
- The project is licensed GPL-3.0; every dependency MUST have a GPL-3.0-compatible license.
- App dependencies (NuGet and npm packages) MUST be pinned via a lockfile committed to the
  repository. Container base images MAY track a major-version tag (e.g. `aspnet:10.0`) so
  OS and runtime security patches land on each build; the published image is the immutable
  record of what shipped.
- Backend: .NET (C#), exposing the server/API.
- Database: PostgreSQL is the system of record; data integrity rules (NOT NULL, unique,
  foreign keys, checks) MUST be enforced as database constraints, not only in app code.
- Frontend: Svelte with SvelteKit.
- Swapping or adding a language, framework, or datastore is a MINOR amendment to this
  section and MUST be justified under Principle I.

## Development Workflow

- Single maintainer; pull requests are optional, direct commits to `main` are allowed.
- Automated checks (build, lint, tests where present) are the quality gate: a change MUST
  NOT be considered done while any check fails.
- Non-trivial logic (branching, parsing, auth, data handling) MUST ship with at least one
  automated check that fails if that logic breaks.
- Each feature follows the Spec Kit flow (specify → plan → tasks → implement), and every plan
  MUST pass the Constitution Check before implementation starts.

## Governance

- This constitution supersedes other project practices; where they conflict, it wins.
- Amendments are made by editing this file (via `/speckit-constitution`), with the change
  and version bump recorded in the commit message.
- Versioning follows semantic versioning: MAJOR for removing or redefining a principle,
  MINOR for adding a principle/section or materially expanding guidance, PATCH for
  clarifications and wording.
- Compliance is checked at each plan's Constitution Check gate and when reviewing a feature
  before merge; unjustified violations block the feature.

**Version**: 1.2.0 | **Ratified**: 2026-09-24 | **Last Amended**: 2026-10-04
