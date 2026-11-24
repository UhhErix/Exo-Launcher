using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

internal static class HdrBrightness
{
    internal const uint MinSdrWhiteRaw = 1000;
    internal const uint MaxSdrWhiteRaw = 6000;
    internal const uint SdrWhiteStep = 50;
    internal const int DefaultCursorFixNits = 220;
    internal const double CursorFixThresholdNits = 160.0;

    private const int GetSdrWhiteLevelType = 11;
    private const int SetSdrWhiteLevelType = unchecked((int)0xFFFFFFEE);
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;

    internal sealed record WriteSdrResult(bool Ok, int Status, bool Unsupported);

    internal static uint SliderToRaw(int slider) =>
        MinSdrWhiteRaw + (uint)Math.Clamp(slider, 0, 100) * SdrWhiteStep;

    internal static int RawToSlider(uint raw) =>
        Math.Clamp((int)Math.Round((raw - (double)MinSdrWhiteRaw) / SdrWhiteStep), 0, 100);

    internal static double RawToNits(uint raw) => raw / 1000.0 * 80.0;

    internal static uint NitsToRaw(double nits) =>
        (uint)Math.Clamp(Math.Round(nits * 1000.0 / 80.0), MinSdrWhiteRaw, MaxSdrWhiteRaw);

    internal static uint? ReadSdrWhiteRaw(WindowsHdr.Screen screen)
    {
        try
        {
            var request = new GetSdrWhiteLevel
            {
                header = new DeviceInfoHeader
                {
                    type = GetSdrWhiteLevelType,
                    size = (uint)Marshal.SizeOf<GetSdrWhiteLevel>(),
                    adapter = screen.Adapter,
                    id = screen.Target,
                },
            };
            return DisplayConfigGetDeviceInfo(ref request) == 0 && request.level > 0 ? request.level : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    internal static WriteSdrResult WriteSdrWhiteRaw(WindowsHdr.Screen screen, uint rawLevel)
    {
        if (HostMode.IsDisposable) return new WriteSdrResult(false, ErrorNotSupported, true);
        try
        {
            var clamped = Math.Clamp(rawLevel, MinSdrWhiteRaw, MaxSdrWhiteRaw);
            var request = new SetSdrWhiteLevel
            {
                header = new DeviceInfoHeader
                {
                    type = SetSdrWhiteLevelType,
                    size = (uint)Marshal.SizeOf<SetSdrWhiteLevel>(),
                    adapter = screen.Adapter,
                    id = screen.Target,
                },
                sdrWhiteLevel = clamped,
                finalValue = 1,
            };
            var status = DisplayConfigSetDeviceInfo(ref request);
            if (status == 0) return new WriteSdrResult(true, 0, false);
            var unsupported = status is ErrorNotSupported or ErrorInvalidParameter or ErrorInvalidFunction;
            return new WriteSdrResult(false, status, unsupported);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new WriteSdrResult(false, ErrorNotSupported, true);
        }
    }

    internal static int? ReadPanelBrightness()
    {
        object? locator = null;
        object? service = null;
        object? items = null;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type is null) return null;
            locator = Activator.CreateInstance(type);
            if (locator is null) return null;
            service = type.InvokeMember("ConnectServer", BindingFlags.InvokeMethod, null, locator, [".", @"root\wmi"]);
            if (service is null) return null;
            items = service.GetType().InvokeMember("ExecQuery", BindingFlags.InvokeMethod, null, service,
                ["SELECT CurrentBrightness FROM WmiMonitorBrightness"]);
            if (items is not IEnumerable enumerable) return null;
            foreach (var item in enumerable)
            {
                if (item is null) continue;
                try
                {
                    var val = item.GetType().InvokeMember("CurrentBrightness", BindingFlags.GetProperty, null, item, null);
                    if (val is not null) return Math.Clamp(Convert.ToInt32(val), 0, 100);
                }
                finally
                {
                    ReleaseCom(item);
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Debug("HDR brightness read: " + ex.GetType().Name);
            return null;
        }
        finally
        {
            ReleaseCom(items);
            ReleaseCom(service);
            ReleaseCom(locator);
        }
    }

    internal static bool WritePanelBrightness(int level)
    {
        if (HostMode.IsDisposable) return false;
        object? locator = null;
        object? service = null;
        object? items = null;
        try
        {
            var clamped = (byte)Math.Clamp(level, 0, 100);
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type is null) return false;
            locator = Activator.CreateInstance(type);
            if (locator is null) return false;
            service = type.InvokeMember("ConnectServer", BindingFlags.InvokeMethod, null, locator, [".", @"root\wmi"]);
            if (service is null) return false;
            items = service.GetType().InvokeMember("ExecQuery", BindingFlags.InvokeMethod, null, service,
                ["SELECT * FROM WmiMonitorBrightnessMethods"]);
            if (items is not IEnumerable enumerable) return false;
            var wrote = false;
            foreach (var item in enumerable)
            {
                if (item is null) continue;
                try
                {
                    item.GetType().InvokeMember("WmiSetBrightness", BindingFlags.InvokeMethod, null, item, [0u, clamped]);
                    wrote = true;
                }
                finally
                {
                    ReleaseCom(item);
                }
            }
            return wrote;
        }
        catch (Exception ex)
        {
            AppLog.Debug("HDR brightness write: " + ex.GetType().Name);
            return false;
        }
        finally
        {
            ReleaseCom(items);
            ReleaseCom(service);
            ReleaseCom(locator);
        }
    }

    private static void ReleaseCom(object? obj)
    {
        if (obj is not null && Marshal.IsComObject(obj))
            try { Marshal.ReleaseComObject(obj); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int type;
        public uint size;
        public WindowsHdr.Luid adapter;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GetSdrWhiteLevel
    {
        public DeviceInfoHeader header;
        public uint level;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SetSdrWhiteLevel
    {
        public DeviceInfoHeader header;
        public uint sdrWhiteLevel;
        public byte finalValue;
    }

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref GetSdrWhiteLevel request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref SetSdrWhiteLevel request);
}
