using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>
/// Update on open (DESIGN.md › Chrome › Update on open): the launch's one update check (AppUpdateService.OpenCheck, started
/// before the page) opens the update sheet when it finds a newer release, once per process. The page asking again (a
/// reload) gets no sheet, Settings' check never offers one, and nothing checks again while Exo runs. A Debug build, or a
/// version that isn't a plain release (a suffix, 0.0.0), is never offered one. Not now joins Try again once an update has
/// failed, here or the one before this start (UpdateOutcome), and holds for the rest of the launch.
/// </summary>
internal sealed partial class UpdateOnOpen
{
    private int _asked;
    private int _failed;

    /// <summary>The page's first ask in this process: true when it opens the update sheet.</summary>
    internal bool Offer(bool updateAvailable, string runningVersion, bool debugBuild, bool lastUpdateFailed)
    {
        if (Interlocked.Exchange(ref _asked, 1) == 1) return false;
        if (!updateAvailable || debugBuild || !IsRelease(runningVersion)) return false;
        if (lastUpdateFailed) Failed();
        return true;
    }

    /// <summary>An update didn't go in: the sheet offers Not now beside Try again.</summary>
    internal void Failed() => Volatile.Write(ref _failed, 1);

    internal bool NotNow => Volatile.Read(ref _failed) == 1;

    /// <summary>A release's own version as VERSION holds it: X.Y.Z in ASCII digits, nothing after, never 0.0.0.</summary>
    internal static bool IsRelease(string? version) =>
        version is not null && ReleaseVersion().IsMatch(version) && version.Any(c => c is >= '1' and <= '9');

    [GeneratedRegex(@"^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersion();
}
