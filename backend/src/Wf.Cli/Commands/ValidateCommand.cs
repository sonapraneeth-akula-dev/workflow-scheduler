using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.Cli.Packaging;
using Wf.Validation;
using Wf.Validation.Build;
using Wf.Validation.SemVer;

namespace Wf.Cli.Commands;

/// <summary>Runs trusted workflow builds; --input declares the complete external project/import closure.</summary>
public static class ValidateCommand
{
    public const string Usage =
        "Usage: wf validate <workflow-directory> [--input <file-or-directory>]... [--baseline <workflow.api.json>] [--package <output.zip>] [--work-directory <directory>]";

    /// <summary>Exit codes: 0 valid, 1 validation/build failure, 2 usage/infrastructure failure, 130 cancellation.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> arguments, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            if (arguments.Count == 0 || arguments[0].StartsWith('-'))
                throw new ArgumentException(Usage);
            var inputs = new List<string>();
            string? package = null, baseline = null, work = null;
            for (var index = 1; index < arguments.Count; index += 2)
            {
                if (index + 1 == arguments.Count || string.IsNullOrWhiteSpace(arguments[index + 1]) ||
                    arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException(Usage);
                var value = arguments[index + 1];
                switch (arguments[index])
                {
                    case "--input": inputs.Add(value); break;
                    case "--package" when package is null: package = value; break;
                    case "--baseline" when baseline is null: baseline = value; break;
                    case "--work-directory" when work is null: work = value; break;
                    default: throw new ArgumentException(Usage);
                }
            }

            var cache = work is null ? null : new ScriptBuildCache(Path.Combine(work, "c"), Path.Combine(work, "a"));
            var result = await Validator.ValidateAsync(arguments[0], cache, additionalInputs: inputs,
                cancellationToken: cancellationToken);
            var errors = result.Errors;
            if (result.IsValid && baseline is not null)
            {
                var previous = ApiBaseline.Parse(await File.ReadAllBytesAsync(baseline, cancellationToken));
                errors = errors.AddRange(BreakingChangeClassifier.Classify(previous,
                    ApiBaseline.FromManifest(result.Build!.Manifest)).Errors);
            }
            foreach (var diagnostic in errors)
                await error.WriteLineAsync(JsonSerializer.Serialize(diagnostic, CliJsonContext.Default.ValidationError));
            if (result.BuildFailure is { } failure)
            {
                await error.WriteLineAsync($"{failure.Stage} failed with exit code {failure.ExitCode}.");
                await error.WriteLineAsync(failure.StandardOutput);
                await error.WriteLineAsync(failure.StandardError);
            }
            if (!errors.IsEmpty)
                return 1;
            var build = result.Build ?? throw new InvalidDataException("Validation returned no successful build.");
            var packaged = package is null ? null : await PackageBuilder.BuildAsync(build, package, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteLineAsync($"Valid: {build.Manifest.Name}@{build.Manifest.Version}");
            if (packaged is not null)
            {
                await output.WriteLineAsync($"Package: {packaged.Path}");
                await output.WriteLineAsync($"SHA256: {packaged.Sha256}");
                await output.WriteLineAsync($"Bytes: {packaged.Size}");
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Validation cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            Win32Exception or JsonException or ArgumentException)
        {
            await error.WriteLineAsync($"{exception.GetType().Name}: {exception.Message}");
            return 2;
        }
    }
}

[JsonSerializable(typeof(ValidationError))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class CliJsonContext : JsonSerializerContext;
