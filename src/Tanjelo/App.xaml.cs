using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Tanjelo.Core;
using Tanjelo.Core.Converters;
using Microsoft.Win32;

namespace Tanjelo;

/// <summary>
/// Runs in one of two modes: as a tray app (the default), or — when started with file paths, e.g. from the
/// "Send to" menu — showing one format menu, doing that one conversion, and exiting.
/// </summary>
public partial class App : Application
{
    Settings _settings = new();
    ConverterEngine? _engine;
    DateTime _engineBuilt;
    HotkeyManager? _hotkey;
    TrayIcon? _tray;
    Mutex? _singleInstance;
    FileSystemWatcher? _settingsWatcher;
    Window? _menu; // the menu, or the options dialog that follows it
    readonly List<ProgressToast> _toasts = []; // newest first; stacked upward from the bottom-right corner
    bool _oneShot;
    Shelf _shelf = new();
    FloatWindow? _float;
    int _sharing; // Share sheets still using the files

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Write(args.Exception);
        _settings = LoadSettings(notify: false);
        Theme.Preference = _settings.Theme;
        Theme.Apply(Resources);
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == UserPreferenceCategory.General) Dispatcher.BeginInvoke(() => Theme.Apply(Resources));
        };

        _shelf = Shelf.Load(FloatWindow.FilePath);
        _shelf.Changed += SaveShelf;

        var files = e.Args
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .Select(Path.GetFullPath)
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToList();
        if (files.Count > 0)
        {
            _oneShot = true;
            _ = ShowMenuAsync(files);
            return;
        }

        _singleInstance = new Mutex(true, @"Local\Tanjelo.Tray", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Tanjelo is already running. Look for its icon in the notification area.", "Tanjelo");
            Shutdown();
            return;
        }
        StartTray(showWelcome: !e.Args.Contains("--autostart"));
    }

    void StartTray(bool showWelcome)
    {
        _tray = new TrayIcon();
        _tray.ConvertFilesClicked += PickFilesAndConvert;
        _tray.ShowShelfClicked += ShowShelf;
        _tray.SettingsClicked += OpenSettings;
        _tray.QuitClicked += () => Shutdown();
        _tray.SetTools(Engine().Tools);

        _hotkey = new HotkeyManager();
        _hotkey.Pressed += OnHotkey;
        _hotkey.ShelfPressed += ToggleShelf;
        _hotkey.AlsoIn = hwnd => _float is { IsVisible: true } shelf && hwnd == shelf.Hwnd;
        _hotkey.Conflict += () => _tray.Notify("Shortcut unavailable", $"Another app is already using {_settings.Hotkey}. Choose a different one in Edit settings.");
        ApplyHotkey();
        WatchSettings();

        if (showWelcome)
            _tray.Notify("Tanjelo is ready", $"Select files in File Explorer and press {_settings.Hotkey}.");
        if (_shelf.Open)
            ShowShelf();
    }

    void ApplyHotkey()
    {
        if (Settings.TryParseHotkey(_settings.Hotkey, out var modifiers, out var key))
        {
            _hotkey!.Set(modifiers, key);
            _tray!.SetHotkey(_settings.Hotkey);
        }
        else
        {
            _tray!.Notify("Shortcut not recognised", $"Couldn't read \"{_settings.Hotkey}\". Use something like Ctrl+Shift+Space.");
        }

        // The shelf's own shortcut is optional and works everywhere.
        if (string.IsNullOrWhiteSpace(_settings.FloatHotkey))
            _hotkey!.SetShelf(null);
        else if (!Settings.TryParseHotkey(_settings.FloatHotkey, out var shelfModifiers, out var shelfKey))
            _tray!.Notify("Shortcut not recognised", $"Couldn't read the floatHotkey \"{_settings.FloatHotkey}\". Use something like Ctrl+Shift+F.");
        else if (!_hotkey!.SetShelf((shelfModifiers, shelfKey)))
            _tray!.Notify("Shortcut unavailable", $"Another app is already using {_settings.FloatHotkey}. Choose a different floatHotkey in Edit settings.");
    }

    /// <summary>The engine, rebuilt now and then so tools installed while we run (ffmpeg, 7-Zip) are picked up.</summary>
    ConverterEngine Engine()
    {
        if (_engine is null || DateTime.UtcNow - _engineBuilt > TimeSpan.FromSeconds(30))
        {
            _engine = new ConverterEngine(Toolbox.Detect(_settings.FfmpegPath, _settings.SevenZipPath), _settings.ToOptions(), new Desktop(this));
            _engineBuilt = DateTime.UtcNow;
            _tray?.SetTools(_engine.Tools);
        }
        return _engine;
    }

    void OnHotkey()
    {
        IReadOnlyList<string> files;
        var foreground = Native.GetForegroundWindow();
        if (_float is { IsVisible: true } shelf && foreground == shelf.Hwnd)
        {
            // On the shelf, the shortcut converts what's selected there, or everything on it.
            files = shelf.SelectedOrAll();
            if (files.Count == 0)
                Toast().ShowMessage("The shelf is empty", "Drop files on it first.");
            else
                _ = ShowMenuAsync(files);
            return;
        }
        try
        {
            files = ExplorerSelection.Get(foreground);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            Toast().ShowError("Couldn't read the selection", ex.Message);
            return;
        }
        if (files.Count == 0)
        {
            Toast().ShowMessage("Nothing selected", "Select one or more files in File Explorer first.");
            return;
        }
        _ = ShowMenuAsync(files);
    }

    void PickFilesAndConvert()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Choose files to convert" };
        if (dialog.ShowDialog() == true)
            _ = ShowMenuAsync(dialog.FileNames);
    }

    async Task ShowMenuAsync(IReadOnlyList<string> files)
    {
        try
        {
            _menu?.Close();
            var engine = Engine();
            (System.Windows.Input.ModifierKeys, System.Windows.Input.Key)? hotkey =
                Settings.TryParseHotkey(_settings.Hotkey, out var modifiers, out var key) ? (modifiers, key) : null;
            var menu = new FormatMenuWindow(files, engine.GetMenu(files), hotkey);
            _menu = menu;
            var target = await menu.PickAsync();
            if (_menu == menu) _menu = null;
            if (target?.FolderPrompt is not null)
                target = AskForFolder(target);
            if (target is { Options.Count: > 0 })
                target = await AskForOptionsAsync(engine, files, target);
            if (target is not null)
                await ConvertAsync(engine, files, target);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            Toast().ShowError("Something went wrong", ex.Message);
        }
        finally
        {
            ExitIfDone();
        }
    }

    /// <summary>
    /// For actions such as "Move to…": asks for a folder and returns the entry with it filled in, or null if
    /// canceled. Windows remembers the last folder picked for this dialog and opens there next time.
    /// </summary>
    static Target? AskForFolder(Target target)
    {
        var dialog = new OpenFolderDialog
        {
            Title = target.FolderPrompt,
            ClientGuid = new Guid("5E3A8C1B-7F4D-4E2A-9B6C-1D0F2A3B4C5D"),
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true)
            return null;
        return target.WithParameters(new Dictionary<string, string> { ["folder"] = dialog.FolderName });
    }

    /// <summary>For tools with settings: shows the options dialog and returns the entry with the chosen values, or null if canceled.</summary>
    async Task<Target?> AskForOptionsAsync(ConverterEngine engine, IReadOnlyList<string> files, Target target)
    {
        var memory = new ToolMemory(ToolMemory.DefaultPath);
        var dialog = new OptionsWindow(engine, files, target, memory.Recall(target));
        _menu = dialog;
        var chosen = await dialog.AskAsync();
        if (_menu == dialog) _menu = null;
        if (chosen is not null) memory.Store(chosen);
        return chosen;
    }

    async Task ConvertAsync(ConverterEngine engine, IReadOnlyList<string> files, Target target)
    {
        if (target.Quiet)
        {
            // Share and Float show their own window; a card appears only if something goes wrong.
            var quiet = await Task.Run(() => engine.RunAsync(files, target));
            foreach (var failure in quiet.Failures) Log.Write($"{target.Key}: {failure.Input}: {failure.Message}");
            if (quiet.Failures.Count > 0) Toast().ShowResult(target, files, quiet);
            return;
        }
        var toast = Toast();
        using var cancel = new CancellationTokenSource();
        toast.CancelRequested += cancel.Cancel;
        toast.ShowRunning(target, files.Count);
        var progress = new Progress<BatchProgress>(toast.Report);
        try
        {
            var result = await Task.Run(() => engine.RunAsync(files, target, progress, cancel.Token));
            foreach (var failure in result.Failures) Log.Write($"{target.Key}: {failure.Input}: {failure.Message}");
            toast.ShowResult(target, files, result);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            toast.ShowError("Conversion failed", ex.Message);
        }
        finally
        {
            toast.CancelRequested -= cancel.Cancel;
        }
    }

    ProgressToast Toast()
    {
        var area = SystemParameters.WorkArea;
        var toast = new ProgressToast { Left = area.Right - 372, Top = area.Bottom - 120 };
        _toasts.Insert(0, toast);
        toast.SizeChanged += (_, _) => Restack();
        toast.Closed += (_, _) =>
        {
            _toasts.Remove(toast);
            Restack();
            ExitIfDone();
        };
        toast.Show();
        return toast;
    }

    void Restack()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom - 12;
        foreach (var toast in _toasts)
        {
            toast.Left = area.Right - toast.ActualWidth - 12;
            toast.Top = bottom - toast.ActualHeight;
            bottom = toast.Top - 8;
        }
    }

    void ExitIfDone()
    {
        // From "Send to", the app stays while a Share sheet is still using the files or the shelf is showing.
        if (_oneShot && _menu is null && _toasts.Count == 0 && _sharing == 0 && _float?.IsVisible != true)
            Shutdown();
    }

    // ---- Float shelf and Share

    FloatWindow ShelfWindow()
    {
        if (_float is not null) return _float;
        _float = new FloatWindow(_shelf, _settings.Hotkey);
        _float.IsVisibleChanged += (_, _) =>
        {
            _shelf.Open = _float.IsVisible;
            SaveShelf();
            ExitIfDone();
        };
        return _float;
    }

    void ShowShelf() => ShelfWindow().Show();

    void ToggleShelf()
    {
        if (_float?.IsVisible == true) _float.Hide();
        else ShowShelf();
    }

    void SaveShelf()
    {
        try
        {
            _shelf.Save(FloatWindow.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"float.json: {ex.Message}");
        }
    }

    async Task ShareAsync(IReadOnlyList<string> files)
    {
        _sharing++;
        try
        {
            await ShareSheet.ShowAsync(files);
        }
        finally
        {
            _sharing--;
            ExitIfDone();
        }
    }

    /// <summary>Share and Float for the engine, which runs them off the UI thread.</summary>
    sealed class Desktop(App app) : IDesktopActions
    {
        public Task ShareAsync(IReadOnlyList<string> files) => app.Dispatcher.InvokeAsync(() => app.ShareAsync(files)).Task.Unwrap();

        public Task FloatAsync(IReadOnlyList<string> paths) => app.Dispatcher.InvokeAsync(() =>
        {
            app._shelf.Add(paths);
            app.ShowShelf();
        }).Task;
    }

    Settings LoadSettings(bool notify)
    {
        var settings = Settings.Load(out var error);
        if (error is not null)
        {
            Log.Write($"settings: {error}");
            if (notify) _tray?.Notify("Settings not loaded", error);
        }
        return settings;
    }

    void OpenSettings()
    {
        if (!File.Exists(Settings.FilePath)) _settings.Save();
        try
        {
            Process.Start(new ProcessStartInfo(Settings.FilePath) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            Process.Start("notepad.exe", $"\"{Settings.FilePath}\""); // no app associated with .json
        }
    }

    void WatchSettings()
    {
        var folder = Path.GetDirectoryName(Settings.FilePath)!;
        Directory.CreateDirectory(folder);
        // Editors save in bursts (truncate, write, rename), so wait for things to settle before reloading.
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            _settings = LoadSettings(notify: true);
            _engine = null;
            Theme.Preference = _settings.Theme;
            Theme.Apply(Resources);
            ApplyHotkey();
        };
        void Changed() => Dispatcher.BeginInvoke(() =>
        {
            debounce.Stop();
            debounce.Start();
        });
        _settingsWatcher = new FileSystemWatcher(folder, Path.GetFileName(Settings.FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _settingsWatcher.Changed += (_, _) => Changed();
        _settingsWatcher.Created += (_, _) => Changed();
        _settingsWatcher.Renamed += (_, _) => Changed();
        _settingsWatcher.EnableRaisingEvents = true;
    }

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write(e.Exception);
        e.Handled = true; // keep the tray app alive
        try { Toast().ShowError("Something went wrong", e.Exception.Message); }
        catch (Exception ex) { Log.Write(ex); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _tray?.Dispose();
        _settingsWatcher?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

static class Log
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tanjelo", "log.txt");

    public static void Write(object message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
