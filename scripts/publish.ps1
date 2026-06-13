# publish.ps1 — Build a release-ready output for Inno Setup
#
# Usage:
#   scripts\publish.ps1               # builds current version
#   scripts\publish.ps1 -Version 1.2.0
#   scripts\publish.ps1 -Clean        # wipe obj/ before publishing
#
# Output lands in installer\publish\
# The installer picks up every file there via its wildcard Source entry.

param(
    [string]$Version = "",
    [switch]$Clean,
    # Skip staging the bundled Python installer (offline / CI builds).
    [switch]$SkipPython
)

$ErrorActionPreference = "Stop"

$repoRoot    = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "src\LauncherTF2\LauncherTF2.csproj"
$outDir      = Join-Path $repoRoot "installer\publish"

if ($Clean) {
    Write-Host "Cleaning build artifacts..." -ForegroundColor Cyan
    & dotnet clean $projectPath -c Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed (exit $LASTEXITCODE)" }
}

# Wipe previous publish output so stale files don't creep in.
if (Test-Path $outDir) {
    Remove-Item $outDir -Recurse -Force
}

$publishArgs = @(
    "publish", $projectPath,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "false",
    # No -p:PublishSingleFile — the installer distributes the full set of
    # managed DLLs alongside LauncherTF2.exe.  A single-file bundle would:
    #   • Increase cold-start time (the runtime must extract the bundle on
    #     every launch before executing managed code).
    #   • Hide the DLL list from AV scanners, sometimes triggering false
    #     positives on unsigned bundles.
    # With explicit DLLs the installer's wildcard Source entry picks them all
    # up, they're individually scanned/signed, and startup is snappier.
    "-p:DebugType=none",          # strip PDB from publish output
    "-p:DebugSymbols=false",
    "-o", $outDir
)

if ($Version -ne "") {
    $publishArgs += "-p:Version=$Version"
    $publishArgs += "-p:AssemblyVersion=$Version"
    $publishArgs += "-p:FileVersion=$Version"
}

Write-Host "Publishing to $outDir ..." -ForegroundColor Cyan
& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE)"
}

# Sanity check — these must be present.
$required = @("LauncherTF2.exe")
foreach ($f in $required) {
    $full = Join-Path $outDir $f
    if (-not (Test-Path $full)) {
        throw "Expected file missing from publish output: $f"
    }
}

# Bundle the Casual Preloader engine (Option A: ship the sources as preloader/).
# NOTE: backup/ and data/ are REQUIRED runtime data — backup/ holds the vanilla
# TF2 particle files the engine stages before every "Apply Preload". Do NOT
# strip it. Only userdata/ (per-user runtime state) and __pycache__ are excluded.
$externalDir = Join-Path $repoRoot "external"
$preloaderDir = Join-Path $outDir "preloader"
Write-Host "Bundling Casual Preloader engine..." -ForegroundColor Cyan
Copy-Item -Path $externalDir -Destination $preloaderDir -Recurse -Force
$userdataDir = Join-Path $preloaderDir "userdata"
if (Test-Path $userdataDir) { Remove-Item $userdataDir -Recurse -Force }
Get-ChildItem -Path $preloaderDir -Directory -Filter "__pycache__" -Recurse | Remove-Item -Recurse -Force

foreach ($req in @("cli\main.py", "core\api.py", "backup\particles", "requirements.txt")) {
    if (-not (Test-Path (Join-Path $preloaderDir $req))) {
        throw "Preloader bundle is missing required item: $req"
    }
}

# Stage the Python installer that installer.iss bundles (and runs when the
# target PC has no Python). Keep this filename in sync with installer.iss.
$installerDir        = Join-Path $repoRoot "installer"
$pythonInstallerPath = Join-Path $installerDir "python-3.11.8-amd64.exe"
$pythonInstallerUrl  = "https://www.python.org/ftp/python/3.11.8/python-3.11.8-amd64.exe"

if ($SkipPython) {
    Write-Host "Skipping Python installer staging (-SkipPython)." -ForegroundColor Yellow
} elseif (Test-Path $pythonInstallerPath) {
    Write-Host "Python installer already staged for bundling." -ForegroundColor Cyan
} else {
    $answer = Read-Host "Python installer not found. Download python-3.11.8-amd64.exe (~25 MB) to bundle in the setup? [Y/n]"
    if ($answer -eq "" -or $answer -match '^(y|yes)$') {
        Write-Host "Downloading Python 3.11.8 installer..." -ForegroundColor Yellow
        try {
            Invoke-WebRequest -Uri $pythonInstallerUrl -OutFile $pythonInstallerPath
        } catch {
            Write-Host "WARNING: Python installer download failed: $_" -ForegroundColor Red
            Write-Host "         The setup will compile but won't bundle Python; users without" -ForegroundColor Red
            Write-Host "         Python will be guided to install it from the Mods tab." -ForegroundColor Red
        }
    } else {
        Write-Host "Skipped. The setup compiles without it (installer.iss uses skipifsourcedoesntexist)." -ForegroundColor Yellow
    }
}

$fileCount = (Get-ChildItem $outDir -File -Recurse).Count
Write-Host ""
Write-Host "Publish succeeded  ($fileCount file(s))." -ForegroundColor Green
Write-Host "Output: $outDir"                          -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. (Optional) Sign the binaries in installer\publish\" -ForegroundColor Yellow
Write-Host "  2. Open installer\installer.iss in Inno Setup Compiler" -ForegroundColor Yellow
Write-Host "     or run:  iscc installer\installer.iss"               -ForegroundColor Yellow
Write-Host "  3. (Optional) Sign the output .exe in installer\Output\" -ForegroundColor Yellow
