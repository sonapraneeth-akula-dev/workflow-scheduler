# Agent resource sampling

Task 8.2.1.2.1 implements counters; task 8.2.1.2.2 ships and persists them.
Latest machine and active-attempt samples remain available locally. When an
actual task host executes through `TaskCommands`, each task observation and
latest machine observation also enter the durable agent journal as
`ResourceSample`. No frontend or overhead-benchmark implementation is included.

`ResourceSamplingService` samples immediately and every second (constructor
accepts a positive interval up to five seconds). `TaskHostPool` registers the
actual host before Execute and captures a final sample before acknowledging
completion. Warm-host activity is excluded by the dispatch baseline; tracking
is removed on success, error, and cancellation. Samples carry attempt identity,
PID/start time, scope labels, and monotonic elapsed interval. First CPU/rate
samples and counter regressions are null, not invented zeroes. Timer periods
are not real-time scheduling guarantees.

## Resource transport and storage contract

`agent.proto` field 15 carries `ResourceSample`: exact `AttemptRef`, dispatched
`reservation_epoch`, contiguous per-attempt `sample_sequence` starting at 1,
and at most 16 KiB of strict `ResourceObservation` JSON. Unknown JSON members,
nonfinite/negative values, invalid scopes, unavailable values without reasons,
and inconsistent byte-delta/rate/duration combinations are rejected. This
numeric allowlist contains no task inputs, outputs, secrets, packet contents,
addresses or ports. `ResourceJsonContext` supplies NativeAOT-safe serialization.
Upgrade/migrate the engine before upgrading agents: older agents remain valid
and simply leave resource columns unavailable; old engines do not accept the
new `ResourceSample` payload.

Names specify units: `atUtc` is UTC, `intervalSeconds` is **monotonic elapsed
seconds**, `cpuPercent` uses **100% = one task CPU core** (may exceed 100), RAM
and cumulative/delta network values are **bytes**, rates **bytes/second**.
Machine CPU remains 0–100%. Exact counters are JSON unsigned 64-bit integers;
summary byte totals are decimal integers. A chart may approximate these large
integers, but must not treat null as zero. Each point carries `cpuScope`,
`memoryScope`, `networkScope` and independent unavailable reasons. First samples,
counter resets, interface-set changes, missing rights and lost ETW events remain
explicit gaps; they are never substituted with machine traffic. Machine samples
are paired into active-attempt series; idle machine history is not persisted.
See the native accounting sections below for the deliberately different Windows
transport-byte/host-only and Linux L3/descendant scopes.

The default cadence is one second, with an immediate baseline and final
pre-Ack read. Final resource records are journaled **before** `TaskCompleted`,
then the task host receives its SDK acknowledgement. Samples retain the global
outbox sequence across reconnect/restart. Capacity/disk errors fail closed
without discarding records (existing journal ceilings: 4,096 records / 32 MiB);
a prolonged offline agent can exhaust that bounded journal. Heartbeats alone
may be coalesced. No per-sample upload URL or credential is passed to task code.

The gateway acknowledges only after `ResourceSampleSink` commits. Its transaction
uses the existing database leader-epoch fence, current agent-session lock, run
lock, assigned-agent/attempt lock and persisted dispatch reservation. Exact
replays, including terminal-attempt journal replays, do not double count.
Conflicting replay contents, ordinal gaps, foreign/superseded sessions and new
terminal-attempt samples are rejected. Original wall-clock timestamps are kept
even across UTC clock corrections: sequence determines order, monotonic duration
determines rates and averages.

Migration `0025_resource_samples.sql` adds only scalar aggregate state and a
verified blob pointer to `task_attempts`:

- `resource_avg_cpu_percent` is duration-weighted over valid CPU intervals;
  `resource_max_cpu_percent` is their maximum. First/reset/unavailable intervals
  do not contribute. `resource_cpu_percent_seconds` and
  `resource_cpu_observed_seconds` retain exact aggregation state.
- `resource_peak_ram_bytes` is the maximum available RAM observation, including
  a baseline. This does not equate Windows resident sums with Linux cgroup RAM.
- `resource_network_sent_bytes` / `resource_network_received_bytes` sum exact
  non-regressing, scope-consistent **delta** bytes, not counter values or rounded
  rates. `resource_network_observed_seconds` exposes valid interval coverage.
  Null means no valid observation; a measured idle interval can legitimately
  produce zero. Totals with gaps cover observed intervals only.
- `resource_sample_count` and `resource_series_uri` / `resource_series_sha256` /
  `resource_series_bytes` identify the immutable series head. No observation
  JSON or machine/task sample payload lives in PostgreSQL.

The engine writes validated agent-originated data through the existing
`IBlobStore`, rather than giving the agent broader blob credentials. Schema-v1
JSON chunks live at
`resources/{runId}/{sha256(node/task)}/{attempt}/{sequence}-{sha256(chunk)}.json`.
Each bounded chunk contains one observation and an integrity-checked link to its
predecessor. Upload precedes the pointer/summary transaction commit; interruption
may leave an unreferenced object, but cannot expose an uncommitted observation.
Retrying identical content is create-only/idempotent. This avoids rewriting a
growing series on every tick. It supersedes the design's illustrative single
`{attempt}.bin` object and MB-named summary fields.

`ResourceRetentionService` sweeps every ten minutes (including startup): after a
run row/partition is removed, it removes **only** that run's `resources/` prefix,
including unreferenced interrupted uploads. Existing runs, including terminal
runs, keep their series. Database failures never authorize deletion. Both
filesystem and Azure stores implement prefix cleanup; Azure's existing managed
identity needs list/delete permission as well as upload/read.

### Fresh Viewer reads for task metrics

Both authenticated routes re-resolve run-scoped Viewer permissions per request,
including every cursor page, and send `Cache-Control: no-store` / `nosniff`.
Unknown or ungranted runs fail closed (403). No storage URL, SAS or filesystem
path is returned as a data field.

```text
GET /api/v1/runs/{canonical-v7-id}/resource-metrics
  -> ResourceAttemptSummary[]: taskKey, attempt, agentId, sampleCount,
     averageCpuPercent, maxCpuPercent, peakRamBytes, networkSentBytes,
     networkReceivedBytes, cpuObservedSeconds, networkObservedSeconds,
     cpuUnavailableReason, memoryUnavailableReason, networkUnavailableReason

GET /api/v1/runs/{canonical-v7-id}/resources
    ?taskKey={URI-encoded node/task}&attempt=1&limit=60&cursor={nextCursor}
  -> { samples: [{ sequence, observation: { task, machine,
       machineUnavailableReason } }], nextCursor }
```

Series are newest-first; reverse for chronological plotting. `limit` is 1–120
(default 60). Cursors are HMAC-signed, attempt/run-bound, expire after one hour,
and are not authorization credentials or confidential storage handles. Invalid,
duplicate or unknown query parameters return 400; an authorized missing attempt
returns 404; storage/corruption errors return 503 without partial sample data.
The existing `/metrics` execution-timings endpoint is unchanged. OpenAPI includes
typed response models and all four series query parameters; regenerate clients
without committing generated frontend changes as part of this backend task.

### Shipping integration validation

`dotnet test backend\tests\Wf.Integration.Tests --filter ResourceMetrics` covers
an **external real agent process**, actual SDK task-host execution with CPU/RAM
and UDP traffic, gRPC gateway, PostgreSQL scalar columns, immutable filesystem
blob series and fresh Viewer HTTP reads. Mathematical/fault-injection tests
separately cover null/zero, resets, weighted intervals, UTC clock corrections,
conflicting/ordered/concurrent duplicates, journal replay, reservation/session/
leader fencing, interrupted publication, corruption and run-retention cleanup.
They are not presented as native OS sampling proof.

On Windows, put `WF_RESOURCE_TEST_IPC_ROOT` on a short, project-owned path
(a disposable `subst` drive is sufficient) to respect Unix-domain-socket path
limits; keep `TEMP`/`TMP` and `WF_RESOURCE_TEST_ARTIFACTS` project-owned too.
On Linux, use the isolated-container delegation pattern already used by
`infra/agent/test-resource-sampling.sh`; the backend integration companion is
`backend/tests/Wf.Integration.Tests/Resources/test-resource-metrics.sh`.
It requires an isolated `WF_TEST_POSTGRES`, restored assets, and explicit
`WF_RESOURCE_ISOLATED_CONTAINER=1`. `--nonroot` runs UID 65534 with only
`CAP_BPF`/`CAP_NET_ADMIN` and no-new-privileges; `--without-bpf` verifies honest
network unavailability without breaking CPU/RAM/task completion. Mount its
project-local `linux/tests` artifact path on a private native Linux Docker
volume if the Windows bind filesystem returns transient immediate-read EIO.

`WF_RESOURCE_REQUIRE_NETWORK=1` makes unavailable native network accounting a
test failure; normal runs instead verify null plus its actual native reason.
`WF_RESOURCE_AGENT_EXECUTABLE` optionally selects a freshly published NativeAOT
agent for the same external-process pipeline. Native ETW/BPF rights and loss
rules are never weakened to make tests green. Installed-service identity
validation and sampler-overhead benchmarking remain separate work.
Windows trace names contain the owning PID and exact process-start UTC ticks.
After the external-agent fixture terminates its own process, it stops only trace
sessions matching that lifetime identity; it never stops unrelated collectors.

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
