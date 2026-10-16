# Agent resource sampling

Task 8.2.1.2.1 implements local sampling only. No protocol changes, shipping,
durable storage, summaries, or frontend are included.

`ResourceSamplingService` runs as an agent hosted service at a one-second
interval (configurable in its constructor to a positive interval up to five seconds).
It samples the machine immediately, then on each timer tick. `TaskHostPool`
registers the actual bound host immediately before Execute; warm hosts are
excluded. Registration captures an initial baseline and a final sample before
acknowledging a completed host, and unregisters on success, error, or cancellation.
Only the latest machine and active-task measurements are retained in memory.
Denied/failed OS reads do not stop task execution: affected measurements
have null values and an error, and the next successful read establishes a new
baseline. Timer periods are not real-time scheduling guarantees.

## Measurement semantics

- Machine CPU: difference in busy/total CPU counters, 0–100%. Reuses heartbeat
  primitives: Windows `GetSystemTimes`; Linux `/proc/stat` (first eight fields,
  excluding duplicate guest time; idle includes iowait). First sample or counter
  regression → null, not an invented zero. On Windows with multiple processor
  groups, CPU is explicitly unavailable rather than mislabeling one group as
  the whole machine.
- Machine RAM: physical used bytes = total minus available. Windows
  `GlobalMemoryStatusEx`; Linux `/proc/meminfo` `MemTotal - MemAvailable`.
  On Linux these are host kernel counters, not a container's cgroup limit.
- Task CPU: `Process.TotalProcessorTime` delta divided by monotonic elapsed time.
  **100% means one fully utilized logical core**; multithreaded hosts can exceed
  100%. Counters reset on each dispatch, excluding prior warm-host activity.
- Task RAM: `Process.WorkingSet64`, current resident bytes, not private bytes or
  a memory limit. On Linux .NET reads process `/proc` data. Both CPU and RAM
  cover the **host process only**, not its children. PID and start time identify
  each attempt; process exit, PID reuse, or access denial cannot produce stale
  successful samples.
- Machine network: cumulative sent/received bytes summed over visible
  non-loopback interfaces, including down/virtual interfaces; not unique traffic
  at an external boundary (virtual links can count a packet more than once).
  In a Linux network namespace these counters cover that namespace only.
  Rates use per-interface differences and monotonic elapsed seconds. Interface
  set changes or counter resets → null rate for that interval.
- **Task network: unavailable**, with null counts/rates and an explicit reason.
  Never substituted with machine traffic, generic process I/O, or
  `/proc/<pid>/net/dev` (which reports network-namespace counters, not process
  attribution).

## Remaining acceptance blocker

FR-046 / US-053 requires per-task network-byte accounting. The current
`TaskHostPool` has no Job Object or dedicated cgroups-v2 isolation implementation;
the Linux service unit also lacks cgroup delegation. There are no existing
isolation telemetry primitives to reuse. Reading the agent's shared cgroup
would attribute sibling tasks to one attempt and is deliberately not done.
Job Object I/O statistics and cgroups-v2 `io.stat` are not task-network counters;
`cpu.stat`/`memory.current` would describe a dedicated task cgroup's CPU/memory,
not one process's CPU/RSS. Adding isolation, ETW/eBPF accounting, or exclusive
network namespaces requires separate approved work. Consequently **task
8.2.1.2.1 is not fully complete**, despite CPU/RAM and machine-network sampling.

Authoritative OS semantics:

- [Windows machine CPU and processor groups](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes)
- [Windows generic job/process I/O counters](https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-io_counters)
- [Linux cgroups v2 CPU, memory, and block I/O](https://docs.kernel.org/admin-guide/cgroup-v2.html)
- [Linux process counters](https://docs.kernel.org/filesystems/proc.html)
- [Linux network-namespace counters](https://www.man7.org/linux/man-pages/man5/proc_pid_net.5.html)

Validation:

```powershell
dotnet test backend\tests\Wf.Agent.Tests --filter ResourceSampler `
  --artifacts-path .agent-artifacts\wf-resource-sampler-3adec91f `
  '-p:WarningsNotAsErrors=NU1902%3BNU1903'
```

`ResourceSamplerTests` includes native machine/process reads and a real
task-host dispatch/cancellation test using the production hosted timer, with
no platform skips. Run the same tests on actual Linux, not only Windows parser
fixtures. Resource transport/storage remains task 8.2.1.2.2.
