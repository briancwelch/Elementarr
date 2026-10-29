<# .SYNOPSIS Smoke-tests the published native payload without installing a service on the Carbon development machine. #>
[CmdletBinding()]
param([string]$PackageDirectory='')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
[xml]$props=Get-Content (Join-Path $projectRoot Directory.Build.props) -Raw
$version=[string]$props.Project.PropertyGroup.Version
if (-not $PackageDirectory) { $PackageDirectory=Join-Path $projectRoot "artifacts/Elementarr-$version-win-x64/app" }
$testRoot=Join-Path $projectRoot ('.local/native-service-smoke-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$port=0
for($attempt=0;$attempt -lt 50;$attempt++) {
    $probe=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,(Get-Random -Minimum 12000 -Maximum 19000))
    try { $probe.Start();$port=$probe.LocalEndpoint.Port;break } catch [Net.Sockets.SocketException] { continue } finally { $probe.Stop() }
}
if(-not $port){throw 'No free native test port.'}
@{Port=$port;BindAddress='127.0.0.1';HostName='localhost'} | ConvertTo-Json | Set-Content (Join-Path $testRoot server.json) -Encoding UTF8
$binary=[IO.Path]::GetFullPath((Join-Path $PackageDirectory Elementarr.exe))
$instance=[Guid]::NewGuid().ToString('N')
$process=Start-Process -FilePath $binary -ArgumentList @('--ELEMENTARR_DATA',('"'+$testRoot+'"'),'--ELEMENTARR_INSTANCE',$instance) -WorkingDirectory $PackageDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testRoot output.log) -RedirectStandardError (Join-Path $testRoot error.log)
try {
    $ready=$false
    for($attempt=0;$attempt -lt 30;$attempt++) {
        $process.Refresh(); if($process.HasExited){throw 'Published executable exited unexpectedly.'}
        try {$health=Invoke-RestMethod "http://127.0.0.1:$port/health" -TimeoutSec 1;$ready=$health.instance -eq $instance -and $health.version -eq $version} catch {$ready=$false}
        if($ready){break};Start-Sleep -Milliseconds 500
    }
    if(-not $ready){throw 'Published executable did not become healthy.'}
    $session=Invoke-RestMethod "http://127.0.0.1:$port/api/session"
    if($session.authenticated -or -not $session.firstRun -or -not $session.plexSignInAvailable){throw 'Unexpected fresh-install ownership state.'}
    Write-Output 'PASS: Self-contained service payload starts with fresh Plex-only ownership and the correct release version.'
    foreach($asset in @('app.js','tv.js','updates.js','app.css')) {if((Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$port/$asset").StatusCode -ne 200){throw ('Missing packaged asset: '+$asset)}}
    Write-Output 'PASS: Packaged movie/TV/update modules are served successfully.'
    $firstHash=(Get-FileHash (Join-Path $testRoot connections.protected)).Hash
    Stop-Process -Id $process.Id; $null=$process.WaitForExit(15000)
    if((Get-FileHash (Join-Path $testRoot connections.protected)).Hash -ne $firstHash){throw 'Stop altered installation identity.'}
    Write-Output 'PASS: Native shutdown preserves protected identity and data.'
    @{Version=$version;Passed=$true;CompletedAt=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json | Set-Content (Join-Path $projectRoot '.local/native-package-validation.json') -Encoding UTF8
}
finally { $process.Refresh();if(-not $process.HasExited){Stop-Process -Id $process.Id};$process.Dispose();Write-Output ('Native smoke evidence: '+$testRoot) }
