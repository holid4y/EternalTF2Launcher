; =============================================================================
; Eternal TF2 Launcher — Inno Setup 6 Script
; =============================================================================
;
; PREREQUISITES
;   Inno Setup 6.3+  https://jrsoftware.org/isinfo.php
;
; HOW TO BUILD
;   1. Publish the app first:
;        scripts\publish.ps1          (PowerShell helper included in repo)
;      or manually:
;        dotnet publish src/LauncherTF2/LauncherTF2.csproj ^
;          -c Release -r win-x64 --self-contained false ^
;          -p:DebugType=none -p:DebugSymbols=false ^
;          -o installer\publish
;
;      NOTE: PublishSingleFile is intentionally NOT used.  The installer
;      bundles the full set of managed DLLs alongside LauncherTF2.exe.
;      Benefits: faster startup, individual file signing, and no AV
;      false-positives from unsigned bundles.
;
;   2. (Recommended) Sign the output binaries before building the installer:
;        signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 ^
;          /a installer\publish\LauncherTF2.exe
;
;   3. Open this file in Inno Setup Compiler and press Ctrl+F9,
;      or run:  iscc installer\installer.iss
;
;   4. (Recommended) Sign the output installer:
;        signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 ^
;          /a installer\Output\EternalTF2Launcher-Setup-{version}.exe
;
; OUTPUT
;   installer\Output\EternalTF2Launcher-Setup-{#AppVersion}.exe
;
; INSTALLATION PATH
;   Default: %LocalAppData%\Programs\Eternal TF2 Launcher
;   No UAC prompt — per-user install, same pattern as VS Code and other
;   modern desktop apps.  The user may elevate via the dialog or /ALLUSERS
;   command-line flag to install system-wide to Program Files.
; =============================================================================

; -- Version — bump this before every release ----------------------------------
#define AppVersion     "1.1.1"

; -- Identity ------------------------------------------------------------------
#define AppName        "Eternal TF2 Launcher"
#define AppExeName     "LauncherTF2.exe"
#define AppPublisher   "Project Eternal"
#define AppURL         "https://github.com/holid4y/EternalTF2Launcher"
#define AppMutexName   "ProjectEternalLauncher_Mutex"

; IMPORTANT: Never change AppId after the first public release.
; Windows uses this GUID to match installers to existing installations
; in Add/Remove Programs.  Changing it creates duplicate entries.
#define AppId          "{{B9C3D5E2-7F1A-4B6C-A8D0-3E5F2C9D7B84}"

; -- Paths (relative to this .iss file) ----------------------------------------
#define SourceDir      "publish"
#define IconFile       "..\resources\Assets\logo64_classic.ico"


; =============================================================================
[Setup]
; Identity
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
AppCopyright=Copyright (C) 2025 Project Eternal

; Installation location
; Per-user install — no UAC prompt.  Files land in the standard user-space
; Programs folder, exactly like VS Code, Figma, and other modern desktop apps.
; The launcher is asInvoker so it can write settings.json / launcher_config.json
; next to the exe without elevation.  The user may choose a system-wide path
; (Program Files) via the optional elevation dialog.
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
; Let the user elevate to admin (system-wide install) if they prefer.
PrivilegesRequiredOverridesAllowed=dialog commandline

; Architecture — x64 binaries only.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Windows 10 build 19041 (version 2004) is the minimum .NET 8 supports.
MinVersion=10.0.19041

; Appearance
WizardStyle=modern
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

; Automatically close the running launcher before upgrading so the file copy
; doesn't fail with access denied on a locked exe.
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=no

; Output
OutputDir=Output
OutputBaseFilename=EternalTF2Launcher-Setup-{#AppVersion}

; Compression — lzma2 solid is ideal for many small managed DLLs.
Compression=lzma2/ultra64
SolidCompression=yes

; UI pages
DisableProgramGroupPage=yes
DisableReadyPage=no
DisableWelcomePage=no
ShowLanguageDialog=no


; =============================================================================
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"


; =============================================================================
[Tasks]
; ── Shortcuts ─────────────────────────────────────────────────────────────────
; Desktop shortcut is checked by default — most users expect this for a launcher.
Name: "desktopicon"; \
  Description: "Create a &desktop shortcut"; \
  GroupDescription: "Shortcuts:"

; Start Menu shortcut is created unconditionally in [Icons] below.
; The startup-with-Windows task is opt-in (unchecked).
Name: "startupwithwindows"; \
  Description: "Start {#AppName} &automatically when Windows starts"; \
  GroupDescription: "Shortcuts:"; \
  Flags: unchecked


; =============================================================================
[Files]
; All files produced by dotnet publish — LauncherTF2.exe, managed DLLs,
; and any CopyToOutputDirectory assets (now includes preloader/).
Source: "{#SourceDir}\*"; \
  DestDir: "{app}"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

; Python installer bundled for users who don't have it. Staged by
; scripts\publish.ps1. skipifsourcedoesntexist lets the setup still compile
; (and install) when it wasn't staged — the launcher then guides the user to
; install Python from the Mods tab.
Source: "python-3.11.8-amd64.exe"; \
  DestDir: "{tmp}"; \
  Flags: ignoreversion deleteafterinstall skipifsourcedoesntexist


; =============================================================================
[Icons]
; ── Start Menu ────────────────────────────────────────────────────────────────
Name: "{group}\{#AppName}";           Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

; ── Desktop (optional, checked by default) ────────────────────────────────────
Name: "{autodesktop}\{#AppName}"; \
  Filename: "{app}\{#AppExeName}"; \
  Tasks: desktopicon

; ── Windows Startup folder (optional, unchecked by default) ───────────────────
; Creates a shortcut in %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup
; so the launcher auto-starts with Windows when the user opts in.
Name: "{userstartup}\{#AppName}"; \
  Filename: "{app}\{#AppExeName}"; \
  Tasks: startupwithwindows


; =============================================================================
[Run]
; Install Python silently if it is not detected AND the installer was bundled.
Filename: "{tmp}\python-3.11.8-amd64.exe"; \
  Parameters: "/quiet InstallAllUsers=0 PrependPath=1 Include_test=0"; \
  StatusMsg: "Installing Python 3.11 (required for the Mod Preloader)..."; \
  Check: ShouldInstallPython; \
  Flags: waituntilterminated

; Offer to launch the app at the end of the installer wizard.
Filename: "{app}\{#AppExeName}"; \
  Description: "&Launch {#AppName} now"; \
  Flags: nowait postinstall skipifsilent


; =============================================================================
; Remove runtime-written files on uninstall.  These are not installed by us —
; Inno Setup won't delete the install directory if it is non-empty, so we
; list them here for a clean removal.  User profiles in %APPDATA% are handled
; by the [Code] prompt below.
[UninstallDelete]
Type: files;          Name: "{app}\app_debug.log"
Type: files;          Name: "{app}\crash_log.txt"
Type: files;          Name: "{app}\settings.json"
Type: files;          Name: "{app}\launcher_config.json"
Type: files;          Name: "{app}\price_cache.json"
Type: files;          Name: "{app}\mod_metadata_cache.json"
Type: files;          Name: "{app}\mod_state.json"
Type: filesandordirs; Name: "{app}\mod_thumbnails"
Type: filesandordirs; Name: "{app}\logs"
; The bundled preloader engine writes runtime state under preloader\userdata\
; (portable mode). Remove the whole engine folder so nothing is left behind.
Type: filesandordirs; Name: "{app}\preloader"
Type: dirifempty;     Name: "{app}"


; =============================================================================
[Code]

// ---------------------------------------------------------------------------
// Python Check
// ---------------------------------------------------------------------------
function IsPythonInstalled(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;

  // Check HKCU and HKLM for PythonCore
  if RegGetSubkeyNames(HKCU, 'SOFTWARE\Python\PythonCore', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      // Look for 3.10+
      if (Length(Names[I]) >= 4) and (Copy(Names[I], 1, 2) = '3.') then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;

  if RegGetSubkeyNames(HKLM, 'SOFTWARE\Python\PythonCore', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if (Length(Names[I]) >= 4) and (Copy(Names[I], 1, 2) = '3.') then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function NeedsPython(): Boolean;
begin
  Result := not IsPythonInstalled();
end;

// Only run the bundled Python installer when Python is missing AND the
// installer was actually staged into {tmp} (skipifsourcedoesntexist means it
// may not be present in offline builds).
function ShouldInstallPython(): Boolean;
begin
  Result := NeedsPython() and FileExists(ExpandConstant('{tmp}\python-3.11.8-amd64.exe'));
end;

// Resolve a Python executable to drive pip with. Prefer the per-user install
// the bundled installer just created (the current process's PATH won't have
// refreshed yet), otherwise fall back to the launcher on PATH.
function ResolvePythonExe(): String;
var
  LocalPy: String;
begin
  LocalPy := ExpandConstant('{localappdata}\Programs\Python\Python311\python.exe');
  if FileExists(LocalPy) then
    Result := LocalPy
  else
    Result := 'py';
end;

// Install the engine's pip dependencies so the preloader actually runs. The
// requirements ship with the app (preloader\requirements.txt). Best-effort:
// failures (e.g. no network) are ignored — the Mods tab guides the user.
procedure InstallPreloaderDeps();
var
  ReqPath, PyExe: String;
  ResultCode: Integer;
begin
  ReqPath := ExpandConstant('{app}\preloader\requirements.txt');
  if not FileExists(ReqPath) then Exit;

  PyExe := ResolvePythonExe();

  WizardForm.StatusLabel.Caption := 'Installing Mod Preloader dependencies...';
  Exec(PyExe,
       '-m pip install --no-warn-script-location -r "' + ReqPath + '"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallPreloaderDeps();
end;

// ---------------------------------------------------------------------------
// .NET 8 Desktop Runtime — required for WPF on .NET 8
// ---------------------------------------------------------------------------
const
  DotNetDownloadUrl =
    'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';

// Detect .NET 8 x64 Desktop Runtime.
//
// Previous attempts failed because:
//   - RegGetSubkeyNames: wrong — versions are VALUES, not sub-keys
//   - GetEnv('ProgramFiles'): wrong in a 32-bit installer process — resolves
//     to C:\Program Files (x86), not C:\Program Files
//
// This version uses {commonpf64}, Inno Setup's own constant for the 64-bit
// Program Files folder.  It always resolves to C:\Program Files on x64
// Windows regardless of whether the installer process is 32-bit or 64-bit.
function IsDotNet8DesktopInstalled(): Boolean;
var
  KeyPath:    String;
  Names:      TArrayOfString;
  I:          Integer;
  FindRec:    TFindRec;
  BaseDir:    String;
begin
  Result := False;

  KeyPath := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\'
           + 'Microsoft.WindowsDesktop.App';

  // Registry — try value names (layout used by recent .NET installers)
  if RegGetValueNames(HKLM, KeyPath, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if (Length(Names[I]) >= 2) and (Copy(Names[I], 1, 2) = '8.') then
      begin
        Result := True;
        Exit;
      end;

  // Registry — try sub-key names (layout used by some older .NET installers)
  if RegGetSubkeyNames(HKLM, KeyPath, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if (Length(Names[I]) >= 2) and (Copy(Names[I], 1, 2) = '8.') then
      begin
        Result := True;
        Exit;
      end;

  // File system — {commonpf64} is always C:\Program Files on x64 Windows,
  // even from a 32-bit installer process (unlike %ProgramFiles% which resolves
  // to C:\Program Files (x86) in a 32-bit process context).
  BaseDir := ExpandConstant('{commonpf64}')
           + '\dotnet\shared\Microsoft.WindowsDesktop.App\';

  if FindFirst(BaseDir + '8.*', FindRec) then
  try
    repeat
      if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
      begin
        Result := True;
        Exit;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

// ---------------------------------------------------------------------------
// Mutex check — detect a running launcher instance (including tray-only state)
// ---------------------------------------------------------------------------
function IsLauncherRunning(): Boolean;
begin
  Result := CheckForMutexes('{#AppMutexName}');
end;

// ---------------------------------------------------------------------------
// InitializeSetup — called before the wizard appears
// ---------------------------------------------------------------------------
function InitializeSetup(): Boolean;
var
  Answer: Integer;
begin
  Result := True;

  // 1. .NET 8 check
  if not IsDotNet8DesktopInstalled() then
  begin
    Answer := MsgBox(
      '.NET 8 Desktop Runtime is required but was not found on your PC.'
      + #13#10#13#10
      + 'Click Yes to open the Microsoft download page.'
      + #13#10
      + 'Install it, then run this installer again.'
      + #13#10#13#10
      + 'Click No to cancel.',
      mbError, MB_YESNO or MB_DEFBUTTON1);

    if Answer = IDYES then
      ShellExec('open', DotNetDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, Answer);

    Result := False;
    Exit;
  end;

  // 2. Running instance check (handles the tray-minimised case that
  //    CloseApplications= might miss when no window is visible)
  if IsLauncherRunning() then
  begin
    Answer := MsgBox(
      '{#AppName} is currently running.'
      + #13#10#13#10
      + 'Please close it before continuing (check the system tray),'
      + #13#10
      + 'or click OK to force-close it and continue.',
      mbConfirmation, MB_OKCANCEL or MB_DEFBUTTON1);

    if Answer = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;

    // Force-close: terminate the process by exe name.
    Exec('taskkill.exe', '/F /IM {#AppExeName}', '', SW_HIDE,
         ewWaitUntilTerminated, Answer);

    // Brief pause to let the process exit and release its resources.
    Sleep(800);
  end;
end;

// ---------------------------------------------------------------------------
// CurUninstallStepChanged — optional AppData cleanup on uninstall
// ---------------------------------------------------------------------------
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDataPath: String;
  Answer:      Integer;
begin
  if CurUninstallStep <> usPostUninstall then Exit;

  AppDataPath := ExpandConstant('{userappdata}\Eternal TF2 Launcher');

  if not DirExists(AppDataPath) then Exit;

  Answer := MsgBox(
    'Do you want to delete your saved profiles and launcher preferences?'
    + #13#10#13#10
    + AppDataPath
    + #13#10#13#10
    + 'Yes  — delete everything (profiles, caches, thumbnails).'
    + #13#10
    + 'No   — keep your data (useful if you plan to reinstall).',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2);   // Default: No

  if Answer = IDYES then
    DelTree(AppDataPath, True, True, True);
end;
