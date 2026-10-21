#requires -Version 7.4
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
. (Join-Path $PSScriptRoot 'certificate.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\.wf\ha-lab'))
$statePath = Join-Path $root 'lab.json'
if (!(Test-Path $statePath)) { throw 'Run infra\ha-lab\start.ps1 before provisioning lab data.' }
$state = Get-Content $statePath -Raw | ConvertFrom-Json
if ($state.Id -notmatch '^[0-9a-f]{32}$') { throw 'Invalid lab identity.' }
$null = Get-Command docker, az -ErrorAction Stop
$address = @(Get-NetIPAddress -InterfaceAlias 'vEthernet (Default Switch)' -AddressFamily IPv4 |
    Where-Object AddressState -eq 'Preferred')
if ($address.Count -ne 1) { throw 'Expected one preferred IPv4 address on the existing Default Switch.' }
$hostAddress = $address[0].IPAddress
$dataRoot = Join-Path $root 'data'
$composeArgs = @('compose', '--project-name', 'kardia-ha-lab-data',
    '--env-file', (Join-Path $dataRoot 'compose.env'),
    '--file', (Join-Path $PSScriptRoot 'data.compose.yml'))

# A fixed Compose name must never adopt another checkout's data or containers.
foreach ($kind in @('container', 'volume', 'network')) {
    $ids = & docker $kind ls --quiet --filter 'label=com.docker.compose.project=kardia-ha-lab-data'
    foreach ($id in $ids) {
        $item = @(& docker $kind inspect $id | ConvertFrom-Json)[0]
        $labels = if ($kind -eq 'container') { $item.Config.Labels } else { $item.Labels }
        if ($labels.'io.kardia.lab' -ne $state.Id) {
            throw "Refusing foreign lab $kind '$id'."
        }
    }
}
$existingVolumes = @(& docker volume ls --quiet --filter 'label=com.docker.compose.project=kardia-ha-lab-data')
if ($existingVolumes.Count -gt 0 -and !(Test-Path (Join-Path $dataRoot 'secrets\database-password'))) {
    throw 'Existing database volumes have no local credentials. Restore protected data state; do not regenerate it.'
}
$null = New-Item -ItemType Directory -Force -Path $dataRoot
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18', 'S-1-5-32-544')) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.SecurityIdentifier]::new($sid), 'FullControl',
        'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
Set-Acl -Path $dataRoot -AclObject $acl
foreach ($directory in @('secrets', 'certs', 'identity')) {
    $null = New-Item -ItemType Directory -Force -Path (Join-Path $dataRoot $directory)
}
foreach ($secret in @('postgres-password', 'database-password', 'blob-key', 'upload-hmac-key')) {
    $path = Join-Path $dataRoot "secrets\$secret"
    if (!(Test-Path $path)) {
        if ($existingVolumes.Count -gt 0) { throw "Missing '$secret'; restore it instead of rotating existing storage implicitly." }
        $bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
        $value = if ($secret -eq 'blob-key') { [Convert]::ToBase64String($bytes) } else { [Convert]::ToHexString($bytes) }
        [IO.File]::WriteAllText($path, $value)
    }
}

$caPath = Join-Path $dataRoot 'certs\ca.crt'
$caKeyPath = Join-Path $dataRoot 'identity\ca.key'
if ((Test-Path $caPath) -ne (Test-Path $caKeyPath)) { throw 'Incomplete lab CA; restore its certificate and private key.' }
if (!(Test-Path $caPath)) {
    if ($existingVolumes.Count -gt 0) { throw 'Existing data has no lab CA; restore its identity.' }
    $key = [Security.Cryptography.RSA]::Create(3072)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=Kardia isolated HA lab CA', $key, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign, $true))
        $request.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($request.PublicKey, $false))
        $ca = $request.CreateSelfSigned([datetimeoffset]::UtcNow.AddMinutes(-5), [datetimeoffset]::UtcNow.AddYears(5))
        try {
            [IO.File]::WriteAllText($caPath, $ca.ExportCertificatePem())
            [IO.File]::WriteAllText($caKeyPath, $key.ExportPkcs8PrivateKeyPem())
        } finally { $ca.Dispose() }
    } finally { $key.Dispose() }
}
$ca = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile($caPath, $caKeyPath)
try {
    if ($ca.NotAfter.ToUniversalTime() -lt [datetime]::UtcNow.AddDays(31)) { throw 'Lab CA expires soon; rotate trust explicitly before continuing.' }
    $serverPath = Join-Path $dataRoot 'certs\server.crt'
    $serverKeyPath = Join-Path $dataRoot 'certs\server.key'
    $renew = !(Test-Path $serverPath) -or !(Test-Path $serverKeyPath)
    if (!$renew) {
        $server = [Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile($serverPath, $serverKeyPath)
        try {
            $renew = !$server.MatchesHostname($hostAddress, $false, $false) -or
                $server.NotAfter.ToUniversalTime() -lt [datetime]::UtcNow.AddDays(7) -or
                !$server.Extensions['2.5.29.35']
        } finally { $server.Dispose() }
    }
    if ($renew) {
        New-LabServerCertificate $ca $serverPath $serverKeyPath @('kardia-lab-data') `
            @([Net.IPAddress]::Parse('127.0.0.1'), [Net.IPAddress]::Parse($hostAddress))
    }
} finally { $ca.Dispose() }

@"
KARDIA_LAB_ID=$($state.Id)
KARDIA_DATA_ADDRESS=$hostAddress
KARDIA_DATA_ROOT=$($dataRoot.Replace('\', '/'))
"@ | Set-Content (Join-Path $dataRoot 'compose.env') -Encoding utf8NoBOM

$upArgs = @('up', '--detach', '--wait', '--wait-timeout', '120')
if ($renew) { $upArgs += '--force-recreate' }
& docker @composeArgs @upArgs
$role = & docker @composeArgs exec -T postgres sh -ec `
    'export PGPASSWORD="$(cat /run/secrets/database_password)"; psql --no-psqlrc "host=127.0.0.1 user=kardia dbname=workflow sslmode=verify-full sslrootcert=/certs/ca.crt" --set ON_ERROR_STOP=1 --tuples-only --no-align --command "SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole FROM pg_roles WHERE rolname = current_user"'
if (($role -join '').Trim() -ne 't') { throw 'Lab application database role is not least-privileged.' }

$blobKey = [IO.File]::ReadAllText((Join-Path $dataRoot 'secrets\blob-key'))
$connectionString = "DefaultEndpointsProtocol=https;AccountName=kardialab;AccountKey=$blobKey;BlobEndpoint=https://127.0.0.1:55443/kardialab;"
$previousConnection = $env:AZURE_STORAGE_CONNECTION_STRING
$previousCa = $env:REQUESTS_CA_BUNDLE
$previousConfig = $env:AZURE_CONFIG_DIR
$previousTelemetry = $env:AZURE_CORE_COLLECT_TELEMETRY
try {
    $env:AZURE_STORAGE_CONNECTION_STRING = $connectionString
    $env:REQUESTS_CA_BUNDLE = $caPath
    $env:AZURE_CONFIG_DIR = Join-Path $dataRoot 'azure-cli'
    $env:AZURE_CORE_COLLECT_TELEMETRY = 'false'
    & az storage container create --name kardia-blobs --only-show-errors --output none
    if ($LASTEXITCODE -ne 0) { throw 'Local blob container provisioning failed.' }
} finally {
    $env:AZURE_STORAGE_CONNECTION_STRING = $previousConnection
    $env:REQUESTS_CA_BUNDLE = $previousCa
    $env:AZURE_CONFIG_DIR = $previousConfig
    $env:AZURE_CORE_COLLECT_TELEMETRY = $previousTelemetry
}

$databasePassword = [IO.File]::ReadAllText((Join-Path $dataRoot 'secrets\database-password'))
$hmac = [IO.File]::ReadAllText((Join-Path $dataRoot 'secrets\upload-hmac-key'))
@"
ConnectionStrings__Workflow=Host=kardia-lab-data;Port=55432;Database=workflow;Username=kardia;Password=$databasePassword;SSL Mode=VerifyFull;Root Certificate=/certs/ca.crt
Storage__Provider=AzureBlob
Storage__Azure__Container=kardia-blobs
Storage__Azure__EmulatorConnectionString=$($connectionString.Replace('127.0.0.1', 'kardia-lab-data'))
Storage__UploadHmacKey=$hmac
"@ | Set-Content (Join-Path $dataRoot 'application.env') -Encoding utf8NoBOM
Write-Host "Lab data ready: PostgreSQL $hostAddress`:55432; HTTPS blobs $hostAddress`:55443."
Write-Host 'Credentials and private CA retained only in protected .wf\ha-lab\data. No Azure resources accessed.'
