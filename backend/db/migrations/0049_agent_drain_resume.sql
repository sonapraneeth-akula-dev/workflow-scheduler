BEGIN;

ALTER TABLE agents ADD COLUMN process_instance_id uuid
    CHECK (process_instance_id IS NULL OR process_instance_id <> '00000000-0000-0000-0000-000000000000');
ALTER TABLE drain_requests ADD COLUMN released_process_instance_id uuid
    CHECK (released_process_instance_id IS NULL OR released_process_instance_id <> '00000000-0000-0000-0000-000000000000');

COMMIT;
