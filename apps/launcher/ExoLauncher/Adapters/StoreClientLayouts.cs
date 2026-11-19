using System.Diagnostics;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

/// <summary>
/// Which store client layout is installed: Epic's new per-user launcher (0.113+, %LOCALAPPDATA%) or the old machine-wide
/// one (Program Files), and Steam's stable or Beta client (the branch package\beta names). Exo targets one of each, so
/// onboarding and every app start ask for the new one. Detection only: Epic's library records are read elsewhere.
/// </summary>
internal static class StoreClientLayouts
{
    /// <summary>Unknown: the per-user launcher is there but its version can't be read; it is never a download (a read glitch).</summary>
    internal enum EpicLayout { None, Old, New, Unknown }

    internal const string EpicUnreadable = "Exo could not read which version of Epic's launcher is installed. Try again.";

    /// <summary>Unknown: Steam is installed but its branch file can't be read; the card shows the reason and holds the step.</summary>
    internal enum SteamChannel { None, Stable, Beta, Unknown }

    /// <summary>The first Epic launcher version with the per-user layout.</summary>
    internal const string NewEpicVersion = "0.113";

    /// <param name="Layout">None, the old machine-wide launcher, or the new per-user one (0.113+).</param>
    /// <param name="Version">The launcher file's version, when readable.</param>
    /// <param name="OldStillThere">The old machine-wide launcher is installed too (a warning once the new one is in).</param>
    internal sealed record EpicState(EpicLayout Layout, string? Version, bool OldStillThere);

    /// <param name="Root">Steam's install folder (where steam.exe is), or null when Steam is not installed.</param>
    /// <param name="Problem">Why the branch couldn't be read (with <see cref="SteamChannel.Unknown"/>), for the card.</param>
    internal sealed record SteamState(SteamChannel Channel, string? Root, string? Problem = null);

    internal sealed record Status(EpicState Epic, SteamState Steam)
    {
        /// <summary>
        /// The Game clients card is due: an old Epic launcher, Steam on its stable client, or a Steam branch that can't be read
        /// yet (held until it can). A store that is not installed asks nothing; a user with neither store has no card at all.
        /// </summary>
        internal bool NeedsFix => Epic.Layout == EpicLayout.Old || Steam.Channel == SteamChannel.Stable;

        /// <summary>
        /// Why a state could not be read (Epic's version, Steam's branch), or null. Unknown is never a confirmed fix: it holds
        /// onboarding and shows the reason with Try again, but never a download and never the start card.
        /// </summary>
        internal string? Problem => Steam.Problem ?? (Epic.Layout == EpicLayout.Unknown ? EpicUnreadable : null);
    }

    /// <summary>The new launcher's exe in its per-user folder (Epic's own layout, 0.113+).</summary>
    internal static string NewEpicExe(string localAppData) => Path.Combine(localAppData, "Epic Games", "Launcher", "EpicGamesLauncher.exe");

    /// <summary>The old machine-wide launcher's exes (Portal\Binaries, Win64 or Win32, under either Program Files).</summary>
    internal static IReadOnlyList<string> OldEpicExes(string programFiles, string programFilesX86) =>
        new[] { programFiles, programFilesX86 }
            .SelectMany(folder => new[] { "Win64", "Win32" }.Select(arch =>
                Path.Combine(folder, "Epic Games", "Launcher", "Portal", "Binaries", arch, "EpicGamesLauncher.exe")))
            .ToList();

    /// <summary>
    /// Pure Epic evaluation. The new per-user launcher wins when present; the old one is reported as still there when it is
    /// installed beside it, so the card can warn without removing anything.
    /// </summary>
    internal static EpicState EvaluateEpic(Func<string, bool> fileExists, Func<string, string?> versionOf, string newExe, IReadOnlyList<string> oldExes)
    {
        var oldExe = oldExes.FirstOrDefault(fileExists);
        if (fileExists(newExe))
        {
            // The per-user launcher counts as new only at 0.113 or later; an older version is the old layout to fix; a version that
            // can't be read is unknown (not a fix).
            var version = versionOf(newExe);
            if (VersionNumber.Parse(version) is null) return new EpicState(EpicLayout.Unknown, null, false);
            var isNew = VersionNumber.AtLeast(version, NewEpicVersion);
            return new EpicState(isNew ? EpicLayout.New : EpicLayout.Old, version, isNew && oldExe is not null);
        }
        if (oldExe is not null) return new EpicState(EpicLayout.Old, versionOf(oldExe), false);
        return new EpicState(EpicLayout.None, null, false);
    }

    /// <summary>Pure Steam evaluation: the branch named in package\beta, or none when Steam is not installed.</summary>
    internal static SteamState EvaluateSteam(string? root, string? beta) =>
        root is null
            ? new SteamState(SteamChannel.None, null)
            : new SteamState(beta == SteamClientUpdate.Beta ? SteamChannel.Beta : SteamChannel.Stable, root);

    /// <summary>Steam's state with its branch read by <paramref name="readBeta"/>; an unreadable branch is Unknown with the reason, never a throw.</summary>
    internal static SteamState ReadSteamState(string? root, Func<string, string?> readBeta)
    {
        if (root is null) return EvaluateSteam(null, null);
        try
        {
            return EvaluateSteam(root, readBeta(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Steam: its branch file couldn't be read: " + ex.GetType().Name);
            return new SteamState(SteamChannel.Unknown, root, "Exo could not read which Steam client this PC uses. Close Steam, then try again.");
        }
    }

    /// <summary>What is installed on this PC now. Never throws: an unreadable part is reported as its state, so the card shows.</summary>
    internal static Status Detect()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var epic = EvaluateEpic(File.Exists, VersionOf, NewEpicExe(local),
            OldEpicExes(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)));
        var root = SteamInstallation.ResolveRoot();
        return new Status(epic, ReadSteamState(root, SteamClientUpdate.ReadBeta));
    }

    private static string? VersionOf(string exe)
    {
        try { return FileVersionInfo.GetVersionInfo(exe).FileVersion?.Trim() is { Length: > 0 } version ? version : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Why a client fix must wait, or null. Steam and Epic restart and install only while no game runs: Exo's own game, a
    /// library game Exo sees running, or a Steam game.
    /// </summary>
    internal static string? FixBlockedBy(bool exoGameRunning, bool steamGameRunning) =>
        exoGameRunning || steamGameRunning ? "Close your game first. Exo fixes the clients once nothing is playing." : null;
}
