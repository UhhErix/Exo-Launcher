using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// What's left of G-SYNC per game. Under a global driver cap, Exo used to lift that cap and force the driver's V-Sync off in
/// a competitive game's own NVIDIA profile, and put back its values once the cap went. Exo no longer caps frames or runs
/// that recipe, so nothing sets those values now: any it still has on record (game-settings\sync.json) go back as Exo
/// starts, and the record goes with them. There was never a V-Sync or low latency choice here apart from the cap.
/// </summary>
internal static class CompetitiveSync
{
    /// <summary>A game Exo uncapped: its program, and its profile's values before (null: none of its own).</summary>
    internal sealed record Uncapped(string Exe, Dictionary<uint, uint?> Before);

    internal static string StatePath { get; set; } = Path.Combine(PathHelper.AppDataDir, "game-settings", "sync.json");

    /// <summary>As Exo starts, on an NVIDIA driver: each game's own values back.</summary>
    internal static void RestoreLeftovers()
    {
        try
        {
            if (!File.Exists(StatePath) || !NvidiaProfiles.Available) return;
            Restore((gameId, game) =>
            {
                var title = Path.GetFileNameWithoutExtension(game.Exe);
                var state = NvidiaProfiles.ForGame(new(game.Exe, title, "", (_, _) => true), [.. game.Before.Keys], game.Before);
                AppLog.Info($"G-SYNC per game is gone: {gameId}'s own cap and V-Sync: {(state.Ok ? "back" : state.Message)}.");
                return state.Ok;
            });
        }
        catch (Exception ex) { AppLog.Warn("G-SYNC per game: " + ex.Message); }
    }

    /// <summary>Puts back each game on record; one the driver refused stays for the next start, and an empty record goes.</summary>
    internal static void Restore(Func<string, Uncapped, bool> putBack)
    {
        if (!File.Exists(StatePath)) return;
        Dictionary<string, Uncapped> all;
        try { all = JsonSerializer.Deserialize<Dictionary<string, Uncapped>>(File.ReadAllText(StatePath)) ?? []; }
        catch (JsonException) { all = []; }
        foreach (var (gameId, game) in all.ToList())
            if (putBack(gameId, game)) all.Remove(gameId);
        if (all.Count == 0)
        {
            DeletionGate.DeleteFile(StatePath);
            return;
        }
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(all));
        File.Move(temporary, StatePath, overwrite: true);
    }
}
