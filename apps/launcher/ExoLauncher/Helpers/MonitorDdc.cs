using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// Reads and sets monitor hardware brightness and contrast over DDC/CI (<c>dxva2.dll</c>).
/// Monitors or connections that do not support DDC/CI (most laptop eDP panels, docks that block MCCS, or
/// monitors with DDC/CI off in the OSD) return <c>null</c> from <see cref="Read"/> so Exo hides those sliders.
/// </summary>
internal static class MonitorDdc
{
    /// <summary>Hardware MCCS levels reported by a monitor over DDC/CI; channels the monitor refuses stay null.</summary>
    internal sealed record Levels(
        int? Brightness, int BrightnessMin, int BrightnessMax,
        int? Contrast, int ContrastMin, int ContrastMax);

    /// <summary>Clamps a requested MCCS level to the monitor's reported <paramref name="min"/>.. <paramref name="max"/> bounds.</summary>
    internal static uint ClampLevel(int value, int min, int max)
    {
        if (max <= min) return (uint)Math.Max(0, min);
        return (uint)Math.Clamp(value, min, max);
    }

    /// <summary>Reads hardware brightness and contrast for <paramref name="gdiName"/>; null when DDC/CI is unavailable.</summary>
    internal static Levels? Read(string gdiName)
    {
        if (string.IsNullOrWhiteSpace(gdiName)) return null;
        try
        {
            return WithPhysicalMonitors(gdiName, monitors =>
            {
                foreach (var monitor in monitors)
                {
                    int? brightness = null;
                    int bMin = 0, bMax = 100;
                    if (GetMonitorBrightness(monitor.hPhysicalMonitor, out var minB, out var curB, out var maxB) && maxB > minB)
                    {
                        bMin = (int)minB;
                        bMax = (int)maxB;
                        brightness = (int)ClampLevel((int)curB, bMin, bMax);
                    }

                    int? contrast = null;
                    int cMin = 0, cMax = 100;
                    if (GetMonitorContrast(monitor.hPhysicalMonitor, out var minC, out var curC, out var maxC) && maxC > minC)
                    {
                        cMin = (int)minC;
                        cMax = (int)maxC;
                        contrast = (int)ClampLevel((int)curC, cMin, cMax);
                    }

                    if (brightness is not null || contrast is not null)
                        return new Levels(brightness, bMin, bMax, contrast, cMin, cMax);
                }
                return null;
            });
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            AppLog.Debug("DDC/CI read skipped: " + ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Sets monitor hardware brightness (MCCS 0x10) over DDC/CI; returns an error line or null on success.</summary>
    internal static string? SetBrightness(string gdiName, int value)
    {
        if (HostMode.IsDisposable) return HostMode.Refusal;
        if (string.IsNullOrWhiteSpace(gdiName)) return "That screen is not connected.";
        try
        {
            var result = WithPhysicalMonitors(gdiName, monitors =>
            {
                foreach (var monitor in monitors)
                {
                    if (!GetMonitorBrightness(monitor.hPhysicalMonitor, out var minB, out _, out var maxB) || maxB <= minB)
                        continue;
                    var target = ClampLevel(value, (int)minB, (int)maxB);
                    if (SetMonitorBrightness(monitor.hPhysicalMonitor, target)) return (string?)null;
                    return "The monitor did not change its hardware brightness.";
                }
                return "This monitor does not support DDC/CI brightness control.";
            });
            return result ?? "This monitor does not support DDC/CI brightness control.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return "Windows could not reach DDC/CI for this monitor.";
        }
    }

    /// <summary>Sets monitor hardware contrast (MCCS 0x12) over DDC/CI; returns an error line or null on success.</summary>
    internal static string? SetContrast(string gdiName, int value)
    {
        if (HostMode.IsDisposable) return HostMode.Refusal;
        if (string.IsNullOrWhiteSpace(gdiName)) return "That screen is not connected.";
        try
        {
            var result = WithPhysicalMonitors(gdiName, monitors =>
            {
                foreach (var monitor in monitors)
                {
                    if (!GetMonitorContrast(monitor.hPhysicalMonitor, out var minC, out _, out var maxC) || maxC <= minC)
                        continue;
                    var target = ClampLevel(value, (int)minC, (int)maxC);
                    if (SetMonitorContrast(monitor.hPhysicalMonitor, target)) return (string?)null;
                    return "The monitor did not change its hardware contrast.";
                }
                return "This monitor does not support DDC/CI contrast control.";
            });
            return result ?? "This monitor does not support DDC/CI contrast control.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return "Windows could not reach DDC/CI for this monitor.";
        }
    }

    private static T? WithPhysicalMonitors<T>(string gdiName, Func<PhysicalMonitor[], T?> use) where T : class
    {
        var hMonitor = FindHMonitor(gdiName);
        if (hMonitor == IntPtr.Zero) return null;
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0) return null;
        var monitors = new PhysicalMonitor[count];
        if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors)) return null;
        try
        {
            return use(monitors);
        }
        finally
        {
            DestroyPhysicalMonitors(count, monitors);
        }
    }

    private static IntPtr FindHMonitor(string gdiName)
    {
        var found = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfoW(hMonitor, ref info) && string.Equals(info.szDevice, gdiName, StringComparison.OrdinalIgnoreCase))
            {
                found = hMonitor;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public int left, top, right, bottom;
        public int workLeft, workTop, workRight, workBottom;
        public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PhysicalMonitor[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PhysicalMonitor[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorContrast(IntPtr hMonitor, out uint pdwMinimumContrast, out uint pdwCurrentContrast, out uint pdwMaximumContrast);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorContrast(IntPtr hMonitor, uint dwNewContrast);
}
