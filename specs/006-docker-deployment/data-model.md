# Data Model: Production Container Stack

This feature adds **no database tables, columns or migrations**, and stores **no new personal data** (Principle
II). The things below are the spec's Key Entities as files and runtime objects. Their exact shapes are in
[contracts/](./contracts/).

## Settings file (`.env`)

- **Where**: next to `compose.yaml` on the host, mode `600`. It is created from `deploy/env.example`.
- **What**: the 4 required settings, 6 optional named settings, and any `InTheKnow__…` API key
  ([contracts/settings.md](./contracts/settings.md)).
- **Secrets**: `POSTGRES_PASSWORD`, `SMTP_PASSWORD`, and optionally the bot check secret.
  - It is never committed: `.gitignore` already has `.env`.
  - It is never copied into an image: `.dockerignore` (research R4).
  - It is never given to the web container.
- **Personal data**: none, apart from what the operator chooses to put in (the SMTP account and the sender
  address). These are the same values as today's environment variables.
- **Lifecycle**: the operator creates it once. Upgrades never touch it. New releases may list a setting to add.
- **Validation**:
  - Compose: `:?` on the 4 required settings.
  - API `Settings.Load` at startup: every 001 to 005 rule, plus the origin-only base URL rule (research R5).

## Stack definition (`compose.yaml`)

- **Where**: next to `.env`. It is a per-release file rendered from `deploy/compose.yaml`
  ([contracts/images.md](./contracts/images.md#stack-definition-per-release)).
- **What**: project `intheknow-prod`, the services `db`, `api` and `web`, network `stack`, volume `db`
  ([contracts/stack.md](./contracts/stack.md)).
- **Lifecycle**: replaced on upgrade, never edited. The previous release's file is kept by the operator for
  rollback (FR-028).

## Image

- **Identity**: `<IMAGE>-api:<version>` and `<IMAGE>-web:<version>`, where `version` is `X.Y.Z` from tag
  `vX.Y.Z`.
- **Platforms**: amd64 and arm64 (automatic publishing), or the builder's own (manual).
- **Lifecycle**: immutable once published. A version is not reused. Re-pushing one replaces it, and the document
  warns against that.
- **Contains**: published app output only. No secrets, tests, dev tools or private fixtures. It runs as non-root.

## Database volume (`intheknow-prod_db`)

- **What**: the PostgreSQL 18 data directory. It holds everything InTheKnow stores: the 001 to 005 tables, which
  are unchanged.
- **Lifecycle**:
  - It is created on the first `up`.
  - It survives `down`, container recreation and upgrades.
  - It is deleted only by `down -v`, which is used only by the restore steps.
- **Schema**: the API migrates it forward at startup. That is existing behavior; there is no down-migration.

## Backup file (`*.dump`)

- **What**: the `pg_dump -Fc` output of the whole `intheknow` database, in one file.
- **Where**: wherever the operator redirects it. It contains every account and token hash, so the document tells
  them to store it like the database itself.
- **Lifecycle**:
  - It is made while the stack runs.
  - It is restored only onto a fresh volume, with `db` started alone first (research R9).
  - Scheduling it and copying it off the host are the operator's job.

## State transitions: stack lifecycle

| From | Action | To |
|---|---|---|
| nothing | `up -d` with a valid `.env` | db healthy → api migrated and healthy → web healthy |
| nothing | `up -d` with a required setting missing | nothing created; error names the setting |
| running | `up -d` with an invalid `PUBLIC_URL` | db, web up; api restarting with the key named in its log; not healthy |
| running | crash of any service | restarted (`restart: always`) |
| running | host reboot | all restarted (Docker; Podman after the one-time host step) |
| running | `pull`, then `up -d` with a new definition | old version keeps running during the pull; changed services recreated; api migrates |
| running | `pull` fails (registry unreachable) | still running the old version |
| running | `down` | stopped; volume kept |
| stopped | `down -v`, `up -d db`, `pg_restore`, `up -d` | running on the restored data |
