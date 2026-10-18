using System.Net;
using System.Text.Json;
using Wf.ControlPlane.Api;
using static Wf.Integration.Tests.Registration.RegistrationTests;

namespace Wf.Integration.Tests.Registration;

public sealed partial class ScheduleFireTests
{
    [Fact]
    public async Task OverlapSkip_public_series_history_keeps_atomic_skip_reason_without_creating_a_run()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        await using var engine = new Engine(database.DataSource, server);
        Assert.True(await engine.Lease.TryAcquireOrRenewAsync(Token));
        var next = (await engine.Scheduler.FireAsync(schedule.Timer, Token))!;
        server.Clock.Now = next.FireAt;
        await engine.Scheduler.FireAsync(next, Token);
        var page = await ReadFires(server, schedule.Series);
        Assert.Equal(schedule.Series, page.SeriesId);
        Assert.Null(page.NextCursor);
        Assert.Collection(page.Items,
            skipped =>
            {
                Assert.Equal(next.FireAt, skipped.FireAt);
                Assert.Equal("Skipped", skipped.Outcome);
                Assert.Equal("overlap", skipped.Reason);
                Assert.Null(skipped.RunId);
            },
            started =>
            {
                Assert.Equal(schedule.Timer.FireAt, started.FireAt);
                Assert.Equal("Started", started.Outcome);
                Assert.NotNull(started.RunId);
            });
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM runs WHERE series_id=$1", schedule.Series));
        await Execute("UPDATE schedules SET deleted_at=now(),paused=true,next_fire_at=NULL WHERE id=$1", schedule.Id);
        Assert.Equal(page.Items, (await ReadFires(server, schedule.Series)).Items);
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task SeriesFires_keyset_paging_is_bounded_exact_and_stable_when_a_newer_fire_arrives()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        var other = await CreateSchedule(server);
        var first = schedule.Timer.FireAt;
        await Execute("""
            INSERT INTO schedule_fires(schedule_id,fire_at,outcome,reason)
            SELECT $1,$2::timestamptz+n*interval '0.000001 second',(n%4)::smallint,'history'
            FROM generate_series(0,102) n
            """, schedule.Id, first);
        await Execute("INSERT INTO schedule_fires(schedule_id,fire_at,outcome) VALUES($1,$2,1)", other.Id, first);
        var page = await ReadFires(server, schedule.Series, "?limit=100");
        Assert.Equal(100, page.Items.Length);
        Assert.NotNull(page.NextCursor);
        Assert.Equal(new[] { "Started", "Skipped", "Missed", "CaughtUp" }.Order(),
            page.Items.Select(item => item.Outcome).Distinct().Order());
        await Execute("INSERT INTO schedule_fires(schedule_id,fire_at,outcome) VALUES($1,$2,0)",
            schedule.Id, first.AddDays(1));
        var last = await ReadFires(server, schedule.Series, "?limit=100&cursor=" + page.NextCursor);
        Assert.Equal(3, last.Items.Length);
        Assert.Null(last.NextCursor);
        var timestamps = page.Items.Concat(last.Items).Select(item => item.FireAt).ToArray();
        Assert.Equal(Enumerable.Range(0, 103).Reverse().Select(index => first.AddTicks(index * 10)), timestamps);
        Assert.Equal(103, timestamps.Distinct().Count());
        Assert.Equal(50, (await ReadFires(server, schedule.Series)).Items.Length);
        Assert.Single((await ReadFires(server, other.Series)).Items);
        Assert.Empty(server.Failures);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?limit=abc")]
    [InlineData("?cursor=")]
    [InlineData("?cursor=not-a-cursor")]
    [InlineData("?cursor=__________8")]
    [InlineData("?cursor=AAAAAAAAAAE")]
    public async Task SeriesFires_invalid_pagination_returns_explicit_400(string query)
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        using var response = await server.Send(HttpMethod.Get, $"/api/v1/series/{schedule.Series}/fires" + query,
            new ByteArrayContent([]), server.Token("Admin"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(server.Failures);
    }

    [Fact]
    public async Task SeriesFires_Viewer_is_resource_scoped_live_revocable_and_independent_of_the_SPA_client()
    {
        await using var server = await Server.Start(database.DataSource, runs: true);
        var schedule = await CreateSchedule(server);
        var other = await CreateSchedule(server);
        var group = Guid.NewGuid();
        var team = schedule.Workflow.Split('/')[0];
        await Execute("""
            INSERT INTO role_bindings(id,scope_kind,scope_id,role,principal_id,principal_kind)
            VALUES($1,1,$2,0,$3,0)
            """, Guid.CreateVersion7(), team, group);
        var viewer = server.Token("Viewer", "user", "wf.read", groups: [group], authorizedClient: Guid.NewGuid().ToString());
        using (var allowed = await Get(schedule.Series, viewer))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            Assert.Equal("no-store", allowed.Headers.CacheControl!.ToString());
            Assert.Empty((await Read(allowed)).Items);
        }
        using (var foreign = await Get(other.Series, viewer)) Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using (var registrar = await Get(schedule.Series, server.Token())) Assert.Equal(HttpStatusCode.Forbidden, registrar.StatusCode);
        using (var app = await Get(schedule.Series, server.Token("Viewer", groups: [group]))) Assert.Equal(HttpStatusCode.Forbidden, app.StatusCode);
        using (var anonymous = await Get(schedule.Series, null)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var unknown = await Get(Guid.NewGuid(), server.Token("Admin"))) Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        await Execute("DELETE FROM role_bindings WHERE principal_id=$1", group);
        using (var revoked = await Get(schedule.Series, viewer)) Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Empty(server.Failures);
        return;

        Task<HttpResponseMessage> Get(Guid series, string? bearer) => server.Send(HttpMethod.Get,
            $"/api/v1/series/{series}/fires", new ByteArrayContent([]), bearer, useDefaultToken: false);
    }

    private static async Task<SeriesFiresPage> ReadFires(Server server, Guid series, string query = "")
    {
        using var response = await server.Send(HttpMethod.Get, $"/api/v1/series/{series}/fires" + query,
            new ByteArrayContent([]), server.Token("Admin"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        return await Read(response);
    }

    private static async Task<SeriesFiresPage> Read(HttpResponseMessage response) =>
        JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(Token), SeriesFiresJson.Default.SeriesFiresPage)!;
}
