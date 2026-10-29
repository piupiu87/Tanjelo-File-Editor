# Contributing to Tanjelo

Thanks for helping. Bug reports, ideas and pull requests are all welcome. For anything big, open an issue first so we
can agree on the approach before you spend time on it; [docs/ROADMAP.md](docs/ROADMAP.md) lists what's planned, and
items marked *good first issue* are small and self-contained.

## Building and testing

You need Windows 10 or 11 (x64) and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet build Tanjelo.slnx
dotnet test --project tests\Tanjelo.Core.Tests
```

The tests make their own fixture files in `%TEMP%\tanjelo-tests` and delete them afterwards. The full run takes several
minutes, mostly for video and Office. To run one class while you work, run the test executable with xUnit's filter:

```
dotnet build tests\Tanjelo.Core.Tests
tests\Tanjelo.Core.Tests\bin\Debug\net10.0-windows10.0.19041.0\Tanjelo.Core.Tests.exe -class "Tanjelo.Core.Tests.PdfTextTests"
```

Tests that need an optional tool (ffmpeg, 7-Zip, Office, LibreOffice, pandoc, the WebView2 runtime, an OCR language or
the HEIF and HEVC extensions) skip themselves when it isn't there, so a run on a bare PC still passes. CI installs
ffmpeg and pandoc; Office tests skip there.

`tanj` is the quickest way to try a change by hand: `tanj targets <files>` shows what the menu would offer, and
`tanj convert <key> [--name=value] <files>` runs one entry.

## How the code is laid out

| Project | What's in it |
|---|---|
| `src/Tanjelo.Core` | Formats (`Formats.cs`), the engine (`ConverterEngine.cs`), optional tools (`Toolbox.cs`) and one converter per engine in `Converters/` |
| `src/Tanjelo` | The WPF tray app: hotkey, Explorer selection, the menu, options dialogs, the editors (`Editors/`), progress cards, the Share sheet, the Float shelf and the settings window |
| `src/tanj` | The command-line front end |
| `tests/Tanjelo.Core.Tests` | xUnit tests for the engine, every converter, the app's pure helpers, and the editors' windows (`Ui.cs` renders them off-screen) |

## Adding a conversion or a tool

1. Implement `IConverter` (one file at a time) or `IBatchOperation` (the whole selection at once) in
   `src/Tanjelo.Core/Converters/`. `TargetsFor` returns the menu entries (`Target`) a file can have; `ConvertAsync`
   writes the result beside the input and returns its path.
2. Name outputs with `OutputPaths.Beside` or `OutputPaths.Suffixed`, which never overwrite anything, and delete a
   half-written output if the conversion fails.
3. Throw `ConversionException` with a message fit to show the user ("This PDF is password-protected.").
4. When the conversion needs a program that may not be installed, find it in `Toolbox`, offer nothing without it, and
   say what to install in `HintFor`.
5. Tools that need settings declare them with `Target.WithOptions`; the app then shows the options dialog and `tanj`
   takes them as `--name=value`.
6. Register the class in `ConverterEngine`. When two converters offer the same key for a file, the earlier one wins.
   Work done by hand, such as drawing on a photo, gets an editor: set `Target.Editor`, and add an `Editor<T>` in
   `src/Tanjelo/Editors/` whose `Result` fills in the same parameters `tanj` takes, so the engine does the saving. Its
   tests build the window with `Ui.Host`, drive it through the methods the mouse reaches (such as
   `ZoomCanvas.PointerDown`), and save renders in light and dark to `%TEMP%\tanjelo-tests\renders`. Audio and video
   editors derive from `TimelineEditor<T>`, which brings the player, the timeline and the keys; their tests check
   frames by number with `Scratch.Numbered` and `Inspect.WhichFrame`, and go in the `Windows player` collection,
   which runs on its own.
7. Add tests next to the others, using `Scratch` for fixture files and `Env.Run` to go through the engine the way the
   app does. Skip with `Assert.SkipUnless` when an optional tool is missing.
8. Describe it in [docs/FORMATS.md](docs/FORMATS.md) or [docs/TOOLS.md](docs/TOOLS.md), add it to the short tables in
   [README.md](README.md), and list any new library in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Ground rules

- **Local only.** Files never leave the computer. Nothing may fetch from or send to the network on its own.
- **Originals are never touched.** Results are new files beside the original.
- **Licences.** Anything shipped inside the app must be MIT, BSD, Apache-2.0, or LGPL as a separate DLL. GPL tools,
  like pandoc, are only ever run as programs the user installed. Pin pre-1.0 packages to an exact version.
- **Style.** Match the code around you: its naming, its idioms and how much it comments. Comments say why, in plain
  words. Keep `dotnet build` free of warnings.
- **Commits.** One change per commit, with a message that says what changed and why. Commit with the name and address
  you want public; GitHub's noreply address works.

## Reporting bugs

Use the bug report form. It asks for the output of `tanj tools`, which shows which optional tools were found and is
often the answer. Include the kind of file and what you picked in the menu; attach a sample file only if it's safe to
share. Errors are also logged to `%LOCALAPPDATA%\Tanjelo\log.txt`.

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).
