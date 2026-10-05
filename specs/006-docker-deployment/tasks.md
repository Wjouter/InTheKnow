---

description: "Task list for Production Container Stack (006)"
---

# Tasks: Production Container Stack

**Input**: Design documents from `/specs/006-docker-deployment/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: Included only where the plan calls for them (constitution: non-trivial logic ships with an automated check). These are the origin-only base URL rule (research R5) and the IPv4-mapped trusted peer (R7). Everything that depends on a host (proxy, ports, reboot, Podman, multi-arch, registries) is the manual [quickstart](./quickstart.md) (R16).

**Organization**: grouped by user story, in priority order: US1 and US2 (P1), US3 to US5 (P2), US6 (P3). US1 and US2 together are the MVP. `deploy/compose.yaml` is written whole in US1, exposure rules included, so the MVP is never an unsafe stack. US2, US3 and US5 are then `DEPLOY.md` sections plus validation.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (a different file, and no dependency on an unfinished task). `DEPLOY.md` tasks are never `[P]` with each other.
- **[Story]**: the user story the task belongs to (US1 to US6).
- **`Manual (maintainer):`**: the task needs a real server, a second machine, a registry, a pushed tag or a GitHub release. **The implement step MUST NOT push tags, push images, publish releases or change package visibility.** It leaves these tasks unchecked and lists them in its final report for the maintainer. It never ticks them off without the maintainer's result.

## Path Conventions

- Paths are from the repository root. Backend: `backend/InTheKnow/`, with its tests in `backend/InTheKnow.Tests/`. Frontend: `frontend/`.
- New files: `.dockerignore`, `backend/Dockerfile`, `frontend/Dockerfile`, `deploy/compose.yaml`, `deploy/env.example`, `.github/workflows/release.yml`, `DEPLOY.md`.
- Edited files: `backend/InTheKnow/Settings.cs`, `backend/InTheKnow.Tests/SettingsTests.cs`, `backend/InTheKnow.Tests/SignInTests.cs`, `.github/workflows/ci.yml`, `README.md`.
- **MUST NOT change**: the root `compose.yaml` and `QUICKSTART.md` (FR-030).
- Commands are written as `docker`. `podman` takes the same arguments. Local runnable checks use whichever runtime is installed. If neither is installed, say so in the report and leave that check for the maintainer.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: keep secrets and build junk out of every build context (research R4)

- [X] T001 Create `.dockerignore` at the repository root. Patterns are anchored at the context root, so write `**/…` wherever a file can sit in a subdirectory. Exclude at least:
  - `.git`, `.github`, `.idea`, `.claude`, `.junie`, `.specify`, `specs`
  - `**/bin`, `**/obj`, `**/node_modules`, `frontend/build`, `frontend/.svelte-kit`
  - `**/.env`, `**/.env.*` (not just `.env`, which only matches the root)
  - every secret line of `.gitignore`, mirrored: `**/appsettings.*.local.json`, `backend/InTheKnow/Properties/launchSettings.json`, `**/*.user`
  - `mytimetable example links.md` (live tokens), `backend/InTheKnow.Tests/` (tests and `Fixtures/private/`)
  - `deploy/`

  Add a one-line comment saying the Dockerfiles also copy named paths only (allowlist), so this file is the second layer.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: the two images and the one app change. Every story needs the images, and US1's start-up check needs the origin-only rule.

**⚠️ CRITICAL**: no user story work can begin until this phase is complete

### Tests first

- [X] T002 [P] Add origin-only cases to `backend/InTheKnow.Tests/SettingsTests.cs` (research R5, quickstart §1). They fail until T004.
  - In the `InvalidNotificationSettingsFailStartup` theory (`withEmail: true`), add rejected rows for `PublicBaseUrl`:
    - `https://intheknow.example.nl/` (trailing slash)
    - `https://intheknow.example.nl/app` (path)
    - `https://intheknow.example.nl?a=1` (query)
    - `https://intheknow.example.nl#x` (fragment)
    - `https://user@intheknow.example.nl` (user info)
  - Add the trailing-slash and path rows for `FrontendBaseUrl` too. The existing `http://` rows stay.
  - In `NotificationSettingsBoundaries`, add accepted loads through `WithEmail(...)`:
    - `FrontendBaseUrl = http://localhost:5173`, with `Email:Host = localhost` and `Email:StartTls = false`, the same as the existing `http://localhost:5000` line;
    - `PublicBaseUrl = https://intheknow.example.nl:8443`.
- [X] T003 [P] Add an IPv4-mapped trusted peer case to `backend/InTheKnow.Tests/SignInTests.cs` (research R7).
  - Before writing it, read how `Settings.TrustedProxyNetworks` is consumed in `backend/InTheKnow/Program.cs` and `SignIn.cs`, and how the test-only `X-Test-Source` header sets the peer (`backend/InTheKnow.Tests/ApiTests.cs:112`).
  - Model it on `ForwardedForOnlyFromTrustedProxies` (`SignInTests.cs:210`), as a new `[Fact]` (or a second assertion in that test).
  - Send `X-Test-Source: ::ffff:192.0.2.1` and `X-Forwarded-For: 203.0.113.9`, with `InTheKnow:Accounts:TrustedProxies:0 = 192.0.2.0/24`. Expect `host(source)` to be `203.0.113.9`.
  - This is a **pinning test**: it may pass on the first run. That's fine. If it fails, don't change app code. Apply the R7 fallback instead. Compose can't turn `${STACK_SUBNET}` into mapped form, so the fallback is a second optional setting:
    - in T008, add `InTheKnow__Accounts__TrustedProxies__3: ${STACK_SUBNET_MAPPED:-"::ffff:172.30.250.0/120"}`;
    - in T009, add `STACK_SUBNET_MAPPED=::ffff:172.30.250.0/120` commented out under `STACK_SUBNET`, with the comment: change it together with `STACK_SUBNET` (`::ffff:<subnet>/120` for a /24);
    - add both to contracts/settings.md (optional named table, and `TrustedProxies__0..3` in the stack-owned list), and note it in research R7.

### Implementation

- [X] T004 Make `Settings.BaseUrl` origin-only in `backend/InTheKnow/Settings.cs:180` (research R5, FR-010, FR-014).
  - Keep today's rule: an absolute URL; `https`, or `http` only when the host is `localhost`.
  - Add all of these:
    - `!url.EndsWith('/')`. `Uri` normalizes `https://x.nl` and `https://x.nl/` to the same `AbsolutePath` `/`, so the trailing slash must be checked on the string;
    - `u.AbsolutePath == "/"`;
    - `u.Query == ""`, `u.Fragment == ""` and `u.UserInfo == ""`.
  - Update both messages at `Settings.cs:137` and `:138`. They must keep the full key name (`InTheKnow:Notifications:PublicBaseUrl` / `…FrontendBaseUrl`). The new text: `must be an origin only (https://host[:port], no path, trailing slash, query or fragment; http only on localhost) when email is configured`.
  - All existing values are already origin-only (`EmailChannelTests.Public`/`Frontend`, QUICKSTART, README, `launchSettings.example.json`). Grep `PublicBaseUrl|FrontendBaseUrl` across `backend/` once more to confirm.
  - T002 and T003 must pass. Run `dotnet test`.
- [X] T005 [P] Create `backend/Dockerfile` (research R2, contracts/images.md). It is built with the repository root as context (`docker build -f backend/Dockerfile .`).
  - **Build stage**: `FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build`.
    - `COPY` only `Directory.Build.props`, `global.json` and `backend/InTheKnow/`, never `COPY . .`.
    - `RUN dotnet restore backend/InTheKnow/InTheKnow.csproj --locked-mode`, with **no** `-r`/`-a`. The lockfile has no runtime section.
    - `RUN dotnet publish backend/InTheKnow/InTheKnow.csproj -c Release --no-restore -p:UseAppHost=false -o /app`.
  - **Runtime stage**: `FROM mcr.microsoft.com/dotnet/aspnet:10.0` (Ubuntu, not chiseled: the healthcheck needs `bash`, and `DefaultTimeZone` needs ICU and `tzdata`).
    - `WORKDIR /app`, `COPY --from=build /app .`, `USER $APP_UID`, `ENTRYPOINT ["dotnet", "InTheKnow.dll"]`.
    - **No `RUN` in the runtime stage.** An arm64 `RUN` would need QEMU, which the release workflow does not install.
    - No `HEALTHCHECK` and no `ENV ASPNETCORE_HTTP_PORTS`. Both are in the stack (R8, R2).
- [X] T006 [P] Create `frontend/Dockerfile` (research R3, contracts/images.md). Context is the repository root.
  - **Build stage**: `FROM --platform=$BUILDPLATFORM docker.io/library/node:24-alpine AS build`, `WORKDIR /src`.
    - `COPY frontend/package.json frontend/package-lock.json ./`, `RUN npm ci`.
    - `COPY frontend/ ./`, `RUN npm run build`.
    - `RUN npm ci --omit=dev --prefix /prod` after copying `package.json` and `package-lock.json` to `/prod`. This is the runtime `node_modules`. The four production packages are pure JS/CSS, so installing on the build platform is correct.
  - **Runtime stage**: `FROM docker.io/library/node:24-alpine`, `WORKDIR /app`, `ENV NODE_ENV=production`.
    - `COPY --from=build` `/src/build`, `/prod/node_modules` and `/src/package.json` into `/app`.
    - `USER node`, `CMD ["node", "build"]`.
    - **No `RUN` in the runtime stage.** No init process: adapter-node handles `SIGTERM`.
    - No setting is baked in. The frontend reads only `$env/dynamic/private`.
- [X] T007 Build both images locally and inspect them (quickstart §2, FR-004, FR-005). Skip with a note if no container runtime is installed.
  - Run `docker build -f backend/Dockerfile -t localhost/intheknow-api:0.0.0-test .` and the same for `frontend/Dockerfile` → `localhost/intheknow-web:0.0.0-test`. Both must succeed with no registry sign-in.
  - Run the two `docker run --rm --entrypoint sh … find …` commands from quickstart §2.
    - The users must be `app` and `node`.
    - The web image's `node_modules` must hold only `@fontsource/*`, `@lucide/svelte`, `uqr` and their dependencies.
    - There must be no `find` hits.
    - The API image must have no `dotnet` SDK (`dotnet --list-sdks` prints nothing).
  - Fix the Dockerfile or `.dockerignore` on any failure.

**Checkpoint**: `dotnet test` passes, and both images build and contain only published output.

---

## Phase 3: User Story 1 - Put InTheKnow online on a fresh server (Priority: P1) 🎯 MVP

**Goal**: one settings file, one `up`, one `make-admin`, and the app answers at the public address.

**Independent Test**: on a clean server, follow `DEPLOY.md` from start to finish. Start the stack, configure the proxy, run the admin command, accept the invite, turn on two-step verification, and see the admin pages unlocked (quickstart §3, §4).

### Implementation for User Story 1

- [ ] T008 [P] [US1] Create `deploy/compose.yaml` exactly as [contracts/stack.md](./contracts/stack.md) specifies (research R6 to R10, R13, R14).
  - **Top level**:
    - `name: intheknow-prod`;
    - network `stack` with `ipam: config: - subnet: ${STACK_SUBNET:-172.30.250.0/24}`;
    - volume `db`;
    - no proxy, no mail server, no Mailpit.
  - **`db`**:
    - `image: docker.io/library/postgres:18`;
    - `POSTGRES_USER: intheknow`, `POSTGRES_DB: intheknow`, `POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?POSTGRES_PASSWORD is required, see DEPLOY.md}`;
    - `volumes: - db:/var/lib/postgresql`;
    - **no `ports:`**.
  - **`api`**:
    - `image: ghcr.io/wjouter/intheknow-api:VERSION`. Literal text: this is the render placeholder of R10. Not a variable.
    - `env_file: .env`;
    - `ports: - "127.0.0.1:${API_PORT:-5000}:8080"`;
    - `depends_on: db: condition: service_healthy`;
    - the API environment, verbatim (keys and values):

      ```yaml
      ASPNETCORE_HTTP_PORTS: "8080"
      ConnectionStrings__Default: Host=db;Username=intheknow;Database=intheknow
      PGPASSWORD: ${POSTGRES_PASSWORD:?POSTGRES_PASSWORD is required, see DEPLOY.md}
      InTheKnow__Accounts__TrustedProxies__0: ${STACK_SUBNET:-172.30.250.0/24}
      InTheKnow__Accounts__TrustedProxies__1: 127.0.0.1
      InTheKnow__Accounts__TrustedProxies__2: "::1"
      InTheKnow__Notifications__PublicBaseUrl: ${PUBLIC_URL:?PUBLIC_URL is required, see DEPLOY.md}
      InTheKnow__Notifications__FrontendBaseUrl: ${PUBLIC_URL:?PUBLIC_URL is required, see DEPLOY.md}
      InTheKnow__Notifications__Email__Host: ${SMTP_HOST:?SMTP_HOST is required, see DEPLOY.md}
      InTheKnow__Notifications__Email__Port: ${SMTP_PORT:-587}
      InTheKnow__Notifications__Email__Username: ${SMTP_USERNAME:-}
      InTheKnow__Notifications__Email__Password: ${SMTP_PASSWORD:-}
      InTheKnow__Notifications__Email__FromAddress: ${SMTP_FROM:?SMTP_FROM is required, see DEPLOY.md}
      ```

  - **`web`**:
    - `image: ghcr.io/wjouter/intheknow-web:VERSION` (literal);
    - **no `env_file`**;
    - `ports: - "127.0.0.1:${WEB_PORT:-3000}:3000"`;
    - **no `depends_on`** (FR-020: it tolerates the API starting later);
    - the web environment, verbatim:

      ```yaml
      ORIGIN: ${PUBLIC_URL:?PUBLIC_URL is required, see DEPLOY.md}
      API_URL: http://api:8080
      ADDRESS_HEADER: X-Forwarded-For
      XFF_DEPTH: "1"
      HOST: 0.0.0.0
      PORT: "3000"
      ```

  - **All three services**:
    - `restart: always` (not `unless-stopped`, R8);
    - `networks: [stack]`;
    - healthchecks with `interval: 10s`, `timeout: 5s`, `retries: 6`:
      - db: `["CMD", "pg_isready", "-U", "intheknow", "-d", "intheknow"]`;
      - api: `["CMD", "bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080"]`, with `start_period: 30s`;
      - web: `["CMD", "node", "-e", "require('net').connect(3000,'127.0.0.1').on('connect',()=>process.exit(0)).on('error',()=>process.exit(1))"]`.
  - No `deploy.replicas` and no scale setting (FR-022).
  - A header comment says: this is a template; the release renders `VERSION` (contracts/images.md); operators never edit it.
- [ ] T009 [P] [US1] Create `deploy/env.example` per [contracts/settings.md](./contracts/settings.md) (research R6).
  - **Header comment**:
    - copy it to `.env` next to `compose.yaml`, then `chmod 600 .env`;
    - one `NAME=value` per line;
    - wrap values containing `$`, `#`, a space or a quote in **single quotes**;
    - upgrades never change this file.
  - **Required, each line EMPTY** (`PUBLIC_URL=`), with the example only in the comment above it. A placeholder would pass `:?` and become a working default (US2 AS5, FR-012):
    - `PUBLIC_URL`. Example `https://intheknow.example.nl`. Comment: origin only (https, host, optional port; no path, no trailing `/`, no query, no fragment).
    - `POSTGRES_PASSWORD`. Comment: generate it with `openssl rand -hex 32`. It is read only when the volume is first created; see `DEPLOY.md` to change it later.
    - `SMTP_HOST`. Example `smtp.example.nl`.
    - `SMTP_FROM`. Example `intheknow@example.nl`.
  - **Optional named settings, commented out with their defaults**:
    - `SMTP_PORT=587`
    - `SMTP_USERNAME=`, `SMTP_PASSWORD=` (set them together)
    - `WEB_PORT=3000`, `API_PORT=5000`
    - `STACK_SUBNET=172.30.250.0/24` (change it only if `up` reports that the pool overlaps)
  - **Other API settings, commented out** (FR-013):
    - `InTheKnow__CheckInterval=00:10:00`
    - `InTheKnow__Notifications__DefaultTimeZone=Europe/Amsterdam`
    - `InTheKnow__Accounts__BotCheck__Provider`, `…__SiteKey`, `…__SecretKey`
    - `InTheKnow__MyTimetables__Domains__0__Host=mytimetables.inholland.nl`
    - a line linking the 001, 003 and 004 `contracts/configuration.md` files for every other key
  - **A "set by the stack, setting them here has no effect" comment**, listing the stack-owned keys of contracts/settings.md.
- [ ] T010 [US1] Check the stack definition locally (depends on T008, T009). Skip with a note if no runtime is installed. In a scratch directory:
  1. Copy `deploy/compose.yaml` and `deploy/env.example` to `compose.yaml` and `.env`.
  2. Run `docker compose config`. It must fail naming `PUBLIC_URL` (or the first required setting), with `… is required, see DEPLOY.md`.
  3. Fill the four required settings and `SMTP_PASSWORD='pa$$ w#rd'`, then run `docker compose config`.
     - It must render.
     - `web` must have no `.env` keys.
     - `api` must show the literal SMTP password.
     - `db` must have no `ports`.
  4. Render with the T007 images, using the R10 `sed` line with `IMAGE=localhost/intheknow VERSION=0.0.0-test`. Then `docker compose up -d`.
     - All three services must reach `healthy`.
     - `api` logs must show the migrations.
     - `ss -tlnp` must show only `127.0.0.1:3000` and `127.0.0.1:5000`.
     - The local SMTP host may log delivery failures. That's fine here.
  5. Then `docker compose down -v`.
- [ ] T011 [US1] Create `DEPLOY.md` at the repository root, with **every FR-029 section as a heading**. Later phases fill their own section, so leave those as one-line `TODO (Txxx)` stubs:
  - Prerequisites
  - Settings
  - First start
  - Reverse proxy
  - First admin
  - Status and logs
  - Upgrade and rollback
  - Backup and restore
  - Podman
  - Building and publishing images
  - Troubleshooting

  Fill these sections in this task:
  - **Prerequisites**:
    - a Linux amd64 or arm64 host with time sync (two-step codes depend on it);
    - Docker Engine with Compose v2, **28 or later required** (`docker version --format '{{.Server.Version}}'`): older engines let hosts on the same LAN reach loopback-published ports and container addresses directly, including the database. Or Podman with `podman compose` (see § Podman for the rootful check);
    - a TLS reverse proxy on the same host;
    - a domain;
    - an SMTP server with STARTTLS.
  - **Settings**:
    - the required, optional and stack-owned tables from contracts/settings.md;
    - the single-quote rule and `chmod 600 .env`;
    - **the key-name map**: an API log line naming `InTheKnow:Notifications:PublicBaseUrl` or `…FrontendBaseUrl` means fix `PUBLIC_URL`;
    - changing `POSTGRES_PASSWORD` after the first start: `docker compose exec db psql -U intheknow -c "ALTER USER intheknow PASSWORD '<new>'"`, then edit `.env` and run `docker compose up -d`.
  - **First start**:
    - `mkdir intheknow && cd intheknow`;
    - download `compose.yaml` and `env.example` from `https://github.com/Wjouter/InTheKnow/releases/download/vX.Y.Z/`;
    - `cp env.example .env`, edit, `chmod 600 .env`;
    - `docker compose up -d`;
    - `docker compose ps` until all three are `healthy`.
  - **First admin**:
    - after `healthy`: `docker compose exec api dotnet InTheKnow.dll make-admin you@example.nl [--language en|nl]`;
    - it prints `invite queued`, and the email link opens "Accept your invite";
    - if no email arrives, look at `docker compose logs api` (003 retry rules).
  - **Status and logs**:
    - `docker compose ps`, `docker compose logs [-f] [db|api|web]`;
    - Docker's `json-file` driver does not rotate logs: the optional `daemon.json` `"log-driver": "local"`, and disk space is the operator's job.
  - Note at the top that every command runs in the directory holding `compose.yaml` and `.env`, and that `podman compose` takes the same arguments.
- [ ] T012 [US1] Replace the body of README's `### Behind a reverse proxy` section in `README.md:81`.
  - Keep the heading text **exactly**: QUICKSTART links `README.md#behind-a-reverse-proxy`, and QUICKSTART must not change.
  - Replace the body with two lines:
    - production runs as the container stack in [DEPLOY.md](DEPLOY.md);
    - a non-container install follows the same proxy rules and the [005 configuration contract](specs/005-sveltekit-frontend/contracts/configuration.md).
  - Also add `DEPLOY.md` to the `## Documentation` list (FR-029: README links it).
- [ ] T013 [US1] Manual (maintainer): run quickstart §3 (first start, missing and malformed `PUBLIC_URL`, `ss`, port in use) and §4 (Caddy, `make-admin`, invite, two-step, the channel test email's `/n/…` links, timing under 30 minutes, 3 commands) on a real server with locally built images. Record the results in the PR description.

**Checkpoint**: the stack starts from one settings file. The required settings are enforced, and the admin flow works.

---

## Phase 4: User Story 2 - Safe to leave running on the internet (Priority: P1)

**Goal**: the operator's proxy meets every rule of the 005 deployment contract, and they can prove it with commands.

**Independent Test**: with the stack and the example Caddy running, run the `DEPLOY.md` checks from another machine: port scan, HTTP redirect, HSTS, `/api` 404 from Caddy, `/n/…` from the API, spoofed `X-Forwarded-For`, and two visitors (quickstart §5).

The exposure itself (loopback ports, no db port, trusted proxies, empty required settings) is already in T008 and T009.

### Implementation for User Story 2

- [ ] T014 [US2] Fill `DEPLOY.md` § Reverse proxy (FR-017, FR-018, research R12).
  - **The rule list, one bullet each**. The operator's proxy must:
    - terminate TLS and redirect HTTP to HTTPS;
    - send `Strict-Transport-Security: max-age=31536000`;
    - route `/n/…` to the API (`127.0.0.1:${API_PORT}`), answer `404` for `/api` and `/api/…`, and send everything else to the web interface (`127.0.0.1:${WEB_PORT}`);
    - replace any client-sent `X-Forwarded-For` with the real client address;
    - pass the `Host` header through;
    - not strip the app's security headers;
    - not log query strings of `/account/…` requests.
  - **The worked Caddyfile**, verbatim from research R12:

    ```caddyfile
    intheknow.example.nl {
    	header Strict-Transport-Security "max-age=31536000"
    	@api path /api /api/*
    	handle @api {
    		respond 404
    	}
    	handle /n/* {
    		reverse_proxy 127.0.0.1:5000
    	}
    	handle {
    		reverse_proxy 127.0.0.1:3000
    	}
    }
    ```

  - Then one line per rule saying how Caddy meets it (from R12). This includes why `respond` sits inside `handle`.
  - Caddy writes no access log without a `log` directive. If the operator enables one, give a `log` block with a `format filter` that deletes the `request>uri` query, for example:

    ```caddyfile
    log {
    	format filter {
    		request>uri query {
    			delete *
    		}
    	}
    }
    ```

    Note that this needs verifying with `caddy adapt` (T016).
  - **Checks**: the curl/nmap block, the spoofed-address check and the two-visitor check from quickstart §5, each with its expected result. Mark which run from another machine and which on the server.
  - Other proxies: meet the rule list. nginx users must set `X-Forwarded-For $remote_addr`, **not** `$proxy_add_x_forwarded_for` (R12).
- [ ] T015 [US2] Add the R7 trust explanation to `DEPLOY.md` § Reverse proxy, under a short "Trusted addresses" subheading.
  - The API trusts forwarded addresses only from `STACK_SUBNET` and loopback.
  - The peer-address check from quickstart §5 verifies this per runtime.
  - Fallback if a runtime shows another peer: add it as `InTheKnow__Accounts__TrustedProxies__4` in `.env` (index 3 is reserved for the stack's mapped-subnet fallback, T003).
  - Docker's published-port firewall rules bypass `ufw`, which is why the stack binds to `127.0.0.1` rather than relying on a firewall.
- [ ] T016 [US2] If `caddy` is installed, put the T014 Caddyfile (with the `log` block) in a scratch file and run `caddy adapt --config <file> --pretty`.
  - The `/api` route must come before the catch-all.
  - The `log` filter must be accepted.
  - Fix `DEPLOY.md` if not. Otherwise leave this check to T017.
- [ ] T017 [US2] Manual (maintainer): run quickstart §5 in full on the real server, for every runtime the maintainer supports, including the routed-subnet check from a LAN neighbour. Record the peer address each runtime shows, and whether the routed-subnet check passed. Apply the R7 fallbacks in `DEPLOY.md` (trusted peer; firewall rule for rootful Podman) where a runtime fails.

**Checkpoint**: US1 + US2 = MVP. The stack is reachable only through a proxy that passes every check.

---

## Phase 5: User Story 3 - Keep it running and upgrade it (Priority: P2)

**Goal**: crashes, reboots, a slow database and upgrades need no manual action beyond the documented steps.

**Independent Test**: with data in the database, reboot the host and see the app come back. Then upgrade to a newer published version and check all data is kept (quickstart §7, §9.3).

Restart policies, healthchecks and `depends_on` are already in T008.

### Implementation for User Story 3

- [ ] T018 [US3] Fill `DEPLOY.md` § Upgrade and rollback (FR-026, FR-028, research R13). The steps:
  1. Back up (link § Backup and restore).
  2. Keep the current `compose.yaml` (for example `mv compose.yaml compose-X.Y.Z.yaml`).
  3. Download the new release's `compose.yaml`.
  4. Add any new settings its release notes list to `.env`.
  5. `docker compose pull`. If this fails, the old version keeps running.
  6. `docker compose up -d`. The API migrates at startup.

  Then state:
  - migrations only go forward;
  - rolling back means restoring the pre-upgrade backup (fresh-volume restore) and starting the previous `compose.yaml`;
  - if the API fails mid-migration, it does not start, and the way back is that backup;
  - scheduling backups and copying them off the host are the operator's job.
- [ ] T019 [US3] Fill `DEPLOY.md` § Podman (FR-008, FR-019, research R14).
  - `podman compose` works with either provider (`docker-compose` v2 or `podman-compose`).
  - **The four required provider features**: `${VAR:?}`, top-level `name:`, `ipam` subnets, and `depends_on: condition: service_healthy`.
  - The minimum `podman-compose` version: write `TODO: fill in after quickstart §8 (T020)`. **Do not invent a version number.**
  - The one-time host step for reboot recovery:
    - rootful: `sudo systemctl enable --now podman-restart.service`;
    - rootless: `systemctl --user enable --now podman-restart.service` plus `sudo loginctl enable-linger $USER`.
  - Docker needs no extra step.
- [ ] T020 [US3] Manual (maintainer):
  - Run quickstart §7: kill, scale to 2, db stopped while the API starts, reboot under 2 minutes.
  - Run quickstart §8: the other runtime, rootless and rootful.
  - Record the `podman compose` provider and version, and replace the T019 TODO with it.
  - Apply any R7, R8 or R14 fallback to `deploy/compose.yaml` or `DEPLOY.md`.
  - The upgrade timing (§9.3) runs in T026.

**Checkpoint**: the stack recovers on its own, and the upgrade path is documented.

---

## Phase 6: User Story 4 - Published images for every release (Priority: P2)

**Goal**: a version tag publishes both multi-arch images and a pinned `compose.yaml`, after the checks pass. Nothing else publishes. The maintainer can push by hand to any registry.

**Independent Test**: push `v0.1.0`, see the checks run and both images and the release assets appear, then start that release's stack on a clean machine (quickstart §9).

### Implementation for User Story 4

- [ ] T021 [P] [US4] Add `workflow_call:` to the `on:` block of `.github/workflows/ci.yml` (research R11).
  - Leave everything else as is, including `permissions: contents: read` and the `ci-${{ github.ref }}` concurrency group.
  - For a tag, `github.ref` is `refs/tags/vX.Y.Z`, so it never collides with a branch run.
- [ ] T022 [US4] Create `.github/workflows/release.yml` (research R11, contracts/images.md, FR-002 to FR-004b).
  - `name: Release`.
  - `on: push: tags: ['v[0-9]+.[0-9]+.[0-9]+']` only. **No `release:` trigger, and no `concurrency` group** (a group named like `ci.yml`'s would deadlock the called workflow).
  - Top-level `permissions: contents: read`.
  - `jobs.checks: uses: ./.github/workflows/ci.yml`, with `permissions: contents: read`. A called workflow can't get more than its caller grants.
  - `jobs.publish`:
    - `needs: checks`, `runs-on: ubuntu-latest`;
    - `permissions: contents: write, packages: write`;
    - `env: GH_TOKEN: ${{ github.token }}` (for `gh`).
  - Steps:
    1. `actions/checkout@v5` (the same version as `ci.yml`).
    2. `VERSION="${GITHUB_REF_NAME#v}"`. Export it through `$GITHUB_ENV`, or recompute it per step.
    3. `echo "${{ github.token }}" | docker login ghcr.io -u ${{ github.actor }} --password-stdin`.
    4. `docker buildx create --use`.
    5. For `api` (`backend/Dockerfile`) and `web` (`frontend/Dockerfile`):
       `docker buildx build --platform linux/amd64,linux/arm64 -f <dir>/Dockerfile --label org.opencontainers.image.source=https://github.com/Wjouter/InTheKnow -t ghcr.io/wjouter/intheknow-<svc>:$VERSION --push .`
    6. `IMAGE=ghcr.io/wjouter/intheknow`, then the R10 render verbatim: `sed -e "s|ghcr.io/wjouter/intheknow|$IMAGE|" -e "s|:VERSION|:$VERSION|" deploy/compose.yaml > compose.yaml`. Then `grep -q ":$VERSION" compose.yaml` and `! grep -q ':VERSION' compose.yaml`.
    7. `gh release view "$GITHUB_REF_NAME" || gh release create "$GITHUB_REF_NAME" --verify-tag --generate-notes`.
    8. `gh release upload "$GITHUB_REF_NAME" compose.yaml deploy/env.example --clobber`.
  - **Hardcode the image as lowercase `ghcr.io/wjouter/…`**. Never derive it from `github.repository_owner` (`Wjouter`), which GHCR rejects.
  - No marketplace actions besides `actions/checkout`.
- [ ] T023 [US4] Fill `DEPLOY.md` § Building and publishing images (FR-004, FR-004a, FR-004b, contracts/images.md).
  - **Local build** (no registry): the two `docker build` lines with `IMAGE` and `VERSION` as inputs.
  - **Manual push to any registry**: `docker login <registry>` (the maintainer's own sign-in, never stored in the repository), then the two `docker push` lines.
    - A manual push holds only the builder's processor type.
  - **Render a stack definition** for those images: the R10 `sed` line with `IMAGE` and `VERSION`.
  - **Warning**: pushing an existing version replaces it, so bump the version instead.
  - **Automatic releases**: push a `vX.Y.Z` tag, or publish a GitHub release that creates one. A release for an existing tag builds nothing.
  - **The one-time step after the first release**: make both GHCR packages public, under the package settings → visibility.
  - Note that every command works with `podman`.
- [ ] T024 [US4] Validate locally.
  - Read `release.yml` against `ci.yml` once more: the trigger, `needs`, permissions, and no `concurrency`.
  - If `actionlint` is installed, run it on both workflows.
  - Run the T022 `sed` render with `VERSION=1.2.3` into a scratch file. Check both image lines read `ghcr.io/wjouter/intheknow-{api,web}:1.2.3`, and no `:VERSION` remains.
  - **Do not push any tag.**
- [ ] T025 [US4] Manual (maintainer): run quickstart §9 steps 1, 2, 5, 6 and 7.
  - Step 1: tag `v0.1.0`, then make the packages public, then do the anonymous multi-arch pull.
  - Step 2: a GitHub-UI release with a new tag, then a release for an existing tag (no run).
  - Step 5: a failing check publishes nothing.
  - Step 6: a branch push or PR builds no image.
  - Step 7: a manual publish to a non-GitHub registry.
  - Record the time from tag to images (SC-008, under 30 minutes).
- [ ] T026 [US3] Manual (maintainer), after T025: run quickstart §9 steps 3 and 4.
  - Step 3: the upgrade from `v0.1.0` to `v0.1.1` with data. Downtime must be under 2 minutes, all data must be kept, and `.env` must be unchanged.
  - Step 4: the registry is unreachable during `pull`, and the old version keeps serving.

**Checkpoint**: releases publish themselves, and manual publishing works with any registry.

---

## Phase 7: User Story 5 - Back up and restore the data (Priority: P2)

**Goal**: one command makes a full backup while the app runs. A documented restore brings it back onto a fresh stack.

**Independent Test**: back up a stack with data, `down -v`, restore onto the empty volume, sign in and see the calendars and changes (quickstart §6).

### Implementation for User Story 5

- [ ] T027 [US5] Fill `DEPLOY.md` § Backup and restore (FR-027, FR-028, research R9).
  - **Backup**, while running: `docker compose exec -T db pg_dump -U intheknow -Fc intheknow > intheknow-$(date +%F).dump`.
    - The file holds every account and token hash, so store it like the database.
  - **Restore** (fresh volume only):
    1. **`docker compose down -v` deletes all current data.** Put this warning in bold.
    2. `docker compose up -d db`, then wait for `healthy` in `docker compose ps`.
    3. `docker compose exec -T db pg_restore -U intheknow -d intheknow --no-owner --exit-on-error < file.dump`.
    4. `docker compose up -d`.
  - Explain why only `db` starts first: the API would migrate and seed the empty database, and the restore would collide with those rows.
  - Restoring over existing data is not supported.
  - Scheduling backups and copying them off the host are the operator's job.
- [ ] T028 [US5] Back up and restore locally with the T010 scratch stack (runtime required, otherwise skip with a note).
  - Create one account with `make-admin` and one row of data.
  - Run the T027 commands.
  - Check the row is back with `docker compose exec db psql -U intheknow -c "SELECT count(*) FROM account"`. Use the actual table name from `backend/InTheKnow/Migrations/`.
  - Check `api` restarts without applying a migration.
  - Then `down -v`.
- [ ] T029 [US5] Manual (maintainer): run quickstart §6 on the real server, ending with a full sign-in that shows calendars and changes (SC-006).

**Checkpoint**: backups and restore work as documented.

---

## Phase 8: User Story 6 - Local development is unchanged (Priority: P3)

**Goal**: QUICKSTART and the dev `compose.yaml` work exactly as before.

**Independent Test**: on a clean checkout, follow QUICKSTART and see it work, with no settings file (quickstart §10).

### Implementation for User Story 6

- [ ] T030 [US6] Verify:
  - `git diff main -- compose.yaml QUICKSTART.md` is empty;
  - `git status --porcelain` shows no `.env` (an ignored file would not show, so also `git check-ignore deploy/.env` must print the path);
  - README's `#behind-a-reverse-proxy` anchor still exists (`grep -n '^### Behind a reverse proxy$' README.md`).

  If a runtime is installed:
  - `docker compose up -d db mailpit` from the root starts only those two;
  - `dotnet test` passes against it;
  - `docker compose down`.
- [ ] T031 [US6] Manual (maintainer): run quickstart §10, including the dev stack and the `~/itk` stack side by side with `API_PORT=5001`.

**Checkpoint**: development is unaffected.

---

## Phase 9: Polish & Cross-Cutting Concerns

- [ ] T032 Fill `DEPLOY.md` § Troubleshooting. Write one entry per spec edge case, each with its symptom and fix:
  - `PUBLIC_URL` with a trailing slash, a path or `http://`: the API restarts and logs the `…PublicBaseUrl` key (see § Settings).
  - Proxy missing or misrouted: the stack is healthy, and the § Reverse proxy checks show which rule fails.
  - Host port in use: the error names it; set `WEB_PORT`/`API_PORT`.
  - `Pool overlaps with other one on this address space`: set `STACK_SUBNET`.
  - SMTP rejects the credentials: `invite queued` still prints, and `docker compose logs api` shows the delivery failure.
  - `make-admin` says the schema is behind: wait for `healthy`.
  - Upgrade failed mid-migration: restore the pre-upgrade backup (§ Upgrade and rollback).
  - Registry unreachable: `pull` fails, and the old version keeps running.
  - A new release adds a setting: see its release notes. A required one is named at startup.
  - Disk full: PostgreSQL stops writing, and existing data is safe. Monitoring disk space is the operator's job.
  - Wrong host clock: enable time sync. Two-step codes depend on it.

  Then remove every remaining `TODO (Txxx)` stub except the T019 Podman version, which T020 fills.
- [ ] T033 Run every automated check: `dotnet test`, and `cd frontend && npm run check && npm test && npm run build` (quickstart §1). All must pass.
- [ ] T034 Secret and scope sweep.
  - `grep -nE '^(PUBLIC_URL|POSTGRES_PASSWORD|SMTP_HOST|SMTP_FROM|SMTP_PASSWORD)=.' deploy/env.example` prints nothing (no uncommented line holds a value).
  - No committed file holds a real password, token or the private links file.
  - The file list matches plan.md's post-design re-check: 7 new files, plus edits to `Settings.cs`, `SettingsTests.cs`, `SignInTests.cs`, `ci.yml` and `README.md`. Nothing else.
- [ ] T035 Read `DEPLOY.md` top to bottom against FR-029's list and contracts/stack.md's command table.
  - Every command must match the contracts exactly: service names, `-T` flags, the `make-admin` form.
  - Every section must be filled.
  - Fix any mismatch.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies.
- **Foundational (Phase 2)**: depends on T001 (the image builds use `.dockerignore`). It blocks all stories.
  - T002 and T003 come before T004.
  - T005 and T006 come before T007.
- **US1 (Phase 3)**: after Phase 2. It creates `DEPLOY.md` (T011), which every later `DEPLOY.md` task fills.
- **US2 (Phase 4)**: after T011 (it fills `DEPLOY.md`). Its exposure config is in T008.
- **US3 (Phase 5)**: after T011. T026 needs T025 (published releases).
- **US4 (Phase 6)**: T021 and T022 need only Phase 2. T023 needs T011.
- **US5 (Phase 7)**: after T011. T028 needs T010's scratch stack.
- **US6 (Phase 8)**: after all file changes (it checks nothing else changed).
- **Polish (Phase 9)**: after all stories.

### User Story Dependencies

- **US1 (P1)**: Foundational only.
- **US2 (P1)**: US1's `deploy/compose.yaml` and `DEPLOY.md` skeleton.
- **US3 (P2)**: US1. Its upgrade validation (T026) needs US4's published releases.
- **US4 (P2)**: Foundational for the workflow. US1's `DEPLOY.md` for its section.
- **US5 (P2)**: US1.
- **US6 (P3)**: independent, but run it last as a regression check.

### Within Each Phase

- Tests (T002, T003) before the `Settings.cs` change (T004).
- File creation before its local check (T008, T009 → T010; T022 → T024; T027 → T028).
- `DEPLOY.md` tasks run one at a time, in task order: T011 → T014 → T015 → T018 → T019 → T023 → T027 → T032 → T035.
- Manual tasks come last in each phase and never block the next phase's file work.

### Parallel Opportunities

- T002 ∥ T003 (different test files).
- T005 ∥ T006 (different Dockerfiles), and both can run alongside T002 to T004.
- T008 ∥ T009 (different files in `deploy/`).
- T021 can start any time after Phase 2, alongside US1 to US3.

---

## Parallel Example: Phase 2 and User Story 1

```bash
# Phase 2: tests and images together
Task: "Add origin-only cases in backend/InTheKnow.Tests/SettingsTests.cs"
Task: "Add the IPv4-mapped trusted peer case in backend/InTheKnow.Tests/SignInTests.cs"
Task: "Create backend/Dockerfile"
Task: "Create frontend/Dockerfile"

# US1: the two deploy files together
Task: "Create deploy/compose.yaml per contracts/stack.md"
Task: "Create deploy/env.example per contracts/settings.md"
```

---

## Implementation Strategy

### MVP First (US1 + US2)

1. Phase 1 and Phase 2: the images build, and the origin-only rule is tested.
2. Phase 3 (US1): the stack starts from one settings file, and the first admin works.
3. Phase 4 (US2): the proxy section and its checks.
4. **STOP and VALIDATE**: quickstart §3 to §5 on a real server with locally built images (T013, T017).

### Incremental Delivery

1. MVP (US1 + US2): deployable from local images.
2. US4: tag-driven publishing. Operators can now download instead of build.
3. US3 + US5: upgrade, rollback, Podman and backups documented and validated against real releases.
4. US6 and Polish: regression check and the troubleshooting section.

### Manual work for the maintainer

The implement step leaves T013, T017, T020, T025, T026, T029 and T031 unchecked and lists them. They need a real server, a second runtime, an arm64 machine, a non-GitHub registry account, and the right to push tags and publish releases.

---

## Notes

- [P] means a different file and no dependency on an unfinished task.
- Commit after each phase. The constitution gate is `dotnet test` plus the frontend checks (T033).
- Never put a working value in `deploy/env.example`'s required lines (T009).
- Never use `COPY . .` or a `RUN` in a runtime stage (T005, T006).
