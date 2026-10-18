using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Wf.Contracts.Registry;
using Wf.Validation.Rules;

namespace Wf.Validation.Tests;

public sealed class TeamRegistrySchemaTests
{
    private static readonly Guid Group = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Editor_schema_properties_match_every_source_generated_registry_contract()
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(BackendRoot(), "src", "Wf.Validation", "Schemas", "team.schema.json")))!;
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema["$schema"]!.GetValue<string>());
        AssertShape(schema, TeamRegistryJsonContext.Default.TeamRegistry, ["team", "types"]);
        var definitions = schema["$defs"]!;
        AssertShape(definitions["presentation"]!, TeamRegistryJsonContext.Default.TeamPresentation, []);
        AssertShape(definitions["workflowType"]!, TeamRegistryJsonContext.Default.WorkflowTypeDefinition,
            ["team", "name", "description", "metadataSchema"]);
        AssertShape(definitions["metadataSchema"]!, TeamRegistryJsonContext.Default.MetadataSchema, ["keys"]);
        AssertShape(definitions["metadataField"]!, TeamRegistryJsonContext.Default.MetadataFieldSchema, ["key"]);
        AssertShape(definitions["roleBinding"]!, TeamRegistryJsonContext.Default.RegistryRoleBinding, ["role", "groupId"]);
        Assert.Equal(Enum.GetNames<RegistryRole>(),
            definitions["roleBinding"]!["properties"]!["role"]!["enum"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Equal("\\S", definitions["nonemptyText"]!["pattern"]!.GetValue<string>());
        Assert.Equal("uuid", definitions["roleBinding"]!["properties"]!["groupId"]!["format"]!.GetValue<string>());
        Assert.Equal(Guid.Empty.ToString("D"),
            definitions["roleBinding"]!["properties"]!["groupId"]!["not"]!["const"]!.GetValue<string>());
    }

    [Fact]
    public void Published_sample_round_trips_metadata_and_team_type_role_scopes()
    {
        var path = Path.Combine(BackendRoot(), "samples", "workflows", "demo", "teams", "demo", "team.json");
        var json = File.ReadAllText(path);
        var registry = Read(json);
        Assert.Empty(TeamRegistryRules.Validate(Encoding.UTF8.GetBytes(json), cancellationToken: Token));
        Assert.Equal("demo", registry.Team);
        Assert.Equal("Demo", registry.Deployment);
        var type = Assert.Single(registry.Types);
        Assert.Equal("release", type.Name);
        Assert.Equal("demo", type.Team);
        Assert.Equal(new RegistryRoleBinding(RegistryRole.Viewer, Group), Assert.Single(registry.RoleBindings));
        Assert.Equal(RegistryRole.Author, Assert.Single(type.RoleBindings).Role);
        Assert.Equal(["dev", "ppe"], type.MetadataSchema.Keys[0].AllowedValues);
        Assert.True(type.MetadataSchema.Keys[0].Required);
        var serialized = JsonSerializer.Serialize(registry, TeamRegistryJsonContext.Default.TeamRegistry);
        Assert.Equal(serialized, JsonSerializer.Serialize(Read(serialized), TeamRegistryJsonContext.Default.TeamRegistry));
        Assert.True(File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, registry.Schema!))));
    }

    [Theory]
    [InlineData("team")]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("metadataSchema")]
    public void Missing_required_type_properties_fail_the_runtime_contract(string property)
    {
        var document = JsonNode.Parse(ValidJson())!;
        Assert.True(document["types"]![0]!.AsObject().Remove(property));
        Assert.Throws<JsonException>(() => Read(document.ToJsonString()));
    }

    [Theory]
    [InlineData(RegistryRole.Viewer)]
    [InlineData(RegistryRole.Operator)]
    [InlineData(RegistryRole.Author)]
    [InlineData(RegistryRole.Admin)]
    public void Every_role_is_a_string_and_keeps_its_exact_group_and_scope(RegistryRole role)
    {
        var registry = Read(ValidJson());
        var binding = new RegistryRoleBinding(role, Group);
        registry = registry with { RoleBindings = [binding], Types = [registry.Types[0] with { RoleBindings = [binding] }] };
        Assert.Empty(TeamRegistryRules.Validate(registry, cancellationToken: Token));
        var json = JsonSerializer.Serialize(registry, TeamRegistryJsonContext.Default.TeamRegistry);
        Assert.Contains($"\"role\": \"{role}\"", json);
        var restored = Read(json);
        Assert.Equal(binding, Assert.Single(restored.RoleBindings));
        Assert.Equal(binding, Assert.Single(Assert.Single(restored.Types).RoleBindings));
    }

    [Theory]
    [InlineData("""[{"role":42,"groupId":"11111111-1111-1111-1111-111111111111"}]""")]
    [InlineData("""[{"role":"Owner","groupId":"11111111-1111-1111-1111-111111111111"}]""")]
    [InlineData("""[{"role":"viewer","groupId":"11111111-1111-1111-1111-111111111111","unknown":true}]""")]
    [InlineData("""[{"role":"Viewer","groupId":"not-a-group-id"}]""")]
    [InlineData("""[{"role":"Viewer"}]""")]
    [InlineData("""[{"groupId":"11111111-1111-1111-1111-111111111111"}]""")]
    [InlineData("""[{"role":"Viewer","role":"Admin","groupId":"11111111-1111-1111-1111-111111111111"}]""")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Malformed_role_map_returns_sanitized_shape_error(string bindings)
    {
        var document = JsonNode.Parse(ValidJson())!;
        document["roleBindings"] = JsonNode.Parse(bindings);
        var error = Assert.Single(TeamRegistryRules.Validate(Encoding.UTF8.GetBytes(document.ToJsonString()), cancellationToken: Token));
        Assert.Equal(TeamRegistryRules.RegistryShape.CreateError("team.json", "$"), error);
        Assert.DoesNotContain("11111111", error.Message + error.Hint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_group_unknown_role_duplicate_and_null_bindings_are_rejected_in_each_scope(bool typeScope)
    {
        var valid = new RegistryRoleBinding(RegistryRole.Viewer, Group);
        ImmutableArray<RegistryRoleBinding>[] invalid =
        [
            [valid with { GroupId = Guid.Empty }],
            [valid with { Role = (RegistryRole)99 }],
            [valid, valid],
            [null!]
        ];
        foreach (var bindings in invalid)
        {
            var registry = Read(ValidJson());
            registry = typeScope ? registry with { Types = [registry.Types[0] with { RoleBindings = bindings }] }
                : registry with { RoleBindings = bindings };
            var error = Assert.Single(TeamRegistryRules.Validate(registry, cancellationToken: Token));
            Assert.Equal("WFV015", error.RuleId);
            var index = bindings.Length == 2 ? 1 : 0;
            Assert.Equal((typeScope ? "$.types[0].roleBindings" : "$.roleBindings") + $"[{index}]", error.Path);
        }
    }

    [Fact]
    public void Omitted_maps_are_empty_and_same_group_can_have_different_roles()
    {
        var registry = Read(ValidJson());
        Assert.Empty(registry.RoleBindings);
        Assert.Empty(registry.Types[0].RoleBindings);
        registry = registry with { RoleBindings = [new(RegistryRole.Viewer, Group), new(RegistryRole.Operator, Group)] };
        Assert.Empty(TeamRegistryRules.Validate(registry, cancellationToken: Token));
    }

    private static string ValidJson() => """
        {"team":"demo","types":[{"team":"demo","name":"release","description":"Release","metadataSchema":{"keys":[]}}]}
        """;

    private static TeamRegistry Read(string json) => JsonSerializer.Deserialize(json, TeamRegistryJsonContext.Default.TeamRegistry)!;

    [Theory]
    [InlineData("Demo", true)]
    [InlineData("Production", true)]
    [InlineData("demo", false)]
    [InlineData("", false)]
    [InlineData("unknown", false)]
    public void Deployment_is_exact_and_defaults_to_production(string deployment, bool valid)
    {
        var registry = Read(ValidJson());
        Assert.Equal("Production", registry.Deployment);
        var errors = TeamRegistryRules.Validate(registry with { Deployment = deployment }, cancellationToken: Token);
        if (valid) Assert.Empty(errors);
        else Assert.Equal("$.deployment", Assert.Single(errors).Path);
    }

    private static void AssertShape(JsonNode schema, JsonTypeInfo type, string[] required)
    {
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(type.Properties.Select(property => property.Name).Order(StringComparer.Ordinal),
            schema["properties"]!.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.Equal(required, schema["required"]!.AsArray().Select(value => value!.GetValue<string>()));
    }

    private static string BackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wf.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Cannot locate backend root.");
    }
}
