using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tanjelo.Core;
using Tanjelo.Core.Converters;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Media.Playback;
using MediaPlayer = Windows.Media.Playback.MediaPlayer;
using Windows.Storage;
using MediaSource = Windows.Media.Core.MediaSource;
using VideoFrame = Windows.Media.VideoFrame;

namespace Tanjelo.Editors;

/// <summary>
/// What a timeline editor shows and plays: the picture at a moment, and playback with sound when it can.
/// Times are seconds from the start of the file, as <see cref="FrameIndex"/> has them.
/// </summary>
interface IMediaPreview : IDisposable
{
    /// <summary>The picture now showing, at most <see cref="FramePlayer.LargestSide"/> pixels wide; null for sound.</summary>
    BitmapSource? Picture { get; }

    bool CanPlay { get; }
    bool Playing { get; }

    /// <summary>Where playback is now; the last time shown while paused.</summary>
    double Position { get; }

    /// <summary>Shows the moment at <paramref name="seconds"/> and stays there, paused or playing on from it. A newer
    /// request supersedes one still on its way, and both complete once the newest is shown.</summary>
    Task ShowAsync(double seconds);

    void Play(double rate = 1);
    void Pause();

    /// <summary>A new picture was copied into <see cref="Picture"/>.</summary>
    event Action? PictureChanged;

    /// <summary>Playback reached the end.</summary>
    event Action? Ended;
}

/// <summary>
/// Plays a file with Windows' own media player in frame-server mode: the graphics card decodes, the sound plays in
/// step, and each frame is copied into a WPF bitmap instead of a window of the player's own. Seeking waits for the
/// player to say the seek is done and then takes the next frame it hands over, which is the exact one. Stepping is
/// done by seeking into the next or previous frame (see FrameIndex.Inside), since the player's own step back jumps a 24th of a second.
/// </summary>
sealed class FramePlayer : IMediaPreview
{
    /// <summary>The largest picture copied out, in pixels on its long side: copying 4K for every frame would be too slow.</summary>
    public const int LargestSide = 1280;

    /// <summary>Keeps every player silent, as the tests need.</summary>
    public static bool Muted { get; set; }

    readonly MediaPlayer _player;
    readonly Dispatcher _dispatcher;
    readonly bool _video;
    readonly SemaphoreSlim _show = new(1, 1);
    VideoFrame? _gpu, _cpu;
    WriteableBitmap? _picture;
    byte[] _pixels = [];
    int _width, _height;
    int _copying;
    bool _disposed;
    double _shown;

    // A seek under way: done once the player says so, then the next frame it hands over is the one.
    TaskCompletionSource? _seeked;

    /// <summary>How long without a new frame before the one showing after a seek counts as the one, in milliseconds.</summary>
    const int Quiet = 100;

    /// <summary>How long to wait for a frame after a seek before seeking again, in milliseconds.</summary>
    const int NoFrame = 400;

    // Frames handed over while a seek is waiting for them.
    readonly SemaphoreSlim _handedOver = new(0);
    volatile bool _waiting;
    double? _wanted;

    FramePlayer(bool video)
    {
        _video = video;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _player = new MediaPlayer { IsVideoFrameServerEnabled = video, IsMuted = Muted, AutoPlay = false };
        _player.PlaybackSession.SeekCompleted += (_, _) => _seeked?.TrySetResult();
        _player.VideoFrameAvailable += (_, _) => OnFrame();
        _player.MediaEnded += (_, _) => _dispatcher.BeginInvoke(() =>
        {
            Playing = false;
            Ended?.Invoke();
        });
    }

    public BitmapSource? Picture => _picture;

    /// <summary>The file playing.</summary>
    public string? Source { get; private set; }
    public bool CanPlay => true;
    public bool Playing { get; private set; }
    public double Position => Playing ? _player.PlaybackSession.Position.TotalSeconds : _shown;

    public event Action? PictureChanged;
    public event Action? Ended;

    /// <summary>
    /// Opens a file in Windows' player and shows its first moment; null when Windows can't play it here, such as
    /// MPEG-2, ProRes or AIFF, HEVC without its extension, or a PC whose graphics can't copy frames.
    /// </summary>
    /// <param name="start">Where to show first: inside the first frame, since the very first one handed over is blank.</param>
    public static async Task<FramePlayer?> OpenAsync(string path, bool video, double start, CancellationToken ct)
    {
        var player = new FramePlayer(video);
        try
        {
            if (await player.LoadAsync(path, ct) && await player.ShowWithin(start, TimeSpan.FromSeconds(5)))
                return player;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or TimeoutException or FileNotFoundException)
        {
            Log.Write($"Windows' player couldn't open {Path.GetFileName(path)}: {ex.Message}");
        }
        player.Dispose();
        ct.ThrowIfCancellationRequested();
        return null;
    }

    /// <summary>Plays another file of the same length in place of this one, such as the same video with new sound, at the same moment.</summary>
    public async Task<bool> SwapAsync(string path, CancellationToken ct)
    {
        var (at, playing) = (Position, Playing);
        Pause();
        if (!await LoadAsync(path, ct)) return false;
        await ShowAsync(at);
        if (playing) Play();
        return true;
    }

    async Task<bool> LoadAsync(string path, CancellationToken ct)
    {
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Opened(MediaPlayer sender, object args) => opened.TrySetResult(true);
        void Failed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            Log.Write($"Windows' player: {args.Error} {args.ErrorMessage}");
            opened.TrySetResult(false);
        }
        _player.MediaOpened += Opened;
        _player.MediaFailed += Failed;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(ct);
            _player.Source = MediaSource.CreateFromStorageFile(file);
            Source = path;
            if (!await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), ct)) return false;
        }
        finally
        {
            _player.MediaOpened -= Opened;
            _player.MediaFailed -= Failed;
        }
        if (!_video) return true;
        var session = _player.PlaybackSession;
        if (session.NaturalVideoWidth == 0 || session.NaturalVideoHeight == 0) return false;
        var scale = Math.Min(1, (double)LargestSide / Math.Max(session.NaturalVideoWidth, session.NaturalVideoHeight));
        var (width, height) = ((int)Math.Round(session.NaturalVideoWidth * scale / 2) * 2, (int)Math.Round(session.NaturalVideoHeight * scale / 2) * 2);
        if (width != _width || height != _height || _gpu is null)
        {
            _gpu?.Dispose();
            _cpu?.Dispose();
            _gpu = VideoFrame.CreateAsDirect3D11SurfaceBacked(DirectXPixelFormat.B8G8R8A8UIntNormalized, width, height);
            _cpu = new VideoFrame(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
            _pixels = new byte[width * height * 4];
            (_width, _height) = (width, height);
            await _dispatcher.InvokeAsync(() => _picture = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null));
        }
        return true;
    }

    async Task<bool> ShowWithin(double seconds, TimeSpan limit)
    {
        var shown = ShowAsync(seconds);
        if (await Task.WhenAny(shown, Task.Delay(limit)) != shown) return false;
        await shown;
        return !_video || _shownFrame;
    }

    bool _shownFrame;

    public async Task ShowAsync(double seconds)
    {
        _wanted = seconds;
        await _show.WaitAsync();
        try
        {
            // Requests that came in while another was being shown: only the newest matters.
            while (_wanted is { } at && !_disposed)
            {
                _wanted = null;
                await SeekAndCopyAsync(at);
            }
        }
        finally
        {
            _show.Release();
        }
    }

    async Task SeekAndCopyAsync(double seconds)
    {
        var copying = _video && !Playing && Interlocked.CompareExchange(ref _copying, 1, 0) == 0;
        try
        {
            while (_handedOver.Wait(0)) { }
            _waiting = copying;
            _shown = seconds;
            await SeekAsync(seconds);
            if (!copying) return;
            // Each frame handed over since the seek began is shown, until none has come for a moment: on a busy PC the
            // first can be the frame from before the seek, and the right one follows. While paused, no more come.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var shown = false;
            for (var again = 0; !_disposed && clock.ElapsedMilliseconds < 3000;)
            {
                if (!await _handedOver.WaitAsync(shown ? Quiet : NoFrame))
                {
                    // Now and then the player calls a seek done without handing over a frame, leaving the old one
                    // showing. Going somewhere else first and then back makes it hand over the right one.
                    if (shown || ++again > 2) break;
                    await SeekAsync(seconds >= 0.1 ? seconds - 0.05 : seconds + 0.05);
                    while (_handedOver.Wait(0)) { }
                    await SeekAsync(seconds);
                    continue;
                }
                while (_handedOver.Wait(0)) { }
                if (await GrabAsync())
                {
                    shown = true;
                    await PresentAsync();
                }
                // Someone wants another moment already, as while dragging the playhead: no need to wait for this one.
                if (_wanted is not null) break;
            }
            if (!shown && await GrabAsync()) await PresentAsync();
        }
        finally
        {
            _waiting = false;
            if (copying) Volatile.Write(ref _copying, 0);
        }
    }

    /// <summary>Sets the player's position and waits for it to say the seek is done.</summary>
    async Task SeekAsync(double seconds)
    {
        var seeked = _seeked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (await Task.WhenAny(seeked.Task, Task.Delay(3000)) != seeked.Task) Log.Write("Windows' player didn't finish a seek in 3 seconds.");
        _seeked = null;
    }

    public void Play(double rate = 1)
    {
        if (_disposed) return;
        _player.PlaybackSession.PlaybackRate = rate;
        if (Playing) return;
        Playing = true;
        _player.Play();
    }

    public void Pause()
    {
        if (!Playing) return;
        _player.Pause();
        Playing = false;
        _shown = _player.PlaybackSession.Position.TotalSeconds;
    }

    // Called on one of the player's threads, for every frame, even while paused.
    void OnFrame()
    {
        if (_disposed) return;
        if (_waiting)
        {
            _handedOver.Release();
            return;
        }
        if (Playing) _ = CopyAsync();
    }

    /// <summary>Copies the frame playing into the picture, skipping it if the last one is still being copied.</summary>
    async Task CopyAsync()
    {
        if (Interlocked.CompareExchange(ref _copying, 1, 0) != 0) return;
        try
        {
            if (await GrabAsync()) await PresentAsync();
        }
        finally
        {
            Volatile.Write(ref _copying, 0);
        }
    }

    /// <summary>Copies the player's current frame into memory: on the graphics card first, then out of it.</summary>
    async Task<bool> GrabAsync()
    {
        if (_gpu is null || _cpu is null || _disposed) return false;
        try
        {
            _player.CopyFrameToVideoSurface(_gpu.Direct3DSurface);
            await _gpu.CopyToAsync(_cpu);
            _cpu.SoftwareBitmap.CopyToBuffer(_pixels.AsBuffer());
            _shownFrame = true;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            // A frame that couldn't be copied is skipped; the next one will do.
            return false;
        }
    }

    /// <summary>Puts the copied frame in the picture, on the window's thread.</summary>
    async Task PresentAsync() => await _dispatcher.InvokeAsync(() =>
    {
        if (_disposed || _picture is null) return;
        _picture.WritePixels(new Int32Rect(0, 0, _width, _height), _pixels, _width * 4, 0);
        PictureChanged?.Invoke();
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Playing = false;
        _seeked?.TrySetResult();
        try
        {
            _player.Pause();
            _player.Source = null;
        }
        catch (COMException) { }
        _player.Dispose();
        _gpu?.Dispose();
        _cpu?.Dispose();
    }
}

/// <summary>
/// Pictures from ffmpeg, one exact frame per moment, for when Windows' player can't show a file at all, as on a PC
/// without graphics drivers: slower (about half a second a frame) and without playback, but every frame is right.
/// </summary>
sealed class StillsPreview(TimelineMedia media, Func<FrameIndex> frames) : IMediaPreview
{
    readonly SemaphoreSlim _show = new(1, 1);
    readonly CancellationTokenSource _closing = new();
    double? _wanted;
    double _shown = -1;

    public BitmapSource? Picture { get; private set; }
    public bool CanPlay => false;
    public bool Playing => false;
    public double Position => Math.Max(0, _shown);

    public event Action? PictureChanged;
    public event Action? Ended { add { } remove { } }

    public async Task ShowAsync(double seconds)
    {
        _wanted = seconds;
        await _show.WaitAsync();
        try
        {
            while (_wanted is { } at && !_closing.IsCancellationRequested)
            {
                _wanted = null;
                var index = frames();
                var frame = index.At(at);
                if (_shown >= 0 && index.At(_shown) == frame) continue;
                _shown = at;
                if (!media.HasVideo) continue;
                byte[] png;
                try
                {
                    png = await media.FrameAsync(index, frame, FramePlayer.LargestSide, _closing.Token);
                }
                catch (Exception ex) when (ex is ConversionException or OperationCanceledException)
                {
                    continue;
                }
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(png);
                bitmap.EndInit();
                bitmap.Freeze();
                Picture = bitmap;
                PictureChanged?.Invoke();
            }
        }
        finally
        {
            _show.Release();
        }
    }

    public void Play(double rate = 1) { }
    public void Pause() { }

    public void Dispose() => _closing.Cancel();
}
