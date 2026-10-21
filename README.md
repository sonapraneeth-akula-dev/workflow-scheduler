# Kardia Workflow Scheduler

Implementation follows `docs/plans/2026-10-15_workflow-scheduler_azure-task-list.md`.

## Local prototype stack

Requires .NET 10, Bun, installed frontend dependencies
(`cd frontend; bun install --frozen-lockfile`), and Docker with Linux containers
or an existing PostgreSQL 17+ database. From the repository root:

| Scenario | Launcher | Database |
| --- | --- | --- |
| Native API, agent and frontend; Docker database | AppHost command below | Persistent PostgreSQL container, managed automatically |
| Fully native; no Docker | Same AppHost with `ConnectionStrings__wf` set | Existing native PostgreSQL 17+ |
| Containerized local environment | `Wf.Cli local up --env dev` or `ppe`, below | Isolated Compose PostgreSQL and application containers |

For an isolated three-VM K3s platform, use `.\infra\ha-lab\start.ps1` from an
elevated PowerShell 7 terminal. See `infra/ha-lab/README.md` for requirements,
private networking, replicated application roles, preserved worker identity,
drain-aware releases and measured VM-crash checkpoint recovery. This provisions
a separate platform without restarting the native stack. The lab's live admin
view is `http://127.0.0.1:5180/admin/system` after starting its loopback forwarder.

For planned production Azure instances, `infra/azure-vm/README.md` supplies
private zonal VM templates, scoped managed identities, versioned Key Vault
configuration, production Entra/TLS images and a safe rollout runbook. These
assets were checked offline; no Azure resources have been provisioned.

```powershell
dotnet run --project .\backend\src\Wf.AppHost
```

For the currently migrated native instance, the single-command launcher is:

```powershell
pwsh -NoProfile -File .\start-local.ps1
```

Requires PowerShell 7 and the tools above, not the Aspire CLI. The script uses
this checkout's `.wf\native-main` state, the persistent `wf-native-main-pg18`
PostgreSQL 18 volume, API/engine/UI ports 5000/5001/5173, and leaves standalone
monitoring untouched. It runs in the foreground; **Ctrl+C** stops its AppHost
resources without deleting the database, blobs or agent identity. Do not start
a second copy while this instance is already running. External database
environment overrides are rejected rather than silently connecting this
profile to another database. The bare AppHost command above remains the
generic/default profile and may use different state and volumes.

With the Aspire CLI installed, `aspire run` from the repository root or `backend\src\Wf.AppHost`
starts the same application. The dashboard URL appears in the terminal.
The AppHost starts migrations, the control plane, one local agent process and
Vite; API health gates agent/UI startup, and failed migrations prevent host
startup. Open `http://127.0.0.1:5173` for the login-free prototype UI; the API
listens on port 5000 and the agent gateway on 5001. Development authentication
is explicitly enabled only for this loopback profile; production guards remain
unchanged. No Entra tenant or application login/logout setup is needed.

AppHost enables the durable hosted run executor: accepted runs advance through
their pinned workflow's lifecycle stages and DAG, dispatch real agent attempts,
commit outputs before dependent tasks run, and honor retry/cancellation policies.
Standalone control-plane launches retain the existing executor-off default;
set `Engine__ExecuteRuns=true` to enable it. `Engine__ApiEndpoint` may override
the package-download origin (HTTPS or loopback HTTP only); agents must be able
to reach it. Packages use short-lived, purpose-bound download capabilities,
not interactive authentication. A missing declared dependency output fails
inside the consuming SDK task unless its author handles the missing key; it
does not stop the control plane.

By default PostgreSQL uses the persistent Docker volume `wf-postgres-data`.
New Docker databases use `postgres:18.6-alpine`; the AppHost mounts the volume
at `/var/lib/postgresql`, with PostgreSQL 18 data in `18/docker`.
**Existing PostgreSQL 17 volumes require an explicit major-version migration.**
Do not attach an existing 17 data volume to the 18 image or delete it to make
startup succeed. Small disposable databases can use a verified dump/restore
into a separate volume. For large production databases, prefer an online
logical-replication migration with a fenced cutover, or evaluate `pg_upgrade`
for a short maintenance window; a full logical dump is not mandatory.
See the [PostgreSQL upgrade and blocker review](docs/operations/postgresql-major-upgrade-and-blockers.md)
for preparation, backup alternatives, cutover, rollback limits and the migrated
native test profile. Retain the original volume and matching filesystem blobs;
the old database is not a safe rollback target after new writes reach 18.
Updating these image pins does not migrate or restart a running database.

Native-profile container names describe their purpose and persistent identity:
`kardia-postgres-<Local:PostgresVolume>` (default
`kardia-postgres-wf-postgres-data`), `kardia-local-agent-linux-<API-port>`,
`kardia-local-prometheus-<port>` and `kardia-local-grafana-<port>`.
Standalone monitoring uses `kardia-monitoring-prometheus` and
`kardia-monitoring-grafana`. Compose retains project-scoped service names such
as `kardia-dev-postgres-1`; test/chaos/benchmark containers include their purpose
and a unique suffix so parallel runs cannot collide.

Docker image pins verified October 19, 2026:

| Component | Image tag |
| --- | --- |
| PostgreSQL | `18.6-alpine` |
| .NET build SDK / runtime | `10.0.402` / `10.0.13` |
| .NET Linux AOT build SDK | `10.0.402-noble-aot` |
| Caddy / Bun frontend builder | `2.11.7-alpine` / `1.4.3` |
| Azurite / Toxiproxy | `3.37.0` / `2.13.0` |
| curl Linux test client | `8.23.0` |
| Azure Linux 3 core CI image | `3.0.20261015` |
| Prometheus / Grafana | `v3.15.0` / `13.2.4` (unchanged) |

Pins retain their existing distribution families and stable channels; PostgreSQL
19 prereleases are not used. JavaScript and NuGet dependencies are unchanged.

To omit the database container entirely, set `ConnectionStrings__wf` to an
existing database connection string before launching; the AppHost still runs
its migrations. Supply a disposable database when evaluating migrations.
`Local__DataRoot` overrides the default `%LOCALAPPDATA%\wf` directory
(`LocalApplicationData/wf` on other platforms), containing filesystem blobs
and the agent's persistent identity, results, log spool and package cache.
The development upload-signing key persists in AppHost user secrets across
restarts. Restarting the stack preserves its database, blobs and agent UUID.
`Secrets:Provider=Local` and the protected local secrets directory are configured;
Azure credentials or an interactive login are not required.

For an isolated native-app/Docker-database checkout without reusing the default
database volume or local files:

```powershell
# Leave unrelated databases and Compose environments untouched.
Remove-Item Env:\ConnectionStrings__wf -ErrorAction SilentlyContinue
dotnet run --project .\backend\src\Wf.AppHost -- `
  "--Local:DataRoot=$PWD\.wf\native-main" `
  --Local:PostgresVolume=wf-native-main-pg18
```

Reuse these same arguments on subsequent starts to preserve this stack's state.
If standalone monitoring is already running, also pass
`--Local:Monitoring=false` to avoid starting another stack on its ports.
Open `http://127.0.0.1:5173` for the frontend,
`http://127.0.0.1:5000/healthz` for backend health, and
`http://127.0.0.1:5000/api/v1/workflows` for the workflow API.
The frontend proxies `/api` and `/hubs` to this backend.
Stop the foreground launcher with **Ctrl+C**; it stops its application resources
without deleting the persistent database volume or local files.

For a fully native setup, create a dedicated empty database in your locally
installed PostgreSQL first, then use a connection string for that database:

```powershell
# Replace the connection string locally; never commit real credentials.
$env:ConnectionStrings__wf = 'Host=127.0.0.1;Port=5432;Database=workflow_local;Username=postgres;Password=YOUR_LOCAL_PASSWORD'
dotnet run --project .\backend\src\Wf.AppHost -- `
  "--Local:DataRoot=$PWD\.wf\native-postgres"
```

The supplied user must be able to run schema migrations. No PostgreSQL container
is created in this mode. Do not point this launcher at a shared, production or
older prototype database: migrations change its schema. Remove
`Env:\ConnectionStrings__wf` before returning to the Docker-database profile.

To run multiple native agents plus an optional real Linux agent:

```powershell
dotnet run --project .\backend\src\Wf.AppHost -- --agents 3 --linux-agent
# Or, from backend\src\Wf.AppHost:
aspire run -- --agents 3 --linux-agent
```

`--agents` is a positive native-process count (default 1); `--linux-agent` adds
one nonroot Linux container and requires Docker even with external PostgreSQL.
The equivalent configuration keys are `Local__Agents` and `Local__LinuxAgent`.
Names, immutable IDs, `instance` labels, result journals, package caches and
spools are isolated. The original agent keeps `agents\local`; additional native
agents use `agents\local-2`, `agents\local-3`, etc. Native agents share the
configured protected local-secret store, not work directories.

The Linux UUID lives in `agents\linux\id`; its state and separate local secrets
live in the persistent `wf-local-agent-<UUID-without-dashes>` Docker volume,
mounted at `/var/lib/wf-agent` with owner-only permissions. Keep that volume
and UUID together across restarts; Windows DPAPI files are not Linux secrets.
The image builds the managed Release agent against .NET 10 using Microsoft's
public `dotnet-public` package mirror with normal TLS verification. Aspire's
container-to-host tunnel connects it to the loopback gateway; the explicit
insecure-transport opt-in is limited to this isolated development container.
This is not an AOT image or a production deployment profile.
`GET /api/v1/machines?status=Healthy` shows the registered native/Linux agents.

Override ports with `Local__ApiPort`, `Local__EnginePort` and `Local__UiPort`;
all must differ. `Local__PostgresVolume` selects a different persistent Docker
volume. This native profile is a local development launcher, not a deployment
publisher. It does not install frontend dependencies or erase persistent data.
The separate Compose profile below uses the same AppHost in publish mode.

The Docker-database profile also starts Prometheus on `127.0.0.1:9090` and
Grafana on `127.0.0.1:3000`; open `/d/workflow-runs` in Grafana for workflow
duration and outcome trends. Both bindings are loopback-only. Grafana permits
anonymous **Viewer** access only, with login forms/basic authentication disabled.
These are local development services, not an authenticated production monitoring
deployment. Prometheus retains 30 days in `wf-prometheus-data`; Grafana uses
`wf-grafana-data`. Stop/restart preserves both volumes.

`Local__Monitoring=false` omits monitoring containers and disables this host's
exporter. With external PostgreSQL, monitoring defaults off so the fully native
profile still needs no Docker; opt in with `Local__Monitoring=true`.
`Local__PrometheusPort` and `Local__GrafanaPort` override the default monitoring
ports; all enabled application/monitoring ports must differ.

For an already-running native AppHost, start monitoring without restarting its
database, agents or frontend:

```powershell
docker compose -p wf-monitoring -f .\infra\monitoring\compose.yml up -d --wait
```

Restart only the control-plane resource after rebuilding it. Its Development
configuration exports to the default loopback Prometheus receiver. Before a
subsequent full AppHost launch, stop these standalone monitoring containers with
`docker compose -p wf-monitoring -f .\infra\monitoring\compose.yml down`
(**without** `--volumes`); AppHost then owns monitoring using the same volumes.
Do not run both owners against the same ports or storage.

### Isolated local Docker Compose environments

Requires .NET 10, Aspire CLI 13.6 on `PATH`, and Docker with Linux containers
and Compose v2 (`up --wait`). Run from this source checkout or a descendant:

```powershell
dotnet run --project .\backend\src\Wf.Cli -- local up --env dev
dotnet run --project .\backend\src\Wf.Cli -- local up --env ppe
# Omitting --env selects ppe for both operations:
dotnet run --project .\backend\src\Wf.Cli -- local down
dotnet run --project .\backend\src\Wf.Cli -- local down --env dev
```

`up` publishes Compose through Aspire, builds managed Release API/engine/agent
images, runs migrations and waits for healthy services. Each project has
PostgreSQL 18.6, an API-only host, two leader-elected engines, one nonroot Linux
agent, a Caddy gateway containing the built SPA and an anonymous Aspire
dashboard. `kardia-dev` binds only `127.2.6.2`; `kardia-ppe` binds only
`127.2.6.7`. Open `http://127.2.6.2` or `http://127.2.6.7` for the UI.
Gateway port 80, API port 5000, HTTP/2 engine ports 5001/5002, dashboard
port 18888, Prometheus port 9090 and Grafana port 3000 are isolated on those
exact addresses. Each environment has its own persistent monitoring volumes
and the same provisioned workflow-trends dashboard. Edge HTTPS domains and
nightly deployment checks remain separate tasks.

Both environments are **trusted-local, login-free Development prototypes**,
including the environment named `ppe`, not production authentication profiles.
HTTP between containers is explicitly restricted to each environment's fixed
Docker subnet and configured package/engine origins. No login/logout flow,
Entra setup, machine certificate installation or public-network exposure is
added; production authentication guards remain unchanged. The SPA is built
with the explicit `local-compose` mode and permits its login-free identity
only on the two local IPs and the two approved dev/PPE edge hostnames.
Neither the existing loopback Vite-only development mode nor ordinary
production builds inherit that bypass.

Caddy preserves `/api` and `/hubs` paths, blocks gateway `/metrics*`,
`/openapi*` and `/swagger*` ops paths, serves SPA deep links without caching
`index.html`, and caches versioned assets immutably. Security headers protect
ordinary pages; report content retains the API's enforced sandbox policy and
same-origin iframe support rather than inheriting `X-Frame-Options: DENY`.
Successful `/assets/*` responses, including ranges and conditional reads, use
`Cache-Control: public, max-age=31536000, immutable`; missing chunks return 404,
never SPA HTML or an immutable error. The current index and SPA deep links use
`no-cache, no-store`.

Before rebuilding, `up` inspects that environment's existing gateway image and
pins its exact image ID to `kardia-gateway-previous:<env>` under the environment
lock. The build receives that pinned image through
`KARDIA_PREVIOUS_GATEWAY_IMAGE`; first install uses the pinned Caddy base only
when the image is genuinely absent. Docker/inspection/pinning errors fail
explicitly. The new image keeps the preceding image's current `/srv` privately
under `/opt/kardia/previous`; Caddy serves missing current hashed assets from
that prior release, without exposing its index. Only one prior release is
retained, not an accumulating history. An already-open tab can therefore fetch
its original lazy JS/CSS after the next deployment. The build remains clean:
it does not retain arbitrary files in Vite's output directory, and dev/ppe
asset histories stay separate.

The gateway proxies HTTP/2 agent gRPC to the first
healthy engine. One-second `/healthz/leader` checks exclude the standby until
it acquires the database lease and finishes recovery. The Linux agent has
only the gateway configured, so reconnects pass through the same leader
selection after an engine failure. Existing in-flight SDK task hosts remain
on the agent while the replacement engine recovers and accepts their results.

Project-scoped volumes isolate PostgreSQL, blobs, control-plane local secrets
and agent state. Owner-only `.wf\local\<env>\runtime.env` stores a stable
database password, upload-signing key and agent UUID; preserve it with the
volumes. Corrupt/unsafe state fails explicitly rather than silently generating
replacement identities or keys. Operations on one environment are locked
against overlap without blocking the other environment. `down` uses the saved
published Compose file, removes only that project's containers/network, and
never removes its volumes, images or private state. Repeating `up` rebuilds
changed application code while retaining data; container tags identify the
local environment, not a Git-derived workflow version. Workflow SemVer,
immutable package hashes and run-version pinning retain their existing meaning.

The control-plane build defaults to NuGet.org. `KARDIA_NUGET_SOURCE` can select
an approved package mirror without disabling TLS verification. Its Docker build
context is the repository root, not `backend`, so published images include the
shared/team failure catalogs:

```powershell
docker build -f .\backend\src\Wf.ControlPlane\Dockerfile -t kardia-control-plane:local .
```

### Application roles and local orchestration

The application has API, engine/control-plane and agent roles, plus PostgreSQL
and blob storage. API-only and engine instances use the same
`kardia-control-plane` image: `Engine__Enabled=false` disables engine leadership
and execution; engine instances enable them. Agents use the separate
`kardia-agent` image. Native AppHost combines API and engine in one process;
the Compose profile already separates one API, two engines and one agent.

The engines share a single database-backed `engine` lease, so extra engine
replicas provide active/standby failover, not additional active scheduler
throughput. API and agent replication are different scaling concerns.
Replicated agents need distinct persistent identities and durable journals.

API/controller replicas can select the existing shared Azure implementation with
`Storage__Provider=AzureBlob`, an HTTPS `Storage__Azure__ServiceUri`, and a private
`Storage__Azure__Container`. Authentication uses managed identity; all replicas
still need the same protected `Storage__UploadHmacKey`. See
`docs/operations/shared-blob-storage.md` for identity, emulator, and security
configuration. The default remains `FileSystem` for existing local profiles.

The supplied `infra/ha-lab` profile runs K3s on three isolated Hyper-V VMs,
with two API replicas, two controllers, three workers, two frontend gateways,
a one-time migration job and shared lab data services. Application resources
use the `kardia` namespace and `kardia-*` names; K3s components remain separate.
Its sequential rollout and actual worker/leader VM-loss checks completed real
checkpointed SDK tasks. See its runbook for measured times and limitations.
No Swarm stack is supplied. Container-only restart tests do not replace the
lab's actual isolated VM power-loss check.

Keep workflow/scheduling/task business logic shared. Deployment changes include
service discovery, leader-aware HTTP/2 routing, readiness and graceful shutdown,
one-time migrations, secrets, shared storage and durable per-agent volumes.
The trusted Compose authentication profile also hard-codes Docker subnets and
allowed service origins; those assumptions cannot simply be copied into a
Kubernetes namespace. Use an explicit suitable security/network profile rather
than weakening the existing guards. The HA lab has its own loopback-only
Development profile. `infra/azure-vm` instead supplies Production Entra/TLS and
managed-identity settings for separate Azure VMs without depending on K3s.

The real
lifecycle acceptance test requires a normal Debug CLI build and explicit
`WF_COMPOSE_LIFECYCLE=true`:

```powershell
dotnet build .\backend\src\Wf.Cli
$env:WF_COMPOSE_LIFECYCLE = "true"
dotnet test .\backend\tests\Wf.Integration.Tests --filter FullyQualifiedName~LocalComposeLifecycleTests
```

It refuses existing containers or volumes for either fixed project name, starts
both real environments, verifies exact published bindings and leader separation,
restarts PPE while dev remains healthy, and checks database/blob/agent state,
keys and UUID persistence. Cleanup removes only its own project resources and
allowlisted volumes, leaving private CLI state for subsequent use.

For the actual gateway/leader-kill acceptance, also install the frontend's
frozen dependencies and Playwright browsers, then run:

```powershell
$env:WF_COMPOSE_GATEWAY = "true"
dotnet test .\backend\tests\Wf.AppHost.Tests --filter FullyQualifiedName~ComposeGatewayTests
```

This test refuses an existing PPE project, compiles and registers a real SDK
workflow through the public CLI/API, kills its leader while a Linux task is
running, and requires the standby to finish the same attempt and dispatch the
following task. Chromium, Firefox and WebKit exercise the actual built SPA,
deep-link reload, login-free session, MessagePack WebSocket and a real
published HTML report iframe through Caddy.
Only owned PPE containers, network, volumes and temporary workflow files are
removed.

The independent asset-deployment acceptance needs Docker, the frontend's frozen
dependencies and all three Playwright browsers, but no Aspire, backend services
or existing Compose project:

```powershell
Set-Location .\frontend
bun run scripts\gateway-assets-acceptance.ts
```

It builds the production gateway image, keeps old Vite-built lazy-route tabs
open in Chromium/Firefox/WebKit, replaces the gateway on an ephemeral loopback
port and verifies original JS bytes and CSS after deployment. First install,
actual cache/HEAD/range/304 headers, SPA deep links, missing-asset 404s and
one-release history bounds are checked. All test images, containers and private
fixture files are removed. The separate `gateway-assets` Frontend CI job runs
the same acceptance without touching the fixed dev/ppe projects.

`.github/workflows/nightly-compose.yml` runs the full Scenario A on Ubuntu
nightly at 08:45 UTC and on manual dispatch. It pins .NET 10.0.402 and Aspire
13.6.0 (with archive SHA-256 verification); the gateway image installs Bun
dependencies with the frozen lockfile. No edge, host Bun or browser install is
needed. For the same test on a Linux host with Docker and Aspire installed:

```bash
dotnet build backend/src/Wf.Cli "-p:WarningsNotAsErrors=NU1902%3BNU1903"
dotnet build backend/tests/Wf.AppHost.Tests "-p:WarningsNotAsErrors=NU1902%3BNU1903"
WF_COMPOSE_SCENARIO=true dotnet test backend/tests/Wf.AppHost.Tests --no-build --filter FullyQualifiedName~ComposeStackTests
```

Both `kardia-dev` and `kardia-ppe` must have no existing containers or volumes.
The test invokes public `wf local up --env dev`, registers and triggers Scenario A
through `127.2.6.2:80`, hard-kills the SQL-identified leader during the build task,
then starts PPE beside dev. It requires all eleven attempts to succeed without
retry, a higher leader epoch, real Linux SDK child outputs and diamond ordering,
distinct environment agents, and HTTP 200 from both exact-IP `/healthz` endpoints.
Cleanup removes only started projects and allowlisted test volumes; private CLI
state persists. CI retains the TRX (including diagnostic output) for fourteen
days. A local pass does not imply a hosted GitHub Actions result.

### Graceful API shutdown

Use `/healthz/ready` for routing readiness and `/healthz` for liveness. On
SIGTERM or host stop, readiness immediately returns 503 while accepted HTTP
requests finish. The API withdraws from Caddy's one-second readiness checks,
waits its propagation delay, then lets Kestrel finish the accepted requests.
Compose gives the API a 45-second termination grace period.

`Drain:PropagationDelay` (`Drain__PropagationDelay` in environment variables)
accepts zero through ten seconds. API-only nodes default to two seconds;
combined/engine nodes default to zero to preserve fast leader handover.
Keep the deployment termination budget longer than the configured propagation
delay, the bounded two-second live-client acknowledgment and the HTTP shutdown
budget. This is routing drain, not user authentication or agent drain.

Live clients acknowledge a fixed `server_draining` reconnect hint before their
transport closes. A missing acknowledgment is logged and cannot block shutdown
indefinitely. The existing official SignalR reconnect and run store recover
through `/events?afterSeq=<last-applied-sequence>` without replacing ordered
replay with a snapshot or polling. The native Linux SIGTERM acceptance runs in
the backend Linux CI leg; `WF_API_DRAIN_BROWSER=true` with `WF_FRONTEND_ROOT`
enables the separate real Chromium/Firefox/WebKit integration test.

### Trusted local HTTPS edge

The shared GrihaSetu platform edge already contains the Kardia dev/PPE routes.
`infra\local\edge\kardia.caddy` is the matching source copy; it forwards gRPC
over h2c and other traffic normally to the two loopback gateways. Its
trusted-caller restriction remains unchanged. Start the desired Compose
environments first, then run the existing platform scripts from an elevated
PowerShell session, using this machine's actual LAN IPv4:

```powershell
$platform = "<path to grihasetu\platform>"
$env:EDGE_BIND_IP = "<local LAN IPv4>"
& "$platform\scripts\edge-up.ps1" -Action regen-cert -SkipPublish
& "$platform\scripts\edge-up.ps1" -Action reload
& "$platform\scripts\apply-edge-hosts.ps1" -ServerIp $env:EDGE_BIND_IP
Clear-DnsClientCache
```

If the native edge is not already running, use `-Action up` before reloading.
Regeneration covers all discovered hostnames; `-SkipPublish` avoids unrelated
consumer-repository certificate writes. The managed hosts script backs up the
hosts file and updates its shared managed block. This does not replace or
restart an already-running edge process.

Open `https://dev.kardia.sonapraneeth.in` or
`https://ppe.kardia.sonapraneeth.in` while the corresponding stack is up.
The local mkcert root must be trusted on every intended client; distribute
only the public `rootCA.pem`, never `rootCA-key.pem` or a server private key.
Do not broaden `EDGE_TRUSTED_CIDRS` to an untrusted network. Windows native
agents can register through the PPE HTTPS origin with
`Agent:AllowInsecureTransport=false`, a stable UUID/state directory and the
existing protected Local secret provider. The edge validation uses the
separate `edge-validation` pool; it proves native registration, not external
Windows package execution.

Windows Schannel curl may report `CRYPT_E_NO_REVOCATION_CHECK` for this local
CA, which does not publish CRL distribution points. Use its per-command
best-effort option for missing/offline CRL metadata, not `--insecure` or a
global certificate-verification bypass:

```powershell
curl.exe --noproxy "*" --ssl-revoke-best-effort -fsS https://ppe.kardia.sonapraneeth.in/healthz
```

The read-only edge smoke test uses default .NET certificate/hostname
validation and requires HTTP/2, both healthy Linux agents and a native Windows
agent in the validation pool; it does not provision or reconfigure the edge:

```powershell
$env:WF_EDGE_SMOKE = "true"
$env:WF_EDGE_PLATFORM_ROOT = $platform
dotnet test .\backend\tests\Wf.AppHost.Tests --filter FullyQualifiedName~LocalEdgeTests
```

### Native trusted-LAN listener

For explicit trusted-LAN access, install `mkcert` and trust its root once on
each intended client. Then:

```powershell
mkcert -install
dotnet run --project .\backend\src\Wf.AppHost -- --lan
# Optional explicit interface when several default routes exist:
dotnet run --project .\backend\src\Wf.AppHost -- --lan --Local:LanAddress=192.168.1.10
```

`--lan` adds `https://<raw-ip>:5001` for API and agent gRPC (HTTP/1.1 and HTTP/2);
the existing plain HTTP API, engine and Vite listeners stay loopback-only.
The same numeric engine/LAN port works because the bound addresses differ.
The launcher selects the sole default-route IPv4 address or requires
`Local__LanAddress`; it refuses nonlocal, wildcard, loopback and multicast
addresses. `Local__LanPort` changes the HTTPS port.

This is an explicit **trusted-network, login-free Development profile**:
LAN clients receive the same full-access development identity as local clients.
Do not enable it on public or untrusted networks. No production authentication
rules, Entra configuration or login/logout screens are changed. The launcher
does not open firewall rules, install trust automatically, expose Vite/dashboard
to the LAN, or configure a domain/reverse proxy. Use the local Vite UI for now.

The launcher invokes `mkcert` to issue an IP-SAN PEM certificate/key in
`<Local:DataRoot>\tls`, never installing a root itself. `Local__LanCaRoot`
selects an alternate mkcert CA directory without changing machine trust.
Alternatively supply both `Local__LanCertificate` and `Local__LanKey` as existing
PEM paths. The host rejects expired, not-yet-valid and wrong-IP certificates.
Clients must trust the issuing `rootCA.pem` from `mkcert -CAROOT`; distribute
**only that public root certificate**, never `rootCA-key.pem` or the server
private key. LAN agents use `https://<raw-ip>:5001` with normal certificate
verification. Standalone control-plane launches accept `--lan` with
`Lan__Address`, `Lan__Certificate`, `Lan__Key`, optional `Lan__Port` and the
explicit Development environment; `--dev` additionally selects login-free
authentication. Other environments reject this local-only LAN listener.

The actual startup/restart tests are opt-in:

```powershell
$env:WF_APPHOST_STARTUP = "true"
dotnet test .\backend\tests\Wf.AppHost.Tests
```

Additionally set `WF_NATIVE_POSTGRES_BIN` to PostgreSQL's native `bin` directory to exercise the
container-free variant. Both variants launch the real dashboard, host, agent
and UI, require health within 120 seconds per startup, and verify persisted
database/blob data and agent identity after recreation. Each test owns and
removes only its unique temporary database directory or Docker volume.
The mixed-agent case additionally checks four actual HTTP inventory rows,
nonroot Linux storage permissions, isolated identities and persistent volume
data across recreation. Run these AppHost tests with normal build output:
Aspire's child project runner uses the projects' default Debug output, not a
test-only `--artifacts-path` tree.

Set `WF_APPHOST_SCENARIO=true`, provide `WF_TEST_POSTGRES` as a PostgreSQL admin
connection, and select
`FullyQualifiedName~LocalStackScenarioTests` for the separate actual scenario A
test. Build the default Debug solution, including the CLI, beforehand. This
test compiles and packages a workflow using the production CLI, registers it
through public HTTP endpoints, triggers a run, and waits for `Succeeded`.
It checks four lifecycle stages, diamond dependency inputs, three parallel
smoke tasks, real native-agent attribution, and SDK child-process execution
from the downloaded package. Stack readiness must remain within 120 seconds;
the test does not write run, attempt or output state directly. The backend
workflow's `local-stack` Windows/Ubuntu matrix runs this opt-in separately
from ordinary tests and uploads its TRX results. The admin connection creates
only the test's unique disposable database; no existing database is migrated.

Set `WF_APPHOST_LAN=true` (optionally `WF_APPHOST_LAN_ADDRESS`) to run the
additional real LAN test. It requires mkcert and Docker, uses an isolated CA
without modifying trust stores, and checks separate Linux curl HTTP/2 health
and inventory requests, rejection of an untrusted root, a real Linux agent's
trusted TLS/gRPC registration, and exact loopback-only plain HTTP bindings.
Only the public root is mounted into clients. `WF_LAN_CURL_IMAGE` overrides
the test's `curlimages/curl:8.12.1` client image.

`GET /api/v1/machines` returns `{ items: [...] }`, ordered by immutable machine
ID, with OS/architecture, hardware, slot/load metrics, pool, labels, heartbeat
health, the current reservation and effective `capacities` per profile. Optional `pool`, `os=windows|linux`,
`status=Healthy|Unhealthy|Draining` and repeated `label=key=value` filters combine
with AND. Expired reservations remain visible until physical fencing settles;
a stale heartbeat reports `Unhealthy` even before the next sweep.
Inventory and edits reuse the existing global Admin authorization boundary.
The explicit loopback development profile remains login-free.

**Live system -> Actual system** (`/admin/system`) is the global-Admin topology and per-worker
work view, with observed-activity pulses, pause/resume and reduced-motion support.
Admin `GET /api/v1/admin/system` adds observed API/controller instances, current
database lease leadership, and per-worker assigned task, parent DAG-node, run
and workflow counts. Five-second heartbeats expire after 15 seconds; crashes
are not reported as healthy indefinitely. The view separates current replicas
from inactive process history and shows active and passive controllers.
On the K3s lab, an optional read-only Metrics API integration adds measured
node/VM and application-pod RAM, capacity, placement and sample freshness;
missing readings are not zero. See the
[live system inventory guide](docs/operations/live-system-inventory.md) for
placement metadata, activity-counter semantics and migration ordering.

**Live system -> Deployments** (`/admin/deployments`) separates actual Kubernetes
rollout observations from durable script-reported Stage/Release activity.
Target versus pod images, desired/updated/ready counts, OnDelete replacements,
container failures/restarts and retained migration jobs remain visible without
deployment controls. Five-second refresh, pause/manual refresh, stale/error
boundaries and unconfirmed interrupted reporters avoid presenting old success
as current health. Reporting starts with migration `0051_deployment_activity.sql`;
unrecorded history is labeled, never fabricated.

Global Admins can also publish one persistent **System notice** from Actual system:
information, warning or critical, with optional expiry. The plain-text bar appears
above navigation on every signed-in page until cleared or expired. Shared
PostgreSQL storage, optimistic concurrency and atomic before/after audit protect
updates across replicas. This is manual communication, not automatic incident
detection or an all-clear health indicator.

Admin `PATCH /api/v1/machines/{id}` accepts a nonempty JSON object containing
`labels`, `pool` and/or `manualCapacity`; labels replace the whole map, and `{ "labels": {} }`
clears it. Omitted fields stay unchanged; explicit nulls, duplicate/unknown
fields, invalid metadata and bodies over 64 KiB are rejected. A successful
edit returns 204 and atomically records before/after metadata in `audit_log`.
Each supplied field becomes an override retained across agent reconnects;
unsupplied fields still follow agent configuration until overridden. Placement
reads edited metadata before its next dispatch, including on a different
control-plane process, without interrupting existing work or freeing a
reservation. Responses are never cached and expose no agent sessions or
storage capabilities.

`manualCapacity` has the shape
`{ "profile": { "transport": "http", "codec": "json", "payloadSizeClass": "small", "loadSettings": { "threads": "8" } }, "capacityRps": 1234 }`.
The rate must be an integer from 1 through 2,147,483,647. Profile identifiers
and setting keys are exact, case-sensitive, 1-100 characters without surrounding
whitespace or control characters; setting values are strings. Sorted settings
form the shared canonical profile key, limited to 2,000 UTF-8 bytes by the server.
A manual write replaces any calibrated value for only that profile, sets source
`Manual` and clears `calibratedAt`; other profiles stay unchanged. Capacity,
optional metadata and before/after audit commit or roll back together.
Empty `capacities` means Unverified, not zero requests/s. Manual values remain
Unverified until measured; calibrated entries include their UTC timestamp.
This editor does not run a load generator.

Admin `POST /api/v1/machines/{id}/calibrate` accepts `{ "profile": { ... } }`
with the same strict profile contract and 64 KiB body limit. It returns 202,
a durable request and a `Location` pointing to
`GET /api/v1/machines/{id}/calibrations/{calibrationId}` (also Admin).
Reserved machines return 409 even after reservation expiry until physically
released. Busy, stale, unhealthy, draining or legacy agents without calibration
command support also return 409. Acceptance atomically audits the request,
advances the machine fence and marks it Draining, blocking both placement and
reservation acquisition across control-plane processes.

The engine resends the same command after reconnect or leader failover.
Expiry or disconnection alone never frees the machine: only a terminal response
from its current session at the exact request fence ends calibration.
Successful positive Int32 req/s received before the two-minute server deadline
updates only the requested profile as Calibrated with a server UTC timestamp.
A Manual override, including one written while calibration is pending, always
wins; status then reports `Succeeded`, the reported rate and `applied: false`.
Capacity, terminal status, restored machine health and completion audit commit
together. Stale/duplicate responses have no effects.

The native agent currently durably reports `Unsupported` without starting load
or inventing a rate. Status exposes `Failed` with `Unsupported`, `TimedOut`,
`Busy` or `Failed`; failures never change capacity. Migration
`0035_calibration_commands.sql` adds the durable requests and capability flag
(false for existing agents). The loopback load executor, Wf.Load, Bond profiles
and repeated-run stability measurement remain deferred.

Admin `POST /api/v1/machines/{id}/drain` accepts
`{ "maxDrainSeconds": 300, "reason": "machine maintenance" }` (1..86400 seconds;
nonblank reason, up to 1024 UTF-8 bytes). `GET` on the same path reports
`Draining` or `Released`. Repeated requests retain the original deadline.
Migration `0045_agent_drain.sql` persists the request and capability flag;
legacy agents without drain support return 409. Existing Admin authorization
is reused; no new login or logout flow.

Draining immediately excludes the machine from placement and pending dispatch
delivery, including after reconnects. Existing tasks may finish until the
deadline; remaining attempts are cancelled, physically stopped, then re-placed
according to their original retry budget and placement constraints.
`NoAutoRetry`, named-machine constraints and exhausted budgets still apply.
Reservations and the Released state require a journaled physical-completion
acknowledgement from the current session at the exact machine epoch. A deadline
or disconnected agent alone never frees live capacity. Released means safe for
maintenance, not Healthy: the machine stays Draining without automatic re-enable.

Admin `POST /api/v1/machines/{id}/resume` accepts
`{ "drainId": "<exact released drain UUID>", "reason": "maintenance verified" }`.
Migration `0049_agent_drain_resume.sql` records an optional process identity in
Hello and the process which physically acknowledged release. Resume requires a
**different, restarted agent process**, a fresh heartbeat, no occupied slots,
no assigned attempts and no live reservations. A gRPC reconnect with a new
session but the same process does not qualify: that process still has its local
drain gate. Status and before/after audit commit atomically; an audit failure
leaves the machine Draining. Repeating after a successful resume is a no-op;
an intervening different drain returns 409.

For rollout automation, include `"requireResumable": true` in the drain request.
This refuses an older agent **before** maintenance starts if it cannot supply
process identity. Old Hello messages remain compatible with registration and
ordinary drain, but a release without recorded process proof cannot be guessed
safe for resume after an upgrade. Deploy the process-identity-capable agent
before requesting a resumable maintenance cycle. Resume never overrides
placement/retry policy or reconstructs lost in-task memory.

`dotnet test backend\tests\Wf.Integration.Tests --filter "FullyQualifiedName~Agents.DrainTests"`
exercises actual PostgreSQL, HTTP/2 agents, packaged SDK hosts and a spare agent:
graceful completion, deadline cancellation with gated cleanup, forced process-tree
termination, blocked engine acknowledgement and journal replay after reconnect.
Each case holds a real reservation until physical release and ends with zero
logical task failures. Forced stops cancel checkpoint/progress forwarding before
terminating the host, so an expected transport shutdown cannot overwrite the
cancellation with `PlatformError`. Backend CI runs this gate on Windows/Linux
and uploads `agent-drain-<OS>` reports. Local Linux evidence uses the same portable
managed build on the actual Linux .NET runtime, not a Linux native AOT build;
host-process/agent-object reconnect tests do not claim a machine reboot.

`/machines` shows that inventory with URL-persisted filters; a machine name opens
`/machines/<uuid>` for hardware, heartbeat/resource metrics, current reservation
and the profile capacity table with Admin manual-capacity and label/pool editors.
Empty labels clears the map; the metadata editor sends
only changed fields. Global Admin permissions come from the existing permission
API, not token inspection, and the local development profile remains login-free.
Migrations `0033_machine_notifications.sql` and `0034_capacity_notifications.sql`
publish committed agent, reservation and capacity changes through PostgreSQL
`live_machines`; the existing authorized
MessagePack `machines/*` subscription coalesces invalidations for 250 ms and
refetches the authoritative inventory without timer polling. Listener/transport
reconnect resynchronizes; failed reads or denied/error live updates hide cached
data, while reconnecting transport explicitly marks displayed data stale.

## Frontend

`frontend` contains the React/TypeScript schedule-management SPA: create, edit,
pause, resume, delete, timezone selection and server-calculated next-five-fire
preview. Bun manages dependencies and scripts; Vite proxies API traffic to the
control plane. See `frontend\README.md` for local/Entra configuration, generated
public/internal clients, actual PostgreSQL/browser tests and current hosting
boundaries. Code schedules show applied file/commit provenance without mutation
controls; commit-browser links use an explicitly configured HTTPS template,
never an assumed application repository.

Schedule details link to `/series/<uuid>` for durable scheduled-fire history.
`GET /api/v1/series/{id}/fires` requires fresh Viewer access to the owning
team/type, independently of the SPA client. Responses contain `seriesId`,
`items` (`fireAt`, `outcome`, nullable `runId`/`reason`) and nullable
`nextCursor`. Fires are newest first, including `Skipped`/`overlap` without a
run and retained history after schedule removal. Pass the opaque cursor back
as `?cursor=...&limit=50`; limits are 1-100. Invalid pagination returns 400,
unknown or unauthorized series 403, and responses are never cached. This
view lists fire history, not the separate full run-history/trends feature.

Catalogue and scheduled-fire run links open `/runs/<uuid>`. Its Task Plan link
uses the run's immutable version UUID, never the currently active version.
`GET /api/v1/workflows/{team}/{type}/{workflow}/versions/{version}` requires fresh
Viewer access to the exact type and returns an explicit safe plan projection:
ordered nodes/tasks, dependency conditions, lifecycle stages, effective placement,
retry policies and timeouts. Historical versions remain readable after workflow
soft deletion; missing versions return 404, denied scopes 403, invalid identities
400, and responses are never cached.

`GET /api/v1/workflows/{team}/{type}/{workflow}/versions?limit=50` returns
Viewer-authorized registration history with 1-100 items per page, newest
registration timestamp first and descending immutable UUID for ties. Follow
`nextCursor` with the same exact workflow and limit; unknown, duplicate,
noncanonical or transplanted query values return 400. Empty registered
workflows return an empty page; authorized missing workflows return 404.
Inactive and soft-deleted workflows retain their history, and every page
rechecks access. `activeVersionId` and `latestVersionId` come from the same
database snapshot as the page, so an intentional activation rollback is
distinct from the latest registration.

Both list items and immutable task-plan details expose a safe `registration`
projection: Git commit, Git author, registering identity, nullable change note,
SDK version and UTC registration time. Source, raw manifests, variable values,
package hashes and storage capabilities are not returned. Migration `0026`
adds the workflow/timestamp/UUID paging index without changing stored versions.
The generated API client includes these contracts; the Versions tab remains a
separate UI task.

`POST /api/v1/workflows/{team}/{type}/{workflow}/versions/{version}/activate`
requires current owning-team/type Admin access (or global Admin), an actor UUID,
and no body or query. It returns `workflowId`, `activeVersionId` and
`latestVersionId` with `no-store`. A registry transaction serializes with
registration, validates the target's exact ownership, updates only the active
pointer, writes a before/after `workflow.activate` audit and notifies listeners.
New runs see the version within five seconds; existing runs keep their immutable
pins. Repeating the active UUID changes no rows or audit history. Missing or
foreign versions return 404; deleted workflows and incompatible variables in
retained unpinned schedules return 409. Pinned schedules keep their target.
Schedule configs, fire cursors and series IDs never roll back through this API;
revert source and re-register to roll back code-defined schedules. The latest
registration baseline stays unchanged: identical re-registration preserves an
Admin rollback, while a newly registered version must exceed that latest
baseline before automatically becoming active.

`GET /api/v1/workflows/{team}/{type}/{workflow}/diff?from=<uuid>&to=<uuid>`
compares two explicit immutable versions of that exact workflow. Fresh Viewer
authorization also permits retained source access, including inactive and
soft-deleted versions. `manifest` contains ordinal-keyed `Added`, `Removed` and
`Changed` entries with nullable `before`/`after` snapshots for nodes, variables,
workflow policies (timeout/placement) and settings (SDK, metadata, stages).
Version labels alone do not create manifest changes. `source` contains exact
UTF-8 `workflow.cs` texts as `before`/`after`, its path and `changed`; clients
compute editor hunks without a server-side quadratic line-diff algorithm.
Unlike the safe Task Plan projection, this endpoint intentionally exposes
authored source and literal manifest configuration; secret references are never
resolved. Do not hardcode credentials in scripts. Source archives use stored
content-addressed `sources/<sha256>` keys (not version-ID ZIP paths), with
SHA-256/length verification, one fixed entry and a 1 MiB expanded text ceiling.
Both versions must belong to the route workflow; no active-version fallback.
Bad/duplicate queries return 400, missing pairs 404, denied scopes 403, corrupt
data 500 and unavailable storage 503; errors never include partial source.
Responses are never cached.

The Versions UI offers **Download source** for any listed immutable release and
**Download pinned source** for a historical release opened directly. These save
the exact retained `workflow.cs`, preserving authored bytes, identity and SemVer;
they do not download the compiled package or external registry/schedule files.
The existing Viewer-authorized comparison read uses that exact UUID on both
sides, with the same hash, size and scope checks; no clone or registration occurs.
**Version download and comparison example** explains older-to-newer comparisons
and includes a clearly illustrative timeout change. When two releases are loaded,
**Compare latest two registrations** runs the example using their actual immutable
IDs; a single-release history explains that another release is needed.

`POST /api/v1/workflows/{team}/{type}/{workflow}/versions/{version}/clone`
downloads an immutable-version scaffold without changing or registering either
workflow. Send exactly `{"newName":"experiment","targetType":"team/type"}` with
fresh Viewer access to the source type and Author access to the target type.
The ZIP preserves authored task bodies and dependency directives, resets the
new identity/type/version (`1.0.0`), and records
`ClonedFrom = <source-workflow>@<immutable-version-UUID>` in metadata. It includes
`workflow.cs`, an empty schedule plus schema, and runnable SDK tests.
Historical/inactive/deleted source versions remain available; an existing
target returns 409 and unsupported or ambiguous source syntax returns 400.
Source reads verify stored hashes and bounds; the API never compiles or
executes scripts. Review target metadata rules before publishing; the scaffold
does not include registry configuration. PR creation remains deferred.

`wf clone <team/type/workflow> --version <uuid> --new-name <leaf>
--target-type <team/type> [--repo <existing-repo>] [--engine <origin>]` writes
that exact historical scaffold to `teams/<team>/<type>/<leaf>` in the workflows
repository (default: current directory). Existing destinations are never
overwritten; bounded ZIP validation, safe paths and atomic publication reject
partial or unsafe downloads. Cloning never compiles, executes or registers
downloaded code. `--engine` overrides `WF_ENGINE_URL`; outside loopback, HTTPS
and `WF_ACCESS_TOKEN` are required. The Versions tab offers the same operation
from history rows or a pinned historical UUID, with a target-name/type dialog
and ZIP download. Server errors retain the draft; source and target permissions
are still rechecked by the API.

SDK manifests now record optional declared task actions: the original SDK method,
command executable, credential/query/fragment-free one-shot HTTP method/URL, or
declared HTTP operation. Description never executes tasks or inspects captured
values. Task Plan excludes command arguments/environment, HTTP headers/bodies,
variable values/defaults, input bindings and package/source capabilities.
Only declared safe secret references are shown. Older manifests without action
metadata explicitly say the declaration is unavailable; functions do not predict
commands they may generate at runtime.

## Backend

Requires the .NET 10 SDK. PostgreSQL integration tests require Docker with Linux
containers, or `WF_TEST_POSTGRES` pointing at a PostgreSQL 17+ test server.
External-server tests create and remove only their own unique schemas; the test
user needs schema-creation permission. CI uses Testcontainers on Linux and a
service PostgreSQL on Windows.

```powershell
dotnet build backend\Wf.slnx
dotnet test backend\Wf.slnx --collect:"XPlat Code Coverage"
dotnet run --file backend\samples\workflows\demo\scenario-a.cs -- --var env=dev
```

The sample executes scenario A's four-node DAG, a real command, illustrative
deployment/check functions, and lifecycle stages through the shared engine core.
Node/task states print live. Exit code is 0 only for `Succeeded`; failures,
timeouts and aborts return 1. Ctrl+C requests an abort and allows cleanup.
`LocalVariables.Parse` accepts repeated `--var key=value` pairs; strings, enums,
durations and secret-reference names use plain text, other types use JSON.
All policies and variables validate before tasks start.

### NYC housing linear regression

The default local sample is
`data-science/regression/linear-regression`: team **Data Science**, workflow
type **Regression**, workflow **Linear Regression - NYC Housing Prices**. Its graph is a
single readable chain:

Regression is the shared workflow type, not the particular model algorithm.
The earlier housing workflow identity remains available only as historical
data after upgrading a populated demo. Publish the corrected identity and apply
`demo\retired.schedules.json` to the earlier identity using a schedule-only
registration. This retires its code schedules without deleting their permanent
schedule/series IDs or historical runs. Do not rewrite pinned manifests or source archives.
Registry replacement retains referenced historical type rows for these foreign
keys, while only the current team snapshot supplies catalogue types and groups.

| Node | Task | Result |
| --- | --- | --- |
| Dataset | Check cache -> Download if missing -> Load and report | Verified source snapshot and fixed training/validation/test CSVs; download task **Skipped** on cache hit |
| Train | Fit linear regression | Price predicted from building area, using configurable gradient-descent epochs (default 100) |
| Validate | Measure validation error | Validation R², MAE and RMSE, without fitting on validation rows |
| Test | Evaluate held-out test | Final metrics, actual/predicted chart and every test prediction; node **Skipped** when `runFinalTest=false` |

Data comes from NYC Department of Finance's public NYC Open Data rolling sales
dataset `usep-8jbt`, with no credentials required. The sample selects tax-class-1
residential sales, 500–5000 gross square feet and $100,000–$3,000,000 sale prices.
It retains one latest sale per property and orders property IDs by SHA-256 for
reproducible splits (default 70% training, 15% validation and 15% final test). The fixed
snapshot does not silently refresh when upstream data changes.

The trigger form, API and direct `--var key=value` options accept:

| Parameter | Default | Constraint |
| --- | --- | --- |
| `runFinalTest` | `true` | Boolean; `false` skips held-out evaluation |
| `forceDownload` | `false` | Boolean; `true` downloads again and replaces this split's cache |
| `trainingPercent`, `validationPercent`, `testPercent` | `70`, `15`, `15` | Integers, each 2-96, summing to 100 |
| `epochs` | `100` | Integer, 1-10000 |
| `learningRate` | `0.1` | Finite number, greater than 0 and at most 1 |

The dataset report records the effective parameters. Invalid combinations fail
before downloading. Changing split percentages selects a separate cache directory
with a `-<training>-<validation>-<test>` suffix; epochs and learning rate reuse the
same splits. Forced refresh bypasses even damaged cache validation and downloads
under the dataset lock. Replacement files are staged before publication, so HTTP
or parsing failure preserves the previous cache; unrelated files are never deleted.

The cache-check task checks disk before making any HTTP request. A cache hit
verifies SHA-256 hashes for the source and all three CSVs, reports **reuse** and
sets `cacheMissing=false`. The engine marks `download-if-missing` **Skipped**
without creating an attempt; `load-and-report` still runs and provides the dataset
to training. Concurrent runs share a file lock;
interrupted downloads never publish a valid cache manifest. A damaged cache
fails explicitly rather than silently training on altered data.

Direct/native execution defaults to
`%LOCALAPPDATA%\wf\datasets\nyc-housing-v1`; `WF_DATASET_ROOT` overrides the parent
directory. Native AppHost sets that parent to `<Local:DataRoot>\datasets`
(its default data root is `%LOCALAPPDATA%\wf`).
Linux AppHost agents and the local Docker Compose stack use
`/var/lib/wf-agent/datasets/nyc-housing-v1` inside their persistent agent volume.
For a **single-node local Docker Swarm**, mount a named volume at
`/var/lib/wf-agent` and set `WF_DATASET_ROOT=/var/lib/wf-agent/datasets`.
Run affinity keeps every task on one worker, so a run never passes a native
disk path to a Linux worker or reads another worker's independent volume.
Multi-node Swarm can cache separately on each worker; to reuse one download
across workers, mount shared storage at the same path on every eligible worker.
Containers need outbound HTTPS access to NYC Open Data. Do not store the cache
in a package extraction directory or delete the agent volume to restart a run.

Publish and trigger the new sample against the native development backend:

```powershell
$env:WF_ACCESS_TOKEN = 'local-development' # explicit loopback Development only
.\backend\samples\workflows\publish-samples.ps1 `
  -Environment dev -Engine http://127.0.0.1:5000 -IncludeSchedules -RunOnce
Remove-Item Env:\WF_ACCESS_TOKEN
```

`-IncludeSchedules` deliberately registers both regression workflows' recurring code
schedules from their `schedules.json` files; without it the publication script
skips scheduled workflows. `-RunOnce` additionally starts one manual run, not
the recurring schedule.

| Schedule profile | Hours (`Etc/UTC`) | `experimentNumber` | `runFinalTest` | `epochs` |
| --- | --- | --- | --- | --- |
| `daily-training-validation` | 00, 04, 08, 12, 16, 20 | 1 (inherited) | `false` | 50 |
| `daily-full-test` | 02, 06, 10, 14, 18, 22 | 2 (overridden) | `true` | 100 |

One automatic run fires every two hours, alternating the profiles; each profile
runs every four hours. Existing profile IDs retain their series and history
despite the original `daily-` names.

The same file defines two visible Service History cards, **Training & validation**
and **Full test**, each tracking its matching recurring instance. **Trigger now**
from either schedule remains in that schedule's card. Change `historyCards`
membership or `visible` in JSON and republish; no UI configuration is involved.

Both profiles default to experiment number 1, 70/15/15 splits, learning rate 0.1 and
`forceDownload=false`. Change shared values under `defaults.experiment`, or
override any parameter in a profile's `variables`, then republish with
`-IncludeSchedules`. For example, the full-test instance sets
`"variables": {"experimentNumber": 2, "runFinalTest": true, "epochs": 100}`,
overriding the shared `"defaults": {"experiment": {"experimentNumber": 1, ...}}`
without changing the training instance. Categories such as `experiment` organize
defaults; override keys stay flat in the instance's `variables`, not nested under
`experiment`. The **Dataset and cache** JSON report records the effective
configuration, including `experimentNumber`, and the task log names the experiment.
Each profile owns a separate series. Both follow the active
workflow version, skip overlapping runs within their own series and use
`catchUp=Latest` after downtime. They appear on the workflow's schedule list;
code schedules are read-only in the UI. Their **Trigger now** action verifies a
profile manually without moving its next automatic fire time.

Open `/t/data-science/regression/w/linear-regression/history`. **Reports**
includes training/validation RMSE by epoch, a fitted-price chart, model
coefficients, validation metrics, final held-out R²/MAE/RMSE, a training-mean
baseline comparison and a CSV of every test prediction. Each report opens in
its own subtab inside **Reports**, without leaving the run. Arrow keys move
between subtabs; Enter or Space opens the focused report. Kind/tag filters,
live updates and source/attempt attribution remain available. Existing report
detail URLs still work. Linear regression predicts continuous prices, so its
metrics do not include classification accuracy. This deliberately simple model
ignores location and condition: its errors are measured honestly, not hidden
behind a success threshold. Successful execution does not imply a useful
property valuation model.

The run's Graph/Timeline/Metrics/Logs/Outputs/Reports/System Events tab bar
stays at the top of the viewport while scrolling its content. On narrow screens,
the same bar scrolls horizontally; the global header and run summary scroll normally.

Run directly without a backend:

```powershell
dotnet run --file .\backend\samples\workflows\demo\teams\data-science\regression\linear-regression\workflow.cs
```

Direct execution prints reports; registered execution publishes them through
the normal authorized report pipeline. The previous demo sources below remain
as explicit opt-in examples/test fixtures, not the default local workflow set.

### NYC housing logistic regression

`data-science/regression/logistic-regression` shares the **Regression** workflow
type and verified housing cache with linear regression. It predicts whether a
sale price meets `priceThreshold` (default $1,000,000), using square footage,
training-only standardization and binary cross-entropy gradient descent.
Validation reports do not train the model; the final held-out test reports
accuracy, log loss, precision/recall, confusion counts, a training-majority
baseline and a CSV of predictions. Undefined precision/recall remain null,
not fabricated zeroes. Successful execution does not guarantee model quality.

It has separate **Training & validation** and **Full test** profiles, each running
every five minutes and tracking its own Service History card. Training retains
the `classification-experiments` schedule ID and existing series/history, with
`runFinalTest=false`; the new `classification-full-test` instance sets it to
`true`. Both share the scenario defaults under `defaults.scenario`.
`mode=random` samples one
outcome per run: 70% normal execution, 10% timeout (equally split between task
and run deadlines), 10% infrastructure failure and 10% service failure.
`timeoutPercent`, `infraFailurePercent` and `serviceFailurePercent` configure
these probabilities; each must be 0-100 and their sum at most 100.
The selected outcome, fault stage and seed are committed in the configuration
report and node outputs before execution. `seed=0` generates a recorded seed;
a positive seed repeats the selection. These percentages describe injected
faults, not guarantees about real dataset/download or execution failures.

For repeatable diagnosis, force `mode=success`, `task-timeout`, `run-timeout`,
`infra-failure` or `service-failure`. `faultStage` selects `train`, `validate`
or `test` (default random); a test-stage fault requires `runFinalTest=true`.
Task-timeout scenarios genuinely wait for the selected task's 15-second
deadline: the task is **TimedOut**, the run **Failed**, category **Timeout**.
Run-timeout scenarios wait for the actual two-minute run deadline and produce
a **TimedOut** run. Retries are disabled; timeout status is never fabricated.
The task-host transport has no independent 100-second HTTP timeout: execution
remains bounded by the dispatched task/run deadline and cancellation grace.
Recorded timeout messages name the task and execution deadline, retaining the
original worker explanation.
Agent restart recovery keeps completed log spools alive until result records
have been replayed. Authenticated late resource observations for an already
terminal attempt are acknowledged with a warning, without changing its stored
resource history; conflicting historical samples and foreign attempts still
fail validation. This prevents a stale telemetry record from stranding the
worker's durable outbox after a timeout.
Infra faults use runtime `PlatformError`, classified as **Infra / Network**;
service faults use `ValidationError`, classified as **Service / Validation**.
Messages identify the stage, seed and
deliberately simulated cause; no real infrastructure is disrupted.

```powershell
dotnet run --file .\backend\samples\workflows\demo\teams\data-science\regression\logistic-regression\workflow.cs -- --var mode=success --var seed=42
```

Publish with the same `publish-samples.ps1 -IncludeSchedules` command above.
Its configured training/full-test cards include their own scheduled runs and
manual runs started from those same schedule series. Standalone diagnostics
remain available in the workflow's full run history.

### Node and task conditions

Use `.When(Condition.Variable("enabled"))` on a node, or
`.Task("download", handler, when: Condition.Output("dataset", "cacheMissing"))`
on a task. Conditions compare a Boolean variable or output with `true` by default;
pass `expected: false` to invert the check. Variables must be declared as `Bool`.
Cross-node outputs require a declared `.Input("source", "flag")` and dependency.
Only later **series** tasks may condition on their own node's committed outputs;
parallel sibling outputs cannot provide stable pre-dispatch conditions.

False conditions produce durable **Skipped** states, with no task code, attempt,
retry budget or placement reservation consumed. A skipped task does not block the
next series task. A node with every task skipped is also skipped. Existing edge
semantics remain: `OnSuccess` does not accept a skipped dependency;
`DependencyCondition.Always` does. Skipped work does not create outputs.
Missing values and non-Boolean outputs fail explicitly as `ValidationError`
(respecting soft-failure policy), rather than being treated as false.
Decisions appear in the run graph and event/snapshot APIs, persist through
recovery, and are not re-evaluated for retries. Reruns evaluate conditions again
for work that is not reused. Definitions without conditions retain their behavior.

In the run graph, select a node and use **Show debug details** beside its
condition or a task's condition. The expression remains visible after evaluation.
Details include the pinned workflow version, expected/actual Boolean value,
recorded outcome, evaluation time/event, and the referenced value source.
Tasks skipped by a node condition show that inherited gate rather than an
invented task evaluation. **Refresh debug details** reloads committed evidence.

`GET /api/v1/runs/{id}/conditions` provides the same read-only evidence with
Run Viewer authorization and `Cache-Control: no-store`. It returns only declared
conditions, referenced Boolean defaults and source metadata, not complete
variables, output objects or invalid non-Boolean values. Output provenance names
the successful task/attempt that committed the referenced key before evaluation,
including the immediate source run for reused node outputs; later commits are
not substituted. It does not trace arbitrary computations inside task code.
Run-variable details show the captured variable reference and declared default,
but historical default-versus-override origin was not recorded. Missing decision
times or producer attribution remain explicitly unavailable, including legacy
runs; they are never inferred from current values.

For the housing sample, `runFinalTest` defaults to `true`. Supply
`--var runFinalTest=false` during direct execution, or override that Boolean in
the trigger form/API, to skip the final-test node and its reports while keeping
training and validation.

### Archived hourly .NET 10 dataflow example

`backend\samples\workflows\demo\teams\demo\release\five-minute-demo\workflow.cs`
is a file-based .NET 10 workflow with four nodes and eight real SDK tasks:
`order` (load, validate) feeds independent `pricing` (subtotal, discount) and
`shipping` (select-service, quote) branches, which join at `invoice` (total,
publish-report). It performs only local arithmetic and report publication:
no deployment, external request, credential or business-system side effect.
The original `five-minute-demo` workflow name and `every-five-minutes` schedule
key are retained for identity/history continuity; the cron now runs hourly.

Each task records explicit `inputs` and `outputs` objects under its task name.
Those records are the actual node outputs passed through declared `.Input`
bindings, not fabricated UI fixtures. Same-node tasks read the preceding task's
committed outputs. Money uses integer cents; the default order is
3 x 1250 = 3750, less 375 discount, plus 500 shipping = **3875 cents (USD)**.
The validation task bounds customer length, quantity, price and discount.

Run it directly without a server:

```powershell
dotnet run --file .\backend\samples\workflows\demo\teams\demo\release\five-minute-demo\workflow.cs
```

For the running local AppHost, publish the script and its adjacent
`schedules.json` together from the repository root:

```powershell
# Placeholder accepted only by the explicit loopback Development backend.
# A normally authenticated engine requires a real Registrar application token.
$env:WF_ACCESS_TOKEN = 'local-development'
$commit = git rev-parse HEAD
dotnet run --project .\backend\src\Wf.Cli -- publish `
  .\backend\samples\workflows\demo\teams\demo\release\five-minute-demo `
  --engine http://127.0.0.1:5000 --git-commit $commit --git-author 'Local development' `
  --change-note 'Local hourly dataflow demo; source retained with registration' `
  --schedule-source-path backend/samples/workflows/demo/teams/demo/release/five-minute-demo/schedules.json `
  --input .\backend\src\Wf.Sdk --input .\backend\src\Wf.Contracts `
  --input .\backend\src\Wf.Engine.Core --input .\backend\src\Wf.Validation `
  --work-directory .\.wf\five-minute-demo-publish
Remove-Item Env:\WF_ACCESS_TOKEN
```

The supplied Git hash identifies the source checkout; commit the sample first
if exact Git provenance is required for your publication. Registration also
retains the exact script and schedule bytes. The code schedule uses `0 * * * *`
and fires once per hour at UTC minute 00, uses `Skip` to prevent overlapping runs, and remains
stored in PostgreSQL across restarts. The AppHost/engine and agent must remain
running for execution; this is the scheduler's own schedule, not a terminal job.

Open `http://127.0.0.1:5173/t/demo/release/w/five-minute-demo/history`.
The schedule detail page is `/schedules/<schedule-id>`; obtain its ID from
`GET /api/v1/schedules?workflowId=demo/release/five-minute-demo`.
That page shows the recurrence and offers **Trigger now**. Open a run:
**Graph** shows the nodes and their task lists; **Outputs** selects an exact
task attempt and displays its recorded input/output JSON; **Reports** opens
**Node and task inputs / outputs**, a table with four node-level aggregate rows
and eight task-level rows. **Logs** also contain each task's input/output trace.
The direct, serverless script prints the same report JSON instead of uploading
it, because that mode has no configured report storage.

### Archived JSON, retry and failure example

`backend\samples\workflows\demo\teams\demo\release\json-failure-demo\workflow.cs`
adds an unscheduled, synthetic-only demo: `sample` selects the scenario once,
`fetch` retries transient failures with a three-second backoff (at most two
retries), `optional-check` can soft-fail, `validate` can hard-fail, and `report`
uses `Always` dependencies to publish a JSON report after either outcome.
Existing demos and their schedules are unchanged.

Use `mode=random` (default), `success`, `retry` (two retries and an optional
failure), or `failure` (one retry, optional failure and required failure).
`seed=0` generates and records a random seed once; supply a nonzero seed to
reproduce a random scenario. No failure is resampled during retries.
`failureType=service` (default) produces dependency, optional diagnostic and
validation messages. `failureType=infra` produces synthetic network retries and
a synthetic storage failure; no real infrastructure is disrupted. For example:

```powershell
dotnet run --file .\backend\samples\workflows\demo\teams\demo\release\json-failure-demo\workflow.cs -- --var mode=retry
dotnet run --file .\backend\samples\workflows\demo\teams\demo\release\json-failure-demo\workflow.cs -- --var mode=failure --var failureType=infra
```

Publish this directory with the same CLI command and SDK inputs shown above,
using a separate work directory and **omitting `--schedule-source-path`**.
Open `/t/demo/release/w/json-failure-demo/history` and trigger a run with the
desired variables. **Reports** renders the stored JSON as an expandable tree.
The report contains the sampled scenario, successful upstream traces and
observed fetch attempt count; its `expectedRequiredOutcome` describes the
sample, not an authoritative final run status. Failed-attempt outputs are
retained separately rather than propagated to downstream node inputs.

**Graph** uses balanced columns on screens at least 1280px wide: dependency
graph and node list together on the left, selected-node inspector on the right.
Without a selection, the graph and list use the full width. Smaller screens
stack the graph, list and inspector vertically. Expand **Node list** to see the
table; graph/table scrolling stays inside each panel, and the table header stays
visible while scrolling.

Select a node and expand **Inspect recorded inputs / outputs**.
The attempt selector includes failed and retried attempts for that node, with
no substitution between attempts or source runs. Inputs appear only when the
workflow explicitly records them; these demos record both `inputs` and
`outputs`. Refresh reads newly committed attempts during execution. A numbered
retry icon on completed runs shows recorded retries, distinct from the
unnumbered configured-policy icon. Active graphs do not poll attempt history.

Workflow definitions, run history, structured task outputs and report metadata
live in PostgreSQL. Report payloads and artifacts use the configured `IBlobStore`;
the local AppHost defaults to filesystem blobs beneath its local data root
(`%LOCALAPPDATA%\wf\blobs` unless overridden). Reports use
`reports/<runId>/<reportId>` keys. SDK `ctx.Reports.PublishAsync("json", ...)`
uploads through the authorized report pipeline; workflows never write directly
to server filesystem paths. Direct, serverless execution prints the JSON
instead because report storage is not configured there.

### Environment-separated workflow sources

Publishable sources use
`backend\samples\workflows\<demo|production>\teams\<team>\<type>\<workflow>\workflow.cs`;
each team's `team.json` declares its types, metadata schema and explicit
`deployment` (`Demo` or `Production`). Navigation discovers registered teams and
types from these registries; source-folder changes alone do not publish code.
The standalone `demo\scenario-a.*` files remain validation/dry-run fixtures.

Set backend `WF_WORKFLOW_ENVIRONMENT` to `demo`, `dev` or `test` to admit only
`Demo` teams, or `prod`/`ppe` for only `Production` teams. Unknown values stop
startup. Default: `prod`, except the explicit native `--dev` launch selects
`dev`. Native AppHost sets `dev`; Compose forwards its selected `dev`/`ppe`
environment. This setting is independent of authentication and ASP.NET's
hosting environment. Existing registries without `deployment` are classified
as `Production`; explicitly classify existing demo teams when upgrading.
Registrations (including manifest-only and schedule-only updates), resource
authorization and scheduler fires enforce the class server-side. Even a global
Admin cannot browse or trigger the other class. Environment changes do not
cancel already accepted runs or alter stored schedule definitions.

The default demo loader selects only `data-science` (Regression workflows).
The demo tree also retains opt-in `platform` (release, maintenance) and `commerce`
(validation, data), with two workflows per type. They use only local synthetic
data. `commerce/validation/payment-guard` intentionally fails by default so the
history shows a real failed run; rerun with `simulateFailure=false` for success.
No history is backdated or fabricated. Days without runs remain neutral.
The production tree contains side-effect-free `operations` readiness and
maintenance templates, not live deployments.

Explicitly opt in to the eight archived synthetic examples:

```powershell
$env:WF_ACCESS_TOKEN = 'local-development' # explicit loopback --dev backend only
.\backend\samples\workflows\publish-samples.ps1 `
  -Environment dev -Engine http://127.0.0.1:5000 -Team platform,commerce -RunOnce
Remove-Item Env:\WF_ACCESS_TOKEN
```

The loader discovers team/type/workflow directories, checks classification and
uses the existing CLI's shared validation, package upload and registration.
`-RunOnce` triggers real runs. For normal authentication, use a Registrar
application token in `WF_ACCESS_TOKEN` and a user token with workflow Operator
permission in `WF_RUN_ACCESS_TOKEN`; a Registrar-only token cannot trigger runs.
The explicit local development backend accepts the placeholder above for both.
Without `-RunOnce`, publication executes no tasks.
Scheduled sources are skipped with a warning unless `-IncludeSchedules` is
explicitly supplied. Existing schedules are therefore untouched by
the default loader. The relocated five-minute source is version `1.0.1`;
previously accepted `1.0.0` packages/source remain immutable and keep running
until a deliberate republish. Use `-Environment ppe -Team operations` to select
only the production tree against a matching backend; never publish demo
registries into `prod`/`ppe`.

### Workflow run history

Workflow history groups its identity, **Workflow versions** (registered releases,
comparisons and activation), **Metrics dashboard** (completed-run/task metrics),
trigger and subscription actions in a compact header.
Run status is the current result. **Schedule triggered** means the scheduler
created that run; it does not mean a completed run is still running. Normal
schedule starts need no reason. Skipped/missed schedule entries retain their
recorded explanation, or explicitly report an unavailable scheduling reason.
The history table omits variables; it focuses on outcomes, timing, versions
and cancellation audit.
The **Trigger** column shows the authenticated user's recorded display name
under **Manual**, including manual **Trigger now** executions of a schedule.
The name is captured when the trigger is accepted and retained with the run
through command recovery; viewing history as a different user does not change it.
Legacy runs and principals without a name show **User name not recorded**.
Scheduled and catch-up triggers do not invent a human actor.
Its separate **Date / time (local)** column uses the viewer's browser-local
timezone with three-digit milliseconds, not a fixed timezone or the backend
host's timezone. Cancellation audit times in this table use the same timezone;
the original timestamp and full precision remain in each time's `datetime` and
tooltip. Other pages retain their existing local-time display.
The separate **Run ID** column contains the execution UUID and opens that run's
details; it is not a test-definition ID. Schedule events without an execution
show **No run** instead of a fabricated identifier.
**Service history** summarizes workflows and their recent executions.
**Run history** lists a workflow's executions and schedule events.
**Series history** narrows those records to one schedule series, including its
skipped/missed schedule events. Workflow history keeps tracked schedules in the
same table instead of sending readers to separate series links. A **Tracking**
summary names the selected sources, even for an empty result; a **Schedule**
column identifies each row when multiple schedules are tracked. Unknown legacy
series retain their UUID rather than borrowing another schedule's name.
**Executed version** shows the semantic version from that run's immutable
registration (for example `1.2.3`), not today's active release. The internal UUID
stays in the version-details URL and tooltip; legacy missing registrations
display **Version not recorded** rather than a guessed current version.
Workflow authors manually set SemVer in `Workflow.Define("team/type/name",
"1.0.1", ...)`. Changed workflow source, package or manifest cannot overwrite
an existing version: registration rejects reused versions with HTTP 409.
Increase the code-owned version before publishing (PATCH for compatible fixes,
MINOR/MAJOR as required by the API compatibility validator); versions are never
automatically derived from Git or incremented by the UI. The real CLI publication
integration test changes task code without a bump and requires rejection, then
changes the version string and requires successful registration without rewriting
the original version.

Open a queued/running run and select **Cancel run** (Operator/Admin permission).
A reason and confirmation are required. The authenticated actor's name/ID,
timestamp, exact reason and UI/API/CLI source are retained for audit; retries
preserve the first accepted request. Cancellation does not pause the schedule.
Acceptance is not completion: cleanup may continue until the run is **Aborted**.
History shows a **Cancellation** column only when a loaded row has cancellation
facts or an Aborting/Aborted status. These records support operator handoffs and
incident/audit investigation; missing legacy attribution is never invented.

### Service history

Service history cards and tables show individual run instances inline.
Each status-colored icon represents one run, with a distinct status symbol,
run ID and timestamp; clicking it opens that exact run in a new tab.
Failed runs use a red cross; timed-out runs use a purple alarm clock and a
separate legend entry.
The **Run outcome summary** accordion above the cards is open by default;
Enter or Space toggles it. It shows timed-out runs, failed runs, Infra failures,
Service failures, unknown causes and counts by failure type/category.
Nonzero failure counts use red accents; timeout and unknown-cause warnings use
yellow/amber accents, with labels and numbers retained in both themes. Infra and
Service breakdowns occupy separate rows, each containing only its own categories.
Zero counts remain neutral.
Counts use unique run IDs across the currently loaded, visible configured cards
in the selected team/type, with each card's personal recent-run limit applied
before deduplication. Shared runs count once. These are not all-time totals:
load more workflows, change type or run count, refresh or receive live updates
to update the scope. Switching table/card format or metadata grouping does not
change it. Loading or failed card queries show an explicit state, not zero counts.
Infra/Service/category counts use only each failed or timed-out run's primary
terminal cause, excluding optional and recovered failures; missing terminal
causes appear under **Unknown cause**. A task deadline produces a **Failed**
run with category **Timeout**; the **Timed out runs** tile counts run deadlines.
**View last run** opens the exact latest run and is
disabled for never-run workflows; **History** opens that workflow's run history.
Cards are configured only in the workflow's applied `schedules.json`, not in the
UI or browser preferences. Optional `historyCards` defines up to 32 ordered cards,
each with a stable unique `id`, friendly `name`, optional `visible` (default
`true`) and 1-32 distinct `scheduleIds` referencing the file's instance IDs:

```json
"historyCards": [
  {"id":"training","name":"Training & validation","visible":true,"scheduleIds":["daily-training-validation"]},
  {"id":"combined","name":"All experiments","visible":false,"scheduleIds":["daily-training-validation","daily-full-test"]}
]
```

Selecting a workflow type shows a top-level panel per workflow, without a
redundant type container. On the **Regression** page, **Linear Regression** and
**Logistic Regression** are peer panels stacked vertically at every screen size,
each containing its own **Training & validation** and **Full test** instance cards.
Across all workflow types, workflow panels also stack vertically, while type
containers retain the nested workflow panels. Table format retains one row per
configured card, including its exact workflow identity. Metadata grouping remains
available and does not change card membership or run counts.

One schedule can appear in multiple cards. Add/remove references, change names,
reorder cards or set `visible:false`, then publish the schedule file; refresh/live
updates reflect the active committed configuration for every viewer. Hidden
cards never pause schedules or delete runs. `historyCards:[]` hides all cards for
that workflow. Omitting `historyCards` preserves one backward-compatible card
tracking all schedules (including future ones) and unscheduled runs.
Each instance can supply an optional friendly `name`, independent of its stable
`id`; renaming preserves schedule and series history. Card IDs use 1-128 ASCII
letters/digits, `_` or `-`, starting with a letter/digit; names use 1-200 characters,
without control characters or leading/trailing whitespace. Duplicate card IDs,
duplicate references, missing instances and invalid fields reject registration
atomically. The read-only **Tracking** summary marks paused/deleted schedules.
Latest status, last-run links, run icons and success rates all use the selected
sources; filtering happens on the server **before** the recent-run limit.
**History** carries that same selection into the existing workflow history page,
and its ordinary status/outcome filters preserve the tracking selection.
Runs started through **Trigger now** remain associated with their selected schedule;
configured cards exclude runs without a schedule series (including standalone
manual runs and reruns), while the backward-compatible all-runs card includes
them. A card tracking multiple schedules uses one History table, with an additional
**Schedule** column identifying each run's named source. A card with no matching
runs shows an explicit empty state, never unrelated runs.

The **Recurring jobs** navbar dropdown mirrors **Service history**: choose
**All teams**, or a team followed by **All workflow types** or a specific type.
Links open `/recurring-jobs` with the exact team/type filters, and scoped page
titles use the selected team's display name. The page shows all nondeleted Code
and User schedules you can view, including paused schedules, in tables grouped
by team, workflow type and workflow. Each workflow has a distinct header band
with a strong divider, workflow name, exact ID and count of jobs shown;
each scheduled job has its own row with status, source, recurrence, schedule
timezone, local next-fire time, version selection, and links to schedule details
and series history. Team and workflow-type filters persist in the URL. Tables
scroll within a keyboard-focusable region on narrow screens. Results load 50
at a time; **Load more recurring jobs** adds the next page. The list refreshes
every 30 seconds. **Trigger now** requires Operator permission for that series and starts
one Manual run using the saved variables and version selection. It never resumes
a paused schedule or changes its next automatic fire. Resolved secret values
are never included in the listing.

Service-history page-content navigation buttons, along with workflow history's
**Workflow versions** and **Metrics dashboard** buttons, open new tabs.
Each shows an outgoing-link icon and announces its new-tab behavior to screen
readers. Navbar links stay in the current tab; action buttons are unchanged.
Timestamps keep their full source precision in the time element while displaying
local time with millisecond precision.
Both formats show the latest **10 runs per card** by default, newest first,
with **no date cutoff**. Open your account menu -> **Settings** -> **Recent runs
per card** to save a count from **1 to 30**. The hard maximum is 30 in
personal settings and the API; larger saved values show an error and use the
default 10 until a valid preference is saved. This personal preference is
saved separately for each signed-in account and tenant in this browser; the
explicit local-development identity has its own preference. It is not synced
between devices. **History** retains the full matching list.
Success rates use the completed instances currently shown. The percentage sits
beside the recent-run count; its accessible label explains completed success.
Run icons wrap after at most ten per row, growing card height rather than
reserving extra column width for larger preferences.
Cards within each grid row share the tallest card's natural height, with action
buttons aligned at the bottom; expanded failure details remain fully visible.
Workflows without runs remain visible with an explicit empty state.
Older saved view URLs ignore the obsolete `window` parameter; personal run
count takes precedence. The service-history API uses `runLimit=1..30`
(default `10`) independently of the workflow page `limit`. It no longer accepts
the daily `window` filter or returns daily aggregates; each item contains
bounded `runs` across all retained history.
**Container grouping** defaults to workflow type across all types, or workflow
within a selected type. Select a metadata key such as
`environment` or `component` to create a container per exact metadata value.
Keys come from authorized type schemas and loaded workflow metadata; missing
values have a **Not set** container and empty values stay distinct. Grouping is
saved in the URL and changes presentation without repeating the history request.
Live updates remain subscribed to every loaded workflow type, even when one
metadata container combines several types. Run changes automatically reload the
displayed history while the live connection is active; reconnecting resynchronizes
it. **Refresh service history** manually reloads the displayed history, workflow
types and display-name configuration without starting any runs. If live updates
fail, use the displayed **Retry live updates** control to reconnect.

Service names use capitalized display labels, not renamed identifiers.
Team `presentation.labels` supplies metadata-key labels such as **Component**.
The optional `presentation.names` dictionary supplies exact resource/grouping
labels, for example `"data-science": "Data Science"`,
`"data-science/regression": "Regression"` and
`"data-science/regression/linear-regression": "Linear Regression - NYC Housing Prices"`.
Unconfigured names turn hyphens/underscores
into spaces and capitalize word initials without lowercasing existing acronyms.
These names appear in service navigation, team headings, containers and workflow
cards/tables. IDs, URLs, metadata values, filters, schedules and run history
remain unchanged. The sample team configurations are authoritative for these labels.

To browse the published samples, open **Service history** in the navigation,
select **Data Science**, then **Regression** (or **All workflow types**).
The direct team view is `/t/data-science/services`; choose **History** on the
**NYC Housing Prices** card for every run of that workflow. Archived example
teams appear only if explicitly published. Source files alone do not appear in
navigation until published with the sample loader above.

`Wf.Engine.Core.FailureClassifier` reuses the existing `FailureCategory` contract:
TaskError, Timeout, Aborted, AgentLost, ResourceLimit, ValidationError, Placement
and PlatformError. Local execution and the SDK task host preserve explicit
`TaskFailureException.Category`/nonretryable metadata before considering
`TimeoutException` or actual timeout/abort facts. Bare/self cancellation without
an engine cancellation cause is an author TaskError, not an assumed Aborted;
ordinary author errors are not relabeled by concurrent cancellation. Unknown
origins default to PlatformError; callers inside task execution supply TaskError.
No exception-message or generic out-of-memory/argument-error guessing occurs.
Attempt lifecycle status and existing author/platform retry budgets remain
separate from category. Scripted typed signals exercise all eight categories
through actual local and child SDK hosts; this does not implement every external
fault producer. The committed metrics path described below preserves these known
categories through gateway completion, durable attempts/events and replay.

Display classifications are separate from these scheduler categories. The
central, version-controlled `failure-categories` directory contains shared
`system-infra\rules.json` and team-owned `teams\<team>\rules.json` files. Each
file has a `rules` array of `{ "id", "contains", "category" }` objects. Matching is
literal, case-insensitive substring matching against the recorded, redacted
message: first shared infrastructure rule, then first owning-team rule, in
file order. A match labels the failure **Infra** or **Service** without changing
its original message, scheduler category or retry policy. Unmatched messages
show **Unclassified**, except typed Placement failures show **Infra / Placement**.
Typed AgentLost, PlatformError and ResourceLimit failures default to Infra,
other failures to Service.

The control plane validates and loads the catalogue at startup; malformed
rules fail startup explicitly. The default catalogue is packaged with the
application; `FailureClassification:Root` can point to a deployment-managed
directory with the same layout. Rule IDs must be unique within a file; text
must be nonblank and contain no control characters. Limits: 64 KiB/file,
128 rules/file, 1,024 teams, 80-character IDs, 256-character matches and
64-character categories. Changes require a control-plane deployment/restart.
Labels are resolved at read time, so a new catalogue can relabel historical
messages; original recorded diagnostics remain unchanged.

Run cards, run tables, workflow latest-run summaries, workflow history and
service history show type/category, impact and the primary message. Terminal
failures take priority over retrying, optional and recovered failures.
Successful retries remain **Recovered**, not run failures. Run details show
every recorded failed attempt under **Failure diagnostics**; Timeline and
the exact-attempt output inspector show its full message and scheduler category.
Missing historical messages are explicitly identified, never reconstructed
as original text. Pre-dispatch task failures are read from committed task events
even when no attempt was created. Placement wait failures explain that no
eligible healthy worker became available and the task did not start; new events
record the 10-minute wait limit. Historical typed placement events without a
message receive a scheduler-category explanation, not a fabricated original
message or attempt. Summaries wrap the reason and mark unstarted tasks explicitly.
Placement wait failures remain **Failed**, separate from execution **Timed out**.
Completed runs load diagnostics once; active runs require
**Refresh failures**, without adding graph polling.

`RunSnapshot.Metrics` projects run, lifecycle-stage/node, task and attempt timing
only when committed events are applied. Queue wait is separate from execution;
task duration sums actual running-attempt intervals, excluding retry backoff,
placement wait and recovery-handler delay. Retry counts increase on another
actual dispatch, not a scheduled retry that is later cancelled. Every persisted
dispatcher attempt has `queue_wait_ms`. Prestart loss has zero execution;
aborted, reused and skipped entities retain their actual lifecycles. Durable
attempt identities and existing author/platform retry budgets are preserved.

The shared projection uses creation/event timestamps rather than another clock.
Full replay and serialized snapshot-plus-tail recovery agree; legacy snapshots
without metrics reconstruct from their original sequence-zero snapshot and
retained committed history. Missing history fails explicitly rather than
inventing times. Failed transactions leave caller-visible proposals unchanged.
These are transition metrics, not process-resource sampling.

`GET /api/v1/runs/{id}/metrics` requires fresh stored-resource Viewer access and
returns committed run, lifecycle-stage, node, task and attempt timing, actual
retry totals and known failure categories. Active values are recorded values,
not a second ticking clock. Pending accepted runs expose their queued baseline;
skipped/reused/nonstarted work does not acquire invented attempts or timings.
The response includes the authoritative sequence and no variables, metadata,
error messages, outputs, payloads or storage capabilities.

Typed producer categories survive gateway completion, `task_attempts`, committed
events, snapshots and replay without changing retry budgets or the existing enum.
Unknown/absent/numeric/noncanonical wire names remain unknown; messages are never
classified by guessing. A parent without an explicit cause exposes a category
only when its terminal failing children have one unambiguous known cause.
Historical attempt failures remain after a later retry succeeds. Responses are
never cached; malformed identities/queries return 400, denied scopes 403,
inconsistent persisted data 500 and unavailable storage 503. Legacy metrics-free
snapshots rebuild from retained original history instead of fabricated zeros.

### OpenTelemetry workflow trends

`Wf.Engine.Core` owns the `Wf.Runtime` meter, consumed automatically by both the
distributed engine and the SDK's `LocalEngine`. Workflow authors do not implement
duration/status metrics or call a business-metrics API. The library uses
`System.Diagnostics.Metrics`; exporter setup belongs to the hosting application,
not to each workflow.

The control plane captures immutable completion data inside the transaction,
then emits **after a successful commit**. Projection, failed transactions,
snapshot restoration and exact group-commit replays never emit completions.
Node/task/attempt timing, retries and observed resource summaries emit with the
workflow completion, not on each resource sample. The leader separately samples
current durable Queued/Running/Aborting counts every 15 seconds, including zeros
for registered workflows with no active runs. Followers contribute zeros.

| Meter instruments | Meaning |
| --- | --- |
| `wf.run.completed`, `wf.node.completed`, `wf.task.completed`, `wf.task.attempt.completed` | Counters partitioned by terminal status and known failure category |
| `wf.run.duration`, `wf.node.duration`, `wf.task.duration`, `wf.task.attempt.duration` | Execution-duration histograms in **seconds**; no invented duration for work that never started |
| Corresponding `*.queue.duration` histograms | Queue/placement/retry wait, separate from execution |
| `wf.run.retries`, `wf.task.retries`, `wf.run.active` | Actual redispatch counters and current durable run-count gauge by status |
| `wf.task.cpu.average`, `wf.task.cpu.max`, `wf.task.memory.peak` | Observed per-attempt CPU-percent and peak-memory-byte distributions |
| `wf.task.network.sent`, `wf.task.network.received`, `wf.task.resource.samples`, `wf.task.cpu.observed.duration`, `wf.task.network.observed.duration` | Observed network-byte/sample counters and observation-coverage histograms in seconds |

Labels are restricted to registered team, workflow type, workflow ID, node/task
keys, status and a known failure category. The exporter adds deployment
environment and service-instance resource attributes. Run IDs, attempt numbers,
users, variables, arbitrary metadata, errors and secrets never become metric
labels. Task resource scopes/availability remain in the existing authorized
resource API; unavailable CPU/RAM/network values are omitted, not zero-filled.
Local SDK runs use team `local`, the definition's type and workflow name; an
embedding host can subscribe its OpenTelemetry provider to `Wf.Runtime`.

Local profiles configure **OTLP/HTTP protobuf -> Prometheus -> Grafana**.
Prometheus's OTLP receiver is enabled explicitly; no beta Prometheus exporter,
extra Collector or public application `/metrics` endpoint is required.
`OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` is the full metrics URL, for example
`http://127.0.0.1:9090/api/v1/otlp/v1/metrics`. Compose uses
`http://wf-prometheus:9090/api/v1/otlp/v1/metrics` inside its isolated network.
`OTEL_METRIC_EXPORT_INTERVAL` overrides the 15000 ms default.
Production has no default endpoint; configure an authenticated OTLP receiver
and standard OTLP exporter headers as appropriate. `Telemetry__Enabled=false`
explicitly disables export, including the Development default receiver.

The provisioned **Workflow runs** dashboard filters environment/team/type/workflow
and shows current status counts, terminal outcomes, completion/failure-rate
trends, duration mean/p50/p95, queue wait, retries and observed task resources.
Prometheus translates dots/units into names such as
`wf_run_completed_total` and `wf_run_duration_seconds_bucket`. Histogram
quantiles are bucket estimates, not the exact PostgreSQL-history quantiles.

Prometheus owns metric time series in its persistent volume; Grafana owns
dashboard state and queries Prometheus. PostgreSQL remains the authoritative
run/event/attempt history, including individual run IDs and timings; existing
resource blobs/summaries and authorized history APIs remain unchanged.
OTLP is best-effort operational telemetry, not an exactly-once audit ledger:
exporter restarts reset counters, transport outages/crashes can lose samples,
and an ambiguous committed transaction acknowledged on replay can lack its
completion sample. No historical backfill or synthetic counter replay occurs.
Trends begin when collection starts; rate/quantile windows need repeated
exports and new completions before showing meaningful data.

Migration `0036_daily_rollups.sql` backfills retained terminal runs and adds
transactional daily workflow/node summaries. Buckets use the immutable UUIDv7
UTC creation day; `node_key=''` denotes the workflow total. Both normal and
group-commit terminal writes refresh their affected buckets before acknowledging;
group commits first project committed timestamps and node states. Ordered
transaction-scoped bucket locks prevent lost concurrent updates. Replays
recompute exact source counts and interpolated p50/p95, never add counts twice or
average percentiles. Failed rollups roll back the terminal transaction.

Failures include failed, aborted and timed-out runs; node hard failures include
failed, timed-out and cancelled nodes. Soft failures are counted separately
(one affected run, or one affected node). Skipped/reused/nonstarted nodes count
as occurrences but contribute no duration without a recorded start and end;
`duration_count` exposes that distinction. Clock-skewed measured intervals clamp
to zero. CPU/RAM retain maxima across every attempt, including failed retries;
RAM uses MiB. Missing duration/resource observations remain null, not fabricated
zeros. Dashboard APIs/UI remain separate work items.

`GET /api/v1/series/{id}/trends?window=7d|30d|90d` uses fresh stored-series
Viewer access (default `30d`), including retained legacy series and deleted
schedules. One window selects UTC creation days including today: `from`
inclusive, `to` exclusive. `totals` and every gap-filled daily row expose completed
run counts, successes, failures (including aborted/timed-out), observed-duration
count, success fraction `0..1` and exact interpolated source p50/p95 milliseconds.
Empty counts have null rates/quantiles; missing timestamps never become observed
zero durations. Streaks count the latest contiguous success/failure outcomes in
the window by UUIDv7 creation order, ignoring active runs and missed/skipped fires.
No variables, errors, metadata or storage pointers are returned. Invalid
identities/windows, denial and unavailable storage return explicit no-store
problems; unknown/duplicate query parameters are rejected. Actual signed-JWT HTTP
acceptance on 90 days at 2,000 runs/day
measures p95 below 500 ms, including authorization and exact window quantiles.

The series page retains Run history as its default view. Its Trends link opens
`/series/<uuid>?view=trends`; `trendWindow=7d|30d|90d` persists the selected window
through reloads and view changes without applying run-history filters to trends.
Summary counts, success/failure streak and measured-duration quantiles accompany
the existing uPlot renderer's UTC daily charts and a complete scrollable table
alternative. Empty windows remain explicitly unmeasured. Current Viewer checks
gate requests and hide cached charts/tables during revalidation, permission
errors and failed reads; permission revalidation does not retry-loop a denied
read. Refresh is explicit. Browser acceptance exercises real signed-JWT API and
PostgreSQL responses in Chromium, Firefox and WebKit, including 90-day
response-to-painted-chart/table rendering below 500 ms, URL restoration,
history navigation, actual denial and desktop-light/mobile-dark accessibility.

Workflow dashboards at `/dashboards/workflow/<base64url-workflow-id>` show four
daily UTC line plots: execution latency (p50/p90/p99/p99.9 together), failure rate,
maximum node CPU and peak node RAM. `p999` means p99.9, not the 999th percentile.
Latency uses completed runs' started-to-ended time, excluding queue time; missing
timestamps remain unmeasured. Whole-window percentiles come from source durations,
not averages of daily percentiles. Failure rate includes failed, aborted and
timed-out runs among completed runs. A separate bar chart counts every current
run status, including queued/running/aborting, for runs created in the selected
7/30/90-day window. Resource maxima include failed retries; gaps remain null and
measured zero stays zero. Sample counts and accessible chart-data tables explain
the measurements. Existing node-ranking tables retain run drill-down links;
schedule, node and machine dashboards retain categorical ranking bars rather
than misleading lines between unrelated nodes. The existing dashboard API adds
`trends` only for workflow scope (null for other scopes), with gap-filled days
and exact whole-window totals read from the same database snapshot as rankings.
Workflow history also embeds the same dashboard in a **Metrics dashboard**
accordion, collapsed by default. Dashboard code and reads load only when opened;
closing it removes the charts, and reopening checks current Viewer access again.
Its window/top controls are independent of run-history filters and remain selected
while the accordion is collapsed. Navigating to another workflow resets the panel.
The existing metrics-page link remains available.

Secret variables accept `secret://<store>/<name>` through the existing
`SecretReference` contract and `.Secret("token", "secret://local/deploy-token")`.
`SecretReference.Parse` requires a qualified reference; `Store` and `SecretName`
are computed, not serialized. Existing safe bare/hierarchical names remain
local-store references for compatibility. Store aliases use 1-63 lowercase
letters/digits/hyphens; secret names use 1-200 ASCII letters/digits, `.`, `_`, `-`
and safe slash-separated segments. Credentials, query strings, fragments,
percent escapes, whitespace and traversal segments are rejected without echoing
the input. Manifests still store exactly `{"Name":"reference"}`; literal string
secret defaults and extra value fields fail shared WFV008 validation. A
name-looking string inside `Name` cannot be distinguished from an intended
reference; never put resolved values there.

Agents resolve declared references at actual task start, not during describe,
registration or dispatch. Default `Secrets:Provider=Local` uses a private
`LocalApplicationData\wf-agent\secrets` directory; override with the absolute
`Secrets:LocalDirectory`. Provision under the same OS account as the agent by
piping a trusted UTF-8 producer into `wf-agent --set-secret secret://local/service/token`.
Values must arrive through redirected stdin, never arguments or environment.
Windows stores DPAPI CurrentUser ciphertext with private ACLs; Linux uses
owner-only directories/files, **not encryption at rest**. Unsafe ownership,
permissions, links and paths fail closed.

Optional `Secrets:Provider=KeyVault` uses explicitly configured
`Secrets:Vaults:<alias>:VaultUri` and managed identity only. Optional
`ManagedIdentityClientId` selects a user-assigned identity; omission selects the
system-assigned identity. No developer/browser/CLI credential fallback.
`ctx.Secret(ctx.Var<SecretReference>("credential"))` accesses a declared resolved
value. Missing or unavailable secrets become safe nonretryable `ValidationError`;
resolved values never enter manifests, dispatches, journals or process arguments.
Rebuild existing SDK packages: task-host private readiness protocol 9 is required.
Provisioning and provider details: [agent secrets](backend/src/Wf.Agent/Secrets/README.md).

`Tasks.HttpOperation(...)` declares an opt-in long-running HTTP operation:
submission URL, status/state mapping and retention, optional cancel URL,
deduplication header/retention, lookup-by-key contract, trusted origins and secret
reference. Attach the returned `HttpOperationTask` directly with `.Task(...)` so
its metadata reaches the manifest; passing its delegate alone loses that contract.
Shared WFV021-WFV023 rules reject unsafe URLs/templates and require both
`.NoAutoRetry()` and zero author retries without deduplication or lookup. Retry
contracts must retain remote identity through the entire run timeout. Ordinary
`Tasks.Http` remains one-shot, including 202 responses.

Execution requires a durable checkpoint publisher and `ctx.RunId`/`ctx.TaskKey`;
ordinary offline local execution fails before any request. The runner durably
acknowledges `SubmissionPending` before POST, using a stable run/task key across
attempts, then acknowledges `RemoteSubmitted` before polling a 202's status URL.
Checkpoints contain contract/request hashes and remote identity, never the request
body or bearer value. Restored submitted attempts only GET the retained status;
restored pending attempts reconcile by authoritative lookup or resubmit the same
key within the declared service deduplication guarantee. A lookup miss alone never
authorizes another POST. Expired guarantees or missing remote status produce
nonretryable `RemoteOutcomeUnknown`, not a guessed success or a fresh submission.

Polling honors delta/date `Retry-After`, with bounded exponential fallback;
each request has the declared timeout, task cancellation and retention deadline.
Immediate terminal responses complete without a poll. Declared result/error
fields become `result`/`error` outputs; service errors are otherwise generic.
Responses stay within the existing output bound. Redirects, unsafe references,
undeclared origins and malformed states fail explicitly. Authentication reuses
the existing agent-resolved `ctx.Secret` bearer reference; no new login flow.
Abort/timeout attempts the declared POST/DELETE cancellation under its own bounded
timeout (at most ten seconds), independent of the cancelled task token. A 2xx
response alone never confirms cancellation: the configured terminal state must
arrive in the cancel response or a bounded status check. Missing references,
unavailable cancellation and unconfirmed responses record `OutcomeUnknown`; the
local outcome remains Cancelled/TimedOut even if remote success/failure is
observed during cleanup. Cancellation never resubmits the original request.

Fixed lifecycle phases become durable per-attempt System Events: submission
pending, submitted, waiting, resuming, succeeded, failed, cancel requested,
cancel confirmed and outcome unknown. Their bounded payload contains only
sequence, phase and occurrence time, never URLs, request/response bodies or
credentials. Progress waits for the agent's durable journal, not engine ACK, so
an engine outage cannot block cleanup. Reconnect replays in order; session,
attempt and physical-reservation fences discard stale progress. Graph state,
attempt status and timers remain unchanged. Individual status polls are task
logs, not System Events.

Run the actual remote API fault matrix with
`dotnet test backend/tests/Wf.Integration.Tests --filter FullyQualifiedName~RemoteOperation`.
The backend Windows/Linux CI matrix retains a separate `remote-http` report.
Cases use actual SDK child processes, durable PostgreSQL checkpoints and a
stub service: immediate/202 terminal states, occupied agent capacity, lost
responses, killed hosts before/after submission, deduplication, lookup-only
hit/miss, retention/status expiry and abort/deadline cancellation. Host-loss
replacement simulates the engine's lost-machine classification; it is not a
physical machine-reboot or agent re-adoption claim.

`await workflow.RunAsync(args)` selects local execution by default, `--describe`,
`--engine <origin>`, `--task-host --socket <absolute-path>`, or
`--dry-run <mocks.json>`. Modes are exclusive; repeated `--var key=value` pairs
work before or after local/remote/dry-run mode flags. Describe emits only the
manifest, without binding required run values or executing tasks.
Remote mode validates variables and POSTs `workflowId`, exact `version` and
overrides to `/api/v1/runs` with an idempotency key, then prints the accepted
run UUID. It does not upload/register the workflow or wait for completion.
Non-loopback HTTP, redirects, invalid/oversized replies and rejection statuses
fail explicitly.

Stage and node task delegates publish with
`await ctx.Reports.PublishAsync(kind, title, payload, ctx.CancellationToken, tags)`.
Inject `new ReportPublisher((publication, token) => ...)` through the existing
`LocalEngine(..., reports: publisher)` port. Every publication carries the
registry's exact full source key, including `$post/summary` for a named post-stage
task; parallel delegates never share mutable source state. Payload bytes and
tags are copied before the sink awaits. Kind/title/tags must be nonblank, unknown
kinds remain valid, and metadata is not trimmed or rewritten. Empty payloads are
valid. Cancellation and sink failures propagate into the ordinary task failure
policy; the publisher does not retry. Missing or unbound publishers fail
explicitly. Existing custom `IReports` implementations remain compatible.
The default SDK quota is 10 reports per run, each at most 10,000,000 bytes
(decimal 10 MB). Successful and in-flight publications share an atomic budget
across all contexts; oversized payloads fail before copying or calling storage.
Sink failure/cancellation releases its local reservation without hiding the
original exception. Reusing the publisher for another LocalEngine resets that
run's budget. `ReportLimitExceededException` is nonretryable and follows the
ordinary Hard/Soft task failure policy; post-stage cleanup still runs.
Pass a validated `Wf.Contracts.ReportLimits` as the publisher's second argument
for overrides. Its `FromTypeLimits` parser accepts the `workflow_types.limits`
shape `{"reports":{"maxCount":10,"maxBytes":10000000}}`, with either field
optional. Values must be positive integers; unknown or duplicate report settings
fail explicitly. Other limit sections remain independent. Shared contracts keep
SDK/server policy parsing identical; the local counter is not a durable storage
receipt.

Remote task hosts now receive an attempt-scoped report capability from the real
agent gateway. Its optional protobuf `bearer_token` field leaves old grants
wire-compatible and keeps credentials out of request URLs. The agent accepts
report destinations only on configured engine origins, with no credentials,
query, fragment, alternate endpoint or duplicate grant. The SDK uses exact
HTTP/2, no redirects and the Authorization header, then validates a bounded
committed receipt. Publishing requires a real Dispatched/Running attempt in a
Running/Aborting run; no user-supplied source or cross-run override is accepted.
Capability expiry is the attempt deadline plus 24 hours for immutable retries.

`POST /api/v1/report-publishes/{reportId}` streams only payload bytes to the
existing blob store. Bounded metadata comes through `X-Wf-Report-Metadata` as
base64url JSON: kind, title, tags, exact length and SHA-256. Kind/title/tag limits
are 128/512/128 UTF-8 bytes, with at most 32 tags; local SDK publishing applies
the same description rules. The server reads the actual workflow type's limits
and serializes admission in PostgreSQL across all hosts, without holding engine
run-state row locks during upload. It verifies length/hash and immutable
`reports/{runId}/{reportId}` content before committing a metadata/pointer-only
report row. A compact global ID locator prevents cross-run/month ID reuse while
the report records remain monthly partitioned.

The same report ID with exact source/attempt/metadata/content replays without
consuming quota or emitting another event, including after task completion.
Different content conflicts. A committed row emits transactional `ReportAdded`
notification IDs only; rollback emits none. Blob success followed by database
failure can leave an unreferenced immutable object; an exact retry can repair the
pointer, and retention cleanup must collect unreferenced objects/ID locators.
Failed or cancelled streams leave no report row or final object. Signed-token,
permission, malformed-policy, count, byte, storage and receipt failures remain
explicit. This upload endpoint is not a viewer download capability.

`GET /api/v1/reports/{id}/content` requires fresh Viewer permission on the
report's durable run scope (including historical runs whose workflow was
deleted). It streams verified immutable bytes without buffering the whole report
or exposing storage paths/grants. A late size/hash fault aborts the response,
never successfully completes a partial body. All successful content responses,
including direct navigation, carry CSP `sandbox allow-scripts; frame-ancestors
'self'; default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline';
img-src data: blob:; font-src data:; connect-src 'none'; form-action 'none';
base-uri 'none'`, `nosniff`, and `no-store`; only these
responses omit the default `X-Frame-Options: DENY`. Denials/errors retain DENY
and a stricter sandbox. Known exact kinds use safe MIME types; untrusted titles
never become filenames. Generic `image` and unknown kinds download as
`application/octet-stream` (clients may identify raster magic on fetched bytes);
explicit `png`, `jpeg`, `svg`, and their exact MIME kinds have truthful types.
The content OpenAPI response is a binary string, not a reflected Stream object.
Inline scripts/styles and embedded visuals work without allowing fetch, forms,
base-URL overrides or external resources. Source restrictions do not prevent
every self-frame navigation. Authenticated clients fetching a Blob must preserve
equivalent CSP/sandbox restrictions when rendering it; response CSP is not copied
automatically to an object URL.

`GET /api/v1/runs/{id}/reports` requires fresh Run Viewer permission and returns
`{runId,items:[{id,runId,sourceKey,attempt,kind,title,tags,sizeBytes,createdAt}]}`.
All fields are required/non-null; source/attempt are exact historical facts and
timestamps are committed UTC, not active clocks. No payload, hash, blob path,
capability or variables appear. Optional single `kind`/`tag` filters are exact,
case-sensitive, nonblank, at most 128 UTF-8 bytes, and combine with AND.
Duplicate/unknown filters fail with 400. Results use stable `createdAt,id` order
and `no-store`.

`GET /api/v1/series/{id}/reports/compare?leftRunId=...&rightRunId=...&sourceKey=...&title=...`
looks up the same exact source/title in two distinct actual runs of the stored
series. Returns `{seriesId,left,right}`, with each side the same required,
metadata-only report item above; fetch content separately. Fresh Viewer grants
must cover the series and **both** runs, including retained historical ownership
after workflow deletion. Missing/foreign/ungranted resources uniformly return
403 `insufficient_scope_role`, without exposing either side's metadata.
Series IDs use canonical lowercase nonempty UUIDs; run IDs require canonical
RFC UUIDv7. All four query values are required exactly once; unknown/duplicate
keys or invalid identities return 400 `invalid_report_compare_query`.
Source/title matching is case-sensitive, preserves author whitespace, Unicode
and historical stage/task keys without normalization, and is bounded to
1025/512 UTF-8 bytes respectively. Neither value may be blank or contain NUL.
If either match is absent, 404 `report_match_not_found` identifies neither side.
If both exist but either run has multiple matches (even across attempts or
kinds), 409 `report_match_ambiguous` requires an unambiguous source/title:
no arbitrary, newest or cross-attempt selection. Missing takes precedence over
ambiguity. Dependency failures return 503 `report_compare_unavailable`.
Comparison is read-only, bounded to two matching metadata rows per run partition,
and uses `no-store` with no content, pointer, capability or report metadata headers.

The SQL `report_added` notification is consumed by the existing live listener.
A separate immediate `ReportAdded` MessagePack message `{runId,reportId}` reaches
only an authorized subscribed run scope, with fresh role checks on every send.
It is an index invalidation, **not** a lifecycle event or `RunDelta` sequence.
The durable fact is the report row: on live `Resync` for a run, listener reconnect,
or client transport reconnect/resubscribe, explicitly refetch the report index
alongside normal durable run resynchronization. Notifications missed while
disconnected cannot be replayed; the index recovers them without REST polling.
Pending overflow or malformed/unavailable notifications also request explicit
resynchronization instead of silently losing updates.

`POST /api/v1/runs` accepts `workflowId`, optional exact `version` (SemVer or
version UUID), and optional `variables` (an empty object by default). A delegated
Operator or scoped Admin must own the workflow's team/type; Author and Viewer
alone cannot trigger. Global Admin also works for development/service calls.
The authenticated `oid`, never a request field, supplies the triggering actor.
Omitted versions resolve through the hosted active-version cache; explicit
versions must belong to the same undeleted workflow. Selected UUIDs remain
pinned through later activations. An unavailable cache returns 503 rather than
using stale data.

All nine variable types and workflow defaults resolve through shared
`Wf.Validation`; invalid/unknown/missing-required values return 400
`application/problem+json` with `errors[]` paths and hints, without echoing values.
Malformed, duplicate, null-required or unknown request fields fail closed;
placement overrides remain unsupported until their own task. Bodies above
1 MiB return 413 and non-JSON content returns 415. Acceptance atomically inserts
the immutable initial snapshot/deadline command and emits PostgreSQL `commands`
NOTIFY, then returns `202 {"runId":"<UUIDv7>"}`. Only leader recovery creates
the durable run rows. The run-detail endpoint remains a separate task.

Manual trigger and schedule run-now acceptance also commit a safe audit row in
that transaction: authenticated actor/kind, action, run/team target, acceptance
timestamp, null prior run and queued version/series summary. No variable values
or upload capabilities enter the summary. Audit persistence failure returns an
explicit 503 and rolls back the command and notification; this is not a
best-effort write after accepting the run. Broader endpoint audit coverage
remains a separate, unfinished work item.

Optional `Idempotency-Key` accepts one 1-200-character visible ASCII key,
without whitespace. PostgreSQL deduplicates for 24 hours per authenticated
principal and route, across concurrent API hosts and restarts. A matching retry
returns the original 202/run ID, even after activation or while the active cache
is unavailable; scoped authorization is checked again on every retry.
Whitespace, JSON object order and omitted request defaults do not change the
fingerprint; array order and supplied values do. Reusing an unexpired key for
a different request returns 409 without echoing either body. Missing keys
leave submissions independent; at expiry the key can identify a new request.
Reservation, command, audit, response and NOTIFY commit together, so validation or
persistence failures cannot leave an orphan run command or consume the key.
Expired rows stop deduplicating immediately; general physical retention
cleanup remains a maintenance task.

`POST /api/v1/schedules/{id}/run-now` accepts no body or `{}`. Scoped Operators
and Admins can manually launch code- or UI-origin instances, including paused
ones, without resuming them or changing their next-fire cursor. The command
captures the instance's effective variables and existing permanent series;
leader recovery persists it with the Manual trigger. Removed instances return
404 for new requests. Keyed retries return the original run even after edits
or removal, subject to fresh scope authorization; reusing that route's key for
a different instance returns 409.

An omitted schedule pin selects the active version. Pins accept an exact UUID
or SemVer, comparisons (`=`, `>`, `>=`, `<`, `<=`) joined by spaces (AND),
`||` alternatives, and `*`, `major.*` or `major.minor.*` wildcards. Ranges select
the highest matching published version from the same undeleted workflow,
excluding prereleases unless a comparison explicitly names a prerelease with
the same major/minor/patch. Numeric components have no Int32/Int64 ceiling;
build metadata does not affect precedence. Pins are limited to 256 characters;
caret/tilde/npm shorthand is not supported. Shared WFV026 validation reports
invalid pins before code schedules are registered. Invalid effective values
or unavailable pins fail without creating a command; run-now does not create
a scheduled-fire row or apply recurring overlap decisions.

User-defined schedule management is UI-only: `POST /api/v1/schedules`,
`PUT /api/v1/schedules/{id}`, `POST .../{id}/pause`, `POST .../{id}/resume`,
`DELETE .../{id}`, and `POST /api/v1/schedules/preview`. Production must configure
`AzureAd:SpaClientId` (`AzureAd__SpaClientId`); absence disables these routes with
403 while leaving other API authentication unchanged. Only authenticated
delegated user tokens with `scp`, a nonempty actor UUID and the exact SPA `azp`
pass this gate; app-only tokens, other clients and `appid` fallback do not,
including global Admins. Team/type Operator or Admin authorization still applies.
Explicit `--dev` authentication supplies a local delegated identity only on these
UI routes, preserving existing local Registrar/Agent behavior elsewhere.

The request contains `workflowId`, `name`, `timezone`, exactly one `cron` or
whole-second `interval`, optional `versionPin`, `overlap`, `catchUp`, and variable
overrides. Validation uses the selected version's schema and shared schedule
rules. Effective defaults are persisted. Preview returns the next five UTC
instants with the same IANA/DST calculator without writing anything. Bodies are
strict JSON capped at 1 MiB; names allow at most 128 Unicode code points.
`GET /api/v1/schedules/{id}` remains a public authenticated Viewer read and
returns the current strong ETag. Reads and `run-now` have no SPA-client restriction.

Schedule reads include `origin`, nullable `sourcePath` and nullable `gitCommit`.
Code provenance comes from that instance's exact applied schedule configuration,
not the active workflow version or the latest registration attempt. A
schedule-only file edit can update its path/commit without changing the workflow
version or series; an identical-content retry preserves the original provenance.
User-origin and legacy code rows without retained provenance return explicit nulls.
No file content or resolved secrets are included. All code edit/delete/pause/resume
routes reject authorized callers with 409 before requiring a UI edit ETag.

Every edit/pause/resume/delete requires exactly one strong numeric `If-Match`
ETag: missing is 428, malformed/weak/wildcard/multiple is 400, stale is 412.
Create returns 201 with `Location` and ETag; edit returns 200, pause/resume 202,
delete 204. Code-origin mutations return 409. A schedule cannot move workflows;
its permanent series survives deletion. Unchanged expressions keep their
intended fire cursor, expression edits recompute it, pause clears it, and resume
starts at the next occurrence after now instead of catching up an intentional
pause. Semantic no-ops retain ETag/cursor/audit state. Mutations, sanitized audit
attribution and scheduler notifications commit atomically.

`GET /api/v1/audit` and `/admin/audit` expose the retained audit trail to the
existing global `Admin` role; a team-scoped Admin or Viewer cannot read it.
Exact `actor`, `targetKind` and `targetId` filters combine with AND.
`from` is inclusive and `to` exclusive, using RFC3339 timestamps with an explicit
offset and at most six fractional digits. The page's date controls use UTC.
Records sort newest first by timestamp and ID; `limit` is 1-100 (default 50),
and `nextCursor` must retain the original filters and limit. Audit IDs are
decimal strings to preserve Int64 precision in browsers. Responses are
`no-store`; current authorization is checked on every read and failed refreshes
hide previously cached records and close any open summary. Each compact row
shows committed actor, action, target and browser-local time; **View summary**
opens the exact recorded JSON in a scrollable, keyboard-accessible modal,
including existing before/after metadata. Escape or **Close summary** returns
focus to its row button. These are administrative/control audit records, not
workflow execution results or task output; no workflow variables or storage
capabilities are joined. This read surface does
not imply the separate all-mutating-endpoint audit-coverage task is complete.

Public schedule reads include `GET /api/v1/schedules?workflowId=team/type/name`
and the existing detail, series-fire and series-history endpoints. The list
requires current Viewer access to that exact workflow's owning type, accepts
`limit` 1-100 (default 50) and an opaque `cursor`, and returns nondeleted Code
and User schedules in ascending immutable UUID order. Keep the workflow and
limit unchanged across pages. Rows reuse the detail projection, including
origin and exact code provenance; existing secret references are never resolved.
No-store reads accept authorized app or user tokens without a SPA-client
restriction. `GET /api/v1/permissions/{scope}` is also public and retains the
same current-resource authorization checks; publishing it grants no new access.

Authenticated OpenAPI 3.1 documents use `Microsoft.AspNetCore.OpenApi`:
`/openapi/v1.json` includes public reads and `run-now`, never UI-only mutations;
`/openapi/ui.json` contains only the SPA schedule-management routes. Request
types for those routes live in the control-plane assembly, not the SDK or CLI.
Public and internal TypeScript clients are generated from those live documents;
the frontend's strict drift check rejects stale generated contracts.
Both documents explicitly pin OpenAPI 3.1 rather than inheriting a future
framework default. With the host running, use `bun run api:generate` from
`frontend` to update both clients, then `bun run api:check` to compare them
without writing files. `KARDIA_API_URL` defaults to `http://127.0.0.1:5001`;
`KARDIA_API_TOKEN` supplies a bearer token outside login-free development.
The Frontend workflow runs this check against an isolated actual control-plane
host, not a checked-in document, and fails if either generated client drifts.
The generator's process tests also prove nonzero exit on changed API/file
contracts, missing files or unavailable documents, without overwriting clients.
`PublicApiContractTests` scans every checked-in frontend API call against these
generated operations, failing on an undocumented call or an unclassified
computed path. Only the six schedule-editor operations (including preview)
may use the internal client; reads and other mutations use the public client.
Frontend CI runs this scan after the actual-host drift gate. Actual CLI
acceptance also rejects schedule-management commands with no HTTP request,
even when supplied a valid Admin token; these UI-only mutations are not CLI
features.

`dotnet run --project backend\tests\Wf.Benchmarks -c Release -- --trigger-api-bench`
starts an isolated real API/PostgreSQL benchmark: 50 warmups, then 500 fresh-key
submissions at open-loop 50 requests/second. JSON output includes HTTP latency,
dispatch lag, offered/achieved rates and durable row counts; exit is nonzero
unless every request succeeds and p95 is strictly below 100 ms. Database and
host setup stay outside measurements. `WF_TEST_POSTGRES` uses a unique disposable
schema; otherwise the benchmark creates and removes its own PostgreSQL container.

The all-mode entrypoint requires ordinary, untrimmed IL because it includes the
gRPC task-host server. File-based workflows use `#:property PublishAot=false`,
as the sample does. The standalone SDK definition, manifest and local APIs
retain their AOT-compatible implementations; the agent still publishes native.

Unit and integration tests cover authoring, real task execution and PostgreSQL
event replay. CI runs on Windows and Linux. Frontend tests will accompany the
frontend implementation.

Provision the PostgreSQL database/schema and configure
`ConnectionStrings__Workflow`, then run the explicit deployment-only migration
mode before starting the control plane:

```powershell
dotnet run --project backend\src\Wf.ControlPlane -- --migrate
```

This exits after applying embedded, ordinally ordered scripts; it starts no
server or leader. DbUp PostgreSQL 7.0.1 journals each successful script in
`schema_versions` in the connection's current schema. A schema-scoped advisory
lock serializes deployers; each script and journal entry share one transaction,
with a 60-second command/lock bound. Existing outer BEGIN/COMMIT wrappers are
removed for this runner; variable substitution is disabled for PostgreSQL dollar
quotes. Failed scripts roll back and fail the process; rerunning skips committed
scripts. Existing manually provisioned, unjournaled schemas require a reviewed
baseline/adoption procedure, not an automatic assumption that all scripts ran.

`MigrationLint` rejects DROP/RENAME/TRUNCATE and added NOT NULL constraints
without a non-null default declared in that script, ignoring comments and
quoted values while checking explicit function/DO-body DDL. It is a static
compatibility check, not a SQL sandbox or proof about dynamic SQL/default
expressions; review those and release contract changes separately, at least one
release later. Initial migration `0003` now supplies the default needed by old
writers; `0013` supplies it to existing schemas without changing existing
deadlines. Normal run creation still writes its resolved policy deadline.

The control plane authenticates with Microsoft.Identity.Web 4.16.1 by default.
Set `AzureAd__TenantId` to the tenant UUID and `AzureAd__ClientId` to the API
application UUID; optional `AzureAd__Instance` defaults to
`https://login.microsoftonline.com/`. Configure the API audience in the Entra
application (and `AzureAd__Audience` when using a custom application ID URI).
Register app roles `Admin` (users/groups or applications), `Registrar` and
`Agent` (applications only), assign them to the appropriate principals and add
the optional `idtyp` access-token claim to the API app registration. Machine
policies require `idtyp=app`, the exact app role, and no delegated `scp`.
Signed JWT issuer, audience, tenant and lifetime are validated; untrusted tokens
receive 401 and authenticated callers missing the required policy receive 403.
Fallback authorization protects new endpoints; only explicit health endpoints
and capability-protected raw upload routes are anonymous. The agent gRPC
gateway requires the `Agent` policy.

For isolated local development, set `DOTNET_ENVIRONMENT=Development` and pass
`--dev` (or `Authentication__Mode=Development`). This deliberately bypasses
tokens with one fixed local identity holding all three global roles; never
expose this mode to untrusted callers. It throws outside Development, including
Production, Staging and Ppe. Development alone does not enable the bypass.
No identity or role is taken from caller-supplied development headers. Migration
mode does not need identity configuration. Live corporate sign-in, browser
MSAL and agent token acquisition remain separate tasks; offline integration
tests use real RSA-signed tokens and the normal validators with local metadata,
not a live Entra tenant or mocked authentication handler.

Task delegates resolve by stable `node/task` keys through `TaskRegistry`; no
assembly scanning. Contexts expose `Var<T>`, AOT-safe `VarEnum<T>`, declared
`Input<T>`, bounded `Outputs`, cancellation, logging and injected report/artifact
publishers. Publishing without a configured host publisher fails explicitly.
Native AOT/trim analyzers run for the SDK and shared contracts.

The run's **Outputs** tab reads the exact selected task attempt, including
failed-attempt snapshots, during and after a run. The corresponding public
operation is `GET /api/v1/runs/{id}/outputs?taskKey=node%2Ftask&attempt=1`.
It requires fresh Viewer access to the run's owning team/type and returns
`{runId, taskKey, attempt, outputs}` with an object or `null`. Historical
attempts without recorded outputs remain unavailable; node-level aggregates
and other attempts are never substituted.

Outputs must be strict UTF-8 JSON objects, at most 65,536 serialized bytes.
Malformed, oversized or database-incompatible producer values fail the task
clearly as nonretryable `ValidationError`, without crashing the gateway or
partially committing success. Supplied resolved-secret maskers run before
durable result journaling; masked expansion still must fit the limit.
Valid outputs persist for every completed outcome; replay cannot overwrite
an attempt's history. The UI displays text-only JSON without losing large
integer precision and hides stale values after authorization/error changes.

Core abort plans preserve the first accepted who/when/reason/source record.
Hosts commit the initial abort event before cancelling attempts, drain active
work, then run eligible `always` and post stages with `ctx.Abort`. Queued aborts
skip all work; running aborts record `EndedAt` only after cleanup actually stops.
Abort targets and the original record survive snapshot/event replay.

`POST /api/v1/runs/{id}/abort` requires fresh Operator or Admin access to the
stored run's team/type, independently of the SPA client. Send
`{"reason":"Stop deployment","source":"API"}`; source defaults to API and accepts
UI/API/CLI only. The nonblank reason is required, at most 500 characters; unknown,
duplicate and malformed JSON properties fail explicitly, and bodies are capped
at 16 KiB including streamed requests. The authenticated principal UUID/display
name and server UTC acceptance time cannot be supplied by the caller.
Acceptance atomically persists one Abort command and `run.abort` audit record,
then notifies recovery only after commit; the API never writes run state.
Concurrent/repeated requests, including while Aborting, return 202 with the first
immutable `{by,byName,at,reason,source}` record without another audit row.
Terminal runs return 409 Problem Details with their current `runStatus`; unauthorized/unknown runs
are indistinguishable 403. Pending accepted triggers can be aborted before run
materialization. Row locks serialize acceptance against terminal commits, while
repeat reads avoid recovery's command-row lock; failed audit writes roll back
acceptance and notification. Responses from authorized endpoint handling are
never cached. The CLI command is documented below; browser confirmation remains
its own task.

Attempt/node/run deadlines and retry backoff use a `TimeProvider`; cleanup gets
fresh cancellation tokens and its own deadlines. A function that ignores
cancellation beyond grace causes an explicit error, not a false terminal result:
the in-process local engine cannot forcibly kill managed code. Distributed task
hosts provide process isolation; built-in command cancellation also kills and
reaps its own process tree.

`RerunPlanner.Plan(newRunId, source, pinnedNodes, fromNodes)` creates a new
Queued/sequence-zero core snapshot with the same version UUID, metadata and
cloned variables, plus parent lineage. Unsuccessful nodes and their downstream
closure reset to Pending; unaffected Succeeded/Reused body nodes and tasks
become Reused with cloned original JSON outputs and `ReusedFromRunId`. Reusable
nodes require an explicit persisted output object, including `{}` for no outputs;
missing data fails instead of silently losing it. Selected successful nodes
also invalidate downstream outputs for every dependency condition.

New runs execute lifecycle stages again; invalidating a setup stage invalidates
its implicit body dependents. A successful source with no selection means a
full rerun; explicit selection must be nonempty, unique and declared. Source
runs must be terminal and match the pinned acyclic node/task definition.
Old abort, completion timestamps and attempt states do not carry over.
The hosted **Re-run from failed** action uses
`POST /api/v1/runs/{id}/rerun`, with current Run Operator authorization,
durable parent/output lineage and idempotent acceptance.

**Clone run** on a confirmed terminal run opens its captured inputs as
pretty-printed **Variables (JSON)**. Edit values and use **Format JSON** to retain
nested objects, arrays, Boolean values and exact large-number tokens; it does not
flatten objects or turn numbers into strings. Only secret reference names appear,
never resolved secret values. Unknown keys, wrong types and invalid values are
rejected against the source's immutable pinned definition.
**Confirm clone** creates a new full run: every node starts fresh, with no reused
outputs or copied attempt/abort state. It retains the source version, metadata,
placement, parent lineage and schedule series, without editing the source,
active version or any saved schedule. History records it as a full **Rerun**.

`GET /api/v1/runs/{id}/clone` reads the same pinned effective inputs;
`POST /api/v1/runs/{id}/clone` accepts a complete `{"variables": {...}}` object
and optional placement override, up to 1 MiB. Both require current Run Operator
permission and return `Cache-Control: no-store`. Accepted requests return 202 and
the new run ID. `Idempotency-Key` preserves one acceptance for 24 hours, scoped to
actor, source and canonical request. Uncertain browser acceptance freezes the
exact edited inputs and key for **Retry clone**, including across closing and
reopening the dialog. Validation errors preserve editable JSON. The `run.clone`
audit entry records lineage and version, not variable values.

**Run and report sign-off.** Approval covers one completed run and its pinned
workflow version, never the workflow globally or a future run. Each report has
an independent approval; approving the run does not approve its reports, and
report approval does not approve the run. Approval does not gate execution.
Current Run Viewers may read the full sign-off/revocation history. Current Run
Operators and Admins may sign off or revoke any active approval with a required
reason. The server records authenticated actor ID, display name (object ID when
unavailable), reason and UTC time. Revocation preserves earlier approvals.

`GET /api/v1/runs/{id}/signoff` returns current state, pinned workflow/version,
and newest-first history. Add `/reports/{reportId}` immediately after the run ID
for a report's independent sign-off. `POST` to the same path signs off;
`POST .../signoff/revoke` revokes. Both accept only `{"reason":"..."}` (nonblank,
at most 2000 characters; body at most 16 KiB), require a completed run, and require
the current strong `ETag` from GET in `If-Match` (initially `"0"`).
Missing preconditions return 428; stale revisions return 412. Reload after an
uncertain response before deciding whether to submit another change; stale
retries cannot silently approve or revoke a newer revision. Current state and
events commit atomically with `run.signoff`, `run.signoff.revoke`,
`report.signoff`, or `report.signoff.revoke` audit entries. History is append-only.
GET accepts `limit=1..50` (default 20) and exclusive `before=<revision>`;
`nextBefore` continues older history while `state` always describes the current
head. Revisions are decimal strings, preserving full 64-bit precision.
Responses use `Cache-Control: no-store`; every request rechecks current scope.
The run page's **Workflow sign-off** panel and each report's **Report sign-off**
panel show current status, reviewer identity, local time, reason, and expandable
**Sign-off history**, with **Load older sign-offs** for the complete record.
Operators confirm **Sign off workflow**, **Sign off report**, or **Revoke sign-off**
with a required reason. Panels refresh every 30 seconds or on **Refresh sign-off**.
After a failed or uncertain write, no automatic retry occurs: the reason stays
visible and read-only until the dialog is closed; reopening reads current state
before allowing another change.
Runs, workflow/series history, service-history cards and report tabs include
current sign-off summaries. Run summaries retain the latest actor, time and
reason, including revocation, and link directly to the run's sign-off history.
Report badges remain independent from workflow approval. Summary reads batch
only already-authorized page targets; they do not issue one request per row.
Schedule events without a run show sign-off as not applicable.

Dry-run executes the same core using explicit task mocks, without invoking any
original task delegate or external service:

```powershell
dotnet run --file backend\samples\workflows\demo\scenario-a.cs -- --dry-run backend\samples\workflows\demo\scenario-a.mocks.json --var env=ppe
```

Dry-run stdout contains one source-generated JSON event log, including snapshot, outputs,
attempts and final exit code. Mocks support `Succeed` with outputs, `Fail`,
`Timeout`, `Throw`, and `FailThenSucceed` with `failuresBeforeSuccess`. Each task
needs its own mock unless the spec explicitly supplies `default`; missing mocks,
unknown keys and unknown JSON properties are errors, not automatic success.
Spec `variables` override workflow defaults; command-line `--var` values win.
Timeouts/backoff advance virtual time with deterministic midpoint jitter and no
real delays. The testing library publishes file apps once through `ScriptBuildCache`:

```csharp
var workflow = await WorkflowUnderTest.BuildAsync("workflow.cs",
    additionalInputs: localDependencyDirectories, cancellationToken: cancellationToken);
var mocks = new MockSpec().FailNThenSucceed("build/compile", 2);
var log = await workflow.RunAsync(mocks, cancellationToken);
```

`WorkflowUnderTest` launches the actual published assembly out of process for
each run, avoiding file-app entry-point limitations. `MockSpec` is immutable,
requires explicit task mocks or an explicit default, and supports outputs,
variables, succeed/fail/timeout/throw and fail-N-then-succeed. Workflow retry
budgets still apply; timeouts/backoff use virtual time. Valid failing workflow
logs return normally; invalid host responses and exit/log mismatches throw
`WorkflowRunException` with stdout/stderr. Cancellation kills/reaps the owned
process tree and removes invocation files. Workflows are trusted code, not a
sandbox; use cancellation to bound real host lifetime.
`Wf.Sdk.Testing.Assertions` supplies framework-independent `DryRunLog` extensions
for run/node/task status, skipped work, completion-before-start order, persisted
retry attempts, lifecycle stages and structural/type-sensitive JSON outputs.
Assertions return the original log for chaining; mismatches or unknown keys
throw `RunAssertionException`, rather than silently passing.

Code-defined schedule files use the published editor schema at
`backend/src/Wf.Contracts/Schemas/schedules.schema.json`; see
`backend/samples/workflows/demo/scenario-a.schedules.json`. `ScheduleFileSchema.Export()`
generates it from the same strict, source-generated contracts used by the server.
`defaults` groups variable values by category; each instance supplies a stable
`id`, exactly one `cron` or positive whole-second constant-format `interval`
(at most 2,147,483,647 seconds), and an IANA `timezone`.
Instance `variables` override defaults; optional `versionPin`, `overlap`
(`Skip`, `Queue`, `Concurrent`) and `catchUp` (`None`, `Latest`) control execution.
Default policies are `Skip` and `Latest`.
`ScheduleFileValidator.Validate(utf8, variableSchemas)` resolves workflow defaults,
then categorised file defaults, then each instance's overrides. It returns
immutable effective JSON values and shared `ScheduleExpression` objects, or
file/path/message/hint errors without partial instances. Unknown or invalid
values, required-but-missing variables and duplicate defaults across categories
fail even when another override would hide them. `Validator` and the testing
facade automatically check optional `schedules.json`; registration uses the same
validation and retains the exact UTF-8 file bytes.

Migration `0007_schedules.sql` adds durable code/UI schedule rows, stable code
keys, expression/policy checks, optimistic `row_version` and a partial due-fire
index excluding paused/deleted rows. Storage follows the design's positive
whole-second `interval_s`; subsecond interval precision is not represented.
Migration `0008_schedule_config_versions.sql` adds independent content-addressed
schedule-file provenance (source path, Git SHA, SHA-256 and attribution).
Composite foreign keys prohibit cross-workflow links; UI schedules cannot link
code configs, and old code rows remain valid during upgrade.
Migration `0018_schedule_file_application.sql` retains file content with a
database-checked SHA-256, adds the workflow's same-workflow active-config pointer,
and creates/backfills one permanent `run_series` per schedule. Atomic code-file
application is part of registration; UI mutations and leader execution remain
their own tasks.

`ScheduleCompatibilityRule.Validate(scheduleUtf8, targetApi)` applies those same
variable checks to a target `ApiBaseline`. Removed/renamed nodes do not affect
schedule compatibility; removed variables still referenced by defaults or
overrides do, even after a valid MAJOR bump. Version-pin resolution chooses the
target API before this check; the rule does not silently select another version.

`TeamRegistryRules` validates strict `team.json` contracts: owning team, type
name/description, metadata keys with required/allowed values, and optional
free-form metadata. Duplicate/normalization/edit-distance conflicts and the
trusted per-team type quota (default 10) fail with WFV015-WFV020 diagnostics.
`Validator` checks the nearest optional `team.json` in the workflow directory or
its first two parents, matching `teams/<team>/<type>/<workflow>`. It checks the
manifest's type and metadata against that registry; invalid registry and schedule
errors accumulate rather than hiding one another. An absent registry remains
allowed for standalone workflows; registration supplies its authoritative scope.

The editor contract lives at
`backend/src/Wf.Validation/Schemas/team.schema.json`, with a complete sample at
`backend/samples/workflows/demo/teams/demo/team.json`. Tests pin schema properties and
required fields to the source-generated runtime contracts. The schema rejects
missing type names/owning teams; the shared validator additionally checks that
owning teams match, metadata constraints, quotas and near-duplicate names.
Optional `roleBindings` on the team and each type map
`Viewer`/`Operator`/`Author`/`Admin` to non-empty Entra `groupId` UUIDs. Omitted
maps are empty; duplicate role/group pairs within one scope fail, while one
group may hold different roles or appear at both scopes. Sample UUIDs are
placeholders, not deployed groups. Registration persists the group bindings in
the same transaction; scope-aware reads and mutation policies enforce the current
bindings through the shared authorization handler.

Migration `0015_team_type_registry.sql` adds `teams` and `workflow_types`.
`RegistryWriter.UpsertAsync(connection, transaction, registry)` uses the same
shared rules and trusted constructor quota (default 10), serializes updates per
team, and replaces the complete team/type snapshot in the caller's registration
transaction. It never commits: commit with all other registration work, or
roll back on any error. JSON-equivalent snapshots with unchanged bindings return
false without updating row versions or notifying; changed snapshots or repaired
bindings issue transaction-scoped
`NOTIFY registry`. Removed types are deleted, not silently retained; future
workflow foreign keys must prevent removal of in-use types.
IDs preserve exact declared names as `team/type`; shared validation and editor
schema now bound both segments to 128 Unicode code points and reject path
separators/control characters, avoiding ambiguous IDs and oversized index keys.
The authenticated catalogue exposes `GET /api/v1/teams`,
`GET /api/v1/teams/{team}/types` and `GET /api/v1/workflows`.
All three use current stored team/type grants through the same scope handler as
mutations and live subscriptions; Author and Operator independently inherit
Viewer. A type-only grant exposes its team container and permitted types, never
sibling types, role/group maps or team-wide presentation containing denied type
names. A team grant exposes its complete presentation; global Admin retains the
full registered team snapshot for administration.

Type rows include metadata schemas and counts of each workflow's latest durable
run status (`NeverRun` for no history). Workflow rows include active/latest
version identities, active-version metadata and a bounded latest-run summary,
not variables, manifests or blob capabilities. Deleted workflows are excluded.
Lists are uncached and scope-filtered again on every request, so registration
revocation needs no token renewal. Ungranted teams return 404 or an empty list.

Workflow lists accept exact `team` and `type` names (type requires team), `limit`
from 1 to 100 (default 50), and the previous page's opaque `cursor`. Paging uses
ordinal workflow identity, not offsets; latest runs use creation time then UUID
for deterministic ties. Invalid, duplicate and unsupported filters fail with
400 Problem Details instead of being silently ignored. Full run search/filter
and catalogue UI/live views remain their dependent tasks.

Migration `0016_role_bindings.sql` adds global/team/type scopes (0/1/2), shared
Viewer/Operator/Author/Admin values (0/1/2/3) and group/user principals (0/1).
`RoleBindingSync` reconciles validated team/type group maps inside
`RegistryWriter`'s transaction, deleting removed groups and removed-type maps.
Global grants, explicit user grants and unrelated teams are preserved.
Even an unchanged JSON snapshot reconciles missing bindings after an upgrade;
unchanged binding rows retain their UUIDs and row versions. Registry and role
changes become visible together only at commit, with no permission cache to
leave stale entries behind.

`AddTeamAuthorization` registers `Team.Viewer`/`Operator`/`Author`/`Admin`
policies. HTTP routes use `RequireTeamRole(role, resourceKind, routeParameter)`;
non-HTTP callers pass `TeamResource` to `IAuthorizationService`. Resources are
resolved from stored teams/types or the persisted run/series workflow identity,
never a caller's team metadata. Ambiguous or unresolved series fail closed.
Team grants include their types; type grants never grant team-wide visibility.
Author and Operator each include Viewer, but neither includes the other.
Scoped Admin includes every role only within its scope; only the exact Entra
app role `Admin` bypasses scoped grants. Database global bindings do not replace
that app role. Delegated users match `oid` user grants and `groups` group grants;
app-only, Agent/Registrar, malformed and group-overage claims cannot acquire
user-scoped roles. Group overage requires a fresh complete token; no Graph
expansion or permission cache is used.

Scoped HTTP denials return 403 `application/problem+json` with a sanitized
`detail` and `reason: insufficient_scope_role`; missing authentication remains
401. The initial `/hubs/live/v1` hub authorizes every `Subscribe(scope,id)` for
`team`, `type`, `run`, `series` and global-Admin-only `machines` (`id="*"`).
Subscribe returns null on success, otherwise a Problem Details object with
`status` 403 (or 400 for unsupported input): an upgraded SignalR invocation
cannot change its HTTP status. Denied subscriptions never join groups; a
denied re-subscribe removes earlier membership. UUID group IDs are canonical.
JWT query tokens are accepted only under the exact hub path segment, never on
REST routes or in preference to an Authorization header; connections close at
token expiration. MessagePack, event fan-out/coalescing, proactive revocation
of existing subscriptions, logs subscriptions and the separate browser-facing
hosting topology remain live/hosting tasks, not implied by this authorization
foundation.

Optional team `presentation` config supplies ordered `typeOrder`, `groupingKeys`
and metadata `listColumns`, plus metadata-keyed `defaultFilters` and display
`labels`; the demo `team.json` includes an example. Type names and metadata keys
are exact/ordinal. Partial type order is allowed without mutating registry
declaration order. Metadata references must exist in at least one declared
type's schema, even when free-form workflow metadata is allowed. A default
filter value must satisfy at least one declaring type's allowed values (or an
unconstrained declaration), allowing defaults to select a subset of types.
Unknown/repeated references, invalid filter values and empty labels return
WFV024 with precise paths but no echoed values. Missing/null presentation and
empty config preserve existing defaults. Shared validation and editor schema
cover the contract; applying it in team views remains a separate UI task.

`TypeNameSimilarity` supplies the shared registry normalization and bounded
Levenshtein helper: invariant lowercase letters/digits only, then remove one
trailing plural `s` when more than one character remains. `WithinTwoEdits`
compares already-normalized strings with ordinary insertion/deletion/substitution
distance <= 2; transposition counts as two edits. It checks a five-cell band per
row, observes cancellation and handles empty-string boundaries without
truncating long names. This is literal normalization, not linguistic stemming.
Within one team, shared offline and registration validation reject names at
distance <= 2 with WFV016, naming the JSON-escaped conflicting type and its
declaration path. The database additionally enforces `(team_id, normalized_name)`
uniqueness. Identical names in different teams remain valid; catalogue links
display `team / type` and preserve each team's exact route and permissions.

The hosted control plane acquires the `0009_leases.sql` engine lease, renews
every 3 seconds and expires after 10 seconds of database time. Each acquisition
has a monotonically increasing epoch. All hosted registry/run/dispatch/result
and group-commit transactions fence their captured epoch before persistence;
PostgreSQL bounds fenced transactions to the lesser of 1 second or remaining
lease lifetime, so a paused client cannot hold takeover indefinitely. A database
authority failure disables writes until reacquisition.
Standby acquisition retries every second; successful leader renewals remain
every three seconds. Graceful host shutdown stops producers, closes and drains
the group appender while renewal remains active, then stops renewal and releases
the lease. Hosted services stop sequentially in reverse registration order;
concurrent stop is explicitly disabled. A stopped lease manager cannot acquire
again. Real two-host/PostgreSQL handover measured <= 992.7 ms to the next leader
and <= 1,644.3 ms through recovery readiness across three stop phases, with all
16 buffered runs preserved and completed per phase. These healthy-storage
measurements do not override the existing bounded appender drain failure/timeout
behavior; hard-kill takeover still waits for lease expiry. Windows tests use
`IHost.StopAsync`, not an actual Unix SIGTERM signal.
`/healthz/leader` returns 503 on standby or until recovery, active versions and
the scheduler cursor snapshot are ready; gRPC sends `NotLeader` with the known
leader hint and uses the actual epoch on commands/ACKs. Configure `Engine:Port`
(default 5000) and `Engine:PublicEndpoint` for multiple instances. Standalone
persistence test/benchmark modules may omit the lease manager; hosted DI always
supplies it.
Self-hosted agents configure ordered `Agent:Engines` origins. A standby's
`NotLeader.leader_hint` bypasses the next endpoint and reconnects directly after
100 ms only when it is a configured HTTP(S) origin with no credentials, path,
query or fragment. Missing, malformed or unconfigured hints never expand that
allowlist; normal disconnects retry the configured list after one second.
Each connection sends a fresh Hello while retaining pending journal sequences
and the highest observed leader epoch across redirects and takeover.

`0010_commands.sql` adds the durable trigger/abort inbox. `CommandInbox` accepts
preallocated UUIDv7 run IDs and preserves the first command of each kind per run.
`RunRecovery` drains pending commands transactionally on acquisition, restores
active queued/running/aborting snapshots plus their committed event tails, and
rebuilds `DeadlineTracker` entries from absolute run/node/attempt deadlines,
retry timestamps and persisted cancellation/kill phases. An outage never resets
the retry time or cancellation grace. Command effects and `processed_at` commit
together; failed or fenced recovery never publishes a ready cache. New commands
and directly committed scheduled runs are discovered while leadership remains
current, and reacquisition rebuilds the bootstrap state. `RecoveredRuns` supplies
this state and due-timer proposals to
the hosted orchestration actor; dispatch/cancel/kill effects are not executed by
the recovery loader. Full hosted workflow/agent coordination remains separate.
The event appender buffers transient database failures within its bounded queue
and retries after 100 ms up to 1 s, retaining the captured lease epoch. An
expired/superseded writer is rejected, never silently transferred to a new epoch.
A lost COMMIT acknowledgement is safe to retry: only an exact, full persisted
event replay succeeds; conflicting or partially replayed batches roll back.
Normal disposal drains; an outage lasting beyond the 5 s shutdown drain budget
fails remaining requests explicitly rather than hanging or acknowledging them.
The failover fixture kills a real leader process while a shared-core no-op run
is active; a second process acquires and completes it within 30 s. Disposable
PostgreSQL stop/start and dropped wire-level COMMIT acknowledgements are tested
separately. Shared external PostgreSQL instances are never stopped by tests.

`AgentLossSweeper` shares the core liveness policy: 15 s without a heartbeat
excludes an agent from placement, but its attempts remain reclaimable until
30 s after that original heartbeat. Engine recovery does not add another grace
period. At expiry, a fenced transaction classifies dispatched/running attempts
as `AgentLost`, records the stop time/reason and removes undelivered dispatches.
On reconnect, the gateway reconciles the full run/task/attempt identities in
`Hello.inflight` before acknowledging Hello; omitted attempts are lost immediately,
reported attempts remain reclaimable. Superseded sessions cannot reconcile.
Attempt classification and dispatch removal roll back together.

`0011_attempt_replacement.sql` retains the original dispatch, placement request
and resolved execution policy on each attempt. `Dispatcher.DispatchAsync` has
an overload accepting that policy; the original overload preserves default
timeouts and the placement request's `AutoRetry` setting. The hosted
`AgentLossHandler` reconstructs lost work from PostgreSQL, re-places on another
eligible agent with at most two platform retries, and never consumes author
retries. `NoAutoRetry`, exhausted budgets and elapsed run/node deadlines fail
the core task with an explicit stored reason. Missing legacy recovery inputs
fail safely rather than inventing a dispatch. No eligible agent leaves work
pending without spending budget. Retry events, attempt, dispatch outbox and
processing marker commit atomically; a failed transaction changes no placement
cache. A fresh attempt timeout is bounded by the original run/node deadlines.

Tests kill an actual agent during an SDK delegate, advance the virtual liveness
clock through the 30 s reclaim window, then execute the replacement successfully
on a second actual agent. This verifies recovery, not a strict wall-clock
heartbeat-to-completion limit. The plan's simultaneous 30 s reclaim window and
30 s total replacement/completion target conflict; timing sign-off remains
blocked pending clarification, without shortening the approved reclaim window.

Placement is authored at workflow, node or task level with `RunsOn` and
`Affinity`. The innermost selector replaces the whole outer selector; affinity
inherits independently. `RunsOn()` explicitly clears constraints; `Affinity.None`
clears an inherited pin. Stages support the same settings.

```csharp
w.Variables(v => v.StringList("machines", ["worker-a", "worker-b"]))
 .RunsOn(v => Machines.All(Os.Linux, Machines.Named(v["machines"]), Machines.Label("dotnet")))
 .Affinity(Affinity.Node)
 .Node("build", n => n.Task("compile", Compile,
     t => t.RunsOn(Machines.Pool("ci")).Affinity(Affinity.None)));
```

`Os.Windows/Linux`, `Machines.Pool`, `Machines.Label` (presence or exact value)
and `Machines.Named` compose with AND; names within one `Named` are alternatives.
`Machines.Ids(uuid1, uuid2)` declares immutable agent IDs instead of names;
`Machines.Count(2)` declares the requested number of machines. Compose them
with OS, pool or label constraints in the same `RunsOn` call. IDs must be
non-empty UUIDs; names and IDs cannot be mixed, counts must be positive and
cannot exceed an explicit ID set. Repeated IDs are deduplicated. A label-based
selector can declare a count without enumerating IDs. The whole selector,
including IDs/count, follows the same innermost-wins rule and appears in the
manifest and historical Task Plan API/UI. Registration checks effective
selectors against the current registered machine inventory; symbolic values
remain deferred until variables bind. Manual triggers, schedule run-now and
scheduled fires recheck that inventory with the bound variables. WFV027
diagnostics identify the task and missing machine ID or label key without
echoing selector values. Unknown IDs and too few configured matches fail before
creating commands/runs; failed scheduled fires also leave claims/cursors unchanged.
Known machines that are unhealthy, full or temporarily unavailable remain valid
configuration and wait for runtime placement rather than failing validation.
Omitted count means all explicit IDs, or one match for other selectors.
Distributed placement intersects explicit IDs with the other constraints.
ID/count selectors choose an exact-size candidate cohort from healthy,
non-draining, matching machines not reserved for another run/node; existing
affinity takes precedence, then stable UUID ordering. Fewer matching machines
leave the task waiting. Busy slots or insufficient RAM wait inside that cohort
rather than spilling onto extra machines. Warm-host/load/round-robin preference
still applies within it. Membership can change as machine health, labels or
reservation ownership changes; this is not an exclusive or durable reservation,
nor task fan-out. Exclusive reservations use the separate manager below.
Legacy name/label/OS selectors without IDs/count keep their existing placement.
Selector callbacks build symbolic metadata once. Describing never reads run
variables or invokes task delegates. Effective task selectors resolve from bound
string/enum variables at run start; machine names also accept string lists.
Missing/empty/secret/wrong-type values fail before execution. Described templates
stay unchanged between runs. Affinity supports `None`, `Node`, `Run` and
`SameMachineAs("node")`; unknown/self/deadlocking references are rejected.
In-process local execution binds selectors but does not create or dispatch to
named agents; machine matching belongs to distributed placement.

`Wf.Engine.Core.Placement.PlacementEngine` filters health, drain, selectors,
reservations, slots and RAM, then prefers warm hosts, lower slot utilization and
stable round-robin ties. `Propose` changes nothing; `Apply` follows the durable
dispatch event and rejects stale/foreign decisions. Capacity supplied by the
dispatcher must include committed dispatches not yet reflected in heartbeats.
Warm-package sets describe the bounded host pool, not every downloaded package.
Node references use the first committed placement; platform re-placement moves
affected pins without spending an author's retries, while `NoAutoRetry` blocks
platform placement. Snapshots restore pins and rotation; a reconstructed dispatcher
replays persisted attempt placements in dispatch-event sequence before its first
new decision for an existing run. This preserves node/run/SameMachineAs pins,
including moved pins, without rechecking historical capacity. Missing or mismatched
recovery identities fail explicitly before another dispatch; new runs do not read
placement history. `ForgetRun` releases terminal-run state. No eligible capacity leaves a task `Ready` with a durable,
live `WaitingForMachine` reason. The original queue time survives retries of the
planner and recovery; at ten minutes, placement fails with category `Placement`
and the task's hard/soft failure policy applies.

Manual trigger and re-run accept `placement: { singleMachine: true, machine:
"optional-exact-name" }`. This does not edit the workflow. Trigger validation
requires one healthy, non-draining, unreserved machine satisfying every effective
task selector and node reservation; errors identify conflicting nodes, including
reservations for more than one machine. UI pickers use authenticated
`POST /api/v1/runs/placement-options` or
`POST /api/v1/runs/{id}/placement-options` with the same version and effective
variables; acceptance and dispatch recheck eligibility. Full capacity queues
tasks rather than spreading them. `runs.placement`, snapshots and dispatch events
preserve the override and selected worker across recovery, and the run shows its
override and current waits. An unnamed lost worker moves the run cohort together
only after old active attempts are classified lost/drained; named-worker loss
fails `Placement` instead of silently using another worker. Re-run without
`placement` keeps the source override; `singleMachine: false` explicitly removes
it. Existing authorization remains unchanged; no login/logout flow is added.

The run's **Logs** tab selects individual task attempts, including earlier
retries. Masked stdout, stderr and context records load through the public
Viewer-authorized indexed range API; the virtualized viewport mounts at most
36 rows and fetches 400-record ranges, never an entire log. Home/End, explicit
archive refresh, pause/resume and follow-tail controls remain keyboard accessible.
Committed run transitions and reconnect resync refresh the attempt list without
timer polling. Authorization failures hide cached log content.

Live logs share the tab's existing MessagePack WebSocket. `MirrorLogs` acquires
an expiring, attempt-specific interest; hidden, paused and unmounted views release
it. Agents mirror only durably accepted, already-masked records, batching at
250 ms with 256-record/512 KiB limits and explicit gap recovery. Live bytes never
enter PostgreSQL: the engine validates current agent session, assigned attempt
and physical reservation epoch, uploads a private ephemeral blob, then commits
only its reference notification for API instances. Blob upload failures request
archive resynchronization without interrupting durable task-result transport.
Expired interests stop mirroring; disconnects and bounded queue overruns request
resynchronization. Ephemeral blobs are retired after 30 seconds; cleanup cannot
delete archived indexes or chunks and logs retry failures explicitly.

The Chrome acceptance fixture reads one million actual archived records through
the signed API and PostgreSQL host, requires first rendering below one second,
and analyzes actual page `DrawFrame` trace events during continuous scrolling
for at least 60 frames/s. It also checks live delivery within one second,
attempt isolation, bounded mounted rows and accessibility. CI retains the raw
trace and measurements; frame-rate uncapping measures rendering capacity rather
than the machine's display refresh rate. Existing range/search/download contracts
remain unchanged.

The run's **System Events** tab and `/runs/<run-id>/events` page show durable
transitions in committed sequence order, including every recorded attempt,
dispatch/start/outcome, retries, waiting and remote-operation phases. Task logs,
individual remote polls, variables, outputs, error text, remote URLs and
operation IDs are not projected. Each dispatch records the actual selected agent
and fixed placement reason atomically with its outbox entry. **Planned agent**
means the pre-decision affinity/run pin, which can differ after agent loss; it is
not a prediction or a copy of the selected agent. An unpinned decision is explicit,
and missing legacy attribution stays unrecorded. Task Plan links use the run's
immutable version UUID, not the current active version.

`GET /api/v1/runs/{id}/system-events` requires fresh Run Viewer authorization and
returns no-store pages with exact `node`, full `task=node/task` and `attempt`
filters. `limit` defaults to 100 and is capped at 200. `nextCursor` binds the run,
filters, limit and committed watermark, so later commits cannot shift historical
pages; restart without a cursor to include them. Sequence/watermark strings retain
all Int64 digits. The UI reads ten events per page and shares the existing
ref-counted live connection; pushed deltas and resync invalidate loaded pages,
including changes arriving during a read. No timer polling is introduced, and
failed authorization/refresh hides stale results. The separate strict
`/events?afterSeq=...` live-resync contract remains unchanged.

Checkpoint ingestion uses a journaled `CheckpointSaved` pointer, never JSON state
in the agent result journal. The engine verifies the immutable run/task/sequence
blob, SHA-256 and JSON (at most 1 MiB), then commits `task_checkpoints` before its
cumulative acknowledgement. The logical run/task keeps only its highest sequence;
exact replay is idempotent, conflicting replay fails, and new saves must be at
least five seconds apart. Superseded attempts and stale physical reservation
fences cannot replace the pointer. A reconnected current session may replay its
pending record. Timestamps use PostgreSQL microsecond precision. Retention removes
the pointer before attempt rows and deletes every checkpoint blob under the exact
run prefix, including older uploads.

`await ctx.Checkpoint.SaveAsync(state, ctx.CancellationToken)` serializes supported
primitive/JSON values; custom DTOs use the overload taking source-generated
`JsonTypeInfo<T>`. The 1 MiB ceiling counts serialized UTF-8 JSON bytes, not
characters. Concurrent saves are serialized and a second save waits until five
seconds after the previous save timestamp. Cancellation interrupts that wait or
the acknowledgement wait without deleting an already-journaled pointer. Failed
or uncertain saves consume their sequence so a later save cannot conflict with
an earlier commit. Save succeeds only after the blob upload, durable agent journal,
engine index commit, engine ACK, and private host ACK; storage/transport failures
surface to the task rather than succeeding locally.
Checkpoint upload transport failures, timeouts, HTTP 5xx, 403 (including a
superseded session), 408 and 429 are `PlatformError`, subject to the existing
bounded platform retry budget and `NoAutoRetry`; they are not author task
failures or unlimited retries. Other rejected HTTP responses remain nonretryable.
Caller/attempt cancellation is never converted into a retryable storage error.

On a replacement attempt, the engine verifies the latest earlier-attempt blob
and embeds its JSON in the dispatch. `ctx.Checkpoint.TryLoad<T>()` reads that
in-memory state; custom DTOs pass their generated type information.
`ctx.Attempt.Number` identifies the attempt and `ctx.Attempt.ResumedFrom` is the
checkpoint's saved UTC time, or null when restarting without a checkpoint.
Successful saves also update the store's locally loaded state. Missing or corrupt
indexed blobs fail dispatch rather than silently restarting from scratch.
Contexts without a configured durable publisher cannot save. SDK checkpoint saves
reject resolved secret values and their base64 representations in decoded JSON
keys, strings (including escaped nested JSON), arrays and scalars before upload or
sequence allocation. Rejection is a nonretryable `ValidationError` with a generic
message, never the secret value. This is literal leak prevention, not arbitrary
encoding or encryption detection.

`Wf.Sdk.Testing.MockSpec.WithCheckpoint("node/task",
new CheckpointMock(stateJson, savedAt, sequence: 1, attempt: 2))` explicitly executes
that named real task delegate with a cloned seed, `TryLoad<T>()`, and
`Attempt.ResumedFrom`. Other tasks still use mocks; checkpoint execution cannot be
the default mock or supply synthetic outputs. This resume-branch test mode uses
virtual workflow deadlines and is intended for immediate restore-path assertions,
not arbitrary asynchronous work. Saves remain unsupported: the dry-run has no
durable publisher and never claims to upload or acknowledge state.

The run's **Timeline** tab includes **Resumed from checkpoint (UTC)** for each
attempt, using its exact original seed time and a semantic `<time>` element.
Non-resumed and historical attempts without attribution show **Not recorded**;
no timestamp is guessed from the task's latest checkpoint. Migration 0044 adds a
nullable attempt column without rewriting historical rows. The gateway records
the verified seed's saved time under the existing leadership, assigned-session
and physical-reservation fences before delivering that dispatch. Exact replays
retain it; a conflicting timestamp fails explicitly. Later saves, retries and
gateway/API reconstruction cannot replace the earlier attempt's attribution.
The existing fresh-Viewer, no-store timeline API exposes only nullable
`resumedFrom`, not checkpoint contents, blob keys or capabilities.

The internal blob-only `PUT /api/v1/checkpoint-uploads` requires a purpose-scoped
header capability for the exact attempt, assigned agent session and reservation
epoch. It cannot commit the logical checkpoint pointer or accept user tokens,
arbitrary object paths or redirect targets. Existing HMAC configuration and
agent transport are reused; no login/logout flow or public read API is added.
HA gateways resolve upload grants against their configured controller origin,
not a client's Host header or the agent's first configured engine. Blob reads and
writes run **outside** the short lease-fenced database transactions. Assignment,
session, reservation and captured lease are checked again after storage I/O
before upload success or checkpoint-pointer commit; a slow blob request cannot
hold allocation/assignment locks or extend the one-second fenced transaction.
Private host readiness protocol 7 includes checkpoint and progress streams with
explicit ACKs (engine durability for checkpoints; agent-journal durability for
progress).
Dispatch receive envelopes allow 4 MiB for the existing inputs plus a maximum-size
checkpoint and protocol metadata; checkpoint JSON and outbound result limits
remain 1 MiB.

`Wf.ControlPlane.Engine.Reservations.ReservationManager.ReserveAsync` allocates
an exact number of matching machines exclusively to an existing run/node.
PostgreSQL transactions and the dispatcher's allocation lock serialize competing
reservations and dispatches. Selection reads fresh machine metadata, health and
pending attempts; busy, stale, draining or already-reserved machines cannot be
claimed. Shortages and insert failures leave no partial reservation. Repeating
the same canonical selector/count returns the original IDs, epochs and expiry;
an active allocation cannot be resized or silently renewed.
The dispatcher confines the owner node to its durable reserved cohort, excludes
every other node/run and persists each machine's reservation epoch in the
dispatch and outbox. Expired but unreleased rows still block other work and new
owner dispatches; expiry alone never makes a machine free. The database-backed
manager lives in the control plane so `Wf.Engine.Core` remains database-free;
both reuse its pure selector filter.

`ReservationLeaseService` renews live owner leases at half their original
lifetime, preserving IDs and epochs. Explicit `RenewAsync` also retains that
lifetime; an expired, closing or ended owner's lease cannot be revived.
The hosted one-second sweep requests release on node/run completion or failure,
abort or expiry. Release atomically cancels unfinished owner tasks, removes their
dispatch outbox entries and advances each machine's fence. A machine stays
reserved while disconnected or awaiting the current session's exact
`ReservationFenced` acknowledgement; merely updating database state or receiving
an ordinary transport acknowledgement never frees it. Late results and resource
samples from expired or fenced reservations are discarded without changing
outputs, events or resource blobs. Ordinary work after release carries the
machine's advanced epoch, not a reset to epoch 1.
Physical containment is mandatory even when resource sampling is unavailable:
Windows uses named independent Job Objects, explicitly terminated and queried
until empty during cleanup; agent handle closure alone does not terminate them.
Linux uses an SDK subreaper supervisor and pidfds to terminate and reap detached
descendants. Explicit shutdown requests cleanup; agent stdin EOF does not.
Persist each agent's UUID,
result-journal directory and its `host-lifetimes` records across restarts.
Uncertain same-boot crashes or unsupported containment primitives quarantine
new work and fence acknowledgements. Recover uncertain crash records through a
whole-machine reboot with that state intact, not an agent/container restart or
deleting records. Independently constructed test pools need an isolated
`containmentDirectory` or their own stable `AgentOptions`; hosted DI supplies
the production agent identity automatically.
Every maintenance pass reads durable owners under the current leader fence, so
a replacement leader adopts and renews existing reservation IDs and machine
epochs rather than allocating replacements. Pending physical fences also
survive engine crashes unchanged and never become implicit releases.
Nodes declare shortage policy with
`.WhenUnavailable(WhenUnavailable.Queue)` (the default) or
`.WhenUnavailable(WhenUnavailable.Reject)`. `ReservationManager.RequestAsync`
accepts that node policy and returns `Reserved`, `Queued` with its current
one-based position, or `Rejected`. Reject atomically cancels unfinished state
and fails the run with durable `FailureReason = "ReservationUnavailable"`;
database failures roll back events, snapshots and queue changes together.
Existing reservation release/fencing still applies to any machines that run owns.
`ReserveAsync` remains the lower-level throw-on-shortage API.

Waiting intents persist their canonical selector, exact count, original lifetime
and FIFO identity. Equivalent retries retain their place; changing a queued
request's selector, count, lifetime or policy is rejected. A hosted one-second
leader-fenced sweep grants the oldest live request automatically, including
after leader replacement, and removes aborted/ended owners. FIFO is global and
strict: an unsatisfied head blocks later requests even if a later selector fits
currently free machines; neither request policy nor `ReserveAsync` can jump it.
The lease lifetime starts when machines are granted, not while waiting, and
pending physical fences continue to block grants.

`GET /api/v1/runs/{id}/reservation-queue` returns each live waiting node's
one-based position, required count, enqueue time and one matching blocking
reservation/run. Fresh Viewer permission is required for the requested run;
both blocker IDs are returned as `null` when its run is not currently visible to that
caller. No selectors, labels, variables or capabilities are returned.
Closing reservations remain blockers until physically fenced. Ended/aborting
owners disappear from queue positions immediately, even before maintenance
removes their intent rows. An empty `items` array means no current wait.
The run page's accessible queue banner refreshes these details every two
seconds, links visible blockers and disappears after allocation. Failed reads
hide stale blocker details; 401/403 stops interval polling, with explicit retry available.
Queue reads do not consume or invent run-event sequences. Initial reservation
requests are still explicit engine calls; this does not claim an autonomous
hosted DAG execution loop.

## Blob storage

`IBlobStore.UploadAsync(content, source)` and `DownloadAsync(content, destination)`
stream with verified SHA-256 and exact length, using bounded buffers.
`BlobContent(kind, sha256, sizeBytes)` generates content-addressed keys; callers
cannot supply filesystem paths. Both stores default to a 5,000,000,000-byte
limit. Publication is atomic/no-overwrite; identical concurrent uploads are
idempotent, while corrupt existing content fails. Failed downloads may have
written untrusted partial data: discard it or abort an already-started HTTP
response rather than reporting success.

`FileSystemBlobStore` requires a service-private root and an external stable
HMAC key of at least 32 bytes. `CreateUploadGrant` returns a bearer token, not an
HTTP URL; `UploadWithGrantAsync` verifies the signed content/size/expiry and
streams to atomic publication. Grants last at most 15 minutes and deliberately
permit identical immutable-content retries until expiry. Never log grants or
keys. Reparse paths are rejected; filesystem ACLs must exclude untrusted writers.

`AzureBlobStore` uses Azure.Storage.Blobs 12.30.1 and a configured
`BlobServiceClient` with production Entra credentials or emulator SharedKey.
Provision a private container externally. `CreateUploadGrantAsync` issues a
short-lived, create-only SAS for a random staging object; expose only its URL,
maximum bytes and expiry, retaining the grant descriptor as trusted server state
on the same singleton. `CompleteUploadAsync` verifies actual bytes (not supplied
SHA metadata) and a pinned ETag before final publication. Never reconstruct the
descriptor from untrusted completion JSON. Direct SAS cannot enforce an ingress
byte cap; completion enforces it, or use mediated `UploadAsync` for a strict cap.
Create-only SAS requires a single raw Put Blob, not an SDK block-upload fallback;
grants larger than the provider's 5,000 MiB single-PUT limit are rejected.

Configure cleanup of abandoned staging objects/files after crashes or unused
expired grants. Actual filesystem/Azurite tests cover streaming, cancellation,
checksum/size failures, atomicity, grant scope and path safety. Live cloud managed
identity and 5 GB throughput are not claimed. Report routes and complete
application-profile configuration remain their separate tasks.

## SDK artifacts

Dispatched SDK tasks publish with
`await ctx.Artifacts.PublishAsync(name, stream, contentType, ctx.CancellationToken)`.
The returned URI is a Viewer-authorized download route, not a storage capability.
The caller retains ownership of its stream. Seekable inputs are hashed from their
current position, rewound, then streamed; non-seekable inputs are hashed into a
private, delete-on-close `.wf-artifact-*` file in the task host's working directory.
That directory must be writable and service-private. Neither path buffers the
artifact in memory. Names are single filenames (≤ 255 UTF-8 bytes), content types
are bounded media types without parameters, and SHA-256/length are verified by
the existing `IBlobStore`. Empty artifacts are valid.

The gateway supplies an `artifacts` capability bound to run, exact task attempt,
owning agent, current agent session, reservation epoch and attempt deadline. The
agent restricts its destination to configured engine origins; the SDK uses exact
HTTP/2 without redirects and never places capabilities in URLs. Upload admission
and completion both recheck durable ownership/fencing. Aborted/lost/replaced
attempts cannot create new artifacts; an exact committed replay remains
idempotent while its original capability and agent session remain valid.

`POST /api/v1/artifact-grants/{id}` accepts
`{name,contentType,sizeBytes,sha256}` using an Authorization bearer capability and
a canonical UUIDv7 ID. It returns `201 {artifactId,expiresAt,committed}`. PostgreSQL
reserves the exact byte count before any upload: committed bytes plus all live
reservations cannot exceed **5,000,000,000 bytes per run**. Same-ID/same-metadata
retries consume no additional budget; changed metadata conflicts. Reservations
last at most 15 minutes. `PUT /api/v1/artifact-publishes/{id}` streams raw
`application/octet-stream` bytes with the exact Content-Length and the same
capability, then returns `201 {runId,artifactId}` only after verified storage and
metadata commit. A per-run advisory lock keeps an active upload counted even
across grant expiry; parallel grants wait rather than oversubscribe. No engine
run/attempt row lock is held while streaming.

`DELETE /api/v1/artifact-grants/{id}` releases only the caller's uncommitted
reservation; the SDK attempts this on upload failure/cancellation. Expired unused
reservations are reclaimed during the next grant. Failed streams cannot create
committed rows. A verified blob followed by a database/fencing failure can leave
unreferenced immutable content; retention must collect such objects, as for
reports. Committed metadata contains source, name, type, byte count, SHA-256 and
an `artifacts/{sha256}` pointer, never payload bytes.

`GET /api/v1/runs/{id}/artifacts` returns committed artifact summaries.
`GET /api/v1/artifacts/{id}/content` rechecks fresh run-scoped Viewer permission
and streams verified bytes as `application/octet-stream` attachment with
`nosniff`, `no-store` and restrictive CSP; artifacts are never inline reports.
Late checksum/read failures abort the response rather than completing partial
content. User tokens cannot mint upload grants, and attempt capabilities cannot
download artifacts. Budget failures raise nonretryable
`ArtifactLimitExceededException` through the ordinary task failure policy.

These routes use the existing configured/injected blob store and upload HMAC
key; the default profile remains filesystem-backed. Tests exercise small real
payloads through PostgreSQL, filesystem, authenticated HTTP and dispatched SDK
child processes, plus synthetic exact 5 GB reservation arithmetic. The separate
task 9.2.1.1.3 owns full 5 GB roundtrips, cross-OS RAM and Azure throughput gates.

## Registration

### Publish to a local/dev engine

`wf publish` reuses the offline validator and deterministic CI package builder,
then obtains an upload grant, uploads the package and registers immutable content:

```powershell
wf publish .\teams\<team>\<type>\<workflow> --engine <engine-origin> `
  --git-commit <lowercase-40-or-64-character-commit> --git-author '<author>' `
  --change-note 'Local development publication'
```

The CLI never opens a sign-in flow. It requires an explicit `WF_ACCESS_TOKEN`
environment value, including on loopback; normally authenticated engines require
a Registrar application token. The existing isolated Development `--dev` backend
instead uses its fixed local identity, without enterprise login. Development
authentication must not be exposed to untrusted callers.

Only trusted local source is compiled. Declare external project/import inputs
with repeated `--input`. If `schedules.json` exists, supply its exact
repository-relative `--schedule-source-path`; absent schedules preserve the
engine's existing schedule configuration. Git author/commit are explicit, never
guessed from the current checkout. Publication never retries automatically.
Post-submission cancellation or failure may already be durable: inspect engine
history before retrying. See `backend\src\Wf.Cli\Publishing\README.md` for complete
options and bounds.

### HTTP registration

The actual host exposes loopback HTTP/1.1 REST/SignalR on `Api:Port` (default
5001), beside the unchanged HTTP/2 agent/engine listener on `Engine:Port`
(default 5000). Ports must differ. Configure `Storage__UploadHmacKey` with an
externally generated, stable base64 key containing at least 32 random bytes;
missing, malformed or short keys fail startup without echoing key material.
Use the same protected key across API replicas/restarts. `Storage__Root`
defaults to `wf-data/blobs` under the executable directory; configure a
service-private, writable shared root explicitly outside isolated local use.
Migration mode still needs neither authentication nor an upload key.

`POST /api/v1/upload-grants` requires the app-only `Registrar` policy and
accepts `{kind:"package",sha256,size}` (1 byte to 512 MiB).
A verified existing blob returns `200 {exists:true}`; otherwise 201 supplies
a relative `uploadUri`, exact `maxBytes`, and expiry. PUT raw bytes to that
URI without an Authorization header. Its HMAC bearer capability binds kind,
digest, exact size and expiry, permits identical retries for ten minutes,
and never authorizes arbitrary paths or content. Known and chunked oversized
bodies return 413; short/wrong-hash bodies return 400; invalid/expired grants
return 403. Partial uploads are never published. `BlobUploadGrants` is shared
with the filesystem adapter; mediated uploads also work with an injected
`AzureBlobStore` without process-local SAS completion state. The default host
uses the filesystem store; see `docs/operations/shared-blob-storage.md` and
`infra/azure-vm/README.md` for Azure profile and managed-identity wiring.
Never log upload URLs, grants or HMAC keys.
The host restricts raw ASP.NET Core request/routing logs to Warning so
capabilities in paths and live access-token query strings are not logged.

`POST /api/v1/registrations` also requires app-only `Registrar`. The strict
contract requires `gitCommit` (40 or 64 lowercase hex characters) and
`gitAuthor`; optional `changeNote` is limited to 4000 characters. A workflow
registration supplies `workflowId` (`team/type/name`), `manifest`, package
`{sha256,size}` and the single-file `source`. Manifest `name` must equal the
complete workflow ID and metadata `type` its type segment. Include the optional
complete `registry` team snapshot, or register the team/type first. Registry-only
requests omit workflow fields. Unknown/duplicate fields, missing/null mandatory
values and malformed contracts fail explicitly; JSON body limits are 8 MiB for
registrations and 4 KiB for grant issuance. Non-JSON content types return 415.

The service runs shared DAG/variable/HTTP/policy/type-metadata/SemVer checks.
After raw-manifest preflight and strict contract/envelope checks, the non-compiling
`Validator.Validate(manifest, schedules, registry)` backstop aggregates supplied
schedule and registry errors in offline order. Invalid raw-manifest semantics
return before companion checks, matching offline description preflight.
400 Problem Details `errors[]` preserve every shared rule ID, file, path, message
and hint; use the same logical file paths when comparing offline and server
results. Request/provenance/ownership/package/policy failures remain registration
envelope diagnostics. Authoritative stored registry, retained schedules and
version compatibility are rechecked under the registration transaction.
The service verifies actual stored package bytes and archives at most 1 MiB of UTF-8 source
as a deterministic `workflow.cs` ZIP. Source keys follow the blob store's
`sources/{sha256}` convention, not user paths. Uploaded immutable source blobs
can remain unreferenced after a rejected transaction; clean those up through
the later storage lifecycle policy.

Migration `0017` adds workflow/version persistence, registry history and the
registration audit foundation. Team-scoped transaction locking makes version
insertion, active/latest pointers, registry/group replacement, history, audit
and `NOTIFY registry` atomic. Versions record source/package hashes and sizes,
SDK version, Git commit/author, change note and the authenticated principal's
`oid`, never a supplied registering identity. Audit summaries retain pointers
and registry fingerprints, not source or variable values. Failure to audit
rolls back the mutation. In-use workflow types cannot be removed by a registry
replacement.

SemVer text preserves prereleases, build metadata and large numeric components,
with a 256-character API/storage bound; build metadata cannot create a second
identity. Reusing a version with different immutable content returns 409.
New versions must satisfy the shared classifier against the latest registered
version, even if an administrator later rolls the active pointer back; 409
responses preserve WFV010 diagnostics/hints. Identical requests return 200 with
no rewrites, history or audit duplication and never reactivate an old version.
201 responses include only changed `workflowVersion`, `registrySnapshot` and/or
`scheduleConfigVersion` UUIDs. Existing runs retain their version IDs.

Schedule-only registration supplies `workflowId`, `schedules` (a string containing
the complete original JSON file), `scheduleSourcePath`, and Git provenance;
omit manifest/package/source. Paths are bounded repository-relative logical
paths using `/`, without dot segments, backslashes or control characters.
The file is limited to 1 MiB of valid UTF-8. Schedule-only changes leave workflow
versions untouched and return only `scheduleConfigVersion`. Code keys keep their
schedule UUID and permanent series through edits, removal and re-addition;
removal soft-deletes/pauses the schedule and clears its cursor, preserving fires
and run history. UI schedules remain untouched. An unchanged expression retains
its existing due cursor and pause state; a changed expression or revived
instance gets a new cursor from the registration clock. Identical content
returns 200 without rewrites; reverting to known content reuses its historical
config UUID and reactivates that snapshot. Provenance remains that of the first
registration of those exact bytes. New workflow registrations also revalidate
the retained schedule file against the active workflow's variables and refresh
effective defaults, without creating a schedule config for unchanged file bytes.
Every file/config/series mutation shares the registry/version/audit/NOTIFY
transaction. Run-now and recurring fires resolve retained version pins and
effective variables; apply-under-load firing verification is separate.

`ActiveVersionCache` is a hosted service shared by future trigger handlers.
It listens to `registry` before loading the initial atomic immutable snapshot,
refreshes after committed notifications without restarting the engine, and
resynchronizes completely after reconnect. Idle listener heartbeats detect
broken connections; transient database failures log an error and make new
lookups unavailable until resynchronized. Corrupt stored identity/manifest data
fails explicitly instead of returning a stale or partial snapshot. Workflows
without an active version, or soft-deleted workflows, are excluded.
Callers capture one `ActiveWorkflowVersion` and use its immutable version ID,
manifest and package for a new run/command; later activation does not retarget
that captured value or existing persisted run IDs. Leader readiness requires
the cache to be synchronized.

`HotActivationTests` holds 20 actual `1.0.0` SDK task-host processes at an HTTP
barrier while registering `1.1.0`. Activation must take at most five seconds;
the next run executes and finishes on the new package before releasing any
old attempt. All 20 old runs then finish with their original UUID and
version-specific binary outputs. Engine process, hosted cache task, PostgreSQL
listener and native agent process/session remain unchanged; each package is
downloaded once. This integration drives the durable run lifecycle through
`RunStore`/`Dispatcher`, completing nodes/runs only after real committed agent
success; it does not claim an autonomous hosted DAG execution loop.

Publish CLI/merge automation, workflow/version detail reads, proactive live revocation,
general audit middleware/partition maintenance and
cloud profiles remain their dependent tasks.

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

`CapacityProfile` identifies measured/manual requests-per-second capacity by
transport, codec, payload-size class and ordinally sorted string load settings.
Its source-generated compact JSON `ProfileKey` preserves exact identifier/value
case and escaping; settings order does not change identity. Keys are bounded to
2,000 UTF-8 bytes for the PostgreSQL composite index. `AgentCapacity` requires a
positive whole-number rate; only Calibrated values carry a UTC calibration time.
Migration `0012_agent_capacities.sql` adds per-agent/profile rows with foreign-key,
source and timestamp checks without changing existing agents. No row means
unverified, not zero capacity. Admin manual overrides and fenced, durable
calibration command/result APIs are available; no Bond codec profiles or load
executor are included. A simulated agent's reported rate verifies the protocol
and storage path, not benchmark accuracy or stability.

HTTPS is required off loopback. `Agent__AllowInsecureTransport=true` is only for
an isolated internal container network; TLS certificate validation stays enabled.
Agent-role token acquisition is a separate authentication backlog item.
Dispatch and cancel commands run asynchronously, so downloads and user tasks do
not block heartbeats or stream reception. Replayed attempts are deduplicated
within the agent process. Started/completed messages retain their sequence until
acknowledged and replay on reconnect or process restart. Production uses an
owner-only, checksummed result journal under the user's local application data
`wf\agents\<agent-UUID>\results`; override with `Agent__ResultJournalDirectory`.
Atomic flushed snapshots retain sequence high-water marks, observed leader epoch
and unACKed results before send. Unlimited attempts journal before the task host
ACK; limited attempts first establish physical exit and inspect the final limit
state, then journal completion so a RAM breach cannot publish success.
Only a valid current-connection/current-leader ACK removes offered records.
Limits are 4,096 records, 1 MiB per record and 32 MiB total; corruption, disk
failure and exhausted capacity fail explicitly rather than dropping results.
Hello includes both running attempts and every unACKed result, even when this
logical backlog exceeds physical task slots. The registry bounds it to 8,192
attempts and a 1 MiB protocol frame; physical slots remain clamped to MaxTasks
until a heartbeat reports actual usage. Replayed terminal results are ACKed
without a second event or changed outputs. Startup restores surviving task hosts
before constructing Hello; machine drain remains an explicit maintenance action.

`TaskHostPool` prewarms compiled workflow processes and verifies their PID,
protocol and workflow version over owner-only Unix domain sockets on Windows
and Linux. Warm dispatch starts no process. Each host binds once to one attempt:
parallel tasks cannot share mutable memory, limits or forced cancellation.
The SDK server executes real registry delegates with typed variables, declared
inputs and bounded outputs; completion waits for acknowledgement before exit.
Cancellation is cooperative first, then kills that host's process tree after
grace (default 30 seconds). Workflow hosts use ordinary, untrimmed .NET IL, not
Native AOT, and require the ASP.NET Core runtime. The sample accepts
`--task-host --socket <path>`; normal local execution is unchanged.

Hosts now survive an abrupt agent-only process kill. Each lifetime has a
flushed, atomically replaced `host-lifetimes/<lifetime>/attempt.json` beside its
pending physical-fence record. It records boot ID, PID, OS creation identity,
diagnostic UTC start time, private socket, Windows job name, package/runtime
paths and the bound attempt/epoch/deadline/caps. Linux identity uses the
boot-relative `/proc/<pid>/stat` start counter, not cross-process reconstructed
`Process.StartTime` wall-clock estimates. Identity is captured once at launch,
not refreshed when an attempt binds. Startup exclusively claims at most 1,024
lifetime records, verifies same-boot native creation identity, restores every
verified package pin without eviction, and authenticates the private socket with
an owner-only per-host token. The SDK retains its own Windows lifetime/resource
Job handles so their names and existing hard caps can be reopened after agent
death. `Resume` requires the exact bound dispatch/epoch and returns the existing
execution/result; it never invokes the delegate again. Original secret values
stay in host memory and are recovered only over this authenticated IPC to
restore masking, not replaced by newly rotated values or persisted in metadata.

Recovered attempts enter `Hello.inflight` before engine reconciliation. Each
production host redirects its own OS stdout/stderr through host-owned pipes,
including inherited native-child output, and masks decoded text before writing
durable `spool/*.record` protobuf files in its private attempt directory.
Records retain producer sequence and original timestamp; resources also have
contiguous sample ordinals. The host's atomically flushed `spool/ack` cursor
controls prefix deletion. Agent log indexing commits each complete frame and
its producer cursor together, so replay after a lost acknowledgement neither
loses bytes nor duplicates a line. A replacement resumes the existing incomplete
log capture rather than marking a surviving host's capture interrupted.
Production progress transitions are host-journaled before proceeding; checkpoint
acknowledgements can reattach. Masked results are flushed to `spool/result.pb`
before delivery and remain available until all records and the result are
acknowledged. Agent-capture I/O failure is explicit and does not change the task
outcome: physical cleanup is proved, its receipt is flushed, and unacknowledged
host bytes remain for recovery instead of being deleted or blocking a proven
machine fence. Unacknowledged host spool storage has an explicit 20 GB/one-million
record ceiling; exceeding it reports a storage failure, not successful capture.
Linux re-adoption uses token-authenticated supervisor control and
requires its final physical-quiescence certificate before removing a lifetime.
Missing/mismatched/dead hosts without a verified completed spool report `AgentLost`; an unproven descendant tree
still quarantines admission and physical-fence acknowledgements. A missing
Windows Job name after root death alone is not proof that its descendants died.
Normal completion, cancellation, drain and fencing still require physical
descendant cleanup before release. Windows hard caps survive re-adoption, but
old IOCP breach notifications cannot be replayed; managed OOM classification
remains available. Recovered native network counters expose an explicit gap.

After two seconds without fresh native agent counters, the SDK records
process-only CPU/RSS observations at one-second intervals while explicitly
marking descendant, machine and native network accounting unavailable. It does
not invent continuity for old ETW/BPF baselines. Once the agent returns, native
observations continue through the same host-owned resource sequence.
Task completion leaves native sampling open until the agent stores its final
observation and seals the record stream; acknowledgement waits for that tail too.
The Linux subreaper supervisor and an independent Windows sibling guard enforce
the absolute deadline plus 30 seconds of cancellation grace even if the agent
never returns. The Windows guard is outside the workload Job, retains its
query/termination handle, verifies the exact target identity and records physical
quiescence only after the entire Job is empty. It uses no Job breakaway exemption.
Guard RSS counts toward the warm-host budget. A hard timeout's durable result
and captured records can be recovered after physical termination without running
the delegate again. This is agent-only restart/deadline recovery, not a claim of
lossless machine-reboot recovery or native network-counter continuity.

Authored `.Defaults(p => p.Limits(cpuCores: 0.5, ramMb: 512))`, node/stage
`.Limits(...)`, and per-task `p => p.Limits(...)` configure optional caps.
The innermost **whole Limits policy** wins; zero means unlimited for that
resource, and `.Limits()` explicitly clears inherited caps. CPU must be finite
and either zero or at least 0.01 cores; RAM uses whole MiB. Existing manifests
omit absent limits and retain unlimited behavior. Caps reach durable dispatch;
requested RAM participates in placement before dispatch.
The pinned Task Plan exposes these same optional caps and displays unlimited
resources explicitly. Standalone in-process local execution rejects positive
caps instead of ignoring them; run through local/Compose agents to enforce them.
Dry-run mocks simulate resource outcomes without applying OS limits to the
simulation process.

Windows uses a nested Job Object CPU hard rate and job-wide committed-memory
cap (worker plus descendants), independently of optional sampling. CPU conversion
uses the actual active logical-processor count, not `DOTNET_PROCESSOR_COUNT`;
multi-processor-group systems and unrepresentable rates fail closed. Native
source-generated interop reuses the existing approach instead of adding CsWin32.
Job memory-denial notifications trigger failure; SDK first-chance
`OutOfMemoryException` tracking also prevents a caught managed OOM from becoming
success. Windows documents native Job completion-port notifications as
best-effort: an unmanaged allocator that handles denial without a managed OOM
cannot provide a guaranteed breach notification, although the hard cap remains
enforced.

Linux requires delegated cgroups v2 CPU/memory controllers. Each worker joins
its private unlimited leaf **before exec**, then binds caps on that same leaf
before secret resolution or task delegates; descendants inherit it. This charges
the warm worker's existing memory too: migrating a ready process into a new
cgroup would leave its earlier charges outside the cap. `cpu.max`, `memory.max`,
zero swap and `memory.oom.group=1` enforce caps. `memory.events` OOM counters,
not reclaim/`max` events, identify a breach.
The containment supervisor stays in an **unlimited sibling** so OOM cannot
destroy its physical-cleanup proof. Optional native accounting remains on the
parent and includes both supervisor and workload; measured RAM can therefore
exceed the workload cap by supervisor overhead. Configure
`Agent__LinuxCgroupRoot` for custom launchers; the shipped systemd unit supplies
it and delegates both controllers using `DelegateSubgroup=agent` (systemd >=254).
No BPF or network capability is required for limits. Missing delegation or
unsupported enforcement fails before task code, never silently runs uncapped.
Confirmed RAM breach terminates the entire owned tree before journal/result
publication, discards outputs, and returns nonretryable `ResourceLimit`.

The global warm pool defaults to four unused processes and 200,000,000 bytes
aggregate RSS, evicting least recently used hosts and emptying after five
monotonic idle minutes. Active hosts are not warm-eviction candidates.
Eviction waits for process exit before admission; package pins survive disposal
until exit. RSS is checked on admission and sampled while warm hosts exist, not
enforced as an instantaneous OS hard limit. SDK private IPC startup uses only
Kestrel/routing/gRPC services; agents retain Native AOT size optimization.

Footprint acceptance remains **blocked**, not complete: final Windows native
cold-dispatch peaks include simultaneous agent growth above its idle baseline
plus the full real SDK task-host RSS. MaxTasks=1 measured 53,555,200 bytes/task
(51.0742 MiB); MaxTasks=2 measured 51,558,400 bytes/task (49.1699 MiB).
Both exceed the contractual 50,000,000 bytes/task. Native idle measured 0.0244%
normalized CPU over 20 s and about 26 MB RSS, below 1%/100 MB.
Set `WF_AGENT_EXECUTABLE` to a published native agent and
`WF_AGENT_FOOTPRINT_BENCHMARK=true` to enable the strict `FootprintBenchmark`
integration lane; it intentionally fails until the combined cold budget is met.
No managed-runtime subtraction, forced GC or working-set purge is used.
Linux measurements remain outstanding.

`PackageCache` downloads immutable ZIP packages into SHA-256-addressed directories.
Set `Agent__PackageCache` to an absolute path to override the user's local
`wf-agent\packages` directory. Packages contain published IL/dependencies plus
`wf-package.json` with `version` and root-level `entryAssembly` DLL fields.
HTTPS is required except on loopback. Hash, path, link, size and manifest checks
precede atomic publication; warm hits verify archive and extracted-file integrity
without HTTP or compilation. Cache failures are explicit attempt platform errors.
Windows publication retries only bounded file-lock/access conflicts.

Published cache storage is bounded to 4 GiB and 32 packages by default, counting
archives plus expanded files. `PackageCache` constructor budgets can override
these positive limits. Successful cold/warm reads durably touch LRU ordering;
oldest unused packages are evicted first, including after restart. Use
`using var package = await cache.GetAsync(...)`: each returned `TaskHostPackage`
owns a caller lease, and every warm/active host independently pins its package
until process exit. Production dispatch already disposes its caller lease.
Pinned capacity or an oversized package fails explicitly without partial
capacity-plan eviction; corruption and filesystem failures are not cache misses.
Use one cache owner per root. Limits exclude foreign content, abandoned staging,
filesystem allocation overhead and one serialized in-progress download/extraction.
Live-host re-adoption verifies and restores all surviving package pins before
any normal cache eviction, even when the configured cache budget was reduced.

`Dispatcher` accepts only database-committed Ready tasks in running runs.
Dispatched event, attempt capacity reservation and dispatch outbox commit in one
PostgreSQL transaction before the gateway sends anything. Committed pending
attempts count against slots/RAM before heartbeats catch up. Streams have one
writer; a new Hello fences the old session, pending commands replay, and task
events are acknowledged only after durable, idempotent acceptance. Core readiness
and node/run coordination are explicit caller responsibilities; this is not yet
the full hosted run actor or HA leader loop.

The control-plane agent gateway listens on loopback HTTP/2 port 5000. Configure
`ConnectionStrings__Workflow` and authentication as above, run the explicit
`--migrate` deployment mode, then run `dotnet run --project
backend\src\Wf.ControlPlane` (with `-- --dev` only in isolated Development).
This is not yet the full hosted run-actor/scope-RBAC control plane.
Registration and heartbeat acknowledgements
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

## Schedule expressions

`Wf.Validation.Schedules.ScheduleExpression.Create(cron, interval, timeZoneId)`
accepts exactly one standard five-field cron expression (or supported macro) or
positive elapsed interval, plus an explicit IANA timezone. UTC results are
strictly after the supplied cursor; `GetNextOccurrences` returns an immutable
preview (five by default). Cron uses Cronos DST semantics: fixed times in a
spring gap move to the first valid instant, fixed fall-fold times fire once,
and interval-like cron preserves distinct UTC occurrences across both offsets.
Elapsed intervals must advance from the last intended fire, not scheduler poll
time. Hosts need ICU/timezone data. The hosted scheduler uses this same expression
implementation for persisted cron and interval cursors.

`Wf.Engine.Core.Scheduling.OverlapPolicy.Decide(activeRuns, policy)` reuses the
schedule contract's Skip/Queue/Concurrent values. With no other nonterminal run
in the same series it returns Start; otherwise default Skip, Queue, or Start
for Concurrent. Count queued runs too, excluding the candidate itself.
Decisions are pure and reject invalid policies/counts. The hosted scheduler
records skipped fires transactionally; series UI and FIFO queued-run release
remain separate tasks.

`Wf.Engine.Core.Scheduling.SchedulerLoop` plans from a persisted next-fire cursor,
a next-occurrence delegate (for example `ScheduleExpression.GetNextOccurrence`),
an explicit nonnegative grace period and a `TimeProvider`. Every slot older than
grace remains `Missed`; slots exactly at grace remain due for a normal start.
Default Latest also emits one separate `CaughtUp` run action, trigger `catch-up`,
referencing the newest missed slot. None suppresses only that action, not history.
Never insert a second `schedule_fires` row for the catch-up action: retain the
missed slot's identity and link the run to its source. The returned next cursor
preserves interval phase and UTC/DST identity. Commit cursor advancement,
missed history and run creation atomically; repeated planning alone is not
exactly-once execution. Applying this beyond-grace policy in the hosted scheduler
uses `ScheduleCatchUp` and the same pure planner.

Migration `0014_schedule_fires.sql` stores fire history with an exact-once
`(schedule_id, fire_at)` key; claim with `ON CONFLICT DO NOTHING` and create a
run only when the insert succeeds in the same transaction. Stable outcomes:
Started=0, Skipped=1, Missed=2, CaughtUp=3. History references existing schedules
(soft-delete them); optional run IDs remain history references after run
retention removes the corresponding run. Timestamps must be finite, and all
callers must use PostgreSQL's microsecond precision for slot identity.

`ScheduleFireService` owns the leader's `ScheduleTimerHeap`. Each due candidate
rechecks the current unpaused/non-deleted schedule under a row lock and fences
the lease inside the same transaction as the fire claim, overlap decision,
initial run/snapshot/node rows and next intended cursor. Scheduled runs retain
the permanent series, selected version UUID, metadata and bound effective
variables; `trigger=Schedule`, `triggered_by=scheduler`. Replayed slots never
replace fire history or create another run. Cursor advancement does not change
the configuration's optimistic row version.

The service listens to `registry` and `schedules` before taking its snapshot,
reloads after committed notifications and on takeover/reconnect, and refreshes
every five seconds as a backstop. Edits are read again at admission, not copied
from stale heap entries. Shared `WorkflowVersionResolver` handles trigger/run-now
and recurring exact/range pins; a recurring unpinned fire reads the current
active pointer in its transaction, independently of notification ordering.
Invalid pins/values log a rejection and retain the due cursor, without starving
other schedules. Transient storage failures disable scheduler readiness until
resynchronization; fatal corrupt state fails explicitly. Queued-run FIFO release
remains a dependent task, not implied by basic exactly-once firing.

`Wf.Chaos.Tests/Infrastructure/ChaosFixture.cs` supplies two real production
engine processes, two real agents, a downloaded SDK task host and an owned
PostgreSQL 18.6 database behind Toxiproxy 2.13.0. Per-agent gateway proxies isolate
one agent without partitioning its healthy peer. The fixture supports hard
leader/agent/host kills, stable-identity agent restart and database partitions;
PID creation identities guard task-host cleanup against reuse. Both engines
share only this fixture's private blob root and signing key. The Toxiproxy
control API is published on host loopback, never an untrusted interface.

Run `dotnet test backend\tests\Wf.Chaos.Tests --filter FullyQualifiedName~ChaosFixtureTests`
for the real fault-tool smoke. Its disk injector fills an **8 MiB container-only
tmpfs**, verifies the actual `ENOSPC` error and verifies recovery after deleting
its single filler file; it never fills the host disk or claims Windows agent
volume exhaustion. Linux runs this smoke explicitly in `backend.yml`; it is
marked `ChaosSmoke`, separate from the default solution lane and from the
pending full randomized/reboot acceptance suite. Agents explicitly retry a peer
EOF during an HTTP/2 handshake instead of exiting on `HttpIOException`, and
require a valid Hello acknowledgement within ten seconds before keeping that
engine connection. An accepting but silent proxy cannot indefinitely block
failover to the next configured engine.

`Scenarios/CoreFaultScenarios.cs` exercises held SDK work under eight real
faults: leader kill, isolated agent partition, database partition, agent-only
restart, unrecoverable agent/tree kill, SDK host kill, physical PostgreSQL
standby promotion and database snapshot-volume exhaustion. Every scenario
checks the original committed event prefix, contiguous unique lifecycle
transitions, one terminal run result, expected attempt count and an empty
dispatch outbox. Temporary partitions and agent restart retain the original
attempt; host/tree loss uses the existing bounded platform replacement policy.
Run `dotnet test backend\tests\Wf.Chaos.Tests --filter FullyQualifiedName~CoreFaultScenarios`;
Linux CI runs it separately from the default solution lane.

Leader recovery has a 30-second wall-clock gate. Database recovery has an
additional 30 seconds after storage becomes available or the promoted standby
is writable and routed. Unrecoverable agent loss preserves the complete
30-second last-heartbeat reclaim interval, then requires replacement execution
and short-fixture success within a further 30 seconds, with completion also
bounded by persisted deadlines. Successful short-fixture completion is an
upper bound on SDK-body startup, not a pre-host `TaskStarted` timestamp.
The standby uses a real base backup and synchronous WAL flushing; promotion
changes database processes, not merely a TCP endpoint. The disk scenario puts
only the isolated run-snapshot partition in an **8 MiB PostgreSQL tmpfs
tablespace**, fills it, completes the SDK body while persistence is blocked,
then frees its single filler and verifies durable result replay. Incompressible
fixture variables require fresh snapshot/TOAST pages; host disks and Windows
agent volumes are never filled. Fenced background work logs and retries
transient storage failures and PostgreSQL `25P04` transaction timeouts rather
than stopping the engine. Each retry reacquires the normal database fence;
the one-second transaction bound and leadership checks remain unchanged.

The nightly/manual `schedule-chaos.yml` lane runs the actual one-hour
`ScheduleExactlyOnce` gate against PostgreSQL 18.6 with durability enabled:
1,000 minutely schedules, uniformly phased 60 ms apart across each minute,
and two hard leader-process kills during firing. The historical PostgreSQL 17 local gate
retained all 60,000 intended fire slots and runs, zero duplicate or invalid
runs, zero extra slots and zero silent misses. Intended-fire-to-durable-commit
p99 was 0.274046 s against the unchanged 2 s limit; maximum failover delay
was 15.887371 s, with 428 late slots recovered and lease epochs 1 -> 2 -> 3.
Commit timing uses PostgreSQL `track_commit_timestamp`; `fsync`,
`full_page_writes` and `synchronous_commit` stay on. Nightly artifacts retain
metrics, process logs and TRX for 14 days. This workload does not claim a
synchronized 1,000-at-once burst meets 2 s; development bursts measured p99
5-6 s. Hosted GitHub Actions execution remains unclaimed until a push.

Every overdue cursor is replanned against one UTC clock snapshot, including on
takeover and reconnect. `Scheduler:FireGrace` is a nonnegative `TimeSpan`
(environment: `Scheduler__FireGrace`), default `00:00:02`, matching the on-time
delay budget. Exactly at grace is an ordinary scheduled fire; older slots are
all recorded as Missed. Default Latest admits one catch-up action from the
newest newly claimed missed source; None records the same history without that
action. The missed row keeps outcome Missed and links its catch-up run with
`run_id` and reason `catch-up`, never a duplicate slot or rewritten outcome.
Stable `RunTrigger` values are Manual=0, Schedule=1, CatchUp=2.

Catch-up admission precedes recent ordinary due slots in the same transaction
and obeys Skip/Queue/Concurrent. An already-active series can suppress catch-up
under Skip; its missed source retains reason `overlap`. A catch-up admitted under
Skip can similarly suppress a newer ordinary slot, which is recorded Skipped.
All missing history, admitted runs and the final future cursor commit together,
so failure/retry or repeated takeover cannot consume only part of the backlog.
Hosted outage tests advance an injected clock, not a wall-clock sleep. The
multi-schedule case stops the actual hosted services, advances ten minutes plus
three seconds beyond the fire grace, and restarts across three leaders: each of
three Latest schedules records ten exact missed slots and one newest-source
catch-up run; two None schedules record those same misses without a run.
Paused/removed controls remain untouched, stale timers do not recreate work,
and the next leader recovers the persisted catch-up runs. Existing migration
`0007_schedules.sql` already supplies the canonical `catchup_policy` column;
no duplicate `catch_up_policy` or follow-up column migration is needed.
Series history displays a Catch-up trigger badge for its linked durable
`catch-up` source, without relabeling that source's Missed outcome.

`Wf.Agent.Secrets.SecretMasker` takes resolved secret values, never reference
names. `Redact` masks complete text; create a separate text stream for each
attempt/stdout/stderr, append decoded chunks and publish `Complete()` before
EOF. Streaming retains at most the longest value minus one UTF-16 characters,
preventing chunk boundaries from exposing matches; overlapping/adjacent matches
collapse to `***`. Use a stateful UTF-8 decoder before supplying byte chunks.
Matching is literal, ordinal and case-sensitive for each value and its standard
UTF-8 base64 form; arbitrary transformations, URL encoding and unpadded/base64url
forms are not detected. Lookahead includes the encoded values.
`RedactJson` masks decoded string values and property names while
keeping JSON valid; matching non-string scalars become strings. Key collisions
fail explicitly rather than discard outputs. The completed-output/error path
uses this masker before durable journaling. Task-start resolution supplies actual
resolved values to log/output/error masking; reference names are not values.
Broad stored-data masking verification remains its separate task.

## Workflow project template

Pack `backend\src\Wf.Templates\Wf.Templates.csproj`, install the resulting NuGet
package with `dotnet new install <package-path>`, then generate a workflow:

```powershell
dotnet new wf-workflow -n Example --sdkVersion 1.0.0
dotnet test
```

The configured NuGet source must provide matching `Wf.Sdk` and `Wf.Sdk.Testing`
packages. The template includes `workflow.cs`, `schedules.json`,
`workflow.api.json`, a local schedule schema and four runnable dry-run tests.
Generated tests use one assembly fixture and the built-in Microsoft Testing
Platform MSBuild integration, preserving the real out-of-process workflow
harness without VSTest startup overhead. Workflow compilation uses ordinary IL.

Isolated install/generate tests prepare actual SDK NuGet packages and enforce
the exact warm generated `dotnet test` command below 10 s, including restore/build
checks and unchanged workflow-cache integrity. Local Windows measurement:
5.916 s warm, 25.320 s with an empty isolated package cache; package/feed preparation
16.547 s separately. Cold download/compilation is not claimed below 10 s.
Windows/Linux CI runs the same integration; local Linux results remain unverified.

The same package includes `dotnet new wf-repo` (`--cliVersion` defaults to
`1.0.0`), which scaffolds a workflows-repository PR gate and local `wf` tool
manifest. Require the **Workflow PR gate** check. It selects changed workflow
directories from the full base/head diff, then runs actual `wf validate` and
`dotnet test`; validation, test and restore failures fail the check. Shared
changes select affected workflows conservatively; deletions, renames and
special-character file names are covered by selection tests.

The repository needs .NET 10, Python 3.9+, full Git history and readable NuGet
sources for tool package `wf` and pinned SDK/testing dependencies. No repository
variables or secrets are required; do not expose feed credentials to PR code.
From a generated repository, run the same gate locally:

```powershell
$env:PR_BASE_SHA = '<full base SHA>'
$env:PR_HEAD_SHA = '<full PR head SHA>'
$env:PR_CHECKOUT_SHA = git rev-parse HEAD
python .github\scripts\pr_gate.py
```

`--list` prints selection-only JSON. Local integration verifies valid, invalid
DAG, failing-test and restore-failure outcomes against real generated workflows.
Windows MSBuild currently rejects apostrophe/ampersand directory names
(`MSB3094`); selection handles them safely, while executable directory scenarios
cover Unicode and spaces. Hosted required-check behavior remains unverified
until a permitted push/PR.

`wf-repo` also includes **Register merged workflows**, triggered only by pushes
to `main`. Its full before/after range selects every affected workflow (all
workflows for an initial push or shared-input change), then runs the same
validation/tests before acquiring credentials. Explicit workflow SemVer must
be bumped in source; Git commits provide provenance, not generated versions.
Queued publications use `queue: max` without canceling in-progress runs.

For this optional CI publisher, configure `AZURE_CLIENT_ID` and
`AZURE_TENANT_ID` as repository secrets and `WF_ENGINE_URL` (HTTPS origin) and
`WF_API_SCOPE` (the API application ID URI plus `/.default`) as variables.
The Entra application's federated credential must trust the repository's
`refs/heads/main` subject with audience `api://AzureADTokenExchange`, and its
service principal needs the API's application-only `Registrar` role and
`idtyp=app` claim. No client secret or Azure subscription is required.
The acquired API token is masked and passed only to the publication process,
not persisted in `GITHUB_ENV`, arguments or artifacts. The existing PR gate
remains credential-free; this does not add prototype sign-in/logout.
An installed local tool runs `register_on_merge.py --validate` and
`--publish` with `PUBLISH_BASE_SHA`/`PUBLISH_HEAD_SHA`; publication additionally
requires matching `GITHUB_SHA`, `GITHUB_EVENT_NAME=push`,
`GITHUB_REF=refs/heads/main`, `WF_ENGINE_URL` and `WF_ACCESS_TOKEN`.
Failed validation, tests, restore or publication fail the run explicitly.

The `RegistrationLatency_twenty_real_main_merges` integration test creates 20
actual scratch-repository main merges and publishes each through the template
helper, real CLI, signed-token API and PostgreSQL. Like a consumer repository,
it references pinned SDK packages, packed once from the current build into an
offline feed with an isolated NuGet cache, rather than rebuilding SDK sources
on every merge. It observes the production
registry listener without restarting it, checks every version/merge SHA and
asserts merge-to-active nearest-rank p95 <= 60 seconds and every conservative
registration-to-active upper bound <= 5 seconds. Both registration timestamps
use the database clock. Per-merge timings appear in test output; this local
publication measurement excludes SDK package preparation, hosted runner queues,
PR test execution and live Entra token exchange.

## CLI and packages

`wf run` submits a manual run without local task execution or waiting for run
completion. `--version` pins an exact UUID or SemVer; omission uses the active
version. `--var key=JSON` supplies typed JSON values. Single-machine submission
also works in compiled workflows with `--engine <origin> --single-machine
[--machine <name>]`.

```powershell
dotnet run --project backend\src\Wf.Cli -- run team/type/workflow --single-machine --machine worker --engine http://localhost:5001 --var 'count=3'
```

`--engine` or `WF_ENGINE_URL` and noninteractive `WF_ACCESS_TOKEN` use the same
origin/token restrictions as `wf abort` below. Redirects and automatic submission
retries are disabled. Exit 0 prints the accepted UUIDv7, not execution success;
1 means rejection, 2 means usage/transport/contract failure, and 130 means
cancellation. An uncertain transport outcome may already have been accepted.

```powershell
dotnet run --project backend\src\Wf.Cli -- abort $runId --reason "Stop deployment" --engine https://engine.example
```

`wf abort` requires an exact UUIDv7 and a nonblank 1-500-character reason.
`--engine` or `WF_ENGINE_URL` supplies an HTTPS origin; HTTP is restricted to
loopback. Supply the API bearer token through `WF_ACCESS_TOKEN`, not a command
argument. A non-loopback target requires it; token acquisition/refresh is not
implemented by this command. Loopback without a token works only when the
server itself permits it, such as explicit local development mode.

The official SignalR JSON/WebSocket client subscribes to the authorized run
before sending one public abort request with source CLI. Output includes the
engine's immutable accepted record and actual live run states, including
Aborting/Aborted, rather than presenting a 202 response as completed
cancellation. An existing first abort retains its original actor/source.
There is no REST polling or automatic abort retry. Redirects are disabled;
missing/oversized/malformed acceptance, live gaps/resync and transport loss fail
explicitly without claiming that durable acceptance was undone.

Exit codes: 0 only after observing Aborted; 1 for denied/rejected acceptance or
another terminal state; 2 for usage/configuration/transport/contract failure;
130 for Ctrl+C. Cancelling monitoring does not cancel an already accepted abort.
Actual-process tests observe Aborting, hard-kill the owning leader while the
controlled task remains running, then observe Aborted under the next leader,
with one original command, audit and abort-bearing event. This proves
lease/command/replay recovery through real PostgreSQL, not surviving arbitrary
external task processes or undoing completed external side effects.

```powershell
dotnet run --project backend\src\Wf.Cli -- validate C:\workflows\team\example --package C:\artifacts\example.zip
```

The directory must contain `workflow.cs`. `wf validate` reuses the shared
validator, including schedules, team registry and optional API-baseline checks.
Use repeated `--input <file-or-directory>` for dependencies outside the source
root, `--baseline <workflow.api.json>` for compatibility validation, and
`--work-directory <directory>` for build-cache artifacts. Exit codes: 0 valid,
1 validation/compiler/describe failure, 2 usage/infrastructure/integrity failure,
130 cancellation. Diagnostics retain rule, file, JSON path, message and hint;
compiler diagnostics remain visible.

`--team-type-quota <positive-integer>` overrides the trusted default of 10 types
per team; quota violations return WFV017 naming the configured limit. The same
option is `ValidationOptions.TypeQuota`, passed as trailing `options` to both
sync/async `Validator` and `WorkflowValidation` entry points. Invalid limits
fail before workflow discovery. `team.json` cannot raise its own quota;
author-provided quota properties are rejected as unknown contract fields.

`--package` emits a sorted ZIP with fixed entry metadata, every published file
and `wf-package.json` containing the workflow version and actual entry assembly.
The command prints the actual ZIP's SHA-256. Writes replace atomically; unsafe
paths, links, inventory changes and agent package-limit violations fail without
destroying an existing package. Compilation disables debug symbols to remove
random intermediate PDB-path changes from assemblies. Reproducibility requires
the same SDK/OS, source paths/environment, pinned dependencies, complete declared
inputs and deterministic workflow code. Tests compare two independent cold
builds byte-for-byte and execute the package through the real agent cache and
warm task hosts. Publishing/registration commands remain separate tasks.

## Script build cache spike

`Wf.Validation.Build.ScriptBuildCache.BuildAsync(new ScriptBuildRequest(script,
sourceRoot, additionalInputs), token)` publishes framework-dependent IL once,
runs the compiled `--describe` entrypoint, and returns the typed manifest, exact
JSON, assembly and publish paths. Content/configuration/dependency fingerprints,
cross-process locking, atomic publication and an integrity inventory protect
cache hits; corruption, source changes, compiler/describe failure and cancellation
fail explicitly, never serve stale success. Callers must declare external
dependencies; floating feeds, undeclared files and nondeterministic descriptions
are not tracked. No eviction, sandbox or implicit timeout is provided by this spike.

Local R-5 measurements: .NET SDK 10.0.402, Windows x64, real 20-node SDK workflow
plus schedule JSON, cached NuGet, concurrent shared-machine work. Totals for cache
operations include fingerprinting and integrity verification; not a full
Validator CLI benchmark.

| Path | Publish | Describe | Total |
|------|---------|----------|-------|
| Raw cold | 37.550 s | 0.679 s | 38.229 s |
| Raw incremental warm | 7.996 s | 0.572 s | 8.569 s |
| Cache miss | 31.744 s | 0.948 s | 35.523 s |
| Cache hit | skipped | skipped | 1.050 s |

The warm < 10 s gate passes. Twenty-six cache cases include real compilation,
schedule invalidation, concurrent builders, corruption and owned process-tree
cancellation. Timing evidence is kept here until a PR is explicitly requested.

`Wf.Validation.Validator.Validate(directory)` and `ValidateAsync` use this
cache for a directory containing `workflow.cs`. `Wf.Sdk.Testing.WorkflowValidation`
exposes the same API to xUnit and other callers. Results contain immutable errors
with rule ID, file, path, message and hint, plus the successful build or original
compiler/describe diagnostics. WFV001-WFV004 cover missing directory/script,
publish failure and describe failure. WFV005-WFV009 cover DAG cycles, unknown
dependencies, duplicate keys, variable type/default/allowed-value constraints and
required-flag shape. Raw manifest checks precede typed contract construction on
both fresh and cached descriptions; errors sort by rule ID and indexed JSON path.
Required variables without defaults are valid definitions; runtime binding checks
their supplied values. Cancellation, infrastructure/cache errors and malformed
description contracts propagate, not turn into validation success.
Directory validation executes trusted workflow code; it is not a sandbox.
The typed `Validator.Validate(manifest, schedules, registry)` overload instead
validates an already-described manifest and optional UTF-8 companion files
without building or executing source; registration uses this backstop. Optional
schedule and registry errors follow manifest errors; version comparisons remain
a separate step.

`dotnet test backend/tests/Wf.Validation.Tests --filter RuleCatalogCoverage`
requires an unskipped offline positive and negative test for every shared
`RuleCatalog` WFV identifier. Add `RuleCoverage(id, valid)` markers to actual
xUnit cases when introducing a rule; the guard rejects missing directions,
unknown markers and newly catalogued rules without evidence. It also executes
the marked real SDK publish/describe success and compiler/definition failures.
Registration-only envelope diagnostics such as REG001 are not shared rules.

`Wf.Validation.SemVer.ApiBaseline.FromManifest` generates the immutable
`workflow.api.json` contract: SemVer version, variable schemas and node keys.
Ordinal ordering and source-generated JSON make equivalent manifests produce
identical bytes. Strict parsing, stream read/write, cancellation and sanitized
errors are covered, including a real SDK round-trip with reflection disabled.
`BreakingChangeClassifier.Classify(previous, current)` returns required bump,
deterministic changes and WFV010 errors with the required level in their hints.
Removals/renames/type changes, narrowed constraints, changed defaults and newly
required values without defaults require MAJOR; compatible additions/loosening
require MINOR; implementation-only changes require PATCH. SemVer precedence
ignores build metadata; same-core prerelease progression satisfies PATCH only.
This is an explicit baseline-comparison step; registration wiring and schedule
compatibility follow separately.

## Benchmarks

```powershell
dotnet run -c Release --project backend\tests\Wf.Benchmarks -- --filter '*Validator*'
```

Full cached validation checks an actual 200-node workflow, 50 schedule instances
and a team registry, including source/dependency hashing, cache integrity and
shared semantic rules. Setup compiles/describes once; cold compilation is not
part of the measured operation. Missing/invalid results or a nonpositive,
nonfinite or >= 2-second mean fail the command. October 15, 2026 Windows
measurement: mean 906.736 ms across ten iterations (standard BDN outlier
filtering), 5.78 MB allocated per call. CI runs this gate on Windows and Linux;
remote results remain unrun until push. The pinned engine baseline compiles
only its copied engine harness with `ENGINE_BASELINE`, excluding newer
validator/group-commit dependencies unavailable in the older code revision.

```powershell
dotnet run -c Release --project backend\tests\Wf.Benchmarks -- --filter '*GroupCommit*'
```

The R-2 spike measures actual PostgreSQL event appends with `fsync`,
`full_page_writes` and synchronous commit enabled. Per-event, immediate and
requested 2 ms batching compare one/32 actors; each configuration also collects
1,024 individual acknowledgements and verifies snapshot-tail recovery.
`GroupCommitAppender` backpressures its bounded queue, commits contiguous event
groups atomically, acknowledges only after COMMIT and drains on normal disposal.
Cancellation before persistence skips a request; cancellation after persistence
starts cannot turn a committed write into a cancelled acknowledgement.

October 15 local measurements: 32-actor immediate-batch ack p95 46.723 ms on
Windows / 36.628 ms on Linux; requested 2 ms groups reduce commits from 1024 to 32
and ack p95 to 42.298 / 27.715 ms. Both profiles miss the 4 ms persistence target.
TD section 13 records the full table, environment and decision: immediate
coalescing by default, optional bounded delay for bursts, never early
acknowledgement of critical effects. This event-only prototype does not replace
the dispatch/result transactions. Azure HA remains unmeasured; no performance
sign-off is implied. `group-commit-metrics.json` accompanies BDN results;
`--artifacts` relocates these outputs too.

```powershell
dotnet run -c Release --project backend\tests\Wf.Benchmarks -- --filter '*Describe*'
```

The precompiled 200-node description benchmark exports full JSON and fails when
p95 reaches 1 second or measurements are missing. Workflow construction and
compilation are outside the measured operation. A local Windows measurement on
October 15, 2026: mean 0.483 ms, p95 0.524 ms. Generated reports stay under
`.agent-artifacts\BenchmarkDotNet.Artifacts` and are ignored by git; results depend on the machine.

```powershell
dotnet run -c Release --project backend\tests\Wf.Benchmarks -- --filter '*EngineCore*' --metrics .agent-artifacts\BenchmarkDotNet.Artifacts\engine-metrics.json
dotnet run -c Release --project backend\tests\Wf.Benchmarks -- --compare .agent-artifacts\BenchmarkDotNet.Artifacts\baseline-metrics.json .agent-artifacts\BenchmarkDotNet.Artifacts\engine-metrics.json
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

```powershell
dotnet run -c Release --project backend\tests\Wf.Benchmarks -- --run-query-bench
```

The run-list/filter gate seeds exactly 100,000 historical run summaries across
four teams, 20 types, 200 workflows and 200 series, spanning approximately 28
hours. Signed JWT requests use production authentication and fresh Viewer
authorization; only one team's 25,000 runs are visible. Each of ten scenarios
requires 100 measured requests after ten warmups with nearest-rank p95 strictly
below 300 ms. Scenarios cover unscoped authorized listing, team, type, status,
AND metadata, time range, combined filters, workflow, series and keyset paging
after 5,000 rows. Complete HTTP bodies, typed deserialization and exact identities,
ordering, values and page boundaries are verified against an independent seed
oracle inside each measured request.

Local Windows/.NET 10/PostgreSQL 17 results on October 16, 2026: every scenario
passes; worst p95 126.3951 ms (unscoped listing), team 33.693 ms, combined filters
21.2728 ms and deep paging 38.0153 ms. These are sequential warm loopback Kestrel
measurements, not cold-cache, concurrent-load, UI-rendering or remote-network
SLAs. No forced query plans, cache purges or production query/index changes.
The database contains summary rows rather than complete historical event graphs.
Reports include dataset, environment, durability settings and per-scenario
percentiles under ignored `.agent-artifacts\BenchmarkDotNet.Artifacts\run-query\<id>\report.json`.
`WF_TEST_POSTGRES` uses a unique schema; otherwise an isolated PostgreSQL 18.6
container is created. Both modes require `fsync`, `full_page_writes` and
`synchronous_commit` ON and remove their own schema/container afterward.
`benchmarks.yml` runs the gate and its actual performance fixture on Windows
and Linux; ordinary backend tests exclude the `Performance` trait. Remote CI
execution is not implied by the local result.

```powershell
dotnet run -c Release -p:ServerGarbageCollection=true --project backend\tests\Wf.Benchmarks -- --live-fanout-bench
```

The live fan-out gate connects 500 actual WebSocket/MessagePack clients with
distinct signed Viewer identities. Each subscribes to the same type and one of
100 evenly sharded runs (five subscribers per run). After every client observes
the warmup, an independent producer schedules 50,000 lifecycle events at 10,000
events/s for five seconds. Synthetic input uses synchronous PostgreSQL binary
COPY and atomic, sequence-checked run watermark updates: this measures the real
notification/live fan-out path, not engine ingestion throughput or browser
rendering. Four independent input lanes each own 25 runs and commit ten rounds
(250 events) every 100 ms. Each run remains strictly ordered; the producer never
waits for an earlier commit. Timestamps precede persistence and include
commit queueing, database work and client receipt, not just socket transmission.

Acceptance requires exact durable input and 250,000 ordered run-event deliveries,
plus all 25,000,000 client/entity updates covered by coalesced list watermarks.
Every covered input contributes a latency sample at its first covering client
receipt; coalescing cannot hide old or missing updates. Both distributions require
nearest-rank p95 <= 1 s and p99 <= 2 s. Wrong identities/states, gaps, duplicate run
events, regressed list watermarks, disconnects or resync requests fail the run.
The producer cannot reduce its offered rate when commits or clients are slow;
its five-second window and scheduling lag are gated and reported separately
from commit drain time. All three PostgreSQL durability settings must remain ON.

The fixture analyzes its seeded run summaries, limits its database pool to eight
connections, and enables 64 automatically prepared statements after two uses.
These are database-plan optimizations, not cached authorization decisions:
each actual send still performs fresh scope and role checks. The report records
these settings; the fixture removes only its owned schema/container. JSON reports, including
failed measurements, remain under
`.agent-artifacts\BenchmarkDotNet.Artifacts\live-fanout\<id>\report.json`. A reduced actual signed
WebSocket smoke and count/latency/durability gate tests run separately; smoke
cannot satisfy the 500-client acceptance gate. `benchmarks.yml` includes Windows
and Linux lanes; local results do not imply hosted CI execution.

**Current status:** the full 500-client acceptance gate has not passed.
Task 6.1.1.1.4 remains pending; the committed fixture and fan-out optimizations
are a work-in-progress checkpoint, not a performance acceptance claim.
The CI job deliberately enforces the unchanged thresholds and can fail until
the remaining performance work is complete.
