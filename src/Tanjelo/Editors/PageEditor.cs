using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Tanjelo.Core;
using Tanjelo.Core.Converters;
using Shapes = System.Windows.Shapes;

namespace Tanjelo.Editors;

/// <summary>One page in the page editor: which PDF and page it is, how far it's turned, and an id that tells copies apart.</summary>
sealed record PageItem(int Source, int Index, int Rotation, int Id);

/// <summary>Where dragged pages land and how they move, apart from the screen.</summary>
static class PageMoves
{
    /// <summary>
    /// The place pages dropped at <paramref name="at"/> go, as an index into the tiles: in the row nearest the pointer,
    /// before the first tile whose middle is right of it, or after the row's last tile.
    /// </summary>
    public static int InsertionAt(IReadOnlyList<Rect> tiles, Point at) => Drop(tiles, at).Index;

    /// <summary>The place a drop goes, with the tile the caret stands beside and whether it stands after it.</summary>
    public static (int Index, Rect Beside, bool After) Drop(IReadOnlyList<Rect> tiles, Point at)
    {
        if (tiles.Count == 0) return (0, Rect.Empty, false);
        static double Away(Rect tile, double y) => y < tile.Top ? tile.Top - y : y > tile.Bottom ? y - tile.Bottom : 0;
        var nearest = Enumerable.Range(0, tiles.Count).MinBy(i => Away(tiles[i], at.Y));
        var row = Enumerable.Range(0, tiles.Count).Where(i => Math.Abs(tiles[i].Top - tiles[nearest].Top) < 1).ToList();
        foreach (var i in row)
            if (tiles[i].Left + tiles[i].Width / 2 > at.X)
                return (i, tiles[i], false);
        return (row[^1] + 1, tiles[row[^1]], true);
    }

    /// <summary>The chosen items, in their order, moved to just before <paramref name="insertAt"/>, an index into the list as it was.</summary>
    public static ImmutableList<T> Move<T>(ImmutableList<T> items, IReadOnlyCollection<int> chosen, int insertAt)
    {
        var picked = chosen.ToHashSet();
        var moving = items.Where((_, i) => picked.Contains(i)).ToList();
        var rest = items.Where((_, i) => !picked.Contains(i)).ToList();
        var before = Enumerable.Range(0, Math.Clamp(insertAt, 0, items.Count)).Count(picked.Contains);
        rest.InsertRange(Math.Clamp(insertAt, 0, items.Count) - before, moving);
        return [.. rest];
    }
}

/// <summary>
/// Organize pages: the PDF's pages as a grid of thumbnails, drawn as they scroll into view. Drag them into a new order,
/// several at once with Ctrl and Shift, turn, copy and delete them, and add pages from other PDFs. Saving writes the
/// page list the engine takes ("3,1-2,5r90").
/// </summary>
sealed class PageEditor : Editor<ImmutableList<PageItem>>
{
    const double Thumb = 136;    // the longest side of a page's picture, in DIPs
    const double TileWidth = 164;

    sealed class Tile
    {
        public required Border Root { get; init; }
        public required Border Paper { get; init; }
        public required Image Picture { get; init; }
        public required RotateTransform Turn { get; init; }
        public required TextBlock Label { get; init; }
    }

    readonly string _file;
    readonly Target _target;
    readonly ScrollViewer _scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FocusVisualStyle = null };
    readonly Grid _surface = new() { Background = Brushes.Transparent, Focusable = true, FocusVisualStyle = null };
    readonly WrapPanel _grid = new() { Margin = new Thickness(12) };
    readonly Canvas _marks = new() { IsHitTestVisible = false };
    readonly Shapes.Rectangle _caret = new() { Width = 3, RadiusX = 1.5, RadiusY = 1.5, Visibility = Visibility.Collapsed };
    readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal };
    readonly CheckBox _onlySelected = new() { Content = Strings.SaveOnlyTheSelectedPages, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly Dictionary<int, Tile> _tiles = [];
    readonly List<PdfThumbnails> _sources = [];
    readonly List<string> _paths = [];
    readonly List<IReadOnlyList<(double Width, double Height)>> _sizes = [];
    readonly Dictionary<(int, int), BitmapSource> _pictures = [];
    readonly HashSet<(int, int)> _asked = [];
    readonly Queue<(int Source, int Index)> _wanted = [];
    readonly HashSet<int> _selected = [];
    readonly CancellationTokenSource _closing = new();
    readonly List<Button> _needSelection = [];
    int? _focus;  // the page the keyboard is on, by id
    int _nextId;
    bool _drawing;
    string? _losses, _problem;

    // A press on a page, which becomes a drag once the pointer has moved a little.
    PageItem? _pressed;
    Point _pressedAt;
    bool _dragging;
    int _insertAt;
    ModifierKeys _pressModifiers;

    public PageEditor(string file, Target target)
    {
        _file = file;
        _target = target;
        _surface.Children.Add(_grid);
        _surface.Children.Add(_marks);
        _marks.Children.Add(_caret);
        _scroller.Content = _surface;
        _scroller.ScrollChanged += (_, _) => AskForVisible();
        _surface.MouseLeftButtonDown += (_, e) =>
        {
            _surface.Focus();
            PointerDown(e.GetPosition(_surface), Keyboard.Modifiers);
            _surface.CaptureMouse();
            e.Handled = true;
        };
        _surface.MouseMove += (_, e) =>
        {
            if (_pressed is not null) PointerMove(e.GetPosition(_surface));
        };
        _surface.MouseLeftButtonUp += (_, e) =>
        {
            PointerUp(e.GetPosition(_surface));
            _surface.ReleaseMouseCapture();
        };
        _surface.LostMouseCapture += (_, _) => CancelDrag();

        AddButton("", Strings.Left, Strings.TurnLeftCtrlL, () => Turn(270), mirror: true);
        AddButton("", Strings.Right, Strings.TurnRightCtrlR, () => Turn(90));
        AddButton("", Strings.Copy, Strings.CopyTheSelectedPagesCtrlD, Duplicate);
        AddButton("", Strings.Delete, Strings.DeleteTheSelectedPagesDelete, Delete);
        AddButton("", Strings.AddPages, Strings.AddThePagesOfAnotherPDFAfter, () => _ = PickAndInsertAsync(), always: true);
        _onlySelected.SetResourceReference(FrameworkElement.StyleProperty, "Check");
        _onlySelected.Checked += (_, _) => RaiseUpdated();
        _onlySelected.Unchecked += (_, _) => RaiseUpdated();
        _toolbar.Children.Add(_onlySelected);
    }

    public override FrameworkElement Main => _scroller;
    public override FrameworkElement Toolbar => _toolbar;
    public override string Summary => _sources.Count > 0 ? Describe.Count(_sources[0].Count, Tanjelo.Core.Strings.OnePage, Tanjelo.Core.Strings.NPages) : "";
    public override string? Notice => _problem ?? _losses;

    public override string Status =>
        Describe.Count(State.Count, Tanjelo.Core.Strings.OnePage, Tanjelo.Core.Strings.NPages) + (_selected.Count > 0 ? string.Format(Strings.Selected, _selected.Count) : "") +
        Strings.DragPagesToMoveThemCtrlOr;

    bool OnlySelected => _onlySelected.IsChecked == true && _selected.Count > 0;

    /// <summary>Saving just the selected pages is a change even when nothing else is.</summary>
    public override bool Changed => base.Changed || (OnlySelected && _selected.Count < State.Count);

    internal IReadOnlyCollection<int> Selected => _selected;
    internal ImmutableList<PageItem> Pages => State;
    internal CheckBox OnlySelectedBox => _onlySelected;

    public override async Task LoadAsync(CancellationToken cancel)
    {
        cancel.Register(_closing.Cancel);
        var thumbnails = await PdfThumbnails.OpenAsync(_file);
        var sizes = await Task.Run(() => Enumerable.Range(0, thumbnails.Count).Select(thumbnails.Size).ToList(), cancel);
        _losses = await Task.Run(() => OrganizePdfTool.Losses(_file), cancel);
        _sources.Add(thumbnails);
        _paths.Add(_file);
        _sizes.Add(sizes);
        Begin([.. Enumerable.Range(0, thumbnails.Count).Select(i => new PageItem(0, i, 0, _nextId++))]);
    }

    public override Target Result()
    {
        var pages = OnlySelected ? State.Where(p => _selected.Contains(p.Id)).ToList() : State.ToList();
        if (pages.Count == 0) throw new ConversionException(Strings.ThereAreNoPagesLeftToSave);
        // Other PDFs get their letters in the order their pages first appear: b, c…
        var others = pages.Where(p => p.Source > 0).Select(p => p.Source).Distinct().ToList();
        var letters = others.Select((source, i) => (source, i + 1)).ToDictionary(x => x.source, x => x.Item2);
        var list = PageList.Format(pages.Select(p => new PageList.Page(p.Source == 0 ? 0 : letters[p.Source], p.Index, p.Rotation)).ToList());
        return _target.WithParameters(new Dictionary<string, string>
        {
            ["pages"] = list,
            ["with"] = string.Join("|", others.Select(s => _paths[s])),
        });
    }

    protected override void Show(ImmutableList<PageItem> pages)
    {
        _selected.IntersectWith(pages.Select(p => p.Id));
        if (_focus is { } focus && pages.All(p => p.Id != focus)) _focus = null;
        _grid.Children.Clear();
        foreach (var page in pages)
        {
            if (!_tiles.TryGetValue(page.Id, out var tile)) _tiles[page.Id] = tile = NewTile(page);
            tile.Turn.Angle = page.Rotation;
            _grid.Children.Add(tile.Root);
        }
        foreach (var gone in _tiles.Keys.Except(pages.Select(p => p.Id)).ToList()) _tiles.Remove(gone);
        Paint();
        _grid.Dispatcher.BeginInvoke(AskForVisible, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    Tile NewTile(PageItem page)
    {
        var (width, height) = _sizes[page.Source][page.Index];
        var scale = Thumb / Math.Max(1, Math.Max(width, height));
        var picture = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        var turn = new RotateTransform();
        var paper = new Border
        {
            Width = Math.Max(8, width * scale),
            Height = Math.Max(8, height * scale),
            Background = Brushes.White,
            BorderThickness = new Thickness(1),
            Child = picture,
            LayoutTransform = turn,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        paper.SetResourceReference(Border.BorderBrushProperty, "FieldBorder");
        var label = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        label.Text = page.Source == 0 ? $"{page.Index + 1}" : $"{(char)('a' + page.Source)}{page.Index + 1}";
        var stack = new StackPanel();
        stack.Children.Add(new Grid { Width = Thumb + 4, Height = Thumb + 4, Children = { paper } });
        stack.Children.Add(label);
        var root = new Border
        {
            Width = TileWidth,
            Padding = new Thickness(10, 10, 10, 8),
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2),
            Child = stack,
            ToolTip = string.Format(Strings.Page, Path.GetFileName(_paths[page.Source]), page.Index + 1),
        };
        if (_pictures.TryGetValue((page.Source, page.Index), out var known)) picture.Source = known;
        return new Tile { Root = root, Paper = paper, Picture = picture, Turn = turn, Label = label };
    }

    /// <summary>Shows which pages are selected and which one the keyboard is on.</summary>
    void Paint()
    {
        foreach (var page in State)
        {
            var tile = _tiles[page.Id];
            var selected = _selected.Contains(page.Id);
            tile.Root.SetResourceReference(Border.BackgroundProperty, selected ? "Selected" : "Surface");
            if (page.Id == _focus) tile.Root.SetResourceReference(Border.BorderBrushProperty, "Accent");
            else tile.Root.BorderBrush = Brushes.Transparent;
            tile.Label.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Accent" : "TextSecondary");
        }
        foreach (var button in _needSelection) button.IsEnabled = _selected.Count > 0;
        RaiseUpdated();
    }

    void AddButton(string glyph, string text, string tip, Action click, bool mirror = false, bool always = false)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 13, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        if (mirror) (icon.RenderTransformOrigin, icon.RenderTransform) = (new Point(0.5, 0.5), new ScaleTransform(-1, 1));
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, ToolTip = tip, Margin = new Thickness(0, 0, 6, 0), IsEnabled = always };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        System.Windows.Automation.AutomationProperties.SetHelpText(button, tip);
        button.SetResourceReference(FrameworkElement.StyleProperty, "SubtleButton");
        button.Click += (_, _) => click();
        _toolbar.Children.Add(button);
        if (!always) _needSelection.Add(button);
    }

    // ---- Changes

    /// <summary>The selected pages' places, in order.</summary>
    List<int> SelectedPlaces() => State.Select((p, i) => (p, i)).Where(x => _selected.Contains(x.p.Id)).Select(x => x.i).ToList();

    internal void Turn(int degrees)
    {
        if (_selected.Count == 0) return;
        Commit(State.Select(p => _selected.Contains(p.Id) ? p with { Rotation = (p.Rotation + degrees) % 360 } : p).ToImmutableList());
    }

    internal void Duplicate()
    {
        if (_selected.Count == 0) return;
        var copies = new List<int>();
        var pages = new List<PageItem>();
        foreach (var page in State)
        {
            pages.Add(page);
            if (!_selected.Contains(page.Id)) continue;
            var copy = page with { Id = _nextId++ };
            pages.Add(copy);
            copies.Add(copy.Id);
        }
        // The copies are what's selected now, ready to be dragged somewhere else.
        Select(copies);
        Commit([.. pages]);
    }

    internal void Delete()
    {
        if (_selected.Count == 0) return;
        if (_selected.Count == State.Count)
        {
            _problem = Strings.APDFNeedsAtLeastOnePage;
            RaiseUpdated();
            return;
        }
        var after = State.Select((p, i) => (p, i)).LastOrDefault(x => _selected.Contains(x.p.Id)).i;
        var remaining = State.Where(p => !_selected.Contains(p.Id)).ToImmutableList();
        // The keyboard moves on to the page after the deleted ones, as in a list.
        var next = remaining[Math.Min(after + 1 - _selected.Count, remaining.Count - 1)];
        Select([next.Id]);
        Commit(remaining);
    }

    /// <summary>Moves the selected pages to just before <paramref name="place"/>, a place among the pages as they are.</summary>
    internal void MoveSelectedTo(int place)
    {
        var moved = PageMoves.Move(State, SelectedPlaces(), place);
        if (!moved.SequenceEqual(State)) Commit(moved);
    }

    /// <summary>Adds every page of another PDF after the selection (or at the end), selected.</summary>
    internal async Task InsertAsync(string path)
    {
        PdfThumbnails thumbnails;
        try
        {
            thumbnails = await PdfThumbnails.OpenAsync(path);
        }
        catch (Exception ex) when (ex is ConversionException or IOException or UnauthorizedAccessException)
        {
            _problem = string.Format(Strings.CouldnTAdd, Path.GetFileName(path), ex.Message);
            RaiseUpdated();
            return;
        }
        var sizes = await Task.Run(() => Enumerable.Range(0, thumbnails.Count).Select(thumbnails.Size).ToList());
        var source = _sources.Count;
        _sources.Add(thumbnails);
        _paths.Add(Path.GetFullPath(path));
        _sizes.Add(sizes);
        var added = Enumerable.Range(0, thumbnails.Count).Select(i => new PageItem(source, i, 0, _nextId++)).ToList();
        var places = SelectedPlaces();
        var at = places.Count > 0 ? places[^1] + 1 : State.Count;
        Select(added.Select(p => p.Id));
        _problem = null;
        Commit(State.InsertRange(at, added));
    }

    async Task PickAndInsertAsync()
    {
        var dialog = new OpenFileDialog { Title = Strings.AddThePagesOf, Filter = Strings.PDFDocumentsPdf, InitialDirectory = Path.GetDirectoryName(_file) };
        if (dialog.ShowDialog(Window.GetWindow(_scroller)) == true) await InsertAsync(dialog.FileName);
    }

    // ---- Selection and dragging, in the grid's own units

    void Select(IEnumerable<int> ids)
    {
        _selected.Clear();
        _selected.UnionWith(ids);
        _focus = _selected.Count > 0 ? _selected.Last() : _focus;
        _problem = null;
        Paint();
    }

    internal IReadOnlyList<Rect> TileRects() =>
        State.Select(p => _tiles[p.Id].Root).Select(tile => new Rect(tile.TranslatePoint(new Point(), _surface), tile.RenderSize)).ToList();

    PageItem? PageAt(Point at)
    {
        var rects = TileRects();
        for (var i = 0; i < rects.Count; i++)
            if (rects[i].Contains(at)) return State[i];
        return null;
    }

    internal void PointerDown(Point at, ModifierKeys modifiers)
    {
        _pressModifiers = modifiers;
        _pressed = PageAt(at);
        _pressedAt = at;
        _dragging = false;
        if (_pressed is not { } page)
        {
            if (!modifiers.HasFlag(ModifierKeys.Control)) Select([]);
            return;
        }
        if (modifiers.HasFlag(ModifierKeys.Shift) && _focus is { } anchor && State.FindIndex(p => p.Id == anchor) is var from and >= 0)
        {
            // From the page the keyboard is on to this one, which stays the start for the next Shift+click.
            var to = State.IndexOf(page);
            _selected.Clear();
            _selected.UnionWith(State.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1).Select(p => p.Id));
            Paint();
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!_selected.Remove(page.Id)) _selected.Add(page.Id);
            _focus = page.Id;
            Paint();
        }
        else if (!_selected.Contains(page.Id))
        {
            // A selected page keeps the selection, so all of it can be dragged; a click without a drag picks just it.
            Select([page.Id]);
        }
        else
        {
            _focus = page.Id;
            Paint();
        }
    }

    internal void PointerMove(Point at)
    {
        if (_pressed is not { } page || !_selected.Contains(page.Id)) return;
        if (!_dragging && (at - _pressedAt).Length < 5) return;
        _dragging = true;
        _surface.Cursor = Cursors.SizeAll;
        var (place, beside, after) = PageMoves.Drop(TileRects(), at);
        _insertAt = place;
        // The caret stands in the gap before the page it goes in front of, or after the last page of the row.
        _caret.SetResourceReference(Shapes.Shape.FillProperty, "Accent");
        System.Windows.Controls.Canvas.SetLeft(_caret, (after ? beside.Right + 4 : beside.Left - 4) - _caret.Width / 2);
        System.Windows.Controls.Canvas.SetTop(_caret, beside.Top + 6);
        _caret.Height = Math.Max(0, beside.Height - 12);
        _caret.Visibility = Visibility.Visible;
        // Near the top or bottom, the grid scrolls to make room.
        var inView = _surface.TranslatePoint(at, _scroller);
        if (inView.Y < 30) _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset - 20);
        else if (inView.Y > _scroller.ViewportHeight - 30) _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + 20);
    }

    internal void PointerUp(Point at)
    {
        if (_dragging)
        {
            MoveSelectedTo(_insertAt);
        }
        else if (_pressed is { } page && _pressModifiers == ModifierKeys.None)
        {
            Select([page.Id]);
        }
        CancelDrag();
    }

    void CancelDrag()
    {
        _pressed = null;
        _dragging = false;
        _caret.Visibility = Visibility.Collapsed;
        _surface.Cursor = null;
    }

    public override bool OnKey(KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.A when ctrl:
                Select(State.Select(p => p.Id));
                return true;
            case Key.Delete or Key.Back:
                Delete();
                return true;
            case Key.D when ctrl:
                Duplicate();
                return true;
            case Key.R when ctrl:
                Turn(90);
                return true;
            case Key.L when ctrl:
                Turn(270);
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End:
                Step(e.Key, ctrl, shift);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Arrows go from page to page (Shift picks the pages passed), and with Ctrl move the selected pages.</summary>
    internal void Step(Key key, bool move, bool extend)
    {
        if (State.Count == 0) return;
        var perRow = Math.Max(1, (int)((_grid.ActualWidth > 0 ? _grid.ActualWidth : TileWidth * 4) / (TileWidth + 8)));
        var at = _focus is { } focus ? State.FindIndex(p => p.Id == focus) : -1;
        var by = key switch { Key.Left => -1, Key.Right => 1, Key.Up => -perRow, Key.Down => perRow, Key.Home => -State.Count, _ => State.Count };
        if (move)
        {
            var places = SelectedPlaces();
            if (places.Count == 0) return;
            var target = by < 0 ? Math.Max(0, places[0] + by) : Math.Min(State.Count, places[^1] + 1 + by);
            MoveSelectedTo(target);
            return;
        }
        var next = State[Math.Clamp(at < 0 ? 0 : at + by, 0, State.Count - 1)];
        if (extend && at >= 0)
        {
            var to = State.IndexOf(next);
            _selected.UnionWith(State.Skip(Math.Min(at, to)).Take(Math.Abs(to - at) + 1).Select(p => p.Id));
            _focus = next.Id;
            Paint();
        }
        else
        {
            Select([next.Id]);
        }
        _tiles[next.Id].Root.BringIntoView();
    }

    // ---- Thumbnails, drawn for the pages in view and kept

    /// <summary>Asks for pictures of the pages in view and a row either side, nearest first.</summary>
    void AskForVisible()
    {
        if (_sources.Count == 0 || _scroller.ViewportHeight <= 0) return;
        var top = _scroller.VerticalOffset - Thumb;
        var bottom = _scroller.VerticalOffset + _scroller.ViewportHeight + Thumb;
        foreach (var page in State)
        {
            var key = (page.Source, page.Index);
            if (_pictures.ContainsKey(key) || _asked.Contains(key)) continue;
            var y = _tiles[page.Id].Root.TranslatePoint(new Point(), _surface).Y;
            if (y + Thumb < top || y > bottom) continue;
            _asked.Add(key);
            _wanted.Enqueue(key);
        }
        if (!_drawing && _wanted.Count > 0) _ = DrawWantedAsync();
    }

    async Task DrawWantedAsync()
    {
        _drawing = true;
        try
        {
            var pixels = (int)Math.Ceiling(Thumb * VisualTreeHelper.GetDpi(_scroller).PixelsPerDip);
            while (_wanted.TryDequeue(out var key) && !_closing.IsCancellationRequested)
            {
                var source = _sources[key.Item1];
                var picture = await Task.Run(async () => Decode(await source.RenderAsync(key.Item2, pixels, _closing.Token)));
                _pictures[key] = picture;
                foreach (var page in State.Where(p => (p.Source, p.Index) == key))
                    _tiles[page.Id].Picture.Source = picture;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // A page Windows can't draw stays blank; the rest still come.
            Log.Write($"organize-pdf thumbnail: {ex.Message}");
        }
        finally
        {
            _drawing = false;
        }
    }

    static BitmapSource Decode(byte[] png)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Whether the page at this place has its picture yet.</summary>
    internal bool PictureShown(int place) => _tiles[State[place].Id].Picture.Source is not null;

    /// <summary>Waits for the pictures asked for so far, for tests and renders.</summary>
    internal async Task SettleThumbnailsAsync()
    {
        AskForVisible();
        while (_drawing || _wanted.Count > 0)
            await Task.Delay(20);
    }
}
