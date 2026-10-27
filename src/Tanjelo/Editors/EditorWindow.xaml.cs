using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Tanjelo.Core;

namespace Tanjelo.Editors;

/// <summary>
/// The window every editor lives in: the file at the top, the editor's tools, its canvas or grid with settings beside
/// it, and zoom, Cancel and Save at the bottom. Saving hands the edits back as a tool entry for the engine to write
/// beside the original; nothing is written here.
/// </summary>
public partial class EditorWindow : Window
{
    readonly Editor _editor;
    readonly TaskCompletionSource<Target?> _result = new();
    readonly CancellationTokenSource _closing = new();
    bool _loaded;
    bool _done;

    internal EditorWindow(string file, Target target, Editor editor)
    {
        InitializeComponent();
        _editor = editor;
        Glyph.Text = new FormatMenuWindow.Entry(target).Glyph;
        Heading.Text = target.Label.TrimEnd('…');
        Subheading.Text = $"{Path.GetFileName(file)} · {Describe.Size(new FileInfo(file).Length)}";
        Title = $"{Heading.Text} · {Path.GetFileName(file)}";
        SaveButton.Content = target.Confirm ?? "Save";

        MainSlot.Content = editor.Main;
        ToolSlot.Content = editor.Toolbar;
        if (editor.Panel is { } panel)
        {
            PanelSlot.Content = panel;
            PanelScroller.Visibility = Visibility.Visible;
        }
        if (editor.Canvas is { } canvas)
        {
            ZoomBar.Visibility = Visibility.Visible;
            canvas.ViewChanged += ShowZoom;
            ZoomOutButton.Click += (_, _) => canvas.Step(-1);
            ZoomInButton.Click += (_, _) => canvas.Step(1);
            FitButton.Click += (_, _) => canvas.Fit();
            ActualButton.Click += (_, _) => canvas.ZoomTo(1);
        }
        editor.Updated += Refresh;

        UndoButton.Click += (_, _) => editor.Undo();
        RedoButton.Click += (_, _) => editor.Redo();
        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Cancel();
        CloseButton.Click += (_, _) => Cancel();
        MaximizeButton.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        KeepButton.Click += (_, _) => Confirm.Visibility = Visibility.Collapsed;
        DiscardButton.Click += (_, _) => Finish(null);
        StateChanged += (_, _) => FitMaximized();
        PreviewKeyDown += OnKey;
        PreviewKeyUp += (_, e) =>
        {
            if (e.Key == Key.Space && _editor.Canvas is { } held) held.SpaceHeld = false;
            if (_editor.OnKeyUp(e)) e.Handled = true;
        };
        Closing += (_, e) =>
        {
            // Closing from the taskbar or Alt+F4 asks first too, like Cancel.
            if (_done) return;
            e.Cancel = true;
            Cancel();
        };
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _result.TrySetResult(null);
        };
        SourceInitialized += OnSourceInitialized;
        ContentRendered += (_, _) =>
        {
            Opacity = 1;
            Activate();
            (_editor.Canvas ?? (IInputElement)_editor.Main).Focus();
        };
        Refresh();
    }

    /// <summary>Shows the window, loads the file, and completes with the entry to run on Save, or null if canceled.</summary>
    public Task<Target?> EditAsync()
    {
        Show();
        _ = LoadAsync();
        return EditResult;
    }

    /// <summary>The entry to run, once saved; null once canceled.</summary>
    internal Task<Target?> EditResult => _result.Task;

    /// <summary>Reads the file into the editor, showing progress and then any problem in place of the canvas.</summary>
    internal async Task LoadAsync()
    {
        Message.Text = "Opening…";
        Busy.Visibility = Visibility.Visible;
        try
        {
            await _editor.LoadAsync(_closing.Token);
            _loaded = true;
            Message.Text = "";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (ex is not ConversionException) Log.Write(ex);
            Message.Text = ex.Message;
            MainSlot.Visibility = Visibility.Collapsed;
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
        Subheading.Text += _editor.Summary.Length > 0 ? " · " + _editor.Summary : "";
        Refresh();
    }

    void Refresh()
    {
        UndoButton.IsEnabled = _loaded && _editor.CanUndo;
        RedoButton.IsEnabled = _loaded && _editor.CanRedo;
        SaveButton.IsEnabled = _loaded && _editor.Changed;
        StatusText.Text = _loaded ? _editor.Status : "";
        NoticeText.Text = _loaded ? _editor.Notice ?? "" : "";
        NoticeBar.Visibility = NoticeText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowZoom();
    }

    void ShowZoom()
    {
        if (_editor.Canvas is not { HasPicture: true } canvas) return;
        ZoomText.Text = $"{Math.Round(canvas.View.Zoom * 100)}%";
        ZoomOutButton.IsEnabled = canvas.View.Zoom > ZoomView.Least;
        ZoomInButton.IsEnabled = canvas.View.Zoom < ZoomView.Most;
    }

    internal void Save()
    {
        if (!SaveButton.IsEnabled || Confirm.Visibility == Visibility.Visible) return;
        Target target;
        try
        {
            target = _editor.Result();
        }
        catch (ConversionException ex)
        {
            NoticeText.Text = ex.Message;
            NoticeBar.Visibility = Visibility.Visible;
            return;
        }
        Finish(target);
    }

    /// <summary>Closes, first asking whether to throw away the changes when there are any.</summary>
    internal void Cancel()
    {
        if (_loaded && _editor.Changed && Confirm.Visibility != Visibility.Visible)
        {
            Confirm.Visibility = Visibility.Visible;
            return;
        }
        Finish(null);
    }

    void Finish(Target? target)
    {
        if (_done) return;
        _done = true;
        _result.TrySetResult(target);
        Close();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (Confirm.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) Confirm.Visibility = Visibility.Collapsed;
            else if (e.Key == Key.Enter) Finish(null);
            else return;
            e.Handled = true;
            return;
        }
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var typing = Keyboard.FocusedElement is TextBox;
        // The editor goes first; a text box being typed in keeps Escape and Ctrl+Z for itself.
        if (_loaded && _editor.OnKey(e))
        {
            e.Handled = true;
            return;
        }
        var canvas = _editor.Canvas;
        switch (e.Key)
        {
            case Key.Escape when !typing:
                Cancel();
                break;
            case Key.S when ctrl:
                Save();
                break;
            case Key.Z when ctrl && !typing && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
            case Key.Y when ctrl && !typing:
                _editor.Redo();
                break;
            case Key.Z when ctrl && !typing:
                _editor.Undo();
                break;
            case Key.Space when canvas is not null && !typing:
                canvas.SpaceHeld = true;
                break;
            case Key.D0 or Key.NumPad0 when ctrl && canvas is not null:
                canvas.Fit();
                break;
            case Key.D1 or Key.NumPad1 when ctrl && canvas is not null:
                canvas.ZoomTo(1);
                break;
            case Key.OemPlus or Key.Add when ctrl && canvas is not null:
                canvas.Step(1);
                break;
            case Key.OemMinus or Key.Subtract when ctrl && canvas is not null:
                canvas.Step(-1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // Opened on the monitor with the pointer, most of its work area, centred: placed in physical pixels while still
    // invisible, so WPF lays it out at that monitor's DPI.
    void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.StyleWindow(hwnd, Theme.IsDark);
        Native.GetCursorPos(out var pointer);
        var work = Native.WorkAreaAt(pointer);
        int workWidth = work.Right - work.Left, workHeight = work.Bottom - work.Top;
        var width = Math.Min(workWidth, (int)(workWidth * 0.82));
        var height = Math.Min(workHeight, (int)(workHeight * 0.88));
        Native.SetWindowPos(hwnd, IntPtr.Zero, work.Left + (workWidth - width) / 2, work.Top + (workHeight - height) / 2, width, height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>A maximized frameless window reaches past the screen by its resize border; this keeps the edges in view.</summary>
    void FitMaximized()
    {
        var border = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = WindowState == WindowState.Maximized
            ? new Thickness(border.Left + 4, border.Top + 4, border.Right + 4, border.Bottom + 4)
            : new Thickness(0);
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    internal Brush Brush(string key) => (Brush)FindResource(key);
}
