using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Tanjelo.Core;

namespace Tanjelo;

/// <summary>
/// The Float shelf: an always-on-top panel at the right edge of the screen where files are parked by dropping them on
/// it, and dragged out again later. It holds references, not copies, and takes no focus when it appears.
/// </summary>
public partial class FloatWindow : Window
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tanjelo", "float.json");

    public sealed class Item(string path)
    {
        ImageSource? _thumbnail;
        public string Path { get; } = path;
        public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
        public ImageSource? Thumbnail => _thumbnail ??= ShellIntegration.Thumbnail(Path, 64);
    }

    readonly Shelf _shelf;
    readonly ObservableCollection<Item> _items = [];
    readonly DispatcherTimer _prune = new() { Interval = TimeSpan.FromSeconds(3) };
    Point? _pressedAt;
    Item? _pressedOn;
    bool _draggingOut;

    public FloatWindow(Shelf shelf, string? hotkey)
    {
        InitializeComponent();
        _shelf = shelf;
        List.ItemsSource = _items;
        var area = SystemParameters.WorkArea;
        (Left, Top) = (area.Right - Width - 12, area.Top + area.Height / 3); // until its height is known
        // "from the shelf", so nobody fears Delete will delete the file itself.
        KeysText.Text = hotkey is null ? Strings.DeleteRemovesFromTheShelf : string.Format(Strings.DeleteRemovesFromTheShelfConverts, hotkey);
        Refresh();
        _shelf.Changed += () => Dispatcher.BeginInvoke(Refresh);

        ClearButton.Click += (_, _) => _shelf.Clear();
        HideButton.Click += (_, _) => Hide();
        _prune.Tick += (_, _) => _shelf.Prune(); // files moved or deleted elsewhere drop off
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _prune.Start();
            else _prune.Stop();
        };
        Activated += (_, _) => _shelf.Prune();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.StyleWindow(hwnd, Theme.IsDark);
            Native.HideFromAltTab(hwnd);
        };
        SizeChanged += (_, _) => Place();

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => Highlight(false);
        Drop += OnDrop;
        List.PreviewMouseLeftButtonDown += OnPress;
        List.PreviewMouseMove += OnMove;
        DragAll.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            DragOut(_items.Select(i => i.Path).ToList());
        };
        PreviewKeyDown += OnKey;
    }

    public IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    /// <summary>The selected files, or all of them when none is selected: what the hotkey converts.</summary>
    public IReadOnlyList<string> SelectedOrAll()
    {
        var selected = List.SelectedItems.Cast<Item>().Select(i => i.Path).ToList();
        return selected.Count > 0 ? selected : _items.Select(i => i.Path).ToList();
    }

    void Refresh()
    {
        var selected = List.SelectedItems.Cast<Item>().Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = _items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        _items.Clear();
        foreach (var path in _shelf.Items)
            _items.Add(kept.GetValueOrDefault(path) ?? new Item(path));
        foreach (var item in _items.Where(i => selected.Contains(i.Path)))
            List.SelectedItems.Add(item);

        var empty = _items.Count == 0;
        List.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        DropZone.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Footer.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.IsEnabled = !empty;
        CountText.Text = empty ? "" : $"  {_items.Count}";
        List.MaxHeight = SystemParameters.WorkArea.Height * 0.6;
    }

    /// <summary>At the right edge of the screen, halfway down.</summary>
    void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Top + Math.Max(0, (area.Height - ActualHeight) / 2);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        // Parking keeps a reference, so it's a link, not a copy.
        var accept = !_draggingOut && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accept ? DragDropEffects.Link : DragDropEffects.None;
        Highlight(accept);
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        Highlight(false);
        if (_draggingOut || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        _shelf.Add(paths);
        e.Handled = true;
    }

    void Highlight(bool on) => DropZone.BorderBrush = (Brush)FindResource(on ? "Accent" : "FieldBorder");

    void OnPress(object sender, MouseButtonEventArgs e)
    {
        _pressedOn = (ItemsControl.ContainerFromElement(List, (DependencyObject)e.OriginalSource) as ListBoxItem)?.DataContext as Item;
        _pressedAt = _pressedOn is null || e.OriginalSource is DependencyObject d && IsInButton(d) ? null : e.GetPosition(List);
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_pressedAt is not { } start || _pressedOn is not { } item || e.LeftButton != MouseButtonState.Pressed) return;
        var moved = e.GetPosition(List) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _pressedAt = null;
        // Dragging a selected item takes the whole selection along; any other item goes alone.
        var paths = List.SelectedItems.Contains(item)
            ? _items.Where(List.SelectedItems.Contains).Select(i => i.Path).ToList()
            : [item.Path];
        DragOut(paths);
    }

    void DragOut(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        _draggingOut = true;
        try
        {
            DragDrop.DoDragDrop(this, DragData(paths), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            _draggingOut = false;
        }
        // Explorer reports a move as "none" when it moves within a drive, so look at the files themselves.
        _shelf.Prune();
    }

    /// <summary>
    /// The files being dragged out, marked to be copied (DROPEFFECT_COPY), so Explorer copies them even within a drive.
    /// The keys held still decide where they're dropped: Shift moves, Alt makes shortcuts.
    /// </summary>
    internal static DataObject DragData(IReadOnlyList<string> paths)
    {
        var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
        if (Native.ShellIdList(paths) is { } ids) data.SetData("Shell IDList Array", new MemoryStream(ids));
        return data;
    }

    void OnRemove(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Item item) _shelf.Remove([item.Path]);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Delete when List.SelectedItems.Count > 0:
                _shelf.Remove(List.SelectedItems.Cast<Item>().Select(i => i.Path).ToList());
                e.Handled = true;
                break;
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
        }
    }

    static bool IsInButton(DependencyObject element)
    {
        for (var d = element; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is Button) return true;
        return false;
    }
}
