using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal sealed record GpuAdapterInfo(
    long Luid,
    string LuidKey,
    string Name,
    uint VendorId,
    long DedicatedBytes,
    long SharedBytes,
    bool Software);

internal sealed record GpuEngineSlice(string Type, double Percent);

internal sealed record GpuProcessEntry(
    int Pid,
    string Name,
    long AdapterLuid,
    string AdapterLuidKey,
    string AdapterName,
    double TotalPercent,
    IReadOnlyList<GpuEngineSlice> Engines,
    long LocalBytes,
    long NonLocalBytes,
    string? ProtectedReason);

internal sealed record GpuUsageAnswer(
    string Summary,
    GpuProcessEntry? TopGpu,
    GpuProcessEntry? TopVram,
    IReadOnlyList<GpuProcessEntry> Hogs);

internal sealed record GpuProcessSnapshot(
    IReadOnlyList<GpuAdapterInfo> Adapters,
    IReadOnlyList<GpuProcessEntry> Processes,
    GpuUsageAnswer Answer,
    long SampledAtMs);

internal sealed partial class GpuProcessMonitor : IDisposable
{
    internal const string EnginePath = @"\GPU Engine(*)\Utilization Percentage";
    internal const string LocalMemPath = @"\GPU Process Memory(*)\Local Usage";
    internal const string NonLocalMemPath = @"\GPU Process Memory(*)\Non-Local Usage";

    private const long CacheWindowMs = 750;
    private const long StaleBaselineMs = 10_000;
    private const int WarmupDelayMs = 150;
    private const long TopVramThresholdBytes = 16L << 20;
    private const long NotableVramHogBytes = 512L << 20;

    internal static GpuProcessMonitor Shared { get; } = new();

    private readonly Lock _gate = new();
    private PdhCounters? _pdh;
    private bool _pdhFailed;
    private long _lastCollectAtMs;
    private IReadOnlyList<GpuAdapterInfo>? _adapters;
    private GpuProcessSnapshot? _cached;

    internal GpuProcessSnapshot Sample(bool allowWarmup = true, int maxProcesses = 25)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (_cached is { } cached && now - cached.SampledAtMs < CacheWindowMs && cached.Processes.Count <= maxProcesses)
                return cached;

            var adapters = _adapters ??= ReadAdapters();
            if (!_pdhFailed && _pdh is null)
            {
                _pdh = PdhCounters.Open([EnginePath, LocalMemPath, NonLocalMemPath]);
                if (_pdh is null) _pdhFailed = true;
            }

            if (_pdh is null)
            {
                var empty = new GpuProcessSnapshot(adapters, [], Summarize([], Environment.ProcessId), now);
                _cached = empty;
                return empty;
            }

            var needsBaseline = _lastCollectAtMs == 0 || now - _lastCollectAtMs > StaleBaselineMs;
            if (!_pdh.Collect())
            {
                var empty = new GpuProcessSnapshot(adapters, [], Summarize([], Environment.ProcessId), now);
                _cached = empty;
                return empty;
            }

            if (needsBaseline && allowWarmup)
            {
                Thread.Sleep(WarmupDelayMs);
                _pdh.Collect();
            }

            _lastCollectAtMs = Environment.TickCount64;
            var engineValues = _pdh.Values(EnginePath);
            var localValues = _pdh.Values(LocalMemPath);
            var nonLocalValues = _pdh.Values(NonLocalMemPath);

            var names = ReadProcessNames();
            var snapshot = Aggregate(engineValues, localValues, nonLocalValues, adapters, names, maxProcesses, Environment.ProcessId, _lastCollectAtMs);
            if (HasUnknownAdapter(snapshot.Processes, adapters))
            {
                adapters = _adapters = ReadAdapters();
                snapshot = Aggregate(engineValues, localValues, nonLocalValues, adapters, names, maxProcesses, Environment.ProcessId, _lastCollectAtMs);
            }
            _cached = snapshot;
            return snapshot;
        }
    }

    internal IReadOnlyDictionary<int, GpuProcessLoad> SampleForAutopilot()
    {
        var snapshot = Sample(allowWarmup: false, maxProcesses: 128);
        var byPid = new Dictionary<int, GpuProcessLoad>(snapshot.Processes.Count);
        foreach (var entry in snapshot.Processes)
        {
            if (byPid.TryGetValue(entry.Pid, out var existing))
            {
                byPid[entry.Pid] = new GpuProcessLoad(
                    Math.Min(100, Math.Max(existing.TotalPercent, entry.TotalPercent)),
                    existing.LocalBytes + entry.LocalBytes,
                    existing.NonLocalBytes + entry.NonLocalBytes);
            }
            else
            {
                byPid[entry.Pid] = new GpuProcessLoad(entry.TotalPercent, entry.LocalBytes, entry.NonLocalBytes);
            }
        }
        return byPid;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pdh?.Dispose();
            _pdh = null;
        }
    }

    internal static string FormatLuid(long luid)
    {
        var high = unchecked((uint)(luid >> 32));
        var low = unchecked((uint)(luid & 0xFFFFFFFF));
        return $"0x{high:X8}_0x{low:X8}";
    }

    internal static bool TryParseLuid(string highHex, string lowHex, out long luid)
    {
        luid = 0;
        var hi = highHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? highHex[2..] : highHex;
        var lo = lowHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? lowHex[2..] : lowHex;
        if (!uint.TryParse(hi, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return false;
        if (!uint.TryParse(lo, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var l)) return false;
        luid = ((long)h << 32) | l;
        return true;
    }

    internal static (int Pid, long Luid, string EngineType)? ParseEngineInstance(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance)) return null;
        var match = EngineInstancePattern().Match(instance.Trim());
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0) return null;
        if (!TryParseLuid(match.Groups["hi"].Value, match.Groups["lo"].Value, out var luid)) return null;
        var rawType = match.Groups["type"].Value.Trim();
        var engineType = NormalizeEngineType(rawType);
        return (pid, luid, engineType);
    }

    internal static (int Pid, long Luid)? ParseMemoryInstance(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance)) return null;
        var match = MemoryInstancePattern().Match(instance.Trim());
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0) return null;
        if (!TryParseLuid(match.Groups["hi"].Value, match.Groups["lo"].Value, out var luid)) return null;
        return (pid, luid);
    }

    internal static string NormalizeEngineType(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Other";
        return raw.Trim() switch
        {
            "3D" or "3d" => "3D",
            "Copy" or "copy" => "Copy",
            "VideoDecode" or "Video Decode" => "Video Decode",
            "VideoEncode" or "Video Encode" => "Video Encode",
            "VideoProcessing" or "Video Processing" => "Video Processing",
            "Compute" or "compute" => "Compute",
            var other => other,
        };
    }

    internal static GpuProcessSnapshot Aggregate(
        IReadOnlyDictionary<string, double>? engineValues,
        IReadOnlyDictionary<string, double>? localValues,
        IReadOnlyDictionary<string, double>? nonLocalValues,
        IReadOnlyList<GpuAdapterInfo> adapters,
        IReadOnlyDictionary<int, string> processNames,
        int maxProcesses = 25,
        int selfPid = -1,
        long sampledAtMs = 0)
    {
        var adapterMap = new Dictionary<long, GpuAdapterInfo>(adapters.Count);
        foreach (var adapter in adapters) adapterMap[adapter.Luid] = adapter;

        var buckets = new Dictionary<(int Pid, long Luid), ProcessBucket>();
        ProcessBucket GetBucket(int pid, long luid)
        {
            if (!buckets.TryGetValue((pid, luid), out var bucket))
                buckets[(pid, luid)] = bucket = new ProcessBucket();
            return bucket;
        }

        if (engineValues is not null)
        {
            foreach (var (instance, value) in engineValues)
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) continue;
                if (ParseEngineInstance(instance) is not { } parsed) continue;
                var bucket = GetBucket(parsed.Pid, parsed.Luid);
                bucket.Engines[parsed.EngineType] = bucket.Engines.TryGetValue(parsed.EngineType, out var prev)
                    ? prev + value
                    : value;
            }
        }

        if (localValues is not null)
        {
            foreach (var (instance, value) in localValues)
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) continue;
                if (ParseMemoryInstance(instance) is not { } parsed) continue;
                GetBucket(parsed.Pid, parsed.Luid).LocalBytes += (long)Math.Round(value);
            }
        }

        if (nonLocalValues is not null)
        {
            foreach (var (instance, value) in nonLocalValues)
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) continue;
                if (ParseMemoryInstance(instance) is not { } parsed) continue;
                GetBucket(parsed.Pid, parsed.Luid).NonLocalBytes += (long)Math.Round(value);
            }
        }

        var entries = new List<GpuProcessEntry>(buckets.Count);
        foreach (var ((pid, luid), bucket) in buckets)
        {
            var slices = bucket.Engines
                .Select(kv => new GpuEngineSlice(kv.Key, Math.Round(Math.Clamp(kv.Value, 0, 100), 1)))
                .Where(s => s.Percent >= 0.1)
                .OrderByDescending(s => s.Percent)
                .ThenBy(s => s.Type, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var totalPercent = slices.Count > 0 ? slices[0].Percent : 0.0;
            if (totalPercent < 0.1 && bucket.LocalBytes <= 0 && bucket.NonLocalBytes <= 0) continue;

            var rawName = processNames.TryGetValue(pid, out var resolved) && !string.IsNullOrWhiteSpace(resolved)
                ? resolved
                : $"PID {pid}";
            var luidKey = FormatLuid(luid);
            var adapterName = adapterMap.TryGetValue(luid, out var adapter) ? adapter.Name : luidKey;
            var protectedReason = AutopilotSafety.WhyLeftAlone(rawName);
            entries.Add(new GpuProcessEntry(
                pid,
                rawName,
                luid,
                luidKey,
                adapterName,
                totalPercent,
                slices,
                bucket.LocalBytes,
                bucket.NonLocalBytes,
                protectedReason));
        }

        entries.Sort((a, b) =>
        {
            var aActive = a.TotalPercent >= 0.5 ? 1 : 0;
            var bActive = b.TotalPercent >= 0.5 ? 1 : 0;
            var c = bActive.CompareTo(aActive);
            if (c != 0) return c;
            c = b.TotalPercent.CompareTo(a.TotalPercent);
            if (c != 0) return c;
            c = b.LocalBytes.CompareTo(a.LocalBytes);
            if (c != 0) return c;
            c = b.NonLocalBytes.CompareTo(a.NonLocalBytes);
            return c != 0 ? c : a.Pid.CompareTo(b.Pid);
        });

        var answer = Summarize(entries, selfPid);
        var limited = entries.Count > maxProcesses ? entries.GetRange(0, maxProcesses) : entries;
        return new GpuProcessSnapshot(adapters, limited, answer, sampledAtMs);
    }

    internal static GpuUsageAnswer Summarize(IReadOnlyList<GpuProcessEntry> entries, int selfPid = -1)
    {
        var topGpu = entries.FirstOrDefault(e => e.TotalPercent >= 0.5);
        var topVram = entries
            .Where(e => e.LocalBytes >= TopVramThresholdBytes)
            .OrderByDescending(e => e.LocalBytes)
            .FirstOrDefault();

        var hogs = entries
            .Where(e => e.Pid != selfPid && e.ProtectedReason is null
                && (AutopilotRules.GpuHog(e.TotalPercent, e.LocalBytes) || e.LocalBytes >= NotableVramHogBytes))
            .Take(5)
            .ToList();

        string summary;
        if (topGpu is not null)
        {
            var engineText = topGpu.Engines.Count > 0 ? $" ({topGpu.Engines[0].Type})" : "";
            var pctText = $"{Math.Round(topGpu.TotalPercent):0}%";
            if (topVram is not null && topVram.Pid == topGpu.Pid && topVram.AdapterLuid == topGpu.AdapterLuid)
            {
                summary = $"{topGpu.Name} is using {pctText} GPU{engineText} and {FormatBytes(topVram.LocalBytes)} VRAM.";
            }
            else if (topVram is not null)
            {
                summary = $"{topGpu.Name} is using {pctText} GPU{engineText}; {topVram.Name} holds the most VRAM ({FormatBytes(topVram.LocalBytes)}).";
            }
            else
            {
                summary = $"{topGpu.Name} is using {pctText} GPU{engineText}.";
            }
        }
        else if (topVram is not null)
        {
            summary = $"GPU compute is idle; {topVram.Name} holds the most VRAM ({FormatBytes(topVram.LocalBytes)}).";
        }
        else
        {
            summary = "Nothing is actively using your GPU right now.";
        }

        return new GpuUsageAnswer(summary, topGpu, topVram, hogs);
    }

    internal static string FormatBytes(long bytes)
    {
        const double GiB = 1024.0 * 1024.0 * 1024.0;
        const double MiB = 1024.0 * 1024.0;
        if (bytes >= 1024L * 1024L * 1024L)
            return $"{(bytes / GiB).ToString("0.0", CultureInfo.InvariantCulture)} GB";
        return $"{Math.Max(1, (long)Math.Round(bytes / MiB)).ToString(CultureInfo.InvariantCulture)} MB";
    }

    private static bool HasUnknownAdapter(IReadOnlyList<GpuProcessEntry> entries, IReadOnlyList<GpuAdapterInfo> adapters)
    {
        if (entries.Count == 0) return false;
        var known = new HashSet<long>(adapters.Select(a => a.Luid));
        foreach (var entry in entries)
            if (!known.Contains(entry.AdapterLuid)) return true;
        return false;
    }

    private static Dictionary<int, string> ReadProcessNames()
    {
        var rows = ProcessSnapshot.ReadCached(TimeSpan.FromMilliseconds(500));
        var map = new Dictionary<int, string>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Pid <= 0 || string.IsNullOrWhiteSpace(row.Name)) continue;
            map[row.Pid] = ProcessSnapshot.StripExe(row.Name);
        }
        return map;
    }

    internal static unsafe IReadOnlyList<GpuAdapterInfo> ReadAdapters()
    {
        var list = new List<GpuAdapterInfo>();
        try
        {
            var factoryIid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(in factoryIid, out var factory) < 0 || factory == IntPtr.Zero) return list;
            try
            {
                var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)factory)[12];
                for (uint i = 0; i < 16; i++)
                {
                    IntPtr adapter;
                    if (enumAdapters1(factory, i, &adapter) < 0) break;
                    try
                    {
                        AdapterDesc1 desc;
                        var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)(*(IntPtr**)adapter)[10];
                        if (getDesc1(adapter, &desc) < 0) continue;
                        var name = new string(desc.Description).TrimEnd('\0').Trim();
                        var software = (desc.Flags & 2) != 0;
                        list.Add(new GpuAdapterInfo(
                            desc.AdapterLuid,
                            FormatLuid(desc.AdapterLuid),
                            string.IsNullOrEmpty(name) ? FormatLuid(desc.AdapterLuid) : name,
                            desc.VendorId,
                            (long)desc.DedicatedVideoMemory,
                            (long)desc.SharedSystemMemory,
                            software));
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            AppLog.Debug("GpuProcessMonitor: DXGI adapter enumeration failed: " + ex.Message);
        }
        return list;
    }

    private sealed class ProcessBucket
    {
        public Dictionary<string, double> Engines { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long LocalBytes { get; set; }
        public long NonLocalBytes { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [GeneratedRegex(@"^pid_(?<pid>\d+)_luid_(?<hi>0x[0-9a-f]+)_(?<lo>0x[0-9a-f]+)_phys_\d+_eng_\d+_engtype_(?<type>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EngineInstancePattern();

    [GeneratedRegex(@"^pid_(?<pid>\d+)_luid_(?<hi>0x[0-9a-f]+)_(?<lo>0x[0-9a-f]+)_phys_\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MemoryInstancePattern();
}
