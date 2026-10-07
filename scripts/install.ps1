# Publishes a Release build to %LOCALAPPDATA%\Programs\Glimpse, registers it to start with Windows,
# and launches it in the tray. Re-run after pulling changes to update in place.
#
#   pwsh scripts/install.ps1                # install + start with Windows
#   pwsh scripts/install.ps1 -NoAutoStart   # install only

param([switch]$NoAutoStart)
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$target = Join-Path $env:LOCALAPPDATA 'Programs\Glimpse'
$staging = Join-Path ([IO.Path]::GetTempPath()) "glimpse-publish-$PID"

Write-Host "Publishing Release build..."
dotnet publish "$repo\src\Glimpse.App" -c Release -p:Platform=x64 -o $staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Stop the running copy (it holds the exe and the hotkey), then swap files.
Get-Process Glimpse -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 500
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
Move-Item $staging $target

$exe = Join-Path $target 'Glimpse.exe'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($NoAutoStart) {
    Remove-ItemProperty $run -Name Glimpse -ErrorAction SilentlyContinue
} else {
    # Same command the tray's "Start with Windows" toggle writes.
    Set-ItemProperty $run -Name Glimpse -Value "`"$exe`" --hidden"
}

$proc = Start-Process $exe -ArgumentList '--hidden' -PassThru
Start-Sleep -Seconds 4
if ($proc.HasExited) {
    throw "Glimpse exited right after starting (code $($proc.ExitCode)). See $env:LOCALAPPDATA\Glimpse\glimpse.log"
}
Write-Host "Installed to $target$(if (-not $NoAutoStart) { ' (starts with Windows)' }). Win+Alt+S to search."
