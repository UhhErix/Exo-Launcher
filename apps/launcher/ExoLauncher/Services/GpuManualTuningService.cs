using System.Text.Json;
using System.Text.Json.Serialization;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

internal sealed class GpuManualTuningService : IDisposable
{
    internal const string StoreFileName = "gpu-manual-tuning.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private sealed record PersistedData
    {
        public string? DeviceId { get; init; }
        public string Mode { get; init; } = "auto";
        public GpuManualProfile? FactoryDefaults { get; init; }
        public GpuManualProfile? SavedProfile { get; init; }
        public GpuManualProfile? LastKnownGood { get; init; }
        public HashSet<string> AcceptedWaivers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public bool PausedAfterCrash { get; init; }
        public string? PauseReason { get; init; }
        public DateTimeOffset? AppliedAtUtc { get; init; }
    }

    private sealed record QueuedOperation(bool IsReset, GpuManualProfile? Profile, string Reason);

    internal static GpuManualTuningService Shared { get; } = CreateDefault();

    private readonly IGpuTuningBackend _backend;
    private readonly TuneStore _tuneStore;
    private readonly string _dataPath;
    private readonly Func<string?> _protectedGameName;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<IReadOnlyList<TuneCrashEvent>> _recentCrashEvents;
    private readonly Func<uint, IGpuComputePassRunner> _runnerFactory;
    private readonly Func<string, IGpuSensorSampler> _samplerFactory;
    private readonly Action<int, int>? _softwareFanWriter;
    private readonly Action? _softwareFanRestore;
    private readonly TimeSpan _undervoltStepDuration;
    private readonly Lock _gate = new();

    private PersistedData _data;
    private GpuTuningCapabilities? _cachedCaps;
    private GpuManualProfile? _activeApplied;
    private QueuedOperation? _queued;
    private Timer? _fanTimer;
    private int? _lastFanTempC;
    private readonly Dictionary<int, int> _lastFanSpeeds = [];
    private CancellationTokenSource? _testCts;
    private GpuStabilityProgress _testProgress = IdleProgress();
    private bool _disposed;

    internal event Action<GpuManualTuningState>? StateChanged;
    internal event Action<GpuStabilityProgress>? TestProgressChanged;

    internal GpuManualTuningService(
        IGpuTuningBackend backend,
        TuneStore tuneStore,
        string dataPath,
        Func<string?> protectedGameName,
        Func<DateTimeOffset>? now = null,
        Func<IReadOnlyList<TuneCrashEvent>>? recentCrashEvents = null,
        Func<uint, IGpuComputePassRunner>? runnerFactory = null,
        Func<string, IGpuSensorSampler>? samplerFactory = null,
        Action<int, int>? softwareFanWriter = null,
        Action? softwareFanRestore = null,
        TimeSpan? undervoltStepDuration = null)
    {
        _backend = backend;
        _tuneStore = tuneStore;
        _dataPath = dataPath;
        _protectedGameName = protectedGameName;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _recentCrashEvents = recentCrashEvents ?? (() => []);
        _runnerFactory = runnerFactory ?? (vendorId => new D3D11GpuComputePassRunner(vendorId));
        _samplerFactory = samplerFactory ?? (vendor => new LiveGpuSensorSampler(vendor));
        _softwareFanWriter = softwareFanWriter;
        _softwareFanRestore = softwareFanRestore;
        _undervoltStepDuration = undervoltStepDuration ?? TimeSpan.FromSeconds(15);
        _data = LoadPersisted();
    }

    private static GpuManualTuningService CreateDefault()
    {
        var dataPath = Path.Combine(PathHelper.AppDataDir, StoreFileName);
        var tuneStore = new TuneStore(AutoTuneWorker.Folder);
        var vendor = GraphicsService.Vendor();
        var isLaptop = PcHealth.ThisPcIsLaptop;

        NvidiaNvApiManualTuningBackend? nvapi = null;
        IGpuTuningBackend backend;
        if (vendor == "nvidia")
        {
            nvapi = new NvidiaNvApiManualTuningBackend(isLaptop);
            var nvml = new NvidiaNvmlManualTuningBackend(isLaptop);
            backend = new FallbackGpuTuningBackend(nvapi, nvml);
        }
        else if (vendor == "amd")
        {
            backend = new AmdAdlxManualTuningBackend(isLaptop);
        }
        else if (vendor == "intel")
        {
            backend = new IntelIgclManualTuningBackend(isLaptop, deviceId => IsWaiverSavedOnDisk(dataPath, deviceId));
        }
        else
        {
            backend = new UnsupportedGpuTuningBackend();
        }

        Action<int, int>? fanWriter = vendor == "nvidia"
            ? (idx, pct) =>
            {
                if (nvapi is not null && backend is FallbackGpuTuningBackend fb && !fb.UsingFallback)
                {
                    if (nvapi.SetFanSpeed(idx, pct)) return;
                }
                _ = NvidiaNvmlManualTuningBackend.SetFanSpeed(idx, pct);
            }
            : null;

        Action? fanRestore = vendor == "nvidia"
            ? () =>
            {
                _ = nvapi?.RestoreAllFansAuto();
                NvidiaNvmlManualTuningBackend.RestoreAllFansAuto();
            }
            : null;

        return new GpuManualTuningService(
            backend,
            tuneStore,
            dataPath,
            AntiCheatCheck.RunningProtectedGameName,
            softwareFanWriter: fanWriter,
            softwareFanRestore: fanRestore);
    }

    internal GpuManualTuningState ReadState()
    {
        lock (_gate)
        {
            var caps = ProbeLocked();
            EnsureFactorySnapshotLocked(caps);
            return BuildStateLocked(ok: true, message: null, caps);
        }
    }

    internal GpuManualTuningState AcceptWaiver(string? deviceId = null)
    {
        lock (_gate)
        {
            var caps = ProbeLocked();
            var targetId = string.IsNullOrWhiteSpace(deviceId) ? caps.DeviceId : deviceId.Trim();
            if (string.IsNullOrWhiteSpace(targetId))
                return BuildStateLocked(false, "No GPU device identifier available for waiver consent.", caps);

            if (caps.Vendor == "intel" && caps.Backend == "igcl")
                _ = _backend.AcceptWaiver();

            var nextWaivers = new HashSet<string>(_data.AcceptedWaivers, StringComparer.OrdinalIgnoreCase) { targetId };
            _data = _data with { AcceptedWaivers = nextWaivers };
            SavePersistedLocked();
            _cachedCaps = caps with { WaiverAccepted = true };
            var state = BuildStateLocked(true, null, _cachedCaps);
            NotifyStateChanged(state);
            return state;
        }
    }

    internal GpuManualTuningState Apply(GpuManualProfile requested, bool forceReapply = false)
    {
        lock (_gate)
        {
            var caps = ProbeLocked();
            EnsureFactorySnapshotLocked(caps);

            var validation = GpuTuningPolicy.ValidateAndClamp(caps, requested);
            if (!validation.Allowed || validation.Clamped is not { } clamped)
                return BuildStateLocked(false, validation.RefusalReason ?? "Tuning settings refused.", caps);

            if (clamped.Mode != "manual")
                return ResetLocked(caps);

            if (_protectedGameName() is { } gameName)
            {
                var reason = $"Queued until {gameName} exits (anti-cheat safe).";
                _queued = new QueuedOperation(false, clamped, reason);
                _data = _data with { SavedProfile = clamped };
                SavePersistedLocked();
                var queuedState = BuildStateLocked(true, reason, caps);
                NotifyStateChanged(queuedState);
                return queuedState;
            }

            return ApplyImmediateLocked(caps, clamped, clearCrashPause: true);
        }
    }

    internal GpuManualTuningState ReapplySaved()
    {
        lock (_gate)
        {
            var caps = ProbeLocked();
            if (_data.SavedProfile is not { Mode: "manual" } saved)
                return BuildStateLocked(false, "No saved manual profile to re-apply.", caps);
            return Apply(saved, forceReapply: true);
        }
    }

    internal GpuManualTuningState Reset()
    {
        lock (_gate)
        {
            var caps = ProbeLocked();
            return ResetLocked(caps);
        }
    }

    internal GpuManualTuningState EvaluateLifecycleTrigger(string trigger)
    {
        lock (_gate)
        {
            var caps = ProbeLocked();
            if (_data.Mode != "manual" || _data.SavedProfile is not { Mode: "manual" } saved)
                return BuildStateLocked(true, null, caps);

            var partId = GpuTuningPolicy.PartIdForVendor(caps.Vendor);
            var verdict = TunePersistence.EvaluateReapply(
                partId: partId,
                trigger: trigger,
                testingMarkerPresent: _tuneStore.Marker() is not null,
                alreadyPaused: _data.PausedAfterCrash || _tuneStore.Held(),
                uncleanApplyPending: File.Exists(Path.Combine(_tuneStore.Folder, TuneStore.ApplyingFile)),
                lastAppliedUtc: _data.AppliedAtUtc ?? _tuneStore.ReadAppliedAt(),
                recentEvents: _recentCrashEvents());

            if (verdict.Decision == TuneReapplyDecision.PauseAfterCrash)
            {
                _data = _data with
                {
                    Mode = "auto",
                    PausedAfterCrash = true,
                    PauseReason = verdict.Reason ?? TunePersistence.PausedNote,
                };
                SavePersistedLocked();
                StopFanLoopLocked(restoreAuto: true);
                _ = _backend.ResetToFactory();
                _activeApplied = _data.FactoryDefaults ?? GpuManualProfile.Auto;
                var paused = BuildStateLocked(true, _data.PauseReason, caps);
                NotifyStateChanged(paused);
                return paused;
            }

            if (verdict.Decision == TuneReapplyDecision.Apply)
            {
                if (_protectedGameName() is { } gameName)
                {
                    _queued = new QueuedOperation(false, saved, $"Queued until {gameName} exits (anti-cheat safe).");
                    var q = BuildStateLocked(true, _queued.Reason, caps);
                    NotifyStateChanged(q);
                    return q;
                }
                return ApplyImmediateLocked(caps, saved, clearCrashPause: false);
            }

            return BuildStateLocked(true, verdict.Reason, caps);
        }
    }

    internal void OnGameStateChanged()
    {
        GpuManualTuningState? updated = null;
        lock (_gate)
        {
            if (_queued is null) return;
            if (_protectedGameName() is not null) return;
            var pending = _queued;
            _queued = null;
            var caps = ProbeLocked();
            updated = pending.IsReset
                ? ResetImmediateLocked(caps)
                : pending.Profile is { } p
                    ? ApplyImmediateLocked(caps, p, clearCrashPause: true)
                    : BuildStateLocked(true, null, caps);
        }
        if (updated is not null) NotifyStateChanged(updated);
    }

    internal GpuManualTuningState StartStabilityTest(string? durationPreset, GpuManualProfile? candidate = null, TimeSpan? overrideDuration = null)
    {
        lock (_gate)
        {
            var caps = ProbeLocked();
            EnsureFactorySnapshotLocked(caps);

            if (caps.LockedByMaker)
                return BuildStateLocked(false, caps.LockReason ?? GpuTuningCapabilities.LockedMessage, caps);
            if (_protectedGameName() is { } gameName)
                return BuildStateLocked(false, $"Close {gameName} before running a GPU stability test.", caps);
            if (_testProgress.Running)
                return BuildStateLocked(false, "A stability test is already running.", caps);

            var previousGood = _data.LastKnownGood ?? _data.FactoryDefaults ?? GpuManualProfile.Auto;
            GpuManualProfile? toTest = null;
            if (candidate is not null)
            {
                var validation = GpuTuningPolicy.ValidateAndClamp(caps, candidate);
                if (!validation.Allowed || validation.Clamped is not { } clamped)
                    return BuildStateLocked(false, validation.RefusalReason ?? "Invalid tuning settings.", caps);
                toTest = clamped;
            }

            var duration = overrideDuration ?? GpuStabilityTest.DurationFor(durationPreset);
            var cts = new CancellationTokenSource();
            _testCts = cts;
            var totalSec = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));
            _testProgress = new GpuStabilityProgress(
                Running: true,
                Kind: "stability",
                Phase: "starting",
                ElapsedSec: 0,
                TotalSec: totalSec,
                Passes: 0,
                TempC: null,
                ClockMhz: null,
                ChecksumOk: true,
                CurrentStep: $"Starting {totalSec} s compute stability test",
                Passed: null,
                RolledBack: false,
                FailureReason: null,
                Summary: null);

            var state = BuildStateLocked(true, null, caps);
            NotifyTestProgress(_testProgress);
            NotifyStateChanged(state);

            _ = Task.Run(() => RunStabilityWorker(caps, toTest, previousGood, duration, cts));
            return state;
        }
    }

    internal GpuManualTuningState StartUndervoltSearch()
    {
        lock (_gate)
        {
            var caps = ProbeLocked();
            EnsureFactorySnapshotLocked(caps);

            if (caps.LockedByMaker)
                return BuildStateLocked(false, caps.LockReason ?? GpuTuningCapabilities.LockedMessage, caps);
            if (caps.RequiresWaiver && !caps.WaiverAccepted)
                return BuildStateLocked(false, "Accept Intel's overclocking waiver before searching for an undervolt.", caps);
            if (!caps.SupportsUndervoltSearch)
                return BuildStateLocked(false, "Undervolt search is not supported on this GPU.", caps);
            if (_protectedGameName() is { } gameName)
                return BuildStateLocked(false, $"Close {gameName} before running Find my undervolt.", caps);
            if (_testProgress.Running)
                return BuildStateLocked(false, "A stability test is already running.", caps);

            var cts = new CancellationTokenSource();
            _testCts = cts;
            _testProgress = new GpuStabilityProgress(
                Running: true,
                Kind: "undervolt",
                Phase: "starting",
                ElapsedSec: 0,
                TotalSec: 1,
                Passes: 0,
                TempC: null,
                ClockMhz: null,
                ChecksumOk: true,
                CurrentStep: "Preparing undervolt search",
                Passed: null,
                RolledBack: false,
                FailureReason: null,
                Summary: null);

            var state = BuildStateLocked(true, null, caps);
            NotifyTestProgress(_testProgress);
            NotifyStateChanged(state);

            _ = Task.Run(() => RunUndervoltWorker(caps, cts));
            return state;
        }
    }

    internal GpuManualTuningState StopTest()
    {
        lock (_gate)
        {
            _testCts?.Cancel();
            var caps = ProbeLocked();
            return BuildStateLocked(true, null, caps);
        }
    }

    internal GpuStabilityResult RunStabilityTestSynchronously(GpuStabilityOptions options, GpuManualProfile? candidate = null)
    {
        GpuTuningCapabilities caps;
        GpuManualProfile previousGood;
        lock (_gate)
        {
            caps = ProbeLocked();
            EnsureFactorySnapshotLocked(caps);
            previousGood = _data.LastKnownGood ?? _data.FactoryDefaults ?? GpuManualProfile.Auto;
            if (candidate is not null)
            {
                var validation = GpuTuningPolicy.ValidateAndClamp(caps, candidate);
                if (!validation.Allowed || validation.Clamped is not { } clamped)
                    return new(false, false, false, true, 0, 0, null, null, null, null, validation.RefusalReason ?? "Invalid profile.");
                var applied = ApplyImmediateLocked(caps, clamped, clearCrashPause: true);
                if (!applied.Ok)
                    return new(false, false, false, true, 0, 0, null, null, null, null, applied.Message ?? "Apply failed.");
            }
        }

        var vendorId = GpuStabilityTest.PciVendorIdFor(caps.Vendor);
        var sampler = _samplerFactory(caps.Vendor);
        var result = GpuStabilityTest.Run(
            () => _runnerFactory(vendorId),
            sampler,
            options,
            rollback: () => RollbackTo(previousGood),
            onProgress: p =>
            {
                lock (_gate) _testProgress = p;
                NotifyTestProgress(p);
            },
            isCancelled: () => _testCts?.IsCancellationRequested == true);

        if (result.Passed && candidate is not null)
        {
            lock (_gate)
            {
                _data = _data with { LastKnownGood = _activeApplied ?? candidate };
                SavePersistedLocked();
            }
        }
        return result;
    }

    private void RunStabilityWorker(
        GpuTuningCapabilities caps,
        GpuManualProfile? candidate,
        GpuManualProfile previousGood,
        TimeSpan duration,
        CancellationTokenSource cts)
    {
        try
        {
            if (candidate is not null)
            {
                lock (_gate)
                {
                    var applied = ApplyImmediateLocked(caps, candidate, clearCrashPause: true);
                    if (!applied.Ok)
                    {
                        _testProgress = _testProgress with
                        {
                            Running = false,
                            Phase = "failed",
                            Passed = false,
                            FailureReason = applied.Message ?? "Could not apply settings to test.",
                            Summary = applied.Message,
                        };
                        NotifyTestProgress(_testProgress);
                        NotifyStateChanged(BuildStateLocked(false, applied.Message, caps));
                        return;
                    }
                }
            }

            _tuneStore.WriteMarker(GpuTuningPolicy.PartIdForVendor(caps.Vendor), 1);
            var vendorId = GpuStabilityTest.PciVendorIdFor(caps.Vendor);
            var sampler = _samplerFactory(caps.Vendor);
            var maxTemp = candidate?.TempLimitC is { } tl ? Math.Min(92.0, tl + 4.0) : 90.0;
            var result = GpuStabilityTest.Run(
                () => _runnerFactory(vendorId),
                sampler,
                new GpuStabilityOptions(duration, MaxTempC: maxTemp),
                rollback: () => RollbackTo(previousGood),
                onProgress: p =>
                {
                    lock (_gate) _testProgress = p;
                    NotifyTestProgress(p);
                },
                isCancelled: () => cts.IsCancellationRequested);

            lock (_gate)
            {
                if (result.Passed)
                {
                    _data = _data with { LastKnownGood = _activeApplied ?? _data.SavedProfile };
                    SavePersistedLocked();
                }
                NotifyStateChanged(BuildStateLocked(true, null, ProbeLocked()));
            }
        }
        finally
        {
            if (Directory.Exists(_tuneStore.Folder)) _tuneStore.ClearMarker();
            lock (_gate)
            {
                if (ReferenceEquals(_testCts, cts)) _testCts = null;
            }
            cts.Dispose();
        }
    }

    private void RunUndervoltWorker(GpuTuningCapabilities caps, CancellationTokenSource cts)
    {
        try
        {
            _tuneStore.WriteMarker(GpuTuningPolicy.PartIdForVendor(caps.Vendor), 1);
            var stock = _data.FactoryDefaults ?? GpuManualProfile.Auto;
            var vendorId = GpuStabilityTest.PciVendorIdFor(caps.Vendor);
            var sampler = _samplerFactory(caps.Vendor);
            var stockClock = sampler.Sample()?.ClockMhz;

            var result = GpuUndervoltSearch.Run(
                caps,
                stock,
                applyProfile: p => _backend.Apply(p),
                resetToStock: () => _backend.ResetToFactory(),
                runStepTest: step => GpuStabilityTest.Run(
                    () => _runnerFactory(vendorId),
                    sampler,
                    new GpuStabilityOptions(_undervoltStepDuration),
                    rollback: () => _ = _backend.ResetToFactory(),
                    onProgress: p =>
                    {
                        lock (_gate) _testProgress = p;
                        NotifyTestProgress(p);
                    },
                    isCancelled: () => cts.IsCancellationRequested,
                    kind: "undervolt",
                    stepLabel: step.Label),
                onProgress: p =>
                {
                    lock (_gate) _testProgress = p;
                    NotifyTestProgress(p);
                },
                isCancelled: () => cts.IsCancellationRequested,
                stockPeakClockMhz: stockClock);

            lock (_gate)
            {
                if (result.AppliedSafeProfile)
                {
                    _activeApplied = result.FinalProfile;
                    _data = _data with
                    {
                        Mode = "manual",
                        SavedProfile = result.FinalProfile,
                        LastKnownGood = result.FinalProfile,
                        PausedAfterCrash = false,
                        PauseReason = null,
                        AppliedAtUtc = _now(),
                    };
                    SavePersistedLocked();
                    _tuneStore.WriteAppliedAt(_now());
                    if (Directory.Exists(_tuneStore.Folder)) _tuneStore.ClearHeld();
                }
                else
                {
                    _activeApplied = stock;
                    _data = _data with { Mode = "auto" };
                    SavePersistedLocked();
                }
                NotifyStateChanged(BuildStateLocked(true, result.Summary, ProbeLocked()));
            }
        }
        finally
        {
            if (Directory.Exists(_tuneStore.Folder)) _tuneStore.ClearMarker();
            lock (_gate)
            {
                if (ReferenceEquals(_testCts, cts)) _testCts = null;
            }
            cts.Dispose();
        }
    }

    private void RollbackTo(GpuManualProfile previousGood)
    {
        lock (_gate)
        {
            var caps = ProbeLocked();
            if (previousGood.Mode == "manual")
            {
                var reapplied = _backend.Apply(previousGood);
                if (reapplied.Ok)
                {
                    _activeApplied = previousGood;
                    _data = _data with { Mode = "manual", SavedProfile = previousGood };
                    SavePersistedLocked();
                    ConfigureFanLoopLocked(caps, previousGood);
                    return;
                }
            }
            _ = ResetImmediateLocked(caps);
        }
    }

    private GpuManualTuningState ResetLocked(GpuTuningCapabilities caps)
    {
        if (_protectedGameName() is { } gameName)
        {
            var reason = $"Reset queued until {gameName} exits (anti-cheat safe).";
            _queued = new QueuedOperation(true, null, reason);
            var q = BuildStateLocked(true, reason, caps);
            NotifyStateChanged(q);
            return q;
        }
        return ResetImmediateLocked(caps);
    }

    private GpuManualTuningState ResetImmediateLocked(GpuTuningCapabilities caps)
    {
        StopFanLoopLocked(restoreAuto: true);
        var res = _backend.ResetToFactory();
        if (!res.Ok)
            return BuildStateLocked(false, res.Message ?? "Driver could not reset GPU tuning.", caps);

        _queued = null;
        _activeApplied = _data.FactoryDefaults ?? GpuManualProfile.Auto;
        _data = _data with
        {
            Mode = "auto",
            SavedProfile = GpuManualProfile.Auto,
            LastKnownGood = _data.FactoryDefaults ?? GpuManualProfile.Auto,
            PausedAfterCrash = false,
            PauseReason = null,
            AppliedAtUtc = null,
        };
        SavePersistedLocked();
        if (Directory.Exists(_tuneStore.Folder))
        {
            _tuneStore.ClearHeld();
            _tuneStore.ClearAppliedAt();
        }
        _cachedCaps = null;
        var state = BuildStateLocked(true, null, ProbeLocked());
        NotifyStateChanged(state);
        return state;
    }

    private GpuManualTuningState ApplyImmediateLocked(GpuTuningCapabilities caps, GpuManualProfile clamped, bool clearCrashPause)
    {
        var res = _backend.Apply(clamped);
        if (!res.Ok)
            return BuildStateLocked(false, res.Message ?? "GPU driver refused manual tuning.", ProbeLocked());

        _cachedCaps = null;
        var nextCaps = ProbeLocked();
        _activeApplied = clamped;
        var now = _now();
        _data = _data with
        {
            DeviceId = nextCaps.DeviceId,
            Mode = "manual",
            SavedProfile = clamped,
            PausedAfterCrash = clearCrashPause ? false : _data.PausedAfterCrash,
            PauseReason = clearCrashPause ? null : _data.PauseReason,
            AppliedAtUtc = now,
        };
        SavePersistedLocked();
        if (clearCrashPause && Directory.Exists(_tuneStore.Folder)) _tuneStore.ClearHeld();
        _tuneStore.WriteAppliedAt(now);
        ConfigureFanLoopLocked(nextCaps, clamped);

        var state = BuildStateLocked(true, null, nextCaps);
        NotifyStateChanged(state);
        return state;
    }

    private void ConfigureFanLoopLocked(GpuTuningCapabilities caps, GpuManualProfile profile)
    {
        if (!caps.SupportsSoftwareFanCurve || _softwareFanWriter is null)
        {
            StopFanLoopLocked(restoreAuto: false);
            return;
        }
        var hasCurve = profile.FanCurve is { Count: > 0 } || profile.FanChannels is { Count: > 0 } && profile.FanChannels.Any(c => c.Manual && c.Points.Count > 0);
        if (!hasCurve)
        {
            StopFanLoopLocked(restoreAuto: true);
            return;
        }
        _lastFanSpeeds.Clear();
        _lastFanTempC = null;
        _fanTimer ??= new Timer(_ => TickSoftwareFans(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    private void StopFanLoopLocked(bool restoreAuto)
    {
        _fanTimer?.Dispose();
        _fanTimer = null;
        _lastFanSpeeds.Clear();
        _lastFanTempC = null;
        if (restoreAuto)
        {
            try { _softwareFanRestore?.Invoke(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Warn("GPU manual fan restore failed: " + ex.Message); }
        }
    }

    private void TickSoftwareFans()
    {
        lock (_gate)
        {
            if (_disposed || _activeApplied is not { Mode: "manual" } profile || _softwareFanWriter is null) return;
            var caps = _cachedCaps ?? ProbeLocked();
            if (!caps.SupportsSoftwareFanCurve) return;
            var tempD = _samplerFactory(caps.Vendor).Sample()?.TemperatureC;
            if (tempD is not { } tempRaw) return;
            var tempC = (int)Math.Round(tempRaw);
            var fanCount = Math.Max(1, caps.FanCount);

            for (var i = 0; i < fanCount; i++)
            {
                var channel = profile.FanChannels?.FirstOrDefault(c => c.FanIndex == i);
                var curve = channel is { Manual: true, Points.Count: > 0 } ? channel.Points : profile.FanCurve;
                if (curve is not { Count: > 0 }) continue;
                var target = EvaluateFanCurve(curve, tempC, _lastFanSpeeds.TryGetValue(i, out var prev) ? prev : null, _lastFanTempC);
                if (_lastFanSpeeds.TryGetValue(i, out var existing) && existing == target) continue;
                _softwareFanWriter(i, target);
                _lastFanSpeeds[i] = target;
            }
            _lastFanTempC = tempC;
        }
    }

    internal static int EvaluateFanCurve(IReadOnlyList<GpuFanPoint> curve, int tempC, int? lastSpeed, int? lastTempC)
    {
        var ordered = curve.OrderBy(p => p.TempC).ToList();
        int raw;
        if (tempC <= ordered[0].TempC) raw = ordered[0].SpeedPercent;
        else if (tempC >= ordered[^1].TempC) raw = ordered[^1].SpeedPercent;
        else
        {
            raw = ordered[^1].SpeedPercent;
            for (var i = 1; i < ordered.Count; i++)
            {
                if (tempC <= ordered[i].TempC)
                {
                    var p0 = ordered[i - 1];
                    var p1 = ordered[i];
                    var span = Math.Max(1, p1.TempC - p0.TempC);
                    raw = p0.SpeedPercent + (int)Math.Round((tempC - p0.TempC) * (double)(p1.SpeedPercent - p0.SpeedPercent) / span);
                    break;
                }
            }
        }

        if (tempC >= 85) raw = 100;
        else if (tempC >= 80 && raw < 85) raw = 85;
        raw = Math.Clamp(raw, 0, 100);

        if (lastSpeed is { } prev && lastTempC is { } pt && raw < prev && tempC > pt - 3)
            return Math.Max(prev, raw);
        return raw;
    }

    private GpuTuningCapabilities ProbeLocked()
    {
        var probed = _backend.Probe();
        if (probed.RequiresWaiver && !probed.WaiverAccepted && _data.AcceptedWaivers.Contains(probed.DeviceId))
            probed = probed with { WaiverAccepted = true };
        _cachedCaps = probed;
        return probed;
    }

    private void EnsureFactorySnapshotLocked(GpuTuningCapabilities caps)
    {
        if (_data.FactoryDefaults is not null && string.Equals(_data.DeviceId, caps.DeviceId, StringComparison.OrdinalIgnoreCase))
            return;
        var defaults = _backend.ReadFactoryDefaults();
        _data = _data with
        {
            DeviceId = caps.DeviceId,
            FactoryDefaults = defaults,
            LastKnownGood = _data.LastKnownGood ?? defaults,
        };
        SavePersistedLocked();
    }

    private GpuManualTuningState BuildStateLocked(bool ok, string? message, GpuTuningCapabilities caps)
    {
        var current = _activeApplied ?? _backend.ReadCurrent();
        var defaults = _data.FactoryDefaults ?? _backend.ReadFactoryDefaults();
        var saved = _data.SavedProfile ?? GpuManualProfile.Auto;
        var mode = _data.PausedAfterCrash ? "auto" : _data.Mode;
        var sensors = _samplerFactory(caps.Vendor).Sample();
        var statusLine = GpuTuningPolicy.DescribeStatus(caps, mode, _data.PausedAfterCrash, _queued is not null, _queued?.Reason, _testProgress);
        return new GpuManualTuningState(
            Ok: ok,
            Message: message,
            Capabilities: caps,
            FactoryDefaults: defaults,
            Current: current,
            Saved: saved,
            Mode: mode,
            PausedAfterCrash: _data.PausedAfterCrash,
            PauseReason: _data.PauseReason,
            CanReapply: _data.PausedAfterCrash && saved.Mode == "manual",
            QueuedPending: _queued is not null,
            QueuedReason: _queued?.Reason,
            Test: _testProgress,
            StatusLine: statusLine,
            LiveTempC: sensors?.TemperatureC,
            LiveClockMhz: sensors?.ClockMhz,
            LiveFanPercent: null,
            LiveWatts: sensors?.Watts);
    }

    private PersistedData LoadPersisted()
    {
        try
        {
            if (!File.Exists(_dataPath)) return new PersistedData();
            return JsonSerializer.Deserialize<PersistedData>(File.ReadAllText(_dataPath), Json) ?? new PersistedData();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PersistedData();
        }
    }

    private void SavePersistedLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_dataPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _dataPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
            File.Move(tmp, _dataPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("GPU manual tuning: could not save state: " + ex.Message);
        }
    }

    private static bool IsWaiverSavedOnDisk(string path, string deviceId)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var data = JsonSerializer.Deserialize<PersistedData>(File.ReadAllText(path), Json);
            return data?.AcceptedWaivers.Contains(deviceId) == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static GpuStabilityProgress IdleProgress() =>
        new(false, "idle", "idle", 0, 0, 0, null, null, true, null, null, false, null, null);

    private void NotifyStateChanged(GpuManualTuningState state)
    {
        try { StateChanged?.Invoke(state); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Debug("GPU tuning event listener error: " + ex.Message); }
    }

    private void NotifyTestProgress(GpuStabilityProgress progress)
    {
        try { TestProgressChanged?.Invoke(progress); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Debug("GPU test progress listener error: " + ex.Message); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _testCts?.Cancel();
            _testCts?.Dispose();
            _testCts = null;
            StopFanLoopLocked(restoreAuto: true);
        }
    }

    private sealed class UnsupportedGpuTuningBackend : IGpuTuningBackend
    {
        public string BackendId => "none";
        public string Vendor => "unknown";
        public GpuTuningCapabilities Probe() => new()
        {
            Vendor = "unknown",
            Backend = "none",
            LockedByMaker = true,
            LockReason = GpuTuningCapabilities.LockedMessage,
        };
        public GpuManualProfile ReadCurrent() => GpuManualProfile.Auto;
        public GpuManualProfile ReadFactoryDefaults() => GpuManualProfile.Auto;
        public GpuApplyResult Apply(GpuManualProfile profile) => new(false, false, GpuTuningCapabilities.LockedMessage, "none");
        public GpuApplyResult ResetToFactory() => new(true, false, null, "none");
        public bool AcceptWaiver() => false;
    }
}
