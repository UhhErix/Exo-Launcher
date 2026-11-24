namespace ExoLauncher.Services;

internal sealed record GpuTuningRange(int Min, int Max, int Step, int Default, bool Supported)
{
    internal static readonly GpuTuningRange Unsupported = new(0, 0, 1, 0, false);

    internal int Clamp(int value)
    {
        if (!Supported) return Default;
        var clamped = Math.Clamp(value, Min, Max);
        var step = Math.Max(1, Step);
        var anchor = Math.Clamp(Default, Min, Max);
        var steps = (int)Math.Round((clamped - anchor) / (double)step);
        var snapped = anchor + steps * step;
        if (snapped > Max) snapped -= step;
        if (snapped < Min) snapped += step;
        return Math.Clamp(snapped, Min, Max);
    }
}

internal sealed record GpuVfPoint(int Index, int VoltageMv, int BaseClockMhz, int OffsetMhz);

internal sealed record GpuFanPoint(int TempC, int SpeedPercent);

internal sealed record GpuFanChannelCurve(int FanIndex, bool Manual, IReadOnlyList<GpuFanPoint> Points);

internal sealed record GpuTuningCapabilities
{
    internal const string LockedMessage = "Locked by your GPU maker";
    internal const string IntelWaiverBody =
        "Overclocking or altering GPU clock frequency, voltage, power, or fan settings outside Intel factory specifications may reduce hardware lifetime, cause instability, and is not covered by Intel warranty. Continue only if you accept these risks for this GPU.";

    public string Vendor { get; init; } = "unknown";
    public string Generation { get; init; } = "Unknown";
    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string Backend { get; init; } = "none";
    public string? FallbackReason { get; init; }
    public bool IsLaptop { get; init; }
    public bool LockedByMaker { get; init; }
    public string? LockReason { get; init; }
    public bool RequiresWaiver { get; init; }
    public bool WaiverAccepted { get; init; }
    public string? WaiverText { get; init; }
    public bool PersistsInDriver { get; init; }
    public bool NeedsResidentForFanCurve { get; init; }
    public string? PersistenceNote { get; init; }
    public bool SupportsCurveEditor { get; init; }
    public bool SupportsUndervoltSearch { get; init; }

    public GpuTuningRange CoreClockOffsetMhz { get; init; } = GpuTuningRange.Unsupported;
    public GpuTuningRange CoreMinClockMhz { get; init; } = GpuTuningRange.Unsupported;
    public GpuTuningRange CoreMaxClockMhz { get; init; } = GpuTuningRange.Unsupported;
    public GpuTuningRange VoltageMv { get; init; } = GpuTuningRange.Unsupported;
    public GpuTuningRange VoltageOffsetMv { get; init; } = GpuTuningRange.Unsupported;
    public GpuTuningRange MemoryClockOffsetMhz { get; init; } = GpuTuningRange.Unsupported;
    public GpuTuningRange MemoryMaxClockMhz { get; init; } = GpuTuningRange.Unsupported;
    public GpuTuningRange PowerLimitPercent { get; init; } = GpuTuningRange.Unsupported;
    public GpuTuningRange PowerLimitWatts { get; init; } = GpuTuningRange.Unsupported;
    public GpuTuningRange TempLimitC { get; init; } = GpuTuningRange.Unsupported;
    public GpuTuningRange ClockLockMhz { get; init; } = GpuTuningRange.Unsupported;

    public IReadOnlyList<string> MemoryTimingLevels { get; init; } = [];
    public IReadOnlyList<GpuVfPoint> VfCurvePoints { get; init; } = [];

    public int FanCount { get; init; }
    public bool SupportsHardwareFanCurve { get; init; }
    public bool SupportsSoftwareFanCurve { get; init; }
    public bool SupportsZeroRpm { get; init; }
    public int FanMaxPoints { get; init; }
    public GpuTuningRange FanSpeedPercent { get; init; } = GpuTuningRange.Unsupported;
    public GpuTuningRange FanTempC { get; init; } = GpuTuningRange.Unsupported;

    internal bool HasAnyControllableSetting =>
        !LockedByMaker && (
            CoreClockOffsetMhz.Supported || CoreMinClockMhz.Supported || CoreMaxClockMhz.Supported
            || VoltageMv.Supported || VoltageOffsetMv.Supported
            || MemoryClockOffsetMhz.Supported || MemoryMaxClockMhz.Supported
            || MemoryTimingLevels.Count > 1
            || PowerLimitPercent.Supported || PowerLimitWatts.Supported
            || TempLimitC.Supported || ClockLockMhz.Supported
            || SupportsCurveEditor || SupportsHardwareFanCurve || SupportsSoftwareFanCurve || SupportsZeroRpm);
}

internal sealed record GpuManualProfile
{
    public string Mode { get; init; } = "auto";
    public int? CoreClockOffsetMhz { get; init; }
    public int? CoreMinClockMhz { get; init; }
    public int? CoreMaxClockMhz { get; init; }
    public int? VoltageMv { get; init; }
    public int? VoltageOffsetMv { get; init; }
    public int? MemoryClockOffsetMhz { get; init; }
    public int? MemoryMaxClockMhz { get; init; }
    public string? MemoryTimingLevel { get; init; }
    public int? PowerLimitPercent { get; init; }
    public int? PowerLimitWatts { get; init; }
    public int? TempLimitC { get; init; }
    public int? ClockLockMhz { get; init; }
    public IReadOnlyList<GpuVfPoint>? VfCurve { get; init; }
    public bool? ZeroRpm { get; init; }
    public IReadOnlyList<GpuFanPoint>? FanCurve { get; init; }
    public IReadOnlyList<GpuFanChannelCurve>? FanChannels { get; init; }

    internal static GpuManualProfile Auto => new() { Mode = "auto" };
}

internal sealed record GpuApplyResult(bool Ok, bool FallbackRequired, string? Message, string? BackendUsed = null);

internal sealed record GpuTuningValidation(bool Allowed, GpuManualProfile? Clamped, string? RefusalReason);

internal sealed record GpuStabilityProgress(
    bool Running,
    string Kind,
    string Phase,
    int ElapsedSec,
    int TotalSec,
    long Passes,
    double? TempC,
    int? ClockMhz,
    bool ChecksumOk,
    string? CurrentStep,
    bool? Passed,
    bool RolledBack,
    string? FailureReason,
    string? Summary);

internal sealed record GpuManualTuningState(
    bool Ok,
    string? Message,
    GpuTuningCapabilities Capabilities,
    GpuManualProfile FactoryDefaults,
    GpuManualProfile Current,
    GpuManualProfile Saved,
    string Mode,
    bool PausedAfterCrash,
    string? PauseReason,
    bool CanReapply,
    bool QueuedPending,
    string? QueuedReason,
    GpuStabilityProgress Test,
    string StatusLine,
    double? LiveTempC,
    int? LiveClockMhz,
    int? LiveFanPercent,
    double? LiveWatts);

internal interface IGpuTuningBackend
{
    string BackendId { get; }
    string Vendor { get; }
    GpuTuningCapabilities Probe();
    GpuManualProfile ReadCurrent();
    GpuManualProfile ReadFactoryDefaults();
    GpuApplyResult Apply(GpuManualProfile profile);
    GpuApplyResult ResetToFactory();
    bool AcceptWaiver();
}

internal static class GpuTuningPolicy
{
    internal static string NormalizeVendor(string? vendor) => vendor?.ToLowerInvariant() switch
    {
        "nvidia" => "nvidia",
        "amd" or "radeon" => "amd",
        "intel" => "intel",
        _ => "unknown",
    };

    internal static string PartIdForVendor(string? vendor) => NormalizeVendor(vendor) switch
    {
        "nvidia" => "nvidia",
        "amd" => "radeon",
        "intel" => "intel",
        _ => "gpu",
    };

    internal static string DetectGeneration(string? vendor, string? gpuName)
    {
        var name = gpuName ?? "";
        var v = NormalizeVendor(vendor);
        if (v == "nvidia")
        {
            if (ContainsAny(name, "5090", "5080", "5070", "5060", "5050", "RTX 50")) return "Blackwell";
            if (ContainsAny(name, "4090", "4080", "4070", "4060", "4050", "RTX 40", "Ada")) return "Ada";
            if (ContainsAny(name, "3090", "3080", "3070", "3060", "3050", "RTX 30", "RTX A")) return "Ampere";
            if (ContainsAny(name, "2080", "2070", "2060", "1660", "1650", "1630", "RTX 20", "GTX 16")) return "Turing";
            if (ContainsAny(name, "1080", "1070", "1060", "1050", "GTX 10")) return "Pascal";
            return "Unknown";
        }
        if (v == "amd")
        {
            if (ContainsAny(name, "9070", "9060", "9050", "RX 9")) return "RDNA4";
            if (ContainsAny(name, "7900", "7800", "7700", "7600", "RX 7")) return "RDNA3";
            if (ContainsAny(name, "6950", "6900", "6800", "6750", "6700", "6650", "6600", "6500", "6400", "RX 6")) return "RDNA2";
            if (ContainsAny(name, "5700", "5600", "5500", "5300", "RX 5")) return "RDNA1";
            return "Unknown";
        }
        if (v == "intel")
        {
            if (ContainsAny(name, "B580", "B570", "B770", "Arc B")) return "Battlemage";
            if (ContainsAny(name, "A770", "A750", "A580", "A380", "A310", "Arc A")) return "Alchemist";
            return "Unknown";
        }
        return "Unknown";
    }

    internal static bool IsRdna3OrNewer(string generation) => generation is "RDNA3" or "RDNA4";

    internal static GpuTuningValidation ValidateAndClamp(GpuTuningCapabilities caps, GpuManualProfile requested)
    {
        if (caps.LockedByMaker || !caps.HasAnyControllableSetting)
            return new(false, null, caps.LockReason ?? GpuTuningCapabilities.LockedMessage);

        var mode = string.Equals(requested.Mode, "manual", StringComparison.OrdinalIgnoreCase) ? "manual" : "auto";
        if (mode == "auto")
            return new(true, GpuManualProfile.Auto, null);

        if (caps.RequiresWaiver && !caps.WaiverAccepted)
            return new(false, null, "Accept Intel's overclocking waiver for this GPU before applying manual tuning.");

        int? minClock = requested.CoreMinClockMhz is { } mn && caps.CoreMinClockMhz.Supported ? caps.CoreMinClockMhz.Clamp(mn) : null;
        int? maxClock = requested.CoreMaxClockMhz is { } mx && caps.CoreMaxClockMhz.Supported ? caps.CoreMaxClockMhz.Clamp(mx) : null;
        if (minClock is { } lo && maxClock is { } hi && lo > hi)
            minClock = hi;

        string? timing = null;
        if (requested.MemoryTimingLevel is { } level && caps.MemoryTimingLevels.Count > 0)
        {
            var matched = caps.MemoryTimingLevels.FirstOrDefault(item => item.Equals(level, StringComparison.OrdinalIgnoreCase));
            timing = matched ?? caps.MemoryTimingLevels[0];
        }

        IReadOnlyList<GpuVfPoint>? vfCurve = null;
        if (requested.VfCurve is { Count: > 0 } points && caps.SupportsCurveEditor)
        {
            var offsetRange = caps.CoreClockOffsetMhz.Supported ? caps.CoreClockOffsetMhz : new GpuTuningRange(-1000, 1000, 15, 0, true);
            vfCurve = [.. points
                .OrderBy(p => p.VoltageMv)
                .Select((p, idx) => new GpuVfPoint(
                    p.Index >= 0 ? p.Index : idx,
                    Math.Clamp(p.VoltageMv, 500, 1500),
                    Math.Max(0, p.BaseClockMhz),
                    offsetRange.Clamp(p.OffsetMhz)))];
        }

        var supportsFan = caps.SupportsHardwareFanCurve || caps.SupportsSoftwareFanCurve;
        IReadOnlyList<GpuFanPoint>? fanCurve = null;
        if (requested.FanCurve is { Count: > 0 } rawFan && supportsFan)
            fanCurve = ClampFanPoints(rawFan, caps);

        IReadOnlyList<GpuFanChannelCurve>? fanChannels = null;
        if (requested.FanChannels is { Count: > 0 } channels && supportsFan && caps.FanCount > 0)
        {
            fanChannels = [.. channels
                .Where(c => c.FanIndex >= 0 && c.FanIndex < caps.FanCount)
                .Select(c => new GpuFanChannelCurve(c.FanIndex, c.Manual, c.Manual ? ClampFanPoints(c.Points, caps) : []))];
        }

        var clamped = new GpuManualProfile
        {
            Mode = "manual",
            CoreClockOffsetMhz = requested.CoreClockOffsetMhz is { } co && caps.CoreClockOffsetMhz.Supported ? caps.CoreClockOffsetMhz.Clamp(co) : null,
            CoreMinClockMhz = minClock,
            CoreMaxClockMhz = maxClock,
            VoltageMv = requested.VoltageMv is { } v && caps.VoltageMv.Supported ? caps.VoltageMv.Clamp(v) : null,
            VoltageOffsetMv = requested.VoltageOffsetMv is { } vo && caps.VoltageOffsetMv.Supported ? caps.VoltageOffsetMv.Clamp(vo) : null,
            MemoryClockOffsetMhz = requested.MemoryClockOffsetMhz is { } mo && caps.MemoryClockOffsetMhz.Supported ? caps.MemoryClockOffsetMhz.Clamp(mo) : null,
            MemoryMaxClockMhz = requested.MemoryMaxClockMhz is { } mm && caps.MemoryMaxClockMhz.Supported ? caps.MemoryMaxClockMhz.Clamp(mm) : null,
            MemoryTimingLevel = timing,
            PowerLimitPercent = requested.PowerLimitPercent is { } plp && caps.PowerLimitPercent.Supported ? caps.PowerLimitPercent.Clamp(plp) : null,
            PowerLimitWatts = requested.PowerLimitWatts is { } plw && caps.PowerLimitWatts.Supported ? caps.PowerLimitWatts.Clamp(plw) : null,
            TempLimitC = requested.TempLimitC is { } tc && caps.TempLimitC.Supported ? caps.TempLimitC.Clamp(tc) : null,
            ClockLockMhz = requested.ClockLockMhz is { } clk && caps.ClockLockMhz.Supported ? caps.ClockLockMhz.Clamp(clk) : null,
            VfCurve = vfCurve,
            ZeroRpm = requested.ZeroRpm is { } z && caps.SupportsZeroRpm ? z : null,
            FanCurve = fanCurve,
            FanChannels = fanChannels,
        };

        return new(true, clamped, null);
    }

    internal static IReadOnlyList<GpuFanPoint> ClampFanPoints(IReadOnlyList<GpuFanPoint> points, GpuTuningCapabilities caps)
    {
        var maxPoints = caps.FanMaxPoints > 0 ? caps.FanMaxPoints : 5;
        var tempRange = caps.FanTempC.Supported ? caps.FanTempC : new GpuTuningRange(25, 100, 1, 50, true);
        var speedRange = caps.FanSpeedPercent.Supported ? caps.FanSpeedPercent : new GpuTuningRange(0, 100, 1, 50, true);
        var ordered = points
            .Take(maxPoints)
            .Select(p => new GpuFanPoint(tempRange.Clamp(p.TempC), speedRange.Clamp(p.SpeedPercent)))
            .OrderBy(p => p.TempC)
            .ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var cur = ordered[i];
            var t = Math.Max(cur.TempC, Math.Min(tempRange.Max, prev.TempC + 1));
            var s = Math.Max(cur.SpeedPercent, prev.SpeedPercent);
            ordered[i] = new GpuFanPoint(t, speedRange.Clamp(s));
        }
        return ordered;
    }

    internal static string DescribeStatus(
        GpuTuningCapabilities caps,
        string mode,
        bool pausedAfterCrash,
        bool queuedPending,
        string? queuedReason,
        GpuStabilityProgress test)
    {
        if (caps.LockedByMaker) return caps.LockReason ?? GpuTuningCapabilities.LockedMessage;
        if (test.Running) return test.CurrentStep ?? "Running stability test.";
        if (pausedAfterCrash) return TunePersistence.PausedNote;
        if (queuedPending) return queuedReason ?? "Queued until your game exits.";
        if (caps.RequiresWaiver && !caps.WaiverAccepted) return "Waiver consent required before tuning this Intel Arc GPU.";
        var backendLabel = caps.Backend switch
        {
            "nvapi" => "NVAPI",
            "nvml" => caps.FallbackReason is not null ? "NVML fallback" : "NVML",
            "adlx" => "ADLX",
            "igcl" => "IGCL",
            _ => "Unavailable",
        };
        if (mode != "manual") return $"Auto (stock) · {backendLabel}";
        var persist = caps.PersistsInDriver
            ? "Persists in driver"
            : caps.NeedsResidentForFanCurve
                ? "Re-applied at sign-in; software fan curves need Exo open"
                : "Re-applied at sign-in";
        return $"Manual active · {backendLabel} · {persist}";
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

internal sealed class FallbackGpuTuningBackend : IGpuTuningBackend
{
    private readonly IGpuTuningBackend _primary;
    private readonly IGpuTuningBackend _fallback;
    private readonly Lock _gate = new();
    private bool _usingFallback;
    private string? _fallbackReason;

    internal FallbackGpuTuningBackend(IGpuTuningBackend primary, IGpuTuningBackend fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public string BackendId
    {
        get { lock (_gate) return _usingFallback ? _fallback.BackendId : _primary.BackendId; }
    }

    public string Vendor => _primary.Vendor;

    internal bool UsingFallback
    {
        get { lock (_gate) return _usingFallback; }
    }

    internal string? FallbackReason
    {
        get { lock (_gate) return _fallbackReason; }
    }

    public GpuTuningCapabilities Probe()
    {
        lock (_gate)
        {
            if (!_usingFallback)
            {
                var primaryCaps = _primary.Probe();
                if (!primaryCaps.LockedByMaker && primaryCaps.HasAnyControllableSetting && primaryCaps.Backend != "none")
                    return primaryCaps;
                if (primaryCaps.LockedByMaker && primaryCaps.IsLaptop)
                    return primaryCaps;
                _usingFallback = true;
                _fallbackReason = primaryCaps.FallbackReason ?? "Private NVAPI interface unavailable on this driver; switched to NVML.";
            }
            var fallbackCaps = _fallback.Probe();
            return fallbackCaps with { FallbackReason = _fallbackReason ?? fallbackCaps.FallbackReason };
        }
    }

    public GpuManualProfile ReadCurrent()
    {
        lock (_gate) return _usingFallback ? _fallback.ReadCurrent() : _primary.ReadCurrent();
    }

    public GpuManualProfile ReadFactoryDefaults()
    {
        lock (_gate) return _usingFallback ? _fallback.ReadFactoryDefaults() : _primary.ReadFactoryDefaults();
    }

    public GpuApplyResult Apply(GpuManualProfile profile)
    {
        lock (_gate)
        {
            if (!_usingFallback)
            {
                var result = _primary.Apply(profile);
                if (result.Ok || !result.FallbackRequired)
                    return result with { BackendUsed = result.BackendUsed ?? _primary.BackendId };
                _usingFallback = true;
                _fallbackReason = result.Message ?? "Private NVAPI call failed; switched to NVML.";
            }
            var fallbackResult = _fallback.Apply(profile);
            return fallbackResult with { BackendUsed = fallbackResult.BackendUsed ?? _fallback.BackendId };
        }
    }

    public GpuApplyResult ResetToFactory()
    {
        lock (_gate)
        {
            if (!_usingFallback)
            {
                var result = _primary.ResetToFactory();
                if (result.Ok || !result.FallbackRequired)
                    return result with { BackendUsed = result.BackendUsed ?? _primary.BackendId };
                _usingFallback = true;
                _fallbackReason = result.Message ?? "Private NVAPI reset failed; switched to NVML.";
            }
            var fallbackResult = _fallback.ResetToFactory();
            return fallbackResult with { BackendUsed = fallbackResult.BackendUsed ?? _fallback.BackendId };
        }
    }

    public bool AcceptWaiver()
    {
        lock (_gate) return _usingFallback ? _fallback.AcceptWaiver() : _primary.AcceptWaiver();
    }
}
