# Live system inventory

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

Frontend pods, desired replica counts, network packet rates, and machine
health that the application has not observed are not fabricated.
