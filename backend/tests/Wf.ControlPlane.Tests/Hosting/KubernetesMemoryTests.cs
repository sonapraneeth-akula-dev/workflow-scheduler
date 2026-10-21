using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wf.ControlPlane.Api.Admin;
using Wf.ControlPlane.Auth;
using Wf.ControlPlane.Hosting;

namespace Wf.ControlPlane.Tests.Hosting;

public sealed class KubernetesMemoryTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("123", 123L)]
    [InlineData("1Ki", 1024L)]
    [InlineData("1.5Mi", 1572864L)]
    [InlineData("3Gi", 3221225472L)]
    [InlineData("1Ei", 1152921504606846976L)]
    [InlineData("1E", 1000000000000000000L)]
    [InlineData("1k", 1000L)]
    [InlineData("1M", 1000000L)]
    [InlineData("1500m", 2L)]
    [InlineData("1u", 1L)]
    [InlineData("1n", 1L)]
    [InlineData("1e3", 1000L)]
    [InlineData("1E+3", 1000L)]
    [InlineData("1e-100", 1L)]
    [InlineData("0e3", 0L)]
    public void Quantities_use_exact_units_and_round_up_to_whole_bytes(string value, long expected) =>
        Assert.Equal(expected, KubernetesMemory.ReadBytes(value));

    [Theory]
    [InlineData("")]
    [InlineData("-1Mi")]
    [InlineData(" 10Mi")]
    [InlineData("NaN")]
    [InlineData("2mi")]
    [InlineData("1.1.1Mi")]
    [InlineData("1e100")]
    [InlineData("9223372036854775808")]
    public void Invalid_or_overflowing_memory_is_not_a_zero_measurement(string value) =>
        Assert.Throws<FormatException>(() => KubernetesMemory.ReadBytes(value));

    [Fact]
    public void Disabled_monitoring_never_reads_a_service_account()
    {
        using var memory = new KubernetesMemory(new ConfigurationBuilder().Build(), TimeProvider.System);
        Assert.False(memory.Enabled);
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("Kardia")]
    [InlineData("kardia/other")]
    public void Namespace_cannot_change_the_resource_path(string value)
    {
        using var client = new HttpClient();
        Assert.Throws<InvalidOperationException>(() => new KubernetesMemory(client, value, () => "token", TimeProvider.System));
    }

    [Fact]
    public async Task Real_observation_shape_sums_container_RAM_and_keeps_frontend_and_passive_pods()
    {
        using var handler = new Api();
        using var client = new HttpClient(handler) { BaseAddress = new("https://kubernetes.default.svc/") };
        var tokenVersion = 0;
        using var memory = new KubernetesMemory(client, "kardia", () => $"rotated-{++tokenVersion}", TimeProvider.System);
        var result = await memory.ReadAsync(CancellationToken.None);
        var node = Assert.Single(result.Nodes);
        Assert.Equal(3L * 1024 * 1024 * 1024, node.CapacityBytes);
        Assert.Equal(1024L * 1024 * 1024, node.WorkingSetBytes);
        var pod = Assert.Single(result.Pods);
        Assert.Equal("kardia-frontend", pod.Role);
        Assert.Equal(30L * 1024 * 1024, pod.WorkingSetBytes);
        Assert.True(pod.Ready);
        Assert.Equal("pod-uid", pod.Uid);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(new[] { "rotated-1", "rotated-2", "rotated-3", "rotated-4" }, handler.Tokens);
        Assert.Contains("labelSelector=", handler.Requests[1]);
        Assert.DoesNotContain(handler.Requests, path => path.Contains("secrets", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_or_older_reused_pod_samples_stay_null_not_zero()
    {
        using var handler = new Api { NoNodeMetrics = true, OldPodSample = true };
        using var client = new HttpClient(handler) { BaseAddress = new("https://kubernetes.default.svc/") };
        using var memory = new KubernetesMemory(client, "kardia", () => "token", TimeProvider.System);
        var result = await memory.ReadAsync(CancellationToken.None);
        Assert.Null(Assert.Single(result.Nodes).WorkingSetBytes);
        Assert.Null(Assert.Single(result.Pods).WorkingSetBytes);
        Assert.Null(Assert.Single(result.Pods).SampledAt);
    }

    [Fact]
    public async Task Permission_failure_is_an_error_not_an_empty_cluster()
    {
        using var handler = new Api { Status = HttpStatusCode.Forbidden };
        using var client = new HttpClient(handler) { BaseAddress = new("https://kubernetes.default.svc/") };
        using var memory = new KubernetesMemory(client, "kardia", () => "token", TimeProvider.System);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Assert.ThrowsAsync<HttpRequestException>(() => memory.ReadAsync(CancellationToken.None))).StatusCode);
    }

    [Fact]
    public async Task Projected_token_control_characters_are_rejected_before_HTTP()
    {
        using var handler = new Api();
        using var client = new HttpClient(handler) { BaseAddress = new("https://kubernetes.default.svc/") };
        using var memory = new KubernetesMemory(client, "kardia", () => "token\r\nAuthorization: bad", TimeProvider.System);
        await Assert.ThrowsAsync<InvalidDataException>(() => memory.ReadAsync(CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Monitoring_route_requires_global_admin()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(new KubernetesMemory(new ConfigurationBuilder().Build(), TimeProvider.System));
        await using var app = builder.Build();
        app.MapKubernetesMemoryEndpoints();
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints));
        Assert.Equal(AuthExtensions.Admin, Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
    }

    private sealed class Api : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public bool NoNodeMetrics { get; init; }
        public bool OldPodSample { get; init; }
        public List<string> Requests { get; } = [];
        public List<string> Tokens { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add(path);
            Tokens.Add(request.Headers.Authorization!.Parameter!);
            var body = path switch
            {
                "/api/v1/nodes?limit=500" => """
                    {"items":[{"metadata":{"name":"kardia-lab-01"},"status":{"capacity":{"memory":"3Gi"},"allocatable":{"memory":"2800Mi"}}}]}
                    """,
                var value when value.StartsWith("/api/v1/namespaces/kardia/pods?", StringComparison.Ordinal) => """
                    {"items":[
                    {"metadata":{"name":"frontend","namespace":"kardia","uid":"pod-uid","labels":{"app":"kardia-frontend"},"creationTimestamp":"2026-10-21T08:00:00Z"},
                    "spec":{"nodeName":"kardia-lab-01"},"status":{"phase":"Running","conditions":[{"type":"Ready","status":"True"}]}},
                    {"metadata":{"name":"not-ours","namespace":"kardia","labels":{"app":"other"}}},
                    {"metadata":{"name":"different-namespace","namespace":"elsewhere","labels":{"app":"kardia-api"}}}]}
                    """,
                "/apis/metrics.k8s.io/v1beta1/nodes" => NoNodeMetrics ? """{"items":[]}""" : """
                    {"items":[{"metadata":{"name":"kardia-lab-01"},"timestamp":"2026-10-21T08:01:00Z","usage":{"memory":"1Gi"}}]}
                    """,
                "/apis/metrics.k8s.io/v1beta1/namespaces/kardia/pods" => $$$"""
                    {"items":[{"metadata":{"name":"frontend","namespace":"kardia"},"timestamp":"2026-10-21T{{{(OldPodSample ? "07:59:59" : "08:01:00")}}}Z",
                    "containers":[{"usage":{"memory":"10Mi"}},{"usage":{"memory":"20Mi"}}]}]}
                    """,
                _ => throw new InvalidOperationException("Unexpected Kubernetes resource request.")
            };
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body) });
        }
    }
}
