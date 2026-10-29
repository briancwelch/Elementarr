#ifndef PublishDir
  #error PublishDir is required.
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif
#ifndef AppVersion
  #define AppVersion "0.4.0"
#endif
[Setup]
AppId={{76EAB07E-7947-442E-A4E1-C0F8E392321D}
AppName=Elementarr
AppVersion={#AppVersion}
AppPublisher=Elementarr
DefaultDirName={autopf}\Elementarr
UsePreviousAppDir=no
DefaultGroupName=Elementarr
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Elementarr-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\..\src\Elementarr.Tray\Elementarr.ico
UninstallDisplayIcon={app}\tray\Elementarr.Tray.exe
CloseApplications=yes
RestartApplications=no
[Tasks]
Name: traystartup; Description: "Show the Elementarr tray icon when I sign in"
[Files]
Source: "{#PublishDir}\app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\tray\*"; DestDir: "{app}\tray"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\updater\*"; DestDir: "{app}\updater"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\Configure-Service.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
[Icons]
Name: "{group}\Elementarr"; Filename: "{app}\tray\Elementarr.Tray.exe"; Parameters: "--open"
Name: "{group}\Elementarr tray controls"; Filename: "{app}\tray\Elementarr.Tray.exe"
[Run]
Filename: "{app}\tray\Elementarr.Tray.exe"; Parameters: "--open"; Description: "Open Elementarr in my browser"; Flags: postinstall nowait skipifsilent runasoriginaluser
[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\Configure-Service.ps1"" -InstallRoot ""{app}"" -Uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveElementarrService"
[Code]
var
  ListenerPage: TInputQueryWizardPage;
  NetworkPage: TInputOptionWizardPage;

// Collect the hostname and operating port before installation and show the browser address.
procedure InitializeWizard();
var
  SavedHost: String;
  SavedPort: Cardinal;
begin
  ListenerPage := CreateInputQueryPage(wpSelectDir, 'Where to open Elementarr', 'Choose your browser address',
    'The Windows service starts automatically. Choose where you will open its web interface.');
  ListenerPage.Add('Hostname (localhost for this computer):', False);
  ListenerPage.Add('Operating port (1024-65535):', False);
  SavedHost := 'localhost'; SavedPort := 8090;
  if not RegQueryStringValue(HKLM, 'SOFTWARE\Elementarr\Listener', 'HostName', SavedHost) then SavedHost := 'localhost';
  if not RegQueryDWordValue(HKLM, 'SOFTWARE\Elementarr\Listener', 'Port', SavedPort) then SavedPort := 8090;
  ListenerPage.Values[0] := ExpandConstant('{param:HOST|' + SavedHost + '}');
  ListenerPage.Values[1] := ExpandConstant('{param:PORT|' + IntToStr(SavedPort) + '}');
  NetworkPage := CreateInputOptionPage(ListenerPage.ID, 'Network access', 'Choose who can reach the web interface',
    'Plex sign-in protects the app. LAN access adds a firewall rule restricted to the Private profile and local subnet.', True, False);
  NetworkPage.Add('This computer only (localhost)');
  NetworkPage.Add('Allow devices on my private local network');
  NetworkPage.SelectedValueIndex := 0;
  if ExpandConstant('{param:LAN|0}') = '1' then NetworkPage.SelectedValueIndex := 1;
end;

// Validate listener inputs and display the exact URL before applying service or firewall preferences.
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
  I: Integer;
  Host: String;
begin
  Result := True;
  if CurPageID = ListenerPage.ID then begin
    Host := ListenerPage.Values[0]; Port := StrToIntDef(ListenerPage.Values[1], 0);
    if (Length(Host) = 0) or (Length(Host) > 253) or (Port < 1024) or (Port > 65535) then Result := False;
    for I := 1 to Length(Host) do
      if Pos(Host[I], 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-') = 0 then Result := False;
    if not Result then MsgBox('Enter a hostname such as localhost or radium.local and a port between 1024 and 65535.', mbError, MB_OK);
    NetworkPage.SubCaptionLabel.Caption := 'Browser address: http://' + Host + ':' + ListenerPage.Values[1];
  end;
  if (CurPageID = NetworkPage.ID) and (NetworkPage.SelectedValueIndex = 0) and
    (ListenerPage.Values[0] <> 'localhost') and (ListenerPage.Values[0] <> '127.0.0.1') then begin
    MsgBox('Use localhost for local-only access, or select private local network access for your network hostname.', mbError, MB_OK);
    Result := False;
  end;
end;

// Stop the existing service before replacing program files; private data is independent of installed binaries.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Parameters: String;
begin
  Result := '';
  if RegKeyExists(HKLM, 'SOFTWARE\Elementarr') then begin
    Parameters := '-NoProfile -NonInteractive -Command "$ErrorActionPreference = ''Stop''; $s = Get-Service Elementarr -ErrorAction SilentlyContinue; if ($s) { Stop-Service Elementarr; $s.WaitForStatus(''Stopped'', [TimeSpan]::FromSeconds(45)) }"';
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Result := 'Could not stop the existing Elementarr service.'
    else if Code <> 0 then Result := 'The existing Elementarr service did not stop. Retry after stopping it in Services.';
  end;
end;

// Run only the fixed bundled configuration script with validated preferences, failing visibly if service setup fails.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Parameters: String;
  Bind: String;
begin
  if CurStep = ssPostInstall then begin
    Bind := '127.0.0.1';
    if NetworkPage.SelectedValueIndex = 1 then Bind := '0.0.0.0';
    Parameters := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Configure-Service.ps1') +
      '" -InstallRoot "' + ExpandConstant('{app}') + '" -HostName "' + ListenerPage.Values[0] +
      '" -Port ' + ListenerPage.Values[1] + ' -BindAddress ' + Bind;
    if WizardIsTaskSelected('traystartup') then Parameters := Parameters + ' -TrayStartup';
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, Code) then
      RaiseException('Elementarr service configuration could not start.');
    if Code <> 0 then RaiseException('Elementarr service setup failed. Check the Windows service and setup log before retrying.');
    WizardForm.FinishedLabel.Caption := 'Elementarr is installed. Open http://' + ListenerPage.Values[0] + ':' + ListenerPage.Values[1] +
      ' and sign in with Plex. The tray menu controls the service. Private data is preserved when updating or uninstalling.';
  end;
end;
