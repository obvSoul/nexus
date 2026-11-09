; NEXUS installer (Inno Setup 6). Built by installer\build.ps1, which passes /DAppVersion=x.y.z.
; Per-user installation: no administrator rights are needed.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{7E0C5B62-3F4A-4C1D-9B7E-2A6D8F1C4E90}
AppName=NEXUS
AppVersion={#AppVersion}
AppVerName=NEXUS {#AppVersion}
AppPublisher=NEXUS
DefaultDirName={userpf}\NEXUS
DefaultGroupName=NEXUS
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=NEXUS-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
SetupIconFile=nexus.ico
UninstallDisplayIcon={app}\Nexus.App.exe
UninstallDisplayName=NEXUS
AppMutex=NexusFileManagerSingleInstance
CloseApplications=yes
WizardStyle=modern
VersionInfoVersion={#AppVersion}
VersionInfoProductName=NEXUS
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=Installateur de NEXUS

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer une icône sur le Bureau"; Flags: unchecked

[Files]
Source: "..\artifacts\stage-{#AppVersion}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\NEXUS"; Filename: "{app}\Nexus.App.exe"
Name: "{autodesktop}\NEXUS"; Filename: "{app}\Nexus.App.exe"; Tasks: desktopicon

[Registry]
; Written by NEXUS itself when "Démarrer avec Windows" is on: never left behind by an uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "NEXUS"; ValueType: none; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\Nexus.App.exe"; Description: "Lancer NEXUS"; Flags: nowait postinstall

[Code]
{ The index, the preferences and the logs live outside the program folder. Keeping them is the default:
  the user decides, and a silent uninstall only removes them when called with /DELETEDATA=1. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  Remove: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := GetEnv('LOCALAPPDATA') + '\Nexus';
  if not DirExists(DataDir) then Exit;
  if UninstallSilent then
    Remove := ExpandConstant('{param:DELETEDATA|0}') = '1'
  else
    Remove := MsgBox('Supprimer aussi l''index, les favoris, l''historique et les réglages de NEXUS ?' + #13#10 + #13#10 +
                     'Choisissez Non pour les conserver en vue d''une réinstallation. Vos fichiers personnels ne sont jamais touchés.',
                     mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  if Remove then DelTree(DataDir, True, True, True);
end;
