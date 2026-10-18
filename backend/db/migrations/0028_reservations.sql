BEGIN;

CREATE TABLE reservations (
    id uuid PRIMARY KEY CHECK (get_byte(uuid_send(id), 6) >> 4 = 7),
    run_day date NOT NULL,
    run_id uuid NOT NULL,
    node_key text NOT NULL,
    agent_id uuid NOT NULL REFERENCES agents(id),
    epoch bigint GENERATED ALWAYS AS IDENTITY (START WITH 2) UNIQUE CHECK (epoch > 1),
    selector jsonb NOT NULL CHECK (jsonb_typeof(selector) = 'object'),
    created_at timestamptz NOT NULL,
    renewed_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    released_at timestamptz,
    release_reason text,
    FOREIGN KEY (run_day, run_id, node_key) REFERENCES node_runs(run_day, run_id, node_key),
    CHECK (run_day = uuid_v7_run_day(run_id)),
    CHECK (renewed_at >= created_at AND expires_at > renewed_at),
    CHECK ((released_at IS NULL) = (release_reason IS NULL)),
    CHECK (released_at IS NULL OR released_at >= created_at),
    CHECK (release_reason IS NULL OR length(btrim(release_reason)) BETWEEN 1 AND 500)
);

-- Expiry alone must not free a machine before its old work is cancelled and fenced.
CREATE UNIQUE INDEX reservations_active_agent ON reservations(agent_id) WHERE released_at IS NULL;
CREATE INDEX reservations_active_node ON reservations(run_day, run_id, node_key) WHERE released_at IS NULL;
CREATE INDEX reservations_expiry ON reservations(expires_at) WHERE released_at IS NULL;

COMMIT;
