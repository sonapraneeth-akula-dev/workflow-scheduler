BEGIN;

-- Workflow registry foreign keys follow when workflow tables exist.
CREATE TABLE schedule_config_versions (
    id uuid PRIMARY KEY,
    workflow_id text NOT NULL CHECK (workflow_id ~ '[^[:space:]]'),
    source_path text NOT NULL CHECK (source_path ~ '[^[:space:]]'),
    git_commit text NOT NULL CHECK (git_commit ~ '^[0-9a-f]{40}([0-9a-f]{24})?$'),
    content_sha256 bytea NOT NULL CHECK (octet_length(content_sha256) = 32),
    git_author text NOT NULL CHECK (git_author ~ '[^[:space:]]'),
    registered_by text NOT NULL CHECK (registered_by ~ '[^[:space:]]'),
    registered_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (workflow_id, content_sha256),
    UNIQUE (workflow_id, id)
);

ALTER TABLE schedules ADD COLUMN config_version_id uuid;
ALTER TABLE schedules ADD CONSTRAINT schedules_config_workflow
    FOREIGN KEY (workflow_id, config_version_id) REFERENCES schedule_config_versions (workflow_id, id);
ALTER TABLE schedules ADD CONSTRAINT schedules_code_config
    CHECK (config_version_id IS NULL OR origin = 0);
CREATE INDEX schedules_config_version ON schedules (config_version_id) WHERE config_version_id IS NOT NULL;

COMMIT;
