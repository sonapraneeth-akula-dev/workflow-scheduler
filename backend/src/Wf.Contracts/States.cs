namespace Wf.Contracts;

public enum RunStatus { Queued, Running, Aborting, Succeeded, Failed, Aborted, TimedOut }

public enum ExecutionStatus
{
    Pending, Ready, Dispatched, Running, Retrying,
    Succeeded, SoftFailed, Failed, TimedOut, Cancelled, Skipped, Reused
}
