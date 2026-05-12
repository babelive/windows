using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using MsbIcon = MsBox.Avalonia.Enums.Icon;

namespace Babelive;

/// <summary>
/// Owns the system-tray <see cref="TrayIcon"/> and its context menu so
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
    private readonly TrayIcon _icon;
    private readonly MainWindow _settings;
    private readonly LyricWindow _lyrics;
    private readonly NativeMenuItem _toggleItem;
    private readonly NativeMenuItem _showLyricsItem;
    private bool _disposed;

    public TrayIconHost(MainWindow settings, LyricWindow lyrics)
    {
        _settings = settings;
        _lyrics = lyrics;

        _icon = new TrayIcon
        {
            Icon = AppIcon.Build(),
            ToolTipText = "Babelive",
            IsVisible = true,
        };

        var menu = new NativeMenu();

        _toggleItem = new NativeMenuItem("Start translation");
        _toggleItem.Click += async (_, _) =>
        {
            try { await _settings.ToggleRunningAsync(); }
            catch (Exception ex) { await ShowError("Toggle failed", ex); }
        };
        menu.Items.Add(_toggleItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        _showLyricsItem = new NativeMenuItem("Show lyric overlay")
        {
            // ToggleType enum location varies between Avalonia versions and
            // native menus on Windows don't always render the checkmark
            // reliably — we track state via IsChecked and update the menu
            // text in the click handler so the user sees the toggle effect.
            IsChecked = true,
        };
        _showLyricsItem.Click += (_, _) =>
        {
            // Avalonia's NativeMenuItem doesn't auto-flip IsChecked for
            // CheckBox toggle type the way WinForms did — do it manually.
            _showLyricsItem.IsChecked = !_showLyricsItem.IsChecked;
            if (_showLyricsItem.IsChecked)
            {
                if (!_lyrics.IsVisible) _lyrics.Show();
                _lyrics.Activate();
            }
            else _lyrics.Hide();
        };
        menu.Items.Add(_showLyricsItem);

        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        };
        menu.Items.Add(exitItem);

        _icon.Menu = menu;

        // Left-click on the icon opens the settings window (Avalonia
        // exposes a single Clicked event — there's no separate
        // DoubleClick — so single-click-to-open is the only sensible UX).
        _icon.Clicked += (_, _) => OpenSettings();

        // Keep the toggle item label in sync with running state
        _settings.OnRunningChanged += UpdateToggleLabel;
        UpdateToggleLabel();

        // Keep "Show lyric overlay" check in sync if the user closes the
        // overlay via its ✕ button (which Hides rather than Closes).
        // Avalonia exposes IsVisible as an AvaloniaProperty; subscribe to
        // changes via the property-changed observable.
        _lyrics.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.IsVisibleProperty
                && _showLyricsItem.IsChecked != _lyrics.IsVisible)
                _showLyricsItem.IsChecked = _lyrics.IsVisible;
        };
    }

    private void OpenSettings()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_settings.IsVisible) _settings.Show();
            if (_settings.WindowState == WindowState.Minimized)
                _settings.WindowState = WindowState.Normal;
            _settings.Activate();
        });
    }

    private void UpdateToggleLabel()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _toggleItem.Header = _settings.IsRunning ? "Stop translation" : "Start translation";
        });
    }

    private static async Task ShowError(string title, Exception ex)
    {
        await MessageBoxManager.GetMessageBoxStandard(
            title, ex.Message, ButtonEnum.Ok, MsbIcon.Warning).ShowAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.OnRunningChanged -= UpdateToggleLabel;
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
