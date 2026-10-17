BEGIN;

ALTER TABLE task_attempts ADD COLUMN resumed_from_checkpoint_at timestamptz
    CHECK (resumed_from_checkpoint_at IS NULL OR
        (attempt > 1 AND resumed_from_checkpoint_at >= timestamp with time zone '1970-01-01'));

COMMIT;
