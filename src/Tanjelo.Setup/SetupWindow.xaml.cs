using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Tanjelo.Setup;

/// <summary>
/// Setup's wizard (Welcome, Location, Installing, Done), or the uninstaller's (a page saying what goes, Uninstalling,
/// Done). The work itself is the <see cref="SetupJob"/>'s; the window shows where it's got to and asks what it can't
/// decide. The tests drive it through <see cref="GoNext"/>, <see cref="GoBack"/> and <see cref="Cancel"/>.
/// </summary>
public partial class SetupWindow : Window
{
    internal enum Page { Welcome, Location, Progress, Done, Confirm }

    public const string LicenceUrl = "https://github.com/piupiu87/Tanjelo-File-Editor/blob/main/LICENSE";

    readonly SetupJob _job;
    readonly Log _log;
    readonly Payload? _payload;
    readonly Installed? _existing;
    readonly string? _uninstallFolder;
    readonly string? _running;
    readonly ObservableCollection<StepRow> _steps = [];
    CancellationTokenSource? _cancel;
    bool _cancellable;
    bool _working;
    bool _settingUp = true;
    string? _installed;

    internal Page Current { get; private set; }

    /// <summary>The install or uninstall under way, or the last one, for the tests to wait for.</summary>
    internal Task Working { get; private set; } = Task.CompletedTask;

    /// <summary>What Setup.exe ends with: 0 once installed, 2 if cancelled before.</summary>
    internal int ExitCode { get; private set; } = ExitCodes.Cancelled;

    /// <summary>Asks a yes-or-no question; the tests answer it themselves.</summary>
    internal Func<string, bool> Ask { get; set; }

    /// <summary>Opens a web page or a file; the tests only note it.</summary>
    internal Action<string> Open { get; set; } = target =>
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }
    };

    /// <summary>Setup's wizard, for the payload inside Setup.exe.</summary>
    internal SetupWindow(SetupJob job, Payload payload, Options options, Log log) : this(job, log)
    {
        _payload = payload;
        _existing = job.Installer.Find();
        Title = Strings.TanjeloSetup;
        var (folder, scope, _) = job.Installer.Choose(_existing, options.AllUsers, options.Folder);
        (scope == Scope.Machine ? ForEveryone : ForMe).IsChecked = true;
        FolderBox.Text = folder;
        if (_existing is { } installed)
        {
            InstalledText.Text = installed.FromVelopack
                ? string.Format(Strings.TanjeloIsInstalledInSetupMovesIt, installed.Version, installed.Folder)
                : string.Format(installed.Scope == Scope.Machine ? Strings.TanjeloIsInstalledForEveryoneIn : Strings.TanjeloIsInstalledForYouIn,
                    installed.Version, installed.Folder, payload.Version);
            InstalledText.Visibility = Visibility.Visible;
        }
        if (Updating)
        {
            // Never two copies: an installed Tanjelo is updated where it is.
            ForMe.IsEnabled = ForEveryone.IsEnabled = FolderBox.IsEnabled = BrowseButton.IsEnabled = false;
            FolderNote.Text = string.Format(Strings.TanjeloIsInstalledInSoItSUpdated, _existing!.Folder);
        }
        _settingUp = false;
        UpdateFolder();
        Show(Page.Welcome);
    }

    /// <summary>The uninstaller's, for the Tanjelo in <paramref name="folder"/>.</summary>
    internal SetupWindow(SetupJob job, string folder, string? running, Log log) : this(job, log)
    {
        _uninstallFolder = folder;
        _running = running;
        Title = Strings.UninstallTanjelo;
        ConfirmText.Text = string.Format(Strings.ThisRemovesTanjeloFrom, folder);
        _settingUp = false;
        Show(Page.Confirm);
    }

    SetupWindow(SetupJob job, Log log)
    {
        _job = job;
        _log = log;
        InitializeComponent();
        Ask = question => MessageBox.Show(this, question, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        HeaderIcon.Source = AppIcon(64);
        StepList.ItemsSource = _steps;
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += ThemeChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= ThemeChanged;
        Closing += (_, e) =>
        {
            // Closing the window while it works is cancelling: it closes once the copy is undone.
            if (!_working) return;
            e.Cancel = true;
            Cancel();
        };
        SourceInitialized += (_, _) => DarkTitleBar();
    }

    bool Updating => _existing is { FromVelopack: false };

    Scope ChosenScope => ForEveryone.IsChecked == true ? Scope.Machine : Scope.User;

    /// <summary>The folder Tanjelo goes in: always one of its own, inside the one typed or chosen.</summary>
    internal string Target => Updating ? _existing!.Folder : Installer.OwnFolder(FolderBox.Text);

    // ---- Theme

    internal void ApplyTheme()
    {
        Theme.Apply(Root.Resources);
        DarkTitleBar();
    }

    void ThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color)
            Dispatcher.BeginInvoke(new Action(ApplyTheme));
    }

    void DarkTitleBar()
    {
        var window = new WindowInteropHelper(this).Handle;
        if (window == IntPtr.Zero) return;
        var dark = Theme.IsDark ? 1 : 0;
        DwmSetWindowAttribute(window, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
    }

    static ImageSource? AppIcon(int size)
    {
        using var stream = typeof(SetupWindow).Assembly.GetManifestResourceStream("app.ico");
        if (stream is null) return null;
        var icon = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return icon.Frames.OrderBy(f => Math.Abs(f.PixelWidth - size)).First();
    }

    // ---- Pages

    void Show(Page page)
    {
        Current = page;
        foreach (var (panel, which) in new (FrameworkElement, Page)[] { (WelcomePage, Page.Welcome), (LocationPage, Page.Location),
                     (ProgressPage, Page.Progress), (DonePage, Page.Done), (ConfirmPage, Page.Confirm) })
            panel.Visibility = which == page ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = page == Page.Location ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = page == Page.Progress ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = page == Page.Done ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.IsEnabled = page != Page.Progress || _cancellable;
        NextButton.IsEnabled = page != Page.Location || FolderProblem.Visibility != Visibility.Visible;
        var version = _payload?.Version ?? "";
        (Heading.Text, Subheading.Text, NextButton.Content) = page switch
        {
            Page.Welcome => (Updating ? Strings.UpdateTanjelo : Strings.InstallTanjelo, string.Format(Strings.Version, version), Strings.Next),
            Page.Location => (Strings.WhereTanjeloGoes, string.Format(Strings.Version, version), Updating ? Strings.Update : Strings.Install),
            Page.Progress when _payload is null => (Strings.UninstallingTanjelo, "", ""),
            Page.Progress => (Updating ? Strings.UpdatingTanjelo : Strings.InstallingTanjelo, string.Format(Strings.Version, version), ""),
            Page.Confirm => (Strings.UninstallTanjeloQuestion, "", Strings.Uninstall),
            _ => (Heading.Text, Subheading.Text, Strings.Finish),
        };
        Subheading.Visibility = Subheading.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        // The keyboard starts on the page's first choice, or on the button that goes on.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (page == Page.Location && !Updating) (ForEveryone.IsChecked == true ? ForEveryone : ForMe).Focus();
            else if (NextButton.IsVisible) NextButton.Focus();
            else if (CancelButton.IsVisible) CancelButton.Focus();
        }));
    }

    void Next(object sender, RoutedEventArgs e) => GoNext();

    void Back(object sender, RoutedEventArgs e) => GoBack();

    void CancelClicked(object sender, RoutedEventArgs e) => Cancel();

    internal void GoNext()
    {
        switch (Current)
        {
            case Page.Welcome:
                Show(Page.Location);
                break;
            case Page.Location:
                UpdateFolder();
                if (FolderProblem.Visibility != Visibility.Visible) Working = InstallAsync();
                break;
            case Page.Confirm:
                Working = UninstallAsync();
                break;
            case Page.Done:
                Finish();
                break;
        }
    }

    internal void GoBack()
    {
        if (Current == Page.Location) Show(Page.Welcome);
    }

    /// <summary>Cancel, or Esc: before the work, closes; during the copy, undoes it; on the last page, closes without starting Tanjelo.</summary>
    internal void Cancel()
    {
        if (Current == Page.Progress)
        {
            if (!_cancellable) return;
            _cancel?.Cancel();
            _cancellable = false;
            CancelButton.IsEnabled = false;
            StatusText.Text = Strings.Cancelling;
            return;
        }
        Close();
    }

    internal void Finish()
    {
        if (_installed is not null && StartCheck.IsChecked == true) _job.Start(_installed);
        Close();
    }

    // ---- Location

    void ScopeChanged(object sender, RoutedEventArgs e)
    {
        if (_settingUp) return;
        // A folder left at the other scope's default moves to this one's.
        var other = ChosenScope == Scope.User ? Scope.Machine : Scope.User;
        if (FolderBox.Text.Trim().Length == 0 || Folders.Same(Installer.OwnFolder(FolderBox.Text), _job.Installer.DefaultFolder(other)))
            FolderBox.Text = _job.Installer.DefaultFolder(ChosenScope);
        UpdateFolder();
    }

    void FolderChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingUp) UpdateFolder();
    }

    void Browse(object sender, RoutedEventArgs e)
    {
        if (FolderPicker.Pick(this, Strings.ChooseWhereTanjeloGoes, FolderBox.Text) is { } chosen)
            FolderBox.Text = Installer.OwnFolder(chosen);
    }

    /// <summary>Says where Tanjelo would go and whether it can, and the room it needs.</summary>
    void UpdateFolder()
    {
        if (_payload is null) return;
        var folder = Target;
        var problem = Updating ? null : _job.Installer.Problem(folder, _existing);
        FolderProblem.Text = problem ?? "";
        FolderProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        if (!Updating) FolderNote.Text = problem is null ? string.Format(Strings.TanjeloGoesInAFolderOfItsOwn, folder) : "";
        FolderNote.Visibility = FolderNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SpaceText.Text = string.Format(Strings.SpaceNeeded, Folders.Size(_payload.Size));
        if (problem is null && Free(folder) is { } free)
            SpaceText.Text = string.Format(Strings.SpaceNeededFreeOn, Folders.Size(_payload.Size), Path.GetPathRoot(folder), Folders.Size(free));
        if (Current == Page.Location) NextButton.IsEnabled = problem is null;
    }

    static long? Free(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    // ---- Installing

    StepRow AddStep(string label, double weight)
    {
        var step = new StepRow(label, weight);
        _steps.Add(step);
        return step;
    }

    void UpdateBar() => Bar.Value = _steps.Count == 0 ? 0 : _steps.Sum(s => s.Filled) / _steps.Sum(s => s.Weight);

    async Task InstallAsync()
    {
        var folder = Target;
        var scope = Updating ? _existing!.Scope : ChosenScope;
        _steps.Clear();
        var copy = AddStep(Strings.CopyingTanjelo, 10);
        var register = AddStep(Strings.AddingTanjeloToTheStartMenu, 1);
        var move = _existing is { FromVelopack: true } ? AddStep(string.Format(Strings.MovingTanjeloOver, _existing.Version), 1) : null;
        _cancel = new CancellationTokenSource();
        _cancellable = _working = true;
        Show(Page.Progress);
        StatusText.Text = Strings.CheckingSetupSCopyOfTanjelo;
        copy.State = StepState.Working;
        UpdateBar();
        var reports = new Reporter<Report>(report => Dispatcher.BeginInvoke(new Action(() => OnReport(report, copy, register, move))));
        try
        {
            await _job.InstallAsync(folder, scope, _payload!, _existing, reports, MayStop, _cancel.Token);
            foreach (var step in new[] { copy, register, move }) if (step is not null) step.State = StepState.Done;
            _installed = folder;
            ExitCode = ExitCodes.Done;
            ShowDone(Updating ? Strings.TanjeloIsUpdated : Strings.TanjeloIsInstalled, string.Format(Strings.TanjeloIsInStartItFrom, _payload!.Version, folder), null);
        }
        catch (OperationCanceledException)
        {
            Halt(StepState.Skipped, "");
            ExitCode = ExitCodes.Cancelled;
            ShowDone(Strings.SetupWasCancelled, Unchanged(), null);
        }
        catch (SetupException ex) when (ex.ExitCode == ExitCodes.NoAdministrator)
        {
            // Back to choosing, with the reason: the folder or "just for me" may do instead.
            _working = false;
            Show(Page.Location);
            FolderProblem.Text = ex.Message;
            FolderProblem.Visibility = Visibility.Visible;
        }
        catch (SetupException ex)
        {
            Halt(StepState.Failed, ex.Message);
            ExitCode = ex.ExitCode;
            ShowDone(Strings.TanjeloWasnTInstalled, Unchanged(), ex.Message);
        }
        finally
        {
            _working = _cancellable = false;
        }
    }

    string Unchanged() => _existing is { } installed ? string.Format(Strings.TanjeloStaysAsItWas, installed.Version) : Strings.NothingWasInstalled;

    /// <summary>Marks the step under way, and those after it, when the work stops early.</summary>
    void Halt(StepState state, string detail)
    {
        var working = _steps.FirstOrDefault(s => s.State == StepState.Working);
        if (working is not null)
        {
            working.State = state;
            working.Detail = detail;
        }
        foreach (var step in _steps.Where(s => s.State == StepState.Waiting)) step.State = StepState.Skipped;
        UpdateBar();
    }

    void OnReport(Report report, StepRow copy, StepRow register, StepRow? move)
    {
        switch (report.Step)
        {
            case Step.Copying:
                copy.State = StepState.Working;
                copy.Fraction = report.Total == 0 ? 0 : report.Done / (double)report.Total;
                if (_cancellable) StatusText.Text = string.Format(Strings.CopyingTanjeloOf, Folders.Part(report.Done, report.Total), Folders.Size(report.Total));
                break;
            case Step.Registering:
                // Past the point of no return: the new files are in.
                _cancellable = false;
                CancelButton.IsEnabled = false;
                copy.State = StepState.Done;
                register.State = StepState.Working;
                StatusText.Text = Strings.AddingTanjeloToTheStartMenu;
                break;
            case Step.MovingOver when move is not null:
                register.State = StepState.Done;
                move.State = StepState.Working;
                StatusText.Text = move.Label;
                break;
            case Step.Removing:
                var remove = _steps.FirstOrDefault();
                if (remove is null) break;
                remove.State = StepState.Working;
                remove.Fraction = report.Total == 0 ? 0 : report.Done / (double)report.Total;
                StatusText.Text = string.Format(Strings.RemovingTanjeloSFilesOf, report.Done, report.Total);
                break;
        }
        UpdateBar();
    }

    /// <summary>Asked when Tanjelo didn't quit: stop it, or leave it and give up.</summary>
    bool MayStop() => Dispatcher.Invoke(() => Ask(Strings.TanjeloIsStillRunningAndDidnT));

    void ShowDone(string heading, string text, string? problem)
    {
        Bar.Value = problem is null && ExitCode == ExitCodes.Done ? 1 : Bar.Value;
        DoneText.Text = text;
        ProblemText.Text = problem ?? "";
        ProblemText.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        LogLine.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        StartCheck.Visibility = _installed is null ? Visibility.Collapsed : Visibility.Visible;
        Heading.Text = heading;
        Show(Page.Done);
    }

    // ---- Uninstalling

    async Task UninstallAsync()
    {
        _steps.Clear();
        AddStep(Strings.RemovingTanjelo, 1).State = StepState.Working;
        _cancellable = false;
        _working = true;
        Show(Page.Progress);
        StatusText.Text = Strings.ClosingTanjelo;
        UpdateBar();
        var reports = new Reporter<Report>(report => Dispatcher.BeginInvoke(new Action(() => OnReport(report, _steps[0], _steps[0], null))));
        try
        {
            var left = await _job.UninstallAsync(_uninstallFolder!, _running, reports, MayStop, CancellationToken.None);
            if (left.Count == 0)
            {
                _steps[0].State = StepState.Done;
                ExitCode = ExitCodes.Done;
                ShowDone(Strings.TanjeloIsUninstalled, Strings.TanjeloIsUninstalledThanks, null);
            }
            else
            {
                Halt(StepState.Failed, "");
                ExitCode = ExitCodes.Failed;
                ShowDone(Strings.TanjeloWasnTFullyUninstalled, "", string.Format(Strings.TheseFilesCouldnTBeRemoved, string.Join(", ", left)));
            }
        }
        catch (SetupException ex)
        {
            Halt(StepState.Failed, "");
            ExitCode = ex.ExitCode;
            ShowDone(Strings.TanjeloWasnTFullyUninstalled, "", ex.Message);
        }
        finally
        {
            _working = false;
        }
    }

    // ---- Links

    void OpenLicence(object sender, RoutedEventArgs e) => Open(LicenceUrl);

    void OpenLog(object sender, RoutedEventArgs e) => Open(_log.FilePath);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
