<p align="center"><img src="docs/logo.svg" width="112" alt=""></p>
<h1 align="center">Tanjelo</h1>

<p align="center">Convert and edit files straight from File Explorer, offline.</p>

<p align="center">
  <a href="https://github.com/piupiu87/Tanjelo-File-Editor/releases/latest"><img src="https://img.shields.io/github/v/release/piupiu87/Tanjelo-File-Editor" alt="Latest release"></a>
  <a href="https://github.com/piupiu87/Tanjelo-File-Editor/actions/workflows/build.yml"><img src="https://github.com/piupiu87/Tanjelo-File-Editor/actions/workflows/build.yml/badge.svg" alt="Build"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/licence-MIT-blue" alt="MIT licence"></a>
</p>

Select files in File Explorer, press **Ctrl+Shift+Space**, and pick what they should become from the menu at your mouse
pointer. The result appears next to the original, which is never overwritten (`photo.png` → `photo.jpg`, then
`photo (2).jpg`, …). Everything runs offline.

<p align="center">
  <img src="docs/images/menu.png" height="340" alt="Tanjelo's menu for lakeside.jpg, on its Tools tab: Compress, Metadata, Crop, Add background, Redact, Annotate and Edit photo, each with a short description">
  <img src="docs/images/annotate.png" height="340" alt="The Annotate editor with a caption, an arrow pointing at the sun, two numbered callouts, a box around the trees and a magnifier on the water">
</p>

Inspired by the Tangerine converter for macOS, and named after the tangelo, a tangerine hybrid. Tanjelo is an
independent project, not affiliated with Tangerine.

## What it does

- **Convert** images, PDFs, video, audio, Office documents, Markdown, EPUB, HTML, subtitles and archives.
  See [what converts to what](#what-converts-to-what).
- **Tools** do one job on a copy: compress to a size, crop, mute, change the speed, even out the volume, split or merge
  PDFs, join videos, save frames, remove the location and other metadata, and more.
- **Editors** open a window for work by hand: organize PDF pages, redact faces and text in photos, annotate
  screenshots, edit photos with live sliders, and trim and bleep video and audio, or redact video, on a timeline.
- **Actions** work on the files themselves: copy their paths, open a terminal there, share them, move them, or park
  them on the **Float shelf** and drag copies out later.
- **`tanj`**, the command-line tool, does all of it from scripts.

## Download

From the [Releases page](../../releases/latest):

- **`Tanjelo-<version>-Setup.exe`** installs Tanjelo for you alone, with no administrator needed: a Start menu entry,
  `tanj` on your PATH, and an uninstaller in Settings → Apps (which keeps your settings, in case you come back). Once
  it's listed on winget, `winget install Tanjelo` does the same.
- **`Tanjelo-<version>-win-x64.zip`** is the same, to unzip anywhere and run `Tanjelo.exe` from, for a USB stick or
  a PC where you'd rather not install.

Both carry the .NET runtime inside, so there's nothing else to install first.

The app isn't code-signed yet, so the first launch may show "Windows protected your PC". Choose
**More info → Run anyway**. See the [code signing policy](#code-signing-policy).

## Using it

| Where | How |
|---|---|
| File Explorer or the desktop | Select files → **Ctrl+Shift+Space** → type to filter (`mp3`, `pdf`…) → **Enter**, or click |
| Notification area | Click the Tanjelo icon to pick files; right-click for options |
| "Send to" menu | Turn on *Show in the "Send to" menu* in Settings, then right-click files → Send to → Tanjelo |
| Windows 11 right-click menu | In a signed release, turn on *Show in the Windows 11 right-click menu* in Settings, then right-click files → **Tanjelo…** for the same menu as the shortcut |
| Float shelf | Drop files on the shelf at the right edge of the screen, drag copies out later (hold Shift to move them), or press the shortcut there to convert them |
| Command line | `tanj convert webp *.png` (see [Command line](#command-line)) |

The menu has three tabs: **Convert**, **Tools** and **Actions**. Press **Tab** (or the shortcut again) to switch, or
just type: `mute` finds the Mute tool from any tab.

Tools whose names end in "…" need a few settings first, so they open a small options dialog: it starts from the
settings you used last time, shows a live preview where that helps, and runs on **Enter** (**Esc** cancels). Editors
open in a window of their own for one file.

The shortcut is only grabbed while Explorer or the desktop is in front, so other apps keep their own Ctrl+Shift+Space.

Everything works from the keyboard, with a focus ring showing where Tab has got to, and with a screen reader such as
Narrator, which reads out the menu's entries as the arrows move and each result as it appears. With a Windows contrast
theme on, Tanjelo uses that theme's colours.

Tanjelo is in English for now. Its words are in resource files, ready to translate: [CONTRIBUTING.md](CONTRIBUTING.md#words-and-translations)
says how to add a language.

## What converts to what

| From | To | Needs |
|---|---|---|
| Images: PNG, JPG, WebP, AVIF, HEIC, GIF, BMP, TIFF, ICO, PSD, TGA, SVG | PNG, JPG, WebP, AVIF, HEIC, GIF, BMP, TIFF, ICO, PDF; several at once to **One PDF** or **One PPTX**; TXT (the text in the picture) | Built in; HEIC output needs Windows' HEIF and HEVC extensions |
| PDF | PNG or JPG per page, TXT, Markdown, PPTX, DOCX, EPUB | Built in; Word for DOCX, pandoc for EPUB |
| Video: MP4, MOV, MKV, WebM, AVI, WMV… | MP4, MOV, MKV, WebM, AVI, WMV, animated GIF; the sound alone as MP3, M4A, WAV, FLAC | ffmpeg |
| Audio: MP3, WAV, FLAC, M4A, OGG, Opus, WMA, AIFF | MP3, M4A, WAV, FLAC, OGG, Opus, AIFF, WMA | ffmpeg |
| Word, RTF, ODT, TXT | PDF, DOCX, DOC, RTF, ODT, TXT, HTML, PNG or JPG per page, Markdown, EPUB | Word or LibreOffice; pandoc for Markdown and EPUB |
| PowerPoint, ODP | PDF, PPTX, PPT, ODP, PNG or JPG per slide | PowerPoint or LibreOffice |
| Excel, ODS, CSV, TSV | PDF, XLSX, XLS, ODS, CSV, TSV | Excel or LibreOffice |
| Markdown | HTML, PDF, TXT, DOCX, EPUB | Built in; pandoc for DOCX and EPUB |
| EPUB | DOCX, Markdown, TXT, HTML, PDF | pandoc |
| HTML | PDF, Markdown, DOCX, EPUB | Built in for PDF; pandoc for the rest |
| Subtitles: SRT, VTT | SRT, VTT, TXT (just the words) | Built in |
| Archives: ZIP, 7Z, RAR, TAR (.GZ, .BZ2, .XZ), ISO, CAB, GZ | Extract here, ZIP, 7Z, TAR.GZ, TAR | Built in; 7-Zip for 7Z and RAR |
| Any files or folders | ZIP, 7Z, TAR; GZIP for a single file | Built in; 7-Zip for 7Z |

With several files selected, the menu shows the formats they can all become. Files already in the chosen format are
skipped. PDFs from Markdown, EPUB and HTML are printed with the WebView2 Runtime, which Windows 11 includes (see
[Requirements](#requirements)). [docs/FORMATS.md](docs/FORMATS.md) says what each conversion keeps and which program
does it.

## Tools, editors and actions

Tools change a copy of a file and keep its format. The copy goes beside the original, named after the change:
`talk-muted.mp4`, `song-normalized.mp3`, `clip-part1.mp4`, `Merged.pdf`. [docs/TOOLS.md](docs/TOOLS.md) describes
every tool, editor and action, with the settings `tanj` takes.

**Quick tools** run straight away: mute a video (without re-encoding), play it at 0.5×, 1.5× or 2× speed keeping the
pitch, or split it into 2, 3 or 4 equal parts; even out a recording's volume, make it mono or stereo, or trim the
silence from its ends; split a PDF into pages, or merge several.

**Tools with settings** open a dialog with a preview:

| Tool | Works on | What it does |
|---|---|---|
| Compress… | Images, video, audio | Shrinks a file to a percentage of its size or to a size in MB |
| Metadata… | JPG, PNG, WebP, audio, video, PDF | Shows the details, and saves a copy without the location, the camera or the rest, or with a new title or author |
| Crop… | Images, video | Crops to an aspect ratio, an exact size, or a box drawn on the preview |
| Add background… | Images | Puts a colour, a gradient or a picture behind it, with space around, rounded corners and a shadow |
| Collage… | Two or more images | Lays them out in a grid, a row, a column, or with one featured |
| Join videos… | Two or more videos | Joins them in any order, without re-encoding when they match |
| Save frames… | Video | Saves full-size PNGs of moments picked on a slider |
| Split at… | Video | Starts a new part at each moment picked, cut without re-encoding where keyframes allow |
| Make a video… | Audio | Makes a video of the sound, with a moving waveform or a still picture |
| Channel volume… | Audio, video | Sets the left and right volume, from 0% to 400% |

<p align="center"><img src="docs/images/add-background.png" width="480" alt="The Add background dialog: a preview of the picture on a diagonal gradient with space around, rounded corners and a soft shadow, beside its settings"></p>

**Editors** open a window of their own, for one file:

| Editor | Works on | What you do |
|---|---|---|
| Organize pages… | PDF | Reorder, turn, copy, delete and add pages, shown as thumbnails |
| Redact… | Images | Cover faces (side-on, masked and in crowds too), words or any area for good; revealing metadata goes too |
| Annotate… | Images | Add arrows, shapes, text, numbered callouts, a magnifier, and pen and highlighter strokes |
| Edit photo… | Images | Adjust light, colour, detail and effects with sliders over a live preview |
| Trim… | Video, audio | Cut video to the exact frame, without re-encoding when the cuts fall on keyframes, and sound to the sample |
| Bleep… | Audio, video | Cover stretches of the sound, each with a tone of its own pitch or silence, and hear them before saving |
| Redact… | Video | Hide areas for stretches of time, found faces too, with boxes that can follow a face |

<p align="center"><img src="docs/images/trim.png" width="680" alt="The Trim editor in the dark theme: a video above a timeline of thumbnails and the sound's waveform, with the part kept outlined between two handles"></p>

**Actions** work with the files and folders themselves: Copy path, Copy, Open in Terminal, Share…, Float (the shelf)
and Move to….

## Requirements

- Windows 10 (version 2004 or later) or 11, x64. Nothing else is needed: the .NET runtime comes with Tanjelo.
  Redact finds and follows faces best on Windows 11; Windows 10 can't run its face finder, so there Windows' own face
  detection finds only upright faces facing the camera.
- Optional programs, found automatically. Formats that need a missing one just don't appear, and Settings and the
  tray's *Converters* submenu show what was found.
  - **ffmpeg** for audio and video: `winget install Gyan.FFmpeg.Essentials`
  - **7-Zip** for .7z and .rar, and faster zips. Windows' built-in `tar` covers the rest.
  - **Microsoft Office** or **LibreOffice** for documents.
  - **HEIF Image Extensions** and **HEVC Video Extensions** from the Microsoft Store, for HEIC output (reading HEIC
    needs nothing). HEVC costs about US$1 unless the PC came with it; `tanj tools` says whether HEIC can be written.
  - **pandoc** for Word, EPUB and Markdown-to-DOCX conversions: `winget install JohnMacFarlane.Pandoc`. When it makes
    a DOCX or an EPUB, pandoc downloads the pictures a Markdown or HTML file links to on the web, so they can go inside
    the file (it never uploads anything); pictures on your computer are used as they are.
  - The **WebView2 Runtime** (Edge's engine) prints Markdown, EPUB and HTML to PDF. Windows 11 has it; on Windows 10,
    `winget install Microsoft.EdgeWebView2Runtime`. It runs with scripts off and every web address blocked, and keeps
    its cache in `%LOCALAPPDATA%\Tanjelo\WebView2`.

## Settings

Tray menu → *Settings…* opens the settings window: the menu's and the shelf's shortcuts (click a box and press the
keys), the theme, the quality of JPG, WebP and HEIC pictures and of PDF pages saved as pictures, where ffmpeg and 7-Zip
are when they aren't found automatically, which optional programs were found, *Start with Windows*, the "Send to"
entry, the Windows 11 menu entry (in signed releases) and the [update check](#privacy). Changes apply at once.

The settings live in `%APPDATA%\Tanjelo\settings.json` (*Open settings file* in the window), which can still be edited
by hand; changes apply as soon as you save it.

```json
{
  "hotkey": "Ctrl+Shift+Space",
  "floatHotkey": null,        // e.g. "Ctrl+Shift+F" to show or hide the Float shelf from anywhere
  "theme": "system",          // "system", "light" or "dark"
  "jpegQuality": 90,
  "webpQuality": 90,
  "heicQuality": 90,
  "pdfRenderDpi": 300,        // resolution for PDF -> image
  "ffmpegPath": null,         // only needed if ffmpeg isn't found automatically
  "sevenZipPath": null,
  "checkForUpdates": false    // ask GitHub once a day whether a newer version is out
}
```

The options dialogs remember each tool's last settings in `%APPDATA%\Tanjelo\tools.json`; delete it to go back to the
defaults. Errors are logged to `%LOCALAPPDATA%\Tanjelo\log.txt`.

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user or
the person installing or operating it.

Tanjelo converts and edits files on your PC: your files never leave it, and nothing about you or how you use Tanjelo is
collected (no account, no telemetry, no crash reports). The only network use is what you ask for:

- **The update check**, off unless you tick *Check for a new version once a day* in Settings. Then Tanjelo asks GitHub
  (`api.github.com`) once a day for the number of the latest release, sending nothing but the request itself, and shows
  a notice with a link to the release page when there's a newer one. It never downloads or installs anything. As with
  any visit to a website, GitHub sees the request come from your IP address. What it found last is kept in
  `%LOCALAPPDATA%\Tanjelo\update.json`. This is the only network request Tanjelo makes itself.
- When **pandoc** turns a Markdown or HTML file into DOCX or EPUB, it downloads the web pictures that file links to, so
  they can go inside (it never uploads anything).
- **Share…** sends files only where you choose in Windows' Share sheet.

Office, LibreOffice, ffmpeg, 7-Zip and the WebView2 runtime run on your PC; WebView2 runs with every web address blocked.

## Code signing policy

Releases are unsigned for now. Signing is planned through [SignPath Foundation](https://signpath.org), which signs
open-source projects for free; this is how it will work.

- **Committers and reviewers:** [piupiu87](https://github.com/piupiu87)
- **Approvers:** [piupiu87](https://github.com/piupiu87)
- **What's signed:** `Tanjelo.exe`, `tanj.exe`, Tanjelo's own DLLs and the installer, built by the GitHub Actions
  release workflow from a tagged commit in this repository. Each release's signing is approved by hand. Libraries
  from other projects keep their own signatures.
- **Privacy:** see [Privacy](#privacy).

## Command line

`tanj.exe` uses the same engine:

```
tanj tools                          # which external tools were found
tanj targets <files...>             # what the selection can become, and its tools and actions
tanj options <key> <files...>       # the settings a tool takes, with their defaults and allowed values
tanj convert <key> <files...>       # e.g. tanj convert mp3 talk.mp4, tanj convert mute talk.mp4
```

Some tools take settings, shown in brackets by `tanj targets`. Override them with `--name=value`; a setting that is on
or off can be turned on with its name alone, as in `--faces`. Tools that open an options dialog in the app take the
same settings here, starting from their defaults (not the dialog's last values). [docs/TOOLS.md](docs/TOOLS.md) lists
each tool's settings.

```
tanj convert split-video-2 --parts=5 lecture.mp4
tanj convert speed-2x --factor=3 timelapse.mp4
tanj convert move-to --folder=D:\Archive *.jpg
tanj convert compress --mode=size --size=8 clip.mp4
tanj convert crop --aspect=16:9 --position=top photo.jpg
tanj convert save-frames --times=0:05,1:12.5 clip.mp4
tanj convert join-videos --order=2,1 intro.mp4 talk.mp4
tanj convert md --page-breaks=true report.pdf     # a --- line between pages
tanj convert organize-pdf --pages="3,1-2,2r90" report.pdf
tanj convert redact --faces --style=pixelate team.jpg
tanj convert annotate --spec=marks.json screenshot.png
tanj convert adjust --exposure=0.3 --clarity=20 --vibrance=25 photo.jpg
tanj convert trim --range=0:30.033-1:00 clip.mp4
tanj convert bleep --ranges="0:10-0:12, 0:20-0:21.5 silence" talk.mp4
tanj convert redact --spec=boxes.json --style=blur clip.mp4
```

When a conversion has something to add, such as how many scanned pages were read with OCR, `tanj` prints it on a
`note` line and the app adds it to the result card.

## Office automation

Office sometimes stops on prompts that its "no alerts" setting doesn't cover. Tanjelo handles this:

- Files are opened from a temp copy. If Office has to be killed mid-open, it blacklists that path, so this keeps your
  real file off the list.
- Each Office call runs under a 3-minute watchdog. The watchdog only ever closes Office instances that Tanjelo started
  itself, never one you have open.
- When Word imports a PDF, its "Word will now convert your PDF…" notice is confirmed automatically. The "don't show
  again" box is never ticked.

## Building

```
dotnet build Tanjelo.slnx
dotnet test --project tests\Tanjelo.Core.Tests
dotnet publish src\Tanjelo -c Release
dotnet publish src\tanj -c Release
```

The tests generate their own fixture files. Tests that need ffmpeg, 7-Zip, Office, LibreOffice, pandoc, the WebView2
runtime or an OCR language skip themselves when that isn't installed. [CONTRIBUTING.md](CONTRIBUTING.md) covers running
one test class and adding a converter.

The published apps land in `src\<project>\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\`, with the .NET
runtime, the licence and the third-party notices included; the releases copy both into one folder. If your checkout path contains a comma, `dotnet publish -o <dir>` fails (MSBuild
reads commas as separators), so publish to the default folder and copy from there. `tools\make-icon.ps1` regenerates
the app icon.

Pushing a tag such as `v1.0.0` makes the GitHub Actions workflow build, test and smoke-test; make the zip, the
installer (with [Velopack](https://velopack.io)'s `vpk pack`) and the winget manifests (from `winget/`); install, use and
uninstall the installer on a fresh runner with its own .NET hidden; and attach them all to a new release. Once the
SignPath secrets are added, the same workflow has the app and the installer signed first, each approved by hand on
SignPath; [.signpath/README.md](.signpath/README.md) says how it's set up.

| Project | What's in it |
|---|---|
| `src/Tanjelo.Core` | Formats, the conversion engine, and one converter per engine (`Converters/`) |
| `src/Tanjelo` | WPF tray app: hotkey, Explorer selection, the format menu, options dialogs, the editors (`Editors/`), progress cards |
| `src/tanj` | Command-line front end |
| `src/Tanjelo.ShellMenu` | The Windows 11 right-click menu entry: a small COM server, published with NativeAOT, and its sparse package |
| `tests/Tanjelo.Core.Tests` | xUnit tests for the engine and every converter, and the editors' windows, rendered and driven off-screen |

## Roadmap

v1.0 makes Tanjelo ready for everyone: an installer and a winget package, the .NET runtime inside, a settings window,
an opt-in update check, an accessibility pass and text ready to translate. It ships unsigned: code signing, and the
Windows 11 menu entry that needs it, are on hold for a later release. See [docs/ROADMAP.md](docs/ROADMAP.md) for the
full plan. Ideas and pull requests are welcome; open an issue first for anything big, and see
[CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE). Release builds include ImageMagick and other libraries under their own licences; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
