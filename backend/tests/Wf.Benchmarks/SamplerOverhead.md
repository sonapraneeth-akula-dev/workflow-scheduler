# Sampler overhead: real-time paired CPU experiment

`SamplerOverheadBenchmarks.cs` is an opt-in real OS integration benchmark, like
the existing paced API benchmarks, not a BenchmarkDotNet invocation-count loop.
No mock collectors, condensed ticks, whole-machine CPU subtraction, or
logical-processor normalization.

Scope: **managed representative agent fixture**, using the unmodified production
`ResourceSampler` and `ResourceSamplingService`. Enabled runs include actual native
task CPU/RAM/network accounting, machine sampling, production source-generated JSON
materialization/validation, `AgentMessage` construction and protobuf serialization.
Baseline runs disable sampling entirely. Both run the same child workload. Native
scopes are required: unavailable/fallback measurements fail rather than pass as zero.

Not included: task-child CPU, startup/teardown, outbox/journal, gRPC, engine/storage,
UI, NativeAOT or installed-service configurations. No end-to-end pipeline overhead
claim. Process `TotalProcessorTime` includes user and system time charged to the
agent fixture; kernel ETW/BPF work charged elsewhere is **not** completely covered.
This is not a whole-system instrumentation-cost bound.

## Contract

- Fixed production interval **1 second**, supported maximum 5 seconds. Wall-clock
  samples retain measured monotonic intervals and native scope labels.
- First perform **90 seconds of actual native 1 Hz sampling + 10 seconds of
  validation** to prewarm the managed process/serialization paths. Preserve its
  complete CPU/warmup/JIT evidence separately; it is not a paired gate result.
  This is a steady-state bound, **not a cold-start/first-task bound**.
- Defaults after global warmup: **5 pairs**, **30 measured seconds + 10 warmup
  seconds per member**, **1 concurrent task**. Alternate disabled/enabled order.
- Each task touches 64 MiB; spins for 10 ms per paced 50 ms cycle; sends/receives
  one 1024-byte UDP datagram to its own IPv4 loopback socket each cycle (20/s).
  No external destinations, packet capture or payload inspection.
- CPU = `100 * delta(agent TotalProcessorTime) / monotonic elapsed time`.
  One fully occupied core = 100%, including on multi-core machines. CPU count
  is metadata, never a divisor. Workload-process CPU is not agent overhead.
- Report all pairs and baseline/enabled/added min, median, nearest-rank p95, max.
  Added = enabled minus disabled **percentage points**, not percent relative to an
  almost-idle baseline. Preserve signed negative deltas; never clamp them.
- Gate: **every paired delta <= 1 percentage point**. At least 3 pairs,
  10 measured seconds and 5 warmup seconds; complete native CPU/RAM/network +
  machine observations at real cadence required. No missing/NaN values or
  unsupported network instrumentation may pass. Require observed nonzero task
  traffic in both directions.
- JSON report names configuration, platform/runtime, exclusions, exact measurement
  durations, agent PID/start ticks, sample count/interval range, protobuf byte
  count and child workload results. `privileges.txt` records actual token rights.
  Warmup CPU and measured-window JIT compilation time/method counts are recorded
  separately; **no JIT cost is subtracted** from the CPU gate. Managed runtime
  compilation can outlive a short warmup; keep failed cold probes rather than
  relabeling them as passing steady-state measurements.
  Per-run observation JSON retains actual production observations. Failures retain
  CPU evidence and report error; nonzero exit.

The measured conclusion applies **only** to recorded cadence, concurrency,
workload and privileges, not arbitrary host network traffic or task concurrency.
Repeated paired measurements reduce drift; this is not a formal confidence
interval or a universal performance proof.

## Windows

Run from repository root; ETW provider/session rights required. Use an isolated
artifact tree so another worker's binaries are not replaced:

```powershell
dotnet test backend\tests\Wf.Benchmarks.Tests -c Release `
  --artifacts-path .agent-artifacts\sampler-overhead-3adec91f\windows-build `
  -p:PublishAot=false -p:WarningsNotAsErrors=NU1902%3BNU1903 `
  --filter FullyQualifiedName~SamplerOverhead
dotnet .agent-artifacts\sampler-overhead-3adec91f\windows-build\bin\Wf.Benchmarks\release\Wf.Benchmarks.dll `
  --sampler-overhead-bench .agent-artifacts\sampler-overhead-3adec91f\windows `
  --pairs 5 --seconds 30 --warmup 10 --concurrency 1
```

Sampler disposal stops its uniquely named `wf-resource-{PID}-{startTicks}-*`
ETW session; child processes and owned jobs are disposed even on failure.

## Linux

Use an **owned disposable container**, private cgroup namespace, isolated internal
Docker network and existing NuGet cache mounted read-only at `/host-nuget`.
Image: `mcr.microsoft.com/dotnet/sdk:10.0-noble-aot` (record exact image digest for
each run). Launch `bash backend/tests/Wf.Benchmarks/test-sampler-overhead.sh` with
`WF_SAMPLER_OVERHEAD_ISOLATED_CONTAINER=1`. Root setup needs `SYS_ADMIN` for the
owned cgroup-v2 mount and cgroup delegation. Container needs BPF and NET_ADMIN;
allow BPF syscalls with its security policy. Do not mount the host cgroup root
writable or reuse another task's fixtures.

The script restores/builds/tests only inside
`.agent-artifacts/sampler-overhead-3adec91f/linux`, then drops to UID/GID 65534,
**CAP_BPF + CAP_NET_ADMIN only**, `NoNewPrivs=1`, for measurement. Workload children
have no effective/ambient capabilities. Cleanup removes owned task/delegation
cgroups and unmounts its mount. Caller removes its exact container/network.
Pass paired options after the script name, e.g.
`--pairs 5 --seconds 30 --warmup 10 --concurrency 1`.
`WF_SAMPLER_OVERHEAD_PREBUILT=1` reuses that exact artifact tree after a successful
build/test, for a measurement-only disposable container. Build-server reuse is
disabled; owned build servers are shut down before enabling cgroup controllers.
Set `WF_SAMPLER_OVERHEAD_RESULT_NAME` to a new simple directory name when retaining
earlier probe evidence alongside a subsequent run.

Do not run Linux and Windows measurements concurrently when claiming same-host
quiet-run precision; record other load and retain all initial probes.

## Observed October 16, 2026

**Both steady-state gates passed**, with all five signed paired deltas <= 1
one-core CPU percentage point. Shared developer host, overlapping Windows/Linux
experiments and other services: **not** a dedicated quiet-host or universal bound.
Managed fixture only; the separately tested NativeAOT agent has no overhead claim.

| Platform / metric | Minimum | Median | p95 / maximum |
| --- | ---: | ---: | ---: |
| Windows baseline CPU % | 0.000000 | 0.000000 | 0.052069 |
| Windows enabled CPU % | 0.208200 | 0.208321 | 0.572742 |
| Windows added CPU points | 0.156247 | 0.208200 | **0.572742** |
| Linux baseline CPU % | 0.007930 | 0.012617 | 0.034445 |
| Linux enabled CPU % | 0.263125 | 0.303053 | 0.342589 |
| Linux added CPU points | 0.230503 | 0.290860 | **0.329971** |

Five samples make nearest-rank p95 equal maximum. Windows added points by pair:
`[0.572742, 0.156251, 0.156247, 0.364398, 0.208200]`; Linux:
`[0.329971, 0.307992, 0.261246, 0.230503, 0.290860]`.

Exact configuration: 1 task, production 1-second timer, global 90-second native
warmup + 10-second validation, then 5 pairs of nominal 30-second measurement +
10-second per-member warmup. Actual monotonic measurement windows:
Windows 30.0018047–30.0191945 seconds; Linux 29.9967575–30.0130978 seconds.
Each platform retained **150 real task + 150 paired machine observations** in its
enabled measurement windows. Task intervals:
Windows 0.8324872–1.1193449 seconds; Linux 0.9823963–1.0104312 seconds.
Every observed task/machine CPU, RAM and network value was available; every
measured run observed positive task CPU and sent/received traffic. Individual
native intervals with measured zero CPU/bytes remain zero, not gaps or omitted
samples. No unavailable fallback passed.

- Windows: build `10.0.26300`, x64, .NET `10.0.13`, 16 logical processors
  (**metadata only**), current developer token; exact enabled/disabled token
  rights in `windows-steady\privileges.txt`, not an installed-service identity.
- Linux: Ubuntu `24.04.5`, x64, kernel `6.18.33.2-microsoft-standard-WSL2`,
  .NET `10.0.13`, SDK `10.0.402`, 16 logical processors; UID/GID 65534,
  `CAP_BPF + CAP_NET_ADMIN` only, `NoNewPrivs=1`. Workload effective/permitted/
  inheritable/ambient capabilities zero; its bounding set retains the two
  capabilities, with no-new-privileges preventing acquisition. Cached image
  digest `sha256:62a3ebde694544ae0790b5ca2ffd56db04298af5652b7295c88fd61097752664`.
- Raw numeric JSON, CPU seconds/wall seconds, assembly hashes, all observations
  and privilege records:
  `.agent-artifacts\sampler-overhead-3adec91f\windows-steady\sampler-overhead.json`
  and `linux\measurements-steady\sampler-overhead.json` under the same root.
  `evidence-summary.json` independently rechecks every CPU calculation and
  observation; `windows-run-steady.log` / `linux-run-steady.log` retain execution.

Initial probes were **not discarded or called passing**. The corrected Windows
original-reader probe failed (3 pairs, 10-second measurement, 5-second warmup):
added `[5.153712, 5.307265, 3.436844]` points. Real 1 Hz component profiling
identified costly process-wide working-set snapshots and full adapter metadata
reconstruction. Native counter-only Windows reads preserve every job member
(including owned console hosts), every non-loopback interface and 64-bit byte
precision. Equivalence tests and actual managed/NativeAOT agent-to-gRPC-to-
PostgreSQL/blob/Viewer integration passed after the change.

Linux's initial 10-second-warmup-only run also failed, with added
`[2.168954, 1.071788, 0.255406, 0.211143, 0.213847]` points. That demonstrates why
this result cannot be advertised as a cold-start/first-task bound. Full native
global warmup now precedes pairing; startup CPU remains in the separate warmup
record. Measured-window JIT compilation time is reported and **never subtracted**
from CPU. Original cold reports/observations remain in `windows-workload-probe`
and `linux\measurements`; the earlier inert-workload fixture error is separately
retained as a rejected run in `windows`. All owned ETW sessions, BPF/cgroups,
mounts, containers, networks and IPC drive mappings were cleaned.
