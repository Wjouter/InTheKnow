# Deploying InTheKnow

InTheKnow runs in production as one container stack: PostgreSQL, the API and the web interface, behind your own
TLS reverse proxy on the same host. For local development, see [QUICKSTART](QUICKSTART.md) instead.

Every command below runs in the directory that holds `compose.yaml` and `.env`. `podman compose` takes the same
arguments as `docker compose`.

## Prerequisites

- A Linux server, amd64 or arm64, with **time sync** enabled. Two-step verification codes depend on the clock.
- **Docker Engine with Compose v2, version 28 or later** (check with `docker version --format '{{.Server.Version}}'`).
  Older engines let other hosts on the same LAN reach loopback-published ports and container addresses directly,
  the database included. Or **Podman** with `podman compose` (see [Podman](#podman), including the rootful check).
- A TLS reverse proxy on the same host. [Reverse proxy](#reverse-proxy) has a worked Caddy example.
- A domain pointing at the server.
- An SMTP server with STARTTLS.

## Settings

All settings live in one file, `.env`, next to `compose.yaml`. Copy it from the release's `env.example`.

- One `NAME=value` per line. `#` starts a comment.
- Wrap a value in **single quotes** if it contains `$`, `#`, a space or a quote: `SMTP_PASSWORD='pa$s #1'`.
  Compose expands `$` in unquoted and double-quoted values.
- Run `chmod 600 .env`. It holds the database and SMTP passwords.
- Upgrades never change this file.

### Required

The stack refuses to start, before creating anything, while one of these is empty. The error reads
`<NAME> is required, see DEPLOY.md`.

| Name | Example | Used for |
|---|---|---|
| `PUBLIC_URL` | `https://intheknow.example.nl` | the address users open, and every link in emails |
| `POSTGRES_PASSWORD` | output of `openssl rand -hex 32` | the database password |
| `SMTP_HOST` | `smtp.example.nl` | outgoing email |
| `SMTP_FROM` | `intheknow@example.nl` | sender address of every email |

`PUBLIC_URL` is an **origin only**: `https://`, the host and an optional port. No path, no trailing `/`, no query
and no fragment. The API checks this at startup. If its log names `InTheKnow:Notifications:PublicBaseUrl` or
`InTheKnow:Notifications:FrontendBaseUrl`, fix `PUBLIC_URL`: both are set from it.

### Optional

| Name | Default | Used for |
|---|---|---|
| `SMTP_PORT` | `587` | SMTP port |
| `SMTP_USERNAME` | empty | SMTP sign-in; set together with the password |
| `SMTP_PASSWORD` | empty | SMTP sign-in |
| `WEB_PORT` | `3000` | host port of the web interface, on `127.0.0.1` |
| `API_PORT` | `5000` | host port of the API, on `127.0.0.1` (the proxy sends `/n/…` here) |
| `STACK_SUBNET` | `172.30.250.0/24` | the stack's network and the API's trusted proxies. Change it only if `up` reports that the pool overlaps. |

Any other API setting can be set under its environment name, for example `InTheKnow__CheckInterval=00:10:00`.
`env.example` lists the common ones. All keys are in the configuration contracts of
[001](specs/001-ical-change-detection/contracts/configuration.md),
[003](specs/003-notification-channels/contracts/configuration.md) and
[004](specs/004-accounts-login/contracts/configuration.md).

### Set by the stack

These are set in `compose.yaml` and override `.env`, so setting them there has no effect. Use the named settings
above instead.

- `ConnectionStrings__Default`, `PGPASSWORD`, `ASPNETCORE_HTTP_PORTS`
- `InTheKnow__Accounts__TrustedProxies__0` to `__2`
- `InTheKnow__Notifications__PublicBaseUrl`, `InTheKnow__Notifications__FrontendBaseUrl`
- `InTheKnow__Notifications__Email__Host`, `__Port`, `__Username`, `__Password`, `__FromAddress`

### Changing the database password

PostgreSQL reads `POSTGRES_PASSWORD` only when the volume is first created. To change it later:

```bash
docker compose exec db psql -U intheknow -c "ALTER USER intheknow PASSWORD '<new>'"
```

Then put the new value in `.env` and run `docker compose up -d`.

## First start

Replace `X.Y.Z` with the release you want.

```bash
mkdir intheknow && cd intheknow
curl -LO https://github.com/Wjouter/InTheKnow/releases/download/vX.Y.Z/compose.yaml
curl -LO https://github.com/Wjouter/InTheKnow/releases/download/vX.Y.Z/env.example
cp env.example .env
chmod 600 .env
```

Fill in the required settings in `.env`, then:

```bash
docker compose up -d
docker compose ps
```

Repeat `docker compose ps` until `db`, `api` and `web` all show `healthy`. The API applies its database migrations
before it reports healthy.

## Reverse proxy

The stack listens on `127.0.0.1` only. Your proxy must:

- terminate TLS and redirect HTTP to HTTPS;
- send `Strict-Transport-Security: max-age=31536000`;
- route `/n/…` to the API (`127.0.0.1:${API_PORT}`), answer `404` for `/api` and `/api/…`, and send everything else
  to the web interface (`127.0.0.1:${WEB_PORT}`);
- replace any client-sent `X-Forwarded-For` with the real client address;
- pass the `Host` header through;
- not strip the app's security headers;
- not log the query strings of `/account/…` requests (they carry one-time tokens).

### Caddy

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

How it meets each rule:

- **TLS and redirect**: Caddy gets a certificate and redirects HTTP for a named site automatically.
- **HSTS**: the `header` line.
- **Routing**: `handle` blocks are mutually exclusive, and the first match wins. `respond` sits **inside** a
  `handle` on purpose: Caddy sorts `handle` before `respond`, so a top-level `respond @api 404` would never run and
  `/api/…` would reach the web interface.
- **Forwarded address**: Caddy replaces a client-sent `X-Forwarded-For` with the real peer, unless the peer is in its
  own `trusted_proxies`, which is empty by default.
- **Host**: passed through by default.
- **Security headers**: Caddy doesn't strip response headers unless told to.
- **Query strings**: Caddy writes no access log without a `log` directive. If you enable one, delete the query:

  ```caddyfile
  log {
  	format filter {
  		request>uri query {
  			delete *
  		}
  	}
  }
  ```

  Check your Caddyfile with `caddy adapt --config Caddyfile --pretty`: the `/api` route must come before the
  catch-all.

**Other proxies**: meet the rule list above. With nginx, set `proxy_set_header X-Forwarded-For $remote_addr;`, **not**
`$proxy_add_x_forwarded_for`, which keeps the client's own value and lets anyone choose their address.

### Checks

Run these **from another machine** after the proxy is up (`D=https://intheknow.example.nl`):

```bash
curl -sI http://intheknow.example.nl/ | head -1                       # 301 or 308 to https
curl -sI $D/ | grep -i '^strict-transport-security: max-age=31536000'
curl -s -D - $D/api/me                                                # 404, empty body, no content-security-policy
curl -s -D - $D/api                                                   # the same: answered by the proxy
curl -s $D/n/unsubscribe/x | grep -i intheknow                        # answered by the API's public page
curl -sI $D/ | grep -iE '^(x-frame-options|referrer-policy|x-content-type-options|content-security-policy):'  # all four
nmap -p 3000,5000,5432 <server address>                               # all closed or filtered
```

**Spoofed address** (from another machine): sign in with a wrong password through the web interface while sending
`X-Forwarded-For: 198.51.100.7` (for example with a browser extension, or by replaying the form post with
`curl -H`). Then, **on the server**:

```bash
docker compose exec db psql -U intheknow -c "SELECT host(source) FROM security_event ORDER BY id DESC LIMIT 3"
```

It must show your real address, not `198.51.100.7`.

**Two visitors**: from machine A, fail sign-in until it is throttled. From machine B, sign in normally: it must
work. (Set `InTheKnow__Accounts__SourceFailuresPerHour=20` in `.env` for a quicker test, and remove it after.)

### Trusted addresses

- The API believes `X-Forwarded-For` only from `STACK_SUBNET` and loopback: the web container, and your proxy's
  connection through the published port.
- To check which peer address your runtime shows, run this **on the server** (use the address of an existing
  account):

  ```bash
  curl -s -o /dev/null -H 'Content-Type: application/json' -d '{"email":"you@example.nl","password":"wrong-password"}' http://127.0.0.1:5000/api/auth/sign-in
  curl -s -o /dev/null -H 'Content-Type: application/json' -H 'X-Forwarded-For: 203.0.113.5' -d '{"email":"you@example.nl","password":"wrong-password"}' http://127.0.0.1:5000/api/auth/sign-in
  docker compose exec db psql -U intheknow -c "SELECT type, host(source) FROM security_event ORDER BY id DESC LIMIT 2"
  ```

  The older row is the runtime's peer, which must be in `STACK_SUBNET` or loopback. The newer one must be
  `203.0.113.5`.
- If your runtime shows a different peer, add it to `.env` as `InTheKnow__Accounts__TrustedProxies__4=<address>`.
  Index 3 is reserved for the stack.
- Docker's firewall rules for published ports bypass `ufw`. That is why the stack binds to `127.0.0.1` instead of
  relying on a firewall.

## First admin

Once all three services are `healthy`:

```bash
docker compose exec api dotnet InTheKnow.dll make-admin you@example.nl [--language en|nl]
```

Leave out `--language` for an English invite. It prints `invite queued`, and the email's link opens
"Accept your invite" at `PUBLIC_URL`. Accept it, set up two-step verification, and the admin pages unlock.

If no email arrives, look at `docker compose logs api`: failed deliveries are logged and retried (003 retry rules).

## Status and logs

```bash
docker compose ps                       # the STATUS column shows healthy
docker compose logs api                 # or db, web; add -f to follow
docker compose down                     # stop the stack and keep the data
```

`docker compose down -v` also deletes the database volume. Use it only to restore a backup.

Docker's default `json-file` log driver doesn't rotate. To rotate, set `"log-driver": "local"` in
`/etc/docker/daemon.json` and restart Docker. Disk space on the host is yours to watch.

## Upgrade and rollback

TODO (T018)

## Backup and restore

TODO (T027)

## Podman

TODO (T019)

## Building and publishing images

TODO (T023)

## Troubleshooting

TODO (T032)
