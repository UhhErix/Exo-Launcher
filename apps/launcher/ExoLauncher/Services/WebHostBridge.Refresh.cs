using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

public sealed partial class WebHostBridge
{
    /// <summary>
    /// Library › Refresh: everything Exo shows about every game, read again.
    /// Sizes are measured again (the size cache is cleared), every store is
    /// asked for its games, time played and last launched again (games that
    /// weren't showing appear), Riot hours stay in their own ledger (tracker.gg is asked only by hand),
    /// achievements are read for every installed or played game
    /// (three at a time), gifts waiting on Steam and Epic are read again, and every cover is fetched again. Each part that
    /// fails is logged and the rest go on.
    /// </summary>
    private async Task<object> LibraryRefreshAllAsync(CancellationToken ct)
    {
        InstalledSizeCache.Clear();
        await _services.Library.GetLibraryAsync(force: true, fullScan: true, ct).ConfigureAwait(false);
        OnLibraryUpdated();

        // Games the user hid stay as they are: Refresh reads what shows (the stores are still read whole, to know what is owned).
        var hidden = _services.Settings.Current.HiddenGames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var played = _services.Library.PeekCachedLibrary()
            .Where(game => (game.Installed || (game.PlaytimeMinutes ?? 0) > 0) && !hidden.Contains(game.Id))
            .ToList();
        var achievements = 0;
        await Parallel.ForEachAsync(played, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, async (game, token) =>
        {
            try
            {
                await _services.Achievements.RefreshAsync(game, token).ConfigureAwait(false);
                Interlocked.Increment(ref achievements);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { AppLog.Debug($"Refresh: achievements for {game.Id} skipped: {ex.GetType().Name}"); }
        }).ConfigureAwait(false);

        _queue.Enqueue(() => _ = ReadGiftsAsync());
        var covers = await _services.Artwork.RefetchAllAsync(hidden, ct).ConfigureAwait(false);
        OnLibraryUpdated();
        AppLog.Info($"Library read again, achievements for {played.Count} games ({achievements} answered), {covers} covers; {hidden.Count} hidden games left as they were.");
        return new { ok = true, achievements, covers };
    }
}
