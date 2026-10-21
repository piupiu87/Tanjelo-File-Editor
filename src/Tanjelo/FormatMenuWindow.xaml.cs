using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Tanjelo.Core;

namespace Tanjelo;

/// <summary>The menu of output formats that pops up at the mouse pointer.</summary>
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
            "extract" => "",                       // folder
            _ => Target.Section switch
            {
                Section.Image => "",               // picture
                Section.Video => "",               // video
                Section.Audio => "",               // music note
                Section.Document => "",            // document
                _ => "",                           // package
            },
        };
    }

    readonly TaskCompletionSource<Target?> _picked = new();
    readonly ICollectionView _view;
    Native.POINT _anchor;
    bool _closing;

    public FormatMenuWindow(IReadOnlyList<string> files, MenuModel menu)
    {
        InitializeComponent();

        var source = (CollectionViewSource)Resources["Entries"];
        source.Source = menu.Targets.Select(t => new Entry(t)).ToList();
        _view = source.View;
        _view.Filter = Matches;

        DescribeSelection(files);
        HintList.ItemsSource = menu.Hints;
        if (menu.Targets.Count == 0)
        {
            EmptyText.Text = "There's nothing to convert this selection to.";
            EmptyText.Visibility = Visibility.Visible;
            Search.IsEnabled = false;
        }
        SelectFirst();

        Search.TextChanged += (_, _) =>
        {
            Placeholder.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _view.Refresh();
            SelectFirst();
        };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => Finish(null);
        Closed += (_, _) => _picked.TrySetResult(null);
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
    }

    /// <summary>Shows the menu and completes with the chosen format, or null if dismissed.</summary>
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

    bool Matches(object item)
    {
        var query = Search.Text.Trim();
        if (query.Length == 0) return true;
        var entry = (Entry)item;
        return entry.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Target.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Group.Contains(query, StringComparison.OrdinalIgnoreCase);
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
        switch (e.Key)
        {
            case Key.Escape:
                Finish(null);
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
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.GetWindowRect(hwnd, out var rect);
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        var work = Native.WorkAreaAt(_anchor);

        var x = _anchor.X + 2;
        if (x + width > work.Right) x = Math.Max(work.Left, _anchor.X - width - 2);
        var y = _anchor.Y + 2;
        if (y + height > work.Bottom) y = Math.Max(work.Top, Math.Min(_anchor.Y - height - 2, work.Bottom - height));

        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER);
        Opacity = 1;
        Activate();
        Search.Focus();
    }
}
