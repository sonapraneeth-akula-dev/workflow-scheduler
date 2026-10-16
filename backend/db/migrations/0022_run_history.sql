BEGIN;

-- Capture provenance at claim time, never infer historical configuration from today's schedule.
-- Existing fires remain NULL: their original configuration is not recoverable.
ALTER TABLE schedule_fires ADD COLUMN config_version_id uuid;

CREATE INDEX runs_history_workflow_at_id_idx ON runs (workflow_id,created_at,id);
CREATE INDEX runs_history_series_at_id_idx ON runs (series_id,created_at,id) WHERE series_id IS NOT NULL;

COMMIT;
