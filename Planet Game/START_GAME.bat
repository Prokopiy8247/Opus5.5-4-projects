@echo off
setlocal
cd /d "%~dp0"

echo Sable Reach launcher
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\DownloadAndStart.ps1"
if errorlevel 1 (
    echo.
    echo Could not start the game. Check your internet connection and try again.
    pause
    exit /b 1
)

endlocal
