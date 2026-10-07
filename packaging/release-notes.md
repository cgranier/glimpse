Find any screenshot or image by the text in it, or by what it looks like. Everything runs on your PC.

### Install

1. Download **`Glimpse-<version>-win-x64.zip`** below and extract it (right-click → *Extract All*).
2. Double-click **`Install.cmd`**.
   This test build isn't code-signed yet, so Windows may show *"Windows protected your PC"*: click **More info → Run anyway**.
3. Press **Win+Alt+S** anywhere. Glimpse lives in the tray and indexes your Screenshots and Downloads folders; add more in Settings (Ctrl+,).

Optional: **Settings → Visual search → Download** (392 MB, once) to search by what images look like and find similar ones.

Installs for your user only (`%LOCALAPPDATA%\Programs\Glimpse`), no admin needed. Uninstall from *Settings → Apps → Installed apps*.
Requires Windows 10 1809+ or Windows 11, x64. Nothing is uploaded; the only network access is the optional model download.

### PowerToys Command Palette (optional, for Developer Mode users)

**`Glimpse-CmdPal-<version>-win-x64.zip`** adds a *Glimpse* command to PowerToys Command Palette. The extension is an unsigned
MSIX package, which Windows only accepts with Developer Mode on. Extract it and run `Register-Extension.cmd`
(Glimpse must be installed and running).

Checksums are in `SHA256SUMS.txt`.
