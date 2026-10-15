BEGIN;

ALTER TABLE runs ADD COLUMN deadline_at timestamptz;
UPDATE runs SET deadline_at = created_at + interval '24 hours';
ALTER TABLE runs ALTER COLUMN deadline_at SET NOT NULL;

CREATE TABLE node_runs (
    run_day date NOT NULL,
    run_id uuid NOT NULL,
    node_key text NOT NULL,
    status smallint NOT NULL CHECK (status BETWEEN 0 AND 11),
    attempts smallint NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    failure_category smallint,
    started_at timestamptz,
    ended_at timestamptz,
    deadline_at timestamptz,
    outputs jsonb,
    reused_from_run_id uuid,
    pinned_agent_id uuid,
    PRIMARY KEY (run_day, run_id, node_key),
    FOREIGN KEY (run_day, run_id) REFERENCES runs (run_day, id)
) PARTITION BY RANGE (run_day);

CREATE TABLE task_attempts (
    run_day date NOT NULL,
    run_id uuid NOT NULL,
    task_key text NOT NULL,
    attempt smallint NOT NULL CHECK (attempt > 0),
    agent_id uuid,
    status smallint NOT NULL CHECK (status BETWEEN 0 AND 11),
    failure_category smallint,
    error text,
    queued_at timestamptz,
    started_at timestamptz,
    ended_at timestamptz,
    queue_wait_ms double precision,
    deadline_at timestamptz NOT NULL,
    retry_at timestamptz,
    cancel_requested_at timestamptz,
    kill_at timestamptz,
    timer_phase smallint NOT NULL DEFAULT 0 CHECK (timer_phase BETWEEN 0 AND 3),
    PRIMARY KEY (run_day, run_id, task_key, attempt),
    FOREIGN KEY (run_day, run_id) REFERENCES runs (run_day, id),
    CHECK ((kill_at IS NULL) = (cancel_requested_at IS NULL)),
    CHECK (kill_at IS NULL OR kill_at >= cancel_requested_at),
    CHECK (error IS NULL OR octet_length(error) <= 4096)
) PARTITION BY RANGE (run_day);

CREATE INDEX ON runs (deadline_at) WHERE status IN (0, 1, 2);
CREATE INDEX ON node_runs (deadline_at) WHERE status IN (0, 1, 2, 3, 4) AND deadline_at IS NOT NULL;
CREATE INDEX ON task_attempts (deadline_at) WHERE status IN (0, 1, 2, 3, 4);
CREATE INDEX ON task_attempts (retry_at) WHERE retry_at IS NOT NULL;
CREATE INDEX ON task_attempts (kill_at) WHERE kill_at IS NOT NULL AND timer_phase = 1;
CREATE INDEX ON task_attempts (agent_id, started_at) WHERE agent_id IS NOT NULL;

CREATE OR REPLACE FUNCTION ensure_run_partitions(day date) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    month_start date := date_trunc('month', day)::date;
    month_end date := (month_start + interval '1 month')::date;
    parent text;
BEGIN
    FOREACH parent IN ARRAY ARRAY['runs', 'run_events', 'run_snapshots', 'node_runs', 'task_attempts']
    LOOP
        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS %I PARTITION OF %I FOR VALUES FROM (%L) TO (%L)',
            parent || '_' || to_char(month_start, 'YYYYMM'), parent, month_start, month_end);
    END LOOP;
END
$$;

SELECT ensure_run_partitions(CURRENT_DATE);
SELECT ensure_run_partitions((CURRENT_DATE + interval '1 month')::date);

COMMIT;
