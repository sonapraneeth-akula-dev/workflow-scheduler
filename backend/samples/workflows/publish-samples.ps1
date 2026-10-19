[CmdletBinding()]
param(
    [ValidateSet('demo', 'dev', 'test', 'prod', 'ppe')]
    [string] $Environment = 'dev',
    [uri] $Engine = 'http://127.0.0.1:5000',
    [string[]] $Team = @(),
    [switch] $RunOnce,
    [switch] $IncludeSchedules
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($env:WF_ACCESS_TOKEN)) {
    throw 'Set WF_ACCESS_TOKEN to a Registrar token before publishing. RunOnce also requires permission to trigger the workflows.'
}
$deployment = if ($Environment -in @('demo', 'dev', 'test')) { 'Demo' } else { 'Production' }
$tree = if ($deployment -eq 'Demo') { 'demo' } else { 'production' }
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$teamRoot = Join-Path $PSScriptRoot "$tree\teams"
$directories = @(Get-ChildItem -LiteralPath $teamRoot -Directory | Sort-Object Name)
if ($Team.Count -eq 0 -and $deployment -eq 'Demo') { $Team = @('data-science') }
foreach ($name in $Team) {
    if ($name -notin $directories.Name) { throw "Unknown $tree sample team: $name" }
}
if ($Team.Count -gt 0) { $directories = @($directories | Where-Object Name -In $Team) }
$workflows = @()
foreach ($directory in $directories) {
    $registry = Get-Content -LiteralPath (Join-Path $directory.FullName 'team.json') -Raw | ConvertFrom-Json
    if ($registry.deployment -cne $deployment -or $registry.team -cne $directory.Name) {
        throw "Sample registry ownership/deployment does not match $($directory.FullName)."
    }
    foreach ($type in Get-ChildItem -LiteralPath $directory.FullName -Directory | Sort-Object Name) {
        if ($type.Name -cnotin $registry.types.name) { throw "Undeclared sample type: $($type.FullName)" }
        foreach ($workflow in Get-ChildItem -LiteralPath $type.FullName -Directory | Sort-Object Name) {
            if (-not (Test-Path -LiteralPath (Join-Path $workflow.FullName 'workflow.cs') -PathType Leaf)) {
                throw "Sample workflow.cs is missing: $($workflow.FullName)"
            }
            $schedule = Join-Path $workflow.FullName 'schedules.json'
            if ((Test-Path -LiteralPath $schedule) -and -not $IncludeSchedules) {
                Write-Warning "Skipping scheduled workflow $($workflow.Name); use -IncludeSchedules to publish its schedule deliberately."
                continue
            }
            $workflows += [pscustomobject]@{
                Id = "$($directory.Name)/$($type.Name)/$($workflow.Name)"
                Directory = $workflow.FullName
                Schedule = if (Test-Path -LiteralPath $schedule) { $schedule } else { $null }
            }
        }
    }
}
if ($workflows.Count -eq 0) { throw 'No sample workflows selected.' }
$commit = & git -C $repository rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve sample Git provenance.' }
$project = Join-Path $repository 'backend\src\Wf.Cli'
& dotnet build $project -c Release --no-restore --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'CLI build failed; restore project dependencies if missing.' }
$cli = Join-Path $project 'bin\Release\net10.0\wf.dll'
foreach ($workflow in $workflows) {
    Write-Host "Publishing $($workflow.Id) [$deployment/$Environment]"
    $arguments = @('publish', $workflow.Directory, '--engine', $Engine.AbsoluteUri,
        '--git-commit', $commit.Trim(), '--git-author', 'Sample publication',
        '--change-note', "Repository $tree sample workflow",
        '--work-directory', (Join-Path $repository ('.wf\samples\' + $workflow.Id.Replace('/', '-'))))
    foreach ($inputProject in @('Wf.Sdk', 'Wf.Contracts', 'Wf.Engine.Core', 'Wf.Validation')) {
        $arguments += @('--input', (Join-Path $repository "backend\src\$inputProject"))
    }
    if ($null -ne $workflow.Schedule) {
        $arguments += @('--schedule-source-path', [IO.Path]::GetRelativePath($repository, $workflow.Schedule).Replace('\', '/'))
    }
    & dotnet $cli @arguments
    if ($LASTEXITCODE -ne 0) { throw "Publication failed: $($workflow.Id)" }
    if ($RunOnce) {
        $publishToken = $env:WF_ACCESS_TOKEN
        try {
            if (-not [string]::IsNullOrWhiteSpace($env:WF_RUN_ACCESS_TOKEN)) {
                $env:WF_ACCESS_TOKEN = $env:WF_RUN_ACCESS_TOKEN
            }
            & dotnet $cli run $workflow.Id --engine $Engine.AbsoluteUri
            if ($LASTEXITCODE -ne 0) { throw "Run-once trigger failed: $($workflow.Id)" }
        }
        finally { $env:WF_ACCESS_TOKEN = $publishToken }
    }
}
