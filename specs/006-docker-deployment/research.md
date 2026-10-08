# Research: Production Container Stack

Phase 0 of [plan.md](./plan.md). Each entry gives the decision, why it was chosen and what else was considered. The
spec left no open questions: its two clarification sessions decided the proxy, the registry, backups, upgrades,
Podman reboot recovery, manual publishing and the CI triggers. The spec left the base images, the registry, the CI
setup and the worked proxy example to this plan; R2, R3, R9 and R11 decide them.

Entries marked **Verify** rest on runtime or platform behavior. Each names the [quickstart](./quickstart.md) step or
test that proves it, and the fallback if it does not hold.

## R1. File layout

**Decision**:
- `backend/Dockerfile` and `frontend/Dockerfile`. Both are built with the **repository root** as context, because
  `Directory.Build.props` (target framework, warnings as errors, lockfiles) lives there.
- `.dockerignore` at the root (R4).
- `deploy/compose.yaml`: the production stack definition, a template with the image version as a placeholder (R10).
- `deploy/env.example`: the example settings file (R6).
- `DEPLOY.md` at the root, next to `QUICKSTART.md`: the deployment document (FR-029).
- `.github/workflows/release.yml`: the tag-only release workflow (R11).

**Rationale**:
- Root-level files are where operators look: the README already links `QUICKSTART.md`.
- `deploy/` keeps the production files away from the dev `compose.yaml`. The dev file and the production file never
  share a directory, a project name or a settings file (FR-030, R7).

**Alternatives considered**:
- A `compose.prod.yaml` next to `compose.yaml`: a root `.env` would then sit next to the dev compose file too, and
  `docker compose` with no `-f` would still pick the dev file. That is easy to mix up.
- Building with `backend/` as context: the build would lose `Directory.Build.props`. Copying it into `backend/`
  duplicates it.

## R2. API image (FR-001, FR-005)

**Decision**:
- Build stage: `mcr.microsoft.com/dotnet/sdk:10.0` on `--platform=$BUILDPLATFORM`.
  - `COPY` only `Directory.Build.props`, `global.json` and `backend/InTheKnow/`. Tests and fixtures are never copied.
  - `dotnet restore --locked-mode`, then `dotnet publish -c Release --no-restore -p:UseAppHost=false -o /app`.
  - There is **no runtime identifier**. The output is portable IL and runs on both processor types.
- Runtime stage: `mcr.microsoft.com/dotnet/aspnet:10.0` (Ubuntu based, not chiseled).
  - `COPY --from=build /app`, `USER $APP_UID` (the image's non-root `app` user), `WORKDIR /app`.
  - `ENTRYPOINT ["dotnet", "InTheKnow.dll"]`.
  - `make-admin` runs as `dotnet InTheKnow.dll make-admin …` in the same container (R13).
- The stack sets `ASPNETCORE_HTTP_PORTS=8080` explicitly instead of relying on the image default.

**Rationale**:
- **Verify**: the lockfile (`packages.lock.json`) has no runtime section. A runtime-specific restore (`-r` or `-a`) in
  locked mode would fail. The packages (Npgsql, Ical.Net, Otp.NET) are fully managed, so a runtime-less publish is
  correct.
  - Proof: the image build itself, and the arm64 image starting (quickstart §2, §9).
- Building on the build machine's own processor type and copying portable output means no emulation in CI. That is
  fast, and there is nothing to run under QEMU.
- The non-chiseled image has `bash`, which the healthcheck uses (R8). It also has ICU and `tzdata`, which
  `DefaultTimeZone` needs.
  - **Verify**: the API fails at startup if `Europe/Amsterdam` cannot be resolved, so the first healthy start proves
    it.
- Dev tools and test code are absent: the runtime stage holds only the published output (quickstart §2 lists the
  files).

**Alternatives considered**:
- `dotnet publish /t:PublishContainer` (SDK container build):
  - It needs no Dockerfile, but it is a second build style next to the frontend's Dockerfile.
  - Its multi-arch output is per runtime identifier, which brings back the lockfile problem.
  - It can't be built by plain `docker build`/`podman build` (FR-004).
- A chiseled runtime image: it is smaller, but has no shell for the healthcheck. A health endpoint plus a .NET
  probe command would be app code that exists only for the container.
- A self-contained or trimmed publish: this needs a runtime identifier (lockfile problem) and buys nothing, since
  the runtime image already has .NET.

## R3. Web image (FR-001, FR-005)

**Decision**:
- Build stage: `docker.io/library/node:24-alpine` on `--platform=$BUILDPLATFORM`.
  - Copy `frontend/package.json` and `package-lock.json`, then run `npm ci`.
  - Copy `frontend/` and run `npm run build`.
  - Then `npm ci --omit=dev` into a clean directory for the runtime `node_modules`.
- Runtime stage: `docker.io/library/node:24-alpine`.
  - Copy `build/`, the production `node_modules` and `package.json`.
  - `USER node`, `CMD ["node", "build"]`.
  - `NODE_ENV=production`.
- adapter-node already shuts down cleanly on `SIGTERM`, so no init process is added.

**Rationale**:
- The production dependencies (`@fontsource/*`, `@lucide/svelte`, `uqr`) are pure JavaScript and CSS. A
  `node_modules` installed on the build machine runs on any processor, so again no emulation is needed.
- The frontend reads its settings only through `$env/dynamic/private` (checked: `src/lib/server/api.ts` is the only
  `$env` import). Nothing operator-specific is baked into the shared image.
- Alpine keeps the image small. Node's official builds include full ICU, so `Intl` date formatting is unchanged.

**Alternatives considered**:
- Bundling every dependency into `build/` (`ssr.noExternal`) to drop `node_modules`: this changes the Vite config for
  a few MB.
- `node:24-slim`: it works the same but is larger. There is no native module that needs glibc.

## R4. Keeping secrets and private data out of images (FR-005, Principle II)

**Decision**: two layers.
1. **Allowlist `COPY`s** (R2, R3). The Dockerfiles copy named paths only, never `COPY . .`.
2. **A root `.dockerignore`** that excludes, at minimum:
   - `.git`
   - `**/bin`, `**/obj`, `**/node_modules`, `frontend/build`, `frontend/.svelte-kit`
   - `.env`, `.env.*`
   - `mytimetable example links.md`, `backend/InTheKnow.Tests/`, `.idea`, `.claude`, `.junie`, `specs`

**Rationale**:
- The build context is the repository root (R1). The root holds the private links file with live tokens (memory:
  *example links file is private*), any developer `.env`, and `backend/InTheKnow.Tests/Fixtures/private/`.
- The allowlist alone keeps them out of the image. The ignore file also keeps them out of the build context sent to
  the builder, and keeps host `bin/obj/node_modules` from overwriting the clean restore.
- Proof: quickstart §2 lists every file in both images and checks that none of these paths appear.

**Alternatives considered**: the ignore file alone. One forgotten pattern would ship a secret. The allowlist alone
still uploads the private files to the builder on every build.

## R5. Public address validation (FR-010, FR-014, edge case "trailing slash, path or http://")

**Decision**:
- `Settings.BaseUrl` (backend) accepts an **origin only**:
  - an absolute `https` URL (`http` still only for `localhost`);
  - no path (`AbsolutePath == "/"` and no trailing `/` in the string);
  - no query, no fragment, no user info.
- This is the one app code change in this feature, with new cases in `SettingsTests`:
  - rejected: a trailing `/`, a path, a query, `http://` on a public host;
  - accepted: the QUICKSTART values `http://localhost:5000` and `http://localhost:5173`, and `https://host:8443`.
- The stack passes `PUBLIC_URL` to both `InTheKnow__Notifications__PublicBaseUrl` and `…FrontendBaseUrl`, and as
  `ORIGIN` to the web interface.
- The API validates these only when `Email:Host` is set. The stack makes `SMTP_HOST` required (R6), so the check
  always runs in production.

**Rationale**:
- The API joins links as `{base}/path` (`Accounts.cs`, `Notifier.cs`), so `https://x.nl/` produces `//account/…` and
  a path base produces links the proxy doesn't route. Today `BaseUrl` accepts both, so a bad address would be used
  silently.
- The API is the one component that already validates these values at startup and fails with a message naming the
  key (001 FR-014 behavior). Putting the check there also protects non-container installs.
- **Which name the operator sees** (US1 AS5):
  - An *empty* `PUBLIC_URL` is caught by the stack before anything starts, with a message naming `PUBLIC_URL` (R6).
  - A *malformed* one stops the API with `InTheKnow:Notifications:PublicBaseUrl must be …`.
  - `DEPLOY.md`'s settings table and its troubleshooting section map those two key names to `PUBLIC_URL`
    ([contracts/settings.md](./contracts/settings.md)). The web container still starts, but the stack is not healthy,
    because the API is down.

**Alternatives considered**:
- Validating in a container entrypoint script: that is a second validator in shell, and it only protects the
  container path.
- Stripping a trailing slash silently: the edge case requires failing, not guessing.
- Making the API message mention `PUBLIC_URL`: the API doesn't know about the stack. The mapping belongs in the
  deployment document.

## R6. Settings file and how settings reach the services (FR-009 to FR-013, SC-002)

**Decision**:
- The settings file is **`.env` next to `compose.yaml`**, copied from `env.example`. Compose reads it for
  interpolation without flags, and the root `.gitignore` already ignores `.env`.
- The example is named `env.example`, without a leading dot. GitHub renames release assets whose names start with a
  dot, which would break the documented download URL. The same name in the repository keeps one name everywhere.
- The required settings are **empty** in `env.example`, with example values only in comments. `${X:?}` fires only
  on unset or empty values, so a placeholder such as `change-me` would pass the check and become a working default
  password (US2 AS5, FR-012).
- **Required** (4, SC-002 allows 6), each written as `${NAME:?NAME is required, see DEPLOY.md}` in the stack
  definition, so `up` refuses to start and names the setting:
  - `PUBLIC_URL`
  - `POSTGRES_PASSWORD`
  - `SMTP_HOST`
  - `SMTP_FROM`
- **Optional, named**: `SMTP_PORT` (587), `SMTP_USERNAME`, `SMTP_PASSWORD` (empty), `WEB_PORT` (3000), `API_PORT`
  (5000), `STACK_SUBNET` (R7).
- **Every other API setting** of 001 to 005 (FR-013) goes in the same file under its own environment name, for
  example `InTheKnow__CheckInterval=00:10:00`. The API service has `env_file: .env`, so these pass through with
  their existing defaults. The web service does **not** get the file. It has no secrets (005 contract) and gets only
  its five variables.
- **Stack-owned wiring** (FR-011) is in the API's `environment:` block, which takes precedence over `env_file`:
  - `ConnectionStrings__Default`
  - `PGPASSWORD`
  - the trusted proxies
  - `ASPNETCORE_HTTP_PORTS`
  - the two base URLs, and the SMTP keys mapped from the named settings

  An operator who sets one of these in `.env` by accident does not break the wiring.
- **The database password stays out of the connection string.** The connection string is
  `Host=db;Username=intheknow;Database=intheknow`, and the password comes from `PGPASSWORD`, which Npgsql reads from
  the environment when the connection string has none.
  - **Verify**: the instance lock opens its own unpooled connection from the same connection string, so the first
    healthy start proves both the pool and the lock connection authenticate (quickstart §3).
  - Fallback: put `Password=${POSTGRES_PASSWORD}` in the connection string and restrict the password to
    `[A-Za-z0-9]` in the document.
- **Quoting**: Compose interpolates `$` inside `.env` values, including in files read through `env_file`. The example
  file tells operators to wrap values with `$`, `#` or spaces in **single quotes**. It suggests
  `openssl rand -hex 32` for the database password, which needs no quoting.
  - **Verify**: quickstart §3 starts with an SMTP password containing `$` and `#` in single quotes, on both
    `docker compose` and `podman-compose`.

**Rationale**:
- One file, read by Compose itself: there is no wrapper script and no second config format.
- Named settings exist only for what the stack must check or combine. Everything else uses the key names the
  existing configuration contracts already document, so there is one name per setting and nothing to keep in sync.
- With `PGPASSWORD`, `;`, `=` and quotes in the password cannot break the connection string.

**Alternatives considered**:
- Named aliases for every API setting: about 30 more lines to keep in sync with five contracts, and they add nothing.
- Raw key names for the required settings too: the `:?` check would read
  `${InTheKnow__Notifications__Email__Host:?…}`, and `PUBLIC_URL` has to feed three keys anyway.
- Docker/Podman secrets for the password: they are a second mechanism, Compose support differs between runtimes, and
  the threat model is the same (a root-readable file on the host). The document says to `chmod 600 .env`.

## R7. Network, published ports and trusted proxies (FR-015, FR-016, SC-003, SC-007)

**Decision**:
- One user-defined network `stack` with a **fixed subnet**, `ipam.config: - subnet: ${STACK_SUBNET:-172.30.250.0/24}`.
- Published ports:
  - `web`: `127.0.0.1:${WEB_PORT:-3000}:3000`
  - `api`: `127.0.0.1:${API_PORT:-5000}:8080`
  - `db`: none.
- API: `InTheKnow__Accounts__TrustedProxies__0=${STACK_SUBNET:-172.30.250.0/24}`, `__1=127.0.0.1`, `__2="::1"`
  (quoted, because podman-compose parses YAML 1.1).
- Web: `ADDRESS_HEADER=X-Forwarded-For`, `XFF_DEPTH=1`, `HOST=0.0.0.0` (inside its container), `PORT=3000`.
- The top-level project name is fixed as **`name: intheknow-prod`**, so the network is `intheknow-prod_stack` and the
  volume is `intheknow-prod_db`.

**Rationale**:
- **FR-016 says "only from the host's proxy and from inside the stack."** Each runtime shows a host proxy's
  connection through a loopback-published port to the container with one of two peer addresses:
  - the network's **gateway** address, which is inside the stack subnet. That is Docker's userland proxy, Docker's
    NAT path, rootful Podman (netavark masquerades loopback-DNAT), and rootless Podman (`rootlessport` connects from
    inside the rootless network namespace);
  - or loopback.

  Trusting exactly the stack subnet plus loopback is therefore the literal FR-016 set, and nothing wider.
  - **Verify** per runtime (Docker, rootful Podman, rootless Podman): quickstart §5 reads the peer address the API
    records for a request sent through the published port, and runs the spoofed-header and two-visitor throttle
    checks.
  - Fallback if a runtime shows a different peer: add that address in `.env` as `InTheKnow__Accounts__TrustedProxies__4`
    and document it for that runtime.
- **The subnet has to be fixed** so it can be written into the trusted proxy list. Compose can't pass an
  auto-assigned subnet into an environment variable.
  - The default `172.30.250.0/24` sits outside Podman's default pool (`10.89.0.0/16`). It overlaps Docker's default
    pool only if Docker has handed out its 14th network as a /16. In that case `up` fails with "pool overlaps", and
    the operator sets `STACK_SUBNET`. `DEPLOY.md` covers that.
- **IPv4-mapped peers.** Kestrel listens dual-stack, so an IPv4 peer can arrive as `::ffff:172.30.250.1`. ASP.NET
  Core's `ForwardedHeadersMiddleware` maps IPv4-mapped peers to IPv4 before matching known networks.
  - **Verify** with a test, not memory: a new `SignInTests` case sends `X-Test-Source: ::ffff:192.0.2.1` with
    `TrustedProxies:0 = 192.0.2.0/24` and expects the forwarded address to be recorded.
  - Fallback: add the mapped form to the stack's list as `TrustedProxies__3: ${STACK_SUBNET_MAPPED:-"::ffff:172.30.250.0/120"}`.
    Compose can't derive it from `STACK_SUBNET`, so an operator who changes one changes both (tasks T003).
- **The path of a visitor's address**:
  1. The proxy replaces `X-Forwarded-For` with the client address.
  2. The web interface takes the rightmost entry (`XFF_DEPTH=1`) as `getClientAddress()`, and sends it to the API as
     `X-Forwarded-For` (`src/lib/server/api.ts`).
  3. The API trusts it, because the peer (the web container) is in the subnet.

  For `/n/…`, the proxy's request reaches the API directly through the published port, with the gateway as the
  trusted peer.
- **Loopback only**: nothing off the host can open a stack port (SC-003). Docker's firewall rules for published
  ports bypass `ufw`, so binding to loopback is the safe default rather than a firewall.
  - **Verify**: Docker Engine 28's release notes say it blocks packets from other LAN hosts addressed to
    loopback-published ports and to container addresses directly. Older engines, including many distribution
    `docker.io` packages, may not. `DEPLOY.md` therefore **requires** 28 or later (FR-015). Rootful Podman
    (netavark) is unverified: quickstart §5's routed-subnet check tests both runtimes. If rootful Podman fails
    it, `DEPLOY.md` § Podman gives a firewall rule that drops forwarded traffic to `STACK_SUBNET` from
    off-host addresses.
- **A fixed project name** keeps the volume when the directory is renamed or moved. It differs from the dev stack's
  project (`intheknow`, from the repository directory), so a developer can run both on one machine without sharing
  the `db` volume.

**Alternatives considered**:
- Trusting all private ranges (`10/8`, `172.16/12`, `192.168/16`): this avoids a fixed subnet, but trusts addresses
  outside the stack, and so goes beyond FR-016. On a LAN where another host can route to container addresses, that
  host could spoof `X-Forwarded-For`.
- `network_mode: host`: no published ports to manage, but it exposes the database on the host's interfaces unless
  each service binds to loopback by its own config, and Docker Desktop and rootless Podman don't support it.
- A Unix socket between the proxy and the stack: not supported uniformly by Kestrel, adapter-node and the proxy in
  the worked example.

## R8. Health, start order and restarts (FR-019 to FR-022, US3)

**Decision**:
- **Healthchecks live in the stack definition**, not in the Dockerfiles:
  - `db`: `pg_isready -U intheknow -d intheknow`.
  - `api`: `bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080'` (the port accepts connections).
  - `web`: `node -e` opening a TCP connection to `127.0.0.1:3000`.
  - Each has `interval: 10s`, `timeout: 5s`, `retries: 6` and a `start_period` (30 s for the API, for migrations).
- **Start order**: `api` has `depends_on: db: condition: service_healthy`. `web` has no `depends_on` the API: it
  starts on its own and shows its existing "couldn't reach the server" handling until the API answers (FR-020).
- `restart: always` on all three services.
- **Exactly one API instance**: the existing advisory lock (001 R4), plus the fixed host port (a second `api`
  replica can't bind `127.0.0.1:5000`). There is no `deploy.replicas` and no scale setting.

**Rationale**:
- **What "healthy" means**:
  - The API opens its port only after `Settings.Load`, the instance lock, migrations and regrouping
    (`Program.cs`: `app.Run()` comes after `MigrateAsync`), so "the port accepts connections" means "configured,
    migrated and serving".
  - A crashed process exits, and the restart policy brings it back.
  - A health endpoint would add app code to report the same thing.
- **Healthchecks in compose**: Podman ignores a Dockerfile `HEALTHCHECK` when it builds or pulls OCI-format images.
  The compose `healthcheck:` works on both runtimes.
- **The API retries for free** (US3 AS2). `depends_on` covers the normal start. After a reboot, or when a runtime
  ignores `depends_on`:
  1. The API fails to connect.
  2. It exits non-zero.
  3. `restart: always` restarts it with backoff until PostgreSQL answers.

  There is no retry loop in app code.
- **`always`, not `unless-stopped`**:
  - **Verify**: Podman's `podman-restart.service` starts containers whose restart policy is `always`. Whether it
    includes `unless-stopped` differs between Podman versions.
  - With Docker, `always` restarts after a reboot too.
  - `docker compose down` removes the containers, so `always` never resurrects a stack the operator stopped.
  - Proof: the reboot test, quickstart §7.

**Alternatives considered**:
- A `/health` endpoint with an HTTP probe: more meaningful only if it checked the database. The API already exits
  when it loses the instance lock connection at startup, and per-request database failures show in the logs. It can
  be added if operators ask for it.
- `wait-for-it` scripts: the restart policy already does the waiting.

## R9. Database service, backup and restore (FR-023, FR-027, FR-028, US5)

**Decision**:
- `image: docker.io/library/postgres:18`, the same major version as the dev stack.
  - `POSTGRES_USER=intheknow`, `POSTGRES_DB=intheknow`, `POSTGRES_PASSWORD=${POSTGRES_PASSWORD:?…}`.
  - Named volume `db` mounted at `/var/lib/postgresql`. PostgreSQL 18's image keeps its data in a versioned
    subdirectory there, the same as the dev compose file.
- Backup, while the stack runs:

  ```bash
  docker compose exec -T db pg_dump -U intheknow -Fc intheknow > intheknow-$(date +%F).dump
  ```

  `pg_dump` takes a consistent snapshot without stopping the API.
- Restore, onto a **fresh** stack:
  1. `docker compose up -d db`, and wait for `healthy`.
  2. `docker compose exec -T db pg_restore -U intheknow -d intheknow --no-owner --exit-on-error < file.dump`
  3. `docker compose up -d`

  Starting only `db` first matters: if the API starts before the restore, it migrates and seeds the empty database,
  and the restore collides with those rows. The API then starts on the restored data, sees the migrations already
  applied, and applies only newer ones.

**Rationale**:
- `-Fc` is one compressed file, and `pg_restore` reads it from stdin. `exec -T` keeps the binary stream clean.
- The database is the only state (spec Assumptions). There is nothing else to back up.
- Restoring over existing data is not supported (edge case). The document starts restore from
  `docker compose down -v`, which deletes the volume, and says so in bold.

**Alternatives considered**:
- Copying the volume directory: needs the stack stopped, and is tied to the PostgreSQL major version and the
  processor type.
- A backup sidecar or a cron container: scheduling is the operator's job (FR-028).

## R10. Stack definition per release, and image names (FR-004a, FR-007)

**Decision**:
- Image names are `<IMAGE>-api` and `<IMAGE>-web`. `IMAGE` defaults to `ghcr.io/wjouter/intheknow`, and the tag is
  the version without the `v` (`1.2.0`).
- `deploy/compose.yaml` in the repository is a **template**: its two image lines read
  `ghcr.io/wjouter/intheknow-api:VERSION` and `…-web:VERSION`.
- The same one-line render is used by CI and by the maintainer:

  ```bash
  sed -e "s|ghcr.io/wjouter/intheknow|$IMAGE|" -e "s|:VERSION|:$VERSION|" deploy/compose.yaml > compose.yaml
  ```

  CI renders with the defaults and attaches `compose.yaml` and `env.example` to the GitHub release. The operator
  downloads both from `…/releases/download/vX.Y.Z/`.
- The rendered file contains no interpolation for the image or the version. Upgrading means replacing the file,
  never editing `.env`.

**Rationale**:
- FR-007: "the operator never edits the version". If the image or the version were `${…}` variables with defaults,
  an operator who once set them in `.env` would silently stay on the old version through every upgrade.
- FR-004a: the registry and name are an input to the commands (`IMAGE`), not a fixed part of the project. Switching
  registry means a different `IMAGE` value plus `docker login` to that registry.
- An unrendered template fails fast: `pull` reports `manifest unknown` for tag `VERSION`.

**Alternatives considered**:
- `image: ${INTHEKNOW_IMAGE:-…}:${INTHEKNOW_VERSION:-1.2.0}`: this is the silent-pin trap above.
- `build:` sections in the stack definition: they point at paths that don't exist on the operator's host, and they
  make `up` build when a pull fails.
- A render script: one `sed` line, documented, is enough.

## R11. Release workflow (FR-002, FR-003, FR-004b, US4, SC-008)

**Decision**: `.github/workflows/release.yml`.
- `on: push: tags: ['v[0-9]+.[0-9]+.[0-9]+']`. There is no `release:` trigger.
- `jobs.checks: uses: ./.github/workflows/ci.yml`. `ci.yml` gains `on: workflow_call:`. Its `concurrency` group
  stays as is: `ci-${{ github.ref }}` is `ci-refs/tags/vX.Y.Z` for a tag, which never matches a branch run.
- `jobs.publish: needs: checks`, with `permissions: packages: write, contents: write`:
  1. `docker login ghcr.io` with `GITHUB_TOKEN` (short-lived, FR-004b).
  2. `docker buildx create --use` (the docker-container driver, for multi-platform builds).
  3. For `api` and `web`:
     `docker buildx build --platform linux/amd64,linux/arm64 -f <dir>/Dockerfile --label org.opencontainers.image.source=https://github.com/Wjouter/InTheKnow -t ghcr.io/wjouter/intheknow-<svc>:<version> --push .`
  4. Render `compose.yaml` (R10).
  5. `gh release view <tag> || gh release create <tag> --verify-tag --generate-notes`
  6. `gh release upload <tag> compose.yaml deploy/env.example --clobber`
- No marketplace actions beyond the existing `actions/checkout`. The few `docker` and `gh` commands are the same ones
  `DEPLOY.md` gives the maintainer.

**Rationale**:
- **Verify**: publishing a GitHub release that creates a new tag creates the tag ref, which fires the tag `push`
  event. So both paths of FR-002 go through the one trigger.
  - Publishing a release for an **existing** tag creates no ref, so there is no push event and no second build. That
    is the edge case, with no deduplication logic.
  - Proof: quickstart §9, steps 1 and 2.
- Branch pushes and pull requests run `ci.yml` as today. Only `release.yml` builds images, and it only runs for
  version tags (US4 AS6).
- `needs: checks` means a failing check publishes nothing (US4 AS2).
- The SDK and Node build stages run on the runner's amd64 (R2, R3), so the arm64 images need no QEMU and the
  workflow stays well inside 30 minutes (SC-008).
- **GHCR visibility**:
  - **Verify**: a package's first publish is private by default, so operators could not pull anonymously.
  - `DEPLOY.md`'s maintainer section has a one-time step: make both packages public in the package settings, and
    link them to the repository. The `image.source` label does the linking.

**Alternatives considered**:
- `docker/build-push-action` with `metadata-action` and `login-action`: three more third-party actions to pin and
  trust, for what four commands do.
- `on: release: published`: this would double-build when the same release also pushed a tag, and needs
  deduplication.
- Pre-release tags (`v1.2.0-rc.1`): not needed yet. The tag pattern can widen later.

## R12. Worked proxy example: Caddy (FR-017, FR-018, US2)

**Decision**: `DEPLOY.md` gives a complete Caddyfile:

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

**How each FR-017 rule is met**:
- **TLS and the HTTP→HTTPS redirect**: Caddy does both automatically for a named site.
- **HSTS**: the `header` line.
- **Routing**: `handle` blocks are mutually exclusive, and the first one that matches wins. `/api` and `/api/*`
  answer `404`, `/n/*` goes to the API, and the rest goes to the web interface.
  - The `respond` sits **inside** a `handle`. In Caddy's directive order, `handle` sorts before `respond`, so a
    top-level `respond @api 404` would never run: the catch-all `handle` would send `/api/*` to the web interface.
  - SvelteKit also answers `404` there, so a status check can't tell the two apart. The check looks for Caddy's empty
    body with no `content-security-policy` header instead (quickstart §5).
  - **Verify**: `caddy adapt --config Caddyfile` shows the routes in this order.
- **Forwarded address**: Caddy replaces a client-sent `X-Forwarded-For` with the real peer unless the peer is in
  Caddy's own `trusted_proxies`, which is empty by default.
- **Host**: passed through by default.
- **App security headers**: Caddy doesn't strip response headers unless told to.
- **Query strings in the log**: Caddy writes no access log unless the site has a `log` directive. The document says
  so. If the operator enables access logs, it gives the `log` filter that deletes the query from `request>uri`.

`DEPLOY.md` also has the check commands (FR-018), listed in [quickstart §5](./quickstart.md). The other proxies are
covered by the FR-017 rule list alone.

**Rationale**: Caddy is the shortest config that meets every rule, because HTTPS and certificates are built in. It
is common on small self-hosted servers. It is Apache-2.0, but it only appears in the documentation, not in the
stack.

**Alternatives considered**:
- nginx: about 40 lines, including a certbot setup and explicit `proxy_set_header` lines. Forgetting
  `X-Forwarded-For $remote_addr` (as opposed to `$proxy_add_x_forwarded_for`) silently breaks US2 AS4.
- Traefik: its labels-based setup assumes it shares the container network, but the spec puts the proxy on the host.

## R13. Operating commands (FR-024, FR-025, SC-002)

**Decision**: these commands work from the directory that holds `compose.yaml` and `.env`.

| Task | Command |
|---|---|
| Start | `docker compose up -d` |
| Status (FR-021) | `docker compose ps` (the `STATUS` column shows `healthy`) |
| First admin (FR-024) | `docker compose exec api dotnet InTheKnow.dll make-admin you@example.nl` |
| Logs (FR-025) | `docker compose logs -f api` |
| Stop | `docker compose down` |
| Upgrade (FR-026) | back up (R9), download the new `compose.yaml`, add new settings from the release notes, `docker compose pull`, `docker compose up -d` |

- `podman compose` takes the same arguments.
- Going live is `up`, `make-admin` and `ps`: 3 commands, within SC-002.

**Rationale**:
- `exec` runs inside the running API container, with its environment, so `make-admin` uses the stack's own settings
  and no second container.
- **Upgrade order**: `pull` runs before `up`, so an unreachable registry fails while the old version keeps running
  (edge case). `up -d` then recreates only the containers whose image changed. Downtime is the API's restart and
  migration (SC-005).
- **Logs**: ASP.NET's console logger and adapter-node already write to stdout and stderr (005 FR-047).
  - Docker's default `json-file` driver doesn't rotate. `DEPLOY.md` gives the one `daemon.json` setting
    (`"log-driver": "local"`) and leaves the decision to the operator (disk space is theirs, per the spec
    Assumptions).

**Alternatives considered**:
- A wrapper script (`./intheknow admin …`): one more file to download and keep in step with the stack, for commands
  that are already one line.

## R14. Podman (FR-008, FR-019)

**Decision**:
- **Supported**:
  - `podman compose` with either provider: `docker-compose` v2, or `podman-compose`. `DEPLOY.md` names the minimum
    `podman-compose` version that has `${VAR:?}`, top-level `name:`, `ipam` subnets and
    `depends_on: condition: service_healthy`.
  - **Verify** these four on the version installed during quickstart §8, and record that version.
- **Image names are fully qualified** (`docker.io/library/postgres:18`, `ghcr.io/…`), so Podman never stops for a
  short-name prompt.
- **One-time host step** for reboot recovery:
  - Rootful: `sudo systemctl enable --now podman-restart.service`.
  - Rootless: `systemctl --user enable --now podman-restart.service` plus `sudo loginctl enable-linger $USER`.
- **Ports**: the stack's ports are above 1024, so rootless Podman can publish them.

**Rationale**: The spec clarifications fixed this. `restart: always` is what `podman-restart.service` starts (R8).

**Alternatives considered**: Quadlet units generated from the stack. They are more native to systemd, but they are a
second stack definition to keep in step with every release, and FR-008 asks for Compose.

## R15. Base image pinning (constitution 1.2.0: base images may track a major tag)

**Decision**:
- Base images are pinned to their **major version tag** (`dotnet/sdk:10.0`, `dotnet/aspnet:10.0`, `node:24-alpine`,
  `postgres:18`), not to a digest.
- Every release build pulls the current patch release of each.
- The app's own dependencies stay locked (`packages.lock.json` with `--locked-mode`, `package-lock.json` with
  `npm ci`).

**Rationale**:
- The base images are the platform the app runs on, not app packages. Their maintainers ship OS and runtime
  security patches under the same tag.
- With no automated updater configured (there is no Dependabot or Renovate here), a digest pin goes stale, and a
  stale base image is the bigger risk for a solo-maintained server app.
- Each published release is still reproducible: its image is immutable in the registry.
- This is recorded in the plan's Complexity Tracking.

**Alternatives considered**: digest pins (`@sha256:…`). These are reproducible builds, but only with Dependabot or
Renovate bumping them. Adding an updater is scope this feature doesn't need.

## R16. Testing strategy

**Decision**:
- **Automated** (the constitution gate):
  - The new `SettingsTests` cases (R5) and the IPv4-mapped `SignInTests` case (R7) run in `dotnet test` on every
    push.
  - The release workflow runs the full `ci.yml` before it publishes.
- **The image build** is exercised on every version tag, and locally with the documented commands. Pull requests do
  not build images (spec clarification).
- **Everything that depends on a host** is in the [quickstart](./quickstart.md) and is run before the first release:
  the proxy, ports, reboot, Podman, multi-arch pull and registry push.

**Rationale**:
- The stack is configuration plus documentation. Its non-trivial logic is the URL validation and the
  trusted-network match, and both have automated checks.
- A CI job that starts the stack on pull requests would contradict the clarification that pull requests never build
  images.

**Alternatives considered**: a compose smoke test in `release.yml` before the push. It is worth adding if a release
ever ships a broken stack. Until then, the quickstart covers it.
