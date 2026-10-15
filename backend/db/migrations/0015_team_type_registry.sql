BEGIN;

CREATE TABLE teams (
    id text PRIMARY KEY CHECK (id ~ '[^[:space:]]' AND char_length(id) <= 128
        AND id !~ '[/\\[:cntrl:]]'),
    name text NOT NULL CHECK (name ~ '[^[:space:]]'),
    presentation jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(presentation) = 'object'),
    registry jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(registry) = 'object')
);

CREATE TABLE workflow_types (
    id text PRIMARY KEY,
    team_id text NOT NULL REFERENCES teams (id),
    name text NOT NULL CHECK (name ~ '[^[:space:]]' AND char_length(name) <= 128
        AND name !~ '[/\\[:cntrl:]]'),
    normalized_name text NOT NULL CHECK (normalized_name <> ''),
    description text NOT NULL CHECK (description ~ '[^[:space:]]'),
    metadata_schema jsonb NOT NULL CHECK (jsonb_typeof(metadata_schema) = 'object'),
    retention_days int CHECK (retention_days > 0),
    limits jsonb CHECK (jsonb_typeof(limits) = 'object'),
    UNIQUE (team_id, normalized_name),
    CHECK (id = team_id || '/' || name)
);

COMMIT;
