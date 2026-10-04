@echo off
setlocal
title Mudtrack Launcher
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Scripts\download_and_play.ps1"
if errorlevel 1 (
  echo.
  echo Mudtrack could not be started. See the error above.
  pause
)
