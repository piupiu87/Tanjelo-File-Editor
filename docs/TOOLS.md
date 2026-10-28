# Tools, editors and actions

Tools change a copy of a file and keep its format. The copy goes beside the original, named after the change:
`talk-muted.mp4`, `song-normalized.mp3`, `clip-part1.mp4`, `Merged.pdf`. Tools and editors are on the menu's **Tools**
tab, actions on its **Actions** tab.

Each tool has a key for `tanj` (shown in brackets below), and its settings are passed as `--name=value`.
`tanj options <key> <files>` lists them, with their defaults and allowed values.

- [Quick tools](#quick-tools)
- [Tools with settings](#tools-with-settings): [Compress](#compress-compress), [Metadata](#metadata-metadata),
  [Crop](#crop-crop), [Add background](#add-background-background), [Collage](#collage-collage),
  [Join videos](#join-videos-join-videos), [Save frames](#save-frames-save-frames),
  [Split at](#split-at-split-video-at), [Make a video](#make-a-video-visualize),
  [Channel volume](#channel-volume-channel-volume)
- [Editors](#editors): [the timeline](#audio-and-video-editors-share-a-timeline),
  [Organize pages](#organize-pages-organize-pdf), [Redact](#redact-redact), [Annotate](#annotate-annotate),
  [Edit photo](#edit-photo-adjust), [Trim](#trim-trim), [Bleep](#bleep-bleep), [Redact video](#redact-video-redact)
- [Actions](#actions)
- [Float shelf](#float-shelf)

## Quick tools

These run as soon as they're picked.

| For | Tools | Engine |
|---|---|---|
| Video | Mute (no re-encoding) · 0.5×, 1.5× or 2× speed, keeping the pitch · split in 2, 3 or 4 equal parts (cut losslessly at keyframes when they line up) | ffmpeg |
| Audio | Normalize volume to −16 LUFS (two passes, one steady gain) · to mono · to stereo · trim silence from the start and end | ffmpeg |
| PDF | Split into one PDF per page · merge several PDFs in the order selected | PDFsharp (built in) |

Some take settings with `tanj`, shown in brackets by `tanj targets`:

```
tanj convert split-video-2 --parts=5 lecture.mp4
tanj convert speed-2x --factor=3 timelapse.mp4
```

## Tools with settings

Tools marked "…" in the menu ask for their settings first, in a dialog that starts from the settings you used last
time and shows a preview where that helps. **Enter** runs it and **Esc** cancels. With `tanj`, they take the same
settings, starting from their defaults rather than the dialog's last values.

### Compress… (`compress`)

*Images, video and audio · Magick.NET, ffmpeg*

Choose a percentage of the original size or a target size in MB, and optionally smaller dimensions.

- Images get the best quality that fits. JPG, PNG, WebP and AVIF keep their format, and a PNG drops to 256 colours if
  it must; others become JPG.
- Video becomes a two-pass H.264 MP4.
- Lossy audio keeps its format; lossless audio becomes MP3.

`tanj convert compress --mode=size --size=8 clip.mp4`

### Metadata… (`metadata`)

*JPG, PNG, WebP, audio, video and PDF · built in, ffmpeg, PDFsharp*

Shows the title, author, camera, location, dates and the rest, and saves a copy without the location (ticked by
default), the camera details or everything else, and with a new title or author. The picture data is copied byte for
byte, and media isn't re-encoded.

- MP4 and MOV copies keep only the standard tags, not phones' own.
- PDFsharp names itself as a PDF's producer.

### Crop… (`crop`)

*Images (animations included) and video · Magick.NET, ffmpeg*

Choose an aspect ratio (1:1, 4:5, 4:3, 3:2, 16:9, 9:16, 3:4, 2:3) or an exact size in pixels, and which part to keep: a
side, a corner, the centre or an exact offset. Or use a free box: drag on the preview to move it, pull its edges and
corners, or draw a new one, with any shape or one of the ratios kept.

Video is re-encoded in its own container (MP4 if ffmpeg can't write it), and its sound is copied.

`tanj convert crop --aspect=16:9 --position=top photo.jpg`. A free box is `--mode=free --x --y --width --height`,
optionally with `--lock=16:9`.

### Add background… (`background`)

*Images · Magick.NET*

A colour, a two-colour gradient or another picture behind it; space around (a share of the picture, so any resolution
looks the same), rounded corners, a soft or strong shadow, and a shape: fit, 1:1, 4:5, 16:9 or 9:16. JPGs stay JPG
unless the background is see-through; everything else becomes PNG.

### Collage… (`collage`)

*Two or more images · Magick.NET*

A grid (balanced, or a set number of columns), a row, a column, or featured (the first picture large, the rest stacked
beside it); pictures filling their cells or shown whole; the width, the space between, the background colour and the
order. Saved as `Collage.jpg`, or `Collage.png` on a see-through background.

### Join videos… (`join-videos`)

*Two or more videos · ffmpeg*

Choose the order, the picture size (like the first clip, like the largest, 1080p or 720p) and how clips of another
shape fit (black bars, or fill and crop). Clips that match are joined without re-encoding; others are encoded once to
one size and frame rate, with silence for clips without sound. Saved as `Joined.mp4`.

`tanj convert join-videos --order=2,1 intro.mp4 talk.mp4`

### Save frames… (`save-frames`)

*Video · ffmpeg*

Pick moments on a slider over the video, with the frame under it as the preview, or type them as times. Each becomes a
full-size PNG, upright even for phone video: `clip-1m05.5s.png`.

`tanj convert save-frames --times=0:05,1:12.5 clip.mp4`

### Split at… (`split-video-at`)

*Video · ffmpeg*

Pick moments the same way, each starting a new part (`clip-part1.mp4`…); the preview lists the parts. The video is cut
losslessly when keyframes fall on the moments, and otherwise re-encoded so every cut is exact.

### Make a video… (`visualize`)

*Audio · ffmpeg*

A moving waveform (in a colour you pick) or a still picture, landscape, portrait or square at full HD, on a background
colour. The picture can be any file or the audio's own cover art, and the video can cover just part of the sound.
Saved as `song-video.mp4`.

### Channel volume… (`channel-volume`)

*Audio and video · ffmpeg*

Left and right from 0% (silent) to 400%. In surround sound, every left-side and right-side channel follows its side,
and the centre and bass stay. The preview draws each channel after the change. A video's picture is copied and only
its sound re-encoded.

## Editors

Editors open in a window of their own, for work done by hand. They're on the Tools tab when one file is selected, and
work from "Send to" too.

- Pictures fit the window: the wheel zooms around the pointer, Space+drag (or the middle button) pans, Ctrl+0 fits and
  Ctrl+1 shows every pixel.
- Undo and redo with Ctrl+Z and Ctrl+Y.
- Save (Ctrl+S) writes a copy beside the original; Cancel asks first when something changed.
- Every edit can also be made with `tanj`.

### Audio and video editors share a timeline

The picture plays in Windows' own player, with its sound, over thumbnails, the shape of the sound, keyframe marks (▲)
and a playhead that always rests on a real frame. Times show as `0:01:02.345` with the frame number, and a typed time
goes to the nearest frame.

- Space plays and pauses; J, K and L play back, pause and play (press again for faster).
- ← and → move a frame (a hundredth of a second in sound), Shift+← and Shift+→ a second; Home and End go to the ends.
- The wheel zooms the timeline around the pointer, Shift+wheel scrolls it, and the bar under it shows the part in view.
- A file Windows can't play (MPEG-2, ProRes, AIFF, or HEVC without its extension) shows exact stills from ffmpeg
  straight away, then plays from a small copy made in the background and deleted when the window closes. What's saved
  is always made from the original.

### Organize pages… (`organize-pdf`)

*PDF · PDFsharp, Windows' PDF renderer*

The pages as thumbnails: drag them into a new order (Ctrl and Shift pick several), turn them, copy and delete them, add
the pages of other PDFs, and save everything or only the selected pages. Saved as `report-organized.pdf`.

- Bookmarks are kept, each opening its page's first copy; those for deleted pages go.
- A PDF's form fields and links between pages don't survive, and the window says so before saving.

**With `tanj`:** `--pages="3,1-2,5r90,1"`. Ranges run either way, r90, r180 and r270 turn a page clockwise, a page
listed twice is copied and one left out is deleted. `--with=other.pdf` adds pages written b1, b2…

```
tanj convert organize-pdf --pages="3,1-2,2r90" report.pdf
```

### Redact… (`redact`)

*Images · Magick.NET, Windows' face detection and OCR*

Draw boxes over anything to hide, or have Windows find the faces and the words (its own face detection and OCR, on this
PC). Every box can be moved, resized or removed before saving. Each is hidden for good, as a solid colour (the
safest), big pixels or a strong blur, and what you see is exactly what's saved. Saved as `photo-redacted.jpg`.

The copy also loses the EXIF thumbnail (a small copy from before), the maker note, the location, XMP and IPTC; the
camera, the date and the colour profile stay.

**With `tanj`:** `--boxes="x,y,w,h;…"`, `--faces`, `--text`, `--style=solid` (or `pixelate`, `blur`),
`--colour=#000000`.

```
tanj convert redact --faces --style=pixelate team.jpg
```

### Annotate… (`annotate`)

*Images · WPF, Magick.NET*

Arrows, lines, rectangles, ellipses, text, numbered callouts (1, 2, 3… in the order they're made), a magnifier (a 2×
loupe) and a freehand pen and highlighter, each in a colour and a line width; text and numbers come in three sizes. A
new callout or magnifier starts as big as the last one was made. Saved as `photo-annotated.jpg`.

- Marks stay editable until saving: pick one to move it, pull its handles, restyle or delete it, or double-click text
  to change it.
- Each tool shows its key on its button (V select, A arrow, L line, R rectangle, E ellipse, T text, N callout,
  M magnifier, P pen, H highlighter), and the panel lists the rest.
- Shift keeps lines to 45° and boxes square, and draws the pen and highlighter straight from where it went down,
  catching level, upright and 45° when the line is close to them.
- The marks are drawn at full size the way the editor shows them; the rest of the picture and its metadata stay as
  they were.

**With `tanj`:** `--spec=marks.json`, or the JSON itself:
`{"marks": [{"type": "arrow", "points": [[10, 10], [200, 120]], "colour": "#E5383B", "width": 8}]}`.
`tanj options annotate` lists every type.

```
tanj convert annotate --spec=marks.json screenshot.png
```

### Edit photo… (`adjust`)

*Images, except GIF and SVG · built in, Magick.NET*

Sliders for light (exposure, contrast, highlights, shadows, whites, blacks), colour (temperature, tint, vibrance,
saturation), detail (sharpness, clarity, noise reduction) and effects (dehaze, grain, vignette), over a live preview of
a screen-sized copy. Saved as `photo-edited.jpg`.

- Hold `\` or Original to compare, double-click a slider to put it back to 0, Reset all, or Auto for a starting point.
- Light, colour and the vignette are worked out in one floating-point pass, so gradients don't band. The copy keeps its
  metadata and colour profile.

**With `tanj`:** `--exposure=0.3` (stops, −3 to 3); `--contrast`, `--highlights`, `--shadows`, `--whites`, `--blacks`,
`--temperature`, `--tint`, `--vibrance`, `--saturation`, `--clarity` and `--vignette` (−100 to 100); `--sharpness`,
`--noise`, `--dehaze` and `--grain` (0 to 100).

```
tanj convert adjust --exposure=0.3 --clarity=20 --vibrance=25 photo.jpg
```

### Trim… (`trim`)

*Video, audio · ffmpeg, Windows' media player*

Drag the start and end handles on the timeline, press I and O at the playhead, or type the times. Saved as
`clip-trimmed.mp4` or `song-trimmed.mp3`.

- In video each cut goes to the nearest frame. When both cuts fall on keyframes the copy is made without re-encoding,
  which is quick and loses nothing; otherwise the part kept is re-encoded so each cut lands on its exact frame. The
  panel says which it will be, and *Snap to keyframes* keeps dragged handles on them.
- The sound stays in step, and a phone video's turn and the file's tags are kept.
- Sound on its own is trimmed on its shape, to the sample: WAV, FLAC and AIFF stay lossless, and MP3, M4A and the
  like are re-encoded at the bitrate they had, keeping their tags and cover art.

**With `tanj`:** `--range=0:30.033-1:00` (`1:00-` keeps the rest, `-1:00` the start); for video `--mode=auto`, or
`exact` to re-encode anyway, or `lossless` to widen the cuts to the keyframes around them.

```
tanj convert trim --range=0:30.033-1:00 clip.mp4
tanj convert trim --range=1:05.5- --mode=lossless clip.mp4
```

### Bleep… (`bleep`)

*Audio, video · ffmpeg, Windows' media player*

Drag on the shape of the sound to mark a stretch, then move it or pull its ends. Each stretch is covered with a tone of
its own pitch or with silence, and the timeline shows it that way: a wave marked with its pitch, or a flat line marked
Silence. Saved as `talk-bleeped.mp4`.

- Click a stretch (on the timeline or in the list) to see and change what covers it: Tone or Silence, and the pitch,
  from 200 Hz to 4 kHz. With none selected, the same controls set what new stretches get.
- **Hear it** (H) plays the selected stretch with a second either side, and whatever plays has the bleeps in. The
  sound is decoded once as the window opens (about half a second for five minutes); after that each change is ready
  to hear in moments, however long the file. A video's picture plays from the file itself, with the bleeped sound in
  step beside it.
- I and O set the selected stretch's ends at the playhead, or mark a second's stretch when none is selected; Tab selects
  the next one and Delete removes it. A stretch drawn over another covered the same way joins it; over one covered
  differently, it takes that part.
- Bleeps start and stop within about a millisecond and a half of their times; everywhere else the sound is left exactly
  as it was. A video's picture is copied, not re-encoded; only its sound is.

**With `tanj`:** `--ranges="0:10-0:12, 0:20-0:21.5"`, each stretch taking `--sound=tone` (or `silence`) and
`--pitch=1000` unless it says its own: `0:10-0:12 silence`, `0:20-0:21.5 tone 800`.

```
tanj convert bleep --ranges="0:10-0:12, 0:20-0:21.5" talk.mp4
tanj convert bleep --ranges="1:02-1:03.5 silence, 1:10-1:11 tone 800" interview.mp3
```

### Redact video… (`redact`)

*Video · ffmpeg, Windows' face detection and tracking*

Draw boxes on the picture. Each is hidden from the frame it's drawn on to the end, and has a bar under the timeline:
drag its ends, or press I and O, to set when it starts and stops. Saved as `clip-redacted.mp4`.

- Find faces adds a box over each face on the frame showing. Follow face moves the selected box with its face through
  its stretch, and ends it where the face is lost. Both use Windows' own face detection and tracking, on this PC.
- Hidden as for photos: a solid colour (the safest), big pixels or a strong blur. While paused, each box shows exactly
  what's saved.
- A box moved or resized by hand stays where it's put. Tab selects the next box, Delete removes one.
- The picture is re-encoded and the sound copied. The copy loses its location, cover pictures, subtitles and data
  tracks such as a camera's GPS log.

**With `tanj`:** `--spec=boxes.json` or the JSON itself: `{"boxes": [{"x": 10, "y": 20, "width": 100, "height": 80,
"from": 1.5, "to": 3}]}`, in pixels and seconds (without `"to"` it runs to the end, and `"track": [{"at": 1.5, "x": …,
"y": …, "width": …, "height": …}, …]` moves it in straight lines between places); `--style=solid` (or `pixelate`,
`blur`), `--colour=#000000`.

```
tanj convert redact --spec=boxes.json --style=blur clip.mp4
```

## Actions

Actions work with the selected files and folders themselves:

| Action | What it does |
|---|---|
| Copy path | Puts the full path of each item on the clipboard, one per line |
| Copy | Puts the items on the clipboard, ready to paste into a folder, an email or a chat |
| Open in Terminal | Opens Windows Terminal (or PowerShell) in each selected folder, or in the folder holding the selected files |
| Share… | Opens the Windows Share sheet for the selected files, next to the pointer: email, apps, nearby PCs and phones. Nearby Sharing appears there when it's turned on in Windows' settings |
| Float | Parks the selection on the Float shelf (below) |
| Move to… | Moves the selection into a folder you pick; names already taken there get " (2)" |

Share and Float need the app; `tanj` lists them but says so when they're run.

```
tanj convert move-to --folder=D:\Archive *.jpg
```

## Float shelf

A small panel that stays on top at the right edge of the screen, for files you'll want again in a moment. Drop files
or folders on it from anywhere, or pick **Float** on the Actions tab, and drag them out later into a folder, an email
or a chat: one, several (select them first) or all of them with **All**. The shelf holds references, not copies:
dragging out to another folder on the same drive moves the file, as it would from Explorer (hold Ctrl to copy), and
files that were moved or deleted drop off.

- Press the Tanjelo shortcut on the shelf to convert what's selected there (or everything on it).
- **Delete** or × takes an item off the shelf, **Clear** takes them all; the files themselves stay where they are.
- It doesn't take the focus when it appears and stays out of Alt+Tab and the taskbar. × or **Esc** hides it, and
  *Show shelf* in the tray menu brings it back. Set `floatHotkey` in the [settings](../README.md#settings) to show or
  hide it with a shortcut of its own.
- The list survives a restart, in `%APPDATA%\Tanjelo\float.json`.
