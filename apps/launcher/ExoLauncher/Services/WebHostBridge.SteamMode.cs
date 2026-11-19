using System.Net.NetworkInformation;
using System.Text.Json;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Steam's own offline mode on Exo's terms. Settings › Game stores › Steam's Offline mode turns it on or off for the
/// account Steam signs in as (<c>steam.setOffline</c>): Steam restarts out of sight into it (a closed Steam only has it
/// written, for its next start) and it stays until turned off. Play on a game borrowed from the account's Steam Family
/// starts Steam offline first when the game plays offline (<see cref="SteamOfflineMode.Decide"/>), and once no game
/// runs Steam goes back online, only when Exo was the one that put it offline; a family game Steam won't start offline
/// is started again online. Steam never restarts while a Steam game runs, while it downloads, or into offline mode
/// without a saved sign-in (<see cref="SteamOfflineMode.CanRestart"/>). A restart can't touch an Exo call: Exo's
/// Discord runs in Exo itself, and Steam starts hidden, never taking the focus.
/// </summary>
public sealed partial class WebHostBridge
{
    /// <summary>Family games Steam didn't start offline this run (by app id): they start online from then on.</summary>
    private readonly HashSet<string> _familyOnlineOnly = new(StringComparer.Ordinal);
    private int _steamBackOnline;

    /// <summary>Steam's row in accounts.list: offline mode on now, and whether it can be (a saved sign-in).</summary>
    private static (bool Offline, bool CanOffline) SteamModeNow(string steamRoot)
    {
        var (account, offline) = SteamLoginUsers.CurrentOffline(steamRoot);
        return (offline, account?.Remembered == true);
    }

    /// <summary>Settings' Offline mode switch.</summary>
    private Task<object> SteamSetOfflineAsync(JsonElement p, bool hasParams)
    {
        if (ReadBool(p, hasParams, "offline") is not { } offline) return Task.FromResult<object>(new { ok = false, message = "Missing offline." });
        return Task.Run<object>(async () =>
        {
            if (SteamInstallation.ResolveRoot() is not { } root) return new { ok = false, message = "Steam is not installed." };
            var now = SteamLoginUsers.CurrentOffline(root).Offline;
            if (offline && now)
            {
                // Offline already (a family game's, an outage's or Steam's own): it stays, now as the player's choice.
                SteamLoginUsers.ChooseOffline();
                return new { ok = true, message = SteamOfflineMode.Words(SteamOfflineMode.Hold.None, offline: true) };
            }
            if (!offline && !now) return new { ok = true, message = SteamOfflineMode.Words(SteamOfflineMode.Hold.None, offline: false) };
            var hold = SteamHold(offline, userAsked: true, root);
            if (hold != SteamOfflineMode.Hold.None)
            {
                AppLog.Info($"Steam: offline mode {(offline ? "on" : "off")} waits ({hold}).");
                return new { ok = false, message = SteamOfflineMode.Words(hold, offline) };
            }
            var (ok, message) = await RestartSteamAsync(offline, _bridgeLifetime.Token, SteamLoginUsers.Chosen, keepClosed: true,
                line => AccountProgress("steam", line)).ConfigureAwait(false);
            return new { ok, message };
        });
    }

    /// <summary>
    /// Before Play on a Steam game, off the UI thread. A family game that plays offline gets Steam offline first; one
    /// that doesn't, or any other Steam game, gets Steam back online first when Exo put it offline for a family game and
    /// no game runs. <c>Switched</c>: this Play turned offline mode on. <c>Note</c>: Play's line once the game runs.
    /// </summary>
    private async Task<(bool Switched, string? Note)> SteamModeForPlayAsync(GameEntry game)
    {
        if (game.Store != StoreKind.Steam || game.LaunchTarget is not { Length: > 0 } appId || SteamInstallation.ResolveRoot() is not { } root)
            return (false, null);
        var offline = SteamLoginUsers.CurrentOffline(root).Offline;
        SteamOfflineMode.Online why = SteamOfflineMode.Online.PlaysOnline;
        if (game.FamilyShared)
        {
            bool refused;
            lock (_familyOnlineOnly) refused = _familyOnlineOnly.Contains(appId);
            why = refused ? SteamOfflineMode.Online.WouldNotStart : SteamOfflineMode.Decide(FamilyAppInfo(root, appId),
                AntiCheatCheck.Cached(game).Label, game.UpdateAvailable);
            AppLog.Info($"Steam: family game {appId} {(why == SteamOfflineMode.Online.Offline ? "plays offline" : "starts online (" + why + ")")}.");
        }
        var wantsOffline = why == SteamOfflineMode.Online.Offline;
        if (offline)
        {
            if (wantsOffline) return (false, PlayingOffline);
            // The player's own offline mode (or Steam's) stays theirs.
            if (SteamLoginUsers.LoadOfflineSwitch()?.Why != SteamLoginUsers.ForFamily) return (false, null);
            // Exo's switch for a family game, with nothing running: this one starts with Steam back online.
            if (!GameRunningSignal.Running && !_services.Launcher.AnyGameRunning && SteamHold(offline: false, userAsked: true, root) == SteamOfflineMode.Hold.None)
            {
                LaunchLine(game, "Steam goes back online…");
                var (back, message) = await RestartSteamAsync(offline: false, _bridgeLifetime.Token, SteamLoginUsers.ForFamily, keepClosed: true).ConfigureAwait(false);
                if (back) return (false, game.FamilyShared ? RunningOnline(SteamOfflineMode.Words(why)) : null);
                AppLog.Info("Steam: stays offline for this Play: " + message);
            }
            // Still offline (a family game runs): Play says so.
            return (false, PlayingOffline);
        }
        if (!game.FamilyShared) return (false, null);
        if (!wantsOffline) return (false, RunningOnline(SteamOfflineMode.Words(why)));
        var hold = SteamHold(offline: true, userAsked: false, root);
        if (hold != SteamOfflineMode.Hold.None)
        {
            AppLog.Info($"Steam: family game {appId} starts online; {hold}.");
            return (false, RunningOnline(SteamOfflineMode.PlayWords(hold)));
        }
        LaunchLine(game, "Steam goes offline for this family game…");
        var (ok, why2) = await RestartSteamAsync(offline: true, _bridgeLifetime.Token, SteamLoginUsers.ForFamily, keepClosed: true).ConfigureAwait(false);
        // Written but slow to sign in still counts: a launch that then fails goes back online (PlayOnlineAfterOfflineAsync).
        if (ok || SteamLoginUsers.LoadOfflineSwitch()?.Why == SteamLoginUsers.ForFamily) return (true, PlayingOffline);
        AppLog.Info("Steam: family game starts online; offline mode didn't start: " + why2);
        return (false, RunningOnline("Steam didn't go offline"));
    }

    private const string PlayingOffline = "Running in Steam's offline mode";

    private static string RunningOnline(string why) => "Running online: " + why;

    /// <summary>
    /// A family game that didn't start with Steam offline (Play turned it on): Steam goes back online and, unless Play
    /// was stopped, the game starts again online. The game starts online from then on this run.
    /// </summary>
    private async Task<LaunchResult> PlayOnlineAfterOfflineAsync(GameEntry game, bool skipDeps, LaunchResult failed)
    {
        // A handoff timeout may mean the player is still reading Steam's agreement, not that offline mode failed.
        if (!SteamOfflineMode.ShouldRetryOnline(failed.Message)) return failed;
        if (game.LaunchTarget is { Length: > 0 } appId) lock (_familyOnlineOnly) _familyOnlineOnly.Add(appId);
        var stopped = failed.Message == "Launch stopped.";
        AppLog.Info($"Steam: {game.Id} didn't start offline ({(stopped ? "stopped" : failed.Message)}); Steam goes back online.");
        LaunchLine(game, stopped ? "Steam goes back online…" : "Steam didn't start it offline. Going back online…");
        var (ok, why) = await Task.Run(() => RestartSteamAsync(offline: false, _bridgeLifetime.Token, SteamLoginUsers.ForFamily, keepClosed: true)).ConfigureAwait(true);
        if (!ok) AppLog.Info("Steam: not back online after the family game: " + why);
        if (stopped || !ok) return failed;
        LaunchLine(game, "Preparing launch…");
        return await _services.Launcher.LaunchAsync(game, skipDeps).ConfigureAwait(true);
    }

    /// <summary>A Steam game closed: Exo's switch for a family game goes back once no game runs (<see cref="SteamBackOnlineAsync"/>).</summary>
    private void SteamOnlineAfterFamily(GameEntry game)
    {
        if (HostMode.IsDisposable) return;
        if (game.Store == StoreKind.Steam && SteamLoginUsers.LoadOfflineSwitch()?.Why == SteamLoginUsers.ForFamily)
            _ = Task.Run(() => SteamBackOnlineAsync(TimeSpan.FromSeconds(15)));
    }

    /// <summary>
    /// Exo's offline switch for a family game undone: after <paramref name="settle"/> (Steam writes its play time as a
    /// game closes), only while no game runs or starts, and only when nothing holds Steam (<see cref="SteamHold"/>);
    /// otherwise the 5-minute Steam check tries again (WebHostBridge.SteamClient).
    /// </summary>
    private async Task SteamBackOnlineAsync(TimeSpan settle)
    {
        if (HostMode.IsDisposable) return;
        if (Interlocked.Exchange(ref _steamBackOnline, 1) == 1) return;
        try
        {
            if (settle > TimeSpan.Zero) await Task.Delay(settle, _bridgeLifetime.Token).ConfigureAwait(false);
            if (SteamLoginUsers.LoadOfflineSwitch()?.Why != SteamLoginUsers.ForFamily || SteamInstallation.ResolveRoot() is not { } root) return;
            if (GameRunningSignal.Running || _services.Launcher.AnyGameRunning || DateTimeOffset.UtcNow - _steamPlayAt < LaunchQuiet) return;
            var hold = SteamHold(offline: false, userAsked: false, root);
            if (hold != SteamOfflineMode.Hold.None)
            {
                AppLog.Info($"Steam: back online after the family game once {hold} ends.");
                return;
            }
            var (ok, message) = await RestartSteamAsync(offline: false, _bridgeLifetime.Token, SteamLoginUsers.ForFamily, keepClosed: true).ConfigureAwait(false);
            AppLog.Info(ok ? "Steam: back online after the family game." : "Steam: not back online yet: " + message);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Steam: back online failed: " + ex.GetType().Name); }
        finally { Volatile.Write(ref _steamBackOnline, 0); }
    }

    /// <summary>What keeps Exo from restarting Steam into <paramref name="offline"/> (or online) now.</summary>
    private SteamOfflineMode.Hold SteamHold(bool offline, bool userAsked, string root)
    {
        var started = SteamOutage.StartedAt();
        var moving = started is { } since && SteamOfflineMode.MovingFiles(ContentLogTail(root), since.AddSeconds(-5));
        var exoJob = _services.Launcher.CurrentProgress is { IsActive: true } job && job.GameId.StartsWith("steam:", StringComparison.OrdinalIgnoreCase);
        return SteamOfflineMode.CanRestart(offline, userAsked, SteamGameRunning(), moving || exoJob,
            SteamLoginUsers.CurrentOffline(root).Account?.Remembered == true, NetworkInterface.GetIsNetworkAvailable(),
            started is not null && StoreWindowHider.VisibleMainStores([StoreKind.Steam]).Contains(StoreKind.Steam));
    }

    /// <summary>
    /// A Steam game runs: Steam's own RunningAppID, or a Steam game of the library Exo sees running (RunningAppID is
    /// Steam's word only while Steam still tracks the game's process).
    /// </summary>
    private static bool SteamGameRunning() =>
        SteamLoginUsers.IsGameRunning() ||
        MainWindow.RunningLibraryGames().Keys.Any(id => id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase));

    private static string? ContentLogTail(string root)
    {
        try { return SteamContentLogProgress.ReadTail(Path.Combine(root, "logs", "content_log.txt"), 96 * 1024); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The game's appinfo record (Steam's local copy of its store entry); null when Steam has none.</summary>
    private static SteamKeyValues? FamilyAppInfo(string root, string appId) =>
        uint.TryParse(appId, out var id) && ReadSharedBytes(Path.Combine(root, "appcache", "appinfo.vdf")) is { } data &&
        SteamAppInfoNames.ReadApps(data, new HashSet<uint> { id }).TryGetValue(id, out var app) ? app : null;

    private void LaunchLine(GameEntry game, string line) =>
        PostEvent("launch.status", new { gameId = game.Id, ok = true, message = line, phase = "preparing" });
}
