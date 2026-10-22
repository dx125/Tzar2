; Tzar2 installer (ADR 0241), compiled by tools/package/package-windows.ps1 with Inno Setup 6:
;   ISCC /DAppVersion=0.7.0-beta.12+sha /DFileVersion=0.7.0-beta.12 /DNumericVersion=0.7.0.12 /DSourceDir=... /DOutputDir=... tzar2.iss
; Per-user by default (no administrator rights; the player may choose all users). The uninstaller removes only the files
; it installed: the player's data (saves, replays, settings, logs, the imported game package) lives in
; %APPDATA%\Godot\app_userdata\Tzar2 (REQ-PLAT-02) and is never touched, so there is no [UninstallDelete] section.

#ifndef AppVersion
  #error AppVersion is required
#endif

[Setup]
; Never change the AppId: Windows recognises upgrades and the uninstaller by it.
AppId={{DF9E2F61-84AB-48E0-B842-E65960AB37C1}
AppName=Tzar2
AppVersion={#AppVersion}
AppVerName=Tzar2 {#FileVersion}
AppPublisher=Tzar2 contributors
AppPublisherURL=https://github.com/dx125/Tzar2
AppSupportURL=https://github.com/dx125/Tzar2/issues
VersionInfoVersion={#NumericVersion}
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
DefaultDirName={autopf}\Tzar2
DefaultGroupName=Tzar2
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile={#SourceDir}\LICENSE.txt
OutputDir={#OutputDir}
OutputBaseFilename=Tzar2-{#FileVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Tzar2 {#FileVersion}
UninstallDisplayIcon={app}\Tzar2.exe
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Tzar2"; Filename: "{app}\Tzar2.exe"
Name: "{group}\Tzar2 (with console)"; Filename: "{app}\Tzar2.console.exe"
Name: "{group}\Read me"; Filename: "{app}\README.txt"
Name: "{autodesktop}\Tzar2"; Filename: "{app}\Tzar2.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Tzar2.exe"; Description: "{cm:LaunchProgram,Tzar2}"; Flags: nowait postinstall skipifsilent
