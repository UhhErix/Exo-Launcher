using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Best settings › Frame generation in games without anti-cheat (<see cref="AppSettings.FrameGenEverywhere"/>): Exo puts
/// the mod the card and game call for (DlssSwapService.FrameGen.cs) in every installed game it works in, so the game's own
/// DLSS Frame Generation switch is there the first time it opens, and takes it out of all of them when the switch goes off.
/// Never in a game with anti-cheat, never back in one where its first session failed
/// (<see cref="AppSettings.FrameGenKeptOff"/>) or where another mod took its place, never while the game runs or a store
/// writes it, and on every graphics card a mod helps: no GPU left behind, old ones most of all. A GeForce RTX 20 or 30,
/// GTX 900, 10 or 16, a Radeon RX 400 or newer, Radeon graphics in a laptop or handheld, an Intel Arc card or Arc
/// graphics. An RTX 40 or 50 keeps its game's own. A card that can't run a game's mod is caught by its first session:
/// one that ends within 30 seconds takes the mod out and keeps the game off (<see cref="FrameGenAfterSession"/>).
/// </summary>
public sealed partial class DlssSwapService
{
    /// <summary>
    /// The card Best settings puts frame generation in games for, or null: every card <see cref="FrameGenCardFor"/>
    /// names except an RTX 40 or 50 (their own frame generation) and an Arc that is neither an A or B card nor Arc
    /// graphics inside a processor (a workstation Arc Pro).
    /// </summary>
    internal static FrameGenCard? FrameGenEverywhereCard(string? gpu) =>
        FrameGenCardFor(gpu) is { DlssFrameGen: false, Class: not "arc-other" } card ? card : null;

    /// <summary>
    /// Every installed game it works in gets Exo's frame generation (see the class summary), one after another, while no
    /// game runs. The games it went into.
    /// </summary>
    /// <param name="keepOff">Keeps a game off for good where Exo couldn't save what it learned there (<see cref="PutRemovedBack"/>).</param>
    internal async Task<int> FrameGenEverywhereAsync(
        IEnumerable<GameEntry> games, string? gpu, Func<GameEntry, bool> busy, Func<string, bool> keptOff, CancellationToken ct,
        Action<string>? keepOff = null)
    {
        if (FrameGenEverywhereCard(gpu) is null) return 0;
        var added = 0;
        foreach (var game in games)
        {
            ct.ThrowIfCancellationRequested();
            if (!game.Installed || string.IsNullOrEmpty(game.Path) || keptOff(game.Id) || busy(game)) continue;
            if (ResolveInstallRoot(game.Path) is not { } root || IsGameRunning(game, root)) continue;
            // The state table (FrameGenDecide) says what happens next; Best settings acts on PutBack and PutIn.
            FrameGenNext next;
            try
            {
                next = FrameGenRead(game, gpu, ct).Next;
                if (next == FrameGenNext.PutBack)
                {
                    if (!PutRemovedBack(game, keepOff)) continue;
                    next = FrameGenRead(game, gpu, ct).Next;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (next != FrameGenNext.PutIn) continue;
            try
            {
                var (ok, message) = await FrameGenOnAsync(game, gpu, ct).ConfigureAwait(false);
                if (ok) added++;
                AppLog.Info($"Frame generation in {game.Title} from Best settings: {(ok ? "on" : message)}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLog.Info($"Frame generation in {game.Title} from Best settings didn't go in: {ex.Message}");
            }
        }
        return added;
    }

    /// <summary>
    /// Exo's files went from a game (antivirus, a store's verify, the player): what is left of Exo's comes out, and the
    /// first time the game goes on to get the mod again, as any game would. The second time it stays off: whatever took
    /// them will take them again. False when the game stays as it is. Where Exo can't save that count, the game goes to
    /// <paramref name="keepOff"/> instead, so it never goes round for ever.
    /// </summary>
    private bool PutRemovedBack(GameEntry game, Action<string>? keepOff)
    {
        var again = FrameGenMemoryFor(game.Id).PutBack > 0;
        var (ok, message) = FrameGenOff(game);
        if (!ok)
        {
            AppLog.Info($"Frame generation in {game.Title}: Exo's files went; {message}");
            return false;
        }
        var saved = again ? KeepFrameGenOff(game.Id, "removed") : ChangeFrameGenMemory(game.Id, memory => memory with { PutBack = memory.PutBack + 1 });
        if (!saved)
        {
            keepOff?.Invoke(game.Id);
            AppLog.Warn($"Frame generation in {game.Title}: Exo's files went; kept off, as Exo couldn't save how often.");
            return false;
        }
        AppLog.Info($"Frame generation in {game.Title}: Exo's files went{(again ? " again; kept off" : "; putting them back once")}.");
        return !again;
    }

    /// <summary>Best settings' switch is off: Exo's frame generation comes out of every game it can (a running one, next round).</summary>
    internal void FrameGenEverywhereOff(IEnumerable<GameEntry> games, Func<GameEntry, bool> busy)
    {
        foreach (var game in games)
        {
            if (FrameGenFolderFor(game.Id) is not { } folder || ReadFrameGenManifest(folder) is null || busy(game)) continue;
            var (ok, message) = FrameGenOff(game);
            AppLog.Info($"Frame generation in {game.Title}: Best settings turned it off; {(ok ? "taken out" : message)}.");
        }
        // Last, so a take-out above leaves nothing kept off either: a record Exo keeps of another mod's file over its
        // loader still says so on the game's line.
        ForgetKeptOff();
    }

    private static Action? _frameGenEverywhereSoon;

    /// <summary>Best settings' switch changed, or the library did: its games are gone through shortly.</summary>
    internal static void FrameGenEverywhereSoon() => _frameGenEverywhereSoon?.Invoke();
}
