using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.Cli.Publishing;

namespace Wf.Cli.Commands;

public static class CloneCommand
{
    public const string Usage =
        "Usage: wf clone <team/type/workflow> --version <exact-UUID> --new-name <name> --target-type <team/type> " +
        "[--repo <workflows-repository>] [--engine <origin>]\n" +
        "Writes teams/<team>/<type>/<name> below --repo (default: current directory), without overwriting. " +
        "Engine: --engine or WF_ENGINE_URL; authentication: WF_ACCESS_TOKEN (required outside loopback). " +
        "Downloaded source is never executed. Review before validate/test/publish.";
    private const int ArchiveLimit = 2 * 1024 * 1024;
    private const int ExpandedLimit = 4 * 1024 * 1024;
    private static readonly string[] Files =
        ["workflow.cs", "schedules.json", "schemas/schedules.schema.json", "WorkflowClone.Tests.csproj", "Tests/CloneTests.cs", "README.md"];

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
        string? staging = null;
        try
        {
            var options = Parse(arguments);
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.Exists(options.Destination)) throw new IOException("Clone destination already exists; nothing was overwritten.");
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, MaxResponseHeadersLength = 32 })
                { Timeout = TimeSpan.FromSeconds(30) };
            var sourcePath = string.Join("/", options.Source.Split('/').Select(Uri.EscapeDataString));
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(options.Engine, $"/api/v1/workflows/{sourcePath}/versions/{options.Version}/clone"))
            {
                Content = JsonContent.Create(new CloneCliRequest(options.Name, options.Type), CloneCliJson.Default.CloneCliRequest)
            };
            if (options.Token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var code = await ProblemCode(response, cancellationToken);
                await error.WriteLineAsync($"Clone rejected: HTTP {(int)response.StatusCode}{(code is null ? "" : " (" + code + ")")}. " + (response.StatusCode switch
                {
                    HttpStatusCode.BadRequest => "Check the exact version, target identity and supported source declaration.",
                    HttpStatusCode.Unauthorized => "Authentication is required.",
                    HttpStatusCode.Forbidden => "Viewer(source) and Author(target type) permissions are required.",
                    HttpStatusCode.NotFound => "The exact source workflow/version was not found.",
                    HttpStatusCode.Conflict => "The target workflow already exists.",
                    _ => "No files were published; no retry was submitted."
                }));
                return 1;
            }
            if (response.Content.Headers.ContentType?.MediaType != "application/zip" ||
                response.Content.Headers.ContentDisposition?.DispositionType != "attachment" ||
                response.Headers.CacheControl?.NoStore != true)
                throw new InvalidDataException("The engine did not return a no-store ZIP attachment.");
            if (response.Content.Headers.ContentLength is > ArchiveLimit)
                throw new InvalidDataException("Clone archive exceeds its download limit.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var bytes = new MemoryStream();
            await CopyBounded(source, bytes, ArchiveLimit, cancellationToken);
            bytes.Position = 0;
            using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
            ValidateArchive(archive);
            var parent = Path.GetDirectoryName(options.Destination)!;
            PublishInputs.RejectLinks(parent);
            Directory.CreateDirectory(parent);
            PublishInputs.RejectLinks(parent);
            staging = Path.Combine(parent, ".wf-clone-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            foreach (var entry in archive.Entries)
            {
                var destination = Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = entry.Open();
                await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await CopyBounded(input, file, checked((int)entry.Length), cancellationToken);
                if (file.Length != entry.Length) throw new InvalidDataException("Clone entry length does not match its archive.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            PublishInputs.RejectLinks(options.Destination);
            Directory.Move(staging, options.Destination);
            staging = null;
            await output.WriteLineAsync($"Cloned {options.Source}@{options.Version} to {options.Destination}");
            await output.WriteLineAsync("Source not executed. Review source, target metadata and tests before publishing.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Clone cancelled; no destination was published.");
            return 130;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or HttpRequestException or OperationCanceledException or NotSupportedException)
        {
            await error.WriteLineAsync($"Clone failed ({exception.GetType().Name}); no destination was published or overwritten.");
            if (exception is ArgumentException) await error.WriteLineAsync(Usage);
            return 2;
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static Options Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count is < 7 or > 11 || arguments.Any(value => string.IsNullOrWhiteSpace(value) ||
            value.Length > 4096 || value.Any(char.IsControl)))
            throw new ArgumentException("Supply a workflow and bounded option/value pairs.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Count; index += 2)
            if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                arguments[index] is not ("--version" or "--new-name" or "--target-type" or "--repo" or "--engine") ||
                !values.TryAdd(arguments[index], arguments[index + 1]))
                throw new ArgumentException("Every supported option requires exactly one value.");
        var source = arguments[0].Split('/');
        if (source.Length != 3 || source.Any(part => !IdentityPart(part)) ||
            !values.TryGetValue("--version", out var version) || !Guid.TryParseExact(version, "D", out var id) ||
            id == Guid.Empty || id.ToString("D") != version ||
            !values.TryGetValue("--new-name", out var name) || !FilePart(name) ||
            !values.TryGetValue("--target-type", out var type) || type.Split('/') is not [var team, var kind] ||
            !FilePart(team) || !FilePart(kind) || type + "/" + name == arguments[0])
            throw new ArgumentException("Supply exact source/version and a safe, different target team/type/name.");
        if (!Uri.TryCreate(values.GetValueOrDefault("--engine") ?? Environment.GetEnvironmentVariable("WF_ENGINE_URL"),
                UriKind.Absolute, out var engine) || engine.Scheme is not ("https" or "http") ||
            engine.Scheme == "http" && !engine.IsLoopback || engine.AbsolutePath != "/" ||
            engine.UserInfo.Length != 0 || engine.Query.Length != 0 || engine.Fragment.Length != 0)
            throw new ArgumentException("Engine must be an HTTPS origin, or HTTP loopback, without credentials, paths or queries.");
        var token = Environment.GetEnvironmentVariable("WF_ACCESS_TOKEN");
        if (token is not null && (token.Length is 0 or > 16384 || token.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))) ||
            token is null && !engine.IsLoopback)
            throw new ArgumentException("WF_ACCESS_TOKEN must be a nonempty bearer token; required outside loopback.");
        var repo = PublishInputs.FullPath(values.GetValueOrDefault("--repo") ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(repo)) throw new ArgumentException("The workflows repository must already exist.");
        var destination = Path.Combine(repo, "teams", team, kind, name);
        PublishInputs.RejectLinks(destination);
        return new(arguments[0], version, name, type, destination, engine, token);
    }

    private static bool IdentityPart(string value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") &&
        value.EnumerateRunes().Take(129).Count() <= 128 && !value.Any(character => character is '\\' or '/' || char.IsControl(character));

    private static bool FilePart(string value) => IdentityPart(value) &&
        !value.Any(character => "<>:\"|?*".Contains(character)) &&
        value[^1] is not ('.' or ' ') &&
        !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(value.Split('.')[0], StringComparer.OrdinalIgnoreCase);

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count != Files.Length ||
            !archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal).SetEquals(Files) ||
            archive.Entries.Any(entry => entry.Length is <= 0 or > ArchiveLimit ||
                ((entry.ExternalAttributes >> 16) & 0xf000) is not (0 or 0x8000) ||
                (entry.ExternalAttributes & (int)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) ||
            archive.Entries.Sum(entry => entry.Length) > ExpandedLimit)
            throw new InvalidDataException("Clone archive must contain exactly the six regular scaffold files within size limits.");
    }

    private static async Task<string?> ProblemCode(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentType?.MediaType is not ("application/json" or "application/problem+json"))
            return null;
        await using var source = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        await CopyBounded(source, bytes, 16 * 1024, token);
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var field in new[] { "code", "reason" })
                if (document.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var code = value.GetString();
                    if (code is "invalid_version_identity" or "invalid_clone_request" or "unsupported_clone_source" or
                        "insufficient_scope_role" or "version_not_found" or "clone_target_exists" or "invalid_clone_content_type" or
                        "version_data_inconsistent" or "version_data_unavailable" or "version_source_unavailable")
                        return code;
                }
        }
        catch (JsonException) { }
        return null;
    }

    private static async Task CopyBounded(Stream input, Stream output, int maximum, CancellationToken token)
    {
        var buffer = new byte[8192];
        var total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            total = checked(total + read);
            if (total > maximum) throw new InvalidDataException("Clone content exceeds its limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private sealed record Options(string Source, string Version, string Name, string Type, string Destination, Uri Engine, string? Token);
}

internal sealed record CloneCliRequest(string NewName, string TargetType);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CloneCliRequest))]
internal partial class CloneCliJson : JsonSerializerContext;
