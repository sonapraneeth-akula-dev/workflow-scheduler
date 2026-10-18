using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;
using Wf.Contracts;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Api;
using Wf.Sdk.Tests;

namespace Wf.Integration.Tests.Auth;

public sealed partial class AuthorizationMatrixTests
{
    [Theory]
    [InlineData("7d", 7)]
    [InlineData("30d", 30)]
    [InlineData("90d", 90)]
    public async Task ServiceHistory_counts_all_statuses_across_series_and_manual_runs_with_real_daily_gaps(string window, int days)
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        var workflow = seed.Type + "/a";
        await CatalogWorkflow(workflow);
        await CatalogWorkflow(seed.Type + "/b-never");
        await CatalogWorkflow(seed.Team + "/sibling/hidden");
        await CatalogWorkflow(seed.OtherType + "/hidden");
        var clock = new ManualTimeProvider();
        var now = clock.GetUtcNow();
        var first = now.Date.AddDays(1 - days);
        foreach (var status in Enum.GetValues<RunStatus>())
            await ServiceHistoryRun(workflow, new(first, TimeSpan.Zero), status,
                status == RunStatus.Succeeded ? null : Guid.NewGuid());
        await ServiceHistoryRun(workflow, now.AddMinutes(1), RunStatus.Succeeded, Guid.NewGuid());
        await ServiceHistoryRun(workflow, new(first.AddTicks(-1), TimeSpan.Zero), RunStatus.Failed, null);
        await ServiceHistoryRun(workflow, new(now.Date.AddDays(1), TimeSpan.Zero), RunStatus.Failed, null);
        await ServiceHistoryRun(seed.Team + "/sibling/hidden", now, RunStatus.Failed, null);
        await ServiceHistoryRun(seed.OtherType + "/hidden", now, RunStatus.Failed, null);
        await using var server = await Server.Start(database.DataSource,
            configureServices: services => services.AddSingleton<TimeProvider>(clock));
        using var response = await server.GetPath($"/api/v1/service-history?team={seed.Team}&window={window}", server.Token(group: group));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        var page = (await response.Content.ReadFromJsonAsync<ServiceHistoryPage>(TestContext.Current.CancellationToken))!;
        Assert.Equal(seed.Team, page.Team);
        Assert.Equal(window, page.Window);
        Assert.Equal(days, page.To.DayNumber - page.From.DayNumber);
        Assert.Equal(2, page.Items.Count);
        var item = Assert.Single(page.Items, entry => entry.Workflow.Id == workflow);
        Assert.Equal(days, item.Days.Count);
        Assert.Equal(page.From, item.Days[0].Day);
        Assert.Equal(page.To.AddDays(-1), item.Days[^1].Day);
        Assert.All(Enum.GetNames<RunStatus>(), status => Assert.Equal(1, item.Days[0].Counts[status]));
        Assert.Equal(1, item.Days[^1].Counts["Succeeded"]);
        Assert.All(item.Days.Skip(1).SkipLast(1), day => Assert.Empty(day.Counts));
        Assert.Equal(8, item.Days.Sum(day => day.Counts.Values.Sum()));
        var never = Assert.Single(page.Items, entry => entry.Workflow.Name == "b-never");
        Assert.Null(never.Workflow.LatestRun);
        Assert.All(never.Days, day => Assert.Empty(day.Counts));
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        foreach (var secret in new[] { seed.Other, "hidden", "private-payload", "variables", "roleBindings", "packageUri" })
            Assert.DoesNotContain(secret, json);
    }

    [Fact]
    public async Task ServiceHistory_reuses_catalog_paging_and_rechecks_revocation_without_global_or_workload_leaks()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        foreach (var name in new[] { "a", "c", "e" }) await CatalogWorkflow(seed.Type + "/" + name);
        await using var server = await Server.Start(database.DataSource);
        var path = "/api/v1/service-history?team=" + seed.Team + "&type=release&limit=2";
        var jwt = server.Token(group: group);
        using var response = await server.GetPath(path, jwt);
        var first = (await response.Content.ReadFromJsonAsync<ServiceHistoryPage>(TestContext.Current.CancellationToken))!;
        Assert.Equal(["a", "c"], first.Items.Select(item => item.Workflow.Name));
        Assert.NotNull(first.NextCursor);
        await CatalogWorkflow(seed.Type + "/b");
        using var nextResponse = await server.GetPath(path + "&cursor=" + first.NextCursor, jwt);
        var next = (await nextResponse.Content.ReadFromJsonAsync<ServiceHistoryPage>(TestContext.Current.CancellationToken))!;
        Assert.Equal("e", Assert.Single(next.Items).Workflow.Name);
        Assert.Null(next.NextCursor);
        foreach (var token in new[]
        {
            server.Token(group: group, identity: "app", role: "Agent"),
            server.Token(group: group, extra: [new("hasgroups", "true")])
        })
        {
            using var denied = await server.GetPath(path, token);
            Assert.Empty((await denied.Content.ReadFromJsonAsync<ServiceHistoryPage>(TestContext.Current.CancellationToken))!.Items);
        }
        using var anonymous = await server.GetPath(path, null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await Save(new(seed.Team, [new(seed.Team, "release", "Service history", new([]))]));
        using var revoked = await server.GetPath(path, jwt);
        Assert.Empty((await revoked.Content.ReadFromJsonAsync<ServiceHistoryPage>(TestContext.Current.CancellationToken))!.Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?type=release")]
    [InlineData("?team=demo&window=1d")]
    [InlineData("?team=demo&window=7d&window=30d")]
    [InlineData("?team=demo&team=other")]
    [InlineData("?team=demo&status=Running")]
    [InlineData("?team=demo&limit=101")]
    [InlineData("?team=demo&cursor=not-a-cursor")]
    public async Task ServiceHistory_rejects_invalid_or_unscoped_queries(string query)
    {
        await using var server = await Server.Start(database.DataSource);
        using var response = await server.GetPath("/api/v1/service-history" + query, server.Token(role: "Admin"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task ServiceHistoryRun(string workflow, DateTimeOffset at, RunStatus status, Guid? series)
    {
        var id = Guid.CreateVersion7(at);
        await using (var partition = database.DataSource.CreateCommand("SELECT ensure_run_partitions(uuid_v7_run_day($1))"))
        {
            partition.Parameters.AddWithValue(id);
            await partition.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using var query = database.DataSource.CreateCommand("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,series_id,trigger,triggered_by,variables,metadata,status,created_at)
            VALUES(uuid_v7_run_day($1),$1,$2,$1,$3,0,'service-history','{"private-payload":"secret"}','{}',$4,$5)
            """);
        query.Parameters.AddWithValue(id);
        query.Parameters.AddWithValue(workflow);
        query.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)series ?? DBNull.Value);
        query.Parameters.AddWithValue((short)status);
        query.Parameters.AddWithValue(at);
        await query.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
