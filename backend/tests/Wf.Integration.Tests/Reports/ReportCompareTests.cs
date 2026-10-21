using System.Collections.Immutable;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wf.Contracts;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Events;
using Wf.Contracts.Registry;
using Wf.Contracts.Reports;
using Wf.ControlPlane.Api;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Engine.Dispatch;
using Wf.Engine.Core;
using Wf.Engine.Core.Placement;
using Wf.Engine.Core.Results;
using DispatchCommand = Wf.Contracts.Agent.V1.Dispatch;

namespace Wf.Integration.Tests.Reports;

public sealed class ReportCompareTests(ReportPublishDatabase database) : IClassFixture<ReportPublishDatabase>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("private compare payload");
    private static readonly string[] ItemFields =
        ["attempt", "createdAt", "id", "kind", "runId", "signOff", "sizeBytes", "sourceKey", "tags", "title"];

    [Fact]
    public async Task ReportCompare_actual_PG17_signed_HTTP_compares_any_two_runs_with_exact_metadata_and_required_OpenAPI()
    {
        await using var fixture = await Start();
        var pair = await Pair(fixture);
        var title = " Résumé e\u0301\r\n<script>' OR true-- ";
        var leftId = await Publish(fixture, pair.Left, title, "html", ["Review", "Review"]);
        var rightId = await Publish(fixture, pair.Right, title, "custom-kind", ["review"]);
        var third = await CreateRun(fixture, pair.Workflow, "$post", "report");
        await fixture.Execute("UPDATE runs SET series_id=$2 WHERE id=$1 AND run_day=uuid_v7_run_day($1)", third.RunId, pair.Series);
        var thirdId = await Publish(fixture, third, title);
        var before = await Facts();
        using var response = await fixture.Get(Path(pair, title: title));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Headers(response);
        var body = await response.Content.ReadAsStringAsync(Token);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(["left", "right", "seriesId"], json.RootElement.EnumerateObject().Select(value => value.Name).Order());
        Assert.Equal(pair.Series, json.RootElement.GetProperty("seriesId").GetGuid());
        foreach (var (side, run, id, kind, tags) in new[]
        {
            ("left", pair.Left.RunId, leftId, "html", new[] { "Review", "Review" }),
            ("right", pair.Right.RunId, rightId, "custom-kind", new[] { "review" })
        })
        {
            var item = json.RootElement.GetProperty(side);
            Assert.Equal(ItemFields, item.EnumerateObject().Select(value => value.Name).Order());
            Assert.Equal(run, item.GetProperty("runId").GetGuid());
            Assert.Equal(id, item.GetProperty("id").GetGuid());
            Assert.Equal("$post/report", item.GetProperty("sourceKey").GetString());
            Assert.Equal(1, item.GetProperty("attempt").GetInt32());
            Assert.Equal(title, item.GetProperty("title").GetString());
            Assert.Equal(kind, item.GetProperty("kind").GetString());
            Assert.Equal(tags, item.GetProperty("tags").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(Payload.Length, item.GetProperty("sizeBytes").GetInt64());
            Assert.Equal(TimeSpan.Zero, item.GetProperty("createdAt").GetDateTimeOffset().Offset);
            var signOff = item.GetProperty("signOff");
            Assert.Equal("0", signOff.GetProperty("revision").GetString());
            Assert.Equal("NotSignedOff", signOff.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, signOff.GetProperty("latest").ValueKind);
        }
        Private(body);
        using var alternate = await fixture.Get(Path(pair with { Left = third, Right = pair.Left }, title: title));
        var reversed = await Success(alternate);
        Assert.Equal(thirdId, reversed.Left.Id);
        Assert.Equal(leftId, reversed.Right.Id);
        Assert.Equal(before, await Facts());
        using var schemaResponse = await fixture.Get("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, schemaResponse.StatusCode);
        using var schema = JsonDocument.Parse(await schemaResponse.Content.ReadAsStringAsync(Token));
        var schemas = schema.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var (name, fields) in new[]
        {
            ("ReportCompareResponse", new[] { "left", "right", "seriesId" }), ("ReportIndexItem", ItemFields)
        })
        {
            var model = schemas.GetProperty(name);
            Assert.Equal(fields, model.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order());
            foreach (var property in model.GetProperty("properties").EnumerateObject())
                Assert.DoesNotContain("\"null\"", property.Value.GetRawText(), StringComparison.Ordinal);
        }
        var operation = schema.RootElement.GetProperty("paths").GetProperty("/api/v1/series/{id}/reports/compare").GetProperty("get");
        var query = operation.GetProperty("parameters").EnumerateArray().Where(value => value.GetProperty("in").GetString() == "query").ToArray();
        Assert.Equal(["leftRunId", "rightRunId", "sourceKey", "title"], query.Select(value => value.GetProperty("name").GetString()).Order());
        Assert.All(query, value => Assert.True(value.GetProperty("required").GetBoolean()));
        foreach (var code in new[] { "200", "400", "401", "403", "404", "409", "503" })
            Assert.True(operation.GetProperty("responses").TryGetProperty(code, out _));
    }

    [Fact]
    public async Task ReportCompare_actual_HTTP_missing_either_or_both_is_reasoned_404_without_partial_metadata_or_normalization()
    {
        await using var fixture = await Start();
        var pair = await Pair(fixture);
        var before = await Facts();
        using (var empty = await fixture.Get(Path(pair)))
            await Failure(empty, HttpStatusCode.NotFound, "report_match_not_found");
        Assert.Equal(before, await Facts());
        var title = " Résumé e\u0301 ";
        await Publish(fixture, pair.Left, title);
        foreach (var current in new[] { pair, pair with { Left = pair.Right, Right = pair.Left } })
        {
            using var missing = await fixture.Get(Path(current, title: title));
            await Failure(missing, HttpStatusCode.NotFound, "report_match_not_found");
        }
        await Publish(fixture, pair.Right, title);
        foreach (var (source, value) in new[]
        {
            ("$post/Report", title), ("other/report", title), ("$post/report", title.Trim()),
            ("$post/report", title.ToUpperInvariant()), ("$post/report", title.Normalize(NormalizationForm.FormC)),
            ("$post/report", "' OR true--")
        })
        {
            using var missing = await fixture.Get(Path(pair, source, value));
            await Failure(missing, HttpStatusCode.NotFound, "report_match_not_found");
        }
        using var exact = await fixture.Get(Path(pair, title: title));
        await Success(exact);
    }

    [Fact]
    public async Task ReportCompare_actual_HTTP_ambiguous_duplicates_across_kinds_and_attempts_are_409_not_newest_or_cross_attempt_pairs()
    {
        await using var fixture = await Start();
        var pair = await Pair(fixture);
        await Publish(fixture, pair.Left);
        await Publish(fixture, pair.Right);
        var duplicate = await Publish(fixture, pair.Left, kind: "html");
        foreach (var current in new[] { pair, pair with { Left = pair.Right, Right = pair.Left } })
        {
            using var ambiguous = await fixture.Get(Path(current));
            await Failure(ambiguous, HttpStatusCode.Conflict, "report_match_ambiguous");
        }
        await fixture.Execute("DELETE FROM reports WHERE run_day=uuid_v7_run_day($1) AND run_id=$1 AND id=$2", pair.Left.RunId, duplicate);
        // Seed a persisted second attempt; all report rows still come through production HTTP admission.
        await fixture.Execute("""
            INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at)
            VALUES(uuid_v7_run_day($1),$1,$2,2,2,now()+interval '1 hour')
            """, pair.Right.RunId, pair.Right.TaskKey);
        await Publish(fixture, pair.Right with { Attempt = 2 });
        var before = await Facts();
        foreach (var current in new[] { pair, pair with { Left = pair.Right, Right = pair.Left } })
        {
            using var attempts = await fixture.Get(Path(current));
            await Failure(attempts, HttpStatusCode.Conflict, "report_match_ambiguous");
        }
        Assert.Equal(before, await Facts());
        await Publish(fixture, pair.Left, kind: "html");
        using (var both = await fixture.Get(Path(pair)))
            await Failure(both, HttpStatusCode.Conflict, "report_match_ambiguous");
        await fixture.Execute("DELETE FROM reports WHERE run_day=uuid_v7_run_day($1) AND run_id=$1", pair.Right.RunId);
        using var absent = await fixture.Get(Path(pair));
        await Failure(absent, HttpStatusCode.NotFound, "report_match_not_found");
    }

    [Fact]
    public async Task ReportCompare_actual_HTTP_preserves_historical_stages_and_full_publisher_UTF8_boundaries()
    {
        await using var fixture = await Start();
        var pair = await Pair(fixture);
        foreach (var (node, task, title) in new[]
        {
            ("build.stage.Node", "Task", "Stage title"),
            ("étage.Node", "Rapport", "Rapport de l’étage"),
            (new string('n', 512), new string('t', 512), new string('é', 256))
        })
        {
            var left = await CreateRun(fixture, pair.Workflow, node, task);
            var right = await CreateRun(fixture, pair.Workflow, node, task);
            foreach (var attempt in new[] { left, right })
                await fixture.Execute("UPDATE runs SET series_id=$2 WHERE run_day=uuid_v7_run_day($1) AND id=$1", attempt.RunId, pair.Series);
            var leftId = await Publish(fixture, left, title);
            var rightId = await Publish(fixture, right, title);
            using var response = await fixture.Get(Path(pair with { Left = left, Right = right }, left.TaskKey, title));
            var result = await Success(response);
            Assert.Equal(leftId, result.Left.Id);
            Assert.Equal(rightId, result.Right.Id);
            Assert.Equal(node + "/" + task, result.Left.SourceKey);
            Assert.Equal(title, result.Right.Title);
        }
    }

    [Theory]
    [InlineData(RegistryRole.Viewer, false)]
    [InlineData(RegistryRole.Operator, true)]
    [InlineData(RegistryRole.Author, false)]
    [InlineData(RegistryRole.Admin, true)]
    public async Task ReportCompare_actual_HTTP_fresh_type_or_team_user_or_group_roles_authorize_all_three_resources(
        RegistryRole role, bool teamScope)
    {
        var probe = new ScopeProbe();
        await using var fixture = await Start(probe: probe);
        var pair = await Pair(fixture);
        await Publish(fixture, pair.Left);
        await Publish(fixture, pair.Right);
        var group = Guid.NewGuid();
        await fixture.Execute("DELETE FROM role_bindings WHERE principal_id=$1", fixture.User);
        await fixture.Execute("""
            INSERT INTO role_bindings(id,scope_kind,scope_id,role,principal_id,principal_kind)
            VALUES($1,$2,$3,$4,$5,$6)
            """, Guid.CreateVersion7(), (short)(teamScope ? 1 : 2),
            teamScope ? pair.Workflow.Split('/')[0] : string.Join('/', pair.Workflow.Split('/').Take(2)),
            (short)role, teamScope ? group : fixture.User, (short)(teamScope ? 0 : 1));
        var credential = fixture.Credential(extra: teamScope ? [new Claim("groups", group.ToString("D"))] : null);
        using (var response = await fixture.Get(Path(pair), credential))
            await Success(response);
        Assert.Equal(new[]
        {
            (TeamResourceKind.Series, pair.Series.ToString("D")), (TeamResourceKind.Run, pair.Left.RunId.ToString("D")),
            (TeamResourceKind.Run, pair.Right.RunId.ToString("D"))
        }, probe.Resources);
        probe.Denied = pair.Right.RunId.ToString("D");
        using (var rightDenied = await fixture.Get(Path(pair), credential))
            await Failure(rightDenied, HttpStatusCode.Forbidden, "insufficient_scope_role");
        probe.Denied = null;
        await fixture.Execute("DELETE FROM role_bindings WHERE principal_id=$1", teamScope ? group : fixture.User);
        using var revoked = await fixture.Get(Path(pair), credential);
        await Failure(revoked, HttpStatusCode.Forbidden, "insufficient_scope_role");
    }

    [Fact]
    public async Task ReportCompare_actual_signed_HTTP_authentication_roles_foreign_missing_and_ambiguous_series_fail_closed()
    {
        await using var fixture = await Start();
        var pair = await Pair(fixture);
        await Publish(fixture, pair.Left);
        await Publish(fixture, pair.Right);
        foreach (var credential in new[] { fixture.Credential(unsigned: true), fixture.Credential(expired: true), "not-a-jwt" })
        {
            using var denied = await fixture.Get(Path(pair), credential);
            await Failure(denied, HttpStatusCode.Unauthorized);
        }
        using (var anonymous = await fixture.Get(Path(pair), anonymous: true))
            await Failure(anonymous, HttpStatusCode.Unauthorized);
        foreach (var credential in new[]
        {
            fixture.Credential(user: Guid.NewGuid()), fixture.Credential(identity: "app"),
            fixture.Credential(role: "Agent"), fixture.Credential(role: "Registrar"),
            fixture.Credential(extra: [new Claim("hasgroups", "true")]),
            fixture.Credential(extra: [new Claim("groups", "not-a-guid")])
        })
        {
            using var denied = await fixture.Get(Path(pair), credential);
            await Failure(denied, HttpStatusCode.Forbidden, "insufficient_scope_role");
        }
        var foreign = await fixture.Seed();
        await Publish(fixture, foreign);
        foreach (var current in new[]
        {
            pair with { Series = Guid.NewGuid() }, pair with { Left = foreign }, pair with { Right = foreign },
            pair with { Left = new(Guid.CreateVersion7(), "$post/report", 1) },
            pair with { Right = new(Guid.CreateVersion7(), "$post/report", 1) }
        })
        {
            using var denied = await fixture.Get(Path(current), fixture.Credential(role: "Admin"));
            await Failure(denied, HttpStatusCode.Forbidden, "insufficient_scope_role");
        }
        await fixture.Execute("UPDATE runs SET series_id=$2 WHERE id=$1 AND run_day=uuid_v7_run_day($1)", foreign.RunId, pair.Series);
        using var ambiguous = await fixture.Get(Path(pair), fixture.Credential(role: "Admin"));
        await Failure(ambiguous, HttpStatusCode.Forbidden, "insufficient_scope_role");
    }

    [Fact]
    public async Task ReportCompare_actual_HTTP_reads_inactive_versions_and_retained_series_after_workflow_deletion()
    {
        await using var fixture = await Start();
        var pair = await Pair(fixture);
        var leftId = await Publish(fixture, pair.Left);
        var rightId = await Publish(fixture, pair.Right);
        var versions = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        for (var index = 0; index < versions.Length; index++)
            await fixture.Execute("""
                INSERT INTO workflow_versions(id,workflow_id,semver,manifest,package_uri,package_sha256,package_size,
                    source_uri,source_sha256,source_size,git_commit,git_author,registered_by,sdk_version)
                VALUES($1,$2,$3,'{}','packages/'||repeat('00',32)||'.zip',decode(repeat('00',32),'hex'),1,
                    'sources/'||repeat('00',32),decode(repeat('00',32),'hex'),1,repeat('a',40),'compare','compare','1.0.0')
                """, versions[index], pair.Workflow, $"1.0.{index}");
        await fixture.Execute("UPDATE workflows SET active_version_id=$2,latest_version_id=$2 WHERE id=$1", pair.Workflow, versions[2]);
        await fixture.Execute("UPDATE runs SET version_id=$2 WHERE id=$1 AND run_day=uuid_v7_run_day($1)", pair.Left.RunId, versions[0]);
        await fixture.Execute("UPDATE runs SET version_id=$2 WHERE id=$1 AND run_day=uuid_v7_run_day($1)", pair.Right.RunId, versions[1]);
        foreach (var attempt in new[] { pair.Left, pair.Right })
            await fixture.Execute("UPDATE runs SET status=$2,ended_at=now() WHERE id=$1 AND run_day=uuid_v7_run_day($1)",
                attempt.RunId, (short)RunStatus.Succeeded);
        using (var inactive = await fixture.Get(Path(pair)))
        {
            var result = await Success(inactive);
            Assert.Equal(leftId, result.Left.Id);
            Assert.Equal(rightId, result.Right.Id);
        }
        await fixture.Execute("UPDATE schedules SET deleted_at=now(),paused=true,next_fire_at=NULL WHERE id=$1", pair.Schedule);
        await fixture.Execute("UPDATE workflows SET active_version_id=NULL,latest_version_id=NULL WHERE id=$1", pair.Workflow);
        await fixture.Execute("DELETE FROM workflow_versions WHERE workflow_id=$1", pair.Workflow);
        await fixture.Execute("DELETE FROM workflows WHERE id=$1", pair.Workflow);
        using (var deleted = await fixture.Get(Path(pair))) await Success(deleted);
        await fixture.Execute("DELETE FROM run_series WHERE id=$1", pair.Series);
        using var retained = await fixture.Get(Path(pair));
        Assert.Equal(pair.Series, (await Success(retained)).SeriesId);
    }

    [Fact]
    public async Task ReportCompare_actual_HTTP_invalid_queries_and_noncanonical_paths_have_explicit_private_400s()
    {
        await using var fixture = await Start();
        var pair = await Pair(fixture);
        var path = Path(pair);
        foreach (var invalid in new[]
        {
            path.Replace("&title=Summary", "", StringComparison.Ordinal), path + "&title=Summary",
            path + "&Title=Summary", path + "&attempt=2", path.Replace("title=Summary", "title=", StringComparison.Ordinal),
            path.Replace("title=Summary", "title=%20", StringComparison.Ordinal),
            path.Replace("title=Summary", "title=%00", StringComparison.Ordinal),
            path.Replace("title=Summary", "title=" + Uri.EscapeDataString(new string('é', 257)), StringComparison.Ordinal),
            Path(pair, new string('a', 1026)), Path(pair with { Right = pair.Left }),
            path.Replace(pair.Left.RunId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal),
            path.Replace(pair.Right.RunId.ToString("D"), pair.Right.RunId.ToString("D").ToUpperInvariant(), StringComparison.Ordinal),
            path.Replace(pair.Series.ToString("D"), pair.Series.ToString("D").ToUpperInvariant(), StringComparison.Ordinal),
            path.Replace(pair.Series.ToString("D"), Guid.Empty.ToString("D"), StringComparison.Ordinal),
            path.Replace(pair.Series.ToString("D"), "not-a-series", StringComparison.Ordinal),
            path.Replace("/series/", "/%73eries/", StringComparison.Ordinal),
            path.Replace(pair.Series.ToString("D"), "%61" + pair.Series.ToString("D")[1..], StringComparison.Ordinal)
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(fixture.Host.Address.AbsoluteUri.TrimEnd('/') + invalid,
                new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }));
            request.Headers.Authorization = new("Bearer", fixture.Credential());
            using var response = await fixture.Host.Client.SendAsync(request, Token);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{invalid}: {response.StatusCode}");
            await Failure(response, HttpStatusCode.BadRequest, "invalid_report_compare_query");
        }
    }

    [Fact]
    public async Task ReportCompare_actual_HTTP_database_failure_is_private_reasoned_503()
    {
        await using var unavailable = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=compare_fixture;Database=unavailable;Timeout=1");
        await using var fixture = await Start(unavailable);
        var pair = new Runs(Guid.NewGuid(), Guid.NewGuid(), "", new(Guid.CreateVersion7(), "$post/report", 1), new(Guid.CreateVersion7(), "$post/report", 1));
        using var response = await fixture.Get(Path(pair));
        await Failure(response, HttpStatusCode.ServiceUnavailable, "report_compare_unavailable");
    }

    private Task<ReportReadFixture> Start(NpgsqlDataSource? source = null, ScopeProbe? probe = null) =>
        ReportReadFixture.Start(source ?? database.DataSource, services: builder =>
        {
            builder.Services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, ReportCompareJsonContext.Default));
            builder.Services.AddWorkflowOpenApi();
            if (probe is not null) builder.Services.AddSingleton<IAuthorizationHandler>(probe);
        }, configure: app => app.MapReportCompareEndpoints());

    private async Task<Runs> Pair(ReportReadFixture fixture)
    {
        var left = await fixture.Seed(at: DateTimeOffset.UtcNow.AddMonths(-1));
        await using var owner = fixture.Source.CreateCommand("SELECT workflow_id FROM runs WHERE run_day=uuid_v7_run_day($1) AND id=$1");
        owner.Parameters.AddWithValue(left.RunId);
        var workflow = (string)(await owner.ExecuteScalarAsync(Token))!;
        var right = await CreateRun(fixture, workflow, "$post", "report");
        var series = Guid.NewGuid();
        var schedule = Guid.CreateVersion7();
        // Only ownership/schedule context is seeded; runs use RunStore/Dispatcher and reports use actual HTTP publishing.
        await fixture.Execute("""
            INSERT INTO schedules(id,workflow_id,origin,name,timezone,interval_s,created_by,updated_by)
            VALUES($1,$2,1,'Compare','Etc/UTC',60,'compare','compare')
            """, schedule, workflow);
        await fixture.Execute("INSERT INTO run_series(id,workflow_id,schedule_id) VALUES($1,$2,$3)", series, workflow, schedule);
        foreach (var attempt in new[] { left, right })
            await fixture.Execute("UPDATE runs SET series_id=$2 WHERE run_day=uuid_v7_run_day($1) AND id=$1", attempt.RunId, series);
        return new(series, schedule, workflow, left, right);
    }

    private static async Task<ResultIdentity> CreateRun(ReportReadFixture fixture, string workflow, string node, string task)
    {
        var host = fixture.Host;
        var run = new RunState(Guid.CreateVersion7(), [new(node, [], [new(task)])],
            new("reports", ImmutableSortedDictionary<string, string>.Empty), Assert.Fail);
        await host.Runs.CreateAsync(run, workflow, Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(5), Token);
        await host.Runs.AppendAsync(run, EventEntity.Run, null, (int)RunStatus.Running, DateTimeOffset.UtcNow, Token);
        foreach (var status in new[] { ExecutionStatus.Ready, ExecutionStatus.Dispatched, ExecutionStatus.Running })
            await host.Runs.AppendAsync(run, EventEntity.Node, node, (int)status, DateTimeOffset.UtcNow, Token);
        var attempt = new ResultIdentity(run.Snapshot.RunId, node + "/" + task, 1);
        await host.Runs.AppendAsync(run, EventEntity.Task, attempt.TaskKey, (int)ExecutionStatus.Ready, DateTimeOffset.UtcNow, Token);
        var agent = Guid.NewGuid();
        await host.Registry.RegisterAsync(new Hello
        {
            Id = agent.ToString("D"), Name = "compare-" + agent.ToString("N"), Os = "windows", Arch = "x64",
            Cores = 2, RamMb = 8192, MaxTasks = 2, Pool = host.Pool, Version = "1", ProtocolVersion = 1
        }, Token);
        using var dispatcher = new Dispatcher(fixture.Source, host.Runs, host.Registry, new PlacementEngine(), TimeProvider.System);
        var hash = new string('a', 64);
        var placed = await dispatcher.DispatchAsync(run,
            new(attempt.RunId, node, task, 1, hash, new(new(pool: host.Pool))), new DispatchCommand
            {
                Task = new() { RunId = attempt.RunId.ToString("D"), NodeKey = node, TaskKey = task, Attempt = 1 },
                Version = "1.0.0", Sha256 = hash, PackageUri = "https://packages.invalid/package.zip",
                DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(2))
            }, Token);
        Assert.NotNull(placed.Agent);
        return attempt;
    }

    private static async Task<Guid> Publish(ReportReadFixture fixture, ResultIdentity attempt, string title = "Summary",
        string kind = "text", string[]? tags = null)
    {
        var id = Guid.CreateVersion7();
        using var request = fixture.Host.Request(attempt, id, Payload, new(kind, title, tags ?? [],
            Convert.ToHexStringLower(SHA256.HashData(Payload)), Payload.Length));
        using var response = await fixture.Host.Client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return id;
    }

    private static string Path(Runs pair, string source = "$post/report", string title = "Summary") =>
        $"/api/v1/series/{pair.Series:D}/reports/compare?leftRunId={pair.Left.RunId:D}&rightRunId={pair.Right.RunId:D}" +
        "&sourceKey=" + Uri.EscapeDataString(source) + "&title=" + Uri.EscapeDataString(title);

    private static async Task<ReportCompareResponse> Success(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Headers(response);
        return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(Token), ReportCompareJsonContext.Default.ReportCompareResponse)!;
    }

    private static async Task Failure(HttpResponseMessage response, HttpStatusCode status, string? reason = null)
    {
        Assert.Equal(status, response.StatusCode);
        Headers(response);
        var body = await response.Content.ReadAsStringAsync(Token);
        Private(body);
        foreach (var forbidden in new[] { "\"left\"", "\"right\"", "\"id\"", "\"sourceKey\"", "\"title\":\"Summary\"", "compare_fixture", "127.0.0.1", "SELECT " })
            Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
        if (reason is not null)
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(reason, json.RootElement.GetProperty("reason").GetString());
        }
    }

    private static void Headers(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.False(response.Content.Headers.Contains("Content-Disposition"));
        Assert.DoesNotContain(response.Headers, header => header.Key.StartsWith("X-Wf-", StringComparison.OrdinalIgnoreCase));
        Assert.False(response.Headers.Contains("Location"));
    }

    private static void Private(string body)
    {
        foreach (var forbidden in new[] { Encoding.UTF8.GetString(Payload), "blob_uri", "blobUri", "sha256", "variables", "bearerToken", "capability", "uploadUri" })
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> Facts()
    {
        await using var query = database.DataSource.CreateCommand("""
            SELECT json_build_array((SELECT count(*) FROM reports),(SELECT count(*) FROM runs),
                (SELECT count(*) FROM run_events),(SELECT count(*) FROM commands),(SELECT count(*) FROM audit_log))::text
            """);
        return (string)(await query.ExecuteScalarAsync(Token))!;
    }

    private sealed record Runs(Guid Series, Guid Schedule, string Workflow, ResultIdentity Left, ResultIdentity Right);

    private sealed class ScopeProbe : AuthorizationHandler<TeamRoleRequirement>
    {
        public List<(TeamResourceKind Kind, string Id)> Resources { get; } = [];
        public string? Denied { get; set; }
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TeamRoleRequirement requirement)
        {
            if (context.Resource is TeamResource resource)
            {
                Resources.Add((resource.Kind, resource.Id));
                if (resource.Id == Denied) context.Fail();
            }
            return Task.CompletedTask;
        }
    }
}
