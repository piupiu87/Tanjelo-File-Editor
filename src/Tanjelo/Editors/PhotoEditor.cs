using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ImageMagick;
using Orientation = System.Windows.Controls.Orientation;
using Tanjelo.Core;
using Tanjelo.Core.Converters;

namespace Tanjelo.Editors;

/// <summary>
/// Edit photo: sliders for light, colour, detail and effects over a live preview. The preview develops a screen-sized
/// copy with the engine's own steps, keeping the noise reduction and dehaze while only light and colour change. Hold \
/// (or the Original button) to see the photo as it was; double-click a slider to put it back to 0.
/// </summary>
sealed class PhotoEditor : Editor<PhotoSettings>
{
    /// <summary>The longest side of the copy the preview develops, in pixels.</summary>
    const int PreviewSide = 1600;

    static readonly (string Title, string[] Names)[] Groups =
    [
        (Strings.Light, ["exposure", "contrast", "highlights", "shadows", "whites", "blacks"]),
        (Strings.Colour, ["temperature", "tint", "vibrance", "saturation"]),
        (Strings.Detail, ["sharpness", "clarity", "noise"]),
        (Strings.Effects, ["dehaze", "grain", "vignette"]),
    ];

    readonly string _file;
    readonly Target _target;
    readonly ZoomCanvas _canvas = new();
    readonly StackPanel _panel = new();
    readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal };
    readonly Dictionary<string, (Slider Slider, TextBlock Value)> _sliders = [];
    MagickImage? _small;
    BitmapSource? _original, _edited;
    double _scale = 1;
    bool _showingOriginal, _showing;

    // A slider being dragged: one undo step for the whole drag.
    bool _gesture, _gestureCommitted;

    // The preview: the newest settings asked for, the ones last drawn, and the noise reduction and dehaze kept.
    PhotoSettings? _wanted, _drawn;
    bool _rendering;
    (double Noise, double Dehaze)? _preparedFor;
    MagickImage? _prepared;
    long _lastMilliseconds;

    public PhotoEditor(string file, Target target)
    {
        _file = file;
        _target = target;
        var original = Panels.Button("", Strings.Original, Strings.HoldToSeeThePhotoAsIt, () => { });
        original.PreviewMouseLeftButtonDown += (_, _) => ShowOriginal(true);
        original.PreviewMouseLeftButtonUp += (_, _) => ShowOriginal(false);
        original.MouseLeave += (_, _) => ShowOriginal(false);
        _toolbar.Children.Add(original);
        _toolbar.Children.Add(Panels.Button("", Strings.Auto, Strings.AStartingPointExposureWhitesAndBlacks, Auto));
        _toolbar.Children.Add(Panels.Button("", Strings.ResetAll, Strings.EverySliderBackTo0, () =>
        {
            if (!State.IsNone) Commit(PhotoSettings.None);
        }));

        var options = target.Options.ToDictionary(o => o.Name);
        foreach (var (title, names) in Groups)
        {
            _panel.Children.Add(Panels.Heading(title, _panel.Children.Count == 0 ? 0 : 18));
            foreach (var name in names)
                _panel.Children.Add(Row(options[name]));
        }
        _panel.Children.Add(Panels.Hint(Strings.DoubleClickASliderToPutIt, 14));
    }

    public override FrameworkElement Main => _canvas;
    public override FrameworkElement Toolbar => _toolbar;
    public override FrameworkElement Panel => _panel;
    public override ZoomCanvas Canvas => _canvas;
    public override string Summary => _small is null ? "" : Describe.Pixels((int)Math.Round(_small.Width / _scale), (int)Math.Round(_small.Height / _scale));

    public override string Status => _showingOriginal ? Strings.TheOriginal :
        _lastMilliseconds > 0 ? string.Format(Strings.PreviewDrawnInMsHoldToCompare, _lastMilliseconds) : Strings.HoldToCompareWithTheOriginal;

    /// <summary>Saving all zeros would make the same photo, so it doesn't count as a change.</summary>
    public override bool Changed => base.Changed && !State.IsNone;

    internal PhotoSettings Settings => State;
    internal Slider SliderFor(string name) => _sliders[name].Slider;
    internal bool ShowingOriginal => _showingOriginal;
    internal BitmapSource? Shown => _canvas.Picture.Source as BitmapSource;
    internal BitmapSource? Original => _original;
    internal long LastMilliseconds => _lastMilliseconds;
    internal void RunAuto() => Auto();

    FrameworkElement Row(Option option)
    {
        var slider = Panels.Themed(new Slider
        {
            Minimum = option.Min,
            Maximum = option.Max,
            SmallChange = option.Name == "exposure" ? 0.05 : 1,
            LargeChange = option.Name == "exposure" ? 0.5 : 10,
            Focusable = true,
            ToolTip = option.Hint,
        }, FrameworkElement.StyleProperty, option.Min < 0 ? "CentredSlider" : "ThinSlider");
        System.Windows.Automation.AutomationProperties.SetName(slider, option.Label);
        if (option.Hint is { } hint) System.Windows.Automation.AutomationProperties.SetHelpText(slider, hint);
        var value = Panels.Themed(new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, Text = Format(option.Name, 0) },
            TextBlock.ForegroundProperty, "TextSecondary");
        var label = new TextBlock { Text = option.Label, FontSize = 12.5 };
        var top = new Grid();
        top.Children.Add(label);
        top.Children.Add(value);
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(top);
        row.Children.Add(slider);

        slider.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                // Double-click: back to 0.
                EndDrag();
                slider.Value = 0;
                e.Handled = true;
                return;
            }
            StartDrag();
        };
        slider.PreviewMouseLeftButtonUp += (_, _) => EndDrag();
        slider.ValueChanged += (_, e) =>
        {
            value.Text = Format(option.Name, e.NewValue);
            if (!_showing && Started) Set(option.Name, e.NewValue);
        };
        _sliders[option.Name] = (slider, value);
        return row;
    }

    static string Format(string name, double value) => name == "exposure"
        ? (value > 0 ? "+" : "") + value.ToString("0.00", CultureInfo.InvariantCulture)
        : (value > 0 ? "+" : "") + Math.Round(value).ToString(CultureInfo.InvariantCulture);

    /// <summary>A slider is being dragged: the changes until <see cref="EndDrag"/> make one undo step.</summary>
    internal void StartDrag() => (_gesture, _gestureCommitted) = (true, false);

    internal void EndDrag() => _gesture = false;

    /// <summary>A slider moved: one undo step per drag, or per click and key.</summary>
    internal void Set(string name, double value)
    {
        var parameters = new Dictionary<string, string>(State.ToParameters()) { [name] = OptionValues.Format(name == "exposure" ? Math.Round(value, 2) : Math.Round(value)) };
        var next = PhotoSettings.From(_target.WithParameters(parameters));
        if (next == State) return;
        if (_gesture && _gestureCommitted) Amend(next);
        else Commit(next);
        _gestureCommitted |= _gesture;
    }

    public override async Task LoadAsync(CancellationToken cancel)
    {
        (_small, _scale) = await Task.Run(() =>
        {
            var full = Redaction.ReadUpright(_file);
            var scale = Math.Min(1, (double)PreviewSide / Math.Max(full.Width, full.Height));
            if (scale < 1) full.Resize(new MagickGeometry((uint)Math.Round(full.Width * scale), (uint)Math.Round(full.Height * scale)) { IgnoreAspectRatio = true });
            return (full, scale);
        }, cancel);
        _original = await Task.Run(() => Panels.Bitmap(_small), cancel);
        _edited = _original;
        _canvas.Show(_original, new Size(Math.Round(_small.Width / _scale), Math.Round(_small.Height / _scale)));
        Begin(PhotoSettings.None);
    }

    public override Target Result() => _target.WithParameters(State.ToParameters());

    protected override void Show(PhotoSettings settings)
    {
        _showing = true;
        foreach (var (name, value) in settings.ToParameters())
            if (_sliders.TryGetValue(name, out var row) && OptionValues.TryNumber(value, out var number))
                row.Slider.Value = number;
        _showing = false;
        _wanted = settings;
        if (!_rendering) _ = RenderAsync();
    }

    /// <summary>Develops the preview for the newest settings, again and again until it has caught up with the sliders.</summary>
    async Task RenderAsync()
    {
        _rendering = true;
        try
        {
            while (_wanted is { } wanted && wanted != _drawn && _small is not null)
            {
                var watch = Stopwatch.StartNew();
                var bitmap = wanted.IsNone ? _original! : await Task.Run(() => Develop(wanted));
                _drawn = wanted;
                _edited = bitmap;
                _lastMilliseconds = wanted.IsNone ? 0 : watch.ElapsedMilliseconds;
                if (!_showingOriginal) _canvas.Replace(bitmap);
                RaiseUpdated();
            }
        }
        catch (Exception ex) when (ex is MagickException or ConversionException)
        {
            Log.Write($"photo preview: {ex.Message}");
        }
        finally
        {
            _rendering = false;
        }
    }

    /// <summary>On a worker thread, one at a time: the screen-sized copy through the engine's steps.</summary>
    BitmapSource Develop(PhotoSettings settings)
    {
        var key = (settings.Noise, settings.Dehaze);
        if (_prepared is null || _preparedFor != key)
        {
            _prepared?.Dispose();
            _prepared = (MagickImage)_small!.Clone();
            Photo.Prepare(_prepared, settings, _scale);
            _preparedFor = key;
        }
        using var developed = _prepared.Clone();
        Photo.Finish(developed, settings, _scale);
        return Panels.Bitmap(developed);
    }

    /// <summary>Waits until the preview shows the current settings, for tests and renders.</summary>
    internal async Task SettleAsync()
    {
        while (_rendering || _wanted != _drawn) await Task.Delay(15);
    }

    internal void ShowOriginal(bool original)
    {
        if (_original is null || original == _showingOriginal) return;
        _showingOriginal = original;
        _canvas.Replace(original ? _original : _edited ?? _original);
        RaiseUpdated();
    }

    void Auto()
    {
        if (_small is null) return;
        var auto = Photo.Auto(_small);
        // Auto sets light and a touch of colour, and leaves the rest as it is.
        var next = State with { Exposure = auto.Exposure, Whites = auto.Whites, Blacks = auto.Blacks, Vibrance = State.Vibrance == 0 ? auto.Vibrance : State.Vibrance };
        if (next != State) Commit(next);
    }

    public override bool OnKey(KeyEventArgs e)
    {
        if (e.Key is Key.Oem5 or Key.OemBackslash)
        {
            ShowOriginal(true);
            return true;
        }
        return false;
    }

    public override bool OnKeyUp(KeyEventArgs e)
    {
        if (e.Key is not (Key.Oem5 or Key.OemBackslash)) return false;
        ShowOriginal(false);
        return true;
    }
}
