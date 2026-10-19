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
    [InlineData(null, 14)]
    [InlineData("7d", 7)]
    [InlineData("14d", 14)]
    [InlineData("30d", 30)]
    [InlineData("90d", 90)]
    public async Task ServiceHistory_counts_all_statuses_across_series_and_manual_runs_with_real_daily_gaps(string? window, int days)
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
        var ids = new List<Guid>();
        foreach (var status in Enum.GetValues<RunStatus>())
            ids.Add(await ServiceHistoryRun(workflow, new(first, TimeSpan.Zero), status,
                status == RunStatus.Succeeded ? null : Guid.NewGuid()));
        var latest = await ServiceHistoryRun(workflow, now.AddMinutes(1), RunStatus.Succeeded, Guid.NewGuid(),
            now.AddMinutes(1).AddSeconds(1), now.AddMinutes(1).AddSeconds(2));
        ids.Add(latest);
        await ServiceHistoryRun(workflow, new(first.AddTicks(-1), TimeSpan.Zero), RunStatus.Failed, null);
        await ServiceHistoryRun(workflow, new(now.Date.AddDays(1), TimeSpan.Zero), RunStatus.Failed, null);
        await ServiceHistoryRun(seed.Team + "/sibling/hidden", now, RunStatus.Failed, null);
        await ServiceHistoryRun(seed.OtherType + "/hidden", now, RunStatus.Failed, null);
        await using var server = await Server.Start(database.DataSource,
            configureServices: services => services.AddSingleton<TimeProvider>(clock));
        var path = $"/api/v1/service-history?team={seed.Team}" + (window is null ? "" : $"&window={window}");
        using var response = await server.GetPath(path, server.Token(group: group));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        var page = (await response.Content.ReadFromJsonAsync<ServiceHistoryPage>(TestContext.Current.CancellationToken))!;
        Assert.Equal(seed.Team, page.Team);
        Assert.Equal(window ?? "14d", page.Window);
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
        Assert.Equal(ids.Order(), item.Runs.Select(run => run.Id).Order());
        Assert.Equal(latest, item.Runs[0].Id);
        Assert.Equal(now.AddMinutes(1).AddSeconds(1), item.Runs[0].StartedAt);
        Assert.Equal(now.AddMinutes(1).AddSeconds(2), item.Runs[0].EndedAt);
        Assert.All(Enum.GetNames<RunStatus>(), status => Assert.Contains(item.Runs, run => run.Status == status));
        var never = Assert.Single(page.Items, entry => entry.Workflow.Name == "b-never");
        Assert.Null(never.Workflow.LatestRun);
        Assert.All(never.Days, day => Assert.Empty(day.Counts));
        Assert.Empty(never.Runs);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        foreach (var secret in new[] { seed.Other, "hidden", "private-payload", "variables", "roleBindings", "packageUri" })
            Assert.DoesNotContain(secret, json);
    }

    [Fact]
    public async Task ServiceHistory_limits_instances_per_workflow_without_truncating_counts_and_preserves_precise_order()
    {
        var group = Guid.NewGuid();
        var seed = await Seed(RegistryRole.Viewer, group, false);
        var clock = new ManualTimeProvider();
        var now = clock.GetUtcNow();
        var expected = new Dictionary<string, List<CatalogLatestRun>>();
        foreach (var (name, count) in new[] { ("a", 65), ("b", 55) })
        {
            var workflow = seed.Type + "/" + name;
            await CatalogWorkflow(workflow);
            var instances = new List<CatalogLatestRun>();
            for (var index = 0; index < count; index++)
            {
                var at = now.AddTicks(index % 3 * 10);
                var status = index % 2 == 0 ? RunStatus.Succeeded : RunStatus.Failed;
                var id = await ServiceHistoryRun(workflow, at, status, index % 2 == 0 ? null : Guid.NewGuid());
                instances.Add(new(id, status.ToString(), at, null, null));
            }
            expected.Add(workflow, instances);
        }
        await using var server = await Server.Start(database.DataSource,
            configureServices: services => services.AddSingleton<TimeProvider>(clock));
        using var response = await server.GetPath("/api/v1/service-history?team=" + seed.Team, server.Token(group: group));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = (await response.Content.ReadFromJsonAsync<ServiceHistoryPage>(TestContext.Current.CancellationToken))!;
        Assert.Equal(2, page.Items.Count);
        foreach (var item in page.Items)
        {
            var instances = expected[item.Workflow.Id];
            Assert.Equal(instances.Count, item.Days.Sum(day => day.Counts.Values.Sum()));
            Assert.Equal(instances.OrderByDescending(run => run.CreatedAt).ThenByDescending(run => run.Id).Take(50),
                item.Runs);
        }
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
        Assert.Equal("14d", first.Window);
        Assert.All(first.Items, item => Assert.Equal(14, item.Days.Count));
        Assert.All(first.Items, item => Assert.Empty(item.Runs));
        Assert.NotNull(first.NextCursor);
        await CatalogWorkflow(seed.Type + "/b");
        using var nextResponse = await server.GetPath(path + "&cursor=" + first.NextCursor, jwt);
        var next = (await nextResponse.Content.ReadFromJsonAsync<ServiceHistoryPage>(TestContext.Current.CancellationToken))!;
        Assert.Equal("e", Assert.Single(next.Items).Workflow.Name);
        Assert.Equal(first.From, next.From);
        Assert.Equal(first.To, next.To);
        Assert.Equal(14, next.Items[0].Days.Count);
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
    [InlineData("?team=demo&window=")]
    [InlineData("?team=demo&window=14d&window=14d")]
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

    private async Task<Guid> ServiceHistoryRun(string workflow, DateTimeOffset at, RunStatus status, Guid? series,
        DateTimeOffset? startedAt = null, DateTimeOffset? endedAt = null)
    {
        var id = Guid.CreateVersion7(at);
        await using (var partition = database.DataSource.CreateCommand("SELECT ensure_run_partitions(uuid_v7_run_day($1))"))
        {
            partition.Parameters.AddWithValue(id);
            await partition.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using var query = database.DataSource.CreateCommand("""
            INSERT INTO runs(run_day,id,workflow_id,version_id,series_id,trigger,triggered_by,variables,metadata,status,created_at,started_at,ended_at)
            VALUES(uuid_v7_run_day($1),$1,$2,$1,$3,0,'service-history','{"private-payload":"secret"}','{}',$4,$5,$6,$7)
            """);
        query.Parameters.AddWithValue(id);
        query.Parameters.AddWithValue(workflow);
        query.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)series ?? DBNull.Value);
        query.Parameters.AddWithValue((short)status);
        query.Parameters.AddWithValue(at);
        query.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)startedAt ?? DBNull.Value);
        query.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)endedAt ?? DBNull.Value);
        await query.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }
}
