using System.Collections.Immutable;
using Wf.Contracts.Definition;

namespace Wf.Validation;

public enum PolicyDiagnosticSeverity { Error, Warning }
public sealed record PolicyDiagnostic(string NodeKey, string TaskKey, PolicyDiagnosticSeverity Severity, string Message);

public static class PolicyRules
{
    public static ImmutableArray<PolicyDiagnostic> Validate(IEnumerable<NodeDefinition> nodes, TimeSpan runTimeout)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (runTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(runTimeout), "Run timeout must be positive.");

        var diagnostics = ImmutableArray.CreateBuilder<PolicyDiagnostic>();
        foreach (var node in nodes)
        foreach (var task in node.Tasks)
        {
            var policy = task.Policy;
            var retry = policy.Retry;
            var validBackoff = retry.Retries <= 10 && retry.Backoff.MaxDelay <= TimeSpan.FromMinutes(10);
            if (retry.Retries > 10)
                Add(PolicyDiagnosticSeverity.Error, "Retry count exceeds the limit of 10 retries.");
            if (retry.Backoff.MaxDelay > TimeSpan.FromMinutes(10))
                Add(PolicyDiagnosticSeverity.Error, "Maximum backoff delay exceeds the limit of 10 minutes.");
            if (node.Timeout is { } nodeTimeout && policy.AttemptTimeout > nodeTimeout)
                Add(PolicyDiagnosticSeverity.Error, $"Attempt timeout exceeds node timeout limit {nodeTimeout:c}.");
            if (policy.AttemptTimeout > runTimeout)
                Add(PolicyDiagnosticSeverity.Error, $"Attempt timeout exceeds run timeout limit {runTimeout:c}.");

            if (validBackoff)
            {
                decimal worstTicks = (decimal)policy.AttemptTimeout.Ticks *
                    (1 + retry.Retries + (policy.AutoRetry ? 2 : 0));
                for (var index = 0; index < retry.Retries; index++)
                {
                    var nominal = retry.Backoff.Kind == BackoffKind.Fixed ? retry.Backoff.Delay.Ticks :
                        Math.ScaleB(retry.Backoff.Delay.Ticks, index);
                    var capped = Math.Min(nominal, retry.Backoff.MaxDelay.Ticks);
                    worstTicks += (long)Math.Min(retry.Backoff.MaxDelay.Ticks, Math.Round(capped * 1.2));
                }
                if (worstTicks > runTimeout.Ticks)
                    Add(PolicyDiagnosticSeverity.Warning,
                        $"Worst-case attempts and backoff (including enabled platform retries) exceed run timeout limit {runTimeout:c}.");
            }

            void Add(PolicyDiagnosticSeverity severity, string message) =>
                diagnostics.Add(new(node.Key, task.Key, severity, $"Task '{node.Key}/{task.Key}': {message}"));
        }
        return diagnostics.ToImmutable();
    }
}
