BEGIN;

CREATE TABLE artifact_grants (
    id uuid PRIMARY KEY CHECK ((get_byte(uuid_send(id),6) >> 4) = 7),
    run_day date NOT NULL,
    run_id uuid NOT NULL,
    source_key text NOT NULL,
    attempt smallint NOT NULL CHECK (attempt > 0),
    agent_id uuid NOT NULL,
    session_id uuid NOT NULL,
    reservation_epoch bigint NOT NULL CHECK (reservation_epoch > 0),
    name text NOT NULL CHECK (octet_length(name) BETWEEN 1 AND 255),
    content_type text NOT NULL CHECK (octet_length(content_type) BETWEEN 1 AND 128),
    size_bytes bigint NOT NULL CHECK (size_bytes BETWEEN 0 AND 5000000000),
    sha256 bytea NOT NULL CHECK (octet_length(sha256) = 32),
    expires_at timestamptz NOT NULL,
    committed boolean NOT NULL DEFAULT false,
    FOREIGN KEY (run_day,run_id,source_key,attempt)
        REFERENCES task_attempts(run_day,run_id,task_key,attempt),
    CHECK (run_day = uuid_v7_run_day(run_id))
);
CREATE INDEX artifact_grants_run ON artifact_grants(run_id);

CREATE TABLE artifacts (
    run_day date NOT NULL,
    id uuid NOT NULL REFERENCES artifact_grants(id),
    run_id uuid NOT NULL,
    source_key text NOT NULL,
    name text NOT NULL,
    content_type text NOT NULL,
    size_bytes bigint NOT NULL CHECK (size_bytes BETWEEN 0 AND 5000000000),
    blob_uri text NOT NULL,
    sha256 bytea NOT NULL CHECK (octet_length(sha256) = 32),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (run_day,id),
    FOREIGN KEY (run_day,run_id) REFERENCES runs(run_day,id),
    CHECK (run_day = uuid_v7_run_day(run_id)),
    CHECK (blob_uri = 'artifacts/' || encode(sha256,'hex'))
) PARTITION BY RANGE (run_day);
CREATE INDEX artifacts_run ON artifacts(run_day,run_id,created_at,id);

CREATE OR REPLACE FUNCTION ensure_run_partitions(day date) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    month_start date := date_trunc('month', day)::date;
    month_end date := (month_start + interval '1 month')::date;
    parent text;
BEGIN
    FOREACH parent IN ARRAY ARRAY['runs', 'run_events', 'run_snapshots', 'node_runs', 'task_attempts', 'reports', 'artifacts']
    LOOP
        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS %I PARTITION OF %I FOR VALUES FROM (%L) TO (%L)',
            parent || '_' || to_char(month_start, 'YYYYMM'), parent, month_start, month_end);
    END LOOP;
END
$$;
DO $$
DECLARE
    month_start date;
BEGIN
    FOR month_start IN SELECT DISTINCT date_trunc('month', run_day)::date FROM runs
    LOOP
        PERFORM ensure_run_partitions(month_start);
    END LOOP;
END
$$;
SELECT ensure_run_partitions(CURRENT_DATE);
SELECT ensure_run_partitions((CURRENT_DATE + interval '1 month')::date);

COMMIT;
