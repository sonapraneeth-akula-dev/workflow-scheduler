#requires -Version 7.0
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateSet('Up', 'Status', 'Stop')]
    [string]$Action = 'Up'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Import-Module Hyper-V -ErrorAction Stop
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\.wf\ha-lab'))
$marker = "Kardia isolated HA lab: $root"
$names = 1..3 | ForEach-Object { 'kardia-lab-{0:00}' -f $_ }
$privateSwitchName = 'kardia-ha-lab-private'
$clusterAddresses = @{}
for ($i = 0; $i -lt $names.Count; $i++) { $clusterAddresses[$names[$i]] = "192.168.247.$(11 + $i)" }
$statePath = Join-Path $root 'lab.json'
$toolImage = 'kardia-ha-lab-tools:1'
$sshArgs = @('-i', (Join-Path $root 'id_ed25519'), '-o', 'BatchMode=yes',
    '-o', 'ConnectTimeout=5', '-o', 'ServerAliveInterval=5', '-o', 'ServerAliveCountMax=3',
    '-o', 'StrictHostKeyChecking=accept-new',
    '-o', "UserKnownHostsFile=$(Join-Path $root 'known_hosts')")

function Get-OwnedVm([string]$Name) {
    $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
    if ($vm -and $vm.Notes -ne $marker) {
        throw "VM '$Name' belongs to another lab. No changes made to it."
    }
    return $vm
}

function Get-LabAddress([string]$Name) {
    $null = Get-OwnedVm $Name
    $adapter = Get-VMNetworkAdapter -VMName $Name | Where-Object SwitchName -eq 'Default Switch'
    $address = $adapter |
        ForEach-Object { $_.IPAddresses } |
        Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' -and $_ -notlike '169.254.*' } |
        Select-Object -First 1
    if ($address) { return $address }
    return Get-NetNeighbor -InterfaceAlias 'vEthernet (Default Switch)' -AddressFamily IPv4 |
        Where-Object { $_.LinkLayerAddress.Replace('-', '') -eq $adapter.MacAddress } |
        Select-Object -ExpandProperty IPAddress -First 1
}

function Invoke-LabSsh([string]$Name, [string]$Address, [string]$Command) {
    & ssh @sshArgs -o "HostKeyAlias=$Name" "kardia@$Address" $Command
}

function Wait-LabSsh([string]$Name, [switch]$NetworkReady) {
    $PSNativeCommandUseErrorActionPreference = $false
    $command = if ($NetworkReady) { 'test -f /run/kardia-lab-network-ready' } else {
        'test -f /var/lib/cloud/instance/boot-finished'
    }
    $deadline = [datetime]::UtcNow.AddMinutes(10)
    while ([datetime]::UtcNow -lt $deadline) {
        $address = Get-LabAddress $Name
        if ($address) {
            $output = & ssh @sshArgs -o "HostKeyAlias=$Name" "kardia@$address" $command 2>&1
            if (($output -join "`n") -match 'REMOTE HOST IDENTIFICATION HAS CHANGED|Host key verification failed|Permission denied|UNPROTECTED PRIVATE KEY FILE|Failed to add the host to the list of known hosts') {
                throw "SSH trust/authentication failed for ${Name}: $($output -join "`n")"
            }
            if ($LASTEXITCODE -eq 0) { return $address }
        }
        Start-Sleep -Seconds 5
    }
    throw "VM '$Name' did not finish cloud-init/network setup within 10 minutes. Inspect its console; do not replace its disk."
}

foreach ($name in $names) { $null = Get-OwnedVm $name }
$privateSwitch = Get-VMSwitch -Name $privateSwitchName -ErrorAction SilentlyContinue
if ($privateSwitch -and ($privateSwitch.Notes -ne $marker -or $privateSwitch.SwitchType -ne 'Private')) {
    throw "Switch '$privateSwitchName' is not this lab's private switch."
}
if ($Action -ne 'Up') {
    foreach ($name in $names) {
        $vm = Get-OwnedVm $name
        if ($vm) {
            if ($Action -eq 'Stop' -and $vm.State -eq 'Running') {
                Stop-VM -Name $name -Confirm:$false
            }
            [pscustomobject]@{
                Name = $name
                State = (Get-OwnedVm $name).State
                Address = Get-LabAddress $name
            }
        }
    }
    return
}

$null = Get-Command docker, ssh, scp, ssh-keygen, kubectl -ErrorAction Stop
$null = Get-VMSwitch -Name 'Default Switch' -ErrorAction Stop
$offCount = @($names | Where-Object { (Get-OwnedVm $_).State -ne 'Running' }).Count
$freeBytes = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1KB
if ($freeBytes -lt (4GB + $offCount * 3GB)) {
    throw "Insufficient free RAM: need 3 GiB per stopped/new VM plus 4 GiB host reserve."
}

if (!(Test-Path $root)) { $null = New-Item -ItemType Directory -Path $root }
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18', 'S-1-5-32-544')) {
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new($sid), 'FullControl',
        'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
}
Set-Acl -Path $root -AclObject $acl

if (!(Test-Path $statePath)) {
    if (@($names | Where-Object { Get-OwnedVm $_ }).Count -gt 0) {
        throw "Existing VMs have no lab state. Restore $statePath; never regenerate their identity."
    }
    @{
        Id = [guid]::NewGuid().ToString('N')
        UbuntuBuild = '20261017'
        K3sVersion = 'v1.36.5+k3s1'
        Token = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    } | ConvertTo-Json | Set-Content $statePath -Encoding utf8NoBOM
}
$state = Get-Content $statePath -Raw | ConvertFrom-Json
if ($state.Id -notmatch '^[0-9a-f]{32}$' -or $state.Token -notmatch '^[0-9A-F]{64}$' -or
    $state.UbuntuBuild -notmatch '^\d{8}$' -or $state.K3sVersion -notmatch '^v\d+\.\d+\.\d+\+k3s\d+$') {
    throw "Invalid lab identity/version state in $statePath."
}
$keyPath = Join-Path $root 'id_ed25519'
if (!(Test-Path $keyPath)) {
    if (@($names | Where-Object { Get-OwnedVm $_ }).Count -gt 0) {
        throw "Existing VMs have no SSH private key. Restore $keyPath."
    }
    & ssh-keygen -q -t ed25519 -N '' -C "kardia-ha-lab-$($state.Id)" -f $keyPath
}
$publicKey = (Get-Content "$keyPath.pub" -Raw).Trim()
if (!$privateSwitch) {
    $null = New-VMSwitch -Name $privateSwitchName -SwitchType Private -Notes $marker
}

& docker build --quiet --tag $toolImage --file (Join-Path $PSScriptRoot 'tools.Dockerfile') $PSScriptRoot
& docker run --rm --memory 768m --cpus 2 --mount "type=bind,source=$root,target=/work" `
    --mount "type=bind,source=$PSScriptRoot,target=/scripts,readonly" $toolImage `
    sh /scripts/prepare-image.sh $state.UbuntuBuild $state.K3sVersion

foreach ($name in $names) {
    $vm = Get-OwnedVm $name
    if (!$vm) {
        $nodeRoot = Join-Path $root $name
        $null = New-Item -ItemType Directory -Path $nodeRoot -Force
        $disk = Join-Path $nodeRoot 'os.vhdx'
        if (!(Test-Path $disk)) {
            Copy-Item (Join-Path $root 'ubuntu-base.vhdx') $disk
            Resize-VHD -Path $disk -SizeBytes 40GB
        }
        @"
instance-id: $($state.Id)-$name
local-hostname: $name
"@ | Set-Content (Join-Path $nodeRoot 'meta-data') -Encoding utf8NoBOM
        @"
#cloud-config
hostname: $name
manage_etc_hosts: true
ssh_pwauth: false
disable_root: true
packages:
  - linux-cloud-tools-virtual
users:
  - name: kardia
    lock_passwd: true
    shell: /bin/bash
    sudo: ALL=(ALL) NOPASSWD:ALL
    ssh_authorized_keys:
      - $publicKey
write_files:
  - path: /etc/modules-load.d/kardia-k3s.conf
    content: |
      overlay
      br_netfilter
  - path: /etc/sysctl.d/90-kardia-k3s.conf
    content: |
      net.ipv4.ip_forward=1
      net.bridge.bridge-nf-call-iptables=1
      net.bridge.bridge-nf-call-ip6tables=1
runcmd:
  - [swapoff, -a]
  - [modprobe, overlay]
  - [modprobe, br_netfilter]
  - [sysctl, --system]
  - [udevadm, trigger, '--action=add', '--subsystem-match=misc']
  - [udevadm, settle]
  - [systemctl, enable, --now, hv-kvp-daemon]
"@ | Set-Content (Join-Path $nodeRoot 'user-data') -Encoding utf8NoBOM
        & docker run --rm --memory 128m --mount "type=bind,source=$nodeRoot,target=/work" `
            $toolImage genisoimage -quiet -output seed.iso -volid cidata -joliet -rock user-data meta-data
        $vm = New-VM -Name $name -Generation 2 -MemoryStartupBytes 3GB -VHDPath $disk `
            -Path $nodeRoot -SwitchName 'Default Switch'
        Set-VM -VM $vm -Notes $marker -AutomaticStartAction Nothing -AutomaticStopAction ShutDown `
            -AutomaticCheckpointsEnabled $false
        Set-VMProcessor -VM $vm -Count 2
        Set-VMMemory -VM $vm -DynamicMemoryEnabled $false
        $null = Add-VMDvdDrive -VM $vm -Path (Join-Path $nodeRoot 'seed.iso')
        Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftUEFICertificateAuthority `
            -FirstBootDevice (Get-VMHardDiskDrive -VM $vm)
    }
    Set-VM -VM $vm -AutomaticCheckpointsEnabled $false
    if ($vm.State -ne 'Running') { Start-VM -VM $vm }
}

$addresses = @{}
foreach ($name in $names) {
    Write-Host "Waiting for $name..."
    $addresses[$name] = Wait-LabSsh $name
    Invoke-LabSsh $name $addresses[$name] 'sudo cloud-init status --wait'
    $clusterMac = '02' + $state.Id.Substring(0, 8) + ('{0:X2}' -f ([Array]::IndexOf($names, $name) + 1))
    $clusterAdapter = Get-VMNetworkAdapter -VMName $name -Name 'kardia-cluster' -ErrorAction SilentlyContinue
    if (!$clusterAdapter) {
        $clusterAdapter = Add-VMNetworkAdapter -VMName $name -Name 'kardia-cluster' `
            -SwitchName $privateSwitchName -StaticMacAddress $clusterMac -Passthru
    }
    if ($clusterAdapter.SwitchName -ne $privateSwitchName -or $clusterAdapter.MacAddress -ne $clusterMac) {
        throw "Unexpected cluster adapter identity on $name."
    }
    $uplink = Get-VMNetworkAdapter -VMName $name | Where-Object SwitchName -eq 'Default Switch'
    $uplinkMac = $uplink.MacAddress -replace '(..)(?!$)', '$1:'
    $privateMac = $clusterMac -replace '(..)(?!$)', '$1:'
    & scp @sshArgs -o "HostKeyAlias=$name" (Join-Path $PSScriptRoot 'configure-network.sh') `
        "kardia@$($addresses[$name]):"
    Invoke-LabSsh $name $addresses[$name] `
        "sudo sh configure-network.sh $($clusterAddresses[$name]) $uplinkMac $privateMac"
    Start-Sleep -Seconds 3
    $addresses[$name] = Wait-LabSsh $name -NetworkReady
}

foreach ($name in $names) {
    $address = $addresses[$name]
    $version = Invoke-LabSsh $name $address 'if systemctl cat k3s.service >/dev/null 2>&1; then sudo k3s --version | head -1; fi'
    if ($version) {
        if ($version -notlike "k3s version $($state.K3sVersion) *") {
            throw "Unexpected K3s version on $name. Up does not upgrade existing clusters."
        }
        Invoke-LabSsh $name $address 'sudo systemctl start k3s'
    } else {
        $artifactRoot = Join-Path $root "downloads\$($state.K3sVersion)"
        & scp @sshArgs -o "HostKeyAlias=$name" (Join-Path $artifactRoot 'k3s') (Join-Path $artifactRoot 'install.sh') "kardia@${address}:"
        $join = if ($name -eq $names[0]) { 'cluster-init: true' } else {
            "server: https://$($clusterAddresses[$names[0]]):6443"
        }
        @"
token: $($state.Token)
node-name: $name
node-ip: $($clusterAddresses[$name])
advertise-address: $($clusterAddresses[$name])
flannel-iface: kardia0
tls-san:
  - kardia-ha-lab
write-kubeconfig-mode: '0600'
secrets-encryption: true
disable:
  - traefik
  - servicelb
node-label:
  - kardia.io/environment=ha-lab
$join
"@ | & ssh @sshArgs -o "HostKeyAlias=$name" "kardia@$address" `
            'sudo install -d -m 700 /etc/rancher/k3s; sudo sh -c "umask 077; cat > /etc/rancher/k3s/config.yaml"'
        Invoke-LabSsh $name $address `
            'sudo install -m 755 k3s /usr/local/bin/k3s && sudo env INSTALL_K3S_SKIP_DOWNLOAD=true sh install.sh server && rm k3s install.sh'
    }
    Invoke-LabSsh $name $address 'sudo k3s kubectl wait --for=condition=Ready nodes --all --timeout=180s'
}

$kubeconfig = Join-Path $root 'kubeconfig'
$config = Invoke-LabSsh $names[0] $addresses[$names[0]] 'sudo cat /etc/rancher/k3s/k3s.yaml'
($config -join "`n").Replace('https://127.0.0.1:6443', "https://$($addresses[$names[0]]):6443`n    tls-server-name: kardia-ha-lab").
    Replace('name: default', 'name: kardia-ha-lab').
    Replace('cluster: default', 'cluster: kardia-ha-lab').
    Replace('user: default', 'user: kardia-ha-lab').
    Replace('current-context: default', 'current-context: kardia-ha-lab') |
    Set-Content $kubeconfig -Encoding utf8NoBOM
& kubectl --kubeconfig $kubeconfig wait --for=condition=Ready nodes --all --timeout=180s
$nodes = & kubectl --kubeconfig $kubeconfig get nodes -o json | ConvertFrom-Json
if ($nodes.items.Count -ne 3) { throw "Expected exactly three lab nodes, found $($nodes.items.Count)." }
foreach ($node in $nodes.items) {
    $internalAddress = $node.status.addresses | Where-Object type -eq 'InternalIP' |
        Select-Object -ExpandProperty address -First 1
    if (!$clusterAddresses.ContainsKey($node.metadata.name) -or $internalAddress -ne $clusterAddresses[$node.metadata.name]) {
        throw "Node '$($node.metadata.name)' does not advertise its stable lab-private address."
    }
}
& kubectl --kubeconfig $kubeconfig -n kube-system patch deployment coredns --type merge `
    --patch-file (Join-Path $PSScriptRoot 'coredns.patch.json')
& kubectl --kubeconfig $kubeconfig -n kube-system rollout status deployment/coredns --timeout=180s
& kubectl --kubeconfig $kubeconfig get nodes -o wide
Write-Host "Kubeconfig: $kubeconfig (not merged into your default context)."
