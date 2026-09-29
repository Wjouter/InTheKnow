# Feature Specification: Web Frontend

**Feature Branch**: `claude/sveltekit-frontend-32b635`

**Created**: 2026-09-27

**Status**: Draft

**Input**: User description: "I want to implement the sveltekit frontend"

**Builds on**: [001-ical-change-detection](../001-ical-change-detection/spec.md) (calendar links, check status, recorded changes), [002-subscriber-change-filters](../002-subscriber-change-filters/spec.md) (filters and presets; it leaves the "event confirmed" wording to the frontend), [003-notification-channels](../003-notification-channels/spec.md) (channels, language, time zone, per-calendar notifications; its emails link to the frontend's notification pages) and [004-accounts-login](../004-accounts-login/spec.md) (accounts, 2FA, admins; its emails link to the frontend's account pages, and it leaves browser-side session handling to this feature). Everything those features offer through the API is built; this feature is the web interface students and admins use instead of calling the API.

## Clarifications

### Session 2026-09-27

- Q: Can this feature make small additions to the backend API, or must the frontend use the API exactly as it is today? → A: Small additive API changes are allowed when 005 lists each one by name (for example newest-first paging on the changes list); what 001 to 004 already return stays unchanged.
- Q: What should a signed-in student see first when they open the app, including one with no calendars yet? → A: The changes feed by default; the student can switch their home page to a combined view (calendar list on top, latest changes below). The choice is a user setting saved on the account (a new API addition, since the API has no general user settings yet), not per browser. With no calendars, the home page shows a short explanation and the add-calendar form.
- Q: What may the web interface's own server write to its logs? → A: Errors plus a basic access log (time, page, status, duration); never IP addresses, the user, query strings, tokens, cookies, passwords or email addresses.
- Q: Where do students change their user settings (home page, language, time zone)? → A: On one settings page split into tabs, a side list on wide screens: Account (address, email change, delete account), Sign-in & security (password, 2FA, sign-in methods, devices), Preferences (language, time zone, home page) and Notifications (the `/notifications` page: channels, the account-wide notification settings). Per-calendar notification settings are not in settings but on each calendar's page, in tabs Overview, Filter and Notifications (`/calendars/{id}/notifications`). Each side has a short note saying where the other is. Later settings get their own tab.
- Q: (during planning) How does the page find the invalid rule, and the number of calendars that follow a preset, when the API returns neither? → A: Two more additive API changes, named in FR-045: the rule position on rule validation errors, and a follower count on personal presets.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Create an account and sign in (Priority: P1)

A student opens the app, creates an account with their email address, a password and a language, confirms the address from the email they receive, and signs in. They can sign out again.

**Why this priority**: Every other page is behind sign-in. Without this, nobody can use the app without calling the API by hand.

**Independent Test**: With a mail catcher, sign up with a new address, open the confirmation link from the email, confirm, sign in, see the signed-in home page, sign out, and check that the home page now sends you to sign-in.

**Acceptance Scenarios**:

1. **Given** a visitor who is not signed in, **When** they open any page behind sign-in, **Then** they are sent to the sign-in page, and after signing in they land on the page they first asked for.
2. **Given** the sign-up page, **When** the visitor submits an address, a password and a language, **Then** they see a "check your email" message. They see the same message whether or not the address already has an account (004 FR-006).
3. **Given** a password that breaks the rules (length, too common, equal to the address), **When** the form is submitted, **Then** the page names the rule that was broken, keeps the other fields filled in, and the password rules are visible next to the field before submitting.
4. **Given** a confirmation email, **When** its link is opened, **Then** the confirmation page asks for the account's password and changes nothing until the student presses the confirm button. After confirming, they are told the account is ready and can sign in.
5. **Given** an unconfirmed account, **When** its owner signs in with the right password, **Then** they are told the address is not confirmed yet and can request a new confirmation email from that page.
6. **Given** a wrong password or an unknown address, **When** sign-in is submitted, **Then** the page shows one generic "email or password incorrect" message.
7. **Given** too many failed attempts, **When** sign-in is submitted, **Then** the page says how long to wait before trying again.
8. **Given** a signed-in student, **When** they sign out, **Then** they are signed out in this browser, their session ends on the server, and pressing the browser's back button does not show their data.
9. **Given** the operator has turned on a bot check, **When** a visitor signs in, signs up, requests a confirmation email or requests a password reset, **Then** the check is shown on that form and its result is sent with it. With no bot check configured, none is shown.

---

### User Story 2 - Add my calendar and see what changed (Priority: P1)

A signed-in student pastes their MyTimetables (or other iCalendar) link, sees the calendar appear with its check status, and reads the changes to their schedule as plain sentences, newest first.

**Why this priority**: This is the reason the app exists. With US1, it is the smallest version that gives a student value.

**Independent Test**: Sign in, add a valid calendar link, see it listed as checked OK, let the checker record changes for it (or use a test feed that changes), and read them on the changes page in the student's time zone.

**Acceptance Scenarios**:

1. **Given** a signed-in student, **When** they add a valid link, **Then** the page shows that it is being checked (this can take several seconds), prevents a second submit, and then shows the calendar in their list with its name and status.
2. **Given** an invalid, unsupported, unreachable or empty link, **When** it is added, **Then** the page says in plain words what is wrong with it (for example "this is not an https link" or "this calendar has no events") and nothing is added.
3. **Given** the calendar's host is busy with other checks, **When** a link is added, **Then** the page asks the student to try again after the stated wait.
4. **Given** a student's calendars, **When** they open the calendar list, **Then** each shows its name (their label, else the feed's name), when it was last checked, whether that check worked, when the next check is due, and, when checks keep failing, that it is failing and since how many checks. The link itself is never shown again after it was added.
5. **Given** recorded changes, **When** the student opens the changes page, **Then** they see the most recent changes first, each as a sentence naming the event, its date and time, and what changed (for example "ROS2 basics on Wed 1 Oct 08:30 moved from AL00.A1.38 to AL00.A1.20"), and they can load older changes.
6. **Given** several changes to one event found in the same check, **When** they are shown, **Then** they appear together under that event.
7. **Given** a status change from tentative to confirmed, **When** it is shown, **Then** it reads as "event confirmed" (002). A change to cancelled reads as "cancelled".
8. **Given** the student's filter marks some changes low priority and hides others, **When** the changes page is shown, **Then** low-priority changes are visibly de-emphasised, hidden changes are left out, and a "show hidden changes" switch shows them too, marked as hidden.
9. **Given** a student with more than one calendar, **When** changes are shown, **Then** each change names the calendar it belongs to.
10. **Given** a calendar in the list, **When** the student removes it and confirms, **Then** it disappears from the list and its changes no longer show.
11. **Given** a signed-in student, **When** they open the home page, **Then** they see the changes feed; after switching the home page to the combined view, they see their calendar list on top and the latest changes below, and that choice stays, on every device they sign in on, until they switch back.
12. **Given** a student with no calendars, **When** they open the home page in either view, **Then** they see a short explanation and the add-calendar form instead of an empty feed.

---

### User Story 3 - Choose which changes matter (Priority: P2)

A student adjusts a calendar's filter: they apply a preset (the Default or another global preset, or one of their own), or build their own rules (show only, hide, or mark low priority when conditions match), and save rules as a personal preset to reuse on other calendars.

**Why this priority**: The Default preset already hides the noise, so students can start without this. It matters once they want to follow only some groups, rooms or courses.

**Independent Test**: On a calendar, see that it uses Default; add a rule hiding changes for one group picked from the calendar's own group list; see those changes disappear from the changes page; save the rules as a preset and apply it to a second calendar.

**Acceptance Scenarios**:

1. **Given** a calendar, **When** the student opens the Filter tab of its calendar page, **Then** they see which preset it follows or that it is customised, its rules as readable sentences, and which rules do not apply to this calendar (for example group rules on a non-MyTimetables calendar) and why.
2. **Given** the rule editor, **When** the student adds a condition on activity type, group, room or teacher, **Then** they can pick values from the ones that occur in this calendar, and still type a value that does not occur (yet).
3. **Given** an edited filter, **When** it is saved, **Then** the calendar becomes customised; an invalid rule is reported next to that rule and the previous filter stays in place.
4. **Given** the preset list, **When** the student applies a preset to a calendar, **Then** the calendar follows it, and later edits to that preset apply to the calendar too.
5. **Given** a calendar's current rules, **When** the student saves them as a personal preset under a name, **Then** the preset appears in their list; a name already in use is refused with a message.
6. **Given** a personal preset that calendars follow, **When** the student edits or deletes it, **Then** the page says how many calendars follow it and what will happen to them (they follow the edit; on delete they keep its last rules as a custom filter), before the student confirms.
7. **Given** a global preset, **When** a student views it, **Then** they can apply it or copy it into a new personal preset, but not change or delete it.

---

### User Story 4 - Control my notifications (Priority: P2)

A student manages where notifications go (their account address, plus other email addresses they add and confirm), in which language and time zone they arrive, and which calendars send them. The links in every change email lead straight to these pages.

**Why this priority**: 003 sends emails without this, to the account address, in the language chosen at sign-up. But students need a way to stop or rename calendar notifications, and 003's emails already link here.

**Independent Test**: From a change email, open the calendar's notification link, sign in, turn that calendar's notifications off, and check that the next change to it sends no email. On the Notifications tab of the settings page, add a second address, confirm it from its email, send a test message, pause it, and remove it.

**Acceptance Scenarios**:

1. **Given** a change email, **When** the student opens a calendar's settings link (`/calendars/{id}/notifications`) while signed out, **Then** they sign in and land on the Notifications tab of that calendar's page; opening the link changes nothing by itself.
2. **Given** a signed-in student, **When** they open a settings link from an email, **Then** they land on that page without signing in again.
3. **Given** the Notifications tab of a calendar's page, **When** the student turns notifications off or on, or sets or clears its label, **Then** the change is saved and shown; the label must be 1 to 100 characters.
4. **Given** the Notifications tab of the settings page (`/notifications`), **When** the student opens it, **Then** they see every channel with its address and status (awaiting confirmation, active, paused, unsubscribed, failing with the reason).
5. **Given** the add-channel form, **When** an address is added, **Then** it shows as awaiting confirmation and the student is told to open the email sent to it. Limits (5 channels, an address already added, an address whose owner declined) are explained in plain words.
6. **Given** a channel, **When** the student pauses, resumes, sends a test message to, resends the confirmation for, or removes it, **Then** only the actions allowed in its current status are offered, and a limit reached (for example "at most 5 tests a day") is explained.
7. **Given** the account's own address channel, **When** it is shown, **Then** it is marked as the account address.
8. **Given** a new language, **When** the student saves it, **Then** the app itself and all later emails switch to that language.
9. **Given** a new time zone, **When** the student saves it, **Then** all times in the app and in later emails use it.
10. **Given** the Preferences tab of the settings page, **When** the student opens it, **Then** they see their language, their time zone (or that the default applies, and which zone that is) and their home page setting; the Notifications tab says that emails follow these and links to them.
11. **Given** the Notifications tab of the settings page, **When** it is shown, **Then** it says that turning a calendar's notifications on or off and its label are on that calendar's page, and links to the calendar list. **Given** the Notifications tab of a calendar's page, **When** it is shown, **Then** it says that where notifications are sent (email addresses) is in the settings page's Notifications tab, and links there.

---

### User Story 5 - Recover and manage my account (Priority: P2)

A student who forgot their password resets it from an emailed link. A signed-in student changes their password or email address, sees the devices signed in to their account and signs any of them out, and can delete their account.

**Why this priority**: Forgotten passwords are common and otherwise need the maintainer's help; the account and reset emails of 004 already link here.

**Independent Test**: Request a reset for an existing address, open the emailed link, set a new password, sign in with it. Then change the email address, confirm from the new address's email, sign out another device, and delete the account.

**Acceptance Scenarios**:

1. **Given** the "forgot password" page, **When** an address is submitted, **Then** the student sees the same "if the address has an account, we sent an email" message whether or not it has one.
2. **Given** a reset link, **When** it is opened, **Then** the page asks for a new password and changes nothing until the student submits; afterwards they are told every device was signed out and they can sign in.
3. **Given** an email change link opened from the new address, **When** the student presses confirm, **Then** the account moves to the new address; if the address was taken meanwhile, they are told so.
4. **Given** a link that is expired, already used or replaced by a newer one, **When** its page is submitted, **Then** the page says the link no longer works and offers the matching way to get a new one (request a new confirmation email, a new reset, or a new email change).
5. **Given** the settings page, **When** the student changes their password, changes their email address, turns 2FA off or deletes their account, **Then** they are asked to confirm who they are (password, plus an authenticator code when 2FA is on) unless they did so in the last 10 minutes, and the action then continues without re-entering it.
6. **Given** the settings page, **When** it is opened, **Then** its Account tab shows the address and a pending email change (if any), and its Sign-in & security tab shows the sign-in methods with when each was last used, and the signed-in devices with when each was last used, with the current one marked.
7. **Given** another signed-in device, **When** the student signs it out, **Then** it disappears from the list; "sign out everywhere" signs out every device, this one included.
8. **Given** the delete-account action, **When** the student confirms, **Then** the page states what will be deleted (calendars, filters, presets, channels, the account), the account is deleted, and the student is signed out. The last admin is told they cannot delete their account.

---

### User Story 6 - Protect my account with an authenticator app (Priority: P2)

A student turns on two-step verification: they scan a code with their authenticator app, enter a code to prove it works, and save their recovery codes. From then on, sign-in asks for a code after the password.

**Why this priority**: Optional for students, but required for admins (004): the first admin cannot use the admin pages until they have set it up.

**Independent Test**: Turn on 2FA by scanning the code, save the recovery codes, sign out, sign in with password plus code, then sign in once with a recovery code and see the number of recovery codes left drop.

**Acceptance Scenarios**:

1. **Given** the 2FA setup page, **When** it is opened, **Then** it shows a scannable code and the same secret as text for manual entry, and asks for a code from the app.
2. **Given** a correct code, **When** it is submitted, **Then** 2FA is on and the ten recovery codes are shown once, with a clear warning that they cannot be shown again and a way to copy or download them.
3. **Given** an account with 2FA, **When** its owner signs in with the right password, **Then** a second step asks for an authenticator code or a recovery code; this step expires after 10 minutes and then sends them back to sign in.
4. **Given** a recovery code was used, **When** the student later opens the Sign-in & security tab, **Then** it shows how many recovery codes are left and offers to generate new ones (which needs a current code).
5. **Given** 2FA is on, **When** the student turns it off, **Then** they must confirm with their password and a current code first; an admin is told to give up the admin role first.

---

### User Story 7 - Join through an invite (Priority: P2)

Someone invited by an admin joins: a named invite (emailed to one address) lets them choose a password and be signed in at once; a shareable invite link (for example posted to a class) opens sign-up with the invite filled in.

**Why this priority**: When sign-up is restricted, invites are the only way in, and the first admin is created through a named invite (004).

**Independent Test**: As admin, create a named invite; open the emailed link, choose a password, and land signed in. Create a shareable invite and sign up through its link with an address outside the allowed domains.

**Acceptance Scenarios**:

1. **Given** a named invite email, **When** its link (`/account/invite`) is opened, **Then** the page asks for a password (and offers the invite's language as default) and changes nothing until the invitee presses the button; afterwards they are signed in.
2. **Given** an admin invite, **When** it is accepted, **Then** the new admin is told that the admin pages unlock once they turn on two-step verification, with a link to set it up.
3. **Given** a shareable invite link (`/account/sign-up?invite=…`), **When** it is opened, **Then** the sign-up page shows that an invite is being used, and sign-up proceeds as in US1.
4. **Given** an invite that was revoked, expired or used up, **When** its page is submitted, **Then** the page says the invite no longer works.
5. **Given** sign-up is restricted and no invite is used, **When** someone outside the allowed domains signs up, **Then** they are told sign-up is by invitation only.

---

### User Story 8 - Admins run the app (Priority: P3)

An admin finds accounts and changes their role, disables, enables or deletes them, or removes their 2FA; decides who may sign up (open, allowed domains) and manages invites; maintains the global presets, including Default; and checks which calendars are failing or overdue and whether email sending works.

**Why this priority**: Only a few people use these pages, and until they exist an admin can still use the API. Needed before the app is opened school-wide.

**Independent Test**: As an admin with 2FA, search for an account and promote it (refused without 2FA, then allowed), invite a named address and revoke a shareable invite, edit Default's rules, and list failing calendars.

**Acceptance Scenarios**:

1. **Given** an admin without two-step verification on this session, **When** they open an admin page, **Then** they are told why it is locked and led to set up 2FA (or to sign in again with a code) instead of seeing an error.
2. **Given** a user without admin rights, **When** they open an admin page, **Then** they get a "not allowed" page; the admin pages are not linked for them.
3. **Given** the accounts page, **When** an admin searches by part of an address, **Then** matching accounts are listed with role, confirmed, disabled and 2FA, a page at a time.
4. **Given** an account, **When** an admin changes its role, disables, enables or deletes it, or removes its 2FA, **Then** a destructive action asks for confirmation first, and a refusal (for example "turn on 2FA before becoming admin", "demote first", "last admin") is explained in plain words.
5. **Given** the sign-up settings, **When** an admin changes open sign-up or the allowed domains, **Then** the new settings are saved and shown; an invalid domain is reported.
6. **Given** the invites page, **When** an admin creates a shareable invite, **Then** its link is shown once with a copy button and a warning that it cannot be shown again; named invites can be resent and any invite revoked.
7. **Given** the global presets page, **When** an admin edits a preset (including Default) or creates or deletes one, **Then** they use the same rule editor as US3; Default cannot be deleted.
8. **Given** the calendars overview, **When** an admin filters on failing or overdue calendars, **Then** they see each calendar's status, last error, subscriber count and link counts, a page at a time, without any links or addresses.
9. **Given** the email status panel, **When** an admin opens it, **Then** it shows whether email is configured, the last attempt and its result, consecutive failures and how many messages are waiting.

---

### User Story 9 - Use the app in English or Dutch (Priority: P3)

The app is shown in English or Dutch. A signed-in user sees their account's language; a visitor who is not signed in sees the language their browser prefers and can switch it.

**Why this priority**: Inholland students are largely Dutch-speaking, and the emails are already in both languages; but English alone is usable, so it comes after the core flows.

**Independent Test**: Visit the sign-in page with a Dutch browser and see it in Dutch; switch to English; sign in to an account set to Dutch and see the app in Dutch.

**Acceptance Scenarios**:

1. **Given** a visitor who is not signed in, **When** they open any page, **Then** it is in Dutch if their browser prefers Dutch, otherwise English, and a visible switch changes it.
2. **Given** the sign-up page, **When** a visitor signs up, **Then** the account's language defaults to the language the page is shown in.
3. **Given** a signed-in user, **When** they open any page, **Then** it is in their account's language.

---

### Edge Cases

- **Email links opened by mail scanners**: fetching any link from an email (account pages, notification pages) never changes anything; only an explicit button press does (004 research R9).
- **Token pages and referrers**: the pages that carry a token or invite in their address never pass that address to another site, including the bot check provider and any linked page (004 configuration).
- **Notification link for a calendar the student no longer has**, or a link opened while signed in as another account: the page says this calendar is not in the signed-in account (and shows which account is signed in), and changes nothing.
- **Session ends while the student is using the app** (idle timeout, signed out elsewhere, password reset, account disabled): the next action sends them to sign-in and, after signing in, back to the page they were on; a disabled account is told it is disabled.
- **Sign-in second step times out** (10 minutes): the code page sends them back to the password step with an explanation.
- **Step-up confirmation expires between opening a form and submitting it**: the student is asked to confirm who they are and the action then continues.
- **Return address after sign-in**: only pages of the app itself are accepted; a link that tries to send the user to another site after sign-in goes to the home page instead.
- **Backend unavailable or slow**: the page says the service is temporarily unavailable and to try again, keeps what the user typed where possible, and never shows technical details.
- **Rate limits**: every "wait" response (sign-in attempts, too many requests, confirmation and test limits, busy calendar host) is shown with the wait in plain words when the wait is known.
- **Bot check provider down**: sign-in continues without it (004 FR-040); sign-up and the request forms say to try again later. **Bot check blocked or not loading in the browser** (for example by a content blocker): the form says the check could not load and how to retry.
- **Calendar or event text containing markup** (names come from third-party feeds, labels and preset names from users): shown as plain text, never interpreted.
- **Recovery codes or a shareable invite link not saved**: leaving the page loses them for good; the page warns before it is left.
- **Events in another year**: dates show the year when it is not the current year.
- **Daylight-saving boundaries**: times are shown in the user's effective time zone as they were at that moment, not with today's offset.
- **Long lists**: a student with many changes and an admin with thousands of accounts or calendars get them a page at a time; nothing tries to load everything at once.
- **Double submit** of any form (slow calendar validation, sign-up, invite creation): one action happens.
- **An admin demoted or disabled while on an admin page**: the next action shows "not allowed" or sends them to sign-in.
- **No email configured on the server**: the add-channel form says adding addresses is not available right now.

## Requirements *(mandatory)*

### Functional Requirements

**Coverage**

- **FR-001**: The web interface MUST offer every user and admin action of features 001 to 004 that is available through the API, except those listed as out of scope in Assumptions, so that no user needs to call the API directly.
- **FR-002**: The web interface MUST get and change all data only through the API of features 001 to 004, plus the additive API changes this feature lists by name (FR-045), and MUST NOT keep its own copy of account, calendar, filter, channel or change data beyond a single page view.

**Sessions and security**

- **FR-003**: The browser MUST talk only to the web interface's own server, never to the API directly. The API session token MUST never be readable by page scripts and MUST be usable only through the web interface's server; how the browser's session refers to it is left to the plan.
- **FR-004**: The browser MUST stay signed in through a session that page scripts cannot read and that is only sent over HTTPS outside local development. It MUST NOT be sent with forms or background requests started by other sites, but a signed-in user who opens a link to the app from elsewhere (for example an email) MUST stay signed in.
- **FR-005**: Every action that changes something MUST be protected against being triggered from another site (cross-site request forgery).
- **FR-006**: Signing out MUST end both the browser's session and the API session. When the API reports the session is no longer valid, the browser's session MUST end too.
- **FR-007**: When calling the API for a browser, the web interface MUST pass on the browser's network address and device description, so that sign-in throttling and the device list work per user (004 research R3).
- **FR-008**: Pages behind sign-in MUST NOT be stored by shared caches, and MUST NOT be shown again from the browser's cache after sign-out.
- **FR-009**: Pages MUST refuse to be shown inside other sites' frames.
- **FR-010**: All text that comes from users or calendar feeds (event, calendar and preset names, labels, addresses, rule values) MUST be shown as plain text, never interpreted as markup.
- **FR-011**: After sign-in, the user MUST be returned to the page they asked for, but only if it is a page of the app itself.
- **FR-047**: The web interface's server MAY log errors (what failed, on which page) and a basic access log (time, page path without query string, status, duration). Its logs MUST NOT contain IP addresses, device descriptions, which user made the request, query strings, tokens, cookies, passwords or email addresses.

**Pages that email links point to** (contracts fixed by 003 and 004)

- **FR-012**: The web interface MUST serve, at these addresses:
  - `/account/confirm?token=…`: confirm an account with the token and the account's password;
  - `/account/reset?token=…`: set a new password;
  - `/account/email?token=…`: confirm an email address change;
  - `/account/invite?token=…`: accept a named invite with a password and optional language;
  - `/account/sign-up?invite=…`: sign up with a shareable invite;
  - `/notifications`: the Notifications tab of the settings page (US4, FR-048);
  - `/calendars/{id}/notifications`: the Notifications tab of one calendar's page (US4, FR-049).
- **FR-013**: The `/account/…` pages MUST tell the browser not to send their address as a referrer to anyone, and MUST act only on an explicit button press, never on opening the page.
- **FR-014**: `/notifications` and `/calendars/{id}/notifications` MUST require sign-in, return to themselves after sign-in (FR-011), and change nothing by being opened.

**Accounts (004)**

- **FR-015**: Users MUST be able to sign up (with email, password and language, and optionally a shareable invite), request a new confirmation email, confirm their account, sign in with a password and, when 2FA is on, an authenticator or recovery code, and sign out.
- **FR-016**: Sign-up, the confirmation request and the password reset request MUST show the same result whether or not the address has an account (004 FR-006).
- **FR-017**: Password fields MUST show the password rules (12 to 128 characters, not a common password, not the email address) before submitting, and name the rule that failed after.
- **FR-018**: When the operator configured a bot check, the sign-in, sign-up, confirmation request and reset request forms MUST show the provider's check and send its result; the provider and its public key come from the API (004 FR-039).
- **FR-019**: Users MUST be able to request and complete a password reset, change their password (or add one), request an email change, list and remove sign-in methods, list signed-in devices and sign out any other one, sign out everywhere, and delete their account.
- **FR-020**: When an action needs a recent confirmation of identity (004 step-up), the web interface MUST ask for the password (plus a code when 2FA is on), and then complete the original action without making the user fill it in again.
- **FR-021**: Users MUST be able to turn on 2FA from a scannable code with the secret also shown as text, see their recovery codes once, see how many are left, generate new ones, and turn 2FA off.
- **FR-022**: Named invites MUST be acceptable on their page, signing the invitee in; an admin invite MUST end with guidance to turn on 2FA.

**Calendars and changes (001, 002)**

- **FR-023**: Users MUST be able to add a calendar link, see each calendar's name, check status, last and next check, and failure count, and remove a calendar after confirming. The link MUST NOT be shown back.
- **FR-024**: Each outcome of adding a link (added, already added, invalid, not https, too long, blocked address, each validation failure, host busy) MUST have its own plain-language message.
- **FR-025**: The changes page MUST show the user's recorded changes most recent first, with older changes available on request, a page at a time.
- **FR-026**: Each change MUST be shown as a readable description of what happened to which event and when, for every change kind and field of 001 (added, removed, name, time, status, location, description, activity type, rooms, teachers, groups, additional details). Changes to one event from one check MUST be grouped. A tentative → confirmed status change MUST read as "event confirmed".
- **FR-027**: Low-priority changes MUST be visibly distinguished; hidden changes MUST be left out unless the user switches "show hidden changes" on, in which case they are marked as hidden.
- **FR-028**: All dates and times MUST be shown in the user's effective time zone (their own, else the server default), and include the year when it is not the current year.
- **FR-046**: The signed-in home page MUST show the changes feed by default. The user MUST be able to switch it to a combined view (calendar list on top, latest changes below) and back; the choice is a user setting saved on the account (FR-045), so it follows the user to every device. With no calendars, either view MUST show a short explanation and the add-calendar form in place of the feed.

**Filters and presets (002)**

- **FR-029**: For each calendar, users MUST see which preset it follows or that it is customised, its rules in words, and which rules are inactive on that calendar.
- **FR-030**: Users MUST be able to edit a calendar's rules with a form covering every outcome and condition of 002, picking condition values from the calendar's own values while still being able to type others.
- **FR-031**: Users MUST be able to apply any personal or global preset to a calendar, save rules as a personal preset, and rename, edit, copy and delete their personal presets, being told beforehand how many calendars follow a preset and what happens to them.
- **FR-032**: Rule and preset validation errors from the API MUST be shown next to the rule or field they concern, and the previously saved filter MUST stay in place.

**Notifications (003)**

- **FR-033**: Users MUST be able to set their language and time zone (or return to the default) on the Preferences tab (FR-048), see the default zone that applies, and, on the Notifications tab of each calendar's page (FR-049), turn that calendar's notifications on or off and set or clear its label.
- **FR-034**: Users MUST be able to add an email channel and, depending on its status, resend its confirmation, pause, resume, send a test message, and remove it. Only the actions valid in the channel's current status are offered.

**Settings and calendar pages**

- **FR-048**: The signed-in user's settings MUST be on one settings page divided into tabs, shown as a side list on wide screens and without horizontal scrolling on phones (FR-043): **Account** (address, email change, delete account), **Sign-in & security** (password, 2FA and recovery codes, sign-in methods, signed-in devices, sign out everywhere), **Preferences** (language, time zone, home page) and **Notifications** (channels, the account-wide notification settings; this tab is the `/notifications` page of FR-012, and it says that per-calendar notification settings are on each calendar's page, with a link to the calendar list). Each tab MUST have its own address, so that it can be linked to and returns to itself after sign-in (FR-011). A setting added later goes in the tab it fits, or a new tab.
- **FR-049**: Each calendar MUST have its own page divided into tabs, in the same style as the settings page: **Overview** (name, check status, last and next check, failures, remove), **Filter** (US3) and **Notifications** (notifications on or off and the label, US4; this tab is the `/calendars/{id}/notifications` page of FR-012, and it says that where notifications are sent is set in the settings page's Notifications tab, with a link there). Each tab MUST have its own address. The calendar list links to each calendar's page.

**Admin (004)**

- **FR-035**: Admin pages MUST be shown only to admins, and MUST lead an admin whose session has no second factor to set up 2FA or sign in with a code, instead of showing an error.
- **FR-036**: Admins MUST be able to list and search accounts a page at a time; change roles; disable, enable and delete accounts; and remove an account's 2FA, each destructive action after a confirmation.
- **FR-037**: Admins MUST be able to view and change the sign-up settings, create named and shareable invites, see pending invites with their uses left and expiry, resend named invites and revoke any invite. A shareable invite's link MUST be shown once, with a copy action.
- **FR-038**: Admins MUST be able to list, create, edit and delete global presets (Default: edit only) with the same rule editor as FR-030.
- **FR-039**: Admins MUST be able to see the calendars overview, filtered on failing and overdue, a page at a time, and the status of each notification channel type.

**Language, errors and access**

- **FR-040**: All fixed text MUST exist in English and Dutch. Signed-in users see their account language; other visitors see their browser's preferred language of the two (English otherwise) and can switch it.
- **FR-041**: Every refusal reason the API can return to a page MUST be shown as a plain-language message in the page's language; raw reason codes, status codes and technical details MUST NOT be shown.
- **FR-042**: When the API asks the user to wait, the page MUST say how long, when the wait is known.
- **FR-043**: Every page MUST be usable on a phone-sized screen without horizontal scrolling, and every action MUST be reachable and operable with a keyboard, with labelled form fields.
- **FR-044**: Every form MUST prevent a second submit while the first is being processed.

**API additions**

- **FR-045**: This feature MAY add to the API only where a requirement here cannot be met with the existing API, and each addition MUST be additive: existing endpoints, parameters and responses of 001 to 004 keep behaving as they do. The one addition known now is newest-first paging of the user's changes (FR-025): the user's changes can be read a page at a time starting from the newest, with the same filtering and hidden-change rules as today. The second is user settings: the account gains a home page setting (changes feed or combined view, default changes feed) that the signed-in user can read and change through the API, validated and stored on the account by the API (FR-046). The third is the rule position on rule validation errors: when the API refuses a filter or preset because one rule is invalid, the refusal also says which rule (FR-032). The fourth is the follower count of personal presets: the user's own presets say how many of the user's calendars follow each one (US3-6, FR-031). Any further addition the plan finds necessary MUST be named in this list before it is built.

### Key Entities *(include if feature involves data)*

- **Browser session**: links one browser to one API session. It holds nothing but what is needed to find that API session; it ends at sign-out or when the API session ends.
- **Language choice**: for visitors who are not signed in, the language they switched to, remembered in their browser only. It is not personal data.
- **Home page setting**: whether the signed-in home page shows the changes feed or the combined view (FR-046). It is stored on the account by the API (FR-045), not by the web interface; it is a display preference, not personal data.

No other data is stored by the web interface; everything else is read from and written to the API (FR-002). Its logs hold no personal data (FR-047).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A new student can go from the sign-up page to seeing their first calendar listed as checked, in under 5 minutes, not counting the wait for the confirmation email.
- **SC-002**: 100% of the user and admin actions of features 001 to 004 that are not out of scope can be done through the web interface; a walkthrough lists each action with the page that offers it.
- **SC-003**: The API session token appears in 0 places the browser can see: page contents, scripts, browser storage and readable cookies.
- **SC-004**: Opening each of the 7 pages in FR-012 without pressing a button changes nothing in 100% of cases.
- **SC-005**: For sign-up, the confirmation request and the reset request, the page shown for an address with an account is identical to the one for an address without.
- **SC-006**: For a test set that covers every change kind and field of 001, 100% of changes are shown as readable descriptions, with no raw field names, codes or JSON.
- **SC-007**: Every page works at a 360-pixel-wide screen with no horizontal scrolling, and every action can be completed with the keyboard alone.
- **SC-008**: 100% of fixed text exists in English and Dutch; an automated check fails on a missing translation.
- **SC-009**: 100% of the refusal reasons the API documents for actions the web interface offers have a message in both languages; an automated check fails on a missing one.
- **SC-010**: Event, calendar and preset names containing markup are shown as their literal text, with 0 markup interpreted.
- **SC-011**: When the API answers normally, 95% of pages show their content within 2 seconds on a typical home or campus connection.

## Assumptions

- **Users and devices**: students and admins use a current desktop or mobile browser with scripting enabled. Students mostly use phones.
- **Same host as the API**: the web interface's server runs next to the API, on the same host or container network, behind the same TLS-terminating reverse proxy (001 Transport). The operator lists it in the API's trusted proxies (004 configuration).
- **One change feed, not a timetable**: the API records changes but offers no list of current events, so this feature shows a feed of changes, not a timetable or calendar view.
- **Newest first**: the changes API pages from oldest to newest with a forward-only cursor. Showing the most recent first (FR-025) therefore needs the backward paging addition of FR-045; what 001 to 004 already return does not change.
- **Language default**: the app offers English and Dutch like the emails. A signed-in user's app language is the account's language; changing it changes both the app and the emails (003 FR-017 made it one setting).
- **Time zone list**: the time zone picker offers the standard time zone names; the API remains the judge of what is valid.
- **Bot check**: the provider's own browser widget is used; loading it sends the visitor's browser signals to that provider, which the operator names in their privacy notice (004).
- **Security log**: there is no API to read it (004), so there is no page for it.
- **Out of scope**: web push and other new channel types; installing the app to the home screen or offline use; a timetable view; passkeys, SURFconext, Google and GitHub sign-in (later features; the sign-in methods list already shows whatever methods exist); admin pages for settings that stay in server configuration (mail server, check intervals, host budgets, session lifetime); reading the security log; Docker deployment of the app (kept Docker-friendly, like the backend); the `/n/…` confirm, decline and unsubscribe pages, which the backend already serves; languages other than English and Dutch; a public landing page beyond a short explanation with sign-in and sign-up on the home page for visitors.
