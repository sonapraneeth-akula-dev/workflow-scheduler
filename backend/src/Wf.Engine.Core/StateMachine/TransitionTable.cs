using System.Collections.Frozen;
using Wf.Contracts;

namespace Wf.Engine.Core.StateMachine;

public static class TransitionTable
{
    private static readonly FrozenSet<(RunStatus From, RunStatus To)> RunTransitions =
        new (RunStatus, RunStatus)[]
        {
            (RunStatus.Queued, RunStatus.Running), (RunStatus.Queued, RunStatus.Aborted),
            (RunStatus.Running, RunStatus.Succeeded), (RunStatus.Running, RunStatus.Failed),
            (RunStatus.Running, RunStatus.Aborting), (RunStatus.Running, RunStatus.TimedOut),
            (RunStatus.Aborting, RunStatus.Aborted)
        }.ToFrozenSet();

    private static readonly FrozenSet<(ExecutionStatus From, ExecutionStatus To)> ExecutionTransitions =
        new Dictionary<ExecutionStatus, ExecutionStatus[]>
        {
            [ExecutionStatus.Pending] = [ExecutionStatus.Ready, ExecutionStatus.Skipped, ExecutionStatus.Cancelled, ExecutionStatus.Reused],
            [ExecutionStatus.Ready] = [ExecutionStatus.Dispatched, ExecutionStatus.Failed, ExecutionStatus.Skipped, ExecutionStatus.Cancelled, ExecutionStatus.TimedOut],
            [ExecutionStatus.Dispatched] = [ExecutionStatus.Running, ExecutionStatus.Retrying, ExecutionStatus.Failed, ExecutionStatus.TimedOut, ExecutionStatus.Cancelled],
            [ExecutionStatus.Running] = [ExecutionStatus.Succeeded, ExecutionStatus.SoftFailed, ExecutionStatus.Failed, ExecutionStatus.TimedOut, ExecutionStatus.Cancelled, ExecutionStatus.Retrying],
            [ExecutionStatus.Retrying] = [ExecutionStatus.Dispatched, ExecutionStatus.Failed, ExecutionStatus.TimedOut, ExecutionStatus.Cancelled, ExecutionStatus.Skipped]
        }.SelectMany(pair => pair.Value.Select(next => (pair.Key, next))).ToFrozenSet();

    public static bool CanTransition(RunStatus from, RunStatus to) => RunTransitions.Contains((from, to));
    public static bool CanTransition(ExecutionStatus from, ExecutionStatus to) => ExecutionTransitions.Contains((from, to));

    public static bool IsTerminal(RunStatus status) =>
        Enum.IsDefined(status) && !RunTransitions.Any(edge => edge.From == status);
    public static bool IsTerminal(ExecutionStatus status) =>
        Enum.IsDefined(status) && !ExecutionTransitions.Any(edge => edge.From == status);

    public static void Require(RunStatus from, RunStatus to, Action<string> log) =>
        Require(CanTransition(from, to), $"Run transition {from} -> {to} is invalid.", log);
    public static void Require(ExecutionStatus from, ExecutionStatus to, Action<string> log) =>
        Require(CanTransition(from, to), $"Execution transition {from} -> {to} is invalid.", log);

    private static void Require(bool allowed, string message, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (allowed)
            return;
        log(message);
        throw new InvalidOperationException(message);
    }
}
