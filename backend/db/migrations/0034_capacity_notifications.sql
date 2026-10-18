BEGIN;

CREATE TRIGGER live_machine_capacity_change AFTER INSERT OR UPDATE OR DELETE ON agent_capacities
FOR EACH ROW EXECUTE FUNCTION notify_live_machines();

COMMIT;
