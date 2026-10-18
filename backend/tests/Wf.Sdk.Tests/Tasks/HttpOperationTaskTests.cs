using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Definition;
using Wf.Contracts.Manifest;
using Wf.Sdk;
using Wf.Validation.Rules;

namespace Wf.Sdk.Tests;

public sealed class HttpOperationTaskTests
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(2);
    private static HttpOperationStatusContract Status => new("state", Retention);

    [Fact]
    public void TypedTaskPreservesContractsInSourceGeneratedManifestWithoutRequestPayload()
    {
        var origins = new List<string> { "https://status.example.test" };
        var operation = Tasks.HttpOperation("https://submit.example.test/jobs",
            new("state", Retention, operationIdField: "id",
                urlTemplate: "https://status.example.test/jobs/{operationId}", resultField: "result", errorField: "error"),
            cancel: new("{statusUrl}/cancel", HttpOperationCancelMethod.Delete),
            deduplication: new("Idempotency-Key", Retention),
            lookup: new("https://submit.example.test/jobs/by-key/{submissionKey}", Retention),
            trustedOrigins: origins, secretReference: "remote-service", body: "request-body-do-not-persist");
        origins.Clear();
        var workflow = Workflow.Define("remote", "1.0.0", w => w.Type("test")
            .PreSetup(s => s.Task("remote", operation, p => p.NoAutoRetry().Retry(0)))
            .Node("jobs", n => n.Task("submit", operation, p => p.Retry(2))));
        var bytes = ManifestWriter.WriteUtf8(workflow);
        var manifest = JsonSerializer.Deserialize(bytes, WorkflowManifestJsonContext.Default.WorkflowManifest)!;
        Assert.Empty(ManifestRules.Validate(manifest, cancellationToken: TestContext.Current.CancellationToken));
        Assert.DoesNotContain("request-body-do-not-persist", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest));
        foreach (var node in manifest.Nodes)
        {
            var contract = Assert.Single(node.Tasks).HttpOperation!;
            Assert.Equal("https://submit.example.test/jobs", contract.SubmissionUrl);
            Assert.Equal("https://status.example.test/jobs/{operationId}", contract.Status.UrlTemplate);
            Assert.Equal("id", contract.Status.OperationIdField);
            Assert.Equal("result", contract.Status.ResultField);
            Assert.Equal("error", contract.Status.ErrorField);
            Assert.Equal("{statusUrl}/cancel", contract.Cancel!.UrlTemplate);
            Assert.Equal(HttpOperationCancelMethod.Delete, contract.Cancel.Method);
            Assert.Equal(TimeSpan.FromSeconds(10), contract.Cancel.RequestTimeout);
            Assert.Equal("Idempotency-Key", contract.Deduplication!.HeaderName);
            Assert.True(contract.Lookup!.Authoritative);
            Assert.Equal(["https://status.example.test"], contract.TrustedOrigins);
            Assert.Equal("remote-service", contract.SecretReference);
            Assert.Equal(TimeSpan.FromSeconds(30), contract.RequestTimeout);
        }
        Assert.Equal(["$pre/remote", "jobs/submit"], workflow.Tasks.Keys.Order());
    }

    [Fact]
    public async Task RuntimeRequiresDurableCheckpointAndIdentityWithoutSendingAnything()
    {
        var operation = Tasks.HttpOperation("https://must-not-contact.invalid/jobs", Status);
        var workflow = Workflow.Define("remote", "1.0.0", w => w.Type("test")
            .Node("jobs", n => n.Task("submit", operation, p => p.NoAutoRetry().Retry(0))));
        var context = new TaskContext(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<Wf.Contracts.TaskFailureException>(() =>
            new TaskRegistry(workflow).ExecuteAsync("jobs/submit", context));
        Assert.False(error.Retryable);
        Assert.Equal(FailureCategory.ValidationError, error.Category);
        Assert.StartsWith("RemoteOperationRequiresCheckpoint:", error.Message);
        Assert.DoesNotContain("must-not-contact", error.Message);
        Assert.Empty(context.Outputs.Values);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("file:///private")]
    [InlineData("ftp://example.test/jobs")]
    [InlineData("https://user:do-not-echo@example.test/jobs")]
    [InlineData("https://example.test/jobs?sig=do-not-echo")]
    [InlineData("https://example.test/jobs#fragment")]
    [InlineData("https://example.test/jobs\\other")]
    [InlineData("https://example.test/jobs%0d%0a")]
    [InlineData("https://example.test/{unknown}")]
    public void UntrustedSubmissionUrlsHaveSanitizedDefinitionErrors(string value)
    {
        var error = Assert.Throws<ArgumentException>(() => Tasks.HttpOperation(value, Status));
        Assert.Equal("Invalid HTTP operation contract.", error.Message);
    }

    [Theory]
    [InlineData("https://other.test/jobs/{operationId}")]
    [InlineData("https://{operationId}.example.test/jobs")]
    [InlineData("https://example.test/jobs/{unknown}")]
    [InlineData("https://example.test/jobs/{operationId}?token=do-not-echo")]
    public void StatusTemplatesCannotChangeOriginOrPersistTokens(string template) =>
        Assert.Throws<ArgumentException>(() => Tasks.HttpOperation("https://example.test/jobs",
            new("state", Retention, operationIdField: "id", urlTemplate: template)));

    [Fact]
    public void InvalidAndUnboundedContractFieldsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new HttpOperationStatusContract("", Retention));
        Assert.Throws<ArgumentException>(() => new HttpOperationStatusContract(new string('x', 129), Retention));
        Assert.Throws<ArgumentException>(() => new HttpOperationStatusContract("state", TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new HttpOperationStatusContract("state", TimeSpan.FromDays(366)));
        Assert.Throws<ArgumentException>(() => new HttpOperationStatusContract("state", Retention, running: "Pending"));
        Assert.Throws<ArgumentException>(() => new HttpOperationStatusContract("state", Retention, locationHeader: null));
        Assert.Throws<ArgumentException>(() => new HttpOperationStatusContract("state", Retention,
            urlTemplate: "https://example.test/jobs/{operationId}"));
        Assert.Throws<ArgumentException>(() => new HttpOperationStatusContract("state", Retention, locationHeader: "bad\r\nheader"));
        Assert.Throws<ArgumentException>(() => new HttpOperationDeduplicationContract("Authorization", Retention));
        Assert.Throws<ArgumentException>(() => new HttpOperationLookupContract("https://example.test/{submissionKey}",
            Retention, authoritative: false));
        Assert.Throws<ArgumentException>(() => new HttpOperationCancelContract("{statusUrl}/cancel",
            requestTimeout: TimeSpan.FromSeconds(11)));
        Assert.Throws<ArgumentException>(() => Tasks.HttpOperation("https://example.test/jobs", Status,
            requestTimeout: TimeSpan.FromSeconds(31)));
        Assert.Throws<ArgumentException>(() => Tasks.HttpOperation("https://example.test/jobs", Status,
            secretReference: "do-not-echo\r\nsecret"));
        Assert.Throws<ArgumentException>(() => Tasks.HttpOperation("https://example.test/jobs", Status,
            trustedOrigins: Enumerable.Repeat("https://other.test", 17)));
        Assert.Throws<ArgumentException>(() => Tasks.HttpOperation("https://example.test/jobs", Status,
            trustedOrigins: ["https://other.test/path/.."]));
        Assert.Throws<ArgumentException>(() => Tasks.HttpOperation("https://example.test/" + new string('x', 2048), Status));
        Assert.Throws<ArgumentException>(() => Tasks.HttpOperation("https://example.test/jobs", Status,
            body: new string('x', 1024 * 1024 + 1)));
        var inputError = Assert.Throws<ArgumentException>(() => Tasks.HttpOperation("https://example.test/jobs", Status,
            contentType: "do-not-echo\r\nheader"));
        Assert.DoesNotContain("do-not-echo", inputError.Message);
    }

    [Fact]
    public void IdBasedStatusCanOmitLocationAndCancelThroughItsDeclaredTrustedOrigin()
    {
        var operation = Tasks.HttpOperation("https://example.test/jobs",
            new("state", Retention, locationHeader: null, operationIdField: "id",
                urlTemplate: "https://example.test/jobs/{operationId}"),
            cancel: new("https://example.test/jobs/{operationId}/cancel"));
        Assert.Null(operation.Definition.Status.LocationHeader);
        Assert.Equal(ImmutableArray<string>.Empty, operation.Definition.TrustedOrigins);
    }

    [Theory]
    [InlineData("remote-service")]
    [InlineData("secret://vault/remote/token")]
    public void SecretReferencesUseTheSharedGrammarWithoutEmbeddingValues(string reference)
    {
        var operation = Tasks.HttpOperation("https://example.test/jobs", Status, secretReference: reference);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(operation.Definition,
            WorkflowManifestJsonContext.Default.HttpOperationDefinition);
        var contract = JsonSerializer.Deserialize(bytes, WorkflowManifestJsonContext.Default.HttpOperationDefinition)!;
        Assert.Equal(reference, contract.SecretReference);
        Assert.Throws<ArgumentException>(() => Tasks.HttpOperation("https://example.test/jobs", Status,
            secretReference: new string('x', 201)));
    }
}
