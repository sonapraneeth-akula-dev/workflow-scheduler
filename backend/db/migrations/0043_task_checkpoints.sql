BEGIN;

CREATE TABLE task_checkpoints (
    run_day date NOT NULL,
    run_id uuid NOT NULL,
    task_key text NOT NULL,
    attempt smallint NOT NULL CHECK (attempt > 0),
    sequence bigint NOT NULL CHECK (sequence > 0),
    saved_at timestamptz NOT NULL CHECK (saved_at >= timestamp with time zone '1970-01-01'),
    blob_uri text NOT NULL,
    sha256 text NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
    size_bytes bigint NOT NULL CHECK (size_bytes BETWEEN 1 AND 1048576),
    PRIMARY KEY (run_day,run_id,task_key),
    FOREIGN KEY (run_day,run_id) REFERENCES runs(run_day,id) ON DELETE CASCADE,
    FOREIGN KEY (run_day,run_id,task_key,attempt) REFERENCES task_attempts(run_day,run_id,task_key,attempt),
    CHECK (run_day=uuid_v7_run_day(run_id)),
    CHECK (blob_uri LIKE 'checkpoints/'||run_id::text||'/%/'||sequence::text||'-'||sha256||'.json')
) PARTITION BY RANGE (run_day);

CREATE OR REPLACE FUNCTION ensure_run_partitions(day date) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    month_start date := date_trunc('month', day)::date;
    month_end date := (month_start + interval '1 month')::date;
    parent text;
BEGIN
    FOREACH parent IN ARRAY ARRAY['runs','run_events','run_snapshots','node_runs','task_attempts','reports','artifacts','task_checkpoints']
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
    FOR month_start IN
        SELECT date_trunc('month', run_day)::date FROM runs
        UNION
        SELECT to_date(substring(c.relname FROM 6),'YYYYMM')
        FROM pg_inherits i JOIN pg_class p ON p.oid=i.inhparent JOIN pg_class c ON c.oid=i.inhrelid
        WHERE p.relnamespace=current_schema()::regnamespace AND p.relname='runs'
            AND c.relnamespace=p.relnamespace AND c.relname ~ '^runs_[0-9]{6}$'
    LOOP
        PERFORM ensure_run_partitions(month_start);
    END LOOP;
END
$$;
SELECT ensure_run_partitions(CURRENT_DATE);
SELECT ensure_run_partitions((CURRENT_DATE + interval '1 month')::date);

COMMIT;
