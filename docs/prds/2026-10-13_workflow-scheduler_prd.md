# PRD: Workflow Scheduler (+ Phase 2: Perf & Response-Parity Testing)

## 1. Overview
- **Version**: 1.0  |  **Date**: 2026-10-13  |  **Status**: Draft
- **Source**: [Discovery doc (Approved)](../plans/2026-10-12_workflow-scheduler_discovery.md). `FR-xxx` / `NFR-xxx` IDs below refer to it.

### Problem Statement
Engineers run multi-step goals with ad-hoc scripts and pipelines: there is no dependency handling, no live
view of progress, no consistent history for recurring runs, and failures surface late. Leads cannot see
status or trends organised by team, type, or metadata. In phase 2, service teams have no shared infra to run
performance and response-parity tests, so regressions reach production because testing is costly to set up.

### Target Users
Internal engineers who author workflows in C#, operators who trigger and watch runs, leads who browse status
and trends, the core platform team that runs the system, and (phase 2) service teams who supply only
request/response hooks to test their services on our machines.

## 2. Goals & Non-Goals
### Business Goals
- One owned, C#-based workflow platform replacing per-team scripting, on Azure and fully reproducible locally (Windows + Linux).
- Extremely reliable: 100% of runs reach a terminal state; ≤ 0.01% of runs fail for platform reasons.
- Agile and lean: platform overhead ≤ 5% of run wall-clock; control plane ≤ 2 vCPU / 1 GB RAM at peak (NFR-031).
- Phase 2: a new service onboards perf + parity tests with only request/response hooks (0 core PRs).

### User Goals
- Authors write a workflow once in C#, unit-test it offline, and have it live within 60 s of merge.
- Operators trigger, schedule, abort, and re-run with confidence and see live state within 1 s.
- Viewers find any workflow in ≤ 3 interactions via team → type → metadata.
- Service teams get perf and parity verdicts with reports, without owning any infrastructure.

### Non-Goals (Out of Scope)
- Visual drag-and-drop workflow editor — _Rationale: logic lives in SDK scripts; the UI views and operates._
- Adopting Temporal/Argo/Airflow as the engine — _Rationale: decision to build our own engine._
- Data/ETL features (lineage, partition backfills) — _Rationale: not the stated goal._
- External/customer-facing multi-tenancy — _Rationale: internal tool._
- Provisioning/autoscaling machines — _Rationale: phase 2 uses already-registered machines._
- Service-specific request/response logic — _Rationale: owned by service teams via hooks._
- Cross-region active-active HA — _Rationale: no stated need; single region with zone redundancy._
- Suspending remote waits without occupying an agent slot — _Rationale: use existing task hosts and checkpoints first; revisit if waiting tasks exhaust capacity._
- Recovering an hours-long synchronous HTTP request when the remote service provides no status or deduplication — _Rationale: its outcome after a disconnect cannot be proven._
- Approval/allow-list for load targets — _Rationale: whatever is checked in is trusted._

## 3. User Personas
- **Workflow Author**: engineer writing single-file C# workflows with the SDK; high technical level.
- **Operator**: triggers, schedules, aborts, retries runs; watches live state; medium–high.
- **Viewer / Lead**: browses by team/type/metadata, reads history, trends, and reports; low–medium.
- **Platform Admin**: core team; manages engine, machines, retention, deployments; high.
- **Service Team Engineer (phase 2)**: writes C# request/response hooks, picks machines, runs tests; high.
- **Agent (system actor)**: machine process executing assigned tasks and reporting state.

| Role | Permissions | Key Actions |
|------|-------------|-------------|
| Author | Register/version/clone workflows in owned team types | Write script, run tests, merge, view versions |
| Operator | Trigger/abort/re-run; manage user-defined schedules in permitted types | Run now, schedule, abort, re-run |
| Viewer | Read-only within permitted teams | Browse, filter, view runs, logs, reports, trends |
| Admin | Full, all teams | Machines, retention, rollback, audit, deployments |
| Service Team | Register own hooks; reserve machines from permitted pools; run phase 2 tests | Write hooks, run perf/parity tests |
| Agent | Execute assigned tasks only | Heartbeat, run tasks, stream logs/metrics |

## 4. Requirements (MoSCoW)
Priority is set per requirement. A few discovery items marked _Should_ are promoted into a _Must_ requirement
because the requirement cannot ship without them: FR-013 (cleanup nodes release machines), FR-020 (node
outputs), FR-030 (retention, confirmed 90 days), FR-032 (graph view), FR-043 (SemVer rules), FR-059 (team
presentation, needed for team isolation), FR-112 (perf thresholds).

| ID | Requirement | Priority | Problem it solves | Milestones | Est. AI |
|----|-------------|----------|-------------------|------------|---------|
| REQ-001 | Authors define workflows as single-file C# scripts with the SDK | Must | No common way to express multi-step, dependency-aware goals in code | M-001.1, M-001.2 | 05:35 |
| REQ-002 | Durable, fast, fault-tolerant execution engine on Windows and Linux agents | Must | Runs get stuck, lost, or slow; no dependency handling | M-002.1, M-002.2, M-002.3 | 11:40 |
| REQ-003 | On-demand and recurring triggers (code-defined and user-defined) | Must | Runs cannot be started with chosen variables or on a reliable schedule | M-003.1, M-003.2, M-003.3 | 05:25 |
| REQ-004 | Every workflow/config issue is caught by unit tests before check-in | Must | Errors found only at registration or at runtime | M-004.1, M-004.2 | 03:30 |
| REQ-005 | Semantic versioning, automatic registration, instant activation, cloning | Must | Changes are untraceable and slow to go live | M-005.1, M-005.2 | 03:20 |
| REQ-006 | Live status of runs, stages, nodes, tasks, and logs | Must | Users cannot see progress without refreshing or digging into machines | M-006.1, M-006.2 | 03:35 |
| REQ-007 | Team-isolated catalogue organised by type and metadata | Must | Types duplicate, teams' content mixes, workflows are hard to find | M-007.1, M-007.2 | 03:25 |
| REQ-008 | Run history and execution metrics | Must | No auditable history; no data to find slow or failing parts | M-008.1, M-008.2 | 03:30 |
| REQ-009 | Multiple reports per run and large artifacts | Must | Task results cannot be reviewed in the UI; raw data has nowhere to go | M-009.1, M-009.2 | 04:15 |
| REQ-010 | SSO, team-scoped RBAC, secrets, audit, public API | Must | Anyone could operate anything; secrets could leak; changes untraceable | M-010.1, M-010.2 | 03:20 |
| REQ-011 | Zero-downtime operations, local/cloud parity, retention | Must | Deploys interrupt users/runs; cloud not reproducible locally; storage grows unbounded | M-011.1, M-011.2, M-011.3 | 05:15 |
| REQ-012 | Recovery: re-run from failed node, workflow-version rollback, missed-fire catch-up | Should | Long workflows re-run end to end; bad changes and outages need manual repair | M-012.1, M-012.2, M-012.3 | 02:20 |
| REQ-013 | Trends and metric dashboards | Should | Slow or flaky parts are invisible over time | M-013.1 | 01:35 |
| REQ-014 | Run completion notifications | Should | Users must watch the UI to learn outcomes | M-014.1 | 00:55 |
| REQ-015 | Machine registry, capacity, selection, exclusive reservation (phase 2) | Must | Tests cannot target machines; shared machines skew results | M-015.1, M-015.2 | 03:30 |
| REQ-016 | Pluggable transport/codec and versioned service hooks (phase 2) | Must | Each new protocol or service needs core changes | M-016.1, M-016.2 | 03:45 |
| REQ-017 | Request sources: schema generation and file replay via connectors (phase 2) | Must | No requests to send without hand-crafted data | M-017.1, M-017.2 | 03:50 |
| REQ-018 | Performance testing (phase 2) | Must | No reliable way to drive and judge target load | M-018.1, M-018.2 | 03:40 |
| REQ-019 | Response-parity testing (phase 2) | Must | Response regressions between versions go undetected | M-019.1 | 02:15 |
| REQ-020 | Two dummy services, sample hooks, reference workflows (phase 2) | Must | No end-to-end proof or onboarding reference | M-020.1 | 01:45 |
| REQ-021 | Perf/parity comparison against a baseline run (phase 2) | Should | Regressions versus earlier runs are not visible | M-021.1 | 00:55 |
| REQ-022 | Per-team quotas on machines and load rate (phase 2) | Could | One team can monopolise shared machines | M-022.1 | 00:50 |
| REQ-023 | Step-load "find max" mode (phase 2) | Could | A service's max sustainable rate is unknown | M-023.1 | 00:55 |
| REQ-024 | Recorded live traffic as a request source (phase 2) | Could | Synthetic/file requests may not reflect real traffic | M-024.1 | 01:00 |
| **Total** | | | | | **80:05** |

### Won't Have
- Drag-and-drop workflow editor — _Rationale: code-first by design._
- Third-party orchestrator as engine — _Rationale: build our own._
- Machine provisioning/autoscaling — _Rationale: register existing machines only._
- Load-target approval workflow — _Rationale: code is trusted._
- Cross-region active-active — _Rationale: no stated need._

## 5. Requirement Breakdown
> Requirement → Milestone → Feature → User Story. Exit-criteria test time (00:15) is included in each milestone estimate.

### REQ-001: Workflows as single-file C# scripts (Must Have)
- **Outcome**: An author writes one `.cs` file importing the SDK that declares type, metadata, typed variables, lifecycle stages, and a node DAG with series/parallel tasks and failure policies, and runs it locally with identical behaviour to the hosted engine.
- **Traces**: FR-001–005, FR-020, FR-025, FR-033, FR-036, FR-038.
- **Est. AI**: 05:35

#### Milestone M-001.1: Express a workflow in code
- **Problem solved**: Authors have no API to declare a dependency-aware, parameterised workflow.
- **Est. AI**: 03:30

##### FEAT-001.1.1: Workflow definition API
Fluent C# API for lifecycle stages, nodes, dependencies, and tasks in series or parallel.
- **Est. AI**: 01:15

- **US-001** — **As an** Author **I want** to declare nodes, dependencies, and tasks (series or parallel) in C# **So that** my workflow's structure lives in code (`Est. AI: 00:45`)
  - [ ] Node with `DependsOn(...)` is part of a DAG; a cycle or unknown dependency fails definition with the offending node names.
  - [ ] A node's tasks run in declared order (series) or concurrently (parallel) with an optional max-parallelism.
  - [ ] Node and task keys are unique within a workflow; duplicates fail with the duplicate key.
- **US-002** — **As an** Author **I want** lifecycle stages (system init → pre-setup → tasks → post-process → system post-process) **So that** setup and cleanup are structured (`Est. AI: 00:30`)
  - [ ] Stages execute in that order; post-process and system post-process run even when earlier stages fail or the run is aborted.
  - [ ] Each stage's state is reported like a node (FR-033).

##### FEAT-001.1.2: Variables, metadata, and type
- **Est. AI**: 00:50

- **US-003** — **As an** Author **I want** typed variables with defaults and required flags **So that** each run gets a validated variable set (`Est. AI: 00:30`)
  - [ ] Supported types: string, int, long, double, bool, enum, string list, duration, secret reference.
  - [ ] A trigger with a missing required variable or wrong type is rejected before the run is created, naming the variable.
  - [ ] Variables are readable from every stage, node, and task.
- **US-004** — **As an** Author **I want** to declare the workflow's type and metadata **So that** it is organised in the UI (`Est. AI: 00:20`)
  - [ ] Exactly one type is required; metadata is key/value and is copied onto every run.

##### FEAT-001.1.3: Failure policies and node outputs
- **Est. AI**: 01:10

- **US-005** — **As an** Author **I want** to mark a task or node failure as hard or soft **So that** I control what a failure stops (`Est. AI: 00:40`)
  - [ ] Hard (default): node/run fails, dependents become Skipped, not-yet-started parallel siblings are cancelled.
  - [ ] Soft: entity ends SoftFailed (warning); dependents and siblings continue; the run can still succeed with warnings.
  - [ ] Dependency conditions on-success (default), on-failure, and always are supported (FR-013).
- **US-006** — **As an** Author **I want** a node's outputs to be inputs of dependent nodes **So that** data flows through the DAG (`Est. AI: 00:30`)
  - [ ] Outputs are typed key/values ≤ 64 KB per node; larger data must be an artifact (error otherwise).
  - [ ] A dependent reading an output of a non-dependency fails definition validation.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Unit tests**: FEAT-001.1.1–001.1.3 tests green (US-001–US-006 criteria, including cycles, duplicate keys, all policy combinations); ≥ 90% line coverage on the definition API.
- [ ] **Unit tests (cross-OS)**: same suite green on Windows and Linux CI runners.
- [ ] **Manual / UAT**: Author writes scenario A (discovery §6) as a script with no SDK workaround; sign-off from tech lead.

#### Milestone M-001.2: Run a workflow locally with built-in tasks
- **Problem solved**: Authors cannot execute their workflow without a hosted environment.
- **Est. AI**: 02:05

##### FEAT-001.2.1: Built-in task types
- **Est. AI**: 00:45

- **US-007** — **As an** Author **I want** built-in HTTP-call, command/script, and SDK-function tasks **So that** common steps need no custom plumbing (`Est. AI: 00:45`)
  - [ ] HTTP task: method, URL, headers, body, expected status; response captured as output.
  - [ ] Ordinary HTTP calls stay synchronous; opt-in remote-operation mode is covered by US-112.
  - [ ] Command task: executable + args, working directory, env; exit code ≠ 0 fails the task; works on Windows and Linux.
  - [ ] SDK-function task: async C# delegate receiving a context (variables, outputs, logger, cancellation token, report/artifact publisher).

##### FEAT-001.2.2: Local run and describe mode
- **Est. AI**: 01:05

- **US-008** — **As an** Author **I want** `dotnet run workflow.cs -- --var env=dev` to execute my workflow locally **So that** I can try it before registering (`Est. AI: 00:40`)
  - [ ] Uses the same engine core as the hosted engine (same state transitions, policies, and stage order).
  - [ ] Prints live node/task state and a final terminal status; process exit code 0 only on Succeeded.
  - [ ] `--engine <url>` submits the run to a local or remote engine instead.
- **US-009** — **As the** platform **I want** `--describe` to emit the workflow manifest as JSON without running tasks **So that** validation, registration, and the UI can read the DAG (`Est. AI: 00:25`)
  - [ ] Manifest includes SDK version, type, metadata, variables schema, stages, nodes, tasks, dependencies, policies, timeouts, retries.
  - [ ] Completes in < 1 s for a 200-node workflow (pre-compiled).

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Unit tests**: FEAT-001.2.1 task types green on Windows and Linux (HTTP against an in-process test server; command with exit codes).
- [ ] **Integration tests**: US-008 local run of scenario A produces the same event sequence as the engine core test fixture (golden file).
- [ ] **Performance**: US-009 describe on a 200-node workflow < 1 s.

### REQ-002: Durable, fast, fault-tolerant execution engine (Must Have)
- **Outcome**: Runs execute their DAG on Windows and Linux agents with dispatch p95 ≤ 50 ms, survive the loss of any single component, and always reach exactly one terminal status.
- **Traces**: FR-012–015, FR-017, FR-037, FR-061, FR-062, FR-063, FR-064, FR-102; NFR-003, NFR-004, NFR-028–NFR-034.
- **Est. AI**: 11:40

#### Milestone M-002.1: Dependency-aware execution core
- **Problem solved**: No deterministic engine executes a DAG with policies, retries, timeouts, and abort.
- **Est. AI**: 03:10

##### FEAT-002.1.1: Event-sourced run state machine
- **Est. AI**: 01:20

- **US-010** — **As an** Operator **I want** every run to end in exactly one of Succeeded, Failed, Aborted, TimedOut **So that** outcomes are unambiguous (`Est. AI: 00:40`)
  - [ ] Every state change is an appended event with a per-run sequence number; current state is rebuildable from events.
  - [ ] No entity can move from a terminal state; invalid transitions are rejected and logged.
- **US-011** — **As an** Author **I want** nodes to start only when their dependencies satisfy the dependency condition **So that** order is respected (`Est. AI: 00:40`)
  - [ ] Independent branches run concurrently; downstream of a hard failure is Skipped; `always` nodes still run.
  - [ ] A 200-node workflow of no-op tasks completes in ≤ 3 s end to end on the engine core (NFR-030).

##### FEAT-002.1.2: Retries, timeouts, abort
- **Est. AI**: 01:35

- **US-012** — **As an** Author **I want** retries and timeouts on every task, with workflow and node defaults **So that** transient failures self-heal and hangs end (`Est. AI: 00:50`)
  - [ ] Set in the script at workflow (`.Defaults`), node, or task level; the innermost value wins; the resolved policy per task is in the manifest and shown in the UI.
  - [ ] Retry policy: max retries (0–10), fixed or exponential backoff with ±20% jitter and a max delay (≤ 10 min), and which failure kinds retry (default: TaskError, Timeout). Validation errors, aborts, resource-limit kills, and errors the task marks non-retryable are never retried.
  - [ ] Defaults when nothing is set: 0 retries, 1 h per attempt, no separate node timeout, 24 h per run; post-process stages default to 15 min.
  - [ ] Timeouts per attempt, per node (all its tasks and retries), and per run. On timeout the attempt gets a cancellation signal, then is killed after the grace period (default 30 s); it ends `TimedOut` and is retried or fails per its failure policy (hard/soft). The task host persists the absolute attempt deadline and enforces it itself (cancel, then self-terminate after the grace period) even while the agent or engine is down; the agent and engine enforce it redundantly. Run timeout ends the run `TimedOut` after post-process stages run.
  - [ ] Platform faults (`AgentLost`, `PlatformError`) are re-placed on another eligible agent up to 2 times without using the author's retries, unless the task is marked `.NoAutoRetry()` (non-idempotent work).
  - [ ] Each attempt is recorded with agent, start/end, and failure reason; the task shows `Retrying` with "attempt 2/4, next in 12 s" live. Backoff waits hold no agent slot; a retry may run on another agent unless affinity pins it.
  - [ ] Deadlines and retry timers are stored, so they survive engine failover; a retry that cannot start before its node or run deadline is not scheduled; abort cancels pending retries immediately.
  - [ ] The validator rejects retries > 10, backoff > 10 min, or a task timeout above its node or run timeout, and warns when worst-case attempts exceed the run timeout. Dry-run tests use a virtual clock, so timeout and retry tests take no real time.
- **US-013** — **As an** Operator **I want** to abort a queued or running run with a reason **So that** I stop wasted work and everyone knows who stopped it and why (`Est. AI: 00:45`)
  - [ ] Abort is available from the UI, the API (`POST /runs/{id}/abort`) and the CLI (`wf abort <runId> --reason "…"`); the reason is mandatory (1–500 chars); requires Operator on the workflow's type or Admin, otherwise 403.
  - [ ] The run records who aborted it (principal id and display name), when (UTC, time accepted), the reason, and the source (UI/API/CLI/kill switch); the record is immutable and is written to the run event log and the audit trail.
  - [ ] The run moves to `Aborting` immediately (visible live ≤ 1 s); running tasks receive a cancellation signal (CancellationToken; for processes: graceful signal then kill after grace period, default 30 s); not-started nodes become Skipped.
  - [ ] Post-process stages and `always` nodes still run (bounded by their timeouts) and can read the abort details from the context; reservations are released; the run ends `Aborted` with `ended_at` = actual stop time.
  - [ ] A queued run goes straight to `Aborted` with no tasks dispatched.
  - [ ] Idempotent: a second abort while `Aborting` returns 202 with the original record (first abort wins); aborting a terminal run returns 409 with its status; an abort survives engine failover.
  - [ ] Aborting a scheduled run does not pause its schedule; an aborted run can be re-run.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Unit tests**: state machine transition table fully covered (100% of transitions, property-based tests over random DAGs ≤ 200 nodes: every run reaches one terminal state; aborts injected at random points (queued, mid-task, during post-process, twice) always end `Aborted` with exactly one abort record).
- [ ] **Unit tests**: US-012 retry/timeout suite on a virtual clock: policy inheritance, backoff and jitter bounds, retry-on kinds, non-retryable errors, platform re-placement budget and `.NoAutoRetry()`, node/run deadlines, abort during backoff, deadlines and retry timers restored after a simulated failover: all green.
- [ ] **Performance**: BenchmarkDotNet engine-core benchmark: per-node overhead p95 ≤ 10 ms; 200-node no-op workflow ≤ 3 s; result stored as CI baseline (regression > 10% fails build).
- [ ] **Unit tests (cross-OS)**: green on Windows and Linux.

#### Milestone M-002.2: Distributed execution on agents
- **Problem solved**: Tasks must run on remote Windows/Linux machines quickly and without starving each other.
- **Est. AI**: 03:30

##### FEAT-002.2.1: Agent service
- **Est. AI**: 01:05

- **US-014** — **As an** Admin **I want** to install an agent as a Windows Service or systemd unit **So that** the machine can run tasks (`Est. AI: 00:40`)
  - [ ] Agent registers with OS, cores, RAM, labels, and version; appears Healthy in the machine list within 5 s.
  - [ ] Missed heartbeats for 15 s → agent Unhealthy; no new dispatch to it.
- **US-015** — **As an** Admin **I want** to drain an agent before machine maintenance **So that** a reboot fails no running tasks (`Est. AI: 00:25`)
  - [ ] Draining agent accepts no new tasks; the machine is released for maintenance after its running tasks finish (or a configured max drain time, after which tasks are retried elsewhere, resuming from checkpoints). Agent binary updates do not need a drain, because running tasks survive an agent restart (US-109).

##### FEAT-002.2.2: Fast dispatch with warm task hosts and resource limits
- **Est. AI**: 01:25

- **US-016** — **As an** Operator **I want** dependent tasks to start immediately **So that** runs finish fast (`Est. AI: 00:45`)
  - [ ] Dependencies satisfied → task running on a warm agent: p95 ≤ 50 ms, p99 ≤ 200 ms (NFR-028).
  - [ ] Trigger → first task running p95 ≤ 1 s for a pre-compiled version (NFR-029); no per-run compilation.
- **US-017** — **As an** Admin **I want** per-task CPU/RAM limits **So that** one task cannot starve others (`Est. AI: 00:40`)
  - [ ] Limits enforced via Job Objects (Windows) and cgroups v2 (Linux); exceeding RAM limit fails the task with reason `ResourceLimit`.
  - [ ] Idle agent ≤ 1% CPU, ≤ 100 MB RAM; per-task harness overhead ≤ 50 MB (NFR-032). Warm task hosts form one globally capped LRU pool (process count and RSS) that is released when the agent is idle.

##### FEAT-002.2.3: Run-time task placement
- **Est. AI**: 00:45

- **US-107** — **As an** Author **I want** each task placed on a suitable machine at run time, with optional selectors (OS, labels, pool, machine names, variable-driven) and affinity (none, same node, whole run, same machine as another node) **So that** runs use the fleet in parallel yet tasks that share local state stay together (`Est. AI: 00:45`)
  - [ ] Default: each task attempt goes to a healthy, non-draining, selector-matching agent with a free slot, preferring one with a warm host for the run, then the least loaded.
  - [ ] Affinity pins hold for the run; after agent loss the task is re-placed and the pin moves.
  - [ ] No eligible machine → task stays in state `Ready` with reason `WaitingForMachine`, shown live; after the placement timeout (default 10 min) it fails with category `Placement`.
  - [ ] Testing override: a run or re-run can be forced onto a single machine (auto-picked or named) from the UI, API, or CLI without changing the workflow (a re-run without `placement` keeps the original run's override); unsatisfiable selectors or multi-machine reservations are rejected at trigger with the conflicting nodes listed; the override is shown on the run.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: control plane + 2 agents (1 Windows, 1 Linux) run scenario A end to end; agent drain test fails 0 tasks.
- [ ] **Integration tests**: US-107 placement suite on 3 simulated agents: selectors, each affinity mode, reservation exclusion, `Ready(reason=WaitingForMachine)` → `Failed(category=Placement)` timeout, re-placement after agent kill, single-machine override (all tasks on one agent; unsatisfiable trigger → 400): all green.
- [ ] **Performance**: dispatch latency p95 ≤ 50 ms / p99 ≤ 200 ms over 10k dispatches; run start p95 ≤ 1 s; agent footprint within NFR-032.
- [ ] **Unit tests**: resource-limit enforcement tests green on both OSes.

#### Milestone M-002.3: Fault tolerance
- **Problem solved**: Loss of an engine instance, agent, machine, or remote-operation response loses or stalls runs.
- **Est. AI**: 05:00

##### FEAT-002.3.1: Highly available control plane
- **Est. AI**: 01:15

- **US-018** — **As an** Operator **I want** runs to continue when an engine instance dies **So that** no work is lost (`Est. AI: 00:45`)
  - [ ] ≥ 2 control-plane instances; leader failover and run resumption ≤ 30 s; after a DB failover, resumption ≤ the database's failover time + 30 s; no run state lost (NFR-033, NFR-034).
- **US-019** — **As the** platform **I want** duplicate task results to be applied once **So that** state stays correct under retries and reconnects (`Est. AI: 00:30`)
  - [ ] Each task attempt has an idempotency key; a duplicated completion message is acknowledged and ignored.

##### FEAT-002.3.2: Agent restart, agent loss, and long-task resume
- **Est. AI**: 02:45

- **US-020** — **As an** Operator **I want** tasks on a lost agent to be retried or failed with a reason **So that** runs never hang (`Est. AI: 00:35`)
  - [ ] Agent lost (no heartbeat for 30 s) → its running attempts end `AgentLost` and are re-placed on another eligible agent (up to 2 platform retries, not counted against the author's retries, unless `.NoAutoRetry()`), else failed with reason.
  - [ ] Timeline from the agent's last heartbeat: 15 s → agent Unhealthy (no new dispatch); its attempts stay `Running` and reclaimable by a restarting agent until 30 s; only then do they end `AgentLost`. A new engine leader applies the same per-attempt deadlines (no separate wait), so runs resume ≤ 30 s (NFR-033).
- **US-109** — **As an** Operator **I want** running tasks to keep running when the agent process crashes, restarts, or is updated **So that** multi-hour tasks such as load tests are not lost (`Est. AI: 00:45`)
  - [ ] Task hosts run outside the agent's process lifetime (Windows: own job object, not killed with the agent; Linux: own cgroup, `KillMode=process`) and keep their results and logs in the attempt directory until acknowledged.
  - [ ] A restarted agent re-adopts live task hosts within 10 s (pid + start-time check; well inside the 30 s reclaim window), reports them as in flight, and the engine keeps the attempts `Running`; no result, log line, or perf sample is lost or duplicated.
  - [ ] Hosts that died while the agent was down end `AgentLost` and follow platform retry (US-020).
- **US-110** — **As an** Author **I want** to checkpoint a long task **So that** after a machine reboot or loss the retry resumes where it left off (`Est. AI: 00:40`)
  - [ ] `await ctx.Checkpoint.SaveAsync(state)` stores JSON state (≤ 1 MB, at most once every 5 s) durably outside the machine; the agent journals the checkpoint record until the engine acknowledges it (highest sequence wins), so the latest checkpoint per task survives engine, agent, and machine failure.
  - [ ] A new attempt reads it with `ctx.Checkpoint.TryLoad<T>()` and `ctx.Attempt.ResumedFrom`; tasks without checkpoints restart from the beginning (default).
  - [ ] The UI shows "resumed from checkpoint at 01:42:10" on the attempt; dry-run mocks can supply a checkpoint so resume logic is unit-testable.
  - [ ] Checkpoints are deleted with the run (retention) and never contain secret values (validated by the secret-leak test).
- **US-112** — **As an** Author **I want** an opt-in HTTP task that safely tracks a long-running remote job **So that** retries and machine loss do not silently start duplicate work (`Est. AI: 00:45`)
  - [ ] A 202 response supplies a status URL (or an operation ID with a declared status URL template); the task records it durably, checks status at the service's suggested interval (bounded backoff otherwise), and completes with the remote result or fails with its error. An immediate terminal response completes normally. Individual HTTP calls time out within the remaining attempt deadline; the task obeys attempt, node, and run timeouts without holding the submit connection open.
  - [ ] Before POST, durably record the intent and a stable key for the logical run and task, unchanged across attempts; after acceptance record the operation reference. After machine loss, the next attempt resumes the recorded operation. If the response was lost before that reference was saved, it re-submits only when the remote service deduplicates the same key throughout the retry window; otherwise it looks up an accepted job by key if supported.
  - [ ] Without remote deduplication, a missing lookup result never triggers another POST: lookup-only services can reattach to a found job on platform retry, but otherwise report `RemoteOutcomeUnknown` for operator reconciliation. When the service supports neither deduplication nor lookup, require `.NoAutoRetry()` and zero author retries. A missing/expired status endpoint is reported, not treated as success. Internal run idempotency alone is insufficient.
  - [ ] Abort or timeout requests remote cancellation if configured; only a remote confirmation is shown as remotely cancelled. Otherwise the task is locally cancelled/timed out and the remote outcome remains unknown. Run System Events show submit/wait/terminal/cancel transitions by task attempt; polling diagnostics stay in attempt logs, with no secrets in either view.

##### FEAT-002.3.3: Chaos suite
- **Est. AI**: 00:45

- **US-021** — **As an** Admin **I want** an automated fault-injection suite **So that** reliability claims are proven on every change (`Est. AI: 00:45`)
  - [ ] Scenarios: kill leader mid-run, kill agent process mid-task (task keeps running), restart agent for an update mid-task, kill task host, reboot a machine mid-task with a checkpointing task, network partition control plane ↔ DB and ↔ agent, DB failover, disk full on agent.
  - [ ] Each scenario asserts: every run reaches a terminal state, no duplicate state transitions, resumption ≤ 30 s (agent killed and not restarted: `AgentLost` and re-placed ≤ 30 s after its last heartbeat; DB failover: ≤ the database's failover time + 30 s).

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests (chaos)**: US-021 suite green in CI nightly and on every engine change; 0 stuck runs over 1,000 randomised runs with injected faults.
- [ ] **Unit tests**: idempotency tests for duplicated, reordered, and late messages green.
- [ ] **Integration tests**: 1 h checkpointing task: leader killed, agent process killed, and the machine rebooted once each → task completes; agent kill loses 0 s of work; reboot resumes from the last checkpoint (≤ 10 s of work lost); exactly one result.
- [ ] **Integration tests**: US-112 stub remote service: 202 → pending → success/failure, host loss after `SubmissionPending` but before POST (lookup miss remains unknown), lost submit response (dedup reattaches; lookup-only found resumes), host/machine loss after `RemoteSubmitted`, expiring status, abort and timeout with/without remote cancel → no duplicate remote job or blind retry; the System Events and logs remain separate. An agent slot remains occupied while waiting.
- [ ] **Manual / UAT**: Admin kills the leader during a live demo run; run completes; sign-off from platform lead.

### REQ-003: On-demand and recurring triggers (Must Have)
- **Outcome**: Runs start on demand, from history ("Run now"), from UI-managed schedules, or from code-defined schedule files, firing on time and exactly once.
- **Traces**: FR-007–010, FR-034, FR-035, FR-039, FR-045, FR-054; NFR-002.
- **Est. AI**: 05:25

#### Milestone M-003.1: On-demand triggering
- **Problem solved**: Users cannot start a run with a chosen variable set.
- **Est. AI**: 01:15

##### FEAT-003.1.1: Trigger via UI and API
- **Est. AI**: 01:00

- **US-022** — **As an** Operator **I want** to trigger a workflow with a variable set from the UI or API **So that** I run it when needed (`Est. AI: 00:35`)
  - [ ] Form generated from the variables schema with defaults pre-filled; invalid values shown inline before submit.
  - [ ] Trigger API p95 < 100 ms (NFR-018); returns 202 with a preallocated run ID; optional idempotency key prevents duplicate runs on client retry.
- **US-023** — **As an** Operator **I want** "Run now" on a recurring instance in its history **So that** I re-run it with the same variables (`Est. AI: 00:25`)
  - [ ] Uses the instance's effective variables and latest (or pinned) version; run joins the same run series with trigger = manual (FR-045).

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: trigger API contract tests (valid, invalid variables, idempotency key) green against PostgreSQL.
- [ ] **UI / E2E tests**: US-022, US-023 Playwright flows pass on Chromium, Firefox, WebKit.
- [ ] **Performance**: trigger API p95 < 100 ms at 50 req/s.

#### Milestone M-003.2: User-defined schedules
- **Problem solved**: Recurring runs need schedules managed in the UI that fire precisely and exactly once.
- **Est. AI**: 02:15

##### FEAT-003.2.1: Schedule management in UI
- **Est. AI**: 01:15

- **US-024** — **As an** Operator **I want** to create, edit, pause, resume, and delete cron or interval schedules with a timezone **So that** runs recur without manual triggers (`Est. AI: 00:45`)
  - [ ] Shows next 5 fire times before saving; DST transitions handled (no double/skipped fire beyond the cron's own semantics).
  - [ ] Edits take effect for the next fire; concurrent edits rejected via version check.
  - [ ] Available only in the Kardia UI (FR-035): the backing endpoints are absent from the public OpenAPI document, the SDK and the CLI, and accept only delegated user tokens issued to the Kardia SPA client (`azp` check); app-only and other-client tokens get 403. This is a product/contract boundary, not a security boundary: a user scripting the UI's token can do nothing beyond what their role already allows in the UI. Code-defined schedules and registration never create or modify user-defined schedules.
- **US-025** — **As an** Operator **I want** an overlap policy (skip, queue, allow concurrent) **So that** slow runs don't pile up (`Est. AI: 00:30`)
  - [ ] Default skip; skipped fires are recorded with reason in the series history.

##### FEAT-003.2.2: Precise, exactly-once firing
- **Est. AI**: 00:45

- **US-026** — **As an** Operator **I want** schedules to fire on time exactly once **So that** history is trustworthy (`Est. AI: 00:45`)
  - [ ] p99 trigger delay ≤ 2 s (NFR-002); one run per (schedule, fire time) even across leader failover.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Unit tests**: cron/interval/timezone/DST next-fire tests (incl. spring-forward/fall-back for 3 timezones); overlap policies.
- [ ] **Integration tests**: 1,000 schedules firing every minute for 1 h with leader killed twice: 0 duplicates, 0 silent misses, p99 delay ≤ 2 s.
- [ ] **UI / E2E tests**: US-024 flow; **Accessibility**: schedule form has zero axe critical/serious violations.

#### Milestone M-003.3: Code-defined schedules
- **Problem solved**: Recurring tests must be declared in code, scheduled by the system, and read-only in the UI.
- **Est. AI**: 01:55

##### FEAT-003.3.1: Schedule config file
- **Est. AI**: 01:00

- **US-027** — **As an** Author **I want** a JSON schedule file (with JSON Schema) holding categorised default variables and test instances with overrides **So that** recurring tests are code-controlled (`Est. AI: 00:40`)
  - [ ] Effective variables = defaults ← instance overrides; unknown variables or invalid values fail validation.
  - [ ] Each instance has a stable ID, schedule (cron/interval + timezone), optional version pin, overlap/catch-up policy.
- **US-028** — **As a** Viewer **I want** code-defined schedules shown read-only with their origin **So that** I know where to change them (`Est. AI: 00:20`)
  - [ ] Origin badge (code/UI) with file path and commit; edit controls absent; API returns 409 on mutation attempts.

##### FEAT-003.3.2: Atomic schedule sync on registration
- **Est. AI**: 00:40

- **US-029** — **As an** Author **I want** schedule file changes applied atomically **So that** no fire is missed or duplicated during a change (`Est. AI: 00:40`)
  - [ ] Add/remove/modify of instances applied in one transaction; removed instances stop; series history kept.
  - [ ] Schedule configs register independently of workflow versions: a schedule-file-only change records a new schedule config (source path, commit, content hash) without a workflow version bump.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Unit tests**: schedule file schema + override resolution tests, including categorised defaults.
- [ ] **Integration tests**: apply 50 schedule-file changes during continuous firing: 0 missed, 0 duplicated fires.
- [ ] **UI / E2E tests**: US-028 read-only rendering and 409 on mutation.

### REQ-004: Pre-check-in validation and testability (Must Have)
- **Outcome**: Every rule the engine enforces at registration is reproducible in `dotnet test`, and workflow logic is unit-testable offline in dry-run mode.
- **Traces**: FR-040–043; NFR-022.
- **Est. AI**: 03:30

#### Milestone M-004.1: Shared validator
- **Problem solved**: Script and config errors are found only at registration or at runtime.
- **Est. AI**: 02:05

##### FEAT-004.1.1: Validator package
- **Est. AI**: 01:10

- **US-030** — **As an** Author **I want** to call the validator from unit tests **So that** all issues are caught before check-in (`Est. AI: 00:45`)
  - [ ] Checks: DAG cycles, unknown dependencies, duplicate keys, variable types/required, cron/interval validity, schedule overrides referencing unknown variables or invalid values, type registry (duplicates, quotas, metadata schema), SemVer rules.
  - [ ] Errors include file, path, and fix hint; full validation of a typical workflow + schedule file < 10 s (NFR-022).
- **US-031** — **As the** platform **I want** registration to use the same validator **So that** local and server never drift (`Est. AI: 00:25`)
  - [ ] Server rejects invalid registration with the identical error list (FR-042); 100% of rejection rules have an offline test.

##### FEAT-004.1.2: SemVer bump rules
- **Est. AI**: 00:40

- **US-032** — **As an** Author **I want** breaking changes to require a MAJOR bump **So that** schedules and callers aren't silently broken (`Est. AI: 00:40`)
  - [ ] Variable removed/renamed/type-changed or node removed/renamed → MAJOR; additive variables or nodes → MINOR; implementation-only → PATCH; wrong bump fails validation with the required level. Schedule validation checks variable compatibility, not node references.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Unit tests**: one test per validation rule (positive + negative); rule catalogue test asserts every server rule has an offline test.
- [ ] **Performance**: validator on a 200-node workflow + 50-instance schedule file < 2 s; full typical suite < 10 s.

#### Milestone M-004.2: Dry-run test harness
- **Problem solved**: Workflow logic cannot be unit-tested without a running engine.
- **Est. AI**: 01:25

##### FEAT-004.2.1: Dry-run harness
- **Est. AI**: 01:10

- **US-033** — **As an** Author **I want** to run my workflow in dry-run with mocked task outcomes **So that** I can assert behaviour in `dotnet test` (`Est. AI: 00:45`)
  - [ ] Mock per task: succeed (with outputs), fail, time out, throw, fail N times then succeed.
  - [ ] Assertions on node order, final statuses, skipped nodes, retries, stage execution, outputs; uses the engine core (same semantics as production).
- **US-034** — **As an** Author **I want** a project template with sample tests and a CI gate **So that** new workflows start testable (`Est. AI: 00:25`)
  - [ ] `dotnet new wf-workflow` creates script, schedule file, tests; CI fails the PR if validation or tests fail.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Unit tests**: harness self-tests for every mock outcome and policy combination.
- [ ] **Integration tests**: template-generated project passes `dotnet test` on Windows and Linux in < 10 s.

### REQ-005: Versioning, registration, instant activation, cloning (Must Have)
- **Outcome**: A merged change is validated, compiled, registered, and active within 60 s with no restart; every version records who changed what; any version can be diffed and cloned.
- **Traces**: FR-006, FR-044, FR-053; NFR-027.
- **Est. AI**: 03:20

#### Milestone M-005.1: Automatic registration and hot activation
- **Problem solved**: Checked-in changes don't reach the engine quickly and safely.
- **Est. AI**: 01:55

##### FEAT-005.1.1: Publish pipeline
- **Est. AI**: 01:05

- **US-035** — **As an** Author **I want** merging to main to register my change automatically **So that** it is live within a minute (`Est. AI: 00:45`)
  - [ ] Webhook/CI → validate → compile once → upload package → register; active p95 ≤ 60 s after merge, ≤ 5 s after registration (NFR-027).
  - [ ] Records git commit, git author, registering identity, change note.
- **US-036** — **As an** Author **I want** `wf publish` to register to a local/dev engine **So that** I can test registration locally (`Est. AI: 00:20`)
  - [ ] Same validation and packaging as CI.

##### FEAT-005.1.2: Hot activation
- **Est. AI**: 00:35

- **US-037** — **As an** Operator **I want** in-flight runs to finish on their version while new runs use the new one **So that** changes never break running work (`Est. AI: 00:35`)
  - [ ] No component restarts; agents fetch new packages on demand and cache them by content hash.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: register v1.1 while 20 runs of v1.0 are active: all finish on v1.0, next run uses v1.1; activation ≤ 5 s after registration.
- [ ] **Performance**: merge → active p95 ≤ 60 s over 20 test merges.

#### Milestone M-005.2: Version history and cloning
- **Problem solved**: Changes are untraceable and authors cannot branch from an old version.
- **Est. AI**: 01:25

##### FEAT-005.2.1: Version history and diff
- **Est. AI**: 00:40

- **US-038** — **As an** Author **I want** to view versions with authors and change notes and diff any two **So that** I understand what changed (`Est. AI: 00:40`)
  - [ ] Diff of manifest (nodes, variables, policies) and script source; each run links to its version.

##### FEAT-005.2.2: Clone from version
- **Est. AI**: 00:30

- **US-039** — **As an** Author **I want** to clone any version into a new workflow **So that** I can experiment safely (`Est. AI: 00:30`)
  - [ ] Generates a new script file + schedule stub (or a PR) with lineage `clonedFrom = <workflow>@<version>`; original untouched.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: version list/diff/clone API tests green.
- [ ] **UI / E2E tests**: US-038, US-039 flows pass; **Accessibility**: zero axe critical/serious on version pages.

### REQ-006: Live status and logs (Must Have)
- **Outcome**: Users see run, stage, node, and task state and logs live (p95 ≤ 1 s) with no polling, including after reconnects.
- **Traces**: FR-018, FR-019, FR-032, FR-064; NFR-001, NFR-009, NFR-019, NFR-020.
- **Est. AI**: 03:35

#### Milestone M-006.1: Live state push
- **Problem solved**: Users cannot see progress without refreshing.
- **Est. AI**: 02:20

##### FEAT-006.1.1: Snapshot + delta stream with resync
- **Est. AI**: 01:20

- **US-040** — **As an** Operator **I want** state changes pushed to my browser **So that** I see progress as it happens (`Est. AI: 00:45`)
  - [ ] State change → UI p95 ≤ 1 s, p99 ≤ 2 s (NFR-001); no periodic polling.
  - [ ] Sustains ≥ 10k events/s fan-out to ≥ 500 clients without dropped updates (coalesced per entity) (NFR-019).
  - [ ] A separate, read-only System Events view on each run shows ordered timestamps, the task attempt, selection of an agent and why, dispatch/start/completion, retries and failures. Filter by node, task or attempt; entries remain available after the run finishes and reconnects.
  - [ ] For US-112, remote submit/wait/completion/failure/cancel transitions are visible without treating each poll or remote log as a system event.
- **US-041** — **As an** Operator **I want** the view to resync after reconnect **So that** I never see stale state (`Est. AI: 00:35`)
  - [ ] Client resumes from its last sequence number; if the gap is too large, reloads the snapshot; UI shows a reconnecting indicator.

##### FEAT-006.1.2: Live run graph and task plan
- **Est. AI**: 00:45

- **US-042** — **As an** Operator **I want** a read-only live DAG view **So that** I see where a run is (`Est. AI: 00:45`)
  - [ ] 200-node graph stays at 60 fps with 100 events/s; nodes keyboard-navigable; status conveyed by icon + text, not colour alone.
  - [ ] From the workflow and any run, a separate read-only Task Plan page shows the task's declared command or action, node dependencies and order, placement rules, retries and timeouts for the version that run used; resolved secret values never appear. For SDK-function tasks it shows the declared function, not invented internal commands.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: resync tests (drop connection mid-run, server restart) show 0 missed or out-of-order updates.
- [ ] **UI / E2E tests**: System Events distinguishes selected agent from planned placement, shows every attempt in order after retry/failover, and links to the correct historical Task Plan after a new version is published; secret values are absent.
- [ ] **Performance**: event latency p95 ≤ 1 s with 500 simulated clients at 10k events/s; graph 60 fps (Chrome performance trace) at 100 events/s.
- [ ] **Accessibility**: zero axe critical/serious violations on run view; graph has a list alternative.

#### Milestone M-006.2: Task logs and outputs
- **Problem solved**: Users can't see what a task is doing or did.
- **Est. AI**: 01:15

##### FEAT-006.2.1: Log streaming and storage
- **Est. AI**: 01:00

- **US-043** — **As an** Operator **I want** live and historical task logs with search and download **So that** I can diagnose failures (`Est. AI: 00:40`)
  - [ ] Live tail ≤ 1 s behind; virtualised viewer handles 1M lines; logs stored outside the main DB.
  - [ ] Logs are viewed by task attempt (including prior retries), separate from System Events; historical logs can be searched and downloaded after the run ends.
  - [ ] Structured outputs per task attempt (JSON ≤ 64 KB, larger fails the task with a clear error) are stored with the attempt and visible in the UI and API during and after execution (FR-019).
- **US-044** — **As an** Admin **I want** secret values masked in logs and outputs **So that** secrets never leak (`Est. AI: 00:20`)
  - [ ] Values resolved from secret references are replaced by `***` before leaving the agent.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: log streaming from Windows and Linux agents; masking test with known secret values (0 occurrences in stored logs); task outputs visible via UI and API, > 64 KB rejected.
- [ ] **Performance**: 1M-line log opens < 1 s and scrolls at 60 fps.

### REQ-007: Team-isolated catalogue organised by type and metadata (Must Have)
- **Outcome**: Each team has its own UI area (team → type → workflows) laid out per its presentation config; types are unique; users find workflows and runs fast by metadata.
- **Traces**: FR-004, FR-021, FR-022, FR-057–FR-059; NFR-006.
- **Est. AI**: 03:25

#### Milestone M-007.1: Team and type registry
- **Problem solved**: Types get duplicated and different teams' content mixes.
- **Est. AI**: 01:50

##### FEAT-007.1.1: Type registry file with validation
- **Est. AI**: 01:05

- **US-045** — **As a** team lead **I want** to declare my team's types and their metadata schema in a checked-in registry file **So that** types are reviewed and consistent (`Est. AI: 00:35`)
  - [ ] Each type: owning team, name, description, metadata schema (required keys, allowed values) + optional free-form keys.
  - [ ] > 10 types per team (configurable quota) fails validation (FR-057).
- **US-046** — **As a** team lead **I want** near-duplicate types rejected **So that** the catalogue stays clean (`Est. AI: 00:30`)
  - [ ] Names compared after normalisation (case, spaces, `-`/`_`, punctuation, plural "s") and edit distance ≤ 2 within a team → rejected with the conflicting type; same name in another team allowed, shown with team prefix (FR-058).

##### FEAT-007.1.2: Per-team presentation config
- **Est. AI**: 00:30

- **US-047** — **As a** team lead **I want** to set type order, grouping keys, list columns, default filters, and labels for my area **So that** it fits how my team works (`Est. AI: 00:30`)
  - [ ] Config affects only that team's area; invalid keys (not in metadata schema) fail validation.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Unit tests**: registry validation (quota, duplicates incl. normalisation/edit-distance cases, metadata schema, presentation keys).
- [ ] **Integration tests**: registry change registered on merge and applied atomically.

#### Milestone M-007.2: Browse and filter
- **Problem solved**: Users can't find workflows and runs quickly.
- **Est. AI**: 01:35

##### FEAT-007.2.1: Team area and type views
- **Est. AI**: 00:35

- **US-048** — **As a** Viewer **I want** to navigate team → type → workflows with latest status **So that** I see only relevant content (`Est. AI: 00:35`)
  - [ ] A team's area never lists another team's types, workflows, or runs; reachable in ≤ 3 interactions from home.

##### FEAT-007.2.2: Filter, group, search
- **Est. AI**: 00:45

- **US-049** — **As a** Viewer **I want** to filter, group, and search by metadata, status, type, and time range **So that** I find what matters (`Est. AI: 00:45`)
  - [ ] p95 < 300 ms over 100k runs (server-side paging, virtualised lists) (NFR-006); filters persisted in the URL (shareable).

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **UI / E2E tests**: US-048 isolation test (team A user sees no team B data in UI or API); US-049 filter/group/search flows on Chromium, Firefox, WebKit.
- [ ] **Performance**: list/filter p95 < 300 ms on a 100k-run seeded DB.
- [ ] **Accessibility**: zero axe critical/serious violations; full keyboard navigation of lists and filters.
- [ ] **Manual / UAT**: usability check: 5 users find a target workflow in ≤ 3 interactions.

### REQ-008: Run history and execution metrics (Must Have)
- **Outcome**: Full history per workflow and run series, and timing, failure, latency, CPU, and RAM metrics for every run, stage, node, task, agent, and engine component.
- **Traces**: FR-023, FR-046; NFR-010.
- **Est. AI**: 03:30

#### Milestone M-008.1: Run history
- **Problem solved**: No auditable history per workflow or recurring series.
- **Est. AI**: 01:25

##### FEAT-008.1.1: History per workflow and run series
- **Est. AI**: 01:10

- **US-050** — **As a** Viewer **I want** run history per workflow and per series with status, duration, trigger, variables, version **So that** I can audit outcomes (`Est. AI: 00:40`)
  - [ ] Series history shows skipped/missed fires with reasons; paged, sortable, filterable.
  - [ ] Aborted runs show an `Aborted` badge with who, when, and reason in run lists, history and series views, and a banner on the run page; history filters by `Aborted` and by who aborted. The run page Abort button (Operator/Admin only) opens a confirm dialog with a required reason field.
- **US-051** — **As an** Operator **I want** a run detail timeline (stages, nodes, tasks, attempts, agent) **So that** I see exactly what happened (`Est. AI: 00:30`)
  - [ ] Gantt-style timeline with queue wait vs execution time per task.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: history API paging/filter tests on 100k runs.
- [ ] **UI / E2E tests**: US-050 (including abort: confirm with reason → banner and history show who/when/reason), US-051 flows; **Accessibility**: zero axe critical/serious.

#### Milestone M-008.2: Execution metrics
- **Problem solved**: There is no data to find slow, failing, or resource-hungry parts.
- **Est. AI**: 02:05

##### FEAT-008.2.1: Metric capture
- **Est. AI**: 01:20

- **US-052** — **As a** Viewer **I want** queue wait, start/end/duration, retries, terminal status, and failure category per run/stage/node/task **So that** I can improve workflows (`Est. AI: 00:40`)
  - [ ] Failure categories: TaskError, Timeout, Aborted, AgentLost, ResourceLimit, ValidationError, Placement, PlatformError.
- **US-053** — **As a** Viewer **I want** CPU and RAM per task process and per machine **So that** I can right-size work (`Est. AI: 00:40`)
  - [ ] Sampled every ≤ 5 s; summary (avg/max CPU, peak RAM) stored per task; series viewable per task.
  - [ ] Network bytes sent/received per task process and per machine, with a rate series, captured the same way (FR-046; required by phase 2 load tasks).

##### FEAT-008.2.2: OpenTelemetry export
- **Est. AI**: 00:30

- **US-054** — **As an** Admin **I want** engine, agent, and SDK metrics, logs, and traces via OpenTelemetry **So that** I can monitor the platform (`Est. AI: 00:30`)
  - [ ] Exported to Azure Monitor in Azure and to the local dashboard locally; metric retention ≥ 90 days.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: metrics recorded for a scenario A run on Windows and Linux agents; CPU/RAM and network-byte samples present at ≤ 5 s intervals.
- [ ] **Performance**: metric capture adds ≤ 1% CPU on agents.

### REQ-009: Multiple reports per run and large artifacts (Must Have)
- **Outcome**: Any stage, node, or task publishes up to 10 reports (≤ 10 MB each, mixed kinds) rendered inline and live, and up to 5 GB of downloadable artifacts per run.
- **Traces**: FR-048–FR-052, FR-056; NFR-023, NFR-024.
- **Est. AI**: 04:15

#### Milestone M-009.1: Publish and view reports
- **Problem solved**: Task results cannot be reviewed in the UI.
- **Est. AI**: 03:20

##### FEAT-009.1.1: SDK report publishing with limits
- **Est. AI**: 00:35

- **US-055** — **As an** Author **I want** to publish reports (kind, title, tags, payload) from any stage, node, or task **So that** reviewers see results (`Est. AI: 00:35`)
  - [ ] > 10 reports per run or > 10 MB per report (configurable per type) fails the publish call with a clear error, handled by the failure policy.

##### FEAT-009.1.2: Renderer registry and renderers
- **Est. AI**: 01:25

- **US-056** — **As a** Viewer **I want** Markdown, HTML, table, chart, image, JSON, text, diff, and PDF reports rendered inline **So that** I review without downloading (`Est. AI: 00:45`)
  - [ ] Reports ≤ 5 MB render < 1 s; larger load progressively without freezing the UI (NFR-023).
- **US-057** — **As an** Admin **I want** HTML/SVG reports sandboxed **So that** report content cannot attack users (`Est. AI: 00:20`)
  - [ ] Shown in a sandboxed iframe; content served with `Content-Security-Policy: sandbox allow-scripts; frame-ancestors 'self'` (no `allow-same-origin`, so it gets an opaque origin; only Kardia can frame it) and `X-Content-Type-Options: nosniff`, also when the content URL is opened directly. Only the report-content response omits `X-Frame-Options`; every other response keeps `DENY`.
- **US-058** — **As a** frontend developer **I want** to add a report kind as a renderer only **So that** no backend/SDK change is needed (`Est. AI: 00:20`)
  - [ ] Unknown kinds fall back to download.

##### FEAT-009.1.3: Report index and cross-run comparison
- **Est. AI**: 01:05

- **US-059** — **As a** Viewer **I want** a report index grouped by stage/node/task, filterable by kind/tag, updating live **So that** I find reports as they appear (`Est. AI: 00:30`)
  - [ ] Index loads p95 < 300 ms; new reports appear ≤ 1 s after publish.
- **US-060** — **As a** Viewer **I want** to compare the same report across runs in a series **So that** I spot changes (`Est. AI: 00:35`)
  - [ ] Side-by-side for any two runs; matched by report source + title.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Unit tests**: SDK limit tests; each renderer has component tests with sample payloads.
- [ ] **Security**: sandbox test — HTML report script cannot read Kardia cookies or storage or call the API (opaque origin), form submission and top navigation are blocked, also when the content URL is opened directly; CSP and `nosniff` headers verified.
- [ ] **Performance**: 5 MB table report renders < 1 s; 10 MB loads progressively with 60 fps scrolling.
- [ ] **UI / E2E tests**: US-056, US-059, US-060 flows; **Accessibility**: zero axe critical/serious on report views.

#### Milestone M-009.2: Large artifacts
- **Problem solved**: Large raw run data has nowhere to be stored or retrieved.
- **Est. AI**: 00:55

##### FEAT-009.2.1: Streamed artifact upload and download
- **Est. AI**: 00:40

- **US-061** — **As an** Author **I want** to store artifacts up to 5 GB per run via streaming **So that** response dumps and raw results are kept (`Est. AI: 00:40`)
  - [ ] Streamed (constant memory) upload/download ≥ 100 MB/s within the Azure region; stored in blob/disk, never the DB; checksum verified.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: 5 GB upload/download on both OSes with agent RAM growth ≤ 50 MB; 5 GB + 1 byte rejected.
- [ ] **Performance**: ≥ 100 MB/s in-region throughput.

### REQ-010: SSO, team-scoped RBAC, secrets, audit, public API (Must Have)
- **Outcome**: Only authenticated users act within their team/type roles; secrets are referenced, never stored or shown; every mutation is audited; everything in the UI is in the public API, except UI-only user-defined schedule mutations (FR-035).
- **Traces**: FR-026, FR-027, FR-029, FR-031; NFR-007, NFR-008.
- **Est. AI**: 03:20

#### Milestone M-010.1: Authentication and team-scoped RBAC
- **Problem solved**: Anyone could view or operate anything.
- **Est. AI**: 01:30

##### FEAT-010.1.1: Entra ID SSO
- **Est. AI**: 00:30

- **US-062** — **As a** user **I want** to sign in with corporate SSO **So that** I don't manage credentials (`Est. AI: 00:30`)
  - [ ] Unauthenticated API calls return 401; agents authenticate with machine credentials, not user tokens.

##### FEAT-010.1.2: Team/type-scoped roles
- **Est. AI**: 00:45

- **US-063** — **As an** Admin **I want** Author/Operator/Viewer/Admin roles scoped by team and type **So that** access matches ownership (`Est. AI: 00:45`)
  - [ ] Enforced in the API (403 with reason); UI hides actions the user lacks; live updates respect scope.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: authorization matrix test (every endpoint × every role × in/out of scope).
- [ ] **Security**: no high/critical findings in dependency scan + SAST (CodeQL).

#### Milestone M-010.2: Secrets, audit, and public API
- **Problem solved**: Secrets could leak and changes are untraceable or UI-only.
- **Est. AI**: 01:50

##### FEAT-010.2.1: Secret references
- **Est. AI**: 00:35

- **US-064** — **As an** Author **I want** to reference secrets by name **So that** values are never stored in definitions, variables, metadata, or logs (`Est. AI: 00:35`)
  - [ ] Resolved on the agent at use time from Key Vault (Azure) or the local secret store; never persisted by the control plane.

##### FEAT-010.2.2: Audit trail
- **Est. AI**: 00:30

- **US-065** — **As an** Admin **I want** every mutating action audited **So that** I know who did what and when (`Est. AI: 00:30`)
  - [ ] Actor, action, target, before/after summary, timestamp; retained ≥ 1 year; filterable in UI.

##### FEAT-010.2.3: Public API
- **Est. AI**: 00:30

- **US-066** — **As an** automation engineer **I want** every UI capability in a documented, versioned API **So that** CI and phase 2 can automate it (`Est. AI: 00:30`)
  - [ ] OpenAPI document published; contract test asserts every UI action maps to a public API operation, except the UI-only user-defined schedule mutations (create, edit, pause, resume, delete; FR-035), which the test asserts are absent from the document and the CLI. Schedule reads (list, history) are public.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: audit coverage test (100% of mutating endpoints produce an audit record); secret-leak test (DB, logs, events scanned for test secret values: 0 hits).
- [ ] **Security**: OWASP ASVS L2 checklist for authn/authz/secrets reviewed; no high/critical SAST findings.

### REQ-011: Zero-downtime operations, local/cloud parity, retention (Must Have)
- **Outcome**: The full stack runs locally with one command; hosted production deployments cause 0 s downtime and 0 failed runs and roll back in ≤ 5 min; data is retained 90 days (configurable per type).
- **Traces**: FR-030, FR-037; NFR-005, NFR-014–NFR-017 (extended to bare metal, 2026-10-13), NFR-025, NFR-026.
- **Est. AI**: 05:15

#### Milestone M-011.1: One-command local stack
- **Problem solved**: Developers cannot reproduce the cloud setup locally.
- **Est. AI**: 01:45

##### FEAT-011.1.1: Local stack
- **Est. AI**: 01:30

- **US-067** — **As a** developer **I want** one command to start the full stack locally **So that** I can develop and test without Azure (`Est. AI: 00:50`)
  - [ ] Starts DB, control plane, one agent, UI in ≤ 2 min on Windows and Linux; local stand-ins for blob, secrets, telemetry.
  - [ ] Docker optional: an existing/native PostgreSQL is used when a connection string is configured.
  - [ ] Options start extra simulated agents (distinct names/labels) and a Linux agent (container or WSL) on the same machine.
- **US-111** — **As a** developer **I want** an optional prod-like local PPE mode with production builds of all Linux/server components in containers (the Windows agent runs natively) behind the local HTTPS edge at `https://ppe.kardia.sonapraneeth.in` **So that** I can catch TLS, cookie, OIDC, WebSocket and failover issues locally and let other machines join (`Est. AI: 00:40`)
  - [ ] `wf local up` / `wf local down` start and stop Release builds of api, two engines, PG, Linux agent(s), sample services, dashboard and a gateway Caddy in Docker, generated from the same AppHost and published only on `127.2.6.7`; a Windows agent can join natively.
  - [ ] `https://ppe.kardia.sonapraneeth.in` is served by the GrihaSetu edge with its mkcert umbrella certificate and opens without a certificate warning on this machine and on LAN clients that trust the mkcert root.
  - [ ] UI, API and SignalR share one HTTPS origin; agents reach only the engine leader; native and LAN agents use gRPC/TLS, in-compose agents may use h2c on the isolated gateway network; killing the leader container resumes runs on the standby.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: CI job on Windows and Linux starts the stack and runs scenario A green in ≤ 2 min startup.
- [ ] **Integration tests**: nightly Linux job runs `wf local up`, runs scenario A against the gateway, kills the engine leader, and the run completes.

#### Milestone M-011.2: Zero-downtime deploy and rollback
- **Problem solved**: Deployments interrupt users and running workflows.
- **Est. AI**: 02:45

##### FEAT-011.2.1: Rolling/blue-green deployment
- **Est. AI**: 01:55

- **US-068** — **As an** Admin **I want** to deploy the control plane, agents, and UI with no downtime **So that** users and runs are unaffected (`Est. AI: 00:45`)
  - [ ] In hosted production profiles (local single-instance mode exempt): 0 s user-visible downtime, 0 failed in-flight runs; SignalR clients reconnect and resync automatically; agents update in waves without draining, because running tasks survive the agent restart (US-109).
- **US-069** — **As an** Admin **I want** to roll back any deployment in ≤ 5 min **So that** bad releases are cheap (`Est. AI: 00:25`)
  - [ ] One action switches traffic to the previous revision; no data loss.
- **US-108** — **As an** Admin **I want** to run and upgrade the platform on bare metal or Azure VMs (Windows/Linux services, no containers) **So that** labs and VM fleets get the same zero-downtime behaviour as Azure PaaS (`Est. AI: 00:45`)
  - [ ] Rolling script upgrades api nodes one at a time (drain via health endpoint), engine standby first then leader handover ≤ 3 s, agents in waves; rollback re-points the release folder.
  - [ ] Agents reach the leader via an endpoint list with `NotLeader` redirect; PG failover (Patroni, ≈ 10–20 s) resumes runs ≤ the failover time + 30 s.

##### FEAT-011.2.2: Backward-compatible migrations and clients
- **Est. AI**: 00:35

- **US-070** — **As an** Admin **I want** DB migrations and API changes to be backward compatible **So that** old and new versions run side by side (`Est. AI: 00:35`)
  - [ ] Expand → migrate → contract migrations; versioned API; hashed static assets keep open tabs working.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: deploy-under-load test: 100 active runs + 200 connected clients during deploy and rollback: 0 failed runs, 0 HTTP 5xx, all clients resynced.
- [ ] **Performance**: rollback completes ≤ 5 min.
- [ ] **Integration tests**: US-108 self-hosted lab (2 control-plane VMs, 3-node Patroni, 1 Windows + 1 Linux agent): same deploy-under-load test with 0 failed runs and 0 HTTP 5xx; Patroni primary kill → all runs reach a terminal state, resume ≤ the Patroni failover time + 30 s.

#### Milestone M-011.3: Data retention
- **Problem solved**: Storage grows without bound.
- **Est. AI**: 00:45

##### FEAT-011.3.1: Retention policies
- **Est. AI**: 00:30

- **US-071** — **As an** Admin **I want** retention of 90 days by default, configurable per type **So that** cost is bounded (`Est. AI: 00:30`)
  - [ ] Applies to runs, events, logs, reports, artifacts, metrics; audit kept ≥ 1 year; purge never blocks live traffic.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: purge job removes expired data across DB and blob for a per-type override; live API p95 unaffected (< 10% change) during purge.

### REQ-012: Recovery (Should Have)
- **Outcome**: Failed runs resume from the failed node, bad workflow versions roll back instantly, and missed fires are recorded and caught up.
- **Traces**: FR-011, FR-016, FR-055.
- **Est. AI**: 02:20

#### Milestone M-012.1: Re-run from failed node
- **Problem solved**: Long workflows must re-run end to end after one failure.
- **Est. AI**: 00:55

##### FEAT-012.1.1: Re-run from failed node(s)
- **Est. AI**: 00:40

- **US-072** — **As an** Operator **I want** to re-run from the failed node(s) reusing succeeded outputs **So that** I save time (`Est. AI: 00:40`)
  - [ ] New run linked to the original; succeeded nodes marked Reused with their outputs; same version and variables.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: scenario A re-run from Deploy-US reuses Build and Deploy-EU outputs; UI shows lineage.

#### Milestone M-012.2: Instant workflow-version rollback
- **Problem solved**: A bad workflow version keeps breaking runs until a fix is merged.
- **Est. AI**: 00:40

##### FEAT-012.2.1: Activate an older version
- **Est. AI**: 00:25

- **US-073** — **As an** Admin **I want** to activate a previous workflow version from UI/API **So that** I recover instantly (`Est. AI: 00:25`)
  - [ ] Active ≤ 5 s; audited; the next merge re-activates the newest version only if it is newer.
  - [ ] Rollback covers workflow versions only; code-defined schedule configs are rolled back through source control (revert, then re-register).

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: rollback during active schedules: 0 missed/duplicated fires; audit record present.

#### Milestone M-012.3: Missed-fire catch-up
- **Problem solved**: A scheduler outage silently drops recurring runs.
- **Est. AI**: 00:45

##### FEAT-012.3.1: Missed-fire recording and catch-up
- **Est. AI**: 00:30

- **US-074** — **As an** Operator **I want** missed fires recorded and caught up per policy (none / latest-only) **So that** outages are visible and recovered (`Est. AI: 00:30`)
  - [ ] Default latest-only; catch-up run marked trigger = catch-up.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: scheduler down 10 min: misses recorded; exactly one catch-up run per schedule under latest-only.

### REQ-013: Trends and metric dashboards (Should Have)
- **Outcome**: Users see success-rate and duration trends per series and dashboards that pinpoint slow and flaky parts.
- **Traces**: FR-024, FR-047.
- **Est. AI**: 01:35

#### Milestone M-013.1: Trend insights
- **Problem solved**: Slow or flaky parts are invisible over time.
- **Est. AI**: 01:35

##### FEAT-013.1.1: Series trends
- **Est. AI**: 00:35

- **US-075** — **As a** Lead **I want** success rate and duration trends per run series **So that** I see drift (`Est. AI: 00:35`)
  - [ ] Pass/fail streak, p50/p95 duration over 7/30/90 days; renders < 500 ms for 90 days of daily runs.

##### FEAT-013.1.2: Metric dashboards
- **Est. AI**: 00:45

- **US-076** — **As a** Lead **I want** dashboards per workflow, schedule, node, and machine **So that** I find the slowest and flakiest parts (`Est. AI: 00:45`)
  - [ ] Top-N slowest nodes, highest failure rate, highest CPU/RAM; drill-down to runs.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: aggregate queries correct against a seeded dataset (known answers).
- [ ] **Performance**: dashboards p95 < 500 ms on 90 days of data at 2,000 runs/day.
- [ ] **Accessibility**: charts have tabular alternatives; zero axe critical/serious.

### REQ-014: Run completion notifications (Should Have)
- **Outcome**: Subscribers learn about completion or failure without watching the UI.
- **Traces**: FR-028.
- **Est. AI**: 00:55

#### Milestone M-014.1: Completion notifications
- **Problem solved**: Users must watch the UI to learn outcomes.
- **Est. AI**: 00:55

##### FEAT-014.1.1: Subscriptions (email, Teams, webhook)
- **Est. AI**: 00:40

- **US-077** — **As an** Operator **I want** to subscribe to a workflow, schedule, or type for completion/failure **So that** I'm told when it matters (`Est. AI: 00:40`)
  - [ ] Delivered ≤ 30 s after terminal state; retries with backoff; webhook payload is a CloudEvent signed with HMAC.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: delivery to a test webhook and mocked email/Teams endpoints; retry on 5xx; signature verified.

### REQ-015: Machine registry, capacity, selection, exclusive reservation (Must Have — phase 2)
- **Outcome**: Tests run on explicitly chosen or label-selected machines, reserved exclusively and always released; each machine's load capacity is known.
- **Traces**: FR-101–FR-104, FR-125.
- **Est. AI**: 03:30

#### Milestone M-015.1: Machine selection
- **Problem solved**: Tests cannot target specific machines or know their capacity.
- **Est. AI**: 02:05

##### FEAT-015.1.1: Machine registry and capacity
- **Est. AI**: 01:15

- **US-078** — **As an** Admin **I want** a machine list with OS, labels, pool, health, capacity, current reservation **So that** I manage the fleet (`Est. AI: 00:35`)
  - [ ] Labels and pools editable by Admin; changes audited.
- **US-079** — **As an** Admin **I want** each machine's max requests/s set manually or measured by a calibration run **So that** load is split correctly (`Est. AI: 00:40`)
  - [ ] Calibration runs a fixed loopback load and records sustainable req/s per capacity profile (transport + codec + payload-size class + load settings); a manual value overrides per profile.

##### FEAT-015.1.2: Targeting
- **Est. AI**: 00:35

- **US-080** — **As a** Service Team engineer **I want** a node to target machines by ID or by label selector + count + OS **So that** tests run where intended (`Est. AI: 00:35`)
  - [ ] Unknown IDs or unsatisfiable selectors fail validation or trigger with a clear message.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: selector matching on mixed Windows/Linux agents; calibration produces a stable value (±5% across 3 runs).
- [ ] **UI / E2E tests**: US-078 machine list; **Accessibility**: zero axe critical/serious.

#### Milestone M-015.2: Exclusive reservation
- **Problem solved**: Shared machines skew results and reservations leak.
- **Est. AI**: 01:25

##### FEAT-015.2.1: Reservations
- **Est. AI**: 01:10

- **US-081** — **As a** Service Team engineer **I want** machines reserved exclusively for my test **So that** results are not skewed (`Est. AI: 00:45`)
  - [ ] Reserved machines run only that run's tasks; released on run/node completion, failure, abort, or lease expiry; engine failover preserves the reservation (the new leader adopts and renews the current epoch before expiry); 0 leaked reservations.
  - [ ] Reservations are renewable leases: the engine renews them while the node or run is active; every dispatch carries the reservation epoch; when a lease expires, its work is cancelled and fenced (stale-epoch dispatches and results rejected) before the machine is reassigned.
- **US-082** — **As an** Operator **I want** to queue or reject (configurable) a run when machines are unavailable **So that** behaviour is predictable (`Est. AI: 00:25`)
  - [ ] Queued runs show position and the blocking reservation.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: concurrent reservation race (20 runs competing for 4 machines): no double reservation; chaos test (kill leader mid-test): the new leader adopts and renews every reservation before expiry, with no reassignment and no double reservation.

### REQ-016: Pluggable transport/codec and versioned service hooks (Must Have — phase 2)
- **Outcome**: New transports (ACS, gRPC) and codecs plug in without touching load/parity/hook code; service teams own only versioned C# request/response hooks, testable offline and isolated at runtime.
- **Traces**: FR-060, FR-105, FR-106, FR-123, FR-124; NFR-011, NFR-013.
- **Est. AI**: 03:45

#### Milestone M-016.1: Transport and codec plug-ins
- **Problem solved**: Each new protocol requires core changes.
- **Est. AI**: 01:45

##### FEAT-016.1.1: Plug-in contracts and HTTP transport
- **Est. AI**: 00:45

- **US-083** — **As a** platform engineer **I want** transport and codec as independent plug-in points **So that** I add ACS or gRPC without touching load, parity, or hooks (`Est. AI: 00:45`)
  - [ ] HTTP/1.1 and HTTP/2 transport shipped; a test transport is added in a separate assembly with 0 changes elsewhere.

##### FEAT-016.1.2: Bond and JSON codecs
- **Est. AI**: 00:45

- **US-084** — **As a** Service Team engineer **I want** Bond (Compact, Fast, Simple Binary, Simple JSON) and JSON codecs **So that** my service's payloads are encoded correctly (`Est. AI: 00:45`)
  - [ ] Round-trip tests for every Bond type in §9 of the discovery doc on every codec.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Unit tests**: codec round-trip matrix (all Bond types × all codecs) green on Windows and Linux.
- [ ] **Performance**: codec encode+decode of a 1 KB payload < 10 µs p99 (BenchmarkDotNet).

#### Milestone M-016.2: Service hook contract
- **Problem solved**: Service teams need to plug in service-specific logic without core access.
- **Est. AI**: 02:00

##### FEAT-016.2.1: Versioned hook contract and registration
- **Est. AI**: 01:15

- **US-085** — **As a** Service Team engineer **I want** to implement request shaping, response normalisation, and comparator extensions against a versioned contract and register versions **So that** I onboard without core PRs (`Est. AI: 00:45`)
  - [ ] Contract package follows SemVer; incompatible hook versions are rejected at registration.
- **US-086** — **As an** Admin **I want** a crashing hook to fail only its task **So that** the engine and other runs are safe (`Est. AI: 00:30`)
  - [ ] Tasks that invoke hooks (request/response hooks, load and parity tasks) run in a dedicated task-host process per task attempt (never in the control plane, the agent process, or a host shared with other tasks); crash → task `Failed` with category `TaskError`, reason code `HookError`.

##### FEAT-016.2.2: Hook test harness
- **Est. AI**: 00:30

- **US-087** — **As a** Service Team engineer **I want** to unit-test hooks against sample payloads **So that** issues are caught before check-in (`Est. AI: 00:30`)
  - [ ] Harness runs shaping → (fake) transport → normalisation → comparator with fixtures.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Unit tests**: hook harness self-tests; sample hooks tests green.
- [ ] **Integration tests**: crash/hang/OOM hook isolation tests: only the hook's task fails; other tasks of the same run, the agent, and the control plane stay healthy.

### REQ-017: Request sources (Must Have — phase 2)
- **Outcome**: Requests are generated from a Bond schema or replayed from files fetched from local, HTTPS, Azure Files, Azure Blob, or Cosmos DB, by download-first or live streaming, with bounded memory.
- **Traces**: FR-119–FR-122.
- **Est. AI**: 03:50

#### Milestone M-017.1: Schema-driven generation
- **Problem solved**: No requests can be sent without hand-crafted data.
- **Est. AI**: 01:00

##### FEAT-017.1.1: Generator from Bond schema
- **Est. AI**: 00:45

- **US-088** — **As a** Service Team engineer **I want** requests generated from my Bond schema with configurable value distributions **So that** I can test without data files (`Est. AI: 00:45`)
  - [ ] All Bond types supported; distributions: fixed, uniform, weighted set, sequence, random with seed (reproducible).

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Unit tests**: generation for every Bond type; same seed → identical request stream.
- [ ] **Performance**: generation ≥ 200k requests/s per core for 1 KB payloads (pre-generated ahead of send).

#### Milestone M-017.2: File replay from sources
- **Problem solved**: Real request sets live in remote stores that need auth, download, or streaming.
- **Est. AI**: 02:50

##### FEAT-017.2.1: Replay engine with local and HTTPS connectors
- **Est. AI**: 00:45

- **US-089** — **As a** Service Team engineer **I want** to replay requests from a local file or HTTPS URL, download-first or streaming **So that** I reuse existing request sets (`Est. AI: 00:45`)
  - [ ] HTTPS auth via secret reference (bearer, header, client cert); resumable download with checksum.

##### FEAT-017.2.2: Azure connectors
- **Est. AI**: 01:20

- **US-090** — **As a** Service Team engineer **I want** Azure Files and Blob sources in both modes **So that** data in storage accounts is usable (`Est. AI: 00:40`)
  - [ ] Auth via managed identity or secret reference; streaming starts sending before download completes.
- **US-091** — **As a** Service Team engineer **I want** a Cosmos DB source (download = query export; stream = paged query or change feed) **So that** requests stored in Cosmos are usable (`Est. AI: 00:40`)
  - [ ] Throttling (429) handled with backoff; progress reported.

##### FEAT-017.2.3: Bounded-memory streaming
- **Est. AI**: 00:30

- **US-092** — **As an** Admin **I want** multi-GB sources streamed with bounded memory **So that** agents don't run out of RAM (`Est. AI: 00:30`)
  - [ ] Reading a 20 GB source keeps process RAM growth ≤ 100 MB.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: each connector × each mode against local emulators (Azurite, Cosmos DB emulator) and a test HTTPS server; failure/resume tests.
- [ ] **Performance**: 20 GB stream with ≤ 100 MB RAM growth; streaming throughput ≥ 100 MB/s in-region.

### REQ-018: Performance testing (Must Have — phase 2)
- **Outcome**: Perf tests drive configured load shapes across reserved machines (split by capacity), stream live metrics, detect saturation, stop instantly on demand, and pass/fail against thresholds.
- **Traces**: FR-061, FR-107, FR-108, FR-112, FR-113, FR-117, FR-126; NFR-012, NFR-021.
- **Est. AI**: 03:40

#### Milestone M-018.1: Accurate load generation
- **Problem solved**: There is no reliable way to drive a target load.
- **Est. AI**: 02:10

##### FEAT-018.1.1: Load shapes and capacity-based distribution
- **Est. AI**: 01:20

- **US-093** — **As a** Service Team engineer **I want** constant, ramp, step, and spike shapes with a target rate split across machines by capacity **So that** I test realistic load (`Est. AI: 00:55`)
  - [ ] Open-model scheduling (no coordinated omission); achieved rate within ±5% of target at steady state; generator overhead < 1 ms p99 per request.
  - [ ] Load and its reservations are kept through engine failover, deploys, and agent restarts (US-109). Each shard checkpoints every 10 s (position in the load shape, request-source offset, counts); after a machine loss the shard resumes on a spare eligible machine at the current point of the shape (no replayed ramp), or the remaining shards continue and the reduced rate is flagged. The gap is shown in the perf report.
- **US-094** — **As an** Operator **I want** a kill switch **So that** I stop load immediately (`Est. AI: 00:25`)
  - [ ] All generators for the run stop sending within 1 s; run is aborted with source `KillSwitch` and who/when/reason recorded as in US-013.

##### FEAT-018.1.2: Saturation detection
- **Est. AI**: 00:35

- **US-095** — **As a** Service Team engineer **I want** the test to stop or flag when my service saturates before the target **So that** I don't overload it blindly (`Est. AI: 00:35`)
  - [ ] Saturation = p95 latency or error rate past configured limits for a configured window; action stop or flag; achieved max reported.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Performance**: against a no-op HTTP sink on an 8-core machine: 20k req/s sustained within ±5%; overhead < 1 ms p99 (dev sizing, NFR-021).
- [ ] **Integration tests**: kill switch stops all generators ≤ 1 s; saturation detected on a sink throttled at 2k req/s.

#### Milestone M-018.2: Perf results and live metrics
- **Problem solved**: Perf results are not visible live, stored, or judged.
- **Est. AI**: 01:30

##### FEAT-018.2.1: Live perf metrics
- **Est. AI**: 00:40

- **US-096** — **As a** Service Team engineer **I want** live throughput, latency percentiles, and error rate per machine and aggregate **So that** I watch the test (`Est. AI: 00:40`)
  - [ ] Updated every 1 s; percentiles merged from per-machine histograms (not averaged).

##### FEAT-018.2.2: Results store and thresholds
- **Est. AI**: 00:35

- **US-097** — **As a** Service Team engineer **I want** results stored with a summary report and pass/fail thresholds **So that** the run has a verdict (`Est. AI: 00:35`)
  - [ ] Full histograms stored as artifacts (disk locally, blob in Azure); summary report attached; breach → run Failed.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: 4-machine test (2 Windows, 2 Linux) shows live per-machine and merged metrics; merged p99 equals offline histogram merge.
- [ ] **UI / E2E tests**: US-096 live view and US-097 report rendering.

### REQ-019: Response-parity testing (Must Have — phase 2)
- **Outcome**: Identical requests go to baseline and candidate; responses are normalised by service hooks and compared by our extensible comparator; runs end with parity %, mismatch categories, samples, and a verdict.
- **Traces**: FR-109–FR-111.
- **Est. AI**: 02:15

#### Milestone M-019.1: Baseline vs candidate comparison
- **Problem solved**: Response regressions between service versions go undetected.
- **Est. AI**: 02:15

##### FEAT-019.1.1: Dual dispatch
- **Est. AI**: 00:40

- **US-098** — **As a** Service Team engineer **I want** each request sent to baseline and candidate and the pair passed through my normalisation hook **So that** comparisons are fair (`Est. AI: 00:40`)
  - [ ] Same request bytes to both (after shaping); pairs correlated by request ID; optional baseline-vs-baseline noise pass.

##### FEAT-019.1.2: Basic comparator
- **Est. AI**: 00:45

- **US-099** — **As a** Service Team engineer **I want** a structural diff covering all Bond types, status code, and headers with ignore/normalise rules **So that** I get useful mismatches out of the box (`Est. AI: 00:45`)
  - [ ] Rules: ignore paths, numeric tolerance, unordered collections, regex normalise; extendable/overridable via hooks.

##### FEAT-019.1.3: Parity report and gating
- **Est. AI**: 00:35

- **US-100** — **As a** Lead **I want** parity %, top mismatch categories, and sample diffs, with failure below threshold **So that** I can decide on a release (`Est. AI: 00:35`)
  - [ ] Report rendered with diff + table kinds; mismatch samples stored as artifacts.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Unit tests**: comparator tests for every Bond type and rule; hook override tests.
- [ ] **Integration tests**: scenario E on dummy service A: expected parity % and categories (known seeded differences); threshold failure works.

### REQ-020: Dummy services, sample hooks, reference workflows (Must Have — phase 2)
- **Outcome**: Two runnable dummy services (A "Catalog", Bond Compact Binary over HTTP; B "Pricing", Bond Simple JSON over HTTP) with baseline and candidate versions covering all Bond types, sample hooks, and perf + parity workflows prove the flow end to end.
- **Traces**: FR-115.
- **Est. AI**: 01:45

#### Milestone M-020.1: End-to-end sample
- **Problem solved**: There is no end-to-end proof or onboarding reference.
- **Est. AI**: 01:45

##### FEAT-020.1.1: Dummy services A and B
- **Est. AI**: 00:45

- **US-101** — **As a** Service Team engineer **I want** two dummy services with baseline and candidate behaviour **So that** I can try the platform safely (`Est. AI: 00:45`)
  - [ ] Schemas together cover every Bond type listed in discovery §9; candidate differs in known fields and latency; run on Windows and Linux.

##### FEAT-020.1.2: Sample hooks and workflows
- **Est. AI**: 00:45

- **US-102** — **As a** Service Team engineer **I want** sample hooks plus perf and parity workflows with schedule files and tests **So that** I can copy them to onboard (`Est. AI: 00:45`)
  - [ ] Scenarios D and E run green in the local stack; hooks covered by harness tests.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: CI runs scenarios D and E against the dummy services on Windows and Linux.
- [ ] **Manual / UAT**: a service team engineer onboards dummy service B from the docs alone in ≤ 1 day; sign-off from service team lead.

### REQ-021: Comparison against a baseline run (Should Have — phase 2)
- **Outcome**: Perf and parity results are compared with a chosen baseline run in the same series.
- **Traces**: FR-114.
- **Est. AI**: 00:55

#### Milestone M-021.1: Compare against baseline run
- **Problem solved**: Regressions versus earlier runs are not visible.
- **Est. AI**: 00:55

##### FEAT-021.1.1: Series comparison
- **Est. AI**: 00:40

- **US-103** — **As a** Lead **I want** current perf/parity results compared to a baseline run (previous or pinned) **So that** I see regressions (`Est. AI: 00:40`)
  - [ ] Shows deltas for throughput, p50/p95/p99, error rate, parity %; optional regression threshold fails the run.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: seeded baseline vs current produce expected deltas; regression threshold fails the run.

### REQ-022: Per-team quotas (Could Have — phase 2)
- **Outcome**: Teams cannot exceed configured machine counts or max load rate.
- **Traces**: FR-116.
- **Est. AI**: 00:50

#### Milestone M-022.1: Quota enforcement
- **Problem solved**: One team can monopolise shared machines.
- **Est. AI**: 00:50

##### FEAT-022.1.1: Quotas
- **Est. AI**: 00:35

- **US-104** — **As an** Admin **I want** per-team quotas on reserved machines and max load rate **So that** capacity is shared fairly (`Est. AI: 00:35`)
  - [ ] Exceeding quota queues or rejects the run with the quota named.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: quota boundary tests (at, above) for machines and rate.

### REQ-023: Step-load "find max" mode (Could Have — phase 2)
- **Outcome**: A test reports the service's maximum sustainable rate.
- **Traces**: FR-127.
- **Est. AI**: 00:55

#### Milestone M-023.1: Find max sustainable rate
- **Problem solved**: A service's maximum sustainable rate is unknown.
- **Est. AI**: 00:55

##### FEAT-023.1.1: Find-max mode
- **Est. AI**: 00:40

- **US-105** — **As a** Service Team engineer **I want** load stepped up until saturation **So that** I learn my service's max rate (`Est. AI: 00:40`)
  - [ ] Reports the highest step meeting latency/error limits; result stored per run series.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: against a sink throttled at 2k req/s, reported max within ±10% of 2k.

### REQ-024: Recorded live traffic as request source (Could Have — phase 2)
- **Outcome**: Captured real traffic can be replayed for parity tests.
- **Traces**: FR-118.
- **Est. AI**: 01:00

#### Milestone M-024.1: Traffic recording source
- **Problem solved**: Synthetic or file requests may not reflect real traffic.
- **Est. AI**: 01:00

##### FEAT-024.1.1: Traffic capture to replay file
- **Est. AI**: 00:45

- **US-106** — **As a** Service Team engineer **I want** to record live requests into a replay file **So that** parity tests use real traffic (`Est. AI: 00:45`)
  - [ ] Capture uses a core-owned service middleware or sidecar writing the standard replay format; sampling rate and declarative secrets/PII redaction are configuration; no new service-team-owned hook contract.

**Exit Criteria** — milestone is done only when all boxes are checked:
- [ ] **Integration tests**: record from dummy service A, replay in a parity run, 100% parity against itself.

## 6. User Experience
### Entry Points
- **Home** → team areas the user belongs to (most recent first).
- **Deep links** from notifications, PRs (registration status), and shared filtered URLs.
- **CLI / API**: `dotnet run workflow.cs`, `wf validate`, `wf publish`, REST API.

### Core Experience
| Step | Action | System Response | Success State |
|------|--------|-----------------|---------------|
| 1 | Author writes `workflow.cs` + schedule file + tests | Validator and dry-run tests run in `dotnet test` | All green locally in < 10 s |
| 2 | Author merges PR | CI validates, compiles, registers | Version active ≤ 60 s; visible in version history |
| 3 | Operator opens team → type → workflow, clicks Run | Variable form pre-filled; run created | Run view opens; first task running ≤ 1 s |
| 4 | Operator watches run | Live graph, timeline, logs, reports appear | Every change visible ≤ 1 s |
| 5 | Run ends | Terminal status, metrics, reports, artifacts; notification sent | Clear verdict and reasons |
| 6 | Lead reviews series history | Trends, failure categories, compare runs | Regressions spotted in ≤ 3 interactions |
| 7 (phase 2) | Service team runs perf/parity workflow | Machines reserved; live perf metrics; parity report | Verdict + reports; machines released |

### Edge Cases
| Step | Action | System Response | Success State |
|------|--------|-----------------|---------------|
| Invalid variables | Trigger with wrong type | Inline error, no run created | User fixes and retries |
| Engine failover | Leader killed mid-run | Runs resume ≤ 30 s; UI reconnects and resyncs | No lost or duplicated state |
| DB failover | Primary database fails over | Runs resume ≤ the database's failover time + 30 s; UI resyncs | No lost or duplicated state |
| Agent lost | Machine disconnects | At 15 s without a heartbeat the agent is Unhealthy and gets no new dispatches; attempts stay `Running` and reclaimable until 30 s, then end `AgentLost` and are retried elsewhere or failed with reason; reservation kept until the node/run ends or its lease expires | Run reaches terminal state |
| Abort | Operator aborts a running run with a reason | Run shows Aborting; tasks signalled; post stages run; who/when/reason recorded | Run ends Aborted; details visible everywhere |
| Overlap | Schedule fires while previous run active | Policy applied (default skip), recorded | History shows reason |
| Missed fires | Scheduler outage | Misses recorded; latest-only catch-up | No silent gaps |
| Report too large | Publish > 10 MB | Publish call fails; failure policy applies | Clear error in task log |
| Machines busy | Reservation unavailable | Queue (position shown) or reject | Predictable outcome |
| Saturation | Service saturates before target | Stop or flag; max achieved reported | No blind overload |
| Code-defined schedule edit | User tries to edit in UI | Controls absent; API 409 with file path | User edits the file instead |

### Pages & Screens
| Page | Purpose |
|------|---------|
| Home | Team areas, recent runs, my subscriptions |
| Team area | Types in team order; latest status per workflow |
| Type view | Workflows grouped/filtered by metadata per team presentation config |
| Workflow | Overview, versions (diff, clone), schedules, run history, trends; read-only Task Plan for a selected version |
| Run | Live graph, timeline, tasks, separate System Events, logs by attempt, reports, artifacts, metrics, variables; links to its version's Task Plan |
| Schedule / run series | Origin, next fires, history with misses/skips, trends, Run now |
| Report viewer | Inline renderers; compare across runs |
| Machines (phase 2) | Registry, capacity, health, reservations, calibration |
| Perf/parity run (phase 2) | Live per-machine metrics, parity summary, diffs |
| Admin | Roles, retention, audit log, notification channels |

## 7. Technical Considerations
Design detail lives in the technical design doc; constraints that shape requirements:
- **Integration points**: Git host webhooks/CI for registration; Microsoft Entra ID; Azure Key Vault; Azure Blob/Files; Cosmos DB; email/Teams/webhooks; services under test (HTTP first, ACS later via transport plug-in).
- **Data storage & privacy**: run state and metadata in a relational DB; logs, reports, artifacts, histograms in blob (Azure) or disk (local); secrets only in the secret store; 90-day retention (audit 1 year).
- **Performance**: NFR-001, NFR-018–NFR-021, NFR-028–NFR-032 are acceptance targets, verified in milestone exit criteria and CI benchmarks.
- **Challenges**: exactly-once scheduling and state transitions under failover; per-task resource limits across Windows/Linux; Bond packaging (internal feed); single-file .NET workflow compilation at registration.

## 8. Success Metrics
- Run reliability: 100% of runs reach a terminal state; ≤ 0.01% platform-caused failures — _engine metrics, 30-day window_.
- Platform overhead: ≤ 5% of run wall-clock — _per-run sum of queue/dispatch overhead vs task time_.
- Live latency: p95 ≤ 1 s state change → UI — _synthetic probe client_.
- Schedule accuracy: p99 trigger delay ≤ 2 s, 0 silent misses — _scheduler metrics_.
- Change lead time: merge → active p95 ≤ 60 s — _registration pipeline metrics_.
- Pre-check-in catch rate: 0 registrations rejected for rules not caught by unit tests — _registration rejections vs PR test results_.
- Footprint: control plane ≤ 2 vCPU / 1 GB at peak; idle agent ≤ 100 MB — _OpenTelemetry resource metrics_.
- Discoverability: target workflow found in ≤ 3 interactions — _usability check (5 users)_.
- Phase 2 onboarding: 0 core PRs per new service; ≤ 1 day to onboard — _onboarding log_.

## 9. Delivery Sequence
Milestones in build order across all requirements. Phase 1 = 57:40; phase 2 = 22:25.

| Order | Milestone | Requirement | Priority | Est. AI | Depends on | Exit criteria met |
|-------|-----------|-------------|----------|---------|------------|-------------------|
| 1 | M-001.1 Express a workflow in code | REQ-001 | Must | 03:30 | — | [ ] |
| 2 | M-002.1 Dependency-aware execution core | REQ-002 | Must | 03:10 | M-001.1 | [ ] |
| 3 | M-001.2 Run locally with built-in tasks | REQ-001 | Must | 02:05 | M-002.1 | [ ] |
| 4 | M-004.1 Shared validator | REQ-004 | Must | 02:05 | M-001.1 | [ ] |
| 5 | M-004.2 Dry-run test harness | REQ-004 | Must | 01:25 | M-002.1, M-004.1 | [ ] |
| 6 | M-002.2 Distributed execution on agents | REQ-002 | Must | 03:30 | M-002.1 | [ ] |
| 7 | M-011.1 One-command local stack | REQ-011 | Must | 01:45 | M-002.2 | [ ] |
| 8 | M-010.1 Authentication and RBAC | REQ-010 | Must | 01:30 | M-011.1 | [ ] |
| 9 | M-005.1 Automatic registration, hot activation | REQ-005 | Must | 01:55 | M-004.1, M-002.2 | [ ] |
| 10 | M-003.1 On-demand triggering | REQ-003 | Must | 01:15 | M-005.1, M-010.1 | [ ] |
| 11 | M-006.1 Live state push | REQ-006 | Must | 02:20 | M-003.1 | [ ] |
| 12 | M-006.2 Task logs and outputs | REQ-006 | Must | 01:15 | M-002.2 | [ ] |
| 13 | M-007.1 Team and type registry | REQ-007 | Must | 01:50 | M-004.1 | [ ] |
| 14 | M-007.2 Browse and filter | REQ-007 | Must | 01:35 | M-007.1, M-006.1 | [ ] |
| 15 | M-008.1 Run history | REQ-008 | Must | 01:25 | M-003.1 | [ ] |
| 16 | M-003.2 User-defined schedules | REQ-003 | Must | 02:15 | M-003.1 | [ ] |
| 17 | M-003.3 Code-defined schedules | REQ-003 | Must | 01:55 | M-003.2, M-005.1 | [ ] |
| 18 | M-005.2 Version history and cloning | REQ-005 | Must | 01:25 | M-005.1 | [ ] |
| 19 | M-002.3 Fault tolerance | REQ-002 | Must | 05:00 | M-002.2, M-003.2 | [ ] |
| 20 | M-009.1 Publish and view reports | REQ-009 | Must | 03:20 | M-006.1 | [ ] |
| 21 | M-009.2 Large artifacts | REQ-009 | Must | 00:55 | M-002.2 | [ ] |
| 22 | M-008.2 Execution metrics | REQ-008 | Must | 02:05 | M-002.2 | [ ] |
| 23 | M-010.2 Secrets, audit, public API | REQ-010 | Must | 01:50 | M-010.1 | [ ] |
| 24 | M-011.2 Zero-downtime deploy and rollback | REQ-011 | Must | 02:45 | M-002.3 | [ ] |
| 25 | M-011.3 Data retention | REQ-011 | Must | 00:45 | M-008.1 | [ ] |
| 26 | M-012.1 Re-run from failed node | REQ-012 | Should | 00:55 | M-002.1 | [ ] |
| 27 | M-012.2 Instant workflow-version rollback | REQ-012 | Should | 00:40 | M-005.2 | [ ] |
| 28 | M-012.3 Missed-fire catch-up | REQ-012 | Should | 00:45 | M-003.2 | [ ] |
| 29 | M-013.1 Trend insights | REQ-013 | Should | 01:35 | M-008.2 | [ ] |
| 30 | M-014.1 Completion notifications | REQ-014 | Should | 00:55 | M-003.1 | [ ] |
| 31 | M-015.1 Machine selection | REQ-015 | Must | 02:05 | M-002.2 | [ ] |
| 32 | M-015.2 Exclusive reservation | REQ-015 | Must | 01:25 | M-015.1 | [ ] |
| 33 | M-016.1 Transport and codec plug-ins | REQ-016 | Must | 01:45 | M-001.2 | [ ] |
| 34 | M-016.2 Service hook contract | REQ-016 | Must | 02:00 | M-016.1 | [ ] |
| 35 | M-017.1 Schema-driven generation | REQ-017 | Must | 01:00 | M-016.1 | [ ] |
| 36 | M-017.2 File replay from sources | REQ-017 | Must | 02:50 | M-017.1, M-010.2 | [ ] |
| 37 | M-018.1 Accurate load generation | REQ-018 | Must | 02:10 | M-015.2, M-017.1 | [ ] |
| 38 | M-018.2 Perf results and live metrics | REQ-018 | Must | 01:30 | M-018.1, M-009.1 | [ ] |
| 39 | M-019.1 Baseline vs candidate comparison | REQ-019 | Must | 02:15 | M-016.2, M-017.1 | [ ] |
| 40 | M-020.1 End-to-end sample | REQ-020 | Must | 01:45 | M-018.2, M-019.1 | [ ] |
| 41 | M-021.1 Compare against baseline run | REQ-021 | Should | 00:55 | M-018.2, M-019.1 | [ ] |
| 42 | M-022.1 Quota enforcement | REQ-022 | Could | 00:50 | M-015.2 | [ ] |
| 43 | M-023.1 Find max sustainable rate | REQ-023 | Could | 00:55 | M-018.1 | [ ] |
| 44 | M-024.1 Traffic recording source | REQ-024 | Could | 01:00 | M-017.2 | [ ] |
| **Total** | | | | **80:05** | | |

## 10. Open Questions
- [ ] None blocking. Assumptions carried from discovery (validate during build): ≥ 1,000 workflow definitions and ≥ 200 concurrent tasks (NFR-003); Entra ID for SSO (NFR-007); Bond packages available from the internal feed (nuget.org exact version as restore fallback, JSON over HTTP if unusable). Tracked as validation risk R-13 in the technical design.
