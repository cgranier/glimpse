Find any screenshot or image by the text in it, what that text means, or what it looks like. Everything runs on your PC.

### New in 0.6.0: search by meaning

Glimpse can now match the text in your images by what it *means*, not just the words you typed, using Google's
EmbeddingGemma 2 on your CPU. "contraseña del wifi" finds an English Wi-Fi password screenshot; "to-do list"
finds a task list that never says "to-do". On a 10,000-image test library, the right image came first for 9 of 27
test queries (5 before) and was in the top five for 17 (12 before).

- **Settings → Search by meaning → Download** (330 MB, once), or tick it on the welcome screen. The first pass reads
  about 200 images a minute in the background; new screenshots take a fraction of a second.
- Results found only by meaning carry a **≈ related text** badge; exact text matches still come first.
- Without the model, Glimpse works exactly as before. Your existing index upgrades itself; no re-index needed.

### Install

1. Download **`Glimpse-<version>-win-x64.zip`** below and extract it (right-click → *Extract All*).
2. Double-click **`Install.cmd`**.
   This test build isn't code-signed yet, so Windows may show *"Windows protected your PC"*: click **More info → Run anyway**.
3. Press **Win+Alt+S** anywhere. Glimpse lives in the tray and indexes your Screenshots and Downloads folders; add more in Settings (Ctrl+,).

Optional: **Settings → Visual search → Download** (392 MB, once) to search by what images look like and find similar ones,
and **Settings → Search by meaning → Download** (330 MB, once) to search text by meaning.

Installs for your user only (`%LOCALAPPDATA%\Programs\Glimpse`), no admin needed. Uninstall from *Settings → Apps → Installed apps*.
Requires Windows 10 1809+ or Windows 11, x64. Nothing is uploaded; the only network access is the optional model downloads.

### PowerToys Command Palette (optional, for Developer Mode users)

**`Glimpse-CmdPal-<version>-win-x64.zip`** adds a *Glimpse* command to PowerToys Command Palette. The extension is an unsigned
MSIX package, which Windows only accepts with Developer Mode on. Extract it and run `Register-Extension.cmd`
(Glimpse must be installed and running).

Checksums are in `SHA256SUMS.txt`.
