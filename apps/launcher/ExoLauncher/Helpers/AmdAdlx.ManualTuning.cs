using System.Runtime.InteropServices;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

internal static unsafe partial class AmdAdlx
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct AdlxIntRange
    {
        public int MinValue;
        public int MaxValue;
        public int Step;
    }

    private const int TuningIsAtFactory = 4;
    private const int TuningIsSupportedManualGfx = 8;
    private const int TuningIsSupportedManualVram = 9;
    private const int TuningIsSupportedManualFan = 10;
    private const int TuningIsSupportedManualPower = 11;
    private const int TuningGetManualGfx = 14;
    private const int TuningGetManualVram = 15;
    private const int TuningGetManualFan = 16;
    private const int TuningGetManualPower = 17;

    private const int Gfx2GetMinFreqRange = 3, Gfx2GetMinFreq = 4, Gfx2SetMinFreq = 5;
    private const int Gfx2GetMaxFreqRange = 6, Gfx2GetMaxFreq = 7, Gfx2SetMaxFreq = 8;
    private const int Gfx2GetVoltageRange = 9, Gfx2GetVoltage = 10, Gfx2SetVoltage = 11;
    private const int Gfx21GetMinFreqDefault = 12, Gfx21GetMaxFreqDefault = 13, Gfx21GetVoltageDefault = 14;

    private const int Gfx1GetRanges = 3, Gfx1GetStates = 4, Gfx1SetStates = 7;

    private const int Vram2IsSupportedTiming = 3, Vram2GetTimingList = 4, Vram2GetTiming = 5, Vram2SetTiming = 6;
    private const int Vram2GetMaxFreqRange = 7, Vram2GetMaxFreq = 8, Vram2SetMaxFreq = 9;
    private const int Vram21GetMaxFreqDefault = 10, Vram21GetTimingDefault = 11;

    private const int PowerGetLimitRange = 3, PowerGetLimit = 4, PowerSetLimit = 5;
    private const int Power1GetLimitDefault = 10;

    private const int FanGetRanges = 3, FanGetStates = 4, FanGetEmptyStates = 5, FanIsValidStates = 6, FanSetStates = 7;
    private const int FanIsSupportedZeroRpm = 8, FanGetZeroRpm = 9, FanSetZeroRpm = 10;
    private const int FanStateGetSpeed = 3, FanStateSetSpeed = 4, FanStateGetTemp = 5, FanStateSetTemp = 6;
    private const int TimingDescGetValue = 3;

    private const int TimingDefault = 0, TimingFast1 = 1, TimingFast2 = 2;

    internal static (GpuTuningCapabilities Caps, GpuManualProfile Current, GpuManualProfile Defaults) ReadManualTuningState(bool isLaptop)
    {
        var fallbackCaps = new GpuTuningCapabilities
        {
            Vendor = "amd",
            Backend = Present ? "adlx" : "none",
            IsLaptop = isLaptop,
            LockedByMaker = true,
            LockReason = Present ? GpuTuningCapabilities.LockedMessage : "AMD Adrenalin ADLX driver library is not installed.",
            PersistsInDriver = true,
        };

        var result = WithGpu<(GpuTuningCapabilities Caps, GpuManualProfile Current, GpuManualProfile Defaults)?>("manual tuning read", (system, gpu) =>
        {
            var gpuName = Text(gpu, GpuName) ?? "AMD Radeon";
            var generation = GpuTuningPolicy.DetectGeneration("amd", gpuName);
            var isRdna3Plus = GpuTuningPolicy.IsRdna3OrNewer(generation);

            nint tuning = 0;
            try
            {
                if (Call(system, SystemGetGpuTuning, &tuning) != AdlxOk || tuning == 0)
                    return (fallbackCaps with { DeviceName = gpuName, Generation = generation }, GpuManualProfile.Auto, GpuManualProfile.Auto);

                var supGfx = IsTuningSupported(tuning, gpu, TuningIsSupportedManualGfx);
                var supVram = IsTuningSupported(tuning, gpu, TuningIsSupportedManualVram);
                var supFan = IsTuningSupported(tuning, gpu, TuningIsSupportedManualFan);
                var supPower = IsTuningSupported(tuning, gpu, TuningIsSupportedManualPower);

                var minClockRange = GpuTuningRange.Unsupported;
                var maxClockRange = GpuTuningRange.Unsupported;
                var voltageRange = GpuTuningRange.Unsupported;
                var voltageOffsetRange = GpuTuningRange.Unsupported;
                int? curMinClock = null, curMaxClock = null, curVoltage = null, curVoltageOffset = null;
                int? defMinClock = null, defMaxClock = null, defVoltage = null, defVoltageOffset = null;

                if (supGfx)
                {
                    nint rawGfx = 0, gfx2 = 0, gfx21 = 0, gfx1 = 0;
                    try
                    {
                        if (GetTuningInterface(tuning, gpu, TuningGetManualGfx, &rawGfx))
                        {
                            gfx2 = Query(rawGfx, "IADLXManualGraphicsTuning2");
                            if (gfx2 != 0)
                            {
                                gfx21 = Query(rawGfx, "IADLXManualGraphicsTuning2_1");
                                if (ReadRange(gfx2, Gfx2GetMinFreqRange, out var rMin) && ReadInt(gfx2, Gfx2GetMinFreq, out var vMin))
                                {
                                    var dMin = gfx21 != 0 && ReadInt(gfx21, Gfx21GetMinFreqDefault, out var dm) ? dm : vMin;
                                    minClockRange = ToRange(rMin, dMin);
                                    curMinClock = vMin;
                                    defMinClock = dMin;
                                }
                                if (ReadRange(gfx2, Gfx2GetMaxFreqRange, out var rMax) && ReadInt(gfx2, Gfx2GetMaxFreq, out var vMax))
                                {
                                    var dMax = gfx21 != 0 && ReadInt(gfx21, Gfx21GetMaxFreqDefault, out var dx) ? dx : vMax;
                                    maxClockRange = ToRange(rMax, dMax);
                                    curMaxClock = vMax;
                                    defMaxClock = dMax;
                                }
                                if (ReadRange(gfx2, Gfx2GetVoltageRange, out var rVolt) && ReadInt(gfx2, Gfx2GetVoltage, out var vVolt))
                                {
                                    var dVolt = gfx21 != 0 && ReadInt(gfx21, Gfx21GetVoltageDefault, out var dv) ? dv : vVolt;
                                    if (rVolt.MinValue < 0 || isRdna3Plus)
                                    {
                                        voltageOffsetRange = ToRange(rVolt, dVolt);
                                        curVoltageOffset = vVolt;
                                        defVoltageOffset = dVolt;
                                    }
                                    else
                                    {
                                        voltageRange = ToRange(rVolt, dVolt);
                                        curVoltage = vVolt;
                                        defVoltage = dVolt;
                                    }
                                }
                            }
                            else
                            {
                                gfx1 = Query(rawGfx, "IADLXManualGraphicsTuning1");
                                if (gfx1 != 0)
                                {
                                    AdlxIntRange rFreq = default, rVolt = default;
                                    if (((delegate* unmanaged[Stdcall]<nint, AdlxIntRange*, AdlxIntRange*, int>)Slot(gfx1, Gfx1GetRanges))(gfx1, &rFreq, &rVolt) == AdlxOk)
                                    {
                                        maxClockRange = ToRange(rFreq, rFreq.MaxValue);
                                        voltageRange = ToRange(rVolt, rVolt.MaxValue);
                                        curMaxClock = rFreq.MaxValue;
                                        curVoltage = rVolt.MaxValue;
                                        defMaxClock = rFreq.MaxValue;
                                        defVoltage = rVolt.MaxValue;
                                    }
                                }
                            }
                        }
                    }
                    finally { ReleaseAll(gfx1, gfx21, gfx2, rawGfx); }
                }

                var vramRange = GpuTuningRange.Unsupported;
                int? curVram = null, defVram = null;
                var timingLevels = new List<string>();
                string? curTiming = null, defTiming = null;

                if (supVram)
                {
                    nint rawVram = 0, vram2 = 0, vram21 = 0;
                    try
                    {
                        if (GetTuningInterface(tuning, gpu, TuningGetManualVram, &rawVram))
                        {
                            vram2 = Query(rawVram, "IADLXManualVRAMTuning2");
                            if (vram2 != 0)
                            {
                                vram21 = Query(rawVram, "IADLXManualVRAMTuning2_1");
                                if (ReadRange(vram2, Vram2GetMaxFreqRange, out var rVram) && ReadInt(vram2, Vram2GetMaxFreq, out var vVram))
                                {
                                    var dVram = vram21 != 0 && ReadInt(vram21, Vram21GetMaxFreqDefault, out var dv) ? dv : vVram;
                                    vramRange = ToRange(rVram, dVram);
                                    curVram = vVram;
                                    defVram = dVram;
                                }
                                if (Read(vram2, Vram2IsSupportedTiming) == true)
                                {
                                    timingLevels.AddRange(ReadTimingLevels(vram2));
                                    if (ReadInt(vram2, Vram2GetTiming, out var tCur)) curTiming = TimingLabel(tCur);
                                    if (vram21 != 0 && ReadInt(vram21, Vram21GetTimingDefault, out var tDef)) defTiming = TimingLabel(tDef);
                                    defTiming ??= curTiming ?? "Default";
                                }
                            }
                        }
                    }
                    finally { ReleaseAll(vram21, vram2, rawVram); }
                }

                var powerRange = GpuTuningRange.Unsupported;
                int? curPower = null, defPower = null;
                if (supPower)
                {
                    nint rawPower = 0, power = 0, power1 = 0;
                    try
                    {
                        if (GetTuningInterface(tuning, gpu, TuningGetManualPower, &rawPower))
                        {
                            power = Query(rawPower, "IADLXManualPowerTuning");
                            if (power != 0)
                            {
                                power1 = Query(rawPower, "IADLXManualPowerTuning1");
                                if (ReadRange(power, PowerGetLimitRange, out var rPow) && ReadInt(power, PowerGetLimit, out var vPow))
                                {
                                    var dPow = power1 != 0 && ReadInt(power1, Power1GetLimitDefault, out var dp) ? dp : 0;
                                    powerRange = ToRange(rPow, dPow);
                                    curPower = vPow;
                                    defPower = dPow;
                                }
                            }
                        }
                    }
                    finally { ReleaseAll(power1, power, rawPower); }
                }

                var fanSpeedRange = GpuTuningRange.Unsupported;
                var fanTempRange = GpuTuningRange.Unsupported;
                var supportsZeroRpm = false;
                bool? curZeroRpm = null;
                IReadOnlyList<GpuFanPoint>? curFanPoints = null;

                if (supFan)
                {
                    nint rawFan = 0, fan = 0;
                    try
                    {
                        if (GetTuningInterface(tuning, gpu, TuningGetManualFan, &rawFan))
                        {
                            fan = Query(rawFan, "IADLXManualFanTuning");
                            if (fan != 0)
                            {
                                AdlxIntRange rSpeed = default, rTemp = default;
                                if (((delegate* unmanaged[Stdcall]<nint, AdlxIntRange*, AdlxIntRange*, int>)Slot(fan, FanGetRanges))(fan, &rSpeed, &rTemp) == AdlxOk)
                                {
                                    fanSpeedRange = ToRange(rSpeed, Math.Clamp(50, rSpeed.MinValue, rSpeed.MaxValue));
                                    fanTempRange = ToRange(rTemp, Math.Clamp(60, rTemp.MinValue, rTemp.MaxValue));
                                    curFanPoints = ReadFanCurvePoints(fan);
                                }
                                supportsZeroRpm = Read(fan, FanIsSupportedZeroRpm) == true;
                                if (supportsZeroRpm) curZeroRpm = Read(fan, FanGetZeroRpm);
                            }
                        }
                    }
                    finally { ReleaseAll(fan, rawFan); }
                }

                var atFactory = ReadGpuBool(tuning, gpu, TuningIsAtFactory) != false;
                var anyControl = minClockRange.Supported || maxClockRange.Supported || voltageRange.Supported || voltageOffsetRange.Supported
                    || vramRange.Supported || timingLevels.Count > 1 || powerRange.Supported || fanSpeedRange.Supported || supportsZeroRpm;
                var locked = !anyControl;

                var vfPoints = isRdna3Plus && voltageOffsetRange.Supported && maxClockRange.Supported
                    ? BuildRdnaCurvePreview(minClockRange.Supported ? minClockRange.Default : 500, maxClockRange.Default, curVoltageOffset ?? 0)
                    : Array.Empty<GpuVfPoint>();

                var caps = new GpuTuningCapabilities
                {
                    Vendor = "amd",
                    Generation = generation,
                    DeviceId = $"amd:{gpuName}",
                    DeviceName = gpuName,
                    Backend = "adlx",
                    IsLaptop = isLaptop,
                    LockedByMaker = locked,
                    LockReason = locked ? GpuTuningCapabilities.LockedMessage : null,
                    PersistsInDriver = true,
                    NeedsResidentForFanCurve = false,
                    PersistenceNote = "AMD Adrenalin persists manual tuning and hardware fan curves in the driver and resets automatically after a GPU hang.",
                    SupportsCurveEditor = isRdna3Plus && voltageOffsetRange.Supported,
                    SupportsUndervoltSearch = voltageRange.Supported || voltageOffsetRange.Supported,
                    CoreMinClockMhz = minClockRange,
                    CoreMaxClockMhz = maxClockRange,
                    VoltageMv = voltageRange,
                    VoltageOffsetMv = voltageOffsetRange,
                    MemoryMaxClockMhz = vramRange,
                    MemoryTimingLevels = timingLevels,
                    PowerLimitPercent = powerRange,
                    VfCurvePoints = vfPoints,
                    FanCount = fanSpeedRange.Supported ? 1 : 0,
                    SupportsHardwareFanCurve = fanSpeedRange.Supported,
                    SupportsSoftwareFanCurve = false,
                    SupportsZeroRpm = supportsZeroRpm,
                    FanMaxPoints = fanSpeedRange.Supported ? 5 : 0,
                    FanSpeedPercent = fanSpeedRange,
                    FanTempC = fanTempRange,
                };

                var current = new GpuManualProfile
                {
                    Mode = atFactory ? "auto" : "manual",
                    CoreMinClockMhz = curMinClock,
                    CoreMaxClockMhz = curMaxClock,
                    VoltageMv = curVoltage,
                    VoltageOffsetMv = curVoltageOffset,
                    MemoryMaxClockMhz = curVram,
                    MemoryTimingLevel = curTiming,
                    PowerLimitPercent = curPower,
                    ZeroRpm = curZeroRpm,
                    FanCurve = curFanPoints,
                    VfCurve = vfPoints.Length > 0 ? vfPoints : null,
                };

                var defaults = new GpuManualProfile
                {
                    Mode = "auto",
                    CoreMinClockMhz = defMinClock,
                    CoreMaxClockMhz = defMaxClock,
                    VoltageMv = defVoltage,
                    VoltageOffsetMv = defVoltageOffset,
                    MemoryMaxClockMhz = defVram,
                    MemoryTimingLevel = defTiming,
                    PowerLimitPercent = defPower,
                    ZeroRpm = supportsZeroRpm ? true : null,
                    FanCurve = curFanPoints,
                };

                return (caps, current, defaults);
            }
            finally { ReleaseAll(tuning); }
        });

        return result ?? (fallbackCaps, GpuManualProfile.Auto, GpuManualProfile.Auto);
    }

    internal static GpuApplyResult ApplyManualTuning(GpuManualProfile profile)
    {
        if (profile.Mode != "manual")
            return ResetTuning() ? new(true, false, null, "adlx") : new(false, false, "AMD ADLX could not reset GPU tuning to factory defaults.", "adlx");

        var applied = WithGpu<GpuApplyResult?>("manual tuning apply", (system, gpu) =>
        {
            nint tuning = 0;
            try
            {
                if (Call(system, SystemGetGpuTuning, &tuning) != AdlxOk || tuning == 0)
                    return new GpuApplyResult(false, false, "AMD ADLX tuning service did not answer.", "adlx");

                if (profile.CoreMinClockMhz is not null || profile.CoreMaxClockMhz is not null || profile.VoltageMv is not null || profile.VoltageOffsetMv is not null)
                {
                    nint rawGfx = 0, gfx2 = 0;
                    try
                    {
                        if (GetTuningInterface(tuning, gpu, TuningGetManualGfx, &rawGfx))
                        {
                            gfx2 = Query(rawGfx, "IADLXManualGraphicsTuning2");
                            if (gfx2 != 0)
                            {
                                if (profile.CoreMinClockMhz is { } minC && !SetInt(gfx2, Gfx2SetMinFreq, minC))
                                    return new GpuApplyResult(false, false, "AMD driver refused GPU minimum clock.", "adlx");
                                if (profile.CoreMaxClockMhz is { } maxC && !SetInt(gfx2, Gfx2SetMaxFreq, maxC))
                                    return new GpuApplyResult(false, false, "AMD driver refused GPU maximum clock.", "adlx");
                                var volt = profile.VoltageOffsetMv ?? profile.VoltageMv;
                                if (volt is { } v && !SetInt(gfx2, Gfx2SetVoltage, v))
                                    return new GpuApplyResult(false, false, "AMD driver refused GPU voltage.", "adlx");
                            }
                        }
                    }
                    finally { ReleaseAll(gfx2, rawGfx); }
                }

                if (profile.MemoryMaxClockMhz is not null || profile.MemoryTimingLevel is not null)
                {
                    nint rawVram = 0, vram2 = 0;
                    try
                    {
                        if (GetTuningInterface(tuning, gpu, TuningGetManualVram, &rawVram))
                        {
                            vram2 = Query(rawVram, "IADLXManualVRAMTuning2");
                            if (vram2 != 0)
                            {
                                if (profile.MemoryMaxClockMhz is { } vramC && !SetInt(vram2, Vram2SetMaxFreq, vramC))
                                    return new GpuApplyResult(false, false, "AMD driver refused VRAM clock.", "adlx");
                                if (profile.MemoryTimingLevel is { } level && !SetInt(vram2, Vram2SetTiming, TimingCode(level)))
                                    return new GpuApplyResult(false, false, "AMD driver refused VRAM timing level.", "adlx");
                            }
                        }
                    }
                    finally { ReleaseAll(vram2, rawVram); }
                }

                if (profile.PowerLimitPercent is { } powerPct)
                {
                    nint rawPower = 0, power = 0;
                    try
                    {
                        if (GetTuningInterface(tuning, gpu, TuningGetManualPower, &rawPower))
                        {
                            power = Query(rawPower, "IADLXManualPowerTuning");
                            if (power != 0 && !SetInt(power, PowerSetLimit, powerPct))
                                return new GpuApplyResult(false, false, "AMD driver refused power limit.", "adlx");
                        }
                    }
                    finally { ReleaseAll(power, rawPower); }
                }

                if (profile.ZeroRpm is not null || profile.FanCurve is { Count: > 0 })
                {
                    nint rawFan = 0, fan = 0;
                    try
                    {
                        if (GetTuningInterface(tuning, gpu, TuningGetManualFan, &rawFan))
                        {
                            fan = Query(rawFan, "IADLXManualFanTuning");
                            if (fan != 0)
                            {
                                if (profile.ZeroRpm is { } zero && Read(fan, FanIsSupportedZeroRpm) == true)
                                    _ = SetBool(fan, FanSetZeroRpm, zero);
                                if (profile.FanCurve is { Count: > 0 } points && !WriteFanCurvePoints(fan, points))
                                    return new GpuApplyResult(false, false, "AMD driver refused the 5-point fan curve.", "adlx");
                            }
                        }
                    }
                    finally { ReleaseAll(fan, rawFan); }
                }

                return new GpuApplyResult(true, false, null, "adlx");
            }
            finally { ReleaseAll(tuning); }
        });

        return applied ?? new(false, false, "AMD ADLX did not answer.", "adlx");
    }

    private static GpuTuningRange ToRange(AdlxIntRange r, int defaultValue)
    {
        if (r.MaxValue < r.MinValue) return GpuTuningRange.Unsupported;
        var step = Math.Max(1, r.Step);
        var def = Math.Clamp(defaultValue, r.MinValue, r.MaxValue);
        return new(r.MinValue, r.MaxValue, step, def, r.MaxValue > r.MinValue);
    }

    private static bool IsTuningSupported(nint tuning, nint gpu, int slot) => ReadGpuBool(tuning, gpu, slot) == true;

    private static bool? ReadGpuBool(nint tuning, nint gpu, int slot)
    {
        byte supported = 0;
        return ((delegate* unmanaged[Stdcall]<nint, nint, byte*, int>)Slot(tuning, slot))(tuning, gpu, &supported) == AdlxOk ? supported != 0 : null;
    }

    private static bool GetTuningInterface(nint tuning, nint gpu, int slot, nint* target) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(tuning, slot))(tuning, gpu, target) == AdlxOk && *target != 0;

    private static bool ReadRange(nint item, int slot, out AdlxIntRange range)
    {
        AdlxIntRange local = default;
        var ok = ((delegate* unmanaged[Stdcall]<nint, AdlxIntRange*, int>)Slot(item, slot))(item, &local) == AdlxOk;
        range = local;
        return ok;
    }

    private static bool ReadInt(nint item, int slot, out int value)
    {
        int local = 0;
        var ok = ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(item, slot))(item, &local) == AdlxOk;
        value = local;
        return ok;
    }

    private static bool SetInt(nint item, int slot, int value)
    {
        var r = ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(item, slot))(item, value);
        return r is AdlxOk or AdlxAlreadyEnabled;
    }

    private static bool SetBool(nint item, int slot, bool value)
    {
        var r = ((delegate* unmanaged[Stdcall]<nint, byte, int>)Slot(item, slot))(item, value ? (byte)1 : (byte)0);
        return r is AdlxOk or AdlxAlreadyEnabled;
    }

    private static IReadOnlyList<string> ReadTimingLevels(nint vram2)
    {
        var levels = new List<string>();
        nint list = 0;
        try
        {
            if (Call(vram2, Vram2GetTimingList, &list) != AdlxOk || list == 0) return levels;
            var size = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(list, ListSize))(list);
            for (uint i = 0; i < size && i < 8; i++)
            {
                nint desc = 0;
                try
                {
                    if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(list, ListAt))(list, i, &desc) != AdlxOk || desc == 0) continue;
                    if (ReadInt(desc, TimingDescGetValue, out var code))
                    {
                        var label = TimingLabel(code);
                        if (!levels.Contains(label)) levels.Add(label);
                    }
                }
                finally { ReleaseAll(desc); }
            }
        }
        finally { ReleaseAll(list); }
        return levels;
    }

    private static IReadOnlyList<GpuFanPoint> ReadFanCurvePoints(nint fan)
    {
        var points = new List<GpuFanPoint>();
        nint list = 0;
        try
        {
            if (Call(fan, FanGetStates, &list) != AdlxOk || list == 0) return points;
            var size = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(list, ListSize))(list);
            for (uint i = 0; i < size && i < 5; i++)
            {
                nint state = 0;
                try
                {
                    if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(list, ListAt))(list, i, &state) != AdlxOk || state == 0) continue;
                    if (ReadInt(state, FanStateGetTemp, out var temp) && ReadInt(state, FanStateGetSpeed, out var speed))
                        points.Add(new(temp, speed));
                }
                finally { ReleaseAll(state); }
            }
        }
        finally { ReleaseAll(list); }
        return points;
    }

    private static bool WriteFanCurvePoints(nint fan, IReadOnlyList<GpuFanPoint> points)
    {
        nint list = 0;
        try
        {
            if (Call(fan, FanGetEmptyStates, &list) != AdlxOk || list == 0) return false;
            var size = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(list, ListSize))(list);
            var count = Math.Min((int)size, points.Count);
            if (count == 0) return false;
            for (var i = 0; i < count; i++)
            {
                nint state = 0;
                try
                {
                    if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(list, ListAt))(list, (uint)i, &state) != AdlxOk || state == 0)
                        return false;
                    if (!SetInt(state, FanStateSetTemp, points[i].TempC) || !SetInt(state, FanStateSetSpeed, points[i].SpeedPercent))
                        return false;
                }
                finally { ReleaseAll(state); }
            }
            int errorIndex = -1;
            if (((delegate* unmanaged[Stdcall]<nint, nint, int*, int>)Slot(fan, FanIsValidStates))(fan, list, &errorIndex) != AdlxOk)
                return false;
            return ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(fan, FanSetStates))(fan, list) is AdlxOk or AdlxAlreadyEnabled;
        }
        finally { ReleaseAll(list); }
    }

    private static string TimingLabel(int code) => code switch
    {
        TimingFast1 => "Fast Timing",
        TimingFast2 => "Fast Timing Level 2",
        _ => "Default",
    };

    private static int TimingCode(string level) => level.ToLowerInvariant() switch
    {
        "fast timing" => TimingFast1,
        "fast timing level 2" => TimingFast2,
        _ => TimingDefault,
    };

    private static GpuVfPoint[] BuildRdnaCurvePreview(int minMhz, int maxMhz, int voltageOffsetMv)
    {
        var points = new GpuVfPoint[5];
        var span = Math.Max(200, maxMhz - minMhz);
        for (var i = 0; i < points.Length; i++)
        {
            var frac = i / 4.0;
            var mv = 750 + (int)Math.Round(frac * 350);
            var clk = minMhz + (int)Math.Round(frac * span);
            points[i] = new GpuVfPoint(i, mv, clk, voltageOffsetMv);
        }
        return points;
    }
}

internal sealed class AmdAdlxManualTuningBackend(Func<bool> isLaptop) : IGpuTuningBackend
{
    public string BackendId => "adlx";
    public string Vendor => "amd";

    public GpuTuningCapabilities Probe() => AmdAdlx.ReadManualTuningState(isLaptop()).Caps;
    public GpuManualProfile ReadCurrent() => AmdAdlx.ReadManualTuningState(isLaptop()).Current;
    public GpuManualProfile ReadFactoryDefaults() => AmdAdlx.ReadManualTuningState(isLaptop()).Defaults;
    public GpuApplyResult Apply(GpuManualProfile profile) => AmdAdlx.ApplyManualTuning(profile);
    public GpuApplyResult ResetToFactory() => AmdAdlx.ResetTuning()
        ? new(true, false, null, "adlx")
        : new(false, false, "AMD ADLX could not reset GPU tuning to factory defaults.", "adlx");
    public bool AcceptWaiver() => true;
}
