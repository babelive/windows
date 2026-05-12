using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Babelive.Audio;

/// <summary>
/// Lowers ("ducks") the per-session volume of every app on the chosen device
/// except our own process, and restores them on demand.
///
/// COM threading note: MMDevice instances are bound to the apartment that
/// created them (creation thread). Since Duck()/Restore() run on the
/// websocket receive thread or a Timer thread, we cannot reuse the MMDevice
/// captured on the WPF UI (STA) thread — we'd hit E_NOINTERFACE. Instead we
/// store only the device ID and re-resolve via MMDeviceEnumerator on whatever
/// thread is calling. Cheap (~ms) and only happens on duck/restore boundaries
/// (not per audio frame).
/// </summary>
public sealed class AudioDucker : IDisposable
{
    private readonly string _deviceId;
    private readonly int _ownProcessId;
    private float _duckRatio;
    private readonly Dictionary<int, float> _saved = new();
    private readonly object _lock = new();
    private bool _isDucked;
    private bool _disposed;

    public AudioDucker(MMDevice device, float duckRatio = 0.25f)
    {
        _deviceId = device.ID;
        _ownProcessId = Process.GetCurrentProcess().Id;
        _duckRatio = Math.Clamp(duckRatio, 0f, 1f);
    }

    /// <summary>0..1 — fraction of original volume kept while ducked. Live-updates.</summary>
    public float DuckRatio
    {
        get => _duckRatio;
        set
        {
            _duckRatio = Math.Clamp(value, 0f, 1f);
            if (_isDucked) ApplyDuckedVolumes();
        }
    }

    public void Duck()
    {
        lock (_lock)
        {
            if (_isDucked || _disposed) return;
            _saved.Clear();
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(_deviceId);
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    try
                    {
                        int pid = (int)s.GetProcessID;
                        if (pid == _ownProcessId || pid == 0) continue;
                        if (s.State == AudioSessionState.AudioSessionStateExpired) continue;
                        var current = s.SimpleAudioVolume.Volume;
                        _saved[pid] = current;
                        s.SimpleAudioVolume.Volume = current * _duckRatio;
                    }
                    catch { /* session died between enumeration and access */ }
                }
                _isDucked = true;
            }
            catch (Exception)
            {
                // COM hiccup or device disappeared — bail this round; the
                // controller will retry on the next translated-audio frame.
            }
        }
    }

    public void Restore()
    {
        lock (_lock)
        {
            if (!_isDucked || _disposed) return;
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(_deviceId);
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    try
                    {
                        int pid = (int)s.GetProcessID;
                        if (_saved.TryGetValue(pid, out var prev))
                            s.SimpleAudioVolume.Volume = prev;
                    }
                    catch { /* ignore */ }
                }
            }
            catch (Exception) { /* swallow */ }
            _saved.Clear();
            _isDucked = false;
        }
    }

    /// <summary>Re-apply the duck ratio to the currently saved set (for live slider changes).</summary>
    private void ApplyDuckedVolumes()
    {
        lock (_lock)
        {
            if (!_isDucked) return;
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(_deviceId);
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    try
                    {
                        int pid = (int)s.GetProcessID;
                        if (_saved.TryGetValue(pid, out var orig))
                            s.SimpleAudioVolume.Volume = orig * _duckRatio;
                    }
                    catch { /* ignore */ }
                }
            }
            catch (Exception) { /* swallow */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Restore(); } catch { /* best-effort */ }
        _disposed = true;
    }
}

/// <summary>
/// Wraps an AudioDucker with a "translation playing" detector based on the
/// PROJECTED end-of-playback time, not when chunks were received.
///
/// The server bursts translation chunks much faster than they play. If we
/// based the duck/restore on receive time, the duck would restore in the
/// middle of playback (because chunks stop arriving long before playback
/// finishes). The user would hear source audio jump back to full volume
/// while the translation is still being spoken. Tracking projected playback
/// end fixes that.
/// </summary>
public sealed class DuckController : IDisposable
{
    private readonly AudioDucker _ducker;
    private readonly System.Threading.Timer _timer;
    private readonly TimeSpan _silenceTimeout;
    private long _playbackEndTicks; // 0 = idle
    private int _translationActive; // 0 = idle, 1 = ducked. Use Interlocked.
    private bool _disposed;

    private const int OutputSampleRate = 24000;
    private static readonly long PlayerLatencyTicks = TimeSpan.FromMilliseconds(100).Ticks;

    /// <summary>
    /// True iff translation audio is currently playing (i.e. we're inside a
    /// duck window). Atomic — readable from any thread.
    /// </summary>
    public bool IsTranslationActive => Volatile.Read(ref _translationActive) != 0;

    /// <summary>
    /// Fires only on duck-state transitions: <c>true</c> = translation just
    /// started playing (callers should turn their own volumes down), <c>false</c>
    /// = translation finished and the silence window elapsed (restore).
    ///
    /// Used by self-ducking consumers (e.g. the CABLE-mode source-monitor
    /// player) which live in our own process and therefore can't go through
    /// <see cref="AudioDucker"/> — that one deliberately skips our own PID so
    /// the translation itself isn't ducked.
    /// </summary>
    public event Action<bool>? DuckedChanged;

    public DuckController(AudioDucker ducker, TimeSpan? silenceTimeout = null)
    {
        _ducker = ducker;
        _silenceTimeout = silenceTimeout ?? TimeSpan.FromMilliseconds(300);
        _timer = new System.Threading.Timer(_ => Tick(), null,
                                            TimeSpan.FromMilliseconds(100),
                                            TimeSpan.FromMilliseconds(100));
    }

    public float DuckRatio
    {
        get => _ducker.DuckRatio;
        set => _ducker.DuckRatio = value;
    }

    /// <summary>
    /// Call on every translated-audio chunk arrival. Pass the chunk byte
    /// count so we can extend the projected end-of-playback timestamp by the
    /// chunk's playback duration.
    /// </summary>
    public void Notify(int audioByteCount)
    {
        if (_disposed) return;
        if (audioByteCount > 0) ExtendPlaybackEnd(audioByteCount);
        try { _ducker.Duck(); } catch { /* never let this kill the WS loop */ }
        SetTranslationActive(true);
    }

    private void ExtendPlaybackEnd(int byteCount)
    {
        int sampleCount = byteCount / 2; // PCM16
        long chunkTicks = (long)sampleCount * TimeSpan.TicksPerSecond / OutputSampleRate;
        long now = DateTime.UtcNow.Ticks;
        long currentEnd, newEnd;
        do
        {
            currentEnd = Interlocked.Read(ref _playbackEndTicks);
            long playbackStart = currentEnd > now ? currentEnd : now + PlayerLatencyTicks;
            newEnd = playbackStart + chunkTicks;
        } while (Interlocked.CompareExchange(ref _playbackEndTicks, newEnd, currentEnd) != currentEnd);
    }

    private void Tick()
    {
        if (_disposed) return;
        long endTicks = Interlocked.Read(ref _playbackEndTicks);
        if (endTicks == 0) return;
        long now = DateTime.UtcNow.Ticks;
        if (now > endTicks + _silenceTimeout.Ticks)
        {
            try { _ducker.Restore(); } catch { /* ignore */ }
            Interlocked.Exchange(ref _playbackEndTicks, 0);
            SetTranslationActive(false);
        }
    }

    /// <summary>
    /// Update <see cref="_translationActive"/> and fire <see cref="DuckedChanged"/>
    /// only on actual transitions, so subscribers don't see one event per
    /// audio chunk.
    /// </summary>
    private void SetTranslationActive(bool active)
    {
        int desired = active ? 1 : 0;
        if (Interlocked.Exchange(ref _translationActive, desired) == desired) return;
        try { DuckedChanged?.Invoke(active); } catch { /* never break the timer */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _timer.Dispose(); } catch { /* ignore */ }
        try { _ducker.Dispose(); } catch { /* ignore */ }
        // Fire one last "restored" so any self-ducking consumers reset their
        // own volumes (the monitor player about to be Disposed too, but a
        // clean exit state is still nicer for any other future subscribers).
        SetTranslationActive(false);
    }
}
