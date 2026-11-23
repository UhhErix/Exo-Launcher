using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal sealed partial class FrameCapService
{
    private const int MaxTraceSeconds = 300;
    private const int FocusPollMs = 500;

    internal sealed record OwnCapTarget(string FilePath, string Key, string Format, string Label);

    internal sealed record CapLabStatus(
        string GameId,
        string Status,
        int CapAFps,
        int CapBFps,
        string? CurrentArm,
        int ElapsedSeconds,
        int TotalSeconds,
        string? Verdict,
        string? Winner,
        double DeltaPercent,
        double NoiseThresholdPercent,
        string? Reason,
        CapLabAbTester.ArmMetrics? ArmA,
        CapLabAbTester.ArmMetrics? ArmB);

    internal sealed record FrameCapState(
        bool Ok,
        string? Message,
        string GameId,
        double RefreshHz,
        bool Vrr,
        bool InVrrRange,
        int VrrFloorHz,
        int FgMultiplier,
        string? FgRouteLabel,
        string Vendor,
        string? Adapter,
        bool IsHybrid,
        string Backend,
        string BackendLabel,
        bool BackendAvailable,
        string? BackendNote,
        IReadOnlyList<FrameCapVendorRouter.CapBackendOption> Backends,
        string SelectedTier,
        int? ActiveCapFps,
        int? TargetOutputFps,
        IReadOnlyList<FrameCapPlanner.CapTier> Tiers,
        IReadOnlyList<int> RefreshLockedCaps,
        FrameCapPlanner.CapTier? UncappedOption,
        FrameCapPlanner.CapSuggestion Suggestion,
        CapLabStutterReport Stutter,
        CapLabStatus Ab,
        string Word,
        bool Good,
        bool Bad);

    internal static FrameCapService Shared { get; } = new();

    internal event Action<CapLabStatus>? CapLabChanged;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<CapLabTraceSecond>> _traces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CapLabStatus> _lastAb = new(StringComparer.OrdinalIgnoreCase);
    private ActiveAbRun? _activeAb;
    private string? _lastGameId;

    private sealed class ActiveAbRun
    {
        public required string GameId { get; init; }
        public required int CapAFps { get; init; }
        public required int CapBFps { get; init; }
        public required string Backend { get; init; }
        public required GameEntry Game { get; init; }
        public required string? ExePath { get; init; }
        public required int? PriorCapFps { get; init; }
        public required Action<int?> ApplyCap { get; init; }
        public required Func<bool> IsGameInFront { get; init; }
        public List<double> FramesA { get; } = [];
        public List<double> FramesB { get; } = [];
        public int ElapsedSeconds { get; set; }
        public string CurrentArm { get; set; } = "A";
        public bool Finished { get; set; }
        public Timer? FocusTimer { get; set; }
    }

    internal void RecordSecond(string? gameId, bool inFront, IReadOnlyList<double> framesMs, SystemSecond? system)
    {
        CapLabStatus? notify = null;
        Action? restoreAction = null;
        Action<int?>? switchAction = null;
        int? nextCap = null;

        lock (_gate)
        {
            if (gameId is null)
            {
                if (_activeAb is { Finished: false } active)
                    (notify, restoreAction) = FinishAbLocked(active, "cancelled", "Cancelled because the game closed.", null);
            }
            else
            {
                _lastGameId = gameId;
                if (inFront && framesMs.Count > 0)
                {
                    if (!_traces.TryGetValue(gameId, out var q))
                    {
                        q = new Queue<CapLabTraceSecond>();
                        _traces[gameId] = q;
                    }
                    var pref = SafePref(gameId);
                    var screen = SafeMainScreen();
                    var activeCap = _activeAb is { Finished: false } ab && string.Equals(ab.GameId, gameId, StringComparison.OrdinalIgnoreCase)
                        ? (ab.CurrentArm == "A" ? ab.CapAFps : ab.CapBFps)
                        : pref?.CapFps;
                    q.Enqueue(new CapLabTraceSecond(
                        [.. framesMs],
                        system,
                        activeCap,
                        pref?.Backend,
                        screen.RefreshHz,
                        screen.Vrr));
                    while (q.Count > MaxTraceSeconds) q.Dequeue();
                }

                if (_activeAb is { Finished: false } run)
                {
                    if (!string.Equals(run.GameId, gameId, StringComparison.OrdinalIgnoreCase) || !inFront)
                    {
                        var reason = !inFront ? "Cancelled because the game lost focus." : "Cancelled because another game took focus.";
                        (notify, restoreAction) = FinishAbLocked(run, "cancelled", reason, null);
                    }
                    else
                    {
                        if (run.CurrentArm == "A") run.FramesA.AddRange(framesMs);
                        else run.FramesB.AddRange(framesMs);

                        run.ElapsedSeconds++;
                        if (run.ElapsedSeconds >= CapLabAbTester.TotalDurationSeconds)
                        {
                            var verdict = CapLabAbTester.Evaluate(run.CapAFps, run.CapBFps, run.FramesA, run.FramesB);
                            (notify, restoreAction) = FinishAbLocked(run, "completed", null, verdict);
                        }
                        else
                        {
                            var arm = CapLabAbTester.ArmForSecond(run.ElapsedSeconds);
                            if (arm != run.CurrentArm)
                            {
                                run.CurrentArm = arm;
                                switchAction = run.ApplyCap;
                                nextCap = arm == "A" ? run.CapAFps : run.CapBFps;
                            }
                            notify = SnapshotRunning(run);
                        }
                    }
                }
            }
        }

        if (switchAction is not null)
        {
            try { switchAction(nextCap); }
            catch (Exception ex) { AppLog.Warn("CapLab A/B switch failed: " + ex.Message); }
        }
        if (restoreAction is not null)
        {
            try { restoreAction(); }
            catch (Exception ex) { AppLog.Warn("CapLab A/B restore failed: " + ex.Message); }
        }
        if (notify is not null) EmitCapLab(notify);
    }

    internal async Task<FrameCapState> ResolveAsync(
        GameEntry game,
        DlssSwapService dlss,
        SettingsService settings,
        CancellationToken ct)
    {
        var fg = await FrameGenService.ResolveAsync(game, dlss, settings, ct).ConfigureAwait(false);
        return await Task.Run(() => BuildState(game, fg, settings, null, true), ct).ConfigureAwait(false);
    }

    internal async Task<FrameCapState> ApplyAsync(
        GameEntry game,
        string? tier,
        int? customCapFps,
        string? requestedBackend,
        DlssSwapService dlss,
        SettingsService settings,
        CancellationToken ct)
    {
        var fg = await FrameGenService.ResolveAsync(game, dlss, settings, ct).ConfigureAwait(false);
        return await Task.Run(() => ApplyLocked(game, fg, tier, customCapFps, requestedBackend, settings), ct).ConfigureAwait(false);
    }

    internal async Task<(bool Ok, string? Message, CapLabStatus Status)> RunAbTestAsync(
        GameEntry game,
        int? requestedA,
        int? requestedB,
        DlssSwapService dlss,
        SettingsService settings,
        CancellationToken ct,
        Action<int?>? testApplyCap = null,
        Func<bool>? testIsInFront = null)
    {
        var state = await ResolveAsync(game, dlss, settings, ct).ConfigureAwait(false);
        var capA = requestedA is > 0 ? requestedA.Value : (state.ActiveCapFps ?? state.Tiers.FirstOrDefault(t => t.Id == FrameCapPlanner.TierMax)?.CapFps ?? 120);
        var capB = requestedB is > 0 ? requestedB.Value : PickCompanionCap(state, capA);
        if (capA <= 0 || capB <= 0 || capA == capB)
            return (false, "Pick two different FPS caps to compare.", IdleAb(game.Id));

        if (!state.BackendAvailable && testApplyCap is null)
            return (false, state.BackendNote ?? "No live frame cap backend is available on this PC.", IdleAb(game.Id));

        var inFrontCheck = testIsInFront ?? (() => IsGameForeground(game.Id));
        if (!inFrontCheck())
            return (false, "Start the game and focus its window to run the 20 s A/B cap test.", IdleAb(game.Id));

        var exe = ResolveExe(game);
        var backend = state.Backend;
        var priorCap = settings.FrameCapFor(game.Id)?.CapFps;
        var applyCap = testApplyCap ?? (fps => ApplyLiveCapForAb(game, exe, backend, fps));

        CapLabStatus started;
        lock (_gate)
        {
            if (_activeAb is { Finished: false } existing)
                FinishAbLocked(existing, "cancelled", "Replaced by a new A/B test.", null);

            var run = new ActiveAbRun
            {
                GameId = game.Id,
                CapAFps = capA,
                CapBFps = capB,
                Backend = backend,
                Game = game,
                ExePath = exe,
                PriorCapFps = priorCap,
                ApplyCap = applyCap,
                IsGameInFront = inFrontCheck,
            };
            if (testIsInFront is null)
                run.FocusTimer = new Timer(_ => CheckActiveFocus(), null, FocusPollMs, FocusPollMs);
            _activeAb = run;
            started = SnapshotRunning(run);
            _lastAb[game.Id] = started;
        }

        try { applyCap(capA); }
        catch (Exception ex) { AppLog.Warn("CapLab A/B start apply failed: " + ex.Message); }

        EmitCapLab(started);
        return (true, null, started);
    }

    internal (bool Ok, string? Message, CapLabStatus Status) StartAbRunForTest(
        GameEntry game,
        int capA,
        int capB,
        int? priorCap,
        Action<int?> applyCap,
        Func<bool> isInFront)
    {
        if (capA <= 0 || capB <= 0 || capA == capB)
            return (false, "Pick two different FPS caps to compare.", IdleAb(game.Id));
        if (!isInFront())
            return (false, "Start the game and focus its window to run the 20 s A/B cap test.", IdleAb(game.Id));

        CapLabStatus started;
        lock (_gate)
        {
            if (_activeAb is { Finished: false } existing)
                FinishAbLocked(existing, "cancelled", "Replaced by a new A/B test.", null);

            var run = new ActiveAbRun
            {
                GameId = game.Id,
                CapAFps = capA,
                CapBFps = capB,
                Backend = FrameCapVendorRouter.BackendNvidiaDrs,
                Game = game,
                ExePath = null,
                PriorCapFps = priorCap,
                ApplyCap = applyCap,
                IsGameInFront = isInFront,
            };
            _activeAb = run;
            started = SnapshotRunning(run);
            _lastAb[game.Id] = started;
        }
        applyCap(capA);
        EmitCapLab(started);
        return (true, null, started);
    }

    internal (bool Ok, string? Message, CapLabStatus Status) CancelAbTest(string? gameId = null, string reason = "Cancelled.")
    {
        CapLabStatus? status = null;
        Action? restore = null;
        lock (_gate)
        {
            if (_activeAb is { Finished: false } run
                && (string.IsNullOrWhiteSpace(gameId) || string.Equals(run.GameId, gameId, StringComparison.OrdinalIgnoreCase)))
            {
                (status, restore) = FinishAbLocked(run, "cancelled", reason, null);
            }
        }
        if (restore is not null)
        {
            try { restore(); }
            catch (Exception ex) { AppLog.Warn("CapLab A/B cancel restore failed: " + ex.Message); }
        }
        if (status is not null)
        {
            EmitCapLab(status);
            return (true, null, status);
        }
        return (true, null, IdleAb(gameId ?? ""));
    }

    internal string? SuggestSentenceFor(string? gameId)
    {
        var targetId = !string.IsNullOrWhiteSpace(gameId) ? gameId : _lastGameId;
        var trace = SnapshotTrace(targetId);
        var frames = FlattenFrames(trace);
        var screen = SafeMainScreen();
        if (screen.RefreshHz <= 0) return null;
        var plan = FrameCapPlanner.Plan(screen.RefreshHz, screen.Vrr, FrameCapPlanner.DefaultVrrFloorHz, 1, true, LowLatencyNameFor(GraphicsService.Vendor()), frames);
        var stutter = CapLabStutterAttribution.Analyze(trace);
        var suggestion = FrameCapPlanner.SuggestTier(plan, stutter, false);
        return suggestion.CapFps is { } cap
            ? $"Frame cap: {suggestion.Tier} ({cap} fps) — {suggestion.Reason}"
            : $"Frame cap: {suggestion.Reason}";
    }

    internal void SeedTraceForTest(string gameId, IReadOnlyList<CapLabTraceSecond> seconds)
    {
        lock (_gate)
        {
            var q = new Queue<CapLabTraceSecond>();
            foreach (var sec in seconds) q.Enqueue(sec);
            _traces[gameId] = q;
        }
    }

    internal void ResetForTest()
    {
        lock (_gate)
        {
            if (_activeAb is { } run)
            {
                run.FocusTimer?.Dispose();
                run.FocusTimer = null;
                run.Finished = true;
            }
            _activeAb = null;
            _traces.Clear();
            _lastAb.Clear();
            _lastGameId = null;
        }
    }

    private FrameCapState ApplyLocked(
        GameEntry game,
        FrameGenRouter.FrameGenDecision fg,
        string? tier,
        int? customCapFps,
        string? requestedBackend,
        SettingsService settings)
    {
        var current = BuildState(game, fg, settings, null, true);
        var existingPref = settings.FrameCapFor(game.Id);
        var normalizedTier = string.IsNullOrWhiteSpace(tier)
            ? (customCapFps is > 0 ? FrameCapPlanner.TierCustom : FrameCapPlanner.TierOff)
            : tier.Trim().ToLowerInvariant();

        if (normalizedTier is FrameCapPlanner.TierOff or FrameCapPlanner.TierUncapped)
        {
            var removeErr = RemoveExistingCap(game, existingPref);
            if (normalizedTier == FrameCapPlanner.TierUncapped)
            {
                settings.SetFrameCap(game.Id, new GameFrameCapPref
                {
                    Tier = FrameCapPlanner.TierUncapped,
                    CapFps = null,
                    Backend = null,
                });
            }
            else
            {
                settings.SetFrameCap(game.Id, null);
            }
            return BuildState(game, fg, settings, removeErr, removeErr is null);
        }

        int? targetFps = normalizedTier switch
        {
            FrameCapPlanner.TierSmooth => current.Tiers.FirstOrDefault(t => t.Id == FrameCapPlanner.TierSmooth)?.CapFps,
            FrameCapPlanner.TierBalanced => current.Tiers.FirstOrDefault(t => t.Id == FrameCapPlanner.TierBalanced)?.CapFps,
            FrameCapPlanner.TierMax => current.Tiers.FirstOrDefault(t => t.Id == FrameCapPlanner.TierMax)?.CapFps,
            FrameCapPlanner.TierRefresh => customCapFps is > 0 ? customCapFps : current.RefreshLockedCaps.FirstOrDefault(),
            FrameCapPlanner.TierCustom => customCapFps,
            _ => customCapFps ?? current.Tiers.FirstOrDefault(t => t.Id == normalizedTier)?.CapFps,
        };

        if (targetFps is not > 0)
            return BuildState(game, fg, settings, "Pick a valid FPS cap.", false);

        var clampedFps = Math.Clamp(targetFps.Value, FrameCapPlanner.MinCapFps, 1000);
        var chosenBackend = !string.IsNullOrWhiteSpace(requestedBackend)
            ? current.Backends.FirstOrDefault(b => string.Equals(b.Id, requestedBackend, StringComparison.OrdinalIgnoreCase) && b.Available)
            : current.Backends.FirstOrDefault(b => b.Id == current.Backend && b.Available)
                ?? current.Backends.FirstOrDefault(b => b.Available);

        if (chosenBackend is null)
            return BuildState(game, fg, settings, current.BackendNote ?? "No frame cap backend is available on this PC.", false);

        if (existingPref is not null && !string.Equals(existingPref.Backend, chosenBackend.Id, StringComparison.OrdinalIgnoreCase))
            RemoveExistingCap(game, existingPref);

        var (ok, err, priorValue) = ApplyCapWithBackend(game, chosenBackend.Id, clampedFps, existingPref);
        if (!ok)
            return BuildState(game, fg, settings, err ?? "Could not apply frame cap.", false);

        settings.SetFrameCap(game.Id, new GameFrameCapPref
        {
            Tier = normalizedTier,
            CapFps = clampedFps,
            Backend = chosenBackend.Id,
            PriorValue = existingPref?.Backend == chosenBackend.Id ? existingPref.PriorValue ?? priorValue : priorValue,
            PriorBackend = chosenBackend.Id,
        });

        return BuildState(game, fg, settings, null, true);
    }

    private FrameCapState BuildState(
        GameEntry game,
        FrameGenRouter.FrameGenDecision fg,
        SettingsService settings,
        string? message,
        bool ok)
    {
        var adapters = GpuCapability.Adapters();
        var vendor = GraphicsService.Vendor() ?? GraphicsService.Vendors(adapters).FirstOrDefault() ?? "unknown";
        var screen = SafeMainScreen(vendor);
        var refreshHz = screen.RefreshHz > 0 ? screen.RefreshHz : 60;
        var vrr = screen.Vrr;
        if (vendor == "amd" && !vrr && RadeonProfileService.Shared.Read(screen) is { FreeSync: true })
            vrr = true;

        var fgMult = FrameGenRouter.MultiplierFor(fg);
        var fgSelected = fg.Routes.FirstOrDefault(r => string.Equals(r.Id, fg.SelectedRouteId, StringComparison.OrdinalIgnoreCase));
        var fgLabel = fgMult > 1 ? fgSelected?.Label ?? $"{fgMult}x frame generation" : null;

        var trace = SnapshotTrace(game.Id);
        var frames = FlattenFrames(trace);
        var lowLatencyLabel = LowLatencyNameFor(vendor);
        var plan = FrameCapPlanner.Plan(
            refreshHz,
            vrr,
            FrameCapPlanner.DefaultVrrFloorHz,
            fgMult,
            supportsLowLatency: vendor is "nvidia" or "amd" or "intel",
            lowLatencyLabel: lowLatencyLabel,
            sessionFrameTimesMs: frames);

        var stutter = CapLabStutterAttribution.Analyze(trace);
        var fluid = FrameGenFacts.ForLaunch(game);
        var isCompetitive = FluidMotionFrames.IsCompetitive(fluid);
        var suggestion = FrameCapPlanner.SuggestTier(plan, stutter, isCompetitive);

        var ac = AntiCheatCheck.Cached(game);
        var isRiot = AntiCheatCheck.IsRiot(game);
        var ownTarget = FindOwnInterfaceCap(game);
        var allowsOwn = !isRiot && AntiCheatCheck.Allows(game, ac, AntiCheatCheck.Change.GameSettingsFile);
        var amdChillSupported = vendor == "amd" && RadeonProfileService.Shared.ChillCapSupported();
        var intelCap = vendor == "intel" ? IntelProfileService.Shared.FrameLimitSupport() : (Supported: false, PerApp: false);

        var routing = FrameCapVendorRouter.Route(new FrameCapVendorRouter.VendorCapInput(
            Adapters: adapters,
            NvidiaAvailable: NvidiaProfiles.GameProfilesUsable,
            AmdAdlxAvailable: AmdAdlx.Present,
            AmdChillSupported: amdChillSupported,
            IntelIgclAvailable: IntelIgcl.Present,
            IntelFrameLimitSupported: intelCap.Supported,
            IntelPerAppSupported: intelCap.PerApp,
            HasOwnInterfaceCap: ownTarget is not null,
            OwnInterfaceSettingName: ownTarget?.Label,
            AllowsOwnInterface: allowsOwn,
            IsRiot: isRiot,
            AntiCheat: ac));

        var pref = settings.FrameCapFor(game.Id);
        var selectedTier = pref?.Tier ?? FrameCapPlanner.TierOff;
        var activeCap = pref?.CapFps;
        var activeBackendId = pref?.Backend ?? routing.RecommendedBackend;
        var backendOption = routing.Backends.FirstOrDefault(b => string.Equals(b.Id, activeBackendId, StringComparison.OrdinalIgnoreCase))
            ?? routing.Backends.FirstOrDefault(b => b.Id == routing.RecommendedBackend);

        var targetOutput = activeCap is > 0 ? activeCap.Value * fgMult : (int?)null;
        var ab = GetAbStatus(game.Id);
        var (word, good, bad) = SummarizeWord(selectedTier, activeCap, targetOutput, fgMult, plan, routing, ab, message, ok);

        return new FrameCapState(
            Ok: ok,
            Message: message,
            GameId: game.Id,
            RefreshHz: plan.RefreshHz,
            Vrr: plan.VrrEnabled,
            InVrrRange: plan.InVrrRange,
            VrrFloorHz: plan.VrrFloorHz,
            FgMultiplier: plan.FgMultiplier,
            FgRouteLabel: fgLabel,
            Vendor: routing.ActiveVendor,
            Adapter: routing.ActiveAdapter,
            IsHybrid: routing.IsHybridLaptop,
            Backend: backendOption?.Id ?? routing.RecommendedBackend,
            BackendLabel: backendOption?.Label ?? "Not available",
            BackendAvailable: backendOption?.Available ?? routing.CanApply,
            BackendNote: backendOption?.Reason ?? routing.Summary,
            Backends: routing.Backends,
            SelectedTier: selectedTier,
            ActiveCapFps: activeCap,
            TargetOutputFps: targetOutput,
            Tiers: plan.Tiers,
            RefreshLockedCaps: plan.RefreshLockedCaps,
            UncappedOption: plan.UncappedOption,
            Suggestion: suggestion,
            Stutter: stutter,
            Ab: ab,
            Word: word,
            Good: good,
            Bad: bad);
    }

    private static (bool Ok, string? Error, string? PriorValue) ApplyCapWithBackend(
        GameEntry game,
        string backendId,
        int fps,
        GameFrameCapPref? existingPref)
    {
        if (backendId == FrameCapVendorRouter.BackendOwnInterface)
        {
            if (AntiCheatCheck.IsRiot(game) || !AntiCheatCheck.Allows(game, AntiCheatCheck.Cached(game), AntiCheatCheck.Change.GameSettingsFile))
                return (false, "In-game config write is blocked for this game's anti-cheat.", null);
            var target = FindOwnInterfaceCap(game);
            if (target is null)
                return (false, "No in-game frame cap setting file was found for this game yet.", null);
            var wrote = WriteOwnInterfaceCap(target, fps, out var prior, out var err);
            return (wrote, err, prior);
        }

        if (backendId == FrameCapVendorRouter.BackendNvidiaDrs)
        {
            var exe = ResolveExe(game);
            if (string.IsNullOrWhiteSpace(exe))
                return (false, "Could not locate the game executable for NVIDIA DRS.", null);
            var res = GpuProfileService.SetGameFrameCap(game, exe, (uint)fps);
            var prior = res.Before is { } b ? b.ToString(CultureInfo.InvariantCulture) : "";
            return (res.Ok, res.Message, prior);
        }

        if (backendId == FrameCapVendorRouter.BackendIntelIgcl)
        {
            var exe = ResolveExe(game);
            var err = IntelProfileService.Shared.SetFrameLimitCap(game.Id, exe, fps);
            return (err is null, err, existingPref?.PriorValue ?? "0");
        }

        if (backendId == FrameCapVendorRouter.BackendAmdChill)
        {
            if (!RadeonProfileService.Shared.ChillCapSupported())
                return (false, "AMD Radeon Chill is not available on this driver; set a cap in AMD Software instead.", null);
            if (!RadeonProfileService.Shared.SetChillCap(game.Id, fps))
                return (false, "Could not save AMD Radeon Chill cap preference.", null);
            if (IsGameForeground(game.Id))
            {
                var liveErr = RadeonProfileService.Shared.ApplyChillCapNow(fps);
                if (liveErr is not null) return (false, liveErr, null);
            }
            return (true, null, existingPref?.PriorValue ?? "0");
        }

        return (false, "Unknown frame cap backend.", null);
    }

    private static string? RemoveExistingCap(GameEntry game, GameFrameCapPref? pref)
    {
        if (pref is null || string.IsNullOrWhiteSpace(pref.Backend)) return null;
        if (pref.Backend == FrameCapVendorRouter.BackendNvidiaDrs)
        {
            var exe = ResolveExe(game);
            if (string.IsNullOrWhiteSpace(exe)) return null;
            uint? restoreVal = uint.TryParse(pref.PriorValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : null;
            var res = GpuProfileService.SetGameFrameCap(game, exe, restoreVal);
            return res.Ok ? null : res.Message;
        }
        if (pref.Backend == FrameCapVendorRouter.BackendIntelIgcl)
        {
            var exe = ResolveExe(game);
            return IntelProfileService.Shared.SetFrameLimitCap(game.Id, exe, null);
        }
        if (pref.Backend == FrameCapVendorRouter.BackendAmdChill)
        {
            RadeonProfileService.Shared.SetChillCap(game.Id, null);
            if (IsGameForeground(game.Id))
                return RadeonProfileService.Shared.ApplyChillCapNow(null);
            return null;
        }
        if (pref.Backend == FrameCapVendorRouter.BackendOwnInterface)
        {
            var target = FindOwnInterfaceCap(game);
            if (target is null) return null;
            RestoreOwnInterfaceCap(target, pref.PriorValue, out var err);
            return err;
        }
        return null;
    }

    private static void ApplyLiveCapForAb(GameEntry game, string? exe, string backend, int? fps)
    {
        if (backend == FrameCapVendorRouter.BackendNvidiaDrs && !string.IsNullOrWhiteSpace(exe))
        {
            GpuProfileService.SetGameFrameCap(game, exe, fps is > 0 ? (uint)fps.Value : null);
            return;
        }
        if (backend == FrameCapVendorRouter.BackendAmdChill)
        {
            RadeonProfileService.Shared.ApplyChillCapNow(fps);
            return;
        }
        if (backend == FrameCapVendorRouter.BackendIntelIgcl)
        {
            IntelProfileService.Shared.ApplyFrameLimitNow(exe, fps ?? 0);
        }
    }

    internal static OwnCapTarget? FindOwnInterfaceCap(GameEntry game)
    {
        if (!game.Installed || string.IsNullOrWhiteSpace(game.Path) || AntiCheatCheck.IsRiot(game)) return null;
        try
        {
            if (GameSettings.IsRocketLeague(game.Path))
            {
                var ini = GameSettings.RocketLeague(new GameSettings.Pc(null, null, null)).File;
                if (File.Exists(ini)) return new OwnCapTarget(ini, "CustomFPS", "ini", "Rocket League · CustomFPS");
            }
            if (GameSettings.IsControlResonant(game.Path))
            {
                var controlFile = GameSettings.ControlResonantConfig;
                if (File.Exists(controlFile)) return new OwnCapTarget(controlFile, "m_maxFrameRate", "json", "CONTROL Resonant · m_maxFrameRate");
            }

            if (GameHdr.UnrealProjects(game.Path) is [var project, ..] && GameHdr.UnrealSettingsFile(project, game) is { } ueFile && File.Exists(ueFile))
            {
                var text = File.ReadAllText(ueFile, Encoding.Latin1);
                if (text.Contains("FrameRateLimit=", StringComparison.Ordinal))
                    return new OwnCapTarget(ueFile, "FrameRateLimit", "ini_float", "GameUserSettings.ini · FrameRateLimit");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    internal static bool WriteOwnInterfaceCap(OwnCapTarget target, int fps, out string? priorValue, out string? error)
    {
        priorValue = null;
        error = null;
        try
        {
            if (!File.Exists(target.FilePath))
            {
                error = "The game's settings file does not exist yet; launch the game once first.";
                return false;
            }
            var raw = File.ReadAllText(target.FilePath, Encoding.UTF8);
            var formatted = target.Format == "ini_float"
                ? string.Create(CultureInfo.InvariantCulture, $"{fps:0.000000}")
                : fps.ToString(CultureInfo.InvariantCulture);
            if (!ReplaceSettingValue(raw, target.Key, target.Format == "json", formatted, out var oldVal, out var updated))
            {
                error = $"Could not find {target.Key} in {Path.GetFileName(target.FilePath)}.";
                return false;
            }
            priorValue = oldVal;
            File.WriteAllText(target.FilePath, updated, Encoding.UTF8);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Could not update {Path.GetFileName(target.FilePath)}: {ex.Message}";
            return false;
        }
    }

    internal static bool RestoreOwnInterfaceCap(OwnCapTarget target, string? priorValue, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(target.FilePath)) return true;
            var restoreTo = !string.IsNullOrWhiteSpace(priorValue)
                ? priorValue
                : target.Format == "ini_float" ? "0.000000" : "0";
            var raw = File.ReadAllText(target.FilePath, Encoding.UTF8);
            if (!ReplaceSettingValue(raw, target.Key, target.Format == "json", restoreTo, out _, out var updated))
                return true;
            File.WriteAllText(target.FilePath, updated, Encoding.UTF8);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Could not restore {Path.GetFileName(target.FilePath)}: {ex.Message}";
            return false;
        }
    }

    private static bool ReplaceSettingValue(string content, string key, bool isJson, string newValue, out string? oldValue, out string updated)
    {
        oldValue = null;
        updated = content;
        var pattern = isJson
            ? $"(\"{Regex.Escape(key)}\"\\s*:\\s*)(-?\\d+(?:\\.\\d+)?)"
            : $"^(\\s*{Regex.Escape(key)}\\s*=\\s*)([^\\r\\n]+)";
        var rx = new Regex(pattern, RegexOptions.Multiline);
        var match = rx.Match(content);
        if (!match.Success) return false;
        oldValue = match.Groups[2].Value.Trim();
        updated = rx.Replace(content, m => m.Groups[1].Value + newValue, 1);
        return true;
    }

    private void CheckActiveFocus()
    {
        CapLabStatus? notify = null;
        Action? restore = null;
        lock (_gate)
        {
            if (_activeAb is not { Finished: false } run) return;
            if (!run.IsGameInFront())
                (notify, restore) = FinishAbLocked(run, "cancelled", "Cancelled because the game lost focus.", null);
        }
        if (restore is not null)
        {
            try { restore(); }
            catch (Exception ex) { AppLog.Warn("CapLab A/B focus-loss restore failed: " + ex.Message); }
        }
        if (notify is not null) EmitCapLab(notify);
    }

    private (CapLabStatus Status, Action Restore) FinishAbLocked(ActiveAbRun run, string status, string? reason, CapLabAbTester.AbVerdict? verdict)
    {
        run.Finished = true;
        run.FocusTimer?.Dispose();
        run.FocusTimer = null;
        if (ReferenceEquals(_activeAb, run)) _activeAb = null;
        var result = new CapLabStatus(
            GameId: run.GameId,
            Status: status,
            CapAFps: run.CapAFps,
            CapBFps: run.CapBFps,
            CurrentArm: null,
            ElapsedSeconds: run.ElapsedSeconds,
            TotalSeconds: CapLabAbTester.TotalDurationSeconds,
            Verdict: verdict?.Verdict,
            Winner: verdict?.Winner,
            DeltaPercent: verdict?.DeltaPercent ?? 0,
            NoiseThresholdPercent: verdict?.NoiseThresholdPercent ?? CapLabAbTester.DefaultNoiseThresholdPercent,
            Reason: reason,
            ArmA: verdict?.ArmA,
            ArmB: verdict?.ArmB);
        _lastAb[run.GameId] = result;
        Action restore = () => run.ApplyCap(run.PriorCapFps);
        return (result, restore);
    }

    private static CapLabStatus SnapshotRunning(ActiveAbRun run) => new(
        GameId: run.GameId,
        Status: "running",
        CapAFps: run.CapAFps,
        CapBFps: run.CapBFps,
        CurrentArm: run.CurrentArm,
        ElapsedSeconds: run.ElapsedSeconds,
        TotalSeconds: CapLabAbTester.TotalDurationSeconds,
        Verdict: null,
        Winner: null,
        DeltaPercent: 0,
        NoiseThresholdPercent: CapLabAbTester.DefaultNoiseThresholdPercent,
        Reason: null,
        ArmA: null,
        ArmB: null);

    private CapLabStatus GetAbStatus(string gameId)
    {
        lock (_gate)
        {
            if (_activeAb is { Finished: false } run && string.Equals(run.GameId, gameId, StringComparison.OrdinalIgnoreCase))
                return SnapshotRunning(run);
            if (_lastAb.TryGetValue(gameId, out var saved))
                return saved;
            return IdleAb(gameId);
        }
    }

    private static CapLabStatus IdleAb(string gameId) => new(
        GameId: gameId,
        Status: "idle",
        CapAFps: 0,
        CapBFps: 0,
        CurrentArm: null,
        ElapsedSeconds: 0,
        TotalSeconds: CapLabAbTester.TotalDurationSeconds,
        Verdict: null,
        Winner: null,
        DeltaPercent: 0,
        NoiseThresholdPercent: CapLabAbTester.DefaultNoiseThresholdPercent,
        Reason: null,
        ArmA: null,
        ArmB: null);

    private void EmitCapLab(CapLabStatus status)
    {
        if (CapLabChanged is not { } handlers) return;
        foreach (var h in handlers.GetInvocationList())
        {
            try { ((Action<CapLabStatus>)h)(status); }
            catch (Exception ex) { AppLog.Warn("CapLab listener failed: " + ex.Message); }
        }
    }

    private IReadOnlyList<CapLabTraceSecond> SnapshotTrace(string? gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return [];
        lock (_gate)
            return _traces.TryGetValue(gameId, out var q) ? [.. q] : [];
    }

    private static List<double> FlattenFrames(IReadOnlyList<CapLabTraceSecond> trace)
    {
        var list = new List<double>();
        for (var i = 0; i < trace.Count; i++)
            list.AddRange(trace[i].FrameTimesMs);
        return list;
    }

    private static int PickCompanionCap(FrameCapState state, int capA)
    {
        var balanced = state.Tiers.FirstOrDefault(t => t.Id == FrameCapPlanner.TierBalanced)?.CapFps;
        var smooth = state.Tiers.FirstOrDefault(t => t.Id == FrameCapPlanner.TierSmooth)?.CapFps;
        var max = state.Tiers.FirstOrDefault(t => t.Id == FrameCapPlanner.TierMax)?.CapFps;
        if (balanced is > 0 && balanced.Value != capA) return balanced.Value;
        if (smooth is > 0 && smooth.Value != capA) return smooth.Value;
        if (max is > 0 && max.Value != capA) return max.Value;
        return Math.Max(FrameCapPlanner.MinCapFps, capA - 12);
    }

    private static bool IsGameForeground(string gameId)
    {
        var live = AutopilotWatch.Shared.Live;
        return live is not null && string.Equals(live.Value.Game, gameId, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveExe(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.Path) || !Directory.Exists(game.Path)) return null;
        if (GameHdr.MainProgram(game.Path) is { } main && File.Exists(main)) return main;
        try
        {
            return Directory.EnumerateFiles(game.Path, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => !Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? LowLatencyNameFor(string? vendor) => vendor switch
    {
        "nvidia" => "NVIDIA Reflex",
        "amd" => "AMD Anti-Lag",
        "intel" => "Intel XeLL",
        _ => null,
    };

    private static GpuProfile.Screen SafeMainScreen(string? vendor = null)
    {
        try { return GpuProfileService.MainScreen(vendor ?? GraphicsService.Vendor()); }
        catch { return new GpuProfile.Screen(null, 60, false); }
    }

    private static GameFrameCapPref? SafePref(string gameId)
    {
        try { return App.Services?.Settings.FrameCapFor(gameId); }
        catch { return null; }
    }

    private static (string Word, bool Good, bool Bad) SummarizeWord(
        string selectedTier,
        int? activeCap,
        int? targetOutput,
        int fgMult,
        FrameCapPlanner.FrameCapPlan plan,
        FrameCapVendorRouter.VendorCapRouting routing,
        CapLabStatus ab,
        string? message,
        bool ok)
    {
        if (!ok && !string.IsNullOrWhiteSpace(message))
            return (message, false, true);
        if (ab.Status == "running")
            return ($"A/B testing {ab.CapAFps} vs {ab.CapBFps} fps ({ab.ElapsedSeconds}/{ab.TotalSeconds}s · Arm {ab.CurrentArm})", false, false);
        if (selectedTier == FrameCapPlanner.TierUncapped)
        {
            var ll = plan.LowLatencyLabel ?? "low latency";
            return ($"Uncapped · {ll}", true, false);
        }
        if (activeCap is > 0)
        {
            var capWord = fgMult > 1 && targetOutput is > 0
                ? $"{activeCap} base ({targetOutput} out) · {routing.Summary}"
                : $"{activeCap} fps · {routing.Summary}";
            return (capWord, true, false);
        }
        if (!routing.CanApply)
            return (routing.Summary, false, false);
        var suggested = plan.Tiers.FirstOrDefault(t => t.Id == FrameCapPlanner.TierMax);
        var hint = suggested?.CapFps is { } s
            ? (plan.InVrrRange ? $"Off · {s} fps VRR cap available" : $"Off · {s} fps refresh cap available")
            : "Off";
        return (hint, false, false);
    }
}
