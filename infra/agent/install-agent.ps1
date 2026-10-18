[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$Executable,
    [Parameter(Mandatory)][uri[]]$Engines,
    [guid]$AgentId = [guid]::NewGuid(),
    [ValidatePattern('^[a-zA-Z0-9._-]{1,200}$')][string]$AgentName = $env:COMPUTERNAME,
    [ValidatePattern('^[a-zA-Z0-9._-]{1,80}$')][string]$ServiceName = 'wf-agent',
    [string]$InstallationDirectory,
    [switch]$NativeResourceSampling
)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This installer requires Windows.' }
if ($AgentId -eq [guid]::Empty) { throw 'AgentId must not be empty.' }
if ($Engines.Count -eq 0) { throw 'At least one engine is required.' }
foreach ($engine in $Engines) {
    if (!$engine.IsAbsoluteUri -or $engine.Scheme -notin @('http', 'https') -or
        $engine.AbsolutePath -ne '/' -or $engine.UserInfo -or $engine.Query -or $engine.Fragment -or
        ($engine.Scheme -eq 'http' -and !$engine.IsLoopback)) {
        throw 'Engines must be HTTPS origins, or HTTP loopback origins, without credentials or queries.'
    }
}
if (!$InstallationDirectory) { $InstallationDirectory = Join-Path $env:ProgramData "Kardia\Agents\$ServiceName" }
$InstallationDirectory = [IO.Path]::GetFullPath($InstallationDirectory)
if (!$PSCmdlet.ShouldProcess($ServiceName, "Install agent into $InstallationDirectory with persistent UUID $AgentId")) { return }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this installer from an elevated PowerShell.' }
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { throw "Service $ServiceName already exists; refusing to overwrite it." }
if ((Test-Path -LiteralPath $InstallationDirectory) -and (Get-ChildItem -LiteralPath $InstallationDirectory -Force)) {
    throw "Installation directory is not empty: $InstallationDirectory"
}
New-Item -ItemType Directory -Path $InstallationDirectory -Force | Out-Null
$binary = Join-Path $InstallationDirectory 'wf-agent.exe'
Copy-Item -LiteralPath $Executable -Destination $binary
@{
    Agent = @{
        Id = $AgentId.ToString(); Name = $AgentName; ServiceName = $ServiceName; Engines = @($Engines.AbsoluteUri)
        Sampling = @{ Enabled = $NativeResourceSampling.IsPresent }
    }
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $InstallationDirectory 'appsettings.json') -Encoding utf8NoBOM
if (![Diagnostics.EventLog]::SourceExists('wf-agent')) { [Diagnostics.EventLog]::CreateEventSource('wf-agent', 'Application') }
$credential = [pscredential]::new('NT AUTHORITY\LocalService', [securestring]::new())
New-Service -Name $ServiceName -BinaryPathName "`"$binary`"" -DisplayName $ServiceName -StartupType Automatic -Credential $credential | Out-Null
Start-Service -Name $ServiceName
$service = Get-Service -Name $ServiceName
$service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [timespan]::FromSeconds(15))
Write-Output "Installed $ServiceName ($AgentId) as LocalService."
