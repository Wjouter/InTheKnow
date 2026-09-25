CREATE TABLE preset (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    owner text NULL CHECK (owner IS NULL OR owner ~ '^[A-Za-z0-9._@-]{1,100}$'),
    name text NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    rules jsonb NOT NULL CHECK (jsonb_typeof(rules) = 'array' AND jsonb_array_length(rules) <= 50),
    is_default boolean NOT NULL DEFAULT false CHECK (NOT is_default OR owner IS NULL),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE NULLS NOT DISTINCT (owner, name)
);
CREATE UNIQUE INDEX ON preset ((true)) WHERE is_default;
CREATE INDEX ON preset (owner);

CREATE TABLE subscription_filter (
    calendar_id bigint NOT NULL REFERENCES calendar (id) ON DELETE CASCADE,
    subscriber text NOT NULL CHECK (subscriber ~ '^[A-Za-z0-9._@-]{1,100}$'),
    preset_id bigint NULL REFERENCES preset (id) ON DELETE RESTRICT,
    rules jsonb NULL CHECK (rules IS NULL OR (jsonb_typeof(rules) = 'array' AND jsonb_array_length(rules) <= 50)),
    PRIMARY KEY (calendar_id, subscriber),
    CONSTRAINT subscription_filter_linked_or_custom CHECK ((preset_id IS NULL) <> (rules IS NULL))
);
CREATE INDEX ON subscription_filter (preset_id);

INSERT INTO preset (owner, name, is_default, rules) VALUES (NULL, 'Default', true, '[
    { "outcome": "hide", "when": { "eventEnded": true } },
    { "outcome": "hide", "when": { "changeKind": ["location"], "activityType": ["holiday"] } },
    { "outcome": "hide", "when": { "changeKind": ["activityType", "groups"] } },
    { "outcome": "lowPriority", "when": { "statusFrom": "tentative", "statusTo": "confirmed" } }
]');

ALTER TABLE detected_change ADD COLUMN event_before jsonb NULL, ADD COLUMN event_after jsonb NULL,
    ADD CHECK (kind = 'changed' OR (event_before IS NULL AND event_after IS NULL));

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
