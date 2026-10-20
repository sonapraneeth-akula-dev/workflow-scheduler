BEGIN;

ALTER TABLE runs ADD COLUMN triggered_by_name text;
ALTER TABLE runs ADD CONSTRAINT runs_triggered_by_name_nonblank CHECK (
    triggered_by_name IS NULL OR btrim(triggered_by_name) <> '');

COMMIT;
