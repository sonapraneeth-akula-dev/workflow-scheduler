using Wf.Contracts.Definition;

namespace Wf.Contracts;

public class TaskFailureException(string message, bool retryable = true,
    FailureCategory category = FailureCategory.TaskError, Exception? innerException = null)
    : Exception(message, innerException)
{
    public bool Retryable { get; } = retryable;
    public FailureCategory Category { get; } = category;
}

public sealed class NonRetryableException(string message, Exception? innerException = null)
    : TaskFailureException(message, retryable: false, innerException: innerException);
