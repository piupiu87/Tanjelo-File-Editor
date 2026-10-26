using Tanjelo.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Tanjelo;

/// <summary>The notification-area icon and its menu.</summary>
sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ToolStripMenuItem _hotkeyItem, _toolsItem, _startupItem, _sendToItem;

    public event Action? ConvertFilesClicked;
    public event Action? ShowShelfClicked;
    public event Action? SettingsClicked;
    public event Action? QuitClicked;

    public TrayIcon()
    {
        _hotkeyItem = new Forms.ToolStripMenuItem { Enabled = false };
        _toolsItem = new Forms.ToolStripMenuItem("Converters");
        _startupItem = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        _sendToItem = new Forms.ToolStripMenuItem("Show in \"Send to\" menu") { CheckOnClick = true };
        _startupItem.Click += (_, _) => Toggle(() => ShellIntegration.StartsWithWindows = _startupItem.Checked);
        _sendToItem.Click += (_, _) => Toggle(() => ShellIntegration.InSendToMenu = _sendToItem.Checked);

        var convert = new Forms.ToolStripMenuItem("Convert files…");
        convert.Font = new Drawing.Font(convert.Font, Drawing.FontStyle.Bold);
        convert.Click += (_, _) => ConvertFilesClicked?.Invoke();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange(
        [
            convert,
            _hotkeyItem,
            new Forms.ToolStripMenuItem("Show shelf", null, (_, _) => ShowShelfClicked?.Invoke()),
            new Forms.ToolStripSeparator(),
            _toolsItem,
            _startupItem,
            _sendToItem,
            new Forms.ToolStripMenuItem("Edit settings…", null, (_, _) => SettingsClicked?.Invoke()),
            new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("Quit Tanjelo", null, (_, _) => QuitClicked?.Invoke()),
        ]);
        menu.Opening += (_, _) =>
        {
            _startupItem.Checked = ShellIntegration.StartsWithWindows;
            _sendToItem.Checked = ShellIntegration.InSendToMenu;
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Tanjelo file converter",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ConvertFilesClicked?.Invoke();
        };
    }

    public void SetHotkey(string hotkey) => _hotkeyItem.Text = $"Or press {hotkey} in File Explorer";

    public void SetTools(Toolbox tools)
    {
        string Status(bool found, string what) => (found ? "✓  " : "✗  ") + what;
        var office = tools.Word || tools.PowerPoint || tools.Excel ? "Microsoft Office" : tools.LibreOffice is not null ? "LibreOffice" : "not found";
        _toolsItem.DropDownItems.Clear();
        _toolsItem.DropDownItems.AddRange(
        [
            new Forms.ToolStripMenuItem(Status(true, "Images and PDF pages (built in)")) { Enabled = false },
            new Forms.ToolStripMenuItem(Status(tools.Ffmpeg is not null, "Audio and video: " + (tools.Ffmpeg is null ? "ffmpeg not found" : "ffmpeg"))) { Enabled = false },
            new Forms.ToolStripMenuItem(Status(office != "not found", "Office documents: " + office)) { Enabled = false },
            new Forms.ToolStripMenuItem(Status(true, "Archives: " + (tools.SevenZip is not null ? "7-Zip" : "Windows (zip, tar)"))) { Enabled = false },
            new Forms.ToolStripMenuItem(Status(tools.Pandoc is not null, "Word and EPUB: " + (tools.Pandoc is null ? "pandoc not found" : "pandoc"))) { Enabled = false },
            new Forms.ToolStripMenuItem(Status(tools.WebView2 is not null, "Markdown to PDF: " + (tools.WebView2 is null ? "WebView2 not found" : "WebView2"))) { Enabled = false },
        ]);
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);

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
