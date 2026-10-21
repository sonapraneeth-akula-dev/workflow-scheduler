#requires -Version 7.4
#requires -RunAsAdministrator
[CmdletBinding()]
param([ValidateRange(1024, 65535)][int]$Port = 5180)

$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\.wf\ha-lab'))
$state = Get-Content (Join-Path $root 'lab.json') -Raw | ConvertFrom-Json
$kube = @('--kubeconfig', (Join-Path $root 'kubeconfig'))
if (Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
    throw "Loopback port $Port is already in use; do not stop another service."
}
$PSNativeCommandUseErrorActionPreference = $false
while ($true) {
    $server = $null
    foreach ($i in 1..3) {
        $name = 'kardia-lab-{0:00}' -f $i
        $vm = Get-VM -Name $name
        if ($vm.Notes -ne "Kardia isolated HA lab: $root") { throw "VM '$name' is not owned by this lab." }
        if ($vm.State -ne 'Running') { continue }
        $address = (Get-VMNetworkAdapter -VMName $name | Where-Object SwitchName -eq 'Default Switch').IPAddresses |
            Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' -and $_ -notlike '169.254.*' } |
            Select-Object -First 1
        if (!$address) { continue }
        $candidate = @("--server=https://$address`:6443", '--tls-server-name=kardia-ha-lab')
        $health = & kubectl @kube @candidate --request-timeout=5s get --raw=/readyz 2>&1
        if ($LASTEXITCODE -eq 0) { $server = $candidate; break }
        if (($health -join "`n") -match 'Unauthorized|Forbidden|x509:|certificate signed by unknown authority') {
            throw "Lab API trust/authorization failed: $($health -join "`n")"
        }
        Write-Warning "Lab API on $name is unavailable; trying another owned server."
    }
    if (!$server) {
        Write-Warning 'No healthy lab API server; retrying in 2 seconds.'
        Start-Sleep -Seconds 2
        continue
    }
    $namespace = & kubectl @kube @server get namespace kardia -o json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $namespace.metadata.labels.'io.kardia.lab' -ne $state.Id) {
        throw 'The isolated kubeconfig does not target this lab namespace.'
    }
    & kubectl @kube @server -n kardia port-forward service/kardia-frontend "$Port`:8080" --address=127.0.0.1 2>&1 |
        ForEach-Object {
            if ("$_" -match 'Unauthorized|Forbidden|x509:|certificate signed by unknown authority') {
                throw "Lab port-forward trust/authorization failed: $_"
            }
            if ("$_" -notlike 'Handling connection for*') { Write-Output $_ }
        }
    Write-Warning "Lab port-forward ended (exit $LASTEXITCODE); reconnecting to a healthy frontend in 2 seconds."
    Start-Sleep -Seconds 2
}
