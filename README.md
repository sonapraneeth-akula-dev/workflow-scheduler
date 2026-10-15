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
All policies and variables validate before tasks start. General remote/task-host
mode dispatch follows in its own backlog items.

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
an in-process host cannot forcibly kill managed code. Process isolation belongs
to the task-host backlog; built-in command cancellation kills and reaps its own
process tree.

Dry-run executes the same core using explicit task mocks, without invoking any
original task delegate or external service:

```powershell
dotnet run --file backend\samples\workflows\scenario-a.cs -- --dry-run backend\samples\workflows\scenario-a.mocks.json --var env=ppe
```

Stdout contains one source-generated JSON event log, including snapshot, outputs,
attempts and final exit code. Mocks support `Succeed` with outputs, `Fail`,
`Timeout`, `Throw`, and `FailThenSucceed` with `failuresBeforeSuccess`. Each task
needs its own mock unless the spec explicitly supplies `default`; missing mocks,
unknown keys and unknown JSON properties are errors, not automatic success.
Spec `variables` override workflow defaults; command-line `--var` values win.
Timeouts/backoff advance virtual time with deterministic midpoint jitter and no
real delays. The testing-library build cache and fluent assertions follow in
their own backlog items.

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
Agent-role token acquisition is a separate authentication backlog item. This
increment registers and heartbeats only: dispatch/drain commands fail explicitly
until their task-host/coordinator implementations are installed, never claim
successful execution. Results journaling and host re-adoption follow separately.

The control-plane agent gateway listens on loopback HTTP/2 port 5000. Configure
`ConnectionStrings__Workflow`, apply `backend\db\migrations\0005_agents.sql` to
the target PostgreSQL schema, then run `dotnet run --project
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
