# Runbook results: 006 manual tasks

Run on 2026-10-07 by the maintainer and Claude, following [manual-runbook.md](./manual-runbook.md).

- **Server**: Contabo VPS, Ubuntu 22.04.5, x86_64, Docker 29.8.2, time sync on. Built locally from `006-docker-deployment`
  (`8a2afa0`, then `d68ca92` after the Message-ID fix).
- **Proxy**: a **Cloudflare tunnel**, not Caddy (ports 80 and 443 and `0.0.0.0:3000` are taken by other services). Routes,
  first match wins: `^/n/` → `http://127.0.0.1:5000`, then catch-all → `http://127.0.0.1:3001` (`WEB_PORT=3001`).
  "Always Use HTTPS" and HSTS are Cloudflare zone settings.
- **Test host**: `https://test.example.com`. **SMTP**: `smtp.example.com:587`.
- **Podman**: the server's Podman 3.4.4 can't run the stack (see T020). Podman was tested on the maintainer's dev machine instead
  (Podman 6.1.3, rootless).

| Task | Status |
|---|---|
| T013 | ✅ done (already ticked) |
| T017 | ✅ pass, with agreed deviations (Cloudflare instead of Caddy, 1-month HSTS, no LAN neighbour) |
| T020 | ✅ Docker pass. Podman passes on 6.1.3 rootless; **3.4.4 is not supported**. Rootful and the Podman reboot were not run |
| T029 | ✅ pass |
| T031 | ✅ pass |
| T025 | 🟡 steps 1, 2, 5 and 6 pass (2026-10-19/20); signed-out pull not run (packages stay private in beta); arm64 hardware and step 7 open |
| T026 | ✅ pass (2026-10-20); the registry was blocked with an unresolvable registry name instead of `/etc/hosts` (no sudo) |

---

## T013: First start and first admin

```text
1  Missing settings: error message (first line): "required variable SMTP_FROM is missing a value: SMTP_FROM is required, see DEPLOY.md" (every missing setting named)
   ps -a empty (yes/no): yes, no containers created (`compose ps -a` itself prints the same errors)
3  All healthy within 2 min (yes/no, time): yes, 17 s     exception in api log: none
   (first attempt hit a stale intheknow-prod_db volume from an earlier try: password auth failed; removed with down -v)
4  Trailing slash -> PublicBaseUrl named: yes   /app: yes   http://: yes
5  ss output: 127.0.0.1:5000, 127.0.0.1:3001 (WEB_PORT=3001); 0.0.0.0:3000 belongs to an unrelated older stack; no 5432
6  Port-in-use error: "Bind for 0.0.0.0:3000 failed: port is already allocated" (used the busy port 3000 instead of a python server)
7  Proxy: Cloudflare tunnel (see header)
8  "invite queued": yes   email arrived: yes   link opened "Accept your invite" at PUBLIC_URL: yes
9  Admin pages unlocked after two-step: yes
10 Unsubscribe/List-Unsubscribe start with PUBLIC_URL/n/: yes (List-Unsubscribe + List-Unsubscribe-Post: One-Click; the body has no unsubscribe link by design)
   settings link OK: yes   /n/ page via proxy: yes (real token, GET only, channel stayed active)
Total time, steps 1 to 9 (minutes): 66 wall clock (13:24:55 -> 14:31); ~15 hands-on. The rest was chat round-trips, the stale volume and a permission prompt. A1-A6 ~10 min; invite -> unlocked ~4 min.
Commands needed to go live, besides editing .env: 3 (docker compose up -d, make-admin, docker compose ps)
Anything unclear or wrong in DEPLOY.md: no Cloudflare tunnel section (route order, HTTPS/HSTS in Cloudflare, /api 404 route); runbook step 0 `git clone` fails on the private repo (used a git bundle)
```

**Bug found and fixed**: emails had no `Message-ID` header (SpamAssassin `MISSING_MID`; Gmail rejects such mail).
`System.Net.Mail` adds none. Fixed in `d68ca92` (`EmailChannel.cs`, plus two test assertions), and confirmed on a real
test email.

## T017: Proxy and exposure checks

```text
HTTP -> HTTPS (first line): HTTP/1.1 301 Moved Permanently -> https://test.example.com/  (Cloudflare "Always Use HTTPS")
HSTS header (paste): strict-transport-security: max-age=2592000  (1 month on purpose for the test host; DEPLOY.md's 12 months stays for production)
/api/me: 303 to /sign-in, body empty (yes), content-security-policy header present (no)  (no /api 404 route in the tunnel; the web gate answers, the API is not reachable)
/api:    303 to /sign-in, body empty (yes), content-security-policy header present (no)
/n/unsubscribe/x mentions InTheKnow (count): 1
Security headers present (which of the 4): all 4
nmap public address: 3000 open (unrelated older stack, 0.0.0.0:3000), 3001/5000/5432/8080 filtered
Spoofed sign-in: HTTP status: 200     recorded source: <my public IPv4>     your real public address: <my public IPv4>
Peer check, Docker: older row source: ::ffff:172.30.250.1     newer row source: 203.0.113.5
Two visitors: A throttled at attempt #: the 21st source failure (#16 of the run; the run used distinct email addresses, because with one address the per-account limit hits first, at #6)
              B signed in (yes/no): yes (phone on mobile data, <IPv6 on mobile data>)     SourceFailuresPerHour removed (yes/no): yes
Routed subnet, Docker: n/a (VPS, no LAN neighbour)
Caddy log token= count: n/a (no Caddy); cloudflared journal token= count: 0
```

Cloudflare HSTS had first been saved as `max-age=0; includeSubDomains; preload` (zone-wide). It's now 1 month, with no
subdomains and no preload.

## T029: Backup and restore

```text
Data before (calendars, filters, channels, changes): 1 calendar, 1 filter (default), 1 email channel, 0 changes (86 event snapshots; all 21 table counts noted)
Site kept answering during pg_dump (yes/no): yes (8/8 requests 200; dump took 0.6 s)
Dump size: 79 KB (79188 bytes)
pg_restore output (paste, or "none"): none (exit 0)
API came back healthy (yes/no): yes     migration or exception lines after restore: none
Signed in with two-step after restore (yes/no): yes
Data after (calendars, filters, channels, changes): identical; all 21 table counts match
```

## T020: Restarts, reboot and Podman

### Docker (server)

```text
Docker: kill -> healthy again: `docker compose kill api` -> NO, stays Exited (137): Docker skips the restart policy for a manual kill/stop (documented behaviour)
        in-container exit (`compose exec api bash -c "kill -TERM 1"`) -> yes, healthy in 7 s, RestartCount 0 -> 1
Docker: scale=2 -> second instance error: "Bind for 127.0.0.1:5000 failed: port is already allocated"     site kept working: yes
        BUT after `--scale api=1` the container that had failed to start was left with NO network and crash-looped
        ("Resource temporarily unavailable": DNS for `db` fails). `up -d` did not fix it; `up -d --force-recreate api` did.
Docker: db stopped -> api restarting: yes, web healthy: yes, the app's "Temporarily unavailable" page (503) for a signed-in visitor: yes
Docker: db started -> api healthy on its own: yes, 52 s
Docker: reboot -> all healthy: yes, 122 s     calendar check logged after reboot: yes (16:16:15, boot 16:15:44)
        the unrelated older stack and cloudflared came back too
```

### Podman 3.4.4 (server, Ubuntu 22.04 apt package, rootless): not supported

```text
podman compose:            does not exist in 3.4 (added in Podman 4.7); used podman-compose 1.6.0 directly
${VAR:?}:                  named, as a Python traceback; no containers created
volume / name:             intheknow-prod_db (yes)
subnet:                    172.30.250.0/24 refused while Docker's stack holds it ("network 172.30.250.0/24 is being used by a network interface"); used STACK_SUBNET=172.30.251.0/24
depends_on healthy:        IGNORED ("Ignored db condition check due to podman 3.4.4 doesn't support healthy!")
network:                   Ubuntu's CNI plugins (0.9.1) reject cniVersion 1.0.0 -> no published ports. Hand-patching to 0.4.0 fixes ports, but
container DNS:             `db` does not resolve (dnsname not wired for rootless; resolv.conf points at the host's resolvers) -> api cannot start
restart: always:           crashed api not restarted; a db stopped by `down` was restarted
Rootful:                   not tested (same 3.4.4; claude has no sudo for podman)
```

### Podman 6.1.3 (maintainer's dev machine, CachyOS, rootless, netavark + pasta)

```text
Podman version: 6.1.3
podman compose provider and version: ">>>> Executing external compose provider "/usr/lib/docker/cli-plugins/docker-compose" <<<<" / Docker Compose version 5.6.0
Mode tested: rootless
${VAR:?} names the setting: yes   volume intheknow-prod_db: yes   subnet 172.30.250.0/24: yes
api waited for healthy db: yes ("db Healthy" before "api Started")   all three healthy: yes, 39 s   make-admin "invite queued": yes
ss shows only 127.0.0.1:3002 and :5002 (WEB_PORT/API_PORT moved; dev db holds 5432): yes
A4 malformed PUBLIC_URL (/, /app, http://): PublicBaseUrl named, all three: yes
A6 port in use: "rootlessport listen tcp 127.0.0.1:5432: bind: address already in use"
Peer check rootless: older row: ::ffff:172.30.250.49 / .51 (pasta; inside STACK_SUBNET, changes per start)    newer row: 203.0.113.5
Peer check rootful:  not tested
Routed subnet rootful: not tested
kill -> healthy again: in-container exit -> yes, 13 s; `compose kill` -> stays exited (same as Docker)
db stopped/started behaves as Docker: yes (api crash-loops on "Name or service not known", 503 "Temporarily unavailable" page, healthy 13 s after `start db`)
Reboot after host step: not run (dev machine)
```

For `DEPLOY.md` § Podman (the T019 TODO): **Podman 4.7 or later** (for `podman compose`, netavark DNS and healthy
conditions). Verified with the `docker-compose` 5.6.0 provider on Podman 6.1.3. The `podman-compose` provider was only
tried on 3.4.4, where Podman itself is the blocker, so its minimum version remains unverified.

## T031: Dev and prod side by side (dev machine)

```text
Only db and mailpit started (yes/no): yes (clean clone of d68ca92, no .env anywhere)     git status clean (yes/no): yes, before and after
dotnet test (passed/failed counts): 458 passed, 0 failed, 6 skipped (464 total)     frontend check/test/build: pass (0 errors, 0 warnings / 137 tests / build OK)
Prod stack healthy with API_PORT=5001 while dev API on 5000 (yes/no): yes (both answered 401 on /api/me)
Volumes listed (paste): intheknow_db, intheknow-prod_db (plus unrelated intheknow_pgdata, k3d-intheknow-images)
Cleanup: down -v on the prod scratch stack only; dev volumes untouched
```

---

## T025: Publishing

Run on 2026-10-19 and 2026-10-20. The packages stay **private** during the beta (deviation from FR-002 "public registry"), so
every pull below is signed in with a `read:packages` token.

```text
Step 1: tag push v0.1.0 (10:24:17 UTC) -> checks passed: yes, publish passed: yes
        minutes from tag push to images published (SC-008, < 30): 5 min 17 s (release published 10:29:30)
        release assets present (compose.yaml, env.example): yes
        image lines in release compose.yaml: ghcr.io/wjouter/intheknow-api:0.1.0, ghcr.io/wjouter/intheknow-web:0.1.0 (+ postgres:18)
        packages made public: no (private beta)   linked to repo: yes (both Wjouter/InTheKnow-tifier, created fresh by this release)
        image index platforms: linux/amd64 + linux/arm64 for both (plus build provenance)
        anonymous pull: not run (private)   signed-in pull amd64: yes, arch amd64   arm64 hardware: not run
        stack from release files healthy: yes, 19 s (itk-server ~/itk-rel, project itk-rel, 172.30.252.0/24, ports 5003/3003;
                                          /api/me 401, web / 200; `compose images` shows the ghcr 0.1.0 images)
Step 2: release v0.1.1 created with `gh release create v0.1.1 --target main` (API, not the UI) -> Release run on the tag push,
        checks + publish passed, assets attached to the existing release, images tagged 0.1.1: yes
        release edit for existing v0.1.0 (notes edited 09:34:15) -> workflow run: none
Step 5: v0.1.2-broken -> run: none
        v0.1.2 -> backend checks failed on DifferTests.DeliberatelyBroken, frontend passed, publish skipped: yes
        no 0.1.2 image (package tags: 0.1.0, 0.1.1) and no v0.1.2 release: yes     tags and branch cleaned up: yes
Step 6: PR #9 showed CI only, no Release run: yes
        branch push (t025-broken-check): no run at all. CI runs only on pushes to main and on pull requests (ci.yml), so
        "CI only" holds in the sense that no Release run started
Step 7: not run yet (needs a Docker Hub or Quay account)
```

Before this, the old app's stack (`/opt/intheknow`, project `intheknow`) was stopped, not removed, and backed up to
`itk-server:~/backup-old-intheknow-2026-10-20` (720 MB): `pg_dumpall`, a `pg_dump -Fc` of the `intheknow` database, a tar of
the `intheknow_pgdata` volume taken with the database stopped, a copy of `/opt/intheknow` (with its `.env` files), `docker save`
of its six images (no longer on GHCR) and `docker inspect` output, with `SHA256SUMS`.

## T026: Upgrade and unreachable registry

Run on 2026-10-20 on `itk-server`, in place on the `~/itk` test stack, so its data never left that stack. First switched
from the local `0.0.0-test` images to the `v0.1.0` release `compose.yaml` (same code), backup `pre-release-0.1.0.dump` first.

```text
Data before: 1 account, 1 calendar, 1 channel, 11 deliveries; all 21 table counts saved (counts-before.txt)
pull + up time: pull 17.0 s, up -d 2.1 s, all three healthy 12.9 s after up     downtime (SC-005, < 2 min): one failed probe
                (502) out of 32 one-second probes of https://test.example.com/ through Cloudflare
All data kept: yes, all 21 table counts identical     api exceptions or migration failures: none
.env md5 unchanged: yes
Registry blocked: 0.1.0 images removed, then `docker compose -f compose-blocked.yaml pull` with ghcr.io renamed to ghcr.invalid:
                  "Error Get "https://ghcr.invalid/v2/": dial tcp: lookup ghcr.invalid ... no such host", exit 255
                  0.1.1 site kept serving: yes (200, all healthy)
hosts entry removed and 0.1.1 compose.yaml restored: n/a (no hosts entry; compose.yaml stayed 0.1.1 throughout)
```

The `~/itk` test stack now runs `v0.1.1` from the release files (`compose.yaml`); `compose-local.yaml` is the old local one.
The empty `~/itk-rel` stack from T025 is removed (`down -v`; it only held what the release start created).

## Still to do

1. **F: merge.** Push `006-docker-deployment` (it now includes `d68ca92`), open the PR with the blocks above, check CI shows
   no Release run, then merge.
2. **G: T025, publishing.** Watch out: the older app already publishes `ghcr.io/wjouter/intheknow-api` (`:latest`, plus
   `intheknow-frontend`). The new release pushes version tags into that **same package**, so check its visibility and repository link
   before making it public.
3. **H: T026, upgrade.** On this server, use a separate directory and `STACK_SUBNET`/ports if the `~/itk` test stack is still running.
4. **Not run**: rootful Podman, the routed-subnet check (no LAN neighbour), and the Podman reboot. Decide whether to run them or to mark them as
   not supported/not tested in `DEPLOY.md`.

## Fixes to apply

**`DEPLOY.md`**
- A "Behind a Cloudflare tunnel" subsection under Reverse proxy: the `^/n/` route above the catch-all (first match wins),
  "Always Use HTTPS" and HSTS as Cloudflare settings (warn against zone-wide `includeSubDomains`/`preload`), an optional
  `^/api(/|$)` → `http_status:404` route. Cloudflare appends the visitor to `X-Forwarded-For`, which `XFF_DEPTH=1` reads correctly
  (verified with the spoof test).
- § Podman: minimum Podman 4.7; Ubuntu 22.04's 3.4.4 is unsupported, with the reasons; fill in the provider/version TODO.
- § Troubleshooting:
  - API restarts with `Resource temporarily unavailable` / no network after a failed start (e.g. a scale attempt) → `docker compose up -d --force-recreate api`.
  - Podman's subnet error wording: "is being used by a network interface" → `STACK_SUBNET` (two runtimes on one host).
  - Web interface answers 500 with "Address header … is absent" in its log → it's being reached without the proxy.
  - `docker compose stop`/`kill` doesn't trigger `restart: always` (expected); a crash or a reboot does.

**`manual-runbook.md` / `quickstart.md`**
- Step 0: `git clone` needs credentials for a private repo (or use a git bundle).
- A1: with an empty `.env`, `compose ps -a` prints the errors instead of an empty list.
- B two visitors: use a different email per attempt, or the per-account limit (5) masks the source limit.
- D1: crash from inside (`compose exec api bash -c "kill -TERM 1"`) instead of `compose kill`.
- D3: the page is titled "Temporarily unavailable".
- Direct requests to the web port need an `X-Forwarded-For` header.

**Outside 006**
- The older stack's port `0.0.0.0:3000` is reachable from the internet.

## Cleanup left on the server

- `~/itk`: the Docker test stack (running, with the admin account and test data).
- `~/itk-podman` plus rootless Podman images `localhost/intheknow-{api,web}:0.0.0-test` and the Podman volume `intheknow-prod_db`.
- `~/InTheKnow` (bundle clone) and `~/itk.bundle`.
- Docker images `localhost/intheknow-{api,web}:0.0.0-test`.
