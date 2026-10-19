using System.Globalization;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Wf.Contracts.Definition;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Auth;

namespace Wf.ControlPlane.Api;

public sealed record CatalogMetadataField(string Key, bool Required, IReadOnlyList<string> AllowedValues);
public sealed record CatalogMetadataSchema(IReadOnlyList<CatalogMetadataField> Keys, bool AllowFreeForm)
{
    public static CatalogMetadataSchema From(MetadataSchema schema) =>
        new(schema.Keys.Select(key => new CatalogMetadataField(key.Key, key.Required, key.AllowedValues)).ToArray(), schema.AllowFreeForm);
}
public sealed record CatalogRegistryType(string Team, string Name, string Description,
    CatalogMetadataSchema MetadataSchema, IReadOnlyList<RegistryRoleBinding> RoleBindings);
public sealed record CatalogPresentation(IReadOnlyList<string> TypeOrder, IReadOnlyList<string> GroupingKeys,
    IReadOnlyList<string> ListColumns, IReadOnlyDictionary<string, string> DefaultFilters,
    IReadOnlyDictionary<string, string> Labels, IReadOnlyDictionary<string, string> Names);
public sealed record CatalogTeam(string Team, IReadOnlyList<CatalogRegistryType> Types,
    [property: JsonPropertyName("$schema")] string? Schema, IReadOnlyList<RegistryRoleBinding> RoleBindings,
    CatalogPresentation? Presentation)
{
    public static CatalogTeam From(TeamRegistry registry) => new(registry.Team,
        registry.Types.Select(type => new CatalogRegistryType(type.Team, type.Name, type.Description,
            CatalogMetadataSchema.From(type.MetadataSchema), type.RoleBindings)).ToArray(), registry.Schema, registry.RoleBindings,
        registry.Presentation is not { } presentation ? null : new(presentation.TypeOrder, presentation.GroupingKeys,
            presentation.ListColumns, presentation.DefaultFilters, presentation.Labels, presentation.Names));
}
public sealed record CatalogType(string Team, string Name, string Description, CatalogMetadataSchema MetadataSchema,
    IReadOnlyDictionary<string, long> LatestStatusCounts);
public sealed record CatalogVersion(Guid Id, string SemVer);
public sealed record CatalogLatestRun(Guid Id, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt);
public sealed record CatalogWorkflow(string Id, string Team, string Type, string Name,
    CatalogVersion? ActiveVersion, CatalogVersion LatestVersion, WorkflowMetadata Metadata, CatalogLatestRun? LatestRun);
public sealed record CatalogWorkflowPage(IReadOnlyList<CatalogWorkflow> Items, string? NextCursor);

public sealed record CatalogQuery(string? Team, string? Type, string? AfterId, int Limit)
{
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = ImmutableDictionary<string, string>.Empty;
    public bool UsePresentationDefaults { get; init; }

    public static CatalogQuery Parse(IQueryCollection query)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in query)
        {
            if (entry.Value.Count != 1)
                throw new ArgumentException("Use only one value for each catalogue filter.");
            if (entry.Key.StartsWith("meta.", StringComparison.Ordinal))
            {
                var key = entry.Key[5..];
                var value = entry.Value.ToString();
                if (string.IsNullOrWhiteSpace(key) || key.EnumerateRunes().Count() > 128 ||
                    key.Any(char.IsControl) || value.EnumerateRunes().Count() > 1024 ||
                    value.Any(char.IsControl) || metadata.Count >= 32)
                    throw new ArgumentException("Use at most 32 metadata filters with 1-128 character keys and values of at most 1024 characters, without control characters.");
                metadata.Add(key, value);
            }
            else if (entry.Key is not ("team" or "type" or "cursor" or "limit" or "defaults"))
                throw new ArgumentException("Use only team, type, cursor, limit, defaults and exact meta.<key> catalogue filters.");
        }
        if (query.ContainsKey("defaults") && query["defaults"].ToString() is not ("true" or "false"))
            throw new ArgumentException("The defaults flag must be true or false.");
        var team = query["team"].Count == 0 ? null : query["team"].ToString();
        var type = query["type"].Count == 0 ? null : query["type"].ToString();
        if (team is not null && !ValidSegment(team) ||
            type is not null && (team is null || !ValidSegment(type)))
            throw new ArgumentException("Use exact team/type names of 1-128 Unicode characters. A type filter requires its team.");
        var limit = 50;
        if (query.ContainsKey("limit") &&
            (!int.TryParse(query["limit"], NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100))
            throw new ArgumentException("The catalogue page limit must be a whole number from 1 to 100.");
        string? after = null;
        if (query.ContainsKey("cursor"))
        {
            var cursor = query["cursor"].ToString();
            if (cursor.Length is < 1 or > 2051) throw new ArgumentException("The catalogue cursor is invalid.");
            try { after = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(cursor)); }
            catch (Exception error) when (error is FormatException or DecoderFallbackException)
            {
                throw new ArgumentException("The catalogue cursor is invalid.");
            }
            if (!WorkflowIdentity.TryGetTypeScope(after, out _) || Cursor(after) != cursor)
                throw new ArgumentException("The catalogue cursor is invalid.");
        }
        var result = new CatalogQuery(team, type, after, limit) { UsePresentationDefaults = query["defaults"] == "true" };
        return metadata.Count == 0 ? result : result with { Metadata = metadata.ToImmutableDictionary(StringComparer.Ordinal) };
    }

    public static string Cursor(string workflowId) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(workflowId));
    public static bool ValidSegment(string value) => WorkflowIdentity.TryGetTypeScope(value + "/type/workflow", out _);
}

[JsonSerializable(typeof(IReadOnlyDictionary<string, string>), TypeInfoPropertyName = "MetadataFilters")]
public partial class CatalogQueryJsonContext : JsonSerializerContext;
