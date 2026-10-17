BEGIN;

CREATE TABLE daily_rollups (
    day date NOT NULL,
    workflow_id text NOT NULL,
    node_key text NOT NULL,
    runs bigint NOT NULL CHECK (runs > 0),
    failures bigint NOT NULL CHECK (failures BETWEEN 0 AND runs),
    soft_failures bigint NOT NULL CHECK (soft_failures BETWEEN 0 AND runs),
    duration_count bigint NOT NULL CHECK (duration_count BETWEEN 0 AND runs),
    p50_ms double precision,
    p95_ms double precision,
    cpu_max_pct double precision,
    ram_peak_mb numeric,
    PRIMARY KEY (day, workflow_id, node_key),
    CHECK ((duration_count = 0 AND p50_ms IS NULL AND p95_ms IS NULL) OR
        (duration_count > 0 AND p50_ms IS NOT NULL AND p95_ms IS NOT NULL
            AND p50_ms >= 0 AND p95_ms >= p50_ms AND p95_ms < 'Infinity'::float8)),
    CHECK (cpu_max_pct IS NULL OR cpu_max_pct >= 0 AND cpu_max_pct < 'Infinity'::float8),
    CHECK (ram_peak_mb IS NULL OR ram_peak_mb >= 0 AND ram_peak_mb < 'Infinity'::numeric)
);

CREATE INDEX ON runs (run_day, workflow_id) WHERE status BETWEEN 3 AND 6;

-- day is the immutable UTC run creation day; the empty node key denotes the whole workflow.
CREATE FUNCTION refresh_daily_rollups(p_day date, p_workflow text) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    -- Acquire before reading aggregates: concurrent terminal transactions must not overwrite newer counts.
    PERFORM pg_advisory_xact_lock(132853531, hashtext((p_day-date '2000-01-01')::text || ':' || p_workflow));
    WITH completed AS MATERIALIZED (
        SELECT run_day,id,status,started_at,ended_at FROM runs
        WHERE run_day=p_day AND workflow_id=p_workflow AND status BETWEEN 3 AND 6
    ), resources AS MATERIALIZED (
        SELECT a.run_day,a.run_id,split_part(a.task_key,'/',1) AS node_key,
            max(a.resource_max_cpu_percent) AS cpu_max_pct,
            max(a.resource_peak_ram_bytes)/1048576::numeric AS ram_peak_mb
        FROM task_attempts a JOIN completed r ON r.run_day=a.run_day AND r.id=a.run_id
        WHERE a.run_day=p_day GROUP BY a.run_day,a.run_id,split_part(a.task_key,'/',1)
    ), run_resources AS (
        SELECT run_day,run_id,max(cpu_max_pct) AS cpu_max_pct,max(ram_peak_mb) AS ram_peak_mb
        FROM resources GROUP BY run_day,run_id
    ), observations AS (
        SELECT ''::text AS node_key,r.status<>3 AS failed,
            EXISTS(SELECT 1 FROM node_runs n WHERE n.run_day=p_day AND n.run_id=r.id AND n.status=6) AS soft_failed,
            CASE WHEN r.started_at IS NOT NULL AND r.ended_at IS NOT NULL
                THEN greatest(0,extract(epoch FROM r.ended_at-r.started_at)*1000)::float8 END AS duration_ms,
            a.cpu_max_pct,a.ram_peak_mb
        FROM completed r LEFT JOIN run_resources a ON a.run_day=r.run_day AND a.run_id=r.id
        UNION ALL
        SELECT n.node_key,n.status IN (7,8,9),n.status=6,
            CASE WHEN n.started_at IS NOT NULL AND n.ended_at IS NOT NULL
                THEN greatest(0,extract(epoch FROM n.ended_at-n.started_at)*1000)::float8 END,
            a.cpu_max_pct,a.ram_peak_mb
        FROM completed r JOIN node_runs n ON n.run_day=r.run_day AND n.run_id=r.id
        LEFT JOIN resources a ON a.run_day=n.run_day AND a.run_id=n.run_id AND a.node_key=n.node_key
        WHERE n.run_day=p_day
    ), aggregate AS (
        SELECT node_key,count(*) AS runs,count(*) FILTER (WHERE failed) AS failures,
            count(*) FILTER (WHERE soft_failed) AS soft_failures,count(duration_ms) AS duration_count,
            percentile_cont(ARRAY[0.5,0.95]) WITHIN GROUP (ORDER BY duration_ms) AS quantiles,
            max(cpu_max_pct) AS cpu_max_pct,max(ram_peak_mb) AS ram_peak_mb
        FROM observations GROUP BY node_key
    )
    INSERT INTO daily_rollups(day,workflow_id,node_key,runs,failures,soft_failures,duration_count,
        p50_ms,p95_ms,cpu_max_pct,ram_peak_mb)
    SELECT p_day,p_workflow,node_key,runs,failures,soft_failures,duration_count,
        quantiles[1],quantiles[2],cpu_max_pct,ram_peak_mb FROM aggregate
    ON CONFLICT (day,workflow_id,node_key) DO UPDATE SET
        runs=excluded.runs,failures=excluded.failures,soft_failures=excluded.soft_failures,
        duration_count=excluded.duration_count,p50_ms=excluded.p50_ms,p95_ms=excluded.p95_ms,
        cpu_max_pct=excluded.cpu_max_pct,ram_peak_mb=excluded.ram_peak_mb;
END
$$;

DO $$
DECLARE bucket record;
BEGIN
    FOR bucket IN SELECT DISTINCT run_day,workflow_id COLLATE "C" AS workflow_id
        FROM runs WHERE status BETWEEN 3 AND 6 ORDER BY run_day,workflow_id
    LOOP
        PERFORM refresh_daily_rollups(bucket.run_day,bucket.workflow_id);
    END LOOP;
END
$$;

COMMIT;
