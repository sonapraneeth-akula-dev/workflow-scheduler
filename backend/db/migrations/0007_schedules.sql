BEGIN;

-- Workflow foreign keys follow the registry migration; code config versions follow in US-029.
-- origin: Code=0, User=1; overlap: Skip=0, Queue=1, Concurrent=2; catchup: None=0, Latest=1.
CREATE TABLE schedules (
    id uuid PRIMARY KEY,
    workflow_id text NOT NULL CHECK (workflow_id ~ '[^[:space:]]'),
    origin smallint NOT NULL CHECK (origin IN (0, 1)),
    code_key text,
    name text NOT NULL CHECK (name ~ '[^[:space:]]'),
    timezone text NOT NULL CHECK (timezone ~ '[^[:space:]]'),
    cron text CHECK (cron IS NULL OR cron ~ '[^[:space:]]'),
    interval_s int CHECK (interval_s > 0),
    variables jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(variables) = 'object'),
    version_pin text CHECK (version_pin IS NULL OR version_pin ~ '[^[:space:]]'),
    overlap_policy smallint NOT NULL DEFAULT 0 CHECK (overlap_policy BETWEEN 0 AND 2),
    catchup_policy smallint NOT NULL DEFAULT 1 CHECK (catchup_policy IN (0, 1)),
    paused bool NOT NULL DEFAULT false,
    next_fire_at timestamptz,
    row_version int NOT NULL DEFAULT 1 CHECK (row_version > 0),
    created_by text NOT NULL CHECK (created_by ~ '[^[:space:]]'),
    updated_by text NOT NULL CHECK (updated_by ~ '[^[:space:]]'),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    deleted_at timestamptz,
    CHECK ((cron IS NULL) <> (interval_s IS NULL)),
    CHECK ((origin = 0 AND code_key IS NOT NULL AND code_key ~ '[^[:space:]]')
        OR (origin = 1 AND code_key IS NULL))
);

CREATE UNIQUE INDEX schedules_code_key ON schedules (workflow_id, code_key) WHERE origin = 0;
CREATE INDEX schedules_next_fire ON schedules (next_fire_at, id) WHERE NOT paused AND deleted_at IS NULL;
CREATE INDEX schedules_workflow ON schedules (workflow_id, id) WHERE deleted_at IS NULL;

COMMIT;
