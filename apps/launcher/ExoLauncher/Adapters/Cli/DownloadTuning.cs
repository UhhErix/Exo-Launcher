using System.Runtime.InteropServices;

namespace ExoLauncher.Adapters.Cli;

/// <summary>
/// Download options for Legendary, gogdl and Nile sized to this PC: their own defaults ignore free memory (Legendary
/// commits 2 GiB of shared memory and starts min(2 x threads, 16) worker processes; gogdl and Nile one thread per CPU
/// thread). Values stay inside each tool's documented range.
/// </summary>
internal static class DownloadTuning
{
    internal const int MinWorkers = 2, MaxWorkers = 16;
    internal const int MinSharedMemoryMiB = 512, MaxSharedMemoryMiB = 2048;

    /// <summary>One worker per CPU thread, at most 16 (Legendary's own cap), and one per 128 MiB of free memory.</summary>
    internal static int Workers(int logicalThreads, long availableMiB) =>
        Math.Clamp((int)Math.Min(logicalThreads, availableMiB / 128), MinWorkers, MaxWorkers);

    /// <summary>
    /// Legendary's shared memory pool in MiB: an eighth of free memory in 64 MiB steps, between 512 MiB and its own
    /// 2 GiB default. A title that needs more fails Legendary's analysis before any download, and Exo then reruns it
    /// on Legendary's default (<see cref="LegendaryCli.SharedMemoryTooSmall"/>).
    /// </summary>
    internal static int LegendarySharedMemoryMiB(long availableMiB) =>
        (int)Math.Clamp(availableMiB / 8 / 64 * 64, MinSharedMemoryMiB, MaxSharedMemoryMiB);

    internal static int Workers() => Workers(Environment.ProcessorCount, AvailableMiB());

    internal static int LegendarySharedMemoryMiB() => LegendarySharedMemoryMiB(AvailableMiB());

    /// <summary>Free physical memory in MiB; on a failed read, enough that the CPU decides.</summary>
    private static long AvailableMiB()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (long)(status.AvailPhys / (1024 * 1024)) : 8192;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}

/// <summary>
/// CDN hosts ranked by one measured probe each: fastest first, failed probes left out.
/// </summary>
internal static class CdnRanking
{
    internal static IReadOnlyList<string> Rank(IEnumerable<(string Host, TimeSpan? Elapsed)> probes) =>
        probes.Where(probe => probe.Elapsed is not null && !string.IsNullOrWhiteSpace(probe.Host))
            .GroupBy(probe => probe.Host, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Host: group.Key, Elapsed: group.Min(probe => probe.Elapsed!.Value)))
            .OrderBy(probe => probe.Elapsed)
            .Select(probe => probe.Host)
            .ToArray();
}

/// <summary>The last ranking, trusted for <paramref name="lifetime"/>; an empty ranking is never kept.</summary>
internal sealed class CdnRankingCache(TimeSpan lifetime, Func<DateTimeOffset> clock)
{
    private readonly Lock _gate = new();
    private IReadOnlyList<string> _ranking = [];
    private DateTimeOffset _measured;

    internal string? Best()
    {
        lock (_gate)
            return _ranking.Count > 0 && clock() - _measured < lifetime ? _ranking[0] : null;
    }

    internal void Store(IReadOnlyList<string> ranking)
    {
        if (ranking.Count == 0) return;
        lock (_gate)
        {
            _ranking = ranking;
            _measured = clock();
        }
    }
}
