#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
foreach ($name in @('ConnectionStrings__wf', 'ConnectionStrings:wf')) {
    if ([Environment]::GetEnvironmentVariable($name)) {
        throw "Clear $name before launching this Docker-backed native profile; use AppHost directly for an external database."
    }
}
$null = Get-Command dotnet, bun, docker -ErrorAction Stop

$launchArguments = @(
    'run'
    '--project'
    (Join-Path $PSScriptRoot backend src Wf.AppHost Wf.AppHost.csproj)
    '--'
    "--Local:DataRoot=$(Join-Path $PSScriptRoot .wf native-main)"
    '--Local:PostgresVolume=wf-native-main-pg18'
    '--Local:ApiPort=5000'
    '--Local:EnginePort=5001'
    '--Local:UiPort=5173'
    '--Local:Monitoring=false'
)
& dotnet @launchArguments
exit $LASTEXITCODE
