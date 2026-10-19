using System.ComponentModel;
using System.Text.Json;
using Wf.Sdk.Testing;
using Wf.Validation.Build;

namespace Wf.Validation.Tests;

public sealed class ValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public async Task EmptyDirectoryIsAnArgumentError(string? directory)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            Validator.Validate(directory!, cancellationToken: ValidatorFixture.Token));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            Validator.ValidateAsync(directory!, cancellationToken: ValidatorFixture.Token));
    }

    [Fact]
    [RuleCoverage("WFV001", false)]
    public async Task MissingDirectoryReturnsStableRelativeError()
    {
        using var fixture = new ValidatorFixture();
        var directory = Path.Combine(fixture.Root, "missing");
        var result = await Validator.ValidateAsync(directory, cancellationToken: ValidatorFixture.Token);
        AssertFailure(result, new("WFV001", ".", "$",
            "The workflow directory does not exist or is not a directory.",
            "Pass an existing workflow directory."));
        Assert.Equal(result.Errors, Validator.Validate(directory, cancellationToken: ValidatorFixture.Token).Errors);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task FileInsteadOfDirectoryReturnsDirectoryError()
    {
        using var fixture = new ValidatorFixture();
        File.WriteAllText(fixture.Script, "");
        var result = await Validator.ValidateAsync(fixture.Script, cancellationToken: ValidatorFixture.Token);
        AssertFailure(result, RuleCatalog.DirectoryNotFound.CreateError(".", "$"));
    }

    [Fact]
    [RuleCoverage("WFV002", false)]
    public async Task MissingScriptReturnsStableErrorWithoutRunningDotnet()
    {
        using var fixture = new ValidatorFixture();
        File.WriteAllText(Path.Combine(fixture.Source, "helper.cs"), "not a workflow");
        var unavailable = new ScriptBuildCache(fixture.CacheDirectory, fixture.ArtifactsDirectory,
            Path.Combine(fixture.Root, "dotnet-does-not-exist"));
        var result = await Validator.ValidateAsync(fixture.Source, unavailable,
            cancellationToken: ValidatorFixture.Token);
        AssertFailure(result, new("WFV002", "workflow.cs", "$",
            "The workflow directory does not contain workflow.cs.",
            "Create workflow.cs in the workflow directory."));
        Assert.False(Directory.Exists(fixture.CacheDirectory));
        Assert.False(Directory.Exists(Path.Combine(fixture.Source, "bin")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Source, "obj")));
    }

    [Fact]
    public async Task DirectoryNamedWorkflowCsIsNotAScript()
    {
        using var fixture = new ValidatorFixture();
        Directory.CreateDirectory(fixture.Script);
        AssertFailure(await Validator.ValidateAsync(fixture.Source, cancellationToken: ValidatorFixture.Token),
            RuleCatalog.WorkflowNotFound.CreateError("workflow.cs", "$"));
    }

    [Fact]
    public async Task RelativeAndAbsoluteInputsProduceIdenticalErrorPayloads()
    {
        using var fixture = new ValidatorFixture();
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), fixture.Source);
        var first = await Validator.ValidateAsync(relative, cancellationToken: ValidatorFixture.Token);
        var second = await Validator.ValidateAsync(fixture.Source, cancellationToken: ValidatorFixture.Token);
        Assert.Equal(first.Errors, second.Errors);
        Assert.DoesNotContain(fixture.Root, JsonSerializer.Serialize(first.Errors));
    }

    [Fact]
    public async Task PreCancelledValidationThrowsEvenForMissingInputs()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Validator.ValidateAsync("missing", cancellationToken: cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Validator.Validate("missing", cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task TestingFacadePreservesErrorsAndCancellation()
    {
        using var fixture = new ValidatorFixture();
        var direct = await Validator.ValidateAsync(fixture.Source, cancellationToken: ValidatorFixture.Token);
        Assert.Equal(direct.Errors, WorkflowValidation.Validate(fixture.Source,
            cancellationToken: ValidatorFixture.Token).Errors);
        Assert.Equal(direct.Errors, (await WorkflowValidation.ValidateAsync(fixture.Source,
            cancellationToken: ValidatorFixture.Token)).Errors);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            WorkflowValidation.Validate(fixture.Source, cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WorkflowValidation.ValidateAsync(fixture.Source, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ProcessStartInfrastructureFailureIsNotAValidationResult()
    {
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow();
        var unavailable = new ScriptBuildCache(fixture.CacheDirectory, fixture.ArtifactsDirectory,
            Path.Combine(fixture.Root, "dotnet-does-not-exist"));
        await Assert.ThrowsAsync<Win32Exception>(() =>
            Validator.ValidateAsync(fixture.Source, unavailable, cancellationToken: ValidatorFixture.Token));
    }

    [Fact]
    public async Task MissingAdditionalInputIsNotAValidationResult()
    {
        using var fixture = new ValidatorFixture();
        fixture.WriteWorkflow();
        await Assert.ThrowsAsync<FileNotFoundException>(() => Validator.ValidateAsync(fixture.Source,
            fixture.Cache, [Path.Combine(fixture.Root, "missing-dependency")], ValidatorFixture.Token));
    }

    [Fact]
    public void CatalogueHasStableUniqueCompleteDescriptors()
    {
        Assert.Equal(["WFV001", "WFV002", "WFV003", "WFV004", "WFV005", "WFV006", "WFV007", "WFV008", "WFV009", "WFV010",
            "WFV011", "WFV012", "WFV013", "WFV014", "WFV015", "WFV016", "WFV017", "WFV018", "WFV019", "WFV020",
            "WFV021", "WFV022", "WFV023", "WFV024", "WFV025", "WFV026", "WFV027", "WFV028"],
            RuleCatalog.All.Select(rule => rule.Id));
        Assert.Equal(RuleCatalog.All.Length, RuleCatalog.All.Select(rule => rule.Id).Distinct().Count());
        Assert.All(RuleCatalog.All, rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Message));
            Assert.False(string.IsNullOrWhiteSpace(rule.Hint));
            Assert.Equal(new(rule.Id, "workflow.cs", "$", rule.Message, rule.Hint),
                rule.CreateError("workflow.cs", "$"));
        });
        Assert.Equal([RuleCatalog.DirectoryNotFound, RuleCatalog.WorkflowNotFound,
            RuleCatalog.BuildFailed, RuleCatalog.DescribeFailed], RuleCatalog.All.Take(4));
    }

    [Fact]
    public void ErrorIsAnImmutableValueWithFilePathMessageAndHint()
    {
        var error = new ValidationError("WFV003", "workflow.cs", "$", "Build failed.", "Fix compiler errors.");
        var same = new ValidationError("WFV003", "workflow.cs", "$", "Build failed.", "Fix compiler errors.");
        Assert.Equal(error, same);
        Assert.Equal(error.GetHashCode(), same.GetHashCode());
        var restored = JsonSerializer.Deserialize<ValidationError>(JsonSerializer.Serialize(error));
        Assert.Equal(error, restored);
    }

    private static void AssertFailure(ValidationResult result, ValidationError expected)
    {
        Assert.False(result.IsValid);
        Assert.Equal(expected, Assert.Single(result.Errors));
        Assert.Null(result.Build);
        Assert.Null(result.BuildFailure);
    }
}
