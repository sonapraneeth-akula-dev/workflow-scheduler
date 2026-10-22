# Live system inventory

Open **Live system -> Actual system** in the main navigation (`/admin/system`). Global
administrators see application processes, their host/pod metadata, the current
controller lease, worker assignments and cluster totals. The diagram follows
browser/frontend -> API -> PostgreSQL durable state -> active/passive controllers
-> workers. Each controller card references an existing process; it does not
invent another replica when API and controller share a process. Every fresh
controller is shown, labeled **Active leader**, **Passive standby**, or without
a ready lease when draining. Only the active leader connects to workers.

Snapshots refresh every two seconds while the page is active. A failed
request or permission check hides cached inventory. If a request stalls
past the 15-second freshness window, old inventory is hidden rather than
left looking healthy. **Pause live updates** retains a clearly labeled
historical snapshot and disables automatic refresh; **Refresh system** can
still fetch an explicit snapshot. **Animate observed activity** controls
motion independently. Reduced-motion preferences disable the one-shot
flow animations. Keyboard-scrollable diagrams and tables show all current
replicas without an eight-entry limit. Healthy and draining processes appear
in the live topology and main process table. **Inactive process observations**
holds stopped and missed-heartbeat history in a collapsed section, so retired
rollout processes cannot push live replicas out of the diagram. An expired
heartbeat is not proof that a process stopped.

Request/response pulses identify the actual API process from its response
header. Worker pulses require an observed heartbeat change or an increase in
assigned work between snapshots. First observations and unchanged counters
do not manufacture worker traffic. Sends to other live clients are reported
on the API process, not animated as messages to this browser.

`GET /api/v1/admin/system` requires the global `Admin` application role on
every request. A team administrator is not a system administrator. Responses
are `no-store`; an unavailable database returns `503`, never an empty healthy
system. Query parameters are rejected.

Apply migration `0048_runtime_instances.sql` through the normal migration job
before starting the new application image. It adds only a new table and its
index; it does not rebuild indexes on existing task/run partitions.

## Process identity and placement

Every API/controller process generates a new immutable instance ID. That same
ID identifies its engine lease. Restarting a process creates a new row, even
when its pod or VM name stays the same. Our default names begin with `kardia-`;
these are application processes, not Kubernetes control-plane components.

| Configuration | Meaning | Default |
| --- | --- | --- |
| `Runtime__Name` | Recognizable application instance name | `kardia-controller-<host>` or `kardia-api-<host>` |
| `Runtime__HostName` | Process host/container hostname | OS machine name |
| `Runtime__NodeName` | Physical/virtual host or Kubernetes node | `NODE_NAME`, then host |
| `Runtime__PodName` | Kubernetes pod, when applicable | `POD_NAME`, otherwise absent |
| `Runtime__Namespace` | Kubernetes namespace, when applicable | `POD_NAMESPACE`, otherwise absent |

Kubernetes deployments should inject `NODE_NAME`, `POD_NAME`, and
`POD_NAMESPACE` from the Downward API. Native/VM services can set the equivalent
`Runtime__*` settings directly. Labels must contain 1..200 characters, without
control characters or surrounding whitespace. No Kubernetes API permission
is needed for application inventory.

An `ApiController` process serves HTTP and participates in controller
leadership; an `Api` process does not participate in that lease. Multiple
controllers do **not** imply active-active scheduling. `isLeader` comes from
the unexpired database lease and a fresh, ready process heartbeat.

On the host stopping signal, a controller immediately stops admitting new
engine work and advertising itself as a leader, before HTTP/gRPC listeners
finish draining. Its lease continues renewing only to flush already accepted
events under their captured epoch; the existing shutdown sequence then releases
it. This prevents new attempts receiving upload URLs for a terminating listener.

## Freshness and counts

Processes publish database-timestamped heartbeats every five seconds.
Heartbeats expire after 15 seconds. `Draining` means the process has withdrawn
readiness; `Stopped` means graceful shutdown was recorded. A crash cannot
record its own shutdown and becomes `Unhealthy` after heartbeat expiry.
Process rows remain visible for up to 24 hours and are cleaned by subsequent
heartbeats. These states describe application observations, not an independent
infrastructure health probe.

Each response uses one repeatable-read transaction and one database observation
timestamp. Worker rows use existing durable agent identities and heartbeats.
An assigned task is a `Dispatched` or `Running` attempt; running tasks count
only `Running`. A DAG node is the parent node path in the stored
`node/task` execution key, distinct within a run. Multiple tasks within one
node therefore count as multiple tasks but one active DAG node. Run and
workflow counts are distinct per worker, not additive across workers.

Assignments on an unhealthy worker remain visible until recovery/fencing
settles them. Counts do **not** assert that those task processes are alive.
Cluster active-workflow totals include queued, running, and aborting runs;
these differ intentionally from per-worker assigned-workflow totals.

HTTP counters count completed API/live request handling, including failures,
but exclude health probes. `X-Kardia-Instance` identifies the process handling
an API/live request. Live connection/subscription counts are process-local;
live deliveries count successful SignalR sends, not browser acknowledgements
or packet capture. Counters reset only with a new process ID and reach the
inventory on the next heartbeat.

Desired replica counts, network packet rates and unobserved machine health are
not fabricated. Kubernetes pod placement and RAM use are separate observations,
not inferred from application heartbeat counts.

## Kubernetes RAM

The **K3s / Kubernetes RAM** section reads actual node and application-pod memory
working sets from the Kubernetes Metrics API. It refreshes every 15 seconds,
independently of the two-second application snapshot, and follows the same
pause/permission boundaries. **Refresh RAM** explicitly refreshes even while
paused. Node rows show used memory, physical capacity and allocatable memory;
pod rows include frontend, API, active/passive controller and worker pods,
their host nodes, readiness and measurement timestamps. Kubernetes pod metrics
do not themselves identify the database lease leader; use the controller
topology for active/passive status.

This means **RAM**, not persistent storage, a saved checkpoint, a container
memory reservation or arbitrary process-memory migration. Node working set
includes K3s/system workloads, so it need not equal the sum of application
pods. Missing samples remain absent, not zero. Samples older than 60 seconds,
implausibly future-dated samples and stalled live snapshots are labeled stale
or hidden. A current Kubernetes request failure hides old RAM readings while
leaving independently healthy application observations available.

`GET /api/v1/admin/system/kubernetes-memory` requires global Admin, rejects
query parameters and returns `no-store`. Disabled or failed observation returns
`503`, not a healthy empty cluster. The API uses bounded requests, verifies the
Kubernetes service TLS certificate against the projected cluster CA, never
follows redirects with its bearer token, and rereads the short-lived projected
token for rotation. Only metadata, readiness and RAM measurements leave this
endpoint; the token and CA private keys never reach browsers.

Enable `Kubernetes__Enabled=true` and `Kubernetes__Namespace=<app-namespace>`
only on API replicas with the projected service-account CA/token mounted at
`/var/run/secrets/kubernetes.io/serviceaccount`. Metrics Server must already be
available. `infra/ha-lab/observer.yaml` grants `get/list` on pods and pod metrics
in `kardia`, plus node metadata/node metrics cluster-wide. No secret reads,
exec, logs, node proxy, writes or other-namespace pod access are granted.
`deploy.ps1` applies those named read-only roles before rolling the API.
Controllers and workers retain disabled token automount and do not receive
these observation permissions. CA replacement requires an API rollout.

Non-Kubernetes VM/native deployments leave this optional integration disabled;
the RAM panel reports that it is not configured rather than claiming K3s
measurements exist. Application process/work counts continue independently.

## Deployment transparency

For the fixed-worker constraints, long-task limitations and proposed maintenance
policies, see the [deployment review](deployment-review.md). That document
records open decisions; it does not change the release behavior described here.

**Live system -> Deployments** (`/admin/deployments`) is a separate Global
Admin-only, read-only view. It never executes a deployment, rollback, pod
replacement or worker drain. Actual system retains its topology, RAM and notice
editor. Two independent panels refresh every five seconds while visible;
**Pause deployment updates** stops automatic polling, and **Refresh deployments**
refreshes both even while paused. Permission revalidation hides cached data.
A failed panel hides its previous data without masking the other panel.
Rollout observations expire after 20 seconds and history snapshots after
20 seconds, including when paused; stale data is not left looking current.

### Actual Kubernetes rollout

`GET /api/v1/admin/system/deployments` observes the fixed `kardia-api` and
`kardia-frontend` Deployments, `kardia-controller` StatefulSet and
`kardia-worker` DaemonSet. Each workload shows its desired image map,
generation/observed generation, desired/updated/ready/available counts and
the number of current pods matching all target container/init-container images.
`In sync` requires matching images and counts plus an observed generation.
`Ready` by itself does not prove the target version is running.

With `OnDelete`, applying a new desired image leaves existing pods untouched:
**Awaiting replacement** is intentional staging, not a completed release.
Known image-pull, container-configuration, crash-loop, scheduling and rollout
deadline failures are **Blocked**; unobserved generations are **Unknown**.
Other incomplete rollouts are **Updating**. API reads occur separately;
this is a bounded observation, not an atomic cluster snapshot.

Expandable pod rows show actual container image/image ID, readiness, node,
restart count, state/reason and exit code. Successfully terminated init
containers are distinct from pod readiness. Migration jobs show the newest
20 `kardia-migrate-*` jobs still retained by Kubernetes; job TTL cleanup can
remove older evidence. Failed attempt counts are not independently labeled
as a terminal job failure. No logs, commands, environment variables, secret
values or arbitrary Kubernetes condition messages reach the browser.

The endpoint requires global `Admin`, rejects query parameters, returns
`no-store`, and shares the optional `Kubernetes__Enabled`/namespace integration
and verified projected CA/token transport described above. Disabled
observation, failed reads, missing resources or truncated lists return `503`,
never a healthy empty cluster. Workload/pod/job responses have bounded size,
list length and an eight-second request budget. `observer.yaml` adds only
namespace-scoped `get` for those four named workloads and `get/list` for jobs;
existing pod and Metrics API permissions remain. Other namespace jobs/pods,
secrets, exec, logs and Kubernetes writes remain unavailable.

### Recorded deployment activity

Apply additive migration `0051_deployment_activity.sql` before the new API
starts. `deployment_operations` and ordered `deployment_activity` events
live in shared PostgreSQL, not browser memory or per-replica files.
`GET /api/v1/admin/deployment-activity` returns the latest 20 operations with
their steps, immutable target tag, Stage/Release mode, initiating principal,
timestamps and reported status. Records remain stored beyond the UI's recent
20-operation window; no automatic retention cleanup is added.

These are **script reports**, not an independent assertion that Kubernetes
finished deploying. Stage success means staging completed; OnDelete roles may
still be on older images. Release reports include controller replacement,
physical worker drain, replacement, explicit resume and final verification.
Running operations silent for over 600 seconds are **Unconfirmed; last
reported Running**, never silently converted to success or failure.
Empty history does not mean no deployments occurred. Changes made outside
the instrumented scripts, earlier deployments and pre-reporting bootstrap
steps are not fabricated.

The scripts start an operation using
`POST /api/v1/admin/deployment-activity` with
`{operationId, mode, target}`, then append
`POST /api/v1/admin/deployment-activity/{id}/events` with
`{sequence, phase, subject, outcome, description}`. Global Admin and a valid
principal object ID are required; only the same initiating object ID **and**
User/App actor kind can append. Start IDs are canonical lowercase UUID
strings; sequences are contiguous 1..128. Exact retries are idempotent;
changed retries, sequence gaps and new steps after terminal completion
return `409`. Complete/Succeeded and Failed/Failed terminate an operation.
Each mutation and its `deployment.start`/`deployment.report` audit record
commit atomically. Storage failures return `503`, with no partial success.
JSON rejects unknown/duplicate/missing properties and bodies above 8 KiB.
Descriptions are bounded plain text, not command output or secrets.

See `infra/ha-lab/README.md` for one-time reporting bootstrap and the existing
drain-aware staging/release commands. Non-Kubernetes/native deployments can
still read recorded history when upgraded; absent Kubernetes observation
remains explicitly unavailable. No Azure/native release instrumentation is
implied by the isolated-lab scripts.

## Global system notice

Global Admins manage **System notice** on Actual system, independently of topology
polling and animation controls. Choose **Create system notice** or **Edit system
notice**, enter a plain-text message (1-2000 characters), select Information,
Warning or Critical, optionally choose a future expiry in your browser's local
time, then **Publish notice**. Blank expiry keeps the notice until cleared.
**Clear notice** requires confirmation. Changes appear immediately in the
publishing browser and within 30 seconds on other active, online signed-in pages.

The bar sits above navigation on every signed-in page. It is not dismissible
per user; HTML and URLs remain literal text. Information and warning updates use
a polite status region; critical updates use an alert without taking keyboard
focus. No default or test incident is published. An absent notice does not mean
the system is healthy; there is no automatic incident detection, scheduled start,
email delivery or independent external status page.

`GET /api/v1/system-notice` requires authentication, including for non-Admin
users. It returns a decimal-string `revision`, `notice` (or `null` when cleared
or expired), `canManage` (global Admin only), and a strong `ETag`. Writes require
global `Admin` and a valid principal object ID:

```http
PUT /api/v1/admin/system-notice
Content-Type: application/json
If-Match: "0"

{"message":"Describe the actual issue or maintenance.","severity":"Warning","expiresAt":null}
```

`DELETE /api/v1/admin/system-notice` uses the same `If-Match` header and no body.
Use the exact ETag from the current read, not the example value. Missing
preconditions return `428`; stale revisions return `412` without mutation.
The editor preserves a conflicting draft. Review the current bar, then explicitly
**Reload latest notice (keep draft)** before retrying. PUT requires all three
properties, rejects duplicate/unknown fields, invalid control characters and
past expiry, and limits JSON to 16 KiB. All routes reject query parameters and
return `no-store`.

Apply additive migration `0050_system_notice.sql` through the normal migration
job before the new API image. One empty singleton lives in shared PostgreSQL,
not process memory or browser storage. Each publish/clear increments its revision
under a row lock and commits the notice together with an audit event
(`system_notice.publish` / `system_notice.clear`, target `SystemNotice/global`,
actor, before/after, revision). Audit failure rolls back the change. Expiry hides
the message on reads and in already-open browsers without requiring a cleanup
job; it does not create a synthetic administrator audit event.

Database failure is an explicit `503`, never a healthy empty response. Browsers
keep an unexpired last-received notice during a refresh failure, while labeling
it unverified and offering **Retry notices**. Offline or stalled snapshots older
than 90 seconds are also labeled; an unavailable initial read reports unknown
notice status. A page that is closed, suspended or cannot reach this application
cannot receive a new notice. Keep a separate out-of-band communication channel
for a full application outage.

## Browser regression checks

The opt-in integration check starts an isolated PostgreSQL fixture, signed
test identities and an ephemeral frontend. Controlled fixture assignments
and real registry heartbeats verify the UI; this is not a VM failover or
physical-task execution test.

```powershell
$env:WF_SYSTEM_BROWSER = 'true'
$env:WF_FRONTEND_ROOT = (Resolve-Path .\frontend).Path
dotnet test .\backend\tests\Wf.Integration.Tests\Wf.Integration.Tests.csproj `
  -c AgentValidation --filter FullyQualifiedName~System_inventory_browser_real
```

Existing frontend dependencies and installed Playwright Chromium, Firefox
and WebKit engines are required. Checks include Admin/Viewer boundaries,
automatic updates, pause/resume, reduced motion, responsive containment,
light/dark contrast, and hiding cached health on an API outage.
