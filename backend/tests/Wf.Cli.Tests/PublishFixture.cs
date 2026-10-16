using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Wf.Cli.Commands;
using Wf.Validation.Build;

namespace Wf.Cli.Tests;

[CollectionDefinition("Publish", DisableParallelization = true)]
public sealed class PublishCollection : ICollectionFixture<PublishFixture>;

public sealed class PublishFixture : IAsyncLifetime
{
    internal const string AccessToken = "publish-unit-token-must-not-echo";
    internal string Root { get; } = Path.Combine(Repository, ".agent-artifacts", "cli-publish-3adec91f", "u",
        Guid.NewGuid().ToString("N")[..8]);
    internal string Source => Path.Combine(Root, "s", "t", "r", "w");
    internal string Registry => Path.Combine(Root, "s", "t", "team.json");
    internal string Work => Path.Combine(Root, "w");
    internal ScriptBuildResult Build { get; private set; } = null!;
    internal string[] Closure => new[] { "Wf.Sdk", "Wf.Contracts", "Wf.Engine.Core", "Wf.Validation" }
        .Select(name => Path.Combine(Repository, "backend", "src", name))
        .Append(Path.Combine(Repository, "backend", "proto")).ToArray();
    internal static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly Dictionary<string, string?> environment = new();

    public async ValueTask InitializeAsync()
    {
        foreach (var (name, value) in new[]
        {
            ("Deterministic", "true"), ("DebugType", "none"), ("DebugSymbols", "false"),
            ("WarningsNotAsErrors", "NU1902;NU1903"), ("WF_ACCESS_TOKEN", AccessToken),
            // Parallel task commits must not change compiler-generated AssemblyInformationalVersion.
            ("SourceRevisionId", new string('a', 40))
        })
        {
            environment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        Directory.CreateDirectory(Source);
        await File.WriteAllTextAsync(Path.Combine(Source, "workflow.cs"), $$"""
            #:project {{Path.Combine(Repository, "backend", "src", "Wf.Sdk", "Wf.Sdk.csproj")}}
            #:property PublishAot=false
            #:property AssemblyName=Publish.Unit.Workflow
            using Wf.Sdk;
            var workflow = Workflow.Define("tests/release/publish", "1.0.0", w => w.Type("release")
                .Node("work", n => n.Task("echo", ctx => ctx.Outputs["answer"] = 42)));
            return await workflow.RunAsync(args);
            """, new UTF8Encoding(false), Token);
        await File.WriteAllTextAsync(Registry, RegistryJson(), Token);
        Build = await new ScriptBuildCache(Path.Combine(Work, "c"), Path.Combine(Work, "a"))
            .BuildAsync(new(Path.Combine(Source, "workflow.cs"), Source, Closure), Token);
    }

    internal static string RegistryJson(string team = "tests") => $$"""
        {"team":"{{team}}","types":[{"team":"{{team}}","name":"release","description":"Release workflows",
        "metadataSchema":{"keys":[],"allowFreeForm":false},"roleBindings":[]}],"roleBindings":[]}
        """;

    internal string[] Common => [Source, "--work-directory", Work,
        .. Closure.SelectMany(path => new[] { "--input", path })];

    internal string[] Arguments(string engine, params string[] extra) => [.. Common, "--engine", engine,
        "--git-commit", new string('a', 40), "--git-author", "Publish Unit Author", .. extra];

    internal string NewFile(string name) => Path.Combine(Root, Guid.NewGuid().ToString("N")[..8] + "-" + name);

    internal static async Task<(int Exit, string Output, string Error)> Run(string[] arguments, CancellationToken? token = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await PublishCommand.RunAsync(arguments, output, error, token ?? Token);
        return (exit, output.ToString(), error.ToString());
    }

    internal static async Task<(int Exit, string Output, string Error)> Process(string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = Repository
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var (name, value) in environment) Environment.SetEnvironmentVariable(name, value);
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static string Repository
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (Directory.Exists(Path.Combine(directory.FullName, "backend", "proto"))) return directory.FullName;
            throw new DirectoryNotFoundException("Publish tests require the repository source tree.");
        }
    }
}

internal sealed class PublishHttpServer : IAsyncDisposable
{
    internal sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);
    internal sealed record Reply(int Status, string Body = "{}", bool Drop = false, string? Location = null,
        string ContentType = "application/json");
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    internal ConcurrentQueue<Request> Requests { get; } = new();
    internal string Origin { get; }

    internal PublishHttpServer(Func<Request, Reply> handler)
    {
        listener.Start();
        Origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        loop = Serve(handler);
    }

    private async Task Serve(Func<Request, Reply> handler)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var connection = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = connection.GetStream();
                var first = (await Line(stream, stop.Token)).Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string line;
                while ((line = await Line(stream, stop.Token)).Length > 0)
                {
                    var separator = line.IndexOf(':');
                    headers.Add(line[..separator], line[(separator + 1)..].Trim());
                }
                using var body = new MemoryStream();
                if (headers.TryGetValue("Content-Length", out var length))
                    await Copy(stream, body, int.Parse(length, CultureInfo.InvariantCulture), stop.Token);
                else if (headers.GetValueOrDefault("Transfer-Encoding") == "chunked")
                {
                    while (true)
                    {
                        var count = int.Parse(await Line(stream, stop.Token), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        if (count == 0) { await Line(stream, stop.Token); break; }
                        await Copy(stream, body, count, stop.Token);
                        await Line(stream, stop.Token);
                    }
                }
                var request = new Request(first[0], first[1], headers, body.ToArray());
                Requests.Enqueue(request);
                var response = handler(request);
                if (response.Drop) continue;
                var bytes = Encoding.UTF8.GetBytes(response.Body);
                var head = $"HTTP/1.1 {response.Status} Test\r\nContent-Length: {bytes.Length}\r\n" +
                    $"Content-Type: {response.ContentType}\r\nConnection: close\r\n" +
                    (response.Location is null ? "" : $"Location: {response.Location}\r\n") + "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), stop.Token);
                await stream.WriteAsync(bytes, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (IOException) when (stop.IsCancellationRequested) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }

    private static async Task Copy(Stream source, Stream target, int count, CancellationToken token)
    {
        Assert.InRange(count, 0, 64 * 1024 * 1024);
        var bytes = new byte[count];
        await source.ReadExactlyAsync(bytes, token);
        await target.WriteAsync(bytes, token);
    }

    private static async Task<string> Line(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        var next = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(next, token) == 0) throw new EndOfStreamException();
            if (next[0] == '\n') return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.Add(next[0]);
            if (bytes.Count > 32 * 1024) throw new InvalidDataException("Test request header is too long.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        try { await loop; }
        finally { stop.Dispose(); }
    }
}
