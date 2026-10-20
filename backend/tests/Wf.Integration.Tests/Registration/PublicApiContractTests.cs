using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wf.ControlPlane.Api;

namespace Wf.Integration.Tests.Registration;

public sealed partial class UserSchedulesTests
{
    [Fact]
    public async Task PublicApiContract_schedule_list_is_public_Viewer_scoped_paged_and_matches_existing_reads()
    {
        await using var server = await RegistrationTests.Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        using (var registered = await server.Post(prepared.Request with
        {
            Schedules = """{"instances":[{"id":"code","timezone":"Etc/UTC","interval":"00:01:00"}]}""",
            ScheduleSourcePath = "workflow.schedules.json"
        }))
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var writer = Operator(server, prepared.Group);
        var first = await Create(server, prepared.Draft, writer);
        var second = await Create(server, prepared.Draft with { Name = "Second UI schedule" }, writer);
        var removed = await Create(server, prepared.Draft with { Name = "Deleted UI schedule" }, writer);
        using (var deletion = await Send(server, HttpMethod.Delete, "/" + removed.Id, writer, "\"1\""))
            Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
        var reader = server.Token("", "user", "wf.read", groups: [prepared.Group], authorizedClient: "11111111-1111-1111-1111-111111111111");
        var path = BasePath + "?workflowId=" + Uri.EscapeDataString(prepared.Draft.WorkflowId) + "&limit=1";
        var collected = new List<UserScheduleResponse>();
        string? cursor = null;
        var before = await Scalar("SELECT count(*) FROM audit_log WHERE target_id=$1", prepared.Draft.WorkflowId);
        do
        {
            Assert.True(collected.Count < 10, "Schedule pagination did not terminate.");
            using var response = await server.Send(HttpMethod.Get, path + (cursor is null ? "" : "&cursor=" + cursor),
                new ByteArrayContent([]), reader);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var page = (await response.Content.ReadFromJsonAsync(UserScheduleJson.Default.ScheduleListPage, Token))!;
            var item = Assert.Single(page.Items);
            Assert.Equal(prepared.Draft.WorkflowId, item.WorkflowId);
            Assert.DoesNotContain(collected, row => row.Id == item.Id);
            using var original = await Send(server, HttpMethod.Get, "/" + item.Id, reader);
            Assert.Equal(HttpStatusCode.OK, original.StatusCode);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(item, UserScheduleJson.Default.UserScheduleResponse),
                JsonSerializer.SerializeToElement(await Read(original), UserScheduleJson.Default.UserScheduleResponse)));
            collected.Add(item);
            if (page.NextCursor is not null)
            {
                Assert.NotEqual(cursor, page.NextCursor);
                using var changed = await server.Send(HttpMethod.Get,
                    path.Replace("limit=1", "limit=2", StringComparison.Ordinal) + "&cursor=" + page.NextCursor,
                    new ByteArrayContent([]), reader);
                Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Contains(collected, row => row.Id == first.Id && row.Origin == "User");
        Assert.Contains(collected, row => row.Id == second.Id);
        Assert.DoesNotContain(collected, row => row.Id == removed.Id);
        var code = Assert.Single(collected, row => row.Origin == "Code");
        Assert.Equal("workflow.schedules.json", code.SourcePath);
        Assert.Equal(prepared.Request.GitCommit, code.GitCommit);
        Assert.Equal(3, collected.Count);
        Assert.Equal(collected.Select(row => row.Id).Order(), collected.Select(row => row.Id));
        Assert.Equal(before, await Scalar("SELECT count(*) FROM audit_log WHERE target_id=$1", prepared.Draft.WorkflowId));
        using var app = await server.Send(HttpMethod.Get, path, new ByteArrayContent([]), server.Token("Admin", "app", null));
        Assert.Equal(HttpStatusCode.OK, app.StatusCode);
        using var denied = await server.Send(HttpMethod.Get, path, new ByteArrayContent([]), server.Token("", "user", "wf.read"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.True(denied.Headers.CacheControl!.NoStore);
        using var anonymous = await server.Send(HttpMethod.Get, path, new ByteArrayContent([]), useDefaultToken: false);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        foreach (var invalid in new[] { "?type=release", "?workflowId=bad", "?workflowId=a/b/c&limit=101",
            "?workflowId=a/b/c&workflowId=d/e/f", "?workflowId=a/b/c&unknown=value" })
        {
            using var response = await server.Send(HttpMethod.Get, BasePath + invalid, new ByteArrayContent([]), reader);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Empty(server.Failures);
    }
}
