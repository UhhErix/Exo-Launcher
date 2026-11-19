namespace ExoLauncher.Services;

/// <summary>A Lucid process as Windows lists it. Its id and start time together name this one process: an id alone is reused.</summary>
internal sealed record LucidProcess(int Id, long Started, string File);

/// <summary>What <see cref="LucidStop"/> needs of Windows, so the order of its steps can be tested with a fake.</summary>
internal interface ILucidProcesses
{
    /// <summary>Every Lucid running anywhere on this PC, with the exe it runs from.</summary>
    IReadOnlyList<LucidProcess> Running();

    /// <summary>Asks these to close as Windows does at sign-out (never a kill). False when Windows refused to ask.</summary>
    bool AskToClose(IReadOnlyList<LucidProcess> which);

    /// <summary>Ends this process, after checking again that it is still the one named (same id, start time and exe).</summary>
    bool End(LucidProcess which);

    Task Wait(TimeSpan time);
}

/// <param name="Found">Lucids from Exo's app folder that were running.</param>
/// <param name="Politely">Of those, how many closed when asked.</param>
/// <param name="Ended">Of those, how many had to be ended.</param>
/// <param name="Left">Of those, how many are still running.</param>
internal sealed record LucidStopped(int Found, int Politely, int Ended, int Left);

/// <summary>
/// Stops the Lucid that Exo hosts from <c>addons\lucid</c> in its app folder, which would otherwise hold the folder that
/// an update, an install, an uninstall or a dev swap has to rename or delete. Ways it could fail: a separate copy the
/// user runs elsewhere is closed or ended with it; a folder whose name only starts like Exo's (<c>app.previous</c>,
/// <c>app-old</c>) or a path with <c>..</c> or other capitals counts as, or escapes from, Exo's folder; a process is ended
/// without being asked first, or after it closed and its id went to another program; Windows refusing the ask leaves
/// Exo waiting the whole time for nothing.
/// </summary>
internal static class LucidStop
{
    /// <summary>
    /// Whether <paramref name="file"/> is inside <paramref name="folder"/> (not the folder itself, not a sibling that shares
    /// its first letters). Both must be full paths; a relative one never matches, since it would be read against whatever
    /// folder Exo happens to run in.
    /// </summary>
    internal static bool IsUnder(string? file, string? folder)
    {
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(file) || !Path.IsPathFullyQualified(folder)) return false;
            var inside = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(file);
            return path.Length > inside.Length && path.StartsWith(inside, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    internal static bool IsUnderAny(string? file, IReadOnlyList<string> folders) => folders.Any(folder => IsUnder(file, folder));

    /// <summary>
    /// Asks the Lucid from one of <paramref name="folders"/> (Exo's app folder, and the store of the Lucid Exo downloaded) to close, gives it <paramref name="ask"/>, and only then ends the
    /// ones still running, each only if it is still the same process from the same exe. Lucids outside the folders are not
    /// listed, asked or ended.
    /// </summary>
    internal static async Task<LucidStopped> HostedAsync(ILucidProcesses os, IReadOnlyList<string> folders, TimeSpan ask)
    {
        var mine = os.Running().Where(p => IsUnderAny(p.File, folders)).ToList();
        if (mine.Count == 0) return new LucidStopped(0, 0, 0, 0);

        // When Windows won't ask (the Restart Manager failing as it did with the hidden Lucid), waiting is for nothing.
        var asked = os.AskToClose(mine);
        await Gone(asked ? ask : TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);

        var stubborn = Still();
        var ended = 0;
        foreach (var process in stubborn)
            if (os.End(process)) ended++;
        if (ended > 0) await Gone(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        var left = Still().Count;
        return new LucidStopped(mine.Count, mine.Count - stubborn.Count, ended, left);

        // The same processes (by id and start time) still running from the folder.
        List<LucidProcess> Still() =>
        [
            .. os.Running().Where(p => IsUnderAny(p.File, folders) && mine.Any(m => m.Id == p.Id && m.Started == p.Started)),
        ];

        async Task Gone(TimeSpan within)
        {
            for (var waited = TimeSpan.Zero; waited < within && Still().Count > 0; waited += Step)
                await os.Wait(Step).ConfigureAwait(false);
        }
    }

    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);
}
