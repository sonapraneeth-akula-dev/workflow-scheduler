BEGIN;

CREATE FUNCTION notify_live_machines() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD IS NOT DISTINCT FROM NEW THEN
        RETURN NULL;
    END IF;
    -- One invalidation per transaction, delivered only after commit.
    PERFORM pg_notify('live_machines', '*');
    RETURN NULL;
END
$$;

CREATE TRIGGER live_machine_change AFTER INSERT OR UPDATE OR DELETE ON agents
FOR EACH ROW EXECUTE FUNCTION notify_live_machines();
CREATE TRIGGER live_machine_reservation_change AFTER INSERT OR UPDATE OR DELETE ON reservations
FOR EACH ROW EXECUTE FUNCTION notify_live_machines();

COMMIT;
