BEGIN;

CREATE TABLE report_ids (
    id uuid PRIMARY KEY CHECK ((get_byte(uuid_send(id),6) >> 4) = 7),
    run_day date NOT NULL,
    UNIQUE (run_day,id)
);

CREATE TABLE reports (
    run_day date NOT NULL,
    id uuid NOT NULL CHECK ((get_byte(uuid_send(id),6) >> 4) = 7),
    run_id uuid NOT NULL,
    source_key text NOT NULL,
    attempt smallint NOT NULL CHECK (attempt > 0),
    kind text NOT NULL CHECK (kind ~ '[^[:space:]]' AND octet_length(kind) <= 128),
    title text NOT NULL CHECK (title ~ '[^[:space:]]' AND octet_length(title) <= 512),
    tags text[] NOT NULL CHECK (cardinality(tags) <= 32),
    size_bytes bigint NOT NULL CHECK (size_bytes >= 0),
    blob_uri text NOT NULL,
    sha256 bytea NOT NULL CHECK (octet_length(sha256) = 32),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (run_day, id),
    FOREIGN KEY (run_day,id) REFERENCES report_ids(run_day,id) ON DELETE CASCADE,
    FOREIGN KEY (run_day, run_id, source_key, attempt)
        REFERENCES task_attempts(run_day, run_id, task_key, attempt),
    CHECK (run_day = uuid_v7_run_day(run_id)),
    CHECK (blob_uri = 'reports/' || run_id::text || '/' || id::text)
) PARTITION BY RANGE (run_day);

CREATE INDEX reports_run ON reports(run_day, run_id, created_at, id);
CREATE INDEX reports_tags ON reports USING gin(tags);

CREATE OR REPLACE FUNCTION ensure_run_partitions(day date) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    month_start date := date_trunc('month', day)::date;
    month_end date := (month_start + interval '1 month')::date;
    parent text;
BEGIN
    FOREACH parent IN ARRAY ARRAY['runs', 'run_events', 'run_snapshots', 'node_runs', 'task_attempts', 'reports']
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

CREATE FUNCTION notify_report_added() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_notify('report_added', json_build_object(
        'kind', 'ReportAdded', 'runId', NEW.run_id, 'reportId', NEW.id)::text);
    RETURN NULL;
END
$$;
CREATE TRIGGER report_added_insert AFTER INSERT ON reports
FOR EACH ROW EXECUTE FUNCTION notify_report_added();

COMMIT;
