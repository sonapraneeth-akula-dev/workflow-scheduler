BEGIN;

CREATE TABLE workflows (
    id text PRIMARY KEY,
    type_id text NOT NULL REFERENCES workflow_types(id),
    active_version_id uuid,
    latest_version_id uuid,
    cloned_from_version_id uuid,
    deleted_at timestamptz,
    CHECK (id ~ '^[^/]+/[^/]+/[^/]+$' AND id !~ '[\\[:cntrl:]]'),
    CHECK (type_id = split_part(id,'/',1)||'/'||split_part(id,'/',2)),
    CHECK (char_length(split_part(id,'/',3)) BETWEEN 1 AND 128
        AND split_part(id,'/',3) ~ '[^[:space:]]')
);

CREATE INDEX workflows_type ON workflows(type_id);

-- SemVer text preserves prereleases/build metadata and the validator's unbounded numeric components.
CREATE TABLE workflow_versions (
    id uuid PRIMARY KEY CHECK ((get_byte(uuid_send(id),6) >> 4) = 7),
    workflow_id text NOT NULL REFERENCES workflows(id),
    semver text NOT NULL CHECK (length(semver) BETWEEN 1 AND 256),
    semver_key text GENERATED ALWAYS AS (split_part(semver,'+',1)) STORED,
    manifest jsonb NOT NULL CHECK (jsonb_typeof(manifest) = 'object'),
    package_uri text NOT NULL,
    package_sha256 bytea NOT NULL CHECK (octet_length(package_sha256) = 32),
    package_size bigint NOT NULL CHECK (package_size BETWEEN 1 AND 536870912),
    source_uri text NOT NULL,
    source_sha256 bytea NOT NULL CHECK (octet_length(source_sha256) = 32),
    source_size bigint NOT NULL CHECK (source_size > 0),
    git_commit text NOT NULL CHECK (git_commit ~ '^[0-9a-f]{40}([0-9a-f]{24})?$'),
    git_author text NOT NULL CHECK (git_author ~ '[^[:space:]]'),
    registered_by text NOT NULL CHECK (registered_by ~ '[^[:space:]]'),
    change_note text,
    sdk_version text NOT NULL CHECK (sdk_version ~ '[^[:space:]]'),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(workflow_id,semver_key),
    UNIQUE(workflow_id,id),
    CHECK (package_uri = 'packages/'||encode(package_sha256,'hex')||'.zip'),
    CHECK (source_uri = 'sources/'||encode(source_sha256,'hex'))
);

ALTER TABLE workflows ADD CONSTRAINT workflows_active_version
    FOREIGN KEY(id,active_version_id) REFERENCES workflow_versions(workflow_id,id);
ALTER TABLE workflows ADD CONSTRAINT workflows_latest_version
    FOREIGN KEY(id,latest_version_id) REFERENCES workflow_versions(workflow_id,id);
ALTER TABLE workflows ADD CONSTRAINT workflows_cloned_from
    FOREIGN KEY(cloned_from_version_id) REFERENCES workflow_versions(id);

CREATE TABLE registry_snapshots (
    id uuid PRIMARY KEY CHECK ((get_byte(uuid_send(id),6) >> 4) = 7),
    team_id text NOT NULL REFERENCES teams(id),
    git_commit text NOT NULL CHECK (git_commit ~ '^[0-9a-f]{40}([0-9a-f]{24})?$'),
    git_author text NOT NULL CHECK (git_author ~ '[^[:space:]]'),
    registered_by text NOT NULL CHECK (registered_by ~ '[^[:space:]]'),
    content jsonb NOT NULL CHECK (jsonb_typeof(content) = 'object'),
    registered_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX registry_snapshots_team ON registry_snapshots(team_id,registered_at DESC,id DESC);

-- Registration writes this in the mutation transaction; general endpoint coverage follows separately.
CREATE TABLE audit_log (
    id bigint GENERATED ALWAYS AS IDENTITY,
    ts timestamptz NOT NULL DEFAULT now(),
    actor text NOT NULL CHECK (actor ~ '[^[:space:]]'),
    actor_kind text NOT NULL CHECK (actor_kind IN ('User','App')),
    action text NOT NULL CHECK (action ~ '[^[:space:]]'),
    target_kind text NOT NULL CHECK (target_kind ~ '[^[:space:]]'),
    target_id text NOT NULL CHECK (target_id ~ '[^[:space:]]'),
    team_id text,
    summary jsonb NOT NULL CHECK (jsonb_typeof(summary) = 'object'),
    PRIMARY KEY(ts,id)
) PARTITION BY RANGE(ts);
CREATE INDEX audit_log_target ON audit_log(target_kind,target_id,ts DESC);
CREATE INDEX audit_log_actor ON audit_log(actor,ts DESC);

CREATE FUNCTION ensure_audit_partition(day date) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    month_start date := date_trunc('month',day)::date;
    month_end date := (month_start + interval '1 month')::date;
BEGIN
    EXECUTE format('CREATE TABLE IF NOT EXISTS %I PARTITION OF audit_log FOR VALUES FROM (%L) TO (%L)',
        'audit_log_'||to_char(month_start,'YYYYMM'),
        month_start::timestamp AT TIME ZONE 'UTC', month_end::timestamp AT TIME ZONE 'UTC');
END
$$;

DO $$
BEGIN
    FOR month_offset IN -11..1 LOOP
        PERFORM ensure_audit_partition((date_trunc('month',CURRENT_DATE) + month_offset * interval '1 month')::date);
    END LOOP;
END
$$;

COMMIT;
