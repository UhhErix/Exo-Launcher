using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Wi-Fi while a game runs, with no setting to turn: Windows' background scans stopped and media streaming mode on, through
/// the documented Wlan API (WlanSetInterface, wlan_intf_opcode_background_scan_enabled false and
/// wlan_intf_opcode_media_streaming_mode true). Microsoft: each scan leaves a gap of "100 milliseconds or more" in what the
/// adapter receives, and streaming mode stops the driver's own scans (.claude/research/network-tuning.md). On battery the
/// active power plan's Wi-Fi power saving also goes to Maximum Performance (on mains it already is). Set again every 30 s
/// while a game runs, since a reconnect clears the Wlan settings, and put back when the last game closes, with the
/// handle Exo held. A PC without Wi-Fi, or a driver that refuses, is left as it is. Fed by the run-state poll
/// (<see cref="GameRunningSignal"/>).
/// </summary>
internal static class WifiGameMode
{
    private const uint ClientVersion = 2, BackgroundScanEnabled = 2, MediaStreamingMode = 3, Connected = 1;
    private const int InterfaceInfoSize = 16 + 512 + 4; // GUID, WCHAR[256], WLAN_INTERFACE_STATE
    private static readonly Guid WirelessSubgroup = new("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");
    private static readonly Guid PowerSavingMode = new("12bbebe6-58d6-4636-95bb-3217ef867c1a");
    private static readonly Lock Gate = new();
    private static IntPtr _handle;
    private static DateTime _applied;
    private static (Guid Scheme, uint Was)? _battery;

    internal static void Hold(bool gaming)
    {
        lock (Gate)
        {
            try
            {
                if (gaming)
                {
                    // Once per 30 s whatever the last try did: a PC without Wi-Fi (no Wlan service) isn't asked every 5 s.
                    if (DateTime.UtcNow - _applied < TimeSpan.FromSeconds(30)) return;
                    Apply();
                }
                else
                {
                    _applied = default;
                    if (_handle != IntPtr.Zero || _battery is not null) Release();
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* no Wlan service on this PC */ }
        }
    }

    private static void Apply()
    {
        _applied = DateTime.UtcNow;
        if (_handle == IntPtr.Zero && WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out _handle) != 0) _handle = IntPtr.Zero;
        var set = 0;
        if (_handle != IntPtr.Zero)
        {
            foreach (var adapter in ConnectedInterfaces())
            {
                if (Set(adapter, BackgroundScanEnabled, false) && Set(adapter, MediaStreamingMode, true)) set++;
            }
        }
        var power = new PcHealth.NativePower();
        if (_battery is null && power.Battery() && power.Active() is { } scheme
            && PowerReadDCValueIndex(IntPtr.Zero, in scheme, in WirelessSubgroup, in PowerSavingMode, out var was) == 0 && was != 0
            && PowerWriteDCValueIndex(IntPtr.Zero, in scheme, in WirelessSubgroup, in PowerSavingMode, 0) == 0)
        {
            _battery = (scheme, was);
            PowerSetActiveScheme(IntPtr.Zero, in scheme);
        }
        if (set > 0 || _battery is not null) AppLog.Debug($"Wi-Fi game mode: scans paused on {set} adapter(s){(_battery is not null ? ", full power on battery" : "")}.");
    }

    private static void Release()
    {
        if (_handle != IntPtr.Zero)
        {
            foreach (var adapter in ConnectedInterfaces())
            {
                Set(adapter, MediaStreamingMode, false);
                Set(adapter, BackgroundScanEnabled, true);
            }
            WlanCloseHandle(_handle, IntPtr.Zero);
            _handle = IntPtr.Zero;
        }
        if (_battery is { } battery)
        {
            PowerWriteDCValueIndex(IntPtr.Zero, in battery.Scheme, in WirelessSubgroup, in PowerSavingMode, battery.Was);
            PowerSetActiveScheme(IntPtr.Zero, in battery.Scheme);
            _battery = null;
        }
        AppLog.Debug("Wi-Fi game mode: put back.");
    }

    /// <summary>The Wi-Fi adapters that are connected now (WLAN_INTERFACE_INFO_LIST: two DWORDs, then each interface).</summary>
    private static List<Guid> ConnectedInterfaces()
    {
        var found = new List<Guid>();
        if (WlanEnumInterfaces(_handle, IntPtr.Zero, out var list) != 0 || list == IntPtr.Zero) return found;
        try
        {
            var count = Marshal.ReadInt32(list);
            for (var i = 0; i < count; i++)
            {
                var item = list + 8 + i * InterfaceInfoSize;
                if ((uint)Marshal.ReadInt32(item + 16 + 512) == Connected) found.Add(Marshal.PtrToStructure<Guid>(item));
            }
        }
        finally { WlanFreeMemory(list); }
        return found;
    }

    /// <summary>
    /// The channel the first connected Wi-Fi adapter is on (wlan_intf_opcode_channel_number); null off Wi-Fi or when Windows doesn't say. Its own short-lived
    /// handle: this one only reads, and never asks for the scan results that Windows ties to location permission.
    /// </summary>
    internal static int? Channel()
    {
        const uint channelNumber = 8;
        try
        {
            if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var handle) != 0) return null;
            try
            {
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0 || list == IntPtr.Zero) return null;
                try
                {
                    for (var i = 0; i < Marshal.ReadInt32(list); i++)
                    {
                        var item = list + 8 + i * InterfaceInfoSize;
                        if ((uint)Marshal.ReadInt32(item + 16 + 512) != Connected) continue;
                        var adapter = Marshal.PtrToStructure<Guid>(item);
                        if (WlanQueryInterface(handle, in adapter, channelNumber, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0 || data == IntPtr.Zero) continue;
                        try { return Marshal.ReadInt32(data); }
                        finally { WlanFreeMemory(data); }
                    }
                    return null;
                }
                finally { WlanFreeMemory(list); }
            }
            finally { WlanCloseHandle(handle, IntPtr.Zero); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private static bool Set(Guid adapter, uint opcode, bool on)
    {
        var value = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(value, on ? 1 : 0);
            return WlanSetInterface(_handle, in adapter, opcode, 4, value, IntPtr.Zero) == 0;
        }
        finally { Marshal.FreeHGlobal(value); }
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr handle, in Guid adapter, uint opcode, IntPtr reserved, out uint size, out IntPtr data, IntPtr valueType);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanSetInterface(IntPtr handle, in Guid adapter, uint opcode, uint size, IntPtr data, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr root, in Guid scheme, in Guid subgroup, in Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr root, in Guid scheme, in Guid subgroup, in Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr root, in Guid scheme);
}
