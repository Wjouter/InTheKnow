# Contract: The production stack definition

This is `deploy/compose.yaml`, a template in the repository. It is rendered per release as `compose.yaml`
([images.md](./images.md#stack-definition-per-release)). The operator never edits it. Decisions are in research
R7 to R9, R13 and R14.

## Project

- `name: intheknow-prod`. The volume is `intheknow-prod_db` wherever the directory lives. It never collides with the
  dev stack (`intheknow`).
- One network, `stack`, with subnet `${STACK_SUBNET:-172.30.250.0/24}`.
- One named volume, `db`.
- No proxy, no mail server and no Mailpit (FR-006, FR-030).

## Services

| | `db` | `api` | `web` |
|---|---|---|---|
| Image | `docker.io/library/postgres:18` | `<IMAGE>-api:<version>` | `<IMAGE>-web:<version>` |
| Published | none | `127.0.0.1:${API_PORT:-5000}:8080` | `127.0.0.1:${WEB_PORT:-3000}:3000` |
| Settings | `POSTGRES_USER=intheknow`, `POSTGRES_DB=intheknow`, `POSTGRES_PASSWORD` | `env_file: .env` + the environment below | the environment below only |
| Healthcheck | `pg_isready -U intheknow -d intheknow` | TCP connect to `127.0.0.1:8080` (bash) | TCP connect to `127.0.0.1:3000` (node) |
| Start | first | `depends_on: db: condition: service_healthy` | no dependency |
| Restart | `always` | `always` | `always` |
| Storage | `db:/var/lib/postgresql` | none (the API writes no files) | none |
| User | the image's `postgres` | `app` (non-root) | `node` (non-root) |

Each healthcheck uses `interval: 10s`, `timeout: 5s` and `retries: 6`. The API's `start_period` is `30s`, to allow
for migrations.

## API environment (stack-owned)

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

## Web environment (stack-owned)

```yaml
ORIGIN: ${PUBLIC_URL:?PUBLIC_URL is required, see DEPLOY.md}
API_URL: http://api:8080
ADDRESS_HEADER: X-Forwarded-For
XFF_DEPTH: "1"
HOST: 0.0.0.0
PORT: "3000"
```

## Guarantees

- **Exposure**: nothing listens on a non-loopback host address (SC-003). The database is reachable only from the
  stack network.
- **Trusted proxies**: the API trusts `X-Forwarded-For` from the stack subnet and loopback only (FR-016, research
  R7).
- **One API instance**: the advisory lock plus the fixed host port (FR-022).
- **State**: the `db` volume only (FR-023). `docker compose down` keeps it. `down -v` deletes it.
- **Recovery**: it recovers from crashes and from reboots (Docker), and from reboots on Podman once the documented
  host step has been done (FR-019).
- **Required settings**: a missing one fails `up` before any container is created, naming the setting (US1 AS5).

## Commands (operator interface)

Run these from the directory that holds `compose.yaml` and `.env`. `podman compose` takes the same arguments.

| Task | Command |
|---|---|
| Start / apply a new definition | `docker compose up -d` |
| Status | `docker compose ps` |
| First admin | `docker compose exec api dotnet InTheKnow.dll make-admin <address> [--language en\|nl]` |
| Logs | `docker compose logs [-f] [db\|api\|web]` |
| Stop (keep data) | `docker compose down` |
| Download new images | `docker compose pull` |
| Backup | `docker compose exec -T db pg_dump -U intheknow -Fc intheknow > <file>.dump` |
| Restore (fresh volume only) | `docker compose up -d db`, then `docker compose exec -T db pg_restore -U intheknow -d intheknow --no-owner --exit-on-error < <file>.dump`, then `docker compose up -d` |
