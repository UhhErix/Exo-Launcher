using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal sealed record ExoAiSnapshot(
    bool AutoApplyBestSettings,
    bool BackgroundDriftCheck,
    string? LastCheckUtc,
    IReadOnlyList<string> AutoApplied,
    IReadOnlyList<string> NeedsAttention,
    ExoAiAutopilotSnapshot Gaming,
    FanStatus Fans);

/// <summary>
/// Unified real-time optimization coordinator ("Exo AI"):
/// silently keeps safe non-elevated Best Settings aligned while idle (never popping unexpected UAC prompts),
/// tracks Autopilot gameplay optimization, and drives optional anti-cheat-safe NVML GPU fan curves.
/// </summary>
internal sealed class ExoAiCoordinator : IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DriftInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FanTickInterval = TimeSpan.FromSeconds(2);

    internal static ExoAiCoordinator Shared { get; } = new(
        new ExoAiFans(),
        () => DriverKeep.AnyGameRunning(),
        () =>
        {
            var applied = new List<string>();
            try
            {
                if (!Setups.HoldsPlan() && PowerPlan.Shared.Auto(PcHealth.ThisPcProfile()) is null && PowerPlan.Shared.Read()?.Exo == true)
                    applied.Add("Exo power plan active");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Warn("ExoAI: power plan check failed: " + ex.GetType().Name); }

            var apOn = Autopilot.Settings.On;
            var gmErr = Autopilot.HandGameMode();
            if (apOn && gmErr is null && Autopilot.GameMode.Held is not null) applied.Add("Windows Game Mode managed by Autopilot");
            var maErr = Autopilot.HandMouseAccel();
            if (apOn && maErr is null && Autopilot.MouseAccel.IsOff()) applied.Add("Pointer acceleration off");
            return applied;
        },
        () =>
        {
            var attention = new List<string>();
            try
            {
                var reading = PcHealth.ReadAsync(PowerPlan.Shared, false, false, CancellationToken.None).GetAwaiter().GetResult();
                foreach (var item in PcHealth.Decide(reading))
                    if (!item.Applied && !item.Current && !item.Held && item.Key != "power" && !item.Busy)
                        attention.Add(item.Label);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Debug("ExoAI: health read skipped: " + ex.GetType().Name); }
            return attention;
        },
        () => Autopilot.Engine.Snapshot(),
        () => DateTimeOffset.UtcNow);

    private readonly Func<bool> _isGaming;
    private readonly Func<IReadOnlyList<string>> _applySafe;
    private readonly Func<IReadOnlyList<string>> _readAttention;
    private readonly Func<ExoAiAutopilotSnapshot> _autopilotSnapshot;
    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _gate = new();

    private Func<ExoAiSettings>? _getSettings;
    private ExoAiSettings? _lastSettings;
    private Timer? _driftTimer;
    private Timer? _fanTimer;
    private DateTimeOffset? _lastCheckUtc;
    private IReadOnlyList<string> _autoApplied = [];
    private IReadOnlyList<string> _needsAttention = [];
    private bool _running;

    internal ExoAiFans Fans { get; }

    internal ExoAiCoordinator(
        ExoAiFans fans,
        Func<bool> isGaming,
        Func<IReadOnlyList<string>> applySafe,
        Func<IReadOnlyList<string>> readAttention,
        Func<ExoAiAutopilotSnapshot> autopilotSnapshot,
        Func<DateTimeOffset> now)
    {
        Fans = fans;
        _isGaming = isGaming;
        _applySafe = applySafe;
        _readAttention = readAttention;
        _autopilotSnapshot = autopilotSnapshot;
        _now = now;
    }

    internal void Start(Func<AppSettings> settingsProvider)
    {
        ExoAiSettings initial;
        lock (_gate)
        {
            _getSettings = () => (settingsProvider().ExoAi ?? new()).Copy();
            initial = _getSettings();
            _lastSettings = initial;
            if (_running) return;
            _running = true;
            _driftTimer = new Timer(_ => RunDriftCheck(), null, InitialDelay, DriftInterval);
        }
        ApplyFanPreset(initial.FanPreset);
    }

    internal void OnSystemResume()
    {
        if (!_running) return;
        _ = Task.Run(() => Fans.ResetCache());
    }

    internal void NotifySettings(ExoAiSettings settings)
    {
        var copy = settings.Copy();
        lock (_gate)
        {
            _lastSettings = copy;
            if (!copy.AutoApplyBestSettings) _autoApplied = [];
        }
        ApplyFanPreset(copy.FanPreset);
        if (copy.AutoApplyBestSettings && !_isGaming())
            RunSafeCheck(applySafe: true);
    }

    internal ExoAiSnapshot RunNow()
    {
        RunSafeCheck(applySafe: true);
        return Snapshot();
    }

    internal ExoAiSnapshot Snapshot()
    {
        var current = ReadSettings();
        var gaming = _autopilotSnapshot();
        var fanStatus = Fans.ReadStatus();
        lock (_gate)
        {
            return new ExoAiSnapshot(
                current.AutoApplyBestSettings,
                current.BackgroundDriftCheck,
                _lastCheckUtc?.ToString("O"),
                [.. _autoApplied],
                [.. _needsAttention],
                gaming,
                fanStatus);
        }
    }

    internal void Shutdown()
    {
        lock (_gate)
        {
            _running = false;
            _driftTimer?.Dispose();
            _driftTimer = null;
            _fanTimer?.Dispose();
            _fanTimer = null;
        }
        Fans.Shutdown();
    }

    public void Dispose() => Shutdown();

    internal void RunDriftCheck()
    {
        if (_isGaming()) return;
        var current = ReadSettings();
        if (!current.BackgroundDriftCheck) return;
        RunSafeCheck(applySafe: current.AutoApplyBestSettings);
    }

    private ExoAiSettings ReadSettings()
    {
        Func<ExoAiSettings>? getter;
        ExoAiSettings? fallback;
        lock (_gate)
        {
            getter = _getSettings;
            fallback = _lastSettings;
        }
        return getter is not null ? getter() : (fallback ?? new ExoAiSettings()).Copy();
    }

    private void RunSafeCheck(bool applySafe)
    {
        IReadOnlyList<string> applied = [];
        if (applySafe)
        {
            try { applied = _applySafe(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Warn("ExoAI: safe optimization failed: " + ex.GetType().Name); }
        }
        IReadOnlyList<string> attention = [];
        try { attention = _readAttention(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Debug("ExoAI: attention scan failed: " + ex.GetType().Name); }

        lock (_gate)
        {
            _autoApplied = applied;
            _needsAttention = attention;
            _lastCheckUtc = _now();
        }
    }

    private void ApplyFanPreset(string preset)
    {
        var status = Fans.SetPreset(ExoAiSettings.NormalizePreset(preset));
        lock (_gate)
        {
            if (!status.Supported)
            {
                _fanTimer?.Dispose();
                _fanTimer = null;
                return;
            }
            _fanTimer ??= new Timer(_ => Fans.Tick(), null, FanTickInterval, FanTickInterval);
        }
    }
}
