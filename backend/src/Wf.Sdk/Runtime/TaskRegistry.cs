using System.Collections.Immutable;

namespace Wf.Sdk;

public sealed class TaskRegistry
{
    private readonly ImmutableDictionary<string, Func<TaskContext, Task>> tasks;

    public TaskRegistry(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        tasks = workflow.Tasks;
    }

    public Task ExecuteAsync(string key, TaskContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(context);
        if (!tasks.TryGetValue(key, out var execute))
            throw new KeyNotFoundException($"Task '{key}' is not registered.");
        context.CancellationToken.ThrowIfCancellationRequested();
        return execute(context.ForTask(key));
    }
}
