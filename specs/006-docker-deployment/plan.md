# Implementation Plan: Production Container Stack

**Branch**: `006-docker-deployment` | **Date**: 2026-10-01 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/006-docker-deployment/spec.md`

## Summary

This feature packages the existing API and web interface as two images and runs them with PostgreSQL as one Compose
stack, behind the operator's own reverse proxy on the same host.

- **Images** (research R2 to R4):
  - `backend/Dockerfile` publishes a portable .NET build onto `dotnet/aspnet:10.0`.
  - `frontend/Dockerfile` puts adapter-node's output on `node:24-alpine`.
  - Both build on the build machine's own processor type and copy portable output, so amd64 and arm64 need no
    emulation.
  - Both run as non-root, are built from the repository root with allowlist `COPY`s and a root `.dockerignore`
    that keeps the private links file, `.env` and the test fixtures out.
- **Stack** (R6 to R9, R13):
  - `deploy/compose.yaml`, project `intheknow-prod`, with `db`, `api` and `web` on a fixed-subnet network.
  - Only the web and API ports are published, on `127.0.0.1`. The API trusts forwarding from that subnet and
    loopback only.
  - Healthchecks are in the stack, `restart: always`, the API waits for a healthy database, and the restart policy
    covers retries.
  - Backup and restore use `pg_dump`/`pg_restore` through `compose exec`.
- **Settings** (R6):
  - One `.env` next to the stack definition.
  - 4 required settings (`PUBLIC_URL`, `POSTGRES_PASSWORD`, `SMTP_HOST`, `SMTP_FROM`), enforced by Compose's
    `${…:?}` before anything starts.
  - `PUBLIC_URL` feeds `ORIGIN` and both API base URLs.
  - Every other API key passes through `env_file` under its existing name.
  - The database password reaches Npgsql as `PGPASSWORD`, not inside the connection string.
- **Releases** (R10, R11):
  - A new `release.yml` runs only on `vX.Y.Z` tag pushes, which includes a GitHub release that creates the tag.
  - It reuses `ci.yml` through `workflow_call` as the gate, then pushes both images for amd64 and arm64 to GHCR with
    `GITHUB_TOKEN`, renders the pinned `compose.yaml` and attaches it to the release with `env.example` (no leading
    dot: GitHub renames such assets).
  - The maintainer's manual build and push uses the same commands, with `IMAGE` as an input.
- **One app code change** (R5): the spec says no app behavior changes, but its edge case "trailing slash, path or
  `http://` fails at startup" can't be met without one.
  - The API joins links as `{base}/path`, and `Settings.BaseUrl` accepts a trailing slash or a path today.
  - It becomes origin-only, with new `SettingsTests` cases. The QUICKSTART dev values (`http://localhost:5000`,
    `http://localhost:5173`) still pass.
  - One `SignInTests` case also pins IPv4-mapped trusted peers (R7).
- **Documentation** (R12 to R14):
  - `DEPLOY.md` covers prerequisites, first start, a worked and tested Caddy example with check commands, the first
    admin, status and logs, upgrade and rollback, backup and restore, the one-time Podman step, the maintainer's
    build and push, and troubleshooting for the edge cases.
  - README's "Behind a reverse proxy" section is replaced by a link to it, so the two can't disagree.

## Technical Context

**Language/Version**:
- Dockerfile, Compose spec, GitHub Actions YAML, POSIX shell for the documented commands.
- C# 14 on .NET 10 for the one validation change.
- The app toolchains are unchanged: .NET 10 SDK, Node 24.

**Primary Dependencies**:
- Base images, tracked by major tag (R15): `mcr.microsoft.com/dotnet/sdk:10.0`, `mcr.microsoft.com/dotnet/aspnet:10.0`,
  `docker.io/library/node:24-alpine`, `docker.io/library/postgres:18`.
- No new NuGet or npm package.
- No new GitHub Action beyond the existing `actions/checkout`, plus the runner's `docker buildx` and `gh`.

**Storage**: PostgreSQL 18 on the named volume `intheknow-prod_db`. No schema change ([data-model.md](./data-model.md)).

**Testing**:
- `dotnet test`: new `SettingsTests` cases for the origin-only rule, and a `SignInTests` case for IPv4-mapped peers.
- The existing frontend checks.
- The host-level validation (proxy, ports, reboot, Podman, multi-arch, registries) is the manual
  [quickstart](./quickstart.md) (R16).

**Target Platform**:
- One Linux server, amd64 or arm64.
- Docker Engine with Compose v2 (28 or later required, research R7), or Podman with `podman compose`.
- The operator's TLS reverse proxy runs on the same host.

**Project Type**: web application (existing `backend/` and `frontend/`), plus deployment packaging.

**Performance Goals**:
- SC-001: under 30 minutes to go live.
- SC-004: under 2 minutes back after a reboot.
- SC-005: under 2 minutes of upgrade downtime.
- SC-008: images published within 30 minutes of a tag. Builds without emulation keep the workflow at the length
  of the checks plus two image builds.

**Constraints**:
- Nothing reachable off the host (SC-003).
- No secret in any image or in the repository (FR-004b, FR-005).
- The dev `compose.yaml` and QUICKSTART are unchanged (FR-030).
- The stack works on both Docker and Podman (FR-008).

**Scale/Scope**:
- One host, one API instance (001 instance lock), whole-school data in one PostgreSQL.
- 3 services, 4 required settings, 3 commands to go live (SC-002).

No NEEDS CLARIFICATION items remain. All decisions are in [research.md](./research.md). The items that depend on a
runtime or platform are marked **Verify** there, each with a quickstart step and a fallback.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Rule | Status | How |
|---|---|---|
| I. Smallest implementation, no speculative code | PASS | Two Dockerfiles, one stack template, one example settings file, one workflow, one document. Things not built: no health endpoint (a TCP check suffices, R8), no wrapper script (R13), no render script (one `sed` line, R10), no alias for every API key (R6), no third-party build actions (R11), no backup container (R9), no Quadlet units (R14). |
| I. Preference order | PASS | The platform is used before new code: Compose's `${:?}` for required settings, `env_file` for pass-through, `depends_on` and restart policies instead of retry code, Npgsql's `PGPASSWORD`, `pg_dump`/`pg_restore`, Docker/Podman's own build and push. The existing `ci.yml` is reused as the release gate, and the existing `Settings` validation is reused for the URL rule. |
| I. Deviations justified | PASS | See Complexity Tracking: one app code change in a packaging feature. |
| II. Store only required data, name each personal field | PASS | No new stored data ([data-model.md](./data-model.md)). The settings file holds the same operator values as today's environment. |
| II. No committed secrets; placeholders only | PASS | `.env` is git-ignored (existing rule), `env.example` leaves the required settings empty (examples in comments only), so there is no working default, and `POSTGRES_PASSWORD` is required with no default (FR-012). `.dockerignore` and allowlist `COPY`s keep secrets out of images (R4). Registry credentials are `GITHUB_TOKEN` or the maintainer's own login (FR-004b). |
| II. Validate input at trust boundaries | PASS | The required settings are checked by Compose. The public address is checked origin-only by the API at startup (R5). Forwarded addresses are trusted only from the stack subnet and loopback (R7). |
| II. Escape browser output | N/A | No UI change. |
| II. Established auth libraries | N/A | No auth change. |
| II. HTTPS outside development | PASS | The stack listens on loopback only. The documented proxy terminates TLS, redirects HTTP and sends HSTS, and the check commands verify it (R12). |
| Tech: .NET, SvelteKit, PostgreSQL | PASS | Unchanged. PostgreSQL 18 is the same major version as development. |
| Tech: GPL-3.0-compatible licences, lockfiles | PASS | No new app dependency. Images restore with `--locked-mode` and `npm ci`. Base images track their major tag, as constitution 1.2.0 allows (R15). The runtime licences (.NET MIT, Node MIT, PostgreSQL licence) and Caddy (Apache-2.0, documentation only) are GPL-3.0-compatible. |
| Workflow: checks are the gate; tests for non-trivial logic | PASS (planned) | The origin-only rule and the IPv4-mapped trust have automated tests (R5, R7). Releases publish only after `ci.yml` passes (R11). |

**Post-design re-check (after Phase 1)**: PASS.
- The design adds 7 files:
  - `backend/Dockerfile`
  - `frontend/Dockerfile`
  - `.dockerignore`
  - `deploy/compose.yaml`
  - `deploy/env.example`
  - `.github/workflows/release.yml`
  - `DEPLOY.md`
- It also edits `Settings.cs` (one predicate), two test files, `ci.yml` (one trigger) and `README.md` (one section).
- It adds no service, store or background process to the app.

## Project Structure

### Documentation (this feature)

```text
specs/006-docker-deployment/
├── spec.md
├── plan.md              # This file
├── research.md          # Phase 0
├── data-model.md        # Phase 1: Key Entities as files and runtime objects; no DB change
├── quickstart.md        # Phase 1: end-to-end validation on a real server
├── contracts/           # Phase 1
│   ├── settings.md      # the operator's .env: required, optional, pass-through, stack-owned
│   ├── stack.md         # services, ports, health, restart, commands
│   └── images.md        # images, CI triggers, manual build/push, per-release stack definition
├── checklists/
└── tasks.md             # Phase 2 (/speckit-tasks)
```

### Source Code (repository root)

```text
.dockerignore                       # new: R4
DEPLOY.md                           # new: FR-029
README.md                           # "Behind a reverse proxy" → link to DEPLOY.md
compose.yaml                        # dev stack, unchanged (FR-030)
QUICKSTART.md                       # unchanged; its README link still resolves (to a new pointer section)
deploy/
├── compose.yaml                    # new: production stack template (contracts/stack.md)
└── env.example                     # new: settings placeholders (contracts/settings.md)
.github/workflows/
├── ci.yml                          # + on: workflow_call
└── release.yml                     # new: tag-only gate + publish (R11)
backend/
├── Dockerfile                      # new: R2
├── InTheKnow/Settings.cs           # BaseUrl → origin-only (R5)
└── InTheKnow.Tests/
    ├── SettingsTests.cs            # + origin-only cases
    └── SignInTests.cs              # + IPv4-mapped trusted peer case
frontend/
└── Dockerfile                      # new: R3
```

**Structure Decision**:
- The production files live in `deploy/`, apart from the dev `compose.yaml` at the root (R1).
- The Dockerfiles sit with the code they build, but use the repository root as context, because of
  `Directory.Build.props`.
- QUICKSTART links `README.md#behind-a-reverse-proxy`. That heading stays, as a two-line pointer to `DEPLOY.md`,
  so QUICKSTART itself is unchanged (FR-030).

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|---|---|---|
| One app code change in a packaging feature (origin-only base URLs) | The spec's edge case requires a trailing slash, a path or `http://` to fail at startup. The API joins links as `{base}/path`, so they would break silently (R5). | An entrypoint shell check duplicates the validation and protects only container installs. Silent normalising contradicts the edge case. |
