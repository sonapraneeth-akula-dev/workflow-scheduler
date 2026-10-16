using Wf.Contracts;
using Wf.Contracts.Definition;

namespace Wf.Engine.Core;

public static class FailureClassifier
{
    /// <summary>
    /// Preserves explicit task failure metadata, then typed timeouts, then attributed cancellation.
    /// Unattributed errors retain their task or platform failure domain; messages are never inspected.
    /// A null error requires an explicit cancellation cause, not a successful completion.
    /// </summary>
    public static FailureCategory Classify(Exception? error, FailureCategory failureDomain = FailureCategory.PlatformError,
        bool timedOut = false, bool aborted = false)
    {
        if (failureDomain is not (FailureCategory.TaskError or FailureCategory.PlatformError))
            throw new ArgumentOutOfRangeException(nameof(failureDomain));
        if (error is TaskFailureException failure)
        {
            if (!Enum.IsDefined(failure.Category))
                throw new ArgumentOutOfRangeException(nameof(error), "Task failure category must be defined.");
            return failure.Category;
        }
        if (error is TimeoutException)
            return FailureCategory.Timeout;
        if (error is null or OperationCanceledException)
        {
            if (timedOut)
                return FailureCategory.Timeout;
            if (aborted)
                return FailureCategory.Aborted;
        }
        ArgumentNullException.ThrowIfNull(error);
        return failureDomain;
    }
}
