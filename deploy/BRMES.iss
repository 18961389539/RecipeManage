#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef AppLabel
  #error AppLabel is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef RepoRoot
  #error RepoRoot is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

#define ProductName "BRMES"
#define DataRoot "{commonappdata}\BRMES"

[Setup]
AppId={{7A9AF095-5D12-4A8C-B95A-D44514EBC7C8}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} {#AppLabel}
AppPublisher=BRMES
DefaultDirName={autopf}\BRMES
DefaultGroupName=BRMES
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=BRMES-Setup-{#AppLabel}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=BRMES
Uninstallable=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Dirs]
Name: "{#DataRoot}"
Name: "{#DataRoot}\App_Data"
Name: "{#DataRoot}\logs"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}\bin"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\deploy\setup-lifecycle.ps1"; DestDir: "{app}\deploy"; Flags: ignoreversion
Source: "{#RepoRoot}\deploy\setup-lifecycle.ps1"; Flags: dontcopy
Source: "{#PayloadDir}\appsettings.json"; DestDir: "{#DataRoot}"; Flags: onlyifdoesntexist

[Icons]
Name: "{autoprograms}\BRMES"; Filename: "http://localhost:5010/"
Name: "{autodesktop}\BRMES"; Filename: "http://localhost:5010/"; Tasks: desktopicon

[Code]
var
  LifecyclePath: string;
  DataPath: string;
  ServiceUrl: string;
  LifecyclePrepared: Boolean;
  LifecycleCommitted: Boolean;
  RemoveDataOnUninstall: Boolean;

function PowerShellPath: string;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function RunLifecycle(const ActionName: string; const DeleteData: Boolean; var ExitCode: Integer): Boolean;
var
  Params: string;
begin
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + LifecyclePath +
    '" -Action ' + ActionName + ' -InstallRoot "' + ExpandConstant('{app}') +
    '" -DataRoot "' + DataPath + '" -Urls "' + ServiceUrl + '"';
  if DeleteData then
    Params := Params + ' -DeleteData';
  Result := Exec(PowerShellPath, Params, '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

procedure InitializeWizard;
begin
  LifecyclePath := ExpandConstant('{tmp}\setup-lifecycle.ps1');
  DataPath := ExpandConstant('{#DataRoot}');
  ServiceUrl := ExpandConstant('{param:BRMESUrl|http://localhost:5010}');
  ExtractTemporaryFile('setup-lifecycle.ps1');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if MsgBox(
    'Before installing or upgrading, confirm that no production batch is running. BRMES will be stopped during setup.',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
  begin
    Result := 'Setup cancelled. Stop active batches before installing or upgrading BRMES.';
    exit;
  end;
  if not RunLifecycle('Prepare', False, ExitCode) or (ExitCode <> 0) then
  begin
    RunLifecycle('Rollback', False, ExitCode);
    Result := 'BRMES could not stop safely. Close any running BRMES processes and retry.';
    exit;
  end;
  LifecyclePrepared := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if not RunLifecycle('Commit', False, ExitCode) or (ExitCode <> 0) then
      RaiseException('BRMES did not pass its health check. Setup will restore the previous installation.');
    LifecycleCommitted := True;
  end;
end;

procedure DeinitializeSetup;
var
  ExitCode: Integer;
begin
  if LifecyclePrepared and not LifecycleCommitted then
    RunLifecycle('Rollback', False, ExitCode);
end;

function InitializeUninstall: Boolean;
var
  ExitCode: Integer;
begin
  if MsgBox(
    'Before uninstalling, confirm that no production batch is running. Continue?',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
  begin
    Result := False;
    exit;
  end;

  RemoveDataOnUninstall := MsgBox(
    'Keep the BRMES database, configuration, logs, and backups?' + #13#10 + #13#10 +
    'Choose No only if you intentionally want to permanently delete all production data.',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDNO;

  LifecyclePath := ExpandConstant('{app}\deploy\setup-lifecycle.ps1');
  DataPath := ExpandConstant('{#DataRoot}');
  ServiceUrl := 'http://localhost:5010';
  if not FileExists(LifecyclePath) then
  begin
    MsgBox('The BRMES service management script is missing. The service was not removed.', mbError, MB_OK);
    Result := False;
    exit;
  end;

  if not RunLifecycle('Uninstall', RemoveDataOnUninstall, ExitCode) or (ExitCode <> 0) then
  begin
    MsgBox('BRMES could not stop or unregister cleanly. The service and data have been left in place.', mbError, MB_OK);
    Result := False;
    exit;
  end;
  Result := True;
end;
