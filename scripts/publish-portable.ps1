# publish-portable.ps1 — Build a self-contained single-file portable release
#
# The output is a single self-contained file:
#   LauncherTF2.exe      — single bundled executable (includes the .NET runtime)
#
# Usage:
#   scripts\publish-portable.ps1
#   scripts\publish-portable.ps1 -Version 1.2.0
#   scripts\publish-portable.ps1 -Clean
#
# Output lands in installer\publish-portable\

param(
    [string]$Version = "",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$repoRoot    = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "src\LauncherTF2\LauncherTF2.csproj"
$outDir      = Join-Path $repoRoot "installer\publish-portable"

if ($Clean) {
    Write-Host "Cleaning build artifacts..." -ForegroundColor Cyan
    & dotnet clean $projectPath -c Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet clean failed (exit $LASTEXITCODE)" }
}

if (Test-Path $outDir) {
    Remove-Item $outDir -Recurse -Force
}

$publishArgs = @(
    "publish", $projectPath,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    # Compress the bundle — reduces exe size at the cost of slightly slower
    # first-launch extraction (subsequent launches use the extracted cache).
    "-p:EnableCompressionInSingleFile=true",
    # Pre-compile to native code so startup after extraction is fast.
    "-p:PublishReadyToRun=true",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "-o", $outDir
)

if ($Version -ne "") {
    $publishArgs += "-p:Version=$Version"
    $publishArgs += "-p:AssemblyVersion=$Version"
    $publishArgs += "-p:FileVersion=$Version"
}

Write-Host "Publishing portable build to $outDir ..." -ForegroundColor Cyan
& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE)"
}

# Sanity check — the launcher exe must always be present.
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
# strip it; doing so makes applying a preload fail ("vanilla staging folder is
# empty"). Only userdata/ (per-user runtime state) and __pycache__ are excluded.
$externalDir = Join-Path $repoRoot "external"
$preloaderDir = Join-Path $outDir "preloader"
Write-Host "Bundling Casual Preloader engine..." -ForegroundColor Cyan
Copy-Item -Path $externalDir -Destination $preloaderDir -Recurse -Force
$userdataDir = Join-Path $preloaderDir "userdata"
if (Test-Path $userdataDir) { Remove-Item $userdataDir -Recurse -Force }
Get-ChildItem -Path $preloaderDir -Directory -Filter "__pycache__" -Recurse | Remove-Item -Recurse -Force

# Sanity: the engine CLI entry point and its staging data must be present.
foreach ($req in @("cli\main.py", "core\api.py", "backup\particles", "requirements.txt")) {
    if (-not (Test-Path (Join-Path $preloaderDir $req))) {
        throw "Preloader bundle is missing required item: $req"
    }
}

# Report final file count and sizes.
$files = Get-ChildItem $outDir -File -Recurse
$totalMB = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host ""
Write-Host "Portable build succeeded  ($($files.Count) file(s), $totalMB MB total)." -ForegroundColor Green
Write-Host "Output: $outDir" -ForegroundColor Green
Write-Host ""
Write-Host "Top-level contents:" -ForegroundColor Yellow
foreach ($item in Get-ChildItem $outDir | Sort-Object { -not $_.PSIsContainer }, Name) {
    if ($item.PSIsContainer) {
        $dirMB = [math]::Round((Get-ChildItem $item.FullName -File -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
        Write-Host ("  {0,-28} {1,7} MB  (folder)" -f ($item.Name + "\"), $dirMB) -ForegroundColor Yellow
    } else {
        $sizeMB = [math]::Round($item.Length / 1MB, 1)
        Write-Host ("  {0,-28} {1,7} MB" -f $item.Name, $sizeMB) -ForegroundColor Yellow
    }
}

# Portable builds can't install Python for the user — warn if it's missing so
# the maintainer knows the mod preloader needs it on the target machine.
$pythonOk = $false
foreach ($cmd in @("py", "python")) {
    if (Get-Command $cmd -ErrorAction SilentlyContinue) { $pythonOk = $true; break }
}

Write-Host ""
if (-not $pythonOk) {
    Write-Host "NOTE: Python was not found on THIS machine. The portable build runs fine," -ForegroundColor Magenta
    Write-Host "      but the Casual Preloader (mods) needs Python 3.10+ on the user's PC." -ForegroundColor Magenta
    Write-Host "      The launcher shows a 'Get Python' prompt in the Mods tab when it's missing." -ForegroundColor Magenta
    Write-Host ""
}
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  Zip the output folder and distribute - no .NET install required." -ForegroundColor Yellow
Write-Host "  Users need Python 3.10+ for mod support (Mods tab guides them if absent)." -ForegroundColor Yellow
