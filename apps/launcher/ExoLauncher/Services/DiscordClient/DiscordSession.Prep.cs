using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// Share when I'm getting ready: the signed-in person's Exo tells their friends' Exo apps over the friend link that a game
/// is installing, building its shaders or ready (<see cref="FriendPrep"/>, <see cref="DiscordPrep"/>), so a Party can say who is
/// still compiling. Off until the person turns it on (Settings › Discord › Privacy), and only while Share my games is on
/// too, since it rides the same link. Told by the Launcher's install events and Prepare's state alone: nothing looks at a
/// game's process, window or files, anti-cheat and Riot's games included.
/// </summary>
internal sealed partial class DiscordSession
{
    private static readonly DiscordPrep Prep = new();
    private static Timer? _prepExpiry;

    internal static void WatchPrep(LaunchOrchestrator launcher, ShaderPrepare shaders, SettingsService settings, LibraryService library)
    {
        Func<string, Func<(string Title, bool May)?>> resolve = id => () =>
            library.FindVisualCard(id) is { } entry ? (entry.Title, MayShare(entry.Store, IsHidden(id, settings.Shared.HiddenGames, library))) : null;
        long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        launcher.ProgressChanged += progress =>
        {
            if (Current is null) return;
            if (Prep.Install(progress.GameId, progress.Phase, progress.Percent, resolve(progress.GameId), Now())) PublishPrep();
        };
        var compiling = new HashSet<string>(StringComparer.Ordinal);
        shaders.Changed += () =>
        {
            if (Current is null) return;
            var running = shaders.Running;
            var changed = false;
            lock (compiling)
            {
                // A run that ended: ready when Prepare built its shaders for this driver.
                foreach (var id in compiling.Where(id => id != running).ToList())
                {
                    compiling.Remove(id);
                    var prepared = library.FindVisualCard(id) is { } entry && shaders.StateOf(entry).State == "prepared";
                    changed |= Prep.Compile(id, running: false, prepared, resolve(id), Now());
                }
                if (running is not null && compiling.Add(running)) changed |= Prep.Compile(running, running: true, prepared: false, resolve(running), Now());
            }
            if (changed) PublishPrep();
        };
        launcher.GameLaunched += game => { if (Prep.Launched(game.Id)) PublishPrep(); };
    }

    /// <summary>Whether this account shares that a game is getting ready: only when it turned that on, and Share my games is on.</summary>
    internal bool SharesReady => SharesGames && Cache.Me?.Id is { } id && App.Services.Settings.Shared.DiscordReadyShared.Contains(id, StringComparer.Ordinal);

    /// <summary>What the link says of getting ready now: nothing unless this account turned it on.</summary>
    private System.Text.Json.Nodes.JsonObject? PrepSection() => SharesReady ? FriendPrep.Write(Prep.Current(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) : null;

    private static void PublishPrep()
    {
        if (Current is not { } session) return;
        lock (session._linkGate) session._links?.Set(FriendPrep.Section, session.PrepSection());
        // "Ready" is said for a while and then goes by itself: one timer, set for the first to run out and only while something is ready.
        var next = Prep.NextExpiryIn(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _prepExpiry ??= new Timer(_ => { if (Prep.Expire(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) PublishPrep(); });
        _prepExpiry.Change(next is { } wait ? wait + 500 : Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Share when I'm getting ready for the account signed in. Off: what was said goes at once and nothing more is sent.
    /// Nothing on the Discord account changes either way.
    /// </summary>
    internal (bool On, string? Error) SetShareReady(bool on)
    {
        if (Cache.Me?.Id is not { } id) return (false, "Discord isn't connected.");
        try
        {
            App.Services.Settings.UpdateProfile(s => s.DiscordReadyShared =
                SettingsService.NormalizeHiddenGames(on ? s.DiscordReadyShared.Append(id) : s.DiscordReadyShared.Where(kept => kept != id)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Warn("Discord client: Share when I'm getting ready not saved (" + ex.GetType().Name + ").");
            return (SharesReady, "Exo didn't save that. Try again.");
        }
        AppLog.Info("Discord client: Share when I'm getting ready " + (on ? "on." : "off."));
        PublishPrep();
        return (on, null);
    }
}
