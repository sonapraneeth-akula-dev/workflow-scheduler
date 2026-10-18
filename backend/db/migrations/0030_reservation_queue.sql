BEGIN;

CREATE TABLE reservation_queue (
    position bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_day date NOT NULL,
    run_id uuid NOT NULL,
    node_key text NOT NULL,
    machine_count integer NOT NULL CHECK (machine_count > 0),
    selector jsonb NOT NULL CHECK (jsonb_typeof(selector) = 'object'),
    lifetime interval NOT NULL CHECK (lifetime > interval '0'),
    queued_at timestamptz NOT NULL,
    UNIQUE(run_day, run_id, node_key),
    FOREIGN KEY (run_day, run_id, node_key) REFERENCES node_runs(run_day, run_id, node_key),
    CHECK (run_day = uuid_v7_run_day(run_id))
);

COMMIT;
