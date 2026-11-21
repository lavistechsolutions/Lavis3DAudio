using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Lavis3DAudio.Audio;

/// <summary>Changes the Windows default playback device (undocumented IPolicyConfig, stable since Windows 7).</summary>
public static class DefaultDevice
{
    const string VirtualDeviceName = "FxSound";

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        // Only SetDefaultEndpoint is called; the earlier slots just keep the vtable order.
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, bool defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, bool defaultPeriod, IntPtr defaultP, IntPtr minP);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, bool visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    /// <summary>Makes <paramref name="deviceId"/> the default for apps and games (communications is left alone).</summary>
    public static void Set(string deviceId)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, Role.Console));
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, Role.Multimedia));
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    public static bool IsVirtual(MMDevice device) =>
        device.FriendlyName.Contains(VirtualDeviceName, StringComparison.OrdinalIgnoreCase);

    public static MMDevice? FindVirtual() => AudioEngine.RenderDevices().FirstOrDefault(IsVirtual);
}
