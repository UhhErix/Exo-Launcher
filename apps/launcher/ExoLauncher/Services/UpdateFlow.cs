namespace ExoLauncher.Services;

/// <summary>
/// Exo's own update as one table (AppUpdateService moves through it). A step the table has no row for leaves the state as it
/// is. Every state has a way out. Update now goes in at once, a game running or not: Exo closes and Setup runs, and the game
/// keeps running. A kept update (WaitingForGame while a game runs, else Ready) is applied by the next Update now or at the
/// next start (AppUpdateService.ApplyKeptAtStartAsync), which still waits for no game at start. The host
/// sends each state to the UI (app.updateState and the update answers' state), which draws only from it.
/// </summary>
internal static class UpdateFlow
{
    internal enum State { Idle, Checking, Downloading, WaitingForGame, Ready, Applying, Restarting, Failed }

    internal enum Step
    {
        /// <summary>A check for a newer release begins.</summary>
        Check,
        /// <summary>The check answered (a newer release or none).</summary>
        CheckDone,
        CheckFailed,
        /// <summary>Update now, or Try again.</summary>
        Install,
        /// <summary>The installer is downloaded (or was already) and passed its size, SHA-256 and identity checks.</summary>
        Verified,
        /// <summary>Not enough space, a failed or wrong download.</summary>
        DownloadFailed,
        GameStarted,
        GameEnded,
        /// <summary>Exo applies the verified installer (right after Update now, or at the next start).</summary>
        Apply,
        InstallerStarted,
        /// <summary>The installer did not start, or the verified file is gone.</summary>
        ApplyFailed,
        /// <summary>Exo closes for the installer (AppServices.Shutdown, once the installer runs).</summary>
        Exited,
    }

    /// <summary>Game: null for either; true or false for a row that holds only when a game is, or is not, running.</summary>
    internal static readonly (State From, Step On, bool? Game, State To)[] Table =
    [
        (State.Idle, Step.Check, null, State.Checking),
        (State.Failed, Step.Check, null, State.Checking),
        (State.Checking, Step.CheckDone, null, State.Idle),
        // A check that could not answer (offline, a captive portal, the address down) is not a failed update: the player did nothing.
        (State.Checking, Step.CheckFailed, null, State.Idle),

        (State.Idle, Step.Install, null, State.Downloading),
        (State.Failed, Step.Install, null, State.Downloading),
        // Update now pressed while a check is still answering: the install goes on (it checks again itself).
        (State.Checking, Step.Install, null, State.Downloading),
        // Pressed again once the installer runs: nothing more starts (InstallAsync answers at once); Exo is closing.
        (State.Restarting, Step.Install, null, State.Restarting),
        (State.Downloading, Step.Verified, null, State.Ready),
        (State.Downloading, Step.DownloadFailed, null, State.Failed),

        (State.Ready, Step.GameStarted, null, State.WaitingForGame),
        (State.WaitingForGame, Step.GameEnded, null, State.Ready),
        (State.Ready, Step.Install, null, State.Applying),
        (State.Ready, Step.Apply, null, State.Applying),
        (State.WaitingForGame, Step.Install, null, State.Applying),
        (State.WaitingForGame, Step.Apply, null, State.Applying),
        (State.Ready, Step.ApplyFailed, null, State.Failed),
        (State.WaitingForGame, Step.ApplyFailed, null, State.Failed),

        (State.Applying, Step.InstallerStarted, null, State.Restarting),
        (State.Applying, Step.ApplyFailed, null, State.Failed),
        (State.Restarting, Step.Exited, null, State.Idle),
        // The window did not close for the installer.
        (State.Restarting, Step.ApplyFailed, null, State.Failed),
    ];

    internal static State Next(State from, Step on, bool gameRunning)
    {
        foreach (var row in Table)
            if (row.From == from && row.On == on && (row.Game is null || row.Game == gameRunning)) return row.To;
        return from;
    }
}
