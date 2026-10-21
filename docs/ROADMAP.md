# Tanjelo roadmap

Where Tanjelo is heading. Most feature ideas come from comparing it with Tangerine for Mac (tangerineformac.com,
reviewed October 2026), which advertises 275 conversions, 26 file tools and 6 file actions, adapted to how Windows works.
Plans change: open an issue to suggest a feature or to say you're working on one.

**Effort:** S = no new UI, uses existing engines · M = a small dialog or one new library · L = an editor UI or a new engine.
Items marked *good first issue* are small and self-contained.

## Principles

Every feature should keep these true:

- **Local only.** Files never leave the computer.
- **Originals are never touched.** Results are new files beside the original (`photo (2).jpg`, never an overwrite).
- **One gesture.** Select, press the hotkey, pick. A dialog appears only when a tool can't work without input, and it
  opens with sensible defaults and remembers the last choice.
- **Optional tools stay optional.** ffmpeg, 7-Zip, Office, pandoc and similar are installed separately. When one is
  missing, its options are hidden and the menu says what to install.
- **MIT-compatible dependencies.** Anything shipped inside the app must be MIT, BSD, Apache-2.0, or LGPL as a separate DLL.
  GPL tools are only ever called as separately installed programs.

## v0.1.0 (released)

Hotkey menu in File Explorer and on the desktop, tray icon, "Send to" entry and the `tanj` CLI. Converts images, PDF pages,
audio, video, Office documents and archives; combines images into one PDF; extracts and creates archives. Light and dark themes.

## v0.2: foundations and quick wins

Build the plumbing later releases need, and close the easy gaps.

Foundations
- [x] Automated tests for `Tanjelo.Core`, run in CI with generated fixtures (M)
- [x] Tools in the engine: operations that aren't format conversions, with optional parameters (S)
- [x] Menu tabs **Convert / Tools / Actions**, switched with Tab or a second hotkey (Tangerine uses Shift / ⌘ / Control) (M)
- [x] PDF library for page-level work: PDFsharp 6.2.4, the last MIT release (6.5.0 moved to AGPL-3.0) (S)

Conversions
- [x] Subtitles: SRT ↔ VTT, and either to TXT (S, *good first issue*)
- [ ] TSV in and out, via Excel (S, *good first issue*)
- [ ] WMV and WMA output, via ffmpeg (S, *good first issue*)
- [ ] Plain TAR and single-file GZIP output (S, *good first issue*)
- [ ] PDF to images at 300 DPI by default, up from 200 (S, *good first issue*)
- [ ] Word and text documents to HTML (Word), and to JPG/PNG (convert to PDF, then render the pages) (S)

Tools that need no dialog
- [ ] Mute video (S, *good first issue*)
- [ ] Video speed presets: 0.5×, 1.5×, 2×, keeping audio pitch (S)
- [ ] Split video into equal parts (S)
- [ ] Normalize audio volume (S)
- [ ] Audio to mono / to stereo (S)
- [ ] Trim silence from the start and end of audio (S)
- [ ] Split a PDF into one file per page (S)
- [ ] Merge PDFs in selection order (S)

File actions (also for folders and mixed selections)
- [ ] Copy path, one per line (S, *good first issue*)
- [ ] Copy files to the clipboard (S, *good first issue*)
- [ ] Open in Terminal: Windows Terminal, falling back to PowerShell (S, *good first issue*)
- [ ] Move to a chosen folder (S)

## v0.3: tools with options

A shared options dialog, then the tools that need it.

- [ ] Options dialog framework: live preview where it helps, sensible defaults, remembers last settings (M)
- [ ] Compress to a target size or percentage, optionally resizing (images, video, audio) (M)
- [ ] View, edit or remove metadata: location, camera, author (images, audio, video, PDF) (M)
- [ ] Crop images: aspect presets or exact pixels (M)
- [ ] Crop video (M)
- [ ] Add a background: colour, gradient or image; padding, rounded corners, shadow (M)
- [ ] Collage: grid, row, column or featured layout (M)
- [ ] Join videos: reorder clips, save one MP4 (M)
- [ ] Save chosen video frames as full-resolution PNGs (M)
- [ ] Split video at chosen points (M)
- [ ] Audio visualizer: waveform or still image to MP4, in landscape, portrait or square (M)
- [ ] Per-channel audio volume (M)

## v0.4: more formats and sharing

- [ ] Markdown in and out, and EPUB to and from PDF, DOCX, TXT, Markdown and HTML, via pandoc as an optional tool (M)
- [ ] PDF to TXT and Markdown, via PdfPig (Apache-2.0) (M)
- [ ] PDF to PPTX, one page per image slide, via PowerPoint (M)
- [ ] HEIC output: check whether Windows' HEIF/HEVC extensions can encode it; our ImageMagick build can only read HEIC (M)
- [ ] Share through the Windows Share sheet and Nearby Sharing (M)
- [ ] Float: an always-on-top shelf to park files and drag them out later (M)

## v0.5: editors

- [ ] Photo editor with live preview: light, colour, detail, dehaze, grain, noise reduction (L)
- [ ] Annotate photos: arrows, shapes, text, numbered callouts, magnifier, freehand (L)
- [ ] Redact photos, with local face and text detection (Windows.Media.FaceAnalysis, Windows.Media.Ocr) (L)
- [ ] Trim video frame by frame, with exact timecodes (L)
- [ ] Trim audio on a waveform (L)
- [ ] Bleep sections of audio (L)
- [ ] Redact areas of a video, each with its own time range (L)
- [ ] Organize PDF pages in a thumbnail grid: reorder, rotate, duplicate, delete (L)

## v1.0: ready for everyone

- [ ] Code signing, so downloads don't trigger "Windows protected your PC" (SignPath Foundation offers free signing for open-source projects) (M)
- [ ] Installer with a Start menu entry and clean uninstall (M)
- [ ] winget package: `winget install Tanjelo` (S)
- [ ] Entry in the Windows 11 right-click menu (needs a packaged app) (M/L)
- [ ] Settings window, so nobody has to edit JSON (M)
- [ ] Update check, off by default so the app stays offline unless you opt in (S)
- [ ] Accessibility pass: screen-reader names, full keyboard use, high-contrast themes (M)
- [ ] Translations: move UI text into resource files (M)

## Later, if there's demand

- [ ] Drag-triggered menu: hold Shift while dragging a file, like Tangerine (L)
- [ ] PDF to XLSX, which needs reliable table extraction (L)

## Project and community

- [ ] CONTRIBUTING.md: building, running tests, and how to add a converter (S)
- [ ] Issue templates: bug reports that ask for `tanj tools` output, and feature requests (S)
- [ ] Screenshots or a short GIF in the README (S)

## Not planned

- Apple-only formats: Pages, RTFD, Finder text clippings
- Online or cloud conversion

## Already ahead of Tangerine

7Z archives, ICO icons, legacy Office formats (DOC, RTF, ODT, ODP, XLS), the `tanj` command-line tool, the "Send to"
entry, and a hotkey that works without dragging.
