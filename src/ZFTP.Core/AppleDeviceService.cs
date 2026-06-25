// ============================================================================
//  ZFTP - AppleDeviceService
//  ---------------------------------------------------------------------------
//  Device discovery for the iPhone/iPad provider, the Apple counterpart to
//  AdbService. It talks to the device over Apple's USB protocol (usbmuxd /
//  lockdownd) via the imobiledevice-net library, whose native bits are bundled
//  by the NuGet package.
//
//  IMPORTANT - this provider is LIMITED by design. Apple's "AFC" file channel
//  only exposes the device's MEDIA area (photos/videos under DCIM, plus the
//  Documents folders of apps that enable File Sharing). It does NOT expose the
//  whole filesystem like Android's /sdcard does - that needs a jailbreak. It
//  also relies on Apple's USB driver being present (install Apple Devices /
//  iTunes), and the phone must be unlocked and "Trust This Computer" tapped.
// ============================================================================

using System.Collections.ObjectModel;
using iMobileDevice;
using iMobileDevice.iDevice;
using iMobileDevice.Lockdown;

namespace ZFTP.Core;

public static class AppleDeviceService
{
    private static bool _loaded;
    private static bool _loadOk;

    /// <summary>True if the native Apple-device libraries loaded (they're bundled).</summary>
    public static bool Available { get { EnsureLoaded(); return _loadOk; } }

    /// <summary>Load the bundled native libraries once. Safe to call repeatedly.</summary>
    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try { NativeLibraries.Load(); _loadOk = true; }
        catch { _loadOk = false; }
    }

    /// <summary>One connected Apple device.</summary>
    public sealed record Device(string Udid, string Name)
    {
        public string Label => string.IsNullOrWhiteSpace(Name) ? Udid : $"{Name} ({Udid})";
    }

    /// <summary>UDIDs of every connected device (paired or not).</summary>
    public static string[] ListUdids()
    {
        EnsureLoaded();
        if (!_loadOk) return Array.Empty<string>();
        try
        {
            var idevice = LibiMobileDevice.Instance.iDevice;
            int count = 0;
            var ret = idevice.idevice_get_device_list(out ReadOnlyCollection<string> udids, ref count);
            if (ret != iDeviceError.Success || udids == null) return Array.Empty<string>();
            return udids.Where(u => !string.IsNullOrWhiteSpace(u))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Connected devices with a friendly name for the picker.</summary>
    public static List<Device> ListDevices()
    {
        var list = new List<Device>();
        foreach (var udid in ListUdids())
            list.Add(new Device(udid, GetDeviceName(udid)));
        return list;
    }

    /// <summary>True if this UDID is currently plugged in.</summary>
    public static bool DeviceConnected(string udid)
    {
        if (string.IsNullOrWhiteSpace(udid)) return false;
        return ListUdids().Any(u => u.Equals(udid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The device's name (e.g. "Sam's iPhone"). Empty if not paired/trusted yet.</summary>
    public static string GetDeviceName(string udid)
    {
        EnsureLoaded();
        if (!_loadOk) return "";
        iDeviceHandle? dh = null;
        LockdownClientHandle? lh = null;
        try
        {
            var idevice = LibiMobileDevice.Instance.iDevice;
            var lockdown = LibiMobileDevice.Instance.Lockdown;
            if (idevice.idevice_new(out dh, udid) != iDeviceError.Success || dh == null) return "";
            // Handshake needs the user to have tapped "Trust" once; if not, we just
            // fall back to showing the UDID.
            if (lockdown.lockdownd_client_new_with_handshake(dh, out lh, "ZFTP") != LockdownError.Success || lh == null)
                return "";
            if (lockdown.lockdownd_get_device_name(lh, out string name) == LockdownError.Success)
                return name ?? "";
            return "";
        }
        catch { return ""; }
        finally
        {
            try { lh?.Dispose(); } catch { }
            try { dh?.Dispose(); } catch { }
        }
    }
}
