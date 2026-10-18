using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Wf.Contracts;
using Wf.Contracts.Events;

namespace Wf.Cli.Commands;

public static class AbortCommand
{
    public const string Usage =
        "Usage: wf abort <runId> --reason <reason> [--engine <origin>]\nEngine: --engine or WF_ENGINE_URL; authentication: WF_ACCESS_TOKEN (required outside loopback).";

    public static async Task<int> RunAsync(IReadOnlyList<string> arguments, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        output = TextWriter.Synchronized(output);
        if (arguments is ["--help"] or ["-h"])
        {
            await output.WriteLineAsync(Usage);
            return 0;
        }
        Guid run;
        string reason;
        Uri origin;
        string? token;
        try
        {
            if (arguments.Count == 0 || !Guid.TryParseExact(arguments[0], "D", out run) || run.Version != 7)
                throw new ArgumentException("Supply an exact UUIDv7 run ID.");
            string? suppliedReason = null, suppliedEngine = null;
            for (var index = 1; index < arguments.Count; index += 2)
            {
                if (index + 1 == arguments.Count) throw new ArgumentException("Every option requires a value.");
                switch (arguments[index])
                {
                    case "--reason" when suppliedReason is null: suppliedReason = arguments[index + 1]; break;
                    case "--engine" when suppliedEngine is null: suppliedEngine = arguments[index + 1]; break;
                    default: throw new ArgumentException("Use --reason and --engine at most once; no other options are supported.");
                }
            }
            if (string.IsNullOrWhiteSpace(suppliedReason) || suppliedReason.Length > 500)
                throw new ArgumentException("A nonblank reason of 1-500 characters is required.");
            reason = suppliedReason;
            if (!Uri.TryCreate(suppliedEngine ?? Environment.GetEnvironmentVariable("WF_ENGINE_URL"), UriKind.Absolute, out var target) ||
                target.Scheme is not ("https" or "http") || target.Scheme == "http" && !target.IsLoopback ||
                target.AbsolutePath != "/" || target.UserInfo.Length != 0 || target.Query.Length != 0 || target.Fragment.Length != 0)
                throw new ArgumentException("The engine must be an HTTPS origin (HTTP only on loopback), without credentials, paths or queries.");
            origin = target;
            token = Environment.GetEnvironmentVariable("WF_ACCESS_TOKEN");
            if (token is not null && (token.Length == 0 || token.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))))
                throw new ArgumentException("WF_ACCESS_TOKEN must contain a nonempty bearer token without whitespace.");
            if (token is null && !origin.IsLoopback)
                throw new ArgumentException("WF_ACCESS_TOKEN is required for a non-loopback engine.");
        }
        catch (ArgumentException exception)
        {
            await error.WriteLineAsync(exception.Message);
            await error.WriteLineAsync(Usage);
            return 2;
        }

        var submitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            await using var live = new HubConnectionBuilder()
                .WithUrl(new Uri(origin, "/hubs/live/v1"), options =>
                {
                    options.AccessTokenProvider = () => Task.FromResult(token);
                    options.Transports = HttpTransportType.WebSockets;
                    options.SkipNegotiation = true;
                    options.HttpMessageHandlerFactory = _ => new SocketsHttpHandler { AllowAutoRedirect = false };
                })
                .AddJsonProtocol(options => options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, AbortCliJson.Default))
                .Build();
            var terminal = new TaskCompletionSource<RunStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            long lastSequence = 0;
            using var changes = live.On<AbortLiveDelta>("RunDelta", delta =>
            {
                if (delta.RunId != run || delta.Events is not { Length: > 0 })
                {
                    terminal.TrySetException(new InvalidDataException("Invalid live run identity or events."));
                    return;
                }
                foreach (var change in delta.Events)
                {
                    if (change.Seq <= 0 || lastSequence != 0 && change.Seq != lastSequence + 1 ||
                        !Enum.IsDefined((EventEntity)change.Entity) ||
                        (change.Entity == (int)EventEntity.Run
                            ? !Enum.IsDefined((RunStatus)change.State)
                            : !Enum.IsDefined((ExecutionStatus)change.State)))
                    {
                        terminal.TrySetException(new InvalidDataException("Invalid or noncontiguous live run event."));
                        return;
                    }
                    lastSequence = change.Seq;
                    if (change.Entity != (int)EventEntity.Run) continue;
                    var status = (RunStatus)change.State;
                    output.WriteLine($"Run {run:D}: {status}");
                    if (status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Aborted or RunStatus.TimedOut)
                        terminal.TrySetResult(status);
                }
            });
            using var resync = live.On<JsonElement>("Resync", _ =>
                terminal.TrySetException(new InvalidDataException("Live run history requires resynchronization; rerun the command.")));
            live.Closed += _ =>
            {
                terminal.TrySetException(new IOException("Live connection closed before a terminal run state."));
                return Task.CompletedTask;
            };
            await live.StartAsync(cancellationToken);
            var denied = await live.InvokeAsync<JsonElement?>("Subscribe", "run", run.ToString("D"), cancellationToken);
            if (denied is not null)
            {
                await error.WriteLineAsync("Abort denied: live run access was not granted.");
                return 1;
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, $"/api/v1/runs/{run:D}/abort"))
            {
                Content = JsonContent.Create(new AbortCliRequest(reason), AbortCliJson.Default.AbortCliRequest)
            };
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            submitted = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                await error.WriteLineAsync($"Abort rejected: HTTP {(int)response.StatusCode}; expected 202 Accepted.");
                return 1;
            }
            await response.Content.LoadIntoBufferAsync(16 * 1024, cancellationToken);
            var accepted = await response.Content.ReadFromJsonAsync(AbortCliJson.Default.AbortRecord, cancellationToken)
                ?? throw new InvalidDataException("The engine returned no accepted abort record.");
            await output.WriteLineAsync("Abort accepted: " + JsonSerializer.Serialize(accepted, AbortCliJson.Default.AbortRecord));
            var ended = await terminal.Task.WaitAsync(cancellationToken);
            if (ended == RunStatus.Aborted) return 0;
            await error.WriteLineAsync($"Run ended {ended}, not Aborted.");
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Abort monitoring cancelled; an already accepted abort continues on the engine.");
            return 130;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or
            HubException or WebSocketException or OperationCanceledException or InvalidOperationException or ArgumentException)
        {
            await error.WriteLineAsync($"Abort failed ({exception.GetType().Name}); " +
                (submitted ? "acceptance may already be durable. No retry was submitted." : "abort was not submitted."));
            return 2;
        }
    }
}

internal sealed record AbortCliRequest(string Reason, AbortSource Source = AbortSource.CLI);
internal sealed record AbortLiveDelta(Guid RunId, AbortLiveEvent[] Events);
internal sealed record AbortLiveEvent(long Seq, int Entity, int State);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(AbortCliRequest))]
[JsonSerializable(typeof(AbortRecord))]
[JsonSerializable(typeof(AbortLiveDelta))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonElement?))]
internal partial class AbortCliJson : JsonSerializerContext;
