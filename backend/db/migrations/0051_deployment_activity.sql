BEGIN;

CREATE TABLE deployment_operations (
    id uuid PRIMARY KEY,
    mode text NOT NULL CHECK (mode IN ('Stage', 'Release')),
    target text NOT NULL CHECK (target ~ '^[a-z0-9][a-z0-9_.-]{0,100}$'),
    actor uuid NOT NULL,
    actor_kind text NOT NULL CHECK (actor_kind IN ('User', 'App')),
    started_at timestamptz NOT NULL,
    last_reported_at timestamptz NOT NULL,
    status text NOT NULL DEFAULT 'Running' CHECK (status IN ('Running', 'Succeeded', 'Failed')),
    sequence int NOT NULL DEFAULT 0 CHECK (sequence BETWEEN 0 AND 128)
);
CREATE INDEX deployment_operations_recent ON deployment_operations(started_at DESC, id DESC);

CREATE TABLE deployment_activity (
    operation_id uuid NOT NULL REFERENCES deployment_operations(id) ON DELETE CASCADE,
    sequence int NOT NULL CHECK (sequence BETWEEN 1 AND 128),
    at timestamptz NOT NULL,
    phase text NOT NULL CHECK (phase IN ('Preflight','Build','Import','Configure','Migrate','Rollout',
        'Controller','Drain','Replace','Resume','Verify','Complete','Failed')),
    subject text CHECK (char_length(subject) BETWEEN 1 AND 200),
    outcome text NOT NULL CHECK (outcome IN ('Running','Succeeded','Failed')),
    description text NOT NULL CHECK (char_length(description) BETWEEN 1 AND 512),
    PRIMARY KEY(operation_id, sequence)
);

COMMIT;
