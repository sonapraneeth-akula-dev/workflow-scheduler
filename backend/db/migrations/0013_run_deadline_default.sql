BEGIN;

ALTER TABLE runs ALTER COLUMN deadline_at SET DEFAULT (now() + interval '24 hours');

COMMIT;
