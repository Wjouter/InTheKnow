# Quickstart: local development

Get InTheKnow running on your machine: PostgreSQL and Mailpit in containers, the .NET API on port 5000 and the
SvelteKit frontend on port 5173. Deploying behind a reverse proxy is covered in the
[README](README.md#behind-a-reverse-proxy).

## You need

- The [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node](https://nodejs.org) 24 or later, with npm
- Docker (or Podman) with Compose

## 1. Start PostgreSQL and Mailpit

```bash
docker compose up -d db mailpit
```

- PostgreSQL listens on `localhost:5432` (user, password and database are all `intheknow`).
- Mailpit catches every email the app sends. Read them at http://localhost:8025.

## 2. Configure the API

Set these in the terminal you start the API from. They point the API at the database and Mailpit, and tell it where
the frontend lives, so the links in emails open your dev frontend. Quote the connection string: it contains `;`.

```bash
export ConnectionStrings__Default="Host=localhost;Username=intheknow;Password=intheknow;Database=intheknow"
export InTheKnow__Notifications__Email__Host=localhost
export InTheKnow__Notifications__Email__Port=1025
export InTheKnow__Notifications__Email__StartTls=false
export InTheKnow__Notifications__Email__FromAddress=intheknow@localhost
export InTheKnow__Notifications__PublicBaseUrl=http://localhost:5000
export InTheKnow__Notifications__FrontendBaseUrl=http://localhost:5173
```

In an IDE, copy `backend/InTheKnow/Properties/launchSettings.example.json` to `launchSettings.json` instead (it is
ignored by git). It holds the same settings.

Optional: notification digests wait 5 minutes to collect changes. To see them sooner, set
`InTheKnow__Notifications__CollectionWindow=00:01:00` (1 minute is the minimum).

## 3. Start the API

```bash
dotnet run --project backend/InTheKnow
```

This runs the HTTP API, the background calendar checker and the notifier. It migrates the database at startup, so
there is nothing to set up by hand.

## 4. Start the frontend

In a second terminal:

```bash
cd frontend
npm ci
npm run dev
```

It talks to the API at `http://localhost:5000` (override with `API_URL` if yours runs elsewhere). Open
http://localhost:5173.

## 5. Make yourself an admin

With the API running:

```bash
dotnet run --project backend/InTheKnow -- make-admin you@example.nl
```

It prints `invite queued`. Open the invite email in Mailpit and follow the link to "Accept your invite". The admin
pages stay locked until that account turns on two-step verification (Settings → Sign-in & security).

Other people can sign up only if an admin allows it (open sign-up, allowed domains or invites), so this is the way in
on a fresh database.

## Run the checks

The backend tests create and drop their own databases on the compose PostgreSQL:

```bash
export INTHEKNOW_TEST_DB="Host=localhost;Username=intheknow;Password=intheknow;Database=postgres"
dotnet test
```

The frontend:

```bash
cd frontend
npm run check
npm test
npm run build
```

## Reset

Stop the containers and delete the database volume to start over:

```bash
docker compose down -v
```

## Where next

- The [README](README.md) lists the features and the spec of each.
- Every feature has its own quickstart with a manual walkthrough, for example the
  [web interface](specs/005-sveltekit-frontend/quickstart.md).
- All configuration keys are in the `contracts/configuration.md` of each spec.
