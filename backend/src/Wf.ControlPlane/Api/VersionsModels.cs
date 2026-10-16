using System.Text.Json.Serialization;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;

namespace Wf.ControlPlane.Api;

public sealed record WorkflowVersionPlan(Guid VersionId, string WorkflowId, string Version, TimeSpan RunTimeout,
    string[] Stages, TaskPlanPlacement? Placement, TaskPlanNode[] Nodes)
{
    public static WorkflowVersionPlan From(Guid id, WorkflowManifest manifest) => new(id, manifest.Name,
        manifest.Version, manifest.RunTimeout, manifest.Stages.ToArray(), TaskPlanPlacement.From(manifest.Placement),
        manifest.Nodes.Select(node =>
        {
            var placement = PlacementPolicy.Inherit(manifest.Placement, node.Placement);
            return new TaskPlanNode(node.Key, node.Dependencies.ToArray(), node.Mode,
                node.MaxParallel, node.Failure, node.Timeout, TaskPlanPlacement.From(placement),
                node.Tasks.Select(task => new TaskPlanTask(task.Key, task.Action ??
                    (task.HttpOperation is { } operation
                        ? new(TaskActionKind.HttpOperation, "POST " + operation.SubmissionUrl) : null),
                    task.HttpOperation?.SecretReference, task.HttpOperation?.RequestTimeout,
                    task.Failure, new(task.Policy.AttemptTimeout,
                        new(task.Policy.Retry.Retries, task.Policy.Retry.Backoff, task.Policy.Retry.On.ToArray()),
                        task.Policy.AutoRetry),
                    TaskPlanPlacement.From(PlacementPolicy.Inherit(placement, task.Placement)))).ToArray());
        }).ToArray());
}

public sealed record TaskPlanNode(string Key, DependencyDefinition[] Dependencies, NodeExecutionMode Mode,
    int? MaxParallel, FailurePolicy Failure, TimeSpan? Timeout, TaskPlanPlacement? Placement, TaskPlanTask[] Tasks);
public sealed record TaskPlanTask(string Key, TaskAction? Action, string? SecretReference, TimeSpan? RequestTimeout,
    FailurePolicy Failure, TaskPlanExecutionPolicy Policy, TaskPlanPlacement? Placement);
public sealed record TaskPlanExecutionPolicy(TimeSpan AttemptTimeout, TaskPlanRetry Retry, bool AutoRetry);
public sealed record TaskPlanRetry(int Retries, BackoffPolicy Backoff, FailureCategory[] On);
public sealed record TaskPlanSelector(SelectorValue? Os, SelectorValue? Pool, LabelSelector[] Labels, SelectorValue[] Machines);
public sealed record TaskPlanPlacement(TaskPlanSelector? Selector, PlacementAffinity? Affinity)
{
    public static TaskPlanPlacement? From(PlacementPolicy? placement) => placement is null ? null :
        new(placement.Selector is not { } selector ? null :
            new(selector.Os, selector.Pool, selector.Labels.ToArray(), selector.Machines.ToArray()), placement.Affinity);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkflowVersionPlan))]
public partial class VersionsJsonContext : JsonSerializerContext;
