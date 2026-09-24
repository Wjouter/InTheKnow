# Feature Specification: Timetable Change Detection

**Feature Branch**: `001-ical-change-detection`

**Created**: 2026-09-24

**Status**: Draft

**Input**: User description: "Backend system that checks iCalendar feeds for changes. Extract as much information as possible (event name, time, location, teacher); a change to any of these is a trigger for a notification (sending notifications is out of scope — only change detection). Must work 100% with Inholland MyTimetables (Semestry). Detect when multiple links point to the same calendar (same user or classmates) and check each calendar only once to avoid overloading the server. Refresh rate must be configurable."

**Clarifications (2026-09-24)**: any iCalendar link must work (personal/work calendars too), MyTimetables support must not be tied to the Inholland domain, default interval is 10 minutes, the system records every field change (deciding which changes matter is left to future user filters), Dutch and English MyTimetables feeds must both work (verified with an English sample, see investigation.md §6), and the state that is compared against lives in the database. Cross-user link sameness stays unverified for now. MyTimetables features are active only on operator-configured domains, each feature can be switched on/off per domain, and all other domains get default iCalendar change detection.

**Research**: [investigation.md](./investigation.md) — analysis of the example MyTimetables links and feeds that this spec relies on.

## Clarifications

### Session 2026-09-24

- Q: When event details are enabled, do the parsed parts replace the raw description and location when comparing? → A: Yes. With event details on, only the parsed parts are compared (raw description not compared; location compared as the rooms set). With event details off, the raw description (minus volatile notes) and location are compared as-is, and a language switch may record a description change (accepted).
- Q: Are flagged links retried, and what happens when every link of a calendar is flagged? → A: A flagged link is un-flagged as soon as it returns events again. A calendar whose links are all flagged is marked failing and retried at a separate, operator-configurable interval (default once a day) until a link recovers.
- Q: How far ahead are recurring events expanded into occurrences? → A: From the start of the series up to an operator-configurable horizon (default 12 months ahead of now). Occurrences that enter or leave the horizon only because time moved on are not recorded as added or removed.
- Q: How long are check results and recorded changes kept, and what happens when a calendar's last subscriber leaves? → A: Only the latest check result per calendar is kept (each check replaces the previous one); recorded changes are kept for an operator-configurable period, default 1 year. When the last subscriber leaves, all of that calendar's stored data is deleted.

### Session 2026-09-24 (planning)

- Q: How is the system operated before user login exists? → A: Through an HTTP API that the later SvelteKit frontend will also use. Until the separate login feature ships, every API call requires an operator key; subscribers are identified by an id in the request path, which login will later replace with the signed-in user.
- Q: What scale must the system handle? → A: A whole school (tens of thousands of students, thousands of calendars), not a single cohort. PostgreSQL remains the database.
- Q: Can one stuck check hold up the others, and what happens to a check that would exceed the per-host request budget? → A: Calendars are checked concurrently, and each check has an overall time limit, so a stuck check is recorded as failed and never delays other calendars. A check that would exceed the budget is never skipped: it stays due, keeps its place in line (longest-waiting first), and is sent at the host's next free slot. Requests to a host are spaced evenly, so many checks that fall due at the same moment (e.g. after a restart) are spread out automatically.
- Q: Can one domain get a bigger request allowance than others, and is the number of concurrent checks adjustable? → A: Yes. The operator sets a default request budget for every host and can override it per configured domain (e.g. after the institution grants a bigger allowance); the number of concurrent checks is also operator-configurable. Changes take effect after a restart, and the even spacing follows the new values.

### Session 2026-09-24 (analysis)

- Q: Are plain `http` feed links accepted? → A: No. The constitution requires HTTPS outside local development, so only `https` links are accepted (FR-001); "any iCalendar link" above means any `https` link.
- Q: When is a new calendar's baseline taken? → A: From the validation fetch of the link that creates it, so a new calendar is watched from the moment it is added, even when checks are running behind (FR-023).
- Q: What does "within one check interval" mean when the host's budget cannot keep up? → A: Within one check cycle: the interval, or calendars on the host ÷ budget when that is longer (SC-005).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Detect changes in my timetable (Priority: P1)

A student has added their calendar link. The system periodically checks the calendar, compares it with the state stored in the database, and records every change to an event's fields (old value → new value), as well as events that were added or removed.

**Why this priority**: This is the core value of the app; without it nothing else matters.

**Independent Test**: Feed the system two versions of a real MyTimetables calendar that differ in known ways and verify exactly those differences are recorded, and nothing else.

**Acceptance Scenarios**:

1. **Given** a calendar's state is stored, **When** a lesson moves from room AL00.A1.38 to AL00.A1.20, **Then** one location change is recorded for that lesson with old and new room.
2. **Given** a calendar's state is stored, **When** a lesson's start or end time changes, **Then** a time change is recorded with old and new times.
3. **Given** a calendar's state is stored, **When** a lesson's teacher is replaced, added or removed, **Then** one teacher change is recorded with old and new teacher list.
4. **Given** a calendar's state is stored, **When** an event's name changes, **Then** a name change is recorded; **When** an event gains or loses its draft marker (`[CONCEPT]` / `[DRAFT]`), **Then** a status change (tentative ↔ confirmed) is recorded instead.
5. **Given** a calendar's state is stored, **When** a new event appears, **Then** an "added" change is recorded; **When** an event disappears, **Then** a "removed" change is recorded.
6. **Given** a calendar's state is stored, **When** it is checked again and only volatile bookkeeping data changed (sync timestamps, generation times), **Then** no change is recorded.
7. **Given** a calendar is fetched for the very first time (the validation fetch of the link that creates it), **When** that fetch succeeds, **Then** its events are stored as the baseline and no changes are recorded.
8. **Given** the system is restarted, **When** the next check runs, **Then** it compares against the state stored before the restart.

---

### User Story 2 - Check each real calendar only once (Priority: P2)

A student adds several links that MyTimetables generated for the same timetable, and classmates add links for the same class group. The system recognises these as one calendar and fetches it once per check, while every subscriber still receives the changes.

**Why this priority**: Protects the source server (the app would otherwise multiply load by the number of links) and keeps the app from being blocked.

**Independent Test**: Register the example duplicate links (and a synthetic classmate link) and verify only one calendar exists and one scheduled fetch occurs per check cycle.

**Acceptance Scenarios**:

1. **Given** a MyTimetables link is registered, **When** the same user adds another link to the same timetable that differs only in its generation marker, **Then** it is attached to the existing calendar and no additional calendar is checked.
2. **Given** a class-group calendar is registered by one student, **When** a classmate adds their own link for the same class group on the same MyTimetables server, **Then** it is attached to the existing calendar after that link is validated.
3. **Given** two links for different class groups, **When** both are added, **Then** two separate calendars are checked.
4. **Given** a link for a personal (non-class-group) MyTimetables timetable, **When** another user adds a link with the same timetable id, **Then** it is not merged with the first user's calendar.
5. **Given** a newly added MyTimetables link that matches an existing calendar but returns no events (invalid or forged), **When** it is validated, **Then** it is rejected and not attached.
6. **Given** a generic (non-MyTimetables) calendar link is registered, **When** the same link is added again (differing only in letter case of the host or order of query parameters), **Then** it is attached to the existing calendar.
7. **Given** a MyTimetables link on a domain that is not configured as a MyTimetables domain, **When** it is added, **Then** it is handled as a generic calendar link.

---

### User Story 3 - Configure how often calendars are checked (Priority: P3)

The operator of the app sets the time between checks.

**Why this priority**: Needed to balance freshness against server load; a sensible default makes it optional for a first version.

**Independent Test**: Set the interval to a value, observe checks occur at that interval; set it below the minimum and observe it is clamped.

**Acceptance Scenarios**:

1. **Given** the operator sets the interval to 30 minutes, **When** the system runs, **Then** each calendar is checked roughly every 30 minutes.
2. **Given** the operator sets an interval below the allowed minimum, **When** the system starts, **Then** the minimum is used and a warning is logged.
3. **Given** no interval is configured, **When** the system runs, **Then** each calendar is checked every 10 minutes.
4. **Given** every link of a calendar is flagged invalid and the failing-calendar interval is set to 6 hours, **When** the system runs, **Then** that calendar is checked roughly every 6 hours until a link returns events again, after which it returns to the normal interval.
5. **Given** the default request budget is 120 per minute and the operator gives `mytimetables.inholland.nl` a budget of 600 per minute, **When** the system runs, **Then** requests to that domain are spaced for 600 per minute and requests to every other host for 120 per minute.
6. **Given** the operator sets the concurrency limit to 32, **When** many calendars are due, **Then** at most 32 checks run at the same time.

---

### User Story 4 - Configure where MyTimetables features apply (Priority: P3)

The operator lists the domains that run MyTimetables (e.g. Inholland, another institution's instance, or Inholland's new domain after a move) and, per domain, switches the individual MyTimetables features on or off. Links on any other domain get default iCalendar change detection.

**Why this priority**: Keeps the MyTimetables logic reusable for other institutions and survives a domain change without a new release; the shipped default (Inholland, all features on) covers the main use case.

**Independent Test**: Configure a second domain with only some features on, register links on both domains and on an unlisted domain, and verify each link gets exactly the features configured for its domain.

**Acceptance Scenarios**:

1. **Given** no domain configuration is provided, **When** the system runs, **Then** `mytimetables.inholland.nl` is a MyTimetables domain with all features enabled.
2. **Given** the operator adds `rooster.example.nl` as a MyTimetables domain with teacher detection disabled, **When** a calendar on that domain is checked, **Then** all other MyTimetables features apply but no teacher list is extracted.
3. **Given** a link on a domain that is not listed, **When** it is added and checked, **Then** only default iCalendar change detection and generic duplicate detection apply.
4. **Given** the operator disables class-group sharing for a domain, **When** two users add links for the same class group on that domain, **Then** two separate calendars are checked.
5. **Given** the features of a domain change, **When** its calendars are next checked, **Then** their stored state is re-established as a new baseline and no changes are recorded for the configuration switch alone.

---

### User Story 5 - Manage subscriptions through an API (Priority: P2)

A client (the operator now, the SvelteKit frontend later) adds and removes calendar links for a subscriber, lists that subscriber's calendars with their check status, reads the changes recorded for them, and the operator lists calendars that are failing.

**Why this priority**: The frontend will be built on this API; without it, subscriptions can only be managed by editing the database.

**Independent Test**: Using the operator key, add a link for a subscriber, list the subscriber's calendars, read their recorded changes after a known edit, remove the subscription, and verify that requests without the key are rejected.

**Acceptance Scenarios**:

1. **Given** a valid operator key, **When** a client adds a valid link for subscriber S, **Then** the link is validated, attached to the right calendar, and the response returns the calendar and link ids but not the link.
2. **Given** a request without a valid operator key, **When** it calls any API endpoint, **Then** it is rejected and nothing is read or changed.
3. **Given** subscriber S has calendars with recorded changes, **When** the client lists S's changes, **Then** only changes of calendars S subscribes to are returned, oldest first, in pages.
4. **Given** subscriber S removes their subscription to a calendar, **When** S was its last subscriber, **Then** the calendar and all its data are deleted.
5. **Given** calendars are failing, **When** the operator lists calendars, **Then** each calendar's status, latest check outcome and failure count are shown, and no link is shown.

---

### Edge Cases

- **Invalid/revoked MyTimetables link**: MyTimetables returns a successful, well-formed but empty calendar. With empty-feed protection enabled, a calendar that previously had events and now has none is treated as a failed check: no removals are recorded, the link is flagged, and another validated link for the same calendar is used if available. If every link is flagged, the calendar is marked failing and retried at the failing-calendar interval; as soon as a link returns events again it is un-flagged and normal checking resumes (this also covers a legitimately empty timetable, e.g. between academic years).
- **Server unreachable, error response, oversized or unparseable feed**: the check is marked failed, no changes are recorded, the stored state is kept, and the next check proceeds normally; repeated failures are visible to the operator.
- **Links pointing at internal/private network addresses, or using any scheme other than `https`** (including plain `http`): rejected when added and never fetched.
- **English vs Dutch MyTimetables feeds**: the feed language is an account-wide setting of the link owner and can change between checks. A switch changes the description labels, the activity-type values, the draft marker (`[CONCEPT]` ↔ `[DRAFT]`) and the calendar name, but no event data. With event details and draft-marker detection enabled, all of these are recognised in both languages and normalised, so a language switch alone records no changes; with either disabled, the change to the affected raw field (description or name) is recorded.
- **Unknown labelled parts** in a MyTimetables description: kept as additional event details; changes to them are recorded like any other field.
- **Recurring events** (common in personal/work calendars): expanded into individual occurrences from the start of the series up to the expansion horizon (FR-015a); occurrences entering or leaving the horizon only because time moved on (or the horizon was reconfigured) are not recorded as added or removed; a change to one occurrence is recorded for that occurrence only; a change to the whole series is recorded for each affected occurrence.
- **Events without a stable identity** in a generic feed: identified by their content; any change then shows up as a removal plus an addition.
- **Multiple rooms / multiple teachers**: compared as unordered sets, so a reordering alone is not a change.
- **Multiple changes to one event in one check** (e.g. time and room): all are recorded, grouped per event.
- **Shared events** (same event in several calendars, e.g. holidays or combined classes): each calendar records it independently for its own subscribers.
- **Daylight-saving transitions and floating (time-zone-less) times**: times are resolved to absolute instants (floating times in the calendar's declared time zone); a pure representation difference is not a change.
- **Last subscriber removes their link**: the calendar is no longer checked, and its links, event snapshots, recorded changes and check result are deleted.
- **Domain configuration changes** (domain added/removed, feature toggled): configuration takes effect at the next start: links are re-grouped under the new identity rules at startup, and affected calendars are re-baselined without recording changes.
- **Many checks for one server due at the same moment** (e.g. after a restart or downtime): they are sent one after another, evenly spaced within the server's request budget, longest-waiting first; none is skipped, and because each calendar's next check is scheduled from when it actually ran, they stay spread out afterwards.
- **More calendars on one server than its request budget allows per interval**: checks for that server are delayed rather than sent faster or skipped; the operator can see overdue calendars and raise the interval or the budget.
- **A check that hangs or runs very long** (slow server, stalled connection, pathological feed): it is stopped at the overall check time limit and recorded as a failed check (timeout); other calendars keep being checked on schedule meanwhile.
- **A link on a MyTimetables domain that does not have the MyTimetables link structure**: the identity features (duplicate link merging, class-group sharing) do not apply to it; the content features still do.

## Requirements *(mandatory)*

### Functional Requirements

**Calendar registration & identity**

- **FR-001**: System MUST accept any `https` iCalendar feed link and MUST reject links with any other scheme (including plain `http`, per the constitution's HTTPS rule) or that resolve to loopback, private or link-local network addresses.
- **FR-002**: The operator MUST be able to configure a list of MyTimetables domains and, per domain, enable or disable each MyTimetables feature: (a) duplicate link merging, (b) class-group sharing, (c) empty-feed protection, (d) teacher detection, (e) event details, (f) draft-marker detection. Default: `mytimetables.inholland.nl` with all features enabled.
- **FR-002a**: MyTimetables features MUST apply only to links whose host exactly matches a configured MyTimetables domain, and only the features enabled for that domain; links on all other domains MUST get default iCalendar change detection. Removal of volatile MyTimetables notes (FR-018) always applies on configured domains.
- **FR-003**: System MUST validate every newly added link with a single fetch before accepting it: the response must be a parseable iCalendar feed; when empty-feed protection is enabled for the link's domain it must also contain at least one event.
- **FR-004**: System MUST treat generic links that are identical after normalisation (host letter case, default port, query-parameter order) as the same calendar.
- **FR-005**: With duplicate link merging enabled, System MUST treat MyTimetables links that differ only in the link-generation marker as the same calendar.
- **FR-006**: With class-group sharing enabled, System MUST treat MyTimetables links from different users that reference the same class-group timetable on the same host as the same calendar.
- **FR-007**: System MUST NOT merge MyTimetables links for timetable kinds other than class groups across different users.
- **FR-008**: System MUST keep all validated links attached to a calendar and MUST fetch each calendar through exactly one of them per check, switching to another validated link when the current one is flagged invalid. When a subscriber removes their subscription, their links MUST be deleted and never used again. When a calendar's last subscriber is removed, the calendar and all its stored data (links, event snapshots, recorded changes, check result) MUST be deleted.
- **FR-008a**: A flagged link MUST be un-flagged when a fetch through it returns events again. When every link of a calendar is flagged, the calendar MUST be marked failing and checked through one of its flagged links at the failing-calendar interval (FR-011a) instead of the normal interval, until a link recovers.
- **FR-009**: System MUST record which subscribers belong to which calendar so that each subscriber's changes can be delivered later.

**Checking**

- **FR-010**: System MUST check every calendar that has at least one subscriber periodically, at a configurable interval.
- **FR-011**: The check interval MUST default to 10 minutes and MUST NOT be allowed below 5 minutes.
- **FR-011a**: The failing-calendar interval MUST be configurable by the operator, MUST default to 24 hours and MUST NOT be allowed below the check interval.
- **FR-012**: System MUST spread checks of different calendars over the interval rather than fetching all calendars at the same moment.
- **FR-012a**: System MUST limit the requests sent to any single host to a budget per minute, spacing them evenly rather than sending them in bursts. The operator MUST be able to set a default budget for all hosts and override it per configured domain.
- **FR-012b**: A scheduled check that cannot be sent because of the budget MUST NOT be skipped: it stays due, keeps its place in line (longest-waiting first), and is sent at the host's next free slot. A calendar's next check MUST be scheduled one interval after the check actually ran.
- **FR-013**: System MUST fetch each calendar at most once per interval, regardless of how many links or subscribers it has.
- **FR-014**: System MUST bound each fetch in time and size, so that a slow or huge feed cannot stall other checks.
- **FR-014a**: System MUST check multiple calendars concurrently, up to an operator-configurable limit (default 16), and MUST bound each whole check (fetch, processing and storage) by an overall time limit. A check that exceeds it MUST be recorded as failed (timeout), MUST NOT record changes, and MUST NOT delay the checks of other calendars.

**Extraction**

- **FR-015**: For every event in any feed the system MUST extract: stable event identity (occurrence-level for recurring events), name, start time, end time, location, description, and status (e.g. confirmed, tentative, cancelled). With draft-marker detection enabled, a draft marker (`[CONCEPT]` or `[DRAFT]`) in the name sets the status to tentative and is not part of the name.
- **FR-015a**: Recurring events MUST be expanded from the start of the series up to an operator-configurable horizon, default 12 months ahead of the check time. An occurrence starting after the previous check's horizon MUST NOT be recorded as added, and one starting after the current check's horizon MUST NOT be recorded as removed.
- **FR-016**: With event details enabled, the system MUST additionally extract from the description: activity type (e.g. lesson, test, holiday, appointment), rooms (list) and attending groups (list); with teacher detection enabled, teachers (list of full names). Dutch and English labels MUST both be recognised. Activity types MUST be stored as language-independent values (Les/Lesson, Toets/Test, Vakantie/Holiday, Afspraak/Appointment); unknown types and any other labelled parts MUST be kept as-is as additional details. Teacher detection applies only when event details is enabled; with teacher detection disabled, the teacher line is kept as an additional detail.
- **FR-016a**: With event details enabled, the parsed parts MUST replace the raw description and location in comparisons: the raw description is not compared and location is compared as the rooms set, so each real change is recorded once. With event details disabled, the raw description (minus volatile notes, FR-018) and location MUST be compared as-is.
- **FR-017**: With teacher detection enabled, System MUST extract teachers correctly when an event has several teachers written as "Last, First" pairs in one list.
- **FR-018**: System MUST ignore volatile data that changes on every fetch or with the feed language (fetch/sync timestamps, "last synchronised" and "managed by MyTimetables" notes, generation times, revision counters, calendar name) when comparing.

**Change detection**

- **FR-019**: System MUST persist each calendar's latest successfully fetched event state in the database and MUST compare every new fetch against that persisted state.
- **FR-019a**: A check whose result is identical to the stored state MUST NOT rewrite the stored event state, and a check with changes MUST write only the affected events, so that database writes grow with the number of changes rather than the number of events.
- **FR-020**: System MUST match events between the stored state and a new fetch by their stable event identity.
- **FR-021**: System MUST record a change for every extracted field whose value differs, and an "added" or "removed" change for events that appear or disappear. It MUST NOT filter changes by event type, date (past or future) or field; selecting which changes matter is left to later user filters.
- **FR-022**: Each recorded change MUST include the calendar, the event identity, the field (or added/removed), the old and new values, the event's name and time for context, and when it was detected.
- **FR-023**: The first successful fetch of a calendar MUST establish the baseline without recording changes. For a calendar created by adding a link, that is the link's validation fetch (FR-003).
- **FR-024**: A check that fails (network/server error, unparseable content, or, with empty-feed protection enabled, a calendar returning zero events where it had events before) MUST NOT record any changes and MUST NOT replace the stored state.
- **FR-025**: Recorded changes MUST be stored in the database so a later notification feature can pick them up, and each change MUST be recorded once (not again on subsequent checks).
- **FR-025a**: Recorded changes MUST be deleted once older than an operator-configurable retention period, default 1 year.
- **FR-026**: List fields (rooms, teachers, groups) MUST be compared as sets; reordering alone is not a change.
- **FR-026a**: When the domain configuration changes the features that apply to a calendar, its next successful check MUST re-establish the baseline without recording changes.

**Operations & privacy**

- **FR-027**: System MUST keep the outcome of the latest check per calendar (success/failure, reason), replacing the previous one, together with the number of consecutive failed checks and since when, so the operator can see failing calendars and links. Older check results MUST NOT be kept.
- **FR-028**: Calendar links MUST be treated as secrets: never logged in full, never shown to other users.
- **FR-029**: System MUST store only the event data needed for change detection and notification; stored teacher names are justified as a notification trigger.

**API**

- **FR-030**: System MUST expose an HTTP API to: add a link for a subscriber (with the validation and identity rules above), list a subscriber's calendars with their latest check outcome, remove a subscriber's subscription to a calendar, list the changes recorded for a subscriber's calendars (oldest first, paged), and list all calendars with their status for the operator.
- **FR-031**: Until user login exists, every API endpoint MUST require the operator key and MUST reject requests without it. The key is operator configuration and treated as a secret.
- **FR-032**: The API MUST NOT return or log calendar links; links are sent to it only in request bodies.

### Key Entities

- **Calendar**: one real calendar at the source (identified per the identity rules above). Holds last check time and status (ok / failing). Checked once per interval.
- **Calendar Link**: a URL a user submitted, attached to exactly one Calendar. Has validation status (valid / flagged invalid). Treated as a secret.
- **MyTimetables Domain**: operator configuration: a host name, the set of enabled MyTimetables features, and optionally its own request budget.
- **Subscription**: the relation between a user and a Calendar, via one or more Calendar Links.
- **Event Snapshot**: the persisted latest state of one event (occurrence) in a calendar: identity, name, start, end and status, plus either location and description (event details disabled) or type, rooms, teachers (when teacher detection is enabled), groups and additional details (event details enabled). This is what each new fetch is compared against.
- **Detected Change**: one change to one event in one calendar: field (or added/removed), old value, new value, detected-at time, and a status for the later notification feature.
- **Check Run**: the outcome of the latest check of one calendar (only one kept per calendar, replaced on every check): time, link used, success/failure and reason, number of events, number of changes, consecutive failure count and failing-since time.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On pairs of real MyTimetables feeds with known differences, 100% of field changes, additions and removals are recorded, with 0 false changes.
- **SC-002**: Checking an unchanged real MyTimetables calendar repeatedly over 24 hours produces 0 recorded changes.
- **SC-003**: For N links (from one or many users) pointing to the same class-group timetable, the source server receives exactly 1 scheduled request per check interval, plus 1 validation request per newly added link.
- **SC-004**: With empty-feed protection enabled, an invalid or revoked MyTimetables link never causes events to be recorded as removed.
- **SC-005**: A change present in the source calendar is recorded within one check cycle after it appears, where the check cycle is the check interval, or `calendars on the host ÷ the host's budget per minute` minutes when that is longer (FR-012a, FR-012b).
- **SC-006**: Both example class-group timetables (ALTI26VT4ES1, ALET26VT4A) are parsed with every event's name, times, rooms, teachers, type and groups extracted correctly; an English-language feed of the same timetable yields the same extracted values.
- **SC-007**: A generic calendar feed (e.g. an exported personal calendar with recurring events) is checked and its changes are recorded with the same accuracy as SC-001 for the generic fields.
- **SC-008**: After a restart, no changes are lost or recorded twice.
- **SC-009**: The same MyTimetables feed served from an unconfigured domain is handled with default iCalendar detection only, and from a configured domain with exactly the enabled features; switching features records 0 changes.
- **SC-010**: Checking an unchanged calendar writes 0 event rows; with 5,000 calendars the system completes every due check within the interval as long as the per-host budget allows it.
- **SC-011**: 100% of API requests without a valid operator key are rejected, and no API response or log line contains a calendar link.
- **SC-012**: With one calendar whose server never responds, every other calendar is still checked on schedule, and the stuck check is recorded as failed within the overall check time limit.
- **SC-013**: When N calendars on one host fall due at the same moment, consecutive requests to that host are never closer together than one minute divided by the budget, none of the N is skipped, and all N are checked within N divided by the budget minutes (when each check finishes faster than the concurrency limit divided by the budget rate, e.g. under 8 s at 16 concurrent checks and 120 per minute).

## Assumptions

- Scope is change detection plus the API that manages it: sending notifications, change filters, the user-facing UI and user accounts/login are separate features. Until login exists, the API is operated with a single operator key, and the frontend must call it from its server side so the key never reaches a browser.
- Target scale is a whole school: tens of thousands of subscribers and thousands of calendars on one deployment, with the source server's tolerance for request volume unknown (hence the per-host budget). How many calendars one MyTimetables host will carry is unverified: class-group links merge, but personal timetable links do not. With the default budget (120/min) and interval (10 min), one host keeps the 10-minute cycle up to 1,200 calendars; 5,000 calendars give a cycle of about 42 minutes until the operator raises that domain's budget.
- MyTimetables (Semestry) is the primary, fully supported source and Inholland feeds are the reference for testing; generic iCalendar feeds are supported for the standard fields.
- Links for the same class group from different users return identical content. This is inferred from the link structure (see investigation.md §2) and must be verified with a second user's link before relying on it.
- Dutch and English are the only MyTimetables languages supported; the English labels and values were confirmed with a real English feed of the same timetable.
- Cancellations in MyTimetables appear as events disappearing from the feed (every observed event has status "confirmed"); generic feeds may also mark events as cancelled via status.
- Generic duplicate detection only merges links that are the same after normalisation; different links with identical content are not merged.
- A MyTimetables timetable with no events cannot be distinguished from an invalid link, so such links are rejected at validation and can be re-added once events are published.
- The source indicates an hourly cache lifetime; the 10-minute default is the owner's choice, and 5 minutes is a considerate lower bound.
- MyTimetables domain configuration is operator-level (not per user) and takes effect from the next check; a configured domain matches its exact host only, not its subdomains.
- Only the latest successful state per calendar is kept as snapshot; history is retained only through the recorded changes, for their retention period.

### Future filter defaults (not part of this feature)

Suggested defaults for the later user-configurable filters; this feature records everything and applies none of them:

- Ignore changes to events that have already ended.
- Present a tentative → confirmed status change as "event confirmed" (low urgency).
- Ignore location changes on holiday events (their room lists are truncated).
- Do not notify on changes to activity type or group lists.
