using System.Net;
using System.Text.Json;
using Wf.ControlPlane.Api;
using static Wf.Integration.Tests.Registration.RegistrationTests;

namespace Wf.Integration.Tests.Registration;

public sealed partial class UserSchedulesTests
{
    [Fact]
    public async Task CodeScheduleReadOnly_provenance_follows_the_exact_applied_schedule_config_not_the_workflow_version_or_retry_commit()
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        var first = prepared.Request with
        {
            GitCommit = new('b', 40),
            Schedules = """{"instances":[{"id":"code","timezone":"Etc/UTC","interval":"00:01:00"}]}""",
            ScheduleSourcePath = "operations/workflow.schedules.json"
        };
        using (var registered = await server.Post(first)) Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var id = Assert.IsType<Guid>(await Scalar("SELECT id FROM schedules WHERE workflow_id=$1", first.WorkflowId!));
        var bearer = Operator(server, prepared.Group);
        using var response = await Send(server, HttpMethod.Get, "/" + id, bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var original = await Read(response);
        Assert.Equal("Code", original.Origin);
        Assert.Equal(first.ScheduleSourcePath, original.SourcePath);
        Assert.Equal(first.GitCommit, original.GitCommit);
        var second = first with
        {
            GitCommit = new('c', 64),
            Schedules = first.Schedules!.Replace("00:01:00", "00:02:00", StringComparison.Ordinal),
            ScheduleSourcePath = "deployment/renamed.schedules.json"
        };
        using (var registered = await server.Post(second)) Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        using var changed = await Send(server, HttpMethod.Get, "/" + id, bearer);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var current = await Read(changed);
        Assert.Equal(second.ScheduleSourcePath, current.SourcePath);
        Assert.Equal(second.GitCommit, current.GitCommit);
        Assert.Equal(original.SeriesId, current.SeriesId);
        Assert.Equal(prepared.Version, await Scalar("SELECT active_version_id FROM workflows WHERE id=$1", first.WorkflowId!));
        var tag = changed.Headers.ETag!.Tag;
        using (var retried = await server.Post(second with { GitCommit = new('d', 40) }))
            Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        using var afterRetry = await Send(server, HttpMethod.Get, "/" + id, bearer);
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(current, UserScheduleJson.Default.UserScheduleResponse),
            JsonSerializer.SerializeToElement(await Read(afterRetry), UserScheduleJson.Default.UserScheduleResponse)));
        Assert.Equal(tag, afterRetry.Headers.ETag!.Tag);
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task CodeScheduleReadOnly_public_Viewer_reads_do_not_require_the_SPA_and_user_schedules_have_no_code_provenance()
    {
        await using var server = await Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        var user = await Create(server, prepared.Draft, Operator(server, prepared.Group));
        Assert.Null(user.SourcePath);
        Assert.Null(user.GitCommit);
        using var registered = await server.Post(prepared.Request with
        {
            Schedules = """{"instances":[{"id":"code","timezone":"Etc/UTC","interval":"00:01:00"}]}""",
            ScheduleSourcePath = "workflow.schedules.json"
        });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var id = Assert.IsType<Guid>(await Scalar("SELECT id FROM schedules WHERE workflow_id=$1 AND origin=0", prepared.Request.WorkflowId!));
        await Execute("UPDATE role_bindings SET role=0 WHERE principal_id=$1", prepared.Group);
        var viewer = server.Token("Viewer", "user", "wf.read", groups: [prepared.Group], authorizedClient: Guid.NewGuid().ToString());
        using var read = await Send(server, HttpMethod.Get, "/" + id, viewer);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var code = await Read(read);
        Assert.Equal("Code", code.Origin);
        Assert.Equal(prepared.Request.GitCommit, code.GitCommit);
        Assert.Equal("workflow.schedules.json", code.SourcePath);
        using var foreign = await Send(server, HttpMethod.Get, "/" + id,
            server.Token("Viewer", "user", "wf.read", groups: [Guid.NewGuid()]));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.DoesNotContain("workflow.schedules.json", await foreign.Content.ReadAsStringAsync(Token));
        Assert.Empty(server.Failures);
    }
}
