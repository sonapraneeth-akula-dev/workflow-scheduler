using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;

namespace Wf.Sdk;

internal static class RemoteOperationRunner
{
    private const string Pending = "SubmissionPending";
    private const string Submitted = "RemoteSubmitted";
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static async Task ExecuteAsync(HttpOperationTask operation, TaskContext context,
        HttpClient? client = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!context.Checkpoint.CanSave || context.RunId == Guid.Empty || string.IsNullOrWhiteSpace(context.TaskKey))
            throw Invalid("RemoteOperationRequiresCheckpoint: durable publishing and run/task identity are required.");
        var clock = timeProvider ?? TimeProvider.System;
        var http = client ?? Client;
        var definition = operation.Definition;
        var secret = definition.SecretReference is { } reference ? context.Secret(reference) : null;
        var key = "wf-" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(context.RunId.ToString("D") + "\n" + context.TaskKey)));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(definition,
            WorkflowManifestJsonContext.Default.HttpOperationDefinition));
        hash.AppendData(Encoding.UTF8.GetBytes("\n" + operation.ContentType + "\n" + operation.Body));
        var fingerprint = Convert.ToHexStringLower(hash.GetHashAndReset());
        RemoteOperationState? state;
        try { state = context.Checkpoint.TryLoad(RemoteOperationJsonContext.Default.RemoteOperationState); }
        catch (JsonException) { throw Invalid("RemoteOperationInvalidCheckpoint: state is not a remote operation checkpoint."); }
        var resumed = state is not null;
        var terminal = false;
        try
        {
            if (state is null)
            {
                state = new(Pending, key, fingerprint, clock.GetUtcNow(), null, null, null);
                await Save(state);
                await Emit(RemoteOperationPhase.SubmissionPending, context.CancellationToken);
            }
            else
            {
                if (state.Stage is not (Pending or Submitted) || state.SubmissionKey != key ||
                    state.Fingerprint != fingerprint || state.SubmittedAt > clock.GetUtcNow() ||
                    state.SubmittedAt == default ||
                    state.Stage == Pending && (state.StatusUrl is not null || state.OperationId is not null || state.NextPollAt is not null) ||
                    state.Stage == Submitted && (state.StatusUrl is null || state.NextPollAt is null))
                    throw Invalid("RemoteOperationInvalidCheckpoint: checkpoint identity or stage does not match.");
                if (state.StatusUrl is { } statusUrl) _ = Resolve(statusUrl);
                await Emit(RemoteOperationPhase.Resuming, context.CancellationToken);
            }
            var statusDeadline = state.SubmittedAt + definition.Status.Retention;
            EnsureRetained(statusDeadline);
            if (state.Stage == Pending)
            {
                Reply? response = null;
                var fromLookup = false;
                if (resumed)
                {
                    if (definition.Lookup is { } lookup && clock.GetUtcNow() < state.SubmittedAt + lookup.Retention)
                    {
                        response = await Send(HttpMethod.Get, Resolve(lookup.UrlTemplate.Replace("{submissionKey}", key,
                            StringComparison.Ordinal)), state.SubmittedAt + lookup.Retention);
                        if (response.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone) response = null;
                        fromLookup = response is not null;
                    }
                    if (response is null && (definition.Deduplication is not { } dedup ||
                        clock.GetUtcNow() >= state.SubmittedAt + dedup.Retention))
                        throw Unknown();
                }
                response ??= await Send(HttpMethod.Post, Resolve(definition.SubmissionUrl),
                    resumed ? Earlier(statusDeadline, state.SubmittedAt + definition.Deduplication!.Retention) : statusDeadline,
                    submission: true);
                CheckStatus(response);
                if (await CompleteAsync(response, allowSynchronous: !fromLookup, allowReferenceOnly: true)) return;
                var operationId = Field(response.Body, definition.Status.OperationIdField);
                string? location = response.Location;
                if (location is null && operationId is not null && definition.Status.UrlTemplate is { } template)
                    location = template.Replace("{operationId}", Uri.EscapeDataString(operationId), StringComparison.Ordinal);
                if (location is null) throw Invalid("RemoteOperationProtocol: pending response has no operation reference.");
                var target = Resolve(location);
                state = state with
                {
                    Stage = Submitted, StatusUrl = target.AbsoluteUri, OperationId = operationId,
                    NextPollAt = NextPoll(response, 0)
                };
                await Save(state);
                await Emit(RemoteOperationPhase.Submitted, context.CancellationToken);
            }
            await Emit(RemoteOperationPhase.Waiting, context.CancellationToken);
            var polls = 0;
            var nextPoll = state.NextPollAt!.Value;
            while (true)
            {
                EnsureRetained(statusDeadline);
                await WaitUntil(nextPoll, statusDeadline);
                var response = await Send(HttpMethod.Get, Resolve(state.StatusUrl!), statusDeadline);
                if (response.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone) throw Unknown();
                CheckStatus(response);
                if (await CompleteAsync(response, allowSynchronous: false)) return;
                context.Log($"Remote operation status poll {++polls}: waiting.");
                nextPoll = NextPoll(response, polls);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            var outcome = await CancelRemoteAsync();
            throw new OperationCanceledException(outcome switch
            {
                RemoteOperationPhase.CancelConfirmed => "Remote operation stopped locally; remote cancellation was confirmed.",
                RemoteOperationPhase.Succeeded => "Remote operation stopped locally; the service reported success.",
                RemoteOperationPhase.Failed => "Remote operation stopped locally; the service reported failure.",
                _ => "RemoteOutcomeUnknown: operation stopped locally without confirmed remote cancellation."
            }, context.CancellationToken);
        }
        catch (TaskFailureException)
        {
            if (!terminal) await Emit(RemoteOperationPhase.OutcomeUnknown, context.CancellationToken);
            throw;
        }

        async Task Emit(RemoteOperationPhase phase, CancellationToken token)
        {
            if (context.ProgressPublisher is { } publisher) await publisher(phase, token);
            if (phase is RemoteOperationPhase.Succeeded or RemoteOperationPhase.Failed or
                RemoteOperationPhase.CancelConfirmed or RemoteOperationPhase.OutcomeUnknown) terminal = true;
        }

        async Task<RemoteOperationPhase> CancelRemoteAsync()
        {
            var outcome = RemoteOperationPhase.OutcomeUnknown;
            if (definition.Cancel is { } cancel && state?.StatusUrl is { } statusUrl)
            {
                using var cleanup = new CancellationTokenSource(cancel.RequestTimeout, clock);
                var deadline = clock.GetUtcNow() + cancel.RequestTimeout;
                try
                {
                    await Emit(RemoteOperationPhase.CancelRequested, cleanup.Token);
                    if (cancel.UrlTemplate.Contains("{operationId}", StringComparison.Ordinal) && state.OperationId is null)
                        throw Invalid("RemoteOperationProtocol: cancellation requires an operation ID.");
                    var target = Resolve(cancel.UrlTemplate.Replace("{statusUrl}", statusUrl, StringComparison.Ordinal)
                        .Replace("{operationId}", Uri.EscapeDataString(state.OperationId ?? ""), StringComparison.Ordinal));
                    var response = await Send(cancel.Method == HttpOperationCancelMethod.Delete ? HttpMethod.Delete : HttpMethod.Post,
                        target, deadline, cleanup.Token);
                    var polls = 0;
                    while (true)
                    {
                        CheckStatus(response);
                        var remote = Field(response.Body, definition.Status.StateField);
                        if (remote == definition.Status.Cancelled) { outcome = RemoteOperationPhase.CancelConfirmed; break; }
                        if (remote == definition.Status.Succeeded) { outcome = RemoteOperationPhase.Succeeded; break; }
                        if (remote == definition.Status.Failed) { outcome = RemoteOperationPhase.Failed; break; }
                        if (remote is not null && remote != definition.Status.Pending && remote != definition.Status.Running)
                            throw Invalid("RemoteOperationProtocol: unrecognized cancellation state.");
                        await WaitUntil(NextPoll(response, polls++), deadline, cleanup.Token);
                        response = await Send(HttpMethod.Get, Resolve(statusUrl), deadline, cleanup.Token);
                    }
                }
                catch (Exception error) when (error is TaskFailureException or OperationCanceledException or IOException)
                { context.Log("Remote cancellation was not confirmed; the remote outcome is unknown."); }
            }
            else context.Log("Remote cancellation is unavailable; the remote outcome is unknown.");
            using var publication = new CancellationTokenSource(TimeSpan.FromSeconds(3), clock);
            await Emit(outcome, publication.Token);
            return outcome;
        }

        Task Save(RemoteOperationState value) =>
            context.Checkpoint.SaveAsync(value, RemoteOperationJsonContext.Default.RemoteOperationState, context.CancellationToken);

        Uri Resolve(string value)
        {
            try { return definition.ResolveReference(value); }
            catch (Exception error) when (error is ArgumentException or UriFormatException)
            { throw Invalid("RemoteOperationProtocol: unsafe or undeclared operation reference."); }
        }

        void EnsureRetained(DateTimeOffset deadline, CancellationToken? cancellationToken = null)
        {
            (cancellationToken ?? context.CancellationToken).ThrowIfCancellationRequested();
            if (clock.GetUtcNow() >= deadline) throw Unknown();
        }

        async Task WaitUntil(DateTimeOffset at, DateTimeOffset deadline, CancellationToken? cancellationToken = null)
        {
            while (clock.GetUtcNow() < at)
            {
                EnsureRetained(deadline, cancellationToken);
                var remaining = Earlier(at, deadline) - clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero) continue;
                await Task.Delay(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining,
                    clock, cancellationToken ?? context.CancellationToken);
            }
            EnsureRetained(deadline, cancellationToken);
        }

        async Task<Reply> Send(HttpMethod method, Uri uri, DateTimeOffset deadline,
            CancellationToken? cancellationToken = null, bool submission = false)
        {
            var requestToken = cancellationToken ?? context.CancellationToken;
            EnsureRetained(deadline, requestToken);
            var remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) throw Unknown();
            using var timeout = new CancellationTokenSource(remaining < definition.RequestTimeout ?
                remaining : definition.RequestTimeout, clock);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(requestToken, timeout.Token);
            using var request = new HttpRequestMessage(method, uri);
            try
            {
                if (secret is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                if (submission)
                {
                    if (operation.Body is not null)
                    {
                        request.Content = new StringContent(operation.Body, Encoding.UTF8);
                        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(operation.ContentType);
                    }
                    if (definition.Deduplication is { } dedup)
                        request.Headers.Add(dedup.HeaderName, key);
                }
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                if ((int)response.StatusCode is >= 300 and < 400)
                    throw Invalid("RemoteOperationProtocol: redirects are not permitted.");
                if (!response.IsSuccessStatusCode)
                    return new(response.StatusCode, null, null, response.Headers.RetryAfter);
                string? location = null;
                if (definition.Status.LocationHeader is { } header && response.Headers.TryGetValues(header, out var values))
                {
                    var locations = values.Take(2).ToArray();
                    if (locations.Length != 1) throw Invalid("RemoteOperationProtocol: ambiguous operation reference.");
                    location = locations[0];
                    _ = Resolve(location);
                }
                if (response.Content.Headers.ContentLength > NodeOutputs.MaxBytes)
                    throw Invalid("RemoteOperationProtocol: response exceeds the output limit.");
                await using var input = await response.Content.ReadAsStreamAsync(stop.Token);
                var bytes = new byte[NodeOutputs.MaxBytes + 1];
                var count = await input.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, stop.Token);
                if (count > NodeOutputs.MaxBytes)
                    throw Invalid("RemoteOperationProtocol: response exceeds the output limit.");
                JsonElement? body = null;
                if (count != 0)
                {
                    using var document = JsonDocument.Parse(bytes.AsMemory(0, count));
                    body = document.RootElement.Clone();
                    if (body.Value.ValueKind == JsonValueKind.Object &&
                        body.Value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() !=
                        body.Value.EnumerateObject().Count())
                        throw Invalid("RemoteOperationProtocol: duplicate response fields.");
                }
                return new(response.StatusCode, body, location, response.Headers.RetryAfter);
            }
            catch (OperationCanceledException) when (!requestToken.IsCancellationRequested)
            { throw new TaskFailureException("RemoteOperationTransport: request timed out; durable intent is retained.", retryable: true); }
            catch (HttpRequestException)
            { throw new TaskFailureException("RemoteOperationTransport: request failed; durable intent is retained.", retryable: true); }
            catch (JsonException)
            { throw Invalid("RemoteOperationProtocol: response is not valid JSON."); }
            catch (FormatException)
            { throw Invalid("RemoteOperationProtocol: invalid request header value."); }
        }

        void CheckStatus(Reply response)
        {
            if ((int)response.Status is >= 200 and < 300) return;
            throw new TaskFailureException("RemoteOperationHttp: service rejected the request.",
                retryable: response.Status is HttpStatusCode.TooManyRequests || (int)response.Status >= 500);
        }

        async Task<bool> CompleteAsync(Reply response, bool allowSynchronous, bool allowReferenceOnly = false)
        {
            var status = Field(response.Body, definition.Status.StateField);
            if (status == definition.Status.Failed || status == definition.Status.Cancelled)
            {
                Output(response.Body, definition.Status.ErrorField, "error");
                await Emit(status == definition.Status.Failed ? RemoteOperationPhase.Failed : RemoteOperationPhase.CancelConfirmed,
                    context.CancellationToken);
                throw new TaskFailureException(status == definition.Status.Failed ?
                    "RemoteOperationFailed: service reported failure." : "RemoteOperationCancelled: service confirmed cancellation.",
                    retryable: false);
            }
            if (status == definition.Status.Succeeded ||
                allowSynchronous && response.Status != HttpStatusCode.Accepted && status is null &&
                response.Location is null && Field(response.Body, definition.Status.OperationIdField) is null)
            {
                if (!Output(response.Body, definition.Status.ResultField, "result") &&
                    definition.Status.ResultField is null && response.Body is { } body)
                    context.Outputs.Set("result", body, OutputJsonContext.Default.JsonElement);
                await Emit(RemoteOperationPhase.Succeeded, context.CancellationToken);
                return true;
            }
            if (status is not null && status != definition.Status.Pending && status != definition.Status.Running ||
                status is null && !allowReferenceOnly)
                throw Invalid("RemoteOperationProtocol: unrecognized or missing operation state.");
            return false;
        }

        bool Output(JsonElement? body, string? field, string name)
        {
            if (field is null || body is not { ValueKind: JsonValueKind.Object } value ||
                !value.TryGetProperty(field, out var output)) return false;
            context.Outputs.Set(name, output, OutputJsonContext.Default.JsonElement);
            return true;
        }

        DateTimeOffset NextPoll(Reply response, int number)
        {
            var now = clock.GetUtcNow();
            var at = response.RetryAfter?.Date ?? now + (response.RetryAfter?.Delta ??
                TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(number, 5))));
            return at > now ? at : now.AddMilliseconds(100);
        }
    }

    private static string? Field(JsonElement? body, string? field)
    {
        if (field is null || body is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty(field, out var property)) return null;
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()) ||
            property.GetString()!.Length > 2048)
            throw Invalid("RemoteOperationProtocol: invalid operation field.");
        return property.GetString();
    }

    private static DateTimeOffset Earlier(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;
    private static TaskFailureException Invalid(string message) => new(message, retryable: false, category: FailureCategory.ValidationError);
    private static TaskFailureException Unknown() => new(
        "RemoteOutcomeUnknown: no safe resubmission or retained remote status is available.", retryable: false);

    private sealed record Reply(HttpStatusCode Status, JsonElement? Body, string? Location, RetryConditionHeaderValue? RetryAfter);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RemoteOperationState(
    [property: JsonRequired] string Stage,
    [property: JsonRequired] string SubmissionKey,
    [property: JsonRequired] string Fingerprint,
    [property: JsonRequired] DateTimeOffset SubmittedAt,
    [property: JsonRequired] string? StatusUrl,
    [property: JsonRequired] string? OperationId,
    [property: JsonRequired] DateTimeOffset? NextPollAt);

[JsonSerializable(typeof(RemoteOperationState))]
internal partial class RemoteOperationJsonContext : JsonSerializerContext;
