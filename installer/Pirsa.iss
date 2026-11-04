; Inno Setup script for the Pirsa installer.
;
; Per-user install: no elevation, no admin rights, uninstalls from Apps & Features.
;
; Compile from the repository root:
;   ISCC.exe /DAppVersion=0.1.0 /DArch=x64 /DPublishDir=out\win-x64 installer\Pirsa.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef Arch
  #define Arch "x64"
#endif

#ifndef PublishDir
  #define PublishDir "..\out\win-" + Arch
#endif

#ifndef OutputDir
  #define OutputDir "..\out\installer"
#endif

[Setup]
AppId={{8E4C1B2A-7F3D-4A6B-9C5E-2D1F0A3B4C5D}
AppName=Pirsa
AppVersion={#AppVersion}
AppVerName=Pirsa {#AppVersion}
AppPublisher=Pirsa
AppPublisherURL=https://github.com/dwifuady/pirsa
AppSupportURL=https://github.com/dwifuady/pirsa/issues
AppUpdatesURL=https://github.com/dwifuady/pirsa/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}
VersionInfoProductName=Pirsa

; Per-user by default: installs to %LOCALAPPDATA%\Programs\Pirsa with no UAC prompt.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Pirsa
DefaultGroupName=Pirsa
DisableProgramGroupPage=yes
UsePreviousAppDir=yes

; Windows 10 1809+ (same floor as the app).
MinVersion=10.0.17763

#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

OutputDir={#OutputDir}
OutputBaseFilename=Pirsa-{#AppVersion}-win-{#Arch}-setup
SetupIconFile=..\Pirsa\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Pirsa.exe
UninstallDisplayName=Pirsa {#AppVersion}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no

LicenseFile=..\LICENSE
ChangesAssociations=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Pirsa"; Filename: "{app}\Pirsa.exe"
Name: "{autodesktop}\Pirsa"; Filename: "{app}\Pirsa.exe"; Tasks: desktopicon

[Registry]
; Per-user stremio:// handler, so addon install links open Pirsa. The app
; writes the same keys for portable copies (Settings -> Addon links). The
; uninstaller removes the handler command and icon but keeps the protocol key
; itself, because a packaged build of Pirsa may share it.
Root: HKCU; Subkey: "Software\Classes\stremio"; ValueType: string; ValueName: ""; ValueData: "URL:Stremio addon link"
Root: HKCU; Subkey: "Software\Classes\stremio"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\stremio\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Pirsa.exe"",0"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\stremio\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Pirsa.exe"" ""%1"""; Flags: uninsdeletekey

[Run]
Filename: "{app}\Pirsa.exe"; Description: "{cm:LaunchProgram,Pirsa}"; Flags: nowait postinstall skipifsilent
