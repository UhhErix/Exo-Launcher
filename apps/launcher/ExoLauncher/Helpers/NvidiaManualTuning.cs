using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Helpers;

internal static class NvApiInterfaceIds
{
    internal const uint EnumPhysicalGpus = 0xE5AC921F;
    internal const uint GetFullName = 0xCEEE8E9F;
    internal const uint GetVfpCurve = 0x21537AD4;
    internal const uint GetClockBoostTable = 0x23F1B133;
    internal const uint SetClockBoostTable = 0x0733E009;
    internal const uint GetClockBoostLock = 0xE440B867;
    internal const uint SetClockBoostLock = 0x39442CFB;
    internal const uint GetClockBoostRanges = 0x64B43A6A;
    internal const uint GetClockBoostMask = 0x507B4B59;
    internal const uint GetPstates20 = 0x6FF81213;
    internal const uint SetPstates20 = 0x0F4DAE6B;
    internal const uint ClientPowerPoliciesGetInfo = 0x34206D86;
    internal const uint ClientPowerPoliciesGetStatus = 0x70916171;
    internal const uint ClientPowerPoliciesSetStatus = 0xAD95F5ED;
    internal const uint ClientThermalPoliciesGetInfo = 0x0D258BB5;
    internal const uint ClientThermalPoliciesGetLimit = 0xE9C425A1;
    internal const uint ClientThermalPoliciesSetLimit = 0x34C0B13D;
    internal const uint ClientFanCoolersGetInfo = 0xFB85B01E;
    internal const uint ClientFanCoolersGetControl = 0x814B209F;
    internal const uint ClientFanCoolersSetControl = 0xA58971A5;
    internal const uint GetCoolerSettings = 0xDA141340;
    internal const uint SetCoolerLevels = 0x891FA0AE;
}

internal sealed unsafe class NvidiaNvApiManualTuningBackend : IGpuTuningBackend
{
    private const int NvApiOk = 0;
    private const int MaxVfEntries = 80;
    private const int MaxCoolers = 32;
    private const uint FanModeAuto = 0;
    private const uint FanModeManual = 1;

    private readonly Func<uint, nint> _queryInterface;
    private readonly Func<bool> _isLaptop;
    private readonly Lock _gate = new();
    private GpuManualProfile? _defaults;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint NativeQueryInterface(uint id);

    internal NvidiaNvApiManualTuningBackend(Func<bool> isLaptop, Func<uint, nint>? queryInterface = null)
    {
        _isLaptop = isLaptop;
        _queryInterface = queryInterface ?? QueryNativeSafe;
    }

    public string BackendId => "nvapi";
    public string Vendor => "nvidia";

    private static nint QueryNativeSafe(uint id)
    {
        try { return NvidiaProfiles.Available ? NativeQueryInterface(id) : 0; }
        catch (DllNotFoundException) { return 0; }
        catch (EntryPointNotFoundException) { return 0; }
    }

    public GpuTuningCapabilities Probe()
    {
        var laptop = _isLaptop();
        lock (_gate)
        {
            if (laptop)
            {
                return new GpuTuningCapabilities
                {
                    Vendor = "nvidia",
                    Backend = "nvapi",
                    IsLaptop = true,
                    LockedByMaker = true,
                    LockReason = GpuTuningCapabilities.LockedMessage,
                };
            }

            if (!TryGetPrimaryGpu(out var gpu, out var gpuName))
            {
                return new GpuTuningCapabilities
                {
                    Vendor = "nvidia",
                    Backend = "none",
                    FallbackReason = "NVAPI could not enumerate an NVIDIA GPU; falling back to NVML.",
                };
            }

            var hasVfRead = _queryInterface(NvApiInterfaceIds.GetVfpCurve) != 0
                && _queryInterface(NvApiInterfaceIds.GetClockBoostTable) != 0
                && _queryInterface(NvApiInterfaceIds.SetClockBoostTable) != 0;

            if (!hasVfRead)
            {
                return new GpuTuningCapabilities
                {
                    Vendor = "nvidia",
                    DeviceName = gpuName,
                    Generation = GpuTuningPolicy.DetectGeneration("nvidia", gpuName),
                    Backend = "none",
                    FallbackReason = "Private NVAPI V/F curve interface is unavailable on this driver; switched to NVML.",
                };
            }

            var vfPoints = ReadVfCurveLocked(gpu);
            var clockRanges = ReadClockRangesLocked(gpu);
            var powerRange = ReadPowerRangeLocked(gpu, out var curPowerPct);
            var tempRange = ReadTempRangeLocked(gpu, out var curTempC);
            var fanInfo = ReadFanInfoLocked(gpu);

            if (vfPoints.Count == 0 && !powerRange.Supported && !tempRange.Supported && fanInfo.Count == 0)
            {
                return new GpuTuningCapabilities
                {
                    Vendor = "nvidia",
                    DeviceName = gpuName,
                    Generation = GpuTuningPolicy.DetectGeneration("nvidia", gpuName),
                    Backend = "none",
                    FallbackReason = "Private NVAPI struct version did not answer on this driver; switched to NVML.",
                };
            }

            var generation = GpuTuningPolicy.DetectGeneration("nvidia", gpuName);
            _defaults ??= new GpuManualProfile
            {
                Mode = "auto",
                CoreClockOffsetMhz = 0,
                MemoryClockOffsetMhz = 0,
                PowerLimitPercent = powerRange.Supported ? powerRange.Default : curPowerPct,
                TempLimitC = tempRange.Supported ? tempRange.Default : curTempC,
                VfCurve = vfPoints.Select(p => p with { OffsetMhz = 0 }).ToList(),
            };

            return new GpuTuningCapabilities
            {
                Vendor = "nvidia",
                Generation = generation,
                DeviceId = $"nvidia:{gpuName}",
                DeviceName = gpuName,
                Backend = "nvapi",
                IsLaptop = false,
                LockedByMaker = false,
                PersistsInDriver = false,
                NeedsResidentForFanCurve = fanInfo.Count > 0,
                PersistenceNote = fanInfo.Count > 0
                    ? "Clocks and limits re-apply at sign-in; software fan curves stay active while Exo is open."
                    : "Clocks and limits reset on reboot and re-apply at sign-in through Exo's guarded task.",
                SupportsCurveEditor = vfPoints.Count > 0,
                SupportsUndervoltSearch = vfPoints.Count > 0 || clockRanges.Core.Supported,
                CoreClockOffsetMhz = clockRanges.Core,
                MemoryClockOffsetMhz = clockRanges.Memory,
                PowerLimitPercent = powerRange,
                TempLimitC = tempRange,
                ClockLockMhz = vfPoints.Count > 0
                    ? new GpuTuningRange(vfPoints.Min(p => p.BaseClockMhz), vfPoints.Max(p => p.BaseClockMhz), 15, 0, true)
                    : GpuTuningRange.Unsupported,
                VfCurvePoints = vfPoints,
                FanCount = fanInfo.Count,
                SupportsHardwareFanCurve = false,
                SupportsSoftwareFanCurve = fanInfo.Count > 0,
                SupportsZeroRpm = false,
                FanMaxPoints = fanInfo.Count > 0 ? 5 : 0,
                FanSpeedPercent = fanInfo.Count > 0 ? new GpuTuningRange(fanInfo.MinSpeed, fanInfo.MaxSpeed, 1, 50, true) : GpuTuningRange.Unsupported,
                FanTempC = fanInfo.Count > 0 ? new GpuTuningRange(30, 95, 1, 60, true) : GpuTuningRange.Unsupported,
            };
        }
    }

    public GpuManualProfile ReadCurrent()
    {
        lock (_gate)
        {
            if (!TryGetPrimaryGpu(out var gpu, out _)) return GpuManualProfile.Auto;
            var vf = ReadVfCurveLocked(gpu);
            _ = ReadPowerRangeLocked(gpu, out var curPower);
            _ = ReadTempRangeLocked(gpu, out var curTemp);
            var hasOffsets = vf.Any(p => p.OffsetMhz != 0);
            return new GpuManualProfile
            {
                Mode = hasOffsets ? "manual" : "auto",
                CoreClockOffsetMhz = vf.Count > 0 ? vf[vf.Count / 2].OffsetMhz : 0,
                PowerLimitPercent = curPower,
                TempLimitC = curTemp,
                VfCurve = vf.Count > 0 ? vf : null,
            };
        }
    }

    public GpuManualProfile ReadFactoryDefaults()
    {
        lock (_gate) return _defaults ?? GpuManualProfile.Auto;
    }

    public GpuApplyResult Apply(GpuManualProfile profile)
    {
        if (HostMode.IsDisposable) return new(false, false, HostMode.Refusal, "nvml");
        if (profile.Mode != "manual") return ResetToFactory();
        lock (_gate)
        {
            if (!TryGetPrimaryGpu(out var gpu, out _))
                return new(false, true, "NVAPI GPU handle unavailable; switching to NVML.", "nvapi");

            if (profile.VfCurve is { Count: > 0 } curve)
            {
                if (!WriteVfCurveLocked(gpu, curve))
                    return new(false, true, "Private NVAPI V/F curve write failed; switching to NVML.", "nvapi");
            }
            else if (profile.CoreClockOffsetMhz is { } coreOff)
            {
                var currentCurve = ReadVfCurveLocked(gpu);
                if (currentCurve.Count > 0)
                {
                    var shifted = currentCurve.Select(p => p with { OffsetMhz = coreOff }).ToList();
                    if (!WriteVfCurveLocked(gpu, shifted))
                        return new(false, true, "Private NVAPI boost table write failed; switching to NVML.", "nvapi");
                }
            }

            if (profile.ClockLockMhz is { } lockMhz && lockMhz > 0)
            {
                if (!SetClockBoostLockLocked(gpu, lockMhz))
                    return new(false, true, "Private NVAPI clock boost lock failed; switching to NVML.", "nvapi");
            }

            if (profile.MemoryClockOffsetMhz is { } memOff && memOff != 0)
            {
                if (!WritePstates20MemoryOffsetLocked(gpu, memOff))
                    return new(false, true, "Private NVAPI memory offset failed; switching to NVML.", "nvapi");
            }

            if (profile.PowerLimitPercent is { } powerPct)
            {
                if (!WritePowerLimitLocked(gpu, powerPct))
                    return new(false, true, "Private NVAPI power limit write failed; switching to NVML.", "nvapi");
            }

            if (profile.TempLimitC is { } tempC)
            {
                if (!WriteTempLimitLocked(gpu, tempC))
                    return new(false, true, "Private NVAPI temperature limit write failed; switching to NVML.", "nvapi");
            }

            return new(true, false, null, "nvapi");
        }
    }

    public GpuApplyResult ResetToFactory()
    {
        if (HostMode.IsDisposable) return new(false, false, HostMode.Refusal, "nvml");
        lock (_gate)
        {
            if (!TryGetPrimaryGpu(out var gpu, out _))
                return new(false, true, "NVAPI GPU handle unavailable; switching to NVML.", "nvapi");

            var ok = true;
            var currentCurve = ReadVfCurveLocked(gpu);
            if (currentCurve.Count > 0)
                ok &= WriteVfCurveLocked(gpu, currentCurve.Select(p => p with { OffsetMhz = 0 }).ToList());
            _ = ClearClockBoostLockLocked(gpu);
            _ = WritePstates20MemoryOffsetLocked(gpu, 0);
            if (_defaults?.PowerLimitPercent is { } defPower) _ = WritePowerLimitLocked(gpu, defPower);
            if (_defaults?.TempLimitC is { } defTemp) _ = WriteTempLimitLocked(gpu, defTemp);
            _ = RestoreFansAutoLocked(gpu);
            return ok
                ? new(true, false, null, "nvapi")
                : new(false, true, "Private NVAPI reset did not complete; switching to NVML.", "nvapi");
        }
    }

    public bool AcceptWaiver() => true;

    internal bool SetFanSpeed(int fanIndex, int speedPercent)
    {
        if (HostMode.IsDisposable) return false;
        lock (_gate)
        {
            if (!TryGetPrimaryGpu(out var gpu, out _)) return false;
            return SetFanControlLocked(gpu, fanIndex, manual: true, Math.Clamp(speedPercent, 0, 100));
        }
    }

    internal bool RestoreAllFansAuto()
    {
        lock (_gate)
        {
            if (!TryGetPrimaryGpu(out var gpu, out _)) return false;
            return RestoreFansAutoLocked(gpu);
        }
    }

    private bool TryGetPrimaryGpu(out nint gpu, out string name)
    {
        gpu = 0;
        name = "NVIDIA GeForce";
        var enumFn = _queryInterface(NvApiInterfaceIds.EnumPhysicalGpus);
        if (enumFn == 0) return false;
        var handles = stackalloc nint[64];
        uint count = 0;
        if (((delegate* unmanaged[Cdecl]<nint*, uint*, int>)enumFn)(handles, &count) != NvApiOk || count == 0)
            return false;
        gpu = handles[0];
        var nameFn = _queryInterface(NvApiInterfaceIds.GetFullName);
        if (nameFn != 0)
        {
            var buf = stackalloc byte[64];
            if (((delegate* unmanaged[Cdecl]<nint, byte*, int>)nameFn)(gpu, buf) == NvApiOk)
            {
                var parsed = Marshal.PtrToStringAnsi((nint)buf)?.Trim();
                if (!string.IsNullOrWhiteSpace(parsed)) name = parsed;
            }
        }
        return true;
    }

    private IReadOnlyList<GpuVfPoint> ReadVfCurveLocked(nint gpu)
    {
        var vfpFn = _queryInterface(NvApiInterfaceIds.GetVfpCurve);
        var tableFn = _queryInterface(NvApiInterfaceIds.GetClockBoostTable);
        if (vfpFn == 0 || tableFn == 0) return [];

        var maskBuf = stackalloc byte[1036];
        new Span<byte>(maskBuf, 1036).Clear();
        *(uint*)maskBuf = 1036u | (1u << 16);
        var maskFn = _queryInterface(NvApiInterfaceIds.GetClockBoostMask);
        if (maskFn != 0 && ((delegate* unmanaged[Cdecl]<nint, byte*, int>)maskFn)(gpu, maskBuf) != NvApiOk)
            return [];

        var vfpSize = 8 + 16 + 80 * 28 + 175 * 16;
        var vfpBuf = NativeMemory.AllocZeroed((nuint)vfpSize);
        var tableSize = 8 + 16 + 255 * 36;
        var tableBuf = NativeMemory.AllocZeroed((nuint)tableSize);
        try
        {
            *(uint*)vfpBuf = (uint)vfpSize | (1u << 16);
            Buffer.MemoryCopy(maskBuf + 4, (byte*)vfpBuf + 4, 16, 16);
            if (((delegate* unmanaged[Cdecl]<nint, void*, int>)vfpFn)(gpu, vfpBuf) != NvApiOk)
                return [];

            *(uint*)tableBuf = (uint)tableSize | (1u << 16);
            Buffer.MemoryCopy(maskBuf + 4, (byte*)tableBuf + 4, 16, 16);
            var hasTable = ((delegate* unmanaged[Cdecl]<nint, void*, int>)tableFn)(gpu, tableBuf) == NvApiOk;

            var list = new List<GpuVfPoint>();
            for (var i = 0; i < MaxVfEntries; i++)
            {
                var entry = (byte*)vfpBuf + 24 + i * 28;
                var freqKhz = *(uint*)(entry + 8);
                var voltUv = *(uint*)(entry + 12);
                if (freqKhz == 0 || voltUv == 0) continue;
                var deltaKhz = hasTable ? *(int*)((byte*)tableBuf + 24 + i * 36 + 16) : 0;
                var freqMhz = (int)(freqKhz / 1000);
                var offMhz = deltaKhz / 1000;
                var mv = (int)(voltUv / 1000);
                if (mv < 500 || mv > 1500) continue;
                list.Add(new GpuVfPoint(i, mv, Math.Max(0, freqMhz - offMhz), offMhz));
            }
            return list;
        }
        finally
        {
            NativeMemory.Free(vfpBuf);
            NativeMemory.Free(tableBuf);
        }
    }

    private bool WriteVfCurveLocked(nint gpu, IReadOnlyList<GpuVfPoint> curve)
    {
        var getFn = _queryInterface(NvApiInterfaceIds.GetClockBoostTable);
        var setFn = _queryInterface(NvApiInterfaceIds.SetClockBoostTable);
        var maskFn = _queryInterface(NvApiInterfaceIds.GetClockBoostMask);
        if (getFn == 0 || setFn == 0 || maskFn == 0) return false;

        var maskBuf = stackalloc byte[1036];
        new Span<byte>(maskBuf, 1036).Clear();
        *(uint*)maskBuf = 1036u | (1u << 16);
        if (((delegate* unmanaged[Cdecl]<nint, byte*, int>)maskFn)(gpu, maskBuf) != NvApiOk) return false;

        var tableSize = 8 + 16 + 255 * 36;
        var tableBuf = NativeMemory.AllocZeroed((nuint)tableSize);
        try
        {
            *(uint*)tableBuf = (uint)tableSize | (1u << 16);
            Buffer.MemoryCopy(maskBuf + 4, (byte*)tableBuf + 4, 16, 16);
            if (((delegate* unmanaged[Cdecl]<nint, void*, int>)getFn)(gpu, tableBuf) != NvApiOk) return false;
            foreach (var pt in curve)
            {
                if (pt.Index < 0 || pt.Index >= MaxVfEntries) continue;
                *(int*)((byte*)tableBuf + 24 + pt.Index * 36 + 16) = pt.OffsetMhz * 1000;
            }
            return ((delegate* unmanaged[Cdecl]<nint, void*, int>)setFn)(gpu, tableBuf) == NvApiOk;
        }
        finally { NativeMemory.Free(tableBuf); }
    }

    private bool SetClockBoostLockLocked(nint gpu, int targetMhz)
    {
        var getFn = _queryInterface(NvApiInterfaceIds.GetClockBoostLock);
        var setFn = _queryInterface(NvApiInterfaceIds.SetClockBoostLock);
        if (getFn == 0 || setFn == 0) return false;

        var size = 28 + 32 * 24;
        var buf = stackalloc byte[size];
        new Span<byte>(buf, size).Clear();
        *(uint*)buf = (uint)size | (2u << 16);
        if (((delegate* unmanaged[Cdecl]<nint, byte*, int>)getFn)(gpu, buf) != NvApiOk) return false;
        *(uint*)(buf + 8) = 1;
        *(uint*)(buf + 28) = 0;
        *(uint*)(buf + 32) = 1;
        *(uint*)(buf + 40) = (uint)(targetMhz * 1000);
        return ((delegate* unmanaged[Cdecl]<nint, byte*, int>)setFn)(gpu, buf) == NvApiOk;
    }

    private bool ClearClockBoostLockLocked(nint gpu)
    {
        var getFn = _queryInterface(NvApiInterfaceIds.GetClockBoostLock);
        var setFn = _queryInterface(NvApiInterfaceIds.SetClockBoostLock);
        if (getFn == 0 || setFn == 0) return false;

        var size = 28 + 32 * 24;
        var buf = stackalloc byte[size];
        new Span<byte>(buf, size).Clear();
        *(uint*)buf = (uint)size | (2u << 16);
        if (((delegate* unmanaged[Cdecl]<nint, byte*, int>)getFn)(gpu, buf) != NvApiOk) return false;
        *(uint*)(buf + 8) = 1;
        *(uint*)(buf + 28) = 0;
        *(uint*)(buf + 32) = 0;
        *(uint*)(buf + 40) = 0;
        return ((delegate* unmanaged[Cdecl]<nint, byte*, int>)setFn)(gpu, buf) == NvApiOk;
    }

    private (GpuTuningRange Core, GpuTuningRange Memory) ReadClockRangesLocked(nint gpu)
    {
        var fn = _queryInterface(NvApiInterfaceIds.GetClockBoostRanges);
        if (fn == 0)
            return (new GpuTuningRange(-500, 500, 15, 0, true), new GpuTuningRange(-1000, 1500, 25, 0, true));

        var size = 12 + 32 * 40;
        var buf = stackalloc byte[size];
        new Span<byte>(buf, size).Clear();
        *(uint*)buf = (uint)size | (1u << 16);
        if (((delegate* unmanaged[Cdecl]<nint, byte*, int>)fn)(gpu, buf) != NvApiOk)
            return (new GpuTuningRange(-500, 500, 15, 0, true), new GpuTuningRange(-1000, 1500, 25, 0, true));

        var core = GpuTuningRange.Unsupported;
        var mem = GpuTuningRange.Unsupported;
        var count = Math.Min(*(uint*)(buf + 4), 32u);
        for (uint i = 0; i < count; i++)
        {
            var entry = buf + 12 + i * 40;
            var clockId = *(uint*)entry;
            var minKhz = *(int*)(entry + 28);
            var maxKhz = *(int*)(entry + 32);
            if (maxKhz <= minKhz) continue;
            var range = new GpuTuningRange(minKhz / 1000, maxKhz / 1000, clockId == 0 ? 15 : 25, 0, true);
            if (clockId == 0) core = range;
            else if (clockId == 4) mem = range;
        }
        return (core, mem);
    }

    private bool WritePstates20MemoryOffsetLocked(nint gpu, int offsetMhz)
    {
        var setFn = _queryInterface(NvApiInterfaceIds.SetPstates20);
        if (setFn == 0) return false;
        var size = 28 + 16 * 560;
        var buf = NativeMemory.AllocZeroed((nuint)size);
        try
        {
            *(uint*)buf = (uint)size | (2u << 16);
            *(uint*)((byte*)buf + 8) = 1;
            *(uint*)((byte*)buf + 12) = 1;
            var ps = (byte*)buf + 28;
            *(uint*)ps = 0;
            var clk = ps + 8;
            *(uint*)clk = 4;
            *(int*)(clk + 16) = offsetMhz * 1000;
            return ((delegate* unmanaged[Cdecl]<nint, void*, int>)setFn)(gpu, buf) == NvApiOk;
        }
        finally { NativeMemory.Free(buf); }
    }

    private GpuTuningRange ReadPowerRangeLocked(nint gpu, out int? currentPercent)
    {
        currentPercent = null;
        var infoFn = _queryInterface(NvApiInterfaceIds.ClientPowerPoliciesGetInfo);
        var statusFn = _queryInterface(NvApiInterfaceIds.ClientPowerPoliciesGetStatus);
        if (infoFn == 0 || statusFn == 0) return GpuTuningRange.Unsupported;

        var infoBuf = stackalloc byte[168];
        new Span<byte>(infoBuf, 168).Clear();
        *(uint*)infoBuf = 168u | (1u << 16);
        if (((delegate* unmanaged[Cdecl]<nint, byte*, int>)infoFn)(gpu, infoBuf) != NvApiOk)
            return GpuTuningRange.Unsupported;
        var count = infoBuf[4];
        if (count == 0) return GpuTuningRange.Unsupported;
        var minPct = (int)(*(uint*)(infoBuf + 12) / 1000);
        var defPct = (int)(*(uint*)(infoBuf + 16) / 1000);
        var maxPct = (int)(*(uint*)(infoBuf + 20) / 1000);
        if (maxPct <= minPct || maxPct <= 0) return GpuTuningRange.Unsupported;

        var statusBuf = stackalloc byte[80];
        new Span<byte>(statusBuf, 80).Clear();
        *(uint*)statusBuf = 80u | (1u << 16);
        if (((delegate* unmanaged[Cdecl]<nint, byte*, int>)statusFn)(gpu, statusBuf) == NvApiOk && statusBuf[4] > 0)
            currentPercent = (int)(*(uint*)(statusBuf + 12) / 1000);

        return new(minPct, maxPct, 1, Math.Clamp(defPct, minPct, maxPct), true);
    }

    private bool WritePowerLimitLocked(nint gpu, int percent)
    {
        var setFn = _queryInterface(NvApiInterfaceIds.ClientPowerPoliciesSetStatus);
        if (setFn == 0) return false;
        var statusBuf = stackalloc byte[80];
        new Span<byte>(statusBuf, 80).Clear();
        *(uint*)statusBuf = 80u | (1u << 16);
        statusBuf[4] = 1;
        *(uint*)(statusBuf + 12) = (uint)(percent * 1000);
        return ((delegate* unmanaged[Cdecl]<nint, byte*, int>)setFn)(gpu, statusBuf) == NvApiOk;
    }

    private GpuTuningRange ReadTempRangeLocked(nint gpu, out int? currentTempC)
    {
        currentTempC = null;
        var infoFn = _queryInterface(NvApiInterfaceIds.ClientThermalPoliciesGetInfo);
        var getFn = _queryInterface(NvApiInterfaceIds.ClientThermalPoliciesGetLimit);
        if (infoFn == 0 || getFn == 0) return GpuTuningRange.Unsupported;

        var infoBuf = stackalloc byte[168];
        new Span<byte>(infoBuf, 168).Clear();
        *(uint*)infoBuf = 168u | (2u << 16);
        if (((delegate* unmanaged[Cdecl]<nint, byte*, int>)infoFn)(gpu, infoBuf) != NvApiOk)
            return GpuTuningRange.Unsupported;
        var count = infoBuf[4];
        if (count == 0) return GpuTuningRange.Unsupported;
        var minC = *(int*)(infoBuf + 12) >> 8;
        var defC = *(int*)(infoBuf + 16) >> 8;
        var maxC = *(int*)(infoBuf + 20) >> 8;
        if (maxC <= minC || maxC <= 30) return GpuTuningRange.Unsupported;

        var limitBuf = stackalloc byte[80];
        new Span<byte>(limitBuf, 80).Clear();
        *(uint*)limitBuf = 80u | (2u << 16);
        if (((delegate* unmanaged[Cdecl]<nint, byte*, int>)getFn)(gpu, limitBuf) == NvApiOk && limitBuf[4] > 0)
            currentTempC = *(int*)(limitBuf + 12) >> 8;

        return new(minC, maxC, 1, Math.Clamp(defC, minC, maxC), true);
    }

    private bool WriteTempLimitLocked(nint gpu, int tempC)
    {
        var getFn = _queryInterface(NvApiInterfaceIds.ClientThermalPoliciesGetLimit);
        var setFn = _queryInterface(NvApiInterfaceIds.ClientThermalPoliciesSetLimit);
        if (getFn == 0 || setFn == 0) return false;
        var limitBuf = stackalloc byte[80];
        new Span<byte>(limitBuf, 80).Clear();
        *(uint*)limitBuf = 80u | (2u << 16);
        if (((delegate* unmanaged[Cdecl]<nint, byte*, int>)getFn)(gpu, limitBuf) != NvApiOk) return false;
        limitBuf[4] = 1;
        *(int*)(limitBuf + 12) = tempC << 8;
        return ((delegate* unmanaged[Cdecl]<nint, byte*, int>)setFn)(gpu, limitBuf) == NvApiOk;
    }

    private (int Count, int MinSpeed, int MaxSpeed) ReadFanInfoLocked(nint gpu)
    {
        var infoFn = _queryInterface(NvApiInterfaceIds.ClientFanCoolersGetInfo);
        if (infoFn != 0)
        {
            var size = 40 + MaxCoolers * 60;
            var buf = stackalloc byte[size];
            new Span<byte>(buf, size).Clear();
            *(uint*)buf = (uint)size | (1u << 16);
            if (((delegate* unmanaged[Cdecl]<nint, byte*, int>)infoFn)(gpu, buf) == NvApiOk)
            {
                var count = (int)Math.Min(*(uint*)(buf + 4), (uint)MaxCoolers);
                if (count > 0) return (count, 0, 100);
            }
        }

        var legacyFn = _queryInterface(NvApiInterfaceIds.GetCoolerSettings);
        if (legacyFn != 0)
        {
            var buf = stackalloc byte[1024];
            new Span<byte>(buf, 1024).Clear();
            *(uint*)buf = 1024u | (1u << 16);
            if (((delegate* unmanaged[Cdecl]<nint, uint, byte*, int>)legacyFn)(gpu, 7, buf) == NvApiOk)
            {
                var count = (int)Math.Min(*(uint*)(buf + 4), 3u);
                if (count > 0) return (count, 20, 100);
            }
        }
        return (0, 0, 100);
    }

    private bool SetFanControlLocked(nint gpu, int fanIndex, bool manual, int speedPercent)
    {
        var getFn = _queryInterface(NvApiInterfaceIds.ClientFanCoolersGetControl);
        var setFn = _queryInterface(NvApiInterfaceIds.ClientFanCoolersSetControl);
        if (getFn != 0 && setFn != 0)
        {
            var size = 40 + MaxCoolers * 48;
            var buf = stackalloc byte[size];
            new Span<byte>(buf, size).Clear();
            *(uint*)buf = (uint)size | (1u << 16);
            if (((delegate* unmanaged[Cdecl]<nint, byte*, int>)getFn)(gpu, buf) == NvApiOk)
            {
                var count = (int)Math.Min(*(uint*)(buf + 4), (uint)MaxCoolers);
                if (fanIndex >= 0 && fanIndex < count)
                {
                    var entry = buf + 40 + fanIndex * 48;
                    *(uint*)(entry + 4) = (uint)speedPercent;
                    *(uint*)(entry + 8) = manual ? FanModeManual : FanModeAuto;
                    return ((delegate* unmanaged[Cdecl]<nint, byte*, int>)setFn)(gpu, buf) == NvApiOk;
                }
            }
        }

        var legacySet = _queryInterface(NvApiInterfaceIds.SetCoolerLevels);
        if (legacySet != 0)
        {
            var buf = stackalloc byte[200];
            new Span<byte>(buf, 200).Clear();
            *(uint*)buf = 200u | (1u << 16);
            *(int*)(buf + 4) = speedPercent;
            *(int*)(buf + 8) = manual ? 1 : 0;
            return ((delegate* unmanaged[Cdecl]<nint, int, byte*, int>)legacySet)(gpu, fanIndex, buf) == NvApiOk;
        }
        return false;
    }

    private bool RestoreFansAutoLocked(nint gpu)
    {
        var (count, _, _) = ReadFanInfoLocked(gpu);
        var ok = true;
        for (var i = 0; i < count; i++)
            ok &= SetFanControlLocked(gpu, i, manual: false, 0);
        return ok;
    }
}

internal sealed unsafe class NvidiaNvmlManualTuningBackend : IGpuTuningBackend
{
    private const int Success = 0;
    private const uint OffsetVersion = 24 | (1u << 24);
    private const int ClockGraphics = 0, ClockMem = 2, PState0 = 0;
    private const uint FanPolicyAuto = 0, FanPolicyManual = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ClockOffset
    {
        public uint Version;
        public int Type, PState, OffsetMhz, MinMhz, MaxMhz;
    }

    private readonly Func<bool> _isLaptop;
    private GpuManualProfile? _defaults;

    internal NvidiaNvmlManualTuningBackend(Func<bool> isLaptop) => _isLaptop = isLaptop;

    public string BackendId => "nvml";
    public string Vendor => "nvidia";

    public GpuTuningCapabilities Probe()
    {
        var laptop = _isLaptop();
        if (laptop)
        {
            return new GpuTuningCapabilities
            {
                Vendor = "nvidia",
                Backend = "nvml",
                IsLaptop = true,
                LockedByMaker = true,
                LockReason = GpuTuningCapabilities.LockedMessage,
            };
        }

        if (!TryOpen(out var lib, out var dev))
        {
            return new GpuTuningCapabilities
            {
                Vendor = "nvidia",
                Backend = "none",
                LockedByMaker = true,
                LockReason = "NVIDIA NVML driver library did not answer.",
            };
        }

        try
        {
            var name = ReadName(lib, dev) ?? "NVIDIA GeForce";
            var gen = GpuTuningPolicy.DetectGeneration("nvidia", name);
            var coreRange = ReadOffsetRange(lib, dev, ClockGraphics, "nvmlDeviceSetGpcClkVfOffset");
            var memRange = ReadOffsetRange(lib, dev, ClockMem, "nvmlDeviceSetMemClkVfOffset");
            var powerRange = ReadPowerRange(lib, dev, out var defWatts);
            var clockLockRange = ReadClockLockRange(lib, dev);
            var fanCount = ReadFanCount(lib, dev);
            var canSetFan = fanCount > 0 && NativeLibrary.TryGetExport(lib, "nvmlDeviceSetFanSpeed_v2", out _);

            _defaults ??= new GpuManualProfile
            {
                Mode = "auto",
                CoreClockOffsetMhz = 0,
                MemoryClockOffsetMhz = 0,
                PowerLimitWatts = defWatts,
                ClockLockMhz = 0,
            };

            var anyControl = coreRange.Supported || memRange.Supported || powerRange.Supported || clockLockRange.Supported || canSetFan;
            return new GpuTuningCapabilities
            {
                Vendor = "nvidia",
                Generation = gen,
                DeviceId = $"nvidia:{name}",
                DeviceName = name,
                Backend = "nvml",
                IsLaptop = false,
                LockedByMaker = !anyControl,
                LockReason = anyControl ? null : GpuTuningCapabilities.LockedMessage,
                PersistsInDriver = false,
                NeedsResidentForFanCurve = canSetFan,
                PersistenceNote = canSetFan
                    ? "NVML fallback active: clocks and power limit re-apply at sign-in; software fan curves require Exo to stay open."
                    : "NVML fallback active: clocks and power limit re-apply at sign-in through Exo's guarded task.",
                SupportsCurveEditor = false,
                SupportsUndervoltSearch = coreRange.Supported && clockLockRange.Supported,
                CoreClockOffsetMhz = coreRange,
                MemoryClockOffsetMhz = memRange,
                PowerLimitWatts = powerRange,
                ClockLockMhz = clockLockRange,
                FanCount = fanCount,
                SupportsHardwareFanCurve = false,
                SupportsSoftwareFanCurve = canSetFan,
                SupportsZeroRpm = false,
                FanMaxPoints = canSetFan ? 5 : 0,
                FanSpeedPercent = canSetFan ? new GpuTuningRange(30, 100, 1, 50, true) : GpuTuningRange.Unsupported,
                FanTempC = canSetFan ? new GpuTuningRange(30, 95, 1, 60, true) : GpuTuningRange.Unsupported,
            };
        }
        finally { Close(lib); }
    }

    public GpuManualProfile ReadCurrent()
    {
        if (!TryOpen(out var lib, out var dev)) return GpuManualProfile.Auto;
        try
        {
            var coreOff = ReadCurrentOffset(lib, dev, ClockGraphics);
            var memOff = ReadCurrentOffset(lib, dev, ClockMem);
            int? powerW = null;
            if (NativeLibrary.TryGetExport(lib, "nvmlDeviceGetPowerManagementLimit", out var getPow))
            {
                uint mw = 0;
                if (((delegate* unmanaged[Cdecl]<nint, uint*, int>)getPow)(dev, &mw) == Success)
                    powerW = (int)(mw / 1000);
            }
            var manual = (coreOff ?? 0) != 0 || (memOff ?? 0) != 0;
            return new GpuManualProfile
            {
                Mode = manual ? "manual" : "auto",
                CoreClockOffsetMhz = coreOff,
                MemoryClockOffsetMhz = memOff,
                PowerLimitWatts = powerW,
            };
        }
        finally { Close(lib); }
    }

    public GpuManualProfile ReadFactoryDefaults() => _defaults ?? GpuManualProfile.Auto;

    public GpuApplyResult Apply(GpuManualProfile profile)
    {
        if (HostMode.IsDisposable) return new(false, false, HostMode.Refusal, "nvml");
        if (profile.Mode != "manual") return ResetToFactory();
        if (!TryOpen(out var lib, out var dev))
            return new(false, false, "NVIDIA NVML driver library did not answer.", "nvml");

        try
        {
            if (profile.ClockLockMhz is { } cap && cap > 0)
            {
                if (!NativeLibrary.TryGetExport(lib, "nvmlDeviceSetGpuLockedClocks", out var lockFn)
                    || ((delegate* unmanaged[Cdecl]<nint, uint, uint, int>)lockFn)(dev, 0, (uint)cap) != Success)
                    return new(false, false, "NVML refused locked GPU clock ceiling (administrator permission may be required).", "nvml");
            }
            else if (NativeLibrary.TryGetExport(lib, "nvmlDeviceResetGpuLockedClocks", out var unlockFn))
            {
                _ = ((delegate* unmanaged[Cdecl]<nint, int>)unlockFn)(dev);
            }

            if (profile.CoreClockOffsetMhz is { } coreOff)
            {
                if (!WriteOffset(lib, dev, ClockGraphics, "nvmlDeviceSetGpcClkVfOffset", coreOff))
                    return new(false, false, "NVML refused core clock offset.", "nvml");
            }

            if (profile.MemoryClockOffsetMhz is { } memOff)
            {
                if (!WriteOffset(lib, dev, ClockMem, "nvmlDeviceSetMemClkVfOffset", memOff))
                    return new(false, false, "NVML refused memory clock offset.", "nvml");
            }

            if (profile.PowerLimitWatts is { } watts)
            {
                if (!NativeLibrary.TryGetExport(lib, "nvmlDeviceSetPowerManagementLimit", out var powFn)
                    || ((delegate* unmanaged[Cdecl]<nint, uint, int>)powFn)(dev, (uint)(watts * 1000)) != Success)
                    return new(false, false, "NVML refused power limit.", "nvml");
            }

            return new(true, false, null, "nvml");
        }
        finally { Close(lib); }
    }

    public GpuApplyResult ResetToFactory()
    {
        if (HostMode.IsDisposable) return new(false, false, HostMode.Refusal, "nvml");
        if (!TryOpen(out var lib, out var dev))
            return new(false, false, "NVIDIA NVML driver library did not answer.", "nvml");
        try
        {
            _ = WriteOffset(lib, dev, ClockGraphics, "nvmlDeviceSetGpcClkVfOffset", 0);
            _ = WriteOffset(lib, dev, ClockMem, "nvmlDeviceSetMemClkVfOffset", 0);
            if (NativeLibrary.TryGetExport(lib, "nvmlDeviceResetGpuLockedClocks", out var resetLock))
                _ = ((delegate* unmanaged[Cdecl]<nint, int>)resetLock)(dev);
            if (NativeLibrary.TryGetExport(lib, "nvmlDeviceGetPowerManagementDefaultLimit", out var defPowFn)
                && NativeLibrary.TryGetExport(lib, "nvmlDeviceSetPowerManagementLimit", out var setPowFn))
            {
                uint defMw = 0;
                if (((delegate* unmanaged[Cdecl]<nint, uint*, int>)defPowFn)(dev, &defMw) == Success && defMw > 0)
                    _ = ((delegate* unmanaged[Cdecl]<nint, uint, int>)setPowFn)(dev, defMw);
            }
            var fans = ReadFanCount(lib, dev);
            for (var i = 0; i < fans; i++) RestoreFanAuto(lib, dev, i);
            return new(true, false, null, "nvml");
        }
        finally { Close(lib); }
    }

    public bool AcceptWaiver() => true;

    internal static bool SetFanSpeed(int fanIndex, int speedPercent)
    {
        if (HostMode.IsDisposable) return false;
        if (!TryOpen(out var lib, out var dev)) return false;
        try
        {
            if (NativeLibrary.TryGetExport(lib, "nvmlDeviceSetFanControlPolicy", out var policyFn))
                _ = ((delegate* unmanaged[Cdecl]<nint, uint, uint, int>)policyFn)(dev, (uint)fanIndex, FanPolicyManual);
            if (!NativeLibrary.TryGetExport(lib, "nvmlDeviceSetFanSpeed_v2", out var setFn)) return false;
            return ((delegate* unmanaged[Cdecl]<nint, uint, uint, int>)setFn)(dev, (uint)fanIndex, (uint)Math.Clamp(speedPercent, 0, 100)) == Success;
        }
        finally { Close(lib); }
    }

    internal static void RestoreAllFansAuto()
    {
        if (!TryOpen(out var lib, out var dev)) return;
        try
        {
            var count = ReadFanCount(lib, dev);
            for (var i = 0; i < count; i++) RestoreFanAuto(lib, dev, i);
        }
        finally { Close(lib); }
    }

    private static void RestoreFanAuto(nint lib, nint dev, int fanIndex)
    {
        if (NativeLibrary.TryGetExport(lib, "nvmlDeviceSetDefaultFanSpeed_v2", out var defFn))
            _ = ((delegate* unmanaged[Cdecl]<nint, uint, int>)defFn)(dev, (uint)fanIndex);
        if (NativeLibrary.TryGetExport(lib, "nvmlDeviceSetFanControlPolicy", out var policyFn))
            _ = ((delegate* unmanaged[Cdecl]<nint, uint, uint, int>)policyFn)(dev, (uint)fanIndex, FanPolicyAuto);
    }

    private static bool TryOpen(out nint lib, out nint dev)
    {
        lib = 0;
        dev = 0;
        if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "nvml.dll"), out lib)) return false;
        if (!NativeLibrary.TryGetExport(lib, "nvmlInit_v2", out var initFn) || ((delegate* unmanaged[Cdecl]<int>)initFn)() != Success)
        {
            NativeLibrary.Free(lib);
            lib = 0;
            return false;
        }
        nint handle = 0;
        if (NativeLibrary.TryGetExport(lib, "nvmlDeviceGetHandleByIndex_v2", out var getFn)
            && ((delegate* unmanaged[Cdecl]<uint, nint*, int>)getFn)(0, &handle) == Success && handle != 0)
        {
            dev = handle;
            return true;
        }
        Close(lib);
        lib = 0;
        return false;
    }

    private static void Close(nint lib)
    {
        if (NativeLibrary.TryGetExport(lib, "nvmlShutdown", out var shutdown))
            ((delegate* unmanaged[Cdecl]<int>)shutdown)();
        NativeLibrary.Free(lib);
    }

    private static string? ReadName(nint lib, nint dev)
    {
        if (!NativeLibrary.TryGetExport(lib, "nvmlDeviceGetName", out var fn)) return null;
        var buf = stackalloc byte[96];
        return ((delegate* unmanaged[Cdecl]<nint, byte*, uint, int>)fn)(dev, buf, 96) == Success
            ? Encoding.ASCII.GetString(buf, 96).TrimEnd('\0').Trim()
            : null;
    }

    private static GpuTuningRange ReadOffsetRange(nint lib, nint dev, int clockType, string legacySetter)
    {
        if (NativeLibrary.TryGetExport(lib, "nvmlDeviceGetClockOffsets", out var getFn))
        {
            var info = new ClockOffset { Version = OffsetVersion, Type = clockType, PState = PState0 };
            if (((delegate* unmanaged[Cdecl]<nint, ClockOffset*, int>)getFn)(dev, &info) == Success && info.MaxMhz > info.MinMhz)
                return new(info.MinMhz, info.MaxMhz, clockType == ClockGraphics ? 15 : 25, 0, true);
        }
        if (NativeLibrary.TryGetExport(lib, legacySetter, out _))
            return new(clockType == ClockGraphics ? -500 : -1000, clockType == ClockGraphics ? 500 : 1500, clockType == ClockGraphics ? 15 : 25, 0, true);
        return GpuTuningRange.Unsupported;
    }

    private static int? ReadCurrentOffset(nint lib, nint dev, int clockType)
    {
        if (!NativeLibrary.TryGetExport(lib, "nvmlDeviceGetClockOffsets", out var getFn)) return null;
        var info = new ClockOffset { Version = OffsetVersion, Type = clockType, PState = PState0 };
        return ((delegate* unmanaged[Cdecl]<nint, ClockOffset*, int>)getFn)(dev, &info) == Success ? info.OffsetMhz : null;
    }

    private static bool WriteOffset(nint lib, nint dev, int clockType, string legacySetter, int offsetMhz)
    {
        if (NativeLibrary.TryGetExport(lib, "nvmlDeviceSetClockOffsets", out var setV1))
        {
            var info = new ClockOffset { Version = OffsetVersion, Type = clockType, PState = PState0, OffsetMhz = offsetMhz };
            if (((delegate* unmanaged[Cdecl]<nint, ClockOffset*, int>)setV1)(dev, &info) == Success) return true;
        }
        if (NativeLibrary.TryGetExport(lib, legacySetter, out var legacyFn))
            return ((delegate* unmanaged[Cdecl]<nint, int, int>)legacyFn)(dev, offsetMhz) == Success;
        return false;
    }

    private static GpuTuningRange ReadPowerRange(nint lib, nint dev, out int? defaultWatts)
    {
        defaultWatts = null;
        if (!NativeLibrary.TryGetExport(lib, "nvmlDeviceGetPowerManagementLimitConstraints", out var fn))
            return GpuTuningRange.Unsupported;
        uint minMw = 0, maxMw = 0;
        if (((delegate* unmanaged[Cdecl]<nint, uint*, uint*, int>)fn)(dev, &minMw, &maxMw) != Success || maxMw <= minMw)
            return GpuTuningRange.Unsupported;
        uint defMw = maxMw;
        if (NativeLibrary.TryGetExport(lib, "nvmlDeviceGetPowerManagementDefaultLimit", out var defFn))
            _ = ((delegate* unmanaged[Cdecl]<nint, uint*, int>)defFn)(dev, &defMw);
        var minW = (int)(minMw / 1000);
        var maxW = (int)(maxMw / 1000);
        var defW = Math.Clamp((int)(defMw / 1000), minW, maxW);
        defaultWatts = defW;
        return new(minW, maxW, 5, defW, maxW > minW);
    }

    private static GpuTuningRange ReadClockLockRange(nint lib, nint dev)
    {
        if (!NativeLibrary.TryGetExport(lib, "nvmlDeviceSetGpuLockedClocks", out _)
            || !NativeLibrary.TryGetExport(lib, "nvmlDeviceGetMaxClockInfo", out var maxFn))
            return GpuTuningRange.Unsupported;
        uint maxClk = 0;
        if (((delegate* unmanaged[Cdecl]<nint, int, uint*, int>)maxFn)(dev, ClockGraphics, &maxClk) != Success || maxClk < 600)
            return GpuTuningRange.Unsupported;
        return new(600, (int)maxClk, 15, (int)maxClk, true);
    }

    private static int ReadFanCount(nint lib, nint dev)
    {
        if (!NativeLibrary.TryGetExport(lib, "nvmlDeviceGetNumFans", out var fn)) return 0;
        uint count = 0;
        return ((delegate* unmanaged[Cdecl]<nint, uint*, int>)fn)(dev, &count) == Success ? (int)Math.Min(count, 8u) : 0;
    }
}
