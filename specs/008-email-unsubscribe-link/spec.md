# Feature Specification: Unsubscribe link in the email footer

**Feature Branch**: `008-email-unsubscribe-link`

**Created**: 2026-10-21

**Status**: Draft

**Input**: User description: "Email: real unsubscribe link in the footer" (InTheKnow roadmap): "Add an actual
unsubscribe link/button at the bottom of notification emails, instead of only the text 'Use your mail client's
unsubscribe option to stop these notifications to this address.' So you can unsubscribe at the bottom of the e-mail
or with your clients button."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Unsubscribe from the bottom of a change email (Priority: P1)

A student gets timetable change emails they no longer want at this address. Their mail app shows no unsubscribe
button (many don't, or hide it), so they scroll to the bottom of the email, click "Unsubscribe", land on the
existing one-button unsubscribe page, press the button, and get no further emails at that address.

**Why this priority**: today the footer only tells them to find their mail client's option. When that option is
missing, the only way out is logging in or marking the mail as spam, which hurts delivery for everyone.

**Independent Test**: send a change email to an active channel, open it, follow the footer link, press the button
on the page, then record new changes and verify nothing more is sent to that channel.

**Acceptance Scenarios**:

1. **Given** a change email, **When** it is opened in either the HTML or the plain-text view, **Then** the footer
   contains a visible unsubscribe link in the recipient's language (English or Dutch).
2. **Given** the footer link, **When** it is opened, **Then** the existing unsubscribe page is shown, and the
   channel is still active.
3. **Given** the unsubscribe page reached from the link, **When** the button is pressed, **Then** the channel
   becomes *unsubscribed*, the page confirms it, and no further emails are sent to that channel.
4. **Given** a change email, **When** the mail client's own unsubscribe button is used instead, **Then** it still
   works exactly as before.

---

### User Story 2 - The same link in the test email (Priority: P2)

A subscriber sends themselves a test notification. The test email already carries the mail client's unsubscribe
option. It now also gets the same footer with the unsubscribe link, so every email that can be unsubscribed from
says how at the bottom.

**Why this priority**: test emails are rare and are requested by the subscriber themself, so they matter less than
change emails. Treating them the same keeps the rule simple.

**Independent Test**: send a test email, check that its footer has the link, and that the link leads to the
unsubscribe page for that channel.

**Acceptance Scenarios**:

1. **Given** a test email, **When** it is opened, **Then** it shows a footer with the unsubscribe link, in both
   the HTML and the plain-text view.

---

### Edge Cases

- **Link scanners**: school and company mail systems open links in emails automatically to check them. Opening the
  link alone MUST NOT unsubscribe anyone; only pressing the button on the page does.
- **Already unsubscribed**: opening the link and pressing the button again shows the normal "done" result, with no
  error and no extra effect.
- **Channel removed or deleted** (by the subscriber, or automatically after 30 days unsubscribed): the link shows
  the existing "not found" page.
- **Forwarded email**: anyone who has the email can unsubscribe that one channel. This is the same exposure the mail
  client's unsubscribe option already has, and it is accepted. It never affects other channels or the account.
- **Language changed after sending**: the page follows the subscriber's current language, as it does today.
- **Confirmation and account emails**: they get no unsubscribe link, as before. Account emails are about the
  account, not optional news, and a confirmation email already has its own "this wasn't me" link.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Every change email and every test email MUST have a footer with an unsubscribe link, in both the HTML
  and the plain-text part. The plain-text part shows the full link address.
- **FR-002**: The link MUST lead to the same unsubscribe page, for the same channel, as the mail client's unsubscribe
  option in that email. No new page, link type or token is added.
- **FR-003**: Opening the link MUST NOT change anything. The channel becomes *unsubscribed* only when the button on
  the page is pressed.
- **FR-004**: The footer text MUST say why the recipient gets the email and that they can stop these notifications
  to this address with the link or with their mail client's unsubscribe option. The text exists in English and
  Dutch, and the email uses the subscriber's language.
- **FR-005**: In the HTML part, the link MUST match the existing footer style (small grey text), MUST look like a
  link (underlined), and MUST meet the same contrast rule as the rest of the footer.
- **FR-006**: The mail client's unsubscribe option MUST keep working as before on every email that has it.
- **FR-007**: Confirmation emails and account emails MUST NOT get an unsubscribe link.
- **FR-008**: Logs MUST NOT contain the unsubscribe link or its token (unchanged from 003 FR-031).

### Amendments to earlier specs

This feature changes recorded decisions of 003 and 007. These documents are updated during implementation so they
agree with this spec:

- **003 clarification "Unsubscribing?"**: there is now a login-free unsubscribe page that the email links to, next
  to the mail client's button.
- **003 FR-030**: the footer unsubscribe link is one of the notification actions that work without login, only for
  its own channel.
- **003 SC-003**: the footer unsubscribe link is the one other email link allowed to change something without login,
  and only through the button on the page it opens.
- **003 contracts/messages.md**: the change email footer mentions the link, and the test email gets the same
  footer. The "Links" line lists the unsubscribe link as the one exception.
- **007 contracts/email-html.md**: the footer may contain a link; the test email now has a footer. 007's rule that
  the plain-text part stays unchanged does not apply to this footer change.

### Key Entities

- **Channel**: one notification address of a subscriber, with a status (*active*, *unsubscribed*, …) and its own
  unsubscribe address. Unchanged by this feature; the footer link uses that same address.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of change and test emails show the unsubscribe link in the footer of both the HTML and the
  plain-text part, in the recipient's language.
- **SC-002**: A recipient goes from opening the email to being unsubscribed in 2 clicks (the link, then the button)
  without logging in.
- **SC-003**: Opening the link without pressing the button leaves the channel active in 100% of cases.
- **SC-004**: After unsubscribing through the link, 0 further emails are sent to that channel.
- **SC-005**: 0 confirmation or account emails contain an unsubscribe link.

## Assumptions

- The existing one-button unsubscribe page is good enough as the landing page; its look stays as it is (007 keeps
  the public pages minimal).
- The footer link is a text link, not a large button, so it does not compete with the email's main content and
  stays readable in every supported mail app. The roadmap's "link/button" is satisfied by the link plus the button
  on the page.
- Test emails get the link because they already carry the mail client's unsubscribe option (003 FR-010).
- Unsubscribing stays per channel. Turning off one calendar stays on the settings links that change emails already
  have, behind login.
- Other channel types than email are out of scope; email is the only channel type today.
