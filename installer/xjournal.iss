; Inno Setup script for the xJournal Windows installer.
; Built by .github/workflows/release.yml:
;   iscc /DAppVersion=1.0.0 /DSourceDir=<publish folder> /DOutputDir=<out> installer\xjournal.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{7C3E5B2A-9F41-4E8B-A6D2-1B0F6C9E4A73}
AppName=xJournal
AppVersion={#AppVersion}
AppVerName=xJournal {#AppVersion}
AppPublisher=CGrebill
AppPublisherURL=https://github.com/CGrebill/xJournal
AppCopyright=Copyright 2026 CGrebill
VersionInfoVersion={#AppVersion}
; Per-user install: no administrator rights needed.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\xJournal
DefaultGroupName=xJournal
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE.md
SetupIconFile=..\src\XJournal.App\Assets\xjournal.ico
UninstallDisplayIcon={app}\xJournal.exe
UninstallDisplayName=xJournal
OutputDir={#OutputDir}
OutputBaseFilename=xJournal-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\xJournal"; Filename: "{app}\xJournal.exe"
Name: "{autodesktop}\xJournal"; Filename: "{app}\xJournal.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\xJournal.exe"; Description: "{cm:LaunchProgram,xJournal}"; Flags: nowait postinstall skipifsilent

; Journals live in %APPDATA%\xJournal and are deliberately NOT removed on uninstall.
