using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NAudio.CoreAudioApi;
using Babelive.Audio;
using Babelive.Translation;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using MsbIcon = MsBox.Avalonia.Enums.Icon;

namespace Babelive;

public partial class MainWindow : Window
{
    private string _apiKey = string.Empty;

    private LoopbackCapture? _capture;
    private AudioPlayer? _player;
    // Source-monitor player: only created in CABLE/virtual-cable capture mode
    // (when the user routed source audio into a virtual cable so we could
    // bypass Teams/DRM loopback protection — see PREVENT_LOOPBACK_CAPTURE).
    // The virtual cable has no physical speaker, so without this the user
    // would hear ONLY the translation, never the original. Plays the same
    // PCM stream the translator sees, on the Playback device.
    private AudioPlayer? _monitorPlayer;
    private RealtimeTranslatorClient? _client;
    private DuckController? _ducker;
    private EndpointMuter? _endpointMuter;
    private DefaultDeviceSetter? _defaultDeviceSetter;
    private AppAudioRouter? _appRouter;
    private bool _running;

    // Public surface so the lyric overlay (LyricWindow) and tray icon can
    // observe state and trigger actions without owning any of the audio
    // plumbing themselves.
    public bool IsRunning => _running;
    public event Action<string>? OnTranslatedDelta;
    public event Action<string>? OnSourceDelta;
    public event Action? OnRunningChanged;
    public event Action<float>? OnVolumeChanged;
    public Task ToggleRunningAsync() => _running ? StopAsync() : StartAsync();

    // Translation playback volume. Persists across Start/Stop cycles —
    // applied to the AudioPlayer the moment it gets created.
    private float _translationVolume = 1.0f;
    public float TranslationVolume
    {
        get => _translationVolume;
        set
        {
            _translationVolume = Math.Clamp(value, 0f, 1f);
            if (_player != null) _player.Volume = _translationVolume;
            try { OnVolumeChanged?.Invoke(_translationVolume); } catch { /* ignore */ }
        }
    }

    private List<MMDevice> _captureDevices = new();
    private List<MMDevice> _playDevices = new();
    // Parallel to CaptureCombo.Items: each combo entry maps to exactly one
    // CaptureSource. Index 0 is the "All system audio (no echo)" entry on
    // Win10 20348+ machines; remaining entries are per-render-device legacy
    // loopbacks.
    private List<CaptureSource> _captureSources = new();

    // User-managed API endpoint + key overrides (persisted to %APPDATA%).
    private AppSettings _appSettings = new();

    public MainWindow()
    {
        // Don't define InitializeComponent() ourselves — Avalonia's XamlIl
        // compiler generates a partial that both loads the XAML AND assigns
        // every x:Name to its strongly-typed field. Overriding it with a
        // hand-rolled "just AvaloniaXamlLoader.Load(this)" leaves the
        // x:Name fields null and crashes on the first access.
        InitializeComponent();
        // Window.Icon controls the taskbar / Alt-Tab / window-thumb icon.
        // Avalonia doesn't auto-derive it from anything; we render the same
        // amber-disc + 译 glyph the tray uses.
        Icon = AppIcon.Build();
        ApplyAppSettings();
        PopulateLanguages();
        PopulateDevices();
    }

    /// <summary>
    /// Sole source of <see cref="_apiKey"/> — read from
    /// <see cref="AppSettings"/> on disk. Called at startup and after the
    /// API settings dialog saves.
    /// </summary>
    private void ApplyAppSettings()
    {
        _appSettings = AppSettings.Load();
        _apiKey = _appSettings.ApiKey?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(_apiKey))
            StatusText.Text = "no API key — click API… to set one";
    }

    private async void ApiBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (await ShowApiSettingsDialog())
        {
            ApplyAppSettings();
            if (!_running && !string.IsNullOrEmpty(_apiKey))
                StatusText.Text = "idle";
        }
    }

    /// <summary>
    /// Open the API settings dialog modally. In Avalonia ShowDialog is async
    /// and only valid against a realized owner window. We use `this` directly
    /// since MainWindow is the orchestrator — for the "started from
    /// LyricWindow → Start → no key" path the lyric overlay calls into us
    /// after MainWindow exists.
    /// </summary>
    private async Task<bool> ShowApiSettingsDialog()
    {
        var dlg = new ApiSettingsWindow(_appSettings);
        // ShowDialog<bool> awaits until the dialog closes and returns its
        // Close(result) value. ApiSettingsWindow sets true on Save, false
        // on Cancel.
        return await dlg.ShowDialog<bool>(this);
    }

    // ---- setup -----------------------------------------------------------
    private void PopulateLanguages()
    {
        foreach (var (code, name) in LanguageCodes.All)
            LangCombo.Items.Add($"{code}  —  {name}");
        LangCombo.SelectedIndex = 0; // zh
    }

    private void PopulateDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var renderDevices = enumerator.EnumerateAudioEndPoints(
                DataFlow.Render, DeviceState.Active).ToList();

            _captureDevices = renderDevices;
            RebuildCaptureSourceList(preserveSelection: false);
            RebuildPlaybackList(preserveSelection: false);
        }
        catch (Exception e)
        {
            StatusText.Text = $"device enumeration failed: {e.Message}";
        }
    }

    private void RefreshPlaybackBtn_Click(object? sender, RoutedEventArgs e)
    {
        RebuildPlaybackList(preserveSelection: true);
    }

    /// <summary>
    /// Rebuild the playback dropdown by re-enumerating active render
    /// endpoints. Preserves the previous selection by device ID when
    /// possible; otherwise falls back to smart-default scoring.
    /// </summary>
    private void RebuildPlaybackList(bool preserveSelection)
    {
        string? prevId = null;
        if (preserveSelection
            && PlayCombo.SelectedIndex > 0  // 0 = "(system default)"
            && PlayCombo.SelectedIndex - 1 < _playDevices.Count)
        {
            prevId = _playDevices[PlayCombo.SelectedIndex - 1].ID;
        }

        using (var enumerator = new MMDeviceEnumerator())
        {
            _playDevices = enumerator.EnumerateAudioEndPoints(
                DataFlow.Render, DeviceState.Active).ToList();
        }

        PlayCombo.Items.Clear();
        PlayCombo.Items.Add("(system default)");
        foreach (var d in _playDevices)
            PlayCombo.Items.Add(d.FriendlyName);

        int restored = -1;
        if (prevId != null)
        {
            for (int i = 0; i < _playDevices.Count; i++)
            {
                if (_playDevices[i].ID == prevId) { restored = i + 1; break; }
            }
        }

        if (restored < 0)
        {
            int bestIdx = -1;
            int bestScore = 0;
            for (int i = 0; i < _playDevices.Count; i++)
            {
                int score = ScorePlaybackCandidate(_playDevices[i], captureId: null);
                if (score > bestScore) { bestScore = score; bestIdx = i; }
            }
            restored = bestIdx >= 0 ? bestIdx + 1 : 0;
        }

        PlayCombo.SelectedIndex = restored;
    }

    // ---- buttons ---------------------------------------------------------
    private async void StartBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_running) await StopAsync();
        else await StartAsync();
    }

    private void ClearBtn_Click(object? sender, RoutedEventArgs e)
    {
        SourceText.Text = string.Empty;
        TargetText.Text = string.Empty;
    }

    private void DuckSlider_ValueChanged(object? sender,
        RangeBaseValueChangedEventArgs e)
    {
        // Live-update if a session is currently active. The monitor reads
        // _ducker.DuckRatio per PCM chunk, so its gain updates on the next
        // ~10 ms tick — no extra call needed.
        if (_ducker != null) _ducker.DuckRatio = (float)(e.NewValue / 100.0);
    }

    private void DuckCheck_Toggled(object? sender, RoutedEventArgs e)
    {
        // The checkbox is consulted when starting; this just enables/disables
        // the slider in the UI for clarity.
        if (DuckSlider != null) DuckSlider.IsEnabled = DuckCheck.IsChecked == true;
    }

    private void RefreshCaptureBtn_Click(object? sender, RoutedEventArgs e)
    {
        RebuildCaptureSourceList(preserveSelection: true);
    }

    /// <summary>
    /// Rebuild the capture-source dropdown contents (well-known apps,
    /// audio-active processes, render-device loopbacks). When called from
    /// the Refresh button, tries to keep the user's previous selection.
    /// </summary>
    private void RebuildCaptureSourceList(bool preserveSelection)
    {
        CaptureSource? prev = null;
        if (preserveSelection
            && CaptureCombo.SelectedIndex >= 0
            && CaptureCombo.SelectedIndex < _captureSources.Count)
        {
            prev = _captureSources[CaptureCombo.SelectedIndex];
        }

        CaptureCombo.Items.Clear();
        _captureSources = new List<CaptureSource>();

        if (ProcessLoopbackCapture.IsSupported)
        {
            CaptureCombo.Items.Add("All system audio (no echo, recommended)");
            _captureSources.Add(CaptureSource.SystemAudioExceptSelf);
        }

        if (ProcessLoopbackCapture.IsSupported)
        {
            var seen = new HashSet<uint>();
            foreach (var (pid, display) in FindWellKnownAppProcesses())
            {
                CaptureCombo.Items.Add($"Only: {display}  [PID {pid}]");
                _captureSources.Add(new IncludeProcessSource(pid));
                seen.Add(pid);
            }
            foreach (var (pid, display) in EnumerateOtherAudioSessionProcesses(seen))
            {
                CaptureCombo.Items.Add($"Only: {display}  [PID {pid}]");
                _captureSources.Add(new IncludeProcessSource(pid));
            }
        }

        foreach (var d in _captureDevices)
        {
            CaptureCombo.Items.Add($"Loopback: {d.FriendlyName}");
            _captureSources.Add(new DeviceCaptureSource(d));
        }

        int restored = -1;
        if (prev != null)
        {
            for (int i = 0; i < _captureSources.Count; i++)
            {
                if (CaptureSourceMatches(prev, _captureSources[i])) { restored = i; break; }
            }
        }
        CaptureCombo.SelectedIndex = restored >= 0 ? restored : 0;
    }

    private static bool CaptureSourceMatches(CaptureSource a, CaptureSource b) => (a, b) switch
    {
        (ExcludeProcessSource ax, ExcludeProcessSource bx) => ax.Pid == bx.Pid,
        (IncludeProcessSource ax, IncludeProcessSource bx) => ax.Pid == bx.Pid,
        (DeviceCaptureSource  ax, DeviceCaptureSource  bx) => ax.Device.ID == bx.Device.ID,
        _ => false,
    };

    // ---- lifecycle -------------------------------------------------------
    private async Task StartAsync()
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            var answer = await MessageBoxManager.GetMessageBoxStandard(
                "Missing API key",
                "No API key is configured.\n\nOpen API settings now?",
                ButtonEnum.YesNo, MsbIcon.Warning).ShowWindowDialogAsync(this);
            if (answer != ButtonResult.Yes) return;

            if (!await ShowApiSettingsDialog()) return;

            ApplyAppSettings();
            if (string.IsNullOrEmpty(_apiKey)) return;
        }

        var langText = LangCombo.SelectedItem as string ?? "zh";
        var targetLang = langText.Split(' ')[0];

        if (CaptureCombo.SelectedIndex < 0 || _captureSources.Count == 0)
        {
            await MessageBoxManager.GetMessageBoxStandard(
                "Setup", "No capture source selected.",
                ButtonEnum.Ok, MsbIcon.Warning).ShowWindowDialogAsync(this);
            return;
        }
        var captureSource = _captureSources[CaptureCombo.SelectedIndex];

        // Teams/Skype redirect logic preserved from WPF version — unchanged.
        string? captureRedirectNote = null;
        List<uint>? protectedPidsToRoute = null;
        MMDevice? protectedRoutingCable = null;
        if (captureSource is IncludeProcessSource protectedCheck
            && IsLoopbackProtectedProcess(protectedCheck.Pid))
        {
            var cable = FindVirtualCableRender(_captureDevices);
            if (cable != null)
            {
                captureSource = new DeviceCaptureSource(cable);
                protectedRoutingCable = cable;
                if (AppAudioRouter.IsSupported)
                {
                    try
                    {
                        var tree = ProcessTree.EnumerateTree("ms-teams", "Teams", "Skype");
                        if (tree.Count > 0) protectedPidsToRoute = tree;
                    }
                    catch { /* P/Invoke fail — fall through; user can route manually */ }
                }
                var appLabel = LoopbackProtectedAppLabel(protectedCheck.Pid);
                int n = protectedPidsToRoute?.Count ?? 0;
                captureRedirectNote = n > 0
                    ? $"{appLabel} blocks loopback — routed {n} process(es) to {cable.FriendlyName}"
                    : $"{appLabel} blocks loopback — using {cable.FriendlyName} " +
                      $"(set {appLabel}'s output device to it manually)";
            }
        }

        MMDevice? playDevice = PlayCombo.SelectedIndex > 0
            ? _playDevices[PlayCombo.SelectedIndex - 1]
            : null;
        var url = AltUrlCheck.IsChecked == true
            ? RealtimeTranslatorClient.BuildAltUrl(_appSettings.BaseUrl)
            : RealtimeTranslatorClient.BuildDefaultUrl(_appSettings.BaseUrl);

        try
        {
            _capture = new LoopbackCapture(captureSource);

            // Per-app routing for Teams/Skype — same as WPF version.
            if (protectedPidsToRoute != null && protectedRoutingCable != null)
            {
                _appRouter ??= new AppAudioRouter();
                foreach (var pid in protectedPidsToRoute)
                {
                    try { _appRouter.Route(pid, protectedRoutingCable.ID); }
                    catch { /* per-PID failure */ }
                }
            }

            _capture.Start();

            if (MuteCheck.IsChecked != true)
            {
                _player = new AudioPlayer(playDevice);
                _translationVolume = _player.Volume;
                try { OnVolumeChanged?.Invoke(_translationVolume); } catch { /* ignore */ }
                _player.Start();
            }

            // CABLE-mode source monitor — unchanged.
            if (MuteCheck.IsChecked != true
                && MuteOtherDevicesCheck.IsChecked != true
                && captureSource is DeviceCaptureSource cableSrc
                && IsVirtualCableDevice(cableSrc.Device))
            {
                _monitorPlayer = new AudioPlayer(playDevice);
                _monitorPlayer.Start();
                _capture.Pcm24KHzAvailable += pcm =>
                {
                    var mp = _monitorPlayer;
                    if (mp == null) return;
                    var dc = _ducker;
                    float gain = (dc != null && dc.IsTranslationActive)
                        ? dc.DuckRatio : 1f;
                    mp.Push(gain >= 0.999f ? pcm : ScalePcm16(pcm, gain));
                };
            }

            if (DuckCheck.IsChecked == true)
            {
                MMDevice deviceToDuck = captureSource switch
                {
                    DeviceCaptureSource dcs => dcs.Device,
                    _ => playDevice ?? LoopbackCapture.DefaultRenderDevice(),
                };
                var ratio = (float)(DuckSlider.Value / 100.0);
                _ducker = new DuckController(new AudioDucker(deviceToDuck, ratio));
            }

            if (MuteOtherDevicesCheck.IsChecked == true)
            {
                string listenId;
                if (playDevice != null) listenId = playDevice.ID;
                else
                {
                    try { listenId = LoopbackCapture.DefaultRenderDevice().ID; }
                    catch { listenId = ""; }
                }

                MMDevice? fallback = null;
                if (!string.IsNullOrEmpty(listenId))
                {
                    fallback = _captureDevices
                        .Where(d => d.ID != listenId)
                        .OrderByDescending(ScoreFallbackDefaultDevice)
                        .FirstOrDefault(d => ScoreFallbackDefaultDevice(d) > 0);
                }

                if (fallback != null)
                {
                    try
                    {
                        using var enumerator = new MMDeviceEnumerator();
                        var currentDefault = enumerator.GetDefaultAudioEndpoint(
                            DataFlow.Render, Role.Multimedia);
                        if (currentDefault.ID == listenId)
                        {
                            _defaultDeviceSetter = new DefaultDeviceSetter();
                            _defaultDeviceSetter.SetDefault(fallback.ID);
                        }
                    }
                    catch { /* best-effort */ }
                }

                if (fallback != null && AppAudioRouter.IsSupported)
                {
                    _appRouter ??= new AppAudioRouter();
                    try
                    {
                        if (captureSource is IncludeProcessSource ips)
                        {
                            _appRouter.Route(ips.Pid, fallback.ID);
                        }
                        else
                        {
                            _appRouter.RouteAllSessionsOn(listenId, fallback.ID);
                        }
                    }
                    catch (Exception routingEx)
                    {
                        StatusText.Text = $"per-app routing failed: {routingEx.Message}";
                        Trace.WriteLine($"[AppAudioRouter] {routingEx}");
                    }
                    if (!string.IsNullOrEmpty(_appRouter.LastError))
                    {
                        Trace.WriteLine($"[AppAudioRouter partial] {_appRouter.LastError}");
                    }
                }

                _endpointMuter = new EndpointMuter();
                if (!string.IsNullOrEmpty(listenId))
                    _endpointMuter.MuteAllExcept(new[] { listenId });
            }

            _client = new RealtimeTranslatorClient(_apiKey, targetLang, _capture.Reader, url)
            {
                EchoSuppressionEnabled = EchoSuppressCheck.IsChecked == true,
            };
            _client.SourceTranscript += s =>
            {
                OnUi(() => Append(SourceText, s));
                try { OnSourceDelta?.Invoke(s); } catch { /* ignore handler errors */ }
            };
            _client.TranslatedTranscript += s =>
            {
                OnUi(() => Append(TargetText, s));
                try { OnTranslatedDelta?.Invoke(s); } catch { /* ignore handler errors */ }
            };
            _client.AudioOut             += b =>
            {
                _player?.Push(b);
                _ducker?.Notify(b.Length);
            };
            _client.Status               += s => OnUi(() => StatusText.Text = s);
            _client.Error                += s => OnUi(() =>
            {
                StatusText.Text = $"error: {Truncate(s, 120)}";
                Append(TargetText, $"\n[error] {s}\n");
            });
            _client.Start();

            _running = true;
            StartBtn.Content = "Stop";
            LangCombo.IsEnabled = CaptureCombo.IsEnabled = PlayCombo.IsEnabled = false;
            MuteCheck.IsEnabled = AltUrlCheck.IsEnabled = DuckCheck.IsEnabled =
                EchoSuppressCheck.IsEnabled = MuteOtherDevicesCheck.IsEnabled = false;
            var statusSuffix = _client.LogPath != null ? $"   log: {_client.LogPath}" : "";
            StatusText.Text = captureRedirectNote != null
                ? $"running → {targetLang}   ⚠ {captureRedirectNote}"
                : $"running → {targetLang}{statusSuffix}";
            try { OnRunningChanged?.Invoke(); } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            await MessageBoxManager.GetMessageBoxStandard(
                "Start failed",
                $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                ButtonEnum.Ok, MsbIcon.Error).ShowWindowDialogAsync(this);
            await CleanupAsync();
        }
    }

    private async Task StopAsync()
    {
        await CleanupAsync();
        _running = false;
        StartBtn.Content = "Start";
        LangCombo.IsEnabled = CaptureCombo.IsEnabled = PlayCombo.IsEnabled = true;
        MuteCheck.IsEnabled = AltUrlCheck.IsEnabled = DuckCheck.IsEnabled =
            EchoSuppressCheck.IsEnabled = MuteOtherDevicesCheck.IsEnabled = true;
        StatusText.Text = "stopped";
        try { OnRunningChanged?.Invoke(); } catch { /* ignore */ }
    }

    private async Task CleanupAsync()
    {
        if (_client != null)
        {
            try { await _client.StopAsync(); } catch { /* ignore */ }
            try { _client.Dispose(); } catch { /* ignore */ }
            _client = null;
        }
        if (_capture != null)
        {
            try { _capture.Stop(); } catch { /* ignore */ }
            try { _capture.Dispose(); } catch { /* ignore */ }
            _capture = null;
        }
        if (_player != null)
        {
            try { _player.Dispose(); } catch { /* ignore */ }
            _player = null;
        }
        if (_monitorPlayer != null)
        {
            try { _monitorPlayer.Dispose(); } catch { /* ignore */ }
            _monitorPlayer = null;
        }
        if (_ducker != null)
        {
            try { _ducker.Dispose(); } catch { /* ignore */ }
            _ducker = null;
        }
        if (_appRouter != null)
        {
            try { _appRouter.Dispose(); } catch { /* ignore */ }
            _appRouter = null;
        }
        if (_endpointMuter != null)
        {
            try { _endpointMuter.Dispose(); } catch { /* ignore */ }
            _endpointMuter = null;
        }
        if (_defaultDeviceSetter != null)
        {
            try { _defaultDeviceSetter.Dispose(); } catch { /* ignore */ }
            _defaultDeviceSetter = null;
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        await CleanupAsync();
        base.OnClosed(e);
    }

    // ---- ui helpers ------------------------------------------------------
    private void OnUi(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }

    private static void Append(TextBox tb, string text)
    {
        // Avalonia TextBox has no AppendText; concat + reset CaretIndex
        // implicitly scrolls the caret into view.
        tb.Text = (tb.Text ?? string.Empty) + text;
        tb.CaretIndex = tb.Text!.Length;
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";

    // ---- title bar drag --------------------------------------------------
    // Avalonia's WindowChrome equivalent: ExtendClientArea hints on the
    // Window. Drag-to-move is implemented by calling BeginMoveDrag on a
    // PointerPressed in the title-bar grid. Caption buttons handle their
    // own clicks (e.Handled=true via Click event semantics) so they don't
    // start a drag.
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void MinBtn_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseBtn_Click(object? sender, RoutedEventArgs e)
        => Close();

    // ---- device picking helpers (unchanged from WPF) ---------------------

    // PKEY_AudioEndpoint_FormFactor — tells us if the endpoint is Speakers,
    // Headphones, Headset, etc. Defined in mmdeviceapi.h but not exposed as
    // a constant by NAudio.
    private static readonly PropertyKey PKEY_AudioEndpoint_FormFactor =
        new(new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), 0);

    /// <summary>
    /// Apply a linear gain to a PCM16 little-endian mono buffer, returning a
    /// new buffer with the scaled samples (clamped to short range). Used for
    /// the CABLE-mode source monitor's ducking.
    /// </summary>
    private static byte[] ScalePcm16(byte[] input, float gain)
    {
        var output = new byte[input.Length];
        for (int i = 0; i + 1 < input.Length; i += 2)
        {
            short sample = (short)(input[i] | (input[i + 1] << 8));
            int scaled = (int)(sample * gain);
            if (scaled > 32767) scaled = 32767;
            else if (scaled < -32768) scaled = -32768;
            output[i]     = (byte)(scaled & 0xFF);
            output[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }
        return output;
    }

    private static bool IsVirtualCableDevice(MMDevice d)
    {
        var name = (d.FriendlyName ?? string.Empty).ToLowerInvariant();
        return name.Contains("cable") || name.Contains("vb-audio")
            || name.Contains("voicemeeter") || name.Contains("virtual");
    }

    private static bool IsLoopbackProtectedProcess(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var name = (p.ProcessName ?? string.Empty).ToLowerInvariant();
            return name == "ms-teams" || name == "teams" || name == "skype";
        }
        catch { return false; }
    }

    private static string LoopbackProtectedAppLabel(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var name = (p.ProcessName ?? string.Empty).ToLowerInvariant();
            return name switch
            {
                "ms-teams" or "teams" => "Teams",
                "skype" => "Skype",
                _ => "This app",
            };
        }
        catch { return "This app"; }
    }

    private static MMDevice? FindVirtualCableRender(IEnumerable<MMDevice> renderDevices)
    {
        MMDevice? altCable = null;
        MMDevice? genericVirtual = null;
        foreach (var d in renderDevices)
        {
            var name = (d.FriendlyName ?? string.Empty).ToLowerInvariant();
            if (name.Contains("16ch") || name.Contains("32ch") || name.Contains("64ch"))
                continue;
            if (name.StartsWith("cable input"))
                return d;
            if (altCable == null && (name.Contains("cable") || name.Contains("vb-audio")))
                altCable = d;
            if (genericVirtual == null && (name.Contains("voicemeeter") || name.Contains("virtual")))
                genericVirtual = d;
        }
        return altCable ?? genericVirtual;
    }

    private static int ScorePlaybackCandidate(MMDevice d, string? captureId)
    {
        var name = (d.FriendlyName ?? string.Empty).ToLowerInvariant();

        if (IsVirtualCableDevice(d)) return 0;
        if (captureId != null && d.ID == captureId) return 0;

        int score = 1;

        switch (GetFormFactor(d))
        {
            case 3: score += 100; break; // Headphones
            case 5: score += 100; break; // Headset
            case 1: score += 20;  break; // Speakers
        }

        if (name.Contains("headphone") || name.Contains("headset")
            || name.Contains("earphone") || name.Contains("earbud"))
            score += 80;
        if (name.Contains("bluetooth") || name.Contains("airpod")
            || name.Contains("beats") || name.Contains("buds")
            || name.Contains("hands-free") || name.Contains("a2dp")
            || name.Contains("wh-") || name.Contains("wf-"))
            score += 60;

        if (name.Contains("hdmi") || name.Contains("displayport")
            || name.Contains("display"))
            score -= 30;

        return score;
    }

    private static int GetFormFactor(MMDevice d)
    {
        try
        {
            var v = d.Properties[PKEY_AudioEndpoint_FormFactor];
            return v.Value is int i ? i : Convert.ToInt32(v.Value);
        }
        catch { return -1; }
    }

    private static int ScoreFallbackDefaultDevice(MMDevice d)
    {
        var name = (d.FriendlyName ?? string.Empty).ToLowerInvariant();
        if (name.Contains("cable") || name.Contains("vb-audio")
            || name.Contains("voicemeeter") || name.Contains("virtual"))
            return 0;
        int score = 1;
        switch (GetFormFactor(d))
        {
            case 1: score += 50; break;
        }
        if (name.Contains("onboard") || name.Contains("realtek")
            || name.Contains("speaker") || name.Contains("internal"))
            score += 30;
        return score;
    }

    private static readonly (string ProcessName, string Display)[] WellKnownApps =
    {
        ("ms-teams",   "Microsoft Teams"),
        ("Teams",      "Microsoft Teams (classic)"),
        ("Zoom",       "Zoom"),
        ("chrome",     "Google Chrome"),
        ("msedge",     "Microsoft Edge"),
        ("firefox",    "Firefox"),
        ("brave",      "Brave"),
        ("Spotify",    "Spotify"),
        ("Discord",    "Discord"),
        ("Slack",      "Slack"),
        ("vlc",        "VLC"),
        ("obs64",      "OBS"),
        ("WhatsApp",   "WhatsApp"),
    };

    private static List<(uint Pid, string Display)> EnumerateOtherAudioSessionProcesses(
        HashSet<uint> exclude)
    {
        var result = new Dictionary<uint, string>();
        var ownPid = (uint)Environment.ProcessId;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var d in devices)
            {
                try
                {
                    var sessions = d.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        try
                        {
                            var s = sessions[i];
                            uint pid = (uint)s.GetProcessID;
                            if (pid == 0 || pid == ownPid) continue;
                            if (exclude.Contains(pid) || result.ContainsKey(pid)) continue;
                            result[pid] = MakeProcessLabel(pid);
                        }
                        catch { /* session went away */ }
                    }
                }
                catch { /* device unavailable */ }
            }
        }
        catch { /* enumerator failed */ }
        return result.Where(kv => !string.IsNullOrEmpty(kv.Value))
                     .Select(kv => (kv.Key, kv.Value))
                     .OrderBy(t => t.Value, StringComparer.OrdinalIgnoreCase)
                     .ToList();
    }

    private static string MakeProcessLabel(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string name = p.ProcessName;
            string title = "";
            try { title = p.MainWindowTitle?.Trim() ?? ""; } catch { }
            if (string.IsNullOrEmpty(title)) return name;
            if (title.Length > 50) title = title[..50] + "…";
            return $"{name} — {title}";
        }
        catch { return ""; }
    }

    private static List<(uint Pid, string Display)> FindWellKnownAppProcesses()
    {
        var result = new List<(uint, string)>();
        var ownPid = Environment.ProcessId;

        foreach (var (procName, display) in WellKnownApps)
        {
            Process[]? procs = null;
            try
            {
                procs = Process.GetProcessesByName(procName);

                Process? main = null;
                foreach (var p in procs)
                {
                    try
                    {
                        if (p.Id == ownPid) continue;
                        if (string.IsNullOrEmpty(p.MainWindowTitle)) continue;
                        if (main == null || p.Id < main.Id) main = p;
                    }
                    catch { /* skip inaccessible */ }
                }
                if (main == null)
                {
                    foreach (var p in procs)
                    {
                        try
                        {
                            if (p.Id == ownPid) continue;
                            if (main == null || p.Id < main.Id) main = p;
                        }
                        catch { }
                    }
                }
                if (main == null) continue;

                string label = display;
                try
                {
                    var title = main.MainWindowTitle?.Trim() ?? "";
                    if (title.Length > 0)
                    {
                        if (title.Length > 50) title = title[..50] + "…";
                        label = $"{display} — {title}";
                    }
                }
                catch { /* MainWindowTitle can throw on protected processes */ }

                result.Add(((uint)main.Id, label));
            }
            catch { /* process enumeration failed */ }
            finally
            {
                if (procs != null)
                {
                    foreach (var p in procs)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
        }
        return result;
    }
}
