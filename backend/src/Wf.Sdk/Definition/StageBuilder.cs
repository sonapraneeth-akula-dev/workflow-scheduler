using Wf.Contracts.Definition;

namespace Wf.Sdk;

public sealed class StageBuilder
{
    private readonly NodeBuilder node;
    internal StageBuilder(NodeBuilder node) => this.node = node;

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
}
