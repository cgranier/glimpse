# Builds the Command Palette extension and registers it (Developer Mode, no signing) from
# %LOCALAPPDATA%\Programs\Glimpse.CmdPal. Re-run to update. The Glimpse app must be installed and
# running (scripts/install.ps1): the extension searches through it.
#
#   pwsh scripts/install-cmdpal.ps1
#   pwsh scripts/install-cmdpal.ps1 -Uninstall

param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$target = Join-Path $env:LOCALAPPDATA 'Programs\Glimpse.CmdPal'
$packageName = 'Glimpse.CommandPalette'

# Unregister first: files of a registered package are in use, and an updated manifest needs a re-register.
Get-AppxPackage -Name $packageName | Remove-AppxPackage
Get-Process Glimpse.CmdPal -ErrorAction SilentlyContinue | Stop-Process
if ($Uninstall) {
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Write-Host "Glimpse extension removed. Run 'Reload' in Command Palette."
    return
}

$devMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($devMode -ne 1) { throw "Developer Mode is off (Settings > System > For developers). It's needed to register an unsigned package." }

Write-Host "Building extension (Release)..."
dotnet build "$repo\src\Glimpse.CmdPal" -c Release -p:Platform=x64 --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$layout = Get-ChildItem "$repo\src\Glimpse.CmdPal\bin\x64\Release" -Recurse -Filter AppxManifest.xml | Select-Object -First 1
if (-not $layout) { throw "no AppxManifest.xml in the build output" }

if (Test-Path $target) { Remove-Item $target -Recurse -Force }
Copy-Item $layout.DirectoryName $target -Recurse

Add-AppxPackage -Register (Join-Path $target 'AppxManifest.xml') -ForceApplicationShutdown
$pkg = Get-AppxPackage -Name $packageName
Write-Host "Registered $($pkg.Name) $($pkg.Version). In Command Palette run 'Reload' (Reload Command Palette extensions), then type 'Glimpse'."
