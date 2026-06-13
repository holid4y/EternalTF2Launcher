param(
	# Use -Clean when MSBuild's incremental cache gets out of sync with disk
	# (typical symptom: CS2001 "Source file *.g.cs could not be found" pointing
	# at obj/Debug). Adds ~5s to the build but resets WPF's XAML-codegen state.
	[switch]$Clean
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$solutionPath = Join-Path $repoRoot "EternalLauncher.sln"

if (-not (Test-Path $solutionPath)) {
	throw "Solution file not found: $solutionPath"
}

if ($Clean) {
	Write-Host "Cleaning previous build output..." -ForegroundColor Cyan
	dotnet clean $solutionPath -c Debug
	if ($LASTEXITCODE -ne 0) {
		throw "dotnet clean failed with exit code $LASTEXITCODE"
	}
}

Write-Host "Building solution in Debug mode..." -ForegroundColor Cyan
dotnet build $solutionPath -c Debug

if ($LASTEXITCODE -ne 0) {
	throw "dotnet build failed with exit code $LASTEXITCODE"
}

Write-Host "Build finished successfully." -ForegroundColor Green