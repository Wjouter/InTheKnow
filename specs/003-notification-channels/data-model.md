# Data Model: Notification Channels

**Spec**: [spec.md](./spec.md) | **Research**: [research.md](./research.md) | **Builds on**:
[001 data model](../001-ical-change-detection/data-model.md), [002 data model](../002-subscriber-change-filters/data-model.md)

Every integrity rule that the database can express is a constraint. The migration is `003_notifications.sql`.
Enumerated values are camelCase, the same in the database and the API, as 002's outcomes are.

## Spec entity → storage mapping

| Spec entity | Storage |
|---|---|
| Channel, email address | table `channel` (the address is `target`) |
| Declined address | table `declined_address` |
| Notification language, time zone | table `subscriber_setting` (no row means English and the operator default) |
| Subscription: notification switch, label | table `subscription_setting` (no row means on, no label) |
| Feed calendar name | new column `calendar.feed_name` |
| Confirmation, test notification | table `channel_message` (a queue entry that also serves as the rate-limit log) |
| Delivery | table `delivery` |
| Message (change digest) | not stored. It is assembled at send time from pending deliveries (research R5). |
| Channel type, its operator status | code (`IChannelType`, [contracts/channel-type.md](./contracts/channel-type.md)); the status is in memory (research R10) |

## Tables

### `channel`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `bigint` | PK, identity | |
| `subscriber` | `text` | NOT NULL, `CHECK (subscriber ~ '^[A-Za-z0-9._@-]{1,100}$')` | Same pattern as `calendar_link.subscriber`. No FK: a subscriber exists through their rows (spec Assumptions). |
| `type` | `text` | NOT NULL, `CHECK (type ~ '^[a-z][a-z0-9-]{0,30}$')` | `email`. A pattern, not a list, so the test-only type needs no schema change. |
| `target` | `text` | NOT NULL, `CHECK (length(target) BETWEEN 1 AND 254)` | The email address as entered (trimmed). **Personal data.** |
| `status` | `text` | NOT NULL, `CHECK (status IN ('awaitingConfirmation','active','paused','unsubscribed','failing'))` | FR-001 |
| `status_since` | `timestamptz` | NOT NULL | App clock. Used for FR-029a. |
| `failure_reason` | `text` | NULL, `CHECK ((status = 'failing') = (failure_reason IS NOT NULL))` | A code such as `smtp_550` (research R10) |
| `activated_at` | `timestamptz` | NULL, `CHECK (status <> 'active' OR activated_at IS NOT NULL)` | The latest move to `active` |
| `last_message_at` | `timestamptz` | NULL | The latest successful send of any message (FR-028) |
| `unsubscribe_token` | `text` | NOT NULL, UNIQUE, `CHECK (length(unsubscribe_token) = 43)` | base64url of 32 random bytes, stored as is (research R7) |
| `failed_attempts` | `integer` | NOT NULL, default `0`, `CHECK (failed_attempts >= 0)` | Per-channel temporary failures in a row |
| `next_attempt_at` | `timestamptz` | NULL | Backoff (research R4) |
| `created_at` | `timestamptz` | NOT NULL | App clock |

Indexes:
- `UNIQUE (subscriber, type, lower(target))`: the same address cannot be added twice by one subscriber (FR-004).
  Other subscribers can each add it.
- `(subscriber)`, for listing.

The cap of 5 per subscriber is the `channel_limit_check` trigger (§ Cross-table rules).

### `channel_message`

Confirmation and test messages. A row is a queue entry until `sent_at` is set, and it stays as the log that the
3/24 h and 5/24 h caps count.

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `id` | `bigint` | PK, identity | |
| `channel_id` | `bigint` | NOT NULL, FK → `channel(id)` ON DELETE CASCADE | |
| `kind` | `text` | NOT NULL, `CHECK (kind IN ('confirmation','test'))` | |
| `requested_at` | `timestamptz` | NOT NULL | App clock. The caps count rows with `requested_at > now − 24 h`. |
| `sent_at` | `timestamptz` | NULL | |
| `language` | `text` | NULL, `CHECK (language IN ('en','nl'))` | Set when sent. The confirmation pages use it (FR-017). |
| `token_hash` | `bytea` | NULL, UNIQUE, `CHECK (token_hash IS NULL OR (kind = 'confirmation' AND length(token_hash) = 32))` | SHA-256 of the confirmation token |
| `expires_at` | `timestamptz` | NULL | `sent_at + 48 h` for a confirmation (FR-005) |
| `used_at` | `timestamptz` | NULL | Set by a successful confirm |
| | | `CHECK ((sent_at IS NULL) = (language IS NULL))`, `CHECK (kind = 'confirmation' OR (expires_at IS NULL AND used_at IS NULL))`, `CHECK (kind <> 'confirmation' OR ((sent_at IS NULL) = (token_hash IS NULL) AND (sent_at IS NULL) = (expires_at IS NULL)))` | A sent confirmation always has a token and an expiry (FR-005) |

Indexes: `(channel_id, requested_at)` and a partial index `(requested_at) WHERE sent_at IS NULL`, for the queue.

### `delivery`

One row per recorded change and per channel it was due for when it was recorded (research R1).

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `channel_id` | `bigint` | NOT NULL, FK → `channel(id)` ON DELETE CASCADE | FR-029: removed with the channel |
| `change_id` | `bigint` | NOT NULL, FK → `detected_change(id)` ON DELETE CASCADE | Removed with the change (retention, calendar deletion) |
| `status` | `text` | NOT NULL, default `'pending'`, `CHECK (status IN ('pending','sent','skipped','expired'))` | FR-024 |
| `reason` | `text` | NULL, `CHECK ((status = 'skipped') = (reason IS NOT NULL))`, `CHECK (reason IN ('hidden','channelNotActive','notificationsOff','subscriptionRemoved'))` | FR-012 |
| `status_at` | `timestamptz` | NULL | When the status was reached. NULL while pending. App clock, except from triggers (database `now()`, informational only). |
| | | PK `(channel_id, change_id)` | |

Indexes:
- `(change_id)`: retention and calendar deletion cascade through it.
- A partial index `(channel_id, change_id) WHERE status = 'pending'`, used to find due channels and to lock one
  channel's pending rows.

The detection time that the window and expiry use comes from joining `detected_change.detected_at` (research R4).
The pending set is usually small, but not during a mass change (about 300k rows for SC-007) or a long outage. So
due channels are fetched in batches of 100 per query (research R6), not one query per message.

### `subscriber_setting`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `subscriber` | `text` | PK, same pattern `CHECK` | |
| `language` | `text` | NOT NULL, default `'en'`, `CHECK (language IN ('en','nl'))` | FR-017 |
| `time_zone` | `text` | NULL, `CHECK (time_zone IS NULL OR length(time_zone) BETWEEN 1 AND 100)` | Canonical IANA id. NULL means `DefaultTimeZone` (FR-018a). IANA validity is checked by the application (research R9). |

No row means English and the default time zone. Removing a subscriber's last subscription leaves the row in place
(FR-029).

### `subscription_setting`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `calendar_id` | `bigint` | NOT NULL, FK → `calendar(id)` ON DELETE CASCADE | |
| `subscriber` | `text` | NOT NULL, same pattern `CHECK` | |
| `notify` | `boolean` | NOT NULL, default `true` | FR-008 |
| `label` | `text` | NULL, `CHECK (label IS NULL OR (length(label) BETWEEN 1 AND 100 AND label !~ '[[:cntrl:]]'))` | FR-019a, research R9 |
| | | PK `(calendar_id, subscriber)` | |

**No row means notifications on and no label**, so existing subscriptions need no backfill (FR-008). This is the
same pattern as 002's `subscription_filter`, with the same cross-table rules (below).

### `declined_address`

| Column | Type | Constraints | Notes |
|---|---|---|---|
| `subscriber` | `text` | NOT NULL, same pattern `CHECK` | |
| `address_hash` | `bytea` | NOT NULL, `CHECK (length(address_hash) = 32)` | `SHA-256(subscriber + "\n" + lower(address))` (research R7) |
| `created_at` | `timestamptz` | NOT NULL | |
| | | PK `(subscriber, address_hash)` | |

It is kept when the channel is removed or cleaned up (FR-006, FR-029a).

### `calendar` (extended)

| New column | Type | Constraints | Notes |
|---|---|---|---|
| `feed_name` | `text` | NULL, `CHECK (feed_name IS NULL OR (length(feed_name) BETWEEN 1 AND 100 AND feed_name !~ '[[:cntrl:]]'))` | `X-WR-CALNAME` from the latest successful check, cleaned (research R9). Never compared (FR-019a). |

### `detected_change` (changed)

`notified_at` is dropped. `delivery` replaces it (spec Assumptions).

## Cross-table rules (triggers)

```sql
-- A subscription_setting row needs a link for its (calendar, subscriber). Same as 002's subscription_filter_check.
CREATE FUNCTION subscription_setting_check() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM 1 FROM subscription_setting WHERE calendar_id = NEW.calendar_id AND subscriber = NEW.subscriber;
    IF NOT FOUND THEN RETURN NULL; END IF;  -- removed later in the same transaction
    PERFORM 1 FROM calendar_link WHERE calendar_id = NEW.calendar_id AND subscriber = NEW.subscriber FOR KEY SHARE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'subscription_setting (%, %) has no link', NEW.calendar_id, NEW.subscriber USING ERRCODE = '23503';
    END IF;
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER subscription_setting_check AFTER INSERT OR UPDATE ON subscription_setting
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION subscription_setting_check();

-- 002's "last link gone" trigger, extended: the subscription's settings go too, and its pending deliveries are skipped.
CREATE OR REPLACE FUNCTION calendar_link_drop_filter() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM calendar_link l WHERE l.calendar_id = OLD.calendar_id AND l.subscriber = OLD.subscriber) THEN
        RETURN NULL;
    END IF;
    DELETE FROM subscription_filter WHERE calendar_id = OLD.calendar_id AND subscriber = OLD.subscriber;
    DELETE FROM subscription_setting WHERE calendar_id = OLD.calendar_id AND subscriber = OLD.subscriber;
    UPDATE delivery d SET status = 'skipped', reason = 'subscriptionRemoved', status_at = now()
    FROM channel ch, detected_change c
    WHERE d.status = 'pending' AND ch.id = d.channel_id AND ch.subscriber = OLD.subscriber
      AND c.id = d.change_id AND c.calendar_id = OLD.calendar_id;
    RETURN NULL;
END $$;

-- FR-001: at most 5 channels per subscriber. The advisory lock makes concurrent adds of one subscriber
-- queue behind each other, so the count after the lock sees every committed row.
CREATE FUNCTION channel_limit_check() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('channel:' || NEW.subscriber));
    IF (SELECT count(*) FROM channel WHERE subscriber = NEW.subscriber) > 5 THEN
        RAISE EXCEPTION 'subscriber % has more than 5 channels', NEW.subscriber
            USING ERRCODE = '23514', CONSTRAINT = 'channel_limit';
    END IF;
    RETURN NULL;
END $$;
CREATE TRIGGER channel_limit_check AFTER INSERT ON channel FOR EACH ROW EXECUTE FUNCTION channel_limit_check();
```

The trigger keeps its 002 name, so the existing `CREATE TRIGGER` stays as it is. Deleting a calendar cascades to its
changes and so to their deliveries, and nothing is left to skip.

## Migration `003_notifications.sql`

1. `ALTER TABLE calendar ADD COLUMN feed_name …`. `ALTER TABLE detected_change DROP COLUMN notified_at`.
2. Create `channel`, `channel_message`, `delivery`, `subscriber_setting`, `subscription_setting` and
   `declined_address`, with their indexes.
3. Create `subscription_setting_check` and `channel_limit_check`, and replace `calendar_link_drop_filter` (above).

There is no data migration. No row in either setting table means the defaults. No channel exists before this
feature, so no delivery is owed for earlier changes.

## Channel lifecycle

```
            add ──▶ awaitingConfirmation ──confirm──▶ active ◀──resume── paused
                        │    ▲                        │  │ ▲──pause──────┘
       decline ("this   │    │ request confirmation   │  │
       wasn't me")      ▼    │ (+ confirm)            │  │ mail client unsubscribe
                    unsubscribed ◀────────────────────┘  │ (also from paused, failing)
                        ▲                                ▼
                        └──── request confirmation ── failing ◀── permanent rejection (any status)
                              + confirm → active
```

| Transition | From | Effect on pending deliveries | Other effects |
|---|---|---|---|
| Add | none | none | A confirmation is queued (`channel_message`). The status is `awaitingConfirmation`. |
| Confirm (`POST /n/confirm`) | `awaitingConfirmation`, `unsubscribed`, `failing` | none (there are none) | `active`, `activated_at`, backoff reset, `used_at` on the token |
| Decline (`POST /n/decline`) | any | `skipped (channelNotActive)` | `unsubscribed`, `declined_address` row |
| Pause | `active` | `skipped (channelNotActive)` | `paused` |
| Resume | `paused` | none | `active`, `activated_at` |
| Unsubscribe (`POST /n/unsubscribe`) | `active`, `paused`, `failing` | `skipped (channelNotActive)` | `unsubscribed` |
| Permanent rejection | any | `expired` | `failing`, `failure_reason`. Queued messages are deleted. |
| Request confirmation | `awaitingConfirmation`, `unsubscribed`, `failing` | none | Queues a confirmation. Rejected if declined or over the cap. |
| Remove | any | the rows are deleted (cascade) | The channel and its address are deleted (FR-029) |
| Cleanup (FR-029a) | `awaitingConfirmation`, `unsubscribed` | as Remove | See research R10 |

Every transition sets `status_since` to the app clock. An illegal transition through the API gets `409`
`invalid_status` ([contracts/api.md](./contracts/api.md)).

A subscription's notification switch:
- **Off**: its pending deliveries on every channel of the subscriber become `skipped (notificationsOff)`. Changes
  recorded while it is off get no row (research R1).
- **On**: nothing is created backwards.

## Delivery lifecycle

```
pending ──▶ sent        (in a sent message, including those beyond the 50 listed, FR-015)
        ──▶ skipped     (hidden at send time, or a state change above: channelNotActive / notificationsOff / subscriptionRemoved)
        ──▶ expired     (older than RetryWindow, or its channel was permanently rejected)
```

A delivery leaves `pending` once, and nothing sets it back (FR-012).

## Personal and sensitive data (Principle II)

| Field | Why it is needed | Protection |
|---|---|---|
| `channel.target` (email address) | Delivering notifications to the student (spec Key Entities) | Deleted with its channel. Unused channels are deleted after 30 days (FR-029a). Never logged (FR-031). Never in another subscriber's email or response. |
| `declined_address.address_hash` | Stopping repeat confirmation emails after "this wasn't me" (FR-006) | A hash salted with the subscriber id, never the address. Kept on purpose after removal. |
| `channel.unsubscribe_token` | Putting the one-click unsubscribe link in every email (FR-010) | Only opts out one channel. Never logged. Stored as is so asynchronous sends can build it (research R7). |
| `channel_message.token_hash` | Single-use proof of consent (FR-005, FR-007) | Only the SHA-256 is stored. The raw token exists only in the email. |
| `subscriber_setting.language`, `.time_zone` | Writing messages the student can read, in their local time (FR-017, FR-018a) | A choice, not identifying |
| `subscription_setting.label` | Naming a calendar in emails (FR-019a) | Shown only to that subscriber. Deleted with the subscription. |
| `calendar.feed_name` | Fallback calendar name (FR-019a) | From the feed, the same data class as the stored events |
| `delivery` rows | Delivery status and at-most-once (FR-024, FR-025) | Ids and statuses only. Removed with the change or the channel. |
