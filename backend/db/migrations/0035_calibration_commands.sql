BEGIN;

ALTER TABLE agents ADD COLUMN supports_calibration_commands boolean NOT NULL DEFAULT false;

CREATE TABLE calibration_requests (
    id uuid PRIMARY KEY CHECK (get_byte(uuid_send(id), 6) >> 4 = 7),
    agent_id uuid NOT NULL REFERENCES agents(id),
    profile_key text NOT NULL CHECK (octet_length(profile_key) BETWEEN 1 AND 2000),
    epoch bigint NOT NULL CHECK (epoch > 1),
    requested_actor text NOT NULL CHECK (length(btrim(requested_actor)) > 0),
    requested_actor_kind text NOT NULL CHECK (requested_actor_kind IN ('User', 'App')),
    created_at timestamptz NOT NULL,
    deadline_at timestamptz NOT NULL CHECK (deadline_at > created_at),
    dispatched_at timestamptz,
    status smallint NOT NULL DEFAULT 0 CHECK (status BETWEEN 0 AND 3),
    finished_at timestamptz,
    reported_rps int CHECK (reported_rps > 0),
    applied boolean NOT NULL DEFAULT false,
    error smallint CHECK (error BETWEEN 2 AND 5),
    CHECK ((status IN (0, 1)) = (finished_at IS NULL)),
    CHECK ((status = 2) = (reported_rps IS NOT NULL)),
    CHECK ((status = 3) = (error IS NOT NULL)),
    CHECK (NOT applied OR status = 2),
    CHECK (status <> 1 OR dispatched_at IS NOT NULL)
);

CREATE UNIQUE INDEX calibration_active_agent ON calibration_requests(agent_id) WHERE status IN (0, 1);

COMMIT;
