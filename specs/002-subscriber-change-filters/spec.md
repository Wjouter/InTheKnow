# Feature Specification: Subscriber Change Filters

**Feature Branch**: `002-subscriber-change-filters`

**Created**: 2026-09-25

**Status**: Draft

**Input**: User description: "I want to create the filtering logic for this project. Filters should apply per subscriber, they should have reasonable defaults (discussed earlier). They should be editable by the user, the user must be able to save presets (they can use on multiple calendars), and admins should be able to create global filter presets for everyone."

**Builds on**: [001-ical-change-detection](../001-ical-change-detection/spec.md). That feature records every change and leaves the choice of which changes matter to this feature (001 FR-021). The defaults below are the ones listed there under "Future filter defaults".

## Clarifications

### Session 2026-09-25

- Q: When a preset is edited, do the calendars using it follow? → A: Linked until customised. A calendar follows its preset's edits until the subscriber edits that calendar's own filter, which gives it its own copy of the rules. Applying a preset again re-links it.
- Q: Are global presets offered or enforced? → A: Offered. Subscribers pick them; new subscriptions start on the Default preset; admins reach everyone on Default by editing it.
- Q: How does a subscriber see only one class from a timetable? → A: With a mix of allow-list rules ("show only") and block-list rules ("hide") in the same filter, rather than two separate filter types. "Show only" rules narrow a calendar to, for example, one attending group (or a course by event name on calendars without group data), and "hide" and "low priority" rules then apply to what is left. So the defaults keep working on an allow-listed calendar. Values such as groups are picked from those present in the calendar, so a typo cannot hide everything (FR-013).
- Q: Can rules use the MyTimetables-specific data (activity type, rooms, teachers, groups)? → A: Yes, but only on calendars on a configured MyTimetables domain with the feature that produces that data enabled. Elsewhere, rules that use it are kept but inactive.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Only see the changes that matter, out of the box (Priority: P1)

A student subscribes to their class calendar and does nothing else. Their list of changes leaves out noise: changes to lessons that are already over, room lists of holidays, activity-type and group-list changes. Confirming a draft event still shows, but at low priority. Every change stays recorded; the filter only decides what this student sees.

**Why this priority**: Without filtering, the change list (and the notifications built on it later) is full of changes nobody cares about. The defaults deliver that value without the student doing anything.

**Independent Test**: Record a set of changes on one calendar that covers each default rule plus some ordinary changes (a room change on a future lesson, a time change). Read the subscriber's changes and verify exactly the expected ones are hidden, low priority or normal.

**Acceptance Scenarios**:

1. **Given** a subscriber on the default filter, **When** a change is recorded for an event whose end time had passed before the change was detected, **Then** the change is hidden from that subscriber.
2. **Given** a subscriber on the default filter, **When** a future lesson moves to a different room, **Then** the change is shown with normal priority.
3. **Given** a subscriber on the default filter, **When** the room list of a holiday event changes, **Then** the change is hidden.
4. **Given** a subscriber on the default filter, **When** an event's activity type or group list changes, **Then** the change is hidden.
5. **Given** a subscriber on the default filter, **When** a future event changes status from tentative to confirmed, **Then** the change is shown with low priority.
6. **Given** a subscriber on the default filter, **When** a future event changes status from confirmed to tentative or cancelled, **Then** the change is shown with normal priority.
7. **Given** any filter, **When** changes are hidden for a subscriber, **Then** they are still recorded and can be listed by that subscriber on request, each marked as hidden.

---

### User Story 2 - Adjust the filter of one calendar (Priority: P1)

A student finds the defaults too strict or too loose for one of their calendars (e.g. they want to hide every change to a course they don't attend, or they do want to see group-list changes). They view that calendar's filter and edit its rules. Other subscribers of the same calendar are not affected.

**Why this priority**: Defaults never fit everyone; editing is the core of "user-customizable filters".

**Independent Test**: Two subscribers share one calendar. One edits their filter for it; verify their change list changes accordingly and the other subscriber's list does not.

**Acceptance Scenarios**:

1. **Given** subscribers A and B share a calendar, **When** A adds a rule hiding changes to events whose name contains "Zelfstudie", **Then** such changes are hidden for A and still shown for B.
2. **Given** a subscriber edits a calendar's filter, **When** they list their changes again, **Then** the new filter applies to every recorded change, including changes recorded before the edit.
3. **Given** a subscriber removes every rule from a calendar's filter, **When** they list their changes, **Then** every change of that calendar is shown with normal priority.
4. **Given** a subscriber submits a filter with an unknown field, an unknown condition or no conditions in a rule, **When** it is saved, **Then** it is rejected with a reason and the previous filter stays in place.

---

### User Story 3 - Show only one class from a timetable (Priority: P2)

A student subscribes to a timetable that covers several class groups (e.g. a programme timetable with ALTI26VT4ES1 and ALTI26VT4ES2), or one that includes courses they don't follow. They add a "show only" rule, for example "attending group is ALTI26VT4ES1" or "event name contains ROS2", and from then on only changes to those events reach them. The default "hide" rules still apply on top.

**Why this priority**: Makes shared or broad timetables usable for one student; the P1 stories already work without it.

**Independent Test**: On a calendar with events for two groups, add a "show only" rule for one group; verify changes to the other group's events are hidden and changes to the chosen group's events keep their normal outcome under the other rules.

**Acceptance Scenarios**:

1. **Given** a MyTimetables calendar with events for groups ES1 and ES2, **When** the subscriber adds a "show only" rule "attending group is ES1", **Then** changes to events attended only by ES2 are hidden and changes to ES1 events are shown.
2. **Given** that rule, **When** a change to an ES1 event is to an event that had already ended, **Then** it is still hidden by the default rule.
3. **Given** that rule and no rule hiding group-list changes (the Default preset has one), **When** ES1 is removed from an event's group list, **Then** the change is shown, because ES1 attended the event before the change.
4. **Given** a filter with two "show only" rules (group ES1; name contains "Minor"), **When** changes are read, **Then** a change is shown if it matches either rule.
5. **Given** a subscriber sets up a "show only" rule, **When** they pick the group, **Then** they can choose from the groups present in that calendar instead of typing it.

---

### User Story 4 - Filter on MyTimetables details (Priority: P2)

On calendars from a configured MyTimetables domain, a student can build rules on the details that only MyTimetables provides: activity type, rooms, teachers and attending groups. For example: "only notify me about tests", or "hide room changes for teacher X's lessons". These conditions are offered only on calendars where that data exists.

**Why this priority**: MyTimetables is the primary source (001); its extra data allows much sharper filters than the generic fields. The generic conditions already cover every calendar.

**Independent Test**: Subscribe one subscriber to a calendar on a configured MyTimetables domain and to a generic calendar. Verify the MyTimetables conditions are offered and work on the first, and are not offered and have no effect on the second, even when the same preset is applied to both.

**Acceptance Scenarios**:

1. **Given** a calendar on a configured MyTimetables domain with event details enabled, **When** the subscriber asks which conditions are available, **Then** activity type, rooms and groups are included, and teachers only when teacher detection is also enabled.
2. **Given** a generic calendar (or a MyTimetables domain with event details off), **When** the subscriber asks which conditions are available, **Then** only the generic conditions are listed.
3. **Given** a preset with a rule "show only activity type test", **When** it is applied to a MyTimetables calendar and to a generic calendar, **Then** on the MyTimetables calendar only test changes are shown, and on the generic calendar the rule is inactive (marked as such), so every change is filtered by the other rules only.
4. **Given** a rule "hide: change kind location, teacher is 'Jansen, Piet'", **When** the room of one of that teacher's lessons changes, **Then** the change is hidden, and room changes of other teachers' lessons are shown.
5. **Given** the operator disables event details for a domain, **When** the system restarts, **Then** the MyTimetables rules on that domain's calendars become inactive without their filters being edited, and become active again when the feature is re-enabled.

---

### User Story 5 - Save a filter as a preset and reuse it (Priority: P2)

A student with several calendars (their class group, a minor, a personal calendar) builds a filter they like and saves it as a named preset. They then apply that preset to their other calendars instead of repeating the edits. Calendars follow later edits to the preset until the student customises one of them.

**Why this priority**: Saves repeated work for students with more than one calendar; the per-calendar editing of US2 already works without it.

**Independent Test**: Save a preset, apply it to two calendars, verify both filter by it; edit the preset and verify both follow; customise one calendar and edit the preset again, and verify only the other one follows.

**Acceptance Scenarios**:

1. **Given** a subscriber has edited a calendar's filter, **When** they save it as a preset named "Minor", **Then** the preset appears in their list of presets.
2. **Given** a subscriber has a preset, **When** they apply it to another of their calendars, **Then** that calendar is linked to the preset and its changes are filtered by the preset's rules.
3. **Given** a preset is linked to two calendars, **When** the subscriber edits the preset, **Then** both calendars filter by the edited rules.
4. **Given** a calendar linked to a preset, **When** the subscriber edits that calendar's own filter, **Then** it becomes customised (a copy with the edit), later preset edits no longer affect it, and applying the preset again re-links it.
5. **Given** a subscriber deletes a preset that some of their calendars are linked to, **When** they list their changes, **Then** those calendars become customised with the rules they had, and their outcomes do not change.
6. **Given** subscriber A has a preset, **When** subscriber B lists their presets, **Then** A's preset is not listed and cannot be applied by B.

---

### User Story 6 - Admins offer global presets to everyone (Priority: P3)

An admin (the operator, until login exists) creates named global presets, e.g. "Everything" (no rules) or "Minimal" (also hides name and description changes), and maintains the built-in Default preset. Every subscriber can see the global presets and apply them to their calendars, or copy one into their own presets and edit that copy. Global presets are offered, never forced.

**Why this priority**: Lets the operator improve filtering for the whole school in one place and gives students ready-made choices. The built-in Default preset (US1) already covers the main need without any admin action.

**Independent Test**: As operator, create a global preset; as two different subscribers, list and apply it; edit the Default preset and verify a subscriber linked to it sees the new behaviour while a customised one does not.

**Acceptance Scenarios**:

1. **Given** the operator creates a global preset "Minimal", **When** any subscriber lists available presets, **Then** "Minimal" is listed as a global preset.
2. **Given** a global preset exists, **When** a subscriber applies it to a calendar, **Then** that calendar is linked to it and its changes are filtered by it.
3. **Given** a global preset exists, **When** a subscriber tries to edit or delete it, **Then** the request is rejected; **When** they copy it, **Then** they get their own preset with the same rules that they can edit.
4. **Given** the operator edits the Default preset, **When** subscribers list their changes, **Then** calendars linked to Default use the edited rules and customised calendars are unaffected.
5. **Given** the Default preset, **When** the operator tries to delete it, **Then** the request is rejected.
6. **Given** the operator deletes a global preset that calendars are linked to, **When** those subscribers list their changes, **Then** those calendars become customised with the rules they had.
7. **Given** global presets exist, **When** a new subscription is created, **Then** it is linked to the Default preset, and no admin action changes an existing subscription's choice of preset.

---

### Edge Cases

- **"Already ended" is judged at detection time, not read time**: a change is "to an ended event" when the event's end time was before the moment the change was detected, both before and after the change. Otherwise every change would disappear from the list once its event passes. A time change that moves a past event into the future is therefore *not* to an ended event and is shown. For an added event the new end time counts; for a removed event the old one.
- **Event conditions look at both sides of a change**: a condition on the event (activity type, group, room, teacher, name) matches when the event matched it before *or* after the change. So a lesson renamed away from "ROS2" still shows under "name contains ROS2", and the change that removes ES1 from an event passes "show only group ES1". Under the Default preset that last change is still hidden, because Default hides group-list changes. For an added event only the new state exists, for a removed event only the old one.
- **Allow-list with nothing active**: when every "show only" rule of a filter is inactive on a calendar (e.g. a group rule on a generic calendar), no allow-list applies there; the calendar is not silently emptied.
- **Allow-list with a value that never occurs** (e.g. a typo in a group name): every change of that calendar is hidden. The subscriber can see this by listing hidden changes; picking values from the calendar (FR-013) makes this unlikely.
- **Events without MyTimetables data** (generic feeds, MyTimetables domains with event details off, or events whose description has no such line): conditions on activity type, rooms, groups or teachers do not match them.
- **Location with and without event details**: the location change kind covers both the raw location (event details off) and the rooms list (event details on); the description change kind covers both the raw description and the additional event details.
- **Conflicting rules**: hidden wins over low priority; a change outside an active allow-list is hidden whatever the other rules say.
- **A calendar's features change** (domain added to or removed from the MyTimetables list, event details or teacher detection toggled): rules using MyTimetables conditions become active or inactive at the next start; the stored filters are not changed.
- **Subscriber removes a subscription**: the filter of that subscription is removed with it; the subscriber's presets stay. Subscribing to the same calendar again starts linked to Default.
- **Shared calendars**: one calendar with many subscribers is filtered separately for each subscriber; filtering never affects what is recorded or what other subscribers see.
- **One subscriber with several links to the same calendar**: it is one subscription, so one filter.
- **Existing subscriptions when this feature ships**: they start linked to Default, like new ones.
- **Subscriber with a very large number of recorded changes**: the filtered change list is still paged, and a page never comes back empty just because every change on it is hidden while later visible changes exist.
- **A preset is edited while subscribers read changes**: each read uses one consistent version of each filter.

## Requirements *(mandatory)*

### Functional Requirements

**Filtering**

- **FR-001**: Filtering MUST NOT change what is recorded: every change is still recorded as in 001, and filters only decide how a recorded change is presented to a subscriber (and later notified).
- **FR-002**: Each subscription (one subscriber and one calendar) MUST have exactly one filter. A filter applies only to that subscriber's view of that calendar.
- **FR-003**: A filter MUST be a list of rules. Each rule MUST have one or more conditions, all of which must match (AND), and one outcome: *show only*, *hide* or *low priority*.
- **FR-004**: The outcome of a change MUST be decided with the rules that are active on the subscription's calendar (FR-006), in this order:
  1. If the filter has at least one active *show only* rule and the change matches none of them, it is *hidden*.
  2. Otherwise, if it matches any active *hide* rule, it is *hidden*.
  3. Otherwise, if it matches any active *low priority* rule, it is *low priority*.
  4. Otherwise it is *normal*.
- **FR-005**: Rule conditions MUST be limited to this set. Conditions available on every calendar:
  - (a) **change kind**: one or more of added, removed, name, time, status, location, description. Location covers both raw location and rooms; description covers both raw description and additional event details.
  - (b) **event had ended**: the event's end time was before the change was detected, both before and after the change.
  - (c) **status transition**: a status change from one status and/or to one status (confirmed, tentative, cancelled, none). Matches status changes only.
  - (d) **event name contains**: case-insensitive text match on the event's name.

  MyTimetables conditions, available only as described in FR-006:
  - (e) **change kind** additionally: activity type, groups, teachers.
  - (f) **activity type is**: one or more of lesson, test, holiday, appointment, or another type given as text.
  - (g) **attending group is**: one or more group names.
  - (h) **room is**: one or more room names.
  - (i) **teacher is**: one or more teacher names.

  A change-kind condition that lists both generic and MyTimetables kinds stays active on every calendar; the kinds that are not active there never match.

  Conditions (d) and (f) to (i) match when the event matched before or after the change. Values in (f) to (i) are compared exactly, ignoring letter case; a condition with several values matches when any of them matches.
- **FR-006**: MyTimetables conditions MUST be active only on calendars whose domain is a configured MyTimetables domain (001 FR-002) with event details enabled; the teacher conditions additionally need teacher detection enabled. Rules using a condition that is not active on a calendar MUST be kept but ignored for that calendar, and MUST be reported as inactive when the subscriber views that calendar's filter.
- **FR-007**: The outcome of a change for a subscriber MUST be decided with the subscription's current filter at the time it is read, so editing a filter also applies to changes recorded earlier.
- **FR-008**: The change list of a subscriber MUST leave out hidden changes by default and MUST mark each returned change as normal or low priority. On request, it MUST also return hidden changes, marked as hidden.
- **FR-009**: The later notification feature MUST be able to get the outcome of each recorded change per subscriber, so hidden changes are not notified and low-priority changes can be notified accordingly.

**Default filter**

- **FR-010**: The system MUST ship a built-in global preset, "Default", containing these rules:
  - hide: event had ended;
  - hide: change kind location, and activity type is holiday;
  - hide: change kind activity type or groups;
  - low priority: status transition from tentative to confirmed.
- **FR-011**: Every new subscription, and every subscription that exists when this feature is deployed, MUST start linked to the Default preset.
- **FR-012**: The operator MUST be able to edit the Default preset's rules and rename it, but MUST NOT be able to delete it. Renaming does not change its role: new subscriptions still start on it.

**Editing**

- **FR-013**: For each of their subscriptions, a subscriber MUST be able to see which conditions are active on it and, for its MyTimetables conditions, the activity types, groups, rooms and teachers that occur in the calendar's current events, so values can be picked rather than typed.
- **FR-014**: A subscriber MUST be able to view and replace the filter of each of their subscriptions, and see whether it is linked to a preset (and which) or customised.

**Presets**

- **FR-015**: A subscriber MUST be able to create, rename, edit and delete their own named presets, save a subscription's filter as a new preset, and apply one of their presets or a global preset to any of their subscriptions.
- **FR-016**: Presets MUST be private to their subscriber: other subscribers can neither list nor apply them.
- **FR-017**: Applying a preset MUST link the subscription to it: the subscription filters with the preset's current rules, including later edits. Editing the subscription's own filter MUST make it customised: it keeps its own copy of the rules with the edit, and later preset edits no longer affect it. Applying a preset again MUST re-link it.
- **FR-018**: Deleting a preset (personal or global) MUST turn the subscriptions linked to it into customised subscriptions with the preset's last rules, so their outcomes do not change.
- **FR-019**: A subscriber's presets MUST remain when they remove subscriptions, including their last one.
- **FR-020**: A preset MAY contain MyTimetables conditions; applying it to a calendar where they are not active is allowed, and those rules are inactive there (FR-006).

**Global presets**

- **FR-021**: The operator MUST be able to create, rename, edit and delete global presets. Global presets are visible to every subscriber.
- **FR-022**: Subscribers MUST be able to apply a global preset and to copy it into their own presets, but MUST NOT be able to edit or delete it.
- **FR-023**: Global presets MUST only be offered: no admin action changes which preset an existing subscription is linked to or its customised rules. Admins reach subscriptions only by editing a preset they are linked to.

**Validation and limits**

- **FR-024**: Filters and presets MUST be validated server-side: unknown change kinds, conditions, statuses or outcomes, rules without conditions, conditions without values, and empty or overlong names or text MUST be rejected with a reason, leaving the previous state unchanged. MyTimetables conditions are not rejected for being inactive on a calendar (FR-020).
- **FR-025**: The system MUST enforce limits: at most 50 rules per filter, at most 50 values per condition, at most 50 personal presets per subscriber, at most 100 global presets, names of 1 to 100 characters, unique per owner (per subscriber, or among global presets), and condition texts and values of 1 to 100 characters.
- **FR-026**: Filter and preset management MUST be available through the HTTP API from 001, under the same authentication. Global preset management MUST be limited to the operator.

### Key Entities

- **Rule**: a set of conditions (FR-005), all of which must match, and an outcome (show only, hide or low priority).
- **Filter**: the rules that apply to one subscription: either the rules of the preset it is linked to, or its own customised rules. Belongs to exactly one subscription and is removed with it.
- **Preset**: a named, reusable list of rules. Either *personal* (owned by one subscriber, private) or *global* (owned by the operator, offered to all). The built-in Default preset is a global preset that cannot be deleted.
- **Subscription** (from 001): the relation between a subscriber and a calendar; now also holds its filter: linked to a preset, or customised.
- **Change outcome**: for one recorded change and one subscriber, the result of the filter: hidden, low priority or normal. Derived when read, not stored.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On a set of recorded changes covering each Default rule and ordinary changes, a subscriber on the Default preset gets 100% of changes with the expected outcome (hidden / low priority / normal).
- **SC-002**: For two subscribers of one calendar with different filters, each sees exactly the outcomes of their own filter; changing one subscriber's filter changes 0 outcomes for the other.
- **SC-003**: After a filter or preset edit, the very next read of the change list reflects it for every linked subscription, for changes recorded before and after the edit; customised subscriptions show 0 changed outcomes.
- **SC-004**: With hidden changes included on request, a subscriber sees 100% of the changes recorded for their calendars; filtering never makes a recorded change unreachable.
- **SC-005**: With a "show only attending group" rule on a timetable of two groups, 100% of changes to events attended only by the other group are hidden, and 0 changes to the chosen group's events are hidden by the allow-list.
- **SC-006**: The same preset applied to a MyTimetables calendar and a generic calendar gives the MyTimetables rules full effect on the first and 0 effect on the second, and the second reports them as inactive.
- **SC-007**: A subscriber can apply a saved or global preset to a calendar in one action.
- **SC-008**: 100% of invalid filters and presets are rejected with a reason, and none of them changes stored state.
- **SC-009**: A subscriber's filtered change list stays paged and a page is returned in under 1 second for a subscriber whose calendars hold 10,000 recorded changes, of which most are hidden.

## Assumptions

- **Who is an admin**: user login does not exist yet (001). Until it does, "admin" means the operator key holder, and "subscriber" means the subscriber id in the request, as in 001. When login ships, it decides which users may manage global presets; the rules here stay the same.
- **Dependency on 001's change record**: conditions (b) and (d) to (i) need, for every recorded change, the event's end time, name, activity type, groups, rooms and teachers both before and after the change, as they were at detection. The 001 change record carries the event's name and start time only for field changes. This feature extends what 001 records per change. No changes recorded before that extension exist yet, so no backfill is needed.
- **"One class"** means an attending group (MyTimetables) or a course recognised by its event name. Generic calendars have no group data, so there only the name condition applies.
- **Presentation**: the "event confirmed" wording for a tentative → confirmed status change is up to the frontend and the notification feature; this feature only supplies the status change and its low priority.
- **Out of scope**: sending notifications, the frontend, per-calendar filters shared between subscribers, filtering at detection time, time-window conditions other than "event had ended", conditions on individual additional-detail labels, OR-combinations inside one rule (use several rules), and forcing global presets on subscribers.
- **Subscriber lifecycle**: 001 has no separate subscriber record; a subscriber exists through their subscriptions. A subscriber's presets are kept even when they currently have no subscriptions.
- **Limits** (FR-025) are sensible caps for a school-wide deployment; they can be raised later without changing behaviour.
