# Data Model: Subscriber Change Filters

**Spec**: [spec.md](./spec.md) | **Research**: [research.md](./research.md) | **Builds on**: [001 data model](../001-ical-change-detection/data-model.md)

Every integrity rule below that the database can express is a constraint. The migration is `002_filters.sql`.

## Spec entity → storage mapping

| Spec entity | Storage |
|---|---|
| Rule | one element of a `jsonb` array (shape in [contracts/rules.md](./contracts/rules.md)) |
| Preset (personal, global, Default) | table `preset` |
| Filter | table `subscription_filter`: linked to a preset, or holding its own rules |
| Subscription | unchanged: view `subscription` from 001. Its filter is looked up by `(calendar_id, subscriber)`. |
| Change outcome | derived at read time by `FilterEngine`. Not stored. |

## Tables

### `preset`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `bigint` | PK, identity | |
| `owner` | `text` | NULL, `CHECK (owner IS NULL OR owner ~ '^[A-Za-z0-9._@-]{1,100}$')` | Subscriber id (same pattern as `calendar_link.subscriber`). `NULL` means a global preset. |
| `name` | `text` | NOT NULL, `CHECK (length(name) BETWEEN 1 AND 100)` | |
| `rules` | `jsonb` | NOT NULL, `CHECK (jsonb_typeof(rules) = 'array' AND jsonb_array_length(rules) <= 50)` | Rule shape is validated by the application (FR-024) |
| `is_default` | `boolean` | NOT NULL, default `false`, `CHECK (NOT is_default OR owner IS NULL)` | |
| `created_at` | `timestamptz` | NOT NULL, default `now()` | |
| | | `UNIQUE NULLS NOT DISTINCT (owner, name)` | Names are unique per subscriber, and among global presets (FR-025) |

Indexes: a unique partial index `ON preset ((true)) WHERE is_default` (at most one Default), and
`(owner)` for listing a subscriber's presets.

**Seed** (in the migration): one row with `owner = NULL`, `name = 'Default'`, `is_default = true` and the four
FR-010 rules (see [contracts/rules.md § Default preset](./contracts/rules.md#default-preset)).

### `subscription_filter`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `calendar_id` | `bigint` | NOT NULL, FK → `calendar(id)` ON DELETE CASCADE | |
| `subscriber` | `text` | NOT NULL, same pattern `CHECK` as `calendar_link.subscriber` | |
| `preset_id` | `bigint` | NULL, FK → `preset(id)` ON DELETE RESTRICT | Set when linked |
| `rules` | `jsonb` | NULL, `CHECK (rules IS NULL OR (jsonb_typeof(rules) = 'array' AND jsonb_array_length(rules) <= 50))` | Set when customised |
| | | PK `(calendar_id, subscriber)`; `CHECK ((preset_id IS NULL) <> (rules IS NULL))` | A row is linked or customised, never both or neither (FR-017) |

Index: `(preset_id)`, so the preset-delete conversion (FR-018) and the RESTRICT check do not scan the table.

**No row means linked to the Default preset** (research R1). A row exists only after a subscriber applies a
preset or edits a filter.

A filter belongs to the (calendar, subscriber) pair, not to a link. When 001's regroup moves a link to another
calendar, the filter row is copied to the new calendar (an existing row there wins, `Checker.cs`).

### Cross-table rules (triggers)

There is no foreign key from `subscription_filter` to the `subscription` view (views cannot be referenced, and
`calendar_link` is not unique on `(calendar_id, subscriber)`), so two triggers enforce the rest:

```sql
-- A filter row needs a link for its (calendar, subscriber), and its preset must be global or the subscriber's own (FR-016).
-- Checked at commit, so regroup can copy a filter to a calendar before moving the link there.
CREATE FUNCTION subscription_filter_check() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE p bigint;
BEGIN
    SELECT preset_id INTO p FROM subscription_filter WHERE calendar_id = NEW.calendar_id AND subscriber = NEW.subscriber;
    IF NOT FOUND THEN RETURN NULL; END IF;  -- removed later in the same transaction
    PERFORM 1 FROM calendar_link WHERE calendar_id = NEW.calendar_id AND subscriber = NEW.subscriber FOR KEY SHARE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'subscription_filter (%, %) has no link', NEW.calendar_id, NEW.subscriber USING ERRCODE = '23503';
    END IF;
    IF p IS NOT NULL AND NOT EXISTS (SELECT 1 FROM preset WHERE id = p AND (owner IS NULL OR owner = NEW.subscriber)) THEN
        RAISE EXCEPTION 'preset % is not visible to %', p, NEW.subscriber USING ERRCODE = '23503';
    END IF;
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER subscription_filter_check AFTER INSERT OR UPDATE ON subscription_filter
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION subscription_filter_check();

-- Removing or moving the last link of a (calendar, subscriber) removes its filter.
CREATE FUNCTION calendar_link_drop_filter() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    DELETE FROM subscription_filter f
    WHERE f.calendar_id = OLD.calendar_id AND f.subscriber = OLD.subscriber
      AND NOT EXISTS (SELECT 1 FROM calendar_link l WHERE l.calendar_id = OLD.calendar_id AND l.subscriber = OLD.subscriber);
    RETURN NULL;
END $$;
CREATE TRIGGER calendar_link_drop_filter AFTER DELETE OR UPDATE OF calendar_id, subscriber ON calendar_link
    FOR EACH ROW EXECUTE FUNCTION calendar_link_drop_filter();

-- A preset never changes owner, so the ownership check above cannot be bypassed afterwards.
CREATE FUNCTION preset_owner_fixed() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.owner IS DISTINCT FROM OLD.owner THEN RAISE EXCEPTION 'preset owner cannot change' USING ERRCODE = '23514'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER preset_owner_fixed BEFORE UPDATE OF owner ON preset FOR EACH ROW EXECUTE FUNCTION preset_owner_fixed();
```

The `FOR KEY SHARE` makes a concurrent removal of the last link wait until the filter write commits. Its cleanup
trigger then sees the new row, so no row is left without a link. The application still checks the subscription and
preset first (`404`); the triggers are the backstop. Deleting a calendar cascades.

### `detected_change` (extended)

| New column | Type | Constraints | Notes |
|---|---|---|---|
| `event_before` | `jsonb` | NULL | For `changed`: the event object before, `fields.details` removed. NULL for `added` and `removed`. |
| `event_after` | `jsonb` | NULL | For `changed`: the event object after, `fields.details` removed. NULL for `added` and `removed`. |

`CHECK (kind = 'changed' OR (event_before IS NULL AND event_after IS NULL))`. For `added` the event is
`new_value`, and for `removed` it is `old_value`; the reader picks the sides accordingly. The event object
shape is unchanged (001 `contracts/detected-change.md`).

## Migration `002_filters.sql`

1. Create `preset` and `subscription_filter`, with their indexes.
2. Insert the Default preset.
3. `ALTER TABLE detected_change ADD COLUMN event_before jsonb, ADD COLUMN event_after jsonb` and the CHECK.
4. Create the triggers of [§ Cross-table rules](#cross-table-rules-triggers).

Existing subscriptions need no data migration (no row = Default). No changes recorded before this migration
exist (spec Assumptions).

## Lifecycle and state transitions

A subscription's filter:

```
                 apply preset P                     edit rules
  (no row) ───────────────────▶  linked to P  ─────────────────▶  customised
   = Default ◀── remove ─────┐       ▲   │                            │
                             │       └───┴──── apply preset P ────────┘
   any state ────────────────┘   preset P deleted: linked to P ──▶ customised (P's last rules)
```

- **Edit rules** (`PUT …/filter`): upsert a row with `preset_id = NULL` and the new rules. From "no row" this
  also customises: the row holds the submitted rules, not Default's.
- **Apply preset**: upsert `preset_id = P`, `rules = NULL`. Applying Default also creates a row (equivalent to
  no row, kept for simplicity of the upsert).
- **Delete preset P**: one transaction converts its linked rows to customised with P's rules, then deletes P
  (research R7).
- **Remove subscription**: the `calendar_link_drop_filter` trigger deletes the row; a later re-subscribe starts on
  Default.

## Outcome evaluation inputs

For each recorded change, `FilterEngine` receives:

| Input | Source |
|---|---|
| kind, field, old/new value | `detected_change` |
| event name | `event_name` |
| detection time | `detected_at` |
| event before / after | `event_before` / `event_after`, or `old_value` / `new_value` for removed / added |
| rules | the subscription's filter: `subscription_filter.rules`, else its preset's `rules`, else Default's |
| active conditions | `settings.FeaturesFor(calendar.host)` |

The read path loads the subscriber's filters once (one query joining `calendar_link`, `calendar`,
`subscription_filter` and `preset`) and evaluates in memory (research R3).

## Personal and sensitive data (Principle II)

| Field | Why it is needed | Protection |
|---|---|---|
| `preset.owner`, `subscription_filter.subscriber` | Scopes a private preset or filter to its subscriber (FR-016) | The same opaque id already stored in `calendar_link.subscriber`. No name or email. |
| `preset.name`, rule text and values | The subscriber's own filter definition | Deleted with the preset, or with the subscription's filter row. Escaped on output by the frontend. |
| `detected_change.event_before/after` | Evaluate event conditions as they were at detection (FR-005) | Same data and retention as the existing change and snapshot columns. Deleted with the calendar or at retention. |

## Validation rules (application side, on top of the constraints)

See [contracts/rules.md § Validation](./contracts/rules.md#validation) for the rule checks and limits
(FR-024, FR-025). Name uniqueness, name length and the 50-rule cap are also constraints; the 50-per-subscriber
and 100-global preset caps are application checks (research R7).
