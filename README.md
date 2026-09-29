# InTheKnow

A self-hostable web app that monitors iCalendar feeds and notifies you when your schedule changes — new events, time/location changes, cancellations. Built for students who want to know when a class moves, gets canceled, or changes room.

## What is InTheKnow?
InTheKnow is a full-stack web application written in .NET and Svelte, designed to monitor iCalendar feeds and notify users about changes in their schedule. The .NET backend runs the checks and the HTTP API, and the SvelteKit frontend is the web interface people use.

## Why?
This app was mainly built for me (Wjouter) out of frustration, because my university constantly changes my schedule without letting anyone know (even the teachers).
This project is tailored for use with Semestry MyTimetables (the scheduling software my university uses). I cannot guarantee that it will work for other calendars / schedules, but effort is made to keep it useful for as many people as possible. Some features will be specifically made for Inholland.

## Features

| Feature | What it does | Spec |
|---|---|---|
| Change detection | Checks each calendar feed on a schedule and records every added, removed, moved or changed event. Several people with the same timetable share one check. | [001](specs/001-ical-change-detection/spec.md) |
| Change filters | Per calendar: rules that show only some changes, hide them, or mark them low priority. Sensible defaults hide noise such as changes to events that already ended. Rules can be saved as presets; admins make presets for everyone. | [002](specs/002-subscriber-change-filters/spec.md) |
| Notifications | One email digest per batch of changes, filtered as above, in English or Dutch and in your time zone. One-click unsubscribe. | [003](specs/003-notification-channels/spec.md) |
| Accounts | Sign-up with email confirmation, password reset, optional two-step verification with an authenticator app, and session management. Admins control who may sign up (open, allowed domains, invites) and manage accounts. Optional bot check (Cloudflare Turnstile or reCAPTCHA). | [004](specs/004-accounts-login/spec.md) |
| Web interface | Every feature above in the browser, in English and Dutch, on phones first: the changes feed as plain sentences, calendars, filters and presets, notifications, account and security settings, and the admin pages. The browser only talks to the frontend; the session token stays in an HttpOnly cookie. | [005](specs/005-sveltekit-frontend/spec.md) |

## Run it

New here? [QUICKSTART.md](QUICKSTART.md) walks through a local development setup step by step.

Needs the .NET 10 SDK and Docker (or Podman) with Compose.

```bash
docker compose up -d db
export ConnectionStrings__Default="Host=localhost;Username=intheknow;Password=intheknow;Database=intheknow"
```

Run the tests (they create and drop their own databases on the compose server):

```bash
export INTHEKNOW_TEST_DB="Host=localhost;Username=intheknow;Password=intheknow;Database=postgres"
dotnet test
```

Invites, sign-up confirmations, password resets and notifications are sent by email, so configure an SMTP server
before you start. For local development, start Mailpit and read the emails at http://localhost:8025:

```bash
docker compose up -d mailpit
export InTheKnow__Notifications__Email__Host=localhost
export InTheKnow__Notifications__Email__Port=1025
export InTheKnow__Notifications__Email__StartTls=false
export InTheKnow__Notifications__Email__FromAddress=intheknow@localhost
export InTheKnow__Notifications__PublicBaseUrl=http://localhost:5000
export InTheKnow__Notifications__FrontendBaseUrl=http://localhost:5173
```

Start the API, the background checker and the notifier. The database is migrated at startup:

```bash
dotnet run --project backend/InTheKnow
```

Make the first admin while the server runs. The command prints `invite queued`, and the server emails an invite;
the admin routes unlock once that account turns on two-step verification:

```bash
dotnet run --project backend/InTheKnow -- make-admin you@example.nl
```

Start the web interface in a second terminal. It needs Node 24 or later, and talks to the API at
`http://localhost:5000`:

```bash
cd frontend
npm ci
npm run dev
```

Open http://localhost:5173. The links in account emails (to `FrontendBaseUrl`) open its pages, so the admin invite
from `make-admin` ends on "Accept your invite".

For IDE runs, copy `backend/InTheKnow/Properties/launchSettings.example.json` to `launchSettings.json` (ignored by
git). It has the settings above. Its variables override the ones you exported.

### Behind a reverse proxy

List the proxy and the frontend's server in `InTheKnow__Accounts__TrustedProxies`. Otherwise every user shares one
address for sign-in throttling. `InTheKnow__ApiKey` from 001 is retired: it is ignored, with a warning.

In production, build the frontend with `npm run build` and run `node build` from `frontend/` with:
- `ORIGIN`: the public origin, the same value as `InTheKnow__Notifications__FrontendBaseUrl`;
- `API_URL`: the API's internal address (default `http://localhost:5000`);
- `ADDRESS_HEADER=X-Forwarded-For` and `XFF_DEPTH=1`, so it sees the browser's address;
- `HOST=127.0.0.1` (or the container network) and `PORT` (default 3000).

The proxy terminates TLS, sends HSTS, sends `/n/…` to the API, answers `404` for `/api/…` (the browser never
reaches the API), and sends everything else to the frontend. The whole list is in the
[005 configuration contract](specs/005-sveltekit-frontend/contracts/configuration.md).

## Documentation

Every feature has a spec, a quickstart with a manual walkthrough, and contracts for its API and configuration:

- [001 iCal change detection](specs/001-ical-change-detection/quickstart.md) — [API](specs/001-ical-change-detection/contracts/api.md), [configuration](specs/001-ical-change-detection/contracts/configuration.md)
- [002 Change filters](specs/002-subscriber-change-filters/quickstart.md) — [API](specs/002-subscriber-change-filters/contracts/api.md), [rules](specs/002-subscriber-change-filters/contracts/rules.md)
- [003 Notification channels](specs/003-notification-channels/quickstart.md) — [API](specs/003-notification-channels/contracts/api.md), [configuration](specs/003-notification-channels/contracts/configuration.md)
- [004 Accounts and login](specs/004-accounts-login/quickstart.md) — [API](specs/004-accounts-login/contracts/api.md), [configuration](specs/004-accounts-login/contracts/configuration.md)
- [005 Web interface](specs/005-sveltekit-frontend/quickstart.md) — [pages](specs/005-sveltekit-frontend/contracts/pages.md), [API additions](specs/005-sveltekit-frontend/contracts/api.md), [configuration](specs/005-sveltekit-frontend/contracts/configuration.md)

Since 004, the API routes of 001 to 003 live under `/api/me/…` (your own data) and `/api/admin/…` (admins), and
every request sends `Authorization: Bearer <session token>` from sign-in.
