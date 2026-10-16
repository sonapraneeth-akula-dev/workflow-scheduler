# Agent resource sampling

Task 8.2.1.2.1 implements local counters only. Latest machine and active-attempt
samples stay in memory; no protocol, shipping, durable storage, report, or
frontend changes. Resource transport/storage remains task 8.2.1.2.2.

`ResourceSamplingService` samples immediately and every second (constructor
accepts a positive interval up to five seconds). `TaskHostPool` registers the
actual host before Execute and captures a final sample before acknowledging
completion. Warm-host activity is excluded by the dispatch baseline; tracking
is removed on success, error, and cancellation. Samples carry attempt identity,
PID/start time, scope labels, and monotonic elapsed interval. First CPU/rate
samples and counter regressions are null, not invented zeroes. Timer periods
are not real-time scheduling guarantees.

## Native accounting: explicit opt-in

Set `Agent:Sampling:Enabled=true` (`Agent__Sampling__Enabled=true` in the
environment). Without opt-in, existing process-only CPU/RSS sampling continues;
task network is null with an explicit enablement reason. Machine sampling is
independent of this setting. Native-scope or network permission failure falls
back to truthful available counters and a reason, not another task's traffic.
OS read failures yield null/error measurements and reset the baseline.

Windows uses a newly created Job Object per host and an owned, real-time
Kernel-Network ETW session. Enable the installer option:

```powershell
.\infra\agent\install-agent.ps1 -Executable .\wf-agent.exe `
  -Engines https://engine.example -NativeResourceSampling -WhatIf
```

Remove `-WhatIf` only for an authorized installation. Installation still uses
LocalService; it does not elevate the running service. The service identity
must be authorized to start/read the ETW session and enable Kernel-Network.
Denied ETW access leaves job CPU/RAM available and task networking explicitly
unavailable. Only owned host PIDs are counted. An owned process handle guards
against PID reuse; exit before draining, lost events, unknown schemas, and
consumer failures invalidate network counts. No packet data, addresses, or
ports are read or retained. The session flushes every second and drains a
private marker on completion; only this agent's session is stopped.

Linux requires cgroup v2, CPU/memory controllers, an **owned delegated subtree**
with no processes directly in its root, and effective `CAP_BPF` +
`CAP_NET_ADMIN`. Set `Agent:Sampling:LinuxCgroupRoot` to that absolute root.
The opt-in installer profile configures this for the standard service:

```sh
bash infra/agent/install-agent.sh --executable ./wf-agent \
  --engine https://engine.example --native-resource-sampling --validate-only
```

Remove `--validate-only` only for an authorized installation as root. It
validates inputs, not kernel or service permissions. Actual installation
requires systemd >=254 (`DelegateSubgroup`) and `/usr/bin/setpriv` from
util-linux. `wf-agent-resources.conf` enables `Delegate=cpu memory`, moves the
agent to its own subgroup, grants only BPF/network capabilities, and configures
the delegated root. Changing the unit's slice/path requires updating that root.
Production requires no `CAP_SYS_ADMIN` or cgroup mount operation.

Each Linux host joins a newly created task cgroup before executing its program;
descendants inherit it. Two minimal `cgroup_skb` BPF programs count `skb->len`
in an unpinned map: no packet contents or host/sibling traffic. Only owned
programs/cgroups/file descriptors are detached, closed, and removed. BPF denial
still permits cgroup CPU/RAM sampling. `/usr/bin/setpriv` clears inheritable and
ambient capabilities and enables no-new-privileges **before** task execution,
including process-only fallback; it also clears the bounding set when the
agent has `CAP_SETPCAP`. Missing capability-drop tooling fails launch rather
than passing agent capabilities to a task.

These accounting scopes are **not a security sandbox**. No CPU/memory limits,
network namespaces, filesystem isolation, or additional resource-limit policy
are introduced. Tasks still run under the configured agent identity.

## Counter semantics

| Counter | Windows native scope | Linux native scope |
| --- | --- | --- |
| Task CPU | Job Object user+kernel time, including descendants | Dedicated cgroup `cpu.stat` `usage_usec`, including descendants |
| Task RAM | Sum of current job-member working sets; shared pages can be counted more than once | `memory.current`, including descendants, cache, and kernel charges; not RSS/private bytes |
| Task network | Owned **host process only**, TCP/UDP IPv4/IPv6 ETW send/receive byte counts; not child traffic or Ethernet wire bytes | Dedicated cgroup ingress/egress L3 `skb->len`, including descendants; includes IP/transport headers, not Ethernet overhead |

Task CPU is a time delta divided by monotonic elapsed time: **100% = one
logical core**, potentially above 100%. Network counters reset at dispatch;
sent/received rates are byte deltas divided by monotonic seconds. Loopback
traffic is included in task accounting. Windows and Linux network counters
have different layer semantics and must not be compared as identical wire
traffic. IPv4/IPv6 UDP fixture tests verify Windows payload lengths and Linux
payload plus IP/UDP header lengths; child tests verify the declared scopes.
Unsupported protocols are not silently advertised as all-protocol accounting.

When native scopes are unavailable, task CPU uses `Process.TotalProcessorTime`
and RAM uses `Process.WorkingSet64` (host-only RSS). Task networking remains
null with a reason, never machine traffic, generic job I/O, `io.stat`, or
`/proc/<pid>/net/dev` as a process substitute.

Machine CPU uses heartbeat primitives: Windows `GetSystemTimes`, Linux
`/proc/stat` first eight fields (excluding duplicate guest time, idle includes
iowait); busy/total deltas are clamped to 0–100%. Windows multiple-processor-group
machine CPU is explicitly unavailable rather than one group mislabeled as
the whole machine. Physical used RAM is total minus available via
`GlobalMemoryStatusEx` or `/proc/meminfo`. Linux machine CPU/RAM use host kernel
counters, not container resource limits.

Machine networking sums visible non-loopback interface counters, including
down/virtual interfaces. Virtual links may count traffic more than once; these
are not unique external-boundary bytes. Linux interface counters cover the
current network namespace. Interface-set changes/resets produce null rates.

## Validation and deployment limits

Use project-local artifacts (including temporary runtime files):

```powershell
$env:TEMP=$PWD.Path; $env:TMP=$PWD.Path
dotnet test backend\tests\Wf.Agent.Tests --filter ResourceSampler `
  --artifacts-path .agent-artifacts\wf-resource-sampler-3adec91f `
  '-p:WarningsNotAsErrors=NU1902%3BNU1903'
```

The Windows native tests require actual ETW rights; no platform skips there.
Linux native tests deliberately skip without a supplied delegated root.
For a complete Linux run, use the explicit disposable-container harness, which
fails cleanup if owned cgroups remain:

```powershell
docker run --rm --cap-add SYS_ADMIN --cap-add BPF --cap-add NET_ADMIN `
  --mount "type=bind,source=$PWD,target=/repo" --workdir /repo `
  --env WF_RESOURCE_ISOLATED_CONTAINER=1 `
  mcr.microsoft.com/dotnet/sdk:10.0-noble-aot `
  bash infra/agent/test-resource-sampling.sh --nonroot
```

`SYS_ADMIN` is used only by this opt-in test harness to create a private writable
cgroup mount. The actual test process runs as UID65534 with only BPF/NET_ADMIN,
no-new-privileges, and delegated cgroups; fixtures assert tasks/children have
zero effective capabilities. Omit `--nonroot` for root coverage. For real
rights-denied coverage use `--without-bpf` (no BPF/NET_ADMIN grants needed).
The script never changes the host service or host cgroup mounts. Optional
`WF_RESOURCE_TEST_FILTER` selects tests; `WF_RESOURCE_OFFLINE_RESTORE=1` disables
NuGet auditing and an optional read-only `/host-nuget` mount supplies cached
packages in disconnected environments. Portable test builds use
`PublishAot=false`; native-AOT publish is not validated by this harness.

Actual Windows and Linux tests cover concurrent isolated UDP counts/directions,
rate math, child scopes, machine/process reads, real host dispatch and
cancellation, and explicit unavailable paths. **Installed LocalService ETW
permissions and a real non-root systemd deployment on Ubuntu/Azure Linux have
not been exercised**; the non-root container run verifies kernel capabilities
and delegation but is not a deployed-service validation.
