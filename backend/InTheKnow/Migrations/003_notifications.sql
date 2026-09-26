ALTER TABLE calendar ADD COLUMN feed_name text NULL
    CHECK (feed_name IS NULL OR (length(feed_name) BETWEEN 1 AND 100 AND feed_name !~ '[[:cntrl:]]'));
ALTER TABLE detected_change DROP COLUMN notified_at;

CREATE TABLE channel (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    subscriber text NOT NULL CHECK (subscriber ~ '^[A-Za-z0-9._@-]{1,100}$'),
    type text NOT NULL CHECK (type ~ '^[a-z][a-z0-9-]{0,30}$'),
    target text NOT NULL CHECK (length(target) BETWEEN 1 AND 254),
    status text NOT NULL CHECK (status IN ('awaitingConfirmation', 'active', 'paused', 'unsubscribed', 'failing')),
    status_since timestamptz NOT NULL,
    failure_reason text NULL CHECK ((status = 'failing') = (failure_reason IS NOT NULL)),
    activated_at timestamptz NULL CHECK (status <> 'active' OR activated_at IS NOT NULL),
    last_message_at timestamptz NULL,
    unsubscribe_token text NOT NULL UNIQUE CHECK (length(unsubscribe_token) = 43),
    failed_attempts integer NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
    next_attempt_at timestamptz NULL,
    created_at timestamptz NOT NULL
);
CREATE UNIQUE INDEX ON channel (subscriber, type, lower(target));
CREATE INDEX ON channel (subscriber);

CREATE TABLE channel_message (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    channel_id bigint NOT NULL REFERENCES channel (id) ON DELETE CASCADE,
    kind text NOT NULL CHECK (kind IN ('confirmation', 'test')),
    requested_at timestamptz NOT NULL,
    sent_at timestamptz NULL,
    language text NULL CHECK (language IN ('en', 'nl')),
    token_hash bytea NULL UNIQUE CHECK (token_hash IS NULL OR (kind = 'confirmation' AND length(token_hash) = 32)),
    expires_at timestamptz NULL,
    used_at timestamptz NULL,
    CHECK ((sent_at IS NULL) = (language IS NULL)),
    CHECK (kind = 'confirmation' OR (expires_at IS NULL AND used_at IS NULL)),
    CHECK (kind <> 'confirmation' OR ((sent_at IS NULL) = (token_hash IS NULL) AND (sent_at IS NULL) = (expires_at IS NULL)))
);
CREATE INDEX ON channel_message (channel_id, requested_at);
CREATE INDEX ON channel_message (requested_at) WHERE sent_at IS NULL;

CREATE TABLE delivery (
    channel_id bigint NOT NULL REFERENCES channel (id) ON DELETE CASCADE,
    change_id bigint NOT NULL REFERENCES detected_change (id) ON DELETE CASCADE,
    status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'sent', 'skipped', 'expired')),
    reason text NULL CHECK (reason IN ('hidden', 'channelNotActive', 'notificationsOff', 'subscriptionRemoved')),
    status_at timestamptz NULL,
    CHECK ((status = 'skipped') = (reason IS NOT NULL)),
    PRIMARY KEY (channel_id, change_id)
);
CREATE INDEX ON delivery (change_id);
CREATE INDEX ON delivery (channel_id, change_id) WHERE status = 'pending';

CREATE TABLE subscriber_setting (
    subscriber text PRIMARY KEY CHECK (subscriber ~ '^[A-Za-z0-9._@-]{1,100}$'),
    language text NOT NULL DEFAULT 'en' CHECK (language IN ('en', 'nl')),
    time_zone text NULL CHECK (time_zone IS NULL OR length(time_zone) BETWEEN 1 AND 100)
);

CREATE TABLE subscription_setting (
    calendar_id bigint NOT NULL REFERENCES calendar (id) ON DELETE CASCADE,
    subscriber text NOT NULL CHECK (subscriber ~ '^[A-Za-z0-9._@-]{1,100}$'),
    notify boolean NOT NULL DEFAULT true,
    label text NULL CHECK (label IS NULL OR (length(label) BETWEEN 1 AND 100 AND label !~ '[[:cntrl:]]')),
    PRIMARY KEY (calendar_id, subscriber)
);

CREATE TABLE declined_address (
    subscriber text NOT NULL CHECK (subscriber ~ '^[A-Za-z0-9._@-]{1,100}$'),
    address_hash bytea NOT NULL CHECK (length(address_hash) = 32),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (subscriber, address_hash)
);

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
