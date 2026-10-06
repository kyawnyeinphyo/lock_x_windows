; Inno Setup script. Build with build.ps1 (it publishes the app first and passes AppVersion).
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "Lock X"
#define AppExe "LockX.exe"

[Setup]
AppId={{6F1C2B7A-3D54-4C0E-9B8A-5E2A7D41C9F3}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Lock X
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\LockX\Assets\LockX.ico
OutputDir=..\dist
OutputBaseFilename=LockX-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; Per-user install by default (no admin prompt); the user may choose "all users" in the dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Nothing to do: the encrypted vault in %LOCALAPPDATA%\LockX is deliberately left in place.

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if not UninstallSilent then
      MsgBox('Lock X has been removed.' + #13#10 + #13#10 +
             'Your encrypted vault was NOT deleted. It is still in:' + #13#10 +
             ExpandConstant('{localappdata}\LockX') + #13#10 + #13#10 +
             'Delete that folder yourself only if you no longer need your files.',
             mbInformation, MB_OK);
end;
