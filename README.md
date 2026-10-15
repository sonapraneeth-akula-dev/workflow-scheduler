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
All policies and variables validate before tasks start. Remote/task-host/dry-run
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
