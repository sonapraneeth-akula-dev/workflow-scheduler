using System.Text.Json;
using Wf.Validation.SemVer;

namespace Wf.Cli.Tests;

[Collection("Validate")]
public sealed class ValidateCommandTests(ValidateFixture fixture)
{
    [Theory]
    [InlineData("")]
    [InlineData("--unknown")]
    [InlineData("directory --package")]
    [InlineData("directory --package --input value")]
    [InlineData("directory --nope value")]
    [InlineData("directory --package one --package two")]
    [InlineData("directory --baseline one --baseline two")]
    [InlineData("directory --work-directory one --work-directory two")]
    [InlineData("directory extra")]
    public async Task Validate_usage_errors_are_explicit(string arguments)
    {
        var result = await ValidateFixture.RunAsync(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), ValidateFixture.Token);
        Assert.Equal(2, result.Exit);
        Assert.Contains("Usage: wf validate", result.Error);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task Validate_missing_directory_and_script_return_structured_shared_errors()
    {
        foreach (var (directory, rule) in new[]
        {
            (Path.Combine(fixture.Root, "absent"), "WFV001"), (fixture.NewDirectory(), "WFV002")
        })
        {
            var result = await ValidateFixture.RunAsync([directory], ValidateFixture.Token);
            Assert.Equal(1, result.Exit);
            Assert.Empty(result.Output);
            using var json = JsonDocument.Parse(result.Error.Trim());
            Assert.Equal(rule, json.RootElement.GetProperty("ruleId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("file").GetString()));
            Assert.Equal("$", json.RootElement.GetProperty("path").GetString());
            Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("hint").GetString()));
        }
    }

    [Theory]
    [InlineData("this is not C#;", "WFV003", "error CS")]
    [InlineData("throw new InvalidOperationException(\"describe rejected\");", "WFV004", "describe rejected")]
    [InlineData("Console.WriteLine(\"not-json\");", null, "InvalidDataException")]
    public async Task Validate_compiler_describe_and_contract_failures_are_not_success(string body, string? rule, string detail)
    {
        var source = fixture.NewDirectory();
        File.WriteAllText(Path.Combine(source, "workflow.cs"), "#:property PublishAot=false\n" + body);
        var result = await ValidateFixture.ProcessAsync(["validate", source, "--work-directory", fixture.NewDirectory()]);
        Assert.Equal(rule is null ? 2 : 1, result.Exit);
        Assert.Contains(detail, result.Error);
        if (rule is not null) Assert.Contains(rule, result.Error);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task Validate_schedule_registry_and_baseline_errors_reuse_shared_rules()
    {
        var schedule = Path.Combine(fixture.Source, "schedules.json");
        var team = Path.Combine(fixture.Source, "team.json");
        var baseline = Path.Combine(fixture.NewDirectory(), "workflow.api.json");
        await File.WriteAllBytesAsync(baseline, ApiBaseline.FromManifest(fixture.Build.Manifest).WriteUtf8(), ValidateFixture.Token);
        try
        {
            File.WriteAllText(schedule, "{}");
            File.WriteAllText(team, "{}");
            var invalid = await ValidateFixture.RunAsync(fixture.Arguments(), ValidateFixture.Token);
            Assert.Equal(1, invalid.Exit);
            Assert.Contains("WFV011", invalid.Error);
            Assert.Contains("WFV015", invalid.Error);
            Assert.Empty(invalid.Output);
            File.Delete(schedule);
            File.Delete(team);
            var version = await ValidateFixture.RunAsync(fixture.Arguments("--baseline", baseline), ValidateFixture.Token);
            Assert.Equal(1, version.Exit);
            Assert.Contains("WFV010", version.Error);
            Assert.Empty(version.Output);
        }
        finally
        {
            File.Delete(schedule);
            File.Delete(team);
        }
    }

    [Fact]
    public async Task Validate_io_and_missing_external_inputs_are_infrastructure_errors()
    {
        foreach (var options in new[]
        {
            new[] { "--input", Path.Combine(fixture.Root, "missing") },
            new[] { "--baseline", Path.Combine(fixture.Root, "missing") },
            new[] { "--package", fixture.NewDirectory() }
        })
        {
            var result = await ValidateFixture.RunAsync(fixture.Arguments(options), ValidateFixture.Token);
            Assert.Equal(2, result.Exit);
            Assert.Empty(result.Output);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }
    }

    [Fact]
    public async Task Validate_cache_corruption_fails_without_stale_success()
    {
        var bytes = await File.ReadAllBytesAsync(fixture.Build.AssemblyPath, ValidateFixture.Token);
        try
        {
            File.WriteAllText(fixture.Build.AssemblyPath, "corrupt");
            var result = await ValidateFixture.RunAsync(fixture.Arguments(), ValidateFixture.Token);
            Assert.Equal(2, result.Exit);
            Assert.Contains("integrity", result.Error);
            Assert.Empty(result.Output);
        }
        finally { await File.WriteAllBytesAsync(fixture.Build.AssemblyPath, bytes, ValidateFixture.Token); }
    }

    [Fact]
    public async Task Validate_cancelled_operation_is_explicit_and_does_not_write_package()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var package = Path.Combine(fixture.NewDirectory(), "cancelled.zip");
        var result = await ValidateFixture.RunAsync(fixture.Arguments("--package", package), cancellation.Token);
        Assert.Equal(130, result.Exit);
        Assert.Contains("cancelled", result.Error);
        Assert.Empty(result.Output);
        Assert.False(File.Exists(package));
    }

    [Fact]
    public async Task Validate_real_cli_valid_and_invalid_process_exit_codes_and_package_hash()
    {
        var output = Path.Combine(fixture.NewDirectory(), "cli.zip");
        var valid = await ValidateFixture.ProcessAsync(["validate", .. fixture.Arguments("--package", output)]);
        Assert.True(valid.Exit == 0, valid.Error);
        Assert.Contains("Valid: tests/cli/validate@1.0.0", valid.Output);
        Assert.Empty(valid.Error);
        await using var stream = File.OpenRead(output);
        var hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ValidateFixture.Token));
        Assert.Contains($"SHA256: {hash}", valid.Output);
        var expected = await Packaging.PackageBuilder.BuildAsync(fixture.Build,
            Path.Combine(fixture.NewDirectory(), "direct.zip"), ValidateFixture.Token);
        Assert.Equal(expected.Sha256, hash);
        var invalid = await ValidateFixture.ProcessAsync(["validate", Path.Combine(fixture.Root, "missing")]);
        Assert.Equal(1, invalid.Exit);
        Assert.Contains("WFV001", invalid.Error);
        var usage = await ValidateFixture.ProcessAsync(["publish"]);
        Assert.Equal(2, usage.Exit);
        Assert.Contains("Usage:", usage.Error);
        var help = await ValidateFixture.ProcessAsync(["--help"]);
        Assert.Equal(0, help.Exit);
        Assert.Contains("Usage:", help.Output);
    }
}
