<#
.SYNOPSIS
Configures a restricted Windows service, its listener, and optional interactive tray startup.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$InstallRoot,
    [ValidateRange(1024,65535)][int]$Port=8090,
    [ValidatePattern('^[a-zA-Z0-9.-]{1,253}$')][string]$HostName='localhost',
    [ValidateSet('127.0.0.1','0.0.0.0')][string]$BindAddress='127.0.0.1',
    [switch]$TrayStartup, [switch]$Uninstall)
$ErrorActionPreference='Stop'
$InstallRoot=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$data=Join-Path $env:ProgramData 'Elementarr\data'
$executable=Join-Path $InstallRoot 'app\Elementarr.exe'
$service=Get-Service -Name Elementarr -ErrorAction SilentlyContinue
if ($Uninstall) {
    if ($service) { if ($service.Status -ne 'Stopped') { Stop-Service Elementarr; $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(45)) }; & sc.exe delete Elementarr | Out-Null }
    Unregister-ScheduledTask -TaskName 'Elementarr Tray' -Confirm:$false -ErrorAction SilentlyContinue
    Remove-NetFirewallRule -Name Elementarr-Service-LAN -ErrorAction SilentlyContinue
    return
}
if (-not (Test-Path -LiteralPath $executable)) { throw 'The installed service executable is missing.' }
if ([Uri]::CheckHostName($HostName) -notin @([UriHostNameType]::Dns,[UriHostNameType]::IPv4)) { throw 'Enter a valid local hostname.' }
if ($BindAddress -eq '127.0.0.1' -and $HostName -notin @('localhost','127.0.0.1')) { throw 'Use localhost for local-only access or enable LAN access for a network hostname.' }
# Migrate the earlier per-user package only when no machine-wide connection data already exists.
$legacyData=Join-Path $env:LOCALAPPDATA 'Elementarr\data'
if (-not (Test-Path -LiteralPath (Join-Path $data 'connections.protected')) -and (Test-Path -LiteralPath (Join-Path $legacyData 'connections.protected'))) {
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    Copy-Item -Path (Join-Path $legacyData '*') -Destination $data -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $data | Out-Null
$command='"'+$executable+'" --ELEMENTARR_DATA "'+$data+'"'
if (-not $service) {
    $credentials=[System.Management.Automation.PSCredential]::new('NT AUTHORITY\LocalService',[System.Security.SecureString]::new())
    New-Service -Name Elementarr -DisplayName Elementarr -Description 'Private movie and TV tracking with Plex sign-in.' -BinaryPathName $command -StartupType Automatic -Credential $credentials | Out-Null
}
else {
    if ($service.Status -ne 'Stopped') { Stop-Service Elementarr; $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(45)) }
    $changed=Invoke-CimMethod -InputObject (Get-CimInstance Win32_Service -Filter "Name='Elementarr'") -MethodName Change -Arguments @{PathName=$command;StartMode='Automatic';StartName='NT AUTHORITY\LocalService'}
    if ($changed.ReturnValue -ne 0) { throw 'Could not configure the Elementarr service.' }
}
& sc.exe sidtype Elementarr unrestricted | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not enable the Elementarr service SID.' }
$serviceSid=([System.Security.Principal.NTAccount]::new('NT SERVICE\Elementarr')).Translate([Security.Principal.SecurityIdentifier]).Value
# Only this service can modify its application binaries and private data; bootstrap helpers remain administrator-writable.
& icacls.exe $InstallRoot /grant ("*${serviceSid}:(OI)(CI)(RX)") | Out-Null
& icacls.exe (Join-Path $InstallRoot app) /grant ("*${serviceSid}:(OI)(CI)(M)") | Out-Null
& icacls.exe $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)(F)' '*S-1-5-32-544:(OI)(CI)(F)' ("*${serviceSid}:(OI)(CI)(M)") | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not secure Elementarr data.' }
@{Port=$Port;BindAddress=$BindAddress;HostName=$HostName} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data server.json) -Encoding UTF8
$registry='HKLM:\SOFTWARE\Elementarr'
New-Item -Path $registry -Force | Out-Null
Set-ItemProperty $registry InstallRoot $InstallRoot
Set-ItemProperty $registry DataDirectory $data
$listenerRegistry=Join-Path $registry Listener
New-Item -Path $listenerRegistry -Force | Out-Null
Set-ItemProperty $listenerRegistry HostName $HostName
Set-ItemProperty $listenerRegistry BindAddress $BindAddress
New-ItemProperty $listenerRegistry -Name Port -Value $Port -PropertyType DWord -Force | Out-Null
$acl=Get-Acl $listenerRegistry
$rights=[Security.AccessControl.RegistryRights]::ReadKey -bor [Security.AccessControl.RegistryRights]::SetValue
$acl.SetAccessRule(([System.Security.AccessControl.RegistryAccessRule]::new([System.Security.Principal.SecurityIdentifier]::new($serviceSid),$rights,[System.Security.AccessControl.AccessControlType]::Allow)))
Set-Acl -Path $listenerRegistry -AclObject $acl
# Grant only query/start/stop rights for this one service to its own token and interactive tray users.
$sddl=(& sc.exe sdshow Elementarr | Where-Object { $_ -match '^D:' } | Select-Object -First 1).Trim()
$descriptor=[System.Security.AccessControl.CommonSecurityDescriptor]::new($false,$false,$sddl)
foreach ($sid in @($serviceSid,'S-1-5-4')) {
    $descriptor.DiscretionaryAcl.AddAccess([Security.AccessControl.AccessControlType]::Allow,([System.Security.Principal.SecurityIdentifier]::new($sid)),0x200B5,[Security.AccessControl.InheritanceFlags]::None,[Security.AccessControl.PropagationFlags]::None)
}
& sc.exe sdset Elementarr $descriptor.GetSddlForm([Security.AccessControl.AccessControlSections]::All) | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not configure scoped service controls.' }
& sc.exe failure Elementarr reset= 86400 actions= restart/5000/restart/15000/none/0 | Out-Null
if ($BindAddress -eq '0.0.0.0') {
    Remove-NetFirewallRule -Name Elementarr-Service-LAN -ErrorAction SilentlyContinue
    New-NetFirewallRule -Name Elementarr-Service-LAN -DisplayName 'Elementarr private LAN' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Profile Private -RemoteAddress LocalSubnet | Out-Null
}
else { Remove-NetFirewallRule -Name Elementarr-Service-LAN -ErrorAction SilentlyContinue }
if ($TrayStartup) {
    # SSH can preserve a stale USERDOMAIN after a machine rename; Task Scheduler accepts the actual account SID.
    $user=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $action=New-ScheduledTaskAction -Execute (Join-Path $InstallRoot 'tray\Elementarr.Tray.exe')
    $principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    $trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
    $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName 'Elementarr Tray' -Action $action -Principal $principal -Trigger $trigger -Settings $settings -Force -ErrorAction Stop | Out-Null
}
else { Unregister-ScheduledTask -TaskName 'Elementarr Tray' -Confirm:$false -ErrorAction SilentlyContinue }
Start-Service Elementarr
(Get-Service Elementarr).WaitForStatus('Running',[TimeSpan]::FromSeconds(45))
if ($TrayStartup) { Start-ScheduledTask -TaskName 'Elementarr Tray' -ErrorAction SilentlyContinue }
Write-Output ("Elementarr service configured at http://${HostName}:$Port")
