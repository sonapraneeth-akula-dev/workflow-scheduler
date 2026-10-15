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
