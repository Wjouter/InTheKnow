# Feature Specification: Check the password-reset link before the new password is typed

**Feature Branch**: `009-reset-link-precheck`

**Created**: 2026-10-21

**Status**: Draft

**Input**: User description: "Email: check password-reset link validity before the user types a new password"
(InTheKnow roadmap, [Wjouter/InTheKnow#17](https://github.com/Wjouter/InTheKnow/issues/17)): "Validate the reset
token when the page opens, so the user sees an expired/used link error up front instead of after typing a new
password and then getting the message that the link does not work."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A dead link says so straight away (Priority: P1)

A student forgot their password, asked for a reset email last week, and only now clicks the link. The link has
expired (or was already used). Today the page asks for a new password, the student thinks one up, types it, presses
the button, and only then learns the link does not work. With this feature the page tells them at once that the
link no longer works and offers to send a new one, before they type anything.

**Why this priority**: this is the whole issue. It saves the user a wasted step and the confusion of a password
that was "accepted" by the form but never saved.

**Independent Test**: request a reset, use the link once (or let it expire), open the same link again, and check
that the page shows the "link does not work" message and the "request a new link" link, with no password field.

**Acceptance Scenarios**:

1. **Given** a reset link that has expired, **When** it is opened, **Then** the page shows the "this link does not
   work" message and a link to request a new reset email, and no password field.
2. **Given** a reset link that was already used to set a password, **When** it is opened again, **Then** the same
   message is shown.
3. **Given** a reset link that was replaced or made useless for any other reason (unknown link, account disabled),
   **When** it is opened, **Then** the same message is shown.
4. **Given** a valid, unused reset link, **When** it is opened, **Then** the page shows the new-password form as
   it does today.
5. **Given** the page reached without any link code at all, **When** it is opened, **Then** the same message is
   shown, as today.

---

### User Story 2 - A valid link still works after the check (Priority: P1)

A student opens a fresh reset link. Opening the page checks it but does not use it up: they can type a new password
and set it, exactly as today.

**Why this priority**: the check must never break the normal path. Mail systems of schools and companies open links
in emails automatically to scan them, so opening the page must not count as using the link.

**Independent Test**: request a reset, open the link (several times, as a scanner would), then set a new password
with it and sign in with that password.

**Acceptance Scenarios**:

1. **Given** a valid reset link, **When** it is opened one or more times, **Then** it stays valid and the password
   can still be set with it.
2. **Given** a valid link whose page is open, **When** the link expires or is used elsewhere before the form is sent,
   **Then** sending the form shows the "this link does not work" message, as today, and the password is not
   changed.

---

### Edge Cases

- **Link scanners**: opening the page (by a person or a scanner) only reads whether the link is still good. It never
  uses the link, changes the password, ends sessions or sends an email.
- **Expiry while the page is open**: the check at opening is a convenience. The check when the form is sent stays
  the final word (User Story 2, scenario 2).
- **No account details shown**: the page says only whether the link works. It does not show the email address or
  any other account detail, for a good or a bad link, so a leaked link reveals no more than it does today.
- **Guessing links**: checking a link reveals no more than sending the form with it already does, and link codes
  are too long to guess. The existing limits on repeated attempts keep applying.
- **Service down**: if the check cannot be done (temporary server problem), the page shows the normal "something
  went wrong, try again" error rather than a form that may not work.
- **Language**: the message follows the visitor's current language (English or Dutch), as the rest of the page does.
- **Other emailed links** (invite, email confirmation, email change): out of scope. They can get the same treatment
  later if wanted.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: When the password-reset page is opened with a link code, the system MUST check whether that code can
  still be used to set a new password, before showing the form.
- **FR-002**: A code counts as usable under exactly the same rules the system already uses when the form is sent:
  it exists, belongs to a password reset, is not used, has not expired, and its account is not disabled.
- **FR-003**: If the code is not usable, the page MUST show the existing "this link does not work" message and the
  link to request a new reset email, and MUST NOT show the password field.
- **FR-004**: If the code is usable, the page MUST show the new-password form as it does today.
- **FR-005**: The check MUST NOT use up, change or extend the code, and MUST have no other effect (no password
  change, no ended sessions, no email, no security-log entry).
- **FR-006**: Sending the form MUST still check the code again and refuse an unusable one, exactly as today.
- **FR-007**: The check MUST answer only "usable" or "not usable"; it MUST NOT say why a code is not usable and MUST
  NOT return any account detail.
- **FR-008**: If the check fails for a reason other than an unusable code, the page MUST show the general error
  instead of the form.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In 100% of tests with an expired, used, unknown or missing link code, the user sees the "this link does
  not work" message without being asked for a password.
- **SC-002**: In 100% of tests with a valid code, the password can be set after the page was opened any number of
  times.
- **SC-003**: The reset page with a bad link appears as fast as it does today with a good one (no noticeable extra
  wait).
- **SC-004**: Opening the page produces zero changes to the account, its sessions, its emails or the security log.

## Assumptions

- The existing "this link does not work" message and "request a new link" link are reused; no new wording is needed.
- The same message is used for every unusable reason (expired, used, unknown, disabled account), as today; telling
  them apart is not asked for and would tell a link holder more about the account.
- Reset links keep their current lifetime and single use; this feature does not change how links are made or sent.
- Only the password-reset page is in scope (see Edge Cases for the other emailed links).
