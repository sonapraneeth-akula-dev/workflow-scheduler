using System.Text.Json;
using Google.Protobuf;
using Wf.Agent.Secrets;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Outputs;

namespace Wf.Agent.Execution;

public static class OutputsGuard
{
    public static TaskCompleted Apply(TaskCompleted result, SecretMasker masker)
    {
        if (TaskOutputJson.Error(result.OutputsJson.Memory) is { } error)
            return TaskOutputJson.Reject(result, error);
        var safe = result.Clone();
        if (safe.Error is not null) safe.Error.Message = masker.Redact(safe.Error.Message);
        if (safe.OutputsJson.IsEmpty) return safe;
        try
        {
            using var document = JsonDocument.Parse(safe.OutputsJson.Memory);
            var masked = masker.RedactJson(document.RootElement);
            // Keep the original representation when masking changes nothing: escaped Unicode can expand beyond 64 KiB.
            if (!JsonElement.DeepEquals(document.RootElement, masked))
                safe.OutputsJson = ByteString.CopyFromUtf8(masked.GetRawText());
            return TaskOutputJson.Error(safe.OutputsJson.Memory) is { } maskedError
                ? TaskOutputJson.Reject(safe, maskedError) : safe;
        }
        catch (InvalidOperationException)
        { return TaskOutputJson.Reject(safe, "Task outputs could not be safely masked."); }
    }
}
