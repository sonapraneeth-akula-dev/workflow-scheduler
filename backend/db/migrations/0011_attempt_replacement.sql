BEGIN;

ALTER TABLE task_attempts
    ADD COLUMN dispatch_payload bytea,
    ADD COLUMN placement_request jsonb,
    ADD COLUMN execution_policy jsonb,
    ADD COLUMN platform_retries smallint NOT NULL DEFAULT 0 CHECK (platform_retries BETWEEN 0 AND 2),
    ADD COLUMN author_retries smallint NOT NULL DEFAULT 0 CHECK (author_retries >= 0),
    ADD COLUMN replacement_processed_at timestamptz,
    ADD CONSTRAINT complete_dispatch_recovery CHECK (
        (dispatch_payload IS NULL AND placement_request IS NULL AND execution_policy IS NULL) OR
        (dispatch_payload IS NOT NULL AND placement_request IS NOT NULL AND execution_policy IS NOT NULL)),
    ADD CONSTRAINT dispatch_recovery_objects CHECK (
        (placement_request IS NULL OR jsonb_typeof(placement_request) = 'object') AND
        (execution_policy IS NULL OR jsonb_typeof(execution_policy) = 'object'));

CREATE INDEX ON task_attempts (run_id, task_key, attempt)
    WHERE status = 7 AND failure_category = 2 AND replacement_processed_at IS NULL;

COMMIT;
