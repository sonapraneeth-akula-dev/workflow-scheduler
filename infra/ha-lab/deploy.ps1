#requires -Version 7.4
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9][a-z0-9_.-]{0,100}$')]
    [string]$ImageTag,
    [switch]$SkipBuild,
    [switch]$StageOnly
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Import-Module Hyper-V -ErrorAction Stop
. (Join-Path $PSScriptRoot 'certificate.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$root = Join-Path $repository '.wf\ha-lab'
$dataRoot = Join-Path $root 'data'
$state = Get-Content (Join-Path $root 'lab.json') -Raw | ConvertFrom-Json
if ($state.Id -notmatch '^[0-9a-f]{32}$') { throw 'Invalid lab identity.' }
$kube = @('--kubeconfig', (Join-Path $root 'kubeconfig'))
$names = @(1..3 | ForEach-Object { 'kardia-lab-{0:00}' -f $_ })
$addresses = @{}
foreach ($name in $names) {
    $vm = Get-VM -Name $name
    if ($vm.Notes -ne "Kardia isolated HA lab: $root" -or $vm.State -ne 'Running') {
        throw "VM '$name' must be owned by this lab and running."
    }
    $addresses[$name] = (Get-VMNetworkAdapter -VMName $name | Where-Object SwitchName -eq 'Default Switch').IPAddresses |
        Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' -and $_ -notlike '169.254.*' } |
        Select-Object -First 1
    if (!$addresses[$name]) { throw "No management address for '$name'; rerun start.ps1." }
}
$nodes = & kubectl @kube get nodes -o json | ConvertFrom-Json
if (@($nodes.items).Count -ne 3 -or @($nodes.items | Where-Object { $_.metadata.name -notin $names }).Count) {
    throw 'The isolated kubeconfig does not target the expected three-node lab.'
}
$dataAddress = @(Get-NetIPAddress -InterfaceAlias 'vEthernet (Default Switch)' -AddressFamily IPv4 |
    Where-Object AddressState -eq 'Preferred')
if ($dataAddress.Count -ne 1) { throw 'Expected one Default Switch host IPv4 address.' }
$hostAddress = $dataAddress[0].IPAddress
if ((Get-Content (Join-Path $dataRoot 'compose.env')) -notcontains "KARDIA_DATA_ADDRESS=$hostAddress") {
    throw 'Shared data host bindings are stale; rerun data.ps1 before deploying.'
}
$namespace = & kubectl @kube get namespace kardia --ignore-not-found -o json
if ($namespace -and (($namespace | ConvertFrom-Json).metadata.labels.'io.kardia.lab' -ne $state.Id)) {
    throw 'The kardia namespace belongs to another deployment.'
}
$deploymentPath = Join-Path $root 'deployment.json'
$previous = if (Test-Path $deploymentPath) { Get-Content $deploymentPath -Raw | ConvertFrom-Json } else { $null }
if ($previous -and $previous.ImageTag -ne $ImageTag -and !$StageOnly) {
    throw 'An installed lab requires a drain-aware release. StageOnly changes desired images without replacing controllers/workers.'
}
$imageRoot = Join-Path $root 'images'
$null = New-Item -ItemType Directory -Force -Path $imageRoot
$imageStatePath = Join-Path $imageRoot "$ImageTag.json"
if (!$SkipBuild) {
    if (Test-Path $imageStatePath) { throw 'An imported image tag is immutable. Use SkipBuild or a new tag.' }
    & docker build --quiet --tag "kardia-control-plane:$ImageTag" `
        --build-arg NUGET_SOURCE=https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json `
        --file (Join-Path $repository 'backend\src\Wf.ControlPlane\Dockerfile') $repository
    & docker build --quiet --tag "kardia-agent:$ImageTag" `
        --file (Join-Path $repository 'backend\src\Wf.Agent\Dockerfile') (Join-Path $repository 'backend')
    $previousGateway = if ($previous) { "kardia-gateway:$($previous.ImageTag)" } else { 'caddy:2.11.7-alpine' }
    & docker build --quiet --tag "kardia-gateway:$ImageTag" --build-arg LOCAL_BUILD_MODE=ha-lab `
        --build-arg "PREVIOUS_GATEWAY_IMAGE=$previousGateway" `
        --file (Join-Path $repository 'infra\local\gateway.Dockerfile') $repository
}
$images = @("kardia-control-plane:$ImageTag", "kardia-agent:$ImageTag", "kardia-gateway:$ImageTag")
$imageIds = @($images | ForEach-Object { & docker image inspect --format '{{.Id}}' $_ })
if (Test-Path $imageStatePath) {
    $storedIds = @(Get-Content $imageStatePath -Raw | ConvertFrom-Json)
    if (($storedIds -join ',') -ne ($imageIds -join ',')) { throw 'An imported tag changed locally. Choose a new immutable image tag.' }
} else {
    $imageIds | ConvertTo-Json | Set-Content $imageStatePath -Encoding utf8NoBOM
}
$archive = Join-Path $imageRoot "$ImageTag.tar"
if (!(Test-Path $archive)) { & docker image save --platform linux/amd64 --output $archive @images }
$sshArgs = @('-i', (Join-Path $root 'id_ed25519'), '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=5',
    '-o', 'ServerAliveInterval=5', '-o', 'ServerAliveCountMax=3', '-o', 'StrictHostKeyChecking=yes',
    '-o', "UserKnownHostsFile=$(Join-Path $root 'known_hosts')")
foreach ($name in $names) {
    & scp @sshArgs -o "HostKeyAlias=$name" $archive "kardia@$($addresses[$name]):/home/kardia/kardia-images.tar"
    & ssh @sshArgs -o "HostKeyAlias=$name" "kardia@$($addresses[$name])" `
        'sudo k3s ctr images import --no-unpack /home/kardia/kardia-images.tar && rm /home/kardia/kardia-images.tar'
}

$caPath = Join-Path $dataRoot 'certs\ca.crt'
$appTls = Join-Path $root 'application-tls'
$null = New-Item -ItemType Directory -Force -Path $appTls
$certPath = Join-Path $appTls 'server.crt'
$keyPath = Join-Path $appTls 'server.key'
$renew = !(Test-Path $certPath) -or !(Test-Path $keyPath)
if (!$renew) {
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile($certPath, $keyPath)
    try { $renew = $certificate.NotAfter.ToUniversalTime() -lt [datetime]::UtcNow.AddDays(7) }
    finally { $certificate.Dispose() }
}
if ($renew) {
    $ca = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile(
        $caPath, (Join-Path $dataRoot 'identity\ca.key'))
    try {
        if ($ca.NotAfter.ToUniversalTime() -lt [datetime]::UtcNow.AddDays(31)) { throw 'Lab CA expires soon; rotate trust explicitly.' }
        $dns = @('kardia-api', 'kardia-api.kardia', 'kardia-api.kardia.svc', 'kardia-api.kardia.svc.cluster.local',
            'kardia-controller-0.kardia-controller.kardia.svc.cluster.local',
            'kardia-controller-1.kardia-controller.kardia.svc.cluster.local')
        New-LabServerCertificate $ca $certPath $keyPath $dns `
            @([Net.IPAddress]::Parse('192.168.247.11'), [Net.IPAddress]::Parse('192.168.247.12'), [Net.IPAddress]::Parse('192.168.247.13'))
    } finally { $ca.Dispose() }
}
@{
    apiVersion = 'v1'; kind = 'Namespace'
    metadata = @{ name = 'kardia'; labels = @{ 'io.kardia.lab' = $state.Id; 'app.kubernetes.io/part-of' = 'kardia' } }
} | ConvertTo-Json -Depth 5 | & kubectl @kube apply -f -
& kubectl @kube -n kardia create configmap kardia-runtime "--from-env-file=$(Join-Path $PSScriptRoot 'runtime.env')" `
    --dry-run=client -o json | & kubectl @kube apply -f -
& kubectl @kube -n kardia create configmap kardia-lab-ca "--from-file=ca.crt=$caPath" `
    --dry-run=client -o json | & kubectl @kube apply -f -
& kubectl @kube -n kardia create configmap kardia-frontend "--from-file=Caddyfile=$(Join-Path $PSScriptRoot 'Caddyfile.app')" `
    --dry-run=client -o json | & kubectl @kube apply -f -
& kubectl @kube -n kardia create secret generic kardia-application `
    "--from-env-file=$(Join-Path $dataRoot 'application.env')" --dry-run=client -o json | & kubectl @kube apply -f -
& kubectl @kube -n kardia create secret tls kardia-service-tls "--cert=$certPath" "--key=$keyPath" `
    --dry-run=client -o json | & kubectl @kube apply -f -

$migrationName = "kardia-migrate-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
@{
    apiVersion = 'batch/v1'; kind = 'Job'
    metadata = @{ name = $migrationName; namespace = 'kardia' }
    spec = @{
        backoffLimit = 0; activeDeadlineSeconds = 300; ttlSecondsAfterFinished = 86400
        template = @{ spec = @{
            restartPolicy = 'Never'; hostNetwork = $true; dnsPolicy = 'ClusterFirstWithHostNet'
            automountServiceAccountToken = $false
            hostAliases = @(@{ ip = $hostAddress; hostnames = @('kardia-lab-data') })
            containers = @(@{
                name = 'kardia-migrate'; image = "kardia-control-plane:$ImageTag"; imagePullPolicy = 'Never'
                args = @('--migrate')
                envFrom = @(@{ configMapRef = @{ name = 'kardia-runtime' } }, @{ secretRef = @{ name = 'kardia-application' } })
                env = @(@{ name = 'Lan__Address'; valueFrom = @{ fieldRef = @{ fieldPath = 'status.hostIP' } } })
                securityContext = @{ runAsNonRoot = $true; runAsUser = 1654; allowPrivilegeEscalation = $false; capabilities = @{ drop = @('ALL') } }
                resources = @{ requests = @{ cpu = '100m'; memory = '128Mi' }; limits = @{ cpu = '1'; memory = '512Mi' } }
                volumeMounts = @(@{ name = 'tls'; mountPath = '/tls'; readOnly = $true }, @{ name = 'ca'; mountPath = '/certs'; readOnly = $true })
            })
            volumes = @(@{ name = 'tls'; secret = @{ secretName = 'kardia-service-tls' } }, @{ name = 'ca'; configMap = @{ name = 'kardia-lab-ca' } })
        } }
    }
} | ConvertTo-Json -Depth 20 | & kubectl @kube apply -f -
try {
    $deadline = [datetime]::UtcNow.AddSeconds(330)
    do {
        $job = & kubectl @kube -n kardia get "job/$migrationName" -o json | ConvertFrom-Json
        if ($job.status.failed -gt 0) { throw "Migration job '$migrationName' failed." }
        if ($job.status.succeeded -eq 1) { break }
        $pods = & kubectl @kube -n kardia get pods -l "job-name=$migrationName" -o json | ConvertFrom-Json
        $blocked = @($pods.items.status.containerStatuses.state.waiting |
            Where-Object reason -in @('CreateContainerConfigError', 'ErrImageNeverPull', 'InvalidImageName'))
        if ($blocked.Count) { throw "Migration container blocked: $($blocked[0].message)" }
        Start-Sleep -Seconds 2
    } while ([datetime]::UtcNow -lt $deadline)
    if ($job.status.succeeded -ne 1) { throw "Migration job '$migrationName' did not complete before the deadline." }
} catch {
    if ($job.status.failed -gt 0) { & kubectl @kube -n kardia logs "job/$migrationName" --tail=80 }
    throw
}
$manifest = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'application.yaml')).
    Replace('{{IMAGE_TAG}}', $ImageTag).Replace('{{DATA_ADDRESS}}', $hostAddress)
$manifest | & kubectl @kube apply -f -
& kubectl @kube -n kardia rollout status deployment/kardia-api --timeout=300s
& kubectl @kube -n kardia rollout status deployment/kardia-frontend --timeout=300s
if ($StageOnly) {
    Write-Host 'Desired images staged; controllers and workers use OnDelete and have NOT been replaced.'
    return
}
& kubectl @kube -n kardia wait --for=condition=ready pod -l app=kardia-controller --timeout=300s
& kubectl @kube -n kardia wait --for=condition=ready pod -l app=kardia-worker --timeout=300s
$workerPods = & kubectl @kube -n kardia get pods -l app=kardia-worker -o json | ConvertFrom-Json
$controllerPods = & kubectl @kube -n kardia get pods -l app=kardia-controller -o json | ConvertFrom-Json
if (@($workerPods.items).Count -ne 3 -or
    @($workerPods.items.spec.containers | Where-Object image -ne "kardia-agent:$ImageTag").Count -or
    @($controllerPods.items).Count -ne 2 -or
    @($controllerPods.items.spec.containers | Where-Object image -ne "kardia-control-plane:$ImageTag").Count) {
    throw 'OnDelete workloads still run another image; use a drain-aware release instead of claiming deployment complete.'
}
$deadline = [datetime]::UtcNow.AddMinutes(2)
do {
    $snapshot = & kubectl @kube -n kardia exec deployment/kardia-api -- `
        curl --fail --silent --show-error --max-time 5 http://127.0.0.1:8080/api/v1/admin/system | ConvertFrom-Json
    $healthy = @($snapshot.instances | Where-Object status -eq 'Healthy')
    $workers = @($snapshot.workers | Where-Object { $_.pool -eq 'kardia-lab' -and $_.status -eq 'Healthy' })
    $ready = @($healthy | Where-Object role -eq 'Api').Count -eq 2 -and
        @($healthy | Where-Object role -eq 'ApiController').Count -eq 2 -and
        @($healthy | Where-Object isLeader).Count -eq 1 -and
        $workers.Count -eq 3 -and @($workers | Where-Object name -notin $names).Count -eq 0
    if ($ready) { break }
    Start-Sleep -Seconds 3
} while ([datetime]::UtcNow -lt $deadline)
if (!$ready) { throw 'Expected two APIs, two controllers with one leader, and three connected workers in the live inventory.' }
@{ ImageTag = $ImageTag; LabId = $state.Id; DeployedAt = [datetimeoffset]::UtcNow } |
    ConvertTo-Json | Set-Content $deploymentPath -Encoding utf8NoBOM
Write-Host "Kardia lab ready: 2 APIs, 2 controllers (one leader), 3 connected workers, 2 frontends; image $ImageTag."
Write-Host 'No external ingress created. Use a loopback-only kubectl port-forward for browser access.'
