using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// The graphics card's use, temperature and memory in use, from Windows' own graphics statistics: what Task Manager
/// reads, the same on any maker's card. No vendor library is loaded and the driver's control interfaces, which Exo leaves
/// alone while a game runs, aren't touched (<see cref="NvidiaSensors"/> maps 21 MB of NVML's for the same numbers). It exists
/// only while the FPS counter shows one of these, on the card with the most memory of its own, and reads only what is asked
/// for. An engine's query is the one that costs (165 us on the RTX 3070, which has 14 engines; temperature 3 us, memory under 1 us), so
/// between full scans it asks only the busiest engine. Layouts: d3dkmthk.h (D3DKMT_QUERYSTATISTICS, D3DKMT_ADAPTER_PERFDATA),
/// offsets read on the RTX 3070 against nvidia-smi.
/// </summary>
internal sealed unsafe class GpuStats : IDisposable
{
    private const int StatisticsSize = 0x328, ResultAt = 24, InputAt = 0x320, FullScanEvery = 16;
    private const int TypeAdapter = 0, TypeSegment = 3, TypeNode = 5;
    private const int QueryAdapterPerfData = 62;
    private const int Temperature = 56, PerfDataSize = 64;

    private readonly uint _adapter;
    private readonly long _luid;
    private readonly uint _segments, _nodes;
    private readonly long[] _running, _at;
    private uint _busiest;
    private int _reads;

    private GpuStats(uint adapter, long luid, uint segments, uint nodes)
    {
        (_adapter, _luid, _segments, _nodes) = (adapter, luid, segments, nodes);
        (_running, _at) = (new long[nodes], new long[nodes]);
    }

    /// <summary>The card with the most memory of its own; null where Windows names none.</summary>
    internal static GpuStats? Open()
    {
        var infos = stackalloc AdapterInfo[16];
        var list = new EnumAdapters { Count = 16, Adapters = infos };
        if (D3DKMTEnumAdapters2(&list) != 0) return null;
        // One buffer for every query: each query fills it before anything reads it.
        var stats = stackalloc byte[StatisticsSize];
        (uint Handle, long Luid, uint Segments, uint Nodes, ulong Memory)? best = null;
        for (var i = 0; i < list.Count; i++)
        {
            var (handle, luid) = (infos[i].Handle, infos[i].Luid);
            var memory = 0ul;
            uint segments = 0, nodes = 0;
            if (Query(stats, TypeAdapter, luid, 0))
            {
                (segments, nodes) = (*(uint*)(stats + ResultAt), *(uint*)(stats + ResultAt + 4));
                for (uint segment = 0; segment < segments; segment++)
                    if (Query(stats, TypeSegment, luid, segment) && Dedicated(stats)) memory += *(ulong*)(stats + ResultAt);
            }
            if (best is null || memory > best.Value.Memory)
            {
                if (best is { } old) Close(old.Handle);
                best = (handle, luid, segments, nodes, memory);
            }
            else Close(handle);
        }
        return best is { Nodes: > 0 } card ? new GpuStats(card.Handle, card.Luid, card.Segments, card.Nodes) : null;
    }

    /// <summary>A segment of the card's own memory: Aperture 0, the others being system memory it maps.</summary>
    private static bool Dedicated(byte* stats) => *(uint*)(stats + ResultAt + 40) == 0;

    /// <summary>
    /// Only what is asked for. Use: its busiest engine, as Task Manager's headline; null on the first read, which has nothing
    /// to compare with. Temperature in degrees. Memory: what is held in the card's own memory.
    /// </summary>
    internal (int? Use, int? Temperature, long? MemoryBytes) Read(bool use, bool temperature, bool memory)
    {
        var stats = stackalloc byte[StatisticsSize];
        return (use ? ReadUse(stats) : null, temperature ? ReadTemperature() : null, memory ? ReadMemory(stats) : null);
    }

    private int? ReadUse(byte* stats)
    {
        var first = _reads++ == 0;
        var scan = first || _reads <= 2 || _reads % FullScanEvery == 0;
        var busiest = 0.0;
        var now = Stopwatch.GetTimestamp();
        for (uint node = scan ? 0 : _busiest; node < (scan ? _nodes : _busiest + 1); node++)
        {
            if (!Query(stats, TypeNode, _luid, node)) continue;
            var running = *(long*)(stats + ResultAt);
            var seconds = (now - _at[node]) / (double)Stopwatch.Frequency;
            var busy = !first && seconds > 0 ? (running - _running[node]) / 1e7 / seconds : 0;
            if (busy > busiest)
            {
                busiest = busy;
                _busiest = node;
            }
            (_running[node], _at[node]) = (running, now);
        }
        return first ? null : (int)Math.Round(Math.Clamp(busiest, 0, 1) * 100);
    }

    private long? ReadMemory(byte* stats)
    {
        long? memory = null;
        for (uint segment = 0; segment < _segments; segment++)
            if (Query(stats, TypeSegment, _luid, segment) && Dedicated(stats)) memory = (memory ?? 0) + *(long*)(stats + ResultAt + 16);
        return memory;
    }

    /// <summary>Tenths of a degree from the adapter; a card that doesn't report it answers 0.</summary>
    private int? ReadTemperature()
    {
        var perf = stackalloc byte[PerfDataSize];
        new Span<byte>(perf, PerfDataSize).Clear();
        var query = new QueryAdapterInfo { Adapter = _adapter, Type = QueryAdapterPerfData, Data = perf, Size = PerfDataSize };
        if (D3DKMTQueryAdapterInfo(&query) != 0) return null;
        var degrees = *(uint*)(perf + Temperature) / 10;
        return degrees is > 0 and < 150 ? (int)degrees : null;
    }

    private static bool Query(byte* stats, int type, long luid, uint input)
    {
        new Span<byte>(stats, StatisticsSize).Clear();
        *(int*)stats = type;
        *(long*)(stats + 4) = luid;
        *(uint*)(stats + InputAt) = input;
        return D3DKMTQueryStatistics(stats) == 0;
    }

    public void Dispose() => Close(_adapter);

    private static void Close(uint handle) => D3DKMTCloseAdapter(&handle);

    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct AdapterInfo { public uint Handle; public long Luid; public uint Sources; public int PreciseRegions; }
    [StructLayout(LayoutKind.Sequential)] private struct EnumAdapters { public uint Count; public AdapterInfo* Adapters; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryAdapterInfo { public uint Adapter; public int Type; public void* Data; public uint Size; }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(EnumAdapters* list);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(uint* handle);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryStatistics(byte* statistics);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(QueryAdapterInfo* query);
}
