# Tanjelo roadmap

Where Tanjelo is heading. Most feature ideas come from comparing it with Tangerine for Mac (tangerineformac.com,
reviewed October 2026), which advertises 275 conversions, 26 file tools and 6 file actions, adapted to how Windows works.
The PDF tools of v1.3 and v1.4 come from comparing it with Adobe Acrobat's tools (November 2026). Plans change: open an issue to suggest a feature or to say you're working on one.

**Effort:** S = no new UI, uses existing engines · M = a small dialog or one new library · L = an editor UI or a new engine.
Items marked *good first issue* are small and self-contained.

## Principles

Every feature should keep these true:

- **Local only.** Files never leave the computer.
- **Originals are never touched.** Results are new files beside the original (`photo (2).jpg`, never an overwrite).
  Pictures taken from the clipboard live in Tanjelo's own folder while they're on the shelf; they reach your folders
  only when you save, drag or paste them.
- **One gesture.** Select, press the hotkey, pick. A dialog appears only when a tool can't work without input, and it
  opens with sensible defaults and remembers the last choice.
- **Optional tools stay optional.** ffmpeg, 7-Zip, Office, pandoc and similar are installed separately, never bundled:
  setup offers them on a page of their own, each with what it's for, and installs the ones you tick through winget.
  When one is missing, its options are hidden and the menu says what to install.
- **MIT-compatible dependencies.** Anything shipped inside the app must be MIT, BSD, Apache-2.0, or LGPL as a separate DLL.
  GPL tools are only ever called as separately installed programs.

## v0.1.0 (released)

Hotkey menu in File Explorer and on the desktop, tray icon, "Send to" entry and the `tanj` CLI. Converts images, PDF pages,
audio, video, Office documents and archives; combines images into one PDF; extracts and creates archives. Light and dark themes.

## v0.2.0: foundations and quick wins (released)

Build the plumbing later releases need, and close the easy gaps.

Foundations
- [x] Automated tests for `Tanjelo.Core`, run in CI with generated fixtures (M)
- [x] Tools in the engine: operations that aren't format conversions, with optional parameters (S)
- [x] Menu tabs **Convert / Tools / Actions**, switched with Tab or by pressing the hotkey again (Tangerine uses Shift / ⌘ / Control) (M)
- [x] PDF library for page-level work: PDFsharp 6.2.4, the last MIT release (6.5.0 moved to AGPL-3.0) (S)

Conversions
- [x] Subtitles: SRT ↔ VTT, and either to TXT (S, *good first issue*)
- [x] TSV in and out, via Excel (S, *good first issue*)
- [x] WMV and WMA output, via ffmpeg (S, *good first issue*)
- [x] Plain TAR and single-file GZIP output (S, *good first issue*)
- [x] PDF to images at 300 DPI by default, up from 200 (S, *good first issue*)
- [x] Word and text documents to HTML (Word), and to JPG/PNG (convert to PDF, then render the pages) (S)

Tools that need no dialog
- [x] Mute video (S, *good first issue*)
- [x] Video speed presets: 0.5×, 1.5×, 2×, keeping audio pitch (S)
- [x] Split video into equal parts (S)
- [x] Normalize audio volume (S)
- [x] Audio to mono / to stereo (S)
- [x] Trim silence from the start and end of audio (S)
- [x] Split a PDF into one file per page (S)
- [x] Merge PDFs in selection order (S)

File actions (also for folders and mixed selections)
- [x] Copy path, one per line (S, *good first issue*)
- [x] Copy files to the clipboard (S, *good first issue*)
- [x] Open in Terminal: Windows Terminal, falling back to PowerShell (S, *good first issue*)
- [x] Move to a chosen folder (S)

## v0.3.0: tools with options (released)

A shared options dialog, then the tools that need it.

- [x] Options dialog framework: live preview where it helps, sensible defaults, remembers last settings (M)
- [x] Compress to a target size or percentage, optionally resizing (images, video, audio) (M)
- [x] View, edit or remove metadata: location, camera, author (images, audio, video, PDF) (M)
- [x] Crop images: aspect presets or exact pixels (M)
- [x] Crop video (M)
- [x] Add a background: colour, gradient or image; padding, rounded corners, shadow (M)
- [x] Collage: grid, row, column or featured layout (M)
- [x] Join videos: reorder clips, save one MP4 (M)
- [x] Save chosen video frames as full-resolution PNGs (M)
- [x] Split video at chosen points (M)
- [x] Audio visualizer: waveform or still image to MP4, in landscape, portrait or square (M)
- [x] Per-channel audio volume (M)

## v0.4.0: more formats and sharing (released)

Documents
- [x] PDF to TXT and Markdown, via PdfPig (Apache-2.0); scanned pages read with Windows' built-in OCR (M)
- [x] PDF to PPTX, one slide per page, and several pictures to one PPTX, built with the Open XML SDK, no PowerPoint needed (M)
- [x] Markdown to HTML, PDF and TXT, built in (Markdig; PDFs printed by Windows' WebView2) (M)
- [x] Markdown and EPUB to and from DOCX, TXT, HTML and Markdown; EPUB to PDF; PDF to EPUB (text only), via pandoc as an optional tool (M)

Images
- [x] HEIC output through Windows' own encoder, when the HEIF and HEVC extensions are installed (S)

Sharing
- [x] Share through the Windows Share sheet, including Nearby Sharing (M)
- [x] Float: an always-on-top shelf to park files, drag them out later, and convert them from there (M)

## v0.5.0: picture and PDF editors (released)

A shared editor window first, then the editors that use it.

- [x] Editor window: zoom and pan, undo, save beside the original; every edit can also be applied with `tanj` (M)
- [x] Organize PDF pages in a thumbnail grid: reorder, rotate, duplicate, delete; bookmarks kept (L)
- [x] Redact photos, with local face and text detection; redactions are burned in and revealing metadata removed (L)
- [x] Annotate photos: arrows, shapes, text, numbered callouts, magnifier, freehand (L)
- [x] Photo editor with live preview: light, colour, detail, dehaze, grain, noise reduction (L)

## v0.6.0: audio and video editors (released)

A shared timeline first, then the editors that use it.

- [x] Timeline: frame-exact playback and scrubbing, thumbnails, waveform and keyframes; every edit can also be applied with `tanj` (L)
- [x] Trim video frame by frame, with exact timecodes; lossless when the cuts fall on keyframes (M)
- [x] Trim audio on a waveform (M)
- [x] Bleep sections of audio or video with a tone or silence (M)
- [x] Redact areas of a video, each with its own time range (L)

## v1.0.0: ready for everyone (released)

- [x] Settings window, so nobody has to edit JSON (M)
- [x] Update check, off by default so the app stays offline unless you opt in (S)
- [x] Accessibility pass: screen-reader names, full keyboard use, high-contrast themes (M)
- [x] Translations: move UI text into resource files (M). English only so far; CONTRIBUTING says how to add a language.
- [x] Installer with a Start menu entry and clean uninstall (M)
- [x] winget package: `winget install Tanjelo` (S). The manifests come with each release, for a pull request to
  microsoft/winget-pkgs.

v1.0 ships unsigned, so the first launch may show "Windows protected your PC" until signing comes.

## v1.1.0: clipboard pictures (released)

Screenshots and copied pictures, used without saving them first.

- [x] Shelf: drag several selected files together, Ctrl+C, Show in folder (S) (PR #6)
- [x] Read pictures from the clipboard: PNG, and bitmaps with transparency; copied files act like a selection (M)
- [x] Screenshots parked on the shelf as they're taken; Ctrl+V on the shelf; held pictures go to the Recycle Bin when taken off (M)
- [x] Menu for the clipboard picture: from the shortcut with nothing selected, its own shortcut, or the tray (M)
- [x] Results back on the clipboard, and a Copy button on every result (S)
- [x] Save to… and Save in "Folder" actions (S)
- [x] `tanj --clipboard` (S)

## v1.2.0: a clearer setup (released)

- [x] Setup wizard: install for you or for everyone on the PC, in a folder you choose, with a progress bar (L)
- [x] Optional tools on a setup page, each with what it's for: ffmpeg, pandoc, WebView2, 7-Zip, LibreOffice and the HEIC extensions, installed through winget (M)
- [x] Tanjelo's own installer and uninstaller replace Velopack; 1.1 installs move over with settings and shelf pictures kept (L)

## v1.3.0: PDF tools

Tools from Adobe Acrobat's list, made one gesture and local. Nothing new is bundled: PDFsharp, PdfPig, Magick.NET
and Windows' own PDF renderer and OCR do it all.

- [ ] Protect a PDF with a password (AES-256), optionally limiting printing, copying and changes; Remove password (M)
- [ ] Compress PDFs: the pictures inside re-encoded at high, medium or low quality (M)
- [ ] Page numbers, header and footer text, and Bates numbers that carry on across several PDFs (M)
- [ ] Watermark with text or a picture: straight, diagonal or tiled (M)
- [ ] Make scanned PDFs searchable with Windows' OCR, and searchable PDFs from pictures (M)
- [ ] Split every N pages, at bookmarks, at listed pages or under a size; save the pictures in a PDF (M)
- [ ] Crop PDF margins, automatically or by set amounts (M)
- [ ] Flatten forms and comments into the page (M)
- [ ] Merge pictures and documents with PDFs into one PDF, a bookmark for each; blank pages and pictures in Organize pages (S)

## v1.4.0: PDF editors

- [ ] Redact PDF: boxes, or words and patterns found on the pages (OCR for scans). What's covered is removed from the file and the rest stays text; a page that can't be cleaned is burned in (L)
- [ ] Fill & sign: type on pages, ticks, the date, a drawn, typed or picture signature, and the PDF's own form fields (L)
- [ ] Compare two PDFs: a side-by-side report with added and removed words marked (M/L)

## Signed releases (on hold)

- [ ] Code signing, so downloads don't trigger "Windows protected your PC" (SignPath Foundation offers free signing for
  open-source projects) (M). The release workflow's signing steps are written and stay off until the SignPath secrets
  are added ([.signpath/README.md](../.signpath/README.md)).
- [ ] Entry in the Windows 11 right-click menu (needs a signed package) (M/L). Built and tried unsigned on CI; it ships
  with the first signed release.

## Later, if there's demand

- [ ] Drag-triggered menu: hold Shift while dragging a file, like Tangerine (L)
- [ ] PDF to XLSX, which needs reliable table extraction (L)
- [ ] Certificate signatures: sign a PDF with a certificate from Windows' store, which PDFsharp can do (M)

## Project and community

- [x] CONTRIBUTING.md: building, running tests, and how to add a converter (S)
- [x] Issue templates: bug reports that ask for `tanj tools` output, and feature requests (S)
- [x] Screenshots in the README, made off-screen by `ReadmeScreenshots` (S)

## Not planned

- Apple-only formats: Pages, RTFD, Finder text clippings
- Online or cloud conversion
- PDF/A: a valid PDF/A from any PDF needs every font embedded and a validator to prove it
- Editing a PDF's text and pictures in place

## Already ahead of Tangerine

7Z archives, ICO icons, legacy Office formats (DOC, RTF, ODT, ODP, XLS), the `tanj` command-line tool, the "Send to"
entry, and a hotkey that works without dragging.
