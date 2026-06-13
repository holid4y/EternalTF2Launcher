@echo off
setlocal EnableExtensions

set "PORT=%~1"
if not defined PORT set "PORT=4173"

set "ROOT=%~dp0.."
set "DOCS_DIR=%ROOT%\docs"

if not exist "%DOCS_DIR%\index.html" (
    echo [docs] Could not find "%DOCS_DIR%\index.html".
    exit /b 1
)

pushd "%DOCS_DIR%" || exit /b 1

where py >nul 2>nul
if %errorlevel%==0 (
    echo [docs] Serving %DOCS_DIR% at http://localhost:%PORT%/
    echo [docs] Press Ctrl+C to stop.
    py -3 -m http.server %PORT%
    set "EXIT_CODE=%errorlevel%"
    popd
    exit /b %EXIT_CODE%
)

where python >nul 2>nul
if %errorlevel%==0 (
    echo [docs] Serving %DOCS_DIR% at http://localhost:%PORT%/
    echo [docs] Press Ctrl+C to stop.
    python -m http.server %PORT%
    set "EXIT_CODE=%errorlevel%"
    popd
    exit /b %EXIT_CODE%
)

popd
echo [docs] Python was not found. Install Python 3 or add it to PATH, then run this script again.
exit /b 1