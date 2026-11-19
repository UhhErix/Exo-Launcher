using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Settings › Update DLSS, FSR and XeSS automatically (<see cref="AppSettings.AutoUpdateUpscalers"/>), the part that reaches the
/// games: every installed game whose last scan found upscaler files moves to the newest ones Exo has, as Update all would, with
/// nothing to press. The same rules as Update all (<see cref="ApplyAsync"/>): a file already newer is kept, a game with
/// anti-cheat only where the player allowed it for that game, never while the game runs or a store writes it, and Restore puts
/// the game's own files back. Ways it could fail: a game never scanned is walked here (it waits for its first status read),
/// a game with anti-cheat the player didn't allow is written, or a failure in one game stops the rest.
/// </summary>
public sealed partial class DlssSwapService
{
    /// <summary>The games it moved to newer files.</summary>
    internal async Task<int> UpscalersKeepCurrentAsync(
        IEnumerable<GameEntry> games, Func<GameEntry, bool> busy, Func<string, string?> consent, CancellationToken ct)
    {
        var cache = CurrentDetectionCache();
        var moved = 0;
        foreach (var game in games)
        {
            ct.ThrowIfCancellationRequested();
            if (!game.Installed || string.IsNullOrEmpty(game.Path) || busy(game)) continue;
            if (cache.TryGet(game, out _) is not { } scan || !scan.Files.Any(dll => dll.SkipReason is null && dll.Mod is null)) continue;
            // Anti-cheat the player hasn't allowed for this game: ApplyAsync would refuse; skipped before any work.
            if (scan.AntiCheat is { Length: > 0 } && consent(game.Id) is null) continue;
            if (ResolveInstallRoot(game.Path) is not { } root || IsGameRunning(game, root)) continue;
            try
            {
                var result = await ApplyAsync(game, null, null, () => consent(game.Id), ct).ConfigureAwait(false);
                if (result.Updated > 0)
                {
                    moved++;
                    AppLog.Info($"Upscalers in {game.Title}: {result.Updated} file(s) moved to the newest by themselves.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Info($"Upscalers in {game.Title} not updated by themselves: {ex.Message}");
            }
        }
        return moved;
    }
}
