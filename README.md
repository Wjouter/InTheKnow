# InTheKnow

A self-hostable web app that monitors iCalendar feeds and notifies you when your schedule changes — new events, time/location changes, cancellations. Built for students who want to know when a class moves, gets canceled, or changes room.

## What is InTheKnow?
InTheKnow is a full-stack web application written in .net and svelte designed to monitor iCalendar feeds and notify users about changes in their schedule.

## Why?
This app was mainly built for me (Wjouter) out of frustration, because with my university constantly changing my schedule without letting anyone know (even the teachers).
This is project is tailored for use with Semestry MyTimeTable (the sceduling software my university uses). I cannot guarantee that it will work for other calendars / schedules, but effort is made to keep it usefull for as many people as possible. Some features will be specifically made for Inholland.

## Run it

Needs the .NET 10 SDK and Docker (or Podman) with Compose.

```bash
docker compose up -d db
export ConnectionStrings__Default="Host=localhost;Username=intheknow;Password=intheknow;Database=intheknow"
export InTheKnow__ApiKey="$(openssl rand -base64 48)"
```

Run the tests (they create and drop their own databases on the compose server):

```bash
export INTHEKNOW_TEST_DB="Host=localhost;Username=intheknow;Password=intheknow;Database=postgres"
dotnet test
```

Start the API and the background checker:

```bash
dotnet run --project backend/InTheKnow
```

See [the quickstart](specs/001-ical-change-detection/quickstart.md) for a walkthrough and
[the API contract](specs/001-ical-change-detection/contracts/api.md) for the endpoints.

