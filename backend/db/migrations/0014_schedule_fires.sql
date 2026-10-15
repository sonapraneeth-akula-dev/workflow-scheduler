BEGIN;

-- Started=0, Skipped=1, Missed=2, CaughtUp=3; matches ScheduleFireOutcome.
-- run_id remains a history reference after run retention removes its partition.
CREATE TABLE schedule_fires (
    schedule_id uuid NOT NULL REFERENCES schedules (id),
    fire_at timestamptz NOT NULL CHECK (isfinite(fire_at)),
    outcome smallint NOT NULL CHECK (outcome BETWEEN 0 AND 3),
    run_id uuid,
    reason text,
    PRIMARY KEY (schedule_id, fire_at)
);

CREATE INDEX schedule_fires_run ON schedule_fires (run_id) WHERE run_id IS NOT NULL;

COMMIT;
