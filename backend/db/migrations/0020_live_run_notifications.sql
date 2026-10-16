BEGIN;

CREATE FUNCTION notify_live_run() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    -- Identical payloads collapse within a transaction; delivery occurs only after COMMIT.
    PERFORM pg_notify('live_runs', NEW.id::text || ':' ||
        CASE WHEN TG_OP = 'INSERT' THEN 0 ELSE OLD.last_seq + 1 END::text);
    RETURN NULL;
END
$$;

CREATE TRIGGER live_run_insert AFTER INSERT ON runs
FOR EACH ROW EXECUTE FUNCTION notify_live_run();
CREATE TRIGGER live_run_update AFTER UPDATE OF last_seq, status ON runs
FOR EACH ROW WHEN (OLD.last_seq IS DISTINCT FROM NEW.last_seq OR OLD.status IS DISTINCT FROM NEW.status)
EXECUTE FUNCTION notify_live_run();

COMMIT;
