using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace Babelive;

/// <summary>
/// Transparent always-on-top "desktop lyrics" overlay (千千静听-style).
/// Source-language sentence on a small dim line above, larger gradient-
/// filled translation below, with a faked stroke (4 stacked black
/// TextBlocks at ±1.2 px offsets) and soft glow.
///
/// Visual spec sourced from NetEase / TTPlayer / QQ Music / Kugou desktop-
/// lyric conventions: warm white→amber gradient, Microsoft YaHei UI 34 pt
/// bold, frame fades in on hover, Viewbox shrink-to-fit so long sentences
/// don't wrap.
/// </summary>
public partial class LyricWindow : Window
{
    private readonly MainWindow _settings;

    // Cap each rolling buffer well within what fits at the default font
    // size, so the Viewbox never shrinks the text down. At 1100 px window
    // width the translation (34 pt CJK) fits ~30 chars on a line; source
    // (18 pt) fits ~55. Going over → Viewbox shrinks → unreadable tiny text.
    private const int MaxTargetChars = 30;
    private const int MaxSourceChars = 55;
    private string _sourceBuf = string.Empty;
    private string _targetBuf = string.Empty;

    // Translation font size (user-adjustable via A−/A+ buttons)
    private double _translationFontSize = 34;
    private const double MinFontSize = 18;
    private const double MaxFontSize = 64;
    private const double FontStep = 2;

    public LyricWindow(MainWindow settings)
    {
        InitializeComponent();
        _settings = settings;
        PositionAtBottomCenter();

        _settings.OnSourceDelta     += OnSourceDelta;
        _settings.OnTranslatedDelta += OnTranslatedDelta;
        _settings.OnRunningChanged  += UpdateToggleLabel;
        UpdateToggleLabel();
    }

    private void PositionAtBottomCenter()
    {
        // Flush against the top of the taskbar — workArea.Bottom already
        // excludes the taskbar, so subtract Height to sit exactly on its edge.
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top  = workArea.Bottom - Height;
    }

    // ---- hover: fade in toolbar + translucent frame ----------------------
    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        Fade(Toolbar,    to: 1.0, ms: 200);
        Fade(HoverFrame, to: 1.0, ms: 280);
        Fade(ResizeGrip, to: 1.0, ms: 200);
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        Fade(Toolbar,    to: 0.0, ms: 320);
        Fade(HoverFrame, to: 0.0, ms: 320);
        // Grip stays faintly visible (not invisible) so the user can still
        // see where to grab even without hovering first.
        Fade(ResizeGrip, to: 0.4, ms: 320);
    }

    private static void Fade(UIElement target, double to, int ms)
    {
        target.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // ---- drag to move + double-click-snap --------------------------------
    // Drag starts LAZILY: we set _maybeStartDrag on MouseDown and only
    // actually call DragMove once the user crosses a 3 px hysteresis in
    // MouseMove. Why: DragMove enters a Win32 NC modal loop that swallows
    // the next mouse-up — if we called it eagerly on every MouseDown,
    // double-click detection (which needs to see two consecutive
    // MouseDowns within DoubleClickTime) would break, because the first
    // click's mouse-up would never reach WPF and ClickCount would reset.
    private bool _maybeStartDrag;
    private System.Windows.Point _maybeDragStartPos;
    private const double DragHysteresisPx = 3;
    // y < TopStripPx counts as "the empty area at the top" for the
    // double-click snap gesture. The source line sits ~62 px from the
    // window's top edge at default 170 px height; 50 leaves a small buffer
    // so a slightly-low double-click still registers without overlapping
    // the source text.
    private const double TopStripPx = 50;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Double-click in the top empty strip → toggle-snap between
        // top-of-screen and bottom-of-screen (flush with taskbar), both
        // centered horizontally. Decision based on which half the panel's
        // CENTER currently sits in, so it works even if the user has
        // dragged the panel a few pixels off the exact top/bottom edge.
        if (e.ClickCount == 2 && e.GetPosition(this).Y < TopStripPx)
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            double midY    = wa.Top + wa.Height / 2;
            double centerY = Top + Height / 2;
            Top = centerY < midY
                ? wa.Bottom - Height   // currently top half → flip to bottom
                : wa.Top;              // currently bottom half → flip to top
            _maybeStartDrag = false;
            e.Handled = true;
            return;
        }

        // Single click — arm a deferred drag instead of calling DragMove
        // immediately (see comment above the field declarations).
        _maybeStartDrag = true;
        _maybeDragStartPos = e.GetPosition(this);
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_maybeStartDrag) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // Button got released somehow without our up-handler seeing it
            // (window lost focus mid-press, etc.) — clear the latch.
            _maybeStartDrag = false;
            return;
        }
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _maybeDragStartPos.X) > DragHysteresisPx ||
            Math.Abs(pos.Y - _maybeDragStartPos.Y) > DragHysteresisPx)
        {
            _maybeStartDrag = false;
            try { DragMove(); } catch { /* DragMove can throw mid-animation */ }
        }
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Tap (down + up without crossing the hysteresis) — just disarm.
        _maybeStartDrag = false;
    }

    // ---- bottom-right resize grip ----------------------------------------
    // Manual mouse tracking. The "proper" approach (synthesizing
    // WM_NCLBUTTONDOWN+HTBOTTOMRIGHT to the OS) is silently dropped on
    // layered windows (AllowsTransparency=True), so do it by hand: capture
    // the mouse on press, recompute Width/Height from the delta on move,
    // release on up. Top-left of the window stays fixed because we only
    // change Width/Height, never Left/Top.
    private bool _isResizing;
    private System.Windows.Point _resizeAnchor;
    private double _resizeStartWidth;
    private double _resizeStartHeight;

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isResizing = true;
        _resizeAnchor = e.GetPosition(this);
        _resizeStartWidth  = ActualWidth;
        _resizeStartHeight = ActualHeight;
        ResizeGrip.CaptureMouse();
        // Stop bubbling — otherwise Window_MouseLeftButtonDown calls
        // DragMove and the move + resize fight each other.
        e.Handled = true;
    }

    private void ResizeGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizing) return;
        var p = e.GetPosition(this);
        // Window's top-left is fixed; coords here are relative to that
        // unchanged origin, so deltas in window-space == deltas in screen-
        // space and we can apply them straight to the size.
        double w = _resizeStartWidth  + (p.X - _resizeAnchor.X);
        double h = _resizeStartHeight + (p.Y - _resizeAnchor.Y);
        Width  = Math.Max(MinWidth,  Math.Min(MaxWidth,  w));
        Height = Math.Max(MinHeight, Math.Min(MaxHeight, h));
    }

    private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isResizing) return;
        _isResizing = false;
        ResizeGrip.ReleaseMouseCapture();
        e.Handled = true;
    }

    // ---- toolbar buttons -------------------------------------------------
    private async void ToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        try { await _settings.ToggleRunningAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Toggle failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.IsVisible) _settings.Show();
        if (_settings.WindowState == WindowState.Minimized)
            _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    private void HideBtn_Click(object sender, RoutedEventArgs e) => Hide();

    private void FontMinusBtn_Click(object sender, RoutedEventArgs e)
    {
        _translationFontSize = Math.Max(MinFontSize, _translationFontSize - FontStep);
        TranslationText.FontSize = _translationFontSize;
    }

    private void FontPlusBtn_Click(object sender, RoutedEventArgs e)
    {
        _translationFontSize = Math.Min(MaxFontSize, _translationFontSize + FontStep);
        TranslationText.FontSize = _translationFontSize;
    }

    private const float VolumeStep = 0.10f;
    private void VolDownBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.TranslationVolume = _settings.TranslationVolume - VolumeStep;
        ShowVolumeFlash();
    }
    private void VolUpBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.TranslationVolume = _settings.TranslationVolume + VolumeStep;
        ShowVolumeFlash();
    }

    private void ShowVolumeFlash()
    {
        // Briefly tag the source line with the current volume so the user
        // gets feedback. Cheap, no extra UI element required.
        var pct = (int)Math.Round(_settings.TranslationVolume * 100);
        var prev = SourceText.Text;
        SourceText.Text = $"♪ {pct}%";
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(900);
            // Only restore if the user didn't trigger another flash since
            if (SourceText.Text == $"♪ {pct}%")
                SourceText.Text = prev;
        });
    }

    // ---- text streaming --------------------------------------------------
    private void OnSourceDelta(string delta)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _sourceBuf = AppendAndTrim(_sourceBuf, delta, MaxSourceChars);
            SourceText.Text = _sourceBuf;
        });
    }

    private void OnTranslatedDelta(string delta)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _targetBuf = AppendAndTrim(_targetBuf, delta, MaxTargetChars);
            TranslationText.Text = _targetBuf;
        });
    }

    /// <summary>
    /// Rolling-window text buffer: append the new delta, then if we've
    /// exceeded the max-chars cap, drop everything before the most recent
    /// sentence boundary (or hard-cut if no boundary in the keep window).
    /// </summary>
    private static string AppendAndTrim(string buf, string delta, int maxChars)
    {
        buf += delta;
        if (buf.Length <= maxChars) return buf;

        int keepStart = buf.Length - maxChars;
        // Look for a sentence boundary inside the keep window so we don't
        // cut mid-word. Skip the very last few chars so we don't trim to
        // "nothing visible".
        int sentenceEnd = -1;
        for (int i = keepStart; i < buf.Length - 4; i++)
        {
            char c = buf[i];
            if (c == '。' || c == '.' || c == '?' || c == '?'
                || c == '!' || c == '!' || c == ';' || c == ';')
            { sentenceEnd = i; break; }
        }
        return sentenceEnd >= 0 ? buf[(sentenceEnd + 1)..].TrimStart() : buf[keepStart..];
    }

    public void UpdateToggleLabel()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ToggleBtn.Content = _settings.IsRunning ? "■ Stop" : "▶ Start";
        });
    }

    public void ClearLyrics()
    {
        _sourceBuf = string.Empty;
        _targetBuf = string.Empty;
        SourceText.Text = string.Empty;
        TranslationText.Text = string.Empty;
    }

    protected override void OnClosed(EventArgs e)
    {
        _settings.OnSourceDelta     -= OnSourceDelta;
        _settings.OnTranslatedDelta -= OnTranslatedDelta;
        _settings.OnRunningChanged  -= UpdateToggleLabel;
        base.OnClosed(e);
    }
}
