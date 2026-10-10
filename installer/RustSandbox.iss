; Inno Setup script for Rust Sandbox.
; Build it with installer\build-installer.ps1, which publishes the app first and passes the version.
;
; What it does:
;  - copies a self-contained win-x64 build to Program Files (no .NET install needed)
;  - registers the "RustSandbox" Windows service (starts with Windows, restarts on crash)
;  - keeps the database, settings and SteamCMD in C:\ProgramData\RustSandbox, which upgrades
;    and uninstalls leave alone unless you ask for them to be deleted
;  - asks for the web port and dashboard sign-in on the first install only

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "Rust Sandbox"
#define ServiceName "RustSandbox"
#define ExeName "RustSandbox.exe"
; The project used to be called Rust Server Manager; upgrades from it move its data and remove its service.
#define LegacyName "Rust Server Manager"
#define LegacyService "RustServerManager"

[Setup]
AppId={{6F1C2B7E-4E59-4C0B-9C7A-2D8E5B3A91F4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=no
UsePreviousGroup=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=RustSandbox-Setup-{#AppVersion}
; LZMA kept producing corrupted setup files on this PC ("The source file is corrupted" on install),
; so this uses the simpler zip (deflate) compressor. The setup file is bigger but builds reliably.
Compression=zip/9
SolidCompression=no
; Inno Setup also packs its own setup data with LZMA unless this is off, and that crashed islzma.dll here too.
InternalCompressLevel=none
WizardStyle=modern
SetupIconFile=..\src\RustSandbox\app.ico
UninstallDisplayIcon={app}\app.ico
UninstallDisplayName={#AppName}
; The service is stopped in PrepareToInstall, so Restart Manager has nothing to close.
CloseApplications=no
SetupLogging=yes
LZMAUseSeparateProcess=yes

[Tasks]
Name: "firewall"; Description: "Let other PCs on my home network open the dashboard (adds a Windows Firewall rule)"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb,*.db,*.db-shm,*.db-wal,appsettings.Development.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\src\RustSandbox\app.ico"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Leftovers from the Rust Server Manager install (its data folder is moved in PrepareToInstall).
Type: filesandordirs; Name: "{autopf}\{#LegacyName}"
Type: filesandordirs; Name: "{autoprograms}\{#LegacyName}"
Type: files; Name: "{autodesktop}\{#LegacyName}.lnk"
Type: files; Name: "{autodesktop}\{#LegacyName}.url"

[Dirs]
Name: "{commonappdata}\{#ServiceName}"; Flags: uninsneveruninstall

[Registry]
; Remembers the port so upgrades can point the shortcuts at the right address.
Root: HKLM; Subkey: "Software\{#ServiceName}"; ValueType: dword; ValueName: "Port"; ValueData: "{code:GetPort}"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{code:DashboardUrl}"; IconFilename: "{app}\app.ico"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{code:DashboardUrl}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon

[Run]
Filename: "{code:DashboardUrl}"; Description: "Open the dashboard"; Flags: postinstall shellexec nowait skipifsilent

[UninstallRun]
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteFirewallRule"

[Code]
var
  SetupPage: TInputQueryWizardPage;
  FirstSetup: Boolean;

function DataDir: String;
begin
  Result := ExpandConstant('{commonappdata}\{#ServiceName}');
end;

function SettingsFile: String;
begin
  Result := DataDir + '\appsettings.json';
end;

function LegacyDataDir: String;
begin
  Result := ExpandConstant('{commonappdata}\{#LegacyService}');
end;

function InitializeSetup: Boolean;
begin
  // An existing settings file means this is an upgrade: keep everything the user set.
  // That includes an upgrade from Rust Server Manager, whose data folder is moved over below.
  FirstSetup := not FileExists(SettingsFile) and not FileExists(LegacyDataDir + '\appsettings.json');
  Result := True;
end;

procedure InitializeWizard;
begin
  SetupPage := CreateInputQueryPage(wpSelectTasks,
    'Dashboard sign-in', 'Choose the web port and the account you sign in with.',
    'These are saved to ' + SettingsFile + '. You can change them there later and restart the "{#AppName}" service.');
  SetupPage.Add('Web port:', False);
  SetupPage.Add('Username:', False);
  SetupPage.Add('Password:', True);
  SetupPage.Add('Confirm password:', True);
  SetupPage.Values[0] := '5080';
  SetupPage.Values[1] := 'admin';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = SetupPage.ID) and not FirstSetup;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;
  if CurPageID <> SetupPage.ID then Exit;
  Port := StrToIntDef(Trim(SetupPage.Values[0]), 0);
  if (Port < 1) or (Port > 65535) then
  begin
    MsgBox('Enter a port between 1 and 65535.', mbError, MB_OK);
    Result := False;
  end
  else if Trim(SetupPage.Values[1]) = '' then
  begin
    MsgBox('Enter a username.', mbError, MB_OK);
    Result := False;
  end
  else if (Length(SetupPage.Values[2]) < 6) or (SetupPage.Values[2] = 'changeme') then
  begin
    MsgBox('Choose a password of at least 6 characters (not "changeme").', mbError, MB_OK);
    Result := False;
  end
  else if SetupPage.Values[2] <> SetupPage.Values[3] then
  begin
    MsgBox('The passwords don''t match.', mbError, MB_OK);
    Result := False;
  end;
end;

function GetPort(Param: String): String;
var
  Port: Cardinal;
begin
  if FirstSetup then
    Result := Trim(SetupPage.Values[0])
  else if RegQueryDWordValue(HKLM, 'Software\{#ServiceName}', 'Port', Port) then
    Result := IntToStr(Port)
  else if RegQueryDWordValue(HKLM, 'Software\{#LegacyService}', 'Port', Port) then
    Result := IntToStr(Port)
  else
    Result := '5080';
end;

function DashboardUrl(Param: String): String;
begin
  Result := 'http://localhost:' + GetPort('') + '/';
end;

function JsonEscape(S: String): String;
begin
  StringChangeEx(S, '\', '\\', True);
  StringChangeEx(S, '"', '\"', True);
  Result := S;
end;

procedure WriteSettings;
var
  Lines: TArrayOfString;
  Port: String;
begin
  Port := GetPort('');
  SetArrayLength(Lines, 11);
  Lines[0] := '{';
  Lines[1] := '  "Urls": "http://0.0.0.0:' + Port + '",';
  Lines[2] := '  "Auth": {';
  Lines[3] := '    "Username": "' + JsonEscape(Trim(SetupPage.Values[1])) + '",';
  Lines[4] := '    "Password": "' + JsonEscape(SetupPage.Values[2]) + '"';
  Lines[5] := '  },';
  Lines[6] := '  "Manager": {';
  Lines[7] := '    "PublicUrl": "http://localhost:' + Port + '"';
  Lines[8] := '  }';
  Lines[9] := '}';
  Lines[10] := '';
  if not SaveStringsToUTF8File(SettingsFile, Lines, False) then
    MsgBox('Couldn''t write ' + SettingsFile + '. The dashboard will use port 5080 and admin / changeme.', mbError, MB_OK);
end;

function RunHidden(const FileName, Params: String): Integer;
var
  Code: Integer;
begin
  if Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := Code
  else
    Result := -1;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // Stop the running copy so its files can be replaced. Game servers it started keep running.
  RunHidden(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}');

  // Upgrading from Rust Server Manager: retire its service and firewall rule, then move its data
  // (servers, stats, settings, sign-in) to the new folder before anything creates that folder.
  RunHidden(ExpandConstant('{sys}\net.exe'), 'stop {#LegacyService}');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'delete {#LegacyService}');
  RunHidden(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="{#LegacyName}"');
  if DirExists(LegacyDataDir) and not DirExists(DataDir) then
  begin
    if RenameFile(LegacyDataDir, DataDir) then
      Log('Moved ' + LegacyDataDir + ' to ' + DataDir)
    else
      Log('Could not move ' + LegacyDataDir + '; the app tries again when it starts.');
  end;
  Result := '';
end;

procedure InstallService;
var
  Exe: String;
begin
  Exe := ExpandConstant('{app}\{#ExeName}');
  // "create" fails harmlessly on upgrades; "config" then points the service at this install.
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'create {#ServiceName} binPath= "\"' + Exe + '\"" start= delayed-auto DisplayName= "{#AppName}"');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'config {#ServiceName} binPath= "\"' + Exe + '\"" start= delayed-auto DisplayName= "{#AppName}"');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'description {#ServiceName} "Web dashboard for managing Rust game servers."');
  RunHidden(ExpandConstant('{sys}\sc.exe'), 'failure {#ServiceName} reset= 86400 actions= restart/5000/restart/15000/restart/60000');
  if (RunHidden(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}') <> 0) and not WizardSilent then
    MsgBox('The {#AppName} service didn''t start. Check Event Viewer > Windows Logs > Application for the error, ' +
      'or whether another program already uses port ' + GetPort('') + '.', mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Exe: String;
begin
  if CurStep <> ssPostInstall then Exit;

  // The data folder holds the dashboard password and RCON passwords: only SYSTEM and Administrators may read it.
  RunHidden(ExpandConstant('{sys}\icacls.exe'), '"' + DataDir + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F');

  if FirstSetup and not WizardSilent then
    WriteSettings;

  Exe := ExpandConstant('{app}\{#ExeName}');
  RunHidden(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="{#AppName}"');
  if WizardIsTaskSelected('firewall') then
    RunHidden(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall add rule name="{#AppName}" dir=in action=allow program="' + Exe + '" profile=private,domain enable=yes');

  InstallService;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep <> usPostUninstall) or UninstallSilent then Exit;
  if MsgBox('Also delete your servers, stats and settings in ' + DataDir + '?' + #13#10#13#10 +
      'Choose No to keep them for a later reinstall. Your Rust server folders are never touched.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(DataDir, True, True, True);
    RegDeleteKeyIncludingSubkeys(HKLM, 'Software\{#ServiceName}');
  end;
end;
