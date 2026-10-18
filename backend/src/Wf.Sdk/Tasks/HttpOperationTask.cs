using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text;
using Wf.Contracts;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

/// <summary>
/// Opt-in POST/status operation, attached directly through NodeBuilder.Task or StageBuilder.Task.
/// Requires a durable checkpoint publisher and a logical run/task identity before execution.
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

    public Task ExecuteAsync(TaskContext context) => RemoteOperationRunner.ExecuteAsync(this, context);
}

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
