@echo off
rem Double-click to install Glimpse for this user (no admin needed).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Glimpse.ps1" %*
if errorlevel 1 (
  echo.
  echo Installation failed. See the message above.
  pause
)
