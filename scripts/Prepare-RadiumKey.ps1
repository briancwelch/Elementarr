<#
.SYNOPSIS
Prepares Carbon's dedicated deployment key and prints the one-time public-key setup to run on Radium.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$keyDirectory = Join-Path $project '.local/ssh'
$keyPath = Join-Path $keyDirectory 'elementarr_radium'
$knownHosts = Join-Path $keyDirectory 'known_hosts'
New-Item -ItemType Directory -Force -Path $keyDirectory | Out-Null
if (-not (Test-Path -LiteralPath $keyPath)) {
    # ProcessStartInfo preserves the empty passphrase argument under Windows PowerShell 5.1.
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = (Get-Command ssh-keygen.exe -ErrorAction Stop).Source
    $start.Arguments = '-q -t ed25519 -N "" -C elementarr-carbon-radium -f "' + $keyPath + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw 'SSH key generation failed.' }
    $process.Dispose()
}
# A dedicated unattended key is protected by the Windows account's ACL, retained only in ignored local state.
$identitySid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls.exe $keyPath /inheritance:r /grant:r "*$($identitySid):(F)" '*S-1-5-18:(F)' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Private key ACL setup failed.' }
if (-not (Test-Path -LiteralPath $knownHosts)) {
    # Trust the first key from the explicitly configured LAN host; later connections strictly reject changed keys.
    & ssh -i $keyPath -o IdentitiesOnly=yes -o BatchMode=yes -o ConnectTimeout=8 `
        -o StrictHostKeyChecking=accept-new -o "UserKnownHostsFile=$knownHosts" conta@10.0.0.14 whoami
    if (-not (Test-Path -LiteralPath $knownHosts)) { throw 'Could not reach Radium to record its SSH host key.' }
}
$publicKey = (Get-Content -LiteralPath ($keyPath + '.pub') -Raw).Trim()
if ($publicKey -notmatch '^ssh-ed25519 [A-Za-z0-9+/=]+(?: [A-Za-z0-9_.-]+)?$') { throw 'The public key format is invalid.' }
# The public key may be copied; the private key never leaves Carbon. Restrict remote use to Carbon's LAN address.
$remoteSetup = @'
$ErrorActionPreference = 'Stop'
$key = 'restrict,from="10.0.0.129" __PUBLIC_KEY__'
$file = "$env:ProgramData\ssh\administrators_authorized_keys"
if ($key -notin @(Get-Content -LiteralPath $file -ErrorAction SilentlyContinue)) {
    Add-Content -LiteralPath $file -Value $key -Encoding ascii
}
icacls.exe $file /inheritance:r /grant:r '*S-1-5-32-544:(F)' '*S-1-5-18:(F)'
whoami
'@
Write-Output 'Run this once in Administrator PowerShell on Radium:'
Write-Output $remoteSetup.Replace('__PUBLIC_KEY__', $publicKey)
