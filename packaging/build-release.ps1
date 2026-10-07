# Builds the release zips. Used by .github/workflows/release.yml and runnable locally:
#
#   pwsh packaging/build-release.ps1 -Version 0.5.0
#
# Produces in -Output (default: artifacts\):
#   Glimpse-<v>-win-x64.zip         app + CLI (self-contained: no .NET install needed) + Install/Uninstall
#   Glimpse-CmdPal-<v>-win-x64.zip  optional Command Palette extension (Developer Mode) + Register script
#   SHA256SUMS.txt

param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Output = 'artifacts',
    [string]$RepoUrl = $(if ($env:GITHUB_REPOSITORY) { "https://github.com/$env:GITHUB_REPOSITORY" } else { 'https://github.com/' })
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$Output = [IO.Path]::GetFullPath((Join-Path $root $Output))
$stage = Join-Path $Output 'stage'
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null

# MSIX wants a four-part numeric version; "0.5.0-beta.1" → 0.5.0.0
$numeric = ($Version -replace '[-+].*$', '').Split('.')
$msixVersion = (@($numeric + @('0', '0', '0', '0'))[0..3]) -join '.'

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed" }
}

# ---- app + CLI ---------------------------------------------------------------------------
$app = Join-Path $stage 'Glimpse'
$appBin = Join-Path $app 'app'
Write-Host "== Glimpse app $Version"
Invoke-Dotnet publish "$root\src\Glimpse.App" -c Release "-p:Platform=x64" "-p:SelfContained=true" `
    "-p:Version=$Version" "-p:DebugType=none" -o $appBin --nologo -v q
Write-Host "== glimpse-cli"
# Same folder: shares the app's bundled runtime instead of carrying a second copy.
Invoke-Dotnet publish "$root\src\Glimpse.Cli" -c Release -r win-x64 --self-contained `
    "-p:Version=$Version" "-p:DebugType=none" -o $appBin --nologo -v q
Get-ChildItem $appBin -Recurse -Include *.pdb | Remove-Item

foreach ($f in 'Install.cmd', 'Install-Glimpse.ps1', 'Uninstall.cmd', 'Uninstall-Glimpse.ps1', 'README.txt') {
    Copy-Item (Join-Path $PSScriptRoot $f) $app
}
(Get-Content (Join-Path $app 'Install-Glimpse.ps1') -Raw).Replace('@REPO_URL@', $RepoUrl) |
    Set-Content (Join-Path $app 'Install-Glimpse.ps1') -NoNewline -Encoding utf8
Copy-Item "$root\LICENSE" (Join-Path $app 'LICENSE.txt')
Copy-Item "$root\THIRD-PARTY-NOTICES.md" $app

$appZip = Join-Path $Output "Glimpse-$Version-win-x64.zip"
Compress-Archive $app $appZip -CompressionLevel Optimal

# ---- Command Palette extension -----------------------------------------------------------
Write-Host "== Command Palette extension $msixVersion"
Invoke-Dotnet build "$root\src\Glimpse.CmdPal" -c Release "-p:Platform=x64" "-p:Version=$Version" --nologo -v q
$layout = Get-ChildItem "$root\src\Glimpse.CmdPal\bin\x64\Release" -Recurse -Filter AppxManifest.xml | Select-Object -First 1
if (-not $layout) { throw 'Command Palette extension: no AppxManifest.xml in the build output' }

$ext = Join-Path $stage 'Glimpse-CmdPal'
Copy-Item $layout.DirectoryName (Join-Path $ext 'package') -Recurse
Get-ChildItem (Join-Path $ext 'package') -Recurse -Include *.pdb | Remove-Item
$manifest = Join-Path $ext 'package\AppxManifest.xml'
$xml = Get-Content $manifest -Raw
$xml = [regex]::Replace($xml, '(<Identity\b[^>]*\bVersion=")[^"]*', "`${1}$msixVersion")
Set-Content $manifest $xml -NoNewline -Encoding utf8
Copy-Item "$PSScriptRoot\cmdpal\*" $ext
Copy-Item "$root\LICENSE" (Join-Path $ext 'LICENSE.txt')

$extZip = Join-Path $Output "Glimpse-CmdPal-$Version-win-x64.zip"
Compress-Archive $ext $extZip -CompressionLevel Optimal

# ---- checksums ---------------------------------------------------------------------------
$sums = Get-ChildItem $Output -Filter *.zip | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
Set-Content (Join-Path $Output 'SHA256SUMS.txt') $sums -Encoding ascii
Remove-Item $stage -Recurse -Force

Get-ChildItem $Output | ForEach-Object { "{0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
