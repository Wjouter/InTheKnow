# Feature Specification: Dark mode

**Feature Branch**: `011-dark-mode`

**Created**: 2026-11-15

**Status**: Draft

**Input**: User description: "Frontend: dark mode" (InTheKnow roadmap,
[Wjouter/InTheKnow#32](https://github.com/Wjouter/InTheKnow/issues/32)): "Add a dark theme to the frontend."
By default the app follows the device's light or dark setting. A theme setting, kept per device, lets a person
pick light or dark themselves: visitors with a toggle in the header, signed-in people in their preferences. A
theme picked before signing up stays in effect after the account is created. The approved design is the "Dark mode" section of the InTheKnow Frontend design
(https://claude.ai/artifact/VwQA99wR7KWbd4gxngyCwm): a dark twin of every screen, one dark colour set in that
design's shared stylesheet, the theme choice on the Settings › Preferences screen, and the theme toggle in the
header of the signed-out screens.

## Clarifications

### Session 2026-11-16

- Q: Must the theme toggle and the Preferences theme choice also work when JavaScript is off or has not loaded
  yet, the way the language switch does today? → A: Yes. Without script a press reloads the page in the new
  theme; with script it changes at once, without a reload.
- Q: When someone changes the theme in one tab, should other tabs of the app that are already open on that
  device change at once, or only when they next load a page? → A: At once, in every open tab, without a reload.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The app is dark when my device is dark (Priority: P1)

A student has their phone or laptop set to dark. They open InTheKnow in the evening to check what changed in
their timetable. Today the app is a bright white page. With this feature the whole app is dark: a near-black
page, slightly lighter header, cards and fields, light text, and the same magenta for buttons, the title band and
the wordmark. They do not have to find or set anything.

**Why this priority**: this is the whole roadmap item. Most students open the app on a phone, often at night, and
a device set to dark is a clear statement of what they want.

**Independent Test**: set the device (or the browser's emulation) to dark, open the changes feed, a calendar's
filter page, a settings page and the sign-in page, and compare each with its dark screen in the design.

**Acceptance Scenarios**:

1. **Given** a device set to dark, **When** any page of the app is opened, **Then** the page uses the dark colour
   set of the design: page, header, cards, fields, text, borders, status colours and shadows.
2. **Given** a device set to dark, **When** a page is loaded or reloaded, **Then** it is dark from the first thing
   shown; there is no moment of the light page.
3. **Given** a device set to dark, **When** a page has selects, checkboxes, radio buttons, text fields or a
   scrollbar, **Then** those are dark too, not white boxes on a dark page.
4. **Given** a device set to dark, **When** a dialog, the history card over the feed, or a toast is shown,
   **Then** it stands out from the page behind it and its text is readable.
5. **Given** a device that switches from light to dark (or back) while the app is open, for example at sunset,
   **When** the device switches, **Then** the app follows without a reload.
6. **Given** a signed-out visitor on a device set to dark, **When** they open the home page, sign-in, sign-up or
   any account page, **Then** those are dark as well.

---

### User Story 2 - Everything stays readable in dark (Priority: P1)

A student with weak eyesight, or anyone outside in poor light, uses the app in dark. Every piece of text, every
field border, every icon that carries meaning and the keyboard focus ring can be told from its background, as
well as in the light theme. The kind of a change (cancelled, moved, new, changed) can still be told apart.

**Why this priority**: a dark theme that is hard to read is worse than none, and it would be applied
automatically to everyone whose device is dark. The light theme was built to a contrast standard (spec 005); the
dark theme must meet the same one.

**Independent Test**: in dark, run an automated contrast check on every page listed under FR-002, and tab through
one form to see the focus ring on every control.

**Acceptance Scenarios**:

1. **Given** dark mode, **When** any page is checked, **Then** all text has a contrast of at least 4.5:1 with its
   background (3:1 for text of 24px and larger, and for bold text of about 19px and larger).
2. **Given** dark mode, **When** any page is checked, **Then** field borders, the switch, checkbox and radio
   outlines, meaningful icons and the focus ring have a contrast of at least 3:1 with what is next to them.
3. **Given** dark mode, **When** a notice, badge or change icon for cancelled, moved, new or changed is shown,
   **Then** its text or icon is readable on its tinted background, and the four kinds still differ by icon or
   words, not by colour alone.
4. **Given** dark mode, **When** text sits on a magenta fill (primary button, title band, counters), **Then** it
   is white and readable, as in light.
5. **Given** dark mode, **When** the switch, the active item of the phone bottom bar, the dark button (also when
   hovered) and the toast are shown, **Then** each is legible: the switch knob is clearly visible on its track in
   both states, and the label text of each meets scenario 1.
6. **Given** dark mode and either language, **When** a page is shown at phone width and at desktop width,
   **Then** scenarios 1 to 5 hold.

---

### User Story 3 - Light stays exactly as it is (Priority: P1)

A student whose device is set to light, or has no preference, opens the app. Nothing has changed for them.

**Why this priority**: dark mode touches the colours every page uses. The existing look was designed and checked;
it must not shift by accident.

**Independent Test**: with the device set to light, compare screenshots of the pages listed under FR-002 before
and after the change.

**Acceptance Scenarios**:

1. **Given** a device set to light, **When** any page is opened, **Then** it looks the same as before this
   feature, apart from the new theme controls (FR-011, FR-018).
2. **Given** a device or browser that states no preference, or does not support one, **When** any page is opened,
   **Then** the light theme is shown.
3. **Given** a page that is printed, **When** the device is set to dark, **Then** the print is still dark text on
   white.

---

### User Story 4 - I pick the theme myself, on this device (Priority: P2)

A student keeps their laptop in light but finds InTheKnow easier to read in dark, or the other way round. In
Settings › Preferences they choose between "Same as this device", "Light" and "Dark". The choice applies at once
and is remembered on that device only: their phone keeps its own choice, and a new device starts on "Same as this
device".

**Why this priority**: following the device is right for most people and already delivers the roadmap item; the
setting is for those whose device setting does not match what they want in this app.

**Independent Test**: on a device set to light, choose "Dark" in Preferences, see the app turn dark, reload, sign
out and open the sign-in page, and see dark each time; then open the app in another browser and see it follow
that device.

**Acceptance Scenarios**:

1. **Given** a device on which no theme was ever chosen, **When** Preferences is opened, **Then** "Same as this
   device" is selected and the app follows the device (User Story 1).
2. **Given** a device set to light, **When** the person chooses "Dark", **Then** the whole app is dark from then
   on, on that device, whatever the device setting is.
3. **Given** a device set to dark, **When** the person chooses "Light", **Then** the whole app is light from then
   on, on that device.
4. **Given** a chosen theme, **When** a page is loaded or reloaded, **Then** it is shown in that theme from the
   first thing shown; the other theme is never shown first.
5. **Given** a chosen theme, **When** the person signs out, **Then** the signed-out pages on that device keep
   that theme, and so does the next person's session on that device.
6. **Given** a theme chosen on one device, **When** the same account is used on another device or browser,
   **Then** that one has its own choice and starts on "Same as this device".
7. **Given** a chosen theme, **When** the person chooses "Same as this device" again, **Then** the app follows
   the device again, including a later change of the device setting.
8. **Given** the Preferences page, **When** the theme choice is shown, **Then** it says that it is saved on this
   device only, so it is not mistaken for the account settings around it.

---

### User Story 5 - A visitor switches the theme, and it sticks through sign-up (Priority: P2)

Someone who has no account yet opens InTheKnow on a laptop set to light and would rather read it in dark. In the
header, next to the language switch, is a theme toggle. One press turns the app dark; another turns it light
again. The browser remembers the last press. They then sign up: the confirmation pages, and the app once they
are signed in, are in the theme they picked, and Preferences shows that theme as selected. They never have to
set it twice.

**Why this priority**: a visitor cannot reach Preferences, and the sign-up and sign-in pages are the first thing
every new student sees. Losing the picked theme at the moment the account is created would feel like a bug.

**Independent Test**: signed out, on a device set to light, press the toggle on the home page, walk through
sign-up to the first signed-in page, and check every page on the way is dark and Preferences shows "Dark".

**Acceptance Scenarios**:

1. **Given** a signed-out visitor, **When** any signed-out page is shown (home, sign-in, sign-up, the account
   pages, about, error pages), **Then** the header has a theme toggle.
2. **Given** a visitor seeing the light theme, **When** they press the toggle, **Then** the app turns dark at
   once and "Dark" is remembered on that device; pressing it again turns the app light and remembers "Light".
3. **Given** a visitor who never pressed the toggle, **When** pages are shown, **Then** the app follows the
   device (User Story 1) and nothing is remembered.
4. **Given** a visitor who pressed the toggle, **When** they reload, close and reopen the browser, or come back
   another day, **Then** the picked theme is still in effect.
5. **Given** a visitor who picked a theme, **When** they sign up, confirm their email and reach the app, **Then**
   every page on the way and after is in that theme, and Preferences shows that theme selected.
6. **Given** a visitor who picked a theme, **When** they sign in to an existing account on that device, **Then**
   the picked theme stays in effect in the same way.
7. **Given** an account created on a device where a theme was picked, **When** that account is used on another
   device, **Then** that device follows its own setting; the pick was not saved with the account.
8. **Given** the toggle, **When** it is used with a keyboard or a screen reader, **Then** it can be focused and
   pressed, and it says which theme a press will switch to, in the page's language.

---

### Edge Cases

- A browser extension or a browser setting that forces its own dark mode on top of the app: the app tells the
  browser it already supports dark, so the page is not recoloured a second time.
- High-contrast (forced colours) mode: the app keeps working as it does today in that mode; forced colours win
  over the dark colour set.
- Hover, pressed, disabled and error states of buttons, links and fields are part of "every page": each must
  meet the contrast rules of User Story 2 in dark.
- The bot check on sign-up and sign-in, if it shows a widget from another party, should follow dark when that
  widget offers it; if it does not, a light widget on the dark page is accepted.
- The two-factor setup page shows a QR code. A QR code must stay dark squares on a light ground with a light
  margin, or phones cannot scan it; it is shown on a light tile in dark mode.
- Error pages (not found, not allowed, service unavailable) are dark too.
- A screen the design has no dark twin for (the admin pages, about, the account email pages) uses the same
  colour set and the same component looks as the screens that do.
- Emails are not part of this feature. They keep the layout of spec 007.
- A browser that refuses to remember anything for the site (private window that is closed, storage blocked): the
  choice applies while it can and is forgotten afterwards; the app then follows the device again. Nothing breaks.
- A remembered choice that is not one of the three known values (edited by hand, or left by an older version)
  is treated as "Same as this device".
- Clearing the browser's data for the site resets the choice to "Same as this device".
- The visitor toggle has two states. A visitor who pressed it cannot go back to "Same as this device" from the
  toggle; that option is in Preferences after signing in, or comes back when the site's data is cleared.
- The toggle switches away from what is shown: on a device set to dark with nothing remembered, the first press
  gives light.
- Two tabs open, one signed out and one signed in: a press on the toggle in the first changes the second too,
  and the other way round (FR-022).
- The signed-in header has no toggle; signed-in people use Preferences. Both write the same remembered choice.
- At phone width the signed-out header must still fit the wordmark, the toggle, the language switch, sign in and
  sign up without sideways scrolling.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Unless a theme was chosen on the device (FR-011), the app MUST show the dark theme when the device
  or browser asks for dark, and the light theme otherwise. It MUST follow a change of that setting while the app
  is open, without a reload.
- **FR-002**: The dark theme MUST cover every page of the app: the signed-out home page, sign-in (both steps),
  sign-up, all account pages (confirm, confirmation, email change, forgot password, reset, invite), the changes
  feed, the history card and history page, calendars, a calendar's overview, filter and notification pages,
  presets and a preset's edit page, all settings pages including two-factor setup, notifications, all admin
  pages, about, and the error pages.
- **FR-003**: The colours of the dark theme MUST be those of the approved design's dark colour set: page
  background `#141413`; raised surfaces (header, cards, fields, dialogs) `#1c1c1a`; the neutral scale turned
  around so the lightest neutral is the strongest text (`#f5f5f2`), body text `#d4d4cf` and quiet text
  `#a6a6a0`; magenta `#e20e80` unchanged for fills, the title band and the wordmark; magenta text and links
  `#ec51a7`; status text red `#ff8d9b`, amber `#f5b651`, green `#6fd39a`, blue `#8ab8f0` on deep tints of the
  same hue; heavier shadows; a darker backdrop behind dialogs; the focus ring in the lighter magenta. Where the
  design and this list differ, the design wins. Where a colour of the design does not meet FR-004, FR-004 wins
  and the colour is changed.
- **FR-004**: In dark, all text MUST have a contrast of at least 4.5:1 with its background, or 3:1 for large
  text. Field borders, control outlines, meaningful icons and the focus ring MUST have at least 3:1. This holds
  for hover, pressed, disabled-but-readable and error states, in both languages, at phone and desktop width.
- **FR-005**: The browser's own controls (selects and their lists, checkboxes, radio buttons, text fields,
  autofill, scrollbars) MUST be dark in dark mode.
- **FR-006**: A page MUST be in the right theme from its first paint, whether that theme comes from the device
  or from the choice remembered on it; the other theme MUST NOT be shown first.
- **FR-007**: The light theme MUST NOT change in any visible way, apart from the two new controls: the theme
  toggle in the signed-out header (FR-018) and the theme choice on Settings › Preferences (FR-011).
- **FR-008**: Text and icons on a coloured fill (magenta, or a solid status colour) MUST keep a fixed light
  colour in both themes; they MUST NOT follow the neutral scale when it turns around.
- **FR-009**: The following MUST be legible in dark, each meeting FR-004: the switch (knob clearly visible on
  its track, on and off); the active item of the phone bottom bar; the dark button in rest, hover and pressed
  state; the toast; the hidden-change row with its dashed outline; the neutral badges ("low priority",
  "hidden").
- **FR-010**: Anything that must stay light to work, such as the two-factor QR code, MUST be shown on a light
  tile in dark mode.
- **FR-011**: Settings › Preferences MUST offer a theme choice with three options: "Same as this device" (the
  default), "Light" and "Dark". A chosen "Light" or "Dark" MUST win over the device setting on every page of the
  app, signed in or signed out.
- **FR-012**: Printing MUST give dark text on white, whatever the theme.
- **FR-013**: The dark theme MUST NOT change any text, layout, size or behaviour; only colours and shadows
  differ between the themes.
- **FR-014**: The theme choice MUST be remembered on the device (browser) it was made on, and only there. It MUST
  NOT be stored with the account or sent to another device, and it MUST survive a reload, a sign-out and a later
  sign-in on that device.
- **FR-015**: The theme choice MUST apply as soon as it is made, and its label or hint MUST say that it is saved
  on this device only. It MUST be in both languages and reachable by keyboard like the other preferences.
- **FR-016**: A missing, unreadable or unknown remembered value MUST be treated as "Same as this device".
- **FR-017**: What is remembered MUST be the theme choice alone: one of three values, with nothing that
  identifies the person.

- **FR-018**: Every signed-out page MUST have a theme toggle in the header. A press MUST switch to the other
  theme than the one shown, at once, and remember that theme on the device as in FR-014. It MUST meet the 44px
  touch size, be usable by keyboard, and name the theme it switches to in both languages.
- **FR-019**: The toggle and the Preferences choice MUST be one and the same remembered choice. A theme picked
  as a visitor MUST stay in effect through sign-up, email confirmation and sign-in on that device, and MUST show
  as the selected option in Preferences afterwards.
- **FR-020**: Creating an account or signing in MUST NOT copy the theme to the account, and MUST NOT change or
  clear the choice remembered on the device.
- **FR-021**: The theme toggle and the Preferences theme choice MUST work when JavaScript is off or has not
  loaded yet, as the language switch does: a press then reloads the page in the chosen theme and remembers it.
  With script the theme changes at once, without a reload. Following the device (FR-001) MUST NOT need script.
- **FR-022**: A theme chosen in one tab, by the toggle or in Preferences, MUST take effect at once in every
  other tab of the app open in the same browser, without a reload, and the Preferences choice shown there MUST
  match. A tab where script is not running takes the theme on its next page load.

### Key Entities

- **Theme choice**: one of "same as this device", "light" or "dark". It belongs to a device (browser), not to an
  account, and has no other attributes.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: With the device set to dark, 100% of the pages listed in FR-002 are dark, in both languages, at
  phone width (390px) and desktop width (1280px).
- **SC-002**: An automated accessibility check of every page in FR-002, run in dark, reports zero colour-contrast
  failures.
- **SC-003**: With the device set to light, screenshots of every page in FR-002 before and after the feature show
  no difference other than the theme toggle in the signed-out header and the theme choice on Preferences.
- **SC-004**: On a slow connection, with the device set to dark, no frame of a page load shows a light
  background.
- **SC-005**: Each dark screen in the design matches the running app: same surfaces, text colours and status
  colours, with only the spots named in FR-009 and FR-010, and colours changed to meet FR-004,
  allowed to differ from the design's dark twins.
- **SC-006**: A person can change the theme in under 10 seconds from the Preferences page, and the change shows
  without a reload when script is running. With JavaScript off, the toggle and the Preferences choice still
  change and remember the theme, after one page reload.
- **SC-007**: A chosen theme is still in effect after a reload, after signing out, and after closing and
  reopening the browser, in 100% of tries on a browser that keeps site data.
- **SC-008**: Choosing a theme on one device changes nothing on a second device signed in to the same account,
  and adds nothing to the account's stored data.
- **SC-009**: With a theme chosen that differs from the device setting, no frame of a page load shows the other
  theme, on a slow connection.
- **SC-010**: A visitor can switch the theme with one press from any signed-out page.
- **SC-011**: A visitor who picks a theme and then signs up sees that theme on 100% of the pages from the toggle
  press to the first signed-in page, and never has to pick it again on that device.
- **SC-012**: With the app open in two tabs of one browser, a theme chosen in the first shows in the second
  within one second, without a reload.

## Assumptions

- The theme choice is per device by request. A per-account preference that follows the person to every device
  is out of scope.
- Signed-in people set the theme on Settings › Preferences (three options); visitors use a two-state toggle in
  the header. Both change the same choice remembered on the device.
- "Set as their default" at sign-up means the picked theme simply stays in effect on that browser (decided
  2026-11-15). No theme is stored with the account, so a second device starts on "Same as this device".
- Because the choice belongs to the device, not the account, it is shared by everyone who uses that browser.
- Remembering a display preference on the device is strictly functional storage, so it needs no consent prompt.
- "Light" in the setting means the light theme as it is today (User Story 3), also on a device set to dark.
- The design's dark screens are twins of the light screens that only swap colours. Known spots where those
  twins are not right and the app must do better are listed in FR-009 and FR-010: the switch knob, the active
  label of the phone bottom bar, the dark button's hover state, the toast text and the QR code.
- The deeper magenta title band considered in the first sketch was dropped: the band keeps the brand magenta in
  both themes.
- Emails are out of scope (spec 007 owns their look, including how mail apps recolour them).
- The app has no pictures or logos that would need a dark version; the wordmark is text.
- The contrast standard is the one the light theme already meets (spec 005, FR-043), so the existing automated
  accessibility checks are reused and run a second time in dark.
- This replaces the sentence in spec 010 (FR-019) that the app has a light theme only.
