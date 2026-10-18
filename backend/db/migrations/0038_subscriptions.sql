BEGIN;

CREATE TABLE subscriptions (
    id uuid PRIMARY KEY,
    principal_id uuid NOT NULL CHECK (principal_id <> '00000000-0000-0000-0000-000000000000'),
    scope_kind text NOT NULL CHECK (scope_kind IN ('workflow','schedule','type')),
    scope_id text NOT NULL,
    type_scope text NOT NULL REFERENCES workflow_types(id) ON DELETE CASCADE,
    series_id uuid REFERENCES run_series(id) ON DELETE CASCADE,
    events text[] NOT NULL CHECK (array_ndims(events)=1 AND array_lower(events,1)=1
        AND cardinality(events) BETWEEN 1 AND 2 AND array_position(events,NULL) IS NULL
        AND events<@ARRAY['Completed','Failed'] AND (cardinality(events)=1 OR events[1]<events[2])),
    channel text NOT NULL CHECK (channel IN ('Webhook','Email','Teams')),
    target text NOT NULL CHECK (char_length(target) BETWEEN 1 AND 2048 AND target !~ '[[:cntrl:]]'),
    secret_ref text CHECK (secret_ref IS NULL OR char_length(secret_ref) BETWEEN 1 AND 273),
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK ((scope_kind='schedule')=(series_id IS NOT NULL)),
    CHECK ((channel='Webhook')=(secret_ref IS NOT NULL)),
    CHECK ((scope_kind='type' AND scope_id=type_scope) OR
        (scope_kind='workflow' AND scope_id ~ '^[^/]+/[^/]+/[^/]+$'
            AND type_scope=split_part(scope_id,'/',1)||'/'||split_part(scope_id,'/',2)) OR
        (scope_kind='schedule' AND scope_id ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'))
);

CREATE UNIQUE INDEX subscriptions_configuration ON subscriptions
    (principal_id,scope_kind,scope_id,events,channel,target,coalesce(secret_ref,''));
CREATE INDEX ON subscriptions(principal_id,id DESC);
CREATE INDEX ON subscriptions(type_scope,scope_kind,scope_id);
CREATE INDEX ON subscriptions(series_id) WHERE series_id IS NOT NULL;

CREATE TABLE notification_outbox (
    id uuid PRIMARY KEY,
    subscription_id uuid NOT NULL REFERENCES subscriptions(id) ON DELETE CASCADE,
    run_day date NOT NULL,
    run_id uuid NOT NULL,
    payload jsonb NOT NULL CHECK (jsonb_typeof(payload)='object'),
    created_at timestamptz NOT NULL DEFAULT now(),
    attempts int NOT NULL DEFAULT 0 CHECK (attempts>=0),
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    delivered_at timestamptz,
    failed_at timestamptz,
    last_error text,
    UNIQUE(subscription_id,run_day,run_id),
    FOREIGN KEY(run_day,run_id) REFERENCES runs(run_day,id) ON DELETE CASCADE,
    CHECK (delivered_at IS NULL OR failed_at IS NULL),
    CHECK (last_error IS NULL OR char_length(last_error) BETWEEN 1 AND 256)
);

CREATE INDEX ON notification_outbox(next_attempt_at,id) WHERE delivered_at IS NULL AND failed_at IS NULL;

COMMIT;
