using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam left in Exo's offline mode once Exo is gone. Exo puts Steam offline for two reasons that end with Exo: an
/// outage Exo waited out (<see cref="SteamLoginUsers.ForOutage"/>) and a Steam Family game Exo started
/// (<see cref="SteamLoginUsers.ForFamily"/>). The record that says why (<c>steam-offline.json</c>) outlives the run that
/// wrote it, so the next start settles it, and a clean exit does too. Steam's file is written only while Steam is not
/// running (it rewrites the file as it exits), so a running Steam is never closed for it: the record waits for its next
/// close. The player's own offline switch (<see cref="SteamLoginUsers.Chosen"/>) and a record written before reasons
/// were kept (no reason at all) are never touched.
/// </summary>
internal static class SteamOfflineOwed
{
    /// <summary>What settling a standing switch does.</summary>
    internal enum Plan
    {
        /// <summary>Not Exo's to settle: the player's own switch, a record with no reason, or nothing standing.</summary>
        Leave,
        /// <summary>Steam is closed: the file is written online now.</summary>
        WriteNow,
        /// <summary>Steam is running: it is not closed or waited on for this; the record waits for its next close.</summary>
        WaitForSteamToClose,
    }

    /// <summary>Whether the standing switch is one that ends with Exo (an outage or a family game), never the player's.</summary>
    internal static bool EndsWithExo(SteamLoginUsers.OfflineSwitch? standing) =>
        standing is not null && standing.Reason is SteamLoginUsers.ForOutage or SteamLoginUsers.ForFamily;

    /// <summary>
    /// What the reason asked for is still wanted: only a family switch while a Steam game it was made for still runs. An
    /// outage belongs to the Steam run that couldn't sign in, so a record left over has nothing left asking for it.
    /// </summary>
    internal static bool StillWanted(SteamLoginUsers.OfflineSwitch? standing, bool steamGameRunning) =>
        standing?.Reason == SteamLoginUsers.ForFamily && steamGameRunning;

    /// <summary>The decision, made with nothing done: whether what stands is Exo's to put back, and whether it can be written now.</summary>
    internal static Plan Decide(SteamLoginUsers.OfflineSwitch? standing, bool steamRunning, bool steamGameRunning) =>
        !EndsWithExo(standing) || StillWanted(standing, steamGameRunning) ? Plan.Leave
        : steamRunning ? Plan.WaitForSteamToClose : Plan.WriteNow;

    /// <summary>
    /// Exo starting: settles a switch a past run left, once what asked for it is over. Written online now while Steam is
    /// closed; while Steam runs the record waits for its next close (Exo never closes it for this). True when that record
    /// is now settled or is waiting, false when there was nothing Exo's to settle.
    /// </summary>
    internal static bool ResolvedAtStart(bool steamRunning, bool steamGameRunning)
    {
        if (HostMode.IsDisposable) return false;
        return Decide(SteamLoginUsers.LoadOfflineSwitch(), steamRunning, steamGameRunning) switch
        {
            Plan.WriteNow => WriteOnlineNow(),
            Plan.WaitForSteamToClose => true,
            _ => false,
        };
    }

    /// <summary>
    /// A running Steam closed, or an Exo exit: an outage or family switch Exo made goes back online while Steam is closed.
    /// Exo never waits on Steam and never closes it; a running Steam keeps its mode and the record stays for later.
    /// The player's own switch is untouched.
    /// </summary>
    internal static bool PutBackWhenSteamClosed(bool steamRunning)
    {
        if (HostMode.IsDisposable) return false;
        return Decide(SteamLoginUsers.LoadOfflineSwitch(), steamRunning, steamGameRunning: false) == Plan.WriteNow && WriteOnlineNow();
    }

    private static bool WriteOnlineNow() => SteamInstallation.ResolveRoot() is { } root && SteamLoginUsers.WriteOnline(root);
}