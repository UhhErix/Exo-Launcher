using System.Globalization;
using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// NVIDIA's management library (nvml.dll), which every NVIDIA driver installs in System32: asked in-process,
/// where nvidia-smi.exe would start a process each time. Nothing here is NVIDIA's to ship: the driver's own copy
/// is loaded by full path, and a PC without one (no NVIDIA driver) answers null.
/// </summary>
internal static unsafe class Nvml
{
    private const int Success = 0;

    // nvml.h (github.com/NVIDIA/nvidia-settings src/nvml.h, October 2026): nvmlDeviceGetHandleByPciBusId_v2 takes
    // "domain:bus:device.function"; nvmlDeviceGetPowerUsage answers the board's draw in milliwatts; nvmlDeviceGetFanSpeedRPM
    // takes nvmlFanSpeedInfo_v1_t {version = 12 | 1 << 24, fan, speed RPM}; nvmlTemperatureThresholds_t SLOWDOWN 1,
    // GPU_MAX 3 ("can be throttled below base clock"), ACOUSTIC_CURR 5 (the temperature target GPU Boost holds).
    // Read on the RTX 3070 (621.12): 23-51 W idle, two fans at 0 RPM (stopped), thresholds 95, 93 and 83 °C, as
    // nvidia-smi -q says (Slowdown, Max Operating, Target).
    private const int ThresholdSlowdown = 1, ThresholdGpuMax = 3, ThresholdTarget = 5;

    /// <summary>What NVML adds to NvAPI's readings: watts, the fastest fan's RPM and the card's temperature limits.</summary>
    internal sealed record Reading(double? Watts, int? FanRpm, HeatLimit? Limit);

    // The sensors' session, opened on the first read and kept: nvmlInit_v2 maps NVML's driver part (21 MB on the RTX
    // 3070), which stays mapped after nvmlShutdown and FreeLibrary anyway, and opening it again costs 2-10 ms a read.
    // Only touched under NvidiaProfiles.Gate.
    private static nint _session;
    private static bool _unavailable;

    /// <summary>
    /// The board power, fan and temperature limits of the NVIDIA GPU on PCI bus <paramref name="bus"/> (NvAPI's); null
    /// without NVML. The caller holds <see cref="NvidiaProfiles.Gate"/>.
    /// </summary>
    internal static Reading? Sensors(uint bus)
    {
        if (_unavailable) return null;
        if (_session == 0)
        {
            if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "nvml.dll"), out var library)) { _unavailable = true; return null; }
            if (!NativeLibrary.TryGetExport(library, "nvmlInit_v2", out var init) || ((delegate* unmanaged[Cdecl]<int>)init)() != Success)
            {
                NativeLibrary.Free(library);
                _unavailable = true;
                AppLog.Info("NVIDIA sensors: NVML did not start, so watts and fan speed aren't shown.");
                return null;
            }
            _session = library;
        }
        var id = System.Text.Encoding.ASCII.GetBytes($"00000000:{bus:X2}:00.0\0");
        nint device = 0;
        fixed (byte* text = id)
            if (((delegate* unmanaged[Cdecl]<byte*, nint*, int>)Export("nvmlDeviceGetHandleByPciBusId_v2"))(text, &device) != Success)
            {
                // A driver installed since: start NVML again on the next read.
                ((delegate* unmanaged[Cdecl]<int>)Export("nvmlShutdown"))();
                NativeLibrary.Free(_session);
                _session = 0;
                return null;
            }

        uint milliwatts = 0, fans = 0;
        double? watts = ((delegate* unmanaged[Cdecl]<nint, uint*, int>)Export("nvmlDeviceGetPowerUsage"))(device, &milliwatts) == Success ? milliwatts / 1000.0 : null;
        int? rpm = null;
        if (((delegate* unmanaged[Cdecl]<nint, uint*, int>)Export("nvmlDeviceGetNumFans"))(device, &fans) == Success)
        {
            var speed = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Export("nvmlDeviceGetFanSpeedRPM");
            var info = stackalloc uint[3];
            for (uint fan = 0; fan < Math.Min(fans, 8u); fan++)
            {
                info[0] = 12 | (1u << 24);
                info[1] = fan;
                if (speed(device, info) == Success) rpm = Math.Max(rpm ?? 0, (int)info[2]);
            }
        }
        var threshold = (delegate* unmanaged[Cdecl]<nint, int, uint*, int>)Export("nvmlDeviceGetTemperatureThreshold");
        var gpu = device;
        int? Degrees(int type)
        {
            uint value = 0;
            return threshold(gpu, type, &value) == Success && value is > 0 and < 150 ? (int)value : null;
        }
        return new Reading(watts, rpm, Limit(Degrees(ThresholdGpuMax), Degrees(ThresholdSlowdown), Degrees(ThresholdTarget)));
    }

    /// <summary>
    /// Hot from the card's maximum operating temperature (else where it starts slowing down); warm from its temperature
    /// target, where GPU Boost starts lowering clocks to hold it (else 10 °C short of hot). Null with neither limit.
    /// </summary>
    internal static HeatLimit? Limit(int? gpuMax, int? slowdown, int? target) =>
        (gpuMax ?? slowdown) is { } hot ? new HeatLimit(false, target is { } warm && warm < hot ? warm : hot - 10, hot) : null;

    private static nint Export(string name) =>
        NativeLibrary.TryGetExport(_session, name, out var address) ? address : throw new EntryPointNotFoundException($"NVML has no {name}.");

    /// <summary>The first NVIDIA GPU's CUDA compute capability and the driver's version, as "8.6, 621.01"; null without one.</summary>
    internal static string? FirstGpu()
    {
        if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "nvml.dll"), out var library)) return null;
        try
        {
            if (!NativeLibrary.TryGetExport(library, "nvmlInit_v2", out var init)
                || !NativeLibrary.TryGetExport(library, "nvmlShutdown", out var shutdown)
                || !NativeLibrary.TryGetExport(library, "nvmlDeviceGetHandleByIndex_v2", out var byIndex)
                || !NativeLibrary.TryGetExport(library, "nvmlDeviceGetCudaComputeCapability", out var capability)
                || !NativeLibrary.TryGetExport(library, "nvmlSystemGetDriverVersion", out var driverVersion))
                return null;
            if (((delegate* unmanaged[Cdecl]<int>)init)() != Success) return null;
            try
            {
                nint device;
                int major, minor;
                var text = stackalloc byte[80];
                if (((delegate* unmanaged[Cdecl]<uint, nint*, int>)byIndex)(0, &device) != Success
                    || ((delegate* unmanaged[Cdecl]<nint, int*, int*, int>)capability)(device, &major, &minor) != Success
                    || ((delegate* unmanaged[Cdecl]<byte*, uint, int>)driverVersion)(text, 80) != Success)
                    return null;
                return string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}, {Marshal.PtrToStringAnsi((nint)text)}");
            }
            finally
            {
                ((delegate* unmanaged[Cdecl]<int>)shutdown)();
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }
}
