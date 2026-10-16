BEGIN;

CREATE INDEX workflow_versions_history ON workflow_versions(workflow_id,created_at DESC,id DESC);

COMMIT;
