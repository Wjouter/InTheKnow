# Contract: Filter rules

The rule format shared by presets, subscription filters and the API. A filter is a JSON array of at most 50 rules.

## Rule

```json
{ "outcome": "hide", "when": { "changeKind": ["location"], "activityType": ["holiday"] } }
```

| Key | Type | Meaning |
|---|---|---|
| `outcome` | `"showOnly"` \| `"hide"` \| `"lowPriority"` | What a match does (FR-003) |
| `when` | object | Conditions. Every key present must match (AND). Must contain at least one key. |

The object is closed: any other key, at either level, is rejected.

## Conditions (`when`)

Available on every calendar:

| Key | Value | Matches |
|---|---|---|
| `changeKind` | non-empty array of `added`, `removed`, `name`, `time`, `status`, `location`, `description` | The change is one of these kinds. `location` covers raw location and rooms; `description` covers the raw description and additional details (research R5). |
| `eventEnded` | `true` (only) | The event's end time was before `detectedAt`, in every state that exists (research R6) |
| `statusFrom`, `statusTo` | `confirmed` \| `tentative` \| `cancelled` \| `none` | A status change from and/or to this status. Either or both may be given. Matches status changes only. |
| `nameContains` | text, 1–100 characters | Case-insensitive substring of the event name, before or after |

MyTimetables only (active per calendar, FR-006):

| Key | Value | Matches |
|---|---|---|
| `changeKind` | additionally `activityType`, `groups`, `teachers` | As above. `teachers` needs teacher detection. |
| `activityType` | non-empty array of `lesson`, `test`, `holiday`, `appointment`, or other text | The event's activity type, before or after |
| `group` | non-empty array of group names | One of the event's attending groups, before or after |
| `room` | non-empty array of room names | One of the event's rooms, before or after |
| `teacher` | non-empty array of teacher names (`"Last, First"`) | One of the event's teachers, before or after |

Values in `activityType`, `group`, `room` and `teacher` compare exactly, ignoring letter case. A condition
with several values matches when any of them matches.

## Outcome (FR-004)

For a change, using only the rules that are active on the change's calendar:

1. If at least one active `showOnly` rule exists and the change matches none of them → **hidden**.
2. Else if it matches any active `hide` rule → **hidden**.
3. Else if it matches any active `lowPriority` rule → **lowPriority**.
4. Else → **normal**.

A rule is **inactive** on a calendar when one of its conditions is not active there (research R5). Inactive
rules are stored, and reported by the API, but ignored.

## Validation

Rejected with `400` and a `reason` (FR-024, FR-025), leaving stored state unchanged:

| `reason` | When |
|---|---|
| `invalid_rules` | Not an array, more than 50 rules, a rule that is not an object, or an unknown key |
| `invalid_outcome` | `outcome` missing or not one of the three values |
| `empty_conditions` | `when` missing or empty |
| `invalid_condition` | Wrong type, an unknown `changeKind` or status value, `eventEnded` not `true`, an array that is empty or has more than 50 values, or a text or value outside 1–100 characters |

MyTimetables conditions are **not** rejected for being inactive on a calendar (FR-020, FR-024).

## Default preset

The built-in global preset `Default` (FR-010):

```json
[
  { "outcome": "hide", "when": { "eventEnded": true } },
  { "outcome": "hide", "when": { "changeKind": ["location"], "activityType": ["holiday"] } },
  { "outcome": "hide", "when": { "changeKind": ["activityType", "groups"] } },
  { "outcome": "lowPriority", "when": { "statusFrom": "tentative", "statusTo": "confirmed" } }
]
```

The operator may edit its rules but not delete it. On a generic calendar the second
and third rules are inactive.

## For the notification feature (FR-009)

The outcome of a recorded change for a subscriber is `FilterEngine.Outcome(rules, change, active)`, the same
function the changes endpoint uses. The notification feature calls it per (change, subscriber) instead of
re-implementing the rules, and skips `hidden` changes.
