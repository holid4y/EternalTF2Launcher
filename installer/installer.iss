; Eternal TF2 Launcher — Windows installer (Inno Setup 6)
;
; Build the payload first, then compile this script:
;   python scripts/package.py --rid win-x64 --freeze-engine
;   iscc installer\installer.iss
;
; The payload is self-contained: the launcher carries its own .NET runtime and
; the mod engine is frozen, so nothing else (Python, pip, .NET) is installed.

#define AppVersion     "2.0.0"
#define AppName        "Eternal TF2 Launcher"
#define AppExeName     "EternalLauncher.exe"
#define AppPublisher   "Project Eternal"
#define AppURL         "https://github.com/holid4y/EternalTF2Launcher"
#define AppMutexName   "ProjectEternalLauncher_Mutex"
; Never change AppId: it is how upgrades find the existing installation.
#define AppId          "{{B9C3D5E2-7F1A-4B6C-A8D0-3E5F2C9D7B84}"
#define SourceDir      "..\artifacts\win-x64\EternalLauncher"
#define IconFile       "..\resources\Assets\logo64_classic.ico"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
AppCopyright=Copyright (C) 2026 Project Eternal
AppMutex={#AppMutexName}

; Per-user install, no UAC prompt. Settings and mods live in %APPDATA% and
; %LOCALAPPDATA%, never next to the executable.
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

WizardStyle=modern
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
CloseApplications=yes
CloseApplicationsFilter={#AppExeName},LauncherTF2.exe,eternal-engine.exe
RestartApplications=no

OutputDir=..\artifacts
OutputBaseFilename=EternalLauncher-{#AppVersion}-win-x64-setup
Compression=lzma2/ultra64
SolidCompression=yes
DisableProgramGroupPage=yes
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startupwithwindows"; Description: "Start {#AppName} when Windows starts"; GroupDescription: "Shortcuts:"; Flags: unchecked

[InstallDelete]
; 1.x shipped a framework-dependent build and the engine sources in preloader\.
; Remove them, but keep preloader\userdata: it holds the old mod library,
; which 2.0 imports on first start.
Type: files; Name: "{app}\LauncherTF2.*"
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.pdb"
Type: files; Name: "{app}\*.runtimeconfig.json"
Type: files; Name: "{app}\*.deps.json"
Type: filesandordirs; Name: "{app}\runtimes"
Type: filesandordirs; Name: "{app}\Resources"
Type: filesandordirs; Name: "{app}\preloader\cli"
Type: filesandordirs; Name: "{app}\preloader\core"
Type: filesandordirs; Name: "{app}\preloader\data"
Type: filesandordirs; Name: "{app}\preloader\backup"
Type: filesandordirs; Name: "{app}\preloader\tools"
Type: files; Name: "{app}\preloader\*.*"
Type: filesandordirs; Name: "{app}\engine"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startupwithwindows

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\engine"
