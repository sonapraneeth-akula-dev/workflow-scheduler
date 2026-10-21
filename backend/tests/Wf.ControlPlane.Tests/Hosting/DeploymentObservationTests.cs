using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wf.ControlPlane.Api.Admin;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Hosting;

namespace Wf.ControlPlane.Tests.Hosting;

public sealed class DeploymentObservationTests
{
    [Fact]
    public async Task Real_counts_images_readiness_jobs_and_fixed_namespace_are_observed_without_secrets()
    {
        using var handler = new Api();
        using var observer = Observer(handler);
        var snapshot = await observer.ReadDeploymentsAsync(CancellationToken.None);
        Assert.Equal(4, snapshot.Workloads.Count);
        Assert.All(snapshot.Workloads, row => Assert.Equal("InSync", row.State));
        Assert.Equal(4, snapshot.Pods.Count);
        Assert.Equal("sha256:actual", snapshot.Pods[0].Containers[0].ImageId);
        Assert.Equal("Succeeded", Assert.Single(snapshot.Jobs).State);
        Assert.Equal(6, handler.Requests.Count);
        Assert.All(handler.Requests, path => Assert.Contains("/namespaces/kardia/", path));
        Assert.DoesNotContain(handler.Requests, path => path.Contains("secrets", StringComparison.Ordinal));
        var json = JsonSerializer.Serialize(snapshot, DeploymentJson.Default.DeploymentSnapshot);
        Assert.DoesNotContain("SENSITIVE_ENV", json);
        Assert.DoesNotContain("sensitive termination text", json);
    }

    [Fact]
    public async Task Staged_OnDelete_images_are_not_a_completed_rollout()
    {
        using var handler = new Api { Staged = true };
        using var observer = Observer(handler);
        var result = await observer.ReadDeploymentsAsync(CancellationToken.None);
        foreach (var name in new[] { "kardia-controller", "kardia-worker" })
        {
            var row = Assert.Single(result.Workloads, item => item.Name == name);
            Assert.Equal("AwaitingReplacement", row.State);
            Assert.Equal(0, row.Updated);
            Assert.Equal(1, row.Ready);
            Assert.Equal(0, row.MatchingImages);
            Assert.Equal("image:v2", row.TargetImages["app"]);
        }
    }

    [Theory]
    [InlineData("CrashLoopBackOff")]
    [InlineData("ImagePullBackOff")]
    [InlineData("CreateContainerConfigError")]
    [InlineData("ErrImageNeverPull")]
    public async Task Container_failures_are_explicit_not_just_Running_pod_phase(string reason)
    {
        using var handler = new Api { WaitingReason = reason };
        using var observer = Observer(handler);
        var result = await observer.ReadDeploymentsAsync(CancellationToken.None);
        var pod = result.Pods[0];
        Assert.Equal("Running", pod.Phase);
        Assert.False(pod.Ready);
        Assert.Equal(reason, pod.Containers[0].Reason);
        Assert.Equal("Waiting", pod.Containers[0].State);
        Assert.Equal("Blocked", result.Workloads[0].State);
    }

    [Fact]
    public async Task Controller_has_not_observed_target_generation_is_unknown_even_with_ready_pods()
    {
        using var handler = new Api { OldGeneration = true };
        using var observer = Observer(handler);
        Assert.All((await observer.ReadDeploymentsAsync(CancellationToken.None)).Workloads,
            row => Assert.Equal("Unknown", row.State));
    }

    [Fact]
    public async Task Truncated_pod_list_is_an_explicit_error()
    {
        using var handler = new Api { Truncated = true };
        using var observer = Observer(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => observer.ReadDeploymentsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(503)]
    public async Task Missing_resources_or_permissions_never_become_healthy_empty_state(int status)
    {
        using var handler = new Api { Status = (HttpStatusCode)status };
        using var observer = Observer(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => observer.ReadDeploymentsAsync(CancellationToken.None));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
    }

    [Fact]
    public async Task Deployment_route_is_read_only_and_requires_global_Admin()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(new KubernetesObserver(new ConfigurationBuilder().Build(), TimeProvider.System));
        await using var app = builder.Build();
        app.MapDeploymentEndpoints();
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints));
        Assert.Equal(AuthExtensions.Admin, Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
        Assert.Equal(["GET"], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
    }

    private static KubernetesObserver Observer(Api handler) =>
        new(new HttpClient(handler) { BaseAddress = new("https://kubernetes.default.svc/") }, "kardia", () => "rotating-token", TimeProvider.System);

    private sealed class Api : HttpMessageHandler
    {
        public bool Staged { get; init; }
        public bool OldGeneration { get; init; }
        public bool Truncated { get; init; }
        public string? WaitingReason { get; init; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public List<string> Requests { get; } = [];
        private readonly string[] names = ["kardia-api", "kardia-frontend", "kardia-controller", "kardia-worker"];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            object body;
            if (path.EndsWith("/pods", StringComparison.Ordinal))
                body = new { metadata = new { @continue = Truncated ? "next-page" : "" }, items = names.Select(name => new
                {
                    metadata = new { name = name + "-pod", @namespace = "kardia", uid = name + "-uid",
                        creationTimestamp = "2026-10-21T16:00:00Z", labels = new { app = name } },
                    spec = new { nodeName = "kardia-lab-01", containers = new[] { new { name = "app", image = "image:v1",
                        env = new[] { new { name = "SENSITIVE_ENV", value = "do-not-export" } } } } },
                    status = new { phase = "Running", conditions = new[] { new { type = "Ready", status = WaitingReason is null ? "True" : "False" } },
                        containerStatuses = new[] { new { name = "app", ready = WaitingReason is null, imageID = "sha256:actual",
                            restartCount = 2, state = WaitingReason is null ?
                                JsonSerializer.SerializeToElement(new { running = new { startedAt = "2026-10-21T16:00:00Z" } }) :
                                JsonSerializer.SerializeToElement(new { waiting = new { reason = WaitingReason } }),
                            lastState = new { terminated = new { message = "sensitive termination text" } } } } }
                }).ToArray() };
            else if (path.EndsWith("/jobs", StringComparison.Ordinal))
                body = new { items = new[] { new
                {
                    metadata = new { name = "kardia-migrate-example", @namespace = "kardia", creationTimestamp = "2026-10-21T16:00:00Z" },
                    spec = new { template = new { spec = new { containers = new[] { new { name = "migrate", image = "image:v1" } } } } },
                    status = new { succeeded = 1, completionTime = "2026-10-21T16:00:01Z",
                        conditions = new[] { new { type = "Complete", status = "True" } } }
                } } };
            else
            {
                var name = path.Split('/')[^1];
                var onDelete = name is "kardia-controller" or "kardia-worker";
                body = new
                {
                    kind = name == "kardia-worker" ? "DaemonSet" : name == "kardia-controller" ? "StatefulSet" : "Deployment",
                    metadata = new { name, @namespace = "kardia", generation = 2 },
                    spec = new { replicas = 1, strategy = new { type = "RollingUpdate" }, updateStrategy = new { type = "OnDelete" },
                        template = new { spec = new { containers = new[] { new { name = "app", image = Staged && onDelete ? "image:v2" : "image:v1" } } } } },
                    status = new { observedGeneration = OldGeneration ? 1 : 2, desiredNumberScheduled = 1,
                        updatedReplicas = Staged && onDelete ? 0 : 1, updatedNumberScheduled = Staged && onDelete ? 0 : 1,
                        readyReplicas = 1, availableReplicas = 1, numberReady = 1, numberAvailable = 1 }
                };
            }
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(JsonSerializer.Serialize(body)) });
        }
    }
}
