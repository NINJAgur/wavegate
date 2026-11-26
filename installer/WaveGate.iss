; Inno Setup script for WaveGate. Build with build-setup.ps1, which publishes the app into dist\app first.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
; Never change AppId: upgrades and the uninstaller find the existing install by it.
AppId={{0C67B04B-7EEE-4EB5-9018-830882B0D8A4}
AppName=WaveGate
AppVersion={#AppVersion}
AppVerName=WaveGate {#AppVersion}
AppPublisher=WaveGate
; The APO DLL must live in Program Files\WaveGate: WaveGate.Installer.exe registers it from there.
DefaultDirName={autopf}\WaveGate
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=WaveGate-Setup-{#AppVersion}
SetupIconFile=..\src\WaveGate.App\Assets\WaveGate.ico
UninstallDisplayIcon={app}\WaveGate.exe
UninstallDisplayName=WaveGate
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
; Per-process loopback (sharing app audio) needs Windows 10 2004 or later.
MinVersion=10.0.19041
CloseApplications=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "Start WaveGate when I sign in"; Flags: unchecked

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Excludes: "WaveGateApo.dll"; Flags: ignoreversion recursesubdirs
; audiodg keeps the APO loaded while a mic is attached, so a locked copy is replaced or removed after a reboot.
Source: "..\dist\app\WaveGateApo.dll"; DestDir: "{app}"; Flags: ignoreversion restartreplace uninsrestartdelete

[Icons]
Name: "{autoprograms}\WaveGate"; Filename: "{app}\WaveGate.exe"
Name: "{autodesktop}\WaveGate"; Filename: "{app}\WaveGate.exe"; Tasks: desktopicon
Name: "{userstartup}\WaveGate"; Filename: "{app}\WaveGate.exe"; Tasks: startup

[Run]
Filename: "{app}\WaveGate.exe"; Description: "Launch WaveGate"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Restore every microphone WaveGate is attached to before its DLL is deleted.
Filename: "{app}\WaveGate.Installer.exe"; Parameters: "uninstall-all"; Flags: runhidden waituntilterminated; RunOnceId: "DetachMics"
