# Deployment review: fixed workers, long-running tasks and operational visibility

Date: 2026-10-22

Status: Discussion record for review; proposed policies are not approved or implemented.

## 1. Purpose and conclusions

This document collects the deployment discussion, verified implementation
observations, limitations and decisions still needed. It distinguishes the
existing isolated K3s lab from the user's target environment.

The latest constraint is: **agent and controller workers are fixed; API instance
count is not yet known**. Do not assume elastic workers, extra machines, spare
process capacity or two target controllers simply because the lab has them.

The resulting deployment position is:

- Prefer publishing a new immutable workflow package over restarting runtime
  components when only workflow code/configuration changes.
- For agent/runtime upgrades on fixed resources, prefer finishing existing tasks
  and deferring replacement if necessary. The current forced drain deadline is
  not a strict non-interruption guarantee.
- Upgrade redundant controllers individually using fenced single-leader
  handover and recovery checks. A single-controller installation needs a
  different availability plan; even redundant handover may pause scheduling.
- Choose an API rollout policy only after confirming API count and temporary
  capacity. Fixed machines do not necessarily mean no temporary API replica.
- Treat checkpoint recovery, service availability and uninterrupted task
  execution as separate guarantees.

**No runtime policy, replica count, GUI installation, deployment script behavior
or infrastructure was changed to implement these recommendations.**

## 2. Confirmed constraints and unresolved meanings

| Item | What is established | What remains open |
| --- | --- | --- |
| Agents and controllers | The user describes them as fixed. | Fixed hosts, fixed replica counts, fixed identities, or processes that must never restart while work runs? |
| API instances | Count is not confirmed. | One or multiple; separate or colocated; whether temporary surge is allowed. |
| Long tasks | Some tasks can run for a very long time; avoiding user interruption matters. | Must the same process continue, or is automatic checkpoint resume acceptable? |
| Extra capacity | Must not be assumed. | Whether temporary processes fit on existing hosts, or any spare worker capacity exists. |
| Maintenance deadlines | No target policy has been approved. | Can an upgrade wait hours/days, or must an emergency upgrade finish within a fixed time? |

Stable agent identity, a fixed number of agents, fixed host placement and an
unchanging process are different constraints. A replacement process can retain
an identity without retaining its former memory. These distinctions must be
resolved before approving a deployment guarantee.

## 3. Separate workflow publication from runtime deployment

The system already separates workflow versions from infrastructure image tags:

| Change | Existing mechanism | Effect on existing work |
| --- | --- | --- |
| Workflow definition/task package | Register an immutable version; activate/select it for new runs. | Existing runs keep their immutable version pins. Publication is not a worker restart. |
| API/frontend binary or assets | Replace application replicas using their rollout policy. | Service availability depends on replica count, readiness and capacity; this is not task-process migration. |
| Controller binary/protocol | Replace controller processes and transfer/recover leadership. | Requires durable-state recovery and compatible agents/packages; scheduling may pause during handover. |
| Agent/task-host runtime | Drain and replace the agent pod/process. | Its executing task processes cannot simply be assumed to survive replacement. |
| K3s, host OS, VM or physical node | Separate infrastructure-maintenance procedure. | The application release scripts are not a safe node-upgrade/reboot procedure. |

New workflow activation does not automatically require deployment of every
agent/controller. However, a workflow using a new SDK/private host protocol or
runtime feature can require a coordinated runtime upgrade. Old package versions
and compatible execution support must remain available for pinned runs.

Evidence: workflow activation/version behavior in the root README and
`backend\src\Wf.ControlPlane\Engine\Registry\WorkflowVersionResolver.cs`.

## 4. Existing isolated-lab topology and release mechanics

These are implementation facts, not a specification of the target environment.

| Component | Lab arrangement | Update behavior |
| --- | --- | --- |
| Nodes | Three owned K3s VMs, each configured with 2 CPUs and 3 GiB RAM. | Application release does not reboot or replace these VMs. |
| API | Two dedicated API replicas. | Rolling update, `maxUnavailable: 0`, `maxSurge: 1`, readiness gates. |
| Frontend | Two gateway/frontend replicas. | Rolling update with the same availability/surge settings. |
| Controllers | Two StatefulSet replicas; one active database-lease leader. | `OnDelete`; explicit standby-first replacement. |
| Agents | One worker DaemonSet pod per node, three total. | `OnDelete`; explicit one-worker-at-a-time drain/replacement/resume. |

### Stage

`infra\ha-lab\deploy.ps1` builds immutable image tags, imports the same images on
the three nodes, applies configuration/read-only observer permissions, runs the
migration job, then applies desired application images.

`-StageOnly` rolls API/frontend pods but leaves existing OnDelete controller and
worker pods running their old images. **Staging success is not release success.**
Importing an image alone does not replace a running pod; imports/builds can still
consume CPU, disk, network and memory, so they are not performance-neutral.

### Release

`infra\ha-lab\release.ps1`:

1. Verifies owned resources, desired image tags, ready pods and supported update
   strategies.
2. Uses application/database observations to replace the standby controller
   before the old leader. Waits for two healthy registered controllers and one
   active leader after each replacement.
3. Excludes one worker from new placement, requests an audited drain, and waits
   for its durable physical `Released` acknowledgement.
4. Deletes only the identified pod UID, waits for a replacement on the target
   image, retains the worker's durable identity, and explicitly resumes a fresh
   idle process using the exact drain receipt.
5. Verifies actual images, readiness, registered workers and leadership before
   updating the completed deployment record.

Failures stop the sequence. There is no universal automatic rollback, automatic
re-enable of a failed drain, or fabricated success. A rerun checks existing
release ownership/state; it does not override another operator's drain.

**Current scripts are lab-specific:** they expect two APIs, two frontends, two
controllers and the three named lab workers. Their ownership/preflight checks
must not be bypassed to run them on a different fixed topology.

## 5. What fixed resources change

### Fixed agent hosts/count

Without extra worker capacity, new work cannot simply move to a new blue/green
pool. Safest proposed sequence: stop admissions to one worker, let its work
finish, replace it in place, verify/resume, then proceed to the next worker.
Remaining workers may continue serving eligible work, but aggregate capacity is
temporarily lower and queues can grow. Machine-pinned work may have no alternate
placement.

For a task lasting days, this may defer that worker's upgrade for days. With
fixed execution capacity and no supported task migration, a finite upgrade
deadline and guaranteed uninterrupted arbitrary tasks cannot both be promised.

Fixed hosts do not rule out temporary processes if there is enough headroom,
but this has not been confirmed. Never run replacement agents against the same
live identity/state directory concurrently.

### Fixed controller hosts/count

| Target controllers | Deployment consequence |
| --- | --- |
| One | Restarting it creates a control-plane gap: no alternate controller can immediately schedule/recover as leader. Current lab release preconditions do not support this topology. |
| Two or more, compatible versions | Update standby processes first, transfer/recover leadership, and update the former leader last. Permit at most one valid scheduling leader; verify a recovered leader before proceeding. A handover gap is possible. |
| Processes may not restart while tasks run | Clarify whether this restriction applies to coordinators or task-executing agents. Fixed binaries cannot be upgraded in place merely by changing an image tag; no production hot-reload guarantee exists. |

The current architecture keeps task-host processes on agents rather than in the
dedicated controller pods. Agents reconnect to configured controllers and replay
journaled events after a transport interruption. Nevertheless, leader recovery,
checkpoint/report uploads, task deadlines and calls that require the control
plane can be affected. A single-controller restart is not proof that all tasks
must be killed, but neither is it a proven zero-impact maintenance strategy.

A controller should stop admitting new engine work before shutdown and flush
accepted events under its captured lease. Pod readiness alone does not prove
that a controller holds leadership or has finished recovery.

### Unknown API count

| API arrangement | Feasible availability claim / review point |
| --- | --- |
| One, no temporary replacement capacity | An in-place process restart has an API availability gap. Do not claim zero API downtime. |
| One, temporary second replica allowed | A readiness-gated replacement may preserve service, provided routing, capacity, host bindings and compatibility support overlap. This is not currently validated as a target topology. |
| Two or more, no surge capacity | It may be possible to replace one at a time while another serves traffic, accepting reduced capacity. This needs a different reviewed availability/surge policy from the current lab settings. |
| Two or more, surge allowed | The current lab's `maxUnavailable: 0`, `maxSurge: 1` pattern may fit, but needs a schedulable temporary replica and real headroom. |

Kubernetes does not permit both rolling-update `maxUnavailable` and `maxSurge`
to be zero. A zero-unavailability setting does not manufacture capacity. Hard
anti-affinity, fixed ports or resource limits can prevent a surge pod becoming
ready and stall a release. Existing lab API/frontend pods use required
per-role host anti-affinity.

API availability is distinct from task execution: replacing a dedicated API pod
does not directly delete an agent's task host. However, UI, triggering, status
queries and task-dependent API/storage operations may still be affected. Exact
dependency paths and acceptable outage windows need confirmation.

## 6. Current long-running-task behavior and limitations

The present drain is **bounded graceful completion followed by forced cleanup**:

- New placement/dispatch to the draining worker stops.
- Existing tasks can finish until the recorded deadline.
- `MaxDrainSeconds` defaults to **300 seconds**, with an accepted range of
  1..86400. Raising it remains a finite deadline, not finish-only maintenance.
- At the deadline the agent cancels unfinished executions, waits for their
  completion and fences/cleans up owned hosts before reporting physical drain.
- Only the acknowledged physical release permits reservation release and safe
  pod replacement. A zero counter, disconnected agent or expired timer alone
  is insufficient.
- Unsettled attempts follow the original retry/placement policy. Automatic
  platform retries are bounded; the common evaluator has a two-retry platform
  budget when enabled. Author retries, `NoAutoRetry`, remaining run/node
  deadlines, failure categories and placement constraints must still be honored.
- `requireResumable: true` verifies **agent process identity support** for safe
  post-maintenance resume. It does not certify that every task is checkpointable.
- `Released` does not mean dispatchable/Healthy. Existing resume requires a
  different freshly registered idle agent process and the exact released drain.

Automatic retry can preserve a workflow run without the user manually starting
it again, but an attempt may pause, resume, restart from scratch or fail. This
is not a blanket transparent-completion guarantee. Retrying external business
operations also requires suitable idempotency/reconciliation.

Sources: `backend\src\Wf.Agent\TaskCommands.cs`,
`backend\src\Wf.ControlPlane\Engine\Agents\AgentDrainService.cs`,
`backend\src\Wf.Engine.Core\Retries\RetryPolicyEvaluator.cs`.

## 7. Checkpoints: existing capability, not automatic migration

SDK tasks can explicitly use `ctx.Checkpoint.SaveAsync(...)` and
`ctx.Checkpoint.TryLoad<T>()` to persist and restore meaningful progress.

Save acknowledgement covers blob upload, the durable agent journal, engine
checkpoint-index commit and acknowledgements back to the task host. A
replacement attempt receives the verified earlier-attempt checkpoint; its
`Attempt.ResumedFrom` attribution is visible in the run timeline.

Important boundaries:

- Task authors must choose resumable state and implement the restore branch.
  Arbitrary stack/heap state, child processes, open files and in-flight network
  calls are not automatically migrated.
- Checkpoints have a 1 MiB serialized JSON limit and at least five seconds
  between acknowledged saves. They are not a per-instruction memory snapshot.
- A checkpointed task still needs an eligible replacement attempt. Retry
  budgets, deadlines and named-machine constraints are not bypassed.
- A missing/corrupt indexed checkpoint fails dispatch explicitly instead of
  silently pretending to resume or restarting from an empty state.
- Saving progress does not atomically commit arbitrary external side effects.
  Recovery can repeat work since the last safe checkpoint.
- The current drain does not automatically request and await a new safe
  checkpoint from every task before cancellation.
- Already-supported long-running HTTP operations retain remote identity in
  checkpoints, but a maintenance handoff must be distinguished from user
  abort/timeout before promising that the remote operation will be left running.

Smaller idempotent work units and durable external operation identities can
reduce recovery cost. They are task-specific designs, not automatic protection
for all existing task implementations.

## 8. Proposed policy options for review

The names below describe proposals, not existing API parameters or UI actions.

| Proposal | Intended behavior | Fixed-resource implications |
| --- | --- | --- |
| Finish-only maintenance | Quiesce new admissions, wait for active tasks, and defer replacement rather than cancelling unfinished work at the maintenance deadline. | Recommended starting point; preserves existing task processes but cannot promise a bounded upgrade time. |
| Checkpoint/resume maintenance | Explicitly checkpoint at a supported safe boundary, confirm durability and physical stop, then resume an eligible attempt. | No additional worker required for an in-place restart, but requires per-task support and validated recovery semantics. |
| Explicit forced maintenance | Administrator approves stopping unfinished work, with impact/preconditions and durable audit recorded before interruption. | Suitable for exceptional urgency, not an implicit routine default. Recovery/failure remains visible. |
| Versioned/blue-green worker pools | Route new work to compatible new workers while old workers finish and retire. | Conditional on spare capacity, distinct identities/state and compatible protocols/schema; not assumed feasible for this user's fixed workers. |
| Separately managed execution | Run durable long jobs independently of the frequently updated agent/monitor. | Larger architecture change; lifecycle, cancellation, identity, ownership and retry semantics need separate design. |

**Finish-only cannot be implemented solely by changing the PowerShell wait.**
The current agent receives a deadline that cancels executions independently of
the script. A new quiesce/non-destructive drain mode must be honored through the
API, persisted state, protocol, agent and recovery paths.

Deferral also needs a defined state: keep the old worker deliberately quiesced
until idle, or provide an explicitly audited way to cancel quiescence safely.
The existing post-restart resume endpoint is not a cancel-pending-drain
operation and cannot simply reopen the same busy process.

Recommended review direction: distinguish workflow publication from runtime
upgrades first; use finish-only maintenance for fixed agents by default;
make checkpoint recovery opt-in and validated; require explicit authorization
for forced interruption. Do not introduce extra worker pools without confirming
that the fixed-resource constraint permits them.

## 9. Storage, compatibility and capacity observations

Workflow definitions/versions, run/task state, leases, drain records, deployment
activity and audit use shared PostgreSQL. Workflow packages, reports/artifacts
and checkpoint blobs use shared blob storage. These are not browser-local or
per-API-replica workflow databases.

The isolated lab's PostgreSQL/Azurite services use persistent Docker volumes on
one shared data host. That host is a **single point of failure**; three K3s/etcd
nodes do not make application PostgreSQL/blob storage highly available or
establish backups/recovery objectives.

Agent identity, result journals, logs, package caches and local private state
use the node's `/var/lib/kardia-ha-lab/worker` mounted at `/state`. This preserves
state across a pod restart on that node, not node-disk loss, automatic cross-host
state replication or RAM migration. Do not copy a live identity/journal to a
second simultaneously running agent.

Mixed old/new versions require backward-compatible database migrations,
agent/controller protocols, task-host/SDK packages and checkpoint formats.
Migration jobs run before application rollout; execution order alone does not
make a destructive migration safe. Expand/contract and rollback compatibility
need explicit review.

Fixed-resource release planning must reserve headroom for K3s/system processes,
active task hosts, image import/unpack, migration jobs and any temporary API
replica. Requests/limits and readiness do not prove arbitrary long tasks cannot
encounter resource pressure. The observed RAM panel is monitoring, not memory
reservation or memory migration.

## 10. Monitoring, reporting and operator communication

Implemented, Global Admin-only application views:

- **Live system -> Actual system** (`/admin/system`): process/worker inventory,
  database-lease leadership, task/run counts, observed flow and Kubernetes RAM.
- **Live system -> Deployments** (`/admin/deployments`): desired versus actual
  images, generation/counts, pod state/reasons/restarts/image IDs, retained
  migration jobs and script-reported Stage/Release timelines.

Deployment observation is read-only and bounded. Current observation targets
the four named lab workloads; a new/versioned worker-pool topology would need
matching observation/release-inventory changes, not just extra pods.

Rollout states distinguish `InSync`, `Updating`, `Blocked`, `Unknown` and
`AwaitingReplacement`. In particular, ready old OnDelete pods do not prove a
new target version is running. Kubernetes reads are separate observations,
not an atomic cluster snapshot or proof of business correctness.

Activity heads/events persist in shared PostgreSQL, atomically with audit.
Reports have ordered, idempotent sequences and initiating-principal ownership.
They report what the scripts did; they are not an independent completion
verdict. The UI shows the latest 20 operations and retained migration jobs;
older operation records are not automatically deleted by that UI limit.
Kubernetes job retention can remove older migration-job observations.

The view refreshes every five seconds, supports pause/manual refresh, and hides
stale snapshots after 20 seconds, including while paused. Failed/unauthorized
reads do not leave cached success looking current. A Running reporter silent
for over 600 seconds is labeled **Unconfirmed**, not automatically successful
or failed. Empty/pre-instrumentation history and bootstrap gaps are explicit;
no earlier deployment history is fabricated.

Global Admins can publish a manual plain-text **System notice** from Actual
system: Information/Warning/Critical, optional local-time expiry, publish,
edit and confirmed clear. The banner appears above navigation for signed-in
users. It is communication, not automatic incident detection, an all-clear
signal, or deployment execution.

Long-task maintenance proposals should add explicit observations for
quiescing/waiting/deferred/forced state, blocked task counts and deployment
impact. Those additional maintenance states are not implemented by the
current deployment timeline.

## 11. Kubernetes GUI and access boundaries

No dedicated Headlamp/Rancher-style GUI was found in the owned lab's deployed
workload inventory during the 2026-10-22 discussion. This does not assert that
no desktop Kubernetes client is installed anywhere on the host.

Headlamp was recommended as a complement to the application views: a desktop
or browser-based Kubernetes interface using kubeconfig/Kubernetes permissions.
Prefer scoped read-only access and a private/loopback-only endpoint for this
lab. Do not expose an unauthenticated cluster dashboard or hand out a
privileged kubeconfig merely to view pods/nodes.

The official Headlamp desktop guide, reviewed on 2026-10-22, warns that Windows
releases are unsigned. Verify release provenance and follow local security
policy; do not treat bypassing OS security warnings as an installation plan.
No GUI installation was requested or performed.

Kubernetes permissions are separate from the application's Global Admin role.
A general cluster GUI does not know our physical drain acknowledgement, run
retry policy or controller lease semantics. Deleting/restarting a busy worker
from a GUI can bypass the application-aware release procedure.

PodDisruptionBudgets concern availability under supported eviction paths;
they do not preserve in-task memory or constrain ordinary workload rolling
updates. Direct deletion of pods/workloads bypasses PDB protection. PDBs and
long termination grace periods are not substitutes for the required task
lifecycle protocol.

## 12. Existing validation and limits of that evidence

The 2026-10-21 isolated-lab acceptance recorded:

- After staging, API/frontend pods on the target image while controllers and
  workers remained Ready on old images and correctly showed
  `AwaitingReplacement`.
- A real standby-first controller release and physical drain/replacement/resume
  of all three workers with retained identities.
- Final observations of all four roles in sync: two APIs, two frontends, two
  controllers and three workers Ready on `ha-lab-deploy-1`.
- Actual recorded operations: bootstrap Stage with two explicit post-bootstrap
  events, Release with 30 ordered events, normal Stage with 15 ordered events.
- Desktop Chromium and mobile/dark Firefox checks of navigation, live activity,
  stale/error/authorization boundaries, layout and accessibility.
- Targeted backend/frontend coverage of observation bounds, authorization,
  idempotency, concurrency, audit rollback and actor identity.

These are historical acceptance results, not a fresh cluster-health assertion.
They do not prove uninterrupted arbitrary hours/days-long non-checkpointable
tasks, a different fixed target topology, single-controller/API zero downtime,
node/OS upgrades, shared-data-host failure recovery, or live Azure readiness.
Azure assets discussed separately were prepared offline, not provisioned here.

Local sanitized acceptance outputs are under `.wf\ha-lab`; they are not
committed review evidence or a substitute for reproducible tests. Do not
distribute kubeconfigs, keys, tokens or private task data with a review export.

## 13. Decisions and acceptance criteria before implementation

Decisions required:

1. Define "fixed": host count, per-role replica count, stable identity,
   placement, and whether temporary process overlap is permitted.
2. Confirm controller/API counts, routing, colocation and acceptable scheduling
   versus API availability gaps.
3. Classify tasks: must finish in the same process; checkpoint-resumable;
   safely restartable/idempotent; durable external operation; machine-pinned.
4. Choose routine maintenance behavior when work exceeds the desired window:
   wait, defer, explicit checkpoint handoff, or authorized forced interruption.
5. Define how deferred/quiesced workers return to service without misusing
   the current post-restart resume flow.
6. Establish capacity, version/schema compatibility, rollback boundaries,
   backup/RPO/RTO requirements and urgent security-maintenance policy.
7. Decide GUI access/deployment method separately from release-execution rights.

Acceptance criteria for a future fixed-capacity policy:

- A task outlasting the maintenance window is not cancelled by finish-only
  maintenance, including if the operator script disconnects or restarts.
- No pod is replaced until required physical completion/ownership proofs exist.
- A deferred rollout is clearly reported, never labeled fully deployed.
- Quiescence survives controller failover/reconnect and can only be changed by
  the specified audited state transition.
- Checkpoint recovery proves durable acknowledgement, the correct attempt seed
  and expected side-effect/idempotency behavior; missing state fails explicitly.
- Non-retryable, exhausted-budget and machine-pinned tasks follow the approved
  policy without silently relaxing their contracts.
- Controller-count and API-count cases are exercised under their actual
  capacity/routing limits, including an unschedulable surge.
- Failure mid-release preserves a safe old process or a clearly stopped,
  fenced state; restart/resume is never guessed from Kubernetes readiness.
- Node/storage failure tests are kept separate from planned application
  deployment tests.

## 14. Related implementation and reference material

Repository:

- [Isolated HA lab runbook](../../infra/ha-lab/README.md)
- [Live system inventory and deployment visibility](live-system-inventory.md)
- [Shared blob-storage operations](shared-blob-storage.md)
- [PostgreSQL upgrade/blocker operations](postgresql-major-upgrade-and-blockers.md)
- [Application workload manifest](../../infra/ha-lab/application.yaml)
- [Stage script](../../infra/ha-lab/deploy.ps1)
- [Release script](../../infra/ha-lab/release.ps1)
- [Agent drain execution](../../backend/src/Wf.Agent/TaskCommands.cs)
- [Durable drain/resume service](../../backend/src/Wf.ControlPlane/Engine/Agents/AgentDrainService.cs)
- [Retry evaluator](../../backend/src/Wf.Engine.Core/Retries/RetryPolicyEvaluator.cs)
- [Workflow version resolver](../../backend/src/Wf.ControlPlane/Engine/Registry/WorkflowVersionResolver.cs)
- [Deployment activity API](../../backend/src/Wf.ControlPlane/Api/Admin/DeploymentActivityEndpoints.cs)

Official references reviewed on 2026-10-22:

- Kubernetes Deployment rolling-update capacity and strategy constraints:
  `https://kubernetes.io/docs/concepts/workloads/controllers/deployment/`
- Kubernetes disruption/PDB guarantees and bypasses:
  `https://kubernetes.io/docs/concepts/workloads/pods/disruptions/`
- Headlamp desktop platforms, custom kubeconfig and signing warning:
  `https://headlamp.dev/docs/latest/installation/desktop/`

These references explain platform behavior; they do not establish application
task-recovery guarantees or approve the proposed policies above.
