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

The menu has three tabs: **Convert**, **Tools** and **Actions**. Press **Tab** (or the shortcut again) to switch, or
just type: `mute` finds the Mute tool from any tab.

Tools whose names end in "…" need a few settings first, so they open a small options dialog: it starts from the
settings you used last time, shows a live preview where that helps, and runs on **Enter** (**Esc** cancels).

The shortcut is only grabbed while Explorer or the desktop is in front, so other apps keep their own Ctrl+Shift+Space.

## What converts to what

| From | To | Engine |
|---|---|---|
| PNG, JPG, WebP, AVIF, HEIC, GIF, BMP, TIFF, ICO, PSD, TGA, SVG | PNG, JPG, WebP, AVIF, GIF, BMP, TIFF, ICO (16–256 px), PDF; several images → **One PDF** | Magick.NET (built in) |
| PDF | PNG / JPG (one per page), DOCX (editable, via Word) | Windows PDF renderer (built in), Word |
| Video (MP4, MOV, MKV, WebM, AVI, WMV…) | MP4, MOV, MKV, WebM, AVI, WMV, animated GIF, and audio only as MP3, M4A, WAV, FLAC | ffmpeg; container-only changes are lossless re-wraps |
| Audio (MP3, WAV, FLAC, M4A, OGG, Opus, WMA, AIFF) | MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA | ffmpeg |
| Word, RTF, ODT, TXT | PDF, DOCX, DOC, RTF, ODT, TXT, HTML, PNG / JPG per page | Microsoft Word (or LibreOffice, without HTML and images) |
| PowerPoint, ODP | PDF, PPTX, PPT, ODP, PNG / JPG per slide | Microsoft PowerPoint (or LibreOffice) |
| Excel, ODS, CSV, TSV | PDF, XLSX, XLS, ODS, CSV, TSV | Microsoft Excel (or LibreOffice, except TSV) |
| Subtitles (SRT, VTT) | SRT, VTT, TXT (just the words) | Built in |
| ZIP, 7Z, RAR, TAR(.GZ/.BZ2/.XZ), ISO, CAB, GZ | Extract here, ZIP, 7Z, TAR.GZ, TAR | 7-Zip, Windows `tar`, .NET |
| Any files or folders | Compress to ZIP or 7Z, pack into TAR; single files to GZIP (`notes.txt.gz`) | 7-Zip or .NET |

With several files selected, the menu shows the formats they can all become. Files already in the chosen format are skipped.

## Tools and actions

Tools change a copy of a file and keep its format. The copy goes beside the original, named after the change:
`talk-muted.mp4`, `song-normalized.mp3`, `clip-part1.mp4`, `Merged.pdf`.

| For | Tools | Engine |
|---|---|---|
| Video | Mute (no re-encoding) · 0.5×, 1.5× or 2× speed, keeping the pitch · split in 2, 3 or 4 equal parts (cut losslessly at keyframes when they line up) | ffmpeg |
| Audio | Normalize volume to −16 LUFS (two passes, one steady gain) · to mono · to stereo · trim silence from the start and end | ffmpeg |
| PDF | Split into one PDF per page · merge several PDFs in the order selected | PDFsharp (built in) |

Tools marked "…" in the menu ask for their settings first, in a dialog with a preview. With `tanj`, pass the same
settings as `--name=value`; `tanj options <key> <files>` lists them.

| Tool | Works on | What you choose | Engine |
|---|---|---|---|
| Compress… (`compress`) | Images, video, audio | A percentage of the original size or a target size in MB, and optionally smaller dimensions. Images get the best quality that fits (JPG, PNG, WebP and AVIF keep their format, a PNG drops to 256 colours if it must, others become JPG); video becomes a two-pass H.264 MP4; lossy audio keeps its format and lossless audio becomes MP3 | Magick.NET, ffmpeg |
| Metadata… (`metadata`) | JPG, PNG, WebP, audio, video, PDF | Shows the title, author, camera, location, dates and the rest, and saves a copy without the location (ticked by default), camera details or everything else, and with a new title or author. The picture data is copied byte for byte and media isn't re-encoded. MP4 and MOV copies keep only the standard tags, not phones' own; PDFsharp names itself as a PDF's producer | Built in, ffmpeg, PDFsharp |
| Crop… (`crop`) | Images (animations included), video | An aspect ratio (1:1, 4:5, 4:3, 3:2, 16:9, 9:16, 3:4, 2:3) or an exact size in pixels, and which part to keep: a side, a corner, the centre or an exact offset. Or a free box: drag on the preview to move it, pull its edges and corners, or draw a new one, with any shape or one of the ratios kept (`--mode=free --x --y --width --height`, optionally `--lock=16:9`). Video is re-encoded in its own container (MP4 if ffmpeg can't write it) and its sound copied | Magick.NET, ffmpeg |
| Add background… (`background`) | Images | A colour, a two-colour gradient or another picture behind it; space around (a share of the picture, so any resolution looks the same), rounded corners, a soft or strong shadow, and a shape: fit, 1:1, 4:5, 16:9 or 9:16. JPGs stay JPG unless the background is see-through; everything else becomes PNG | Magick.NET |
| Collage… (`collage`) | Two or more images | A grid (balanced, or a set number of columns), a row, a column, or featured (the first picture large, the rest stacked beside it); pictures filling their cells or shown whole; the width, the space between, the background colour and the order. Saved as `Collage.jpg` (`.png` on a see-through background) | Magick.NET |
| Join videos… (`join-videos`) | Two or more videos | The order, the picture size (like the first clip, like the largest, 1080p or 720p) and how clips of another shape fit (black bars, or fill and crop). Clips that match are joined without re-encoding; others are encoded once to one size and frame rate, with silence for clips without sound. Saved as `Joined.mp4` | ffmpeg |
| Save frames… (`save-frames`) | Video | Moments picked on a slider over the video (with the frame under it as the preview) or typed as times. Each becomes a full-size PNG, upright even for phone video: `clip-1m05.5s.png` | ffmpeg |
| Split at… (`split-video-at`) | Video | Moments picked the same way, each starting a new part (`clip-part1.mp4`…); the preview lists the parts. Cut losslessly when keyframes fall on the moments, otherwise re-encoded so every cut is exact | ffmpeg |
| Make a video… (`visualize`) | Audio | A moving waveform (in a colour you pick) or a still picture, landscape, portrait or square at full HD, on a background colour; the picture can be any file or the audio's own cover art, and the video can cover just part of the sound. Saved as `song-video.mp4` | ffmpeg |
| Channel volume… (`channel-volume`) | Audio, video | Left and right from 0% (silent) to 400%; in surround sound every left-side and right-side channel follows its side and the centre and bass stay. The preview draws each channel after the change. A video's picture is copied and only its sound re-encoded | ffmpeg |

Actions work with the selected files and folders themselves:

| Action | What it does |
|---|---|
| Copy path | Puts the full path of each item on the clipboard, one per line |
| Copy | Puts the items on the clipboard, ready to paste into a folder, an email or a chat |
| Open in Terminal | Opens Windows Terminal (or PowerShell) in each selected folder, or in the folder holding the selected files |
| Move to… | Moves the selection into a folder you pick; names already taken there get " (2)" |

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
  "pdfRenderDpi": 300,        // resolution for PDF -> image
  "ffmpegPath": null,         // only needed if ffmpeg isn't found automatically
  "sevenZipPath": null
}
```

The options dialogs remember each tool's last settings in `%APPDATA%\Tanjelo\tools.json`; delete it to go back to the
defaults. Errors are logged to `%LOCALAPPDATA%\Tanjelo\log.txt`.

## Command line

`tanj.exe` uses the same engine:

```
tanj tools                          # which external tools were found
tanj targets <files...>             # what the selection can become, and its tools and actions
tanj options <key> <files...>       # the settings a tool takes, with their defaults and allowed values
tanj convert <key> <files...>       # e.g. tanj convert mp3 talk.mp4, tanj convert mute talk.mp4
```

Some tools take settings, shown in brackets by `tanj targets`. Override them with `--name=value`. Tools that open
an options dialog in the app take the same settings here, starting from their defaults (not the dialog's last values):

```
tanj convert split-video-2 --parts=5 lecture.mp4
tanj convert speed-2x --factor=3 timelapse.mp4
tanj convert move-to --folder=D:\Archive *.jpg
tanj convert compress --mode=size --size=8 clip.mp4
tanj convert crop --aspect=16:9 --position=top photo.jpg
tanj convert save-frames --times=0:05,1:12.5 clip.mp4
tanj convert join-videos --order=2,1 intro.mp4 talk.mp4
```

## Building

```
dotnet build Tanjelo.slnx
dotnet test --project tests\Tanjelo.Core.Tests
dotnet publish src\Tanjelo -c Release
dotnet publish src\tanj -c Release
```

The tests generate their own fixture files. Tests that need ffmpeg, 7-Zip, Office or LibreOffice skip themselves when
that tool isn't installed.

The published apps land in `src\<project>\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\`, with the license
and third-party notices included. If your checkout path contains a comma, `dotnet publish -o <dir>` fails (MSBuild
reads commas as separators), so publish to the default folder and copy from there.
`tools\make-icon.ps1` regenerates the app icon.

Pushing a tag such as `v0.1.0` makes the GitHub Actions workflow build, test, smoke-test, and attach a zip to a new release.

| Project | What's in it |
|---|---|
| `src/Tanjelo.Core` | Formats, the conversion engine, and one converter per engine (`Converters/`) |
| `src/Tanjelo` | WPF tray app: hotkey, Explorer selection, the format menu, options dialogs, progress cards |
| `src/tanj` | Command-line front end |
| `tests/Tanjelo.Core.Tests` | xUnit tests for the engine and every converter |

## Notes on Office automation

Office sometimes stops on prompts that its "no alerts" setting doesn't cover. Tanjelo handles this:

- Files are opened from a temp copy. If Office has to be killed mid-open, it blacklists that path, so this keeps your real file off the list.
- Each Office call runs under a 3-minute watchdog. The watchdog only ever closes Office instances that Tanjelo started itself, never one you have open.
- When Word imports a PDF, its "Word will now convert your PDF…" notice is confirmed automatically. The "don't show again" box is never ticked.

## Roadmap

Next up: more formats and sharing, such as Markdown and EPUB through pandoc, PDF to text, HEIC output and the Windows
Share sheet. See [docs/ROADMAP.md](docs/ROADMAP.md) for the full plan. Ideas and pull requests are welcome; open an
issue first for anything big.

## License

[MIT](LICENSE). Release builds include ImageMagick and other libraries under their own licences; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
