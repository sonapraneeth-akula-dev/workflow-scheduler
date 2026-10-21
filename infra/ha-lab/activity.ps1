#requires -Version 7.4

function Invoke-LabActivityApi([string[]]$Kube, [string]$Path, [object]$Body) {
    $curl = @('curl', '--silent', '--show-error', '--max-time', '15', '--write-out', '\n%{http_code}')
    if ($null -ne $Body) {
        $curl += @('--request', 'POST', '--header', 'Content-Type: application/json', '--data-binary', '@-')
    }
    $curl += "http://127.0.0.1:8080/api/v1/admin/deployment-activity$Path"
    $output = if ($null -ne $Body) {
        $Body | ConvertTo-Json -Depth 5 -Compress | & kubectl @Kube -n kardia exec -i deployment/kardia-api -- @curl
    } else { & kubectl @Kube -n kardia exec deployment/kardia-api -- @curl }
    if ($LASTEXITCODE -ne 0) { throw 'Deployment activity API transport failed; no further deployment steps will run.' }
    $text = $output -join "`n"
    if ($text -notmatch '(?s)^(.*)\n(\d{3})$') { throw 'Malformed deployment activity response.' }
    $status = [int]$Matches[2]
    $payload = $Matches[1]
    if ($status -notin @(200, 404)) {
        throw "Deployment activity API returned HTTP $status; no further deployment steps will run."
    }
    [pscustomobject]@{ Status = $status; Value = if ($status -eq 200) { $payload | ConvertFrom-Json } else { $null } }
}

function Start-LabDeploymentActivity {
    param([string[]]$Kube, [ValidateSet('Stage', 'Release')][string]$Mode, [string]$Target,
        [switch]$Bootstrap, [switch]$InitialInstall)
    $activity = [pscustomobject]@{
        Kube = $Kube; Id = [guid]::NewGuid().ToString('D'); Mode = $Mode; Target = $Target
        Sequence = 0; Enabled = $false
    }
    if ($InitialInstall) {
        Write-Warning 'Initial bootstrap has no reporting API yet. Earlier build/import steps will remain unrecorded.'
        return $activity
    }
    $probe = Invoke-LabActivityApi $Kube ''
    if ($probe.Status -eq 404) {
        if (!$Bootstrap -or $Mode -ne 'Stage') {
            throw 'Deployment activity API is absent. Use deploy.ps1 -BootstrapDeploymentActivity once to install reporting.'
        }
        Write-Warning 'Reporting bootstrap: earlier build/import/migration steps will remain unrecorded.'
        return $activity
    }
    if ($Bootstrap) { throw 'Deployment activity already exists; bootstrap cannot bypass reporting.' }
    $started = Invoke-LabActivityApi $Kube '' @{
        operationId = $activity.Id; mode = $Mode; target = $Target
    }
    if ($started.Status -ne 200 -or $started.Value.id -ne $activity.Id -or
        $started.Value.sequence -ne 0 -or $started.Value.status -ne 'Running') {
        throw 'Deployment activity start receipt is inconsistent.'
    }
    $activity.Enabled = $true
    $activity
}

function Report-LabDeploymentActivity {
    param($Activity, [string]$Phase, [string]$Description, [string]$Subject,
        [ValidateSet('Running', 'Succeeded', 'Failed')][string]$Outcome = 'Running')
    if (!$Activity -or !$Activity.Enabled) { return }
    $sequence = $Activity.Sequence + 1
    $reported = Invoke-LabActivityApi $Activity.Kube "/$($Activity.Id)/events" @{
        sequence = $sequence; phase = $Phase; subject = if ($Subject) { $Subject } else { $null }
        outcome = $Outcome; description = $Description
    }
    if ($reported.Status -ne 200 -or $reported.Value.id -ne $Activity.Id -or $reported.Value.sequence -ne $sequence) {
        throw 'Deployment activity event receipt is inconsistent.'
    }
    $Activity.Sequence = $sequence
}

function Fail-LabDeploymentActivity($Activity, [string]$Mode) {
    if (!$Activity -or !$Activity.Enabled) { return }
    try {
        Report-LabDeploymentActivity $Activity -Phase Failed -Outcome Failed `
            -Description "$Mode script stopped. Inspect operator logs; observed workload state is reported separately."
    } catch {
        Write-Error "Cannot record deployment interruption: $($_.Exception.Message). The last reported state is unconfirmed." -ErrorAction Continue
    }
}
