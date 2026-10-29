<#
.SYNOPSIS
Starts, restarts, or stops this user's native Elementarr instance. Data is separate from installed binaries.
#>
[CmdletBinding()]
param([switch]$Restart, [switch]$Stop, [switch]$NoBrowser,
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'Elementarr\data'))
$ErrorActionPreference = 'Stop'
$binary = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Elementarr.exe'))
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
$processFile = Join-Path $DataDirectory 'native-process.json'
$port = 8090
$serverFile = Join-Path $DataDirectory 'server.json'
if (Test-Path -LiteralPath $serverFile) {
    $server = Get-Content -LiteralPath $serverFile -Raw | ConvertFrom-Json
    $port = [int]$server.Port
}
if ($port -lt 1024 -or $port -gt 65535) { throw 'The operating port must be between 1024 and 65535.' }
$hash = [Security.Cryptography.SHA256]::Create()
try { $identity = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($DataDirectory.ToLowerInvariant()))).Replace('-', '').Substring(0, 24) }
finally { $hash.Dispose() }
$mutex = New-Object Threading.Mutex($false, "Local\Elementarr.Launcher.$identity")
$locked = $false
try {
    $locked = $mutex.WaitOne(10000)
    if (-not $locked) { throw 'Another Elementarr startup operation is in progress.' }
    $running = $null
    if (Test-Path -LiteralPath $processFile) {
        $saved = Get-Content -LiteralPath $processFile -Raw | ConvertFrom-Json
        $candidate = Get-Process -Id $saved.Id -ErrorAction SilentlyContinue
        # Stop only the exact executable and start time recorded by this launcher; PIDs can be reused.
        if ($candidate -and $candidate.Path -eq $binary -and $candidate.StartTime.ToUniversalTime().Ticks -eq [long]$saved.StartTicks) { $running = $candidate }
    }
    if ($running -and ($Restart -or $Stop)) {
        Stop-Process -Id $running.Id
        if (-not $running.WaitForExit(15000)) { throw 'Elementarr did not stop within 15 seconds.' }
        $running = $null
        Remove-Item -LiteralPath $processFile -ErrorAction SilentlyContinue
    }
    if ($Stop) { return }
    if (-not $running) {
        if (-not (Test-Path -LiteralPath $binary)) { throw 'Elementarr.exe is missing from the installation folder.' }
        $logs = Join-Path $DataDirectory 'logs'
        New-Item -ItemType Directory -Force -Path $logs | Out-Null
        foreach ($name in @('output', 'error')) {
            $log = Join-Path $logs "$name.log"
            if (Test-Path -LiteralPath $log) { Move-Item -LiteralPath $log -Destination (Join-Path $logs "$name.previous.log") -Force }
        }
        # Start-Process uses a command line on Windows; quote the data path and reject embedded quotation marks.
        if ($DataDirectory.Contains('"')) { throw 'The data directory cannot contain quotation marks.' }
        $instance = [Guid]::NewGuid().ToString("N")
        $running = Start-Process -FilePath $binary -ArgumentList @('--ELEMENTARR_DATA', ('"' + $DataDirectory + '"'), '--ELEMENTARR_INSTANCE', $instance) -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'output.log') -RedirectStandardError (Join-Path $logs 'error.log')
        @{ Id = $running.Id; StartTicks = $running.StartTime.ToUniversalTime().Ticks; Port = $port } | ConvertTo-Json | Set-Content -LiteralPath $processFile -Encoding UTF8
        $ready = $false
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            $running.Refresh()
            if ($running.HasExited) { throw "Elementarr could not start. Check $logs\error.log (the port may already be in use)." }
            try { $health = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$port/health" -TimeoutSec 1; $ready = $health.StatusCode -eq 200 -and ($health.Content | ConvertFrom-Json).instance -eq $instance }
            catch { $ready = $false }
            if ($ready) { break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) { throw "Elementarr did not become ready. Check $logs." }
    }
    else { $port = [int]$saved.Port }
    if (-not $NoBrowser) { Start-Process "http://localhost:$port" }
}
catch {
    if (-not $NoBrowser) {
        Add-Type -AssemblyName System.Windows.Forms
        [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Elementarr', 'OK', 'Error') | Out-Null
    }
    throw
}
finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
