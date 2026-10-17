namespace Wf.Agent.Limits;

internal interface IResourceLimiter : IDisposable
{
    string? LinuxCgroupPath { get; }
    bool MemoryLimitExceeded { get; }
}
