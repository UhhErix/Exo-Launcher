using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private async Task<object> GameFrameCapAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, message = "Game not found." };

        var tier = ReadString(p, hasParams, "tier");
        var backend = ReadString(p, hasParams, "backend");
        int? capFps = hasParams && p.TryGetProperty("capFps", out var fpsEl) && fpsEl.ValueKind == JsonValueKind.Number && fpsEl.TryGetInt32(out var fpsNum)
            ? fpsNum
            : null;

        if (tier is not null || capFps is not null || backend is not null)
        {
            var applied = await FrameCapService.Shared.ApplyAsync(game, tier, capFps, backend, _dlss, _services.Settings, ct).ConfigureAwait(false);
            return FormatFrameCapState(applied);
        }

        var state = await FrameCapService.Shared.ResolveAsync(game, _dlss, _services.Settings, ct).ConfigureAwait(false);
        return FormatFrameCapState(state);
    }

    private async Task<object> GameCapLabRunAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, message = "Game not found." };

        int? capA = hasParams && p.TryGetProperty("capAFps", out var aEl) && aEl.ValueKind == JsonValueKind.Number && aEl.TryGetInt32(out var aNum) ? aNum : null;
        int? capB = hasParams && p.TryGetProperty("capBFps", out var bEl) && bEl.ValueKind == JsonValueKind.Number && bEl.TryGetInt32(out var bNum) ? bNum : null;

        var (ok, message, status) = await FrameCapService.Shared.RunAbTestAsync(game, capA, capB, _dlss, _services.Settings, ct).ConfigureAwait(false);
        return new
        {
            ok,
            message,
            ab = FormatCapLabStatus(status),
        };
    }

    private object GameCapLabCancel(JsonElement p, bool hasParams)
    {
        var gameId = ReadString(p, hasParams, "id");
        var (ok, message, status) = FrameCapService.Shared.CancelAbTest(gameId, "Cancelled by user.");
        return new
        {
            ok,
            message,
            ab = FormatCapLabStatus(status),
        };
    }

    private void OnCapLabChanged(FrameCapService.CapLabStatus status) =>
        PostEvent("game.capLab", new
        {
            gameId = status.GameId,
            status = status.Status,
            capAFps = status.CapAFps,
            capBFps = status.CapBFps,
            currentArm = status.CurrentArm,
            elapsedSeconds = status.ElapsedSeconds,
            totalSeconds = status.TotalSeconds,
            verdict = status.Verdict,
            winner = status.Winner,
            deltaPercent = status.DeltaPercent,
            noiseThresholdPercent = status.NoiseThresholdPercent,
            reason = status.Reason,
            armA = FormatArmMetrics(status.ArmA),
            armB = FormatArmMetrics(status.ArmB),
        });

    private static object FormatFrameCapState(FrameCapService.FrameCapState s) => new
    {
        ok = s.Ok,
        message = s.Message,
        gameId = s.GameId,
        refreshHz = s.RefreshHz,
        vrr = s.Vrr,
        inVrrRange = s.InVrrRange,
        vrrFloorHz = s.VrrFloorHz,
        fgMultiplier = s.FgMultiplier,
        fgRouteLabel = s.FgRouteLabel,
        vendor = s.Vendor,
        adapter = s.Adapter,
        isHybrid = s.IsHybrid,
        backend = s.Backend,
        backendLabel = s.BackendLabel,
        backendAvailable = s.BackendAvailable,
        backendNote = s.BackendNote,
        backends = s.Backends.Select(b => new
        {
            id = b.Id,
            vendor = b.Vendor,
            label = b.Label,
            available = b.Available,
            perGameNative = b.PerGameNative,
            latencyNote = b.LatencyNote,
            antiCheatNote = b.AntiCheatNote,
            reason = b.Reason,
        }).ToList(),
        selectedTier = s.SelectedTier,
        activeCapFps = s.ActiveCapFps,
        targetOutputFps = s.TargetOutputFps,
        tiers = s.Tiers.Select(FormatTier).ToList(),
        refreshLockedCaps = s.RefreshLockedCaps,
        uncappedOption = s.UncappedOption is { } u ? FormatTier(u) : null,
        suggestion = new
        {
            tier = s.Suggestion.Tier,
            capFps = s.Suggestion.CapFps,
            reason = s.Suggestion.Reason,
        },
        stutter = new
        {
            totalSpikes = s.Stutter.TotalSpikes,
            topCause = s.Stutter.TopCause,
            topCauseLabel = s.Stutter.TopCauseLabel,
            summary = s.Stutter.Summary,
            perProcessVramAvailable = s.Stutter.PerProcessVramAvailable,
            breakdown = s.Stutter.Breakdown.Select(b => new
            {
                cause = b.Cause,
                label = b.Label,
                spikes = b.Spikes,
                sharePercent = b.SharePercent,
                detail = b.Detail,
                hint = b.Hint,
            }).ToList(),
        },
        ab = FormatCapLabStatus(s.Ab),
        word = s.Word,
        good = s.Good,
        bad = s.Bad,
    };

    private static object FormatTier(FrameCapPlanner.CapTier t) => new
    {
        id = t.Id,
        label = t.Label,
        capFps = t.CapFps,
        outputFps = t.OutputFps,
        predictedVarianceMs2 = t.PredictedVarianceMs2,
        predictedStdDevMs = t.PredictedStdDevMs,
        predictedDipsPerMinute = t.PredictedDipsPerMinute,
        measured = t.Measured,
        summary = t.Summary,
    };

    private static object FormatCapLabStatus(FrameCapService.CapLabStatus status) => new
    {
        gameId = status.GameId,
        status = status.Status,
        capAFps = status.CapAFps,
        capBFps = status.CapBFps,
        currentArm = status.CurrentArm,
        elapsedSeconds = status.ElapsedSeconds,
        totalSeconds = status.TotalSeconds,
        verdict = status.Verdict,
        winner = status.Winner,
        deltaPercent = status.DeltaPercent,
        noiseThresholdPercent = status.NoiseThresholdPercent,
        reason = status.Reason,
        armA = FormatArmMetrics(status.ArmA),
        armB = FormatArmMetrics(status.ArmB),
    };

    private static object? FormatArmMetrics(CapLabAbTester.ArmMetrics? m) => m is null ? null : new
    {
        capFps = m.CapFps,
        frames = m.Frames,
        averageFps = m.AverageFps,
        low1Fps = m.Low1Fps,
        low01Fps = m.Low01Fps,
        averageMs = m.AverageMs,
        frameTimeVarianceMs2 = m.FrameTimeVarianceMs2,
        frameTimeStdDevMs = m.FrameTimeStdDevMs,
    };
}
