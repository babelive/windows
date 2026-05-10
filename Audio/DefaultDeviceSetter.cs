using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Babelive.Audio;

/// <summary>
/// Programmatically switch the Windows default render endpoint via the
/// undocumented <c>IPolicyConfigVista</c> COM interface
/// (CLSID <c>{870AF99C-171D-4F9E-AF0D-E63DF40C2BC9}</c>) — the same API
/// <c>mmsys.cpl</c> uses internally. Stable since Windows Vista.
///
/// Used to redirect source apps away from the user's listening device:
/// if the user has Bluetooth headphones connected and selected as our
/// translation Playback device, we want source apps to default to Onboard
/// Speaker (which we then mute via <see cref="EndpointMuter"/>) instead of
/// also routing to the headphones (where they'd be heard alongside the
/// translation).
///
/// Saves the previous default for restoration on <see cref="Restore"/> /
/// <see cref="Dispose"/>. We set all three roles (Multimedia, Console,
/// Communications) so apps that pick a specific role still follow.
/// </summary>
public sealed class DefaultDeviceSetter : IDisposable
{
    private string? _savedDefaultId;
    private bool _disposed;

    public bool SetDefault(string newDeviceId)
    {
        if (_disposed) return false;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _savedDefaultId = current.ID;

            ApplyDefault(newDeviceId);
            return true;
        }
        catch
        {
            _savedDefaultId = null;
            return false;
        }
    }

    public void Restore()
    {
        if (_disposed || _savedDefaultId == null) return;
        try { ApplyDefault(_savedDefaultId); } catch { /* best-effort */ }
        _savedDefaultId = null;
    }

    private static void ApplyDefault(string deviceId)
    {
        var policyConfig = CreatePolicyConfig();
        try
        {
            policyConfig.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
            policyConfig.SetDefaultEndpoint(deviceId, ERole.eConsole);
            policyConfig.SetDefaultEndpoint(deviceId, ERole.eCommunications);
        }
        finally
        {
            try { Marshal.FinalReleaseComObject(policyConfig); } catch { }
        }
    }

    private static IPolicyConfigVista CreatePolicyConfig()
    {
        var t = Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"))
            ?? throw new InvalidOperationException("PolicyConfigVista CLSID not registered.");
        var instance = Activator.CreateInstance(t)
            ?? throw new InvalidOperationException("Failed to instantiate IPolicyConfigVista.");
        return (IPolicyConfigVista)instance;
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Restore(); } catch { /* best-effort */ }
        _disposed = true;
    }

    // ---- ERole + COM interface ----

    private enum ERole : uint
    {
        eConsole        = 0,
        eMultimedia     = 1,
        eCommunications = 2,
    }

    /// <summary>
    /// Mirror of Microsoft's IPolicyConfigVista vtable. Order matters — even
    /// methods we don't call are declared (with correct argument counts and
    /// IntPtr placeholders) so SetDefaultEndpoint sits at the right offset.
    /// </summary>
    [ComImport, Guid("568b9108-44bf-40b4-9006-86afe5b5a620"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
    {
        [PreserveSig] int GetMixFormat(IntPtr pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(IntPtr pszDeviceName, int bDefault, IntPtr ppFormat);
        [PreserveSig] int SetDeviceFormat(IntPtr pszDeviceName, IntPtr pEndpointFormat, IntPtr pMixFormat);
        [PreserveSig] int GetProcessingPeriod(IntPtr pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(IntPtr pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode(IntPtr pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode(IntPtr pszDeviceName, IntPtr pMode);
        [PreserveSig] int GetPropertyValue(IntPtr pszDeviceName, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue(IntPtr pszDeviceName, IntPtr key, IntPtr pv);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

        [PreserveSig]
        int SetEndpointVisibility(IntPtr pszDeviceName, int visible);
    }
}
