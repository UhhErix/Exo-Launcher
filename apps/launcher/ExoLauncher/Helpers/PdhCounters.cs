using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// A few of Windows' performance counters in one PDH query, by their English names (so any Windows language works), read the
/// way Performance Monitor and Task Manager read them: no administrator rights, no driver. A counter this PC doesn't have (no
/// ACPI thermal zone, an older GPU driver without GPU Engine) is left out and reads as null; the others still work.
/// <see cref="Collect"/> takes a sample; a rate (% DPC Time, Utilization Percentage) needs two, so the first answer is null.
/// </summary>
internal sealed class PdhCounters : IDisposable
{
    private readonly nint _query;
    private readonly Dictionary<string, nint> _counters = new(StringComparer.Ordinal);
    private bool _disposed;

    private PdhCounters(nint query) => _query = query;

    /// <summary>A query of the counters Windows has among <paramref name="paths"/>; null when PDH won't open one at all.</summary>
    internal static PdhCounters? Open(IEnumerable<string> paths)
    {
        if (PdhOpenQuery(null, 0, out var query) != 0) return null;
        var counters = new PdhCounters(query);
        foreach (var path in paths)
            if (PdhAddEnglishCounter(query, path, 0, out var counter) == 0) counters._counters[path] = counter;
        if (counters._counters.Count == 0)
        {
            counters.Dispose();
            return null;
        }
        return counters;
    }

    internal bool Has(string path) => _counters.ContainsKey(path);

    /// <summary>One sample of every counter; false when PDH didn't take it.</summary>
    internal bool Collect() => !_disposed && PdhCollectQueryData(_query) == 0;

    /// <summary>A single-instance counter's value (a _Total, or a counter with no instances); null when it has none yet.</summary>
    internal double? Value(string path)
    {
        if (!_counters.TryGetValue(path, out var counter)) return null;
        return PdhGetFormattedCounterValue(counter, Format, out _, out var value) == 0 && value.Status <= 1 ? value.Double : null;
    }

    /// <summary>A wildcard counter's values by instance name (_Total left out); null when it has none yet.</summary>
    internal Dictionary<string, double>? Values(string path)
    {
        if (!_counters.TryGetValue(path, out var counter)) return null;
        uint size = 0;
        if (PdhGetFormattedCounterArray(counter, Format, ref size, out _, 0) != MoreData || size == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, Format, ref size, out var count, buffer) != 0) return null;
            var values = new Dictionary<string, double>((int)count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count; i++)
            {
                // PDH_FMT_COUNTERVALUE_ITEM_W (x64): the name pointer, then CStatus and the double 8 bytes on.
                var item = buffer + i * 24;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                if (name is null || name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                var status = (uint)Marshal.ReadInt32(item, 8);
                if (status > 1) continue;
                var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                // Two instances can share a name (two zones, two engines): they add up, as Task Manager sums a process's engines of one kind.
                values[name] = values.TryGetValue(name, out var before) ? before + value : value;
            }
            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PdhCloseQuery(_query);
    }

    private const uint Format = 0x200 | 0x8000; // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100
    private const uint MoreData = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct FormattedValue { public uint Status; public uint Padding; public double Double; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, nint userData, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out FormattedValue value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, out uint count, nint buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
}
