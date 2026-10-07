# Glimpse

Find any screenshot by the text in it. Press **Win+Alt+S**, type a few letters of something you remember
seeing ("moca", "invoice 2024", "in:notes network"), and matching images show up with the words outlined.

Windows take on [gyotaku](https://github.com/xevrion/gyotaku), plus visual search. Everything stays local: OCR is the engine
built into Windows (`Windows.Media.Ocr`), the index is a SQLite FTS5 table with the trigram tokenizer, so
any 3+ character fragment matches anywhere in a word.

## Layout

```
src/Glimpse.Core   OCR, SQLite index, incremental indexer, folder watcher
src/Glimpse.Cli    `glimpse` command: index / search / ocr / stats / sources
src/Glimpse.App    WinUI 3 search window (unpackaged, Mica, global hotkey)
```

## Install

```powershell
pwsh scripts/install.ps1                # Release build → %LOCALAPPDATA%\Programs\Glimpse, start with Windows, launch in tray
pwsh scripts/install.ps1 -NoAutoStart   # same, without the login entry
```

Re-run to update. Glimpse lives in the tray: left-click toggles the window, right-click for
Re-index now / Start with Windows / Open config folder / Quit. The X button hides to the tray.
Launching it again just brings up the running copy. Windows 11 puts new tray icons in the ^ overflow;
drag it onto the taskbar to keep it visible.

## Develop

```powershell
dotnet build src/Glimpse.App
src/Glimpse.App/bin/x64/Debug/net9.0-windows10.0.19041.0/win-x64/Glimpse.exe           # window
src/Glimpse.App/bin/x64/Debug/net9.0-windows10.0.19041.0/win-x64/Glimpse.exe --hidden  # background, hotkey only

dotnet run --project src/Glimpse.Cli -- index           # all sources
dotnet run --project src/Glimpse.Cli -- search moca
```

The app indexes on startup (only new/changed files) and then watches the folders.

## Visual search

Besides OCR text, every image is embedded with [CLIP](https://openai.com/research/clip) ViT-B/16 (ONNX, run
on the GPU through DirectML, CPU fallback), so you can search by what an image *looks like*: "network diagram",
"bar chart", "dark dashboard". About 1.5 minutes for 9k images on an RTX 2080.

Results are hybrid: **exact** text hits (a one-word query, or the words together as typed) come first, then
loose text hits and visual matches interleaved (reciprocal rank fusion). Visual matches carry a
"≈ looks like" badge. **Visual** toggle (Ctrl+T) or a `~` prefix = visual only. **Ctrl+M** = more like the
selected image.

The model is a one-time ~392 MB download into `%LOCALAPPDATA%\Glimpse\models\clip-vit-b16`:

```powershell
$dir = "$env:LOCALAPPDATA\Glimpse\models\clip-vit-b16"; mkdir $dir -Force
$base = "https://huggingface.co/Xenova/clip-vit-base-patch16/resolve/main"
foreach ($f in "vocab.json","merges.txt","onnx/text_model_quantized.onnx","onnx/vision_model.onnx") {
  curl.exe -L -o "$dir\$(Split-Path $f -Leaf)" "$base/$f" }
```

Without it, visual search is simply off. The app embeds new images after each OCR pass; from the CLI run
`glimpse embed`.

## Search syntax

| Query                  | Meaning                                       |
|------------------------|-----------------------------------------------|
| `moca network`         | both fragments appear (text or file name), plus visual matches |
| `~network diagram`     | visual only (same as the Visual toggle)       |
| `like:1234`            | images that look like image 1234 (Ctrl+M)     |
| `"network map"`        | exact phrase                                  |
| `in:notes`             | source name prefix (`in:screen` = both screenshot folders) |
| `after:2026-05`        | modified on/after (also `2026` or `2026-05-14`) |
| `before:2026-06-15`    | modified before                               |

## Keys

Win+Alt+S toggle · Enter open · Ctrl+C copy image · Ctrl+Shift+C copy path · Ctrl+E show in folder · Ctrl+T visual only · Ctrl+M more like this · Esc clear / hide · Ctrl+Q quit

## Config

`%LOCALAPPDATA%\Glimpse\config.json` (created on first run):

- `sources`: name + path for each folder (default: OneDrive screenshots, old screenshots, _mNOTES, Downloads)
- `hotkey`: e.g. `"Win+Alt+S"`, `"Ctrl+Alt+F"`
- `hydrateCloudFiles`: OneDrive online-only files are skipped unless this is `true` (indexing downloads them)
- `workers`: parallel OCR workers

Index lives next to it in `index.db` (about 45 MB for 8k images).

## Roadmap

- [x] Visual search (CLIP embeddings via ONNX) — "network diagram", "dark dashboard"
- [x] Tray icon + start with Windows
- [ ] Command Palette extension (`ss moca`)
- [ ] Settings page (sources, hotkey) in the app
- [ ] Clipboard-only snips (Win+Shift+S without auto-save)
- [ ] OCR + embed several GIF frames, not just the first
