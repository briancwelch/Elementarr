<# .SYNOPSIS Tests on Carbon Windows/Docker and records a source-bound release validation gate. #>
[CmdletBinding()]
param([ValidateRange(1024,65535)][int]$Port=8090)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $before=& (Join-Path $PSScriptRoot Get-SourceFingerprint.ps1)
    & dotnet run --project tests/Elementarr.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Local integration checks failed.' }
    & npm.cmd ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Frontend test dependencies could not be restored.' }
    & npm.cmd run test:ui
    if ($LASTEXITCODE -ne 0) { throw 'Frontend DOM regression checks failed.' }
    Get-ChildItem src/Elementarr/wwwroot -Filter *.js | ForEach-Object { & node --check $_.FullName; if ($LASTEXITCODE -ne 0) { throw 'Frontend syntax check failed.' } }
    Get-ChildItem scripts,tests,packaging -Recurse -Filter *.ps1 | ForEach-Object {
        $tokens=$null; $issues=$null
        [Management.Automation.Language.Parser]::ParseFile($_.FullName,[ref]$tokens,[ref]$issues) | Out-Null
        if ($issues) { throw ('PowerShell syntax error in '+$_.Name+': '+$issues[0].Message) }
    }
    & docker build --target tests -t elementarr-tests:local .
    if ($LASTEXITCODE -ne 0) { throw 'Docker integration checks failed.' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Start-Local.ps1 -Port $Port -RadiumServices
    if ($LASTEXITCODE -ne 0) { throw 'The local Docker application failed to start.' }
    $health=Invoke-RestMethod "http://localhost:$Port/health"
    if ($health.status -ne 'ok') { throw 'Local Docker health failed.' }
    foreach ($asset in @('app.js','tv.js','updates.js','app.css')) { if ((Invoke-WebRequest -UseBasicParsing "http://localhost:$Port/$asset").StatusCode -ne 200) { throw ('Docker asset failed: '+$asset) } }
    $fingerprint=& (Join-Path $PSScriptRoot Get-SourceFingerprint.ps1)
    if ($fingerprint -ne $before) { throw 'Source changed during validation; run the gate again.' }
    New-Item -ItemType Directory -Force .local | Out-Null
    @{Passed=$true;Fingerprint=$fingerprint;CompletedAt=[DateTimeOffset]::UtcNow.ToString('O');DockerPort=$Port;Version=$health.version} | ConvertTo-Json | Set-Content .local/release-validation.json -Encoding UTF8
    Write-Output ('Carbon local Docker release gate passed for source '+$fingerprint)
}
finally { Pop-Location }
