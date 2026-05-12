using System.Diagnostics;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Babelive.Audio;

/// <summary>
/// Where to capture audio from. The new <see cref="ExcludeProcessSource"/>
/// (default <see cref="Self"/>) is the recommended source on Windows 10 build
/// 20348+ — it captures all system audio EXCEPT our own process, so we don't
/// hear our own translation playback and don't need a virtual audio cable.
/// </summary>
public abstract record CaptureSource
{
    public static CaptureSource SystemAudioExceptSelf => ExcludeProcessSource.Self;
}

/// <summary>Loopback from a specific render endpoint (legacy behavior).</summary>
public sealed record DeviceCaptureSource(MMDevice Device) : CaptureSource;

/// <summary>Process-loopback EXCLUDE: capture system audio except this PID + descendants.</summary>
public sealed record ExcludeProcessSource(uint Pid) : CaptureSource
{
    public static ExcludeProcessSource Self =>
        new(unchecked((uint)Process.GetCurrentProcess().Id));
}

/// <summary>Process-loopback INCLUDE: capture only this PID + descendants.</summary>
public sealed record IncludeProcessSource(uint Pid) : CaptureSource;

/// <summary>
/// Captures audio (via either WASAPI device loopback or Windows 10 build
/// 20348+ process loopback), downmixes to mono, resamples to 24 kHz, packs
/// into PCM16, and pushes byte chunks onto a Channel for the websocket
/// sender to consume.
/// </summary>
public sealed class LoopbackCapture : IDisposable
{
    public const int TargetSampleRate = 24000;

    private readonly IWaveIn _capture;
    private readonly BufferedWaveProvider _bufferedInput;
    private readonly ISampleProvider _resampledMono;
    private readonly Channel<byte[]> _channel;
    private float[] _readBuffer = new float[2400]; // 100 ms @ 24 kHz
    private bool _disposed;

    public ChannelReader<byte[]> Reader => _channel.Reader;
    public WaveFormat SourceFormat => _capture.WaveFormat;
    public CaptureSource Source { get; }

    /// <summary>
    /// Fires after each captured chunk has been enqueued for the translator.
    /// Emits the same 24 kHz / mono / PCM16 buffer. Used by the CABLE-mode
    /// source-monitor feature: when the user's capture source is a virtual
    /// cable, the original audio was diverted into the cable and would
    /// otherwise never reach physical speakers — this lets MainWindow tee the
    /// same stream into a second AudioPlayer on the Playback device.
    ///
    /// PCM format matches <see cref="AudioPlayer"/>'s input (24 kHz mono
    /// PCM16) so no extra conversion is needed downstream.
    /// </summary>
    public event Action<byte[]>? Pcm24KHzAvailable;

    public LoopbackCapture(CaptureSource source)
    {
        Source = source;
        _capture = source switch
        {
            DeviceCaptureSource d => new WasapiLoopbackCapture(d.Device),
            ExcludeProcessSource e => new ProcessLoopbackCapture(e.Pid, ProcessLoopbackMode.ExcludeProcessTree),
            IncludeProcessSource i => new ProcessLoopbackCapture(i.Pid, ProcessLoopbackMode.IncludeProcessTree),
            _ => throw new ArgumentException($"Unsupported CaptureSource: {source.GetType()}"),
        };

        var srcFormat = _capture.WaveFormat;
        _bufferedInput = new BufferedWaveProvider(srcFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(3),
        };

        ISampleProvider sp = _bufferedInput.ToSampleProvider();
        if (sp.WaveFormat.Channels > 1)
            sp = new DownmixToMonoSampleProvider(sp);
        _resampledMono = new WdlResamplingSampleProvider(sp, TargetSampleRate);

        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
        });

        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, _) => _channel.Writer.TryComplete();
    }

    public void Start() => _capture.StartRecording();

    public void Stop()
    {
        try { _capture.StopRecording(); } catch { /* ignore */ }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        _bufferedInput.AddSamples(e.Buffer, 0, e.BytesRecorded);

        // How many 24 kHz mono samples are now derivable from what we have
        double srcSeconds = (double)_bufferedInput.BufferedBytes
                            / _capture.WaveFormat.AverageBytesPerSecond;
        int availableOutSamples = (int)(srcSeconds * TargetSampleRate);

        // Round down to 10 ms (240-sample) chunks for consistent latency
        int outChunk = (availableOutSamples / 240) * 240;
        if (outChunk <= 0) return;

        if (_readBuffer.Length < outChunk) _readBuffer = new float[outChunk];
        int read = _resampledMono.Read(_readBuffer, 0, outChunk);
        if (read <= 0) return;

        var outBuf = new byte[read * 2];
        for (int i = 0; i < read; i++)
        {
            int v = (int)Math.Round(_readBuffer[i] * 32767f);
            if (v > 32767) v = 32767;
            else if (v < -32768) v = -32768;
            outBuf[i * 2]     = (byte)(v & 0xFF);
            outBuf[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        _channel.Writer.TryWrite(outBuf);

        // Tee the same PCM16/24 kHz buffer to the monitor subscriber (if any).
        // Wrapped in try/catch so a buggy handler never tears down capture.
        try { Pcm24KHzAvailable?.Invoke(outBuf); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _capture.StopRecording(); } catch { /* ignore */ }
        try { _capture.Dispose(); } catch { /* ignore */ }
        _channel.Writer.TryComplete();
    }

    // ---- discovery -------------------------------------------------------
    public static List<MMDevice> EnumerateRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
    }

    public static MMDevice DefaultRenderDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }
}

/// <summary>
/// Generic N-channel-to-mono downmix; averages all channels per frame.
/// NAudio's StereoToMonoSampleProvider only handles 2 channels; this works
/// for surround mixes too.
/// </summary>
internal sealed class DownmixToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly int _srcChannels;
    private float[] _scratch = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    public DownmixToMonoSampleProvider(ISampleProvider src)
    {
        _src = src;
        _srcChannels = src.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 1);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int needed = count * _srcChannels;
        if (_scratch.Length < needed) _scratch = new float[needed];
        int read = _src.Read(_scratch, 0, needed);
        int frames = read / _srcChannels;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0f;
            int basePos = f * _srcChannels;
            for (int c = 0; c < _srcChannels; c++)
                sum += _scratch[basePos + c];
            buffer[offset + f] = sum / _srcChannels;
        }
        return frames;
    }
}
