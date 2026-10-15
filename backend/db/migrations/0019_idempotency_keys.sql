CREATE TABLE idempotency_keys (
    principal_id uuid NOT NULL CHECK (principal_id <> '00000000-0000-0000-0000-000000000000'),
    route text NOT NULL CHECK (length(route) BETWEEN 1 AND 1024),
    key text NOT NULL CHECK (key ~ '^[!-~]{1,200}$'),
    request_hash bytea NOT NULL CHECK (octet_length(request_hash) = 32),
    status_code integer CHECK (status_code BETWEEN 200 AND 299),
    response jsonb,
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL CHECK (expires_at > created_at),
    PRIMARY KEY (principal_id, route, key),
    CHECK ((status_code IS NULL) = (response IS NULL))
);

CREATE INDEX ix_idempotency_keys_expiry ON idempotency_keys (expires_at);
