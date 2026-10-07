# Third-party notices

Glimpse is MIT-licensed (see LICENSE). Release builds include the components below, each under its own
license. License texts ship inside each component's NuGet package and at the links given.

## Bundled in the release zips

| Component | License |
|---|---|
| .NET runtime (self-contained) | MIT — https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |
| Windows App SDK / WinUI 3 | Microsoft Software License Terms (Windows App SDK); redistribution as part of an application is permitted — https://aka.ms/windowsappsdk/license |
| ONNX Runtime (DirectML build) | MIT — https://github.com/microsoft/onnxruntime/blob/main/LICENSE |
| DirectML | Microsoft Software License Terms (DirectML); distribution in applications you build is permitted — https://github.com/microsoft/DirectML/blob/master/LICENSE |
| Microsoft.Data.Sqlite | MIT — https://github.com/dotnet/efcore/blob/main/LICENSE.txt |
| SQLitePCLRaw | Apache-2.0 — https://github.com/ericsink/SQLitePCL.raw |
| SQLite | Public domain — https://sqlite.org/copyright.html |
| C#/WinRT | MIT — https://github.com/microsoft/CsWinRT/blob/master/LICENSE |
| WebView2 SDK (pulled in by Windows App SDK) | BSD-style — https://www.nuget.org/packages/Microsoft.Web.WebView2 |

Command Palette extension zip, additionally:

| Component | License |
|---|---|
| Microsoft.CommandPalette.Extensions | MIT — https://github.com/microsoft/PowerToys/blob/main/LICENSE |
| Shmuelie.WinRTServer | MIT — https://github.com/shmuelie/Shmuelie.WinRTServer |

## Downloaded on request (not bundled)

Settings → Visual search → Download fetches, from Hugging Face:

| Component | License |
|---|---|
| CLIP ViT-B/16 model weights (OpenAI) | MIT — https://github.com/openai/CLIP/blob/main/LICENSE |
| ONNX export by Xenova | https://huggingface.co/Xenova/clip-vit-base-patch16 |

## Used, not distributed

Text recognition uses the OCR engine built into Windows (`Windows.Media.Ocr`).
Glimpse was inspired by [gyotaku](https://github.com/xevrion/gyotaku).
