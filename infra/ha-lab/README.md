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
