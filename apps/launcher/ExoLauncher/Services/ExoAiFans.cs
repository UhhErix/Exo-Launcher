using System.Runtime.InteropServices;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal readonly record struct FanApplyResult(bool Ok, bool Unsupported, bool NeedsAdmin, string? Message);

internal interface IFanBackend : IDisposable
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    int? ReadTempC();
    int ReadFanCount();
    int? ReadFanPercent(int index);
    FanApplyResult SetFanPercent(int index, int percent);
    void RestoreAuto(int index);
}

internal sealed record FanStatus(
    bool Supported,
    string Preset,
    int? TempC,
    int? FanPercent,
    int? TargetPercent,
    int FanCount,
    string? Note);

/// <summary>
/// Anti-cheat-safe user-mode GPU fan curve controller via NVIDIA NVML (nvml.dll).
/// Falls back cleanly on non-NVIDIA hardware, mobile/locked VBIOS controllers, or driver refusals,
/// and always restores automatic VBIOS fan control on preset "auto" or app shutdown.
/// </summary>
internal sealed class ExoAiFans : IDisposable
{
    private const int DownwardHysteresisC = 3;
    private static readonly (int TempC, int Percent)[] AutoCurve = [(40, 35), (55, 48), (68, 68), (78, 86), (85, 100)];
    private static readonly (int TempC, int Percent)[] QuietCurve = [(40, 30), (60, 42), (75, 65), (82, 85), (86, 100)];
    private static readonly (int TempC, int Percent)[] BalancedCurve = [(40, 35), (55, 50), (70, 72), (80, 90), (85, 100)];
    private static readonly (int TempC, int Percent)[] PerformanceCurve = [(35, 45), (50, 65), (65, 82), (75, 95), (82, 100)];

    private readonly Func<IFanBackend> _backendFactory;
    private readonly Lock _gate = new();
    private IFanBackend? _backend;
    private readonly HashSet<int> _touchedFans = [];
    private string _preset = "auto";
    private int? _lastTargetPct;
    private int? _lastTempC;
    private string? _note;
    private bool _blocked;

    internal ExoAiFans(Func<IFanBackend>? backendFactory = null) =>
        _backendFactory = backendFactory ?? (() => NvmlFanBackend.Open());

    internal static int? EvaluateCurve(string preset, int tempC, int? lastTargetPct = null, int? lastTempC = null)
    {
        var norm = Models.ExoAiSettings.NormalizePreset(preset);
        var curve = norm switch
        {
            "quiet" => QuietCurve,
            "balanced" => BalancedCurve,
            "performance" => PerformanceCurve,
            _ => AutoCurve,
        };
        var raw = Interpolate(curve, tempC);
        var clamped = ApplySafetyClamp(tempC, raw);
        if (lastTargetPct is { } prevPct && lastTempC is { } prevTemp && clamped < prevPct && tempC > prevTemp - DownwardHysteresisC)
        {
            // Keep safety floors active even when holding downward hysteresis.
            return Math.Max(prevPct, ApplySafetyClamp(tempC, clamped));
        }
        return clamped;
    }

    private static int ApplySafetyClamp(int tempC, int pct)
    {
        if (tempC >= 85) return 100;
        if (tempC >= 80 && pct < 85) pct = 85;
        return Math.Clamp(pct, 30, 100);
    }

    private static int Interpolate((int TempC, int Percent)[] points, int tempC)
    {
        if (tempC <= points[0].TempC) return points[0].Percent;
        for (var i = 1; i < points.Length; i++)
        {
            var (t1, p1) = points[i];
            if (tempC <= t1)
            {
                var (t0, p0) = points[i - 1];
                var span = Math.Max(1, t1 - t0);
                return p0 + (int)Math.Round((tempC - t0) * (double)(p1 - p0) / span);
            }
        }
        return points[^1].Percent;
    }

    private IFanBackend EnsureBackendLocked()
    {
        if (_backend is null)
            _backend = _backendFactory();
        return _backend;
    }

    internal FanStatus SetPreset(string preset)
    {
        lock (_gate)
        {
            _preset = Models.ExoAiSettings.NormalizePreset(preset);
            _note = null;
            _blocked = false;
            _lastTargetPct = null;
            _lastTempC = null;
            TickLocked();
            return ReadStatusLocked();
        }
    }

    internal void Tick()
    {
        lock (_gate) TickLocked();
    }

    internal void ResetCache()
    {
        lock (_gate)
        {
            _lastTargetPct = null;
            _lastTempC = null;
            _touchedFans.Clear();
            TickLocked();
        }
    }

    private void TickLocked()
    {
        if (_blocked) return;
        var backend = EnsureBackendLocked();
        if (!backend.IsAvailable)
        {
            _note = _preset == "auto" ? null : backend.UnavailableReason ?? "Custom fan curves are only available on supported NVIDIA GPUs.";
            _blocked = true;
            return;
        }
        if (backend.ReadTempC() is not { } tempC) return;
        var fanCount = Math.Max(0, backend.ReadFanCount());
        if (fanCount == 0)
        {
            _note = _preset == "auto" ? null : "This GPU doesn't expose software fan control.";
            _blocked = true;
            return;
        }
        var target = EvaluateCurve(_preset, tempC, _lastTargetPct, _lastTempC);
        if (target is null) return;
        if (target == _lastTargetPct && _touchedFans.Count > 0)
        {
            _lastTempC = tempC;
            return;
        }

        for (var i = 0; i < fanCount; i++)
        {
            var res = backend.SetFanPercent(i, target.Value);
            if (!res.Ok)
            {
                _note = _preset == "auto" ? null : res.Message ?? (res.NeedsAdmin
                    ? "Your NVIDIA driver needs administrator permission to adjust fan speeds."
                    : "This card doesn't allow custom software fan curves.");
                _blocked = true;
                _lastTargetPct = null;
                RestoreAllAutoLocked();
                return;
            }
            _touchedFans.Add(i);
        }
        _lastTargetPct = target;
        _lastTempC = tempC;
        _note = null;
    }

    internal FanStatus ReadStatus()
    {
        lock (_gate) return ReadStatusLocked();
    }

    private FanStatus ReadStatusLocked()
    {
        var backend = EnsureBackendLocked();
        if (!backend.IsAvailable)
            return new(false, _preset, null, null, null, 0, _note ?? backend.UnavailableReason);

        var temp = backend.ReadTempC();
        var count = Math.Max(0, backend.ReadFanCount());
        int? fanPct = count > 0 ? backend.ReadFanPercent(0) : null;
        var supported = count > 0 && !_blocked;
        var note = _note ?? (count == 0 ? "This GPU doesn't report controllable fans." : null);
        return new(supported, _preset, temp, fanPct, _lastTargetPct, count, note);
    }

    internal void Shutdown()
    {
        lock (_gate)
        {
            RestoreAllAutoLocked();
            _preset = "auto";
            _lastTargetPct = null;
            _lastTempC = null;
            _backend?.Dispose();
            _backend = null;
        }
    }

    public void Dispose() => Shutdown();

    private void RestoreAllAutoLocked()
    {
        if (_backend is null || _touchedFans.Count == 0) return;
        foreach (var fan in _touchedFans)
        {
            try { _backend.RestoreAuto(fan); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Warn("ExoAiFans: restore failed: " + ex.GetType().Name); }
        }
        _touchedFans.Clear();
    }

    private sealed unsafe class NvmlFanBackend : IFanBackend
    {
        private const int Success = 0, ErrorNotSupported = 3, ErrorNoPermission = 4;
        private const uint TempSensorGpu = 0, FanPolicyAuto = 0, FanPolicyManual = 1;

        private readonly nint _library;
        private readonly nint _device;
        private bool _disposed;

        public bool IsAvailable { get; }
        public string? UnavailableReason { get; }

        private NvmlFanBackend(nint library, nint device, string? unavailable)
        {
            _library = library;
            _device = device;
            IsAvailable = library != 0 && device != 0 && unavailable is null;
            UnavailableReason = unavailable;
        }

        internal static NvmlFanBackend Open()
        {
            if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "nvml.dll"), out var lib))
                return new(0, 0, "Custom GPU fan curves need an NVIDIA driver with NVML installed.");
            if (!NativeLibrary.TryGetExport(lib, "nvmlInit_v2", out var initPtr)
                || ((delegate* unmanaged[Cdecl]<int>)initPtr)() != Success)
            {
                NativeLibrary.Free(lib);
                return new(0, 0, "NVML did not initialize.");
            }
            nint dev = 0;
            if (!NativeLibrary.TryGetExport(lib, "nvmlDeviceGetHandleByIndex_v2", out var handlePtr)
                || ((delegate* unmanaged[Cdecl]<uint, nint*, int>)handlePtr)(0, &dev) != Success)
            {
                if (NativeLibrary.TryGetExport(lib, "nvmlShutdown", out var shutPtr))
                    ((delegate* unmanaged[Cdecl]<int>)shutPtr)();
                NativeLibrary.Free(lib);
                return new(0, 0, "No NVIDIA GPU was found for fan control.");
            }
            return new(lib, dev, null);
        }

        public int? ReadTempC()
        {
            if (!IsAvailable || !NativeLibrary.TryGetExport(_library, "nvmlDeviceGetTemperature", out var ptr)) return null;
            uint temp = 0;
            return ((delegate* unmanaged[Cdecl]<nint, uint, uint*, int>)ptr)(_device, TempSensorGpu, &temp) == Success ? (int)temp : null;
        }

        public int ReadFanCount()
        {
            if (!IsAvailable) return 0;
            if (NativeLibrary.TryGetExport(_library, "nvmlDeviceGetNumFans", out var ptr))
            {
                uint count = 0;
                if (((delegate* unmanaged[Cdecl]<nint, uint*, int>)ptr)(_device, &count) == Success) return (int)count;
            }
            uint speed = 0;
            if (NativeLibrary.TryGetExport(_library, "nvmlDeviceGetFanSpeed", out var speedPtr)
                && ((delegate* unmanaged[Cdecl]<nint, uint*, int>)speedPtr)(_device, &speed) == Success)
                return 1;
            return 0;
        }

        public int? ReadFanPercent(int index)
        {
            if (!IsAvailable) return null;
            uint speed = 0;
            if (NativeLibrary.TryGetExport(_library, "nvmlDeviceGetFanSpeed_v2", out var v2)
                && ((delegate* unmanaged[Cdecl]<nint, uint, uint*, int>)v2)(_device, (uint)index, &speed) == Success)
                return (int)speed;
            if (index == 0 && NativeLibrary.TryGetExport(_library, "nvmlDeviceGetFanSpeed", out var v1)
                && ((delegate* unmanaged[Cdecl]<nint, uint*, int>)v1)(_device, &speed) == Success)
                return (int)speed;
            return null;
        }

        public FanApplyResult SetFanPercent(int index, int percent)
        {
            if (HostMode.IsDisposable) return new(false, false, false, HostMode.Refusal);
            if (!IsAvailable) return new(false, true, false, UnavailableReason);
            if (!NativeLibrary.TryGetExport(_library, "nvmlDeviceSetFanSpeed_v2", out var setSpeed))
                return new(false, true, false, "This NVIDIA driver doesn't support custom fan speeds.");

            if (NativeLibrary.TryGetExport(_library, "nvmlDeviceSetFanControlPolicy", out var setPolicy))
            {
                var policyRc = ((delegate* unmanaged[Cdecl]<nint, uint, uint, int>)setPolicy)(_device, (uint)index, FanPolicyManual);
                if (policyRc is ErrorNoPermission) return new(false, false, true, "Your NVIDIA driver requires administrator rights to change fan speeds.");
                if (policyRc is ErrorNotSupported) return new(false, true, false, "This GPU's firmware locks fan control to auto.");
            }

            var rc = ((delegate* unmanaged[Cdecl]<nint, uint, uint, int>)setSpeed)(_device, (uint)index, (uint)percent);
            if (rc == Success) return new(true, false, false, null);
            return new(false, rc == ErrorNotSupported, rc == ErrorNoPermission, rc switch
            {
                ErrorNotSupported => "This GPU doesn't allow custom fan speed control.",
                ErrorNoPermission => "Your NVIDIA driver requires administrator rights to change fan speeds.",
                _ => $"NVIDIA driver returned NVML {rc} when setting fan speed.",
            });
        }

        public void RestoreAuto(int index)
        {
            if (HostMode.IsDisposable) return;
            if (!IsAvailable) return;
            if (NativeLibrary.TryGetExport(_library, "nvmlDeviceSetDefaultFanSpeed_v2", out var reset))
                ((delegate* unmanaged[Cdecl]<nint, uint, int>)reset)(_device, (uint)index);
            if (NativeLibrary.TryGetExport(_library, "nvmlDeviceSetFanControlPolicy", out var setPolicy))
                ((delegate* unmanaged[Cdecl]<nint, uint, uint, int>)setPolicy)(_device, (uint)index, FanPolicyAuto);
        }

        public void Dispose()
        {
            if (_disposed || _library == 0) return;
            _disposed = true;
            try
            {
                if (NativeLibrary.TryGetExport(_library, "nvmlShutdown", out var shut))
                    ((delegate* unmanaged[Cdecl]<int>)shut)();
            }
            finally { NativeLibrary.Free(_library); }
        }
    }
}
