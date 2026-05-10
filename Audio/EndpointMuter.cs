using NAudio.CoreAudioApi;

namespace Babelive.Audio;

/// <summary>
/// Mutes / restores the master endpoint volume of render devices. Different
/// from <see cref="AudioDucker"/>:
///
/// <list type="bullet">
///   <item><b>AudioDucker</b> changes per-session volume (in the audio
///         engine, BEFORE WASAPI loopback taps). Muting a session also
///         mutes the audio fed to the API → translation stops.</item>
///   <item><b>EndpointMuter</b> changes the device-level master mute flag,
///         applied AFTER the audio engine (at the driver / hardware stage).
///         The API still sees full-volume source audio via loopback, while
///         the physical speaker emits silence.</item>
/// </list>
///
/// The intended use: silence the speakers (or HDMI / etc.) the source plays
/// to, while the translation plays through a separate output device the user
/// is listening on (Bluetooth headphones, USB headset, …). Result: user
/// hears only translation, room hears nothing.
///
/// Like AudioDucker, we store only device IDs and re-resolve via a fresh
/// <see cref="MMDeviceEnumerator"/> on the calling thread — MMDevice COM
/// instances are apartment-bound and break when crossed between WPF UI (STA)
/// and worker (MTA) threads.
/// </summary>
public sealed class EndpointMuter : IDisposable
{
    private readonly Dictionary<string, bool> _savedMute = new();
    private readonly object _lock = new();
    private bool _isMuted;
    private bool _disposed;

    /// <summary>
    /// Mute every active render endpoint except those in <paramref name="keepDeviceIds"/>.
    /// Saves the prior mute state for restore.
    /// </summary>
    public void MuteAllExcept(IEnumerable<string> keepDeviceIds)
    {
        var keep = new HashSet<string>(keepDeviceIds, StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            if (_isMuted || _disposed) return;
            _savedMute.Clear();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(
                    DataFlow.Render, DeviceState.Active);
                foreach (var d in devices)
                {
                    try
                    {
                        if (keep.Contains(d.ID)) continue;
                        var vol = d.AudioEndpointVolume;
                        _savedMute[d.ID] = vol.Mute;
                        vol.Mute = true;
                    }
                    catch { /* device unavailable / privileged */ }
                }
                _isMuted = true;
            }
            catch { /* enumerator failure — bail */ }
        }
    }

    /// <summary>Restore mute state for every device we touched.</summary>
    public void Restore()
    {
        lock (_lock)
        {
            if (!_isMuted || _disposed) return;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                foreach (var (id, prevMute) in _savedMute)
                {
                    try
                    {
                        var d = enumerator.GetDevice(id);
                        d.AudioEndpointVolume.Mute = prevMute;
                    }
                    catch { /* device went away */ }
                }
            }
            catch { /* ignore */ }
            _savedMute.Clear();
            _isMuted = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Restore(); } catch { /* best-effort */ }
        _disposed = true;
    }
}
