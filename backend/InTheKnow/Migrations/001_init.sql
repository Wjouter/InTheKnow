CREATE TABLE calendar (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    identity_hash bytea NOT NULL UNIQUE CHECK (length(identity_hash) = 32),
    host text NOT NULL CHECK (host = lower(host) AND length(host) BETWEEN 1 AND 253),
    status text NOT NULL DEFAULT 'ok' CHECK (status IN ('ok', 'failing')),
    next_check_at timestamptz NOT NULL,
    lease_id uuid NULL,
    baseline_features text NULL,
    horizon_end timestamptz NULL,
    snapshot_hash bytea NULL CHECK (snapshot_hash IS NULL OR length(snapshot_hash) = 32),
    last_checked_at timestamptz NULL,
    last_link_id bigint NULL,
    last_ok boolean NULL,
    last_error text NULL CHECK (last_ok IS DISTINCT FROM false OR last_error IS NOT NULL),
    last_event_count integer NULL CHECK (last_event_count >= 0),
    last_change_count integer NULL CHECK (last_change_count >= 0),
    consecutive_failures integer NOT NULL DEFAULT 0 CHECK (consecutive_failures >= 0),
    failing_since timestamptz NULL CHECK ((consecutive_failures = 0) = (failing_since IS NULL)),
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ON calendar (next_check_at);

CREATE TABLE calendar_link (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    calendar_id bigint NOT NULL REFERENCES calendar (id) ON DELETE CASCADE,
    subscriber text NOT NULL CHECK (subscriber ~ '^[A-Za-z0-9._@-]{1,100}$'),
    url text NOT NULL CHECK (url ~ '^https://') CHECK (length(url) <= 2048),
    flagged_at timestamptz NULL,
    last_used_at timestamptz NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (calendar_id, subscriber, url)
);
CREATE INDEX ON calendar_link (subscriber, calendar_id);

ALTER TABLE calendar ADD FOREIGN KEY (last_link_id) REFERENCES calendar_link (id) ON DELETE SET NULL;
CREATE INDEX ON calendar (last_link_id);

CREATE TABLE event_snapshot (
    calendar_id bigint NOT NULL REFERENCES calendar (id) ON DELETE CASCADE,
    event_key text NOT NULL CHECK (length(event_key) BETWEEN 1 AND 1024),
    name text NOT NULL,
    starts_at timestamptz NOT NULL,
    ends_at timestamptz NOT NULL,
    status text NULL,
    occurrence_start timestamptz NULL,
    fields jsonb NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(fields) = 'object'),
    CHECK (ends_at >= starts_at),
    PRIMARY KEY (calendar_id, event_key)
);

CREATE TABLE detected_change (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    calendar_id bigint NOT NULL REFERENCES calendar (id) ON DELETE CASCADE,
    event_key text NOT NULL,
    kind text NOT NULL CHECK (kind IN ('added', 'removed', 'changed')),
    field text NULL CHECK ((kind = 'changed') = (field IS NOT NULL)),
    old_value jsonb NULL,
    new_value jsonb NULL CHECK (kind <> 'changed' OR old_value IS NOT NULL OR new_value IS NOT NULL),
    event_name text NOT NULL,
    event_starts_at timestamptz NOT NULL,
    detected_at timestamptz NOT NULL,
    notified_at timestamptz NULL,
    CHECK (kind <> 'added' OR (old_value IS NULL AND new_value IS NOT NULL)),
    CHECK (kind <> 'removed' OR (old_value IS NOT NULL AND new_value IS NULL))
);
CREATE INDEX ON detected_change (detected_at);
CREATE INDEX ON detected_change (calendar_id, id);

CREATE VIEW subscription AS SELECT DISTINCT calendar_id, subscriber FROM calendar_link;
