<p align="center"><img src="docs/logo.svg" width="112" alt=""></p>
<h1 align="center">Tanjelo</h1>

Instant file conversion for Windows. Select files in File Explorer, press **Ctrl+Shift+Space**, pick a format
from the menu at your mouse pointer, and the converted copy appears next to the original. Everything runs offline,
and nothing is ever overwritten (`photo.png` → `photo.jpg`, then `photo (2).jpg`, …).

Inspired by the Tangerine converter for macOS, and named after the tangelo, a tangerine hybrid. Tanjelo is an
independent project, not affiliated with Tangerine.

## Download

Get the latest `Tanjelo-<version>-win-x64.zip` from the [Releases page](../../releases), unzip it anywhere, and run `Tanjelo.exe`.
It needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows offers to install it if it's missing).

The app isn't code-signed yet, so the first launch may show "Windows protected your PC". Choose **More info → Run anyway**.

## Using it

| Where | How |
|---|---|
| File Explorer or the desktop | Select files → **Ctrl+Shift+Space** → type to filter (`mp3`, `pdf`…) → **Enter**, or click |
| Notification area | Click the Tanjelo icon to pick files; right-click for options |
| "Send to" menu | Turn on *Show in "Send to" menu* in the tray menu, then right-click files → Send to → Tanjelo |
| Command line | `tanj convert webp *.png` (see below) |

The shortcut is only grabbed while Explorer or the desktop is in front, so other apps keep their own Ctrl+Shift+Space.

## What converts to what

| From | To | Engine |
|---|---|---|
| PNG, JPG, WebP, AVIF, HEIC, GIF, BMP, TIFF, ICO, PSD, TGA, SVG | PNG, JPG, WebP, AVIF, GIF, BMP, TIFF, ICO (16–256 px), PDF; several images → **One PDF** | Magick.NET (built in) |
| PDF | PNG / JPG (one per page), DOCX (editable, via Word) | Windows PDF renderer (built in), Word |
| Video (MP4, MOV, MKV, WebM, AVI, WMV…) | MP4, MOV, MKV, WebM, AVI, animated GIF, and audio only as MP3, M4A, WAV, FLAC | ffmpeg; container-only changes are lossless re-wraps |
| Audio (MP3, WAV, FLAC, M4A, OGG, Opus, WMA, AIFF) | MP3, M4A, WAV, FLAC, OGG, Opus, AIFF | ffmpeg |
| Word, RTF, ODT, TXT | PDF, DOCX, DOC, RTF, ODT, TXT | Microsoft Word (or LibreOffice) |
| PowerPoint, ODP | PDF, PPTX, PPT, ODP, PNG / JPG per slide | Microsoft PowerPoint (or LibreOffice) |
| Excel, ODS, CSV | PDF, XLSX, XLS, ODS, CSV | Microsoft Excel (or LibreOffice) |
| ZIP, 7Z, RAR, TAR(.GZ/.BZ2/.XZ), ISO, CAB | Extract here, ZIP, 7Z, TAR.GZ | 7-Zip, Windows `tar`, .NET |
| Any files or folders | Compress to ZIP or 7Z | 7-Zip or .NET |

With several files selected, the menu shows the formats they can all become. Files already in the chosen format are skipped.

## Requirements

- Windows 10 or 11 (x64) with the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- Optional, detected automatically (formats that need a missing tool just don't appear, and the tray's *Converters* submenu shows what was found):
  - **ffmpeg** for audio and video: `winget install Gyan.FFmpeg.Essentials`
  - **7-Zip** for .7z/.rar and faster zips (Windows' built-in `tar` covers the rest)
  - **Microsoft Office** or **LibreOffice** for documents

## Settings

Tray menu → *Edit settings…* opens `%APPDATA%\Tanjelo\settings.json`. Changes apply as soon as you save.

```json
{
  "hotkey": "Ctrl+Shift+Space",
  "theme": "system",          // "system", "light" or "dark"
  "jpegQuality": 90,
  "webpQuality": 90,
  "pdfRenderDpi": 200,        // resolution for PDF -> image
  "ffmpegPath": null,         // only needed if ffmpeg isn't found automatically
  "sevenZipPath": null
}
```

Errors are logged to `%LOCALAPPDATA%\Tanjelo\log.txt`.

## Command line

`tanj.exe` uses the same engine:

```
tanj tools                          # which external tools were found
tanj targets <files...>             # what the selection can become
tanj convert <format> <files...>    # e.g. tanj convert mp3 talk.mp4
```

## Building

```
dotnet build Tanjelo.slnx
dotnet publish src\Tanjelo -c Release
dotnet publish src\tanj -c Release
```

The published apps land in `src\<project>\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\`, with the license
and third-party notices included. If your checkout path contains a comma, `dotnet publish -o <dir>` fails (MSBuild
reads commas as separators), so publish to the default folder and copy from there.
`tools\make-icon.ps1` regenerates the app icon.

Pushing a tag such as `v0.1.0` makes the GitHub Actions workflow build, smoke-test, and attach a zip to a new release.

| Project | What's in it |
|---|---|
| `src/Tanjelo.Core` | Formats, the conversion engine, and one converter per engine (`Converters/`) |
| `src/Tanjelo` | WPF tray app: hotkey, Explorer selection, the format menu, progress cards |
| `src/tanj` | Command-line front end |

## Notes on Office automation

Office sometimes stops on prompts that its "no alerts" setting doesn't cover. Tanjelo handles this:

- Files are opened from a temp copy. If Office has to be killed mid-open, it blacklists that path, so this keeps your real file off the list.
- Each Office call runs under a 3-minute watchdog. The watchdog only ever closes Office instances that Tanjelo started itself, never one you have open.
- When Word imports a PDF, its "Word will now convert your PDF…" notice is confirmed automatically. The "don't show again" box is never ticked.

## License

[MIT](LICENSE). Release builds include ImageMagick and other libraries under their own licences; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
