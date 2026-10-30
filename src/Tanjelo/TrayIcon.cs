using Tanjelo.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Tanjelo;

/// <summary>The notification-area icon and its menu.</summary>
sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ToolStripMenuItem _hotkeyItem, _toolsItem, _startupItem, _sendToItem;
    Action? _clicked; // for the notice showing now

    public event Action? ConvertFilesClicked;
    public event Action? ShowShelfClicked;
    public event Action? SettingsClicked;
    public event Action? QuitClicked;

    public TrayIcon()
    {
        _hotkeyItem = new Forms.ToolStripMenuItem { Enabled = false };
        _toolsItem = new Forms.ToolStripMenuItem(Strings.Converters);
        _startupItem = new Forms.ToolStripMenuItem(Strings.StartWithWindows) { CheckOnClick = true };
        _sendToItem = new Forms.ToolStripMenuItem(Strings.ShowInSendToMenu) { CheckOnClick = true };
        _startupItem.Click += (_, _) => Toggle(() => ShellIntegration.StartsWithWindows = _startupItem.Checked);
        _sendToItem.Click += (_, _) => Toggle(() => ShellIntegration.InSendToMenu = _sendToItem.Checked);

        var convert = new Forms.ToolStripMenuItem(Strings.ConvertFiles);
        convert.Font = new Drawing.Font(convert.Font, Drawing.FontStyle.Bold);
        convert.Click += (_, _) => ConvertFilesClicked?.Invoke();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange(
        [
            convert,
            _hotkeyItem,
            new Forms.ToolStripMenuItem(Strings.ShowShelf, null, (_, _) => ShowShelfClicked?.Invoke()),
            new Forms.ToolStripSeparator(),
            _toolsItem,
            _startupItem,
            _sendToItem,
            new Forms.ToolStripMenuItem(Strings.Settings, null, (_, _) => SettingsClicked?.Invoke()),
            new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem(Strings.QuitTanjelo, null, (_, _) => QuitClicked?.Invoke()),
        ]);
        menu.Opening += (_, _) =>
        {
            _startupItem.Checked = ShellIntegration.StartsWithWindows;
            _sendToItem.Checked = ShellIntegration.InSendToMenu;
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = Strings.TanjeloFileConverter,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.BalloonTipClicked += (_, _) => _clicked?.Invoke();
        _icon.BalloonTipClosed += (_, _) => _clicked = null;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ConvertFilesClicked?.Invoke();
        };
    }

    public void SetHotkey(string hotkey) => _hotkeyItem.Text = string.Format(Strings.OrPressInFileExplorer, hotkey);

    public void SetTools(Toolbox tools)
    {
        string Status(bool found, string what) => (found ? "✓  " : "✗  ") + what;
        var office = tools.Word || tools.PowerPoint || tools.Excel ? Strings.MicrosoftOffice : tools.LibreOffice is not null ? Strings.LibreOffice : Strings.NotFound;
        _toolsItem.DropDownItems.Clear();
        _toolsItem.DropDownItems.AddRange(
        [
            new Forms.ToolStripMenuItem(Status(true, Strings.ImagesAndPDFPagesBuiltIn)) { Enabled = false },
            new Forms.ToolStripMenuItem(Status(tools.Ffmpeg is not null, Strings.AudioAndVideo + (tools.Ffmpeg is null ? Strings.FfmpegNotFound : "ffmpeg"))) { Enabled = false },
            new Forms.ToolStripMenuItem(Status(office != "not found", Strings.OfficeDocuments + office)) { Enabled = false },
            new Forms.ToolStripMenuItem(Status(true, Strings.Archives + (tools.SevenZip is not null ? Strings.N7Zip : Strings.WindowsZipTar))) { Enabled = false },
            new Forms.ToolStripMenuItem(Status(tools.Pandoc is not null, Strings.WordAndEPUB + (tools.Pandoc is null ? Strings.PandocNotFound : "pandoc"))) { Enabled = false },
            new Forms.ToolStripMenuItem(Status(tools.WebView2 is not null, Strings.MarkdownToPDF + (tools.WebView2 is null ? Strings.WebView2NotFound : Strings.WebView2))) { Enabled = false },
        ]);
    }

    /// <param name="clicked">What clicking the notice does, such as opening a page; nothing by default.</param>
    public void Notify(string title, string text, Action? clicked = null)
    {
        _clicked = clicked;
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
    }

    static void Toggle(Action change)
    {
        try { change(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Forms.MessageBox.Show(ex.Message, "Tanjelo", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
        }
    }

    static Drawing.Icon LoadIcon()
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("app.ico")!;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
