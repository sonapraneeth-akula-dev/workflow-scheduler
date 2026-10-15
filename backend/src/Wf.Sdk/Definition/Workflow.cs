namespace Wf.Sdk;

public static class Workflow
{
    public static WorkflowDefinition Define(string name, string version, Action<WorkflowBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new WorkflowBuilder(name, version);
        configure(builder);
        return builder.Build();
    }
}
