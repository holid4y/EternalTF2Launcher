@echo off
echo 🚀 Launching Project Eternal...

cd /d "%~dp0.."
if exist "src\LauncherTF2\bin\Debug\net8.0-windows\LauncherTF2.exe" (
    start "" "src\LauncherTF2\bin\Debug\net8.0-windows\LauncherTF2.exe"
) else (
    echo ⚠️ Executable not found. Attempting to run via dotnet...
    dotnet run --project src\LauncherTF2\LauncherTF2.csproj
)
