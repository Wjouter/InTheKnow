# Feature Specification: Production Container Stack

**Feature Branch**: `006-docker-deployment`

**Created**: 2026-09-30

**Status**: Draft

**Input**: User description: "I want to create a docker container / stack to easily put this app in production."

**Builds on**: [001-ical-change-detection](../001-ical-change-detection/spec.md) (the API and background checker, the database, configuration from the environment), [003-notification-channels](../003-notification-channels/spec.md) (outgoing email, the public `/n/…` pages), [004-accounts-login](../004-accounts-login/spec.md) (the `make-admin` command, trusted proxies for sign-in throttling) and [005-sveltekit-frontend](../005-sveltekit-frontend/spec.md) (the web interface and its [deployment contract](../005-sveltekit-frontend/contracts/configuration.md): public origin, reverse proxy routing, HTTPS, HSTS, forwarded client address). This feature packages what exists so an operator can run it on a server. Its one app change is stricter startup validation: the public and frontend base URLs must be an origin only (the first edge case below).

## Clarifications

### Session 2026-09-30

- Q: Does the stack include the HTTPS reverse proxy, or does the operator bring their own? → A: The operator brings their own reverse proxy on the same host. The stack runs the database, API and web interface, reachable only from the host; the deployment document gives the routing rules of the 005 contract and one worked example.
- Q: Are the container images built on the server, or published ready-made? → A: The project publishes ready-made images from GitHub. The operator's stack pulls a pinned version; building the images from the repository stays possible.
- Q: How do backups work? → A: Manually, with documented backup and restore commands. Scheduling and off-host copies are the operator's job.
- Q: When an operator upgrades, where do they change the version, and do they also download that release's stack definition? → A: Each release ships its own stack definition with the image version pinned inside it; upgrading means downloading that definition and restarting. The settings file lives on the host, outside the containers, where the operator edits it directly; upgrades never change it, and the operator only adds to it when a release introduces a new setting.
- Q: Under Podman, is a one-time setup step on the host acceptable for bringing the stack back after a reboot? → A: Yes. The document gives the one-time Podman host step (enable the restart service, plus linger for rootless); reboot recovery is required once it is done. Docker needs no extra step.

### Session 2026-10-01

- Q: Can the maintainer build the images locally and publish them by hand, to a registry other than GitHub's? → A: Yes. The documented build commands work on the maintainer's machine, the registry and image name are an input rather than fixed, and a documented push command publishes them to any registry the maintainer is signed in to. Switching registry changes only the registry address and the maintainer's own sign-in. A manual push only needs the maintainer's own processor type; automatic publishing stays on GitHub's registry only.
- Q: When does GitHub build images automatically? → A: Only when a version tag is pushed, whether from the command line or by publishing a GitHub release that creates the tag. Pushes to branches and pull requests never build or publish images; they run the existing checks only.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Put InTheKnow online on a fresh server (Priority: P1)

An operator has a server with a container runtime and a reverse proxy that handles HTTPS, a domain name pointing at it and an SMTP account. They download the stack definition and example settings, fill in one settings file (public address, database password, SMTP details), start the stack with one command, point their proxy at it following the document, create the first admin with one command, and open the app over HTTPS in a browser.

**Why this priority**: This is the feature. Today going live means building two apps by hand, running a database, and keeping five addresses in sync across two apps; one mistake silently breaks throttling or email links.

**Independent Test**: On a clean machine with only a container runtime, a reverse proxy, a domain and SMTP credentials, follow the deployment document from start to finish: start the stack, configure the proxy from the worked example, run the admin command, accept the invite from the email, turn on two-step verification, and see the admin pages unlocked.

**Acceptance Scenarios**:

1. **Given** a server with a container runtime and a settings file filled in from the example, **When** the operator starts the stack with one command, **Then** the published images are downloaded, the database, API and web interface start in the right order, and the database schema is created.
2. **Given** a running stack and a proxy configured as the document describes, **When** someone opens the public address, **Then** the app answers over HTTPS.
3. **Given** a running stack on an empty database, **When** the operator runs the admin command with their address, **Then** it prints `invite queued`, the invite email arrives through the configured SMTP server, and its link opens "Accept your invite" at the public address.
4. **Given** the operator sets the public address once, **When** the stack starts, **Then** the web interface's origin, the links in account and notification emails, and the public `/n/…` pages all use that address; the operator never enters it twice.
5. **Given** a required setting is missing or invalid (no public address, no database password, no SMTP host), **When** the stack starts, **Then** the affected service stops with a message naming the setting, and the stack does not report itself healthy.
6. **Given** the stack is running, **When** the operator checks its status, **Then** each service shows whether it is healthy.

---

### User Story 2 - Safe to leave running on the internet (Priority: P1)

The stack follows the exposure rules of the 005 deployment contract by default, so an operator who changes nothing in the stack gets a secure setup: nothing reachable from outside the host except through their proxy, and throttling per real visitor. The document tells them exactly what their proxy must do and how to check it.

**Why this priority**: The app holds student accounts and calendar links with tokens. An unsafe default ships unsafe installs (Principle II). It is P1 together with US1 because a stack that is easy but exposes the database is not fit for production.

**Independent Test**: With the stack and the example proxy running, scan the server from another machine and see only the proxy's ports; request `http://`, `/api/…` and `/n/…` and check each answer against the document's checks; sign in wrongly from two different machines until one is throttled and check the other is not.

**Acceptance Scenarios**:

1. **Given** a running stack, **When** a port scan runs from outside the server, **Then** no port of the stack (database, API, web interface) is reachable; they are reachable from the host only.
2. **Given** the proxy configured from the document, **When** a request arrives over plain HTTP, **Then** it is redirected to HTTPS, and HTTPS responses carry `Strict-Transport-Security: max-age=31536000`.
3. **Given** the proxy configured from the document, **When** a request for `/api/…` arrives from the internet, **Then** the answer is `404`; a request for `/n/…` reaches the API's public pages; everything else reaches the web interface.
4. **Given** two visitors on different addresses behind the operator's proxy, **When** one exceeds the sign-in failure limit, **Then** only that visitor is throttled; the other can still sign in. A forwarded-address header sent by a visitor is replaced by the proxy, not trusted.
5. **Given** the example settings, **When** the operator looks at them, **Then** they contain placeholders only; no working default password exists for the database or anything else, and the settings file itself is ignored by version control.
6. **Given** the document's proxy example, **When** a visitor opens an `/account/…` link that carries a token, **Then** the proxy does not write the token (the query string) to its log.

---

### User Story 3 - Keep it running and upgrade it (Priority: P2)

The operator's server reboots, a new version of InTheKnow comes out, or the database briefly starts slower than the app. The stack recovers on its own, and an upgrade to a newer published version keeps every account, calendar and recorded change.

**Why this priority**: Once live, students depend on notifications arriving. Manual restarts after every reboot or upgrade are how self-hosted apps quietly die. It follows US1 and US2 because it only matters once the stack runs.

**Independent Test**: With accounts, calendars and changes in the database, reboot the host and check the app comes back unattended; then upgrade to a newer published version with the documented steps and check all data is still there and the new schema is applied.

**Acceptance Scenarios**:

1. **Given** a running stack (under Podman, after the document's one-time host step), **When** the host reboots, **Then** all services start again without anyone touching them, and checking and notifying resume.
2. **Given** the database is not ready yet when the API starts, **When** the API starts, **Then** it waits or retries until the database is ready instead of staying down.
3. **Given** a running stack with data, **When** the operator follows the documented upgrade steps (back up, download the new release's stack definition, restart), **Then** the new images are downloaded, the API migrates the database at startup, and every account, calendar, filter, preset, channel and recorded change is kept. Downtime is limited to the restart.
4. **Given** a service crashes, **When** it exits, **Then** it is restarted automatically.
5. **Given** the stack, **When** it runs, **Then** exactly one API instance runs (001's instance lock); the stack offers no way to start a second one by accident.

---

### User Story 4 - Published images for every release (Priority: P2)

When the maintainer releases a version of InTheKnow, by pushing a version tag or publishing a GitHub release, ready-made images for the API and the web interface are published automatically, tagged with that version, after all checks pass. Nothing else triggers an automatic image build. The maintainer can also build the images on their own machine and push them by hand to any registry, so the project is not tied to GitHub's.

**Why this priority**: US1 and US3 depend on it: operators download images instead of building. It is P2 because the stack can be tested with locally built images before the first release is published.

**Independent Test**: Tag a release in the repository, see the checks run and the images appear in the registry with the release's version tag, then start a stack with that version on a clean machine.

**Acceptance Scenarios**:

1. **Given** a new release tag, **When** it is pushed, **Then** the build, lint and test checks run, and only if all pass are both images published with the release's version tag.
2. **Given** a failing check, **When** a release tag is pushed, **Then** no image is published for it.
3. **Given** a published version, **When** an operator's server of either supported processor type pulls it, **Then** the right image for that processor is used.
4. **Given** a clone of the repository, **When** someone builds the images themselves, **Then** they get working images without the registry.
5. **Given** the maintainer publishes a GitHub release that creates a new version tag, **When** the release is published, **Then** the images are built and published exactly as for a tag pushed from the command line.
6. **Given** a push to a branch or a pull request, **When** CI runs, **Then** the checks run but no image is built or published.
7. **Given** a local clone and a sign-in to some registry (not GitHub's), **When** the maintainer runs the documented build and push commands with that registry's address and a version, **Then** both images appear in that registry under that version for the maintainer's own processor type, and a stack definition pointing at that registry starts them on a server of that type.

---

### User Story 5 - Back up and restore the data (Priority: P2)

The operator can make a backup of everything InTheKnow stores with one documented command, and restore it onto a fresh stack, for example before an upgrade or after losing the server.

**Why this priority**: Migrations only go forward, so the backup is the only way back from a failed upgrade. It is P2 because the app works without it, but nobody should run it for long without it.

**Independent Test**: Make a backup of a stack with data, start a new stack on an empty volume, restore the backup, and sign in with an existing account and see its calendars and changes.

**Acceptance Scenarios**:

1. **Given** a running stack, **When** the operator runs the documented backup command, **Then** a single backup file of the whole database is written to a location they choose, while the app keeps running.
2. **Given** a backup file and a fresh stack, **When** the operator runs the documented restore steps, **Then** the app starts with all data from the backup.

---

### User Story 6 - Local development is unchanged (Priority: P3)

A developer following QUICKSTART still starts only the database and the mail catcher in containers and runs the API and web interface from source, without any production settings.

**Why this priority**: The production stack must not make development harder. It is P3 because it is about not breaking something, not adding value.

**Independent Test**: On a clean checkout, follow QUICKSTART step by step and see it work unchanged, with no settings file created.

**Acceptance Scenarios**:

1. **Given** a clean checkout, **When** a developer runs QUICKSTART's container command, **Then** only the development database and mail catcher start, as today, and no production setting is required.
2. **Given** the backend tests, **When** they run against the development database, **Then** they pass as today.

---

### Edge Cases

- The public address has a trailing slash, a path, or `http://`: startup fails with a message; it is not silently used.
- The operator's proxy is missing or misrouted: the stack itself still runs and reports healthy; the document's checks (US2) show which rule fails.
- The ports the stack publishes on the host are already in use: the stack fails to start and names the port; the ports are settable in the settings file.
- The SMTP server rejects the credentials: the API logs the delivery failure (003 retry rules apply); the admin command still prints `invite queued`, and the document tells the operator where to look.
- The admin command runs before the API has migrated a fresh database: it says the schema is behind (existing behavior), and the document says to wait for the stack to be healthy first.
- An upgrade fails mid-migration: the API does not start, the old data is intact up to the failed migration's own transaction, and the document points to restoring the pre-upgrade backup.
- The registry is unreachable during an upgrade: the download fails with the runtime's error; the running version is not stopped before the new images are downloaded, if the documented upgrade steps are followed.
- A new release adds a setting: the release notes list it; if it has a default, the unchanged settings file keeps working; if it is required, the API stops at startup naming it (FR-014).
- Restoring onto a stack that already has data: the document's restore steps start from an empty database; restoring over existing data is not supported.
- The disk fills up: the database stops accepting writes; the stack does not lose the existing data. Monitoring disk space is the operator's job and the document says so.
- A GitHub release is published for a tag that already exists and already built: no second build runs; the images for that version stay as they are.
- A version is pushed by hand to a registry that already holds that version: the push replaces it; the document warns that published versions should not be overwritten and that the maintainer should bump the version instead.
- The host clock is wrong: out of scope; the document tells the operator to run time sync (two-step verification codes depend on it).

## Requirements *(mandatory)*

### Functional Requirements

**Images**

- **FR-001**: The project MUST provide a container image for the API (including the background checker, the notifier and the `make-admin` command) and one for the web interface, each built from the repository's own sources and lockfiles.
- **FR-002**: On each version tag, whether pushed directly or created by publishing a GitHub release, the project MUST automatically run the build, lint and test checks and, only if all pass, publish both images to a public container registry, tagged with the release's version. Images MUST NOT be built or published automatically for any other event (branch pushes, pull requests, tags that are not versions).
- **FR-003**: Automatically published images MUST be available for 64-bit x86 and 64-bit ARM servers under the same tag.
- **FR-004**: Anyone with a clone of the repository MUST be able to build both images locally with documented commands, without the registry.
- **FR-004a**: The maintainer MUST be able to publish locally built images to any container registry by hand with documented commands. A manual push MAY contain only the processor type of the machine that built it; FR-003 applies to automatic publishing. The registry address and image name MUST be an input to the build and push commands, not fixed in the project; switching registry MUST need only a different address and the maintainer's sign-in to it.
- **FR-004b**: Registry credentials MUST NOT be stored in the repository. Manual publishing uses the maintainer's own sign-in; automatic publishing uses the CI's own short-lived credentials.
- **FR-005**: Images MUST NOT contain secrets, test code or the project's build tooling (SDKs, compilers, dev dependencies, test runners), and MUST run the app as a non-root user. What the official runtime base image itself ships (for example the shell, or npm in the Node image) is acceptable.

**Stack**

- **FR-006**: The project MUST provide a production stack definition that runs the database, the API and the web interface together, started and stopped with one command each. It MUST NOT include a reverse proxy or mail server.
- **FR-007**: Each release MUST publish its own stack definition with that release's image version pinned inside it. The operator upgrades by replacing the stack definition with the new release's; they never edit the version themselves.
- **FR-008**: The stack MUST work with Docker Compose and with Podman Compose, as QUICKSTART already promises for development.

**Configuration**

- **FR-009**: All settings the operator must or may change MUST live in one settings file on the host, outside the containers, next to the stack definition, editable with any text editor. It is created by copying a committed example that holds placeholders and a comment per setting, and MUST be ignored by version control. Upgrades MUST NOT overwrite or require editing it, except to add a setting a new release introduces (listed in its release notes).
- **FR-010**: The operator MUST set the public address once; the stack MUST derive from it the web interface's origin, the API's frontend link base and the API's public page base.
- **FR-011**: The stack MUST set the internal wiring itself (the API's address for the web interface, the database connection, trusted proxies, the forwarded-address settings); the operator MUST NOT have to set them for a proxy on the same host.
- **FR-012**: The database password MUST be required and have no default; the stack MUST refuse to start without it.
- **FR-013**: Every other API setting of 001–005 (check interval, SMTP, bot check, MyTimetables domains, time zone, retention, …) MUST remain settable through the same settings file, with the existing defaults.
- **FR-014**: Invalid or missing required settings MUST stop the affected service at startup with a message naming the setting (001's startup validation).

**Exposure (005 deployment contract)**

- **FR-015**: The stack MUST publish only the web interface and the API's port, and only on the host's loopback address, so the operator's proxy on the same host can reach them and nothing outside the host can. The database MUST NOT be published at all. The published host ports MUST be settable.
- **FR-016**: The web interface and the API MUST trust forwarded client addresses only from the host's proxy and from inside the stack, so throttling works per visitor once the proxy sets the forwarded address.
- **FR-017**: The deployment document MUST list what the operator's proxy must do, from the 005 contract: terminate TLS and redirect HTTP to HTTPS; send `Strict-Transport-Security: max-age=31536000`; route `/n/…` to the API, answer `404` for `/api/…`, and send everything else to the web interface; replace any client-sent forwarded-address header with the real client address; pass the `Host` header through; not strip the app's security headers; not log query strings of `/account/…` requests.
- **FR-018**: The document MUST include one worked, tested example configuration for a common reverse proxy that meets FR-017, and a short list of checks (commands the operator runs) that verify each rule of FR-017 on their own setup.

**Running**

- **FR-019**: Services MUST restart automatically after a crash and after a host reboot. Under Podman, reboot recovery MAY depend on a one-time host step, which the document MUST give (enabling Podman's restart service, and linger for a non-root user); Docker MUST need no extra step.
- **FR-020**: The API MUST start only once the database accepts connections; the web interface MUST tolerate the API starting later.
- **FR-021**: Each service MUST report its health so the operator can see it in the stack's status.
- **FR-022**: The stack MUST run exactly one API instance.
- **FR-023**: The database MUST store its data on a persistent volume that survives container recreation and upgrades.
- **FR-024**: The operator MUST be able to run the `make-admin` command against the running stack with one documented command, using the stack's own settings.
- **FR-025**: Logs of every service MUST go to the container runtime's standard log output; the existing log rules (005 FR-047, 004's no-secrets rules) still apply.

**Upgrades and backups**

- **FR-026**: The document MUST describe an upgrade as a short sequence of steps: back up, download the new release's stack definition, add any new settings its release notes list, download the images, restart. The database is migrated by the API at startup (existing behavior).
- **FR-027**: The document MUST give one command to back up the whole database to a single file while the stack runs, and the steps to restore such a file onto a fresh stack. The app keeps no other state.
- **FR-028**: The document MUST state that migrations only go forward, so rolling back an upgrade means restoring the backup made before it and starting the previous release's stack definition; and that scheduling backups and copying them off the host are the operator's job.

**Documentation and development**

- **FR-029**: The project MUST include a deployment document covering: prerequisites (host, reverse proxy, domain, SMTP), first start, proxy setup, first admin, status and logs, upgrade, backup and restore, building images locally and publishing them by hand to a registry of choice, and the edge cases above. README MUST link to it.
- **FR-030**: The existing development setup (`compose.yaml` with the database and mail catcher, QUICKSTART) MUST keep working unchanged, and MUST NOT require the production settings file. The mail catcher MUST NOT be part of the production stack.

### Key Entities

- **Settings file**: the operator's copy of the example settings; holds the public address, database password, SMTP details and optional app settings. Lives on the host, outside the containers; kept across upgrades. Contains secrets, never committed.
- **Stack definition**: the per-release file that describes the services and pins that release's image version. Replaced on upgrade; the operator does not edit it.
- **Image**: a published, versioned package of the API or the web interface, one per release, for both supported processor types.
- **Database volume**: the persistent storage of all InTheKnow data. The only state the stack keeps.
- **Backup file**: a single-file copy of the whole database that can be restored onto a fresh stack.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An operator with a server running a reverse proxy, a domain pointing at it and SMTP credentials goes from nothing to a signed-in admin with the admin pages unlocked in under 30 minutes, following only the deployment document.
- **SC-002**: Going live needs at most 3 stack commands after filling in the settings file (start, create admin, check status), and at most 6 required settings.
- **SC-003**: A scan from outside the host finds no port of the stack open; only the operator's proxy ports answer.
- **SC-004**: After a host reboot the app is reachable again within 2 minutes with no manual action (under Podman, once the documented one-time host step is done).
- **SC-005**: An upgrade following the document keeps 100% of accounts, calendars, filters, presets, channels and recorded changes, with under 2 minutes of downtime.
- **SC-006**: A backup restored onto a fresh stack gives back 100% of the data; an existing user signs in and sees their calendars and changes.
- **SC-007**: Throttling separates visitors: one visitor exceeding the sign-in limit never blocks another visitor on a different address.
- **SC-008**: Every version tag whose checks pass has both images published within 30 minutes; a release whose checks fail publishes nothing; branch pushes and pull requests publish nothing.
- **SC-010**: The maintainer goes from a clean clone to both images published by hand in a non-GitHub registry using only the document's commands.
- **SC-009**: QUICKSTART works unchanged on a clean checkout.

## Assumptions

- One host runs the stack and the operator's reverse proxy, with one public domain; the app and `/n/…` share it. A proxy on another host, multiple hosts, load balancing and scaling out are out of scope (the API runs as one instance by design).
- The operator already runs a reverse proxy that can terminate TLS and get certificates; setting one up from scratch is covered only by the document's worked example.
- The operator provides an SMTP server; the stack does not include a mail server.
- CI stays on GitHub and the repository stays public; GitHub's registry is the only target for automatic publishing. Another registry is used by publishing by hand (FR-004a); moving automatic publishing to it is out of scope. Releases are marked by version tags.
- 64-bit x86 and 64-bit ARM cover the servers operators use; other processor types are out of scope.
- The host has a container runtime with Compose (Docker or Podman) and time sync.
- The database is the app's only state: the API writes no files and the web interface has no secrets of its own (005 configuration contract), so backups cover the database only.
- Downtime of a minute or two during upgrades is acceptable; zero-downtime upgrades are out of scope.
- Monitoring, alerting, disk-space checks, backup scheduling and off-host backup storage are the operator's job; the document names them.
- The stack uses the same PostgreSQL major version as development.
- "Docker container / stack" in the request is taken as a requirement: the deployment is container-based. The base images, registry, CI setup and the proxy used in the worked example are left to the plan.
