# Installs Glimpse for the current user (no admin): copies the app to %LOCALAPPDATA%\Programs\Glimpse,
# adds a Start menu entry and an "Installed apps" entry, optionally starts it with Windows, and launches it.
# Runs on Windows PowerShell 5.1 and PowerShell 7. Re-run a newer version to upgrade; settings and the
# index (in %LOCALAPPDATA%\Glimpse) are kept.

param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Glimpse'),
    [switch]$NoAutoStart,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'app'
if (-not (Test-Path (Join-Path $source 'Glimpse.exe'))) {
    throw "app\Glimpse.exe not found. Extract the whole zip first, then run Install.cmd from the extracted folder."
}

# Files from a downloaded zip carry Windows' "came from the internet" mark; clear it so the app's DLLs
# load without prompts. (This doesn't bypass anything for other files on your PC.)
Get-ChildItem $PSScriptRoot -Recurse -File | Unblock-File

# A running copy holds its files and the hotkey.
Get-Process Glimpse -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

Write-Host "Installing to $Destination ..."
if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
New-Item -ItemType Directory -Force (Split-Path $Destination) | Out-Null
Copy-Item $source $Destination -Recurse
foreach ($f in 'Uninstall.cmd', 'Uninstall-Glimpse.ps1', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md') {
    $p = Join-Path $PSScriptRoot $f
    if (Test-Path $p) { Copy-Item $p $Destination }
}

$exe = Join-Path $Destination 'Glimpse.exe'
$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''

# Start menu
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Glimpse.lnk'))
$link.TargetPath = $exe
$link.WorkingDirectory = $Destination
$link.Description = 'Find screenshots and images by their text or how they look'
$link.Save()

# Settings > Apps > Installed apps entry, so it can be uninstalled the usual way.
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Glimpse'
New-Item $key -Force | Out-Null
$size = [int]((Get-ChildItem $Destination -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
$values = @{
    DisplayName     = 'Glimpse'
    DisplayVersion  = $version
    Publisher       = 'Glimpse'
    DisplayIcon     = "$exe,0"
    InstallLocation = $Destination
    UninstallString = "`"$(Join-Path $Destination 'Uninstall.cmd')`""
    URLInfoAbout    = '@REPO_URL@'   # filled in by build-release.ps1
}
foreach ($name in $values.Keys) { Set-ItemProperty $key -Name $name -Value $values[$name] }
Set-ItemProperty $key -Name EstimatedSize -Value $size -Type DWord
Set-ItemProperty $key -Name NoModify -Value 1 -Type DWord
Set-ItemProperty $key -Name NoRepair -Value 1 -Type DWord

# Start with Windows (same entry the in-app Settings toggle manages)
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($NoAutoStart) { Remove-ItemProperty $run -Name Glimpse -ErrorAction SilentlyContinue }
else { Set-ItemProperty $run -Name Glimpse -Value "`"$exe`" --hidden" }

Write-Host ""
Write-Host "Glimpse $version installed."
Write-Host "  Open it from Start, or press Win+Alt+S anywhere. It lives in the tray (^ next to the clock)."
Write-Host "  Settings (gear button or Ctrl+,) has folders, the shortcut, and the optional visual search download."
if (-not $NoLaunch) { Start-Process $exe }
