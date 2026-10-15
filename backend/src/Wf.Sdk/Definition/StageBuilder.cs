namespace Wf.Sdk;

public sealed class StageBuilder
{
    private readonly NodeBuilder node;
    internal StageBuilder(NodeBuilder node) => this.node = node;

    public StageBuilder Task(string key, Action<TaskContext> execute)
    {
        node.Task(key, execute);
        return this;
    }

    public StageBuilder Task(string key, Func<TaskContext, Task> execute)
    {
        node.Task(key, execute);
        return this;
    }
}
