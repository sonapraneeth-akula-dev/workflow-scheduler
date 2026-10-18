using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Events;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Engine.Dispatch;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.Integration.Tests.Execution;

public sealed partial class RunTimelineApiTests
{
    [Fact]
    public Task SystemEvents_actual_dispatch_loss_replacement_and_remote_transitions_preserve_attempt_order_and_safe_projection() =>
        ActualDispatchRetry(browser: false, systemEvents: true);

    [Fact]
    [Trait("Category", "FrontendBrowser")]
    public async Task SystemEvents_actual_live_retry_history_and_pinned_plan_in_three_browsers()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WF_SYSTEM_EVENTS_BROWSER") == "true",
            "Set WF_SYSTEM_EVENTS_BROWSER=true and WF_FRONTEND_ROOT after installing frontend dependencies and browsers.");
        foreach (var project in new[] { "chromium", "firefox", "webkit" })
            await ActualDispatchRetry(browser: false, systemEvents: true, systemBrowser: project);
    }

    [Fact]
    public async Task SystemEvents_actual_Viewer_authorization_stable_paging_filters_and_legacy_history()
    {
        var (team, group) = await Registry();
        var run = await Create(team);
        var store = Store();
        var now = DateTimeOffset.UtcNow;
        await store.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, now, Token);
        await store.AppendAsync(run, EventEntity.Task, "node/task", (int)ExecutionStatus.Ready, now, Token);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, browser: true);
        var jwt = api.Jwt(group);
        var path = EventsPath(run.Snapshot.RunId);
        using (var anonymous = await api.Get(path, null, Token))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var denied = await api.Get(path, api.Jwt(), Token))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var first = await Events(api, path + "?limit=1", jwt);
        Assert.Equal("2", first.LastSeq);
        Assert.Equal("1", Assert.Single(first.Events).Seq);
        Assert.NotNull(first.NextCursor);
        await store.AppendAsync(run, EventEntity.Node, "node", (int)ExecutionStatus.Ready, now, Token);
        var next = await Events(api, path + "?limit=1&cursor=" + first.NextCursor, jwt);
        Assert.Equal("2", next.LastSeq);
        Assert.Equal("2", Assert.Single(next.Events).Seq);
        Assert.Null(next.NextCursor);
        var current = await Events(api, path, jwt);
        Assert.Equal(["1", "2", "3"], current.Events.Select(change => change.Seq));
        var node = await Events(api, path + "?node=node", jwt);
        Assert.Equal(["2", "3"], node.Events.Select(change => change.Seq));
        var task = await Events(api, path + "?task=node%2Ftask", jwt);
        Assert.Equal("2", Assert.Single(task.Events).Seq);
        Assert.Empty((await Events(api, path + "?attempt=2", jwt)).Events);
        await store.AppendAsync(run, EventEntity.Task, "node/task", (int)ExecutionStatus.Dispatched, now, Token);
        var legacy = (await Events(api, path, jwt)).Events[^1];
        Assert.Null(legacy.SelectionReason);
        Assert.Null(legacy.AgentId);
        Assert.Null(legacy.PlannedAgentId);
        foreach (var invalid in new[] { "?limit=0", "?limit=01", "?limit=201", "?limit=1&limit=1",
            "?node=x&task=node%2Ftask", "?attempt=32768", "?unknown=secret",
            "?limit=2&cursor=" + first.NextCursor })
        {
            using var response = await api.Get(path + invalid, jwt, Token);
            await Problem(response, 400, "invalid_system_events_query");
        }
        using (var foreignCursor = await api.Get(EventsPath((await Create(team)).Snapshot.RunId) +
            "?limit=1&cursor=" + first.NextCursor, jwt, Token))
            await Problem(foreignCursor, 400, "invalid_system_events_query");
        using (var strictResync = await api.Get($"/api/v1/runs/{run.Snapshot.RunId:D}/events?afterSeq=0&node=node", jwt, Token))
            await Problem(strictResync, 400, "invalid_run_query");
    }

    [Fact]
    public async Task SystemEvents_corrupt_selection_is_sanitized_and_never_projected_as_freeform_text()
    {
        var (team, group) = await Registry();
        var run = await Create(team);
        await Store().AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
        await Execute("""
            UPDATE run_events SET payload=convert_to(
                jsonb_set(convert_from(payload,'UTF8')::jsonb,'{SelectionReason}','"private-secret"')::text,'UTF8')
            WHERE run_id=$1
            """, run.Snapshot.RunId);
        await using var api = await MetricsApiServer.Start(database.DataSource, Token, browser: true);
        using var response = await api.Get(EventsPath(run.Snapshot.RunId), api.Jwt(group), Token);
        await Problem(response, 500, "run_data_inconsistent");
    }

    private async Task CheckSystemEvents(MetricsApiServer api, Guid run, string jwt, Guid version, Guid firstAgent, Guid secondAgent)
    {
        var events = new List<SystemEvent>();
        string? cursor = null;
        string? watermark = null;
        do
        {
            Assert.True(events.Count < 100, "Event pagination did not terminate.");
            var page = await Events(api, EventsPath(run) + "?limit=2" + (cursor is null ? "" : "&cursor=" + cursor), jwt);
            watermark ??= page.LastSeq;
            Assert.Equal(watermark, page.LastSeq);
            Assert.Equal(run, page.RunId);
            Assert.Equal(version, page.VersionId);
            events.AddRange(page.Events);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(Enumerable.Range(1, checked((int)long.Parse(watermark!, CultureInfo.InvariantCulture)))
            .Select(value => value.ToString(CultureInfo.InvariantCulture)), events.Select(change => change.Seq));
        var dispatches = events.Where(change => change.Entity == 2 && change.State == 2 && change.Progress is null).ToArray();
        Assert.Equal([1, 2], dispatches.Select(change => change.Attempt));
        Assert.Equal([firstAgent, secondAgent], dispatches.Select(change => change.AgentId));
        Assert.All(dispatches, change => Assert.Equal("LeastLoaded", change.SelectionReason));
        Assert.Null(dispatches[0].PlannedAgentId);
        Assert.Equal(firstAgent, dispatches[1].PlannedAgentId);
        Assert.Contains(events, change => change.State == 4 && change.Attempt == 1 && change.FailureCategory == "AgentLost");
        Assert.Equal([1, 2, 3], events.Where(change => change.Attempt == 1 && change.Progress is not null)
            .Select(change => change.Progress!.Phase));
        Assert.Equal([9, 3], events.Where(change => change.Attempt == 2 && change.Progress is not null)
            .Select(change => change.Progress!.Phase).Take(2));
        var filtered = await Events(api, EventsPath(run) + "?node=node&task=node%2Ftask&attempt=2", jwt);
        Assert.All(filtered.Events, change => { Assert.Equal(2, change.Attempt); Assert.Equal("node/task", change.Key); });
        Assert.NotEmpty(filtered.Events);
    }

    private async Task<SystemEventsPage> Events(MetricsApiServer api, string path, string jwt)
    {
        using var response = await api.Get(path, jwt, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        var text = await response.Content.ReadAsStringAsync(Token);
        foreach (var secret in new[] { "private-", "variables", "outputs", "payload", "capabilities", "grants",
            "failureReason", "operationId", "statusUrl", "packageUri", "dispatch_payload", "execution_policy" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        return JsonSerializer.Deserialize(text, SystemEventsJson.Default.SystemEventsPage)!;
    }

    private static Task RemoteProgress(RunStore store, AgentSession session, DispatchCommand command, TimeProvider clock,
        uint sequence, RemoteOperationPhase phase) => store.AcceptAsync(session, new()
        {
            TaskProgress = new()
            {
                Task = command.Task.Clone(), ReservationEpoch = command.ReservationEpoch,
                ProgressSequence = sequence, Phase = phase, At = Timestamp.FromDateTimeOffset(clock.GetUtcNow())
            }
        }, TestContext.Current.CancellationToken);

    private async Task SystemEventBrowser(MetricsApiServer api, Guid run, string jwt, string project, Func<Task> publish)
    {
        var frontend = Environment.GetEnvironmentVariable("WF_FRONTEND_ROOT");
        Assert.True(frontend is not null && File.Exists(System.IO.Path.Combine(frontend, "scripts", "system-events-browser.ts")),
            "WF_FRONTEND_ROOT must identify the repository frontend directory.");
        var root = System.IO.Path.Combine(ArtifactRoot(), "system-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = System.IO.Path.Combine(root, "ready");
        var start = new ProcessStartInfo("bun")
        {
            WorkingDirectory = frontend, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(System.IO.Path.Combine("scripts", "system-events-browser.ts"));
        start.Environment["WF_SYSTEM_EVENTS_API"] = api.Api;
        start.Environment["WF_SYSTEM_EVENTS_TOKEN"] = jwt;
        start.Environment["WF_SYSTEM_EVENTS_RUN"] = run.ToString("D");
        start.Environment["WF_SYSTEM_EVENTS_PROJECT"] = project;
        start.Environment["WF_SYSTEM_EVENTS_READY"] = marker;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            while (!File.Exists(marker))
            {
                if (process.HasExited) Assert.Fail($"System Events {project} exited.\n{await stdout}\n{await stderr}");
                await Task.Delay(25, deadline.Token);
            }
            Assert.Equal(run.ToString("D"), await File.ReadAllTextAsync(marker, deadline.Token));
            await publish();
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, $"System Events {project} failed.\n{await stdout}\n{await stderr}");
            output.WriteLine(await stdout);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string EventsPath(Guid run) => $"/api/v1/runs/{run:D}/system-events";
}
