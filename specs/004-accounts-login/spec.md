# Feature Specification: Accounts and Login

**Feature Branch**: `004-accounts-login`

**Created**: 2026-09-27

**Status**: Draft

**Input**: User description: "I want to add the accounts and login system for this app. The account system should (for now) support login with e-mail and password. But should be (maybe modular?) able to support passkeys, OAuth (SURFconext for instance) and other third party services like google, and github. It should also support 2fa (via an 2fa app like google authenticator)."

**Builds on**: [001-ical-change-detection](../001-ical-change-detection/spec.md), whose API identifies a subscriber by an id in the request and requires the operator key "until login exists" (001 FR-031); [002-subscriber-change-filters](../002-subscriber-change-filters/spec.md), which leaves to this feature who may manage global presets; and [003-notification-channels](../003-notification-channels/spec.md), which expects accounts to be valid only after their address is confirmed and moves channels, language, time zone and notification switches to the account.

## Clarifications

### Session 2026-09-27

- Q: Who may sign up? → A: An admin decides, and can change it at any time: open sign-up (anyone) on or off, plus a list of allowed email domains. Invites always work: with open sign-up off, addresses in an allowed domain can still sign up, and anyone else only through an invite. With open sign-up off and no domains, sign-up is invite-only. Admins manage this through the API now and the frontend later.
- Q: Is a separate operator status needed next to admin? → A: No. Admin is the only elevated role in the app. The operator key (001 FR-031) is retired, and everything it protected becomes an admin endpoint. "Operator" now only means whoever runs the server: they set what must stay in server configuration (mail server, secrets, frontend address, check intervals) and can make an admin from the server, which is how the first admin is created and how a lost admin is recovered. Admins promote and demote other admins in the app.
- Q: Can an invite be used by more than one person? → A: Yes. The admin chooses how many sign-ups an invite allows (default 1). An invite for one named address is emailed, allows exactly one sign-up and confirms the account at once; an invite without an address is a link the admin shares (e.g. with a class), each person confirms their own address, and it can never grant admin.
- Q: How is "admins must have 2FA" enforced? → A: At three points: every admin request needs an admin account whose session has proven a second factor; an account can only be promoted to admin when it already has 2FA on; and an admin cannot turn off their own 2FA. Admins created from the server or through an admin invite start without 2FA, and their admin access stays locked until they turn it on.
- Q: Can an account have several sign-in methods that are added and removed later? → A: Yes. Any mix (e.g. a password, SURFconext and a passkey, the latter two in later features) can be added and removed while signed in, as long as at least one remains. Every account keeps exactly one confirmed email address whatever its methods, because notifications, security notices and resets need it.
- Q: Should sign-in and sign-up be protected against bots? → A: Yes, with a bot check (CAPTCHA) chosen by the operator: Cloudflare Turnstile first, Google reCAPTCHA as a second option, and other providers pluggable later. It guards sign-in, sign-up and the other actions anyone can trigger without being signed in. It is off until the operator configures a provider.
- Q: When an account has had 5 failed sign-in attempts, should the waiting time block only the network address the failures came from, or every attempt at that account? → A: Only that address. A second, higher limit per account across all addresses (20 failures per hour) makes every address wait, up to 15 minutes. Someone guessing from elsewhere can therefore not keep the owner locked out; someone on the owner's own network address still waits with them.
- Q: How should an admin find the account they want to promote, demote, or turn 2FA off for? → A: Admins can list and search all accounts (address, role, confirmed, 2FA on/off, created at), and can also disable or delete other accounts.
- Q: Who should be able to read the security log? → A: Only whoever runs the server; there is no API for it. Users can see their own signed-in devices (sessions) and sign out any of them except the one they are using.
- Q: SC-005 promised at most 20 evaluated wrong attempts per hour, but FR-012's 15-minute cap lets one attempt through after each wait. Which gives? → A: Keep FR-012 as it is and state the true bound in SC-005: 23 per rolling hour (20, then one per 15-minute wait).
- Q: Several actions said "by entering the password", which an account without a password cannot do. How does such an account confirm who it is? → A: Those actions require confirming who they are with an existing method (FR-027a), plus a code when 2FA is on. A confirmation stays valid for 10 minutes in that session. Changing the password still needs the current password, since only accounts with one can do it.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Create an account and sign in with email and password (Priority: P1)

A student creates an account with their email address and a password, confirms the address from the email they receive, and signs in. From then on the app knows who they are. They can sign out again.

**Why this priority**: Nothing else in this feature works without an account and a way to sign in; it is the MVP.

**Independent Test**: Sign up with a new address, confirm it through the emailed link, sign in with the password, call an endpoint that needs a signed-in user, sign out, and check that the same call is now rejected.

**Acceptance Scenarios**:

1. **Given** no account exists for an address and the sign-up settings allow it (US8), **When** someone signs up with that address, a valid password and a language, **Then** an unconfirmed account is created and a confirmation email in that language is sent to the address.
2. **Given** an unconfirmed account, **When** its owner opens the confirmation link and confirms on the linked page with the account's password, **Then** the account becomes confirmed; merely fetching the link changes nothing, and the link without the password confirms nothing.
3. **Given** an unconfirmed account, **When** its owner signs in with the correct password, **Then** sign-in is refused with the reason "address not confirmed" and they can request a new confirmation email.
4. **Given** a confirmed account, **When** its owner signs in with the correct email and password, **Then** they are signed in and can use the app as that account.
5. **Given** a sign-in with a wrong password, an unknown address, or a password that fails the password rules, **When** it is submitted, **Then** the response is the same generic "email or password incorrect" in all cases.
6. **Given** a signed-in user, **When** they sign out, **Then** that session ends and requests made with it are rejected.
7. **Given** an address that already has a confirmed account, **When** someone signs up with it, **Then** the response looks exactly like a successful sign-up, no second account is created, and the owner gets an email saying someone tried to sign up with their address.

---

### User Story 2 - My calendars, filters and channels are mine (Priority: P1)

Everything a student did through the API by subscriber id (calendars, recorded changes, filters and personal presets, notification channels, language, time zone, notification switches) now belongs to their account. A signed-in student sees and changes only their own data.

**Why this priority**: 001 to 003 were built on the promise that login would replace the subscriber id. Until it does, the API cannot be opened to students, and a mistake here exposes one student's calendar links and schedule to another.

**Independent Test**: Create two confirmed accounts A and B. As A, add a calendar, a personal preset and a channel. As B, try to read and change each of A's items through every user endpoint; every attempt is rejected, and B's own lists stay empty.

**Acceptance Scenarios**:

1. **Given** a signed-in account, **When** it adds a calendar link, a filter, a personal preset or a channel, **Then** the item belongs to that account.
2. **Given** accounts A and B, **When** B requests or changes any of A's calendars, changes, presets, channels or settings, **Then** the request is rejected and reveals nothing about A's data, not even whether the item exists.
3. **Given** a request with no session, an expired session, or a session of an unconfirmed account, **When** it calls any user endpoint, **Then** it is rejected.
4. **Given** an account has just been confirmed, **When** the owner lists their channels, **Then** the account's own address is there as an active email channel, with no second confirmation (003 Assumptions, "Accounts later").
5. **Given** a new account, **When** it is created, **Then** its notification language is the one chosen at sign-up and its time zone is the operator-configured default (003 FR-018a).

---

### User Story 3 - Recover a forgotten password (Priority: P2)

A student who forgot their password requests a reset link by email and chooses a new password.

**Why this priority**: Without it, a forgotten password means a lost account and a request to the operator.

**Independent Test**: Request a reset for a confirmed account, set a new password through the emailed link, and check that the old password and every other session no longer work and the new password does.

**Acceptance Scenarios**:

1. **Given** any address, **When** someone requests a password reset for it, **Then** the response is the same whether or not an account exists, and a reset email is sent only if one does.
2. **Given** a reset link, **When** its owner sets a valid new password within its validity period, **Then** the password is replaced, the link stops working, all sessions of the account end, and a "your password was changed" email is sent.
3. **Given** a reset link that is expired, already used, or older than a newer reset link for the same account, **When** it is used, **Then** it is rejected and the password is unchanged.
4. **Given** an account with two-factor authentication on, **When** its owner completes a password reset, **Then** they still need a code from their authenticator app or a recovery code to sign in.
5. **Given** an unconfirmed account, **When** its owner completes a password reset through the emailed link, **Then** the account also becomes confirmed, because the link proves control of the address.

---

### User Story 4 - Protect the account with an authenticator app (Priority: P2)

A student turns on two-factor authentication: they add the account to an authenticator app (e.g. Google Authenticator), confirm with a code, and store a set of recovery codes. Signing in then needs the password and a current code.

**Why this priority**: Explicitly requested; it stops sign-in with a leaked or reused password. It is required for admins (US7).

**Independent Test**: Turn on 2FA for an account, sign out, and check that the password alone does not sign in, that the password plus a current code does, that the same code cannot be used twice, and that a recovery code works exactly once.

**Acceptance Scenarios**:

1. **Given** a signed-in account without 2FA, **When** its owner starts setup, **Then** they receive the information an authenticator app needs (a scannable setup link and the key to type in by hand), and 2FA stays off until they submit a valid code from the app.
2. **Given** a valid code is submitted during setup, **When** 2FA is switched on, **Then** the owner receives 10 single-use recovery codes, shown once, and an email confirming that 2FA was turned on.
3. **Given** 2FA is on, **When** the owner signs in with the correct password, **Then** they are not signed in until they also submit a current code from the app or an unused recovery code.
4. **Given** a code that was already accepted once, **When** it is submitted again, **Then** it is rejected.
5. **Given** a recovery code is used to sign in, **When** sign-in completes, **Then** that recovery code can never be used again and the owner gets an email saying a recovery code was used and how many remain.
6. **Given** 2FA is on for a regular account, **When** the owner turns it off, **Then** they must first confirm their password and a current code or recovery code; afterwards all other sessions end and an email is sent. An admin cannot turn off their own 2FA (US7).
7. **Given** 2FA is on, **When** the owner generates new recovery codes (after confirming a current code), **Then** all earlier recovery codes stop working.

---

### User Story 5 - Manage my account (Priority: P2)

A signed-in student changes their password or email address, sees which devices are signed in and signs out one they do not recognise or all of them, and can delete their account with all their data.

**Why this priority**: Needed to keep an account safe after a suspected leak, to move from a personal to a school address, and to leave (Principle II: data never kept longer than needed).

**Independent Test**: Change the password, change the email address and confirm the new one, end a session on another device, sign out everywhere, and delete the account; after each step check sessions, emails sent and stored data.

**Acceptance Scenarios**:

1. **Given** a signed-in account, **When** its owner changes the password (entering the current one), **Then** the new password works, all other sessions end, and a notice is emailed.
2. **Given** a signed-in account, **When** its owner requests an email change (entering the current password), **Then** a confirmation link is sent to the new address, a notice is sent to the old address, and the account keeps the old address until the new one is confirmed.
3. **Given** the new address is confirmed, **When** the change completes, **Then** the account's own email channel (US2-4) moves to the new address; the old address receives nothing more unless the owner adds it again as a separate channel with its own confirmation (003 FR-005).
4. **Given** a signed-in account, **When** its owner chooses "sign out everywhere", **Then** every session of the account ends, the current one included.
5. **Given** a signed-in account, **When** its owner deletes it (entering the password, and a code if 2FA is on), **Then** the account, its sign-in methods, sessions, subscriptions, filters, personal presets, channels and pending messages are deleted, a final "your account was deleted" email is sent, and calendars that lose their last subscriber are removed as 001 specifies.
6. **Given** a signed-in account with sessions on several devices, **When** its owner lists their sessions, **Then** they see for each its device description, when it was created and last used, which one is the current session, and a reference to end it by, and never the session's secret.
7. **Given** a signed-in account, **When** its owner ends one of their other sessions, **Then** requests made with that session are rejected from then on; ending the current session this way is refused with the reason "use sign out", and a session of another account cannot be addressed (FR-029).

---

### User Story 6 - Use several sign-in methods, and add or remove them later (Priority: P2)

Email and password is the first of several ways to sign in. Passkeys, SURFconext (institutional sign-in) and third-party providers such as Google and GitHub are later features. A student can have any mix of them on one account, add one later (e.g. a passkey, or a password on an account that started with SURFconext), and remove one they no longer use, as long as one remains. Each new method type must be addable without reworking accounts, sessions, two-factor authentication or the data that belongs to an account.

**Why this priority**: Explicitly requested ("modular", "add and remove them later"). It costs little now and avoids migrating every account when SURFconext arrives; it mirrors how 003 made channel types pluggable (003 US6).

**Independent Test**: A test-only sign-in method, added in the test suite only, is added to a password account, used to sign in, then the password is removed and re-added; removing the last method is refused. The account, session and data-ownership tests pass unchanged for both methods.

**Acceptance Scenarios**:

1. **Given** a signed-in account, **When** its owner lists their sign-in methods, **Then** they see each method's type, when it was added and when it was last used, but never its secret data.
2. **Given** a signed-in account with a password, **When** its owner adds a second method (after confirming who they are, FR-027a), **Then** both methods sign in to the same account and its data, and a notice is emailed.
3. **Given** an account with two methods, **When** its owner removes one (after confirming who they are), **Then** that method no longer signs in, sessions signed in with it end, and a notice is emailed.
4. **Given** an account without a password, **When** its owner adds one while signed in, **Then** they can sign in with email and password from then on.
5. **Given** an account whose only sign-in method is not a password, **When** its owner uses the app, **Then** everything works as for a password account, except change password, which is refused with a reason.
6. **Given** an account with a single sign-in method, **When** its owner tries to remove that method, **Then** the removal is refused, so an account can never lock itself out.
7. **Given** any mix of methods, **When** the owner changes their email address or deletes the account, **Then** the rules of US5 apply unchanged; the account's email address never depends on which methods it has.

---

### User Story 7 - Admins manage what is shared by everyone (Priority: P2)

Some accounts are admins. They manage global filter presets (002), see the views that used to need the operator key (all calendars and their status in 001, channel type health in 003), promote and demote other admins, find, disable and delete accounts, and help a student who lost both their authenticator app and their recovery codes. The person running the server makes the first admin.

**Why this priority**: Until now "admin" meant whoever held the operator key (002 Assumptions), and this feature retires that key, so the admin role must exist before the key goes.

**Independent Test**: On an empty deployment, make the first admin from the server; check that the admin can create a global preset and list all calendars, that a regular account cannot, that the admin cannot do either until 2FA is on, and that the old operator key no longer opens anything.

**Acceptance Scenarios**:

1. **Given** an admin account with 2FA on, **When** it manages global presets or opens the operator views, **Then** the request succeeds.
2. **Given** a regular account, **When** it calls an admin endpoint, **Then** the request is rejected.
3. **Given** an admin account without 2FA, **When** it calls an admin endpoint, **Then** the request is rejected with the reason "two-factor authentication required".
4. **Given** a student who lost their authenticator app and all recovery codes, **When** an admin removes 2FA from that account, **Then** 2FA is off, all sessions of that account end, the owner is emailed, and the action is logged with the admin's account.
5. **Given** a new deployment with no accounts, **When** the person running the server makes an address admin from the server, **Then** that address receives an admin invite (or, if it already has a confirmed account, that account becomes admin); either way its admin access stays locked until 2FA is on (scenario 3).
6. **Given** an admin, **When** they promote a regular account that has 2FA on to admin or demote another admin, **Then** the role changes, the affected account is emailed, and the action is logged with the acting admin's account.
7. **Given** only one admin is left, **When** they try to demote themselves or delete their account, **Then** it is refused with a reason.
8. **Given** a request carrying the old operator key, **When** it calls any endpoint, **Then** the key grants nothing.
9. **Given** a regular account without 2FA, **When** an admin tries to promote it, **Then** the promotion is refused with the reason "two-factor authentication required".
10. **Given** an admin, **When** they try to turn off their own 2FA, **Then** it is refused with the reason "demote first".
11. **Given** an admin signed in on a device before turning on 2FA, **When** they turn it on in that session, **Then** that session counts as having proven a second factor; their other sessions opened with the password alone do not, and are refused on admin endpoints.
12. **Given** an admin, **When** they list or search accounts, **Then** they see each account's address, role, whether it is confirmed, whether 2FA is on and when it was created, and never any sign-in method's secret data.
13. **Given** an admin, **When** they disable a regular account, **Then** all its sessions end, it cannot sign in until an admin re-enables it, its owner is emailed, and the action is logged with the admin's account; re-enabling emails the owner and is logged the same way.
14. **Given** an admin, **When** they delete a regular account, **Then** it is deleted exactly as in US5-5, its owner gets the final email, and the action is logged with the admin's account.
15. **Given** an admin, **When** they try to disable or delete another admin or their own account through these actions, **Then** it is refused with the reason "demote first" (their own account is deleted through US5).

---

### User Story 8 - Admins decide who may sign up (Priority: P2)

An admin chooses whether anyone may sign up, lists the email domains whose addresses may always sign up (e.g. the school's), and invites people: one named person by email, or a group through a shareable link that allows a chosen number of sign-ups. Invites work whatever the other settings are.

**Why this priority**: A school deployment must be able to limit accounts to its own students and still let in an invited guest; a self-hoster wants to open or close sign-up without editing server configuration.

**Independent Test**: With open sign-up off and `inholland.nl` allowed, check that an `inholland.nl` address can sign up, a `gmail.com` address cannot, and the same `gmail.com` address can sign up through an invite; turn open sign-up on and check that the `gmail.com` address can now sign up without one.

**Acceptance Scenarios**:

1. **Given** open sign-up is on, **When** anyone signs up, **Then** sign-up proceeds as in US1, whatever the domain.
2. **Given** open sign-up is off and `inholland.nl` is an allowed domain, **When** someone signs up with an `inholland.nl` address, **Then** sign-up proceeds as in US1.
3. **Given** open sign-up is off, **When** someone signs up without an invite with an address outside every allowed domain, **Then** it is refused with the reason "sign-up is by invitation for this address", whether or not an account exists for it.
4. **Given** open sign-up is off and no domain is allowed, **When** anyone signs up without an invite, **Then** it is refused the same way (invite-only).
5. **Given** an admin invites a named address, **When** the invite is created, **Then** an invite email in the language the admin chose is sent, with a link valid for 7 days (or the validity the admin chose).
6. **Given** an invite for a named address, **When** its recipient opens it and chooses a password, **Then** an account for that address is created and confirmed at once (only that mailbox got the link), whatever the sign-up settings, and the invite cannot be used again.
7. **Given** an admin creates a shareable invite allowing 30 sign-ups, **When** 30 people have signed up through it, **Then** the 31st is refused with "invite no longer valid"; each of the 30 signed up with their own address and confirmed it as in US1.
8. **Given** a shareable invite, **When** an admin tries to make it grant admin, **Then** it is refused: only an invite for one named address can grant admin.
9. **Given** a pending invite, **When** an admin revokes it, **Then** its link stops working at once; admins can list invites with their sign-ups used and left, and resend an invite for a named address.
10. **Given** an admin changes the sign-up settings, **When** the change is saved, **Then** existing accounts and pending invites are unaffected, and the change is logged with the admin's account.

---

### User Story 9 - Keep bots out of sign-in and sign-up (Priority: P3)

The operator turns on a bot check (Cloudflare Turnstile, or Google reCAPTCHA) so that automated scripts cannot mass-create accounts, guess passwords at scale, or use the reset and confirmation emails to flood inboxes. People see the provider's check (often invisible) on the pages that need it; the server verifies each result with the provider before acting.

**Why this priority**: Throttling (FR-012, FR-012a) already limits abuse per account and per address, so the app is safe without it; a bot check adds protection against spread-out attacks from many addresses, which matters once sign-up is open to a whole school or the public.

**Independent Test**: Configure a test-only bot check provider; check that every protected action is refused without a valid result, accepted with one, and that a result cannot be used twice. Repeat the provider-specific verification with Turnstile's and reCAPTCHA's published test keys.

**Acceptance Scenarios**:

1. **Given** no provider is configured, **When** anyone signs in or signs up, **Then** no bot check is asked for and everything works as in US1.
2. **Given** Turnstile is configured, **When** a request for a protected action (FR-037) arrives without a bot check result, with an invalid or expired one, or with one already used, **Then** it is refused with the reason "bot check failed", before the address, password or invite is looked at.
3. **Given** a provider is configured, **When** the frontend asks which bot check to show, **Then** the API tells it the provider and its public site key, never the secret key.
4. **Given** Turnstile is configured, **When** the operator switches to reCAPTCHA and restarts, **Then** the protected actions require a reCAPTCHA result and no other behaviour changes.
5. **Given** the provider cannot be reached, **When** someone signs in, **Then** sign-in proceeds without the check (throttling still applies) and the outage is logged; **When** someone tries another protected action, **Then** it is refused with "try again later".

---

### Edge Cases

- **Sign-up with an address of an unconfirmed account**: the unconfirmed account's password is replaced by the new one, earlier confirmation links stop working, and a new one is sent. Someone who does not control the address can therefore not keep it reserved, and because confirming needs both the link and the password (FR-003), they cannot end up with a confirmed account on someone else's address either. An owner whose confirmation fails because someone else set the password completes a password reset instead (FR-009).
- **Unconfirmed accounts** are deleted automatically 30 days after sign-up, with their address (as 003 does for unconfirmed channels).
- **Address case**: `Student@Example.nl` and `student@example.nl` are the same account; the address is stored as entered and compared without regard to case.
- **Guessing passwords or codes**: after 5 failed attempts for one account from one network address, further attempts for that account from that address are refused for an increasing waiting time, up to 15 minutes; the owner can still sign in from another address. Only after 20 failures for the account within an hour, from any mix of addresses, do attempts from every address wait, again up to 15 minutes. A student sharing a campus address with the person guessing waits with them. Each source (network address) has its own, much higher, operator-configurable limit across all accounts, because many students share one campus address and their typos must not block each other. The account is never locked permanently, and a completed password reset clears the waiting time. Codes (2FA and recovery) are limited the same way.
- **Clock drift**: a code from the period just before or after the current one is accepted, so a phone clock a little off still works.
- **Two open sign-ins**: signing in on a second device does not end the first session.
- **Stale sessions**: a session ends after 30 days without use (operator-configurable), and immediately when the password is changed or reset, 2FA is turned off, "sign out everywhere" is used, or the account is deleted.
- **Email change to an address already used by another account**: the request looks successful, nothing changes, and the other account's owner is not told who asked.
- **Email change that is never confirmed**: the confirmation link expires after 24 hours, and the account keeps its old address.
- **Deleting an account that shares calendars**: only this account's subscriptions go; other subscribers of the same calendar keep it and its history (001 FR-009).
- **Last admin**: the last admin cannot demote themselves or delete their account. If the last admin is lost anyway (e.g. lost mailbox and 2FA), the person running the server makes a new one from the server.
- **Allowed domains** match exactly: `inholland.nl` does not include `student.inholland.nl`; an admin lists each domain that should be allowed. Domains are compared without regard to case.
- **Invite for an address that already has a confirmed account**: the admin is told and no invite is sent. An invite for an address with an unconfirmed account replaces that account when used.
- **Shareable invite and existing addresses**: a sign-up through a shareable invite with an address that already has an account looks like any other sign-up (FR-006) and uses up one sign-up, so the invite's counter reveals nothing about which addresses have accounts.
- **Shareable invite that leaks**: at worst it lets in as many people as it has sign-ups left, as regular users only; the admin can revoke it at once.
- **Removing a method while signed in with it**: allowed if another method remains; the current session ends with it and the owner signs in with a remaining method.
- **Removed password, forgotten other method**: a password reset through the email address adds a password again (FR-009), so the email address stays the way back into any account; 2FA still applies.
- **Email change after sign-up** is not limited by the sign-up settings: they decide who may create an account, not which address an existing member uses.
- **Mail server down during sign-up or reset**: the account or reset is still recorded, the email is retried like 003's messages, and the user can request a new email later.
- **Bot check failure and enumeration**: a failed bot check is decided before the address is looked at, so it gets the same response for every address (FR-006).
- **A bot check result used twice** (e.g. replayed) is refused, even within its validity.
- **Disabled accounts**: signing in with the correct password is refused with the reason "account disabled"; a wrong password gets the usual generic response. Sign-up, reset and confirmation requests for its address get the same response as for any address (FR-006) but send nothing and change nothing, and no notifications are sent to its channels while it is disabled. Its data is kept until an admin re-enables or deletes it; its owner cannot delete it, because that needs signing in.
- **Links opened by mail scanners**: confirmation and reset links only take effect after an explicit action on the linked page, never when merely fetched (as 003 FR-007).

## Requirements *(mandatory)*

### Functional Requirements

**Accounts and sign-up**

- **FR-001**: A person MUST be able to sign up with an email address, a password and a language (English or Dutch) when the sign-up settings allow their address (FR-001a) or they use an invite (FR-001c).
- **FR-001a**: The sign-up settings MUST consist of open sign-up (on or off) and a list of allowed email domains, both managed by admins. With open sign-up on, every address is allowed. With it off, only addresses whose domain is on the list are allowed; domains match exactly and without regard to case. A new deployment MUST start with open sign-up off and no domains (invite-only).
- **FR-001b**: A sign-up refused by the sign-up settings MUST be refused with the reason "sign-up is by invitation for this address", decided before and independent of whether an account exists for the address (FR-006).
- **FR-001c**: An admin MUST be able to create an invite, choosing its validity (1 to 90 days, default 7) and one of two kinds. Every invite lets its users sign up whatever the sign-up settings, and only takes effect after an explicit action on the linked page.
  - **For a named address**: allows exactly 1 sign-up, is emailed to that address in the language the admin chooses, may make the account an admin, and creates an account that is confirmed at once. Inviting an address that already has a confirmed account MUST be refused with a reason.
  - **Shareable**: allows the number of sign-ups the admin chooses (1 to 1,000, default 1) and is shown to the admin as a link to share. Each person signs up with their own address and confirms it as in FR-003. It MUST NOT grant admin. Every sign-up submitted through it uses up one, whether or not a new account results (FR-006); once all are used, the link is refused with "invite no longer valid".
- **FR-001e**: Admins MUST be able to list invites (kind, sign-ups used and left, expiry, creating admin), revoke any invite with immediate effect, and resend an invite for a named address. The link of a shareable invite MUST be shown only when it is created.
- **FR-001d**: Changing the sign-up settings MUST NOT affect existing accounts or pending invites.
- **FR-002**: Email addresses MUST be syntactically valid, at most 254 characters, and unique across accounts without regard to case.
- **FR-003**: A new account MUST be unconfirmed and receive a confirmation email, except one created through an invite for a named address (FR-001c). An unconfirmed account MUST NOT be able to sign in or use any endpoint except requesting a new confirmation email. Confirmation links MUST expire after 24 hours, MUST be single-use, and MUST only take effect after an explicit action on the linked page that includes the account's password, so only someone with both the mailbox and the password can confirm.
- **FR-004**: A new confirmation email MUST be available on request (except for a disabled account, FR-033c), at most 3 per account per 24 hours; each new one invalidates the earlier links.
- **FR-005**: Unconfirmed accounts MUST be deleted automatically, with their address, 30 days after sign-up.
- **FR-006**: Sign-up, password reset requests and sign-in failures MUST return the same response whether or not an account exists for the address (no account enumeration). For a disabled account the responses are the same, but nothing is sent or changed (FR-033c). Sign-up with an address that has a confirmed account MUST send its owner a "someone tried to sign up with your address" email and change nothing else; sign-up with an address that has an unconfirmed account MUST replace that account's password and send a new confirmation link.

**Passwords**

- **FR-007**: Passwords MUST be 12 to 128 characters, any characters allowed (including spaces), with no rules requiring character types. Passwords on a bundled list of commonly used passwords, or equal to the account's email address, MUST be rejected with a reason.
- **FR-008**: Passwords MUST be stored so that nobody, including the operator, can recover them.
- **FR-009**: A password reset MUST be requestable by address (for a disabled account nothing is sent, FR-033c). The reset link MUST expire after 1 hour, be single-use, be invalidated by a newer reset link, and only take effect after an explicit action on the linked page. Completing a reset MUST end every session of the account, confirm an unconfirmed account, and send a notice email. For an account without a password, completing a reset adds one as a sign-in method.
- **FR-010**: A password reset MUST NOT turn off or bypass 2FA.

**Sign-in and sessions**

- **FR-011**: A confirmed account that is not disabled (FR-033c) MUST be able to sign in with its email address and password, and, when 2FA is on, a current authenticator code or an unused recovery code.
- **FR-012**: Failed sign-in and code attempts MUST be throttled per account and network address: after 5 failures for an account from one address, further attempts for that account from that address are refused for an increasing waiting time up to 15 minutes, while other addresses are unaffected. Each account MUST also have a limit across all addresses of 20 failures per hour, after which attempts from every address are refused for up to 15 minutes. Failed attempts MUST also be throttled per source (network address) across accounts, with a separate, much higher, operator-configurable threshold so one campus address shared by many students is not blocked by a few typos. Accounts MUST never be locked permanently by failed attempts.
- **FR-012a**: Account emails triggered without signing in MUST be capped: at most 3 password reset emails and at most 3 "someone tried to sign up with your address" notices per address per 24 hours, and sign-up, reset and invite-acceptance requests MUST be throttled per source. Requests over a cap MUST get the same response as others (FR-006), without sending.
- **FR-013**: A session MUST end on sign-out, when its owner ends it from another session (FR-014a), after 30 days without use (operator-configurable), and for all sessions of the account when the password is changed or reset, 2FA is turned off, "sign out everywhere" is used, or the account is deleted. Changing the password or turning off 2FA MUST keep the session that made the change.
- **FR-014**: A signed-in user MUST be able to sign out of the current session and to sign out everywhere.
- **FR-014a**: A signed-in user MUST be able to list their own sessions (device description, created at, last used at, which one is current, and a reference that is not a secret and cannot be used to sign in; never the session's secret) and end any of them by that reference except the current one, which they end by signing out.

**Two-factor authentication**

- **FR-015**: A signed-in user MUST be able to turn on 2FA with a standard time-based authenticator app. Setup MUST provide a scannable setup link and a manually typeable key, and 2FA MUST NOT be on until a valid code from the app has been submitted.
- **FR-016**: Turning 2FA on MUST produce 10 single-use recovery codes, shown once. Generating new recovery codes MUST require a current code and MUST invalidate all earlier ones.
- **FR-017**: An authenticator code MUST be accepted at most once. Codes from one period before or after the current one MUST be accepted to tolerate clock drift.
- **FR-018**: Turning 2FA off MUST require confirming who they are (FR-027a), including a current code or recovery code.
- **FR-019**: An admin MUST be able to turn off 2FA for another account (for an owner who lost both the app and the recovery codes). This MUST end all sessions of that account, email its owner, and be logged with the acting admin's account.
- **FR-020**: Turning 2FA on or off, using a recovery code, and changing or resetting the password MUST each send a notice email to the account's address.

**Account management**

- **FR-021**: A signed-in user MUST be able to change their password by entering the current one.
- **FR-022**: A signed-in user MUST be able to change their email address after confirming who they are (FR-027a). The new address MUST be confirmed (link valid 24 hours, explicit action) before it replaces the old one; the old address MUST be notified when the change is requested. A request for an address already used by another account MUST look successful and change nothing.
- **FR-023**: When an email change completes, the account's own email channel (FR-028) MUST move to the new address; other channels stay as they are.
- **FR-024**: A signed-in user MUST be able to delete their account after confirming who they are (FR-027a), which includes a current code or recovery code when 2FA is on. Deletion MUST remove the account and everything that belongs to it (sign-in methods, sessions, subscriptions, filters, personal presets, channels, pending messages, 003's "this wasn't me" blocks). Calendars left without subscribers MUST be removed as 001 specifies. A final notice email MUST be sent to the account's address.
- **FR-025**: All account emails (confirmation, reset, notices) MUST be in the account's language (English or Dutch), and MUST be sent through the mail server and sending limits of 003.

**Sign-in methods**

- **FR-026**: An account MUST be able to have one or more sign-in methods; email and password is the only type offered in this feature. Passkeys, SURFconext, Google and GitHub are later types (out of scope) and MUST fit the same model: adding a type MUST NOT change accounts, sessions, 2FA, or other types' data. This MUST be verified with a test-only sign-in method (US6).
- **FR-027**: An account MUST be able to have any mix of sign-in methods, including none that is a password. Its email address MUST be an attribute of the account rather than what identifies it, so a later method can sign in to an account independently of the address. Every account MUST keep exactly one confirmed email address whatever its methods; a later method that does not supply one MUST ask for it at sign-up.
- **FR-027a**: A signed-in user MUST be able to list their sign-in methods (type, added at, last used, never secret data), add a method, and remove one. Adding or removing a method MUST first require confirming who they are with an existing method, plus a current code or recovery code when 2FA is on. A confirmation MUST stay valid for 10 minutes in the session that made it, and MUST NOT carry over to other sessions. Removing the last remaining method MUST be refused. Removing a method MUST end the sessions signed in with it. Every addition and removal MUST send a notice email and be logged (FR-035).

**Ownership of existing data**

- **FR-028**: When an account is confirmed, its address MUST become an active email channel of that account without a second confirmation (003 Assumptions). It counts toward 003's limit of 5 channels.
- **FR-029**: Every user endpoint of 001, 002 and 003 that identifies a subscriber by an id in the request MUST instead act for the signed-in account, and MUST NOT offer any way to address another account's data. A request for an item that exists but belongs to another account MUST get the same response as a request for an item that does not exist.
- **FR-030**: The login-free pages of 003 (confirm a channel, "this wasn't me", the mail client's unsubscribe) MUST keep working without login.
- **FR-031**: A new account's notification language MUST be the language chosen at sign-up and its time zone the operator-configured default (003 FR-018a); both remain changeable through the API.

**Admins and the operator**

- **FR-032**: An account MUST be either a regular user or an admin; there is no other elevated role. Admin endpoints (global presets of 002, every endpoint that required the operator key in 001 to 003, the sign-up settings and invites of FR-001a to FR-001e, role changes of FR-033a, account management of FR-033c, and FR-019) MUST be rejected for regular users, and for admins whose session has not proven a second factor.
- **FR-032a**: "Admins must have 2FA" MUST be enforced at three points: (1) every admin request needs a session that proved a second factor, either at sign-in or by turning on 2FA within that session; (2) promoting an account to admin (FR-033a) MUST be refused while it has 2FA off; (3) an admin MUST NOT be able to turn off their own 2FA (demote first). Admins created from the server or through an admin invite (FR-033b, FR-001c) start without 2FA; their admin endpoints stay locked until they turn it on, and responses tell them why. When an admin turns off another admin's 2FA (FR-019), that admin's admin endpoints lock the same way until they set it up again.
- **FR-033**: The operator key of 001 FR-031 MUST be retired: no endpoint accepts it any more.
- **FR-033a**: An admin MUST be able to promote a confirmed account that has 2FA on to admin and demote another admin. The affected account MUST be emailed. The last remaining admin MUST NOT be able to demote themselves or delete their account.
- **FR-033c**: An admin MUST be able to list and search all accounts by address, showing address, role, confirmed or not, 2FA on or off and created at, never sign-in method secrets. An admin MUST be able to disable, re-enable and delete a regular account; admin accounts MUST first be demoted. Disabling MUST end all the account's sessions and block every sign-in, account email not requested by an admin, and notification to its channels until it is re-enabled; its data is kept. Deleting MUST remove the account as in FR-024. Each action MUST email the owner and be logged with the acting admin's account.
- **FR-033b**: Someone with access to the server MUST be able to make an address admin without signing in to the app: an existing confirmed account with that address becomes admin, otherwise (no account, or an unconfirmed one) an admin invite (FR-001c) is sent to it. This is how the first admin is created and how a lost last admin is replaced; it MUST be logged.

**Bot checks**

- **FR-037**: When the operator has configured a bot check provider, these actions MUST require a valid, unused bot check result, verified server-side with the provider before anything else about the request is evaluated: sign-in (password step), sign-up, sign-up through a shareable invite, password reset request, and new confirmation email request. With no provider configured, no bot check is required. Actions that need a signed-in session, the 2FA code step, and links that were emailed to one address (confirmation, reset, invite for a named address) MUST NOT require one.
- **FR-038**: Cloudflare Turnstile and Google reCAPTCHA MUST be supported. Everything except showing the check and verifying its result MUST be the same for every provider, verified by running the bot check tests against a test-only provider (as FR-026 does for sign-in methods). Adding a provider later MUST NOT change the protected actions or their rules.
- **FR-039**: The provider, its public site key and its secret key MUST be server configuration; the secret key is an operator secret. The API MUST tell the frontend which provider is active and its public site key.
- **FR-040**: If the provider cannot be reached, sign-in MUST proceed without the check (throttling still applies), and the other protected actions MUST be refused with "try again later". Failed verifications and provider outages MUST be logged, without the bot check result itself.

**Privacy and logging**

- **FR-034**: Passwords, authenticator keys, codes, recovery codes, session secrets, and confirmation and reset links MUST never be logged or returned by the API, except the authenticator key and recovery codes once, at setup. Full email addresses MUST NOT be logged (as 003 SC-010).
- **FR-035**: Security events (sign-in success and failure, throttling, 2FA on/off, recovery code use, password and email changes, admin actions, account deletion) MUST be logged with the account id, time and source (network address, needed to investigate and throttle abuse), and without the items of FR-034. The security log MUST NOT be offered through the API, to admins or anyone else; only whoever runs the server can read it. Security events MUST be kept for 90 days and then deleted, also for deleted accounts.
- **FR-036**: The API MUST offer every action of this feature (sign-up, invites, sign-up settings, confirmation, sign-in with and without 2FA, reset, 2FA setup, changing the password or adding one to an account without, changing the email address, managing sign-in methods, listing and ending sessions, signing out everywhere, account deletion, admin roles, listing, disabling and deleting accounts) so the later frontend can offer each of them to signed-in users. Links in account emails MUST point to pages at the operator-configured frontend address (as 003 FR-009).

### Key Entities *(include if feature involves data)*

- **Account**: one person using the app. Holds the email address (needed to confirm it, sign in, reset the password and send notifications), whether it is confirmed, whether an admin has disabled it, its role (user or admin), its language and time zone (moved from 003's per-subscriber settings), and when it was created (needed to delete unconfirmed accounts after 30 days). No name, phone number or other personal data is collected. It replaces 001's subscriber id as the owner of subscriptions, filters, presets and channels.
- **Sign-in method**: one way to sign in to one account: a type (only email and password for now) plus that type's own data (for a password: a one-way hash; for later external providers: the provider's stable identifier for the person), when it was added and when it was last used. An account has one or more.
- **Session**: one signed-in device or browser of one account: created at, last used at, the sign-in method it was opened with, whether it has proven a second factor, and a short device description derived from the browser (e.g. "Firefox on Windows", needed so the owner can recognise it, FR-014a); no network address is stored with it. Ends as described in FR-013.
- **Two-factor setting**: per account: the authenticator key (a secret, needed to check codes), whether 2FA is on, the last accepted code period (to reject reuse), and the recovery codes, each stored one-way and marked used once used.
- **Account email token**: a single-use, expiring link for confirming an account, confirming a new address, or resetting a password; stored one-way, with its purpose, account and expiry. A pending email change also holds the new address until it is confirmed or expires.
- **Invite**: an admin's invitation: its kind (named address or shareable), the address for a named invite (needed to send it and create the account), language, whether it grants admin (named only), sign-ups allowed and used, the inviting admin, and expiry; its link is stored one-way. Deleted once used or revoked, or 30 days after expiry.
- **Sign-up settings**: one per deployment: open sign-up on or off, and the list of allowed email domains.
- **Security event**: an entry in the security log of FR-035: event type, account id, time and source network address; kept 90 days.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A new student can sign up, confirm their address and be signed in within 3 minutes, not counting email delivery time.
- **SC-002**: An automated test covering every user endpoint of 001 to 003 shows that 100% of attempts by account A to read or change account B's calendars, changes, presets, channels or settings are rejected, with the same response as for a non-existent item.
- **SC-003**: For sign-up, password reset requests and sign-in failures, the response for an address with an account is identical to the response for an address without one.
- **SC-004**: With 2FA on, 0 sign-ins succeed with the password alone, including right after a password reset.
- **SC-005**: No more than 23 wrong passwords or codes per account per rolling hour are evaluated (20, then one per 15-minute wait); failed attempts from one network address never stop the owner from signing in from another address, and no account is kept from signing in from every address for more than 15 minutes at a time.
- **SC-006**: The account, session and data-ownership test suites pass unchanged for both email and password and a test-only sign-in method.
- **SC-007**: 0 log lines or API responses contain a password, an authenticator key or code outside its one-time setup response, a recovery code, a session secret, a confirmation or reset link, or a full email address.
- **SC-008**: After an account is deleted, 0 records of that account's personal data remain, except its security events, which are deleted when their 90 days are up (FR-035).
- **SC-009**: After a password change or reset, the next request made with any other session of that account is rejected.
- **SC-010**: For both English and Dutch, 100% of the fixed text in every account email is in the account's language.
- **SC-011**: At the start of a semester, 500 sign-ins per minute are handled with each sign-in completing within 2 seconds.
- **SC-012**: For every combination of open sign-up (on, off) and allowed domains (none, one matching, one not matching), with and without an invite, sign-up is allowed exactly when open sign-up is on, the domain is allowed, or a valid invite is used; a shareable invite allowing N sign-ups accepts exactly N.
- **SC-013**: 0 endpoints accept the retired operator key, and 0 admin endpoints succeed for a regular account or for a session that has not proven a second factor.
- **SC-014**: With the test-only sign-in method, every sequence of adding and removing methods leaves each account with at least one method, and 0 sessions survive the removal of the method they were signed in with.
- **SC-015**: With a provider configured, 100% of requests for protected actions without a valid, unused bot check result are refused, and 0 unsigned-in actions outside FR-037 ask for one.

## Assumptions

- **Operator** now means whoever runs the server, not a role in the app. Settings that the earlier specs call operator-configured (mail server, frontend address, check intervals, host budgets, MyTimetables domains, session lifetime) stay in server configuration; moving any of them into the admin pages is a later decision.
- **No data to migrate**: no deployment with real subscriber data exists yet, so existing subscriber ids are not carried over to accounts.
- **2FA**: optional for regular users, required for admins before they can use admin endpoints (FR-032a). How 2FA combines with later sign-in methods (e.g. whether SURFconext or a passkey already counts as two factors) is decided by those features.
- **Later sign-in methods**: linking an external identity to an existing account (e.g. by matching email addresses) carries a takeover risk and is decided by the feature that adds the method; this feature only makes sure the account model does not need to change for it (FR-026, FR-027).
- **Frontend later**: there is no frontend yet, so sign-up, 2FA setup (including showing the scannable code), resets and account management are API-only in this feature, and links in account emails point to pages at the operator-configured frontend address (as 003). The later frontend builds the pages on top of this API.
- **Password rules** (FR-007) follow current guidance: length over composition, and a local list of common passwords rather than an external breach service, which would send data about users' passwords to a third party.
- **Throttling instead of lockout** (FR-012), because a hard lockout lets anyone lock a student out by guessing wrong on purpose. For the same reason the waiting time applies per network address, and only the higher limit across addresses makes the whole account wait; the remaining risks (someone guessing from the student's own campus address, or from a handful of addresses that together keep the account over its hourly limit) are accepted, and the bot check (US9) reduces it.
- **Emails** reuse 003's mail server configuration, sending limits and retries; security notices are sent even when all of the account's notification channels are paused or unsubscribed, because they are about the account, not about schedule changes.
- **Bot check privacy**: a bot check sends the visitor's network address and browser signals to the provider, a third party. It is therefore off by default and the operator's choice (Principle II); the operator is responsible for mentioning it in the privacy notice. Turnstile is listed first because it asks less of visitors and collects less than reCAPTCHA. Providers' own accessibility options (e.g. audio or invisible checks) are relied on; the app adds no check of its own.
- **Out of scope**: passkeys, SURFconext and other OpenID Connect or OAuth providers (Google, GitHub), the frontend, other 2FA types (SMS, email codes, hardware keys), naming individual sessions, reading the security log through the API, "remember this device" for 2FA, account recovery without email access, regular users inviting others, bot check providers other than Turnstile and reCAPTCHA (e.g. hCaptcha; they fit FR-038), turning the bot check on per action or from the admin pages, and languages other than English and Dutch.
- **Limits** (link validity, throttling, session lifetime, recovery code count) are sensible defaults for a school-wide deployment and can be tuned later without changing behaviour.
