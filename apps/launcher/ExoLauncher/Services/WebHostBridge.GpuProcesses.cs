using System.Text.Json;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private static object GpuProcesses(JsonElement p, bool hasParams)
    {
        var limit = hasParams && p.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n)
            ? Math.Clamp(n, 1, 100)
            : 25;
        var snap = GpuProcessMonitor.Shared.Sample(allowWarmup: true, maxProcesses: limit);
        var usedLuids = snap.Processes.Select(proc => proc.AdapterLuid).ToHashSet();
        return new
        {
            ok = true,
            adapters = snap.Adapters
                .Where(a => !a.Software || usedLuids.Contains(a.Luid))
                .Select(a => new
                {
                    luid = a.LuidKey,
                    name = a.Name,
                    vendorId = a.VendorId,
                    dedicatedBytes = a.DedicatedBytes,
                    sharedBytes = a.SharedBytes,
                    software = a.Software,
                })
                .ToList(),
            processes = snap.Processes.Select(GpuProcessDto).ToList(),
            answer = GpuAnswerDto(snap.Answer),
        };
    }

    private static object? GpuProcessDto(GpuProcessEntry? entry) => entry is null ? null : new
    {
        pid = entry.Pid,
        name = entry.Name,
        adapterLuid = entry.AdapterLuidKey,
        adapterName = entry.AdapterName,
        totalPercent = entry.TotalPercent,
        engines = entry.Engines.Select(e => new { type = e.Type, percent = e.Percent }).ToList(),
        localBytes = entry.LocalBytes,
        nonLocalBytes = entry.NonLocalBytes,
        protectedReason = entry.ProtectedReason,
    };

    private static object? GpuAnswerDto(GpuUsageAnswer? answer) => answer is null ? null : new
    {
        summary = answer.Summary,
        topGpu = GpuProcessDto(answer.TopGpu),
        topVram = GpuProcessDto(answer.TopVram),
        hogs = answer.Hogs.Select(GpuProcessDto).ToList(),
    };
}
