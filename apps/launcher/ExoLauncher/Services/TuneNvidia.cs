using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>What a driver answered that stops tuning, in the words the row shows.</summary>
internal sealed class TuneDriverException(string message) : Exception(message);

/// <summary>
/// A GeForce through NVML (nvml.h, github.com/NVIDIA/nvidia-settings src/nvml.h), which every NVIDIA driver installs: a graphics
/// clock offset for P0 (nvmlClockOffset_v1_t: version 24 | 1 &lt;&lt; 24, type, pstate, offset, min, max), and on Cooler a lock at
/// the peak the card reached at stock, so the same clock comes at less voltage. No voltage call and no power limit is ever
/// made. The setters need an administrator; the getter doesn't. Read on the RTX 3070 (driver 621.12): offset 0, range
/// -1000 to +1000, maximum clock 2100 MHz. Whether the setters answer on a GeForce on Windows is what the first real run shows.
/// </summary>
internal sealed unsafe class TuneNvidia : ITunePart, IDisposable
{
    internal const int Success = 0;
    private const uint OffsetVersion = 24 | (1u << 24);
    private const int ClockGraphics = 0, PState0 = 0;
    private const int Furthest = 240, Step = 30;

    [StructLayout(LayoutKind.Sequential)]
    private struct ClockOffset
    {
        public uint Version;
        public int Type, PState, OffsetMhz, MinMhz, MaxMhz;
    }

    private readonly nint _library, _device;
    private readonly string _mode;
    private int _cap;

    public string Id => "nvidia";
    public TuneLimits Limits { get; }

    internal TuneNvidia(string mode, int? hotC)
    {
        _mode = mode;
        (_library, _device) = Open() ?? throw new TuneDriverException("NVIDIA's driver didn't answer.");
        var range = Range(_library, _device) ?? throw new TuneDriverException("This card's driver doesn't offer a clock offset.");
        Limits = For(range.Max, hotC);
    }

    /// <summary>The search on a card whose driver allows <paramref name="driverMax"/> MHz. Sources: docs/architecture/auto-tune.md.</summary>
    internal static TuneLimits For(int driverMax, int? hotC) =>
        new(Math.Min(Furthest, driverMax / Step * Step), Step, Step, hotC is { } hot ? hot - 5 : 85, 180, 600, 180);

    /// <summary>Whether this PC's first NVIDIA GPU can be tuned: read-only, no administrator.</summary>
    internal static bool Supported()
    {
        try
        {
            if (Open() is not { } session) return false;
            try { return Range(session.Library, session.Device) is { Max: >= Step }; }
            finally { Close(session.Library); }
        }
        catch (TuneDriverException) { return false; }
    }

    private static (nint Library, nint Device)? Open()
    {
        if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "nvml.dll"), out var library)) return null;
        if (!NativeLibrary.TryGetExport(library, "nvmlInit_v2", out var init) || ((delegate* unmanaged[Cdecl]<int>)init)() != Success)
        {
            NativeLibrary.Free(library);
            return null;
        }
        nint device = 0;
        if (((delegate* unmanaged[Cdecl]<uint, nint*, int>)Export(library, "nvmlDeviceGetHandleByIndex_v2"))(0, &device) == Success) return (library, device);
        Close(library);
        return null;
    }

    private static void Close(nint library)
    {
        ((delegate* unmanaged[Cdecl]<int>)Export(library, "nvmlShutdown"))();
        NativeLibrary.Free(library);
    }

    private static nint Export(nint library, string name) =>
        NativeLibrary.TryGetExport(library, name, out var address) ? address : throw new TuneDriverException($"This driver has no {name}.");

    internal static int? ReadOffset()
    {
        var opened = Open();
        if (opened is null) return null;
        var (library, device) = opened.Value;
        try { return Range(library, device)?.Now; }
        finally { Close(library); }
    }

    private static (int Now, int Max)? Range(nint library, nint device)
    {
        if (!NativeLibrary.TryGetExport(library, "nvmlDeviceGetClockOffsets", out var get)) return null;
        var info = new ClockOffset { Version = OffsetVersion, Type = ClockGraphics, PState = PState0 };
        return ((delegate* unmanaged[Cdecl]<nint, ClockOffset*, int>)get)(device, &info) == Success ? (info.OffsetMhz, info.MaxMhz) : null;
    }

    public void Settle(TuneRun stock)
    {
        if (_mode != "cooler") return;
        if (stock.PeakMhz is not { } peak || peak <= 0) throw new TuneDriverException("The card's clock didn't read, so Exo stopped.");
        uint highest = 0;
        var max = ((delegate* unmanaged[Cdecl]<nint, int, uint*, int>)Export(_library, "nvmlDeviceGetMaxClockInfo"))(_device, ClockGraphics, &highest);
        _cap = max == Success && highest > 0 ? Math.Min(peak, (int)highest) : peak;
    }

    public void Apply(int depth)
    {
        Limits.Check(depth);
        // The lock first: the offset never meets an uncapped card, even for a moment.
        if (_cap > 0) Answer(((delegate* unmanaged[Cdecl]<nint, uint, uint, int>)Export(_library, "nvmlDeviceSetGpuLockedClocks"))(_device, 0, (uint)_cap));
        SetOffset(depth);
    }

    public int? Read() => Range(_library, _device)?.Now;

    public void Stock()
    {
        SetOffset(0);
        Answer(((delegate* unmanaged[Cdecl]<nint, int>)Export(_library, "nvmlDeviceResetGpuLockedClocks"))(_device));
    }

    public int[]? SignIn(int depth) => [Limits.Check(depth), _cap];

    private void SetOffset(int mhz)
    {
        var info = new ClockOffset { Version = OffsetVersion, Type = ClockGraphics, PState = PState0, OffsetMhz = mhz };
        Answer(((delegate* unmanaged[Cdecl]<nint, ClockOffset*, int>)Export(_library, "nvmlDeviceSetClockOffsets"))(_device, &info));
    }

    private static void Answer(int nvml)
    {
        if (nvml == Success) return;
        AppLog.Warn($"Auto tune: NVML answered {nvml}.");
        // nvml.h: 3 not supported, 4 no permission.
        throw new TuneDriverException(nvml == 3 ? "This card's driver doesn't allow this, so nothing changed." : $"NVIDIA's driver said no (NVML {nvml}).");
    }

    public void Dispose() => Close(_library);
}

/// <summary>A Radeon: AMD's own tuning through ADLX, the search and its limits being AMD's. Depth 1 is that tuning on. The driver keeps it across restarts itself.</summary>
internal sealed class TuneRadeon(string mode, int? hotC, Func<bool> stop) : ITunePart
{
    private bool _on;

    public string Id => "radeon";
    public TuneLimits Limits { get; } = new(1, 1, 0, hotC is { } hot ? hot - 5 : 95, 60, 600, 180);

    public void Settle(TuneRun stock) { }

    public void Apply(int depth)
    {
        Limits.Check(depth);
        if (_on) return;
        if (!AmdAdlx.StartAutoTune(mode == "faster", TimeSpan.FromMinutes(45), stop)) throw new TuneDriverException("AMD's tuning didn't finish, so you're back at stock.");
        _on = true;
    }

    public int? Read() => AmdAdlx.AutoTuneOn(mode == "faster") is { } on ? on ? 1 : 0 : null;

    public void Stock()
    {
        if (!AmdAdlx.ResetTuning()) throw new TuneDriverException("AMD's driver didn't reset its tuning.");
        _on = false;
    }

    public int[]? SignIn(int depth) => null;
}
