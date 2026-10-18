using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;

namespace Wf.Validation.SemVer;

/// <summary>
/// Checked-in public workflow API. Node identities and variable contracts are versioned;
/// task bodies, dependencies, policies and metadata are implementation details.
/// </summary>
public sealed class ApiBaseline
{
    public const string FileName = "workflow.api.json";

    public string Version { get; }
    public ImmutableArray<VariableSchema> Variables { get; }
    public ImmutableArray<string> Nodes { get; }

    public ApiBaseline(string version, ImmutableArray<VariableSchema> variables, ImmutableArray<string> nodes)
    {
        if (string.IsNullOrWhiteSpace(version) || !IsSemVer(version))
            throw new ArgumentException("Version must use SemVer 2.0.0.", nameof(version));
        if (variables.IsDefault || variables.Any(variable => variable is null))
            throw new ArgumentException("Variables must be initialized and nonnull.", nameof(variables));
        if (nodes.IsDefault || nodes.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Nodes must be initialized with nonempty keys.", nameof(nodes));
        if (variables.Select(variable => variable.Key).Distinct(StringComparer.Ordinal).Count() != variables.Length)
            throw new ArgumentException("Variable keys must be unique.", nameof(variables));
        if (nodes.Distinct(StringComparer.Ordinal).Count() != nodes.Length)
            throw new ArgumentException("Node keys must be unique.", nameof(nodes));
        Version = version;
        Variables = variables.OrderBy(variable => variable.Key, StringComparer.Ordinal)
            .Select(variable => new VariableSchema(variable.Key, variable.Type, variable.Required, variable.Default,
                variable.AllowedValues.Order(StringComparer.Ordinal).ToImmutableArray())).ToImmutableArray();
        Nodes = nodes.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    /// <summary>Projects a validated manifest without retaining executable or deployment configuration.</summary>
    public static ApiBaseline FromManifest(WorkflowManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Nodes.IsDefault || manifest.Nodes.Any(node => node is null))
            throw new ArgumentException("Manifest nodes must be initialized and nonnull.", nameof(manifest));
        return new(manifest.Version, manifest.Variables, manifest.Nodes.Select(node => node.Key).ToImmutableArray());
    }

    public byte[] WriteUtf8() =>
        JsonSerializer.SerializeToUtf8Bytes(this, ApiBaselineJsonContext.Default.ApiBaseline);

    /// <summary>Rejects malformed or ambiguous input without including untrusted values in diagnostics.</summary>
    public static ApiBaseline Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8, ApiBaselineJsonContext.Default.ApiBaseline) ?? throw InvalidBaseline();
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw InvalidBaseline();
        }
    }

    /// <summary>Leaves the caller's stream open; cancellation and infrastructure errors propagate.</summary>
    public static async Task<ApiBaseline> ReadAsync(Stream source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        try
        {
            return await JsonSerializer.DeserializeAsync(source, ApiBaselineJsonContext.Default.ApiBaseline,
                cancellationToken).ConfigureAwait(false) ?? throw InvalidBaseline();
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw InvalidBaseline();
        }
    }

    /// <summary>Leaves the caller's stream open; cancellation and infrastructure errors propagate.</summary>
    public Task WriteAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(destination);
        return JsonSerializer.SerializeAsync(destination, this, ApiBaselineJsonContext.Default.ApiBaseline,
            cancellationToken);
    }

    private static InvalidDataException InvalidBaseline() => new("The workflow API baseline is invalid.");

    private static bool IsSemVer(string version)
    {
        var metadata = version.Split('+');
        if (metadata.Length > 2 || (metadata.Length == 2 && !metadata[1].Split('.').All(IsIdentifier)))
            return false;
        var release = metadata[0].Split('-', 2);
        var numbers = release[0].Split('.');
        return numbers.Length == 3 && numbers.All(IsNumber) &&
            (release.Length == 1 || release[1].Split('.').All(identifier =>
                IsIdentifier(identifier) && (!identifier.All(char.IsAsciiDigit) || IsNumber(identifier))));
    }

    private static bool IsNumber(string value) =>
        value.Length > 0 && (value.Length == 1 || value[0] != '0') && value.All(char.IsAsciiDigit);

    private static bool IsIdentifier(string value) =>
        value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true, WriteIndented = true, AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ApiBaseline))]
internal partial class ApiBaselineJsonContext : JsonSerializerContext;
