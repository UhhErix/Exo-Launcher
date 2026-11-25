using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ExoLauncher.Helpers;

internal static unsafe partial class AmdAdlx
{
    // IADLXSystem.GetGPUTuningServices 8. IADLXGPUTuningServices (IGPUTuning.h): IsAtFactory 4, ResetToFactory 5,
    // IsSupportedAutoTuning 6, GetAutoTuning 12 (TuningGetSmartAccessMemory's 18 counts on from it, and Exo reads that on real
    // Radeons). IADLXGPUAutoTuning (IGPUAutoTuning.h): IsSupportedUndervoltGPU 3, IsSupportedOverclockGPU 4,
    // IsCurrentUndervoltGPU 6, IsCurrentOverclockGPU 7, StartUndervoltGPU 9, StartOverclockGPU 10. The listener is one
    // method, OnGPUAutoTuningComplete, with no base interface.
    private const int TuningReset = 5, TuningGetAuto = 12, AutoSupportedOverclock = 4, AutoCurrentUndervolt = 6, AutoStartUndervolt = 9;

    private static readonly ManualResetEventSlim AutoTuneDone = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static byte OnAutoTuned(nint self, nint happened)
    {
        try { AutoTuneDone.Set(); }
        catch { }
        return 1;
    }

    private static T? WithAutoTuning<T>(string what, Func<nint, nint, nint, T?> body) => WithGpu<T>(what, (system, gpu) =>
    {
        nint tuning = 0, item = 0, auto = 0;
        try
        {
            if (Call(system, SystemGetGpuTuning, &tuning) != AdlxOk || tuning == 0) return default;
            if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(tuning, TuningGetAuto))(tuning, gpu, &item) != AdlxOk || item == 0) return default;
            auto = Query(item, "IADLXGPUAutoTuning");
            return auto == 0 ? default : body(tuning, gpu, auto);
        }
        finally { ReleaseAll(auto, item, tuning); }
    });

    /// <summary>Whether AMD's undervolt is on offer for the main AMD GPU; both tunings share a row.</summary>
    internal static bool AutoTuneSupported() => WithAutoTuning("auto tuning read", (_, _, auto) => Supported(auto) && Read(auto, AutoSupportedOverclock) == true);

    /// <summary>Whether that tuning is on now; null when ADLX can't say.</summary>
    internal static bool? AutoTuneOn(bool overclock) => WithAutoTuning<bool?>("auto tuning read", (_, _, auto) => Read(auto, AutoCurrentUndervolt + (overclock ? 1 : 0)));

    /// <summary>Runs AMD's tuning, which it does in the driver, until it says it finished; false when it didn't start, timed out or <paramref name="stop"/> said so.</summary>
    internal static bool StartAutoTune(bool overclock, TimeSpan limit, Func<bool> stop) => WithAutoTuning("auto tuning", (_, _, auto) =>
    {
        // Lives as long as the worker: ADLX may call it for as long as it's running.
        var table = (nint*)NativeMemory.Alloc((nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, byte>)&OnAutoTuned;
        var listener = (nint*)NativeMemory.Alloc((nuint)sizeof(nint));
        listener[0] = (nint)table;
        AutoTuneDone.Reset();
        if (((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(auto, AutoStartUndervolt + (overclock ? 1 : 0)))(auto, (nint)listener) != AdlxOk) return false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < limit)
        {
            if (AutoTuneDone.Wait(TimeSpan.FromSeconds(1))) return true;
            if (stop()) return false;
        }
        return false;
    });

    /// <summary>Back to factory settings. ADLX's one reset also clears any manual tuning made in AMD Software.</summary>
    internal static bool ResetTuning() => WithGpu("tuning reset", (system, gpu) =>
    {
        nint tuning = 0;
        try
        {
            return Call(system, SystemGetGpuTuning, &tuning) == AdlxOk && tuning != 0
                && ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(tuning, TuningReset))(tuning, gpu) == AdlxOk;
        }
        finally { ReleaseAll(tuning); }
    });

    private static bool? Read(nint item, int slot)
    {
        byte value = 0;
        return ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(item, slot))(item, &value) == AdlxOk ? value != 0 : null;
    }
}
