BEGIN;

ALTER TABLE agents ADD COLUMN reservation_epoch bigint NOT NULL DEFAULT 1 CHECK (reservation_epoch > 0);
ALTER TABLE agents ADD COLUMN reservation_fenced_epoch bigint NOT NULL DEFAULT 1
    CHECK (reservation_fenced_epoch > 0 AND reservation_fenced_epoch <= reservation_epoch);
ALTER TABLE task_attempts ADD COLUMN reservation_epoch bigint NOT NULL DEFAULT 1 CHECK (reservation_epoch > 0);
ALTER TABLE reservations ADD COLUMN release_requested_at timestamptz;
ALTER TABLE reservations ADD COLUMN release_requested_reason text;
ALTER TABLE reservations ADD COLUMN fence_epoch bigint;
ALTER TABLE reservations ADD CONSTRAINT reservations_release_request CHECK (
    (release_requested_at IS NULL AND release_requested_reason IS NULL AND fence_epoch IS NULL)
    OR (release_requested_at IS NOT NULL AND release_requested_reason IS NOT NULL AND fence_epoch IS NOT NULL
        AND release_requested_at >= created_at AND length(btrim(release_requested_reason)) BETWEEN 1 AND 500
        AND fence_epoch > epoch)
);

UPDATE agents a SET reservation_epoch=r.epoch
FROM reservations r WHERE r.agent_id=a.id AND r.released_at IS NULL;
UPDATE task_attempts t SET reservation_epoch=r.epoch
FROM reservations r WHERE r.agent_id=t.agent_id AND r.run_day=t.run_day AND r.run_id=t.run_id
    AND r.node_key=split_part(t.task_key,'/',1) AND r.released_at IS NULL;

CREATE INDEX reservations_pending_fence ON reservations(agent_id, fence_epoch)
    WHERE released_at IS NULL AND fence_epoch IS NOT NULL;

COMMIT;
