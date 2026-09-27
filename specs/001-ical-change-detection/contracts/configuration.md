# Contract: Operator configuration

The standard .NET configuration sources are used, in this order (later sources win):
1. `appsettings.json`, which is shipped with the defaults;
2. `appsettings.{Environment}.json`;
3. environment variables, where `__` separates sections.

Configuration is read at startup (research R13).

## Shape (shipped `appsettings.json`)

```json
{
  "ConnectionStrings": {
    "Default": ""
  },
  "InTheKnow": {
    "CheckInterval": "00:10:00",
    "MaxRequestsPerHostPerMinute": 120,
    "MaxConcurrentChecks": 16,
    "FailingCheckInterval": "1.00:00:00",
    "ExpansionHorizonMonths": 12,
    "ChangeRetention": "365.00:00:00",
    "MyTimetables": {
      "Domains": [
        { "Host": "mytimetables.inholland.nl" }
      ]
    }
  }
}
```

## Settings

| Key | Type | Default | Rule | FR |
|---|---|---|---|---|
| `ConnectionStrings:Default` | Npgsql connection string | none | **Required, secret.** Set it through the environment (`ConnectionStrings__Default`) and never commit it. Startup fails if it is empty. | — |
| `InTheKnow:ApiKey` | string | — | **Retired by 004** (FR-033): no longer required, and ignored; startup logs a warning if it is still set. Sessions replace it ([004 configuration.md](../../004-accounts-login/contracts/configuration.md)). | FR-031 |
| `InTheKnow:MaxRequestsPerHostPerMinute` | int | `120` | Must be ≥ 1, otherwise startup fails. This is the request budget per source host and covers both scheduled checks and link validation. Requests are spaced evenly, one every `1 min / budget` (0.5 s at the default). A check that doesn't fit waits for the next free slot and is never skipped. With the defaults, one host supports about 1,200 calendars per 10-minute interval before checks start to fall behind. **At scale this, not `CheckInterval`, sets the pace**: one check cycle is `max(CheckInterval, calendars on the host ÷ budget)` minutes, for example about 42 min for 5,000 calendars at 120/min (SC-005, research R19). Watch `GET /api/admin/calendars?overdue=true` and raise the domain's budget when it fills up. This is the **default for every host**; a domain entry can override it (below). | FR-012a, FR-012b |
| `InTheKnow:MaxConcurrentChecks` | int | `16` | Must be between 1 and 64, otherwise startup fails. The upper bound leaves room in Npgsql's default pool of 100 connections for the API. Each running check holds at most one connection, and only while it reads or writes. This caps throughput at about `MaxConcurrentChecks ÷ check duration` checks per second, so raise it together with a large per-domain budget. | FR-014a |
| `InTheKnow:CheckInterval` | TimeSpan | `00:10:00` | Values below `00:05:00` are clamped to 5 minutes, with a warning logged | FR-010, FR-011 |
| `InTheKnow:FailingCheckInterval` | TimeSpan | `1.00:00:00` | Values below `CheckInterval` are clamped to it, with a warning logged | FR-011a |
| `InTheKnow:ExpansionHorizonMonths` | int | `12` | Must be ≥ 1, otherwise startup fails | FR-015a |
| `InTheKnow:ChangeRetention` | TimeSpan | `365.00:00:00` | Must be > 0, otherwise startup fails | FR-025a |
| `InTheKnow:MyTimetables:Domains` | array of domain objects | Inholland, all features on | See below | FR-002, FR-002a |

### Domain object

| Key | Type | Default | Feature |
|---|---|---|---|
| `Host` | string | required | The exact host, case-insensitive. Subdomains are **not** included. Duplicate hosts cause startup to fail. |
| `DuplicateLinkMerging` | bool | `true` | FR-002(a), FR-005 |
| `ClassGroupSharing` | bool | `true` | FR-002(b), FR-006 |
| `EmptyFeedProtection` | bool | `true` | FR-002(c), FR-024 |
| `TeacherDetection` | bool | `true` | FR-002(d), FR-016, FR-017. Only has an effect when `EventDetails` is on. |
| `EventDetails` | bool | `true` | FR-002(e), FR-016, FR-016a |
| `DraftMarkerDetection` | bool | `true` | FR-002(f), FR-015 |
| `MaxRequestsPerMinute` | int | `MaxRequestsPerHostPerMinute` | FR-012a. Overrides the default request budget for this host, for example after the institution grants a bigger allowance. Must be ≥ 1. This is not a feature, so changing it never re-baselines calendars (FR-026a). |

A feature that is not specified defaults to **on**. A budget that is not specified uses the global default. Removal of volatile notes (FR-018) always applies to a listed
domain.

### Examples

Adding a second institution with teacher detection off (US4-2), in `appsettings.Production.json`:

```json
{ "InTheKnow": { "MyTimetables": { "Domains": [
  { "Host": "mytimetables.inholland.nl" },
  { "Host": "rooster.example.nl", "TeacherDetection": false }
] } } }
```

Adding the second institution with environment variables instead:

```bash
InTheKnow__MyTimetables__Domains__1__Host=rooster.example.nl
InTheKnow__MyTimetables__Domains__1__TeacherDetection=false
```

Giving Inholland a bigger allowance, with more concurrent checks to use it:

```json
{ "InTheKnow": {
  "MaxConcurrentChecks": 32,
  "MyTimetables": { "Domains": [ { "Host": "mytimetables.inholland.nl", "MaxRequestsPerMinute": 600 } ] }
} }
```

The same with environment variables (index 0 is the shipped Inholland entry):

```bash
InTheKnow__MaxConcurrentChecks=32
InTheKnow__MyTimetables__Domains__0__MaxRequestsPerMinute=600
```


A budget override is available only for listed domains. Any other host uses the global default. To give a
non-MyTimetables host its own budget, list it with every feature set to `false`; the volatile-note removal that
then applies only strips MyTimetables' own footer lines.

Arrays are merged by index across sources, so overriding index 0 replaces the Inholland entry. To stop treating
Inholland as MyTimetables, replace the whole array in an appsettings file.

## Notifications (feature 003)

The `InTheKnow:Notifications` section (collection and retry windows, default time zone, public and frontend base
URLs, and the SMTP settings) is described in
[003 contracts/configuration.md](../../003-notification-channels/contracts/configuration.md).
`Logging:LogLevel:Microsoft.AspNetCore` MUST stay at `Warning` or higher: at `Information`, ASP.NET Core logs request
paths, and the notification links under `/n/` carry tokens.

## Constants (not configurable, research R4, R5 and R10)

| Constant | Value |
|---|---|
| Overall time limit per check | 2 min |
| Claim lease (a crashed check is retried after this) | 5 min |
| Longest dispatcher sleep when nothing is claimable | 1 s |
| Longest wait of a validation for a host slot | 5 s |
| Retention delete | every 10 min |
| Fetch timeout | 30 s |
| Maximum feed size | 10 MB |
| Maximum redirects | 5 |
| Maximum occurrences per calendar | 50,000 |
