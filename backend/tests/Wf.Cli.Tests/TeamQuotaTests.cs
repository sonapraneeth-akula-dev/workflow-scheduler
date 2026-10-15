using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Registry;
using Wf.Validation;
using Wf.Validation.Rules;

namespace Wf.Cli.Tests;

public sealed class TeamQuotaArgumentTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("1.5")]
    [InlineData("ten")]
    [InlineData("+10")]
    [InlineData(" 10")]
    [InlineData("10 ")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("--unknown")]
    public async Task Invalid_quota_is_an_explicit_usage_error_before_validation(string value)
    {
        var result = await ValidateFixture.RunAsync(["missing", "--team-type-quota", value], ValidateFixture.Token);
        Assert.Equal(2, result.Exit);
        Assert.Contains("--team-type-quota requires a positive integer.", result.Error);
        Assert.Empty(result.Output);
        Assert.DoesNotContain("WFV001", result.Error);
    }

    [Theory]
    [InlineData("missing --team-type-quota", "requires a positive integer")]
    [InlineData("missing --team-type-quota 10 --team-type-quota 11", "may be specified only once")]
    [InlineData("missing --team-type-quota 10 --unknown 11", "Usage: wf validate")]
    [InlineData("missing --team-type-quota 10 unexpected", "Usage: wf validate")]
    public async Task Missing_duplicate_and_unknown_arguments_are_not_ignored(string arguments, string diagnostic)
    {
        var result = await ValidateFixture.RunAsync(arguments.Split(' '), ValidateFixture.Token);
        Assert.Equal(2, result.Exit);
        Assert.Contains(diagnostic, result.Error);
        Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("10")]
    [InlineData("0011")]
    [InlineData("2147483647")]
    public async Task Positive_integer_quota_reaches_shared_validation(string quota)
    {
        var result = await ValidateFixture.RunAsync(
            [Path.Combine("missing", Guid.NewGuid().ToString("N")), "--team-type-quota", quota],
            ValidateFixture.Token);
        Assert.Equal(1, result.Exit);
        Assert.Contains("WFV001", result.Error);
        Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Shared_validator_rejects_invalid_options_even_without_a_team_or_workflow(int quota)
    {
        var directory = Path.Combine("missing", Guid.NewGuid().ToString("N"));
        var options = new ValidationOptions { TypeQuota = quota };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Validator.Validate(directory, cancellationToken: ValidateFixture.Token, options: options));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Validator.ValidateAsync(directory, cancellationToken: ValidateFixture.Token, options: options));
    }
}

[Collection("Validate")]
public sealed class TeamQuotaTests(ValidateFixture fixture, ITestOutputHelper output)
{
    private const string Secret = "author-controlled-do-not-echo";
    private string TeamPath => Path.Combine(fixture.Root, "team.json");

    [Fact]
    public async Task Real_cli_allows_ten_rejects_eleven_and_accepts_trusted_quota_override()
    {
        try
        {
            await WriteTeamAsync(10);
            var ten = await ValidateFixture.ProcessAsync(["validate", .. fixture.Arguments()]);
            output.WriteLine($"team10 exit={ten.Exit}; stdout={ten.Output.Trim()}; stderr={ten.Error.Trim()}");
            Assert.True(ten.Exit == 0, ten.Error);
            Assert.Contains("Valid: tests/cli/validate@1.0.0", ten.Output);
            Assert.Empty(ten.Error);

            await WriteTeamAsync(11);
            var eleven = await ValidateFixture.ProcessAsync(["validate", .. fixture.Arguments()]);
            output.WriteLine($"team11 exit={eleven.Exit}; stdout={eleven.Output.Trim()}; stderr={eleven.Error.Trim()}");
            AssertQuotaFailure(eleven, 10);

            var allowed = await ValidateFixture.ProcessAsync(
                ["validate", .. fixture.Arguments("--team-type-quota", "11")]);
            output.WriteLine($"team11 quota11 exit={allowed.Exit}; stdout={allowed.Output.Trim()}; stderr={allowed.Error.Trim()}");
            Assert.True(allowed.Exit == 0, allowed.Error);
            Assert.Empty(allowed.Error);
            Assert.Contains("Valid: tests/cli/validate@1.0.0", allowed.Output);

            var lowered = await ValidateFixture.ProcessAsync(
                ["validate", .. fixture.Arguments("--team-type-quota", "9")]);
            output.WriteLine($"team11 quota9 exit={lowered.Exit}; stdout={lowered.Output.Trim()}; stderr={lowered.Error.Trim()}");
            AssertQuotaFailure(lowered, 9);
        }
        finally { File.Delete(TeamPath); }
    }

    [Fact]
    public async Task Shared_sync_and_async_entry_points_preserve_default_and_configured_quotas_on_cached_workflow()
    {
        Assert.Equal(10, new ValidationOptions().TypeQuota);
        try
        {
            await WriteTeamAsync(10);
            var ten = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.Closure, ValidateFixture.Token);
            Assert.True(ten.IsValid);
            Assert.True(ten.Build!.CacheHit);

            await WriteTeamAsync(11);
            var eleven = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.Closure, ValidateFixture.Token);
            var error = Assert.Single(eleven.Errors);
            Assert.Equal("WFV017", error.RuleId);
            Assert.Contains("Configured type quota: 10.", error.Hint);
            Assert.True(eleven.Build!.CacheHit);

            var allowed = Validator.Validate(fixture.Source, fixture.Cache, fixture.Closure, ValidateFixture.Token,
                options: new ValidationOptions { TypeQuota = 11 });
            Assert.True(allowed.IsValid);
            Assert.True(allowed.Build!.CacheHit);

            var lowered = await Validator.ValidateAsync(fixture.Source, fixture.Cache, fixture.Closure, ValidateFixture.Token,
                options: new ValidationOptions { TypeQuota = 9 });
            Assert.Contains("Configured type quota: 9.", Assert.Single(lowered.Errors).Hint);
        }
        finally { File.Delete(TeamPath); }
    }

    [Fact]
    public async Task Real_cli_rejects_author_controlled_registry_quota_instead_of_using_it()
    {
        try
        {
            var bytes = RegistryBytes(11);
            using var document = JsonDocument.Parse(bytes);
            await File.WriteAllTextAsync(TeamPath,
                document.RootElement.GetRawText().Insert(1, "\"typeQuota\":999,"), ValidateFixture.Token);
            var result = await ValidateFixture.ProcessAsync(["validate", .. fixture.Arguments()]);
            Assert.Equal(1, result.Exit);
            using var diagnostic = JsonDocument.Parse(result.Error.Trim());
            Assert.Equal("WFV015", diagnostic.RootElement.GetProperty("ruleId").GetString());
            Assert.DoesNotContain(Secret, result.Error);
            Assert.Empty(result.Output);
        }
        finally { File.Delete(TeamPath); }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("invalid")]
    [InlineData("2147483648")]
    public async Task Real_cli_rejects_invalid_quota_without_executing_validation(string value)
    {
        var result = await ValidateFixture.ProcessAsync(
            ["validate", .. fixture.Arguments("--team-type-quota", value)]);
        Assert.Equal(2, result.Exit);
        Assert.Contains("--team-type-quota requires a positive integer.", result.Error);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task Configured_quota_preserves_cancellation_and_does_not_create_package()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var options = new ValidationOptions { TypeQuota = 11 };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Validator.ValidateAsync(
            fixture.Source, fixture.Cache, fixture.Closure, cancellation.Token, options));
        var package = Path.Combine(fixture.NewDirectory(), "cancelled-quota.zip");
        var result = await ValidateFixture.RunAsync(
            fixture.Arguments("--team-type-quota", "11", "--package", package), cancellation.Token);
        Assert.Equal(130, result.Exit);
        Assert.Contains("cancelled", result.Error);
        Assert.Empty(result.Output);
        Assert.False(File.Exists(package));
    }

    private async Task WriteTeamAsync(int count) =>
        await File.WriteAllBytesAsync(TeamPath, RegistryBytes(count), ValidateFixture.Token);

    private static byte[] RegistryBytes(int count)
    {
        var types = Enumerable.Range(0, count).Select(index => new WorkflowTypeDefinition(
            Secret, index == 0 ? "test" : new string((char)('a' + index - 1), 8), Secret,
            new MetadataSchema([], AllowFreeForm: true))).ToImmutableArray();
        return JsonSerializer.SerializeToUtf8Bytes(new TeamRegistry(Secret, types), TeamRegistryJsonContext.Default.TeamRegistry);
    }

    private static void AssertQuotaFailure((int Exit, string Output, string Error) result, int quota)
    {
        Assert.Equal(1, result.Exit);
        Assert.Empty(result.Output);
        using var json = JsonDocument.Parse(result.Error.Trim());
        var diagnostic = json.RootElement;
        Assert.Equal(TeamRegistryRules.TypeQuota.Id, diagnostic.GetProperty("ruleId").GetString());
        Assert.Equal("../team.json", diagnostic.GetProperty("file").GetString());
        Assert.Equal("$.types", diagnostic.GetProperty("path").GetString());
        Assert.Contains("quota", diagnostic.GetProperty("message").GetString());
        Assert.Contains($"Configured type quota: {quota}.", diagnostic.GetProperty("hint").GetString());
        Assert.DoesNotContain(Secret, result.Error);
    }
}
