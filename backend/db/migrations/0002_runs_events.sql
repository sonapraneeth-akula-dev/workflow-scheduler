BEGIN;

CREATE FUNCTION uuid_v7_run_day(id uuid) RETURNS date
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE AS $$
    SELECT (to_timestamp(
        ('x' || substr(replace(id::text, '-', ''), 1, 12))::bit(48)::bigint / 1000.0
    ) AT TIME ZONE 'UTC')::date
$$;

-- Registry foreign keys are added when the registry tables exist.
CREATE TABLE runs (
    run_day date NOT NULL,
    id uuid NOT NULL,
    workflow_id text NOT NULL,
    version_id uuid NOT NULL,
    series_id uuid,
    trigger smallint NOT NULL,
    triggered_by text NOT NULL,
    variables jsonb NOT NULL,
    metadata jsonb NOT NULL,
    status smallint NOT NULL CHECK (status BETWEEN 0 AND 6),
    failure_category smallint,
    status_reason text,
    created_at timestamptz NOT NULL,
    started_at timestamptz,
    ended_at timestamptz,
    last_seq bigint NOT NULL DEFAULT 0 CHECK (last_seq >= 0),
    snapshot_seq bigint NOT NULL DEFAULT 0 CHECK (snapshot_seq BETWEEN 0 AND last_seq),
    PRIMARY KEY (run_day, id),
    CHECK ((get_byte(uuid_send(id), 6) >> 4) = 7),
    CHECK (run_day = uuid_v7_run_day(id)),
    CHECK (jsonb_typeof(variables) = 'object' AND jsonb_typeof(metadata) = 'object')
) PARTITION BY RANGE (run_day);

CREATE INDEX ON runs (workflow_id, id DESC);
CREATE INDEX ON runs (series_id, id DESC) WHERE series_id IS NOT NULL;
CREATE INDEX ON runs (status) WHERE status IN (0, 1, 2);
CREATE INDEX ON runs USING gin (metadata jsonb_path_ops);

CREATE TABLE run_events (
    run_day date NOT NULL,
    run_id uuid NOT NULL,
    seq bigint NOT NULL CHECK (seq > 0),
    ts timestamptz NOT NULL,
    kind smallint NOT NULL,
    entity_key text,
    attempt smallint,
    payload bytea NOT NULL,
    PRIMARY KEY (run_day, run_id, seq),
    FOREIGN KEY (run_day, run_id) REFERENCES runs (run_day, id),
    CHECK (run_day = uuid_v7_run_day(run_id))
) PARTITION BY RANGE (run_day);

CREATE TABLE run_snapshots (
    run_day date NOT NULL,
    run_id uuid NOT NULL,
    seq bigint NOT NULL CHECK (seq >= 0),
    created_at timestamptz NOT NULL,
    payload bytea NOT NULL,
    PRIMARY KEY (run_day, run_id, seq),
    FOREIGN KEY (run_day, run_id) REFERENCES runs (run_day, id)
) PARTITION BY RANGE (run_day);

CREATE FUNCTION ensure_run_partitions(day date) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    month_start date := date_trunc('month', day)::date;
    month_end date := (month_start + interval '1 month')::date;
    parent text;
BEGIN
    FOREACH parent IN ARRAY ARRAY['runs', 'run_events', 'run_snapshots']
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
