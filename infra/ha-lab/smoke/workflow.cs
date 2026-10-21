#:project ..\..\..\backend\src\Wf.Sdk\Wf.Sdk.csproj
#:property PublishAot=false
using Wf.Contracts;
using Wf.Sdk;

var workflow = Workflow.Define("ha-lab/acceptance/recovery", "1.0.2", w =>
{
    w.Type("acceptance").Metadata("environment", "dev")
        .RunsOn(Os.Linux, Machines.Pool("kardia-lab"))
        .RunTimeout(TimeSpan.FromMinutes(15))
        .Defaults(p => p.Timeout(TimeSpan.FromMinutes(10)))
        .Variables(v => v.Int("steps", 24));
    foreach (var node in new[] { "alpha", "beta", "gamma" })
        w.Node(node, n => n.Parallel()
            .Task("first", Work)
            .Task("second", Work));
});
return await workflow.RunAsync(args);

static async Task Work(TaskContext context)
{
    var steps = context.Var<int>("steps");
    if (steps is < 1 or > 60)
        throw new TaskFailureException("steps must be 1..60.", retryable: false,
            category: Wf.Contracts.Definition.FailureCategory.ValidationError);
    var completed = context.Checkpoint.TryLoad<int>();
    if (completed < 0 || completed > steps)
        throw new InvalidDataException("Checkpoint progress is outside the requested work.");
    context.Log($"START task={context.TaskKey} attempt={context.Attempt.Number} resumed={completed} machine={Environment.MachineName}");
    for (var step = completed + 1; step <= steps; step++)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), context.CancellationToken);
        try { await context.Checkpoint.SaveAsync(step, context.CancellationToken); }
        catch (TaskFailureException error) when (error.InnerException is HttpRequestException transport)
        {
            context.Log($"CHECKPOINT_TRANSPORT error={transport.HttpRequestError} cause={transport.InnerException?.GetType().Name}");
            throw;
        }
        context.Log($"PROGRESS task={context.TaskKey} attempt={context.Attempt.Number} completed={step}/{steps}");
    }
    context.Outputs["completedSteps"] = steps;
    context.Outputs["resumedSteps"] = completed;
    context.Outputs["attempt"] = checked((int)context.Attempt.Number);
    context.Outputs["machine"] = Environment.MachineName;
}
