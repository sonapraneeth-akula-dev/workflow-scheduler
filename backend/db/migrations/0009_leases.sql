BEGIN;

CREATE TABLE leases (
    name text PRIMARY KEY,
    holder text NOT NULL CHECK (holder ~ '[^[:space:]]'),
    leader_hint text NOT NULL CHECK (leader_hint ~ '^https?://'),
    epoch bigint NOT NULL CHECK (epoch > 0),
    expires_at timestamptz NOT NULL
);

CREATE FUNCTION fence_engine_lease(expected_holder text, expected_epoch bigint) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    valid_until timestamptz;
    remaining_ms bigint;
BEGIN
    SELECT expires_at INTO valid_until FROM leases
    WHERE name = 'engine' AND holder = expected_holder AND epoch = expected_epoch
        AND expires_at > clock_timestamp()
    FOR SHARE;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING ERRCODE = 'WF001', MESSAGE = 'Engine lease is not held.';
    END IF;
    remaining_ms := floor(extract(epoch FROM (valid_until - clock_timestamp())) * 1000);
    IF remaining_ms <= 0 THEN
        RAISE EXCEPTION USING ERRCODE = 'WF001', MESSAGE = 'Engine lease expired.';
    END IF;
    -- A paused client cannot hold the fence beyond its lease or indefinitely block takeover.
    PERFORM set_config('transaction_timeout', least(remaining_ms, 1000)::text, true);
END
$$;

COMMIT;
