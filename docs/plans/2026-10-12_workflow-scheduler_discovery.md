# Discovery: Workflow Scheduler (+ Phase 2: Perf & Response-Parity Testing)
**Date**: 2026-10-12 | **Status**: Approved | **Owner**: soakula | **Revised**: 2026-10-15 — remote long-running API operations (FR-062–FR-064); previously run abort (FR-015), retries/timeouts (FR-014), long-task recovery (FR-061)

> Produced in autopilot: interview answers below are **assumed defaults** (marked _Assumed_).
> Every assumption is listed in §9 and §12 for stakeholder confirmation before status → Approved.

## 1. Idea Summary
A system that runs **workflows** to achieve a goal. A workflow is a graph of **nodes** with optional
dependencies; each node runs a set of **tasks** in series or parallel. Users trigger runs on demand or on a
schedule (recurring, parameterised), watch live status of the workflow and every node, and get a final
status. Workflows carry **metadata** and belong to a **type**; the UI organises and separates them by these.
History of recurring runs is tracked. Workflow logic is written as a **script that imports our workflow
library (SDK)**, which defines lifecycle stages: system init → workflow pre-setup → tasks → workflow
post-process → system post-process. Each workflow takes a **set of variables**; a run is triggered for a
given variable set. Recurring schedules come in two kinds: **code-defined** (system-scheduled recurring
tests, read-only in UI) and **user-defined** (created and modified only in the UI). We build **our own
engine** in **C#** (backend, SDK, hooks), hosted on Azure VMs/services or bare metal, fully reproducible **locally**
on one machine (built first), on **Windows and Linux**. Backend and frontend must be **extremely performant and reactive**.
**Phase 2**: a separate service team uses our infra to run **performance** and **response-parity** tests on
selected machines. We own everything (load generation, source connectors, parity framework, storage); the
service team owns **only request/response-specific logic** via a narrow hook surface.

## 2. Problem & Motivation
- **What breaks today** (_Assumed_): multi-step goals are run via ad-hoc scripts/pipelines; no single view of
  progress, no dependency handling, no consistent history for recurring runs, failures found late.
- **Who it hurts**: engineers operating the goals (manual babysitting), leads needing status/trend visibility,
  and (phase 2) service teams who lack shared infra for load and parity testing.
- **Cost of doing nothing**: duplicated scripting per team, no auditable history, perf/parity regressions
  reach production because testing is expensive to set up.

## 3. Users & Stakeholders
| Role | Needs | Technical level | Permissions |
|------|-------|-----------------|-------------|
| Workflow Author | Write workflow scripts using the SDK; define nodes, tasks, variables, metadata, type | High (engineer) | Create/edit definitions in owned types |
| Operator | Trigger, schedule, abort, retry runs; watch live status | Medium–High | Run/abort/retry on permitted workflows |
| Viewer / Lead | Browse by type & metadata, see status and history/trends | Low–Medium | Read-only |
| Platform Admin (core team) | Manage engine, machines, quotas, connectors, retention | High | Full |
| Service Team (phase 2) | Supply request/response hooks for their service (request shaping, response normalisation, comparator extensions); pick machines; run tests | High | Register hooks for own service only; reserve machines from permitted pools |
| Machine/Agent (phase 2, system actor) | Receive and execute tasks, report status | n/a | Execute only assigned tasks |

## 4. Goals & Success Criteria
- **Goal**: Extremely reliable dependency-aware execution — **Measured by**: 100% of runs reach a terminal state
  (Succeeded/Failed/Aborted/TimedOut) with no orphaned/stuck nodes over 30 days; ≤ 0.01% of runs fail for platform (non-workflow) reasons.
- **Goal**: Agile / fast execution — **Measured by**: platform overhead (scheduling + dispatch + state persistence) adds ≤ 5% to a run's wall-clock time vs running the same tasks directly (NFR-028–NFR-030).
- **Goal**: Lean on CPU and RAM — **Measured by**: engine and agent footprint targets in NFR-031/NFR-032 met under peak load.
- **Goal**: Live visibility — **Measured by**: node state change visible in UI p95 ≤ 1 s after it occurs (NFR-001).
- **Goal**: Schedules fire on time — **Measured by**: p99 trigger delay ≤ 2 s vs scheduled time; 0 silently missed fires (misses are recorded).
- **Goal**: Organised discovery — **Measured by**: user finds a workflow via type/metadata filter in ≤ 3 interactions (usability check).
- **Goal (phase 2)**: Service team self-serve — **Measured by**: a new service onboards a perf + parity test by writing only request/response hooks (0 core PRs per onboarding).
- **Goal**: Local parity with cloud — **Measured by**: the same workflow script runs unchanged locally and on Azure, on Windows and Linux (CI matrix green on both OSes).

## 5. Scope
### In Scope
**Phase 1 — Core engine & UI**
- Workflow SDK (library): workflows written as scripts importing it; lifecycle stages system init → workflow
  pre-setup → tasks (DAG of nodes; node = tasks in series or parallel) → workflow post-process → system
  post-process; variables; metadata (key/value); type.
- Own execution engine (scheduler, orchestrator, agents) — no third-party orchestrator.
- Triggers: on-demand and scheduled (cron/interval, timezone-aware), each bound to a variable set.
- Recurring schedules: code-defined (separate schedule config file, read-only in UI) and user-defined (UI-only create/modify).
- Pre-check-in quality: workflow scripts, schedule config files, and service hooks fully unit-testable offline;
  every issue the engine would reject is caught before check-in.
- Semantic versioning of workflows with author tracking; clone from any version.
- Metrics for monitoring/improvement: timing, failure statuses, latency, CPU, RAM.
- Task-generated reports (Markdown, HTML, tables, charts, images, diffs, etc.) viewable in the UI; many reports of mixed kinds per run.
- Zero-downtime platform deployments; workflow and schedule config changes go live immediately after check-in without redeploying.
- Runs fully locally on one machine (pure local on `localhost` / raw IP, or Docker Compose with per-environment
  domain URLs), on Azure (VMs or managed services), and on bare metal; Windows + Linux for engine, agents, and SDK.
  The first build targets this single machine (pure local + Docker Compose); bare-metal and Azure deployments use
  the same code and are verified in CI/lab, not on this machine.
- Run lifecycle: queued → running → (aborting) → succeeded/failed/aborted/timed-out. Node/task lifecycle: pending → ready → dispatched → running → (retrying) → succeeded/soft-failed/failed/timed-out/cancelled/skipped/reused.
- Live status of workflow, nodes, tasks; logs/outputs per task.
- Retries, timeouts, abort of a started run (who, when, reason recorded), re-run (whole or from failed node).
- History: all runs, grouped by schedule ("run series"), filterable, with trends.
- UI: separate views per workflow type; group/filter/search by metadata.
- Extensibility: authors can write SDK-function tasks alongside the built-in HTTP and command/script tasks.

**Phase 2 — Perf & response-parity testing on shared infra**
- Machine (agent) registry with labels/capacity; select machines explicitly or by label per run/node.
- Exclusive reservation of machines for the duration of a test.
- **Owned by us**: load generation (auto-generated from a request schema, or replayed from a file pulled from
  a specific source); source connectors (authentication, download, streaming); parity framework with a
  basic comparator; results storage; machine management.
- **Exposed to service team (only)**: request/response hooks — request shaping/transformation, response
  normalisation, and override/extension of our basic comparator.
- A **sample service setup**: two dummy services, each with its own Bond-defined request/response schema,
  together covering every Microsoft Bond type (see §9), each with baseline & candidate versions, plus
  sample C# hooks — proves the end-to-end perf and parity flow.
- Perf & parity results attached to runs and comparable across a run series.

### Out of Scope (Non-Goals)
- Visual drag-and-drop workflow editor — _Rationale: workflow logic lives in SDK scripts; UI views/operates._
- Adopting Temporal/Argo/Airflow etc. as the engine — _Rationale: decision to build our own engine; listed in §10 as reference only._
- General-purpose data/ETL features (lineage, partition backfills) — _Rationale: not the stated goal; Airflow/Dagster own that space._
- External/customer-facing multi-tenancy — _Rationale: internal tool._
- Provisioning/autoscaling machines — _Rationale: phase 2 selects from already-registered machines._
- Service-specific request/response logic — _Rationale: owned by service team via hooks; everything else is ours._
- Cross-region active-active HA — _Rationale: no stated need; revisit with scale data._
- Transparent recovery of an hours-long synchronous HTTP request to a service with no operation status or deduplication — _Rationale: after disconnect, its external outcome cannot be known safely._

## 6. Key Scenarios / User Journeys
### A. Author and run on demand
Author writes a workflow script importing the SDK: type "Release Validation", metadata `team=payments`,
variables `env, version`. System init → pre-setup (fetch config) → nodes Build → (Deploy-EU ‖ Deploy-US) →
Smoke → post-process (publish report) → system post-process (cleanup). Author runs it locally first, then
registers it. Operator triggers it with `env=staging, version=1.4`. UI shows the graph; nodes turn
running/succeeded live; Deploy-US fails after 2 retries → Smoke skipped → run ends Failed. From the workflow
page the operator opens this run's System Events to see when and why each attempt was placed, then opens
Deploy-US's version-pinned Task Plan to see its intended action and its per-attempt Logs to see what it printed.
Operator fixes the cause and re-runs from Deploy-US; Build/Deploy-EU results are reused.

### A2. Abort a started run
Mid-run, the operator notices the wrong `version` was passed and clicks Abort, entering the reason "wrong build
version". Running tasks are signalled to stop, not-started nodes are skipped, post-process still publishes a
partial report, and the run ends Aborted. The run page, history, series view, notifications and audit log all
show who aborted it, when, and why. Aborting again or aborting a finished run changes nothing.

### A3. Wait for a remote operation
An author configures a task to submit a long-running request to another service. The service accepts it and
returns a way to check progress; the task remains running while it waits, without holding one HTTP request
open for hours. The operator can see when the remote operation was accepted, is waiting, completes, fails,
or cannot be reconciled. If the task machine disappears, the replacement continues tracking the same remote
operation rather than starting a second one when the service supports deduplication. Aborting the run asks
the remote service to cancel when supported; the UI does not claim the remote work stopped until confirmed.

### B. Recurring parameterised schedule and its history
A schedule config file (separate from the workflow script) defines default variables grouped by category
(e.g. `environment`, `load`, `parity`) and test instances such as "nightly-staging" (02:00 PT, overrides
`env=staging`). Unit tests validate it before check-in; on merge the system schedules it and the UI shows it
read-only (history visible, no edits). Separately, an operator creates "nightly-prod-canary" in the UI and
later edits its interval there. Both show their origin (code/UI). Each night a run is created and linked to
its schedule. A lead opens the schedule's history: pass/fail streak, duration trend, CPU/RAM per node, link to
each run, and a "Run now" button that triggers the same instance immediately. One
night the scheduler was down; the missed fire is shown and (per policy) caught up once.

### B2. Change, version, clone
Author changes a task's timeout and adds a new optional variable → SemVer bumps minor (1.3.0 → 1.4.0),
recorded with author and change note. Unit tests (dry-run with mocked tasks) pass locally before check-in.
Another author clones v1.2.0 into a new workflow to experiment without touching the original.

### C. Browse by team, type, and metadata
Viewer opens the Payments team area (laid out per Payments' own presentation config), picks the "Release
Validation" type, groups by `env`, filters `env=staging`, sees latest status per workflow, drills into one.
Another team's types never appear here.

### D. Phase 2 — Perf test on selected machines
Service team provides only a request-shaping hook (e.g. add tenant header, sign body) and the request schema
of their API. Perf workflow (ours): Reserve machines (label `perf-pool`, 4, Windows or Linux) → Warm-up → Load
(our generator synthesises requests from the schema, or replays a file our connector downloads/streams from
the chosen source with its auth; ramp to N RPS for 10 min across the 4 machines) → Store results → Release
machines (always runs). Live view shows each machine's throughput/latency/error rate. Results are
compared with the previous run in the series.

### E. Phase 2 — Response-parity test
Team points the parity workflow at baseline (v1) and candidate (v2) of dummy service A. Our request source
(schema-generated or file from a source) sends identical requests to both. Our basic comparator diffs
responses; the team's hook normalises responses (drop timestamps/IDs) and extends comparison with one
service-specific rule (e.g. price within ±0.01). Run ends with parity %, top mismatch categories,
sample diffs; below threshold → run Failed.

## 7. Functional Requirements
### Phase 1 — Core
| ID | Requirement | Priority | Notes |
|----|-------------|----------|-------|
| FR-001 | The system shall let authors define a workflow as a script that imports the workflow SDK, declaring a DAG of nodes with optional dependencies. | Must | Reject cycles at registration. |
| FR-002 | The system shall let a node contain one or more tasks executed either in series or in parallel. | Must | |
| FR-003 | The system shall support typed workflow variables with defaults; each run is triggered for a concrete variable set passed to all lifecycle stages, nodes, and tasks. | Must | Validate at trigger. |
| FR-004 | The system shall attach key/value metadata to each workflow definition and copy it onto each run. | Must | |
| FR-005 | The system shall assign every workflow to exactly one workflow type. | Must | |
| FR-006 | The system shall version workflow definitions with semantic versioning (MAJOR.MINOR.PATCH), recording author, timestamp, and change note per version; each run records the version it used. | Must | Edits never mutate past runs. |
| FR-007 | The system shall start a run on demand via UI and API with a supplied variable set. | Must | |
| FR-008 | The system shall start runs on a schedule (cron or fixed interval, timezone-aware) with a bound variable set. | Must | |
| FR-009 | The system shall let users pause, resume, edit, and delete user-defined schedules. | Must | UI only (FR-035); code-defined: view only. |
| FR-010 | The system shall apply a per-schedule overlap policy when a previous run is still active: skip, queue, or allow concurrent. | Must | Default: skip. |
| FR-011 | The system shall record missed schedule fires and apply a catch-up policy (none / latest-only). | Should | Default: latest-only. |
| FR-012 | The system shall execute a node only after its dependencies complete per their failure policy (FR-038); downstream nodes of a hard-failed node are marked Skipped. | Must | Independent branches continue. |
| FR-013 | The system shall support per-node dependency conditions: on-success (default), on-failure, always. | Should | Enables cleanup nodes (release machines, stop load). |
| FR-014 | The system shall support retries and timeouts set in the workflow script at workflow (defaults), node, and task level, innermost winning. Retries: count, fixed or exponential backoff with jitter and a max delay, and which failure kinds are retried. Timeouts: per attempt, per node, and per run. A timed-out attempt is stopped (signal, then kill after a grace period) and is retried or fails per policy. Every attempt and its reason is recorded and visible live, retry waits and deadlines survive engine failover, and abort stops pending retries. | Must | Defaults: 0 retries, 1 h per attempt, 24 h per run. Platform faults (agent lost) are re-placed automatically up to 2 times without using the author's retries unless the task opts out. |
| FR-015 | The system shall let operators abort a queued or running workflow run from the UI, API, or CLI with a mandatory reason. The system shall record who aborted it, when, the reason, and the source (UI/API/CLI/kill switch); running tasks receive a cancellation signal, not-started nodes are skipped, post-process stages and `always` nodes still run, and the run ends Aborted. Abort details are shown on the run, in history, live, in notifications, and in the audit trail. | Must | First abort wins (idempotent); aborting a finished run is rejected. |
| FR-016 | The system shall let operators re-run a failed run from failed node(s), reusing outputs of succeeded nodes. | Should | |
| FR-017 | The system shall expose a terminal status for each run: Succeeded, Failed, Aborted, TimedOut. | Must | |
| FR-018 | The system shall push live state changes for runs (Queued, Running, Aborting, Succeeded, Failed, Aborted, TimedOut) and for nodes and tasks (Pending, Ready, Dispatched, Running, Retrying, Succeeded, SoftFailed, Failed, TimedOut, Cancelled, Skipped, Reused) to clients; clients load one snapshot then apply deltas, and resync from a sequence number after reconnect. A separate read-only System Events view shall show each run's ordered, timestamped task-attempt lifecycle, including why an agent was selected, when it was dispatched/started/completed, and retries or failures. | Must | Filter by node, task and attempt; no periodic polling. |
| FR-019 | The system shall capture logs and structured outputs per task attempt, viewable during and after execution; users shall be able to search and download task logs separately from system events. | Must | |
| FR-020 | The system shall let node outputs be consumed as inputs by dependent nodes. | Should | |
| FR-021 | The system shall organise the UI as team → workflow type → workflows; each team's types and workflows appear only under that team's area and never mix with another team's. | Must | Team isolation in UI. |
| FR-022 | The system shall let users filter, group, and search workflows and runs by metadata, status, type, and time range. | Must | |
| FR-023 | The system shall show run history per workflow and per schedule (run series): status, duration, trigger, variables. | Must | |
| FR-024 | The system shall show trends for a run series (success rate, duration over time). | Should | |
| FR-025 | The system shall provide built-in task types (HTTP call, script/command, SDK function) usable from workflow scripts. | Must | Phase 2 tasks build on these. |
| FR-026 | The system shall enforce role-based permissions (Author, Operator, Viewer, Admin) scoped by team and workflow type. | Must | |
| FR-027 | The system shall keep an audit trail of who triggered, aborted (with reason), edited, or scheduled what and when. | Must | |
| FR-028 | The system shall notify subscribers (email/Teams/webhook) on run completion or failure. | Should | |
| FR-029 | The system shall reference secrets by name from a secret store and never persist or display secret values in definitions, params, metadata, or logs. | Must | |
| FR-030 | The system shall apply a configurable retention period to run history, logs, and outputs. | Should | Default 90 days. |
| FR-031 | The system shall expose all UI capabilities through a documented, versioned API, except user-defined schedule mutations (FR-035), which are UI-only. | Must | CI + phase 2 automation. |
| FR-032 | The system shall provide a read-only graph view of a workflow and its live run state, plus a separate, version-pinned Task Plan page showing what each task was defined to do (commands or other task actions, dependencies, placement rules, retries and timeouts) without exposing secret values. | Must | Not an editor; past runs retain the plan for their original version. SDK-function internals cannot be predicted from the manifest. |
| FR-033 | The SDK shall define lifecycle stages executed in order: system init → workflow pre-setup → tasks (node DAG) → workflow post-process → system post-process; post-process stages run even when earlier stages fail. | Must | Stage status shown live like nodes. |
| FR-034 | The system shall support two kinds of recurring schedules: code-defined (recurring test instances declared in a schedule config file, scheduled by the system) and user-defined (created in the UI). | Must | |
| FR-035 | The system shall make code-defined schedules read-only in the UI and allow user-defined schedules to be created, modified, paused, resumed, and deleted only through the Kardia UI. The endpoints behind these UI actions are excluded from the public, versioned API contract, the SDK and the CLI, and accept only the Kardia UI's delegated user tokens (a product/contract boundary, not a security boundary: a user scripting the UI's token can do nothing beyond what their role allows in the UI); code-defined schedules and registration never create or modify user-defined schedules; reading them (list, history) stays in the public API. | Must | Code-defined sync on registration/deploy. |
| FR-036 | The system shall let authors run a workflow script locally against a local engine with identical behaviour to the hosted engine. | Must | |
| FR-037 | The system shall execute workflows, SDK, and agents on both Windows and Linux. | Must | |
| FR-038 | The SDK shall let authors set a failure policy per task and per node in the workflow script: **hard** (failure fails the node/workflow; dependents skipped; not-yet-started parallel siblings cancelled) or **soft** (marked soft-failed/warning; dependents and siblings continue). | Must | Default when unspecified: hard. |
| FR-039 | The system shall read code-defined recurring tests from a schedule config file separate from the workflow script, containing (a) default variables grouped by category and (b) test instances, each with schedule (cron/interval) and variable overrides; effective variables = defaults ← instance overrides. | Must | |
| FR-040 | The SDK shall provide a test harness to run a workflow offline in dry-run mode with mocked task outcomes, so unit tests can assert node order, dependency handling, hard/soft failure paths, retries, timeouts, and lifecycle stages. | Must | Runs in `dotnet test`, no engine needed. |
| FR-041 | The SDK shall expose the same validator the engine uses at registration (DAG cycles, missing dependencies, variable types/required values, cron validity, schedule-file overrides referencing unknown variables or invalid values, SemVer bump rules) so any config or script change is caught by unit tests before check-in. | Must | One validator → no drift between local and server. |
| FR-042 | The system shall reject registration of any workflow or schedule config that fails validation, with actionable errors. | Must | Server-side backstop. |
| FR-043 | The system shall detect breaking changes (variable removed, renamed, or type-changed; node removed or renamed) and require a MAJOR bump; additive variables or nodes → MINOR; implementation-only changes → PATCH. Schedule validation checks variable compatibility, not node references. | Should | Enforced by FR-041 validator. |
| FR-044 | The system shall let users view, diff, and clone any workflow version into a new workflow (or new major line), preserving lineage. | Must | |
| FR-045 | The system shall let users trigger a recurring instance on demand from its history (same instance variables, latest or pinned version), recording the run in the same run series with trigger = manual. | Must | |
| FR-046 | The system shall record metrics per run, stage, node, task, agent, and engine component: queue wait, start/end/duration, retries, terminal status and failure reason/category, success/failure rates, latency (p50/p95/p99), CPU, RAM, and (phase 2) network I/O. | Must | Viewable in UI and exported (NFR-010). |
| FR-047 | The system shall show metric dashboards and trends per workflow, schedule, node, and machine to identify slow/flaky parts. | Should | |
| FR-048 | The SDK shall let any lifecycle stage, node, or task publish reports (kind, title, optional tags, payload), attached to the run and its source stage/node/task; limits: ≤ 10 reports per run, ≤ 10 MB each (configurable per type). Exceeding a limit fails the publishing call with a clear error, handled by the task's failure policy (FR-038). | Must | Mixed kinds per run. |
| FR-049 | The UI shall render report kinds inline for user review: Markdown, HTML, table (CSV/JSON rows; sortable, filterable, virtualised), chart (time-series/bar from JSON), image (PNG/JPEG/SVG), JSON tree, text/log, side-by-side diff, PDF; any other file offered as download. | Must | Phase 2 perf and parity reports use these kinds. |
| FR-050 | The UI shall show a report index per run (grouped by stage/node/task, filterable by kind/tag), show reports as soon as they are published during a live run, and let users compare the same report across runs in a run series. | Must | |
| FR-051 | The UI shall render HTML/SVG reports in a sandboxed iframe; report content is served with `Content-Security-Policy: sandbox allow-scripts; frame-ancestors 'self'` (only Kardia can frame it; only this response omits `X-Frame-Options DENY`; opaque origin: any script has no access to Kardia's origin, cookies, storage, or API) and `X-Content-Type-Options: nosniff`. | Must | Security: reports are user-generated content. |
| FR-052 | The frontend shall render report kinds through a renderer registry so new kinds are added as a frontend renderer only (no backend or SDK change); unknown kinds fall back to download. | Must | |
| FR-053 | The system shall activate a newly registered workflow version or schedule config without restarting or redeploying any component; runs already in flight finish on the version they started with, and the next triggered/scheduled run uses the new version. | Must | Post check-in changes go live immediately. Workflow versions and schedule configs register independently: a schedule-only change needs no workflow version bump. |
| FR-054 | The system shall apply schedule config changes (add/remove/modify instances, overrides) atomically, with no missed or duplicated fires during the change. | Must | |
| FR-055 | The system shall let admins roll back a workflow to a previous version instantly from the UI/API. Code-defined schedule configs are rolled back through source control (revert, then re-register). | Should | Rollback = activate older workflow version. |
| FR-056 | The SDK shall let tasks store run data artifacts (e.g. response dumps, raw load results) up to 5 GB per run via streamed upload; artifacts are listed per run and downloadable in the UI but not rendered as reports. | Must | Separate from reports (FR-048). |
| FR-057 | The system shall assign every workflow type to exactly one owning team; a team may own up to 10 types (configurable quota). | Must | |
| FR-058 | The system shall reject duplicate or near-duplicate types at validation time: names compared after normalisation (case, spaces, `-`/`_`, punctuation, plural "s") and by similarity (e.g. edit distance ≤ 2 within the same team); same normalised name across teams is allowed but shown with team prefix. | Must | Runs in pre-check-in validator (FR-041) and server-side (FR-042). |
| FR-059 | The system shall let each team declare its own presentation for its area in the registry file: type order, grouping keys, list columns, default filters, display labels. | Should | No team's layout affects another's. |
| FR-060 | The system shall define transport and codec as two independent, core-owned plug-in points, so a new transport (ACS, HTTP, gRPC, …) or codec (Bond, JSON, …) is added without changing load generation, parity, or hook code. | Must | v1: HTTP transport + Bond codec (JSON fallback). ACS transport client written by the owner on this plug-in point. |
| FR-061 | The system shall keep long-running tasks (e.g. multi-hour load tests) going through control-plane crashes, restarts, deploys, and DB failover, and through an agent process crash, restart, or update; and shall let tasks save checkpoints so that after machine loss or reboot the retried attempt resumes from the last checkpoint instead of from the start. Built-in load and parity tasks checkpoint automatically; perf samples and results already collected are never lost. | Must | Run state is durable in the DB before any effect; engine memory is only a cache. |
| FR-062 | The system shall offer an opt-in HTTP task mode for remote asynchronous operations: accept a submission response with a status location, check progress without holding the submission request open, and complete or fail the task from the remote terminal result within the configured task, node, and run deadlines. Ordinary HTTP tasks retain their current synchronous behavior. | Must | A waiting task still occupies an agent slot in phase 1. |
| FR-063 | The system shall recover a remote-operation task after host, agent, or machine loss by resuming its recorded operation, reattaching via a stable per-run/task submission key when the remote service deduplicates, or finding an accepted job via authoritative lookup. An uncertain submission shall not be automatically repeated without remote deduplication; operators shall see that its outcome needs reconciliation. | Must | Record intent before submitting; a lookup that returns no job does not prove submission never happened. Internal completion deduplication does not guarantee external side effects execute once. |
| FR-064 | The system shall show remote-operation submit, wait, terminal outcome, and cancellation attempts in the run's System Events separately from task logs; abort or timeout shall request remote cancellation when supported and show whether it was confirmed or remains unknown. | Must | No claim that local task cancellation automatically stops remote work. |

### Phase 2 — Perf & response-parity testing
| ID | Requirement | Priority | Notes |
|----|-------------|----------|-------|
| FR-101 | The system shall maintain a registry of machines (agents) with labels, capacity, health, and pool membership. | Must | |
| FR-102 | The system shall let a run or node target machines explicitly (by ID) or by label selector and count. | Must | |
| FR-103 | The system shall reserve selected machines exclusively for a test and release them on completion, failure, abort, or reservation timeout. | Must | No leaked reservations. |
| FR-104 | The system shall queue or reject (configurable) a run when requested machines are unavailable. | Must | |
| FR-105 | The system shall expose to service teams only a versioned C# request/response hook contract: request shaping/transformation, response normalisation, and comparator override/extension. | Must | Everything else is core-owned. |
| FR-106 | The system shall let service teams register and version their hooks without changes to core code. | Must | |
| FR-107 | The system shall let perf tests configure load shape (constant, ramp, step, spike), target rate/concurrency, duration, and distribution across machines. | Must | Core-owned generator. |
| FR-108 | The system shall stream live perf metrics (throughput, latency percentiles, error rate) per machine and in aggregate during the run. | Must | |
| FR-109 | The system shall send identical requests to a baseline and a candidate endpoint and pass response pairs (after service normalisation hooks) to the comparator. | Must | |
| FR-110 | The system shall provide a basic comparator (structural diff of Bond/JSON payloads covering all Bond types, status code, headers, configurable ignore/normalise rules for timestamps, IDs, ordering) that service teams can modify or extend via hooks. | Must | Core-owned default. |
| FR-111 | The system shall report parity %, mismatch categories, and sample diffs, and fail the run below a configured threshold. | Must | |
| FR-112 | The system shall fail perf runs that breach configured thresholds (e.g. p95 latency, error rate). | Should | |
| FR-113 | The system shall store test results in a configurable store (local disk locally; blob/DB on Azure) and keep a summary attached to the run. | Must | Core-owned. |
| FR-114 | The system shall compare perf/parity results across runs in a series (baseline run vs current). | Should | |
| FR-115 | The system shall ship two dummy services (A and B), each with its own Bond request/response schema and baseline + candidate versions, plus sample C# hooks, demonstrating the end-to-end perf and parity flow. | Must | Together cover all Bond types (§9). |
| FR-116 | The system shall enforce per-team quotas on machines and max load rate. | Could | No approval/allow-list for load targets — targets in code are trusted. |
| FR-117 | The system shall provide a kill switch that immediately stops load generation for a run. | Must | Safety. |
| FR-118 | The system shall support recording real traffic as a request source for parity tests. | Could | Schema/file sources first. Capture uses a core-owned service middleware or sidecar writing the standard replay format; sampling and declarative redaction are configuration; no new service-team-owned hook contract. |
| FR-119 | The system shall generate requests automatically from a request schema (Bond schema first; OpenAPI/JSON Schema later) with configurable value distributions, covering all Bond types. | Must | |
| FR-120 | The system shall generate load by replaying requests from a provided file obtained from a configured source. | Must | |
| FR-121 | The system shall provide source connectors that handle authentication, download, and streaming of request data, in this order: local files; HTTPS URL; Azure Files; Azure Blob; Azure Cosmos DB. Every non-local source shall support both modes, chosen per test: **download-first** (fetch fully to local cache, then read) and **live streaming** (read while fetching, no full local copy). | Must | Core-owned; credentials via secret store (FR-029). Cosmos: download = export query results; stream = paged query/change feed. |
| FR-122 | The system shall stream large request sources without loading them fully into memory. | Must | |
| FR-123 | The system shall ship codecs for Bond (Compact Binary, Fast Binary, Simple Binary, Simple JSON) and plain JSON, via the codec plug-in point (FR-060). | Must | Dummy services: Bond over HTTP. |
| FR-124 | The system shall provide a test harness for service hooks so service teams can unit-test request shaping, response normalisation, and comparator extensions against sample payloads before check-in. | Must | Same pre-check-in rule as FR-040/041. |
| FR-125 | The system shall record each load machine's capacity (max requests/s), either set manually or measured by a calibration run on that machine, and split a test's target rate across the selected machines by capacity. | Must | Large production machines get higher capacity automatically. |
| FR-126 | The system shall let each perf test set its own target rate per service, and stop or flag the test when the service under test saturates (latency or error rate rising past configured limits) before the target is reached. | Must | e.g. a service that tops out at 2k req/s. |
| FR-127 | The system shall offer a step-load "find max" mode that increases load until the service saturates and reports its maximum sustainable rate. | Could | |

## 8. Non-Functional Requirements
| ID | Category | Target | Notes |
|----|----------|--------|-------|
| NFR-001 | Live status latency | State change → UI p95 ≤ 1 s, p99 ≤ 2 s | Push-based |
| NFR-002 | Schedule accuracy | p99 trigger delay ≤ 2 s; 100% of missed fires recorded | |
| NFR-003 | Scale (phase 1) | Up to 2,000 runs/day (peak/outlier; typical lower, adjust with usage), ≤ 200 nodes/workflow (confirmed); ≥ 1,000 workflow definitions; ≥ 200 concurrent running tasks | Definitions & concurrency _Assumed_ |
| NFR-004 | Durability | No run state lost on engine restart; in-flight runs resume or end Failed with reason | |
| NFR-005 | Availability | API/UI/scheduler 99.9% monthly, deployments included (they cause no downtime, NFR-025) | _Assumed_ |
| NFR-006 | UI performance | List/filter views p95 < 300 ms over 100k runs (server-side paging + virtualised lists) | |
| NFR-007 | Security | SSO (_Assumed_ Microsoft Entra ID), RBAC, TLS on every non-loopback, non-container-internal link (UDS, loopback HTTP, and h2c inside an isolated local Docker network are permitted), secrets via secret store only | |
| NFR-008 | Auditability | 100% of mutating actions audited, retained ≥ 1 year | |
| NFR-009 | Accessibility | UI meets WCAG 2.2 AA | |
| NFR-010 | Observability | Engine, agents, and SDK emit metrics, logs, traces via OpenTelemetry; resource metrics (CPU, RAM) sampled ≤ 5 s per task process and machine; metric retention ≥ 90 days | Feeds FR-046/FR-047 |
| NFR-011 | Extensibility | New service onboarded with only request/response hooks, 0 core code changes | Phase 2 driver |
| NFR-012 | Load-gen fidelity (phase 2) | Achieved RPS within ±5% of target at steady state; generator overhead reported | |
| NFR-013 | Isolation (phase 2) | A failing/crashing C# hook fails only its task, never the engine or other runs | Hook-invoking tasks run in a dedicated task-host process per task attempt on agents; a hook crash fails the task with category TaskError, code HookError |
| NFR-014 | Data retention | Runs, logs, reports, artifacts, metrics: 90 days (confirmed), configurable per type | Artifacts may need shorter retention for cost (see risk) |
| NFR-015 | Portability | Engine, agents, SDK, UI pass the same test suite on Windows and Linux | CI matrix |
| NFR-016 | Local dev | Two single-machine modes, both without Azure dependencies (local stand-ins for storage/queues/secrets). **Pure local**: the everyday dev stack starts with one command in ≤ 2 min and is reached at `http://localhost:<port>` (or the machine's raw IP over HTTPS for LAN agents); no domain name. **Docker Compose** (prod-like, may take longer to build/start): production builds of all Linux/server components in containers (the Windows agent runs natively), one stack per environment type, each behind the local HTTPS edge at its own domain URL (`https://<env>.kardia.sonapraneeth.in`; prod `https://kardia.sonapraneeth.in`; GrihaSetu platform pattern), so other machines can join | |
| NFR-017 | Deployability | The same versioned application code and per-OS binaries deploy locally, to bare metal, to Azure VMs, or to managed services; hosting packages differ (service folders versus container images), while environment-specific adapters are selected by config | Bare metal is supported by the code but not built on this machine |
| NFR-018 | Backend API latency | Read APIs p95 < 50 ms, p99 < 150 ms; trigger-run API p95 < 100 ms | Excluding long queries |
| NFR-019 | Event throughput | Engine sustains ≥ 10k state events/s fan-out to ≥ 500 concurrent UI clients without dropping updates | Coalesce per entity |
| NFR-020 | Frontend reactivity | First load (cold) < 2 s, route change < 200 ms, 60 fps while live-updating a 200-node graph at 100 events/s; JS bundle < 300 KB gz for initial route | |
| NFR-021 | Load-gen efficiency (phase 2) | Generator overhead < 1 ms p99 per request. Per-machine throughput is **not fixed**: it comes from each machine's registered capacity (FR-125). Dev/test sizing assumption: 20k requests/s per 8-core machine for small Bond payloads | Max load per test is service-specific (some services saturate at ~2k req/s); production machines are much larger |
| NFR-022 | Pre-check-in validation | Full validation + dry-run test suite for a typical workflow and its schedule file completes in < 10 s locally; 100% of registration-time rejection rules are reproducible offline | |
| NFR-023 | Report viewing | Reports ≤ 5 MB render in < 1 s; larger reports load progressively (paged tables, streamed text) without freezing the UI | |
| NFR-024 | Report & artifact volume | Reports: ≤ 10 per run, ≤ 10 MB each; report index loads p95 < 300 ms. Artifacts: ≤ 5 GB per run, upload/download streamed at ≥ 100 MB/s within Azure region. Payloads stored in blob (Azure) / disk (local), never in the main DB | Confirmed limits |
| NFR-025 | Zero-downtime deploys | In hosted production profiles, platform deployments (API, engine, agents, UI) cause 0 s of user-visible downtime and fail 0 in-flight runs: rolling/blue-green rollout, agents updated in waves without draining (running tasks survive the agent restart, FR-061; drain only for machine maintenance or an incompatible protocol migration), backward-compatible DB migrations (expand → migrate → contract), versioned API + SignalR auto-reconnect with resync, hashed static assets so open browser tabs keep working | Local single-instance mode is exempt |
| NFR-026 | Rollback | Any platform deployment can be rolled back in ≤ 5 min with no data loss | |
| NFR-027 | Change activation | Workflow/schedule change active p95 ≤ 60 s after merge (webhook-triggered validate → pre-compile → register) and ≤ 5 s after registration completes | "Instantaneous" post check-in |
| NFR-028 | Dispatch latency | Dependencies satisfied → dependent task starts on an agent: p95 ≤ 50 ms, p99 ≤ 200 ms (warm agent, pre-compiled workflow) | Agile execution |
| NFR-029 | Run start latency | Trigger (manual/schedule) → first task running: p95 ≤ 1 s for a pre-compiled workflow version; no per-run compilation | Compile once at registration (FR-053) |
| NFR-030 | Engine overhead | Engine-added time per node (state persist + dispatch + status publish) ≤ 10 ms p95; a 200-node workflow of no-op tasks finishes in ≤ 3 s end to end | Benchmark in CI; regression > 10% fails the build |
| NFR-031 | Control-plane footprint | Engine + API + scheduler at phase-1 peak (2,000 runs/day, ≥ 200 concurrent tasks, 500 UI clients): ≤ 2 vCPU average, ≤ 1 GB RAM total; idle ≤ 2% CPU, ≤ 300 MB RAM | Allocation-light hot paths, no per-event polling |
| NFR-032 | Agent footprint | Idle agent ≤ 1% CPU, ≤ 100 MB RAM; per-task harness overhead ≤ 50 MB RAM; agents enforce per-task CPU/RAM limits so one task cannot starve others | Win + Linux. Warm task hosts form a globally capped pool released when the agent goes idle |
| NFR-033 | Reliability under faults | Loss of any single engine node or agent: no run state lost, in-flight runs resume within ≤ 30 s of the crash or restart (leader failover). An agent is Unhealthy after 15 s without a heartbeat; its attempts stay reclaimable until 30 s, then end AgentLost and are re-placed per retry policy; a restarted agent re-adopts its running tasks within 10 s. DB failover: no run state lost; recovery time = the database's own failover time + ≤ 30 s (Azure Database for PostgreSQL zone-redundant HA ≈ 60–120 s; Patroni ≈ 10–20 s). Task dispatch is at-least-once and internal state transitions are idempotently applied once; external side effects require FR-063. Automated chaos/fault-injection suite in CI covers process kill, network partition, DB failover, disk full. An agent process restart or update does not stop running tasks; after machine loss, checkpointing tasks resume from their last checkpoint (FR-061) | Extends NFR-004. **Exception (Windows-only self-hosted sites)**: no automatic DB failover; the database is failed over manually (RTO in minutes). Everything else, including engine, API, and agents on Windows, is fully supported |
| NFR-034 | No single point of failure | Every control-plane component runs ≥ 2 instances in Azure (leader election for scheduler); local mode may run single-instance | |

## 9. Constraints & Assumptions
- **Constraint**: We build and own the engine, SDK, load generation, source connectors, parity framework, storage, and machine management. Service team owns only request/response hooks.
- **Constraint**: Hosted on Azure VMs, Azure managed services, or bare metal; must be fully replicable locally. The first build targets this single machine (pure local + Docker Compose), which is not a bare-metal server.
- **Constraint**: Engine machines and test machines may be Windows or Linux; everything must work on both. Sole exception: automatic DB failover needs Linux database hosts or a managed database, so Windows-only self-hosted sites fail the database over manually (NFR-033).
- **Constraint**: Third-party orchestrators are not used (decision: build our own).
- **Constraint**: C# for all backend code, SDK, libraries, and service hooks (.NET, cross-platform).
- **Constraint**: No approval/allow-list for load targets — whatever is checked into code is assumed correct.
- **Decision (code layout)**: backend and frontend are separate codebases (e.g. `backend/` .NET solution, `frontend/` Bun + Vite app), built, tested, and versioned independently; they share only the API contract (OpenAPI + SignalR message types, with TypeScript types generated from the backend contract so they never drift).
- **Decision (backend)**: ASP.NET Core (C#) hosts the REST API and the SignalR hub for live updates. React runs only in the browser and talks to this C# backend. Frontend deployment: static files served by ASP.NET Core by default (one deployable, simplest locally), or from Azure Static Web Apps/CDN — both work because the code is separate.
- **Decision (frontend)**: React + TypeScript SPA built with Vite; Bun as package manager/script runner (dev tooling only — production serves static files, so Bun is not in production). Live updates via ASP.NET Core SignalR (WebSockets; push, no polling). UI kit: **Tailwind CSS v4 + shadcn/ui** (accessible Radix-based components copied into repo, no runtime CSS-in-JS); DAG view: **React Flow (xyflow)**; lists: **TanStack Virtual**; server data: **TanStack Query**. _Rationale: Blazor Server adds a round-trip per interaction, Blazor WASM a large initial download (NFR-020). Astro is built for content/mostly-static sites with small interactive islands; this app is one fully live dashboard → Astro would wrap a single React island and add nothing. Fluent UI React v9 is the alternative if a Microsoft-native look is required._
- **Decision (workflow format)**: each workflow is a single .NET 10 file-based app (`dotnet run workflow.cs`). Shared code across workflows goes in our SDK/NuGet packages referenced via `#:package` (or `#:project` locally).
- **Decision (dummy services transport/codec)**: Bond over HTTP. Bond C# packages come from the Microsoft-internal feed (https://aka.ms/bond); nuget.org by exact version (unlisted, not deleted) is the restore fallback; JSON over HTTP is the fallback if the package proves unusable.
- **Context**: real services today are called via **ACS** (internal client transport). Transport and codec are pluggable (FR-060) so ACS, gRPC, etc. can be onboarded later without touching load/parity/hook code.
- **Assumption**: Two dummy services, each with its own Bond schema and wire protocol, e.g.
  **A "Catalog"** — Bond Compact Binary over HTTP; **B "Pricing"** — Bond Simple JSON over HTTP. Between them
  their request/response schemas cover all Bond types: `bool`, `int8/16/32/64`, `uint8/16/32/64`, `float`,
  `double`, `string`, `wstring`, `blob`, `enum`, nested `struct`, struct inheritance, generic structs,
  `list<T>`, `vector<T>`, `set<T>`, `map<K,V>`, `nullable<T>`, `bonded<T>`, default values, and field modifiers
  `optional` / `required` / `required_optional` — _Validated by: schema review against [Bond type reference](https://microsoft.github.io/bond/manual/compiler.html)._
- **Assumption**: Users are internal engineers; auth via corporate SSO — _Validated by: owner confirmation._
- **Assumption**: UI is for viewing/operating and UI-managed schedules; workflow logic is only in SDK scripts — _Validated by: owner._
- **Assumption**: Scale per NFR-003 — _Validated by: estimate from current scripts/pipelines._
- **Assumption**: Tasks are mostly I/O-bound calls/commands, minutes-to-hours long, not sub-second — _Validated by: inventory of target workflows._
- **Assumption**: Dummy services expose HTTP endpoints; baseline vs candidate differ in a few fields and latency — _Validated by: service team review._
- **Assumption**: Default failure semantics = skip downstream of failure, independent branches continue — _Validated by: owner._

## 10. Prior Art & Research
Decision: **we build our own engine**. Existing engines below are listed as reference for design patterns
(durable state, schedule semantics, UI conventions) and as a benchmark for what "done" looks like.
- [Temporal](https://temporal.io) — durable workflow engine: retries, timers, schedules, history. Fits durability/retry/schedule needs; no domain UI organised by type/metadata; DAG expressed in code.
- [Argo Workflows](https://argoproj.github.io/workflows/) — Kubernetes-native DAG engine, CronWorkflows, UI, labels. Strong fit on K8s; weaker for bare machines (phase 2).
- [Apache Airflow](https://airflow.apache.org) — DAG scheduler with history UI; data-pipeline oriented, Python-only DAGs, heavier ops.
- [Prefect](https://www.prefect.io) / [Dagster](https://dagster.io) — Python orchestrators with good UIs; data-centric.
- [Azure DevOps Pipelines](https://learn.microsoft.com/azure/devops/pipelines/) / [GitHub Actions](https://docs.github.com/actions) — stages/jobs with dependencies, schedules, self-hosted agents with labels (≈ machine selection). Cheapest option if already in use; weak for metadata-organised views and live per-node dashboards.
- [k6](https://k6.io), [Locust](https://locust.io), [Azure Load Testing](https://azure.microsoft.com/products/load-testing) — load generators; reference for load shapes, metrics, distributed workers (ours is built in-house).
- [Schemathesis](https://schemathesis.io) — generates requests from OpenAPI/GraphQL schemas; reference for FR-119.
- [Diffy (opendiffy)](https://github.com/opendiffy/diffy) — response parity via baseline/candidate + secondary baseline for noise detection. Directly informs FR-109–FR-111.
- [GoReplay](https://github.com/buger/goreplay) — traffic capture/replay; candidate request source for FR-118.
- [GitHub Scientist](https://github.com/github/scientist) — in-process experiment/parity pattern; comparator design ideas.
- [Microsoft's asynchronous request-reply pattern](https://learn.microsoft.com/en-us/azure/architecture/patterns/asynchronous-request-reply) — reference contract for remote jobs (202, status location, retry interval, idempotency, optional cancellation); fits inside existing task hosts without another service.
- Standards: [WCAG 2.2](https://www.w3.org/TR/WCAG22/) (NFR-009), [OpenTelemetry](https://opentelemetry.io) (NFR-010), [CloudEvents](https://cloudevents.io) (candidate format for live status events/webhooks).

## 11. Suggestions & Opportunities
- **Own engine, borrow the hard-won semantics**: copy Temporal's durable event-history model (state rebuilt from an append-only log → survives restarts) and Argo/Airflow schedule semantics (overlap policy, catch-up). These are where in-house engines usually get bitten.
- **Local = cloud**: abstract only where Azure and local genuinely differ (blob vs disk, queue, secrets); everything else identical. CI matrix on Windows + Linux from day one — path separators, process signals (cancel), and shell differences are the usual cross-OS breakers.
- **Code vs UI schedules**: two separate kinds, never mixed — code-defined recurring tests read-only in UI; user-defined schedules UI-only.
- **Single-file workflows — watch-outs**: first run compiles (cached after; pre-build on registration so runs start fast); engine needs a "describe" mode to read DAG/variables without running tasks (also used by FR-041 validation); pin SDK version per script so SDK upgrades don't silently change old workflows.
- **Reactivity path**: engine → in-process event stream → SignalR groups per run/type → client store patched per entity, rendered with virtualisation; coalesce bursts server-side (e.g. 100 ms windows) so the UI never falls behind.
- **Cleanup/always nodes** (FR-013): essential for phase 2 so machines are always released and load always stops.
- **Noise baseline for parity**: run baseline vs baseline (Diffy approach) to auto-learn noisy fields before judging candidate.
- **Run series as first-class entity** (keyed permanently by schedule; each run stores its effective variables) so history survives definition and variable edits.
- **Definition versioning + re-run from node** (FR-006, FR-016) avoid re-running long workflows end to end.
- **Edge cases**: DAG cycles; variable schema changes breaking existing schedules; DST transitions in cron; clock skew across machines (perf timestamps); engine restart mid-run; agent disconnect mid-test (reservation timeout); partial failure among parallel tasks (handled by hard/soft policy, FR-038); huge logs/outputs (size caps + external storage).
- **Remote API edge cases**: the submit response is lost after the remote service accepts work; operation status expires before the run ends; cancellation is unsupported or fails; a remote job outlives the task deadline. Preserve the remote outcome as unknown where it cannot be confirmed, rather than reporting a successful cancellation or blindly resubmitting.
- **Sequencing**: use existing task hosts and checkpoints for remote waits in v1; releasing an agent slot while waiting would require a separate durable suspension mechanism and is deferred until capacity data justifies it.
- **Abuse/safety**: no target approval by decision; kill switch (FR-117) remains the safety net for a runaway test; C# hooks isolated out-of-process (NFR-013).
- **Sequencing / cut from v1**: v1 = SDK + engine + phase 1 Must items, local mode, both OSes, read-only graph. v1.1 = trends, notifications, re-run-from-node. Phase 2 starts with machine registry + reservation + Bond-schema-based load + basic comparator on dummy services A & B; file sources/connectors next; traffic recording last.

## 12. Risks & Open Questions
- [x] ~~Build vs adopt engine~~ → build our own.
- [x] ~~Hosting~~ → Azure VMs, managed services, or bare metal; full local replica (pure local on localhost / raw IP, Docker Compose per environment with domain URLs). First build: this single machine.
- [x] ~~Machine OS~~ → Windows and Linux, both supported.
- [x] ~~SDK language~~ → C# everything (backend, SDK, hooks).
- [x] ~~Code vs UI schedules~~ → two kinds: code-defined (read-only in UI), user-defined (UI-only).
- [x] ~~Load-target approval~~ → none; code is trusted.
- [x] ~~Hook language~~ → C#.
- [x] ~~Where code-defined recurring tests are declared~~ → separate schedule config file with categorised defaults + per-instance overrides (FR-039).
- [x] ~~Schedule config file format~~ → JSON + JSON Schema, validated by FR-041 in unit tests.
- [x] ~~Version author~~ → record both git commit author and registering identity.
- [x] ~~Registration~~ → automatic on merge to main (repo webhook/CI → validate → pre-compile → register, NFR-027); explicit publish command for local/dev engines.
- [x] ~~Workflow format~~ → single .NET 10 file-based script.
- [x] ~~Failure behaviour~~ → per task/node hard or soft, set in the script; default hard (FR-038).
- [x] ~~Request-data connectors~~ → local files, HTTPS URL, Azure Files, Azure Blob, then Cosmos DB; every non-local source supports download-first and live streaming (FR-121).
- [x] ~~Bond support~~ → Bond OSS ended March 2025 upstream, but Microsoft internally supports it (incl. security fixes); use the internal feed package (https://aka.ms/bond), nuget.org exact version as restore fallback, JSON over HTTP if unusable.

### Resolved (from detailed review)
- [x] ~~OQ-1 Scale/retention~~ → 1,000–2,000 runs/day, ≤ 200 nodes/workflow, 90-day retention (NFR-003, NFR-014).
- [x] ~~OQ-2 Workflow types~~ → checked-in type registry file per team, PR-reviewed; strict duplicate checks (FR-058); ≤ 10 types per team (FR-057); teams' UI areas fully separated with per-team presentation config (FR-021, FR-059).
- [x] ~~OQ-3 Metadata~~ → schema per type in the registry (required keys + allowed values) + optional free-form keys.
- [x] ~~OQ-4 Transport/codec~~ → transport and codec pluggable independently (FR-060); dummy services use Bond over HTTP; ACS onboarded as a transport later.
- [x] ~~Reports/artifacts~~ → ≤ 10 reports/run, ≤ 10 MB each; artifacts ≤ 5 GB/run (FR-048, FR-056, NFR-024).

- [x] ~~Nodes per workflow~~ → ≤ 200.
- [x] ~~Load-generator throughput~~ → service-specific; per-machine capacity set or calibrated per machine (FR-125); per-test target + saturation detection (FR-126); 20k req/s per 8-core machine as dev sizing assumption.
- [x] ~~ACS transport~~ → owner writes the ACS client on the transport plug-in point; v1 ships HTTP transport (FR-060).
- [x] ~~Artifact storage budget~~ → 2,000 runs/day and 5 GB/run are outlier maximums; quotas and retention adjustable per type based on actual usage.

No product-scope questions remain. Technical delivery sequencing is tracked in technical-design Q-2.
### Risks
- **Risk**: In-house durable orchestrator is costly and bug-prone (stuck runs, exactly-once schedule firing) — mitigate with event-log design + chaos tests (kill engine mid-run).
- **Risk**: Cross-OS drift (Windows vs Linux) — mitigate with CI matrix from day one.
- **Risk**: Shared machines → noisy-neighbour skew in perf results unless reservation is exclusive.
- **Risk**: Hook contract churn after service teams onboard → version the contract from day one.
- **Risk**: Artifact storage cost (up to 5 GB/run) — mitigate with quotas, shorter artifact retention, Azure Blob cool/cold tiers, lifecycle policies.
- **Risk (low)**: Bond end-of-life upstream; mitigated by internal Microsoft support. Still keep Bond usage behind one serialisation boundary so the engine itself never depends on it.

## 13. Glossary
| Term | Meaning |
|------|---------|
| Workflow (definition) | Versioned SDK script declaring lifecycle stages, node DAG, variables, metadata, and a type. |
| Workflow SDK | Our library imported by workflow scripts; defines lifecycle stages, nodes, tasks. |
| Lifecycle stages | System init → workflow pre-setup → tasks → workflow post-process → system post-process. |
| Variable set | Concrete values for a workflow's variables; each run is triggered for one. |
| Team | Owner of workflow types (≤ 10); has its own isolated UI area and presentation config. |
| Type registry file | Checked-in JSON listing each team's types, metadata schema, and UI presentation. |
| Report | Rendered review output of a run (≤ 10/run, ≤ 10 MB each). |
| Artifact | Raw run data (e.g. response dumps), downloadable only (≤ 5 GB/run). |
| Transport / Codec | How requests travel (ACS, HTTP, gRPC) / how payloads are encoded (Bond, JSON); independently pluggable. |
| Workflow type | Category grouping workflows; each has its own UI view. |
| Node | Unit in the DAG; contains tasks run in series or parallel; may depend on other nodes. |
| Task | Smallest executable step, run by a built-in task type. |
| Remote operation | A job accepted by an external API that completes later; its status is checked separately from the submission request. |
| Run | One execution of a workflow definition version with a concrete variable set. |
| Schedule | Recurring trigger (cron/interval) with a bound variable set; code-defined (schedule config file, read-only in UI) or user-defined (UI-only). |
| Schedule config file | Separate file holding categorised default variables and recurring test instances with overrides. |
| Test instance | One code-defined recurring entry: schedule + variable overrides; its runs form a run series. |
| Dry run | SDK test mode executing workflow logic with mocked tasks, for unit tests before check-in. |
| Run series | All runs produced by one schedule (fires and Run now), keyed permanently by schedule ID (editing variables keeps the series; each run stores its effective variables); unit of history and trends. Ad-hoc runs belong to no series. |
| Metadata | Key/value attributes on workflows/runs used to organise and filter. |
| Service hooks | The only service-team-owned code: request shaping, response normalisation, comparator extensions. |
| Source connector | Core-owned component that authenticates to, downloads, or streams request data from a source. |
| Machine / Agent | Registered host executing tasks; selectable and reservable in phase 2. |
| Perf test | Load test measuring throughput, latency, errors under a defined load shape. |
| Response parity | Comparing responses of baseline vs candidate service for identical requests. |
| Baseline / Candidate | Reference (current) vs new version of a service under parity test. |
