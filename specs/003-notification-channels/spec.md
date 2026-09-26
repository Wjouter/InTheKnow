# Feature Specification: Notification Channels

**Feature Branch**: `003-notification-channels`

**Created**: 2026-09-26

**Status**: Draft

**Input**: User description: "I want to implement the notification system for this project. I want the system to be modular to support multiple notification providers. E-mail is the main one (and the only one we can actually fully implement and test right now) but I want web push notifications later (once only after the frontend is complete) and maybe webhooks later."

**Builds on**: [001-ical-change-detection](../001-ical-change-detection/spec.md), which records every change and leaves sending to this feature (001 FR-025), and [002-subscriber-change-filters](../002-subscriber-change-filters/spec.md), which decides per subscriber whether a change is hidden, low priority or normal (002 FR-009). This feature sends the changes that are not hidden.

## Clarifications

### Session 2026-09-26

- Q: How many channels can a subscriber have? → A: Several, up to 5 (e.g. a school and a personal address now, web push later).
- Q: Which calendars notify which channel? → A: Each subscription has its own "notify me" switch. Every active channel gets the changes of every subscription whose switch is on.
- Q: When are changes sent, and how are low-priority changes handled? → A: One message per channel with everything that became due within a short collection window (default 5 minutes). Low-priority changes are included and marked.
- Q: Language? → A: Notifications are in English or Dutch, chosen per subscriber. The setting is available through the API now; the frontend will offer it later.
- Q: Consent? → A: Until accounts exist, an address must be confirmed before it receives anything but the confirmation email. The later account feature will not make an account valid before its address is confirmed, so an account's own address will count as confirmed.
- Q: Unsubscribing? → A: Through the notification settings of the calendar in the later frontend, which requires login. Each email links, per calendar, to that calendar's notification settings page; there is no login-free unsubscribe page. The only login-free way out is the unsubscribe button of the mail client itself, which stops all emails to that address (kept because large mail providers expect it from senders of large volumes).
- Q: How does an email name a calendar, given that 001 stores no calendar name? → A: By the student's own label for that subscription, set through the API now and the frontend later. Without a label, the feed's own calendar name from its latest successful check is used, and the calendar's host when the feed has none.
- Q: In which time zone do emails show event and detection times? → A: In the subscriber's own time zone, used for all their notifications. It is set through the API now and in the account (frontend) later; the later account feature sets its default at account creation. Until then, a subscriber who has not set one gets the operator-configured default (Europe/Amsterdam).
- Q: How long are addresses of unconfirmed or unsubscribed channels kept? → A: A channel that has been *awaiting confirmation* or *unsubscribed* for 30 days is deleted automatically with its address. For "this wasn't me", only a one-way hash of the address is kept per subscriber, which blocks new confirmations to that address from that subscriber, also after the channel is removed.
- Q: How does the operator notice that sending fails for everyone (e.g. mail server down)? → A: Through an operator API endpoint showing per channel type: whether it is configured, the latest send outcome and reason, consecutive failures and since when, and how many messages are waiting; failed send attempts are also logged. No active alerting.
- Q: Which delivery status does a pending change get when it is dropped for a reason other than the filter (channel paused, unsubscribed or failing, notifications off, subscription removed)? → A: *skipped*, with a reason: hidden, channel not active, notifications off, or subscription removed.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Get an email when a lesson changes (Priority: P1)

A student adds their email address as a notification channel. They receive a confirmation email and confirm it. From then on, when a change that their filter does not hide is detected on one of their calendars, they receive an email saying which lesson changed and how (e.g. "ROS2 lecture on Tue 6 Oct moved from room D1.05 to D2.12"). Changes that come in close together arrive in one email.

**Why this priority**: This is the reason the project exists: students learn about timetable changes without checking the timetable themselves. Email is the only channel that can be built and tested end to end now.

**Independent Test**: Register and confirm an address for a subscriber, record changes on their calendar that cover a hidden, a low-priority and a normal outcome, and verify the captured email contains exactly the normal and low-priority changes with the right details.

**Acceptance Scenarios**:

1. **Given** a subscriber, **When** they add an email address, **Then** a channel is created as *awaiting confirmation* and one confirmation email is sent to that address; no other email is sent to it.
2. **Given** a channel awaiting confirmation, **When** the recipient opens the confirmation link and confirms, **Then** the channel becomes *active*.
3. **Given** an active email channel, **When** a change is recorded on one of the subscriber's calendars and its outcome for that subscriber is normal, **Then** the change is emailed to that address.
4. **Given** an active email channel, **When** a change is recorded whose outcome for that subscriber is hidden, **Then** it is not emailed.
5. **Given** a change that was not hidden when recorded, **When** the subscriber edits their filter so it is hidden before it is sent, **Then** it is not emailed.
6. **Given** a channel that became active today, **When** changes recorded before that moment exist, **Then** they are not emailed.
7. **Given** a change to a calendar that two subscribers share, each with an active channel, **When** it is sent, **Then** each subscriber gets it according to their own filter, and neither email reveals the other subscriber.
8. **Given** three changes become due for a channel within the collection window, one of them low priority, **When** the window ends, **Then** one email lists all three, with the low-priority one marked as such.
9. **Given** a subscriber with two active email channels, **When** a change is due, **Then** both addresses receive it.
10. **Given** a subscriber labelled one calendar "Minor robotics" and left another unlabelled, **When** an email lists changes of both, **Then** the first is named "Minor robotics" and the second by the feed's own calendar name.

---

### User Story 2 - Stop notifications for a calendar or an address (Priority: P1)

A student no longer wants emails about one calendar (e.g. a minor they stopped), or gets too many emails during exam week, or wants no emails at all. Each email links, per calendar, to that calendar's notification settings, where the logged-in student turns its notifications off (the page belongs to the later frontend; until then the same is done through the API). They can also pause, resume or remove a channel there. The mail client's own unsubscribe button stops all emails of that channel without login.

**Why this priority**: Sending email without a way out is unacceptable to recipients and to mail providers. This must ship together with US1.

**Independent Test**: With an active channel and two calendars, verify each email links to the settings page of each calendar it lists; turn a calendar off and on, pause, resume and remove the channel through the API, and use the mail client's unsubscribe; after each, record changes and verify what is and is not sent.

**Acceptance Scenarios**:

1. **Given** an email listing changes of calendars X and Y, **When** it is read, **Then** it contains, for each of X and Y, a link to that calendar's notification settings page, and no link that changes anything without login.
2. **Given** calendar X's notifications are turned off (through the API now, the settings page later), **When** changes are recorded, **Then** no channel of that subscriber receives changes of X any more, while changes of their other calendars still arrive, and X's changes are still recorded and listed as usual (002).
3. **Given** an active channel, **When** the recipient uses the unsubscribe button of their mail client, **Then** the channel becomes *unsubscribed* and receives no further emails.
4. **Given** a confirmation or "this wasn't me" link, **When** it is only fetched (as automatic link scanners in mail systems do) without the recipient's action, **Then** nothing changes.
5. **Given** a subscription whose notifications are off, **When** the subscriber turns them on again, **Then** only changes recorded after that moment are sent.
6. **Given** an unsubscribed channel, **When** the subscriber wants emails again, **Then** they must request a new confirmation, and emails resume only after the recipient confirms.
7. **Given** an active channel, **When** the subscriber pauses it, **Then** nothing is sent to it and its pending changes are recorded as *skipped* (channel not active); **When** they resume it, **Then** only changes recorded after the resume are sent.
8. **Given** a channel, **When** the subscriber removes it, **Then** it and its address are deleted, and pending sends to it are dropped.
9. **Given** a channel awaiting confirmation, **When** the recipient uses the "this wasn't me" option in the confirmation email, **Then** the channel becomes *unsubscribed* and no further confirmation emails are sent to that address for that subscriber.

---

### User Story 3 - Emails in English or Dutch (Priority: P2)

A student chooses whether their notifications are in English or Dutch. Every email, and the confirmation pages it links to, is in that language. For now the choice is made through the API; the frontend will offer it later.

**Why this priority**: Inholland has both Dutch and international students. Emails in one fixed language already deliver the P1 value.

**Independent Test**: Set one subscriber to Dutch and another to English, record the same change for both, and verify each email and the confirmation pages are fully in the chosen language, except for text that comes from the calendar.

**Acceptance Scenarios**:

1. **Given** a subscriber who never chose a language, **When** an email is sent to them, **Then** it is in English.
2. **Given** a subscriber whose language is Dutch, **When** a change email is sent, **Then** its fixed text, the names of change kinds, and its dates are in Dutch, while event names, rooms, event statuses and other calendar text are shown as they are in the calendar.
3. **Given** a subscriber switches from English to Dutch, **When** the next message is sent, **Then** it is in Dutch, including the confirmation email if one is requested.
4. **Given** a Dutch confirmation email, **When** the recipient opens its confirmation or "this wasn't me" link, **Then** the page is in Dutch.
5. **Given** a request to set a language other than English or Dutch, **When** it is saved, **Then** it is rejected with a reason and the previous language stays.
6. **Given** a subscriber who never set a time zone and one who set America/New_York, **When** the same change is emailed to both, **Then** the first shows its times in the operator default (Europe/Amsterdam) and the second in New York time (FR-018a).

---

### User Story 4 - Reliable delivery without duplicates (Priority: P2)

The mail server is sometimes briefly unavailable, and some addresses turn out not to exist. Changes are delivered once, late rather than never when the problem is temporary, and a channel that cannot receive mail is marked as such so the student can see it and fix the address.

**Why this priority**: Missing or duplicated notifications quickly make students distrust the service. The P1 stories already deliver value when everything works.

**Independent Test**: Make the mail capture refuse connections for a while, then accept again, and verify every pending change arrives once. Make it permanently reject one address and verify that channel is marked failing while other channels keep receiving. Restart the system mid-way and verify there are no duplicates.

**Acceptance Scenarios**:

1. **Given** the mail server is unreachable, **When** changes are due to be sent, **Then** they are retried with increasing delays and sent once it is reachable again, within the retry window (FR-026).
2. **Given** a send has kept failing for the whole retry window, **When** the window ends, **Then** those changes are recorded as *expired* and are not sent later.
3. **Given** the mail server permanently rejects an address, **When** that happens, **Then** the channel becomes *failing* with the reason, no further sends are attempted to it, and the subscriber sees this status and reason on the channel.
4. **Given** a failing channel, **When** the subscriber requests a new confirmation and the recipient confirms, **Then** the channel becomes active again; changes recorded while it was failing are not sent.
5. **Given** the system restarts while changes are pending, **When** it runs again, **Then** each pending change is sent once and changes already sent are not sent again.
6. **Given** the mail server is unreachable for every channel, **When** sends fail, **Then** no channel is marked failing because of it.
7. **Given** the mail server has been unreachable for a while, **When** the operator requests the channel type status, **Then** email shows the latest failure and its reason, the number of consecutive failures and since when, and how many messages are waiting; after the next successful send it shows success and 0 consecutive failures.

---

### User Story 5 - Mass changes stay readable and within limits (Priority: P2)

When a programme re-publishes its timetable, hundreds of events can change at once for thousands of students. Each student still gets one readable email, and the system does not overload the mail server or get the school's sender address blocked.

**Why this priority**: At whole-school scale this will happen every semester. It only matters once US1 works.

**Independent Test**: Record 300 changes on one calendar with 1,000 subscribers, each with an active channel. Verify each subscriber gets one email with capped content, and that the send rate never exceeds the configured limit.

**Acceptance Scenarios**:

1. **Given** many changes for one channel become due within the collection window, **When** it ends, **Then** they are sent as one message (FR-014).
2. **Given** one message would list more than the maximum number of changes (FR-015), **When** it is sent, **Then** it lists the first ones and states how many more there are.
3. **Given** many channels have messages due at the same time, **When** they are sent, **Then** no more messages leave per minute than the operator-configured limit, and the rest wait their turn rather than being dropped.

---

### User Story 6 - New channel types plug in without rework (Priority: P2)

The maintainer later adds web push (once the frontend exists) and possibly webhooks. Adding a channel type only requires building how that type confirms its target and delivers a message. Choosing which changes to send, applying filters and notification switches, combining, language, rate limiting, retrying, tracking and the channel lifecycle stay as they are.

**Why this priority**: The user explicitly wants the system modular for multiple notification providers. It is proven now with a second channel type that exists only in the automated tests, since email is the only type that can be built for real.

**Independent Test**: In the automated tests, register a test-only channel type next to email. Run the US1, US2 and US4 delivery scenarios against both and verify the test type receives exactly the same changes, statuses and retries as email.

**Acceptance Scenarios**:

1. **Given** a subscriber with an active email channel and an active test-type channel, **When** changes are recorded, **Then** both receive the same changes, combined the same way.
2. **Given** the test-type channel fails temporarily and then permanently, **When** sends are attempted, **Then** it gets the same retries and ends in the same *failing* status as an email channel would, without affecting the email channel.
3. **Given** the list of channel types a subscriber can add, **When** it is requested, **Then** only email is offered in production; the test type exists only in tests.

---

### User Story 7 - Send a test notification (Priority: P3)

After confirming their address, a student (or the operator, while setting up the mail server) sends a test notification to a channel to check that emails arrive and do not end up in spam.

**Why this priority**: Convenient for setup and support; real changes already show whether delivery works.

**Independent Test**: Request a test notification for an active channel and verify it arrives; request more than the limit and verify the extra requests are rejected.

**Acceptance Scenarios**:

1. **Given** an active channel, **When** the subscriber requests a test notification, **Then** a clearly labelled test email in the subscriber's language is sent to it.
2. **Given** a channel that is not active, **When** a test notification is requested, **Then** it is rejected with the channel's status as reason.
3. **Given** the test notification limit has been reached (FR-023), **When** another is requested, **Then** it is rejected and nothing is sent.

---

### Edge Cases

- **Outcome decided at send time**: the filter is applied when a change is sent, not when it is recorded, so filter edits in between count (002 FR-007). A change that is hidden at send time is recorded as *skipped* (hidden) and is never sent later, even if the filter changes again.
- **No backfill**: a channel only receives changes recorded while it is active and while the subscription's notifications are on, and only for subscriptions that existed when the change was recorded. Confirming, resuming, turning a calendar back on, re-confirming after unsubscribe or failure, and subscribing to a calendar do not send older changes. A calendar's baseline records no changes (001 FR-023), so subscribing never triggers a flood.
- **Links regrouped to another calendar** (001, at startup): the subscription's notification switch and label move with it, as its filter does in 002. If the subscriber already has a subscription on the target calendar, that one's switch and label are kept. A regroup re-baselines and records no changes, so it sends nothing.
- **Calendar turned off while changes are pending**: pending changes of that calendar are not sent; they are recorded as *skipped* (notifications off).
- **Subscription removed before sending**: pending changes of that calendar are not sent to that subscriber; they are recorded as *skipped* (subscription removed). Subscribing to the same calendar again starts with notifications on.
- **Change deleted by retention** (001 FR-025a) while still pending: it is not sent. With the default retention of one year and a retry window of hours, this should not happen in practice.
- **A change supersedes an earlier pending one** (e.g. a lesson moves to room A, then to room B, within one collection window): both are listed, in the order they were detected. Merging them is out of scope.
- **Event name, description or other event text contains markup or odd characters**: it is shown as plain text in the email and cannot change the email's layout or add links.
- **Same address on several channels**: a subscriber cannot add the same address twice. Different subscribers can each add the same address; each channel is confirmed and unsubscribed separately, and each email names only the calendars of the subscriber it belongs to. The mail client's unsubscribe stops only the channel of the email it was used on.
- **Sixth channel**: adding a channel when the subscriber already has 5 is rejected with a reason.
- **Confirmation link expired or used twice**: an expired link is rejected and the subscriber can request a new one; using an already used link changes nothing and says so.
- **Settings link of a removed subscription**: the link leads to the frontend, which decides what to show; nothing is changed by opening it.
- **Calendar turned off from one channel's email**: the switch belongs to the subscription, so all of the subscriber's channels stop receiving that calendar. The settings page says so.
- **Confirmation requests used to spam an address**: limited per channel (FR-006), and a recipient who chose "this wasn't me" gets no further confirmation emails from that subscriber, also if the subscriber removes the channel and adds the address again.
- **Channel never confirmed or left unsubscribed**: deleted with its address after 30 days (FR-029a); the subscriber can add the address again later, unless its recipient chose "this wasn't me".
- **Language or time zone changed while a message is waiting in its collection window**: the message is sent in the language and time zone set at send time.
- **Mail server accepts an email and later bounces it**: the bounce is not processed (out of scope); the change counts as sent.
- **System stops right after the mail server accepted an email but before it was recorded as sent**: that one email may be sent again on restart. This is the only way a duplicate can occur.
- **Detection runs behind**: changes are sent when they are recorded, not when the source changed them. The email shows when the change was detected.
- **Channel type configured off** (e.g. no mail server configured): channels of that type cannot be added or confirmed, existing ones keep their status and receive nothing, and their pending changes expire after the retry window. The API reports the type as unavailable.

## Requirements *(mandatory)*

### Functional Requirements

**Channels**

- **FR-001**: A subscriber MUST be able to have up to 5 notification channels. A channel has a type, a target (for email: an address) and a status: *awaiting confirmation*, *active*, *paused*, *unsubscribed* or *failing*.
- **FR-002**: Email MUST be the only channel type offered in this feature. Web push and webhooks are later channel types (out of scope) and MUST fit the same channel model, statuses and delivery rules.
- **FR-003**: Everything except confirming a target and delivering a message MUST be the same for every channel type: which changes are sent (FR-010 to FR-013), combining (FR-014, FR-015), the send rate limit (FR-016), language (FR-017, FR-018), delivery tracking (FR-024, FR-025), retries and expiry (FR-026, FR-027), and statuses (FR-001). This MUST be verified by running the delivery tests against email and a test-only channel type (US6).
- **FR-004**: Email addresses MUST be validated when added: syntactically valid, at most 254 characters, not already on another channel of the same subscriber, and within the channel limit (FR-001). Invalid input is rejected with a reason and nothing is stored.

**Consent**

- **FR-005**: A new email channel MUST start *awaiting confirmation* and receive a confirmation email. It MUST NOT receive any other email until the recipient confirms it. The confirmation link MUST expire after 48 hours.
- **FR-006**: A subscriber MUST be able to request a new confirmation email for a channel that is awaiting confirmation, unsubscribed or failing. At most 3 confirmation emails per channel are sent per 24 hours, the first one included. After the recipient chooses "this wasn't me", that subscriber MUST NOT be able to send further confirmation emails to that address: requesting one is rejected, and adding the address again, also after the channel was removed or deleted (FR-029a), is rejected with a reason. The block MUST be kept only as a one-way hash of the address per subscriber, never the address itself.
- **FR-007**: The confirmation and "this wasn't me" links MUST work without login, MUST act only on their own channel, and MUST only take effect after an explicit action by the recipient on the linked page, not when the link is merely fetched. Confirmation emails MUST contain a "this wasn't me" link.

**Turning notifications off**

- **FR-008**: Each subscription MUST have a notification switch, on for new subscriptions and for subscriptions that exist when this feature is deployed. The subscriber MUST be able to turn it on and off through the API. While it is off, no channel of the subscriber receives changes of that calendar; changes recorded while it is off are never sent. The switch MUST NOT affect what is recorded or what the subscriber's change list shows (002).
- **FR-009**: Every change email MUST contain, for each calendar it lists, a link to that calendar's notification settings page in the frontend. The frontend's address is operator configuration. The link itself MUST NOT change anything; turning notifications off there requires login (later features).
- **FR-010**: Every email except the confirmation email MUST support the unsubscribe button built into common mail clients, which sets the channel to *unsubscribed* without login and acts only on that channel. Test emails MUST also contain a link to the notification settings page.

**What is sent**

- **FR-011**: A change MUST be sent to an active channel of a subscriber when all of these hold: the change was recorded on a calendar of one of the subscriber's subscriptions; that subscription existed and had notifications on when the change was recorded, and still has them on when it is sent; the channel was active when the change was recorded and still is when it is sent; and its outcome for the subscriber at send time (002 FR-009) is not hidden.
- **FR-012**: Changes that no longer meet FR-011 at send time MUST NOT be sent and MUST be recorded as *skipped* for that channel, with the reason: *hidden* (outcome hidden), *channel not active* (paused, unsubscribed, or failing at send time), *notifications off*, or *subscription removed*. A skipped change is never sent later. When a channel becomes failing, its pending changes are recorded as *expired* instead (FR-027).
- **FR-013**: Low-priority changes MUST be sent like normal changes, and each message MUST mark which of its changes are low priority.

**Combining and limits**

- **FR-014**: When a change becomes due for a channel and no message is collecting for it, a collection window MUST start (operator-configurable, 1 to 60 minutes, default 5). When the window ends, every change due for that channel MUST be sent in one message, grouped per calendar and in detection order within a calendar.
- **FR-015**: A single message MUST list at most 50 changes; when more are due, it MUST list the first 50 in detection order and state how many more there are. The remaining ones count as sent.
- **FR-016**: Sending MUST respect an operator-configured maximum number of messages per minute per channel type (default for email: 60). Messages over the limit MUST wait, not be dropped, and still count toward the retry window.

**Language**

- **FR-017**: Each subscriber MUST have a notification language, English or Dutch, English by default. The subscriber MUST be able to change it through the API; other values MUST be rejected with a reason. Every message, including confirmation and test messages, MUST use the language set when it is sent, and the confirmation pages MUST use the language of the confirmation email they came from.
- **FR-018**: All fixed text MUST be available in both languages: email text, subjects, the names of change kinds and outcomes, and the confirmation pages. Dates and times MUST be written the way the language customarily writes them. Text from the calendar (event names, descriptions, rooms, teachers, groups, activity types, event statuses) MUST be shown as it is and not translated.

- **FR-018a**: Each subscriber MUST have a notification time zone (an IANA time zone name such as Europe/Amsterdam). Until they set one, the operator-configured default (default Europe/Amsterdam) applies. The subscriber MUST be able to change it through the API; unknown time zones MUST be rejected with a reason and the previous one stays. Every date and time in a message (event times and detection times) MUST be shown in the subscriber's time zone set when the message is sent, including summer/winter time.

**Content**

- **FR-019**: Each change in an email MUST show: which calendar it belongs to (by its name, FR-019a, never its link), the event name, the event's date and time (before and after, for time changes), what changed with its old and new value, whether it is low priority, and when it was detected. Added and removed events show the event as it is or was.
- **FR-019a**: A subscriber MUST be able to set and clear an optional label for each of their subscriptions (1 to 100 characters, validated like other names; invalid labels are rejected with a reason and the previous label stays). A calendar's name in emails MUST be the label if set; otherwise the feed's own calendar name from its latest successful check; otherwise the calendar's host. The feed's name MUST NOT be compared when detecting changes (001 FR-018), so a new name never records a change.
- **FR-020**: Emails MUST NOT contain calendar links, feed tokens or data of other subscribers. Text from the calendar MUST be shown as plain text, so it cannot add markup or links.
- **FR-021**: Emails MUST be readable both in mail clients that show formatting and in plain-text-only clients.
- **FR-022**: Emails MUST be sent from an operator-configured sender address and name.

**Test notifications**

- **FR-023**: A subscriber MUST be able to send a test notification to one of their active channels, at most 5 times per channel per 24 hours. It is labelled as a test and contains no changes.

**Delivery tracking**

- **FR-024**: For each change and each channel it is due for, the system MUST record its delivery status: *pending*, *sent*, *skipped* (with its reason, FR-012) or *expired* (not delivered within the retry window), and when that status was reached.
- **FR-025**: A change MUST be delivered at most once per channel, including across restarts, except for the single message that was accepted by the mail server right before the system stopped and not yet recorded as sent.

**Failures**

- **FR-026**: Temporary delivery failures MUST be retried with increasing delays for up to an operator-configured retry window (default 24 hours). After that, the changes MUST be recorded as *expired*.
- **FR-027**: A permanent rejection of a target (e.g. the address does not exist) MUST set the channel to *failing* with the reason, and its pending changes MUST be recorded as *expired*. A failure that affects every channel of a type (e.g. mail server unreachable) MUST NOT mark channels as failing.

**Management**

- **FR-028**: A subscriber MUST be able to add, list, pause, resume and remove their channels, request a new confirmation, send a test notification, turn each subscription's notifications on and off, set and clear each subscription's label, and view and set their language and time zone. Listing channels MUST show each channel's type, target, status, the reason when failing, and when it last received a message. Listing subscriptions MUST show whether notifications are on, the label, and the name used in emails.
- **FR-029**: Removing a channel MUST delete it with its target and its delivery records. Removing a subscriber's last subscription MUST NOT remove their channels, language or time zone.
- **FR-029a**: A channel that has been *awaiting confirmation* or *unsubscribed* for 30 days (counted from the later of entering that status and its latest confirmation email) MUST be deleted automatically, like a removal (FR-029). The "this wasn't me" block (FR-006) stays.
- **FR-030**: Notification management MUST be available through the HTTP API from 001, under the same authentication. The confirmation and "this wasn't me" links and the mail client's unsubscribe button MUST be the only notification actions that work without it, and only for their own channel.
- **FR-030a**: The operator MUST be able to request, through the operator API of 001, the status of each channel type: whether it is configured, the outcome of its latest send attempt (success or failure, and the reason), the number of consecutive failed attempts and since when, and the number of messages waiting to be sent. It MUST NOT show addresses or other subscriber data. Every failed send attempt MUST be logged with its reason (within FR-031).
- **FR-031**: Logs MUST NOT contain full email addresses, confirmation or unsubscribe links or tokens, or email content.
- **FR-032**: Mail server credentials MUST be operator secrets, supplied like the other secrets of 001 and never stored in the repository.

### Key Entities

- **Channel**: one place where a subscriber receives notifications. Type (email only in this feature), target, status, reason when failing, when it became active, when it last received a message. Belongs to one subscriber, at most 5 per subscriber; removed on request.
- **Email address** (personal data, the channel's target): needed to deliver notifications to the student; nothing else about the student is stored. Deleted with its channel, and automatically for unused channels (FR-029a).
- **Declined address** (per subscriber): a one-way hash of an address whose recipient chose "this wasn't me", needed to stop that subscriber from sending it confirmation emails again. Never the address itself; kept when the channel is removed.
- **Notification language** (per subscriber): English or Dutch, needed to write messages the student can read. Not personal data beyond that choice.
- **Notification time zone** (per subscriber): an IANA time zone name, needed to show times as the student lives them; unset means the operator default. Not personal data beyond that choice.
- **Subscription** (from 001, extended by 002): now also holds whether notifications are on for that calendar, and an optional label.
- **Subscription label** (free text from the student): needed so a student recognises their calendars in emails. Shown only to that subscriber, never compared or shared; removed with the subscription.
- **Feed calendar name** (from 001's calendar): the name the feed gives itself, kept from the latest successful check only as a fallback name for emails.
- **Confirmation**: a single-use, expiring proof that the recipient of the target agreed to receive notifications. Belongs to one channel.
- **Delivery**: for one recorded change and one channel, its status (pending, sent, skipped, expired), the reason when skipped, and when it reached it. Removed with its channel or its change.
- **Message**: one email (or later, one push or webhook call) to one channel, combining the changes of one collection window, or a confirmation or test notification. Written in the subscriber's language.
- **Channel type**: what differs per provider: how a target is validated and confirmed, and how a message is delivered. Email in this feature; web push and webhooks later. Has an operator-visible status (FR-030a): configured or not, latest send outcome, consecutive failures and since when, messages waiting.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In an end-to-end test against a local mail capture, 100% of changes with outcome normal or low priority reach every active channel of their subscriber for calendars with notifications on, and 0 changes with outcome hidden, or of calendars with notifications off, are sent.
- **SC-002**: 0 emails other than the confirmation email are sent to an address that has not been confirmed.
- **SC-003**: 100% of change emails link to the notification settings page of each calendar they list, 100% of non-confirmation emails support the mail client's unsubscribe button, and after it is used 0 further emails are sent to that channel. 0 links in emails change anything without login, except the confirmation and "this wasn't me" links in confirmation emails.
- **SC-004**: Across a test with repeated restarts while changes are pending, each change is delivered exactly once per channel.
- **SC-005**: After a mail server outage shorter than the retry window, 100% of pending changes are delivered after it recovers; 0 channels are marked failing because of the outage, and during the outage the operator status (FR-030a) shows email failing with its reason.
- **SC-006**: A change recorded on a calendar reaches a subscriber's inbox within 10 minutes with the default collection window, when the system and mail server are healthy and the send rate limit is not reached.
- **SC-007**: With 1,000 subscribers each getting 300 changes at once, each subscriber receives exactly one email per channel for that batch, no email lists more than 50 changes, and the configured send rate is never exceeded.
- **SC-008**: The delivery test suite passes unchanged for both email and the test-only channel type.
- **SC-009**: For both English and Dutch, 100% of the fixed text in every email type and linked page is in the subscriber's language.
- **SC-010**: 0 emails, log lines or API responses contain a calendar link, and 0 log lines contain a full email address.

## Assumptions

- **Subscriber identity**: user login does not exist yet (001), and there is no separate subscriber record: a subscriber exists through their subscriptions and presets (002). Until it does, a subscriber is the subscriber id in the request, and channels, the language and the notification switches belong to that id. The operator key protects the API; address confirmation is what prevents mail to people who did not ask for it.
- **Accounts later**: the later account feature will not make an account valid before its email address is confirmed. When it ships, the account's own address counts as confirmed and needs no second confirmation; other addresses a user adds keep the confirmation of FR-005. Channels, language, time zone and switches then move to the account; the account feature sets the time zone's default at account creation, and the frontend lets the user change it.
- **Frontend later**: the frontend will offer, behind login, channel management, the language setting and each calendar's notification settings (including turning it off and its label), all on top of the API of this feature. Email links point to those settings pages at an operator-configured frontend address; until the frontend exists they lead nowhere useful, and students stop emails with the mail client's unsubscribe button or through the operator. The backend serves only the minimal confirmation and "this wasn't me" pages, one button each, in both languages.
- **Depends on 002** (implemented): the outcome of a change per subscriber comes from 002's filter function (002 FR-009, `contracts/rules.md` "For the notification feature"), the same one the change list uses, with the outcomes normal, low priority and hidden.
- **Extends 001's calendar record**: 001 ignores the feed's calendar name because it changes with the feed language (001 FR-018). This feature keeps the latest one per calendar for display only. For a shared calendar it is whatever the most recently used link returned, possibly in the other language; a label avoids that.
- **Replaces 001's per-change notification status**: 001 records a single "notified" status per change, which 002 left untouched. A change can be due for many subscribers and channels, so this feature tracks delivery per change and channel (FR-024) instead.
- **Mail server**: the operator configures an existing outgoing mail server (e.g. the school's or a mail service) with its credentials. Sender domain setup (so emails are not marked as spam) is the operator's task. Tests use a local mail capture.
- **Default language**: English, because new subscribers have not chosen yet and English is readable for both Dutch and international students.
- **Out of scope**: web push and webhook channel types (later features; push after the frontend), user accounts and the frontend, bounce and complaint processing, quiet hours, merging successive changes to the same event, notifications about the system itself (e.g. a failing calendar link) and active alerts to the operator (the operator checks FR-030a), per-change delivery history in the API, and languages other than English and Dutch.
- **Limits** (FR-001, FR-006, FR-029a, FR-014, FR-015, FR-016, FR-023, FR-026) are sensible defaults for a school-wide deployment; they can be raised later without changing behaviour.
