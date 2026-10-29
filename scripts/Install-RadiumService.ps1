<#
.SYNOPSIS
Migrates Radium from Docker to the validated Windows service on port 8090, retaining private data and a Docker rollback.
#>
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $build=Get-Content artifacts/release-build.json -Raw | ConvertFrom-Json
    $native=Get-Content .local/native-package-validation.json -Raw | ConvertFrom-Json
    if ($build.Fingerprint -ne (& scripts/Get-SourceFingerprint.ps1) -or -not $native.Passed -or $native.Version -ne $build.Version) { throw 'Validate/build/smoke-test this exact source before deployment.' }
    $installerName="Elementarr-$($build.Version)-win-x64-Setup.exe"
    $installer=Join-Path $projectRoot ('artifacts/'+$installerName)
    $expectedHash=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    $ssh=@('-i',(Join-Path $projectRoot '.local/ssh/elementarr_radium'),'-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',('UserKnownHostsFile='+(Join-Path $projectRoot '.local/ssh/known_hosts')))
    & scp @ssh $installer ("conta@10.0.0.14:"+$installerName)
    if ($LASTEXITCODE -ne 0) { throw 'Installer transfer failed.' }
    $remote=@'
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$installer=Join-Path $env:USERPROFILE '__INSTALLER__'
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne '__HASH__') { throw 'Transferred installer hash mismatch.' }
$stamp=Get-Date -Format yyyyMMdd-HHmmss
$backup=Join-Path C:/Elementarr/backups ('native-cutover-'+$stamp)
$target=Join-Path $env:ProgramData 'Elementarr\data'
New-Item -ItemType Directory -Force -Path (Join-Path $backup data) | Out-Null
$operatorSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls.exe $backup /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)(F)' '*S-1-5-32-544:(OI)(CI)(F)' ("*${operatorSid}:(OI)(CI)(F)") | Out-Null
$container='elementarr-elementarr-1'
$image=(& docker inspect $container --format '{{.Image}}').Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not identify the existing Docker installation.' }
& docker image tag $image ('elementarr:before-native-'+$stamp)
if ($LASTEXITCODE -ne 0) { throw 'Could not preserve the Docker rollback image.' }
$stopped=$false
try {
    & docker stop $container | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop the Docker app for a consistent final migration.' }
    $stopped=$true
    & docker cp ($container+':/data/.') (Join-Path $backup data)
    if ($LASTEXITCODE -ne 0) { throw 'Final Docker data backup failed.' }
    Copy-Item C:/Elementarr/current-release.txt (Join-Path $backup previous-docker-release.txt)
    if (Test-Path -LiteralPath $target) {
        $resolved=[IO.Path]::GetFullPath($target)
        $expected=[IO.Path]::GetFullPath((Join-Path $env:ProgramData 'Elementarr\data'))
        if ($resolved -ne $expected) { throw 'Unexpected machine data directory.' }
        $priorService=Get-Service Elementarr -ErrorAction SilentlyContinue
        if ($priorService -and $priorService.Status -ne 'Stopped') { Stop-Service Elementarr; $priorService.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(45)) }
        $previousData=[IO.Path]::GetFullPath((Join-Path $backup previous-machine-data))
        if (-not $previousData.StartsWith([IO.Path]::GetFullPath($backup).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe previous-data backup path.' }
        Move-Item -LiteralPath $resolved -Destination $previousData
    }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -Path (Join-Path $backup 'data/*') -Destination $target -Recurse -Force
    $snapshot=Get-Content (Join-Path $target snapshot.json) -Raw | ConvertFrom-Json
    $television=Get-Content (Join-Path $target tv-snapshot.json) -Raw | ConvertFrom-Json
    $connectionsHash=(Get-FileHash (Join-Path $target connections.protected)).Hash
    $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/TASKS=traystartup','/HOST=radium.local','/PORT=8090','/LAN=1',('/LOG="'+(Join-Path $backup installer.log)+'"'))
    $setup=Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($setup.ExitCode -ne 0) { throw ('Windows installer failed with exit code '+$setup.ExitCode) }
    $healthy=$false
    for ($attempt=0;$attempt -lt 45;$attempt++) {
        try { $health=Invoke-RestMethod http://127.0.0.1:8090/health -TimeoutSec 2; $healthy=$health.status -eq 'ok' -and $health.version -eq '__VERSION__' } catch { $healthy=$false }
        if ($healthy) { break }; Start-Sleep -Seconds 1
    }
    if (-not $healthy) { throw 'The native service did not report the expected version/health.' }
    if ((Get-FileHash (Join-Path $target connections.protected)).Hash -ne $connectionsHash) { throw 'Protected source credentials changed during migration.' }
    $session=Invoke-RestMethod http://127.0.0.1:8090/api/session
    if ($session.firstRun) { throw 'The existing Plex owner was not retained.' }
    & docker update --restart=no $container | Out-Null
    $service=Get-CimInstance Win32_Service -Filter "Name='Elementarr'"
    $result=[ordered]@{Version=$health.version;ServiceState=$service.State;ServiceAccount=$service.StartName;MovieCount=@($snapshot.Library).Count;MovieHistoryCount=@($snapshot.History).Count;TvSeriesCount=@($television.Library).Count;TvEpisodeWatches=@($television.History).Count;ExistingOwnerRetained=(-not $session.firstRun);ProtectedConnectionsUnchanged=$true;Backup=$backup;DockerStopped=$true}
    $result | ConvertTo-Json | Set-Content C:/Elementarr/native-deployment.json -Encoding UTF8
    $result | ConvertTo-Json
}
catch {
    Stop-Service Elementarr -ErrorAction SilentlyContinue
    if ($stopped) { & docker start $container | Out-Null }
    throw
}
'@
    $remote=$remote.Replace('__INSTALLER__',$installerName).Replace('__HASH__',$expectedHash).Replace('__VERSION__',[string]$build.Version)
    # Transfer the reviewed script instead of exceeding Windows OpenSSH's command-line limit.
    $remoteScriptName='Elementarr-native-cutover.ps1'
    $localScript=Join-Path $projectRoot ('.local/'+$remoteScriptName)
    [IO.File]::WriteAllText($localScript,$remote,[Text.UTF8Encoding]::new($false))
    $scriptHash=(Get-FileHash -LiteralPath $localScript -Algorithm SHA256).Hash
    & scp @ssh $localScript ('conta@10.0.0.14:'+$remoteScriptName)
    if ($LASTEXITCODE -ne 0) { throw 'Migration script transfer failed.' }
    $launch='$script=Join-Path $env:USERPROFILE '''+$remoteScriptName+'''; if ((Get-FileHash -LiteralPath $script).Hash -ne '''+$scriptHash+''') { throw ''Migration script hash mismatch.'' }; & $script'
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($launch))
    & ssh @ssh conta@10.0.0.14 powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand $encoded
    if ($LASTEXITCODE -ne 0) { throw 'Radium migration did not complete; check the remote rollback result.' }
    Write-Output 'Radium Windows service is running at http://radium.local:8090'
}
finally { Pop-Location }
