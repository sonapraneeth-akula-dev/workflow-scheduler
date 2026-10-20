using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;

namespace Wf.Integration.Tests.Registration;

public sealed partial class UserSchedulesTests
{
    [Fact]
    public async Task RecurringJobs_global_list_is_scoped_paged_filtered_and_includes_paused_Code_and_User_jobs()
    {
        await using var server = await RegistrationTests.Server.Start(database.DataSource, userSchedules: true);
        var first = await Prepare(server);
        var second = await Prepare(server);
        var foreign = await Prepare(server);
        using (var registered = await server.Post(first.Request with
        {
            Schedules = """{"instances":[{"id":"code","timezone":"Etc/UTC","interval":"00:05:00"}]}""",
            ScheduleSourcePath = "workflow.schedules.json"
        }))
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var writer = Operator(server, first.Group);
        var active = await Create(server, first.Draft, writer);
        var paused = await Create(server, first.Draft with { Name = "Paused recurring job" }, writer);
        await Change(server, HttpMethod.Post, "/" + paused.Id + "/pause", writer, "\"1\"");
        var removed = await Create(server, first.Draft with { Name = "Removed recurring job" }, writer);
        using (var deletion = await Send(server, HttpMethod.Delete, "/" + removed.Id, writer, "\"1\""))
            Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
        var other = await Create(server, second.Draft, Operator(server, second.Group));
        var hidden = await Create(server, foreign.Draft, Operator(server, foreign.Group));
        var reader = server.Token("", "user", "wf.read", groups: [first.Group, second.Group], authorizedClient: null);
        var items = new List<UserScheduleResponse>();
        string? cursor = null;
        do
        {
            Assert.True(items.Count < 10, "Global schedule pagination did not terminate.");
            using var response = await server.Send(HttpMethod.Get,
                BasePath + "?limit=1" + (cursor is null ? "" : "&cursor=" + cursor), new ByteArrayContent([]), reader);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var page = (await response.Content.ReadFromJsonAsync(UserScheduleJson.Default.ScheduleListPage, Token))!;
            var item = Assert.Single(page.Items);
            Assert.DoesNotContain(items, existing => existing.Id == item.Id);
            items.Add(item);
            if (page.NextCursor is { } next)
            {
                foreach (var changed in new[]
                {
                    BasePath + "?limit=2&cursor=" + next,
                    BasePath + "?limit=1&team=" + first.Draft.WorkflowId.Split('/')[0] + "&cursor=" + next
                })
                {
                    using var invalid = await server.Send(HttpMethod.Get, changed, new ByteArrayContent([]), reader);
                    Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                }
                using var narrowed = await server.Send(HttpMethod.Get, BasePath + "?limit=1&cursor=" + next,
                    new ByteArrayContent([]), server.Token("", "user", "wf.read", groups: [first.Group]));
                Assert.Equal(HttpStatusCode.BadRequest, narrowed.StatusCode);
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(4, items.Count);
        Assert.Equal(items.Select(item => item.Id).Order(), items.Select(item => item.Id));
        Assert.Contains(items, item => item.Id == active.Id && item.Origin == "User");
        Assert.Contains(items, item => item.Id == paused.Id && item.Paused && item.NextFireAt is null);
        Assert.Contains(items, item => item.Id == other.Id);
        Assert.Single(items, item => item.Origin == "Code");
        Assert.DoesNotContain(items, item => item.Id == removed.Id || item.Id == hidden.Id);
        var identity = first.Draft.WorkflowId.Split('/');
        using var filtered = await server.Send(HttpMethod.Get,
            BasePath + "?team=" + identity[0] + "&type=" + identity[1], new ByteArrayContent([]), reader);
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        var matching = (await filtered.Content.ReadFromJsonAsync(UserScheduleJson.Default.ScheduleListPage, Token))!;
        Assert.Equal(3, matching.Items.Length);
        Assert.All(matching.Items, item => Assert.Equal(first.Draft.WorkflowId, item.WorkflowId));
        using var noAccess = await server.Send(HttpMethod.Get, BasePath, new ByteArrayContent([]),
            server.Token("", "user", "wf.read"));
        Assert.Equal(HttpStatusCode.OK, noAccess.StatusCode);
        Assert.Empty((await noAccess.Content.ReadFromJsonAsync(UserScheduleJson.Default.ScheduleListPage, Token))!.Items);
        using var anonymous = await server.Send(HttpMethod.Get, BasePath, new ByteArrayContent([]), useDefaultToken: false);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task RecurringJobs_global_list_rechecks_revoked_grants_and_rejects_the_previous_scope_cursor()
    {
        await using var server = await RegistrationTests.Server.Start(database.DataSource, userSchedules: true);
        var prepared = await Prepare(server);
        var writer = Operator(server, prepared.Group);
        await Create(server, prepared.Draft, writer);
        await Create(server, prepared.Draft with { Name = "Second job" }, writer);
        var reader = server.Token("", "user", "wf.read", groups: [prepared.Group], authorizedClient: null);
        using var before = await server.Send(HttpMethod.Get, BasePath + "?limit=1", new ByteArrayContent([]), reader);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var page = (await before.Content.ReadFromJsonAsync(UserScheduleJson.Default.ScheduleListPage, Token))!;
        Assert.NotNull(page.NextCursor);
        var registry = prepared.Request.Registry!.Value.Deserialize(TeamRegistryJsonContext.Default.TeamRegistry)! with { RoleBindings = [] };
        using var revoked = await server.Post(new(prepared.Request.GitCommit, prepared.Request.GitAuthor,
            Registry: JsonSerializer.SerializeToElement(registry, TeamRegistryJsonContext.Default.TeamRegistry)));
        Assert.Equal(HttpStatusCode.Created, revoked.StatusCode);
        using var stale = await server.Send(HttpMethod.Get, BasePath + "?limit=1&cursor=" + page.NextCursor,
            new ByteArrayContent([]), reader);
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        using var after = await server.Send(HttpMethod.Get, BasePath, new ByteArrayContent([]), reader);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Empty((await after.Content.ReadFromJsonAsync(UserScheduleJson.Default.ScheduleListPage, Token))!.Items);
        Assert.Empty(server.Failures);
    }
}
