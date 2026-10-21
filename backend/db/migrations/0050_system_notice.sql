BEGIN;

CREATE TABLE system_notice (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
    message text,
    severity text,
    expires_at timestamptz,
    CHECK (
        (message IS NULL AND severity IS NULL AND expires_at IS NULL) OR
        (message IS NOT NULL AND char_length(message) BETWEEN 1 AND 2000 AND
            severity IS NOT NULL AND severity IN ('Info', 'Warning', 'Critical'))
    )
);

INSERT INTO system_notice(singleton) VALUES(true);

COMMIT;
