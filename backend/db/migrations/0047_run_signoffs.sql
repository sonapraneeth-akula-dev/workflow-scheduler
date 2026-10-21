BEGIN;

CREATE TABLE run_signoffs (
    run_id uuid NOT NULL CHECK ((get_byte(uuid_send(run_id),6) >> 4) = 7),
    target_kind smallint NOT NULL CHECK (target_kind IN (0,1)),
    target_id uuid NOT NULL CHECK ((get_byte(uuid_send(target_id),6) >> 4) = 7),
    revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
    PRIMARY KEY (run_id,target_kind,target_id),
    CHECK (target_kind <> 0 OR target_id = run_id)
);

CREATE TABLE run_signoff_events (
    run_id uuid NOT NULL,
    target_kind smallint NOT NULL,
    target_id uuid NOT NULL,
    revision bigint NOT NULL CHECK (revision > 0),
    action smallint NOT NULL CHECK (action IN (0,1)),
    reason text NOT NULL CHECK (length(reason) BETWEEN 1 AND 2000 AND reason ~ '[^[:space:]]'),
    actor_id uuid NOT NULL CHECK (actor_id <> '00000000-0000-0000-0000-000000000000'),
    actor_name text NOT NULL CHECK (length(actor_name) BETWEEN 1 AND 512),
    at timestamptz NOT NULL,
    PRIMARY KEY (run_id,target_kind,target_id,revision),
    FOREIGN KEY (run_id,target_kind,target_id) REFERENCES run_signoffs(run_id,target_kind,target_id)
);

CREATE FUNCTION protect_run_signoff_history() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Sign-off history is append-only';
END
$$;

CREATE TRIGGER run_signoff_history_immutable BEFORE UPDATE OR DELETE ON run_signoff_events
FOR EACH ROW EXECUTE FUNCTION protect_run_signoff_history();

COMMIT;
