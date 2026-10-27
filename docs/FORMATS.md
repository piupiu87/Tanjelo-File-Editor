# What converts to what

The menu's **Convert** tab lists what the selected files can become. With several files selected, it shows the formats
they can all become, and files already in the chosen format are skipped. A format that needs a program you haven't
installed doesn't appear; the tray's *Converters* submenu and `tanj tools` show what was found. Results go beside the
original and never overwrite anything.

- [Images](#images)
- [PDF](#pdf)
- [Video](#video) and [audio](#audio)
- [Word, RTF, ODT and TXT](#word-rtf-odt-and-txt), [PowerPoint and ODP](#powerpoint-and-odp),
  [Excel, ODS, CSV and TSV](#excel-ods-csv-and-tsv)
- [Markdown](#markdown), [EPUB](#epub) and [HTML](#html)
- [Subtitles](#subtitles)
- [Archives](#archives), and [any files or folders](#any-files-or-folders)

## Images

PNG, JPG, WebP, AVIF, HEIC, GIF, BMP, TIFF, ICO, PSD, TGA and SVG become:

- PNG, JPG, WebP, AVIF, GIF, BMP, TIFF or PDF.
- HEIC, upright and keeping the date taken, the camera and the location. Writing HEIC needs Windows' HEIF and HEVC
  extensions; reading it needs nothing.
- ICO, from 16 to 256 px.
- Several images at once: **One PDF**, or **One PPTX** with a slide each, shaped like the first picture.
- TXT: the text in a screenshot or a photo of a page, read with Windows' OCR.

*Engine:* Magick.NET and the Open XML SDK (built in), Windows' HEIF encoder and OCR.

## PDF

- PNG or JPG, one per page.
- TXT and Markdown: the text in reading order, columns one after the other. Markdown keeps headings, bold, lists and
  paragraphs. Scanned pages are read with Windows' own OCR.
- PPTX: a slide per page, the size of the pages, with each page's text in the speaker notes. No PowerPoint needed.
- DOCX, editable, made by Word.
- EPUB: the text and headings, made by pandoc.

*Engine:* Windows' PDF renderer and OCR, PdfPig and the Open XML SDK (built in), Word, pandoc.

## Video

MP4, MOV, MKV, WebM, AVI, WMV and the other videos ffmpeg reads become:

- MP4, MOV, MKV, WebM, AVI, WMV or an animated GIF. When only the container changes, the streams are re-wrapped
  losslessly.
- The sound alone, as MP3, M4A, WAV or FLAC.

*Engine:* ffmpeg.

## Audio

MP3, WAV, FLAC, M4A, OGG, Opus, WMA and AIFF become MP3, M4A, WAV, FLAC, OGG, Opus, AIFF or WMA.

*Engine:* ffmpeg.

## Word, RTF, ODT and TXT

- PDF, DOCX, DOC, RTF, ODT, TXT and HTML.
- PNG or JPG, one per page.
- Markdown, with the pictures saved in a `name media` folder beside it, and EPUB, both made by pandoc. DOC and RTF go
  through Word first. A TXT file gets EPUB this way, but not Markdown.

*Engine:* Microsoft Word, or LibreOffice (which doesn't make HTML or pictures); pandoc.

## PowerPoint and ODP

PDF, PPTX, PPT and ODP, or PNG or JPG, one per slide.

*Engine:* Microsoft PowerPoint, or LibreOffice.

## Excel, ODS, CSV and TSV

PDF, XLSX, XLS, ODS, CSV and TSV.

*Engine:* Microsoft Excel, or LibreOffice (which doesn't make TSV).

## Markdown

- HTML: a styled page, with the pictures from your computer embedded.
- PDF: A4, or Letter where that's used, printed offline. Nothing is fetched from the web.
- TXT: the text without the markup.
- DOCX and EPUB, made by pandoc.

Tables, task lists and footnotes are included.

*Engine:* Markdig and WebView2 (built in), pandoc.

## EPUB

- DOCX.
- Markdown, with the pictures in a folder beside it.
- TXT.
- HTML: one page, pictures included.
- PDF, printed offline like Markdown.

*Engine:* pandoc, WebView2.

## HTML

- PDF: the page as it is, with its own pictures and styles, printed offline with scripts off.
- Markdown, keeping the links to its pictures, and DOCX and EPUB, titled from the page. All three are made by pandoc.

*Engine:* WebView2, pandoc.

## Subtitles

SRT and VTT become SRT, VTT or TXT (just the words).

*Engine:* built in.

## Archives

ZIP, 7Z, RAR, TAR (also .tar.gz, .tar.bz2 and .tar.xz), ISO, CAB and GZ can be extracted beside the archive, or turned
into ZIP, 7Z, TAR.GZ or TAR.

*Engine:* 7-Zip, Windows' `tar`, .NET.

## Any files or folders

- Compress to ZIP or 7Z, or pack into TAR.
- A single file to GZIP (`notes.txt.gz`).

*Engine:* 7-Zip or .NET.
