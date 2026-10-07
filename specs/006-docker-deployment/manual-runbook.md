# Manual runbook: open tasks of 006

These are the tasks that need a real server, a second machine, tags or a registry: T013, T017, T020, T025, T026,
T029 and T031. Run them in the order below. Each step points to the [quickstart](./quickstart.md) section it comes
from, and ends with a **Report back** block.

**How to report back**: copy each filled-in Report back block into the chat. Fill every line, and write `n/a` with a
reason when a line doesn't apply. Paste error output as-is.

- **Redact**: passwords, `.env` contents, invite and confirmation links, session tokens, `/n/…` tokens, and anything
  after `token=`.
- **Fine to leave in**: your domain and IP addresses. Replace them with `<domain>` / `<ip>` if you prefer.
- A task is ticked only when every line of its block says it passed, or the fix is agreed. If something fails, send
  the block anyway: the fallbacks are in research R7, R8 and R14, and I'll apply them to `deploy/compose.yaml` or
  `DEPLOY.md`.

## Order at a glance

| # | Task | Where | Needs |
|---|---|---|---|
| A | T013 First start and first admin | server | domain, Caddy, real SMTP, locally built images |
| B | T017 Proxy and exposure checks | server, a second machine, a LAN neighbour | A |
| C | T029 Backup and restore | server | A, plus a calendar with data |
| D | T020 Restarts, reboot and Podman | server, a Podman machine or VM | A |
| E | T031 Dev and prod side by side | your dev machine | nothing |
| F | Merge the branch | GitHub | A to E pass |
| G | T025 Publishing | GitHub, an arm64 machine, another registry | F |
| H | T026 Upgrade and unreachable registry | server | G steps 1 and 2 |

---

## 0. Prepare the server (once)

```bash
docker version --format '{{.Server.Version}}'      # must be 28 or later
uname -m                                           # x86_64 or aarch64
timedatectl | grep -i synchronized                 # yes
# The repository is private: on the dev machine, `git bundle create itk.bundle 006-docker-deployment` and copy it over
git clone -b 006-docker-deployment itk.bundle InTheKnow && cd InTheKnow
IMAGE=localhost/intheknow VERSION=0.0.0-test
docker build -f backend/Dockerfile  -t "$IMAGE-api:$VERSION" .
docker build -f frontend/Dockerfile -t "$IMAGE-web:$VERSION" .
mkdir -p ~/itk && sed -e "s|ghcr.io/wjouter/intheknow|$IMAGE|" -e "s|:VERSION|:$VERSION|" deploy/compose.yaml > ~/itk/compose.yaml
cp deploy/env.example ~/itk/.env && chmod 600 ~/itk/.env
```

Point the domain at the server, open ports 80 and 443, and install Caddy. From here on, every `docker compose`
command runs in `~/itk`.

**Report back (0)**

```text
Docker version:
Architecture:
Time sync on (yes/no):
Both images built without errors (yes/no):
```

---

## A. T013: First start and first admin (quickstart §3, §4)

Start a timer now: SC-001 asks for under 30 minutes from the downloaded files to an unlocked admin.

1. **Missing settings**: leave `.env` as copied, then run `docker compose up -d`.
   Expected: it fails and names a required setting, and no containers exist (`docker compose ps -a` repeats the error).
2. **Fill in** `PUBLIC_URL=https://<domain>`, `POSTGRES_PASSWORD` (from `openssl rand -hex 32`), `SMTP_HOST`,
   `SMTP_FROM`, `SMTP_USERNAME`, and `SMTP_PASSWORD='<real password>'` in single quotes.
3. Run `docker compose up -d`, then `watch docker compose ps`.
   Expected: all three are `healthy` within 2 minutes, and `docker compose logs api` shows no exception. The
   `libgssapi_krb5` line and the DataProtection warnings are expected.
4. **Malformed address**: set `PUBLIC_URL=https://<domain>/`, then `docker compose up -d`.
   Expected: `api` keeps restarting, and `docker compose logs api` names `InTheKnow:Notifications:PublicBaseUrl`.
   Repeat with `https://<domain>/app` and with `http://<domain>`. Then restore the correct value and run `up -d`.
5. **Ports**: run `ss -tlnp | grep -E ':(3000|5000|5432)\b'`.
   Expected: only `127.0.0.1:3000` and `127.0.0.1:5000`.
6. **Port in use**: run `python3 -m http.server 3001 --bind 127.0.0.1 &`, set `WEB_PORT=3001`, then `up -d`.
   Expected: the error names the port. Then `kill %1`, remove `WEB_PORT`, and run `up -d`.
7. **Proxy**: put the `DEPLOY.md` Caddyfile in `/etc/caddy/Caddyfile` with your domain, then
   `sudo systemctl reload caddy`.
8. **First admin**: run `docker compose exec api dotnet InTheKnow.dll make-admin <your address>`.
   Expected: `invite queued` (the `libgssapi_krb5` lines before it are harmless), an email arrives, and its link opens "Accept your invite" at `PUBLIC_URL`.
9. Accept the invite, set up two-step verification, and check that the admin pages unlock. **Stop the timer.**
10. **Email links**: add an email channel for your address, confirm it, then send a test email. Expected:
    - its unsubscribe link and `List-Unsubscribe` header start with `https://<domain>/n/`;
    - its settings link starts with `https://<domain>`;
    - opening the unsubscribe link shows the API's page, not a proxy error. Don't confirm the unsubscribe.

**Report back (T013)**

```text
1  Missing settings: error message (first line):            ps -a empty (yes/no):
3  All healthy within 2 min (yes/no, time):                 exception in api log (none / paste):
4  Trailing slash -> PublicBaseUrl named (yes/no):  /app (yes/no):  http:// (yes/no):
5  ss output (paste):
6  Port-in-use error (paste first line):
8  "invite queued" (yes/no):  email arrived (yes/no):  link opened "Accept your invite" at PUBLIC_URL (yes/no):
9  Admin pages unlocked after two-step (yes/no):
10 Unsubscribe/List-Unsubscribe start with PUBLIC_URL/n/ (yes/no):  settings link OK (yes/no):  /n/ page via proxy (yes/no):
Total time, steps 1 to 9 (minutes):
Commands needed to go live, besides editing .env (expected 3: up, make-admin, ps):
Anything unclear or wrong in DEPLOY.md:
```

---

## B. T017: Proxy and exposure checks (quickstart §5)

### From another machine (not the server, preferably off your LAN)

```bash
D=https://<domain>
curl -sI http://<domain>/ | head -1
curl -sI $D/ | grep -i '^strict-transport-security'
curl -s -D - $D/api/me
curl -s -D - $D/api
curl -s $D/n/unsubscribe/x | grep -ci intheknow
curl -sI $D/ | grep -iE '^(x-frame-options|referrer-policy|x-content-type-options|content-security-policy):'
nmap -Pn -p 3000,5000,5432,8080 <server public address>
```

**Spoofed address**: send a wrong sign-in through the proxy with a forged header (use your admin address):

```bash
curl -s -o /dev/null -w '%{http_code}\n' -H 'Sec-Fetch-Site: same-origin' -H 'X-Forwarded-For: 198.51.100.7' \
  --data-urlencode 'email=<your address>' --data-urlencode 'password=wrong-password-123' $D/sign-in
```

Then, on the server:

```bash
docker compose exec db psql -U intheknow -c "SELECT type, host(source), occurred_at FROM security_event ORDER BY id DESC LIMIT 3"
```

Expected: the newest `signInFailed` row has this machine's public address, not `198.51.100.7`.

### On the server: peer address through the published port

Do this once per runtime (Docker now; Podman in D):

```bash
curl -s -o /dev/null -H 'Content-Type: application/json' -d '{"email":"<your address>","password":"wrong-password"}' http://127.0.0.1:5000/api/auth/sign-in
curl -s -o /dev/null -H 'Content-Type: application/json' -H 'X-Forwarded-For: 203.0.113.5' -d '{"email":"<your address>","password":"wrong-password"}' http://127.0.0.1:5000/api/auth/sign-in
docker compose exec db psql -U intheknow -c "SELECT type, host(source) FROM security_event ORDER BY id DESC LIMIT 2"
```

Expected: the older row is in `172.30.250.0/24` or `127.0.0.1`, and the newer row is `203.0.113.5`.

### Two visitors

1. Add `InTheKnow__Accounts__SourceFailuresPerHour=20` to `.env`, then run `docker compose up -d`.
2. On machine A, run the spoofed-address `curl` above 25 times, without the `X-Forwarded-For` header:
   `for i in $(seq 25); do curl … ; done`, with a different `email=` each time (one address hits the 5-per-account
   limit first). The status changes once the source is throttled. Note at which attempt.
3. On machine B (another public address, for example a phone off Wi-Fi), sign in normally in a browser.
   Expected: it works.
4. Remove the line from `.env`, then run `docker compose up -d`.

### From a LAN neighbour: routed container subnet

Use a machine on the same LAN segment as the server:

```bash
sudo ip route add 172.30.250.0/24 via <server LAN address>
nmap -Pn -p 3000,5432,8080 172.30.250.0/28
sudo ip route del 172.30.250.0/24
```

Expected: every port `filtered`. Repeat this when the stack runs on rootful Podman (D).

### Logs

On the server, run `sudo journalctl -u caddy --since today | grep -c 'token='`. Expected: `0`.

**Report back (T017)**

```text
HTTP -> HTTPS (first line):
HSTS header (paste):
/api/me: status, body empty (yes/no), content-security-policy header present (yes/no):
/api: status, body empty (yes/no), content-security-policy header present (yes/no):
/n/unsubscribe/x mentions InTheKnow (count):
Security headers present (which of the 4):
nmap public address (paste port lines):
Spoofed sign-in: HTTP status:     recorded source:     your real public address:
Peer check, Docker: older row source:          newer row source:
Two visitors: A throttled at attempt #:     B signed in (yes/no):     SourceFailuresPerHour removed (yes/no):
Routed subnet, Docker (paste nmap port lines):
Caddy log token= count:
```

---

## C. T029: Backup and restore (quickstart §6)

1. **Add data**: as the admin, add a calendar (a MyTimetables link), a filter and a channel. Wait until at least
   one check has run (default every 10 minutes) and, if you can, until a change has been recorded. Note what you
   see: calendar names, the number of changes.
2. Keep the site open in a browser, then run:

   ```bash
   docker compose exec -T db pg_dump -U intheknow -Fc intheknow > before.dump
   ```

   While it runs, reload the site: it must keep answering.
3. Restore:

   ```bash
   docker compose down -v
   docker compose up -d db                   # wait until `docker compose ps` shows db healthy
   docker compose exec -T db pg_restore -U intheknow -d intheknow --no-owner --exit-on-error < before.dump
   docker compose up -d
   docker compose logs api | grep -iE 'migrat|exception'
   ```

4. Sign in fully (password and two-step), and compare with what you noted in step 1.

**Report back (T029)**

```text
Data before (calendars, filters, channels, changes):
Site kept answering during pg_dump (yes/no):
Dump size:
pg_restore output (paste, or "none"):
API came back healthy (yes/no):     migration or exception lines after restore (paste or "none"):
Signed in with two-step after restore (yes/no):
Data after (calendars, filters, channels, changes):
```

---

## D. T020: Restarts, reboot and Podman (quickstart §7, §8)

### Docker, on the server (quickstart §7)

1. `docker compose exec api bash -c "kill -TERM 1"` (a crash from inside), then wait. Expected: it restarts and
   becomes `healthy`. A `docker compose kill` or `stop` does not trigger `restart: always`.
2. `docker compose up -d --scale api=2`. Expected: the second instance fails (port in use or instance lock), and
   the site keeps working. Then run `docker compose up -d --scale api=1 --force-recreate api`
   (without `--force-recreate`, the failed instance can keep running without a network).
3. **Database down while the API starts**:
   1. `docker compose stop db`, then `docker compose restart api`.
   2. Watch `docker compose ps` for a minute: `api` keeps restarting, `web` stays `healthy`, and the site shows the
      "Temporarily unavailable" page (503), not a proxy error.
   3. `docker compose start db`. Expected: `api` becomes healthy on its own, and the page loads again on reload.
4. **Reboot**: run `sudo reboot`, and time it from the reboot until all three are `healthy` again. Then check the
   API log for a calendar check after the reboot.

### Podman (quickstart §8), on a second machine or VM

Do this rootless, and rootful too if you want to support it.

1. Install Podman, plus `podman-compose` or `docker-compose`. Record what `podman compose version` prints, including
   the provider line.
2. Build the images with `podman build` (the same two lines as in 0), and render `~/itk` as in 0.
3. Repeat A steps 1 to 5 and step 8 (SMTP can be the same server; the proxy isn't needed). Check that:
   - the missing setting is named (`${VAR:?}`);
   - the volume is called `intheknow-prod_db` (`podman volume ls`, which proves the top-level `name:`);
   - the network has subnet `172.30.250.0/24` (`podman network inspect intheknow-prod_stack | grep subnet`);
   - `api` started only after `db` was healthy (`condition: service_healthy`).
4. Run the B peer-address check, with `podman compose exec db psql …`.
5. **Rootful only**: run the B routed-subnet check from a LAN neighbour.
6. Run steps 1 and 3 of the Docker part above, with `podman compose`.
7. Run the one-time host step from `DEPLOY.md` § Podman (rootless: both commands), then reboot and time it as in
   Docker step 4.

**Report back (T020)**

```text
Docker: kill -> healthy again (yes/no, seconds):
Docker: scale=2 -> second instance error (paste first line):     site kept working (yes/no):
Docker: db stopped -> api restarting (yes/no), web healthy (yes/no), "couldn't reach the server" page (yes/no):
Docker: db started -> api healthy on its own (yes/no, seconds):
Docker: reboot -> all healthy (yes/no, seconds):     calendar check logged after reboot (yes/no):

Podman version:
podman compose provider and version (paste `podman compose version`):
Mode tested: rootless / rootful / both
${VAR:?} names the setting (yes/no):   volume intheknow-prod_db (yes/no):   subnet 172.30.250.0/24 (yes/no):
api waited for healthy db (yes/no):    all three healthy (yes/no):    make-admin "invite queued" (yes/no):
ss shows only 127.0.0.1:3000 and :5000 (yes/no):
Peer check rootless: older row:          newer row:
Peer check rootful:  older row:          newer row:
Routed subnet rootful (paste nmap port lines):
kill -> healthy again (yes/no):    db stopped/started behaves as Docker (yes/no):
Reboot after host step: all healthy (yes/no, seconds), per mode:
```

---

## E. T031: Dev and prod side by side (quickstart §10), on your dev machine

1. On a clean checkout of the branch, follow [QUICKSTART](../../QUICKSTART.md) from step 1, with no `.env` anywhere.
   Run `docker compose up -d db mailpit`. Expected: only those two start, and `git status` stays clean.
2. Run `dotnet test`, and `cd frontend && npm run check && npm test && npm run build`.
3. With the dev API running on port 5000, start a prod stack in `~/itk` from locally built images (as in 0), with
   `API_PORT=5001` and dummy required settings (`PUBLIC_URL=https://intheknow.example.nl`, `SMTP_HOST=smtp.example.nl`,
   `SMTP_FROM=a@example.nl`, a random password). Wait for `healthy`.
4. Run `docker volume ls | grep intheknow`. Expected: `intheknow_db` and `intheknow-prod_db`, separate.
5. Clean up: `docker compose down -v` in `~/itk` only. **Never `-v` on the dev stack.**

**Report back (T031)**

```text
Only db and mailpit started (yes/no):     git status clean (yes/no):
dotnet test (passed/failed counts):     frontend check/test/build (pass/fail):
Prod stack healthy with API_PORT=5001 while dev API on 5000 (yes/no):
Volumes listed (paste):
```

---

## F. Merge the branch (no report needed)

1. Push `006-docker-deployment` and open a pull request. CI must pass, and the PR must show **no** Release run.
   That covers G step 6 for pull requests.
2. Put the results of A to E in the PR description (T013 asks for this; I can write it from your report blocks).
3. Merge into `main`. Tags in G must point at a commit that contains `release.yml`.

---

## G. T025: Publishing (quickstart §9 steps 1, 2, 5, 6, 7)

1. **Tag from the command line**: on `main`, run `git tag v0.1.0 && git push origin v0.1.0`. Note the time.
   - Expected: the Release workflow runs `checks`, then `publish`. The release `v0.1.0` gets `compose.yaml` and
     `env.example`. `grep image: compose.yaml` shows `ghcr.io/wjouter/intheknow-{api,web}:0.1.0`.
   - **One-time step**: on GitHub, go to Packages, open `intheknow-api` and then `intheknow-web`, and in Package
     settings set the visibility to Public. Check that each is linked to the repository.
   - **Anonymous pull** on an amd64 and an arm64 machine, signed out (`docker logout ghcr.io`):
     `docker pull ghcr.io/wjouter/intheknow-api:0.1.0`, the same for `-web`, and
     `docker image inspect --format '{{.Architecture}}' ghcr.io/wjouter/intheknow-api:0.1.0`.
   - Start that release's stack on a clean machine (DEPLOY.md § First start, with `vX.Y.Z` = `v0.1.0`), and check that
     all three become `healthy`.
2. **Release from the GitHub UI** with a new tag: Releases, then Draft a new release, then "Create new tag"
   `v0.1.1` on `main`, then Publish. Expected: the same result as step 1, for `0.1.1`.
   Then edit or publish a release for the **existing** tag `v0.1.0` (for example, edit its notes and save).
   Expected: **no** new workflow run.
3. **Failing check** (step 5): create a throwaway branch from `main`, break one test (for example, an
   `Assert.True(false)`), commit, and push the branch.
   1. `git tag v0.1.2-broken && git push origin v0.1.2-broken`. Expected: **no** Release run (not a version tag).
   2. `git tag v0.1.2 && git push origin v0.1.2`. Expected: `checks` fails, `publish` is skipped, there is no
      `0.1.2` image and no `v0.1.2` release.
   3. Clean up: `git push origin :v0.1.2 :v0.1.2-broken`, `git tag -d v0.1.2 v0.1.2-broken`, and delete the branch.
4. **Branch push** (step 6): the PR in F covered pull requests. Confirm that pushing a branch runs only CI.
5. **Manual publish to another registry** (step 7), for example Docker Hub or Quay. In a clean clone of `main`:

   ```bash
   IMAGE=<registry>/<you>/intheknow VERSION=0.1.0-manual
   docker build -f backend/Dockerfile  -t "$IMAGE-api:$VERSION" .
   docker build -f frontend/Dockerfile -t "$IMAGE-web:$VERSION" .
   docker login <registry>
   docker push "$IMAGE-api:$VERSION" && docker push "$IMAGE-web:$VERSION"
   sed -e "s|ghcr.io/wjouter/intheknow|$IMAGE|" -e "s|:VERSION|:$VERSION|" deploy/compose.yaml > compose.yaml
   ```

   Start it on a server of the same processor type, with a `.env`, and check that the images come from that
   registry (`docker compose images`).

**Report back (T025)**

```text
Step 1: tag push -> checks passed (yes/no), publish passed (yes/no)
        minutes from tag push to images published (SC-008, < 30):
        release assets present (compose.yaml, env.example) (yes/no):
        image lines in release compose.yaml (paste):
        packages made public (yes/no), linked to repo (yes/no):
        anonymous pull amd64 (arch printed):     arm64 (arch printed):
        stack from release files healthy (yes/no):
Step 2: UI release v0.1.1 -> published like step 1 (yes/no):
        release edit for existing v0.1.0 -> workflow run (none / paste):
Step 5: v0.1.2-broken -> run (none / paste):    v0.1.2 -> checks failed, publish skipped (yes/no)
        no 0.1.2 image and no v0.1.2 release (yes/no):     tags and branch cleaned up (yes/no):
Step 6: PR showed CI only, no Release run (yes/no):     branch push CI only (yes/no):
Step 7: registry used:     push OK (yes/no):     stack from that registry healthy (yes/no):
        `docker compose images` (paste):
```

---

## H. T026: Upgrade and unreachable registry (quickstart §9 steps 3, 4)

Use a server running `v0.1.0` from the release files, with data (an account, a calendar, a filter, a preset, a
channel, a change). Note the data, and `md5sum .env`.

1. **Upgrade** to `v0.1.1`, following DEPLOY.md § Upgrade and rollback:

   ```bash
   docker compose exec -T db pg_dump -U intheknow -Fc intheknow > pre-0.1.1.dump
   mv compose.yaml compose-0.1.0.yaml
   curl -LO https://github.com/Wjouter/InTheKnow/releases/download/v0.1.1/compose.yaml
   time (docker compose pull && docker compose up -d)
   ```

   Then measure the downtime: run `while ! curl -sf -o /dev/null https://<domain>/; do sleep 1; done` from another
   terminal, started just before `up -d`, or time the gap until `docker compose ps` is all `healthy`.
   Check the data, then `md5sum .env`.
2. **Registry unreachable**:
   1. `echo '0.0.0.0 ghcr.io' | sudo tee -a /etc/hosts`
   2. `cp compose.yaml compose-0.1.1.yaml && cp compose-0.1.0.yaml compose.yaml`
   3. `docker image rm ghcr.io/wjouter/intheknow-api:0.1.0 ghcr.io/wjouter/intheknow-web:0.1.0`
   4. `docker compose pull`. Expected: it fails, and the running `0.1.1` site keeps working.
   5. Undo: remove the line from `/etc/hosts`, then `cp compose-0.1.1.yaml compose.yaml`. Don't run `up` with the
      0.1.0 file.

**Report back (T026)**

```text
Data before:
pull + up time:          downtime (SC-005, < 2 min):
All data kept (accounts, calendars, filters, presets, channels, changes) (yes/no, or what's missing):
.env md5 unchanged (yes/no):
Registry blocked: pull error (paste first lines):     0.1.1 site kept serving (yes/no):
hosts entry removed and 0.1.1 compose.yaml restored (yes/no):
```

---

## Cleanup when everything is done

- Remove the test stacks: `docker compose down -v` in each `~/itk`, but only for stacks whose data you don't need.
- Remove the local test images: `docker image rm localhost/intheknow-api:0.0.0-test localhost/intheknow-web:0.0.0-test`.
- If you don't want `0.1.0-manual` in the other registry, delete it there.
- Decide whether `v0.1.0` and `v0.1.1` stay as real releases, or get deleted before the first real one.

## What I do with your reports

- Tick T013, T017, T020, T025, T026, T029 and T031 in `tasks.md`.
- Fill the `podman-compose` version TODO in `DEPLOY.md` § Podman from the T020 block.
- Apply any fallback a failed line calls for (R7 trusted peer or firewall rule, R8 restart policy, R14 Podman), and
  update `DEPLOY.md` to match.
- Write the PR description from the A to E blocks, if you want.
