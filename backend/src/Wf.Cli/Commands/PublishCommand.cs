using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Wf.Cli.Packaging;
using Wf.Cli.Publishing;
using Wf.Contracts.Registration;
using Wf.Validation;
using Wf.Validation.Build;
using Wf.Validation.SemVer;

namespace Wf.Cli.Commands;

/// <summary>Publishes trusted local source using the same validator and deterministic package as CI.</summary>
public static class PublishCommand
{
    public const string Usage =
        "Usage: wf publish <workflow-directory> --engine <origin> --git-commit <hash> --git-author <author> " +
        "[--change-note <text>] [--input <file-or-directory>]... [--work-directory <directory>] " +
        "[--baseline <workflow.api.json>] [--team-type-quota <positive-integer>] [--schedule-source-path <repository-relative-path>]\n" +
        "Authentication: WF_ACCESS_TOKEN is required, including on loopback. Only trusted local workflow.cs source is compiled.";

    /// <summary>Exit codes: 0 registered/unchanged, 1 validation/rejection, 2 usage/infrastructure, 130 cancellation.</summary>
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
        var submitted = false;
        string? staging = null;
        try
        {
            var options = Parse(arguments);
            cancellationToken.ThrowIfCancellationRequested();
            var source = await PublishInputs.ReadAsync(options.Directory, options.SchedulePath, cancellationToken);
            var cache = new ScriptBuildCache(Path.Combine(options.Work, "c"), Path.Combine(options.Work, "a"));
            var result = await Validator.ValidateAsync(options.Directory, cache, options.Inputs, cancellationToken,
                options.Quota is { } quota ? new ValidationOptions { TypeQuota = quota } : null);
            var errors = result.Errors;
            if (result.IsValid && options.Baseline is { } baseline)
                errors = errors.AddRange(BreakingChangeClassifier.Classify(
                    ApiBaseline.Parse(await PublishInputs.ReadFileAsync(baseline, 1024 * 1024, cancellationToken)),
                    ApiBaseline.FromManifest(result.Build!.Manifest)).Errors);
            foreach (var diagnostic in errors)
                await error.WriteLineAsync(JsonSerializer.Serialize(diagnostic, CliJsonContext.Default.ValidationError));
            if (result.BuildFailure is { } failure)
            {
                await error.WriteLineAsync($"{failure.Stage} failed with exit code {failure.ExitCode}.");
                await error.WriteLineAsync(failure.StandardOutput);
                await error.WriteLineAsync(failure.StandardError);
            }
            if (!errors.IsEmpty) return 1;
            var build = result.Build ?? throw new InvalidDataException("Validation returned no successful build.");
            source.ValidateIdentity(build.Manifest);
            // Reject mutable local inputs rather than attaching different source/provenance to a cached package.
            var key = await cache.ComputeKeyAsync(new(Path.Combine(options.Directory, "workflow.cs"),
                options.Directory, options.Inputs), cancellationToken);
            if (key != build.CacheKey || source != await PublishInputs.ReadAsync(options.Directory, options.SchedulePath, cancellationToken))
                throw new IOException("Publication inputs changed during validation; nothing was submitted.");
            staging = Path.Combine(options.Work, "p-" + Guid.NewGuid().ToString("N"));
            PublishInputs.RejectLinks(options.Work);
            var package = await PackageBuilder.BuildAsync(build, Path.Combine(staging, "workflow.zip"), cancellationToken);
            using var manifest = JsonDocument.Parse(build.ManifestUtf8);
            var registration = new RegistrationRequest(options.Commit, options.Author, build.Manifest.Name,
                manifest.RootElement, new(package.Sha256, package.Size), source.Source, source.Registry,
                options.Note, source.Schedules, source.Schedules is null ? null : options.SchedulePath);
            var body = JsonSerializer.SerializeToUtf8Bytes(registration, RegistrationJsonContext.Default.RegistrationRequest);
            if (body.Length > 8 * 1024 * 1024)
                throw new InvalidDataException("Registration JSON exceeds the engine's 8 MiB limit.");
            using var client = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false, MaxResponseHeadersLength = 32
            }) { Timeout = TimeSpan.FromMinutes(2) };
            using (var grantRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Engine, "/api/v1/upload-grants"))
            {
                Content = JsonContent.Create(new UploadGrantRequest("package", package.Sha256, package.Size),
                    RegistrationJsonContext.Default.UploadGrantRequest)
            })
            {
                grantRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
                using var response = await client.SendAsync(grantRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
                    return await Rejected("Upload grant", response, error, cancellationToken);
                await response.Content.LoadIntoBufferAsync(64 * 1024, cancellationToken);
                var grant = await response.Content.ReadFromJsonAsync(RegistrationJsonContext.Default.UploadGrantResponse, cancellationToken)
                    ?? throw new InvalidDataException("The engine returned no upload grant.");
                if (grant.Exists)
                {
                    if (response.StatusCode != HttpStatusCode.OK || grant.UploadUri is not null ||
                        grant.MaxBytes is not null || grant.ExpiresAt is not null)
                        throw new InvalidDataException("The engine returned an inconsistent existing-package grant.");
                }
                else
                {
                    if (response.StatusCode != HttpStatusCode.Created || grant.MaxBytes != package.Size ||
                        grant.ExpiresAt is null || grant.ExpiresAt <= DateTimeOffset.UtcNow ||
                        grant.UploadUri is not { Length: > 16 and <= 2064 } path ||
                        !path.StartsWith("/api/v1/uploads/", StringComparison.Ordinal) ||
                        path["/api/v1/uploads/".Length..].Split('.') is not [{ Length: > 0 }, { Length: > 0 }] ||
                        path["/api/v1/uploads/".Length..].Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
                        throw new InvalidDataException("The engine returned an invalid, expired or unsafe upload grant.");
                    await using var stream = File.OpenRead(package.Path);
                    using var upload = new HttpRequestMessage(HttpMethod.Put, new Uri(options.Engine, path))
                    {
                        Content = new StreamContent(stream)
                    };
                    upload.Content.Headers.ContentType = new("application/zip");
                    // Upload capability authorizes only these bytes; never forward the bearer token.
                    using var uploaded = await client.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (uploaded.StatusCode != HttpStatusCode.Created)
                        return await Rejected("Package upload", uploaded, error, cancellationToken);
                }
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Engine, "/api/v1/registrations"))
            {
                Content = new ByteArrayContent(body)
            };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
            cancellationToken.ThrowIfCancellationRequested();
            submitted = true;
            using var registered = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (registered.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK))
                return await Rejected("Registration", registered, error, cancellationToken);
            await registered.Content.LoadIntoBufferAsync(64 * 1024, cancellationToken);
            var accepted = await registered.Content.ReadFromJsonAsync(RegistrationJsonContext.Default.RegistrationResponse, cancellationToken)
                ?? throw new InvalidDataException("The engine returned no registration receipt.");
            if ((registered.StatusCode == HttpStatusCode.Created) != accepted.Changed ||
                new[] { accepted.WorkflowVersion, accepted.RegistrySnapshot, accepted.ScheduleConfigVersion }
                    .Any(id => id is { } value && (value == Guid.Empty || value.Version != 7)))
                throw new InvalidDataException("The engine returned an inconsistent registration receipt.");
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteLineAsync($"{(accepted.Changed ? "Registered" : "Already registered")}: {build.Manifest.Name}@{build.Manifest.Version}");
            await output.WriteLineAsync($"SHA256: {package.Sha256}");
            await output.WriteLineAsync(JsonSerializer.Serialize(accepted, RegistrationJsonContext.Default.RegistrationResponse));
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync(submitted
                ? "Publish cancelled; registration may already be durable. No retry was submitted."
                : "Publish cancelled; registration was not submitted.");
            return 130;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            await error.WriteLineAsync($"Publish failed ({exception.GetType().Name}); " +
                (submitted ? "registration may already be durable. No retry was submitted." : "registration was not submitted."));
            return 2;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            Win32Exception or JsonException or ArgumentException or InvalidOperationException)
        {
            await error.WriteLineAsync($"{exception.GetType().Name}: {exception.Message}");
            if (exception is ArgumentException) await error.WriteLineAsync(Usage);
            if (submitted) await error.WriteLineAsync("Registration may already be durable. No retry was submitted.");
            return 2;
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static Options Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count is 0 or > 256 || arguments.Any(value => string.IsNullOrWhiteSpace(value) ||
            value.Length > 4096 || value.Contains('\0')) ||
            arguments[0].StartsWith('-'))
            throw new ArgumentException("Supply a workflow directory and bounded option/value pairs.");
        try
        {
            if (arguments.Sum(value => (long)new UTF8Encoding(false, true).GetByteCount(value)) > 64 * 1024)
                throw new ArgumentException("Publish arguments exceed the 64 KiB UTF-8 limit.");
        }
        catch (EncoderFallbackException) { throw new ArgumentException("Publish arguments must be valid UTF-8 text."); }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var inputs = new List<string>();
        for (var index = 1; index < arguments.Count; index += 2)
        {
            if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Every publish option requires a value.");
            var name = arguments[index];
            var value = arguments[index + 1];
            if (name == "--input") inputs.Add(PublishInputs.FullPath(value));
            else if (name is not ("--engine" or "--git-commit" or "--git-author" or "--change-note" or
                "--work-directory" or "--baseline" or "--team-type-quota" or "--schedule-source-path") ||
                !values.TryAdd(name, value))
                throw new ArgumentException("Use supported publish options at most once; only --input may repeat.");
        }
        if (!values.TryGetValue("--engine", out var engine) || engine.Length > 2048 ||
            !Uri.TryCreate(engine, UriKind.Absolute, out var origin) || origin.Scheme is not ("https" or "http") ||
            origin.Scheme == "http" && !origin.IsLoopback || origin.AbsolutePath != "/" ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            !origin.IsWellFormedOriginalString() || engine.Any(char.IsWhiteSpace) || engine.Contains('\\') ||
            engine[(engine.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/').Contains('/'))
            throw new ArgumentException("--engine must be an HTTPS origin (HTTP only on loopback), without credentials, paths or queries.");
        var token = Environment.GetEnvironmentVariable("WF_ACCESS_TOKEN");
        if (string.IsNullOrEmpty(token) || token.Length > 16 * 1024 ||
            token.Any(character => character is <= ' ' or > '~'))
            throw new ArgumentException("WF_ACCESS_TOKEN requires a nonempty ASCII bearer token without whitespace, at most 16 KiB.");
        if (!values.TryGetValue("--git-commit", out var commit) || commit.Length is not (40 or 64) ||
            !commit.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("--git-commit requires a lowercase 40- or 64-character Git commit hash.");
        if (!values.TryGetValue("--git-author", out var author) || author.Length > 512 || author.Any(char.IsControl))
            throw new ArgumentException("--git-author requires nonblank text of at most 512 characters without control characters.");
        values.TryGetValue("--change-note", out var note);
        if (note?.Length > 4000) throw new ArgumentException("--change-note is limited to 4000 characters.");
        int? quota = null;
        if (values.TryGetValue("--team-type-quota", out var quotaText))
        {
            if (!int.TryParse(quotaText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                throw new ArgumentException("--team-type-quota requires a positive integer.");
            quota = parsed;
        }
        values.TryGetValue("--schedule-source-path", out var schedule);
        if (schedule is not null && (schedule.Length > 1024 ||
            schedule.Any(character => character is '\\' or ':' || char.IsControl(character)) ||
            schedule.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or "..")))
            throw new ArgumentException("--schedule-source-path requires an exact repository-relative path without dot segments.");
        var directory = PublishInputs.FullPath(arguments[0]);
        var work = PublishInputs.FullPath(values.GetValueOrDefault("--work-directory") ?? Path.Combine(directory, "obj", "wf-publish"));
        var baseline = values.TryGetValue("--baseline", out var file) ? PublishInputs.FullPath(file) : null;
        return new(directory, work, origin, token, commit, author, note, inputs, baseline, quota, schedule);
    }

    private static async Task<int> Rejected(string operation, HttpResponseMessage response, TextWriter error, CancellationToken token)
    {
        await error.WriteLineAsync($"{operation} rejected: HTTP {(int)response.StatusCode}. No retry was submitted.");
        await response.Content.LoadIntoBufferAsync(64 * 1024, token);
        if (response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json")
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
            if (problem.RootElement.ValueKind == JsonValueKind.Object &&
                problem.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                foreach (var diagnostic in errors.EnumerateArray())
                    await error.WriteLineAsync(JsonSerializer.Serialize(
                        diagnostic.Deserialize(CliJsonContext.Default.ValidationError), CliJsonContext.Default.ValidationError));
        }
        return 1;
    }

    private sealed record Options(string Directory, string Work, Uri Engine, string Token, string Commit, string Author,
        string? Note, IReadOnlyList<string> Inputs, string? Baseline, int? Quota, string? SchedulePath);
}
