BEGIN;
ALTER TABLE task_attempts ADD COLUMN outputs json,
    ADD CONSTRAINT task_outputs_object_bounded CHECK (
        outputs IS NULL OR (json_typeof(outputs) = 'object' AND octet_length(outputs::text) <= 65536));
COMMIT;
