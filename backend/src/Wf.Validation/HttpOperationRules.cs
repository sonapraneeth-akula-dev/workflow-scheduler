using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;

namespace Wf.Validation;

public static class HttpOperationRules
{
    public static readonly ValidationRule InvalidContract = new("WFV021",
        "The HTTP operation contract or its effective execution policy is invalid.",
        "Use bounded status, cancel, deduplication and authoritative lookup contracts, safe HTTP(S) URLs on trusted origins, and a valid effective retry policy.");

    public static readonly ValidationRule UnsafeRetry = new("WFV022",
        "An HTTP operation without submission deduplication or authoritative lookup cannot be retried automatically.",
        "Set .NoAutoRetry() and .Retry(0) on this task or its inherited policy, or declare a remote deduplication or authoritative lookup guarantee.");

    public static readonly ValidationRule InsufficientRetention = new("WFV023",
        "The HTTP operation service retention does not cover the retry window.",
        "Guarantee status and declared deduplication/lookup retention for at least the run timeout, or disable platform retries with .NoAutoRetry() and author retries with .Retry(0).");

    public static ImmutableArray<ValidationError> Validate(WorkflowManifest manifest, string file = "workflow.cs",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Validate(JsonSerializer.SerializeToElement(manifest,
            WorkflowManifestJsonContext.Default.WorkflowManifest), file, cancellationToken);
    }

    public static ImmutableArray<ValidationError> Validate(JsonElement manifest, string file = "workflow.cs",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var errors = ImmutableArray.CreateBuilder<ValidationError>();
        if (manifest.ValueKind != JsonValueKind.Object || !manifest.TryGetProperty("nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array)
            return [];
        var nodeIndex = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("tasks", out var tasks) &&
                tasks.ValueKind == JsonValueKind.Array)
            {
                var taskIndex = 0;
                foreach (var task in tasks.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = $"$.nodes[{nodeIndex}].tasks[{taskIndex++}]";
                    if (task.ValueKind != JsonValueKind.Object || !task.TryGetProperty("httpOperation", out var operation) ||
                        operation.ValueKind == JsonValueKind.Null)
                        continue;
                    Check(task, operation, path);
                }
            }
            nodeIndex++;
        }
        return errors.OrderBy(error => error.RuleId, StringComparer.Ordinal)
            .ThenBy(error => error.Path, StringComparer.Ordinal).ToImmutableArray();

        void Check(JsonElement task, JsonElement operation, string path)
        {
            HttpOperationDefinition? contract;
            try
            {
                if (HasDuplicateProperties(operation))
                    throw new InvalidDataException();
                contract = operation.Deserialize(WorkflowManifestJsonContext.Default.HttpOperationDefinition);
                if (contract is null)
                    throw new InvalidDataException();
            }
            catch (Exception error) when (error is ArgumentException or JsonException or InvalidDataException)
            {
                errors.Add(InvalidContract.CreateError(file, path + ".httpOperation"));
                return;
            }

            // Policies in --describe are already resolved by workflow/node/task precedence.
            var autoRetry = true;
            var retries = 0;
            if (task.TryGetProperty("policy", out var policy))
            {
                if (policy.ValueKind != JsonValueKind.Object || HasDuplicateProperties(policy) ||
                    policy.TryGetProperty("autoRetry", out var automatic) &&
                    automatic.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errors.Add(InvalidContract.CreateError(file, path + ".policy"));
                    return;
                }
                if (policy.TryGetProperty("autoRetry", out automatic))
                    autoRetry = automatic.GetBoolean();
                if (policy.TryGetProperty("retry", out var retry) &&
                    (retry.ValueKind != JsonValueKind.Object ||
                     retry.TryGetProperty("retries", out var count) &&
                     (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out retries) || retries is < 0 or > 10)))
                {
                    errors.Add(InvalidContract.CreateError(file, path + ".policy"));
                    return;
                }
            }
            if (!autoRetry && retries == 0)
                return;
            if (contract.Deduplication is null && contract.Lookup is null)
            {
                errors.Add(UnsafeRetry.CreateError(file, path + ".policy"));
                return;
            }
            if (!manifest.TryGetProperty("runTimeout", out var timeout) || timeout.ValueKind != JsonValueKind.String ||
                !TimeSpan.TryParseExact(timeout.GetString(), "c", CultureInfo.InvariantCulture, out var window) ||
                window <= TimeSpan.Zero)
                errors.Add(InvalidContract.CreateError(file, "$.runTimeout"));
            else if (contract.Status.Retention < window ||
                contract.Deduplication is { Retention: var deduplicationRetention } && deduplicationRetention < window ||
                contract.Lookup is { Retention: var lookupRetention } && lookupRetention < window)
                errors.Add(InsufficientRetention.CreateError(file, path + ".httpOperation"));
        }
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                    return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                if (HasDuplicateProperties(item))
                    return true;
        return false;
    }
}
