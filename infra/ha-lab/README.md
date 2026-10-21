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
