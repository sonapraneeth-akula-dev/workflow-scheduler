using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api;

public sealed record WorkflowVersionPlan(Guid VersionId, string WorkflowId, string Version, TimeSpan RunTimeout,
    string[] Stages, TaskPlanPlacement? Placement, TaskPlanNode[] Nodes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkflowVersionRegistration? Registration = null)
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
                        task.Policy.AutoRetry, task.Policy.Limits),
                    TaskPlanPlacement.From(PlacementPolicy.Inherit(placement, task.Placement)))).ToArray());
        }).ToArray());
}

public sealed record WorkflowVersionRegistration(string GitCommit, string GitAuthor, string RegisteredBy,
    string? ChangeNote, string SdkVersion, DateTimeOffset RegisteredAt);
public sealed record WorkflowVersionSummary(Guid VersionId, string WorkflowId, string Version,
    WorkflowVersionRegistration Registration);
public sealed record WorkflowVersionPage(WorkflowVersionSummary[] Items, string? NextCursor,
    Guid? ActiveVersionId, Guid? LatestVersionId);
public sealed record WorkflowVersionPosition(DateTimeOffset At, Guid Id);
public sealed record WorkflowVersionActivation(string WorkflowId, Guid ActiveVersionId, Guid? LatestVersionId);

public sealed record WorkflowVersionsQuery(string WorkflowId, int Limit, WorkflowVersionPosition? After)
{
    public static WorkflowVersionsQuery Parse(string workflowId, IQueryCollection query)
    {
        if (!WorkflowIdentity.TryGetTypeScope(workflowId, out _))
            throw new ArgumentException("Use an exact team/type/workflow identity.");
        foreach (var entry in query)
            if (entry.Key is not ("limit" or "cursor") || entry.Value.Count != 1)
                throw new ArgumentException("Use only limit and cursor, each occurring exactly once.");
        var limit = 50;
        if (query.TryGetValue("limit", out var size) &&
            (!int.TryParse(size[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
             limit is < 1 or > 100 || size[0] != limit.ToString(CultureInfo.InvariantCulture)))
            throw new ArgumentException("limit must be a canonical whole number from 1 to 100.");
        var result = new WorkflowVersionsQuery(workflowId, limit, null);
        if (!query.TryGetValue("cursor", out var value)) return result;
        var cursor = value[0]!;
        if (cursor.Length is < 100 or > 256) throw InvalidCursor();
        string decoded;
        try { decoded = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(cursor)); }
        catch (Exception error) when (error is FormatException or DecoderFallbackException) { throw InvalidCursor(); }
        var parts = decoded.Split('\n');
        if (parts.Length != 4 || parts[0] != "v1" ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks < DateTimeOffset.MinValue.Ticks || ticks > DateTimeOffset.MaxValue.Ticks || ticks % 10 != 0 ||
            parts[1] != ticks.ToString(CultureInfo.InvariantCulture) ||
            !Guid.TryParseExact(parts[2], "D", out var id) || id == Guid.Empty || parts[2] != id.ToString("D") ||
            parts[2][14] != '7' || parts[2][19] is not ('8' or '9' or 'a' or 'b') ||
            parts[3] != result.Binding() || WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(decoded)) != cursor)
            throw InvalidCursor();
        return result with { After = new(new(ticks, TimeSpan.Zero), id) };
    }

    public string Cursor(DateTimeOffset at, Guid id) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("v1\n" +
            at.UtcTicks.ToString(CultureInfo.InvariantCulture) + "\n" + id.ToString("D") + "\n" + Binding()));

    private string Binding() => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(WorkflowId + "\n" + Limit.ToString(CultureInfo.InvariantCulture))));

    private static ArgumentException InvalidCursor() => new("The version cursor is invalid; keep the original workflow and limit.");
}

public sealed record TaskPlanNode(string Key, DependencyDefinition[] Dependencies, NodeExecutionMode Mode,
    int? MaxParallel, FailurePolicy Failure, TimeSpan? Timeout, TaskPlanPlacement? Placement, TaskPlanTask[] Tasks);
public sealed record TaskPlanTask(string Key, TaskAction? Action, string? SecretReference, TimeSpan? RequestTimeout,
    FailurePolicy Failure, TaskPlanExecutionPolicy Policy, TaskPlanPlacement? Placement);
public sealed record TaskPlanExecutionPolicy(TimeSpan AttemptTimeout, TaskPlanRetry Retry, bool AutoRetry,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TaskResourceLimits? Limits = null);
public sealed record TaskPlanRetry(int Retries, BackoffPolicy Backoff, FailureCategory[] On);
public sealed record TaskPlanSelector(SelectorValue? Os, SelectorValue? Pool, LabelSelector[] Labels,
    SelectorValue[] Machines, Guid[] MachineIds, int? Count);
public sealed record TaskPlanPlacement(TaskPlanSelector? Selector, PlacementAffinity? Affinity)
{
    public static TaskPlanPlacement? From(PlacementPolicy? placement) => placement is null ? null :
        new(placement.Selector is not { } selector ? null :
            new(selector.Os, selector.Pool, selector.Labels.ToArray(), selector.Machines.ToArray(),
                selector.MachineIds.ToArray(), selector.Count), placement.Affinity);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkflowVersionPlan))]
[JsonSerializable(typeof(WorkflowVersionPage))]
[JsonSerializable(typeof(WorkflowVersionActivation))]
[JsonSerializable(typeof(Wf.ControlPlane.Versions.WorkflowVersionDiff))]
public partial class VersionsJsonContext : JsonSerializerContext;
