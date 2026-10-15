BEGIN;

-- Scope: Global=0, Team=1, Type=2; role matches RegistryRole; principal: Group=0, User=1.
CREATE TABLE role_bindings (
    id uuid PRIMARY KEY,
    scope_kind smallint NOT NULL CHECK (scope_kind BETWEEN 0 AND 2),
    scope_id text NOT NULL,
    role smallint NOT NULL CHECK (role BETWEEN 0 AND 3),
    principal_id uuid NOT NULL CHECK (principal_id <> '00000000-0000-0000-0000-000000000000'),
    principal_kind smallint NOT NULL CHECK (principal_kind IN (0, 1)),
    UNIQUE (scope_kind, scope_id, role, principal_id),
    CHECK ((get_byte(uuid_send(id), 6) >> 4) = 7),
    CHECK ((scope_kind = 0 AND scope_id = '*')
        OR (scope_kind = 1 AND char_length(scope_id) BETWEEN 1 AND 128
            AND scope_id ~ '[^[:space:]]' AND scope_id !~ '[/\\[:cntrl:]]')
        OR (scope_kind = 2 AND scope_id ~ '^[^/]+/[^/]+$' AND scope_id !~ '[\\[:cntrl:]]'
            AND char_length(split_part(scope_id, '/', 1)) BETWEEN 1 AND 128
            AND char_length(split_part(scope_id, '/', 2)) BETWEEN 1 AND 128
            AND split_part(scope_id, '/', 1) ~ '[^[:space:]]'
            AND split_part(scope_id, '/', 2) ~ '[^[:space:]]'))
);

CREATE INDEX role_bindings_principal ON role_bindings (principal_id);

COMMIT;
