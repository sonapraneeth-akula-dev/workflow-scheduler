BEGIN;

CREATE TABLE dashboard_rollups (
    scope text NOT NULL CHECK (scope IN ('workflow','schedule','machine')),
    scope_id uuid NOT NULL,
    workflow_id text NOT NULL,
    node_key text NOT NULL,
    day date NOT NULL,
    observations bigint NOT NULL CHECK (observations > 0),
    failures bigint NOT NULL CHECK (failures BETWEEN 0 AND observations),
    soft_failures bigint NOT NULL CHECK (soft_failures BETWEEN 0 AND observations-failures),
    duration_count bigint NOT NULL CHECK (duration_count BETWEEN 0 AND observations),
    durations_ms double precision[] NOT NULL,
    cpu_max_pct double precision,
    ram_peak_mb numeric,
    latest_run_id uuid NOT NULL,
    PRIMARY KEY (scope,scope_id,workflow_id,node_key,day),
    CHECK (scope <> 'workflow' OR scope_id='00000000-0000-0000-0000-000000000000'),
    CHECK (cardinality(durations_ms)=duration_count AND array_position(durations_ms,NULL) IS NULL
        AND 0<=ALL(durations_ms) AND 'Infinity'::float8>ALL(durations_ms)),
    CHECK (cpu_max_pct IS NULL OR cpu_max_pct>=0 AND cpu_max_pct<'Infinity'::float8),
    CHECK (ram_peak_mb IS NULL OR ram_peak_mb>=0 AND ram_peak_mb<'Infinity'::numeric)
);

CREATE INDEX ON dashboard_rollups(day,workflow_id);

-- Keep source durations, not daily percentiles: windows must retain exact observation-weighted quantiles.
CREATE FUNCTION refresh_dashboard_rollups(p_day date, p_workflow text) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(132853531, hashtext((p_day-date '2000-01-01')::text || ':' || p_workflow));
    DELETE FROM dashboard_rollups WHERE day=p_day AND workflow_id=p_workflow;
    WITH completed AS MATERIALIZED (
        SELECT run_day,id,series_id FROM runs
        WHERE run_day=p_day AND workflow_id=p_workflow AND status BETWEEN 3 AND 6
    ), resources AS (
        SELECT a.run_id,split_part(a.task_key,'/',1) AS node_key,
            max(a.resource_max_cpu_percent) AS cpu_max_pct,
            max(a.resource_peak_ram_bytes)/1048576::numeric AS ram_peak_mb
        FROM task_attempts a JOIN completed r ON r.run_day=a.run_day AND r.id=a.run_id
        WHERE a.run_day=p_day GROUP BY a.run_id,split_part(a.task_key,'/',1)
    ), nodes AS (
        SELECT r.id,r.series_id,n.node_key,n.status,n.started_at,n.ended_at,a.cpu_max_pct,a.ram_peak_mb
        FROM completed r JOIN node_runs n ON n.run_day=r.run_day AND n.run_id=r.id
        LEFT JOIN resources a ON a.run_id=n.run_id AND a.node_key=n.node_key
        WHERE n.run_day=p_day AND n.status BETWEEN 5 AND 11
    ), observations AS (
        SELECT s.scope,s.scope_id,n.id,n.node_key,n.status,n.started_at,n.ended_at,n.cpu_max_pct,n.ram_peak_mb
        FROM nodes n CROSS JOIN LATERAL (VALUES
            ('workflow','00000000-0000-0000-0000-000000000000'::uuid),('schedule',n.series_id)
        ) s(scope,scope_id) WHERE s.scope_id IS NOT NULL
        UNION ALL
        SELECT 'machine',a.agent_id,r.id,split_part(a.task_key,'/',1),a.status,a.started_at,a.ended_at,
            a.resource_max_cpu_percent,a.resource_peak_ram_bytes/1048576::numeric
        FROM completed r JOIN task_attempts a ON a.run_day=r.run_day AND a.run_id=r.id
        WHERE a.run_day=p_day AND a.status BETWEEN 5 AND 11 AND a.agent_id IS NOT NULL
    ), measured AS (
        SELECT *,CASE WHEN started_at IS NOT NULL AND ended_at IS NOT NULL
            THEN greatest(0,date_part('epoch',ended_at-started_at)*1000) END AS duration_ms
        FROM observations
    )
    INSERT INTO dashboard_rollups(scope,scope_id,workflow_id,node_key,day,observations,failures,soft_failures,
        duration_count,durations_ms,cpu_max_pct,ram_peak_mb,latest_run_id)
    SELECT scope,scope_id,p_workflow,node_key,p_day,count(*),count(*) FILTER (WHERE status IN (7,8,9)),
        count(*) FILTER (WHERE status=6),count(duration_ms),
        coalesce(array_agg(duration_ms) FILTER (WHERE duration_ms IS NOT NULL),'{}'::float8[]),
        max(cpu_max_pct),max(ram_peak_mb),max(id::text COLLATE "C")::uuid
    FROM measured GROUP BY scope,scope_id,node_key;
END
$$;

CREATE OR REPLACE FUNCTION refresh_daily_rollups(p_day date, p_workflow text) RETURNS void
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
    PERFORM refresh_dashboard_rollups(p_day,p_workflow);
END
$$;

DO $$
DECLARE bucket record;
BEGIN
    FOR bucket IN SELECT DISTINCT run_day,workflow_id COLLATE "C" AS workflow_id
        FROM runs WHERE status BETWEEN 3 AND 6 ORDER BY run_day,workflow_id
    LOOP
        PERFORM refresh_dashboard_rollups(bucket.run_day,bucket.workflow_id);
    END LOOP;
END
$$;

COMMIT;
