BEGIN;

CREATE TABLE retention_blob_deletions (
    kind smallint NOT NULL CHECK (kind IN (0,1)),
    object_id text NOT NULL,
    queued_at timestamptz NOT NULL DEFAULT now(),
    attempts int NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    last_error text CHECK (last_error IS NULL OR last_error='blob_delete_failed'),
    PRIMARY KEY(kind,object_id),
    CHECK ((kind=0 AND object_id ~ '^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$')
        OR (kind=1 AND object_id ~ '^[0-9a-f]{64}$'))
);

CREATE INDEX ON retention_blob_deletions(next_attempt_at,kind,object_id);
CREATE INDEX ON artifact_grants(sha256);
CREATE INDEX ON artifacts(sha256);

COMMIT;
