using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Tanjelo.Core;

namespace Tanjelo;

/// <summary>
/// Every setting in one window. Each change is handed to the app at once, which applies it and writes settings.json;
/// that file stays the source, so a hand edit shows up here too.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>One of the optional programs, found or not.</summary>
    public sealed record ToolRow(string Name, bool Found, string Status)
    {
        public string Spoken => $"{Name}: {Status}";
    }

    Settings _settings;
    readonly Action<Settings> _apply;
    readonly IWindowsSettings _windows;
    readonly Func<string?, string?, Toolbox> _detect;
    readonly DispatcherTimer _pathDelay = new() { Interval = TimeSpan.FromMilliseconds(400) };
    int _detecting;
    string? _updatePage;
    bool _filling; // while the controls are being set from the settings, so their events don't change them back

    /// <summary>Raised when a shortcut box starts and stops listening for keys, so the app can let go of its own meanwhile.</summary>
    internal event Action<bool>? ListeningForShortcut;

    /// <summary>Raised by "Open settings file".</summary>
    internal event Action? OpenFileClicked;

    /// <summary>Raised with a web page to open, such as a new version's release page.</summary>
    internal event Action<string>? PageClicked;

    /// <param name="apply">Takes each changed copy of the settings: the app applies it and saves it.</param>
    /// <param name="windows">The settings Windows keeps: Start with Windows and "Send to".</param>
    /// <param name="detect">Finds the optional programs, given the ffmpeg and 7-Zip paths; run off the UI thread.</param>
    internal SettingsWindow(Settings settings, Action<Settings> apply, IWindowsSettings windows, Func<string?, string?, Toolbox> detect)
    {
        InitializeComponent();
        _settings = settings.Clone();
        _apply = apply;
        _windows = windows;
        _detect = detect;
        Subheading.Text = $"Tanjelo {Version} · changes apply at once";
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);

        MenuShortcut.Recorded += shortcut => SetShortcut(menu: true, shortcut);
        ShelfShortcut.Recorded += shortcut => SetShortcut(menu: false, shortcut);
        foreach (var box in new[] { MenuShortcut, ShelfShortcut })
        {
            box.Listening += listening => ListeningForShortcut?.Invoke(listening);
            box.Hint += ShowShortcutProblem;
        }
        ClearShelfShortcut.Click += (_, _) => SetShortcut(menu: false, null);

        ThemeSystem.Checked += (_, _) => Change(s => s.Theme = "system");
        ThemeLight.Checked += (_, _) => Change(s => s.Theme = "light");
        ThemeDark.Checked += (_, _) => Change(s => s.Theme = "dark");

        Quality(JpegSlider, JpegValue, v => $"{v}", (s, v) => s.JpegQuality = v);
        Quality(WebpSlider, WebpValue, v => $"{v}", (s, v) => s.WebpQuality = v);
        Quality(HeicSlider, HeicValue, v => $"{v}", (s, v) => s.HeicQuality = v);
        Quality(DpiSlider, DpiValue, v => $"{v} DPI", (s, v) => s.PdfRenderDpi = v);

        FfmpegPath.TextChanged += (_, _) => PathTyped(s => s.FfmpegPath = Blank(FfmpegPath.Text));
        SevenZipPath.TextChanged += (_, _) => PathTyped(s => s.SevenZipPath = Blank(SevenZipPath.Text));
        FfmpegBrowse.Click += (_, _) => Browse(FfmpegPath, "ffmpeg.exe");
        SevenZipBrowse.Click += (_, _) => Browse(SevenZipPath, "7z.exe");
        _pathDelay.Tick += (_, _) =>
        {
            _pathDelay.Stop();
            _ = FindToolsAsync();
        };

        StartupCheck.Click += (_, _) => ChangeWindows(() => _windows.StartsWithWindows = StartupCheck.IsChecked == true);
        SendToCheck.Click += (_, _) => ChangeWindows(() => _windows.InSendToMenu = SendToCheck.IsChecked == true);
        CheckUpdatesBox.Click += (_, _) => Change(s => s.CheckForUpdates = CheckUpdatesBox.IsChecked == true);
        UpdateLink.Click += (_, _) =>
        {
            if (_updatePage is { } page) PageClicked?.Invoke(page);
        };

        OpenFileButton.Click += (_, _) => OpenFileClicked?.Invoke();
        DoneButton.Click += (_, _) => Close();
        CloseButton.Click += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        SourceInitialized += (_, _) => Native.StyleWindow(new WindowInteropHelper(this).Handle, Theme.IsDark);
        ContentRendered += (_, _) => MenuShortcut.Focus();

        Show(_settings);
        ShowUpdate("", null);
        _ = FindToolsAsync();
    }

    static string Version => typeof(SettingsWindow).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    /// <summary>The settings as they are now, such as after settings.json was edited by hand. Only what differs is touched.</summary>
    internal void Show(Settings settings)
    {
        _settings = settings.Clone();
        _filling = true;
        try
        {
            if (MenuShortcut.Shortcut != settings.Hotkey && !MenuShortcut.IsKeyboardFocused) MenuShortcut.Shortcut = settings.Hotkey;
            if (ShelfShortcut.Shortcut != Blank(settings.FloatHotkey) && !ShelfShortcut.IsKeyboardFocused) ShelfShortcut.Shortcut = settings.FloatHotkey;
            ClearShelfShortcut.IsEnabled = ShelfShortcut.Shortcut is not null;
            var theme = settings.Theme.ToLowerInvariant();
            (theme == "dark" ? ThemeDark : theme == "light" ? ThemeLight : ThemeSystem).IsChecked = true;
            JpegSlider.Value = Math.Clamp(settings.JpegQuality, 1, 100);
            WebpSlider.Value = Math.Clamp(settings.WebpQuality, 1, 100);
            HeicSlider.Value = Math.Clamp(settings.HeicQuality, 1, 100);
            DpiSlider.Value = Math.Clamp(settings.PdfRenderDpi, 72, 600);
            CheckUpdatesBox.IsChecked = settings.CheckForUpdates;
            if (Blank(FfmpegPath.Text) != Blank(settings.FfmpegPath)) FfmpegPath.Text = settings.FfmpegPath ?? "";
            if (Blank(SevenZipPath.Text) != Blank(settings.SevenZipPath)) SevenZipPath.Text = settings.SevenZipPath ?? "";
            ShowWindowsSettings();
        }
        finally
        {
            _filling = false;
        }
        if (IsLoaded) Native.StyleWindow(new WindowInteropHelper(this).Handle, Theme.IsDark);
    }

    void ShowWindowsSettings()
    {
        try
        {
            StartupCheck.IsChecked = _windows.StartsWithWindows;
            SendToCheck.IsChecked = _windows.InSendToMenu;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShowWindowsProblem(ex.Message);
        }
    }

    /// <summary>Changes a copy of the settings and hands it to the app.</summary>
    void Change(Action<Settings> edit)
    {
        if (_filling) return;
        var changed = _settings.Clone();
        edit(changed);
        _settings = changed;
        _apply(changed);
    }

    void Quality(Slider slider, TextBlock value, Func<int, string> format, Action<Settings, int> set)
    {
        slider.ValueChanged += (_, _) =>
        {
            var whole = (int)Math.Round(slider.Value);
            value.Text = format(whole);
            Change(s => set(s, whole));
        };
    }

    void SetShortcut(bool menu, string? shortcut)
    {
        var box = menu ? MenuShortcut : ShelfShortcut;
        var current = menu ? _settings.Hotkey : Blank(_settings.FloatHotkey);
        var other = menu ? Blank(_settings.FloatHotkey) : _settings.Hotkey;
        if (shortcut is null && menu)
        {
            box.Shortcut = current;
            return;
        }
        if (shortcut is not null && string.Equals(shortcut, other, StringComparison.OrdinalIgnoreCase))
        {
            ShowShortcutProblem(menu ? "That's the shelf's shortcut. Choose other keys." : "That's the menu's shortcut. Choose other keys.");
            box.Shortcut = current;
            return;
        }
        box.Shortcut = shortcut;
        ClearShelfShortcut.IsEnabled = ShelfShortcut.Shortcut is not null;
        ShowShortcutProblem("");
        Change(s =>
        {
            if (menu) s.Hotkey = shortcut!;
            else s.FloatHotkey = shortcut;
        });
    }

    /// <summary>What the update check found, with the page its link opens when there's a new version; empty text hides the line.</summary>
    internal void ShowUpdate(string text, string? page)
    {
        _updatePage = page;
        // The link is an inline, which can't be hidden, so it's taken out of the line when there's nothing to see.
        UpdateStatus.Inlines.Clear();
        UpdateStatus.Inlines.Add(new System.Windows.Documents.Run(text));
        if (page is not null)
        {
            UpdateStatus.Inlines.Add(" ");
            UpdateStatus.Inlines.Add(UpdateLink);
        }
        UpdateStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A problem with a shortcut, such as another app having it; empty to clear it.</summary>
    internal void ShowShortcutProblem(string message)
    {
        ShortcutProblem.Text = message;
        ShortcutProblem.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowWindowsProblem(string message)
    {
        WindowsProblem.Text = message;
        WindowsProblem.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ChangeWindows(Action change)
    {
        ShowWindowsProblem("");
        try
        {
            change();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                   or System.Runtime.InteropServices.COMException)
        {
            ShowWindowsProblem(ex.Message);
        }
        ShowWindowsSettings();
    }

    void PathTyped(Action<Settings> edit)
    {
        if (_filling) return;
        Change(edit);
        _pathDelay.Stop();
        _pathDelay.Start();
    }

    void Browse(TextBox box, string exe)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Choose {exe}",
            Filter = $"{exe}|{exe}|Programs (*.exe)|*.exe",
            FileName = exe,
        };
        if (File.Exists(box.Text)) dialog.InitialDirectory = Path.GetDirectoryName(box.Text);
        if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName;
    }

    /// <summary>Looks for the optional programs off the UI thread and shows what was found; only the latest look is shown.</summary>
    internal async Task FindToolsAsync()
    {
        var look = ++_detecting;
        string? ffmpeg = Blank(FfmpegPath.Text), sevenZip = Blank(SevenZipPath.Text);
        Toolbox tools;
        try
        {
            tools = await Task.Run(() => _detect(ffmpeg, sevenZip));
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return;
        }
        if (look != _detecting) return;
        ShowTools(tools, ffmpeg, sevenZip);
    }

    void ShowTools(Toolbox tools, string? ffmpegPath, string? sevenZipPath)
    {
        ShowStatus(FfmpegStatus, ffmpegPath, "ffmpeg.exe", tools.Ffmpeg,
            "Not found. Audio and video conversions need it: winget install Gyan.FFmpeg.Essentials");
        ShowStatus(SevenZipStatus, sevenZipPath, "7z.exe", tools.SevenZip,
            "Not found. Windows' own zip and tar cover the rest: winget install 7zip.7zip");

        var office = new List<string>();
        if (tools.Word) office.Add("Word");
        if (tools.PowerPoint) office.Add("PowerPoint");
        if (tools.Excel) office.Add("Excel");
        OtherTools.ItemsSource = new[]
        {
            office.Count > 0 ? new ToolRow("Microsoft Office", true, "Found: " + string.Join(", ", office))
                : tools.LibreOffice is not null ? new ToolRow("LibreOffice", true, "Found: " + tools.LibreOffice)
                : new ToolRow("Office", false, "Not found. Word, PowerPoint and Excel files need Microsoft Office or LibreOffice."),
            tools.Pandoc is not null ? new ToolRow("pandoc", true, "Found: " + tools.Pandoc)
                : new ToolRow("pandoc", false, "Not found. Word, EPUB and Markdown-to-DOCX need it: winget install JohnMacFarlane.Pandoc"),
            tools.WebView2 is not null ? new ToolRow("WebView2", true, "Found: version " + tools.WebView2)
                : new ToolRow("WebView2", false, "Not found. Markdown, EPUB and HTML to PDF need it: winget install Microsoft.EdgeWebView2Runtime"),
            tools.Heic ? new ToolRow("HEIC", true, "Windows can write HEIC")
                : new ToolRow("HEIC", false, "HEIC output needs the HEIF and HEVC Video Extensions from the Microsoft Store."),
        };
    }

    static void ShowStatus(TextBlock status, string? chosen, string exe, string? found, string missing)
    {
        var text = found is not null ? "Found: " + found : missing;
        if (chosen is not null && !File.Exists(chosen))
            text = $"There's no {exe} at that path. " + (found is not null ? "Using " + found : missing);
        status.Text = text;
        status.SetResourceReference(TextBlock.ForegroundProperty, found is not null ? "Success" : "TextTertiary");
    }

    static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim().Trim('"');
}
