# PostgreSQL major upgrades and local blocker review

Reviewed: October 21, 2026. Scope: PostgreSQL 17 to 18, the native development
stack, and the five previously recorded blockers. Production procedures below
are a proposed runbook, not a claim of production migration acceptance.

## Large production databases: move the copy outside the outage

Recommended starting point: build a separate PostgreSQL 18 cluster, copy data
while 17 continues serving traffic, replicate subsequent changes, then briefly
fence writes and switch the application. Logical replication works between
major versions [1, 2]. It still needs an initial full data transfer, target
storage and catch-up capacity; it removes that transfer from the cutover
window, not from the migration.

| Method | Data-size cost | Write interruption | Best fit |
| --- | --- | --- | --- |
| Logical replication / managed blue-green equivalent | Background initial copy, ongoing changes, second cluster | Final fencing, catch-up, validation and endpoint switch | Tight downtime budget; compatibility and replication prerequisites satisfied |
| `pg_upgrade --clone` | Fast filesystem clones where supported | Stop both clusters for upgrade and validation | Same compatible host/storage; reflink-capable filesystem |
| `pg_upgrade --link` | Reuses data files with little extra data storage | Stop both clusters for upgrade and validation | Short outage acceptable; independent recovery copy available |
| Logical dump/restore | Full export, transfer, restore and index build | Potentially long if all work occurs after stopping writes | Small databases, disposable environments, or a generous outage budget |

`pg_upgrade --check` must pass for the selected transfer mode. Clone mode needs
a supported filesystem; link mode requires the old and new data directories
on the same filesystem. After starting the upgraded link-mode cluster, the
old files cannot safely serve as the original cluster. Install compatible
extensions/binaries and follow the tool's post-upgrade instructions [3].
Do not assume a generic PostgreSQL 18 image contains the PostgreSQL 17 binaries
and extension libraries required by `pg_upgrade`.
For 17 to 18, match cluster checksum settings: PostgreSQL 18 enables checksums
by default. Review collation-sensitive reindexing and other release-specific
incompatibilities before the rehearsal [10].

### Backups without another huge logical dump

Migration and recovery are different requirements. A replica or a retained
old cluster is not an independent disaster-recovery backup.

- Maintain a tested physical base-backup plus archived WAL/PITR chain. This
  avoids repeated full logical exports; changed WAL still consumes storage [4].
- Evaluate PostgreSQL's incremental physical backups where the existing
  backup system supports them. Configure WAL summarization and sufficient
  summary retention [11]. Keep the required backup chain and rehearse
  reconstruction; incremental files alone are not a complete restore [5].
- Storage snapshots/clones can reduce additional copying for a rehearsed
  `pg_upgrade` path. Coordinate every data/tablespace/WAL volume for consistency;
  a snapshot on the same failing storage is not sufficient disaster recovery [3].

A PostgreSQL 17 physical backup restores PostgreSQL 17. It cannot simply be
attached to a PostgreSQL 18 server as a major-version conversion [1].
Set explicit RPO/RTO, recovery-storage headroom and a restore-test requirement
before choosing the migration method.

### Preparation while PostgreSQL 17 remains online

1. Inventory database size, write rate, large transactions, extensions,
   collations, encoding, roles, privileges, sequences, tablespaces, large
   objects, partition maintenance and non-table state. Rehearse on
   representative data and workload; check PostgreSQL 18 release compatibility.
2. Verify publication/replica identities for every table requiring updates or
   deletes. Decide how to handle partitioned tables and newly created
   partitions. Database schema/DDL, sequence state and large objects are not
   automatically transferred by built-in logical replication [6].
3. Prepare the target schema without seeding table data, plus roles, extensions,
   permissions and required external objects. Freeze application migrations and
   automatic partition DDL during migration, or explicitly coordinate each change on both clusters.
   Keep target application writers, schedulers and engines stopped.
4. Configure the publisher for logical WAL, replication slots and senders;
   size subscriber workers and initial-copy concurrency [7]. Monitor copy
   progress, apply errors, long transactions, WAL retention and disk space.
   Plan any required source configuration restart before the cutover window.
   Reserve WAL headroom against measured write rate and worst-case copy time.
   Abort/reseed before retained WAL threatens source availability.
5. Complete the background table copy and catch up changes. Validate
   constraints, representative checksums/counts, query plans, permissions and
   application behavior before scheduling the switch. Full-table checks over
   huge data belong in the background rehearsal/validation, not the write pause.

### Short, fenced cutover

1. Pause new admissions, recurring fires and external producers. Drain running
   work, or use a rehearsed durable-checkpoint/recovery procedure. Stop every
   database writer: API mutations, schedulers, engines, migrations and agent
   result ingestion. Fence old credentials/traffic and drain old pooled
   connections; changing DNS alone does not prevent split-brain writes.
2. Wait for source write transactions to finish. Record a committed,
   replicated cutover marker or equivalent applied-progress watermark for each
   subscription. Require every table synchronized, no apply errors, and proof
   the target applied the final source commits. A near-zero lag dashboard
   alone is not the acceptance criterion.
3. Synchronize non-replicated sequence state and any explicitly supported
   non-table state. Verify critical constraints, pinned workflow versions,
   scheduled-fire positions and durable task/result checkpoints.
4. Disable the forward subscription after the final source changes are
   applied. Switch the connection endpoint, replace connection pools, and run
   bounded read-only checks before admitting target writes. Start exactly one
   scheduler/engine leadership domain; reconnect agents and resume producers.
5. Observe errors, latency, locks, duplicate/missed schedule fires, task
   recovery, report reads and replication-slot cleanup. Keep the source fenced
   and recovery assets until the agreed retention window expires.

Use a measured cutover budget and explicit abort points. Seconds or a small
number of minutes may be feasible, but are not guaranteed by this document.
Rehearsal must include the actual client reconnect and scheduler recovery path.

### Rollback boundary

For the separate-cluster logical-replication path, before target application
writes begin, a rehearsed rollback can switch back to the fenced, unchanged
source. Once PostgreSQL 18 accepts writes, PostgreSQL 17 is stale.
Do not point traffic back and silently discard those writes. Either roll
forward on 18, recover it using its tested backup/PITR procedure, or use a
separately designed and rehearsed reverse-replication/reconciliation plan.
One-way logical replication does not provide automatic failback.

### Workflow Scheduler-specific checks

The database is only part of this application's state. Preserve compatible
filesystem/object-store blobs, workflow packages, report payloads, local secret
configuration and agent identity/result-journal directories. Do not reset
these directories as part of a database upgrade.

Check workflow/version identities, historical runs, reports and sign-off audit
history; package/report downloads; recurring schedules and engine leadership;
agent reconnection and durable result replay. Verify these against the frozen
source, then observe resumed execution. Health HTTP 200 alone is insufficient.

For official Docker PostgreSQL 18, mount the volume at `/var/lib/postgresql`;
the default versioned data directory is `/var/lib/postgresql/18/docker`.
The older 17 layout used `/var/lib/postgresql/data` [8]. Changing only an image
tag does not convert the files. This repository's AppHost already selects
`18.6-alpine` and the matching volume mount.

## Native test migration performed on October 21, 2026

Although loss of this test data was acceptable, the source database was only
58 MB. A small custom-format export and separate target volume were simpler
than discarding its workflow/report fixtures.

| Item | Result |
| --- | --- |
| Previous database | PostgreSQL 17.11; volume `wf-native-main` |
| Current database | PostgreSQL 18.6; volume `wf-native-main-pg18` |
| Current container | `kardia-postgres-wf-native-main-pg18` |
| Data mount | `/var/lib/postgresql` |
| Preserved application state | `.wf\native-main`, including blobs and agent journals/identity |
| Local migration archive | Ignored `.agent-artifacts\postgresql18-migration-20261021\workflow-pg17.dump`; 6,499,823 bytes |
| Frozen-source/restore comparison | 9 versions; 511 runs; 2,629 reports; 19,759 run events; 0 sign-off events; exact match before target startup |
| Post-cutover observation | API leadership and frontend ready; existing agent reconnected; subsequent run events persisted on 18 |

The old volume was not deleted or attached to 18. The temporary migration
container was removed without removing the new named volume. The original
volume and archive are retained recovery assets, not lossless failback targets
after resumed writes.

Reuse the same data-root and **new** volume on later native launches:

```powershell
dotnet run --project .\backend\src\Wf.AppHost -- `
  "--Local:DataRoot=$PWD\.wf\native-main" `
  --Local:PostgresVolume=wf-native-main-pg18 `
  --Local:Monitoring=false
```

`Local:Monitoring=false` preserves the independently running monitoring
Compose stack. Omit it only when AppHost should own monitoring and the
monitoring ports are available. Leave unrelated databases/stacks untouched.

## Other recorded blockers: later-review ledger

These are operational states as observed on October 21, 2026, not all open
engineering work in the project.

| Tracking ID | Current state | Evidence / next review action |
| --- | --- | --- |
| `task-2-3-3-1-3` | **Blocked: isolated rebootable machine unavailable** | Needs an owned VM/machine, image and access. A container restart is not a machine-reboot test. Rehearse actual reboot during active tasks and verify durable recovery; the 1,000-run randomized nightly gate remains unclaimed. Do not reboot the shared workstation or Docker Desktop. |
| `task-network-metrics-unavailable` | **Blocked: native sampling not enabled** | Local AppHost does not set `Agent__Sampling__Enabled=true`; sampling is startup configuration. Later: enable it for the chosen agent, restart only when idle, run a bounded real task, and verify fresh persisted/API/UI measurements. An initial interval baseline may be absent; historical samples cannot be backfilled. Existing isolated native-accounting evidence does not prove this live profile is enabled. |
| `activate-service-run-cap` | **Resolved: stale restart prerequisite** | Current local API rejects `runLimit=31` with HTTP 400 and a documented maximum of 30. The control plane was rebuilt/restarted; no additional outage is required for activation. Keep the boundary regression tests. |
| `failure-live-activation` | **Resolved: stale restart prerequisite** | Current failed-run API rows contain persisted failure summaries, including `Infra/Network`, `Service/Timeout` and `Service/Validation`. Classification reads are active. Future rule/demo changes still require their own pinned-version scenario checks; this observation is not acceptance of every classification rule. |

The former fifth blocker, `container-live-migration`, is resolved by the
PostgreSQL 18 cutover above.

## Descriptive container names

Put the project, role and useful instance discriminator first. Native stack
identity comes from its volume or bound port; parallel disposable tests keep
a short unique suffix rather than dropping uniqueness entirely. Explicit
container names are global to the Docker daemon and can otherwise collide [9].

| Purpose | Name / rule |
| --- | --- |
| Native database | `kardia-postgres-<volume>`; current `kardia-postgres-wf-native-main-pg18` |
| Optional native Linux agent | `kardia-local-agent-linux-<API-port>`; default `kardia-local-agent-linux-5000` |
| Standalone monitoring | `kardia-monitoring-prometheus`, `kardia-monitoring-grafana` |
| Retained session-owned Linux SDK fixture | `kardia-test-linux-sdk-3adec91f` |
| Retained session-owned PostgreSQL 17 fixture | `kardia-test-linux-postgres17-3adec91f` |
| New isolated UI-test databases | `kardia-test-ui-postgres-<short-unique-id>` |

Linux agent container names no longer expose a full agent UUID; persistent
agent identity and volume naming remain unchanged. The two stopped fixture
containers were renamed in place without restarting them or changing mounts.
The main database's former random Aspire name disappeared with its old
AppHost instance; the new instance uses the source-configured descriptive name.

The screenshot's `wf-ui-24c7543c-...` PostgreSQL container belongs to a different
stack on port 50296. The unlabeled `cranky_mahavira` Alpine container mounts a
temporary directory outside this checkout; ownership was not established.
Neither was renamed, restarted or deleted. Confirm ownership and name-based
references before any later cleanup; changing these names blindly would hide
rather than solve an ownership problem.

## Primary references

1. [PostgreSQL 18: Upgrading a cluster](https://www.postgresql.org/docs/18/upgrading.html)
2. [PostgreSQL 18: Logical replication](https://www.postgresql.org/docs/18/logical-replication.html)
3. [PostgreSQL 18: pg_upgrade](https://www.postgresql.org/docs/18/pgupgrade.html)
4. [PostgreSQL 18: Continuous archiving and PITR](https://www.postgresql.org/docs/18/continuous-archiving.html)
5. [PostgreSQL 18: Full and incremental pg_basebackup](https://www.postgresql.org/docs/18/app-pgbasebackup.html)
6. [PostgreSQL 18: Logical replication restrictions](https://www.postgresql.org/docs/18/logical-replication-restrictions.html)
7. [PostgreSQL 18: Logical replication configuration](https://www.postgresql.org/docs/18/logical-replication-config.html)
8. [Docker Official Image: PostgreSQL volume layout](https://hub.docker.com/_/postgres)
9. [Aspire: Explicit container names and collision warning](https://aspire.dev/reference/api/typescript/aspire.hosting/withcontainername/)
10. [PostgreSQL 18: Release migration compatibility](https://www.postgresql.org/docs/18/release-18.html)
11. [PostgreSQL 18: WAL summarization](https://www.postgresql.org/docs/18/runtime-config-wal.html#RUNTIME-CONFIG-WAL-SUMMARIZATION)
