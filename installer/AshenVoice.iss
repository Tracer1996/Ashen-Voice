#define MyAppName "Ashen Voice"
#define MyAppVersion "1.4.4"
#define MyAppPublisher "Ashen Voice"
#define MyAppExeName "AshenVoice.exe"

[Setup]
AppId={{59B3B5C7-8D86-45EA-96FA-22B4660A3E1D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Ashen Voice
DefaultGroupName=Ashen Voice
DisableProgramGroupPage=yes
OutputDir=..\release
OutputBaseFilename=AshenVoice-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupLogging=yes
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Ashen Voice"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Ashen Voice"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Ashen Voice"; Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Ashen Voice"" /F"; Flags: runhidden; RunOnceId: "RemoveAshenVoiceStartupTask"
