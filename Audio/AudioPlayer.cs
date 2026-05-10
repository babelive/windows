using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Babelive.Audio;

/// <summary>
/// Plays back PCM16 mono @ 24 kHz frames pushed by the translator. If
/// constructed with an explicit MMDevice it routes there via WASAPI shared
/// mode; otherwise it falls back to WaveOutEvent (system default).
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private static readonly WaveFormat Format = new(24000, 16, 1);
    private readonly BufferedWaveProvider _buffer;
    private readonly IWavePlayer _output;
    private bool _disposed;

    public AudioPlayer(MMDevice? device = null)
    {
        _buffer = new BufferedWaveProvider(Format)
        {
            BufferDuration = TimeSpan.FromMinutes(2),
            DiscardOnBufferOverflow = true,
        };

        if (device != null)
        {
            // The MMDevice instance handed to us may be stale: we cached it
            // at app startup, and BT sleep/reconnect or our own endpoint-mute
            // cycle can leave the COM RCW invalidated. Re-resolve via a
            // fresh enumerator on this thread.
            MMDevice fresh;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                fresh = enumerator.GetDevice(device.ID);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    $"Playback device '{device.FriendlyName}' couldn't be re-resolved " +
                    $"(it may have been disconnected). Try a different device.", e);
            }

            try
            {
                _output = new WasapiOut(fresh, AudioClientShareMode.Shared,
                                        useEventSync: true, latency: 100);
            }
            catch (System.Runtime.InteropServices.COMException cex)
                when ((uint)cex.HResult == 0x88890004) // AUDCLNT_E_DEVICE_INVALIDATED
            {
                throw new InvalidOperationException(
                    $"Playback device '{device.FriendlyName}' is currently unavailable " +
                    $"(AUDCLNT_E_DEVICE_INVALIDATED). Likely causes: a Bluetooth device " +
                    $"that has gone to sleep / disconnected, or a USB device that was " +
                    $"unplugged. Wake or reconnect it (try playing any sound through it " +
                    $"first), or pick a different Playback device.", cex);
            }
        }
        else
        {
            _output = new WaveOutEvent { DesiredLatency = 100 };
        }

        _output.Init(_buffer);
    }

    public void Start() => _output.Play();

    /// <summary>0.0 (muted) ‥ 1.0 (full) playback volume.</summary>
    public float Volume
    {
        get => _output?.Volume ?? 1f;
        set { if (_output != null) _output.Volume = Math.Clamp(value, 0f, 1f); }
    }

    public void Push(byte[] pcm)
    {
        if (pcm == null || pcm.Length == 0) return;
        try { _buffer.AddSamples(pcm, 0, pcm.Length); }
        catch (InvalidOperationException) { /* buffer overflow; discarded */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _output.Stop(); } catch { /* ignore */ }
        try { _output.Dispose(); } catch { /* ignore */ }
    }
}
