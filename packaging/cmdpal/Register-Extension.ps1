# Registers the Glimpse extension with PowerToys Command Palette, for the current user.
#
# The extension is an unsigned MSIX package, and Windows only accepts unsigned packages with
# Developer Mode on (Settings > System > For developers). That's why this is an optional extra:
# Glimpse itself works without it. Needs Glimpse installed and running (the extension searches
# through it) and PowerToys with Command Palette.

param([switch]$Unregister)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\Glimpse.CmdPal'
$name = 'Glimpse.CommandPalette'

Get-AppxPackage -Name $name | Remove-AppxPackage
Get-Process Glimpse.CmdPal -ErrorAction SilentlyContinue | Stop-Process -Force
if ($Unregister) {
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Write-Host "Glimpse extension removed."
    return
}

$devMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($devMode -ne 1) {
    throw "Developer Mode is off. Turn it on in Settings > System > For developers, then run this again."
}
if (-not (Get-AppxPackage -Name 'Microsoft.CommandPalette')) {
    Write-Warning "Command Palette (PowerToys) doesn't seem to be installed; registering anyway."
}

$package = Join-Path $PSScriptRoot 'package'
Get-ChildItem $PSScriptRoot -Recurse -File | Unblock-File
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
Copy-Item $package $target -Recurse   # registered packages are run in place, so they need a stable home
Add-AppxPackage -Register (Join-Path $target 'AppxManifest.xml') -ForceApplicationShutdown

Write-Host "Registered. In Command Palette (Win+Alt+Space) type 'Glimpse'."
Write-Host "Tip: Command Palette > Settings > Extensions > Glimpse lets you set an alias such as 'ss'."
