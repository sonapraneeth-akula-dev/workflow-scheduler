#requires -Version 7.4
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9][a-z0-9_.-]{0,100}$')]
    [string]$ImageTag,
    [ValidateRange(1, 86400)]
    [int]$MaxDrainSeconds = 300
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Import-Module Hyper-V -ErrorAction Stop
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$root = Join-Path $repository '.wf\ha-lab'
$state = Get-Content (Join-Path $root 'lab.json') -Raw | ConvertFrom-Json
if ($state.Id -notmatch '^[0-9a-f]{32}$') { throw 'Invalid lab identity.' }
$kube = @('--kubeconfig', (Join-Path $root 'kubeconfig'), '--request-timeout=30s')
$names = @(1..3 | ForEach-Object { 'kardia-lab-{0:00}' -f $_ })
foreach ($name in $names) {
    $vm = Get-VM -Name $name
    if ($vm.Notes -ne "Kardia isolated HA lab: $root" -or $vm.State -ne 'Running') {
        throw "VM '$name' must be owned by this lab and running."
    }
}
$nodes = & kubectl @kube get nodes -o json | ConvertFrom-Json
if (@($nodes.items).Count -ne 3 -or @($nodes.items | Where-Object { $_.metadata.name -notin $names }).Count) {
    throw 'The isolated kubeconfig does not target the expected three-node lab.'
}
$namespace = & kubectl @kube get namespace kardia -o json | ConvertFrom-Json
if ($namespace.metadata.labels.'io.kardia.lab' -ne $state.Id) { throw 'Foreign kardia namespace.' }
$deploymentPath = Join-Path $root 'deployment.json'
$previous = Get-Content $deploymentPath -Raw | ConvertFrom-Json
if ($previous.LabId -ne $state.Id) { throw 'Foreign deployment record.' }
if (!(Test-Path (Join-Path $root "images\$ImageTag.json"))) { throw 'Stage this immutable image tag with deploy.ps1 -StageOnly first.' }
$lock = [IO.File]::Open((Join-Path $root 'release.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    function Get-LabPods([string]$Role) {
        (& kubectl @kube -n kardia get pods -l "app=kardia-$Role" -o json | ConvertFrom-Json).items
    }
    function Test-PodReady($Pod) {
        !$Pod.metadata.deletionTimestamp -and
            @($Pod.status.conditions | Where-Object { $_.type -eq 'Ready' -and $_.status -eq 'True' }).Count -eq 1
    }
    function Wait-LabState([string]$Message, [scriptblock]$Condition, [int]$Seconds = 180) {
        $deadline = [datetime]::UtcNow.AddSeconds($Seconds)
        do {
            if (& $Condition) { return }
            Start-Sleep -Seconds 2
        } while ([datetime]::UtcNow -lt $deadline)
        throw "Timed out: $Message. No further pods will be replaced; drained workers remain unavailable."
    }
    function Invoke-LabApi([string]$Path, [string]$Body, [int[]]$Expected = @(200)) {
        $curl = @('curl', '--silent', '--show-error', '--max-time', '10', '--write-out', '\n%{http_code}')
        if ($Body) { $curl += @('--request', 'POST', '--header', 'Content-Type: application/json', '--data-binary', '@-') }
        $curl += "http://127.0.0.1:8080/api/v1/$Path"
        $output = if ($Body) {
            $Body | & kubectl @kube -n kardia exec -i deployment/kardia-api -- @curl
        } else { & kubectl @kube -n kardia exec deployment/kardia-api -- @curl }
        $text = $output -join "`n"
        if ($text -notmatch '(?s)^(.*)\n(\d{3})$') { throw "Malformed API response for '$Path'." }
        $status = [int]$Matches[2]
        $payload = $Matches[1]
        if ($status -notin $Expected) { throw "API '$Path' returned HTTP ${status}: $payload" }
        [pscustomobject]@{ Status = $status; Value = if ($payload) { $payload | ConvertFrom-Json } else { $null } }
    }
    function Get-LabSnapshot { (Invoke-LabApi 'admin/system').Value }
    function Get-LabControllers($Snapshot) {
        @($Snapshot.instances | Where-Object {
            $_.role -eq 'ApiController' -and $_.status -eq 'Healthy' -and
            $_.podName -in @('kardia-controller-0', 'kardia-controller-1')
        })
    }
    function Replace-LabPod($Pod, [string]$Role, [string]$Image) {
        $podName = $Pod.metadata.name
        $uid = $Pod.metadata.uid
        $node = $Pod.spec.nodeName
        Write-Host "Replacing $podName on $node."
        @{
            apiVersion = 'v1'; kind = 'DeleteOptions'
            preconditions = @{ uid = $uid }
        } | ConvertTo-Json -Depth 3 |
            & kubectl @kube delete --raw "/api/v1/namespaces/kardia/pods/$podName" -f - | Out-Null
        Wait-LabState "new ready $Role replacing $podName" {
            $pods = @(Get-LabPods $Role | Where-Object {
                if ($Role -eq 'worker') { $_.spec.nodeName -eq $node }
                else { $_.metadata.name -eq $podName -and $_.spec.nodeName -in $names }
            })
            $pods.Count -eq 1 -and $pods[0].metadata.uid -ne $uid -and
                $pods[0].spec.containers[0].image -eq $Image -and (Test-PodReady $pods[0])
        } 300
    }
    $expected = @{
        api = @{ Kind = 'deployment'; Count = 2; Image = "kardia-control-plane:$ImageTag" }
        frontend = @{ Kind = 'deployment'; Count = 2; Image = "kardia-gateway:$ImageTag" }
        controller = @{ Kind = 'statefulset'; Count = 2; Image = "kardia-control-plane:$ImageTag" }
        worker = @{ Kind = 'daemonset'; Count = 3; Image = "kardia-agent:$ImageTag" }
    }
    foreach ($role in $expected.Keys) {
        $target = $expected[$role]
        $workload = & kubectl @kube -n kardia get "$($target.Kind)/kardia-$role" -o json | ConvertFrom-Json
        if ($workload.spec.template.spec.containers[0].image -ne $target.Image -or
            @($workload.spec.template.spec.initContainers | Where-Object { $_ -and $_.image -ne $target.Image }).Count) {
            throw "Desired $role image differs from '$ImageTag'; stage the full release first."
        }
        if ($role -in @('controller', 'worker') -and $workload.spec.updateStrategy.type -ne 'OnDelete') {
            throw "$role must use OnDelete; automatic replacement is unsafe."
        }
        Wait-LabState "$($target.Count) ready owned $role pods before release" {
            $pods = @(Get-LabPods $role)
            $pods.Count -eq $target.Count -and @($pods | Where-Object {
                $_.spec.nodeName -notin $names -or !(Test-PodReady $_) -or
                ($role -in @('api', 'frontend') -and $_.spec.containers[0].image -ne $target.Image)
            }).Count -eq 0
        }
    }
    # Standby first; the observed database lease, not pod readiness, identifies the leader.
    while ($true) {
        $pods = @(Get-LabPods 'controller')
        $pending = @($pods | Where-Object { $_.spec.containers[0].image -ne $expected.controller.Image })
        if (!$pending.Count) { break }
        $snapshot = Get-LabSnapshot
        $controllers = @(Get-LabControllers $snapshot)
        if ($controllers.Count -ne 2 -or @($controllers | Where-Object isLeader).Count -ne 1) {
            throw 'Require two healthy observed controllers and one live lease leader before replacement.'
        }
        $candidate = $controllers | Where-Object podName -in $pending.metadata.name |
            Sort-Object isLeader | Select-Object -First 1
        $pod = $pods | Where-Object { $_.metadata.name -eq $candidate.podName }
        Replace-LabPod $pod 'controller' $expected.controller.Image
        Wait-LabState 'controller re-registration and a live lease leader' {
            $current = @(Get-LabControllers (Get-LabSnapshot))
            $current.Count -eq 2 -and @($current | Where-Object isLeader).Count -eq 1 -and
                @($current | Where-Object { $_.podName -eq $candidate.podName -and $_.id -ne $candidate.id }).Count -eq 1
        }
    }
    $reason = "HA lab rollout $ImageTag"
    foreach ($name in $names) {
        $pods = @(Get-LabPods 'worker' | Where-Object { $_.spec.nodeName -eq $name })
        if ($pods.Count -ne 1 -or !(Test-PodReady $pods[0])) { throw "Expected one ready worker on $name." }
        $workers = @((Get-LabSnapshot).workers | Where-Object { $_.name -eq $name -and $_.pool -eq 'kardia-lab' })
        if ($workers.Count -ne 1) { throw "Expected one registered worker for $name." }
        $worker = $workers[0]
        if ($pods[0].spec.containers[0].image -eq $expected.worker.Image -and $worker.status -eq 'Healthy') { continue }
        $path = "machines/$($worker.id)"
        $existing = Invoke-LabApi "$path/drain" -Expected @(200, 404)
        if ($existing.Status -eq 200 -and $existing.Value.reason -ne $reason) {
            throw "Worker $name has another operator's drain; it will not be resumed or replaced."
        }
        $receipt = if ($existing.Status -eq 200) { $existing.Value } else {
            Write-Host "Draining $name; allowing up to $MaxDrainSeconds seconds for current work."
            (Invoke-LabApi "$path/drain" (@{
                maxDrainSeconds = $MaxDrainSeconds; reason = $reason; requireResumable = $true
            } | ConvertTo-Json -Compress) @(202)).Value
        }
        Wait-LabState "physical drain release for $name" {
            $current = (Invoke-LabApi "$path/drain").Value
            if ($current.id -ne $receipt.id) { throw "Drain identity changed for $name." }
            $current.status -eq 'Released'
        } ($MaxDrainSeconds + 120)
        Replace-LabPod $pods[0] 'worker' $expected.worker.Image
        $resume = @{ drainId = $receipt.id; reason = $reason } | ConvertTo-Json -Compress
        Wait-LabState "fresh idle process and audited resume for $name" {
            (Invoke-LabApi "$path/resume" $resume @(204, 409)).Status -eq 204
        }
        Wait-LabState "healthy dispatchable worker $name" {
            @((Get-LabSnapshot).workers | Where-Object { $_.id -eq $worker.id -and $_.status -eq 'Healthy' }).Count -eq 1
        }
        Write-Host "Worker $name resumed with its original identity."
    }
    Wait-LabState 'complete release runtime inventory' {
        $snapshot = Get-LabSnapshot
        $controllers = @(Get-LabControllers $snapshot)
        @($snapshot.instances | Where-Object { $_.role -eq 'Api' -and $_.status -eq 'Healthy' }).Count -eq 2 -and
            $controllers.Count -eq 2 -and @($controllers | Where-Object isLeader).Count -eq 1 -and
            @($snapshot.workers | Where-Object { $_.pool -eq 'kardia-lab' -and $_.name -in $names -and $_.status -eq 'Healthy' }).Count -eq 3
    }
    foreach ($role in $expected.Keys) {
        $pods = @(Get-LabPods $role)
        if ($pods.Count -ne $expected[$role].Count -or @($pods | Where-Object {
            !(Test-PodReady $_) -or $_.spec.containers[0].image -ne $expected[$role].Image
        }).Count) { throw "Actual $role pods do not match the completed release." }
    }
    $record = @{ ImageTag = $ImageTag; LabId = $state.Id; DeployedAt = [datetimeoffset]::UtcNow } | ConvertTo-Json
    $temporary = Join-Path $root "deployment.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, $record)
        [IO.File]::Move($temporary, $deploymentPath, $true)
    } finally { if (Test-Path $temporary) { Remove-Item $temporary } }
    Write-Host "Release $ImageTag complete: all roles healthy; each replaced worker physically drained, restarted and explicitly resumed."
} finally { $lock.Dispose() }
