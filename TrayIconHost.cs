using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Babelive;

/// <summary>
/// Owns the system-tray <see cref="NotifyIcon"/> and its context menu so
/// the user can:
/// <list type="bullet">
///   <item>Toggle translation on/off</item>
///   <item>Show / hide the lyric overlay</item>
///   <item>Open the settings window</item>
///   <item>Exit the application</item>
/// </list>
/// Owns nothing audio-related itself — just a controller around the two
/// windows.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MainWindow _settings;
    private readonly LyricWindow _lyrics;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _showLyricsItem;
    private bool _disposed;

    public TrayIconHost(MainWindow settings, LyricWindow lyrics)
    {
        _settings = settings;
        _lyrics = lyrics;

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "Babelive",
        };

        var menu = new ContextMenuStrip();

        _toggleItem = new ToolStripMenuItem("Start translation") { ShortcutKeyDisplayString = "" };
        _toggleItem.Click += async (_, _) =>
        {
            try { await _settings.ToggleRunningAsync(); }
            catch (Exception ex) { ShowError("Toggle failed", ex); }
        };
        menu.Items.Add(_toggleItem);

        menu.Items.Add(new ToolStripSeparator());

        _showLyricsItem = new ToolStripMenuItem("Show lyric overlay") { CheckOnClick = true, Checked = true };
        _showLyricsItem.CheckedChanged += (_, _) =>
        {
            if (_showLyricsItem.Checked)
            {
                if (!_lyrics.IsVisible) _lyrics.Show();
                _lyrics.Activate();
            }
            else _lyrics.Hide();
        };
        menu.Items.Add(_showLyricsItem);

        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exitItem);

        _icon.ContextMenuStrip = menu;

        // Double-click → open settings (common pattern)
        _icon.DoubleClick += (_, _) => OpenSettings();

        // Keep the toggle item label in sync with running state
        _settings.OnRunningChanged += UpdateToggleLabel;
        UpdateToggleLabel();

        // Keep "Show lyric overlay" check in sync if the user closes the
        // overlay via its ✕ button (which Hides rather than Closes).
        _lyrics.IsVisibleChanged += (_, _) =>
        {
            if (_showLyricsItem.Checked != _lyrics.IsVisible)
                _showLyricsItem.Checked = _lyrics.IsVisible;
        };
    }

    private void OpenSettings()
    {
        if (!_settings.IsVisible) _settings.Show();
        if (_settings.WindowState == WindowState.Minimized)
            _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    private void UpdateToggleLabel()
    {
        // NotifyIcon menu lives on UI thread; Dispatcher.Invoke for safety.
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _toggleItem.Text = _settings.IsRunning ? "Stop translation" : "Start translation";
        });
    }

    private static void ShowError(string title, Exception ex) =>
        MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Generate a tiny solid-color icon at runtime so we don't need a .ico asset.</summary>
    private static Icon LoadIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(255, 58, 115, 196));
            g.FillEllipse(brush, 2, 2, 28, 28);
            using var font = new Font("Segoe UI", 14, System.Drawing.FontStyle.Bold);
            using var white = new SolidBrush(Color.White);
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("译", font, white, new RectangleF(0, 0, 32, 32), sf);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.OnRunningChanged -= UpdateToggleLabel;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
