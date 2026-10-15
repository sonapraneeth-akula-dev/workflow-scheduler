BEGIN;

CREATE TABLE agents (
    id uuid PRIMARY KEY,
    name text NOT NULL UNIQUE CHECK (length(name) BETWEEN 1 AND 200),
    os smallint NOT NULL CHECK (os IN (0, 1)),
    arch smallint NOT NULL CHECK (arch IN (0, 1)),
    cores int NOT NULL CHECK (cores > 0),
    ram_mb int NOT NULL CHECK (ram_mb > 0),
    max_tasks int NOT NULL CHECK (max_tasks > 0),
    labels text[] NOT NULL,
    pool text NOT NULL CHECK (length(pool) BETWEEN 1 AND 200),
    status smallint NOT NULL CHECK (status BETWEEN 0 AND 2),
    last_heartbeat_at timestamptz NOT NULL,
    agent_version text NOT NULL CHECK (length(agent_version) BETWEEN 1 AND 100),
    session_id uuid NOT NULL,
    cpu_percent double precision NOT NULL DEFAULT 0 CHECK (cpu_percent BETWEEN 0 AND 100),
    ram_used_mb bigint NOT NULL DEFAULT 0 CHECK (ram_used_mb >= 0),
    disk_free_mb bigint NOT NULL DEFAULT 0 CHECK (disk_free_mb >= 0),
    slots_used int NOT NULL DEFAULT 0 CHECK (slots_used BETWEEN 0 AND max_tasks)
);

CREATE INDEX ON agents USING gin (labels);
CREATE INDEX ON agents (last_heartbeat_at) WHERE status = 0;

COMMIT;
