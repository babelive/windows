using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Babelive.Audio;

/// <summary>
/// Per-application audio output device routing on Windows 10 RS4+ / Win 11
/// via the undocumented WinRT-activated <c>IAudioPolicyConfig</c> interface
/// (the same API the Win11 Volume Mixer per-app "Output device" dropdown
/// uses internally).
///
/// Implementation notes:
/// <list type="bullet">
///   <item>The interface is activated via <c>combase!RoGetActivationFactory</c>
///         (WinRT activation), not <c>CoCreateInstance</c>. Class string:
///         <c>Windows.Media.Internal.AudioPolicyConfig</c>.</item>
///   <item>.NET 5+ removed the built-in WinRT marshalers, so
///         <c>UnmanagedType.HString</c> and <c>UnmanagedType.IInspectable</c>
///         throw <c>MarshalDirectiveException</c>. We marshal HSTRINGs by
///         hand (<c>WindowsCreateString</c> / <c>WindowsDeleteString</c>)
///         and treat the factory as a raw <c>IUnknown</c>* with the 3
///         IInspectable methods declared explicitly as the first 3 slots
///         (after the implicit IUnknown 3) so vtable positions line up.</item>
///   <item>Two IIDs for two Windows generations (vtable layout identical):
///         Win11 / Win10 21H2+ uses <c>AB3D4648-…</c>; older Win10 uses
///         <c>2A59116D-…</c>. We pick by build number.</item>
///   <item><c>SetPersistedDefaultAudioEndpoint</c> takes the device id
///         wrapped as <c>\\?\SWD#MMDEVAPI#{mmdevice-id}#{render-iface-guid}</c>
///         encoded as an HSTRING.</item>
///   <item>Win11 22H2+ live-migrates active sessions when the persisted
///         default changes — Chrome jumps devices without a restart.</item>
/// </list>
/// Distilled from EarTrumpet (MIT) + SoundSwitch (GPL) reverse-engineering.
/// </summary>
public sealed class AppAudioRouter : IDisposable
{
    public static bool IsSupported =>
        Environment.OSVersion.Version.Major >= 10
        && Environment.OSVersion.Version.Build >= 17134; // Win10 RS4

    private readonly List<uint> _routedPids = new();
    private bool _disposed;

    public string? LastError { get; private set; }
    public int RoutedCount => _routedPids.Count;

    /// <summary>
    /// Persistently route <paramref name="pid"/>'s audio output to
    /// <paramref name="mmDeviceId"/> (the raw <c>MMDevice.ID</c> string).
    /// Sets both eMultimedia and eConsole roles. Throws on failure.
    /// Tracked so <see cref="Dispose"/> can revert.
    /// </summary>
    public void Route(uint pid, string mmDeviceId)
    {
        SetPersistedDefaultEndpoint(pid, mmDeviceId);
        _routedPids.Add(pid);
    }

    /// <summary>
    /// Route every audio session currently active on the device with id
    /// <paramref name="fromDeviceId"/> (excluding our own PID and PID 0)
    /// to <paramref name="toMmDeviceId"/>. Inner per-session failures are
    /// recorded in <see cref="LastError"/>.
    /// </summary>
    public void RouteAllSessionsOn(string fromDeviceId, string toMmDeviceId)
    {
        var ownPid = (uint)Environment.ProcessId;
        var failures = new List<string>();
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(fromDeviceId);
        var sessions = device.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            uint pid = 0;
            try
            {
                var s = sessions[i];
                pid = (uint)s.GetProcessID;
                if (pid == 0 || pid == ownPid) continue;
                if (s.State == AudioSessionState.AudioSessionStateExpired) continue;
                Route(pid, toMmDeviceId);
            }
            catch (Exception ex)
            {
                failures.Add($"PID {pid}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        if (failures.Count > 0) LastError = string.Join(" | ", failures);
    }

    /// <summary>Revert every routing override we set.</summary>
    public void Restore()
    {
        foreach (var pid in _routedPids)
        {
            try { ClearPersistedDefaultEndpoint(pid); } catch { /* best-effort */ }
        }
        _routedPids.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Restore(); } catch { /* best-effort */ }
        _disposed = true;
    }

    // ---- Internals --------------------------------------------------------

    private static void SetPersistedDefaultEndpoint(uint pid, string mmDeviceId)
    {
        var wrapped = WrapDeviceId(mmDeviceId);
        int hr = WindowsCreateString(wrapped, (uint)wrapped.Length, out IntPtr hstr);
        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
        try
        {
            CallSet(pid, RoleMultimedia, hstr);
            CallSet(pid, RoleConsole, hstr);
        }
        finally { WindowsDeleteString(hstr); }
    }

    private static void ClearPersistedDefaultEndpoint(uint pid)
    {
        // IntPtr.Zero clears the override (revert to system default).
        CallSet(pid, RoleMultimedia, IntPtr.Zero);
        CallSet(pid, RoleConsole, IntPtr.Zero);
    }

    private static void CallSet(uint pid, int role, IntPtr hstrDeviceId)
    {
        IntPtr factoryPtr = ActivateFactory();
        try
        {
            var factoryObj = Marshal.GetObjectForIUnknown(factoryPtr);
            try
            {
                int hr;
                if (Is21H2OrLater())
                {
                    var f = (IAudioPolicyConfigFactoryWin11)factoryObj;
                    hr = f.SetPersistedDefaultAudioEndpoint(pid, FlowRender, role, hstrDeviceId);
                }
                else
                {
                    var f = (IAudioPolicyConfigFactoryDown)factoryObj;
                    hr = f.SetPersistedDefaultAudioEndpoint(pid, FlowRender, role, hstrDeviceId);
                }
                // 0x88890008 (AUDCLNT_E_RESOURCES_INVALIDATED-ish): the target
                // process has no active audio session yet — the persisted
                // setting is still recorded.
                if (hr < 0 && (uint)hr != 0x88890008) Marshal.ThrowExceptionForHR(hr);
            }
            finally
            {
                try { Marshal.FinalReleaseComObject(factoryObj); } catch { }
            }
        }
        finally { Marshal.Release(factoryPtr); }
    }

    /// <summary>
    /// Activate the IAudioPolicyConfig factory via RoGetActivationFactory.
    /// Returns the raw IUnknown* (with one ref count owned by caller).
    /// </summary>
    private static IntPtr ActivateFactory()
    {
        // Build the activatable-class HSTRING
        int hr = WindowsCreateString(ActivatableClass, (uint)ActivatableClass.Length,
                                     out IntPtr classNameHstring);
        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
        try
        {
            var iid = Is21H2OrLater() ? IidWin11 : IidDown;
            hr = RoGetActivationFactory(classNameHstring, ref iid, out IntPtr factoryPtr);
            if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
            return factoryPtr;
        }
        finally { WindowsDeleteString(classNameHstring); }
    }

    private static bool Is21H2OrLater()
    {
        var v = Environment.OSVersion.Version;
        return v.Major > 10 || (v.Major == 10 && v.Build >= 22000);
    }

    private const string ActivatableClass = "Windows.Media.Internal.AudioPolicyConfig";

    private const string MmDevicePrefix      = @"\\?\SWD#MMDEVAPI#";
    private const string RenderInterfaceGuid = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    private static string WrapDeviceId(string mmDeviceId) =>
        $"{MmDevicePrefix}{mmDeviceId}{RenderInterfaceGuid}";

    // EDataFlow / ERole values (we only use a few)
    private const int FlowRender      = 0;
    private const int RoleConsole     = 0;
    private const int RoleMultimedia  = 1;

    private static readonly Guid IidWin11 = new("AB3D4648-E242-459F-B02F-541C70306324");
    private static readonly Guid IidDown  = new("2A59116D-6C4F-45E0-A74F-707E3FEF9258");

    // Vtable layout: IUnknown's 3 methods are implicit (CLR) when interface
    // type is IsIUnknown. The COM object's actual vtable then has 3
    // IInspectable methods, then 19 reserved, then our 3. We declare the
    // 3 IInspectable + 19 reserved + 3 ours so slot offsets match.

    [ComImport, Guid("AB3D4648-E242-459F-B02F-541C70306324"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfigFactoryWin11
    {
        // IInspectable (slots 3-5 in the actual vtable; 0-2 here after
        // CLR's implicit IUnknown)
        [PreserveSig] int GetIids(out int iidCount, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        // Reserved 1-19 (vtable slots 6-24)
        [PreserveSig] int __r01();
        [PreserveSig] int __r02();
        [PreserveSig] int __r03();
        [PreserveSig] int __r04();
        [PreserveSig] int __r05();
        [PreserveSig] int __r06();
        [PreserveSig] int __r07();
        [PreserveSig] int __r08();
        [PreserveSig] int __r09();
        [PreserveSig] int __r10();
        [PreserveSig] int __r11();
        [PreserveSig] int __r12();
        [PreserveSig] int __r13();
        [PreserveSig] int __r14();
        [PreserveSig] int __r15();
        [PreserveSig] int __r16();
        [PreserveSig] int __r17();
        [PreserveSig] int __r18();
        [PreserveSig] int __r19();

        // Slot 25
        [PreserveSig]
        int SetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, IntPtr hstrDeviceId);
        // Slot 26
        [PreserveSig]
        int GetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, out IntPtr hstrDeviceId);
        // Slot 27
        [PreserveSig]
        int ClearAllPersistedApplicationDefaultEndpoints();
    }

    // Identical vtable shape, downlevel IID
    [ComImport, Guid("2A59116D-6C4F-45E0-A74F-707E3FEF9258"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfigFactoryDown
    {
        [PreserveSig] int GetIids(out int iidCount, out IntPtr iids);
        [PreserveSig] int GetRuntimeClassName(out IntPtr className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);

        [PreserveSig] int __r01();
        [PreserveSig] int __r02();
        [PreserveSig] int __r03();
        [PreserveSig] int __r04();
        [PreserveSig] int __r05();
        [PreserveSig] int __r06();
        [PreserveSig] int __r07();
        [PreserveSig] int __r08();
        [PreserveSig] int __r09();
        [PreserveSig] int __r10();
        [PreserveSig] int __r11();
        [PreserveSig] int __r12();
        [PreserveSig] int __r13();
        [PreserveSig] int __r14();
        [PreserveSig] int __r15();
        [PreserveSig] int __r16();
        [PreserveSig] int __r17();
        [PreserveSig] int __r18();
        [PreserveSig] int __r19();

        [PreserveSig]
        int SetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, IntPtr hstrDeviceId);
        [PreserveSig]
        int GetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, out IntPtr hstrDeviceId);
        [PreserveSig]
        int ClearAllPersistedApplicationDefaultEndpoints();
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,        // HSTRING
        [In] ref Guid iid,
        out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        uint length,
        out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
}
