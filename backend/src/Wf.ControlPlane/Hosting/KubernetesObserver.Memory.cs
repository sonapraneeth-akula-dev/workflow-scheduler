using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wf.ControlPlane.Hosting;

public sealed record KubernetesNodeMemory(string Name, long CapacityBytes, long AllocatableBytes,
    long? WorkingSetBytes, DateTimeOffset? SampledAt);
public sealed record KubernetesPodMemory(string Name, string Namespace, string Uid, string NodeName,
    string Role, string Phase, bool Ready, long? WorkingSetBytes, DateTimeOffset? SampledAt);
public sealed record KubernetesMemorySnapshot(DateTimeOffset ObservedAt, int StaleAfterSeconds,
    IReadOnlyList<KubernetesNodeMemory> Nodes, IReadOnlyList<KubernetesPodMemory> Pods);

public sealed partial class KubernetesObserver : IDisposable
{
    private const string ServiceAccount = "/var/run/secrets/kubernetes.io/serviceaccount";
    private const string Selector = "app in (kardia-api,kardia-controller,kardia-frontend,kardia-worker)";
    private readonly HttpClient? client;
    private readonly string? namespaceName;
    private readonly Func<string>? readToken;
    private readonly TimeProvider time;
    private readonly X509Certificate2Collection roots = [];
    public bool Enabled => client is not null;

    public KubernetesObserver(IConfiguration configuration, TimeProvider time)
    {
        this.time = time;
        if (!configuration.GetValue("Kubernetes:Enabled", false)) return;
        namespaceName = ValidateNamespace(configuration["Kubernetes:Namespace"] ??
            throw new InvalidOperationException("Kubernetes:Namespace is required for RAM monitoring."));
        roots.ImportFromPem(File.ReadAllText($"{ServiceAccount}/ca.crt"));
        if (roots.Count == 0) throw new InvalidOperationException("Kubernetes API trust requires its projected CA.");
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck
        };
        policy.CustomTrustStore.AddRange(roots);
        client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            SslOptions = { CertificateChainPolicy = policy }
        }) { BaseAddress = new Uri("https://kubernetes.default.svc/"), Timeout = TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        readToken = () => File.ReadAllText($"{ServiceAccount}/token");
    }

    public KubernetesObserver(HttpClient client, string namespaceName, Func<string> readToken, TimeProvider time)
    {
        this.client = client;
        this.namespaceName = ValidateNamespace(namespaceName);
        this.readToken = readToken;
        this.time = time;
    }

    public async Task<KubernetesMemorySnapshot> ReadAsync(CancellationToken token)
    {
        if (!Enabled) throw new InvalidOperationException("Kubernetes RAM monitoring is not configured.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        using var nodes = await ReadJson("api/v1/nodes?limit=500", deadline.Token);
        using var pods = await ReadJson($"api/v1/namespaces/{namespaceName}/pods?limit=500&labelSelector={Uri.EscapeDataString(Selector)}", deadline.Token);
        using var nodeMetrics = await ReadJson("apis/metrics.k8s.io/v1beta1/nodes", deadline.Token);
        using var podMetrics = await ReadJson($"apis/metrics.k8s.io/v1beta1/namespaces/{namespaceName}/pods", deadline.Token);
        var nodeUsage = Items(nodeMetrics).ToDictionary(Name, StringComparer.Ordinal);
        var podUsage = Items(podMetrics).ToDictionary(Name, StringComparer.Ordinal);
        var nodeRows = Items(nodes).Select(node =>
        {
            var name = Name(node);
            var memory = nodeUsage.TryGetValue(name, out var metrics)
                ? Memory(metrics, container: false) : (null, null);
            return new KubernetesNodeMemory(name, ReadBytes(node.GetProperty("status").GetProperty("capacity").GetProperty("memory").GetString()!),
                ReadBytes(node.GetProperty("status").GetProperty("allocatable").GetProperty("memory").GetString()!),
                memory.Item1, memory.Item2);
        }).OrderBy(node => node.Name, StringComparer.Ordinal).ToArray();
        var podRows = Items(pods).Where(pod =>
            pod.GetProperty("metadata").GetProperty("namespace").GetString() == namespaceName &&
            pod.GetProperty("metadata").TryGetProperty("labels", out var labels) &&
            labels.TryGetProperty("app", out var app) &&
            app.GetString() is "kardia-api" or "kardia-controller" or "kardia-frontend" or "kardia-worker")
            .Select(pod =>
            {
                var metadata = pod.GetProperty("metadata");
                var name = Name(pod);
                var createdAt = metadata.GetProperty("creationTimestamp").GetDateTimeOffset();
                var memory = podUsage.TryGetValue(name, out var metrics) &&
                    metrics.GetProperty("metadata").GetProperty("namespace").GetString() == namespaceName &&
                    metrics.GetProperty("timestamp").GetDateTimeOffset() >= createdAt
                    ? Memory(metrics, container: true) : (null, null);
                var status = pod.GetProperty("status");
                var ready = status.TryGetProperty("conditions", out var conditions) &&
                    conditions.EnumerateArray().Any(condition => condition.GetProperty("type").GetString() == "Ready" &&
                        condition.GetProperty("status").GetString() == "True");
                return new KubernetesPodMemory(name, namespaceName!, metadata.GetProperty("uid").GetString()!,
                    pod.GetProperty("spec").TryGetProperty("nodeName", out var node) ? node.GetString()! : "Unscheduled",
                    metadata.GetProperty("labels").GetProperty("app").GetString()!,
                    status.GetProperty("phase").GetString()!, ready, memory.Item1, memory.Item2);
            }).OrderBy(pod => pod.Name, StringComparer.Ordinal).ToArray();
        return new(time.GetUtcNow(), 60, nodeRows, podRows);
    }

    private async Task<JsonDocument> ReadJson(string path, CancellationToken token)
    {
        // Read the projected token on every request so Kubernetes rotation is honored.
        var bearer = readToken!().Trim();
        if (bearer.Length is < 1 or > 16384 || bearer.Any(character => character <= ' ' || character >= '\x7f'))
            throw new InvalidDataException("Kubernetes projected bearer token is invalid.");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await client!.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
    }

    private static JsonElement.ArrayEnumerator Items(JsonDocument document)
    {
        if (document.RootElement.TryGetProperty("metadata", out var metadata) &&
            metadata.TryGetProperty("continue", out var cursor) && !string.IsNullOrEmpty(cursor.GetString()))
            throw new InvalidDataException("Kubernetes RAM inventory exceeds the bounded observation page.");
        return document.RootElement.GetProperty("items").EnumerateArray();
    }

    private static string Name(JsonElement item) => item.GetProperty("metadata").GetProperty("name").GetString() ??
        throw new InvalidDataException("Kubernetes resource name is missing.");

    private static (long?, DateTimeOffset?) Memory(JsonElement item, bool container)
    {
        if (container && item.GetProperty("containers").GetArrayLength() == 0)
            throw new InvalidDataException("Kubernetes pod metrics contain no container measurements.");
        var bytes = container
            ? item.GetProperty("containers").EnumerateArray().Aggregate(0L,
                (total, row) => checked(total + ReadBytes(row.GetProperty("usage").GetProperty("memory").GetString()!)))
            : ReadBytes(item.GetProperty("usage").GetProperty("memory").GetString()!);
        return (bytes, item.GetProperty("timestamp").GetDateTimeOffset());
    }

    public static long ReadBytes(string text)
    {
        if (text is null || text.Length > 64) throw new FormatException("Invalid Kubernetes memory quantity.");
        var match = Quantity().Match(text);
        if (!match.Success) throw new FormatException("Invalid Kubernetes memory quantity.");
        var suffix = match.Groups["suffix"].Value;
        var number = match.Groups["number"].Value;
        var factor = suffix switch
        {
            "" => 1m, "n" => 0.000000001m, "u" => 0.000001m, "m" => 0.001m,
            "k" or "K" => 1000m, "M" => 1000000m, "G" => 1000000000m,
            "T" => 1000000000000m, "P" => 1000000000000000m, "E" => 1000000000000000000m,
            "Ki" => 1024m, "Mi" => 1048576m, "Gi" => 1073741824m,
            "Ti" => 1099511627776m, "Pi" => 1125899906842624m, "Ei" => 1152921504606846976m,
            _ => 1m
        };
        if (suffix is not ("E" or "Ei") && (suffix.StartsWith('e') || suffix.StartsWith('E'))) number += suffix;
        try
        {
            var value = decimal.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture) * factor;
            if (value == 0m && match.Groups["number"].Value.Any(character => character is >= '1' and <= '9'))
                return 1;
            return checked((long)decimal.Ceiling(value));
        }
        catch (Exception error) when (error is OverflowException or FormatException)
        { throw new FormatException("Kubernetes memory quantity is outside the supported byte range."); }
    }

    private static string ValidateNamespace(string value) => Namespace().IsMatch(value) ? value :
        throw new InvalidOperationException("Kubernetes:Namespace must be a valid namespace name.");

    [GeneratedRegex(@"^(?<number>\+?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+))(?<suffix>[eE][+-]?[0-9]+|[numkKMGTPE]|[KMGTPE]i)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Quantity();
    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex Namespace();

    public void Dispose()
    {
        client?.Dispose();
        foreach (var root in roots) root.Dispose();
    }
}
