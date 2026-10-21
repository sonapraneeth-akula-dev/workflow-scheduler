# Azure VM production profile

Prepared deployment assets, **not an Azure deployment**. These templates create
billable VMs only when an authorized operator explicitly deploys them. No Azure
account inspection, resource creation or cloud recovery test was performed.
Use `..\ha-lab` for the working local K3s lab; its Development authentication,
emulator credentials and host-based storage are not production settings.

## Topology and prerequisites

`main.bicep` creates at least two **separate VMs per role**: `kardia-prod-frontend-*`,
`kardia-prod-api-*`, `kardia-prod-controller-*`, `kardia-prod-agent-*`. Spread each
role across availability zones with enough surviving worker capacity. Controllers
share one PostgreSQL-fenced leadership lease; the API never schedules work.
This profile runs one hardened Docker container per VM, not K3s. Application
inventory uses VM names and the same `/admin/system` view; pod fields are empty.
K3s is not required for the recovery protocol or Azure application images.

Supply these existing, approved resources before deployment:

* Private workload subnets, private DNS records matching every VM name, Bastion/VPN
  management and explicit outbound connectivity. There are no public VM IPs.
  Role NSGs allow only management, private application TLS and load-balancer
  probes inbound; private data/peers and named Azure service tags outbound.
* A zonal ingress/load-balancer design with a stable browser hostname, TLS
  passthrough from port 443 to frontend port 8443, and backend pools supplied in
  `frontendMachines`. A TCP probe on 8443 tests the listener, not application
  readiness: gate rollout on `/healthz/ready` through the gateway as well.
  Preserve WebSockets and HTTP/2 if another proxy is inserted; workers normally
  connect directly to the private controller HTTPS origins.
* A backed-up, private, highly available PostgreSQL service and a private,
  key-disabled Azure Blob account/container. Enable suitable retention,
  restore procedures and zone redundancy for these data services separately.
  This VM template does not create or validate their availability.
* An RBAC-mode Azure Container Registry with admin/anonymous access disabled,
  and an RBAC-mode Key Vault with private networking and purge protection.
  The template scopes `AcrPull` to the registry, `Key Vault Secrets User` to each
  role's configuration secret, and `Storage Blob Data Contributor` to the blob
  container for API/controller VMs only. An ABAC-enabled registry needs its
  corresponding repository-reader assignments instead; do not deploy this
  RBAC-mode example unchanged.
* A versioned, approved **Gen2 x64 Linux image** supporting Trusted Launch,
  encryption at host and accelerated networking for the chosen VM sizes/zones.
  Preinstall Docker, Azure CLI, jq, curl, OpenSSL and cloud-init. No application
  secrets or registry tokens in the image. The template does not download and
  execute an unpinned installer during VM provisioning.

OS disks and NICs use `Detach`, not automatic deletion. Agent UUID, journals,
packages and local logs live on `/var/lib/kardia/agent` on that retained disk.
Snapshot/restore it according to the data policy; a newly provisioned VM is a
new worker unless the old worker's disk/identity is deliberately restored.
Never run two processes with the same UUID or restore a disk while its previous
owner can still dispatch. Place disposable workers and durable data on
independent failure domains. Container isolation here is not a sandbox for
untrusted workflow authors: task processes inherit access to their VM identity.
Grant each worker identity only its required `Agent` role and workflow secrets.

## Authentication, TLS and versioned configuration

Register separate Entra API and SPA applications. Configure the API's delegated
scope, SPA redirect URI for the production HTTPS hostname, tenant restrictions
and group-role bindings. Configure **v2 access tokens** and the `idtyp` optional
access-token claim. Define application role `Agent` and assign it to every
worker VM's managed-identity service principal after provisioning. Worker
tokens require `roles: Agent`, `idtyp: app` and no delegated `scp`. Configure
registrar identities separately with `Registrar`; do not give workers that
role. Assign application `Admin` only to intended administrators.

Copy the four `*.bundle.example.json` files to a private, Git-ignored location.
Replace all placeholders and example application IDs, then store each complete
bundle as a **versioned Key Vault secret** named in
`configurationReferences`. Frontend build IDs are public identifiers, not
credentials. Never put real bundle contents, PostgreSQL passwords, HMAC keys,
certificate keys or access tokens in Bicep parameters, image layers, shell
history or committed files.

API/controller bundles use Production Entra authentication, `Storage.Provider`
`AzureBlob`, the same blob container, PostgreSQL database, and the **same
base64-encoded random HMAC key of at least 32 bytes**. Generate once with a
cryptographic RNG; retain it across ordinary releases and rotate deliberately.
Use a dedicated database application role, `SSL Mode=VerifyFull`, and the
mounted `/certs/ca.crt`. Production storage uses managed identity, never an
Azurite connection string or account key. Set the private API/controller TLS
endpoints exactly as shown; ordinary loopback development listeners remain
loopback-only. The installer sets each controller's advertised HTTPS origin
to its own VM DNS name, not a shared frontend hostname.

Server certificates must cover their private VM DNS names; frontend
certificates cover the browser hostname. Shared role bundles may use a narrowly
scoped private wildcard certificate. Keep intermediates in `certificatePem`
and optional private roots in `caPem`. The installer verifies certificate/key
matching, hostname, chain and at least one day of remaining validity.
Plan rotation before expiry. Workers receive CA trust only, **no server keys**,
no database password and no storage HMAC. Their role bundle must not contain a
shared `Agent.Id`; the installer generates and persists one UUID per VM.
Add separate narrowly scoped Key Vault permissions for the workflow-secret
aliases workers actually use; the template grants only their configuration
secret, not an entire workflow-secrets vault.

The app configuration and TLS key remain private files on the OS disk,
read-only-mounted into the container. Protect disks, backups and operator
access accordingly. Temporary Key Vault bundles and Azure CLI/Docker token
caches are removed on success or failure; retained release directories contain
the protected effective settings and certificate required by the running
process. Do not print them while diagnosing a failed install.

## Build and install

Build API/controller from `backend\src\Wf.ControlPlane\Dockerfile`, agent from
`backend\src\Wf.Agent\Dockerfile` (build context `backend`), and frontend from
`infra\azure-vm\frontend.Dockerfile` (repository-root context). Frontend requires
`VITE_ENTRA_TENANT_ID`, `VITE_ENTRA_SPA_CLIENT_ID` and `VITE_ENTRA_API_SCOPE`
build arguments, using the registered scope, for example
`api://<API-client-UUID>/access_as_user`. It always builds `VITE_AUTH_MODE=entra`
in `production`, never `ha-lab` or `local-compose`. Set
`PREVIOUS_GATEWAY_IMAGE` to the prior frontend release to retain its assets for
open browser tabs. Push approved images to ACR, record digests, and pin them
using `@sha256:...` in `images`; do not deploy `latest`.

Copy `main.example.bicepparam` to a private parameter file and replace all
example values. VM names/suffixes must be unique, valid Linux hostnames and
fit Azure's name limits. Confirm location supports the image, selected
zones/VM sizes and networking features. Compile offline first:

```powershell
bicep build .\infra\azure-vm\main.bicep
bicep build-params .\infra\azure-vm\main.example.bicepparam
```

Then an authorized operator must review an Azure ARM validation and `what-if`
against the intended subscription/resource group before deployment; offline
compilation cannot check resource existence, provider support, RBAC, quota,
DNS, routing, region/zone availability or costs. Do not deploy the example
parameter file. VM cloud-init writes `/etc/kardia/role.json` and
`/usr/local/sbin/kardia-install-role`; it intentionally does **not** start
applications before RBAC/DNS/database preparation.

Wait for role-assignment propagation, create/verify private DNS, grant each
worker identity its Entra application role, and perform one reviewed
**expand-only** database migration using the immutable control-plane image
with `--migrate` and a private, read-only-mounted Production configuration.
Use a dedicated migration identity with required schema privileges. Do not run
multiple migration processes or auto-migrate when every replica boots.

On each intended VM, after verifying `/etc/kardia/role.json`:

```sh
sudo /usr/local/sbin/kardia-install-role
```

Install controllers, APIs and workers, then frontends. The initial-only
installer authenticates through the VM's managed identity, reads the exact
secret version, logs into ACR with an ephemeral token, pulls the digest and
starts a non-root container with dropped capabilities, read-only rootfs,
private state mounts and bounded local logs. Host networking permits VM IMDS
and private peer connectivity; role NSGs protect the listeners. It refuses any
existing same-role container, including a stopped one. A `Started` message is
**not** a readiness claim. Verify container status, HTTPS trust, API readiness,
one ready controller leader, live worker registration and admin counts before
admitting real workflows. `docker --restart unless-stopped` handles ordinary
VM/container restarts, not unattended image upgrades.
The production frontend removes Caddy's low-port binary capability: port 8443
does not need it, and leaving it attached prevents execution with `--cap-drop ALL`.

## Safe updates and failure recovery

Use the lab's proven drain protocol, not an unconditional `docker compose up`
or replacement of all VMs. Freeze concurrent releases and retain old image
digests, versioned secret references, OS state and schema compatibility.

1. Apply a reviewed compatible migration once. For frontend/API, replace one
   VM at a time: withdraw it from ingress, wait for in-flight requests and
   long-lived connections to drain, then stop/remove only its exact role
   container, update its protected role profile and run the installer. Rejoin
   only after HTTPS/readiness succeeds. Keep another healthy replica and the
   previous frontend assets. A single-server websocket cannot survive process
   replacement; the browser reconnects and reloads durable state.
2. Identify controller leadership using `/healthz/leader` and inventory.
   Replace the standby first; verify it healthy. Replace the leader only with
   another healthy controller available and wait for a higher fenced leader
   epoch and recovery readiness before proceeding. Do not infer leadership
   from VM power state. Controller shutdown withdraws engine admission;
   workers reconnect and replay their durable outboxes.
3. For each worker, use an authenticated Admin request to
   `POST /api/v1/machines/<UUID>/drain` with
   `{"maxDrainSeconds":300,"reason":"<unique-release>","requireResumable":true}`.
   Retain the returned drain receipt. Poll its drain endpoint until **Released**
   for that same receipt: task counters reaching zero are not proof that child
   processes released work. Only then stop/remove the exact old container,
   update its role profile, and reinstall with the **same retained state/UUID**.
   Wait for its new idle process registration. Resume using
   `POST /api/v1/machines/<UUID>/resume` with
   `{"drainId":"<receipt-id>","reason":"<unique-release>"}`. A conflict means
   prerequisites are not satisfied; investigate, never clear the drain in SQL.
   Failures leave it drained. Repeat only after other workers remain healthy.

For a replacement VM, choose a new suffix/identity, add its DNS/cert coverage
and frontend/agent origins as appropriate, and establish readiness first.
Never destroy an old worker's state before physical drain release. To roll
back, restore compatible previous image and secret references through the
same one-at-a-time procedure, preserving agent state; never blindly revert a
schema or resume another operator's drain. Certificate or configuration
rotation is also a rollout. OS/kernel maintenance must use the same drains,
not independently reboot every role through an automatic patch schedule.

An unplanned crash expires the old controller/assignment leases. Surviving
controllers fence old ownership; eligible tasks retry on healthy workers from
their **last accepted application checkpoint**. Persist external side effects
idempotently. This is not arbitrary RAM migration, zero downtime, exactly-once
execution, or guaranteed recovery for non-retryable tasks/exhausted retry
budgets. Size capacity and retry policies against the required recovery SLO,
and test Azure VM loss, database failover, DNS/TLS/identity rotation and restore
in a nonproduction subscription before promotion.

## Validation boundary

Offline Bicep and example-parameter compilation, real production image builds,
production TLS/authentication routing checks, and isolated installer tests are
the local acceptance boundary. `test-install-role.sh` uses real jq/OpenSSL and
fake `az`/`docker` executables **inside a disposable network-disabled container**;
it refuses host execution. It verifies four role installations, credential
cleanup, stable worker identity and refusal of unsafe images/configuration,
certificate mismatch, missing state identity and live-container replacement.
Run with a disposable Linux image containing Bash, jq, curl, OpenSSL and
ShellCheck, mounting this directory read-only and setting
`KARDIA_OFFLINE_TEST=1`; do not mount the Docker socket or host `/etc`.

The local Bicep compiler reports `BCP081` for some newer Network/Storage
resource API versions whose types are not yet bundled. This does not validate
those schemas; do not suppress the warning or equate compilation with Azure
ARM validation. Live Entra, IMDS, ACR/Key Vault/Blob RBAC, Azure networking and
Azure VM failover remain **unverified without an authorized cloud deployment**.
The isolated runtime transport check uses temporary filesystem blobs and an
internal-only disposable PostgreSQL database, not Azure credentials or data
services. It verifies real Production-mode Kestrel TLS, controller readiness,
frontend TLS/API/gRPC routing and rejection of anonymous admin/worker requests;
it does not verify live Entra sign-in or managed-identity token acquisition.
The measured real local rollout/VM-loss results are in `..\ha-lab\README.md`.
