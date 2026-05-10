using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
// NAudio's IAudioCaptureClient is `internal`, and NAudio's IAudioClient
// declares Initialize with `[In] WaveFormat pFormat` which the CLR tries to
// marshal as a COM interface (failing with InvalidCastException via
// GetCOMIPFromRCW). We define our own COM interfaces in this file with
// IntPtr parameters and marshal WAVEFORMATEX manually.

namespace Babelive.Audio;

public enum ProcessLoopbackMode
{
    /// <summary>Capture only the target process (and its child processes).</summary>
    IncludeProcessTree = 0,
    /// <summary>Capture everything EXCEPT the target process tree.</summary>
    ExcludeProcessTree = 1,
}

/// <summary>
/// Process-loopback capture for Windows 10 build 20348+ / Windows 11.
///
/// Activates the magic "VAD\Process_Loopback" virtual audio device with a
/// process-id filter via <c>ActivateAudioInterfaceAsync</c>:
/// <list type="bullet">
///   <item><see cref="ProcessLoopbackMode.IncludeProcessTree"/> — capture only
///         that process's audio.</item>
///   <item><see cref="ProcessLoopbackMode.ExcludeProcessTree"/> — capture all
///         system audio EXCEPT that process. Excluding our own PID gives us
///         system audio without picking up our own translation playback, so
///         no virtual audio cable is needed even when capture and playback
///         share the same physical device.</item>
/// </list>
///
/// Format is fixed: 16-bit stereo PCM @ 44.1 kHz. The API rejects
/// <c>GetMixFormat</c> / <c>IsFormatSupported</c> with E_NOTIMPL so the format
/// can't be negotiated. Downstream resampling (in <see cref="LoopbackCapture"/>)
/// already accepts arbitrary input rates.
///
/// Implements <see cref="IWaveIn"/> so it's a drop-in replacement for
/// <c>WasapiLoopbackCapture</c> in the existing pipeline.
/// </summary>
public sealed class ProcessLoopbackCapture : IWaveIn
{
    public const int RequiredWindowsBuild = 20348;

    public WaveFormat WaveFormat { get; set; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public static bool IsSupported =>
        Environment.OSVersion.Platform == PlatformID.Win32NT
        && Environment.OSVersion.Version.Build >= RequiredWindowsBuild;

    private readonly uint _targetPid;
    private readonly ProcessLoopbackMode _mode;

    private IAudioClientPL? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private EventWaitHandle? _bufferReady;
    private Thread? _captureThread;
    private CancellationTokenSource? _cts;
    private byte[] _buffer = Array.Empty<byte>();
    private bool _disposed;

    public ProcessLoopbackCapture(uint targetPid, ProcessLoopbackMode mode)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(
                $"Process Loopback requires Windows 10 build {RequiredWindowsBuild} or higher.");
        _targetPid = targetPid;
        _mode = mode;
        WaveFormat = new WaveFormat(44100, 16, 2);
    }

    public void StartRecording()
    {
        if (_audioClient != null)
            throw new InvalidOperationException("Already recording.");

        // The OS-supplied IAudioClient from ActivateAudioInterfaceAsync is
        // bound to the MTA. Calling it from the WPF UI's STA thread triggers
        // cross-apartment marshaling that fails because our IAudioClientPL
        // isn't registered with COM. So we run the entire setup on an MTA
        // worker thread, then keep using it from MTA threads only (the
        // capture thread). Stop() / Dispose() also marshal back to MTA.
        Task.Run(SetupOnMtaAsync).GetAwaiter().GetResult();

        // Capture loop runs on its own thread. Default ApartmentState on
        // a Thread in .NET on Windows is MTA.
        _cts = new CancellationTokenSource();
        _captureThread = new Thread(() => CaptureLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "ProcessLoopbackCapture",
        };
        _captureThread.Start();
    }

    private async Task SetupOnMtaAsync()
    {
        // (1) activation
        _audioClient = await ActivateOnMtaAsync().ConfigureAwait(false);

        // (2) initialize with a manually-marshaled WAVEFORMATEX
        const long bufferDurationHns = 200 * 10_000; // 200 ms in 100-ns units
        const uint AUDCLNT_STREAMFLAGS_LOOPBACK       = 0x00020000;
        const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK  = 0x00040000;
        const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
        const uint streamFlags = AUDCLNT_STREAMFLAGS_LOOPBACK
                               | AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                               | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM;
        const int AUDCLNT_SHAREMODE_SHARED = 0;

        var wfx = new WAVEFORMATEX
        {
            wFormatTag      = 1, // WAVE_FORMAT_PCM
            nChannels       = (ushort)WaveFormat.Channels,
            nSamplesPerSec  = (uint)WaveFormat.SampleRate,
            wBitsPerSample  = (ushort)WaveFormat.BitsPerSample,
            nBlockAlign     = (ushort)(WaveFormat.Channels * (WaveFormat.BitsPerSample / 8)),
            nAvgBytesPerSec = (uint)(WaveFormat.SampleRate * WaveFormat.Channels * (WaveFormat.BitsPerSample / 8)),
            cbSize          = 0,
        };

        IntPtr pFormat = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
        try
        {
            Marshal.StructureToPtr(wfx, pFormat, fDeleteOld: false);
            int hr = _audioClient.Initialize(AUDCLNT_SHAREMODE_SHARED, streamFlags,
                                             bufferDurationHns, 0, pFormat, IntPtr.Zero);
            if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
        }
        finally
        {
            Marshal.FreeHGlobal(pFormat);
        }

        // (3) wire up the buffer-ready event handle
        _bufferReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        int hr2 = _audioClient.SetEventHandle(_bufferReady.SafeWaitHandle.DangerousGetHandle());
        if (hr2 < 0) throw Marshal.GetExceptionForHR(hr2)!;

        // (4) get the IAudioCaptureClient service
        var captureClientGuid = typeof(IAudioCaptureClient).GUID;
        hr2 = _audioClient.GetService(ref captureClientGuid, out object captureObj);
        if (hr2 < 0) throw Marshal.GetExceptionForHR(hr2)!;
        if (captureObj == null)
            throw new InvalidOperationException("GetService returned null for IAudioCaptureClient.");
        _captureClient = captureObj as IAudioCaptureClient
            ?? throw new InvalidCastException(
                $"Failed to cast GetService result to IAudioCaptureClient. " +
                $"Returned type: {captureObj.GetType().FullName}, " +
                $"IsComObject: {Marshal.IsComObject(captureObj)}");

        // (5) start streaming
        hr2 = _audioClient.Start();
        if (hr2 < 0) throw Marshal.GetExceptionForHR(hr2)!;
    }

    public void StopRecording()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }

        // _audioClient.Stop() must be called on an MTA thread for the same
        // reason Initialize must (cross-apartment marshaling failure
        // otherwise).
        if (_audioClient != null)
        {
            try { Task.Run(() => _audioClient.Stop()).GetAwaiter().GetResult(); }
            catch { /* ignore */ }
        }

        _captureThread?.Join(TimeSpan.FromSeconds(2));
        _captureThread = null;

        if (_captureClient != null)
        {
            try { Marshal.FinalReleaseComObject(_captureClient); } catch { /* ignore */ }
            _captureClient = null;
        }
        if (_audioClient != null)
        {
            try { Marshal.FinalReleaseComObject(_audioClient); } catch { /* ignore */ }
            _audioClient = null;
        }
        _bufferReady?.Dispose();
        _bufferReady = null;

        RecordingStopped?.Invoke(this, new StoppedEventArgs(null));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopRecording(); } catch { /* ignore */ }
    }

    // ---- capture thread --------------------------------------------------

    private void CaptureLoop(CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            int frameSize = WaveFormat.Channels * (WaveFormat.BitsPerSample / 8);
            while (!ct.IsCancellationRequested)
            {
                if (!_bufferReady!.WaitOne(200)) continue;
                if (ct.IsCancellationRequested) break;

                int hr = _captureClient!.GetNextPacketSize(out int packetFrames);
                while (packetFrames > 0 && !ct.IsCancellationRequested)
                {
                    hr = _captureClient.GetBuffer(out IntPtr dataPtr,
                                                  out int framesAvailable,
                                                  out var bufferFlags,
                                                  out long _, out long _);
                    if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;

                    int byteCount = framesAvailable * frameSize;
                    if (_buffer.Length < byteCount) _buffer = new byte[byteCount];

                    if ((bufferFlags & AudioClientBufferFlags.Silent) != 0)
                        Array.Clear(_buffer, 0, byteCount);
                    else if (byteCount > 0)
                        Marshal.Copy(dataPtr, _buffer, 0, byteCount);

                    _captureClient.ReleaseBuffer(framesAvailable);

                    if (byteCount > 0)
                        DataAvailable?.Invoke(this, new WaveInEventArgs(_buffer, byteCount));

                    hr = _captureClient.GetNextPacketSize(out packetFrames);
                }
            }
        }
        catch (Exception ex) { error = ex; }
        if (error != null)
            RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
    }

    // ---- activation -------------------------------------------------------

    private async Task<IAudioClientPL> ActivateOnMtaAsync()
    {
        var activation = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.PROCESS_LOOPBACK,
            ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
            {
                TargetProcessId = _targetPid,
                ProcessLoopbackMode = _mode == ProcessLoopbackMode.IncludeProcessTree
                    ? PROCESS_LOOPBACK_MODE.INCLUDE
                    : PROCESS_LOOPBACK_MODE.EXCLUDE,
            },
        };

        IntPtr activationParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
        IntPtr propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(activation, activationParamsPtr, fDeleteOld: false);
            var pv = new PROPVARIANT_BLOB
            {
                vt = (ushort)VARENUM.VT_BLOB,
                cbSize = (uint)Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>(),
                pBlobData = activationParamsPtr,
            };
            Marshal.StructureToPtr(pv, propVariantPtr, fDeleteOld: false);

            var iidIAudioClient = typeof(IAudioClientPL).GUID;
            var handler = new ActivationCompletionHandler();

            ActivateAudioInterfaceAsync(VirtualAudioDevicePath, ref iidIAudioClient,
                                        propVariantPtr, handler, out _);

            // Block this MTA Task.Run thread while the OS fires the callback.
            // The callback is what actually delivers IAudioClient to us.
            bool completed = await Task.Run(() => handler.Wait(TimeSpan.FromSeconds(10)))
                                       .ConfigureAwait(false);
            if (!completed)
                throw new TimeoutException("ActivateAudioInterfaceAsync timed out after 10s.");
            if (handler.HResult < 0)
                throw Marshal.GetExceptionForHR(handler.HResult)!;
            return handler.Result
                ?? throw new InvalidOperationException("Activation succeeded but returned null.");
        }
        finally
        {
            Marshal.FreeHGlobal(propVariantPtr);
            Marshal.FreeHGlobal(activationParamsPtr);
        }
    }

    // ---- P/Invoke + COM ---------------------------------------------------

    private const string VirtualAudioDevicePath = "VAD\\Process_Loopback";

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid riid,
        [In] IntPtr activationParams, // PROPVARIANT*
        [In] IActivateAudioInterfaceCompletionHandler completionHandler,
        [Out] out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig]
        int OnActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult,
                              [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    // Mirror of NAudio's internal IAudioCaptureClient. Same IID, same vtable.
    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr dataBuffer, out int numFramesToRead,
                      out AudioClientBufferFlags bufferFlags,
                      out long devicePosition, out long qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(int numFramesRead);

        [PreserveSig]
        int GetNextPacketSize(out int numFramesInNextPacket);
    }

    private enum AUDIOCLIENT_ACTIVATION_TYPE : int
    {
        DEFAULT = 0,
        PROCESS_LOOPBACK = 1,
    }

    private enum PROCESS_LOOPBACK_MODE : int
    {
        INCLUDE = 0,
        EXCLUDE = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    private enum VARENUM : ushort { VT_BLOB = 65 }

    /// <summary>
    /// PROPVARIANT layout for VT_BLOB. On x64: 24 bytes total
    /// (vt + 6 reserved + 4 cbSize + 4 padding + 8 pBlobData).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT_BLOB
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;
        [FieldOffset(8)] public uint cbSize;
        [FieldOffset(16)] public IntPtr pBlobData;
    }

    private sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly ManualResetEventSlim _done = new(false);
        public IAudioClientPL? Result;
        public int HResult;

        public int OnActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                int hr = op.GetActivateResult(out int activateHr, out object iface);
                HResult = activateHr < 0 ? activateHr : hr;
                if (HResult >= 0)
                {
                    Result = iface as IAudioClientPL
                        ?? throw new InvalidCastException(
                            $"GetActivateResult did not return an IAudioClient. " +
                            $"Type: {iface?.GetType().FullName ?? "null"}");
                }
            }
            catch (Exception ex)
            {
                HResult = ex.HResult != 0 ? ex.HResult : -1;
            }
            finally
            {
                _done.Set();
            }
            return 0; // S_OK
        }

        public bool Wait(TimeSpan timeout) => _done.Wait(timeout);
    }

    // ---- our own COM interfaces (own all marshaling decisions) ----

    /// <summary>
    /// Same IID as the standard IAudioClient (1CB9AD4C-...) but with
    /// IntPtr-based parameters so the CLR doesn't try to marshal a managed
    /// WaveFormat class as a COM interface (which it does for NAudio's
    /// equivalent, throwing InvalidCastException via GetCOMIPFromRCW).
    /// </summary>
    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClientPL
    {
        [PreserveSig]
        int Initialize(int shareMode, uint streamFlags,
                       long hnsBufferDuration, long hnsPeriodicity,
                       IntPtr pFormat,         // const WAVEFORMATEX*
                       IntPtr audioSessionGuid); // LPCGUID, IntPtr.Zero == NULL

        [PreserveSig] int GetBufferSize(out uint bufferSize);
        [PreserveSig] int GetStreamLatency(out long streamLatency);
        [PreserveSig] int GetCurrentPadding(out uint currentPadding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig]
        int GetService([In] ref Guid riid,
                       [Out, MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint   nSamplesPerSec;
        public uint   nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }
}
