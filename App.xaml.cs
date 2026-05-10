using System.Windows;
using Application = System.Windows.Application;

namespace Babelive;

public partial class App : Application
{
    private MainWindow? _settings;
    private LyricWindow? _lyrics;
    private TrayIconHost? _tray;

    private void App_Startup(object sender, StartupEventArgs e)
    {
        // Settings window: holds all the audio + translator plumbing. Hidden
        // by default; opened from the tray icon or the lyric overlay's ⚙.
        _settings = new MainWindow();
        // Hide-on-close so the X button doesn't terminate the app — exit
        // is only via the tray menu's "Exit" item.
        _settings.Closing += (_, args) =>
        {
            args.Cancel = true;
            _settings.Hide();
        };

        // Lyric overlay: the new "main" window. Transparent, topmost, bottom-center.
        _lyrics = new LyricWindow(_settings);
        _lyrics.Show();

        // System tray icon: Start/Stop, show/hide overlay, open settings, exit.
        _tray = new TrayIconHost(_settings, _lyrics);

        // When the user picks "Exit" from the tray menu we Shutdown(); make
        // sure the audio/translator state is cleaned up first.
        Exit += async (_, _) =>
        {
            try { if (_settings.IsRunning) await _settings.ToggleRunningAsync(); } catch { /* ignore */ }
            _tray?.Dispose();
        };
    }
}
