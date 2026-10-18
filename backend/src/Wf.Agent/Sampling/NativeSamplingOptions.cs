using Microsoft.Extensions.Configuration;

namespace Wf.Agent.Sampling;

public sealed record NativeSamplingOptions(bool Enabled = false, string? LinuxCgroupRoot = null)
{
    public static NativeSamplingOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Agent:Sampling");
        var enabled = section["Enabled"] is { } text && bool.Parse(text);
        var root = section["LinuxCgroupRoot"];
        if (root is not null && !Path.IsPathFullyQualified(root))
            throw new ArgumentException("Agent:Sampling:LinuxCgroupRoot must be an absolute delegated cgroup-v2 path.");
        return new NativeSamplingOptions(enabled, root);
    }
}
