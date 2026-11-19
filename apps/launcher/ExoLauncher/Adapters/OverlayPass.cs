namespace ExoLauncher.Adapters;

/// <summary>
/// One pass that brings Steam's switches to Exo's rules, decided before anything is written. Steam holds localconfig.vdf
/// and sharedconfig.vdf in memory and writes them as it exits, so Exo writes them only while Steam is not running: a
/// pass that finds Steam running waits, keeps what it wants in the record (the "?" marker) and never closes Steam for
/// it. Whoever asked — Play, Properties, a library read — gets the same answer.
/// </summary>
internal static class OverlayPass
{
    /// <summary>What a pass does with what it wants written.</summary>
    internal enum Plan
    {
        /// <summary>Nothing is wanted that Steam doesn't have already.</summary>
        Nothing,
        /// <summary>Steam is not running: the wanted values go into its files now.</summary>
        WriteNow,
        /// <summary>Steam is running: the wanted values wait in the record for the next close of Steam.</summary>
        Wait,
    }

    internal static Plan Decide(bool steamRunning, bool anythingToWrite) =>
        !anythingToWrite ? Plan.Nothing : steamRunning ? Plan.Wait : Plan.WriteNow;

    /// <summary>What a pass that waits answers, and what Properties shows for a switch changed while Steam runs.</summary>
    internal const string WaitsForSteamToClose = "Steam takes it when it next closes.";

    /// <summary>
    /// A Steam switch picked in Properties: while Steam runs it is kept in the record and answered at once, Steam being
    /// never closed for it (true, the sentence); with Steam closed the pass writes it as every other pass does.
    /// </summary>
    internal static (bool Waits, string Message) SwitchAnswer(bool steamRunning) =>
        steamRunning ? (true, WaitsForSteamToClose) : (false, string.Empty);

    /// <summary>What one pass wants written: per game, and per game's record.</summary>
    internal sealed record Wanted(
        IReadOnlyDictionary<string, bool> Overlay,
        IReadOnlyDictionary<string, string> OverlayRecord,
        IReadOnlyDictionary<string, string> Input)
    {
        /// <summary>Whether anything waits for Steam to close: nothing to write without it.</summary>
        internal bool Anything => Overlay.Count > 0 || Input.Count > 0;
    }

    /// <summary>
    /// Steam's file with the wanted switches and Steam Input in it, and the record once they landed (the "?" taken
    /// off). Pure: the same file and the same wants give the same bytes, whether Steam was closed for them or the player
    /// closed it. Nothing else in the file changes (<see cref="SteamOverlayConfig.Write"/> and its neighbours keep the
    /// rest byte for byte).
    /// </summary>
    internal static (string Text, Dictionary<string, string> Record) Written(string text, Wanted wanted)
    {
        var written = SteamInputConfig.Write(
            SteamOverlayConfig.Write(text, wanted.Overlay), wanted.Input);
        var record = new Dictionary<string, string>(wanted.OverlayRecord, StringComparer.OrdinalIgnoreCase);
        foreach (var appId in wanted.Overlay.Keys) record[appId] = record[appId].TrimEnd('?');
        return (written, record);
    }
}
