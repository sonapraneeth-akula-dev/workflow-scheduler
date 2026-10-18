BEGIN;

-- Keep legacy provenance rows valid; new registrations retain the exact file, not normalized JSON.
ALTER TABLE schedule_config_versions ADD COLUMN content text;
ALTER TABLE schedule_config_versions ADD CONSTRAINT schedule_config_content_hash
    CHECK (content IS NULL OR sha256(convert_to(content,'UTF8')) = content_sha256);
ALTER TABLE workflows ADD COLUMN active_schedule_config_id uuid;
ALTER TABLE workflows ADD CONSTRAINT workflows_active_schedule_config
    FOREIGN KEY(id,active_schedule_config_id) REFERENCES schedule_config_versions(workflow_id,id);

ALTER TABLE schedules ADD CONSTRAINT schedules_workflow_identity UNIQUE(workflow_id,id);
CREATE TABLE run_series (
    id uuid PRIMARY KEY,
    workflow_id text NOT NULL,
    schedule_id uuid NOT NULL UNIQUE,
    FOREIGN KEY(workflow_id,schedule_id) REFERENCES schedules(workflow_id,id)
);
INSERT INTO run_series(id,workflow_id,schedule_id)
SELECT gen_random_uuid(),workflow_id,id FROM schedules;

COMMIT;
