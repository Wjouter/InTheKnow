# Contract: The settings file

This is the operator's `.env`, which sits next to `compose.yaml` and is copied from `deploy/env.example`.

- It is read by Compose for interpolation, and passed whole to the API service (`env_file`).
- It is never passed to the web or database services. They get only the values the stack picks for them
  ([stack.md](./stack.md)).
- Decisions are in [research R6](../research.md).

## Format

- One `NAME=value` per line. `#` starts a comment.
- Wrap a value in **single quotes** if it contains `$`, `#`, a space or a quote. Compose expands `$` in unquoted and
  double-quoted values.
- File mode `600`. It holds the database and SMTP passwords.

## Required (stack refuses to start without them)

- `up` fails before any container starts, with `<NAME> is required, see DEPLOY.md`.
- In `env.example` these lines are **empty** (`PUBLIC_URL=`), with an example in the comment above each.
  - `${X:?}` fires only when a value is unset or empty.
  - A placeholder such as `change-me` would pass the check, and would become a working default password (US2 AS5,
    FR-012).

| Name | Example | Feeds |
|---|---|---|
| `PUBLIC_URL` | `https://intheknow.example.nl` | web `ORIGIN`; API `InTheKnow__Notifications__PublicBaseUrl` and `…__FrontendBaseUrl` |
| `POSTGRES_PASSWORD` | output of `openssl rand -hex 32` | db `POSTGRES_PASSWORD`; API `PGPASSWORD` |
| `SMTP_HOST` | `smtp.example.nl` | API `InTheKnow__Notifications__Email__Host` |
| `SMTP_FROM` | `intheknow@example.nl` | API `InTheKnow__Notifications__Email__FromAddress` |

**`PUBLIC_URL` rule**:
- It must be an origin only: `https://`, a host and an optional port.
- It must have no path, no trailing `/`, no query and no fragment.
- A malformed value stops the API with `InTheKnow:Notifications:PublicBaseUrl must be …` (research R5).
  `DEPLOY.md` maps that message to `PUBLIC_URL`.

**`POSTGRES_PASSWORD`** is read by PostgreSQL only when the volume is first created. Changing it later needs
`ALTER USER` inside the database as well; `DEPLOY.md` gives the command.

## Optional, named

| Name | Default | Feeds |
|---|---|---|
| `SMTP_PORT` | `587` | API `…__Email__Port` |
| `SMTP_USERNAME` | empty | API `…__Email__Username` (set together with the password) |
| `SMTP_PASSWORD` | empty | API `…__Email__Password` |
| `WEB_PORT` | `3000` | host port of the web interface, on `127.0.0.1` |
| `API_PORT` | `5000` | host port of the API, on `127.0.0.1` (the proxy sends `/n/…` here) |
| `STACK_SUBNET` | `172.30.250.0/24` | the stack network, and the API's trusted proxies. Change it only if `up` reports that the pool overlaps. |

## Optional, any other API setting (FR-013)

Every key of the 001 to 005 configuration contracts can be set under its environment name, with its existing default:

```dotenv
InTheKnow__CheckInterval=00:10:00
InTheKnow__Notifications__DefaultTimeZone=Europe/Amsterdam
InTheKnow__Accounts__BotCheck__Provider=turnstile
InTheKnow__Accounts__BotCheck__SiteKey=…
InTheKnow__Accounts__BotCheck__SecretKey=…
InTheKnow__MyTimetables__Domains__0__Host=mytimetables.inholland.nl
```

`env.example` lists the common ones commented out, and links each contract for the rest.

## Owned by the stack (setting them in `.env` has no effect)

The stack sets these in the API's `environment:`, which overrides `env_file`:

- `ConnectionStrings__Default`
- `PGPASSWORD`
- `ASPNETCORE_HTTP_PORTS`
- `InTheKnow__Accounts__TrustedProxies__0..2`
- `InTheKnow__Notifications__PublicBaseUrl`, `…__FrontendBaseUrl`
- `InTheKnow__Notifications__Email__Host`, `…__Port`, `…__Username`, `…__Password`, `…__FromAddress`

Set these through the named settings above.

## Upgrades

- An upgrade never rewrites `.env`.
- A release that adds a setting lists it in its release notes.
  - If the setting has a default, the unchanged file keeps working.
  - If it is required, the stack definition marks it with `:?`, or the API names it at startup (FR-014).
