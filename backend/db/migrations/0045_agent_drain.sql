BEGIN;

ALTER TABLE agents ADD COLUMN supports_drain_commands boolean NOT NULL DEFAULT false;

CREATE TABLE drain_requests (
    id uuid PRIMARY KEY CHECK (get_byte(uuid_send(id), 6) >> 4 = 7),
    agent_id uuid NOT NULL UNIQUE REFERENCES agents(id),
    created_at timestamptz NOT NULL,
    deadline_at timestamptz NOT NULL CHECK (deadline_at > created_at),
    reason text NOT NULL CHECK (octet_length(reason) BETWEEN 1 AND 1024 AND length(btrim(reason)) > 0),
    released_at timestamptz CHECK (released_at >= created_at),
    requested_actor text NOT NULL CHECK (length(btrim(requested_actor)) > 0),
    requested_actor_kind text NOT NULL CHECK (requested_actor_kind IN ('User', 'App'))
);

COMMIT;
