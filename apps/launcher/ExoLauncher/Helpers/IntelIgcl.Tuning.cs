using System.Runtime.InteropServices;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

internal static unsafe partial class IntelIgcl
{
    private const int FanSpeedUnitsPercent = 1;
    private const int MaxFanTablePoints = 32;

    [StructLayout(LayoutKind.Sequential)]
    internal struct OcControlInfo
    {
        public byte IsSupported;
        public byte IsRelative;
        public byte IsReference;
        public int Units;
        public double Min;
        public double Max;
        public double Step;
        public double Default;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OcProperties
    {
        public uint Size;
        public byte Version;
        public byte IsSupported;
        public OcControlInfo GpuFrequencyOffset;
        public OcControlInfo GpuVoltageOffset;
        public OcControlInfo VramFrequencyOffset;
        public OcControlInfo VramVoltageOffset;
        public OcControlInfo PowerLimit;
        public OcControlInfo TemperatureLimit;
        public OcControlInfo VramMemorySpeedLimit;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FanProperties
    {
        public uint Size;
        public byte Version;
        public byte CanControl;
        public uint SupportedModes;
        public uint SupportedUnits;
        public int MaxRpm;
        public int MaxPoints;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FanTempSpeed
    {
        public int Temperature;
        public int Speed;
        public int Units;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FanSpeedTable
    {
        public uint Size;
        public byte Version;
        public int NumPoints;
        public fixed byte TableBytes[MaxFanTablePoints * 12];
    }

    internal static (GpuTuningCapabilities Caps, GpuManualProfile Current, GpuManualProfile Defaults) ReadManualTuningState(bool isLaptop, Func<string, bool> isWaiverAccepted)
    {
        var fallbackCaps = new GpuTuningCapabilities
        {
            Vendor = "intel",
            Backend = Present ? "igcl" : "none",
            IsLaptop = isLaptop,
            LockedByMaker = true,
            LockReason = Present ? GpuTuningCapabilities.LockedMessage : "Intel IGCL driver library (ControlLib.dll) is not installed.",
            RequiresWaiver = true,
            WaiverText = GpuTuningCapabilities.IntelWaiverBody,
        };

        var result = WithSession<(GpuTuningCapabilities Caps, GpuManualProfile Current, GpuManualProfile Defaults)?>("manual tuning read", session =>
        {
            var (device, shortName, _) = session.Main();
            var gpuName = shortName ?? "Intel Arc";
            var generation = GpuTuningPolicy.DetectGeneration("intel", gpuName);
            var deviceId = $"intel:{gpuName}";

            if (isLaptop)
            {
                return (new GpuTuningCapabilities
                {
                    Vendor = "intel",
                    Generation = generation,
                    DeviceId = deviceId,
                    DeviceName = gpuName,
                    Backend = "igcl",
                    IsLaptop = true,
                    LockedByMaker = true,
                    LockReason = GpuTuningCapabilities.LockedMessage,
                    RequiresWaiver = true,
                    WaiverAccepted = isWaiverAccepted(deviceId),
                    WaiverText = GpuTuningCapabilities.IntelWaiverBody,
                }, GpuManualProfile.Auto, GpuManualProfile.Auto);
            }

            var props = new OcProperties { Size = (uint)sizeof(OcProperties), Version = 0 };
            var hasOc = TryExport(session, "ctlOverclockGetProperties", out var getPropsFn)
                && ((delegate* unmanaged[Cdecl]<nint, OcProperties*, int>)getPropsFn)(device, &props) == Success
                && props.IsSupported != 0;

            var freqRange = hasOc ? ToRange(props.GpuFrequencyOffset, 5) : GpuTuningRange.Unsupported;
            var voltRange = hasOc ? ToRange(props.GpuVoltageOffset, 5) : GpuTuningRange.Unsupported;
            var powerRange = hasOc ? ToRange(props.PowerLimit, 5) : GpuTuningRange.Unsupported;
            var tempRange = hasOc ? ToRange(props.TemperatureLimit, 1) : GpuTuningRange.Unsupported;
            var vramRange = hasOc ? ToRange(props.VramMemorySpeedLimit, 50) : GpuTuningRange.Unsupported;

            int? curFreq = null, curVolt = null, curPower = null, curTemp = null, curVram = null;
            if (hasOc)
            {
                curFreq = ReadDoubleAsInt(session, "ctlOverclockGpuFrequencyOffsetGetV2", device);
                curVolt = ReadDoubleAsInt(session, "ctlOverclockGpuVoltageOffsetGetV2", device);
                curPower = ReadDoubleAsInt(session, "ctlOverclockPowerLimitGetV2", device);
                curTemp = ReadDoubleAsInt(session, "ctlOverclockTemperatureLimitGetV2", device);
                curVram = ReadDoubleAsInt(session, "ctlOverclockVramMemorySpeedLimitGet", device);
            }

            var (fanCount, canControlFan, maxPoints) = ReadFanCapabilities(session, device);
            var anyControl = freqRange.Supported || voltRange.Supported || powerRange.Supported || tempRange.Supported || vramRange.Supported || canControlFan;
            var waived = isWaiverAccepted(deviceId);

            var caps = new GpuTuningCapabilities
            {
                Vendor = "intel",
                Generation = generation,
                DeviceId = deviceId,
                DeviceName = gpuName,
                Backend = "igcl",
                IsLaptop = false,
                LockedByMaker = !anyControl,
                LockReason = anyControl ? null : GpuTuningCapabilities.LockedMessage,
                RequiresWaiver = true,
                WaiverAccepted = waived,
                WaiverText = GpuTuningCapabilities.IntelWaiverBody,
                PersistsInDriver = false,
                NeedsResidentForFanCurve = false,
                PersistenceNote = "Intel Arc hardware fan tables run in the driver; clock, voltage, and power settings reset on reboot and re-apply at sign-in.",
                SupportsCurveEditor = false,
                SupportsUndervoltSearch = voltRange.Supported && voltRange.Min < 0,
                CoreClockOffsetMhz = freqRange,
                VoltageOffsetMv = voltRange,
                PowerLimitWatts = powerRange,
                TempLimitC = tempRange,
                MemoryMaxClockMhz = vramRange,
                FanCount = fanCount,
                SupportsHardwareFanCurve = canControlFan,
                SupportsSoftwareFanCurve = false,
                SupportsZeroRpm = false,
                FanMaxPoints = canControlFan ? Math.Clamp(maxPoints, 2, 10) : 0,
                FanSpeedPercent = canControlFan ? new GpuTuningRange(0, 100, 1, 50, true) : GpuTuningRange.Unsupported,
                FanTempC = canControlFan ? new GpuTuningRange(30, 100, 1, 60, true) : GpuTuningRange.Unsupported,
            };

            var isManual = (curFreq ?? 0) != 0 || (curVolt ?? 0) != 0;
            var current = new GpuManualProfile
            {
                Mode = isManual ? "manual" : "auto",
                CoreClockOffsetMhz = curFreq,
                VoltageOffsetMv = curVolt,
                PowerLimitWatts = curPower,
                TempLimitC = curTemp,
                MemoryMaxClockMhz = curVram,
            };

            var defaults = new GpuManualProfile
            {
                Mode = "auto",
                CoreClockOffsetMhz = freqRange.Supported ? freqRange.Default : 0,
                VoltageOffsetMv = voltRange.Supported ? voltRange.Default : 0,
                PowerLimitWatts = powerRange.Supported ? powerRange.Default : null,
                TempLimitC = tempRange.Supported ? tempRange.Default : null,
                MemoryMaxClockMhz = vramRange.Supported ? vramRange.Default : null,
            };

            return (caps, current, defaults);
        });

        return result ?? (fallbackCaps, GpuManualProfile.Auto, GpuManualProfile.Auto);
    }

    internal static bool SetOverclockWaiver()
    {
        return WithSession("overclock waiver", session =>
        {
            var (device, _, _) = session.Main();
            if (!TryExport(session, "ctlOverclockWaiverSet", out var fn)) return false;
            return ((delegate* unmanaged[Cdecl]<nint, int>)fn)(device) == Success;
        });
    }

    internal static GpuApplyResult ApplyManualTuning(GpuManualProfile profile)
    {
        if (profile.Mode != "manual") return ResetManualTuning();

        var result = WithSession<GpuApplyResult?>("manual tuning apply", session =>
        {
            var (device, _, _) = session.Main();
            if (!TryExport(session, "ctlOverclockWaiverSet", out var waiverFn)
                || ((delegate* unmanaged[Cdecl]<nint, int>)waiverFn)(device) != Success)
                return new GpuApplyResult(false, false, "Intel IGCL waiver acknowledgement failed.", "igcl");

            if (profile.CoreClockOffsetMhz is { } freq && !WriteDouble(session, "ctlOverclockGpuFrequencyOffsetSetV2", device, freq))
                return new GpuApplyResult(false, false, "Intel IGCL refused GPU frequency offset.", "igcl");
            if (profile.VoltageOffsetMv is { } volt && !WriteDouble(session, "ctlOverclockGpuVoltageOffsetSetV2", device, volt))
                return new GpuApplyResult(false, false, "Intel IGCL refused GPU voltage offset.", "igcl");
            if (profile.PowerLimitWatts is { } watts && !WriteDouble(session, "ctlOverclockPowerLimitSetV2", device, watts))
                return new GpuApplyResult(false, false, "Intel IGCL refused power limit.", "igcl");
            if (profile.TempLimitC is { } temp && !WriteDouble(session, "ctlOverclockTemperatureLimitSetV2", device, temp))
                return new GpuApplyResult(false, false, "Intel IGCL refused temperature limit.", "igcl");
            if (profile.MemoryMaxClockMhz is { } vram && !WriteDouble(session, "ctlOverclockVramMemorySpeedLimitSet", device, vram))
                return new GpuApplyResult(false, false, "Intel IGCL refused VRAM speed limit.", "igcl");
            if (profile.FanCurve is { Count: > 0 } points && !WriteFanTable(session, device, points))
                return new GpuApplyResult(false, false, "Intel IGCL refused the fan speed table.", "igcl");

            return new GpuApplyResult(true, false, null, "igcl");
        });

        return result ?? new(false, false, "Intel IGCL did not answer.", "igcl");
    }

    internal static GpuApplyResult ResetManualTuning()
    {
        var result = WithSession<GpuApplyResult?>("manual tuning reset", session =>
        {
            var (device, _, _) = session.Main();
            var ok = true;
            if (TryExport(session, "ctlOverclockResetToDefault", out var resetFn))
                ok &= ((delegate* unmanaged[Cdecl]<nint, int>)resetFn)(device) == Success;
            _ = ResetFansToDefault(session, device);
            return ok
                ? new GpuApplyResult(true, false, null, "igcl")
                : new GpuApplyResult(false, false, "Intel IGCL could not reset overclocking defaults.", "igcl");
        });
        return result ?? new(false, false, "Intel IGCL did not answer.", "igcl");
    }

    private static bool TryExport(Session session, string name, out nint address)
    {
        try
        {
            address = session.Export(name);
            return address != 0;
        }
        catch (InvalidOperationException)
        {
            address = 0;
            return false;
        }
    }

    private static GpuTuningRange ToRange(OcControlInfo info, int fallbackStep)
    {
        if (info.IsSupported == 0) return GpuTuningRange.Unsupported;
        var min = (int)Math.Round(info.Min);
        var max = (int)Math.Round(info.Max);
        if (max <= min) return GpuTuningRange.Unsupported;
        var step = info.Step >= 1.0 ? (int)Math.Round(info.Step) : fallbackStep;
        var def = Math.Clamp((int)Math.Round(info.Default), min, max);
        return new(min, max, Math.Max(1, step), def, true);
    }

    private static int? ReadDoubleAsInt(Session session, string exportName, nint device)
    {
        if (!TryExport(session, exportName, out var fn)) return null;
        double value = 0;
        return ((delegate* unmanaged[Cdecl]<nint, double*, int>)fn)(device, &value) == Success ? (int)Math.Round(value) : null;
    }

    private static bool WriteDouble(Session session, string exportName, nint device, double value)
    {
        if (!TryExport(session, exportName, out var fn)) return false;
        return ((delegate* unmanaged[Cdecl]<nint, double, int>)fn)(device, value) == Success;
    }

    private static (int Count, bool CanControl, int MaxPoints) ReadFanCapabilities(Session session, nint device)
    {
        if (!TryExport(session, "ctlEnumFans", out var enumFn) || !TryExport(session, "ctlFanGetProperties", out var propsFn))
            return (0, false, 0);
        uint count = 0;
        if (((delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)enumFn)(device, &count, null) != Success || count == 0)
            return (0, false, 0);
        var handles = stackalloc nint[(int)Math.Min(count, 8u)];
        count = Math.Min(count, 8u);
        if (((delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)enumFn)(device, &count, handles) != Success || count == 0)
            return (0, false, 0);
        var props = new FanProperties { Size = (uint)sizeof(FanProperties), Version = 0 };
        if (((delegate* unmanaged[Cdecl]<nint, FanProperties*, int>)propsFn)(handles[0], &props) != Success)
            return ((int)count, false, 0);
        return ((int)count, props.CanControl != 0, props.MaxPoints > 0 ? props.MaxPoints : 5);
    }

    private static bool WriteFanTable(Session session, nint device, IReadOnlyList<GpuFanPoint> points)
    {
        if (!TryExport(session, "ctlEnumFans", out var enumFn) || !TryExport(session, "ctlFanSetSpeedTableMode", out var tableFn))
            return false;
        uint count = 0;
        if (((delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)enumFn)(device, &count, null) != Success || count == 0)
            return false;
        var handles = stackalloc nint[(int)Math.Min(count, 8u)];
        count = Math.Min(count, 8u);
        if (((delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)enumFn)(device, &count, handles) != Success || count == 0)
            return false;

        var table = new FanSpeedTable
        {
            Size = (uint)sizeof(FanSpeedTable),
            Version = 0,
            NumPoints = Math.Min(points.Count, MaxFanTablePoints),
        };
        var items = (FanTempSpeed*)table.TableBytes;
        for (var i = 0; i < table.NumPoints; i++)
        {
            items[i] = new FanTempSpeed
            {
                Temperature = points[i].TempC,
                Speed = points[i].SpeedPercent,
                Units = FanSpeedUnitsPercent,
            };
        }

        var ok = true;
        for (uint i = 0; i < count; i++)
            ok &= ((delegate* unmanaged[Cdecl]<nint, FanSpeedTable*, int>)tableFn)(handles[i], &table) == Success;
        return ok;
    }

    private static bool ResetFansToDefault(Session session, nint device)
    {
        if (!TryExport(session, "ctlEnumFans", out var enumFn) || !TryExport(session, "ctlFanSetDefaultMode", out var defFn))
            return false;
        uint count = 0;
        if (((delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)enumFn)(device, &count, null) != Success || count == 0)
            return false;
        var handles = stackalloc nint[(int)Math.Min(count, 8u)];
        count = Math.Min(count, 8u);
        if (((delegate* unmanaged[Cdecl]<nint, uint*, nint*, int>)enumFn)(device, &count, handles) != Success || count == 0)
            return false;
        var ok = true;
        for (uint i = 0; i < count; i++)
            ok &= ((delegate* unmanaged[Cdecl]<nint, int>)defFn)(handles[i]) == Success;
        return ok;
    }
}

internal sealed class IntelIgclManualTuningBackend(Func<bool> isLaptop, Func<string, bool> isWaiverAccepted) : IGpuTuningBackend
{
    public string BackendId => "igcl";
    public string Vendor => "intel";

    public GpuTuningCapabilities Probe() => IntelIgcl.ReadManualTuningState(isLaptop(), isWaiverAccepted).Caps;
    public GpuManualProfile ReadCurrent() => IntelIgcl.ReadManualTuningState(isLaptop(), isWaiverAccepted).Current;
    public GpuManualProfile ReadFactoryDefaults() => IntelIgcl.ReadManualTuningState(isLaptop(), isWaiverAccepted).Defaults;
    public GpuApplyResult Apply(GpuManualProfile profile) => IntelIgcl.ApplyManualTuning(profile);
    public GpuApplyResult ResetToFactory() => IntelIgcl.ResetManualTuning();
    public bool AcceptWaiver() => IntelIgcl.SetOverclockWaiver();
}
