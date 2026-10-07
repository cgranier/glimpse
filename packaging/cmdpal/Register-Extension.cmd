@echo off
rem Registers the Glimpse extension with PowerToys Command Palette (needs Developer Mode).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Register-Extension.ps1" %*
pause
