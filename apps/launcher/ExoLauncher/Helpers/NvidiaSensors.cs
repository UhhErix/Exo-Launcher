using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

/// <summary>
/// A GeForce's live readings for PC › Graphics, in the shape a Radeon's take (<see cref="GpuSensors"/>): temperature,
/// load, clock and video memory from NvAPI (nvapi64.dll through nvapi_QueryInterface, initialised by
/// <see cref="NvidiaProfiles.Available"/>); board power, fan and the card's temperature limits from NVML
/// (<see cref="Nvml.Sensors"/>), since NvAPI's public SDK has no watts and its tach reading answers
/// NVAPI_NOT_SUPPORTED on the RTX 3070. All of it under the one NvAPI lock. Ids and layouts are NVIDIA's public ones
/// (github.com/NVIDIA/nvapi main, October 2026: nvapi_interface.h; nvapi.h and nvapi_lite_common.h, pack(8)):
/// - NvAPI_EnumPhysicalGPUs 0xE5AC921F (NvPhysicalGpuHandle[64], NvU32*); NvAPI_GPU_GetBusId 0x1BE0B8E5 (gpu, NvU32*)
/// - NvAPI_GPU_GetThermalSettings 0xE3640A56 (gpu, NVAPI_THERMAL_TARGET_ALL 15, NV_GPU_THERMAL_SETTINGS_V2*): 68 bytes,
///   version 2; count@4, then three sensors of 20 bytes from 8, currentTemp@12 and target@16 in each (GPU 1, MEMORY 2)
/// - NvAPI_GPU_GetDynamicPstatesInfoEx 0x60DED2ED: 72 bytes, version 1; utilization[8] of {bIsPresent:1, percentage}
///   from 8, the GPU domain first
/// - NvAPI_GPU_GetAllClockFrequencies 0xDCB616C3: NV_GPU_CLOCK_FREQUENCIES_V2, 264 bytes, version 3, ClockType 0
///   (current); domain[32] of {bIsPresent:1, frequency kHz} from 8, graphics first
/// - NvAPI_GPU_GetMemoryInfoEx 0xC0599498 (R520 and later): 80 bytes, version 1; dedicatedVideoMemory@8,
///   availableDedicatedVideoMemory@16, curAvailableDedicatedVideoMemory@40, in bytes. In use is available less
///   current available (what nvidia-smi calls used; dedicated less available is its reserved, 175 MB here).
/// The hotspot isn't in NvAPI's public SDK or NVML (only a private call whose sensor order changes with each
/// generation and is gone on the RTX 50 series), so a GeForce shows none. Read on the RTX 3070 (driver 621.12): one
/// GPU sensor (38 °C), GPU load 2%, 210 MHz, 554 of 8192 MB, no memory sensor; a whole read 1.4 ms.
/// </summary>
internal static unsafe class NvidiaSensors
{
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint QueryInterface(uint id);

    private const int ThermalSize = 68, PstatesSize = 72, ClocksSize = 264, MemorySize = 80;
    private const uint ThermalTargetAll = 15, TargetGpu = 1, TargetMemory = 2;

    /// <summary>The first NVIDIA GPU's readings now; null when NvAPI doesn't answer.</summary>
    internal static GpuSensors? Read()
    {
        if (!NvidiaProfiles.Available) return null;
        lock (NvidiaProfiles.Gate)
        {
            try
            {
                var handles = stackalloc nint[64];
                uint count = 0, bus = 0;
                if (((delegate* unmanaged[Cdecl]<nint*, uint*, int>)Resolve(0xE5AC921F))(handles, &count) != 0 || count == 0) return null;
                var gpu = handles[0];

                double? core = null, memory = null;
                var thermal = stackalloc byte[ThermalSize];
                if (Versioned(0xE3640A56, gpu, thermal, ThermalSize, 2, ThermalTargetAll))
                    for (var i = 0; i < Math.Min(*(int*)(thermal + 4), 3); i++)
                    {
                        var sensor = thermal + 8 + i * 20;
                        var target = *(uint*)(sensor + 16);
                        if (target == TargetGpu) core ??= *(int*)(sensor + 12);
                        else if (target == TargetMemory) memory ??= *(int*)(sensor + 12);
                    }

                var pstates = stackalloc byte[PstatesSize];
                double? usage = Versioned(0x60DED2ED, gpu, pstates, PstatesSize, 1) && (*(uint*)(pstates + 8) & 1) != 0 ? *(uint*)(pstates + 12) : null;

                var clocks = stackalloc byte[ClocksSize];
                int? clock = Versioned(0xDCB616C3, gpu, clocks, ClocksSize, 3) && (*(uint*)(clocks + 8) & 1) != 0 ? (int)(*(uint*)(clocks + 12) / 1000) : null;

                var vram = stackalloc byte[MemorySize];
                int? used = null, total = null;
                if (Versioned(0xC0599498, gpu, vram, MemorySize, 1) && *(ulong*)(vram + 8) > 0)
                {
                    total = (int)(*(ulong*)(vram + 8) >> 20);
                    used = (int)((*(ulong*)(vram + 16) - Math.Min(*(ulong*)(vram + 40), *(ulong*)(vram + 16))) >> 20);
                }

                var board = ((delegate* unmanaged[Cdecl]<nint, uint*, int>)Resolve(0x1BE0B8E5))(gpu, &bus) == 0 ? Nvml.Sensors(bus) : null;
                return new GpuSensors(core, null, board?.Watts, clock, used, total, board?.FanRpm, usage, memory, board?.Limit);
            }
            catch (Exception ex)
            {
                AppLog.Debug("NvAPI sensors: " + ex.Message);
                return null;
            }
        }
    }

    /// <summary>Calls an NvAPI getter that fills a versioned struct (cleared first); <paramref name="index"/> goes between them when given.</summary>
    private static bool Versioned(uint id, nint gpu, byte* data, int size, uint version, uint? index = null)
    {
        new Span<byte>(data, size).Clear();
        *(uint*)data = (uint)size | (version << 16);
        var fn = Resolve(id);
        return (index is { } i
            ? ((delegate* unmanaged[Cdecl]<nint, uint, byte*, int>)fn)(gpu, i, data)
            : ((delegate* unmanaged[Cdecl]<nint, byte*, int>)fn)(gpu, data)) == 0;
    }

    private static nint Resolve(uint id)
    {
        var fn = QueryInterface(id);
        return fn != 0 ? fn : throw new EntryPointNotFoundException($"NvAPI 0x{id:X8} is not in this driver.");
    }
}
