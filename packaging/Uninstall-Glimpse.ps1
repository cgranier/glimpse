# Removes Glimpse for the current user: the app, Start menu and Installed apps entries, start-with-Windows,
# and the Command Palette extension if it was registered. Your images are never touched.
# The index, settings and visual model in %LOCALAPPDATA%\Glimpse are removed only if you say so.
#
#   Uninstall.cmd                 asks about the data
#   ... -RemoveData / -KeepData   decides without asking

param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Glimpse'),
    [switch]$RemoveData,
    [switch]$KeepData
)
$ErrorActionPreference = 'Continue'
$Destination = $Destination.TrimEnd('\', '.')

Get-Process Glimpse, Glimpse.CmdPal -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

Get-AppxPackage -Name 'Glimpse.CommandPalette' -ErrorAction SilentlyContinue | Remove-AppxPackage
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Glimpse -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Glimpse' -Recurse -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Glimpse.lnk') -ErrorAction SilentlyContinue

foreach ($dir in $Destination, (Join-Path $env:LOCALAPPDATA 'Programs\Glimpse.CmdPal')) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}

$data = Join-Path $env:LOCALAPPDATA 'Glimpse'
if ((Test-Path $data) -and -not $KeepData) {
    $remove = $RemoveData
    if (-not $RemoveData) {
        $size = (Get-ChildItem $data -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1MB
        $answer = Read-Host ("Also delete Glimpse's index, settings and model ({0:N0} MB in {1})? [y/N]" -f $size, $data)
        $remove = $answer -match '^(y|yes)$'
    }
    if ($remove) { Remove-Item $data -Recurse -Force; Write-Host "Removed $data" }
    else { Write-Host "Kept $data (reinstalling picks up where you left off)." }
}

Write-Host "Glimpse is uninstalled."
