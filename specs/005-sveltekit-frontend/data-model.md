# Data Model: Web Frontend

Phase 1 of [plan.md](./plan.md). The web interface keeps no database and no server-side store (FR-002).

It has:
- the **browser-side state**: cookies that hold only what the spec's Key Entities allow;
- **one column** added to the API's `account` table (FR-045);
- **view models**: shapes built per request from API responses and thrown away after the page is rendered.

## Browser-side state (cookies)

| Cookie | Entity (spec) | Value | Attributes | Set by | Cleared by | Why it is needed |
|---|---|---|---|---|---|---|
| `itk_session` | Browser session | The API session token | `HttpOnly; Secure¹; SameSite=Lax; Path=/; Max-Age=34560000` (400 days) | Sign-in (no 2FA), the code step, named-invite acceptance | Sign-out, sign-out everywhere, any `401 not_signed_in` from the API, account deletion | FR-003, FR-004: links the browser to its API session. Nothing else is stored (research R3). |
| `itk_pending` | Browser session (before the second step) | The awaiting-code token | `HttpOnly; Secure¹; SameSite=Lax; Path=/sign-in; Max-Age=600` | Sign-in with `secondFactorRequired: true` | A successful code step, sign-out, expiry (10 min) | US6-3: the code step needs the half-signed-in session. It is kept apart from `itk_session`, so the gate never treats it as signed in. |
| `itk_invite` | (none; a token carrier) | A shareable invite token | `HttpOnly; Secure¹; SameSite=Lax; Path=/account/sign-up; Max-Age=3600` | `GET /account/sign-up?invite=…` (then `303` to the clean address) | A successful sign-up, or expiry | Keeps the token out of the address bar before a bot check script loads (research R15) |
| `itk_lang` | Language choice | `en` or `nl` | `Secure¹; SameSite=Lax; Path=/; Max-Age=31536000` | The visitor's language switch | Never (it is overwritten on the next switch) | US9-1: remembers a visitor's switch. It is not personal data. Signed-in users follow their account instead. |

¹ SvelteKit leaves out `Secure` only on plain `http://localhost` during development.

**Validation**: each cookie is read only as an opaque string and is only ever passed on to the API, apart from
`itk_lang`. An `itk_lang` value other than `en`/`nl` is ignored.

**Personal data**: none of these cookies holds personal data in readable form. The tokens are credentials: they are
never logged (FR-047), never rendered into a page, and never readable by scripts (SC-003).

## API addition: `account.home_page` (FR-045, FR-046)

Migration `backend/InTheKnow/Migrations/005_frontend.sql`:

```sql
ALTER TABLE account ADD COLUMN home_page text NOT NULL DEFAULT 'changes'
    CHECK (home_page IN ('changes', 'combined'));
```

| Column | Type | Constraint | Meaning |
|---|---|---|---|
| `home_page` | `text` | `NOT NULL DEFAULT 'changes'`, `CHECK IN ('changes','combined')` | Which view the signed-in home page shows (spec Key Entity "Home page setting"). A display preference, not personal data (Principle II). Existing accounts get `changes`. It is deleted with the account (004 cascade). |

- **Read**: `GET /api/me` gains `"homePage"`.
- **Write**: `PUT /api/me/settings` ([contracts/api.md](./contracts/api.md)).
- **Validation**: server-side, with `400 invalid_home_page`, and the `CHECK` backs it.

No other table changes:
- **Newest-first paging** reads `detected_change` backwards on its existing `(calendar_id, id)` index.
- **Rule positions** come from `FilterRules.Parse`.
- **Follower counts** are `count(*)` on `subscription_filter` over its existing `preset_id` index.

## View models (per request, never stored)

These are the shapes `load` functions build from API responses. Every user- or feed-supplied string in them is
printed as text (FR-010).

### Me (root layout, every signed-in request)

| Field | From | Used for |
|---|---|---|
| `email` | `GET /api/me` | Header, the Account tab, and the "signed in as" line on the wrong-account page |
| `role` | `GET /api/me` | Shows the Admin nav link (US8-2) |
| `language` | `GET /api/me` | Page language (FR-040) |
| `twoFactor`, `recoveryCodesLeft`, `hasPassword`, `pendingEmail` | `GET /api/me` | Settings tabs, and whether step-up asks for a code (research R8) |
| `homePage` | `GET /api/me` (addition) | The home view (FR-046) |
| `effectiveTimeZone` | `GET /api/me/notification-settings` | All date formatting (FR-028) |

The two calls run in parallel in `hooks.server.ts` and are put on `event.locals`. Visitors skip both.

### ChangeGroup (changes feed, home page)

Built from one page of `GET /api/me/changes?order=newest` plus `GET /api/me/calendars` (for the names).

| Field | Rule |
|---|---|
| `eventName` | `eventName` of the group's first change |
| `when` | `eventStartsAt`, formatted in the effective zone. The year is added when it isn't the current year (research R12). |
| `calendarName` | The calendar's `name` (label, else feed name, else host). Shown only when the user has more than one calendar (US2-9). |
| `tone` | The most severe tone in the group: `cancelled` > `moved` > `new` > `changed` (research R11) |
| `items[]` | `{ sentence, outcome }` for each change. `outcome` is `normal`, `lowPriority` or `hidden`. |
| grouping key | `(calendarId, eventKey, detectedAt)`, over consecutive items of the page (001 detected-change "Grouping") |

**Paging**:
- The page size is 50.
- "Older changes" links to `?before=<next>`, which is present only when the API returns a `next`.
- "Newest" links back to the start.
- `showHidden=1` in the query sets `includeHidden=true` (US2-8).

### Calendar (calendar list and Overview tab)

From `GET /api/me/calendars`:
- `calendarId`, `name`, `status`;
- `lastCheckedAt`, `lastOk`, `lastError` → a message (research R10);
- `consecutiveFailures`, `nextCheckAt`;
- `notify`, `label`.

The links (`links[]`) are **not** carried into the view: only their count is, so the URL is never shown (FR-023).

**States shown**:

| Status | When |
|---|---|
| Checked OK | `lastOk` |
| Last check failed: `<reason>` | `lastOk === false` and `status !== 'failing'` |
| Failing since `<n>` checks: `<reason>` | `status === 'failing'` |
| Not checked yet | `lastCheckedAt === null` |

### Filter and rule editor (calendar Filter tab, presets, admin presets)

- From `GET /api/me/calendars/{id}/filter`: `source`, `rules`, `inactiveRules`, `activeConditions` and `values`,
  as in 002.
- The editor state is the `rules` array (002 [rules.md](../002-subscriber-change-filters/contracts/rules.md)).
- Each rule carries its `sentence`, `inactive` and `inactiveReason`, and an `error` when the last save was refused
  with `rule: <index>` (research R13, R14).

### Preset (presets page)

From `GET /api/me/presets` (and `GET /api/admin/presets`):
- `id`, `name`, `scope`, `isDefault`, `rules`;
- `followers`: an addition, personal presets only. It fills the "N calendars follow this preset; they will …"
  confirmation (US3-6).

### Channel (Notifications tab)

From `GET /api/me/channels`:
- `id`, `type`, `target` (printed as text), `status`, `failureReason` → a message, `lastMessageAt`,
  `accountAddress`.

**Actions offered, by status** (FR-034):

| `status` | Actions |
|---|---|
| `awaitingConfirmation` | Resend confirmation, remove |
| `active` | Pause, send test, remove |
| `paused` | Resume, remove |
| `failing` | Resend confirmation, remove |
| `unsubscribed` | Resend confirmation, remove |

- The account address channel (`accountAddress: true`) is marked "account address" (US4-7).
- `GET /api/me/channel-types` returning no `email` hides the add form behind "adding addresses isn't available
  right now" (edge case "No email configured").

### Admin rows

- **Account**: from `GET /api/admin/accounts`: `id`, `email`, `role`, `confirmed`, `disabled`, `twoFactor`,
  `createdAt`.
- **Invite**: from `GET /api/admin/invites`.
- **Admin calendar**: from `GET /api/admin/calendars`, with no links or addresses.
- **Channel type status**: from `GET /api/admin/channel-types`.

All are used as the API returns them, a page at a time with `after`/`next` where the API pages.

## State transitions the pages follow

None are owned by the frontend. The pages show the API's own:
- **Sign-in**: visitor → (`itk_pending`) → signed in; signed in → visitor on sign-out or `401`.
- **Channel**: the 003 channel status machine. The table above lists the actions per status.
- **Filter source**: preset → custom (on edit) → preset (on apply) (002).
- **Two-step setup**: off → setup started (`POST /api/me/2fa/setup`) → on (`enable`, recovery codes shown once) →
  off (`DELETE`, step-up with a code) (004).
