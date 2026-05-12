using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MsBox.Avalonia;
using MsbIcon = MsBox.Avalonia.Enums.Icon;
using MsBox.Avalonia.Enums;

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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override async void OnFrameworkInitializationCompleted()
    {
        // Avalonia routes desktop apps through IClassicDesktopStyleApplicationLifetime.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true,
                                              SingleInstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                // Another instance owns the mutex. Show a non-modal popup
                // (no owner yet) and exit before any UI/audio state is set up.
                await MessageBoxManager.GetMessageBoxStandard(
                    "Babelive",
                    "Babelive is already running. Check the system tray.",
                    ButtonEnum.Ok,
                    MsbIcon.Info).ShowAsync();
                desktop.Shutdown();
                return;
            }

            // Don't quit when the last visible window is hidden — the tray
            // icon keeps the app alive. Exit happens only via the tray menu's
            // "Exit" item which calls Shutdown explicitly.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Settings window: holds all the audio + translator plumbing.
            // Hidden by default; opened from the tray icon or the lyric
            // overlay's ⚙. Hide-on-close so the ✕ button doesn't terminate.
            _settings = new MainWindow();
            _settings.Closing += (_, e) =>
            {
                // Allow real shutdown (Application.Shutdown) to pass through;
                // only intercept user-initiated close.
                if (e.CloseReason != WindowCloseReason.ApplicationShutdown)
                {
                    e.Cancel = true;
                    _settings.Hide();
                }
            };

            // Lyric overlay: the "main" window. Transparent, topmost, bottom-center.
            _lyrics = new LyricWindow(_settings);
            _lyrics.Show();

            // System tray icon: Start/Stop, show/hide overlay, open settings, exit.
            _tray = new TrayIconHost(_settings, _lyrics);

            // When the user picks "Exit" from the tray menu we Shutdown(); make
            // sure the audio/translator state is cleaned up first.
            desktop.Exit += async (_, _) =>
            {
                try { if (_settings.IsRunning) await _settings.ToggleRunningAsync(); }
                catch { /* ignore */ }
                _tray?.Dispose();
                try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* not owned */ }
                _singleInstanceMutex?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
