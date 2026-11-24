using System.Text.Json;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private async Task<object> GameGraphicsFeaturesAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, message = "Game not found." };
        return await Task.Run(object () =>
        {
            var ctx = GameGraphicsFeatures.CreateLiveContext(game, _services.Settings);
            var snapshot = GameGraphicsFeatures.Inspect(game, ctx);
            return SerializeGraphicsFeatures(snapshot);
        }, ct).ConfigureAwait(false);
    }

    private async Task<object> GameGraphicsFeaturesSetAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var game = await FindGameAsync(ReadString(p, hasParams, "id"), ct).ConfigureAwait(false);
        if (game is null) return new { ok = false, message = "Game not found." };
        var feature = ReadString(p, hasParams, "feature");
        var on = ReadBool(p, hasParams, "on");
        var acceptRisk = ReadBool(p, hasParams, "acceptAntiCheatRisk") == true;
        if (string.IsNullOrWhiteSpace(feature) || on is null)
            return new { ok = false, message = "Missing feature or state." };

        return await Task.Run(object () =>
        {
            var ctx = GameGraphicsFeatures.CreateLiveContext(game, _services.Settings);
            var snapshot = GameGraphicsFeatures.Set(
                game,
                feature,
                on.Value,
                acceptRisk,
                (id, ac) => _services.Settings.SetGraphicsModConsent(id, ac),
                ctx);
            return SerializeGraphicsFeatures(snapshot);
        }, ct).ConfigureAwait(false);
    }

    private static object SerializeGraphicsFeatures(GameGraphicsFeatures.Snapshot snapshot) => new
    {
        ok = snapshot.Ok,
        message = snapshot.Message,
        engine = snapshot.Engine,
        running = snapshot.Running,
        antiCheat = snapshot.AntiCheat,
        features = snapshot.Features.Select(f => new
        {
            id = f.Id,
            name = f.Name,
            state = f.State,
            route = f.Route,
            routeKind = f.RouteKind,
            canToggle = f.CanToggle,
            needsAntiCheatConfirm = f.NeedsAntiCheatConfirm,
            antiCheatWarning = f.AntiCheatWarning,
            closestOption = f.ClosestOption,
            details = f.Details,
        }).ToList(),
    };
}
