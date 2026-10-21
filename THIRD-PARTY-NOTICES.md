# Third-party notices

Tanjelo's own code is MIT-licensed (see [LICENSE](LICENSE)). It builds on the components below, each under its own licence.

## Included in release downloads

| Component | License | Notes |
|---|---|---|
| [Magick.NET](https://github.com/dlemstra/Magick.NET) | Apache-2.0 | .NET bindings for ImageMagick |
| [ImageMagick](https://imagemagick.org) and the native libraries Magick.NET bundles with it (aom, brotli, cairo, libde265, libheif, libjpeg-turbo, libpng, libwebp, OpenEXR, zlib, and others) | ImageMagick License, plus each library's own license (MIT, BSD, LGPL and others) | Shipped unmodified as the separate `Magick.Native-Q8-x64.dll`. Full copyright notices and license texts are in `licenses/ImageMagick-NOTICE.txt` next to the app. |
| [PDFsharp](https://github.com/empira/PDFsharp) 6.2.4 (`PdfSharp*.dll`) | MIT, © empira Software GmbH | Splits and merges PDF pages. Version 6.2.4 is the last MIT release; PDFsharp 6.5.0 and later are AGPL-3.0 or commercial, so Tanjelo stays on 6.2.x. The package's `PdfSharp.WPFonts.dll` holds a small Segoe UI subset from an early Windows Phone SDK that PDFsharp uses to draw text; Tanjelo never draws text with it. |
| [Microsoft.Extensions.Logging.Abstractions](https://github.com/dotnet/runtime), Microsoft.Extensions.DependencyInjection.Abstractions and (next to `tanj.exe`) System.Security.Cryptography.Pkcs | MIT, © .NET Foundation | Required by PDFsharp |
| Microsoft Windows SDK projections for .NET (`Microsoft.Windows.SDK.NET.dll`, `WinRT.Runtime.dll`) | [Windows SDK license terms](https://aka.ms/WinSDKLicenseURL) | Used for the built-in Windows PDF renderer |

The .NET runtime isn't bundled; users install the [.NET Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (MIT).

## Used if installed, never bundled

Tanjelo starts these as separate programs that the user installs, and doesn't redistribute any of them:

| Program | License |
|---|---|
| [FFmpeg](https://ffmpeg.org) | LGPL-2.1 or GPL-2.0+, depending on the build |
| [7-Zip](https://www.7-zip.org) | LGPL-2.1, with the unRAR restriction for RAR code |
| [LibreOffice](https://www.libreoffice.org) | MPL-2.0 |
| Microsoft Office | Proprietary; controlled through its documented COM automation interface |
| Windows `tar.exe` (libarchive) | Part of Windows |

If you redistribute a build that bundles any of these (for example FFmpeg next to the app), that build takes on their license obligations too.
