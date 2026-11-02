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
    static App() => KeyboardFocus.Install();

    Settings _settings = new();
    ConverterEngine? _engine;
    DateTime _engineBuilt;
    HotkeyManager? _hotkey;
    ClipboardWatcher? _watcher;
    TrayIcon? _tray;
    Mutex? _singleInstance;
    FileSystemWatcher? _settingsWatcher;
    SettingsWindow? _settingsWindow;
    UpdateCheck? _updates;
    DispatcherTimer? _updateTimer;
    bool _checkingUpdates;
    Window? _menu; // the menu, or the options dialog that follows it
    readonly List<ProgressToast> _toasts = []; // newest first; stacked upward from the bottom-right corner
    bool _oneShot;
    readonly IClipboard _clipboard = new WindowsClipboard();
    readonly ClipboardFolder _clipboardFolder = new(ClipboardFolder.DefaultPath, () => DateTime.Now);
    readonly Dictionary<string, int> _picturesInMenus = new(StringComparer.OrdinalIgnoreCase); // clipboard pictures saved for a menu that's open
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
            // General is the light/dark app setting; Accessibility and Color, a contrast theme going on or off.
            if (args.Category is UserPreferenceCategory.General or UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color)
                Dispatcher.BeginInvoke(() => Theme.Apply(Resources));
        };

        _shelf = Shelf.Load(FloatWindow.FilePath, _clipboardFolder, Recycle);
        _shelf.Changed += SaveShelf;

        var files = Selection(e.Args)
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
            MessageBox.Show(Strings.TanjeloIsAlreadyRunningLookForIts, "Tanjelo");
            Shutdown();
            return;
        }
        StartTray(showWelcome: !e.Args.Contains("--autostart"));
    }

    /// <summary>
    /// The files named on the command line, as "Send to" passes them, or listed in the file after <c>--paths-file</c>,
    /// which the Windows 11 menu entry writes when the selection is too long for a command line; that file is deleted.
    /// </summary>
    internal static IReadOnlyList<string> Selection(IReadOnlyList<string> args)
    {
        var files = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--paths-file" && i + 1 < args.Count)
            {
                var list = args[++i];
                try
                {
                    files.AddRange(File.ReadAllLines(list).Where(line => line.Length > 0));
                    File.Delete(list);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Write($"{list}: {ex.Message}");
                }
            }
            else if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                files.Add(args[i]);
            }
        }
        return files;
    }

    void StartTray(bool showWelcome)
    {
        _tray = new TrayIcon();
        _tray.ConvertFilesClicked += PickFilesAndConvert;
        _tray.ShowShelfClicked += ShowShelf;
        _tray.ClipboardClicked += () => _ = OpenClipboardMenuAsync(null);
        _tray.HasClipboardInput = WindowsClipboard.HasPictureOrFiles;
        _tray.SettingsClicked += OpenSettings;
        _tray.QuitClicked += () => Shutdown();
        _tray.SetTools(Engine().Tools);

        _hotkey = new HotkeyManager();
        _hotkey.Pressed += () => _ = OnHotkeyAsync();
        _hotkey.ShelfPressed += ToggleShelf;
        _hotkey.ClipboardPressed += OnClipboardHotkey;
        _hotkey.AlsoIn = hwnd => _float is { IsVisible: true } shelf && hwnd == shelf.Hwnd;
        _hotkey.Conflict += () => _tray.Notify(Strings.ShortcutUnavailable, string.Format(Strings.AnotherAppIsAlreadyUsingChooseA, _settings.Hotkey));
        ApplyHotkey();
        WatchSettings();
        StartUpdateChecks();
        _watcher = new ClipboardWatcher { Enabled = _settings.ParkScreenshots };
        _watcher.Screenshot += change => _ = ParkScreenshotAsync(change);
        // Only here, in the one copy of Tanjelo in the tray: pictures left in its folder by a crash, or by a menu closed
        // with Esc, belong to no shelf. Another copy, such as one started from "Send to", leaves them alone.
        Recycle(_clipboardFolder.Orphans(_shelf.Items));

        if (showWelcome)
            _tray.Notify(Strings.TanjeloIsReady, string.Format(Strings.SelectFilesInFileExplorerAndPress, _settings.Hotkey));
        if (_shelf.Open)
            ShowShelf();
    }

    void ApplyHotkey()
    {
        _settingsWindow?.ShowShortcutProblem("");
        if (Settings.TryParseHotkey(_settings.Hotkey, out var modifiers, out var key))
        {
            _hotkey!.Set(modifiers, key);
            _tray!.SetHotkey(_settings.Hotkey);
        }
        else
        {
            _tray!.Notify(Strings.ShortcutNotRecognised, string.Format(Strings.CouldnTReadUseSomethingLikeCtrl, _settings.Hotkey));
        }

        // The shelf's and the clipboard menu's own shortcuts are optional and work everywhere.
        ApplyGlobal(HotkeyManager.Global.Shelf, _settings.FloatHotkey,
            Strings.CouldnTReadTheShelfShortcutUse, Strings.AnotherAppIsAlreadyUsingChooseA2, Strings.AnotherAppIsAlreadyUsingChooseOther);
        ApplyGlobal(HotkeyManager.Global.Clipboard, _settings.ClipboardHotkey,
            Strings.CouldnTReadTheClipboardShortcutUse, Strings.AnotherAppIsAlreadyUsingChooseAClipboard, Strings.AnotherAppIsAlreadyUsingChooseOtherClipboard);
        _tray!.SetClipboardHotkey(string.IsNullOrWhiteSpace(_settings.ClipboardHotkey) ? null : _settings.ClipboardHotkey);
    }

    /// <param name="unreadable">What the tray says when the keys can't be read.</param>
    /// <param name="taken">What the tray says when another app has the keys, and <paramref name="takenHere"/> what the settings window says.</param>
    void ApplyGlobal(HotkeyManager.Global which, string? keys, string unreadable, string taken, string takenHere)
    {
        if (string.IsNullOrWhiteSpace(keys))
            _hotkey!.SetGlobal(which, null);
        else if (!Settings.TryParseHotkey(keys, out var modifiers, out var key))
            _tray!.Notify(Strings.ShortcutNotRecognised, string.Format(unreadable, keys));
        else if (!_hotkey!.SetGlobal(which, (modifiers, key)))
        {
            _tray!.Notify(Strings.ShortcutUnavailable, string.Format(taken, keys));
            _settingsWindow?.ShowShortcutProblem(string.Format(takenHere, keys));
        }
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

    async Task OnHotkeyAsync()
    {
        var foreground = Native.GetForegroundWindow();
        if (_float is { IsVisible: true } shelf && foreground == shelf.Hwnd)
        {
            // On the shelf, the shortcut converts what's selected there, or everything on it.
            var parked = shelf.SelectedOrAll();
            if (parked.Count == 0)
                Toast().ShowMessage(Strings.TheShelfIsEmpty, Strings.DropFilesOnItFirst);
            else
                await ShowMenuAsync(parked);
            return;
        }
        IReadOnlyList<string> files;
        string? folder;
        try
        {
            (files, folder) = ExplorerSelection.GetWithFolder(foreground);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            Toast().ShowError(Strings.CouldnTReadTheSelection, ex.Message);
            return;
        }
        // With nothing selected, it's for the picture or the files on the clipboard.
        if (files.Count > 0)
            await ShowMenuAsync(files);
        else
            await OpenClipboardMenuAsync(folder, Strings.NothingSelected, Strings.SelectFilesOrCopyAPictureFirst);
    }

    /// <summary>The clipboard menu's own shortcut, from any app; in Explorer or on the desktop, its folder is offered to save in.</summary>
    void OnClipboardHotkey()
    {
        string? folder = null;
        var foreground = Native.GetForegroundWindow();
        try
        {
            if (ExplorerSelection.IsFileWindow(foreground)) folder = ExplorerSelection.GetWithFolder(foreground).Folder;
        }
        catch (Exception ex)
        {
            Log.Write(ex); // the menu just goes without Save in "Folder"
        }
        _ = OpenClipboardMenuAsync(folder);
    }

    /// <summary>
    /// The menu for what's on the clipboard: copied files act like a selection, and a picture gets a menu of its own.
    /// </summary>
    /// <param name="folderInFront">The folder Save in "Folder" saves in, when the shortcut came from Explorer or the desktop.</param>
    async Task OpenClipboardMenuAsync(string? folderInFront, string? emptyHeading = null, string? emptyDetail = null)
    {
        ClipboardInput input;
        uint sequence;
        try
        {
            sequence = _clipboard.Sequence;
            input = await Task.Run(() => ClipboardInput.Choose([], folderInFront, _clipboard.Read()));
        }
        catch (Exception ex) when (ex is ConversionException or ImageMagick.MagickException)
        {
            Toast().ShowError(Strings.CouldnTReadTheClipboard, ex.Message);
            return;
        }
        if (input.Files.Count > 0)
            await ShowMenuAsync(input.Files);
        else if (input.Picture is { } picture)
            await ShowPictureMenuAsync(picture, sequence, input.SaveHere);
        else
            Toast().ShowMessage(emptyHeading ?? Strings.NothingOnTheClipboard, input.Problem ?? emptyDetail ?? Strings.CopyAPictureOrFilesFirst);
    }

    /// <summary>
    /// The menu for a picture on the clipboard. It's saved in Tanjelo's folder first, so the menu can show it, but goes on
    /// the shelf only once something is picked: Esc leaves nothing behind.
    /// </summary>
    async Task ShowPictureMenuAsync(ClipboardPicture picture, uint sequence, string? saveHere)
    {
        string file;
        bool isNew;
        try
        {
            (file, isNew) = await Task.Run(() => _clipboardFolder.Save(picture, sequence));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write(ex);
            Toast().ShowError(Strings.CouldnTReadTheClipboard, ex.Message);
            return;
        }
        _picturesInMenus[file] = _picturesInMenus.GetValueOrDefault(file) + 1;
        try
        {
            await ShowMenuAsync([file], new PictureMenu(picture, saveHere));
        }
        finally
        {
            // Dismissed, or replaced by another menu: a picture saved just for this menu goes, unless another menu
            // shows it now or it's on the shelf.
            if (--_picturesInMenus[file] == 0) _picturesInMenus.Remove(file);
            if (isNew && !_picturesInMenus.ContainsKey(file) && !_shelf.Contains(file))
                OutputPaths.TryDelete(file);
        }
    }

    /// <summary>The menu shows a picture from the clipboard: its own heading, its own entries, and picking parks it.</summary>
    sealed record PictureMenu(ClipboardPicture Picture, string? SaveHere);

    void PickFilesAndConvert()
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = Strings.ChooseFilesToConvert };
        if (dialog.ShowDialog() == true)
            _ = ShowMenuAsync(dialog.FileNames);
    }

    async Task ShowMenuAsync(IReadOnlyList<string> files, PictureMenu? picture = null)
    {
        try
        {
            // A new menu replaces one still open, but not an editor, which may hold unsaved work.
            if (_menu is not Editors.EditorWindow) _menu?.Close();
            var engine = Engine();
            (System.Windows.Input.ModifierKeys, System.Windows.Input.Key)? hotkey =
                Settings.TryParseHotkey(_settings.Hotkey, out var modifiers, out var key) ? (modifiers, key) : null;
            var model = engine.GetMenu(files);
            // Editors work on one file at a time; tanj can still apply the same edits to several.
            if (files.Count > 1) model = model with { Targets = model.Targets.Where(t => t.Editor is null).ToList() };
            if (picture is not null) model = ClipboardPicture.ForMenu(model, picture.SaveHere);
            var menu = new FormatMenuWindow(files, model, hotkey, picture is null ? null
                : (Strings.ClipboardPicture, string.Format(Strings.PngSize, picture.Picture.Width, picture.Picture.Height)));
            _menu = menu;
            var target = await menu.PickAsync();
            if (_menu == menu) _menu = null;
            var parked = picture is not null && target is not null;
            if (parked)
            {
                // Picked: the picture goes on the shelf, used now, and stays there even if the editor or dialog is
                // canceled. Its results are likely to take its place on the clipboard.
                _shelf.Add(files);
                _shelf.MarkUsed(files);
            }
            if (target?.Editor is not null)
                target = await EditAsync(engine, files[0], target);
            if (target?.FolderPrompt is not null)
                target = AskForFolder(target);
            if (target is { Options.Count: > 0, Editor: null })
                target = await AskForOptionsAsync(engine, files, target);
            if (target is not null)
                await ConvertAsync(engine, files, target);
            if (parked)
                ShowShelf(); // without taking the focus, so what was parked can be seen and dragged out
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            Toast().ShowError(Strings.SomethingWentWrong, ex.Message);
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

    /// <summary>For editors: opens the file in one and returns the entry with the edits filled in, or null if canceled.</summary>
    async Task<Target?> EditAsync(ConverterEngine engine, string file, Target target)
    {
        var window = new Editors.EditorWindow(file, target, Editors.Editor.For(engine, file, target));
        _menu = window;
        var edited = await window.EditAsync();
        if (_menu == window) _menu = null;
        return edited;
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
            _shelf.MarkUsed(files);
            // Results of pictures the shelf holds are written beside them, in Tanjelo's folder, so they're parked too.
            var held = result.Outputs.Where(_shelf.Holds).ToList();
            if (held.Count > 0)
            {
                _shelf.Add(held);
                ShowShelf();
            }
            var onShelf = held.Count > 0 && held.Count == result.Outputs.Count;
            // ...and go back on the clipboard, where the picture came from, ready to paste.
            var copied = onShelf && result.Failures.Count == 0 && _settings.CopyClipboardResults && await CopyResultsAsync(held);
            toast.ShowResult(target, files, result, onShelf, copied);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            toast.ShowError(Strings.ConversionFailed, ex.Message);
        }
        finally
        {
            toast.CancelRequested -= cancel.Cancel;
        }
    }

    /// <summary>Puts results on the clipboard off the UI thread, since a picture is made into bitmaps first; false if that failed.</summary>
    async Task<bool> CopyResultsAsync(IReadOnlyList<string> results)
    {
        try
        {
            await Task.Run(() => ClipboardResults.Copy(_clipboard, results));
            return true;
        }
        catch (Exception ex) when (ex is ConversionException or IOException or UnauthorizedAccessException)
        {
            Log.Write($"clipboard: {ex.Message}");
            return false;
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

    // ---- Screenshots, parked on the shelf as they're taken

    /// <summary>
    /// Saves the screenshot on the clipboard in Tanjelo's folder and parks it, in place of the one before when it's that one
    /// edited in Snipping Tool, then shows the shelf without taking the focus.
    /// </summary>
    async Task ParkScreenshotAsync(ClipboardChange change)
    {
        try
        {
            // The picture, even when the tool put a file there too.
            var picture = await Task.Run(() => ClipboardPicture.Choose(_clipboard.Read() with { Files = [] }).Picture);
            if (picture is null)
            {
                Log.Write($"clipboard: no picture in a screenshot from {change.Owner ?? "Print Screen"}");
                return;
            }
            var (file, _) = await Task.Run(() => _clipboardFolder.Save(picture, change.Sequence));
            var replaced = _watcher!.Decisions.Replaces(change, file, picture.Width, picture.Height, DateTime.Now,
                p => _shelf.Contains(p) && !_shelf.IsUsed(p));
            if (replaced is null || !_shelf.Replace(replaced, file)) _shelf.Add([file]);
            ShowShelf();
            _float!.ShowNotice(Strings.ScreenshotParkedOnTheShelf);
        }
        catch (Exception ex)
        {
            Log.Write(ex); // nobody asked for this, so a problem goes in the log rather than on a card
        }
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

    /// <summary>
    /// Sends files Tanjelo held to the Recycle Bin, so they can still be got back: off the UI thread, on one of the
    /// single-threaded apartments the shell expects.
    /// </summary>
    static void Recycle(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var thread = new Thread(() =>
        {
            try
            {
                Native.Recycle(paths);
            }
            catch (Exception ex)
            {
                Log.Write(ex); // a thread of its own: anything thrown here would end the app
            }
        })
        {
            IsBackground = true,
            Name = "Recycle Bin",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
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
            if (notify) _tray?.Notify(Strings.SettingsNotLoaded, error);
        }
        return settings;
    }

    void OpenSettings()
    {
        if (_settingsWindow is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var window = new SettingsWindow(_settings, ChangeSettings, new WindowsSettings(), Toolbox.Detect);
        // While a shortcut box listens, the shortcuts that work everywhere mustn't fire instead.
        window.ListeningForShortcut += listening =>
        {
            if (!listening) ApplyHotkey();
            else foreach (var which in Enum.GetValues<HotkeyManager.Global>()) _hotkey?.SetGlobal(which, null);
        };
        window.OpenFileClicked += OpenSettingsFile;
        window.PageClicked += OpenPage;
        if (_updates is not null)
        {
            var (text, link) = _updates.Describe();
            window.ShowUpdate(text, link);
        }
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Show();
        window.Activate();
    }

    /// <summary>A change from the settings window: applied at once and written to settings.json.</summary>
    void ChangeSettings(Settings settings)
    {
        var before = _settings;
        _settings = settings;
        try
        {
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"settings: {ex.Message}");
            _tray?.Notify(Strings.SettingsNotSaved, ex.Message);
        }
        ApplySettings(before);
    }

    /// <summary>Puts new settings to work; the shortcuts are registered again only when they changed.</summary>
    void ApplySettings(Settings before)
    {
        _engine = null;
        if (before.Theme != _settings.Theme)
        {
            Theme.Preference = _settings.Theme;
            Theme.Apply(Resources);
        }
        if (_tray is not null && (before.Hotkey != _settings.Hotkey || before.FloatHotkey != _settings.FloatHotkey || before.ClipboardHotkey != _settings.ClipboardHotkey))
            ApplyHotkey();
        if (_watcher is not null) _watcher.Enabled = _settings.ParkScreenshots;
        if (_settings.CheckForUpdates && !before.CheckForUpdates)
            _ = CheckForUpdatesAsync();
    }

    // ---- Update check (off unless turned on in Settings)

    void StartUpdateChecks()
    {
        _updates = new UpdateCheck(new System.Net.Http.SocketsHttpHandler(), UpdateCheck.DefaultStatePath,
            typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0), () => DateTimeOffset.Now);
        // A minute after starting, so a sign-in isn't slowed and the network is up; then every hour, asking at most once a day.
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(1);
            _ = CheckForUpdatesAsync();
        };
        _updateTimer.Start();
    }

    async Task CheckForUpdatesAsync()
    {
        if (_updates is not { } updates || _checkingUpdates || !_settings.CheckForUpdates || !updates.Due) return;
        _checkingUpdates = true;
        try
        {
            _settingsWindow?.ShowUpdate(Strings.Checking, null);
            await updates.RunIfDueAsync(_settings.CheckForUpdates);
            var (text, link) = updates.Describe();
            _settingsWindow?.ShowUpdate(text, link);
            if (updates.ShouldAnnounce)
            {
                updates.MarkAnnounced();
                _tray?.Notify(string.Format(Strings.TanjeloIsOut, updates.Newer), Strings.ClickToSeeWhatSNewOn, () => OpenPage(updates.Page));
            }
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    static void OpenPage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            Log.Write($"{url}: {ex.Message}");
        }
    }

    void OpenSettingsFile()
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
            var before = _settings;
            _settings = LoadSettings(notify: true);
            ApplySettings(before);
            _settingsWindow?.Show(_settings);
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
        try { Toast().ShowError(Strings.SomethingWentWrong, e.Exception.Message); }
        catch (Exception ex) { Log.Write(ex); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _updateTimer?.Stop();
        _hotkey?.Dispose();
        _watcher?.Dispose();
        _tray?.Dispose();
        _settingsWatcher?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
        // Settings, tool choices and the shelf are saved as they change, so the rest of the shutdown can be skipped.
        ExitGuard.EndNow(e.ApplicationExitCode);
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
