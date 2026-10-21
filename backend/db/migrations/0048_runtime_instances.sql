BEGIN;

CREATE TABLE runtime_instances (
    id text PRIMARY KEY CHECK (id ~ '^[0-9a-f]{32}$'),
    name text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    host_name text NOT NULL CHECK (length(host_name) BETWEEN 1 AND 200),
    node_name text NOT NULL CHECK (length(node_name) BETWEEN 1 AND 200),
    pod_name text CHECK (length(pod_name) BETWEEN 1 AND 200),
    namespace text CHECK (length(namespace) BETWEEN 1 AND 200),
    engine_enabled boolean NOT NULL,
    version text NOT NULL CHECK (length(version) BETWEEN 1 AND 100),
    started_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    last_heartbeat_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    ready boolean NOT NULL,
    stopped_at timestamptz,
    http_requests bigint NOT NULL CHECK (http_requests >= 0),
    live_connections int NOT NULL CHECK (live_connections >= 0),
    live_subscriptions int NOT NULL CHECK (live_subscriptions >= 0),
    live_deliveries bigint NOT NULL CHECK (live_deliveries >= 0)
);

CREATE INDEX runtime_instances_heartbeat ON runtime_instances(last_heartbeat_at);

COMMIT;
