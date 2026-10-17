BEGIN;

ALTER TABLE runs ADD COLUMN placement jsonb;
ALTER TABLE runs ADD CONSTRAINT runs_placement_object CHECK (
    placement IS NULL OR jsonb_typeof(placement) = 'object');

COMMIT;
