BEGIN;

CREATE TABLE agent_capacities (
    agent_id uuid NOT NULL REFERENCES agents (id) ON DELETE CASCADE,
    profile_key text NOT NULL CHECK (
        octet_length(profile_key) BETWEEN 1 AND 2000 AND profile_key ~ '[^[:space:]]'),
    capacity_rps int NOT NULL CHECK (capacity_rps > 0),
    source smallint NOT NULL CHECK (source IN (0, 1)),
    calibrated_at timestamptz,
    PRIMARY KEY (agent_id, profile_key),
    CHECK ((source = 0 AND calibrated_at IS NULL) OR (source = 1 AND calibrated_at IS NOT NULL))
);

COMMIT;
