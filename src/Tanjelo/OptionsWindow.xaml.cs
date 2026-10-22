using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Tanjelo.Core;
using Forms = System.Windows.Forms;
using Shapes = System.Windows.Shapes;

namespace Tanjelo;

/// <summary>
/// Asks for a tool's settings between picking it in the menu and running it. One field per option, a live preview
/// when the tool has one, and the values the tool was last run with (or its defaults) to start from.
/// </summary>
public partial class OptionsWindow : Window
{
    /// <summary>Quick picks beside every colour field.</summary>
    static readonly string[] Swatches = ["#FFFFFF", "#F3EFEA", "#2B2B2B", "#000000", "#F97A3D", "#3A7BD5"];

    sealed record Field(Option Option, FrameworkElement Row, TextBlock Error, Control? Input);

    /// <summary>A slider over the length of the video or audio, for picking times; shown once the preview reports the length.</summary>
    sealed record Scrubber(string Name, FrameworkElement Row, Slider Slider);

    readonly ConverterEngine _engine;
    readonly IReadOnlyList<string> _files;
    readonly Target _target;
    readonly Dictionary<string, string> _values;
    readonly List<Field> _fields = [];
    readonly List<Scrubber> _scrubbers = [];

    /// <summary>Puts a value into a field's controls, which pass it on as if it had been typed or clicked.</summary>
    readonly Dictionary<string, Action<string>> _show = [];

    // The box dragged over the preview, such as a crop: as the last preview placed it, as it's drawn now (in the
    // picture's own pixels), the drag under way, and the shapes that draw it.
    PreviewBox? _box;
    Rect _drawn;
    BoxDrag? _drag;
    Shapes.Path? _shade;
    Shapes.Rectangle? _frame, _frameShadow;
    Shapes.Rectangle[] _handles = [];
    readonly TaskCompletionSource<Target?> _result = new();
    readonly DispatcherTimer _previewDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly bool _canPreview;
    CancellationTokenSource? _previewCancel;
    double? _at;
    Native.POINT _anchor;
    bool _closing;

    // An empty field only turns red once it has been edited or the user tried to run the tool.
    readonly HashSet<string> _touched = [];
    bool _triedRun;
    bool _placingSlider;

    /// <param name="remembered">Values to start from instead of the defaults, such as the ones used last time.</param>
    public OptionsWindow(ConverterEngine engine, IReadOnlyList<string> files, Target target, IReadOnlyDictionary<string, string> remembered)
    {
        InitializeComponent();
        _engine = engine;
        _files = files;
        _target = target;
        _values = new Dictionary<string, string>(target.Parameters);
        foreach (var (name, value) in remembered)
            if (_values.ContainsKey(name)) _values[name] = value;

        Glyph.Text = new FormatMenuWindow.Entry(target).Glyph;
        Heading.Text = target.Label.TrimEnd('…');
        Subheading.Text = files.Count == 1
            ? $"{Path.GetFileName(files[0])} · {Describe.Size(new FileInfo(files[0]).Length)}"
            : $"{files.Count} items · " + string.Join(", ", files.Take(3).Select(Path.GetFileName)) + (files.Count > 3 ? ", …" : "");
        Note.Text = files.Count == 1 ? "Saved as a copy beside the original" : "Saved beside the originals";
        RunButton.Content = target.ConfirmText;

        foreach (var option in target.Options)
            Fields.Children.Add(Build(option));

        _canPreview = engine.CanPreview(files, target);
        if (_canPreview)
        {
            PreviewPane.Visibility = Visibility.Visible;
            PreviewFrame.Background = Brush("Subtle");
            PreviewChecker.Background = Checkerboard();
        }
        _previewDelay.Tick += (_, _) =>
        {
            _previewDelay.Stop();
            _ = UpdatePreviewAsync();
        };

        CloseButton.Click += (_, _) => Finish(null);
        CancelButton.Click += (_, _) => Finish(null);
        RunButton.Click += (_, _) => Run();
        Header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        PreviewKeyDown += OnKey;
        Closed += (_, _) =>
        {
            _previewCancel?.Cancel();
            _result.TrySetResult(null);
        };
        BoxLayer.MouseLeftButtonDown += (_, e) =>
        {
            if (!BeginDrag(e.GetPosition(BoxLayer))) return;
            BoxLayer.CaptureMouse();
            e.Handled = true;
        };
        BoxLayer.MouseMove += (_, e) =>
        {
            if (_drag is null) BoxLayer.Cursor = CursorAt(e.GetPosition(BoxLayer));
            else DragTo(e.GetPosition(BoxLayer));
        };
        BoxLayer.MouseLeftButtonUp += (_, _) =>
        {
            EndDrag();
            BoxLayer.ReleaseMouseCapture();
        };
        BoxLayer.LostMouseCapture += (_, _) => EndDrag();
        BoxLayer.SizeChanged += (_, _) => DrawBox();
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
        Refresh(preview: true);
    }

    /// <summary>Shows the dialog and completes with the target carrying the chosen values, or null if canceled.</summary>
    public Task<Target?> AskAsync()
    {
        Show();
        return _result.Task;
    }

    FrameworkElement Build(Option option)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        if (option.Kind != OptionKind.Toggle)
            row.Children.Add(new TextBlock { Text = option.Label, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) });

        var (view, input) = option.Kind switch
        {
            OptionKind.Number => NumberField(option),
            OptionKind.Choice => ChoiceField(option),
            OptionKind.Toggle => ToggleField(option),
            OptionKind.Colour => ColourField(option),
            OptionKind.File => FileField(option),
            OptionKind.TimeRange => TimeRangeField(option),
            OptionKind.Times => TimesField(option),
            OptionKind.Order => OrderField(option),
            _ => TextField(option),
        };
        row.Children.Add(view);
        if (option.Hint is { } hint && option.Kind != OptionKind.Text)
            row.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(option.Kind == OptionKind.Toggle ? 27 : 0, 4, 0, 0),
                Foreground = Brush("TextTertiary"),
            });
        var error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = Brush("Danger") };
        row.Children.Add(error);
        _fields.Add(new Field(option, row, error, input));
        return row;
    }

    (FrameworkElement, Control?) NumberField(Option option)
    {
        var box = Box(width: 84);
        box.Text = _values[option.Name];
        box.TextChanged += (_, _) => Set(option.Name, box.Text);
        _show[option.Name] = value => box.Text = value;
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(box);
        if (option.Unit is { } unit)
            line.Children.Add(new TextBlock { Text = unit, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextSecondary") });
        if (!option.Slider) return (line, box);

        var slider = new Slider
        {
            Style = (Style)FindResource("ThinSlider"),
            Minimum = option.Min,
            Maximum = option.Max,
            SmallChange = option.Whole ? 1 : (option.Max - option.Min) / 100,
            LargeChange = (option.Max - option.Min) / 10,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        var syncing = false;
        void FromText()
        {
            if (syncing || !OptionValues.TryNumber(box.Text, out var value)) return;
            syncing = true;
            slider.Value = Math.Clamp(value, option.Min, option.Max);
            syncing = false;
        }
        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            syncing = true;
            box.Text = OptionValues.Format(option.Whole ? Math.Round(e.NewValue) : Math.Round(e.NewValue, 1));
            syncing = false;
        };
        box.TextChanged += (_, _) => FromText();
        FromText();
        var dock = new DockPanel();
        DockPanel.SetDock(line, Dock.Left);
        dock.Children.Add(line);
        dock.Children.Add(slider);
        return (dock, box);
    }

    (FrameworkElement, Control?) ChoiceField(Option option)
    {
        // Chips have a bottom margin so wrapped rows keep apart; the panel takes it back.
        var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, -6) };
        var chips = new Dictionary<string, RadioButton>();
        RadioButton? first = null;
        foreach (var choice in option.Choices)
        {
            var chip = new RadioButton
            {
                Content = choice.Label,
                GroupName = "option-" + option.Name,
                Style = (Style)FindResource("Chip"),
                IsChecked = _values[option.Name] == choice.Value,
            };
            chip.Checked += (_, _) => Set(option.Name, choice.Value);
            panel.Children.Add(chip);
            chips[choice.Value] = chip;
            if (first is null || chip.IsChecked == true) first = chip;
        }
        _show[option.Name] = value =>
        {
            if (chips.TryGetValue(value, out var chip)) chip.IsChecked = true;
        };
        return (panel, first);
    }

    (FrameworkElement, Control?) ToggleField(Option option)
    {
        var check = new CheckBox
        {
            Content = option.Label,
            Style = (Style)FindResource("Check"),
            IsChecked = OptionValues.TryFlag(_values[option.Name], out var on) && on,
        };
        check.Checked += (_, _) => Set(option.Name, "true");
        check.Unchecked += (_, _) => Set(option.Name, "false");
        return (check, check);
    }

    (FrameworkElement, Control?) TextField(Option option)
    {
        var box = Box();
        box.Tag = option.Hint;
        box.Text = _values[option.Name];
        box.TextChanged += (_, _) => Set(option.Name, box.Text);
        return (box, box);
    }

    (FrameworkElement, Control?) ColourField(Option option)
    {
        var box = Box(width: 92);
        var swatch = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("FieldBorder"),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Pick any colour…",
        };
        void ShowSwatch() => swatch.Background = Core.Colour.TryParse(box.Text, out var c)
            ? new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B))
            : Brushes.Transparent;
        box.Text = _values[option.Name];
        box.TextChanged += (_, _) =>
        {
            Set(option.Name, box.Text);
            ShowSwatch();
        };
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            if (PickColour(box.Text) is { } picked) box.Text = picked;
        };
        ShowSwatch();

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(swatch);
        line.Children.Add(box);
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var hex in Swatches)
        {
            Core.Colour.TryParse(hex, out var c);
            var dot = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Margin = new Thickness(0, 0, 5, 0),
                BorderThickness = new Thickness(1),
                BorderBrush = Brush("FieldBorder"),
                Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                Cursor = Cursors.Hand,
                ToolTip = hex,
            };
            dot.MouseLeftButtonUp += (_, _) => box.Text = hex;
            presets.Children.Add(dot);
        }
        line.Children.Add(presets);
        return (line, box);
    }

    /// <summary>The Windows colour picker, keeping the current transparency; null if canceled.</summary>
    string? PickColour(string current)
    {
        Core.Colour.TryParse(current, out var colour);
        using var dialog = new Forms.ColorDialog { FullOpen = true, AnyColor = true, Color = System.Drawing.Color.FromArgb(colour.R, colour.G, colour.B) };
        if (dialog.ShowDialog(new DialogOwner(new WindowInteropHelper(this).Handle)) != Forms.DialogResult.OK) return null;
        return new Core.Colour(dialog.Color.R, dialog.Color.G, dialog.Color.B, colour.A == 0 ? (byte)255 : colour.A).ToString();
    }

    sealed class DialogOwner(IntPtr handle) : Forms.IWin32Window
    {
        public IntPtr Handle => handle;
    }

    (FrameworkElement, Control?) FileField(Option option)
    {
        var box = Box();
        box.Tag = option.Optional ? "None" : "Choose a file";
        box.Text = _values[option.Name];
        box.TextChanged += (_, _) => Set(option.Name, box.Text);
        // A long path shows its end, where the file's name is.
        void ShowName() => box.ScrollToHorizontalOffset(double.MaxValue);
        box.Loaded += (_, _) => ShowName();
        box.TextChanged += (_, _) => ShowName();
        var browse = new Button { Content = "Browse…", Style = (Style)FindResource("SubtleButton") };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = option.Label, Filter = option.FileFilter ?? "All files|*.*" };
            if (File.Exists(box.Text)) dialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(box.Text));
            if (dialog.ShowDialog(this) == true) box.Text = dialog.FileName;
        };
        var dock = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        dock.Children.Add(browse);
        dock.Children.Add(box);
        return (dock, box);
    }

    (FrameworkElement, Control?) TimeRangeField(Option option)
    {
        var text = _values[option.Name];
        var dash = text.IndexOf('-');
        var from = Box(width: 96);
        var to = Box(width: 96);
        (from.Tag, to.Tag) = ("Start", "End");
        (from.Text, to.Text) = dash < 0 ? (text, "") : (text[..dash].Trim(), text[(dash + 1)..].Trim());
        void Update() => Set(option.Name, from.Text.Trim().Length == 0 && to.Text.Trim().Length == 0 ? "" : $"{from.Text.Trim()}-{to.Text.Trim()}");
        from.TextChanged += (_, _) => Update();
        to.TextChanged += (_, _) => Update();
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(from);
        line.Children.Add(new TextBlock { Text = "to", Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextSecondary") });
        line.Children.Add(to);
        return (line, from);
    }

    (FrameworkElement, Control?) TimesField(Option option)
    {
        var box = Box();
        box.Tag = "Such as 0:05, 1:12.5";
        box.Text = _values[option.Name];
        box.TextChanged += (_, _) => Set(option.Name, box.Text);

        var slider = new Slider { Style = (Style)FindResource("ThinSlider"), Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        var time = new TextBlock { Text = "0:00", Width = 56, Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("TextSecondary") };
        var add = new Button { Content = "Add", Style = (Style)FindResource("SubtleButton"), ToolTip = "Add this moment to the list" };
        slider.ValueChanged += (_, e) =>
        {
            time.Text = OptionValues.FormatTime(e.NewValue);
            if (_placingSlider) return;
            _at = e.NewValue;
            SchedulePreview();
        };
        add.Click += (_, _) =>
        {
            var at = Math.Round(slider.Value, 2);
            box.Text = OptionValues.TryTimes(box.Text, out var times)
                ? OptionValues.FormatTimes(times.Append(at).Distinct())
                : $"{box.Text.TrimEnd(' ', ',')}, {OptionValues.FormatTime(at)}";
        };
        var scrub = new DockPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        DockPanel.SetDock(add, Dock.Right);
        DockPanel.SetDock(time, Dock.Right);
        scrub.Children.Add(add);
        scrub.Children.Add(time);
        scrub.Children.Add(slider);
        _scrubbers.Add(new Scrubber(option.Name, scrub, slider));

        var panel = new StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(scrub);
        return (panel, box);
    }

    (FrameworkElement, Control?) OrderField(Option option)
    {
        OptionValues.TryOrder(_values[option.Name], out var saved);
        var order = saved.Count == _files.Count ? saved.ToList() : Enumerable.Range(0, _files.Count).ToList();
        var list = new StackPanel();
        void Move(int at, int by)
        {
            (order[at], order[at + by]) = (order[at + by], order[at]);
            Rebuild();
            Set(option.Name, order.SequenceEqual(Enumerable.Range(0, order.Count)) ? "" : string.Join(",", order.Select(i => i + 1)));
        }
        void Rebuild()
        {
            list.Children.Clear();
            for (var i = 0; i < order.Count; i++)
            {
                var at = i;
                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 2), Background = Brush("Subtle") };
                var up = new Button { Content = "", Style = (Style)FindResource("IconButton"), IsEnabled = at > 0, ToolTip = "Move up" };
                var down = new Button { Content = "", Style = (Style)FindResource("IconButton"), IsEnabled = at < order.Count - 1, ToolTip = "Move down" };
                up.Click += (_, _) => Move(at, -1);
                down.Click += (_, _) => Move(at, 1);
                DockPanel.SetDock(down, Dock.Right);
                DockPanel.SetDock(up, Dock.Right);
                line.Children.Add(down);
                line.Children.Add(up);
                line.Children.Add(new TextBlock
                {
                    Text = $"{at + 1}.  {Path.GetFileName(_files[order[at]])}",
                    Margin = new Thickness(10, 6, 6, 6),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                list.Children.Add(new Border { CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = line });
            }
        }
        Rebuild();
        return (new ScrollViewer { Content = list, MaxHeight = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false }, null);
    }

    TextBox Box(double? width = null)
    {
        var box = new TextBox { Style = (Style)FindResource("FieldBox") };
        if (width is { } w) box.Width = w;
        return box;
    }

    void Set(string name, string value)
    {
        _values[name] = value;
        _touched.Add(name);
        Refresh(preview: true);
    }

    /// <summary>Shows the fields that apply, checks each value, and lines up a new preview when they're all fine.</summary>
    void Refresh(bool preview)
    {
        var ok = true;
        var red = false;
        foreach (var field in _fields)
        {
            var shown = field.Option.IsShown(_values);
            field.Row.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            var value = _values[field.Option.Name];
            var problem = shown ? field.Option.Problem(value) : null;
            var flag = problem is not null && (value.Trim().Length > 0 || _touched.Contains(field.Option.Name) || _triedRun);
            field.Error.Text = flag ? $"{field.Option.Label} {problem}." : "";
            field.Error.Visibility = flag ? Visibility.Visible : Visibility.Collapsed;
            ok &= problem is null;
            red |= flag;
        }
        RunButton.IsEnabled = ok;
        // Tools with a scrubber preview the frame under it even before any time has been picked.
        if (preview && (ok || _scrubbers.Count > 0))
        {
            SchedulePreview();
        }
        else if (!ok && _canPreview && _scrubbers.Count == 0)
        {
            _previewDelay.Stop();
            _previewCancel?.Cancel();
            PreviewBusy.Visibility = Visibility.Collapsed;
            ShowPreview(null, red
                ? "The preview comes back when the settings marked in red are fixed."
                : "The preview appears once every setting is filled in.");
        }
    }

    void SchedulePreview()
    {
        if (!_canPreview) return;
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    async Task UpdatePreviewAsync()
    {
        _previewCancel?.Cancel();
        var cancel = _previewCancel = new CancellationTokenSource();
        var target = _target.WithParameters(_values);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var request = new PreviewRequest((int)Math.Ceiling(PreviewPane.Width * dpi)) { At = _at };
        PreviewBusy.Visibility = Visibility.Visible;
        try
        {
            var preview = await Task.Run(() => _engine.PreviewAsync(_files, target, request, cancel.Token), cancel.Token);
            if (!cancel.IsCancellationRequested) ShowPreview(preview, null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ex is not ConversionException) Log.Write(ex);
            if (!cancel.IsCancellationRequested) ShowPreview(null, ex.Message);
        }
        finally
        {
            if (_previewCancel == cancel) PreviewBusy.Visibility = Visibility.Collapsed;
        }
    }

    void ShowPreview(Preview? preview, string? problem)
    {
        PreviewImage.Source = preview?.Image is { } bytes ? Decode(bytes) : null;
        PreviewChecker.Visibility = PreviewImage.Source is null ? Visibility.Collapsed : Visibility.Visible;
        PreviewMessage.Text = problem ?? (PreviewImage.Source is null && preview?.Facts.Count is null or 0 ? "No preview for this file." : "");
        // Some previews are only facts, such as an audio file's new size; then there's no picture to make room for.
        PreviewFrame.Visibility = PreviewImage.Source is null && PreviewMessage.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        FactList.Margin = new Thickness(2, PreviewFrame.Visibility == Visibility.Visible ? 10 : 0, 2, 0);
        FactList.ItemsSource = preview?.Facts;
        if (preview?.Box is { } box && PreviewImage.Source is not null)
        {
            _box = box;
            // A preview that arrives mid-drag doesn't pull the box back to where it was.
            if (_drag is null) _drawn = new Rect(box.X, box.Y, box.Width, box.Height);
            BoxLayer.Visibility = Visibility.Visible;
            DrawBox();
        }
        else if (_drag is null)
        {
            _box = null;
            BoxLayer.Visibility = Visibility.Collapsed;
        }
        if (preview is { Duration: > 0 } media)
        {
            foreach (var scrubber in _scrubbers)
            {
                var first = scrubber.Row.Visibility != Visibility.Visible;
                scrubber.Slider.Maximum = media.Duration;
                scrubber.Slider.LargeChange = media.Duration / 20;
                scrubber.Slider.SmallChange = Math.Min(1, media.Duration / 100);
                scrubber.Row.Visibility = Visibility.Visible;
                // The slider starts at the first moment already chosen, which is what the preview shows.
                if (first && _at is null && OptionValues.TryTimes(_values[scrubber.Name], out var times) && times.Count > 0)
                {
                    _placingSlider = true;
                    scrubber.Slider.Value = Math.Min(times[0], media.Duration);
                    _placingSlider = false;
                }
            }
        }
    }

    /// <summary>How near an edge, in screen pixels, still counts as on it.</summary>
    const double Grip = 6;

    /// <summary>The picture's pixels per pixel of the preview on screen.</summary>
    double Scale => _box is { } box && BoxLayer.ActualWidth > 0 ? box.SourceWidth / BoxLayer.ActualWidth : 1;

    Point ToPicture(Point onScreen) => new(onScreen.X * Scale, onScreen.Y * Scale);

    /// <summary>Starts a drag at a point on the preview; false when there's no box to drag.</summary>
    internal bool BeginDrag(Point onScreen)
    {
        if (_box is not { } box) return false;
        _drag = BoxDrag.Start(ToPicture(onScreen), _drawn, new Size(box.SourceWidth, box.SourceHeight), box.Ratio, Grip * Scale);
        // The tool switches to the dragged box (a crop to Free), starting from the box as it is now.
        CommitBox();
        foreach (var (name, value) in box.OnDrag)
            SetValue(name, value);
        return true;
    }

    internal void DragTo(Point onScreen)
    {
        if (_drag is null) return;
        _drawn = _drag.To(ToPicture(onScreen));
        DrawBox();
        CommitBox();
    }

    internal void EndDrag() => _drag = null;

    /// <summary>Writes the box as drawn into its options, which shows the numbers in their fields too.</summary>
    void CommitBox()
    {
        if (_box is not { } box) return;
        static string Whole(double value) => OptionValues.Format(Math.Round(value));
        SetValue(box.XOption, Whole(_drawn.X));
        SetValue(box.YOption, Whole(_drawn.Y));
        SetValue(box.WidthOption, Whole(_drawn.Width));
        SetValue(box.HeightOption, Whole(_drawn.Height));
    }

    /// <summary>Changes an option as if it had been typed or clicked, so its field shows the new value.</summary>
    void SetValue(string name, string value)
    {
        if (_values.TryGetValue(name, out var old) && old == value) return;
        if (_show.TryGetValue(name, out var show)) show(value);
        else Set(name, value);
    }

    /// <summary>The shading outside the box, its outline, and handles on its corners (and edges, when any shape goes).</summary>
    void DrawBox()
    {
        if (_box is not { } box || BoxLayer.ActualWidth <= 0) return;
        if (_shade is null)
        {
            _shade = new Shapes.Path { Fill = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)) };
            _frameShadow = new Shapes.Rectangle { Stroke = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), StrokeThickness = 3.5 };
            _frame = new Shapes.Rectangle { Stroke = Brushes.White, StrokeThickness = 1.5 };
            _handles = Enumerable.Range(0, 8).Select(_ => new Shapes.Rectangle
            {
                Width = 9,
                Height = 9,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                StrokeThickness = 1,
            }).ToArray();
            foreach (var shape in new Shapes.Shape[] { _shade, _frameShadow, _frame }.Concat(_handles))
            {
                shape.IsHitTestVisible = false;
                BoxLayer.Children.Add(shape);
            }
        }
        var scale = 1 / Scale;
        var rect = new Rect(_drawn.X * scale, _drawn.Y * scale, _drawn.Width * scale, _drawn.Height * scale);
        _shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, BoxLayer.ActualWidth, BoxLayer.ActualHeight)), new RectangleGeometry(rect));
        foreach (var frame in new[] { _frameShadow!, _frame! })
        {
            Canvas.SetLeft(frame, rect.X);
            Canvas.SetTop(frame, rect.Y);
            (frame.Width, frame.Height) = (rect.Width, rect.Height);
        }
        // Corners, then the middles of the edges, which only resize when the shape is free.
        (double X, double Y)[] spots =
        [
            (rect.Left, rect.Top), (rect.Right, rect.Top), (rect.Right, rect.Bottom), (rect.Left, rect.Bottom),
            (rect.Left + rect.Width / 2, rect.Top), (rect.Right, rect.Top + rect.Height / 2),
            (rect.Left + rect.Width / 2, rect.Bottom), (rect.Left, rect.Top + rect.Height / 2),
        ];
        for (var i = 0; i < _handles.Length; i++)
        {
            Canvas.SetLeft(_handles[i], spots[i].X - _handles[i].Width / 2);
            Canvas.SetTop(_handles[i], spots[i].Y - _handles[i].Height / 2);
            _handles[i].Visibility = i < 4 || box.Ratio is null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>The pointer for what a drag from here would do: resize, move, or draw a new box.</summary>
    Cursor? CursorAt(Point onScreen)
    {
        if (_box is not { } box) return null;
        var at = ToPicture(onScreen);
        return BoxDrag.Hit(at, _drawn, Grip * Scale, cornersOnly: box.Ratio is not null) switch
        {
            BoxDrag.Edges.Left | BoxDrag.Edges.Top or BoxDrag.Edges.Right | BoxDrag.Edges.Bottom => Cursors.SizeNWSE,
            BoxDrag.Edges.Right | BoxDrag.Edges.Top or BoxDrag.Edges.Left | BoxDrag.Edges.Bottom => Cursors.SizeNESW,
            BoxDrag.Edges.Left or BoxDrag.Edges.Right => Cursors.SizeWE,
            BoxDrag.Edges.Top or BoxDrag.Edges.Bottom => Cursors.SizeNS,
            _ => _drawn.Contains(at) ? Cursors.SizeAll : Cursors.Cross,
        };
    }

    static BitmapImage Decode(byte[] bytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Squares behind the preview picture, so transparent parts show as transparent.</summary>
    Brush Checkerboard()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brush("Field"), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var squares = new GeometryGroup();
        squares.Children.Add(new RectangleGeometry(new Rect(0, 0, 8, 8)));
        squares.Children.Add(new RectangleGeometry(new Rect(8, 8, 8, 8)));
        drawing.Children.Add(new GeometryDrawing(Brush("Subtle"), null, squares));
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    void Run()
    {
        _triedRun = true;
        Refresh(preview: false);
        if (RunButton.IsEnabled) Finish(_target.WithParameters(_values));
    }

    void Finish(Target? target)
    {
        if (_closing) return;
        _closing = true;
        _result.TrySetResult(target);
        Close();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Finish(null);
                break;
            case Key.Enter:
                Run();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // Placed like the menu, in physical pixels on the pointer's monitor: first moved there while still invisible,
    // so WPF lays it out at that monitor's DPI, then centred on the pointer and kept inside the work area.
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
        var input = _fields.FirstOrDefault(f => f.Row.Visibility == Visibility.Visible && f.Input is not null)?.Input;
        input?.Focus();
        if (input is TextBox box) box.SelectAll();
        // Fields come and go with other choices; keep the whole dialog on screen as it grows.
        SizeChanged += (_, args) => Place(first: false, args.NewSize);
    }

    /// <param name="newSize">The size WPF is about to give the window, which it doesn't have yet during SizeChanged.</param>
    void Place(bool first, Size? newSize = null)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.GetWindowRect(hwnd, out var rect);
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (newSize is { } size)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            (width, height) = ((int)Math.Round(size.Width * dpi.DpiScaleX), (int)Math.Round(size.Height * dpi.DpiScaleY));
        }
        var work = Native.WorkAreaAt(_anchor);
        var (x, y) = first
            ? MenuPlacement.Centered(_anchor, work, width, height)
            : MenuPlacement.Inside(work, rect.Left, rect.Top, width, height);
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | (first ? 0 : Native.SWP_NOACTIVATE));
    }

    Brush Brush(string key) => (Brush)FindResource(key);
}
