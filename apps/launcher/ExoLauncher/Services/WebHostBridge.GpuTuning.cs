using System.Text.Json;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private async Task<object> GpuTuningStateAsync(CancellationToken ct) =>
        SerializeGpuTuningState(await Task.Run(GpuManualTuningService.Shared.ReadState, ct).ConfigureAwait(false));

    private async Task<object> GpuTuningApplyAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var reapply = ReadBool(p, hasParams, "reapply") == true;
        var state = await Task.Run(() =>
            reapply
                ? GpuManualTuningService.Shared.ReapplySaved()
                : GpuManualTuningService.Shared.Apply(ParseGpuManualProfile(p, hasParams)), ct).ConfigureAwait(false);
        return SerializeGpuTuningState(state);
    }

    private async Task<object> GpuTuningResetAsync(CancellationToken ct) =>
        SerializeGpuTuningState(await Task.Run(GpuManualTuningService.Shared.Reset, ct).ConfigureAwait(false));

    private async Task<object> GpuTuningWaiverAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var accept = ReadBool(p, hasParams, "accept") == true;
        var deviceId = ReadString(p, hasParams, "deviceId");
        var state = await Task.Run(() =>
            accept
                ? GpuManualTuningService.Shared.AcceptWaiver(deviceId)
                : GpuManualTuningService.Shared.ReadState(), ct).ConfigureAwait(false);
        return SerializeGpuTuningState(state);
    }

    private async Task<object> GpuTuningTestAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action") ?? "start";
        var duration = ReadString(p, hasParams, "duration") ?? "60s";
        JsonElement profEl = default;
        var hasProfile = hasParams && p.TryGetProperty("profile", out profEl) && profEl.ValueKind == JsonValueKind.Object;
        var candidate = hasProfile ? ParseGpuManualProfile(profEl, true) : null;
        var state = await Task.Run(() =>
            string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase)
                ? GpuManualTuningService.Shared.StopTest()
                : GpuManualTuningService.Shared.StartStabilityTest(duration, candidate), ct).ConfigureAwait(false);
        return SerializeGpuTuningState(state);
    }

    private async Task<object> GpuTuningUndervoltAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action") ?? "start";
        var state = await Task.Run(() =>
            string.Equals(action, "stop", StringComparison.OrdinalIgnoreCase)
                ? GpuManualTuningService.Shared.StopTest()
                : GpuManualTuningService.Shared.StartUndervoltSearch(), ct).ConfigureAwait(false);
        return SerializeGpuTuningState(state);
    }

    private void PostGpuTuningChanged(GpuManualTuningState state) => PostEvent("gpu.tuningChanged", new
    {
        mode = state.Mode,
        statusLine = state.StatusLine,
        pausedAfterCrash = state.PausedAfterCrash,
        queuedPending = state.QueuedPending,
        queuedReason = state.QueuedReason,
        backend = state.Capabilities.Backend,
    });

    private void PostGpuTuningTestProgress(GpuStabilityProgress p) => PostEvent("gpu.tuningTestProgress", new
    {
        running = p.Running,
        kind = p.Kind,
        phase = p.Phase,
        elapsedSec = p.ElapsedSec,
        totalSec = p.TotalSec,
        passes = p.Passes,
        tempC = p.TempC,
        clockMhz = p.ClockMhz,
        checksumOk = p.ChecksumOk,
        currentStep = p.CurrentStep,
        passed = p.Passed,
        rolledBack = p.RolledBack,
        failureReason = p.FailureReason,
        summary = p.Summary,
    });

    private static GpuManualProfile ParseGpuManualProfile(JsonElement p, bool hasParams)
    {
        if (!hasParams || p.ValueKind != JsonValueKind.Object) return GpuManualProfile.Auto;
        var mode = ReadString(p, true, "mode") ?? "manual";

        List<GpuVfPoint>? vfCurve = null;
        if (p.TryGetProperty("vfCurve", out var vfEl) && vfEl.ValueKind == JsonValueKind.Array)
        {
            vfCurve = [];
            var idx = 0;
            foreach (var item in vfEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var ptIndex = ReadInt(item, "index") ?? idx;
                var mv = ReadInt(item, "voltageMv") ?? 0;
                var baseClk = ReadInt(item, "baseClockMhz") ?? 0;
                var off = ReadInt(item, "offsetMhz") ?? 0;
                vfCurve.Add(new GpuVfPoint(ptIndex, mv, baseClk, off));
                idx++;
            }
        }

        List<GpuFanPoint>? fanCurve = null;
        if (p.TryGetProperty("fanCurve", out var fanEl) && fanEl.ValueKind == JsonValueKind.Array)
        {
            fanCurve = ParseFanPoints(fanEl);
        }

        List<GpuFanChannelCurve>? fanChannels = null;
        if (p.TryGetProperty("fanChannels", out var chEl) && chEl.ValueKind == JsonValueKind.Array)
        {
            fanChannels = [];
            foreach (var item in chEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var fanIndex = ReadInt(item, "fanIndex") ?? 0;
                var manual = ReadBool(item, true, "manual") == true;
                var pts = item.TryGetProperty("points", out var ptsEl) && ptsEl.ValueKind == JsonValueKind.Array
                    ? ParseFanPoints(ptsEl)
                    : [];
                fanChannels.Add(new GpuFanChannelCurve(fanIndex, manual, pts));
            }
        }

        return new GpuManualProfile
        {
            Mode = mode,
            CoreClockOffsetMhz = ReadInt(p, "coreClockOffsetMhz"),
            CoreMinClockMhz = ReadInt(p, "coreMinClockMhz"),
            CoreMaxClockMhz = ReadInt(p, "coreMaxClockMhz"),
            VoltageMv = ReadInt(p, "voltageMv"),
            VoltageOffsetMv = ReadInt(p, "voltageOffsetMv"),
            MemoryClockOffsetMhz = ReadInt(p, "memoryClockOffsetMhz"),
            MemoryMaxClockMhz = ReadInt(p, "memoryMaxClockMhz"),
            MemoryTimingLevel = ReadString(p, true, "memoryTimingLevel"),
            PowerLimitPercent = ReadInt(p, "powerLimitPercent"),
            PowerLimitWatts = ReadInt(p, "powerLimitWatts"),
            TempLimitC = ReadInt(p, "tempLimitC"),
            ClockLockMhz = ReadInt(p, "clockLockMhz"),
            VfCurve = vfCurve,
            ZeroRpm = ReadBool(p, true, "zeroRpm"),
            FanCurve = fanCurve,
            FanChannels = fanChannels,
        };
    }

    private static List<GpuFanPoint> ParseFanPoints(JsonElement arr)
    {
        var list = new List<GpuFanPoint>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (ReadInt(item, "tempC") is { } t && ReadInt(item, "speedPercent") is { } s)
                list.Add(new GpuFanPoint(t, s));
        }
        return list;
    }

    private static int? ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v) ? v : null;

    private static object SerializeRange(GpuTuningRange r) => new
    {
        min = r.Min,
        max = r.Max,
        step = r.Step,
        @default = r.Default,
        supported = r.Supported,
    };

    private static object SerializeProfile(GpuManualProfile p) => new
    {
        mode = p.Mode,
        coreClockOffsetMhz = p.CoreClockOffsetMhz,
        coreMinClockMhz = p.CoreMinClockMhz,
        coreMaxClockMhz = p.CoreMaxClockMhz,
        voltageMv = p.VoltageMv,
        voltageOffsetMv = p.VoltageOffsetMv,
        memoryClockOffsetMhz = p.MemoryClockOffsetMhz,
        memoryMaxClockMhz = p.MemoryMaxClockMhz,
        memoryTimingLevel = p.MemoryTimingLevel,
        powerLimitPercent = p.PowerLimitPercent,
        powerLimitWatts = p.PowerLimitWatts,
        tempLimitC = p.TempLimitC,
        clockLockMhz = p.ClockLockMhz,
        vfCurve = p.VfCurve?.Select(pt => new { index = pt.Index, voltageMv = pt.VoltageMv, baseClockMhz = pt.BaseClockMhz, offsetMhz = pt.OffsetMhz }).ToList(),
        zeroRpm = p.ZeroRpm,
        fanCurve = p.FanCurve?.Select(pt => new { tempC = pt.TempC, speedPercent = pt.SpeedPercent }).ToList(),
        fanChannels = p.FanChannels?.Select(ch => new
        {
            fanIndex = ch.FanIndex,
            manual = ch.Manual,
            points = ch.Points.Select(pt => new { tempC = pt.TempC, speedPercent = pt.SpeedPercent }).ToList(),
        }).ToList(),
    };

    private static object SerializeGpuTuningState(GpuManualTuningState s) => new
    {
        ok = s.Ok,
        message = s.Message,
        mode = s.Mode,
        pausedAfterCrash = s.PausedAfterCrash,
        pauseReason = s.PauseReason,
        canReapply = s.CanReapply,
        queuedPending = s.QueuedPending,
        queuedReason = s.QueuedReason,
        statusLine = s.StatusLine,
        live = new
        {
            tempC = s.LiveTempC,
            clockMhz = s.LiveClockMhz,
            fanPercent = s.LiveFanPercent,
            watts = s.LiveWatts,
        },
        capabilities = new
        {
            vendor = s.Capabilities.Vendor,
            generation = s.Capabilities.Generation,
            deviceId = s.Capabilities.DeviceId,
            deviceName = s.Capabilities.DeviceName,
            backend = s.Capabilities.Backend,
            fallbackReason = s.Capabilities.FallbackReason,
            isLaptop = s.Capabilities.IsLaptop,
            lockedByMaker = s.Capabilities.LockedByMaker,
            lockReason = s.Capabilities.LockReason,
            requiresWaiver = s.Capabilities.RequiresWaiver,
            waiverAccepted = s.Capabilities.WaiverAccepted,
            waiverText = s.Capabilities.WaiverText,
            persistsInDriver = s.Capabilities.PersistsInDriver,
            needsResidentForFanCurve = s.Capabilities.NeedsResidentForFanCurve,
            persistenceNote = s.Capabilities.PersistenceNote,
            supportsCurveEditor = s.Capabilities.SupportsCurveEditor,
            supportsUndervoltSearch = s.Capabilities.SupportsUndervoltSearch,
            coreClockOffsetMhz = SerializeRange(s.Capabilities.CoreClockOffsetMhz),
            coreMinClockMhz = SerializeRange(s.Capabilities.CoreMinClockMhz),
            coreMaxClockMhz = SerializeRange(s.Capabilities.CoreMaxClockMhz),
            voltageMv = SerializeRange(s.Capabilities.VoltageMv),
            voltageOffsetMv = SerializeRange(s.Capabilities.VoltageOffsetMv),
            memoryClockOffsetMhz = SerializeRange(s.Capabilities.MemoryClockOffsetMhz),
            memoryMaxClockMhz = SerializeRange(s.Capabilities.MemoryMaxClockMhz),
            powerLimitPercent = SerializeRange(s.Capabilities.PowerLimitPercent),
            powerLimitWatts = SerializeRange(s.Capabilities.PowerLimitWatts),
            tempLimitC = SerializeRange(s.Capabilities.TempLimitC),
            clockLockMhz = SerializeRange(s.Capabilities.ClockLockMhz),
            memoryTimingLevels = s.Capabilities.MemoryTimingLevels,
            vfCurvePoints = s.Capabilities.VfCurvePoints.Select(pt => new { index = pt.Index, voltageMv = pt.VoltageMv, baseClockMhz = pt.BaseClockMhz, offsetMhz = pt.OffsetMhz }).ToList(),
            fanCount = s.Capabilities.FanCount,
            supportsHardwareFanCurve = s.Capabilities.SupportsHardwareFanCurve,
            supportsSoftwareFanCurve = s.Capabilities.SupportsSoftwareFanCurve,
            supportsZeroRpm = s.Capabilities.SupportsZeroRpm,
            fanMaxPoints = s.Capabilities.FanMaxPoints,
            fanSpeedPercent = SerializeRange(s.Capabilities.FanSpeedPercent),
            fanTempC = SerializeRange(s.Capabilities.FanTempC),
        },
        factoryDefaults = SerializeProfile(s.FactoryDefaults),
        current = SerializeProfile(s.Current),
        saved = SerializeProfile(s.Saved),
        test = new
        {
            running = s.Test.Running,
            kind = s.Test.Kind,
            phase = s.Test.Phase,
            elapsedSec = s.Test.ElapsedSec,
            totalSec = s.Test.TotalSec,
            passes = s.Test.Passes,
            tempC = s.Test.TempC,
            clockMhz = s.Test.ClockMhz,
            checksumOk = s.Test.ChecksumOk,
            currentStep = s.Test.CurrentStep,
            passed = s.Test.Passed,
            rolledBack = s.Test.RolledBack,
            failureReason = s.Test.FailureReason,
            summary = s.Test.Summary,
        },
    };
}
