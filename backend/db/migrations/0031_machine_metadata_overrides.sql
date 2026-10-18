BEGIN;

ALTER TABLE agents ADD COLUMN labels_override text[];
ALTER TABLE agents ADD COLUMN pool_override text
    CHECK (pool_override IS NULL OR length(pool_override) BETWEEN 1 AND 200);

COMMIT;
