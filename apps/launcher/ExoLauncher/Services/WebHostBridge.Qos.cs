using System.Text.Json;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    private async Task<object> SystemQosAsync(JsonElement p, bool hasParams, CancellationToken ct)
    {
        var action = ReadString(p, hasParams, "action");
        var gameId = ReadString(p, hasParams, "gameId");
        string? message = null;

        if (action is "on" or "off")
        {
            var game = await FindGameAsync(gameId, ct).ConfigureAwait(false);
            if (game is null)
                message = "Game not found.";
            else
                message = await Task.Run(() => GameQosPolicy.Shared.SetAsync(game, action == "on", ct), ct).ConfigureAwait(false);
        }

        var library = _services.Library.PeekCachedLibrary();
        var state = await Task.Run(() => GameQosPolicy.Shared.Read(library), ct).ConfigureAwait(false);
        return new
        {
            ok = message is null,
            message,
            nlaBypassed = state.NlaBypassed,
            note = state.Note,
            games = state.Games.Select(g => new
            {
                gameId = g.GameId,
                title = g.Title,
                exeName = g.ExeName,
                enabled = g.Enabled,
            }).ToList(),
        };
    }
}
