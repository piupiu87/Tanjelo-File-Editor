using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Run = System.Windows.Documents.Run;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Tanjelo.Core;

namespace Tanjelo;

/// <summary>The menu that pops up at the mouse pointer, with Convert, Tools and Actions tabs.</summary>
public partial class FormatMenuWindow : Window
{
    public sealed record Entry(Target Target)
    {
        public string Label => Target.Label;
        public string Description => Target.Description;
        public string Group => Target.Section switch
        {
            Section.Image => "Image",
            Section.Video => "Video",
            Section.Audio => "Audio",
            Section.Document => "Document",
            _ => "Archive",
        };
        public string Glyph => Target.Key switch
        {
            "extract" => "",                     // folder
            "mute" => "",                        // speaker, muted
            _ when Target.Key.StartsWith("speed-") => "",   // stopwatch
            _ when Target.Key.StartsWith("split-") => "",   // scissors
            "normalize" => "",                   // volume
            _ => Target.Section switch
            {
                Section.Image => "",             // picture
                Section.Video => "",             // video
                Section.Audio => "",             // music note
                Section.Document => "",          // document
                _ => "",                         // package
            },
        };
    }

    sealed record Tab(TargetKind Kind, string Title, string Placeholder, string Empty, RadioButton Button);

    readonly TaskCompletionSource<Target?> _picked = new();
    readonly ICollectionView _view;
    readonly List<Entry> _entries;
    readonly Tab[] _tabs;
    readonly (ModifierKeys Modifiers, Key Key)? _hotkey;
    Tab _tab;
    Native.POINT _anchor;
    bool _above; // opened above the pointer, so a change in height keeps the bottom edge where it is
    bool _closing;

    /// <param name="hotkey">The global shortcut. Pressing it again while the menu is open switches tabs.</param>
    public FormatMenuWindow(IReadOnlyList<string> files, MenuModel menu, (ModifierKeys, Key)? hotkey = null)
    {
        InitializeComponent();
        _hotkey = hotkey;
        _entries = menu.Targets.Select(t => new Entry(t)).ToList();
        _tabs =
        [
            new(TargetKind.Convert, "Convert", "Convert to…", "There's nothing to convert this selection to.", ConvertTab),
            new(TargetKind.Tool, "Tools", "Find a tool…", "There are no tools for this selection.", ToolsTab),
            new(TargetKind.Action, "Actions", "Find an action…", "There are no actions for this selection.", ActionsTab),
        ];

        _tab = _tabs.FirstOrDefault(t => Count(t) > 0) ?? _tabs[0];

        var source = (CollectionViewSource)Resources["Entries"];
        source.Source = _entries;
        _view = source.View;
        _view.Filter = item => ((Entry)item).Target.Kind == _tab.Kind && Matches((Entry)item);

        DescribeSelection(files);
        HintList.ItemsSource = menu.Hints;
        if (menu.Targets.Count == 0)
            Search.IsEnabled = false;
        foreach (var tab in _tabs)
            tab.Button.Checked += (_, _) => ShowTab(tab);
        ShowTab(_tab, refresh: true);

        Search.TextChanged += (_, _) =>
        {
            Placeholder.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            // Typing the name of a tool while on Convert jumps to the tab that has it.
            ShowTab(Count(_tab) > 0 ? _tab : _tabs.FirstOrDefault(t => Count(t) > 0) ?? _tab, refresh: true);
        };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => Finish(null);
        Closed += (_, _) => _picked.TrySetResult(null);
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
    }

    /// <summary>Shows the menu and completes with the chosen entry, or null if dismissed.</summary>
    public Task<Target?> PickAsync()
    {
        Show();
        return _picked.Task;
    }

    void DescribeSelection(IReadOnlyList<string> files)
    {
        if (files.Count == 1)
        {
            var path = files[0];
            Heading.Text = Path.GetFileName(path.TrimEnd('\\'));
            Subheading.Text = Directory.Exists(path)
                ? "Folder"
                : $"{Formats.Ext(path).ToUpperInvariant()} file · {FormatSize(new FileInfo(path).Length)}";
        }
        else
        {
            Heading.Text = $"{files.Count} items";
            Subheading.Text = string.Join(", ", files.Take(3).Select(Path.GetFileName)) + (files.Count > 3 ? ", …" : "");
        }

        var thumbnail = ShellIntegration.Thumbnail(files[0], 64);
        if (thumbnail is not null)
        {
            Thumbnail.Source = thumbnail;
            FallbackGlyph.Visibility = Visibility.Collapsed;
        }
    }

    static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    bool Matches(Entry entry)
    {
        var query = Search.Text.Trim();
        if (query.Length == 0) return true;
        return entry.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Target.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Group.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Entries on a tab that match the filter text.</summary>
    int Count(Tab tab) => _entries.Count(e => e.Target.Kind == tab.Kind && Matches(e));

    void ShowTab(Tab tab, bool refresh = false)
    {
        if (tab == _tab && !refresh) return;
        _tab = tab;
        tab.Button.IsChecked = true;
        Placeholder.Text = tab.Placeholder;
        _view.Refresh();

        foreach (var t in _tabs)
        {
            var count = Count(t);
            var header = new TextBlock();
            header.Inlines.Add(new Run(t.Title) { FontWeight = FontWeights.SemiBold });
            if (count > 0)
                header.Inlines.Add(new Run("  " + count) { FontSize = 11.5, Foreground = (Brush)FindResource("TextTertiary") });
            t.Button.Content = header;
            t.Button.IsEnabled = count > 0 || t == tab;
        }

        var empty = List.Items.Count == 0;
        EmptyText.Text = Search.Text.Trim().Length > 0 ? $"Nothing matches “{Search.Text.Trim()}”." : tab.Empty;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SelectFirst();
    }

    /// <summary>Moves to the next (or previous) tab that has entries.</summary>
    void CycleTabs(int direction)
    {
        var index = Array.IndexOf(_tabs, _tab);
        for (var step = 1; step < _tabs.Length; step++)
        {
            var next = _tabs[((index + direction * step) % _tabs.Length + _tabs.Length) % _tabs.Length];
            if (Count(next) > 0)
            {
                ShowTab(next);
                return;
            }
        }
    }

    void SelectFirst()
    {
        List.SelectedIndex = List.Items.Count > 0 ? 0 : -1;
        if (List.SelectedItem is not null) List.ScrollIntoView(List.SelectedItem);
    }

    void Move(int delta)
    {
        var count = List.Items.Count;
        if (count == 0) return;
        List.SelectedIndex = ((List.SelectedIndex < 0 ? 0 : List.SelectedIndex + delta) % count + count) % count;
        List.ScrollIntoView(List.SelectedItem);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_hotkey is { } hotkey && key == hotkey.Key && Keyboard.Modifiers == hotkey.Modifiers)
        {
            CycleTabs(1);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                Finish(null);
                break;
            case Key.Tab:
                CycleTabs(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                break;
            case Key.Down:
                Move(1);
                break;
            case Key.Up:
                Move(-1);
                break;
            case Key.PageDown or Key.PageUp:
                Move(e.Key == Key.PageDown ? 5 : -5);
                break;
            case Key.Enter:
                if (List.SelectedItem is Entry entry) Finish(entry.Target);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void OnEntryHover(object sender, MouseEventArgs e) => ((ListBoxItem)sender).IsSelected = true;

    void OnListClick(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(List, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (item?.DataContext is Entry entry) Finish(entry.Target);
    }

    void Finish(Target? target)
    {
        if (_closing) return;
        _closing = true;
        _picked.TrySetResult(target);
        Close();
    }

    // Placement happens in physical pixels so the menu lands on the right monitor at any DPI: first move the
    // still-invisible window to the pointer (so WPF lays it out at that monitor's DPI), then clamp it inside
    // the monitor's work area, flipping up/left like a context menu when there's no room.
    void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.StyleWindow(hwnd, Theme.IsDark);
        Native.GetCursorPos(out _anchor);
        Native.SetWindowPos(hwnd, IntPtr.Zero, _anchor.X, _anchor.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    void OnContentRendered(object? sender, EventArgs e)
    {
        Place(first: true);
        Opacity = 1;
        Activate();
        Search.Focus();
        // Switching tabs changes the height; keep the menu on the same side of the pointer.
        SizeChanged += (_, _) => Place(first: false);
    }

    void Place(bool first)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.GetWindowRect(hwnd, out var rect);
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        var work = Native.WorkAreaAt(_anchor);

        var x = _anchor.X + 2;
        if (x + width > work.Right) x = Math.Max(work.Left, _anchor.X - width - 2);
        if (first) _above = _anchor.Y + 2 + height > work.Bottom;
        var y = _above
            ? Math.Max(work.Top, Math.Min(_anchor.Y - height - 2, work.Bottom - height))
            : Math.Max(work.Top, Math.Min(_anchor.Y + 2, work.Bottom - height));

        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | (first ? 0 : Native.SWP_NOACTIVATE));
    }
}
