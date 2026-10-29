<#
.SYNOPSIS
Starts Elementarr in Docker Desktop on Carbon, bound only to localhost.
#>
[CmdletBinding()]
param([ValidateRange(1024,65535)][int]$Port = 8090, [switch]$RadiumServices)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$developmentEnv = Join-Path $project '.local/development.env'
New-Item -ItemType Directory -Force -Path (Join-Path $project '.local') | Out-Null
if (-not (Test-Path -LiteralPath $developmentEnv)) {
    [IO.File]::WriteAllText($developmentEnv, "ELEMENTARR_BIND_ADDRESS=127.0.0.1`nELEMENTARR_PORT=$Port`n")
}
if ($PSBoundParameters.ContainsKey('Port')) {
    $contents = Get-Content -LiteralPath $developmentEnv -Raw
    if ($contents -match '(?m)^ELEMENTARR_PORT=') { $contents = [regex]::Replace($contents, '(?m)^ELEMENTARR_PORT=.*$', "ELEMENTARR_PORT=$Port") }
    else { $contents += "`nELEMENTARR_PORT=$Port`n" }
    [IO.File]::WriteAllText($developmentEnv, $contents)
}
Push-Location $project
try {
    $files = @('-f', 'compose.yaml')
    if ($RadiumServices) { $files += @('-f', 'compose.radium.yaml') }
    & docker compose @files --project-name elementarr-dev --env-file $developmentEnv up -d --build --wait --wait-timeout 90
    if ($LASTEXITCODE -ne 0) { throw 'The local container did not become healthy. Inspect Docker Desktop logs.' }
    $portLine = Get-Content -LiteralPath $developmentEnv | Where-Object { $_ -match '^ELEMENTARR_PORT=' } | Select-Object -First 1
    $activePort = if ($portLine) { $portLine.Split('=',2)[1] } else { '8090' }
    Write-Output "Elementarr: http://localhost:$activePort"
    Write-Output 'Sign in with Plex. The first account becomes this installation owner.'
}
finally { Pop-Location }
