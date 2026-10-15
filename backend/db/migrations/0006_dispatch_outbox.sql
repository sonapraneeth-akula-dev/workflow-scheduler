BEGIN;

ALTER TABLE task_attempts ADD COLUMN required_ram_mb bigint NOT NULL DEFAULT 0 CHECK (required_ram_mb >= 0);

CREATE TABLE dispatch_outbox (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_day date NOT NULL,
    run_id uuid NOT NULL,
    task_key text NOT NULL,
    attempt smallint NOT NULL,
    agent_id uuid NOT NULL REFERENCES agents(id),
    payload bytea NOT NULL,
    UNIQUE (run_day, run_id, task_key, attempt),
    FOREIGN KEY (run_day, run_id, task_key, attempt)
        REFERENCES task_attempts(run_day, run_id, task_key, attempt)
);

CREATE INDEX ON dispatch_outbox (agent_id, id);

COMMIT;
