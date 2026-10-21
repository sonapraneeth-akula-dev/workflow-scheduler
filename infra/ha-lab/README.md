# Isolated Hyper-V K3s lab

Run from an elevated PowerShell 7 terminal:

```powershell
.\infra\ha-lab\start.ps1
.\infra\ha-lab\start.ps1 -Action Status
kubectl --kubeconfig .\.wf\ha-lab\kubeconfig get nodes -o wide
```

Creates only `kardia-lab-01`, `kardia-lab-02`, and `kardia-lab-03`, each with
2 vCPUs, 3 GiB static RAM, and a 40 GiB growable disk. Requires
Hyper-V, Docker Linux containers, OpenSSH, kubectl, an existing `Default Switch`,
and at least 13 GiB free RAM for a fresh lab. It does not change physical NICs,
host NAT/firewall rules, Docker settings, other VMs, or the native application.
Creates `kardia-ha-lab-private`, a **private** VM-only virtual switch with no
host adapter. Each VM keeps its Default Switch uplink for downloads and host
management, plus `kardia0` with stable addresses `192.168.247.11` through `.13`.
K3s/etcd and Flannel advertise only those private addresses. Default Switch DHCP
changes therefore do not change etcd member addresses.

The three Linux machines run K3s servers with embedded etcd. K3s components stay
in `kube-system`; application resources use the `kardia` namespace and
`kardia-*` names. The script provisions the platform, not application resources.
The script also spreads three CoreDNS replicas across the three hosts so that
losing node 01 does not remove the cluster's only DNS server.
One stopped server leaves two members for etcd quorum. This is a local lab,
not a production storage or infrastructure availability guarantee.

Ubuntu 24.04 cloud image build `20261017` and K3s `v1.36.5+k3s1` are pinned.
The latter was the stable channel version when selected on October 21, 2026.
An isolated tools container verifies Canonical's signed SHA256 manifest, converts
the generic QCOW2 image to VHDX, and creates NoCloud seed ISOs. Azure-specific
VHDs are deliberately not used for local Hyper-V. K3s binaries are checked
against their versioned release checksum. Secure Boot uses Microsoft's UEFI
certificate authority template.

All local state lives under the Git-ignored `.wf\ha-lab`, ACL-restricted to the
current Windows user, administrators, and SYSTEM. It contains private SSH keys,
the cluster token, seed files, VM disks, and an administrator kubeconfig.
Never commit or publish it. SSH uses trust-on-first-use for the isolated guest
network, then pins host keys by VM name in the lab's own `known_hosts`, independently
of changing DHCP addresses. A changed key fails
closed. Preserve these files; `Up` refuses to regenerate credentials for existing
VMs or modify a same-named VM owned elsewhere.

`Up` is repeatable and starts stopped lab VMs; it does not upgrade K3s or recreate
disks. Default Switch DHCP addresses may change after host reboots; rerun `Up` to
refresh this lab's kubeconfig. TLS verifies the shared `kardia-ha-lab` certificate
SAN, not the changing management address; certificate verification is never
disabled. The default kubectl context remains untouched.
The kubeconfig initially targets node 01; if it is down, select another live
lab node's management address with both kubectl options:
`--server=https://<address>:6443 --tls-server-name=kardia-ha-lab`.

```powershell
.\infra\ha-lab\start.ps1 -Action Stop
```

`Stop` requests guest shutdown, retaining disks and identity. Drain application
work before using it once application workloads are installed. There is no
automatic delete command.

Sources: Canonical's Ubuntu cloud-image artifact and checksum documentation,
cloud-init's NoCloud datasource reference, and K3s's HA embedded-etcd and server
configuration references. Do not use this local lab as a production Azure
profile; this script creates no cloud resources.

The browser's explicit `ha-lab` build profile permits login-free access only at
`127.0.0.1`. It does not expand the Local Compose hostname allowlist or enable
a bypass in production Entra builds. Never expose this lab frontend publicly.

## Shared lab data

After starting the VMs, run in PowerShell **7.4 or newer**, with Azure CLI installed:

```powershell
.\infra\ha-lab\data.ps1
```

Creates only the `kardia-ha-lab-data` Docker Compose project: PostgreSQL
`18.6-alpine`, Azurite `3.37.0`, and Caddy `2.11.7-alpine`. Existing same-named
resources must carry this lab's identity label; foreign resources are refused.
PostgreSQL and blobs use named durable volumes outside the VM failure domain.
**The Windows host and Docker remain storage single points of failure.** This
tests application/VM failover, not database or storage HA. Production uses managed
PostgreSQL and Azure Blob Storage instead of this profile.

PostgreSQL port `55432` and blob HTTPS port `55443` bind only loopback and the
existing Default Switch host address. No host firewall or NAT rules are changed.
All remote PostgreSQL connections require TLS and SCRAM authentication. The
`kardia` application login owns `workflow`, allowing application migrations and
partition maintenance, but has no superuser, role-creation, or database-creation
privileges. Its password differs from the bootstrap administrator's password.
Blob authentication uses a private local emulator account, not an Azure account.
The CLI contacts only the explicit local HTTPS endpoint; no cloud login or
resource provisioning occurs.

Secrets, private CA, leaf certificates, Compose environment, and application
environment remain in ACL-protected, Git-ignored `.wf\ha-lab\data`. The CA private
key is **never** mounted into containers. Application containers need only the
CA certificate, protected `application.env`, and a `kardia-lab-data` host alias
pointing at the Default Switch host address. Database clients use `VerifyFull`;
blob clients must trust that CA. Emulator configuration requires an explicit
Development application environment; it is not a production authentication
shortcut.
The emulator's SDK endpoint uses the Default Switch host **IP**, included in the
server certificate. Do not replace it with `kardia-lab-data`: the .NET SDK treats
DNS-hosted URLs as production-style addresses and can discard the container
segment when constructing blob paths. PostgreSQL still uses the verified DNS
alias.

Rerunning preserves credentials, volumes, and CA identity. It refreshes host
bindings and renews the 30-day server certificate when needed, recreating only
this data project's containers during certificate renewal. Do this before
application work or during a maintenance window. After a host networking change,
rerun the script and refresh application host aliases. Missing credentials or CA
for existing data fail closed; restore protected state rather than regenerate it.
This script provides neither backups nor an automatic trust/key rotation system.

Inspect this project's services without printing secret configuration:

```powershell
docker compose --project-name kardia-ha-lab-data `
  --env-file .\.wf\ha-lab\data\compose.env `
  --file .\infra\ha-lab\data.compose.yml ps
```

PostgreSQL TLS configuration follows its version 18 SSL and `pg_hba.conf`
references; Azurite path-style account handling follows its official README.

## Application roles

With the platform and shared data ready, deploy from an elevated PowerShell
7.4+ terminal:

```powershell
.\infra\ha-lab\deploy.ps1 -ImageTag ha-lab-local-1
.\infra\ha-lab\forward.ps1
```

Open `http://127.0.0.1:5180/admin/system`. The forwarder binds **only**
`127.0.0.1`, reconnects when its frontend pod is replaced, and chooses another
owned, TLS-verified K3s API server when one is unavailable. It does not change the
default kubectl context. Native ports `5000`, `5001`, and `5173` remain untouched.
Keep the forwarder running while using the browser; Ctrl+C stops that forwarder.

`deploy.ps1` builds the existing application Dockerfiles, imports the same
Linux/amd64 images into all three machines, runs migrations once before applying
the application resources, and checks the **actual runtime inventory** before
reporting success. `-SkipBuild` reuses images already built with that tag.
Imported tags are immutable: use a new tag for a new build. Archives and image
identities are retained in protected `.wf\ha-lab\images`.
The public .NET package mirror is used for the lab's control-plane build.

All application resources use namespace `kardia` and `kardia-*` names:

| Role | Kubernetes workload | Placement and state |
| --- | --- | --- |
| Frontend | Deployment, 2 replicas | Separate hosts; same-origin TLS proxy to APIs/controllers |
| API | Deployment, 2 replicas | Separate hosts; private-address HTTPS port 8443 |
| Controller | StatefulSet, 2 replicas | Separate hosts; unique HTTPS endpoint on port 8444; one database lease leader, one standby |
| Worker | DaemonSet, 1 per machine | 2 task slots each; persistent UUID, result journals, logs, package cache and local secret directory |

API/controller host networking reuses the application's existing explicit trusted
Development LAN listener; loopback ports differ between the roles to prevent
collisions. Every private node IP and controller/service DNS name is covered by
the protected application certificate. Only the public CA certificate and the
required leaf key are mounted, never the CA private key. The frontend and workers
validate upstream certificates. This is intentionally a login-free **isolated
local lab**, not an Azure production configuration.

Workers retain `/var/lib/kardia-ha-lab/worker` on each owned VM across pod
replacements. Missing identity alongside existing state fails rather than
silently registering a different worker. Permanently losing a disk loses that
worker's local journal; durable controller work and retry policy, not copied RAM,
govern placement elsewhere.

API/frontend updates use readiness-gated rolling deployments with zero planned
unavailable replicas and one surge replica. Pod disruption budgets retain a
replica during voluntary evictions. Controllers/workers use **OnDelete**:
applying a new image does not interrupt their live processes. A different tag on
an installed lab is refused unless `-StageOnly` is explicit. That option updates
API/frontend deployments and desired controller/worker images; it does **not**
complete a controller/worker rollout or re-enable a drained worker. Do not use it
as an unattended release procedure. Migrations still need an expand-only,
backward-compatible design; this script cannot make arbitrary schema changes
safe to roll back.

For an installed lab, stage a new immutable tag, then complete the sequential
rollout:

```powershell
.\infra\ha-lab\deploy.ps1 -ImageTag ha-lab-local-2 -StageOnly
.\infra\ha-lab\release.ps1 -ImageTag ha-lab-local-2 -MaxDrainSeconds 300
```

Deployment transparency is under **Live system -> Deployments**. **Actual
system** retains runtime topology, RAM and the system-notice editor. Global
Admins see actual desired/updated/ready images, pod image IDs and container
reasons/restarts, plus retained migration jobs and recent script-reported
Stage/Release steps. Stage success is not Release success: an OnDelete workload
can remain **Awaiting replacement** with healthy pods on its previous image.
The browser provides no deployment controls.

When first upgrading a lab whose API predates deployment reporting, explicitly
bootstrap it once:

```powershell
.\infra\ha-lab\deploy.ps1 -ImageTag ha-lab-reporting-1 -StageOnly -BootstrapDeploymentActivity
.\infra\ha-lab\release.ps1 -ImageTag ha-lab-reporting-1 -MaxDrainSeconds 300
```

Bootstrap is accepted only when the old reporting endpoint returns `404`.
It does not bypass authorization/storage/transport errors and is refused once
reporting exists. Earlier build/import/migration steps are explicitly
unrecorded; reporting starts after the new API/frontend rollout.
Initial installation has the same honest pre-API gap. Normal subsequent
staging/release requires working reporting before further deployment steps.
The migration job applies `0051_deployment_activity.sql`; the shared workflow
PostgreSQL database stores operation heads and ordered events atomically with
audit. The existing local `deployment.json` remains the installed-image guard,
not the browser's activity history.

Scripts report through the existing owned-cluster kubectl/loopback development
boundary; they do not add credentials to the browser or require Kubernetes
writes from the observer account. Only the script's initiating Admin
principal can append its operation. Static bounded step descriptions exclude
command output and secrets. Failures try to record a terminal interruption,
then stop without continuing rollout steps. If reporting also fails or the
script is killed, the last Running record remains **Unconfirmed** after ten
minutes, never manufactured completion. An interrupted report may have committed;
the API supports exact sequence retries, while rerunning the script creates
a new operation and uses the existing safe resume/preflight rules.

The observer adds only namespaced read access for the four fixed workloads
and migration-job inventory; controllers/workers still have no observer token.
Five-second UI refresh, explicit pause/manual refresh and 20-second stale
boundaries keep observed rollout and recorded activity independent. Empty
history means no recorded operations, not no earlier deployments. Details:
[live system inventory](../../docs/operations/live-system-inventory.md).

`release.ps1` checks ownership, desired images and healthy replicas before
replacing anything. It updates the observed standby controller before the lease
leader, waiting for two healthy controller processes and one live leader after
each replacement. Each worker receives an audited, process-identity-capable
drain. The script waits for **physical Released acknowledgement**, not zero
counters or a timer, then replaces only that exact pod UID, retains the host's
worker state and resumes using the exact drain receipt. Resume requires a
different, freshly registered idle process. Work exceeding the drain deadline
is stopped and follows its original retry/placement policy; non-retryable tasks
are not promised transparent recovery.

Failures stop the sequence without automatically re-enabling drained workers
or declaring a completed deployment. Fix the cause, then rerun `release.ps1`
with the **same staged tag**: completed healthy workers are skipped, and only
drains bearing that release's reason are continued. Another operator's drain
is refused. The durable deployment record changes only after actual images,
readiness and runtime inventory agree. Do not concurrently stage another
release. Legacy workers without process identity support are refused before
draining; upgrade them only during an independently verified quiescent
maintenance window.

`/admin/system` reports observed process/worker heartbeats and durable task/run
counts, with the real responding API and observed traffic deltas. It does not
invent frontend placement or make a healthy-lease claim from a Kubernetes pod's
Running status. `kubectl --kubeconfig .\.wf\ha-lab\kubeconfig -n kardia get pods -o wide`
shows the full Kubernetes placement, including the frontend pods.
The live diagram shows all current controller replicas, explicitly labeled
active leader or passive standby; stopped/missed-heartbeat process history is
collapsed separately rather than counted as desired replicas.

The **K3s / Kubernetes RAM** section displays measured memory working sets for
all three nodes and every application pod, including frontends and passive
controllers. Node physical/allocatable memory, pod placement/readiness and
sample times are shown. Readings refresh every 15 seconds; unavailable/stale
samples never become invented zero usage. `observer.yaml` gives only API
replicas read-only namespaced pod/pod-metrics and cluster node/node-metrics
access through a rotating projected token and verified cluster CA. Workers
and controllers receive no Kubernetes API token. This is RAM monitoring,
not persistent storage or checkpoint replication.

## Actual checkpointed work

The optional `smoke` workflow creates three parallel DAG nodes and six real SDK
tasks, restricted to the `kardia-lab` Linux pool. It saves an integer progress
checkpoint after each five-second step and records completed steps, resumed
steps, attempt and machine in task outputs. It performs no external side
effects and creates no schedule. Its registry is explicitly `Demo`; do not
publish it into production.

With the loopback forwarder healthy, publish from the repository root:

```powershell
$env:WF_ACCESS_TOKEN = 'local-development'
$env:SSL_CERT_FILE = (Resolve-Path .\.wf\ha-lab\data\certs\ca.crt).Path
dotnet run --project .\backend\src\Wf.Cli -- publish .\infra\ha-lab\smoke `
  --engine http://127.0.0.1:5180 --git-commit (git rev-parse HEAD) `
  --git-author 'HA lab operator' --input .\backend\src\Wf.Sdk `
  --input .\backend\src\Wf.Contracts --input .\backend\src\Wf.Engine.Core `
  --input .\backend\src\Wf.Validation --work-directory .\.wf\ha-lab\smoke-publish
Remove-Item Env:\WF_ACCESS_TOKEN
Remove-Item Env:\SSL_CERT_FILE
$body = @{
  workflowId = 'ha-lab/acceptance/recovery'
  version = '1.0.2'
  variables = @{ steps = 24 }
} | ConvertTo-Json
Invoke-RestMethod http://127.0.0.1:5180/api/v1/runs -Method Post `
  -ContentType application/json -Body $body `
  -Headers @{ 'Idempotency-Key' = "lab-$([guid]::NewGuid().ToString('N'))" }
```

`steps` accepts 1..60, default 24 (approximately two minutes per task).
Watch `/admin/system` and the run's graph, timeline, logs and outputs. Verify
the final logical task statuses and resumed progress, not merely a healthy
replacement pod. A checkpoint is application-owned persisted state, not a
snapshot of arbitrary memory. Multiple failures still consume the configured
retry budget; deliberately killing every worker repeatedly can exhaust it.

### Verified live recovery

On October 21, 2026, the real six-task SDK fixture ran across all three lab VMs,
using shared PostgreSQL/Azurite over verified TLS, not mocked task assignments:

| Scenario | Durable run ID | Observed result |
| --- | --- | --- |
| Sequential controller/worker rollout, one-second worker drain deadlines | `01a15e47-7d36-77d4-96a0-3fa9db1fda72` | All six logical tasks succeeded and resumed checkpoint progress; all 36 steps completed. Role replacement took 82.74 seconds; workload finished 370.77 seconds after release began. |
| Forced power-off of `kardia-lab-01`, hosting the active controller and two running tasks | `01a15e4e-10be-7ab8-9090-72044bff0caf` | Another controller held a higher fenced epoch after 11.41 seconds. Both crashed tasks resumed checkpoint progress on other workers; all six logical tasks completed all 18 steps. Workload recovery took 322.03 seconds. |

The faulted VM was restarted, its management address refreshed, and all three
nodes/workers returned healthy. The other VMs were not restarted. The loopback
admin view was available after restoration. Local, Git-ignored
`roll-evidence.json` and `fault-evidence.json` retain per-attempt identity,
checkpoint and output evidence. Times are observations, not recovery SLOs:
lease expiry, heartbeat-loss policy, retry backoff and remaining capacity all
affect recovery. Surviving tasks can also retry after controller-bound upload
capabilities become stale; this is checkpoint recovery, not uninterrupted RAM
continuation or exactly-once external side effects.

The checks exposed and fixed resource blob I/O holding the global allocation
lock and a terminating controller still admitting work after its HTTP listener
closed. Resource publication now rechecks authority around blob I/O outside
fenced transactions; shutdown withdraws new engine admission immediately while
allowing already accepted durable events to flush.

## Azure production instances

Use `..\azure-vm\README.md` for the separate production profile: named private
zonal frontend/API/controller/agent VMs, immutable application images,
scoped managed identities, versioned Key Vault configuration and Entra/TLS.
It reuses the application's shared storage, lease fencing, durable journals,
checkpoint recovery and explicit drain/resume protocol; it does not require
K3s or copy this lab's login bypass and emulator credentials.

The Azure templates, image builds and isolated installer/TLS/authentication
checks were validated locally. Azure resources were **not** provisioned;
live Entra/IMDS/RBAC, Azure networking and cloud VM/data-service failover require
an authorized nonproduction deployment before promotion. Follow the
one-at-a-time rollout procedure there; the initial installer deliberately
refuses to replace an existing role container or silently discard worker state.
