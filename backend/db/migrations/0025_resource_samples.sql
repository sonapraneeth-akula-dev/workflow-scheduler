BEGIN;
ALTER TABLE task_attempts
    ADD COLUMN resource_sample_count bigint NOT NULL DEFAULT 0 CHECK (resource_sample_count >= 0),
    ADD COLUMN resource_series_uri text,
    ADD COLUMN resource_series_sha256 text,
    ADD COLUMN resource_series_bytes bigint,
    ADD COLUMN resource_cpu_percent_seconds double precision NOT NULL DEFAULT 0,
    ADD COLUMN resource_cpu_observed_seconds double precision NOT NULL DEFAULT 0,
    ADD COLUMN resource_avg_cpu_percent double precision,
    ADD COLUMN resource_max_cpu_percent double precision,
    ADD COLUMN resource_peak_ram_bytes numeric(20,0),
    ADD COLUMN resource_network_sent_bytes numeric(20,0),
    ADD COLUMN resource_network_received_bytes numeric(20,0),
    ADD COLUMN resource_network_observed_seconds double precision NOT NULL DEFAULT 0,
    ADD CONSTRAINT resource_pointer_complete CHECK (
        (resource_series_uri IS NULL AND resource_series_sha256 IS NULL AND resource_series_bytes IS NULL AND resource_sample_count=0)
        OR (resource_series_uri IS NOT NULL AND resource_series_uri LIKE 'resources/'||run_id::text||'/%' AND resource_series_sha256 IS NOT NULL
            AND resource_series_sha256 ~ '^[0-9a-f]{64}$' AND resource_series_bytes IS NOT NULL
            AND resource_series_bytes BETWEEN 1 AND 32768 AND resource_sample_count>0)),
    ADD CONSTRAINT resource_summary_nonnegative CHECK (
        resource_cpu_percent_seconds >= 0 AND resource_cpu_percent_seconds < 'Infinity'::float8
        AND resource_cpu_observed_seconds >= 0 AND resource_cpu_observed_seconds < 'Infinity'::float8
        AND resource_network_observed_seconds >= 0 AND resource_network_observed_seconds < 'Infinity'::float8
        AND (resource_avg_cpu_percent IS NULL OR resource_avg_cpu_percent >= 0 AND resource_avg_cpu_percent < 'Infinity'::float8)
        AND (resource_max_cpu_percent IS NULL OR resource_max_cpu_percent >= 0 AND resource_max_cpu_percent < 'Infinity'::float8)
        AND (resource_peak_ram_bytes IS NULL OR resource_peak_ram_bytes >= 0)
        AND (resource_network_sent_bytes IS NULL OR resource_network_sent_bytes >= 0)
        AND (resource_network_received_bytes IS NULL OR resource_network_received_bytes >= 0));
COMMIT;
