BEGIN;

CREATE FUNCTION enqueue_terminal_notifications() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status NOT BETWEEN 3 AND 6 OR NEW.ended_at IS NULL THEN RETURN NEW; END IF;
    IF TG_OP='UPDATE' THEN
        IF OLD.status BETWEEN 3 AND 6 AND OLD.ended_at IS NOT NULL THEN RETURN NEW; END IF;
    END IF;
    INSERT INTO notification_outbox(id,subscription_id,run_day,run_id,payload)
    SELECT gen_random_uuid(),s.id,NEW.run_day,NEW.id,
        jsonb_build_object(
            'specversion','1.0','id',NEW.id::text,'source','urn:kardia:runs',
            'type',CASE WHEN NEW.status=3 THEN 'io.kardia.run.completed' ELSE 'io.kardia.run.failed' END,
            'subject',NEW.workflow_id,
            'time',to_char(NEW.ended_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
            'datacontenttype','application/json',
            'data',jsonb_build_object('runId',NEW.id,'workflowId',NEW.workflow_id,
                'versionId',NEW.version_id,'seriesId',NEW.series_id,
                'status',CASE NEW.status WHEN 3 THEN 'Succeeded' WHEN 4 THEN 'Failed'
                    WHEN 5 THEN 'Aborted' WHEN 6 THEN 'TimedOut' END,
                'endedAt',to_char(NEW.ended_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"')))
    FROM subscriptions s
    WHERE s.type_scope=split_part(NEW.workflow_id,'/',1)||'/'||split_part(NEW.workflow_id,'/',2)
        AND (s.scope_kind='type' OR s.scope_kind='workflow' AND s.scope_id=NEW.workflow_id
            OR s.scope_kind='schedule' AND s.series_id=NEW.series_id)
        AND ('Completed'=ANY(s.events) OR NEW.status<>3 AND 'Failed'=ANY(s.events))
    ON CONFLICT(subscription_id,run_day,run_id) DO NOTHING;
    RETURN NEW;
END $$;

-- Group commit publishes status before its terminal snapshot supplies ended_at.
-- The second update and ordinary RunStore writes use the same transactional trigger.
CREATE TRIGGER terminal_notifications AFTER INSERT OR UPDATE OF status,ended_at ON runs
    FOR EACH ROW EXECUTE FUNCTION enqueue_terminal_notifications();

COMMIT;
