using System.Net;
using System.Text.Json;
using Wf.Contracts.Runs;
using Wf.ControlPlane.Api;
using static Wf.Integration.Tests.Registration.RegistrationTests;

namespace Wf.Integration.Tests.Registration;

public sealed partial class TriggerRunTests
{
    [Fact]
    public async Task TriggerRun_uses_the_immutable_uuid_returned_by_variable_schema_after_active_version_changes()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var request = await Request(server);
        var group = Guid.NewGuid();
        var first = await Register(server, request, group);
        var bearer = Operator(server, group);
        using var schema = await server.Send(HttpMethod.Get, "/api/v1/workflows/" + request.WorkflowId + "/variables",
            null!, token: bearer, useDefaultToken: false);
        Assert.Equal(HttpStatusCode.OK, schema.StatusCode);
        var selected = JsonSerializer.Deserialize(await schema.Content.ReadAsStringAsync(Token),
            VariablesJsonContext.Default.WorkflowVariablesResponse)!;
        Assert.Equal(first, selected.VersionId);
        var latest = await Register(server, request with { Manifest = Manifest(request.WorkflowId!, "1.0.1") }, group);
        Assert.NotEqual(first, latest);
        var run = await Accepted(server, new TriggerRunRequest(request.WorkflowId!, selected.VersionId.ToString("D")), bearer);
        Assert.Equal(first, (await Payload(run)).GetProperty("VersionId").GetGuid());
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task TriggerRun_openapi_exposes_typed_body_response_and_optional_idempotency_header()
    {
        await using var server = await Server.Start(database.DataSource, runs: true, userSchedules: true);
        using var response = await server.Send(HttpMethod.Get, "/openapi/v1.json", null!, token: server.Token("Admin"));
        var json = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, json);
        using var document = JsonDocument.Parse(json);
        var post = document.RootElement.GetProperty("paths").GetProperty("/api/v1/runs").GetProperty("post");
        Assert.Equal("#/components/schemas/TriggerRunRequest", post.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/TriggerRunResponse", post.GetProperty("responses").GetProperty("202")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var header = Assert.Single(post.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.Equal("header", header.GetProperty("in").GetString());
        Assert.False(header.TryGetProperty("required", out var required) && required.GetBoolean());
        Assert.Equal("string", header.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal(200, header.GetProperty("schema").GetProperty("maxLength").GetInt32());
        var fields = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("TriggerRunRequest").GetProperty("properties");
        foreach (var field in new[] { "workflowId", "version", "variables" }) Assert.True(fields.TryGetProperty(field, out _));
        foreach (var status in new[] { "400", "401", "403", "404", "409", "413", "415", "503" })
            Assert.True(post.GetProperty("responses").TryGetProperty(status, out _));
        Assert.Empty(server.Failures);
    }
}
