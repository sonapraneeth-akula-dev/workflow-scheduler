using System.Text;
using System.Text.Json.Serialization;

namespace Wf.Contracts.Artifacts;

public sealed record ArtifactMetadata([property: JsonRequired] string Name, [property: JsonRequired] string ContentType,
    [property: JsonRequired] long SizeBytes, [property: JsonRequired] string Sha256)
{
    public const long RunBudget = 5_000_000_000;
    public const string GrantPath = "/api/v1/artifact-grants";
    public const string PublishPath = "/api/v1/artifact-publishes";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Encoding.UTF8.GetByteCount(Name) > 255 ||
            Name is "." or ".." || Name.Any(c => char.IsControl(c) || c is '/' or '\\' or '"' or ':') ||
            string.IsNullOrWhiteSpace(ContentType) || ContentType.Length > 128 ||
            !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(ContentType, out var mime) ||
            mime.Parameters.Count != 0 || ContentType.Any(char.IsControl) ||
            SizeBytes < 0 || Sha256 is not { Length: 64 } || !Sha256.All(char.IsAsciiHexDigit) ||
            Sha256 != Sha256.ToLowerInvariant())
            throw new ArgumentException("Invalid artifact name, content type, size or SHA-256.");
    }
}

public sealed record ArtifactGrant([property: JsonRequired] Guid ArtifactId, [property: JsonRequired] DateTimeOffset ExpiresAt,
    [property: JsonRequired] bool Committed);
public sealed record ArtifactReceipt([property: JsonRequired] Guid RunId, [property: JsonRequired] Guid ArtifactId);
public sealed record ArtifactSummary(Guid Id, Guid RunId, string SourceKey, string Name, string ContentType,
    long SizeBytes, string Sha256, DateTimeOffset CreatedAt);
public sealed class ArtifactLimitExceededException : TaskFailureException
{
    public ArtifactLimitExceededException() : base("Artifact bytes exceed the 5000000000-byte run budget.", retryable: false) { }
}

[JsonSerializable(typeof(ArtifactMetadata))]
[JsonSerializable(typeof(ArtifactGrant))]
[JsonSerializable(typeof(ArtifactReceipt))]
[JsonSerializable(typeof(ArtifactSummary[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
public partial class ArtifactJsonContext : JsonSerializerContext;
