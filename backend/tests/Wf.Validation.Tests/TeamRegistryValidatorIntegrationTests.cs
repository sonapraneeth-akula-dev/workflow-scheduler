using System.Text;
using Wf.Sdk.Testing;
using Wf.Validation.Rules;

namespace Wf.Validation.Tests;

[Collection("Validator")]
public sealed class TeamRegistryValidatorIntegrationTests
{
    private const string ValidTeam = """
        {"team":"demo","types":[{"team":"demo","name":"release","description":"Release workflows",
            "metadataSchema":{"keys":[],"allowFreeForm":false}}]}
        """;

    [Fact]
    public async Task TeamRegistrySharedValidatorDiscoversTeamFileAndMatchesDirectRuleErrors()
    {
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow();
        var team = Path.Combine(fixture.Root, "team.json");
        await File.WriteAllTextAsync(team, ValidTeam, ValidatorFixture.Token);
        var valid = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs,
            ValidatorFixture.Token);
        Assert.True(valid.IsValid);
        Assert.NotNull(valid.Build);
        var invalidCases = new[] {
            "not-json",
            ValidTeam.Replace("\"name\":\"release\"", "\"name\":\"other\"", StringComparison.Ordinal),
            ValidTeam.Replace("\"keys\":[]",
                "\"keys\":[{\"key\":\"environment\",\"required\":true,\"allowedValues\":[\"dev\"]}]",
                StringComparison.Ordinal),
            """
            {"team":"demo","types":[
              {"team":"demo","name":"Load-Test","description":"First","metadataSchema":{"keys":[]}},
              {"team":"demo","name":"load_tests","description":"Conflicting","metadataSchema":{"keys":[]}}
            ]}
            """,
            """
            {"team":"demo","roleBindings":[{"role":"Viewer","groupId":"00000000-0000-0000-0000-000000000000"}],
              "types":[{"team":"demo","name":"release","description":"Release","metadataSchema":{"keys":[]}}]}
            """
        };
        foreach (var json in invalidCases)
        {
            await File.WriteAllTextAsync(team, json, ValidatorFixture.Token);
            var expected = TeamRegistryRules.ValidateWorkflow(Encoding.UTF8.GetBytes(json), valid.Build.Manifest.Metadata,
                file: "../team.json", cancellationToken: ValidatorFixture.Token);
            Assert.NotEmpty(expected);
            var direct = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs,
                ValidatorFixture.Token);
            var facade = await WorkflowValidation.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs,
                ValidatorFixture.Token);
            Assert.Equal(expected, direct.Errors);
            Assert.Equal(expected, facade.Errors);
        }
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "team.json"), ValidTeam, ValidatorFixture.Token);
        Assert.True((await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs,
            ValidatorFixture.Token)).IsValid);
        File.Delete(Path.Combine(fixture.Source, "team.json"));
        File.Delete(team);
        Directory.CreateDirectory(team);
        var directory = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs,
            ValidatorFixture.Token);
        Assert.Equal(TeamRegistryRules.RegistryShape.CreateError("../team.json", "$"),
            Assert.Single(directory.Errors));
        Directory.Delete(team);
        await File.WriteAllTextAsync(team, invalidCases[1], ValidatorFixture.Token);
        await File.WriteAllTextAsync(Path.Combine(fixture.Source, "schedules.json"), """
            {"instances":[{"id":"smoke","cron":"* * * * *","timezone":"Etc/UTC","variables":{"unknown":1}}]}
            """, ValidatorFixture.Token);
        var combined = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.AdditionalInputs,
            ValidatorFixture.Token);
        Assert.Equal(2, combined.Errors.Length);
        Assert.Contains(combined.Errors, error => error.RuleId == "WFV013");
        Assert.Contains(combined.Errors, error => error.RuleId == "WFV019");
    }
}
