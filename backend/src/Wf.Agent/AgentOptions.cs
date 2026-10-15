using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Wf.Agent;

public sealed class AgentOptions
{
    public IReadOnlyList<Uri> Engines { get; }
    public string Id { get; }
    public string Name { get; }
    public string Pool { get; }
    public uint MaxTasks { get; }
    public IReadOnlyDictionary<string, string> Labels { get; }
    public TimeSpan HeartbeatInterval { get; }

    public AgentOptions(IEnumerable<Uri> engines, string id, string name, string pool, uint maxTasks,
        IReadOnlyDictionary<string, string>? labels = null, TimeSpan? heartbeatInterval = null,
        bool allowInsecureTransport = false)
    {
        ArgumentNullException.ThrowIfNull(engines);
        var endpoints = engines.Distinct().ToArray();
        if (endpoints.Length == 0) throw new ArgumentException("At least one Agent:Engines endpoint is required.", nameof(engines));
        foreach (var endpoint in endpoints)
        {
            if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
                endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length != 0 ||
                endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                throw new ArgumentException("Agent engine endpoints must be HTTP(S) origins without credentials, paths or queries.", nameof(engines));
            if (endpoint.Scheme == "http" && !endpoint.IsLoopback && !allowInsecureTransport)
                throw new ArgumentException("Non-loopback agent engines require HTTPS. Explicitly opt in only for an isolated container network.", nameof(engines));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(pool);
        if (maxTasks == 0) throw new ArgumentOutOfRangeException(nameof(maxTasks));
        var interval = heartbeatInterval ?? TimeSpan.FromSeconds(5);
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval), "Heartbeat interval must be positive and at most 5 seconds.");
        var copiedLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in labels ?? new Dictionary<string, string>())
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            copiedLabels.Add(key, value);
        }
        Engines = Array.AsReadOnly(endpoints);
        Id = id;
        Name = name;
        Pool = pool;
        MaxTasks = maxTasks;
        Labels = new ReadOnlyDictionary<string, string>(copiedLabels);
        HeartbeatInterval = interval;
    }

    public static AgentOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("Agent");
        var engines = section.GetSection("Engines").GetChildren()
            .Select(item => new Uri(item.Value ?? throw new ArgumentException("An Agent:Engines endpoint is empty."), UriKind.Absolute));
        var maxTasks = (uint)Environment.ProcessorCount;
        if (section["MaxTasks"] is { } text &&
            !uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out maxTasks))
            throw new ArgumentException("Agent:MaxTasks must be a positive unsigned integer.");
        return new AgentOptions(engines, section["Id"] ?? Environment.MachineName,
            section["Name"] ?? Environment.MachineName, section["Pool"] ?? "default", maxTasks,
            section.GetSection("Labels").GetChildren().ToDictionary(item => item.Key,
                item => item.Value ?? throw new ArgumentException("An agent label is empty."), StringComparer.Ordinal),
            allowInsecureTransport: section["AllowInsecureTransport"] is { } insecure && bool.Parse(insecure));
    }
}
