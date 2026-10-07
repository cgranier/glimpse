# Glimpse

Find any screenshot by the text in it. Press **Win+Alt+S**, type a few letters of something you remember
seeing ("moca", "invoice 2024", "in:notes network"), and matching images show up with the words outlined.

Windows take on [gyotaku](https://github.com/xevrion/gyotaku). Everything stays local: OCR is the engine
built into Windows (`Windows.Media.Ocr`), the index is a SQLite FTS5 table with the trigram tokenizer, so
any 3+ character fragment matches anywhere in a word.

## Layout

```
src/Glimpse.Core   OCR, SQLite index, incremental indexer, folder watcher
src/Glimpse.Cli    `glimpse` command: index / search / ocr / stats / sources
src/Glimpse.App    WinUI 3 search window (unpackaged, Mica, global hotkey)
```

## Run

```powershell
dotnet build src/Glimpse.App
src/Glimpse.App/bin/x64/Debug/net9.0-windows10.0.19041.0/win-x64/Glimpse.exe           # window
src/Glimpse.App/bin/x64/Debug/net9.0-windows10.0.19041.0/win-x64/Glimpse.exe --hidden  # background, hotkey only

dotnet run --project src/Glimpse.Cli -- index           # all sources
dotnet run --project src/Glimpse.Cli -- search moca
```

The app indexes on startup (only new/changed files) and then watches the folders.

## Search syntax

| Query                  | Meaning                                       |
|------------------------|-----------------------------------------------|
| `moca network`         | both fragments appear (text or file name)     |
| `"network map"`        | exact phrase                                  |
| `in:notes`             | source name prefix (`in:screen` = both screenshot folders) |
| `after:2026-05`        | modified on/after (also `2026` or `2026-05-14`) |
| `before:2026-06-15`    | modified before                               |

## Keys

Enter open · Ctrl+C copy image · Ctrl+Shift+C copy path · Ctrl+E show in folder · Esc clear / hide · Ctrl+Q quit

## Config

`%LOCALAPPDATA%\Glimpse\config.json` (created on first run):

- `sources`: name + path for each folder (default: OneDrive screenshots, old screenshots, _mNOTES, Downloads)
- `hotkey`: e.g. `"Win+Alt+S"`, `"Ctrl+Alt+F"`
- `hydrateCloudFiles`: OneDrive online-only files are skipped unless this is `true` (indexing downloads them)
- `workers`: parallel OCR workers

Index lives next to it in `index.db` (about 45 MB for 8k images).

## Roadmap

- [ ] Visual search (CLIP embeddings via ONNX) — "network diagram", "dark dashboard"
- [ ] Tray icon + start with Windows
- [ ] Command Palette extension (`ss moca`)
- [ ] Settings page (sources, hotkey) in the app
- [ ] Clipboard-only snips (Win+Shift+S without auto-save)
