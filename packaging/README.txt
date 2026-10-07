GLIMPSE — find any screenshot or image by the text in it, or by what it looks like
====================================================================================

INSTALL
  1. Extract this zip (right-click > Extract All). Don't run it from inside the zip.
  2. Double-click Install.cmd.
     - Windows may say "Windows protected your PC": this test build isn't code-signed yet.
       Click "More info" > "Run anyway".
  3. Glimpse opens. From now on press Win+Alt+S anywhere, or use the tray icon (^ next to the clock).

  It installs for your user only, in %LOCALAPPDATA%\Programs\Glimpse. No admin rights needed.

FIRST STEPS
  - Glimpse starts indexing your Screenshots and Downloads folders right away (a few minutes for
    thousands of images). Add or remove folders in Settings (gear button, or Ctrl+,).
  - Type anything you remember seeing in an image: an error message, a name, a word on a diagram.
  - Optional: Settings > Visual search > Download (392 MB, once) lets you search by what images look
    like ("network diagram", "bar chart") and find similar images (Ctrl+M).

KEYS
  Enter open · Ctrl+C copy image · Ctrl+Shift+T copy text · Ctrl+E show in folder
  Ctrl+M more like this · Ctrl+T visual only · Esc hide · Ctrl+Q quit

PRIVACY
  Everything runs on your PC. Text is read with the OCR built into Windows, and the visual model
  runs locally. Nothing is uploaded. The only network access is the optional model download from
  huggingface.co, when you click Download.

REQUIREMENTS
  Windows 10 (1809+) or Windows 11, x64. A DirectX 12 graphics card speeds up visual search but
  isn't required.

UNINSTALL
  Settings > Apps > Installed apps > Glimpse > Uninstall, or run Uninstall.cmd in
  %LOCALAPPDATA%\Programs\Glimpse. Your images are never touched; you choose whether to keep the index.

COMMAND LINE
  app\glimpse-cli.exe (index, search, stats ...) — run it with no arguments for help.
