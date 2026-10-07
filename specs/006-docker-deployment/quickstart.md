# Quickstart: Production Container Stack

This is the validation guide for this feature. It proves the stack, the images and `DEPLOY.md` work end to end.

- Contracts: [settings.md](./contracts/settings.md), [stack.md](./contracts/stack.md),
  [images.md](./contracts/images.md).
- Model: [data-model.md](./data-model.md). Decisions: [research.md](./research.md). Steps marked **Verify** in
  research point here.

## Prerequisites

- **A clone of the repository**, with the dev prerequisites of [QUICKSTART](../../QUICKSTART.md) for §1.
- **A server**:
  - Linux, amd64 or arm64, with time sync;
  - Docker Engine with Compose v2 (28 or later required, research R7), **or** Podman with `podman compose`;
  - a domain pointing at the server, ports 80 and 443 open, and Caddy installed (the worked example).
- **For §5's routed-subnet check**: a second machine on the server's LAN (same L2 segment), or a VM bridged onto it.
- **For §8**: a second machine, or a VM, with the *other* runtime.
- **For §9**: an arm64 machine, push rights on the GitHub repository, and an account on one non-GitHub registry.
- **SMTP credentials** for a real SMTP server with STARTTLS. The API allows `StartTls=false` only for localhost, and
  inside the stack, localhost is the API container itself, so Mailpit can't stand in here.

## 1. Automated checks

```bash
dotnet test
cd frontend && npm run check && npm test && npm run build
```

**Expected**: all pass. The checks include:
- the new `SettingsTests` cases:
  - `https://x.nl/`, `https://x.nl/app`, `https://x.nl?a=1` and `http://x.nl` are rejected, each naming the key;
  - `http://localhost:5173` and `https://x.nl:8443` are accepted;
- the IPv4-mapped `SignInTests` case: peer `::ffff:192.0.2.1` with `TrustedProxies:0 = 192.0.2.0/24` records the
  forwarded address (research R5, R7).

## 2. Build the images locally (FR-004, FR-005)

From the repository root:

```bash
IMAGE=localhost/intheknow VERSION=0.0.0-test
docker build -f backend/Dockerfile  -t "$IMAGE-api:$VERSION" .
docker build -f frontend/Dockerfile -t "$IMAGE-web:$VERSION" .
```

**Expected**: both builds succeed with no registry sign-in. Then check the contents and the user:

```bash
docker run --rm --entrypoint sh "$IMAGE-api:$VERSION" -c 'id -un; find / -xdev \( -name "*.Tests*" -o -name ".env" -o -name "*mytimetable*" -o -name ".git" -o -name "Fixtures" -o -name "dotnet-sdk*" \) 2>/dev/null'
docker run --rm --entrypoint sh "$IMAGE-web:$VERSION" -c 'id -un; ls /app; ls /app/node_modules; find / -xdev \( -name ".env" -o -name "*mytimetable*" -o -name "vitest" -o -name ".git" \) 2>/dev/null'
```

**Expected**:
- The users are `app` and `node`.
- The API's `/app` holds the published output only.
- The web interface's `node_modules` holds only the four production packages.
- No `find` hits.
- Repeat the check once with a `.env` file and the private links file present in the repository root (research R4).

Render a stack definition for these local images:

```bash
mkdir -p ~/itk && sed -e "s|ghcr.io/wjouter/intheknow|$IMAGE|" -e "s|:VERSION|:$VERSION|" deploy/compose.yaml > ~/itk/compose.yaml
cp deploy/env.example ~/itk/.env && chmod 600 ~/itk/.env
```

## 3. First start (US1 AS1, AS4 to AS6)

In `~/itk`:

1. Leave `.env` as copied and run `docker compose up -d`.
   **Expected**: it fails before creating anything, naming `PUBLIC_URL` (or the first missing required setting).
   No containers exist (`docker compose ps -a` repeats the error).
2. Fill in:
   - `PUBLIC_URL=https://<your domain>`
   - `POSTGRES_PASSWORD=$(openssl rand -hex 32)` (paste the value)
   - `SMTP_HOST`, `SMTP_FROM`, `SMTP_USERNAME`
   - `SMTP_PASSWORD='pa$$ w#rd'`, single-quoted, to prove quoting (research R6). Use the real password for the US1
     walkthrough.
3. Run `docker compose up -d`, then `watch docker compose ps`.
   **Expected**:
   - `db` becomes healthy, then `api` starts, then all three are `healthy` within 2 minutes.
   - `docker compose logs api` shows the migrations and no exception. The pool, the instance lock connection and
     `PGPASSWORD` all work.
4. Set `PUBLIC_URL=https://<your domain>/`, then `docker compose up -d`.
   **Expected**: `api` keeps restarting, its log names `InTheKnow:Notifications:PublicBaseUrl`, and `ps` doesn't
   show it healthy. Repeat with `/app` and with `http://`. Restore the correct value afterwards.
5. Check that nothing listens off loopback:

   ```bash
   ss -tlnp | grep -E ':(3000|5000|5432)\b'
   ```

   **Expected**: only `127.0.0.1:3000` and `127.0.0.1:5000`, and no `5432`.
6. Set `WEB_PORT` to a port already in use, then `up -d`.
   **Expected**: the error names the port. Restore the value.

## 4. Proxy and first admin (US1 AS2, AS3; SC-001, SC-002)

1. Put the `DEPLOY.md` Caddyfile in place with your domain, then `systemctl reload caddy`.
2. Run:

   ```bash
   docker compose exec api dotnet InTheKnow.dll make-admin you@example.nl
   ```

   **Expected**: `invite queued`. The email arrives through your SMTP server, and its link opens "Accept your invite"
   at `PUBLIC_URL`.
3. Accept the invite, turn on two-step verification, and see the admin pages unlocked.
4. In the web interface, add an email channel for your address, confirm it from the email, and send a test
   email (003 US1). **Expected**: its unsubscribe link and `List-Unsubscribe` header start with
   `PUBLIC_URL/n/`, its settings link starts with `PUBLIC_URL`, and opening the unsubscribe link shows the
   API's page through the proxy (US1 AS4).
5. Time the whole run (steps 1 to 3) from the downloaded files: under 30 minutes. Count the commands: `up`, `exec … make-admin` and
   `ps` (3).

## 5. Exposure and proxy checks (US2; FR-016 to FR-018; SC-003, SC-007)

These are the `DEPLOY.md` checks. Run them from another machine unless noted.

```bash
D=https://<your domain>
curl -sI http://<your domain>/ | head -1                              # 301/308 to https
curl -sI $D/ | grep -i '^strict-transport-security: max-age=31536000'
curl -s -D - $D/api/me                                                # 404, empty body, no content-security-policy
curl -s -D - $D/api                                                   # same: answered by Caddy, not SvelteKit
curl -s $D/n/unsubscribe/x | grep -i intheknow                        # answered by the API's public page
curl -sI $D/ | grep -iE '^(x-frame-options|referrer-policy|x-content-type-options|content-security-policy):'  # all four present
nmap -p 3000,5000,5432 <server address>                               # all closed/filtered
```

- **Routed container subnet** (FR-015, research R7). From the second machine on the server's LAN, for every runtime
  in §8 (Docker, rootful Podman):

  ```bash
  sudo ip route add 172.30.250.0/24 via <server LAN address>
  nmap -Pn -p 3000,5432,8080 172.30.250.0/28          # all filtered
  sudo ip route del 172.30.250.0/24
  ```

  **Expected**: every port `filtered`. If a port answers, that runtime exposes the stack to the LAN: apply the R7
  fallback (Docker: upgrade to 28+; rootful Podman: the § Podman firewall rule) and repeat.

- **Spoofed forwarded address** (US2 AS4):
  1. Sign in wrongly with `-H 'X-Forwarded-For: 198.51.100.7'` on the sign-in form post.
  2. Check that `security_event.source` records your real address, not `198.51.100.7`:

     ```bash
     docker compose exec db psql -U intheknow -c "SELECT host(source) FROM security_event ORDER BY id DESC LIMIT 3"
     ```

- **Peer address through the published API port** (research R7, **Verify**). Run this on the server, for every
  runtime in §8. Send a wrong sign-in for the admin account from §4 straight to the API port, twice:

  ```bash
  curl -s -o /dev/null -H 'Content-Type: application/json' -d '{"email":"you@example.nl","password":"wrong-password"}' http://127.0.0.1:5000/api/auth/sign-in
  curl -s -o /dev/null -H 'Content-Type: application/json' -H 'X-Forwarded-For: 203.0.113.5' -d '{"email":"you@example.nl","password":"wrong-password"}' http://127.0.0.1:5000/api/auth/sign-in
  docker compose exec db psql -U intheknow -c "SELECT type, host(source) FROM security_event ORDER BY id DESC LIMIT 2"
  ```

  **Expected**:
  - The first row (no header) records the runtime's **real peer**. It must be in `STACK_SUBNET` or loopback. Record
    it.
  - The second records `203.0.113.5`, which proves that peer is trusted.
  - If the first address is outside both, apply the R7 fallback.
- **Two visitors** (SC-007):
  1. From machine A, fail sign-in until the source throttle triggers, with a different email address each time (one
     address hits the per-account limit first). Use a low limit for the test:
     `InTheKnow__Accounts__SourceFailuresPerHour=20`.
  2. From machine B, sign in successfully.
- **Logs**: `journalctl -u caddy` and Caddy's own log output contain no `/account/…?token=` query (no access log by
  default).

## 6. Backup and restore (US5; FR-027; SC-006)

With data (an account, a calendar, a recorded change):

```bash
docker compose exec -T db pg_dump -U intheknow -Fc intheknow > before.dump
docker compose down -v
docker compose up -d db          # wait for healthy
docker compose exec -T db pg_restore -U intheknow -d intheknow --no-owner --exit-on-error < before.dump
docker compose up -d
```

**Expected**:
- The restore prints no errors, and the API starts without applying any migration.
- The existing user signs in and sees their calendars and changes.
- The backup ran while `api` was healthy, and the app kept answering during it.

## 7. Restarts (US3; FR-019 to FR-022; SC-004)

1. `docker compose exec api bash -c "kill -TERM 1"`. **Expected**: it is restarted and healthy again. (`docker compose
   kill` and `stop` don't trigger `restart: always`.)
2. **One API instance**: `docker compose up -d --scale api=2`. **Expected**: the second instance fails on the port
   (or the instance lock), and the first keeps serving.
3. **The API starts before the database is ready** (US3 AS2):
   1. `docker compose stop db`
   2. `docker compose restart api`
   3. `docker compose ps` and `logs api`: the API exits and is restarted, again and again.
   4. **The web interface tolerates it** (FR-020): `web` stays `healthy` in `docker compose ps`, and opening the
      public address shows the "Temporarily unavailable" page (503), not a proxy error.
   5. `docker compose start db`
   6. **Expected**: the API becomes healthy without anyone touching it, and the page loads again on reload.
4. **Reboot** the host. **Expected**: everything is healthy again within 2 minutes, with nobody touching it, and a
   calendar check runs.

The upgrade (SC-005) and the unreachable-registry edge case need images in a real registry. They are in §9, after
the first release.

## 8. The other runtime (FR-008, research R14)

Repeat §2 (with `podman build`), §3, §4 step 2, §5's peer address check and §7 steps 1, 3 and 4 with
`podman compose`.
Do it once rootless, and once rootful if you support it.

- **Before** the reboot test, run the one-time host step from `DEPLOY.md`.
- Record:
  - the `podman compose` provider and version;
  - whether `${VAR:?}`, `name:`, `ipam` and `condition: service_healthy` behaved as in §3;
  - the peer address the API saw (rootless, rootful).
- If any of these differ, apply the fallback written in research R7, R8 or R14, and update `DEPLOY.md`.

## 9. Publishing (US4; FR-002 to FR-004b; SC-008, SC-010)

1. **Tag from the command line**: push tag `v0.1.0`.
   **Expected**:
   - `release.yml` runs the checks, then publishes
     `ghcr.io/wjouter/intheknow-{api,web}:0.1.0` for amd64 and arm64 within 30 minutes;
   - a GitHub release `v0.1.0` has `compose.yaml` (pinned to `0.1.0`) and `env.example`.

   Make both packages public (the one-time step). Then pull anonymously on an arm64 machine and on an amd64 machine,
   and start the stack from the release's `compose.yaml`.
2. **Release from the GitHub UI** with a new tag `v0.1.1`: same result (research R11, **Verify**). Then publish a
   release for the existing tag `v0.1.0`: **no** workflow run.
3. **Upgrade** (US3 AS3, FR-026, SC-005):
   1. On a server running `v0.1.0` with data, follow `DEPLOY.md`'s upgrade steps to `v0.1.1`: back up, download the
      new `compose.yaml`, then time `docker compose pull && docker compose up -d`.
   2. **Expected**: under 2 minutes of downtime, all accounts, calendars, filters, presets, channels and changes
      kept, and `.env` unchanged.
4. **Registry unreachable** (edge case):
   1. Block `ghcr.io`, for example with an `/etc/hosts` entry to `0.0.0.0`.
   2. Put back the `v0.1.0` definition and run `docker compose pull` after removing the cached `0.1.0` images.
   3. **Expected**: it fails with the runtime's error, and the running `v0.1.1` keeps serving.
   4. Unblock and restore.
5. **Failing check**: on a throwaway branch, break one test and tag `v0.1.2-broken`. It is not a version tag, so
   there is no run. Then tag `v0.1.2` on that broken commit. **Expected**: the checks fail and nothing is published.
   Delete the tag and the release afterwards.
6. **Branch push and pull request**: CI runs the checks, and no image job appears.
7. **Manual publish to another registry** (SC-010):
   1. In a clean clone, follow `DEPLOY.md`'s maintainer section with `IMAGE=<other registry>/<you>/intheknow`
      and `VERSION=0.1.0-manual`.
   2. Render the definition with the same `IMAGE`, and start it on a server of the same processor type.
   3. **Expected**: both images come from that registry, and the stack runs.

## 10. Development is unchanged (US6; FR-030; SC-009)

On a clean checkout, follow [QUICKSTART](../../QUICKSTART.md) from step 1.

**Expected**:
- `docker compose up -d db mailpit` starts only those two, with no settings file, and `git status` stays clean.
- `dotnet test` and the frontend checks pass as in §1.
- A dev stack and the `~/itk` stack can run at the same time on one machine, with separate volumes
  (`intheknow_db`, `intheknow-prod_db`). Set `API_PORT=5001` in `~/itk/.env` while the dev API uses port 5000.
