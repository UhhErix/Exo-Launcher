namespace ExoLauncher.Services;

/// <summary>
/// One plain status per game and one for the PC, as states (the UI says them in its own words; none carries an id, a value
/// or a count). A game's is the worst thing true of its entries, in this order: the game won't keep some settings, waits for
/// its first run, the player's changes kept, put back after the update, optimized. A refused write is never Optimized.
/// Ways it could fail: Optimized shown while a write was refused or never happened (an unreadable file, a failed write);
/// a game never optimized shown as anything; a status that needs the entries to be read again to be told.
/// </summary>
internal static class KeepStatus
{
    /// <summary>A game's status: None when Optimize is not on for it.</summary>
    internal enum Game { None, Optimized, PutBackAfterUpdate, YourChangesKept, WaitsForFirstRun, WontKeepSome }

    /// <summary>The PC's: the games' and the driver side's (item C) folded into one.</summary>
    internal enum Pc { None, Optimized, PutBackAfterDriverUpdate, NeedsYourOk }

    internal static Game Of(Keep.KeepGame? game)
    {
        if (game?.Keeping is not { } keeping) return Game.None;
        var states = game.Entries.Values.Select(entry => entry.State).ToList();
        if (states.Contains(KeepState.Refused)) return Game.WontKeepSome;
        if (keeping.Pending || states.Contains(KeepState.Waiting)) return Game.WaitsForFirstRun;
        if (states.Contains(KeepState.Yielded)) return Game.YourChangesKept;
        if (states.Count == 0 && keeping.Groups.Count > 0 && keeping.Again is null) return Game.Optimized;
        if (keeping.Again is not null) return Game.PutBackAfterUpdate;
        // Undone is a value found moved that could not be put back yet, so it is not held; a skipped line is not asked for.
        return states.Contains(KeepState.Undone) ? Game.None : Game.Optimized;
    }

    /// <summary>A game's status by its profile id.</summary>
    internal static Game Of(string profileId) => Of(Keep.Load().Games.GetValueOrDefault(profileId));

    /// <summary>The PC's status from every game's: Optimized when any game is held and none needs the player; nothing here asks for an OK.</summary>
    internal static Pc ForPc(IEnumerable<Game> games, bool driverPutBack = false, bool needsOk = false)
    {
        if (needsOk) return Pc.NeedsYourOk;
        if (driverPutBack) return Pc.PutBackAfterDriverUpdate;
        return games.Any(game => game is Game.Optimized or Game.PutBackAfterUpdate or Game.YourChangesKept) ? Pc.Optimized : Pc.None;
    }

    /// <summary>
    /// What the page is told: the PC's status and each optimized game's, by game id, from the record alone. A game's record
    /// is told under each library game it was pressed for (<see cref="Keep.KeepGame.GameIds"/>), and a game with no status is
    /// left out. Nothing here reads the library or a game's install folder.
    /// </summary>
    internal sealed record Report(string Pc, IReadOnlyDictionary<string, string> Games);

    /// <summary>The word the page gets for a status: the enum's name with a lower-case first letter ("putBackAfterUpdate").</summary>
    internal static string Word<T>(T status) where T : struct, Enum
    {
        var name = status.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// The report from the record: every game that has one, under each library game id it was pressed for. A driver Exo's
    /// settings went back under since the last game ran (<see cref="DriverKeep.PutBackShows"/>) is said on the PC's side.
    /// </summary>
    internal static Report For(Keep.Doc doc, bool driverPutBack = false)
    {
        var statuses = new Dictionary<string, Game>(StringComparer.Ordinal);
        foreach (var game in doc.Games.Values)
        {
            var status = Of(game);
            if (status == Game.None) continue;
            foreach (var gameId in game.GameIds) statuses[gameId] = status;
        }
        return new Report(Word(ForPc(statuses.Values, driverPutBack)), statuses.ToDictionary(pair => pair.Key, pair => Word(pair.Value), StringComparer.Ordinal));
    }
}
