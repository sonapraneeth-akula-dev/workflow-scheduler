using Wf.Contracts.Definition;

namespace Wf.Sdk;

public sealed class StageBuilder
{
    private readonly NodeBuilder node;
    internal StageBuilder(NodeBuilder node) => this.node = node;

    public StageBuilder Timeout(TimeSpan value) { node.Timeout(value); return this; }
    public StageBuilder Retry(int count, BackoffPolicy? backoff = null, IEnumerable<FailureCategory>? on = null)
    { node.Retry(count, backoff, on); return this; }
    public StageBuilder NoAutoRetry() { node.NoAutoRetry(); return this; }
    public StageBuilder RunsOn(params PlacementSelector[] selectors) { node.RunsOn(selectors); return this; }
    public StageBuilder RunsOn(Func<PlacementVariables, PlacementSelector> configure) { node.RunsOn(configure); return this; }
    public StageBuilder Affinity(PlacementAffinity value) { node.Affinity(value); return this; }
    public StageBuilder WhenUnavailable(WhenUnavailable value) { node.WhenUnavailable(value); return this; }

    public StageBuilder Task(string key, Action<TaskContext> execute, FailurePolicy failure = FailurePolicy.Hard)
    {
        node.Task(key, execute, failure);
        return this;
    }

    public StageBuilder Task(string key, Func<TaskContext, Task> execute, FailurePolicy failure = FailurePolicy.Hard)
    {
        node.Task(key, execute, failure);
        return this;
    }

    public StageBuilder Task(string key, Action<TaskContext> execute, Action<PolicyBuilder> configure,
        FailurePolicy failure = FailurePolicy.Hard)
    {
        node.Task(key, execute, configure, failure);
        return this;
    }

    public StageBuilder Task(string key, Func<TaskContext, Task> execute, Action<PolicyBuilder> configure,
        FailurePolicy failure = FailurePolicy.Hard)
    {
        node.Task(key, execute, configure, failure);
        return this;
    }

    public StageBuilder Task(string key, HttpOperationTask operation, FailurePolicy failure = FailurePolicy.Hard)
    {
        node.Task(key, operation, failure);
        return this;
    }

    public StageBuilder Task(string key, HttpOperationTask operation, Action<PolicyBuilder> configure,
        FailurePolicy failure = FailurePolicy.Hard)
    {
        node.Task(key, operation, configure, failure);
        return this;
    }
}
