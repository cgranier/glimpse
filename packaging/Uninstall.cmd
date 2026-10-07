@echo off
rem Runs the uninstaller from a temporary copy, so it can delete the folder it was started from.
copy /y "%~dp0Uninstall-Glimpse.ps1" "%TEMP%\Uninstall-Glimpse.ps1" >nul
cd /d "%TEMP%"
rem This file is about to be deleted. "(goto)" makes cmd leave the batch file right away while still
rem running the rest of this (already read and expanded) line, so it never goes back to a missing file.
(goto) 2>nul & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%TEMP%\Uninstall-Glimpse.ps1" -Destination "%~dp0." %* & pause
