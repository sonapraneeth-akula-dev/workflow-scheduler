using Wf.Agent.Secrets;
using Wf.Contracts.Agent.V1;

namespace Wf.Agent.Execution;

public static class OutputsGuard
{
    public static TaskCompleted Apply(TaskCompleted result, SecretMasker masker) => masker.RedactResult(result);
}
