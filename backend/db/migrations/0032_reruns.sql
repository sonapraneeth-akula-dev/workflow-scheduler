BEGIN;

-- Lineage remains meaningful after the parent's retention period expires.
ALTER TABLE runs ADD COLUMN parent_run_id uuid;
ALTER TABLE runs ADD CONSTRAINT runs_parent_run_id_check CHECK
    (parent_run_id IS NULL OR (parent_run_id <> id AND (get_byte(uuid_send(parent_run_id),6) >> 4) = 7));
CREATE INDEX runs_parent_run_id_idx ON runs(parent_run_id) WHERE parent_run_id IS NOT NULL;

COMMIT;
