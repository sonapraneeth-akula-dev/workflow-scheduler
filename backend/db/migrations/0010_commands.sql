BEGIN;

CREATE TABLE commands (
    id uuid PRIMARY KEY,
    kind smallint NOT NULL CHECK (kind IN (0, 1)),
    run_id uuid NOT NULL CHECK ((get_byte(uuid_send(run_id), 6) >> 4) = 7),
    payload jsonb NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    created_by text NOT NULL CHECK (length(btrim(created_by)) BETWEEN 1 AND 200),
    created_at timestamptz NOT NULL,
    processed_at timestamptz,
    UNIQUE (run_id, kind),
    CHECK (processed_at IS NULL OR processed_at >= created_at)
);

CREATE INDEX ON commands (created_at, kind, id) WHERE processed_at IS NULL;
COMMIT;
