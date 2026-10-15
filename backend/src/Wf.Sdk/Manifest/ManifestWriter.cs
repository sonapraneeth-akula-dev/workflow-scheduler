using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;

namespace Wf.Sdk;

public static class ManifestWriter
{
    public static WorkflowManifest Create(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var sdkVersion = typeof(Workflow).Assembly.GetName().Version?.ToString(3) ??
            throw new InvalidOperationException("SDK assembly version is unavailable.");
        return new(sdkVersion, workflow.Name, workflow.Version, workflow.Metadata, workflow.Variables,
            LifecycleStages.Before.Concat(LifecycleStages.After).Where(key => workflow.Nodes.Any(n => n.Key == key))
                .ToImmutableArray(),
            workflow.Nodes, workflow.RunTimeout, workflow.Placement);
    }

    public static byte[] WriteUtf8(WorkflowDefinition workflow) =>
        JsonSerializer.SerializeToUtf8Bytes(Create(workflow), WorkflowManifestJsonContext.Default.WorkflowManifest);

    public static string Write(WorkflowDefinition workflow) => Encoding.UTF8.GetString(WriteUtf8(workflow));

    public static Task WriteAsync(WorkflowDefinition workflow, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return JsonSerializer.SerializeAsync(destination, Create(workflow),
            WorkflowManifestJsonContext.Default.WorkflowManifest, cancellationToken);
    }
}
