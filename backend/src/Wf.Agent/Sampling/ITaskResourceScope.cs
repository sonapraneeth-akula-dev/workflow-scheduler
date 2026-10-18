using System.Diagnostics;

namespace Wf.Agent.Sampling;

public interface ITaskResourceScope : IDisposable
{
    void PrepareStart(ProcessStartInfo startInfo);
    void Bind(Process process);
    void BeginAttempt();
    void EndAttempt();
    ResourceCounters ReadCounters();
}
