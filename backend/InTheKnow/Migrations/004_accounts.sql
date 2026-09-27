-- 004: accounts replace subscriber ids as the owner of all data (specs/004 data-model.md, research R1).

-- 1. The rows the spec does not carry over. subscription_filter goes before preset because of its RESTRICT.
DELETE FROM delivery;
DELETE FROM channel_message;
DELETE FROM channel;
DELETE FROM declined_address;
DELETE FROM subscription_setting;
DELETE FROM subscription_filter;
DELETE FROM calendar_link;
DELETE FROM calendar;
DELETE FROM subscriber_setting;
DELETE FROM preset WHERE owner IS NOT NULL;

-- 2. New tables, in foreign key order.
CREATE TABLE account (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email text NOT NULL CHECK (length(email) BETWEEN 3 AND 254),
    confirmed_at timestamptz NULL,
    disabled_at timestamptz NULL CHECK (disabled_at IS NULL OR confirmed_at IS NOT NULL),
    role text NOT NULL DEFAULT 'user' CHECK (role IN ('user', 'admin'))
        CHECK (role = 'user' OR confirmed_at IS NOT NULL) CHECK (role = 'user' OR disabled_at IS NULL),
    language text NOT NULL CHECK (language IN ('en', 'nl')),
    time_zone text NULL CHECK (time_zone IS NULL OR length(time_zone) BETWEEN 1 AND 100),
    created_at timestamptz NOT NULL
);
CREATE UNIQUE INDEX ON account (lower(email));
CREATE INDEX ON account (created_at) WHERE confirmed_at IS NULL;

CREATE TABLE sign_in_method (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id bigint NOT NULL REFERENCES account (id) ON DELETE CASCADE,
    type text NOT NULL CHECK (type ~ '^[a-z][a-z0-9-]{0,30}$'),
    key text NULL CHECK (key IS NULL OR length(key) BETWEEN 1 AND 255),
    secret text NULL CHECK (type <> 'password' OR (secret IS NOT NULL AND key IS NULL)),
    created_at timestamptz NOT NULL,
    last_used_at timestamptz NULL
);
CREATE UNIQUE INDEX ON sign_in_method (account_id) WHERE type = 'password';
CREATE UNIQUE INDEX ON sign_in_method (type, key) WHERE key IS NOT NULL;
CREATE INDEX ON sign_in_method (account_id);

CREATE TABLE session (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id bigint NOT NULL REFERENCES account (id) ON DELETE CASCADE,
    method_id bigint NOT NULL REFERENCES sign_in_method (id) ON DELETE CASCADE,
    token_hash bytea NOT NULL UNIQUE CHECK (length(token_hash) = 32),
    device text NOT NULL CHECK (length(device) BETWEEN 1 AND 60),
    awaiting_code boolean NOT NULL DEFAULT false,
    second_factor boolean NOT NULL DEFAULT false CHECK (NOT (awaiting_code AND second_factor)),
    reauthenticated_at timestamptz NULL,
    created_at timestamptz NOT NULL,
    last_used_at timestamptz NOT NULL
);
CREATE INDEX ON session (account_id);
CREATE INDEX ON session (last_used_at);

CREATE TABLE two_factor (
    account_id bigint PRIMARY KEY REFERENCES account (id) ON DELETE CASCADE,
    secret bytea NOT NULL CHECK (length(secret) = 20),
    enabled_at timestamptz NULL,
    last_step bigint NOT NULL DEFAULT 0
);

CREATE TABLE recovery_code (
    account_id bigint NOT NULL REFERENCES two_factor (account_id) ON DELETE CASCADE,
    code_hash bytea NOT NULL CHECK (length(code_hash) = 32),
    used_at timestamptz NULL,
    PRIMARY KEY (account_id, code_hash)
);

CREATE TABLE invite (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind text NOT NULL CHECK (kind IN ('named', 'shareable')),
    email text NULL CHECK ((kind = 'named') = (email IS NOT NULL)) CHECK (email IS NULL OR length(email) BETWEEN 3 AND 254),
    language text NOT NULL CHECK (language IN ('en', 'nl')),
    grants_admin boolean NOT NULL DEFAULT false CHECK (NOT grants_admin OR kind = 'named'),
    max_uses integer NOT NULL CHECK (max_uses BETWEEN 1 AND 1000) CHECK (kind = 'shareable' OR max_uses = 1),
    used integer NOT NULL DEFAULT 0 CHECK (used BETWEEN 0 AND max_uses),
    token_hash bytea NULL UNIQUE CHECK (token_hash IS NULL OR length(token_hash) = 32) CHECK (kind = 'named' OR token_hash IS NOT NULL),
    created_by bigint NULL REFERENCES account (id) ON DELETE SET NULL,
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > created_at AND expires_at <= created_at + interval '90 days')
);
CREATE UNIQUE INDEX ON invite (lower(email)) WHERE kind = 'named';

CREATE TABLE account_message (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind text NOT NULL CHECK (kind IN ('confirm', 'signUpAttempt', 'reset', 'passwordChanged', 'emailChange', 'emailChangeNotice',
        'twoFactorOn', 'twoFactorOff', 'twoFactorOffByAdmin', 'recoveryCodeUsed', 'methodAdded', 'methodRemoved', 'roleChanged',
        'disabled', 'enabled', 'deleted', 'invite')),
    account_id bigint NULL REFERENCES account (id) ON DELETE CASCADE CHECK (account_id IS NOT NULL OR kind IN ('deleted', 'invite')),
    invite_id bigint NULL REFERENCES invite (id) ON DELETE CASCADE CHECK ((kind = 'invite') = (invite_id IS NOT NULL)),
    to_address text NOT NULL CHECK (length(to_address) BETWEEN 3 AND 254),
    language text NOT NULL CHECK (language IN ('en', 'nl')),
    args jsonb NOT NULL DEFAULT '{}' CHECK (jsonb_typeof(args) = 'object'),
    requested_at timestamptz NOT NULL,
    sent_at timestamptz NULL,
    expired_at timestamptz NULL CHECK (sent_at IS NULL OR expired_at IS NULL),
    token_hash bytea NULL UNIQUE
        CHECK (token_hash IS NULL OR (kind IN ('confirm', 'reset', 'emailChange') AND length(token_hash) = 32))
        CHECK (kind NOT IN ('confirm', 'reset', 'emailChange') OR (sent_at IS NULL) = (token_hash IS NULL)),
    token_expires_at timestamptz NULL CHECK ((token_hash IS NULL) = (token_expires_at IS NULL)),
    used_at timestamptz NULL CHECK (used_at IS NULL OR token_hash IS NOT NULL)
);
CREATE INDEX ON account_message (requested_at) WHERE sent_at IS NULL AND expired_at IS NULL;
CREATE INDEX ON account_message (account_id, kind, requested_at);
CREATE INDEX ON account_message (lower(to_address), kind, requested_at);

CREATE TABLE signup_settings (
    id boolean PRIMARY KEY DEFAULT true CHECK (id),
    open boolean NOT NULL,
    allowed_domains text[] NOT NULL CHECK (cardinality(allowed_domains) <= 100)
        CHECK (allowed_domains = lower(allowed_domains::text)::text[])
);
INSERT INTO signup_settings (id, open, allowed_domains) VALUES (true, false, '{}');

-- No foreign keys: events outlive the account (FR-035).
CREATE TABLE security_event (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at timestamptz NOT NULL,
    type text NOT NULL CHECK (type ~ '^[a-z][a-zA-Z]{1,40}$'),
    account_id bigint NULL,
    actor_id bigint NULL,
    source inet NULL,
    detail jsonb NOT NULL DEFAULT '{}'
);
CREATE INDEX ON security_event (occurred_at);

-- 3. Owner columns become account foreign keys. The view and the two triggers whose UPDATE OF list names an owner
-- column block the type change, so they are dropped and recreated unchanged.
DROP VIEW subscription;
DROP TRIGGER calendar_link_drop_filter ON calendar_link;
DROP TRIGGER preset_owner_fixed ON preset;

ALTER TABLE calendar_link DROP CONSTRAINT calendar_link_subscriber_check,
    ALTER COLUMN subscriber TYPE bigint USING NULL, ADD FOREIGN KEY (subscriber) REFERENCES account (id) ON DELETE CASCADE;
ALTER TABLE subscription_filter DROP CONSTRAINT subscription_filter_subscriber_check,
    ALTER COLUMN subscriber TYPE bigint USING NULL, ADD FOREIGN KEY (subscriber) REFERENCES account (id) ON DELETE CASCADE;
ALTER TABLE channel DROP CONSTRAINT channel_subscriber_check,
    ALTER COLUMN subscriber TYPE bigint USING NULL, ADD FOREIGN KEY (subscriber) REFERENCES account (id) ON DELETE CASCADE;
ALTER TABLE subscription_setting DROP CONSTRAINT subscription_setting_subscriber_check,
    ALTER COLUMN subscriber TYPE bigint USING NULL, ADD FOREIGN KEY (subscriber) REFERENCES account (id) ON DELETE CASCADE;
ALTER TABLE declined_address DROP CONSTRAINT declined_address_subscriber_check,
    ALTER COLUMN subscriber TYPE bigint USING NULL, ADD FOREIGN KEY (subscriber) REFERENCES account (id) ON DELETE CASCADE;
ALTER TABLE preset DROP CONSTRAINT preset_owner_check,
    ALTER COLUMN owner TYPE bigint USING NULL, ADD FOREIGN KEY (owner) REFERENCES account (id) ON DELETE CASCADE;

-- 4. Recreate and extend.
CREATE VIEW subscription AS SELECT DISTINCT calendar_id, subscriber FROM calendar_link;
CREATE TRIGGER calendar_link_drop_filter AFTER DELETE OR UPDATE OF calendar_id, subscriber ON calendar_link
    FOR EACH ROW EXECUTE FUNCTION calendar_link_drop_filter();
CREATE TRIGGER preset_owner_fixed BEFORE UPDATE OF owner ON preset FOR EACH ROW EXECUTE FUNCTION preset_owner_fixed();

-- NO ACTION, checked at the end of the statement: deleting an account cascades to presets and filters in either order.
ALTER TABLE subscription_filter DROP CONSTRAINT subscription_filter_preset_id_fkey,
    ADD FOREIGN KEY (preset_id) REFERENCES preset (id);

ALTER TABLE channel ADD COLUMN is_account_address boolean NOT NULL DEFAULT false CHECK (NOT is_account_address OR type = 'email');
CREATE UNIQUE INDEX ON channel (subscriber) WHERE is_account_address;

ALTER TABLE delivery DROP CONSTRAINT delivery_reason_check,
    ADD CHECK (reason IN ('hidden', 'channelNotActive', 'notificationsOff', 'subscriptionRemoved', 'accountDisabled'));

DROP TABLE subscriber_setting;

-- 5. Cross-table rules. Each takes an advisory lock, so concurrent removals queue and the check after it sees the
-- committed rows (both functions stay VOLATILE: every statement gets a fresh snapshot).

-- FR-027a, SC-014: an existing account keeps at least one sign-in method. Checked at commit, so a takeover can
-- delete and insert in one transaction.
CREATE FUNCTION sign_in_method_keep_one() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('method:' || OLD.account_id));
    IF EXISTS (SELECT 1 FROM account WHERE id = OLD.account_id)
       AND NOT EXISTS (SELECT 1 FROM sign_in_method WHERE account_id = OLD.account_id) THEN
        RAISE EXCEPTION 'account % would have no sign-in method', OLD.account_id USING ERRCODE = '23514', CONSTRAINT = 'last_method';
    END IF;
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER sign_in_method_keep_one AFTER DELETE ON sign_in_method
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION sign_in_method_keep_one();

-- FR-033a: once there is an admin, the last one stays. Fires only when an admin row changes role or is deleted.
CREATE FUNCTION account_keep_admin() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('admins'));
    IF NOT EXISTS (SELECT 1 FROM account WHERE role = 'admin') THEN
        RAISE EXCEPTION 'the last admin cannot be removed' USING ERRCODE = '23514', CONSTRAINT = 'last_admin';
    END IF;
    RETURN NULL;
END $$;
CREATE TRIGGER account_keep_admin AFTER UPDATE OF role OR DELETE ON account
    FOR EACH ROW WHEN (OLD.role = 'admin') EXECUTE FUNCTION account_keep_admin();
