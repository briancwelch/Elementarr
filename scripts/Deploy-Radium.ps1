<#
.SYNOPSIS
Builds and starts Elementarr on Radium through LAN SSH, preserving its existing configuration and data.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[a-zA-Z0-9_.-]+$')][string]$UserName = 'conta',
    [ValidatePattern('^10\.0\.0\.14$')][string]$Server = '10.0.0.14',
    [ValidatePattern('^[A-Za-z]:/[A-Za-z0-9_./-]+$')][string]$Destination = 'C:/Elementarr'
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$keyPath = Join-Path $project '.local/ssh/elementarr_radium'
$knownHosts = Join-Path $project '.local/ssh/known_hosts'
if (-not (Test-Path -LiteralPath $keyPath) -or -not (Test-Path -LiteralPath $knownHosts)) {
    throw 'Run scripts/Prepare-RadiumKey.ps1 and install its public key on Radium first.'
}
# Use this project's dedicated key and pinned host key without changing the user's global SSH configuration.
$sshOptions = @('-i', $keyPath, '-o', 'IdentitiesOnly=yes', '-o', 'BatchMode=yes',
    '-o', 'StrictHostKeyChecking=yes', '-o', "UserKnownHostsFile=$knownHosts", '-o', 'ConnectTimeout=8')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$archiveName = "elementarr-source-$stamp.tar"
$archivePath = Join-Path $project ".local/$archiveName"
New-Item -ItemType Directory -Force -Path (Join-Path $project '.local') | Out-Null
Push-Location $project
try {
    # Include only application sources and public assets; credentials and runtime data never enter the archive.
    & tar --exclude=bin --exclude=obj -cf $archivePath .dockerignore Dockerfile compose.yaml compose.radium.yaml global.json Directory.Build.props src
    if ($LASTEXITCODE -ne 0) { throw 'Could not package the source.' }
    & scp @sshOptions $archivePath "${UserName}@${Server}:$archiveName"
    if ($LASTEXITCODE -ne 0) { throw 'Source transfer failed.' }
    # Encode the fixed PowerShell script so Windows OpenSSH works with either cmd or PowerShell as its default shell.
    $remoteScript = @"
`$ErrorActionPreference = 'Stop'
`$ProgressPreference = 'SilentlyContinue'
`$destination = '$Destination'
New-Item -ItemType Directory -Force -Path `$destination | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path `$destination '.env'))) {
    throw 'Create C:/Elementarr/.env on Radium with ELEMENTARR_BIND_ADDRESS=10.0.0.14 first. See docs/DEPLOYMENT.md.'
}
`$release = Join-Path `$destination 'releases/$stamp'
New-Item -ItemType Directory -Force -Path `$release | Out-Null
& tar -xf (Join-Path `$env:USERPROFILE '$archiveName') -C `$release
if (`$LASTEXITCODE -ne 0) { throw 'Source extraction failed.' }
Set-Location -LiteralPath `$release
`$engineType = & docker info --format '{{.OSType}}'
if (`$LASTEXITCODE -ne 0 -or `$engineType -ne 'linux') { throw 'The SSH account cannot reach a Linux Docker engine. Keep Docker Desktop running in Linux container mode under this account.' }
`$envFile = Join-Path `$destination '.env'
& docker compose -f compose.yaml -f compose.radium.yaml --project-name elementarr --env-file `$envFile build --pull
if (`$LASTEXITCODE -ne 0) { throw 'Container build failed; the existing container has not been replaced.' }
& docker compose -f compose.yaml -f compose.radium.yaml --project-name elementarr --env-file `$envFile up -d --wait --wait-timeout 90
if (`$LASTEXITCODE -ne 0) { throw 'Container did not become healthy. Inspect docker compose logs.' }
Set-Content -LiteralPath (Join-Path `$destination 'current-release.txt') -Value `$release
& docker compose -f compose.yaml -f compose.radium.yaml --project-name elementarr --env-file `$envFile ps
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($remoteScript))
    & ssh @sshOptions "${UserName}@${Server}" powershell.exe -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand $encoded
    if ($LASTEXITCODE -ne 0) { throw 'Radium deployment failed. See the remote output above.' }
    Write-Output 'Elementarr is healthy on Radium: http://radium.local:8090'
}
finally { Pop-Location }
