BEGIN;

CREATE TABLE retention_config (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    default_days int NOT NULL DEFAULT 90 CHECK (default_days BETWEEN 1 AND 36500),
    audit_days int NOT NULL DEFAULT 365 CHECK (audit_days BETWEEN 365 AND 36500)
);

INSERT INTO retention_config(singleton) VALUES(true);

COMMIT;
