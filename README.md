# Kardia Workflow Scheduler

Implementation follows `docs/plans/2026-10-15_workflow-scheduler_azure-task-list.md`.

## Backend

Requires the .NET 10 SDK. PostgreSQL integration tests require Docker with Linux
containers, or `WF_TEST_POSTGRES` pointing at a PostgreSQL 17+ test server.
External-server tests create and remove only their own unique schemas; the test
user needs schema-creation permission. CI uses Testcontainers on Linux and a
service PostgreSQL on Windows.

```powershell
dotnet build backend\Wf.slnx
dotnet test backend\Wf.slnx --collect:"XPlat Code Coverage"
dotnet run --file backend\samples\workflows\scenario-a.cs -- --var env=dev
```

The sample executes scenario A's four-node DAG, a real command, illustrative
deployment/check functions, and lifecycle stages through the shared engine core.
Node/task states print live. Exit code is 0 only for `Succeeded`; failures,
timeouts and aborts return 1. Ctrl+C requests an abort and allows cleanup.
`LocalVariables.Parse` accepts repeated `--var key=value` pairs; strings, enums,
durations and secret-reference names use plain text, other types use JSON.
All policies and variables validate before tasks start.

`await workflow.RunAsync(args)` selects local execution by default, `--describe`,
`--engine <origin>`, `--task-host --socket <absolute-path>`, or
`--dry-run <mocks.json>`. Modes are exclusive; repeated `--var key=value` pairs
work before or after local/remote/dry-run mode flags. Describe emits only the
manifest, without binding required run values or executing tasks.
Remote mode validates variables and POSTs `workflowId`, exact `version` and
overrides to `/api/v1/runs` with an idempotency key, then prints the accepted
run UUID. It does not upload/register the workflow or wait for completion.
Non-loopback HTTP, redirects, invalid/oversized replies and rejection statuses
fail explicitly; the full hosted trigger API remains its own backlog task.

The all-mode entrypoint requires ordinary, untrimmed IL because it includes the
gRPC task-host server. File-based workflows use `#:property PublishAot=false`,
as the sample does. The standalone SDK definition, manifest and local APIs
retain their AOT-compatible implementations; the agent still publishes native.

Unit and integration tests cover authoring, real task execution and PostgreSQL
event replay. CI runs on Windows and Linux. Frontend tests will accompany the
frontend implementation.

Task delegates resolve by stable `node/task` keys through `TaskRegistry`; no
assembly scanning. Contexts expose `Var<T>`, AOT-safe `VarEnum<T>`, declared
`Input<T>`, bounded `Outputs`, cancellation, logging and injected report/artifact
publishers. Publishing without a configured host publisher fails explicitly.
Native AOT/trim analyzers run for the SDK and shared contracts.

Core abort plans preserve the first accepted who/when/reason/source record.
Hosts commit the initial abort event before cancelling attempts, drain active
work, then run eligible `always` and post stages with `ctx.Abort`. Queued aborts
skip all work; running aborts record `EndedAt` only after cleanup actually stops.
Abort targets and the original record survive snapshot/event replay.
Attempt/node/run deadlines and retry backoff use a `TimeProvider`; cleanup gets
fresh cancellation tokens and its own deadlines. A function that ignores
cancellation beyond grace causes an explicit error, not a false terminal result:
the in-process local engine cannot forcibly kill managed code. Distributed task
hosts provide process isolation; built-in command cancellation also kills and
reaps its own process tree.

Dry-run executes the same core using explicit task mocks, without invoking any
original task delegate or external service:

```powershell
dotnet run --file backend\samples\workflows\scenario-a.cs -- --dry-run backend\samples\workflows\scenario-a.mocks.json --var env=ppe
```

Dry-run stdout contains one source-generated JSON event log, including snapshot, outputs,
attempts and final exit code. Mocks support `Succeed` with outputs, `Fail`,
`Timeout`, `Throw`, and `FailThenSucceed` with `failuresBeforeSuccess`. Each task
needs its own mock unless the spec explicitly supplies `default`; missing mocks,
unknown keys and unknown JSON properties are errors, not automatic success.
Spec `variables` override workflow defaults; command-line `--var` values win.
Timeouts/backoff advance virtual time with deterministic midpoint jitter and no
real delays. The testing-library build cache and fluent assertions follow in
their own backlog items.

Placement is authored at workflow, node or task level with `RunsOn` and
`Affinity`. The innermost selector replaces the whole outer selector; affinity
inherits independently. `RunsOn()` explicitly clears constraints; `Affinity.None`
clears an inherited pin. Stages support the same settings.

```csharp
w.Variables(v => v.StringList("machines", ["worker-a", "worker-b"]))
 .RunsOn(v => Machines.All(Os.Linux, Machines.Named(v["machines"]), Machines.Label("dotnet")))
 .Affinity(Affinity.Node)
 .Node("build", n => n.Task("compile", Compile,
     t => t.RunsOn(Machines.Pool("ci")).Affinity(Affinity.None)));
```

`Os.Windows/Linux`, `Machines.Pool`, `Machines.Label` (presence or exact value)
and `Machines.Named` compose with AND; names within one `Named` are alternatives.
Selector callbacks build symbolic metadata once. Describing never reads run
variables or invokes task delegates. Effective task selectors resolve from bound
string/enum variables at run start; machine names also accept string lists.
Missing/empty/secret/wrong-type values fail before execution. Described templates
stay unchanged between runs. Affinity supports `None`, `Node`, `Run` and
`SameMachineAs("node")`; unknown/self/deadlocking references are rejected.
In-process local execution binds selectors but does not create or dispatch to
named agents; machine matching belongs to distributed placement.

`Wf.Engine.Core.Placement.PlacementEngine` filters health, drain, selectors,
reservations, slots and RAM, then prefers warm hosts, lower slot utilization and
stable round-robin ties. `Propose` changes nothing; `Apply` follows the durable
dispatch event and rejects stale/foreign decisions. Capacity supplied by the
dispatcher must include committed dispatches not yet reflected in heartbeats.
Warm-package sets describe the bounded host pool, not every downloaded package.
Node references use the first committed placement; platform re-placement moves
affected pins without spending an author's retries, while `NoAutoRetry` blocks
platform placement. Snapshots restore pins and rotation; `ForgetRun` releases
terminal-run state. Placement timeout and API single-machine overrides follow
in their own backlog task.

## Agent

The Native AOT agent runs in a console, as Windows Service `wf-agent`, or under
systemd. Service lifetime integration, installers and the persistent engine
registry are implemented.

```powershell
$env:Agent__Engines__0 = "http://localhost:5000"
$env:Agent__Id = "f2111fd3-f78e-455b-bbe0-75aeae01fdd1"
$env:Agent__Labels__dotnet = "10"
dotnet run --project backend\src\Wf.Agent
dotnet publish backend\src\Wf.Agent -c Release -r win-x64
```

Publish `linux-x64` on Linux with the Native AOT compiler prerequisites; Windows
and Linux CI publish native executables and connect each to a real HTTP/2 test
gateway. Run the native smoke test locally by setting `WF_AGENT_EXECUTABLE` to
the published executable before `dotnet test`.

`Agent__Engines__1`, etc. configure additional engine origins. Optional settings:
`Agent__Name`, `Agent__Pool` (default `default`), `Agent__MaxTasks` (default CPU
count), `Agent__Labels__<key>`. `Agent__Id` is a required stable UUID; generate
it once and retain it across restarts. Names default to the machine name and
must be unique in the registry. Hello reports OS, architecture,
CPU count, physical RAM, labels and binary version. Five-second heartbeats report
machine CPU/RAM/disk and slots. Disconnects retry configured engines within one
second; leader hints are followed only when they match a configured endpoint.

HTTPS is required off loopback. `Agent__AllowInsecureTransport=true` is only for
an isolated internal container network; TLS certificate validation stays enabled.
Agent-role token acquisition is a separate authentication backlog item.
Dispatch and cancel commands run asynchronously, so downloads and user tasks do
not block heartbeats or stream reception. Replayed attempts are deduplicated
within the agent process. Started/completed messages retain their sequence until
acknowledged and replay on reconnect; this queue is in memory, not yet the durable
result journal. Drain, restart recovery and host re-adoption follow separately.

`TaskHostPool` prewarms compiled workflow processes and verifies their PID,
protocol and workflow version over owner-only Unix domain sockets on Windows
and Linux. Warm dispatch starts no process. Each host binds once to one attempt:
parallel tasks cannot share mutable memory, limits or forced cancellation.
The SDK server executes real registry delegates with typed variables, declared
inputs and bounded outputs; completion waits for acknowledgement before exit.
Cancellation is cooperative first, then kills that host's process tree after
grace (default 30 seconds). Workflow hosts use ordinary, untrimmed .NET IL, not
Native AOT, and require the ASP.NET Core runtime. Resource-limit enforcement and
durable result recovery remain separate backlog items. The sample accepts
`--task-host --socket <path>`; normal local execution is unchanged.

`PackageCache` downloads immutable ZIP packages into SHA-256-addressed directories.
Set `Agent__PackageCache` to an absolute path to override the user's local
`wf-agent\packages` directory. Packages contain published IL/dependencies plus
`wf-package.json` with `version` and root-level `entryAssembly` DLL fields.
HTTPS is required except on loopback. Hash, path, link, size and manifest checks
precede atomic publication; warm hits verify archive and extracted-file integrity
without HTTP or compilation. Cache failures are explicit attempt platform errors.
Windows publication retries only bounded file-lock/access conflicts.

`Dispatcher` accepts only database-committed Ready tasks in running runs.
Dispatched event, attempt capacity reservation and dispatch outbox commit in one
PostgreSQL transaction before the gateway sends anything. Committed pending
attempts count against slots/RAM before heartbeats catch up. Streams have one
writer; a new Hello fences the old session, pending commands replay, and task
events are acknowledged only after durable, idempotent acceptance. Core readiness
and node/run coordination are explicit caller responsibilities; this is not yet
the full hosted run actor or HA leader loop.

The control-plane agent gateway listens on loopback HTTP/2 port 5000. Configure
`ConnectionStrings__Workflow`, apply the numbered scripts in
`backend\db\migrations` to the target PostgreSQL schema, then run `dotnet run --project
backend\src\Wf.ControlPlane`. This is a single local gateway, not the HA/auth
control plane delivered by later tasks. Registration and heartbeat acknowledgements
follow durable writes. Agents lose health at exactly 15 seconds without a
heartbeat; dispatch eligibility checks the deadline even before the sweeper runs.
Reconnecting Hello replaces the session token, fencing old connections.

`infra\agent\install-agent.ps1` installs a Native AOT executable and persistent
UUID/configuration as a Windows LocalService service (elevated PowerShell).
`-WhatIf` validates inputs without changes. On Linux, run
`sudo bash infra/agent/install-agent.sh --executable <published-path> --engine
<origin>` to install the restricted `wf-agent` system user and systemd unit;
`--validate-only` makes no changes. Both refuse to overwrite an existing install.
CI installs, starts, checks and removes services on its disposable runners.

## Schedule expressions

`Wf.Validation.Schedules.ScheduleExpression.Create(cron, interval, timeZoneId)`
accepts exactly one standard five-field cron expression (or supported macro) or
positive elapsed interval, plus an explicit IANA timezone. UTC results are
strictly after the supplied cursor; `GetNextOccurrences` returns an immutable
preview (five by default). Cron uses Cronos DST semantics: fixed times in a
spring gap move to the first valid instant, fixed fall-fold times fire once,
and interval-like cron preserves distinct UTC occurrences across both offsets.
Elapsed intervals must advance from the last intended fire, not scheduler poll
time. Hosts need ICU/timezone data; schedule persistence, APIs and the leader
scheduler remain separate tasks.

## Script build cache spike

`Wf.Validation.Build.ScriptBuildCache.BuildAsync(new ScriptBuildRequest(script,
sourceRoot, additionalInputs), token)` publishes framework-dependent IL once,
runs the compiled `--describe` entrypoint, and returns the typed manifest, exact
JSON, assembly and publish paths. Content/configuration/dependency fingerprints,
cross-process locking, atomic publication and an integrity inventory protect
cache hits; corruption, source changes, compiler/describe failure and cancellation
fail explicitly, never serve stale success. Callers must declare external
dependencies; floating feeds, undeclared files and nondeterministic descriptions
are not tracked. No eviction, sandbox or implicit timeout is provided by this spike.

Local R-5 measurements: .NET SDK 10.0.402, Windows x64, real 20-node SDK workflow
plus schedule JSON, cached NuGet, concurrent shared-machine work. Totals for cache
operations include fingerprinting and integrity verification; not a full
Validator CLI benchmark.

| Path | Publish | Describe | Total |
|------|---------|----------|-------|
| Raw cold | 37.550 s | 0.679 s | 38.229 s |
| Raw incremental warm | 7.996 s | 0.572 s | 8.569 s |
| Cache miss | 31.744 s | 0.948 s | 35.523 s |
| Cache hit | skipped | skipped | 1.050 s |

The warm < 10 s gate passes. Twenty-six cache cases include real compilation,
schedule invalidation, concurrent builders, corruption and owned process-tree
cancellation. Timing evidence is kept here until a PR is explicitly requested.

`Wf.Validation.Validator.Validate(directory)` and `ValidateAsync` use this
cache for a directory containing `workflow.cs`. `Wf.Sdk.Testing.WorkflowValidation`
exposes the same API to xUnit and other callers. Results contain immutable errors
with rule ID, file, path, message and hint, plus the successful build or original
compiler/describe diagnostics. WFV001-WFV004 cover missing directory/script,
publish failure and describe failure. WFV005-WFV009 cover DAG cycles, unknown
dependencies, duplicate keys, variable type/default/allowed-value constraints and
required-flag shape. Raw manifest checks precede typed contract construction on
both fresh and cached descriptions; errors sort by rule ID and indexed JSON path.
Required variables without defaults are valid definitions; runtime binding checks
their supplied values. Cancellation, infrastructure/cache errors and malformed
description contracts propagate, not turn into validation success.
Validation executes trusted workflow code; it is not a sandbox. Team, schedule
and version rules follow in separate backlog tasks.

`Wf.Validation.SemVer.ApiBaseline.FromManifest` generates the immutable
`workflow.api.json` contract: SemVer version, variable schemas and node keys.
Ordinal ordering and source-generated JSON make equivalent manifests produce
identical bytes. Strict parsing, stream read/write, cancellation and sanitized
errors are covered, including a real SDK round-trip with reflection disabled.
Breaking-change classification and version-bump enforcement follow separately.

## Benchmarks

```powershell
dotnet run -c Release --project backend\tests\Wf.Benchmarks -- --filter '*Describe*'
```

The precompiled 200-node description benchmark exports full JSON and fails when
p95 reaches 1 second or measurements are missing. Workflow construction and
compilation are outside the measured operation. A local Windows measurement on
October 15, 2026: mean 0.483 ms, p95 0.524 ms. Generated reports stay under
`BenchmarkDotNet.Artifacts` and are ignored by git; results depend on the machine.

```powershell
dotnet run -c Release --project backend\tests\Wf.Benchmarks -- --filter '*EngineCore*' --metrics BenchmarkDotNet.Artifacts\engine-metrics.json
dotnet run -c Release --project backend\tests\Wf.Benchmarks -- --compare BenchmarkDotNet.Artifacts\baseline-metrics.json BenchmarkDotNet.Artifacts\engine-metrics.json
```

Execution measures a 200-node no-op chain through the actual local/core engine.
The run p95 must stay <= 3 s; per-node Ready-to-terminal p95 <= 10 ms, measured
from 1,000 individual nodes after warmup, not inferred by dividing run time.
Local Windows measurements: run mean 58.79 ms, p95 69.982 ms; node p95 1.236 ms.

`benchmarks.yml` executes the pinned code revision in
`backend/tests/Wf.Benchmarks/baseline.json` and current code on the same
Windows/Linux runner with identical measurement code. Either p95 regressing by
more than 10% fails, as do missing/invalid results or incompatible environments.
Each run uploads baseline/current metrics and full BenchmarkDotNet reports.
Baseline revision changes are explicit, reviewable code changes; there is no
first-run skip or automatic acceptance of slower results. Virtualized runners
can still be noisy; investigate a failed comparison rather than relaxing limits.
