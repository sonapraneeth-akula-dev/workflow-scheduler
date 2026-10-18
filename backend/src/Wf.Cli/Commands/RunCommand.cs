using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Wf.Contracts.Runs;

namespace Wf.Cli.Commands;

public static class RunCommand
{
    public const string Usage =
        "Usage: wf run <team/type/workflow> [--version <version>] [--single-machine [--machine <name>]] [--engine <origin>] [--var <key=JSON>]...\nEngine: --engine or WF_ENGINE_URL; authentication: WF_ACCESS_TOKEN (required outside loopback).";

    public static async Task<int> RunAsync(IReadOnlyList<string> arguments, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (arguments is ["--help"] or ["-h"])
        {
            await output.WriteLineAsync(Usage);
            return 0;
        }
        TriggerRunRequest body;
        Uri origin;
        string? token;
        try
        {
            if (arguments.Count == 0 || arguments[0].Split('/') is not [_, _, _] ||
                arguments[0].Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." ||
                    part.EnumerateRunes().Take(129).Count() > 128 || part.Any(c => c == '\\' || char.IsControl(c))))
                throw new ArgumentException("Supply an exact team/type/workflow identity.");
            string? engine = null, version = null, machine = null;
            var single = false;
            var variables = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
            for (var index = 1; index < arguments.Count; index++)
            {
                switch (arguments[index])
                {
                    case "--single-machine" when !single: single = true; break;
                    case "--engine" when engine is null: engine = Value(); break;
                    case "--version" when version is null: version = Value(); break;
                    case "--machine" when machine is null: machine = Value(); break;
                    case "--var":
                        var value = Value();
                        var equal = value.IndexOf('=');
                        if (equal < 1 || !variables.TryAdd(value[..equal], Parse(value[(equal + 1)..])))
                            throw new ArgumentException("Use unique --var key=JSON values.");
                        break;
                    default: throw new ArgumentException("Unknown or repeated run option.");
                }
                string Value()
                {
                    if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]) ||
                        arguments[index].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("Run option requires a nonempty value.");
                    return arguments[index];
                }
            }
            if (machine is not null && (!single || machine.Length > 200 || machine.Any(char.IsControl)))
                throw new ArgumentException("--machine requires --single-machine and a valid machine name.");
            if (!Uri.TryCreate(engine ?? Environment.GetEnvironmentVariable("WF_ENGINE_URL"), UriKind.Absolute, out var target) ||
                target.Scheme is not ("https" or "http") || target.Scheme == "http" && !target.IsLoopback ||
                target.AbsolutePath != "/" || target.UserInfo.Length != 0 || target.Query.Length != 0 || target.Fragment.Length != 0)
                throw new ArgumentException("The engine must be an HTTPS origin (HTTP only on loopback), without credentials, paths or queries.");
            origin = target;
            token = Environment.GetEnvironmentVariable("WF_ACCESS_TOKEN");
            if (token is not null && (token.Length == 0 || token.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))) ||
                token is null && !origin.IsLoopback)
                throw new ArgumentException("Supply a valid WF_ACCESS_TOKEN bearer token for a non-loopback engine.");
            body = new(arguments[0], version)
            {
                Variables = variables.ToImmutable(), Placement = single ? new(true, machine) : null
            };
            if (JsonSerializer.SerializeToUtf8Bytes(body, RunJsonContext.Default.TriggerRunRequest).Length > 1024 * 1024)
                throw new ArgumentException("The run request exceeds 1 MiB.");
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            await error.WriteLineAsync(exception.Message);
            await error.WriteLineAsync(Usage);
            return 2;
        }
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "/api/v1/runs"))
            {
                Content = JsonContent.Create(body, RunJsonContext.Default.TriggerRunRequest)
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Accepted)
            {
                await error.WriteLineAsync($"Run rejected: HTTP {(int)response.StatusCode}; expected 202 Accepted.");
                return 1;
            }
            await response.Content.LoadIntoBufferAsync(16 * 1024, cancellationToken);
            var accepted = await response.Content.ReadFromJsonAsync(RunJsonContext.Default.TriggerRunResponse, cancellationToken);
            if (accepted is null || accepted.RunId.Version != 7)
                throw new InvalidDataException("The engine did not return a valid accepted UUIDv7 run ID.");
            await output.WriteLineAsync(accepted.RunId.ToString("D"));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Run submission cancelled; an already accepted run continues on the engine.");
            return 130;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            await error.WriteLineAsync($"Run submission failed ({exception.GetType().Name}); acceptance may be durable. No retry was submitted.");
            return 2;
        }
    }

    private static JsonElement Parse(string value)
    {
        using var json = JsonDocument.Parse(value, new() { AllowDuplicateProperties = false });
        return json.RootElement.Clone();
    }
}
