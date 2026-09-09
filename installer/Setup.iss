[Setup]
AppId={{73775248-A7C8-49E0-8F0F-F5D7AFBD4D1F}
AppName=ZPL Simulator
AppVersion=1.0.2
DefaultDirName={autopf}\ZplSimulator
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=ZplSimulator-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=ZPL Simulator

[Files]
Source: "..\artifacts\ZplSimulator\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Install.ps1') + '"',
      '', SW_SHOW, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      RaiseException('Printer installation failed. Run Install.cmd in the installation folder to see the error and retry.');
end;

function InitializeUninstall(): Boolean;
var ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Install.ps1') + '" -Uninstall',
    '', SW_SHOW, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then MsgBox('Printer removal failed. Close print jobs and retry. Installation files were kept.', mbError, MB_OK);
end;
