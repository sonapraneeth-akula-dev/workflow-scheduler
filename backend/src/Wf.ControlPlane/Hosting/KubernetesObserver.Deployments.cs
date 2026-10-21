using System.Text.Json;

namespace Wf.ControlPlane.Hosting;

public sealed record DeploymentCondition(string Type, string Status, string? Reason);
public sealed record DeploymentContainer(string Name, bool Init, string Image, string? ImageId,
    string State, string? Reason, int? ExitCode, int Restarts, bool Ready);
public sealed record DeploymentPod(string Name, string Uid, string Role, string NodeName, string Phase,
    bool Ready, bool Terminating, DateTimeOffset CreatedAt, IReadOnlyList<DeploymentCondition> Conditions,
    IReadOnlyList<DeploymentContainer> Containers);
public sealed record DeploymentWorkload(string Name, string Kind, string Strategy, long Generation,
    long? ObservedGeneration, int Desired, int Updated, int Ready, int Available, int MatchingImages,
    string State, IReadOnlyDictionary<string, string> TargetImages, IReadOnlyList<DeploymentCondition> Conditions);
public sealed record DeploymentJob(string Name, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt,
    string State, int Active, int Succeeded, int Failed, IReadOnlyList<string> Images,
    IReadOnlyList<DeploymentCondition> Conditions);
public sealed record DeploymentSnapshot(DateTimeOffset ObservedAt, int StaleAfterSeconds, string Namespace,
    IReadOnlyList<DeploymentWorkload> Workloads, IReadOnlyList<DeploymentPod> Pods, IReadOnlyList<DeploymentJob> Jobs);

public sealed partial class KubernetesObserver
{
    public async Task<DeploymentSnapshot> ReadDeploymentsAsync(CancellationToken token)
    {
        if (!Enabled) throw new InvalidOperationException("Kubernetes deployment observation is not configured.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var pods = await ReadJson($"api/v1/namespaces/{namespaceName}/pods?limit=500&labelSelector={Uri.EscapeDataString(Selector)}", deadline.Token);
        var podRows = Items(pods).Where(pod => Text(pod.GetProperty("metadata"), "namespace") == namespaceName &&
            pod.GetProperty("metadata").TryGetProperty("labels", out var labels) &&
            Text(labels, "app") is "kardia-api" or "kardia-controller" or "kardia-frontend" or "kardia-worker")
            .Select(ReadDeploymentPod).OrderBy(pod => pod.Name, StringComparer.Ordinal).ToArray();
        if (podRows.Select(pod => pod.Uid).Distinct(StringComparer.Ordinal).Count() != podRows.Length)
            throw new InvalidDataException("Duplicate pod observations.");
        var workloads = new List<DeploymentWorkload>();
        foreach (var (resource, name, kind) in new[]
        {
            ("deployments", "kardia-api", "Deployment"), ("deployments", "kardia-frontend", "Deployment"),
            ("statefulsets", "kardia-controller", "StatefulSet"), ("daemonsets", "kardia-worker", "DaemonSet")
        })
        {
            using var document = await ReadJson($"apis/apps/v1/namespaces/{namespaceName}/{resource}/{name}", deadline.Token);
            var root = document.RootElement;
            var metadata = root.GetProperty("metadata");
            if (Text(metadata, "name") != name || Text(metadata, "namespace") != namespaceName || Text(root, "kind") != kind)
                throw new InvalidDataException("Deployment observation identity does not match its requested workload.");
            var spec = root.GetProperty("spec");
            var status = root.TryGetProperty("status", out var observed) ? observed : default;
            var generation = metadata.GetProperty("generation").GetInt64();
            var observedGeneration = status.ValueKind == JsonValueKind.Object &&
                status.TryGetProperty("observedGeneration", out var value) ? value.GetInt64() : (long?)null;
            var desired = kind == "DaemonSet" ? Number(status, "desiredNumberScheduled") : Number(spec, "replicas", 1);
            var updated = Number(status, kind == "DaemonSet" ? "updatedNumberScheduled" : "updatedReplicas");
            var ready = Number(status, kind == "DaemonSet" ? "numberReady" : "readyReplicas");
            var available = Number(status, kind == "DaemonSet" ? "numberAvailable" :
                kind == "StatefulSet" ? "readyReplicas" : "availableReplicas");
            var strategy = Text(spec.GetProperty(kind == "Deployment" ? "strategy" : "updateStrategy"), "type") ??
                throw new InvalidDataException("Deployment update strategy is missing.");
            var images = Images(spec.GetProperty("template").GetProperty("spec"));
            var currentPods = podRows.Where(pod => pod.Role == name && !pod.Terminating &&
                pod.Phase is not ("Succeeded" or "Failed")).ToArray();
            var matching = currentPods.Count(pod => pod.Containers.Count == images.Count &&
                pod.Containers.All(container => images.TryGetValue((container.Init ? "init/" : "") + container.Name, out var image) &&
                    image == container.Image));
            var conditions = Conditions(status);
            var state = observedGeneration is null || observedGeneration < generation ? "Unknown" :
                conditions.Any(condition => condition.Reason == "ProgressDeadlineExceeded" ||
                    condition.Type == "ReplicaFailure" && condition.Status == "True") ||
                currentPods.Any(pod => pod.Conditions.Any(condition =>
                    condition.Type == "PodScheduled" && condition.Status == "False" && condition.Reason == "Unschedulable") ||
                    pod.Containers.Any(container => container.State == "Waiting" && container.Reason is
                        "CrashLoopBackOff" or "ErrImagePull" or "ImagePullBackOff" or "CreateContainerConfigError" or
                        "ErrImageNeverPull" or "InvalidImageName" or "CreateContainerError" or "RunContainerError")) ? "Blocked" :
                updated >= desired && ready >= desired && available >= desired && matching >= desired &&
                    currentPods.Length == desired ? "InSync" :
                strategy == "OnDelete" && updated < desired && ready >= desired ? "AwaitingReplacement" : "Updating";
            workloads.Add(new(name, kind, strategy, generation, observedGeneration, desired, updated, ready, available,
                matching, state, images, conditions));
        }
        using var jobs = await ReadJson($"apis/batch/v1/namespaces/{namespaceName}/jobs?limit=500", deadline.Token);
        var jobRows = Items(jobs).Where(job => Text(job.GetProperty("metadata"), "namespace") == namespaceName &&
            Name(job).StartsWith("kardia-migrate-", StringComparison.Ordinal)).Select(job =>
        {
            var metadata = job.GetProperty("metadata");
            var status = job.TryGetProperty("status", out var value) ? value : default;
            var conditions = Conditions(status);
            var state = conditions.Any(condition => condition.Type == "Failed" && condition.Status == "True") ? "Failed" :
                conditions.Any(condition => condition.Type == "Complete" && condition.Status == "True") ? "Succeeded" :
                Number(status, "active") > 0 ? "Running" : "Pending";
            return new DeploymentJob(Name(job), metadata.GetProperty("creationTimestamp").GetDateTimeOffset(),
                Text(status, "completionTime") is { } completed ? DateTimeOffset.Parse(completed,
                    System.Globalization.CultureInfo.InvariantCulture) : null,
                state, Number(status, "active"), Number(status, "succeeded"), Number(status, "failed"),
                Images(job.GetProperty("spec").GetProperty("template").GetProperty("spec")).Values.ToArray(), conditions);
        }).OrderByDescending(job => job.CreatedAt).ThenBy(job => job.Name, StringComparer.Ordinal).Take(20).ToArray();
        return new(time.GetUtcNow(), 20, namespaceName!, workloads, podRows, jobRows);
    }

    private static DeploymentPod ReadDeploymentPod(JsonElement pod)
    {
        var metadata = pod.GetProperty("metadata");
        var spec = pod.GetProperty("spec");
        var status = pod.GetProperty("status");
        var conditions = Conditions(status);
        var containers = new List<DeploymentContainer>();
        foreach (var (field, statusField, init) in new[]
        {
            ("initContainers", "initContainerStatuses", true), ("containers", "containerStatuses", false)
        })
        {
            if (!spec.TryGetProperty(field, out var entries)) continue;
            var statuses = status.TryGetProperty(statusField, out var rows) ?
                rows.EnumerateArray().ToDictionary(row => RequiredText(row, "name"), StringComparer.Ordinal) : [];
            foreach (var entry in entries.EnumerateArray())
            {
                var name = RequiredText(entry, "name");
                var present = statuses.TryGetValue(name, out var item);
                var state = present && item.TryGetProperty("state", out var current) ? current : default;
                var kind = new[] { "waiting", "running", "terminated" }.FirstOrDefault(key =>
                    state.ValueKind == JsonValueKind.Object && state.TryGetProperty(key, out _));
                var detail = kind is null ? default : state.GetProperty(kind);
                containers.Add(new(name, init, RequiredText(entry, "image"), Text(item, "imageID"),
                    kind switch { "waiting" => "Waiting", "running" => "Running", "terminated" => "Terminated", _ => "Unknown" },
                    Text(detail, "reason"), kind == "terminated" ? Number(detail, "exitCode") : null,
                    Number(item, "restartCount"), present && item.TryGetProperty("ready", out var ready) && ready.GetBoolean()));
            }
        }
        return new(Name(pod), RequiredText(metadata, "uid"), RequiredText(metadata.GetProperty("labels"), "app"),
            Text(spec, "nodeName") ?? "Unscheduled", RequiredText(status, "phase"),
            conditions.Any(condition => condition.Type == "Ready" && condition.Status == "True"),
            Text(metadata, "deletionTimestamp") is not null, metadata.GetProperty("creationTimestamp").GetDateTimeOffset(),
            conditions, containers);
    }

    private static Dictionary<string, string> Images(JsonElement spec)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, prefix) in new[] { ("initContainers", "init/"), ("containers", "") })
            if (spec.TryGetProperty(field, out var entries))
                foreach (var entry in entries.EnumerateArray())
                    result.Add(prefix + RequiredText(entry, "name"), RequiredText(entry, "image"));
        if (result.Count == 0) throw new InvalidDataException("Workload images are missing.");
        return result;
    }

    private static DeploymentCondition[] Conditions(JsonElement status) => status.ValueKind == JsonValueKind.Object &&
        status.TryGetProperty("conditions", out var conditions) ? conditions.EnumerateArray()
        .Select(condition => new DeploymentCondition(RequiredText(condition, "type"), RequiredText(condition, "status"),
            Text(condition, "reason"))).ToArray() : [];
    private static string? Text(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static string RequiredText(JsonElement element, string property) => Text(element, property) ??
        throw new InvalidDataException($"Kubernetes observation is missing {property}.");
    private static int Number(JsonElement element, string property, int fallback = 0)
    {
        var number = element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.GetInt32() : fallback;
        return number >= 0 ? number : throw new InvalidDataException("Negative Kubernetes observation count.");
    }
}
