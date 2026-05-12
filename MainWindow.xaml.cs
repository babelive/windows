using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using NAudio.CoreAudioApi;
using Babelive.Audio;
using Babelive.Translation;
using MessageBox = System.Windows.MessageBox;

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
    // Loaded once at startup and re-applied whenever the API settings
    // dialog saves. ApiKey here, when non-empty, takes priority over the
    // .env / OPENAI_API_KEY env var fallback that LoadEnv() establishes.
    private AppSettings _appSettings = new();

    public MainWindow()
    {
        InitializeComponent();
        ApplyAppSettings();
        PopulateLanguages();
        PopulateDevices();
    }

    /// <summary>
    /// Sole source of <see cref="_apiKey"/> — read from
    /// <see cref="AppSettings"/> on disk. Called at startup and after the
    /// API settings dialog saves. The dialog is the only entry point for
    /// the API key (no env var / .env file fallback), so users have one
    /// place to look and no chance of an accidental key leak through
    /// environment variables or stray files.
    /// </summary>
    private void ApplyAppSettings()
    {
        _appSettings = AppSettings.Load();
        _apiKey = _appSettings.ApiKey?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(_apiKey))
            StatusText.Text = "no API key — click API… to set one";
    }

    private void ApiBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ShowApiSettingsDialog() == true)
        {
            // Re-merge: dialog already saved to disk and mutated
            // _appSettings; refresh the in-process api key from it.
            ApplyAppSettings();
            // Don't clobber a "running" status with the no-key warning.
            if (!_running && !string.IsNullOrEmpty(_apiKey))
                StatusText.Text = "idle";
        }
    }

    /// <summary>
    /// Open the API settings dialog modally. Owner can ONLY be set on a
    /// window whose HWND has been realized (i.e. that has been Show()n at
    /// least once). MainWindow is hidden at startup and only gets shown
    /// when the user clicks the tray icon or its "Settings…" menu item —
    /// so when the dialog is reached via lyric-overlay → Start → "no API
    /// key" → Yes, MainWindow's HWND doesn't exist yet and assigning
    /// Owner throws InvalidOperationException. Fall back to centering on
    /// screen in that case.
    /// </summary>
    private bool? ShowApiSettingsDialog()
    {
        var dlg = new ApiSettingsWindow(_appSettings);
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
            dlg.Owner = this;
        else
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog();
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

    private void RefreshPlaybackBtn_Click(object sender, RoutedEventArgs e)
    {
        RebuildPlaybackList(preserveSelection: true);
    }

    /// <summary>
    /// Rebuild the playback dropdown by re-enumerating active render
    /// endpoints. Useful after Bluetooth (re)connects or USB headphones
    /// are plugged in / out. Preserves the previous selection by device ID
    /// when possible; otherwise falls back to the smart-default scoring.
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

        // Try to preserve the user's selection by device ID
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
            // Smart default: prefer headphones / Bluetooth, then real speakers
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
    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_running) await StopAsync();
        else await StartAsync();
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        SourceText.Clear();
        TargetText.Clear();
    }

    private void DuckSlider_ValueChanged(object sender,
        System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        // Live-update if a session is currently active. The monitor reads
        // _ducker.DuckRatio per PCM chunk, so its gain updates on the next
        // ~10 ms tick — no extra call needed.
        if (_ducker != null) _ducker.DuckRatio = (float)(e.NewValue / 100.0);
    }

    private void DuckCheck_Toggled(object sender, RoutedEventArgs e)
    {
        // The checkbox is consulted when starting; this just enables/disables
        // the slider in the UI for clarity.
        if (DuckSlider != null) DuckSlider.IsEnabled = DuckCheck.IsChecked == true;
    }

    private void RefreshCaptureBtn_Click(object sender, RoutedEventArgs e)
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
        // Snapshot the current selection so we can re-pick it post-rebuild.
        CaptureSource? prev = null;
        if (preserveSelection
            && CaptureCombo.SelectedIndex >= 0
            && CaptureCombo.SelectedIndex < _captureSources.Count)
        {
            prev = _captureSources[CaptureCombo.SelectedIndex];
        }

        CaptureCombo.Items.Clear();
        _captureSources = new List<CaptureSource>();

        // (1) Process-loopback EXCLUDE on our own PID — captures everything
        //     except our own translation playback. No echo even when the
        //     same physical device is used for capture and playback.
        if (ProcessLoopbackCapture.IsSupported)
        {
            CaptureCombo.Items.Add("All system audio (no echo, recommended)");
            _captureSources.Add(CaptureSource.SystemAudioExceptSelf);
        }

        // (2) Process-loopback INCLUDE entries: well-known apps that are
        //     currently running, plus any other process with an active
        //     audio session on a render endpoint.
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

        // (3) Legacy device loopbacks (one per active render endpoint).
        foreach (var d in _captureDevices)
        {
            CaptureCombo.Items.Add($"Loopback: {d.FriendlyName}");
            _captureSources.Add(new DeviceCaptureSource(d));
        }

        // Restore previous selection if it still exists, else default to 0.
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
            // Offer to open the API settings dialog inline so the user
            // doesn't have to dismiss this dialog, hunt for the API…
            // button, set the key, and click Start a third time.
            var answer = MessageBox.Show(
                "No API key is configured.\n\nOpen API settings now?",
                "Missing API key",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            if (ShowApiSettingsDialog() != true) return;

            ApplyAppSettings();
            if (string.IsNullOrEmpty(_apiKey)) return;
            // fall through and continue with Start
        }

        var langText = LangCombo.SelectedItem as string ?? "zh";
        var targetLang = langText.Split(' ')[0];

        if (CaptureCombo.SelectedIndex < 0 || _captureSources.Count == 0)
        {
            MessageBox.Show("No capture source selected.", "Setup",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var captureSource = _captureSources[CaptureCombo.SelectedIndex];

        // Teams hard-blocks process loopback via AUDCLNT_STREAMFLAGS_PREVENT_
        // LOOPBACK_CAPTURE — picking "Only: Microsoft Teams" gives silence.
        // If the user has a virtual cable installed (the standard workaround),
        // silently redirect to that device's loopback instead. We assume Teams
        // has been pointed at the cable in its audio settings; if not the
        // loopback will be quiet, but the status hint below explains why.
        string? captureRedirectNote = null;
        // PIDs we want to push to CABLE Input via per-app routing once
        // _appRouter is constructed (done a bit lower in this method).
        // Populated only in the loopback-protected-app → CABLE redirect path.
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
                // For new Teams: audio comes from msedgewebview2 children of
                // ms-teams.exe. For classic Teams / consumer Skype: audio
                // comes from renderer subprocesses sharing the same exe name.
                // Per-app routing is keyed on PID and doesn't inherit, so we
                // collect the whole tree from every well-known root name.
                // EnumerateTree's BFS keeps msedgewebview2 belonging to
                // Outlook / Edge OUT of this set (they're not Teams children).
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

            // Per-app route the loopback-protected app's PID tree to CABLE
            // Input BEFORE starting capture, so Win11 22H2+'s live session
            // migration completes (typically <100 ms) before our loopback
            // sees a single silent tick. On older Windows the routing is
            // persistent but only affects sessions opened AFTER it's set —
            // user may need to rejoin the call once.
            if (protectedPidsToRoute != null && protectedRoutingCable != null)
            {
                _appRouter ??= new AppAudioRouter();
                foreach (var pid in protectedPidsToRoute)
                {
                    try { _appRouter.Route(pid, protectedRoutingCable.ID); }
                    catch { /* per-PID failure; logged in LastError if any */ }
                }
            }

            _capture.Start();

            if (MuteCheck.IsChecked != true)
            {
                _player = new AudioPlayer(playDevice);
                // Don't FORCE the playback session to _translationVolume on
                // Start — that was clobbering whatever the user (or Windows)
                // had set for Babelive's session volume, jumping it to
                // 100% every Start. Instead, READ the current session volume
                // into our tracker so the lyric-overlay 🔉/🔊 buttons step
                // relative to the real value (Windows persists session
                // volume per-app via SimpleAudioVolume across runs).
                _translationVolume = _player.Volume;
                try { OnVolumeChanged?.Invoke(_translationVolume); } catch { /* ignore */ }
                _player.Start();
            }

            // CABLE-mode source monitor: when capture is a virtual cable
            // (VB-CABLE / VoiceMeeter / generic "virtual" device), the source
            // audio was routed INTO the cable to bypass Teams/DRM
            // PREVENT_LOOPBACK_CAPTURE protection — but that means the cable
            // is the only "speaker" the source app sees, and the user's real
            // speakers never get the original. Tee the captured PCM through
            // the Playback device so they hear the source alongside the
            // translation. Skipped when:
            //   - "Transcript only" → user wants no audio playback at all
            //   - "Mute other speakers" → user wants translation-only
            //   - capture is not a virtual cable → original audio already
            //     reaches physical speakers through normal Windows routing
            if (MuteCheck.IsChecked != true
                && MuteOtherDevicesCheck.IsChecked != true
                && captureSource is DeviceCaptureSource cableSrc
                && IsVirtualCableDevice(cableSrc.Device))
            {
                _monitorPlayer = new AudioPlayer(playDevice);
                _monitorPlayer.Start();
                // CRITICAL: don't ever set _monitorPlayer.Volume directly.
                // NAudio's WasapiOut.Volume writes the session-level
                // SimpleAudioVolume, and _monitorPlayer shares a Windows
                // audio session with _player (same PID + same device), so
                // touching it would also duck the translation. Instead, we
                // scale PCM samples in-flight using the current duck state;
                // _monitorPlayer.Volume stays at its session default.
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

            // Optional ducking: lower other apps' session volumes on the
            // device the user is actually listening on. This is purely about
            // what the user HEARS (preventing source audio from talking over
            // the translation playback), not about what the model captures.
            // For device-loopback we duck the loopback'd device. For
            // process-loopback we don't have a specific source device, so
            // duck whichever device the source apps render to (= playback
            // device if specified, else system default render).
            if (DuckCheck.IsChecked == true)
            {
                MMDevice deviceToDuck = captureSource switch
                {
                    DeviceCaptureSource dcs => dcs.Device,
                    _ => playDevice ?? LoopbackCapture.DefaultRenderDevice(),
                };
                var ratio = (float)(DuckSlider.Value / 100.0);
                _ducker = new DuckController(new AudioDucker(deviceToDuck, ratio));
                // The CABLE-mode source monitor reads _ducker.IsTranslationActive
                // and _ducker.DuckRatio on every PCM chunk and scales samples
                // accordingly — no event subscription needed, slider changes
                // pick up automatically on the next ~10 ms chunk.
            }

            // Optional "Mute other speakers": for translation-only listening.
            //
            // Three coordinated steps:
            //  (1) If the current Windows default render device IS our
            //      Playback device (typical: BT headphones became default
            //      when connected), switch the system default to a sensible
            //      non-Playback device (prefer Onboard Speaker) via
            //      IPolicyConfigVista. This affects NEW audio sessions only.
            //  (2) Per-app routing: forcibly redirect existing audio sessions
            //      already living on the Playback device to the fallback
            //      device, via the WinRT IAudioPolicyConfig API. This is
            //      what handles the sticky-binding case where Chrome was
            //      already on the BT headphones before we started — Win11
            //      22H2+ live-migrates the session.
            //  (3) Mute the master endpoint of every render device EXCEPT
            //      Playback. WASAPI loopback / Process Loopback tap the
            //      audio engine BEFORE endpoint mute is applied at the
            //      driver, so the API still receives full source audio while
            //      the physical speakers stay silent.
            //
            // All three are reverted on Stop.
            if (MuteOtherDevicesCheck.IsChecked == true)
            {
                string listenId;
                if (playDevice != null) listenId = playDevice.ID;
                else
                {
                    try { listenId = LoopbackCapture.DefaultRenderDevice().ID; }
                    catch { listenId = ""; }
                }

                // Pick the fallback device once — used for both the system
                // default switch (1) and the per-app routing target (2).
                MMDevice? fallback = null;
                if (!string.IsNullOrEmpty(listenId))
                {
                    fallback = _captureDevices
                        .Where(d => d.ID != listenId)
                        .OrderByDescending(ScoreFallbackDefaultDevice)
                        .FirstOrDefault(d => ScoreFallbackDefaultDevice(d) > 0);
                }

                // Step (1): switch system default if it's the listening device
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

                // Step (2): per-app routing for sessions already on listenId
                if (fallback != null && AppAudioRouter.IsSupported)
                {
                    // May already exist if Teams→CABLE routing was set up
                    // above; reuse so we don't clobber its tracked PIDs.
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
                        // Hard failure: routing didn't run at all. Surface in
                        // the status line so the user knows mute-other-
                        // speakers may be partial, but don't block them with
                        // a modal dialog.
                        StatusText.Text = $"per-app routing failed: {routingEx.Message}";
                        Trace.WriteLine($"[AppAudioRouter] {routingEx}");
                    }
                    if (!string.IsNullOrEmpty(_appRouter.LastError))
                    {
                        // Soft / partial failure: most sessions re-routed but
                        // a couple resisted (system processes, dead PIDs,
                        // DRM-protected sessions, races between enumeration
                        // and routing, …). Translation still works fine, so
                        // log quietly instead of blocking every Start with a
                        // modal popup.
                        Trace.WriteLine($"[AppAudioRouter partial] {_appRouter.LastError}");
                    }
                }

                // Step (3): mute every other render endpoint
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
            MessageBox.Show($"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                            "Start failed",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            await CleanupAsync();
        }
        await Task.CompletedTask;
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
            // _capture is already disposed above, so no further events fire;
            // safe to drop the subscriber reference along with the player.
            try { _monitorPlayer.Dispose(); } catch { /* ignore */ }
            _monitorPlayer = null;
        }
        if (_ducker != null)
        {
            // Restores volumes via Dispose -> AudioDucker.Restore
            try { _ducker.Dispose(); } catch { /* ignore */ }
            _ducker = null;
        }
        // Order: clear per-app routing FIRST so apps re-evaluate against the
        // about-to-be-restored system default; then unmute endpoints; then
        // restore the system default.
        if (_appRouter != null)
        {
            try { _appRouter.Dispose(); } catch { /* ignore */ }
            _appRouter = null;
        }
        if (_endpointMuter != null)
        {
            // Restores per-device mute via Dispose -> EndpointMuter.Restore
            try { _endpointMuter.Dispose(); } catch { /* ignore */ }
            _endpointMuter = null;
        }
        if (_defaultDeviceSetter != null)
        {
            // Restores prior Windows default render device
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
        if (Dispatcher.CheckAccess()) a();
        else Dispatcher.BeginInvoke(a);
    }

    private static void Append(System.Windows.Controls.TextBox tb, string text)
    {
        tb.AppendText(text);
        tb.ScrollToEnd();
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";

    // PKEY_AudioEndpoint_FormFactor — tells us if the endpoint is Speakers,
    // Headphones, Headset, etc. Defined in mmdeviceapi.h but not exposed as
    // a constant by NAudio.
    private static readonly PropertyKey PKEY_AudioEndpoint_FormFactor =
        new(new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), 0);

    /// <summary>
    /// Apply a linear gain to a PCM16 little-endian mono buffer, returning a
    /// new buffer with the scaled samples (clamped to short range). Used for
    /// the CABLE-mode source monitor's ducking: scaling at the sample level
    /// keeps the operation OUT of <see cref="AudioPlayer.Volume"/> (which is
    /// session-shared with the translation player and would duck it too).
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

    /// <summary>
    /// Heuristic: does this device look like a virtual audio cable
    /// (VB-CABLE, VoiceMeeter, generic "virtual …")?  Used both to skip them
    /// when auto-picking a Playback device and to detect CABLE-mode capture
    /// (so we can tee the source through the real Playback device).
    /// </summary>
    private static bool IsVirtualCableDevice(MMDevice d)
    {
        var name = (d.FriendlyName ?? string.Empty).ToLowerInvariant();
        return name.Contains("cable") || name.Contains("vb-audio")
            || name.Contains("voicemeeter") || name.Contains("virtual");
    }

    /// <summary>
    /// Returns true if the given PID belongs to a Microsoft calling app that
    /// sets <c>AUDCLNT_STREAMFLAGS_PREVENT_LOOPBACK_CAPTURE</c> on its render
    /// stream — Windows' Process Loopback API returns silence for these for
    /// privacy reasons, so we redirect to a virtual-cable loopback instead.
    /// Currently matches:
    /// <list type="bullet">
    ///   <item>new Teams (<c>ms-teams.exe</c>)</item>
    ///   <item>classic Teams (<c>Teams.exe</c>)</item>
    ///   <item>Skype consumer (<c>Skype.exe</c>) — same call stack lineage</item>
    /// </list>
    /// Zoom / Discord / Meet / Webex / Slack do NOT need this — their WebRTC
    /// stacks don't set the flag and Process Loopback works directly.
    /// </summary>
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

    /// <summary>
    /// Human-readable label for the app behind <paramref name="pid"/>, used
    /// only in the status-bar redirect note so the user knows which app
    /// triggered the auto-route.
    /// </summary>
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

    /// <summary>
    /// Find a virtual-cable RENDER endpoint to use as a loopback source.
    /// Priority:
    ///   1. Standard 2-channel <c>CABLE Input (VB-Audio Virtual Cable)</c>
    ///      — what Teams / Chrome / most stereo apps default to. Perfect
    ///      format match, no resampling surprises.
    ///   2. Any other VB-Audio cable variant (Hi-Fi Cable, CABLE-A/B/C/D
    ///      from the donationware pack).
    ///   3. VoiceMeeter / generic "virtual" devices.
    /// Multi-channel ASIO-bridge variants (<c>… 16ch</c>, <c>… 32ch</c>,
    /// <c>… 64ch</c>) are SKIPPED — they cause format-mismatch / silent-
    /// channel issues when a 2-channel app (Teams) is routed through them.
    /// </summary>
    private static MMDevice? FindVirtualCableRender(IEnumerable<MMDevice> renderDevices)
    {
        MMDevice? altCable = null;
        MMDevice? genericVirtual = null;
        foreach (var d in renderDevices)
        {
            var name = (d.FriendlyName ?? string.Empty).ToLowerInvariant();

            // Skip pro-audio multi-channel variants — wrong format for
            // stereo apps and they'll silently degrade.
            if (name.Contains("16ch") || name.Contains("32ch") || name.Contains("64ch"))
                continue;

            // Tier 1: short-circuit on the canonical 2-ch CABLE Input.
            if (name.StartsWith("cable input"))
                return d;

            if (altCable == null && (name.Contains("cable") || name.Contains("vb-audio")))
                altCable = d;
            if (genericVirtual == null && (name.Contains("voicemeeter") || name.Contains("virtual")))
                genericVirtual = d;
        }
        return altCable ?? genericVirtual;
    }

    /// <summary>
    /// Score a playback candidate. Higher = better. Returns 0 (skip) for
    /// devices we never want to auto-pick (virtual cables, the capture
    /// device itself).
    /// </summary>
    private static int ScorePlaybackCandidate(MMDevice d, string? captureId)
    {
        var name = (d.FriendlyName ?? string.Empty).ToLowerInvariant();

        // Never auto-pick virtual cables for playback (they have no physical
        // output) and never pick the same device we're capturing from.
        if (IsVirtualCableDevice(d))
            return 0;
        if (captureId != null && d.ID == captureId)
            return 0;

        int score = 1; // baseline: any real device beats system default

        // Form-factor hints (most reliable)
        switch (GetFormFactor(d))
        {
            case 3: score += 100; break; // Headphones
            case 5: score += 100; break; // Headset
            case 1: score += 20;  break; // Speakers
        }

        // Name keyword fallback (catches BT devices that report as Speakers)
        if (name.Contains("headphone") || name.Contains("headset")
            || name.Contains("earphone") || name.Contains("earbud"))
            score += 80;
        if (name.Contains("bluetooth") || name.Contains("airpod")
            || name.Contains("beats") || name.Contains("buds")
            || name.Contains("hands-free") || name.Contains("a2dp")
            || name.Contains("wh-") || name.Contains("wf-"))
            score += 60;

        // De-prioritise HDMI / DisplayPort outputs (usually external monitors)
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

    /// <summary>
    /// Score for "good fallback default device when redirecting source apps
    /// away from the user's listening device". Prefer Onboard / built-in
    /// speakers; reject virtual cables (no physical output → user wouldn't
    /// be able to confirm audio is working).
    /// </summary>
    private static int ScoreFallbackDefaultDevice(MMDevice d)
    {
        var name = (d.FriendlyName ?? string.Empty).ToLowerInvariant();
        if (name.Contains("cable") || name.Contains("vb-audio")
            || name.Contains("voicemeeter") || name.Contains("virtual"))
            return 0;
        int score = 1;
        switch (GetFormFactor(d))
        {
            case 1: score += 50; break;  // Speakers
        }
        if (name.Contains("onboard") || name.Contains("realtek")
            || name.Contains("speaker") || name.Contains("internal"))
            score += 30;
        return score;
    }

    // Common audio-producing apps. Process names are case-insensitive on
    // Windows; .ProcessName drops the .exe suffix.
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

    /// <summary>
    /// Enumerates every render-device audio session and returns a friendly
    /// label per non-self, non-already-added process. Catches anything that
    /// is currently producing sound but isn't in our well-known list.
    /// </summary>
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
        // Filter out empty labels (process exited between enumeration & lookup)
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

    /// <summary>
    /// Finds the "main" process for each well-known audio-producing app that
    /// is currently running. Returns one PID per app (the one with a visible
    /// main window — the user-facing process). Process Loopback INCLUDE mode
    /// captures the target PID + its descendants, so passing the parent
    /// browser PID covers all renderer/audio child processes.
    /// </summary>
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

                // Prefer an instance with a visible main window — that's
                // the user-facing process for browsers / Teams / Zoom etc.
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
                // Fallback: any instance (some apps render audio without a window)
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

    // ---- Custom title-bar buttons --------------------------------------
    // Replace the system caption controls (which are gone because we use
    // WindowStyle=None + AllowsTransparency=True for the rounded dark
    // chrome). Close just hides — the lyric overlay + tray icon stay
    // running; the window's own Closing handler in App startup forces
    // Hide() instead of true Close() so we don't tear down audio state.
    private void MinBtn_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
        => Close();
}
