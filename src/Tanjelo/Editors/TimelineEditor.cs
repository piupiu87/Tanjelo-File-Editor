using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Orientation = System.Windows.Controls.Orientation;
using Tanjelo.Core;
using Tanjelo.Core.Converters;

namespace Tanjelo.Editors;

/// <summary>What every timeline editor has, whatever its state: for the tests, which drive them all the same way.</summary>
interface ITimelineEditor
{
    Task Loaded { get; }
    TimelineView View { get; }
    IMediaPreview? Preview { get; }
    double Playhead { get; }
    int Frame { get; }
    TextBox TimeBox { get; }
    Task Seek();
    bool Press(Key key, ModifierKeys modifiers = ModifierKeys.None, bool repeat = false);
}

/// <summary>
/// The base of the audio and video editors: the picture over a transport bar and a timeline. The file plays in
/// Windows' own player; when Windows can't play it, stills from ffmpeg show at once and a small copy is made to play
/// instead. The frames come from ffprobe in the background, so the playhead always rests on a real frame, and the
/// thumbnails and the shape of the sound arrive as they're read. Editors draw their marks on the timeline and keep
/// their settings beside it; the keys are the same in all of them.
/// </summary>
abstract class TimelineEditor<T> : Editor<T>, ITimelineTool, ITimelineEditor
{
    /// <summary>How far ←/→ move in sound without frames, and with Shift in anything.</summary>
    const double SoundStep = 0.01, LongStep = 1;

    readonly Toolbox _tools;
    readonly Grid _main = new();
    readonly Border _stage = new() { Margin = new Thickness(0, 0, 0, 8) };
    readonly ZoomCanvas _canvas = new();
    readonly TextBox _time;
    readonly TextBlock _where;
    readonly Button _play;
    readonly StackPanel _transport = new() { Orientation = Orientation.Horizontal };
    readonly DockPanel _transportRow = new() { Margin = new Thickness(10, 0, 10, 6), LastChildFill = false };
    readonly DispatcherTimer _ticker;
    readonly CancellationTokenSource _closing = new();
    readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    OutputPaths.TempDirectory? _temp;
    IMediaPreview? _preview;
    TimelineMedia? _media;
    FrameIndex? _frames;
    double _playhead;
    double _rate = 1;
    string _loading = "";
    bool _reversing;

    protected TimelineEditor(string file, Target target, Toolbox tools)
    {
        File = file;
        Entry = target;
        _tools = tools;
        Video = Formats.Videos.Contains(Formats.Key(file));
        Timeline.Tool = this;
        Timeline.ShowPictures = Video;
        Timeline.SoundFills = !Video;
        Timeline.Scrubbed += t => MoveTo(t);
        System.Windows.Automation.AutomationProperties.SetName(Timeline, Strings.Timeline);
        System.Windows.Automation.AutomationProperties.SetHelpText(Timeline,
            Strings.SpacePlaysOrPausesTheArrowsStep);

        _play = Panels.Button("\uE768", Strings.Play, Strings.PlayOrPauseSpace, TogglePlay);
        _play.Margin = new Thickness(0, 0, 4, 0);
        _transport.Children.Add(_play);
        _transport.Children.Add(Small("\uE892", Strings.BackAFrameASecondWithShift, () => Step(-1, false)));
        _transport.Children.Add(Small("\uE893", Strings.OnAFrameASecondWithShift, () => Step(1, false)));
        _time = Panels.Themed(new TextBox
        {
            Width = 108,
            Margin = new Thickness(10, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = Strings.TypeATimeAndPressEnterTo,
        }, FrameworkElement.StyleProperty, "FieldBox");
        System.Windows.Automation.AutomationProperties.SetName(_time, Strings.Time);
        System.Windows.Automation.AutomationProperties.SetHelpText(_time, Strings.TypeATimeAndPressEnterTo);
        _time.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) GoToTyped();
            else if (e.Key == Key.Escape) ShowTime();
            else return;
            e.Handled = true;
            _canvas.Focus();
        };
        _time.LostKeyboardFocus += (_, _) => ShowTime();
        _transport.Children.Add(_time);
        _where = Panels.Themed(new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, TextBlock.ForegroundProperty, "TextSecondary");
        _transport.Children.Add(_where);
        DockPanel.SetDock(_transport, Dock.Left);
        _transportRow.Children.Add(_transport);

        _stage.Child = _canvas;
        _main.RowDefinitions.Add(new RowDefinition { Height = Video ? new GridLength(1, GridUnitType.Star) : new GridLength(0) });
        _main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _main.RowDefinitions.Add(new RowDefinition { Height = Video ? GridLength.Auto : new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        _main.Margin = new Thickness(0, 10, 0, 10);
        Grid.SetRow(_transportRow, 1);
        Grid.SetRow(Timeline, 2);
        _main.Children.Add(_stage);
        _main.Children.Add(_transportRow);
        _main.Children.Add(Timeline);
        if (!Video) _stage.Visibility = Visibility.Collapsed;

        _ticker = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(25) };
        _ticker.Tick += (_, _) => FollowPlayback();
    }

    static Button Small(string glyph, string tip, Action click)
    {
        var button = Panels.Themed(new Button { Content = glyph, ToolTip = tip, FontSize = 12, Padding = new Thickness(8) }, FrameworkElement.StyleProperty, "IconButton");
        button.Click += (_, _) => click();
        return button;
    }

    protected string File { get; }
    protected Target Entry { get; }

    /// <summary>Whether the file is a video, with a picture above the timeline.</summary>
    protected bool Video { get; }

    protected TimelineView Timeline { get; } = new();

    /// <summary>The file, once opened.</summary>
    protected TimelineMedia Media => _media ?? throw new InvalidOperationException("The file isn't open yet.");

    /// <summary>The video's frames: estimated from the frame rate until they've been read. Null for sound.</summary>
    protected FrameIndex? Frames => _frames;

    protected double Duration => _media?.Duration ?? 0;

    /// <summary>Where the playhead is: the start of the frame showing, or a time in the sound.</summary>
    public double Playhead => _playhead;

    /// <summary>The frame the playhead is on; 0 for sound.</summary>
    public int Frame => _frames?.At(_playhead) ?? 0;

    /// <summary>
    /// The start of the frame in the picture now, for what's drawn on it. While playing it's a moment behind the
    /// playhead, which follows the clock, as each frame takes a moment to copy out; after a seek it catches up once the
    /// new frame shows.
    /// </summary>
    protected double Showing => _frames is { } frames && _preview is { } preview ? frames.Start(frames.At(preview.PictureAt)) : _playhead;

    public override FrameworkElement Main => _main;
    public override (string Name, string Keys) Spoken => (Strings.Picture, Strings.SpacePlaysOrPausesAndTheArrows);
    public override ZoomCanvas? Canvas => Video ? _canvas : null;

    /// <summary>Controls of the editor's own at the right of the transport bar, such as the length kept.</summary>
    protected void AddToTransport(FrameworkElement element)
    {
        DockPanel.SetDock(element, Dock.Right);
        _transportRow.Children.Add(element);
    }

    public override string Summary => _media is not { } media ? "" : media.HasVideo
        ? $"{Describe.Pixels(media.Width, media.Height)} · {Rate(media.FrameRate)} · {Describe.Length(media.Duration)}"
        : string.Format(Strings.KHz, media.SampleRate / 1000.0, media.Channels switch { 1 => Strings.Mono, 2 => Strings.Stereo, var n => string.Format(Strings.Channels, n) }, Describe.Length(media.Duration));

    static string Rate(double fps) => fps <= 0 ? Strings.VariableFrameRate : string.Format(CultureInfo.InvariantCulture, Strings.Fps, Math.Round(fps, 3));

    /// <summary>What's being read in the background, then the editor's own line.</summary>
    public override string Status => string.Join(" · ", new[] { _loading, EditorStatus }.Where(s => s.Length > 0));

    /// <summary>The editor's own line for the bottom bar.</summary>
    protected virtual string EditorStatus => "";

    /// <summary>What's showing and playing; for the tests.</summary>
    public IMediaPreview? Preview => _preview;

    /// <summary>Completes once the frames, the sound's shape, the thumbnails and any preview copy are all in.</summary>
    public Task Loaded => _loaded.Task;

    public TextBox TimeBox => _time;
    public TimelineView View => Timeline;
    internal string Where => _where.Text;

    /// <summary>The state to start from, once the file is open.</summary>
    protected abstract T Initial();

    public override async Task LoadAsync(CancellationToken cancel)
    {
        using var link = CancellationTokenSource.CreateLinkedTokenSource(cancel, _closing.Token);
        var ct = link.Token;
        _media = await TimelineMedia.OpenAsync(_tools, File, ct);
        if (Video && !_media.HasVideo)
            throw new ConversionException(Strings.ThereSNoPictureInThisVideo);
        _frames = Video ? _media.EstimatedFrames : null;
        Timeline.Frames = _frames;
        Timeline.Duration = _media.Duration;

        var start = _frames?.Inside(0) ?? 0;
        var player = await FramePlayer.OpenAsync(File, Video, start, ct);
        var needsCopy = player is null;
        Use(player ?? (IMediaPreview)new StillsPreview(_media, () => _frames ?? _media.EstimatedFrames));
        if (player is null) await _preview!.ShowAsync(start);
        if (Video) _canvas.Show(_preview!.Picture ?? Blank(), new Size(_media.Width, _media.Height));
        Begin(Initial());
        ShowTime();
        _ = LoadRestAsync(needsCopy);
    }

    static BitmapSource Blank()
    {
        var blank = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[16], 8);
        blank.Freeze();
        return blank;
    }

    void Use(IMediaPreview preview)
    {
        _preview?.Dispose();
        _preview = preview;
        preview.PictureChanged += () =>
        {
            if (preview == _preview && preview.Picture is { } picture && _canvas.Picture.Source != picture) _canvas.Replace(picture);
            PictureShown();
        };
        preview.Ended += () =>
        {
            if (preview != _preview) return;
            _ticker.Stop();
            _sound?.Pause();
            _playhead = _frames is { } frames ? frames.Start(frames.Count - 1) : Duration;
            ShowTime();
        };
        _play.IsEnabled = preview.CanPlay;
        if (!preview.CanPlay) _play.ToolTip = Strings.PlayingNeedsWindowsPlayerWhichCanT;
    }

    /// <summary>Reads what the window can show before it's all in: the frames, the shape of the sound, thumbnails, and a copy to play if needed.</summary>
    async Task LoadRestAsync(bool needsCopy)
    {
        var ct = _closing.Token;
        var media = _media!;
        var parts = new Dictionary<string, double>();
        void Progress(string what, double fraction)
        {
            lock (parts) parts[what] = fraction;
            Dispatch(() =>
            {
                lock (parts)
                    _loading = string.Join(" · ", parts.Where(p => p.Value < 1).Select(p => $"{p.Key} {p.Value:0%}"));
                RaiseUpdated();
            });
        }
        var tasks = new List<Task>();
        if (Video)
        {
            tasks.Add(Task.Run(async () =>
            {
                Progress(Strings.ReadingFrames, 0);
                var frames = await media.LoadFramesAsync(new ActionProgress<double>(f => Progress(Strings.ReadingFrames, f)), ct);
                Dispatch(() =>
                {
                    _frames = frames;
                    Timeline.Frames = frames;
                    _playhead = frames.Start(frames.At(_playhead));
                    FramesLoaded();
                    ShowTime();
                });
                Progress(Strings.ReadingFrames, 1);
            }, ct));
            tasks.Add(Task.Run(() => media.ThumbnailsAsync((int)TimelineView.PicturesHeight - 4, media.Duration / 240, thumbnail =>
            {
                var bitmap = BitmapSource.Create(thumbnail.Width, thumbnail.Height, 96, 96, PixelFormats.Bgr32, null, thumbnail.Pixels, thumbnail.Width * 4);
                bitmap.Freeze();
                Dispatch(() => Timeline.AddThumbnail(double.IsNaN(thumbnail.Time) ? 0 : thumbnail.Time, bitmap));
            }, ct), ct));
        }
        if (media.HasAudio)
            tasks.Add(Task.Run(async () =>
            {
                var sound = await media.LoadWaveformAsync(ct);
                Dispatch(() => Timeline.Sound = sound);
            }, ct));
        if (needsCopy)
            tasks.Add(Task.Run(async () =>
            {
                Progress(Strings.MakingACopyToPlay, 0);
                var copy = await media.PreviewCopyAsync(TempFolder,new ActionProgress<double>(f => Progress(Strings.MakingACopyToPlay, f)), ct);
                Progress(Strings.MakingACopyToPlay, 1);
                var opened = await Dispatcher.InvokeAsync(() => FramePlayer.OpenAsync(copy, Video, _frames?.Inside(Frame) ?? _playhead, ct)).Task.Unwrap();
                if (opened is null) return;
                Dispatch(() =>
                {
                    if (_closing.IsCancellationRequested)
                    {
                        opened.Dispose();
                        return;
                    }
                    PlaysCopy = true;
                    _copy = copy;
                    Use(opened);
                    if (opened.Picture is { } picture) _canvas.Replace(picture);
                    Seek();
                });
            }, ct));
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex) when (ex is OperationCanceledException || ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            Dispatch(() =>
            {
                _loading = ex is ConversionException ? ex.Message : Strings.CouldnTReadAllOfTheFile;
                RaiseUpdated();
            });
        }
        finally
        {
            _loaded.TrySetResult();
        }
    }

    /// <summary>Whether the file plays from a small copy made by ffmpeg, because Windows can't play the file itself.</summary>
    internal bool PlaysCopy { get; private set; }

    /// <summary>The file the player has: the original, or the copy made when Windows can't play it.</summary>
    protected string PlayingFrom => _copy ?? File;

    /// <summary>Cancelled as the window closes, for work in the background that should stop then.</summary>
    protected CancellationToken Closing => _closing.Token;

    string? _copy;

    /// <summary>A folder for files made while the window is open, such as a copy to play; deleted when it closes.</summary>
    protected string TempFolder
    {
        get
        {
            lock (_closing) return (_temp ??= OutputPaths.CreateTemp()).Path;
        }
    }

    Dispatcher Dispatcher => _main.Dispatcher;

    void Dispatch(Action action) => Dispatcher.BeginInvoke(() =>
    {
        if (!_closing.IsCancellationRequested) action();
    });

    /// <summary>The frames have been read: times worked out from the estimate can be snapped to the real ones.</summary>
    protected virtual void FramesLoaded() { }

    /// <summary>A new picture is showing, such as a frame while paused.</summary>
    protected virtual void PictureShown() { }

    /// <summary>The playhead moved.</summary>
    protected virtual void PlayheadMoved() { }

    // ---- Moving about

    /// <summary>Moves the playhead to the frame showing at a time (or the time, in sound) and shows it.</summary>
    public void MoveTo(double seconds)
    {
        if (_media is null) return;
        _reversing = false;
        _stopAt = null;
        _playhead = _frames is { } frames ? frames.Start(frames.At(seconds)) : Math.Clamp(Math.Round(seconds, 3), 0, Duration);
        Timeline.Reveal(_playhead);
        Seek();
        if (_preview?.Playing == true) _sound?.Play(_playhead, _rate);
        ShowTime();
        PlayheadMoved();
    }

    /// <summary>Shows the playhead's moment; the returned task completes when it's showing.</summary>
    public Task Seek() => _preview?.ShowAsync(_frames is { } frames ? frames.Inside(frames.At(_playhead)) : _playhead) ?? Task.CompletedTask;

    /// <summary>On or back by a frame (a hundredth of a second in sound), or by a second.</summary>
    public void Step(int by, bool bySecond)
    {
        if (_media is null) return;
        Pause();
        if (bySecond) MoveTo(Math.Clamp(_playhead + by * LongStep, 0, Duration));
        else if (_frames is { } frames) MoveTo(frames.Start(Math.Clamp(frames.At(_playhead) + by, 0, frames.Count - 1)));
        else MoveTo(_playhead + by * SoundStep);
    }

    void GoToTyped()
    {
        if (!Timecode.TryParse(_time.Text, out var seconds))
        {
            ShowTime();
            return;
        }
        // A typed time goes to the frame that starts nearest to it.
        MoveTo(_frames is { } frames ? frames.Start(frames.Nearest(Math.Min(seconds, Duration))) : Math.Min(seconds, Duration));
    }

    void ShowTime()
    {
        Timeline.Playhead = _playhead;
        if (!_time.IsKeyboardFocusWithin) _time.Text = Timecode.Format(_playhead);
        _where.Text = _frames is { } frames
            ? string.Format(Strings.FrameOf, frames.At(_playhead) + 1, frames.Count)
            : string.Format(Strings.Of, Timecode.Format(Duration));
    }

    // ---- Playing

    internal bool Playing => _preview?.Playing == true || _reversing;

    void TogglePlay()
    {
        if (Playing) Pause();
        else Play(1);
    }

    /// <summary>Plays from one time to another, then pauses there, as to hear a part again; false when nothing can play.</summary>
    protected bool PlayBetween(double from, double to)
    {
        if (_preview is not { CanPlay: true }) return false;
        Pause();
        MoveTo(Math.Max(0, from));
        Play(1);
        _stopAt = Math.Min(Duration, to);
        return true;
    }

    /// <summary>Where playback started by <see cref="PlayBetween"/> pauses.</summary>
    double? _stopAt;

    /// <summary>
    /// Plays a sound file of the same length in place of the file's own sound, such as the sound with bleeps in; null
    /// goes back to the file's own. Sound on its own is swapped in the player; a video's picture goes on playing from
    /// its own file, muted, with the sound beside it in step. False when nothing plays here, as with stills.
    /// </summary>
    protected async Task<bool> UseSoundAsync(string? path)
    {
        if (_preview is not FramePlayer player) return false;
        if (!Video)
        {
            var swapped = await player.SwapAsync(path ?? PlayingFrom, _closing.Token);
            // The player paused for the swap, which stopped the playhead following it.
            if (player.Playing) _ticker.Start();
            ShowPlaying();
            return swapped;
        }
        var sound = path is null ? null : await SoundPlayer.OpenAsync(path, _closing.Token);
        if (path is not null && sound is null) return false;
        _sound?.Dispose();
        _sound = sound;
        player.SoundOff = sound is not null;
        if (player.Playing) sound?.Play(player.Position, _rate);
        return true;
    }

    /// <summary>A sound playing in place of the video's own, from <see cref="UseSoundAsync"/>.</summary>
    SoundPlayer? _sound;

    /// <summary>The sound playing in place of the video's own; for the tests.</summary>
    internal SoundPlayer? Sound => _sound;

    protected void Play(double rate)
    {
        if (_preview is not { CanPlay: true } preview) return;
        _reversing = false;
        _stopAt = null;
        // From the start again once at the end.
        if (_frames is { } frames ? frames.At(_playhead) >= frames.Count - 1 : _playhead >= Duration - 0.01) MoveTo(0);
        _rate = rate;
        preview.Play(rate);
        _sound?.Play(preview.Position, rate);
        _ticker.Start();
        ShowPlaying();
    }

    protected void Pause()
    {
        _reversing = false;
        _stopAt = null;
        if (_preview is not { Playing: true } preview)
        {
            ShowPlaying();
            return;
        }
        preview.Pause();
        _sound?.Pause();
        _ticker.Stop();
        // Rest on the frame playback stopped at.
        _playhead = _frames is { } frames ? frames.Start(frames.At(preview.Position)) : preview.Position;
        Seek();
        ShowTime();
        ShowPlaying();
        PlayheadMoved();
    }

    void ShowPlaying()
    {
        var content = (StackPanel)_play.Content;
        ((TextBlock)content.Children[0]).Text = Playing ? "\uE769" : "\uE768";
        ((TextBlock)content.Children[1]).Text = Playing ? Strings.Pause : Strings.Play;
    }

    void FollowPlayback()
    {
        if (_preview is not { Playing: true } preview)
        {
            if (!_reversing) _ticker.Stop();
            return;
        }
        _playhead = _frames is { } frames ? frames.Start(frames.At(preview.Position)) : Math.Min(Duration, preview.Position);
        if (_stopAt is { } stop && preview.Position >= stop)
        {
            Pause();
            return;
        }
        _sound?.Follow(preview.Position);
        Timeline.Reveal(_playhead);
        ShowTime();
        PlayheadMoved();
    }

    /// <summary>
    /// Plays backwards by stepping back through the frames as fast as the player can seek (a few frames a second,
    /// since Windows' player only plays forwards), keeping up with the clock by skipping frames when it must.
    /// </summary>
    async Task ReverseAsync()
    {
        if (_preview is null) return;
        if (_preview.Playing)
        {
            _preview.Pause();
            _sound?.Pause();
            _ticker.Stop();
        }
        _reversing = true;
        _rate = 1;
        ShowPlaying();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = clock.Elapsed.TotalSeconds;
        while (_reversing && !_closing.IsCancellationRequested && _playhead > 0)
        {
            var now = clock.Elapsed.TotalSeconds;
            var target = Math.Max(0, _playhead - (now - last) * _rate);
            last = now;
            _playhead = _frames is { } frames ? frames.Start(Math.Min(frames.At(target), Math.Max(0, frames.At(_playhead) - 1))) : target;
            ShowTime();
            PlayheadMoved();
            Timeline.Reveal(_playhead);
            await Seek();
            await Task.Delay(15);
        }
        _reversing = false;
        ShowPlaying();
    }

    // ---- Keys: the same in every timeline editor

    public override bool OnKey(KeyEventArgs e) =>
        _media is not null && Keyboard.FocusedElement is not TextBox && Press(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers, e.IsRepeat);

    /// <summary>A key pressed with these modifiers, as <see cref="OnKey"/> passes it on; true when used.</summary>
    public override bool Press(Key key, ModifierKeys modifiers = ModifierKeys.None, bool repeat = false)
    {
        if (_media is null || OnTimelineKey(key, modifiers, repeat)) return _media is not null;
        if (modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Alt)) return false;
        switch (key)
        {
            case Key.Space:
                if (!repeat) TogglePlay();
                return true;
            case Key.K:
                Pause();
                return true;
            case Key.L:
                Play(_preview?.Playing == true ? Math.Min(4, _rate * 2) : 1);
                return true;
            case Key.J:
                if (_reversing) _rate = Math.Min(4, _rate * 2);
                else _ = ReverseAsync();
                return true;
            case Key.Left or Key.Right:
                Step(key == Key.Left ? -1 : 1, modifiers.HasFlag(ModifierKeys.Shift));
                return true;
            case Key.Home:
                Pause();
                MoveTo(0);
                return true;
            case Key.End:
                Pause();
                MoveTo(Duration);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The editor's own keys, asked first; true when used.</summary>
    protected virtual bool OnTimelineKey(Key key, ModifierKeys modifiers, bool repeat) => false;

    /// <summary>The keys every timeline editor has, for the panel.</summary>
    protected IEnumerable<(string Keys, string Does)> TimelineKeys() =>
    [
        (Strings.Space, Strings.PlayOrPause),
        (Strings.JKL, Strings.BackPausePlayAgainForFaster),
        ("←, →", Video ? Strings.AFrameBackOrOn : Strings.AHundredthOfASecond),
        (Strings.Shift2, Strings.ASecondBackOrOn),
        (Strings.HomeEnd, Strings.TheStartOrTheEnd),
    ];

    // ---- The timeline's pointer and marks: the editors' own

    public virtual bool Down(TimelinePoint at, ModifierKeys modifiers) => false;
    public virtual void Move(TimelinePoint at, ModifierKeys modifiers) { }
    public virtual void Up(TimelinePoint at) { }
    public virtual Cursor? CursorAt(TimelinePoint at) => null;
    public virtual void Draw(DrawingContext dc, TimelineView view) { }

    /// <summary>
    /// A cut between frames nearest to a time: the start of the frame nearest it, or the end. In sound, to the
    /// millisecond. Where a trim starts or ends, or a bleep.
    /// </summary>
    protected double SnapCut(double seconds) =>
        _frames is { } frames ? Math.Min(frames.Snap(Math.Clamp(seconds, 0, Duration)), Duration) : Math.Clamp(Math.Round(seconds, 3), 0, Duration);

    public override void Close()
    {
        _closing.Cancel();
        _ticker.Stop();
        _reversing = false;
        _sound?.Dispose();
        _sound = null;
        _preview?.Dispose();
        _preview = null;
        // The player lets go of a copy a moment after it's closed.
        if (_temp is { } temp) _ = Task.Delay(1000).ContinueWith(_ => temp.Dispose(), TaskScheduler.Default);
    }
}
