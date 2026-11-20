# Feature Specification: Dark emails

**Feature Branch**: `012-dark-email`

**Created**: 2026-11-15

**Status**: Draft

**Input**: User description: "Emails follow the device's light or dark setting automatically. That is the
default. An account-wide setting can overrule it: always light or always dark." A follow-up to the dark mode of
the web app (spec 011), which left emails out, and a change to the email layout of spec 007, which asks mail
apps for light mode only.

## Clarifications

### Session 2026-11-19

- Q: Which mail apps should the manual acceptance checks use for each kind of mail-app behaviour? → A: Honours
  the email's dark colours: Apple Mail (iOS or macOS). Recolours itself: Gmail app (Android) and new Outlook /
  Outlook on the web. Ignores the dark colours: Gmail web.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The email is dark when my mail app is dark (Priority: P1)

A student reads their mail on a phone set to dark. Today an InTheKnow email is a bright white card in a dark
inbox, or the mail app recolours it on its own with results nobody designed. With this feature the email carries
a dark version of itself: near-black page, a slightly lighter card, light text, the same magenta button. A mail
app that supports it shows that version when the device is dark and the light version when it is light. The
student sets nothing.

**Why this priority**: this is the request. It is also the only way to follow the device at all: the server does
not know the reader's device when it sends, and one message is read on several devices, so the choice has to
travel inside the email.

**Independent Test**: send every email type to a test mailbox, open each in a mail app that honours an email's
own dark colours, and switch the device between light and dark.

**Acceptance Scenarios**:

1. **Given** an account with the email theme on its default, **When** an email is opened in a mail app that
   honours an email's own dark colours on a device set to dark, **Then** the email shows the dark version: dark
   page, dark card, light text, light status labels on deep tints, magenta button with white text.
2. **Given** the same email, **When** it is opened on a device set to light, **Then** it looks exactly as emails
   look today.
3. **Given** the same email open in a mail app, **When** the device switches between light and dark, **Then**
   the email follows, as far as that mail app does so for any email.
4. **Given** any email type (change notification, test notification, and every account email), **When** it is
   sent, **Then** it carries both versions.
5. **Given** an email in its dark version, **When** it is checked, **Then** every text and label has a contrast
   of at least 4.5:1 with its background, and the four kinds of change still differ by their words, not by
   colour alone.

---

### User Story 2 - A mail app that goes its own way still shows a readable email (Priority: P1)

A student uses a mail app that ignores an email's own dark colours. Some of these keep the light email as it is;
others recolour it themselves when the device is dark. In both cases the email stays readable: no text
disappears into its background, the button is still a button, the wordmark is still there.

**Why this priority**: several widely used mail apps behave this way, and which app a student uses is not ours
to choose. Carrying two versions must not make the email worse anywhere than it is today.

**Independent Test**: open every email type in one mail app that ignores the dark colours and one that forces
its own dark mode, on a device set to dark, and read each from top to bottom.

**Acceptance Scenarios**:

1. **Given** a mail app that ignores an email's own dark colours, **When** an email is opened, **Then** it shows
   the light version, the same as today.
2. **Given** a mail app that recolours emails itself in dark mode, **When** an email is opened on a device set to
   dark, **Then** all text is readable on its background and the action button is recognisable.
3. **Given** any mail app that spec 007 lists as supported, **When** an email is opened, **Then** it fits
   without sideways scrolling, as today.

---

### User Story 3 - I overrule it for my account (Priority: P2)

A student's mail app shows the dark version but they find the light one easier to read; or their mail app never
shows the dark version and they want it anyway. In Settings › Preferences they set the email theme to "Always
light" or "Always dark". From then on every email to their account is sent in that one version, on every device
they read it on.

**Why this priority**: following the device is right for most people and needs no setting. The override is for
the cases where the mail app gets it wrong or the person simply prefers one look.

**Independent Test**: set the email theme to each of the three values in turn, send a test notification each
time, and open it on a device set to light and on one set to dark.

**Acceptance Scenarios**:

1. **Given** an account that never touched the setting, **When** Preferences is opened, **Then** the email theme
   shows "Same as my device" as chosen.
2. **Given** the email theme set to "Always light", **When** an email is sent to that account, **Then** it is the
   light version only and asks mail apps to keep it light, as every email does today.
3. **Given** the email theme set to "Always dark", **When** an email is sent to that account and opened on a
   device set to light, in a mail app that shows emails as sent, **Then** it shows the dark version.
4. **Given** the email theme set to "Always dark", **When** the email is opened in a mail app that recolours
   emails itself, **Then** it is readable (User Story 2, scenario 2).
5. **Given** a changed email theme, **When** it is saved, **Then** it holds for every email sent after that, to
   every address of the account, whichever device asked for the email; emails already sent do not change.
6. **Given** a changed email theme, **When** the person sends themselves a test notification, **Then** that email
   uses the new setting.
7. **Given** the web app's own theme setting (spec 011, per device), **When** either setting is changed,
   **Then** the other is not affected, and the page says which one is for emails and for the whole account.

---

### Edge Cases

- An email to an address that has no account yet, or whose account is not known at that moment (sign-up
  confirmation, invite): it follows the device, the default.
- An email about an account sent to a different address than the account's own (confirming a new email
  address): it uses the account's setting.
- A forwarded or printed email: forwarding keeps whatever the receiving mail app makes of it; print is the mail
  app's concern.
- A mail app that shows the dark version but keeps images or its own link colours light: the email has no
  images, and links carry their own colour in both versions.
- The plain-text part has no colours and does not change.
- The email must stay under the size limit of spec 007 (FR-022) with both versions in it; the dark version adds
  colours only, not a second copy of the content.
- A stored value that is missing or not one of the three is treated as "Same as my device".
- An account that is deleted: its email theme goes with it.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Every email InTheKnow sends (change notifications, the test notification and all account emails)
  MUST carry a dark version next to the light one, and MUST tell mail apps it supports both, unless the
  account's email theme says otherwise (FR-007, FR-008). This replaces FR-019 of spec 007 for those emails.
- **FR-002**: The light version MUST be the layout of spec 007, unchanged in any visible way.
- **FR-003**: The dark version MUST use the dark colour set of the web app (spec 011, FR-003): page `#141413`,
  card `#1c1c1a`, headings `#f5f5f2`, body text `#d4d4cf`, quiet text `#a6a6a0`, lines between rows `#343431`,
  links and the wordmark's second half in the magenta that is readable on dark (`#ec51a7` for text), the button
  magenta `#e20e80` with white text, and the change labels in red `#ff8d9b`, amber `#f5b651` and green `#6fd39a`
  on deep tints of the same hue, matching the three label hues of the light email (time changes share amber with
  field changes, so the web app's blue is not used; FR-004, research R4); the low priority label light grey on dark
  grey.
- **FR-004**: The dark version MUST differ from the light one in colours only: same texts, order, sizes,
  spacing, button and links.
- **FR-005**: In the dark version every text and label MUST have a contrast of at least 4.5:1 with its
  background (3:1 for the wordmark and the heading, as large text).
- **FR-006**: In a mail app that ignores the dark version or recolours emails itself, the email MUST stay
  readable: no text invisible on its background, the button recognisable, no sideways scrolling. The supported
  mail apps and fallbacks of spec 007 (FR-017, FR-018) keep applying.
- **FR-007**: An account MUST have an email theme with three values: "Same as my device" (the default), "Always
  light" and "Always dark". With "Always light" an email MUST be sent as today: light version only, asking mail
  apps for light.
- **FR-008**: With "Always dark" an email MUST be sent with the dark version as its only version, so that a mail
  app that shows emails as sent shows it dark on any device.
- **FR-009**: The email theme MUST be set on Settings › Preferences, next to the language and time zone that
  emails already follow. Its label MUST make clear that it is for emails and for the whole account, to tell it
  apart from the web app's per-device theme.
- **FR-010**: The email theme MUST apply to every email sent to the account after it was saved, on every address
  the account receives email on. Emails that are not tied to a known account MUST use the default.
- **FR-011**: The plain-text part, subjects, headers (including the unsubscribe headers), recipients and the
  order of the parts MUST NOT change.
- **FR-012**: The HTML part MUST stay within the size limit of spec 007 (FR-022) for every email type and
  setting.
- **FR-013**: The email theme MUST be validated on the server as one of the three values; anything else is
  refused, and a missing value counts as the default.
- **FR-014**: The email theme MUST be stored with the account and nowhere else, and MUST be removed with the
  account. Nothing about the reader's device or mail app is collected.

### Key Entities

- **Email theme**: one of "same as my device", "always light", "always dark". It belongs to an account, has the
  default "same as my device", and decides which versions an email to that account carries. It is not personal
  beyond being a display preference, and it is separate from the web app's theme choice, which belongs to a
  device.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In Apple Mail (iOS or macOS), which honours an email's own dark colours, 100% of the email types
  show the dark version on a device set to dark and the light version on a device set to light, in both
  languages, with the default setting.
- **SC-002**: The light version of every email type is identical to today's email, compared side by side.
- **SC-003**: In the dark version, a contrast check of every email type reports no failure.
- **SC-004**: In Gmail web (ignores the dark version), and in the Gmail app on Android and new Outlook / Outlook on
  the web (both recolour emails themselves) on a device or account set to dark, every email type can be read in
  full and its button used, at 320px and at desktop width.
- **SC-005**: A person can change the email theme in under 15 seconds from the Preferences page, and the next
  email they receive uses it.
- **SC-006**: With "Always light" or "Always dark" set, the same email looks the same on a device set to light
  and on one set to dark, in a mail app that shows emails as sent.
- **SC-007**: No email type exceeds the size limit of spec 007 with the dark version added.

## Assumptions

- Whether an email follows the device is up to the mail app. Some honour an email's own dark colours; others
  ignore them or recolour the email themselves. This feature makes the email right where it can be, and readable
  everywhere else (User Story 2). It cannot make every mail app follow the device.
- The override is per account, by request: one message is read on several devices, so a per-device setting for
  email is not possible.
- "Always dark" cannot stop a mail app that recolours emails on its own from changing it again; it must only
  stay readable there.
- The dark colours are those of the web app's dark theme (spec 011), so email and app look alike.
- No dark email screen exists in the InTheKnow Frontend or Email Layout designs yet. The colour set is fixed by
  FR-003; a dark twin in the Email Layout design can be added before planning if a visual check is wanted.
- The setting sits on Settings › Preferences because that page already says emails follow its language and time
  zone. A person without an account has no setting and gets the default.
- This feature depends on spec 007 (email layout) and on the colour set of spec 011. It does not depend on
  spec 011 being built first, but it replaces the "Emails are out of scope" lines of that spec and the "no
  dark-mode version" assumption and FR-019 of spec 007.
- The open manual check of spec 007 (T027, forced dark mode in a real mail app) is covered by SC-004 here.
