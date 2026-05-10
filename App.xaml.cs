using System.Threading;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Babelive;

public partial class App : Application
{
    // Named system-wide mutex used to detect a second instance. The "Local\"
    // prefix scopes it to the current logon session, so two different users
    // on the same machine can each run their own copy.
    private const string SingleInstanceMutexName = @"Local\Babelive.SingleInstance.Mutex";
    private Mutex? _singleInstanceMutex;

    private MainWindow? _settings;
    private LyricWindow? _lyrics;
    private TrayIconHost? _tray;

    private void App_Startup(object sender, StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // Another instance owns the mutex. Bail out without touching any
            // UI/audio state so we don't double up tray icons or grab the mic.
            MessageBox.Show(
                "Babelive is already running. Check the system tray.",
                "Babelive",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

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
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* not owned */ }
            _singleInstanceMutex?.Dispose();
        };
    }
}
