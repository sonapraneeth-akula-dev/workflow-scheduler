using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Sdk;
using Wf.Sdk.Local;
using Wf.Validation.Rules;

namespace Wf.Validation.Tests;

public sealed class SecretRefTests
{
    [Theory]
    [InlineData("secret://local/deploy-token", "local", "deploy-token")]
    [InlineData("secret://vault-prod/services/deploy.token", "vault-prod", "services/deploy.token")]
    [InlineData("secret://a1/Token_2", "a1", "Token_2")]
    public void Qualified_references_resolve_only_store_and_name(string text, string store, string name)
    {
        var reference = SecretReference.Parse(text);
        Assert.Equal(text, reference.Name);
        Assert.Equal(text, reference.ToString());
        Assert.Equal(store, reference.Store);
        Assert.Equal(name, reference.SecretName);
        Assert.True(SecretReference.IsValidName(text));
    }

    [Theory]
    [InlineData("deploy-token")]
    [InlineData("store/token")]
    public void Existing_bare_names_remain_local_references_without_changing_manifest_shape(string name)
    {
        var reference = new SecretReference(name);
        Assert.Equal("local", reference.Store);
        Assert.Equal(name, reference.SecretName);
        Assert.Throws<ArgumentException>(() => SecretReference.Parse(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unqualified secret value")]
    [InlineData(" secret://local/token")]
    [InlineData("secret:///token")]
    [InlineData("secret://Local/token")]
    [InlineData("secret://-local/token")]
    [InlineData("secret://local-/token")]
    [InlineData("secret://local")]
    [InlineData("secret://local/")]
    [InlineData("secret://local/../token")]
    [InlineData("secret://local/a/./token")]
    [InlineData("secret://local/a//token")]
    [InlineData("secret://local/token?value=literal")]
    [InlineData("secret://local/token#literal")]
    [InlineData("secret://user:password@vault/token")]
    [InlineData("secret://local/token%2Fvalue")]
    [InlineData("secret://local/token\\value")]
    [InlineData("https://vault/secrets/token")]
    [InlineData("token\n")]
    [InlineData("../token")]
    public void Invalid_reference_is_rejected_without_echoing_input(string text)
    {
        Assert.False(SecretReference.IsValidName(text));
        var error = Assert.Throws<ArgumentException>(() => new SecretReference(text));
        if (text.Length > 0) Assert.DoesNotContain(text, error.Message);
    }

    [Fact]
    public void Reference_limits_are_exact_and_null_is_rejected()
    {
        Assert.True(SecretReference.IsValidName($"secret://{new string('a', 63)}/{new string('b', 200)}"));
        Assert.False(SecretReference.IsValidName($"secret://{new string('a', 64)}/token"));
        Assert.False(SecretReference.IsValidName($"secret://local/{new string('b', 201)}"));
        Assert.False(SecretReference.IsValidName(null));
    }

    [Theory]
    [InlineData("\"literal-secret-value\"")]
    [InlineData("""{"Name":"token","Value":"literal-secret-value"}""")]
    [InlineData("""{"Name":"token","Name":"other"}""")]
    [InlineData("""{"Name":"secret://local/token?value=literal-secret-value"}""")]
    [InlineData("""{"Name":123}""")]
    [InlineData("""{"name":"secret://local/token"}""")]
    public void Literal_or_extra_secret_fields_fail_shared_and_manifest_validation(string json)
    {
        using var document = JsonDocument.Parse(json);
        var error = Assert.Single(SecretRefValidator.Validate(document.RootElement, "workflow.cs", "$.variables[0].default"));
        Assert.Equal("WFV008", error.RuleId);
        Assert.Equal("$.variables[0].default", error.Path);
        Assert.DoesNotContain("literal-secret-value", error.Message + error.Hint);
        var manifest = $$"""{"variables":[{"key":"token","type":"Secret","default":{{json}}}]}""";
        using var manifestDocument = JsonDocument.Parse(manifest);
        var errors = ManifestRules.Validate(manifestDocument.RootElement,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(errors, item => item.Path == "$.variables[0].default" || item.RuleId == "WFV007");
        Assert.All(errors, item => Assert.DoesNotContain("literal-secret-value", item.Message + item.Hint));
    }

    [Fact]
    public void SDK_manifest_and_runtime_binding_keep_only_reference_names()
    {
        var workflow = Workflow.Define("secrets", "1.0.0", builder => builder.Type("test")
            .Variables(variables => variables.Secret("token", "secret://vault-prod/deploy"))
            .Node("work", node => node.Task("noop", _ => Task.CompletedTask)));
        var manifest = ManifestWriter.Create(workflow);
        var json = JsonSerializer.Serialize(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest);
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement.GetProperty("variables")[0].GetProperty("default");
        Assert.Single(value.EnumerateObject());
        Assert.Equal("secret://vault-prod/deploy", value.GetProperty("Name").GetString());
        Assert.Empty(SecretRefValidator.Validate(value));
        Assert.Empty(ManifestRules.Validate(manifest, cancellationToken: TestContext.Current.CancellationToken));
        var values = LocalVariables.Parse(workflow.Variables, ["--var", "token=secret://local/override"]);
        var bound = VariableBinder.Bind(workflow.Variables, values);
        var reference = Assert.IsType<SecretReference>(bound["token"]);
        Assert.Equal("local", reference.Store);
        Assert.Equal("override", reference.SecretName);
        var parsed = JsonSerializer.Deserialize(json, WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        Assert.True(JsonElement.DeepEquals(workflow.Variables[0].Default!.Value, parsed.Variables[0].Default!.Value));
    }
}
