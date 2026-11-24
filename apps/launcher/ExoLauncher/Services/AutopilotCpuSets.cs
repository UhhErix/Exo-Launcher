using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal sealed record CpuSetInfo(uint Id, ushort Group, byte CoreIndex, byte LastLevelCacheIndex, byte EfficiencyClass, byte LogicalProcessorIndex);

internal static class AutopilotCpuSets
{
    internal static uint[] BackgroundSetIds(IReadOnlyList<CpuSetInfo> sets, PowerProfiles.CpuClass cpuClass, CcdLayout? ccd = null)
    {
        if (sets.Count < 6) return [];
        var group0 = sets.Where(s => s.Group == 0).ToList();
        if (group0.Count < 6) return [];

        var maxEff = group0.Max(s => s.EfficiencyClass);
        var minEff = group0.Min(s => s.EfficiencyClass);
        if (cpuClass == PowerProfiles.CpuClass.IntelHybrid || maxEff > minEff)
        {
            var eCores = group0.Where(s => s.EfficiencyClass < maxEff).Select(s => s.Id).ToArray();
            if (eCores.Length > 0) return eCores;
        }

        var cacheGroups = group0.GroupBy(s => s.LastLevelCacheIndex).OrderBy(g => g.Key).ToList();
        if (cpuClass == PowerProfiles.CpuClass.AmdDualCcdX3d && ccd is { VCache: true } && cacheGroups.Count >= 2)
        {
            var nonVCache = group0
                .Where(s => s.LogicalProcessorIndex < 64 && ((ccd.VCacheMask >> s.LogicalProcessorIndex) & 1UL) == 0)
                .Select(s => s.Id)
                .ToArray();
            if (nonVCache.Length > 0) return nonVCache;
        }

        if (cpuClass is PowerProfiles.CpuClass.AmdDualCcdX3d or PowerProfiles.CpuClass.AmdDualCcdZen5 or PowerProfiles.CpuClass.AmdDualCcd && cacheGroups.Count >= 2)
        {
            var secondary = cacheGroups[^1].Select(s => s.Id).ToArray();
            if (secondary.Length > 0) return secondary;
        }

        var nonZero = group0
            .Where(s => s.CoreIndex != 0 && s.LogicalProcessorIndex != 0)
            .OrderBy(s => s.LogicalProcessorIndex)
            .ToList();
        if (nonZero.Count < 2) return [];
        var take = Math.Clamp(group0.Count / 4, 2, nonZero.Count);
        return [.. nonZero.TakeLast(take).Select(s => s.Id)];
    }

    internal static string? DescribePlan(IReadOnlyList<CpuSetInfo> sets, PowerProfiles.CpuClass cpuClass, CcdLayout? ccd = null)
    {
        var ids = BackgroundSetIds(sets, cpuClass, ccd);
        if (ids.Length == 0) return null;
        var group0 = sets.Where(s => s.Group == 0).ToList();
        if (group0.Count == 0) return null;
        var maxEff = group0.Max(s => s.EfficiencyClass);
        var minEff = group0.Min(s => s.EfficiencyClass);
        if (cpuClass == PowerProfiles.CpuClass.IntelHybrid || maxEff > minEff)
            return $"Background hogs run on {ids.Length} efficiency threads via CPU Sets, keeping P-cores clear.";
        if (cpuClass == PowerProfiles.CpuClass.AmdDualCcdX3d && ccd is { VCache: true })
            return $"Background hogs run on {ids.Length} non-V-Cache threads via CPU Sets, leaving the 3D V-Cache CCD for AMD's driver.";
        if (cpuClass is PowerProfiles.CpuClass.AmdDualCcdZen5 or PowerProfiles.CpuClass.AmdDualCcd && group0.Select(s => s.LastLevelCacheIndex).Distinct().Count() >= 2)
            return $"Background hogs run on {ids.Length} secondary-CCD threads via CPU Sets.";
        return $"Background hogs run on {ids.Length} trailing threads via CPU Sets, off Core 0.";
    }

    internal static IReadOnlyList<CpuSetInfo> ReadSystemCpuSets()
    {
        try
        {
            uint needed = 0;
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out needed, IntPtr.Zero, 0);
            if (needed == 0 || needed > 1024 * 1024) return [];
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, needed, out var returned, IntPtr.Zero, 0) || returned == 0) return [];
                var list = new List<CpuSetInfo>();
                var offset = 0;
                while (offset + 32 <= returned)
                {
                    var size = Marshal.ReadInt32(buffer, offset);
                    if (size < 32 || offset + size > returned) break;
                    var type = Marshal.ReadInt32(buffer, offset + 4);
                    if (type == 0)
                    {
                        var id = unchecked((uint)Marshal.ReadInt32(buffer, offset + 8));
                        var group = unchecked((ushort)Marshal.ReadInt16(buffer, offset + 12));
                        var logical = Marshal.ReadByte(buffer, offset + 14);
                        var core = Marshal.ReadByte(buffer, offset + 15);
                        var llc = Marshal.ReadByte(buffer, offset + 16);
                        var eff = Marshal.ReadByte(buffer, offset + 18);
                        list.Add(new(id, group, core, llc, eff, logical));
                    }
                    offset += size;
                }
                return list;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    internal static uint[]? GetDefaultCpuSets(nint processHandle)
    {
        try
        {
            if (!GetProcessDefaultCpuSets(processHandle, null, 0, out var required))
            {
                var err = Marshal.GetLastWin32Error();
                if (err != 122) return null;
            }
            if (required == 0) return [];
            var ids = new uint[required];
            return GetProcessDefaultCpuSets(processHandle, ids, required, out _) ? ids : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    internal static bool SetDefaultCpuSets(nint processHandle, uint[]? ids)
    {
        if (HostMode.IsDisposable) return false;
        try
        {
            var count = ids is { Length: > 0 } ? (uint)ids.Length : 0u;
            return SetProcessDefaultCpuSets(processHandle, count == 0 ? null : ids, count);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static bool PinCurrentThreadToBackgroundCores()
    {
        if (HostMode.IsDisposable) return false;
        try
        {
            var sets = ReadSystemCpuSets();
            var ids = BackgroundSetIds(sets, PowerProfiles.CpuClass.Unknown);
            if (ids.Length == 0) return false;
            return SetThreadSelectedCpuSets(GetCurrentThread(), ids, (uint)ids.Length);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadSelectedCpuSets(nint thread, [In] uint[]? cpuSetIds, uint cpuSetIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessDefaultCpuSets(nint process, [Out] uint[]? cpuSetIds, uint cpuSetIdCount, out uint requiredIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultCpuSets(nint process, [In] uint[]? cpuSetIds, uint cpuSetIdCount);
}
