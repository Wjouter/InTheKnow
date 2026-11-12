-- 010: a lesson's history is one index range scan (research R3).
CREATE INDEX ON detected_change (calendar_id, event_key, id);

-- 010: when the current snapshot's baseline was taken, so history can say where it starts (research R4).
-- NULL for calendars baselined before this migration: the start is unknown.
ALTER TABLE calendar ADD COLUMN baseline_at timestamptz NULL;
