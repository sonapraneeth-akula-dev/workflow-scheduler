using System.Collections.Immutable;
using System.Text;
using Wf.Contracts.Definition;

namespace Wf.Sdk;

public sealed record HttpResponseOutput(int StatusCode, string Body, ImmutableDictionary<string, string[]> Headers);

public static partial class Tasks
{
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static Func<TaskContext, Task> Http(HttpMethod method, string url,
        IReadOnlyDictionary<string, string>? headers = null, string? body = null,
        int? expectedStatus = null, string contentType = "application/json")
    {
        ArgumentNullException.ThrowIfNull(method);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("HTTP task URL must be an absolute HTTP(S) URI.", nameof(url));
        if (expectedStatus is < 100 or > 599)
            throw new ArgumentOutOfRangeException(nameof(expectedStatus));
        var copiedHeaders = headers?.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase) ??
            ImmutableDictionary<string, string>.Empty;
        using var validationRequest = BuildRequest();
        var publicUrl = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
        return Declare(async context =>
        {
            using var request = BuildRequest();
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken);
            if (expectedStatus is { } expected ? (int)response.StatusCode != expected : !response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP task received status {(int)response.StatusCode}; expected " +
                    (expectedStatus?.ToString() ?? "a success status") + ".", null, response.StatusCode);
            await response.Content.LoadIntoBufferAsync(NodeOutputs.MaxBytes, context.CancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(context.CancellationToken);
            var responseHeaders = response.Headers.Concat(response.Content.Headers)
                .ToImmutableDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            context.Outputs["response"] = new HttpResponseOutput((int)response.StatusCode, responseBody, responseHeaders);
        }, new(TaskActionKind.Http, method.Method + " " + publicUrl));

        HttpRequestMessage BuildRequest()
        {
            var request = new HttpRequestMessage(method, uri);
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, contentType);
            foreach (var (key, value) in copiedHeaders)
                request.Headers.Add(key, value);
            return request;
        }
    }
}
