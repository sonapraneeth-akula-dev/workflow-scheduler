using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text;
using Wf.Contracts;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

/// <summary>
/// Opt-in POST/status operation, attached directly through NodeBuilder.Task or StageBuilder.Task.
/// Authoring and manifest support only: execution is unavailable until the durable remote runner exists.
/// </summary>
public sealed class HttpOperationTask
{
    public HttpOperationDefinition Definition { get; }
    internal string? Body { get; }
    internal string ContentType { get; }

    internal HttpOperationTask(HttpOperationDefinition definition, string? body, string contentType)
    {
        if (body is not null && Encoding.UTF8.GetByteCount(body) > 1024 * 1024 ||
            string.IsNullOrWhiteSpace(contentType) || contentType.Length > 256 ||
            contentType.Any(char.IsControl) || !MediaTypeHeaderValue.TryParse(contentType, out _))
            throw new ArgumentException("Invalid HTTP operation request inputs.");
        Definition = definition;
        Body = body;
        ContentType = contentType;
    }

    public Task ExecuteAsync(TaskContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        throw new HttpOperationRuntimeUnsupportedException();
    }
}

/// <summary>Stable, nonretryable classifier until the durable remote-operation runner is implemented.</summary>
public sealed class HttpOperationRuntimeUnsupportedException()
    : TaskFailureException("HttpOperationRuntimeUnsupported: durable HTTP operation execution is not implemented.",
        retryable: false, category: FailureCategory.ValidationError);

public static partial class Tasks
{
    public static HttpOperationTask HttpOperation(string submissionUrl, HttpOperationStatusContract status,
        HttpOperationCancelContract? cancel = null, HttpOperationDeduplicationContract? deduplication = null,
        HttpOperationLookupContract? lookup = null, IEnumerable<string>? trustedOrigins = null,
        string? secretReference = null, TimeSpan? requestTimeout = null, string? body = null,
        string contentType = "application/json") =>
        new(new(submissionUrl, status, cancel, deduplication, lookup,
            trustedOrigins?.Take(17).ToImmutableArray() ?? [],
            secretReference, requestTimeout), body, contentType);
}
