using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wf.Contracts.Runs;
using Wf.Sdk.Local;
using Wf.Validation;

namespace Wf.Sdk.Hosting;

public static class RunModeDispatcher
{
    private enum Mode { Local, Engine, Describe, TaskHost, DryRun }

    [RequiresDynamicCode("The --task-host mode serves framework-dependent workflow IL, not Native AOT.")]
    [RequiresUnreferencedCode("The --task-host mode requires untrimmed workflow IL.")]
    public static async Task<int> RunAsync(WorkflowDefinition workflow, string[] args, TextWriter? output = null,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequested();
        var mode = Mode.Local;
        var selected = false;
        string? target = null;
        string? socket = null;
        var variables = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--var":
                    variables.Add("--var");
                    variables.Add(Value());
                    break;
                case "--engine":
                    Select(Mode.Engine);
                    target = Value();
                    break;
                case "--describe":
                    Select(Mode.Describe);
                    break;
                case "--task-host":
                    Select(Mode.TaskHost);
                    break;
                case "--socket":
                    if (socket is not null) throw new ArgumentException("--socket can be supplied only once.", nameof(args));
                    socket = Value();
                    break;
                case "--dry-run":
                    Select(Mode.DryRun);
                    target = Value();
                    break;
                default:
                    throw new ArgumentException("Unknown workflow option. Use --var, --engine, --describe, --task-host or --dry-run.", nameof(args));
            }

            string Value()
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Workflow option requires a non-empty value.", nameof(args));
                return args[index];
            }
        }
        if (mode != Mode.TaskHost && socket is not null)
            throw new ArgumentException("--socket requires --task-host.", nameof(args));
        if (mode is Mode.Describe or Mode.TaskHost && variables.Count > 0)
            throw new ArgumentException("--var is not accepted in --describe or --task-host mode.", nameof(args));
        output ??= Console.Out;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; stopping.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            switch (mode)
            {
                case Mode.Describe:
                    await output.WriteLineAsync(ManifestWriter.Write(workflow).AsMemory(), stopping.Token).ConfigureAwait(false);
                    return 0;
                case Mode.TaskHost:
                    return await TaskHostServer.RunAsync(workflow, socket ??
                        throw new ArgumentException("--task-host requires --socket <absolute-path>.", nameof(args)), stopping.Token).ConfigureAwait(false);
                case Mode.DryRun:
                    return await DryRunHost.RunAsync(workflow, ["--dry-run", target!, .. variables],
                        output, stopping.Token).ConfigureAwait(false);
                default:
                    var values = LocalVariables.Parse(workflow.Variables, variables.ToArray());
                    if (mode == Mode.Local)
                        return (await new LocalEngine(workflow, values, output).RunAsync(stopping.Token).ConfigureAwait(false)).ExitCode;
                    _ = VariableBinder.Bind(workflow.Variables, values);
                    return await SubmitAsync(workflow, target!, values, output, httpClient, stopping.Token).ConfigureAwait(false);
            }
        }
        finally { Console.CancelKeyPress -= cancel; }

        void Select(Mode next)
        {
            if (selected) throw new ArgumentException("Select exactly one workflow execution mode.", nameof(args));
            selected = true;
            mode = next;
        }
    }

    private static async Task<int> SubmitAsync(WorkflowDefinition workflow, string endpoint,
        ImmutableDictionary<string, JsonElement> variables, TextWriter output, HttpClient? httpClient,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var origin) || origin.Scheme is not ("https" or "http") ||
            origin.Scheme == "http" && !origin.IsLoopback || origin.AbsolutePath != "/" ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("--engine requires an HTTPS origin (HTTP only on loopback), without credentials, paths or queries.", nameof(endpoint));
        using var owned = httpClient is null ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) : null;
        var client = httpClient ?? owned!;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "/api/v1/runs"))
        {
            Content = JsonContent.Create(new TriggerRunRequest(workflow.Name, workflow.Version) { Variables = variables },
                RunJsonContext.Default.TriggerRunRequest)
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Accepted)
            throw new HttpRequestException($"Run submission returned HTTP {(int)response.StatusCode}; expected 202 Accepted.", null, response.StatusCode);
        await response.Content.LoadIntoBufferAsync(64 * 1024, cancellationToken).ConfigureAwait(false);
        var accepted = await response.Content.ReadFromJsonAsync(RunJsonContext.Default.TriggerRunResponse, cancellationToken).ConfigureAwait(false);
        if (accepted is null || accepted.RunId == Guid.Empty)
            throw new InvalidDataException("Engine accepted the run without returning a non-empty runId.");
        await output.WriteLineAsync(accepted.RunId.ToString("D").AsMemory(), cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
