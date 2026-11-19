using System.Globalization;
using ExoLauncher.Adapters;
using ExoLauncher.Adapters.Cli;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Steam Input for games that aren't Steam's (Properties > Steam Input). A game that reads Xbox controllers only gets a
/// PlayStation controller through Steam Input when Steam starts it, so Exo adds a Steam shortcut that starts the game's
/// own program (<see cref="SteamShortcuts"/>) with Steam Input on for it, written with the overlay switch while Steam is
/// closed, and Play starts it through Steam (steam://rungameid). Offered for games Exo starts from their own program
/// (local games, GOG games, Epic games Legendary installed), once a PlayStation controller has been on this PC, unless
/// the game's Steam store page says it reads one itself; never for a game with anti-cheat, since Steam's overlay goes
/// into what Steam starts. Turning it off takes out only what Exo added; a shortcut the player changed or deleted in
/// Steam is theirs, and Exo's choice for that game goes off.
/// </summary>
public sealed partial class WebHostBridge
{
    private const int SonyVendor = 0x054C;

    /// <summary>What the game's Steam shortcut starts; null where Exo can't start the game from its own program.</summary>
    private SteamShortcuts.Wanted? ShortcutFor(GameEntry game)
    {
        if (!game.Installed || !SteamShortcutsAllowed(game)) return null;
        var settings = _services.Settings;
        var options = settings.LaunchArgs(game.Id).Trim();
        settings.Current.LaunchOverrides.TryGetValue(game.Id, out var launch);
        var exe = game.LaunchTarget ?? game.Path;
        switch (game.Store)
        {
            case StoreKind.Local or StoreKind.Gog when IsProgram(exe):
                var folder = launch?.WorkingDirectory is { Length: > 0 } wd && Directory.Exists(wd) ? wd : Path.GetDirectoryName(exe)!;
                return new(game.Id, game.Title, exe!, folder, options, exe);
            case StoreKind.Epic when LegendaryProperties.IsAppName(game.LaunchTarget) && EpicAdapter.ResolveLegendary() is { } legendary
                && EpicAdapter.ReadLegendaryInstalledJson().Any(row => string.Equals(row.AppName, game.LaunchTarget, StringComparison.OrdinalIgnoreCase)):
                // Legendary starts the game signed in (Exo's own Epic sign-in, in Legendary's folder), as Play does for a game it installed.
                var args = LegendaryCli.LaunchArgs(game.LaunchTarget!, options).Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
                return new(game.Id, game.Title, legendary, Path.GetDirectoryName(legendary)!, string.Join(' ', args));
            default:
                return null;
        }
    }

    private static bool IsProgram(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) && !path.Contains('"');

    /// <summary>
    /// Not Steam's, not Riot's, no anti-cheat by store or title: the cheap half of <see cref="AntiCheatCheck"/>. The install
    /// itself is checked when the player turns it on and again at each Play through Steam.
    /// </summary>
    private static bool SteamShortcutsAllowed(GameEntry game) =>
        game.Store is not (StoreKind.Steam or StoreKind.Riot) && AntiCheatCheck.Quick(game) is null;

    /// <summary>Exo's record: game id to the shortcut it wrote, and the games whose Steam Input the player took over in Steam.</summary>
    private (Dictionary<string, SteamShortcuts.Kept> Record, HashSet<string> PlayersInput) ShortcutRecord()
    {
        var record = new Dictionary<string, SteamShortcuts.Kept>(StringComparer.OrdinalIgnoreCase);
        var players = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gameId, value) in _services.Settings.Current.SteamShortcuts)
        {
            if (value.TrimEnd('!').Split(':') is not [var id, var stamp]
                || !uint.TryParse(id, CultureInfo.InvariantCulture, out var appId) || !uint.TryParse(stamp, CultureInfo.InvariantCulture, out var s)) continue;
            record[gameId] = new SteamShortcuts.Kept(appId, s);
            if (value.EndsWith('!')) players.Add(gameId);
        }
        return (record, players);
    }

    /// <summary>The shortcuts to have now: the games sent through Steam that are installed and still start from their own program.</summary>
    private List<SteamShortcuts.Wanted> ShortcutsWanted()
    {
        var chosen = _services.Settings.Current.ThroughSteam;
        if (chosen.Count == 0) return [];
        return [.. chosen.Select(id => _services.Library.Find(id)).OfType<GameEntry>().Select(ShortcutFor).OfType<SteamShortcuts.Wanted>()];
    }

    /// <summary>Steam Input on for each of Exo's shortcuts whose Steam Input the player hasn't taken over in Steam.</summary>
    private static Dictionary<string, string> ShortcutInputChoices(IReadOnlyDictionary<string, SteamShortcuts.Kept> record, IReadOnlySet<string> playersInput)
    {
        var choices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gameId, kept) in record)
            if (!playersInput.Contains(gameId)) choices[kept.AppId.ToString(CultureInfo.InvariantCulture)] = "steam";
        return choices;
    }

    /// <summary>
    /// Keeps what Steam has now: the shortcuts' record, Exo's choice off for the games whose shortcut the player took over,
    /// and the shortcuts whose Steam Input the player changed in Steam (Steam Input's plan dropped them).
    /// </summary>
    private void SaveShortcuts(Dictionary<string, SteamShortcuts.Kept> record, IReadOnlyCollection<string> dropped,
        IReadOnlySet<string> playersInput, IReadOnlyDictionary<string, string> inputChoices)
    {
        var appIds = record.ToDictionary(r => r.Value.AppId.ToString(CultureInfo.InvariantCulture), r => r.Key, StringComparer.Ordinal);
        var players = new HashSet<string>(playersInput, StringComparer.OrdinalIgnoreCase);
        foreach (var (appId, gameId) in appIds)
            if (!players.Contains(gameId) && !inputChoices.ContainsKey(appId)) players.Add(gameId);
        var values = record.ToDictionary(
            r => r.Key,
            r => $"{r.Value.AppId.ToString(CultureInfo.InvariantCulture)}:{r.Value.Stamp.ToString(CultureInfo.InvariantCulture)}{(players.Contains(r.Key) ? "!" : "")}",
            StringComparer.OrdinalIgnoreCase);
        var s = _services.Settings.Current;
        if (dropped.Count > 0) AppLog.Info($"Steam shortcuts: {dropped.Count} changed or deleted in Steam; Exo's Steam Input for those games is off.");
        if (dropped.Count > 0 || values.Count != s.SteamShortcuts.Count || values.Any(v => s.SteamShortcuts.GetValueOrDefault(v.Key) != v.Value))
            _services.Settings.UpdateProfile(x =>
            {
                x.SteamShortcuts = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
                x.ThroughSteam.RemoveAll(id => dropped.Contains(id, StringComparer.OrdinalIgnoreCase));
            });
    }

    /// <summary>shortcuts.vdf beside the account's localconfig.vdf.</summary>
    private static string ShortcutsPath(string config) => Path.Combine(Path.GetDirectoryName(config)!, "shortcuts.vdf");

    /// <summary>The shortcuts' plan on the file as it is; null when the file isn't one Exo can read (then it is never written).</summary>
    private static (byte[]? File, Dictionary<string, SteamShortcuts.Kept> Record, List<string> Dropped)? PlanShortcuts(
        string config, IReadOnlyList<SteamShortcuts.Wanted> wanted, IReadOnlyDictionary<string, SteamShortcuts.Kept> record)
    {
        if (wanted.Count == 0 && record.Count == 0) return null;
        try { return SteamShortcuts.Plan(ReadSharedBytes(ShortcutsPath(config)), wanted, record); }
        catch (Exception ex) when (ex is InvalidDataException or UserFacingException)
        {
            AppLog.Warn("Steam shortcuts: shortcuts.vdf not changed: " + ex.Message);
            return null;
        }
    }

    private static void WriteShortcuts(string config, byte[] file)
    {
        var path = ShortcutsPath(config);
        var tmp = path + ".exo-tmp";
        File.WriteAllBytes(tmp, file);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>A PlayStation controller is on this PC, or has been while Exo ran (one Exo calibrated or set up counts).</summary>
    private bool PlayStationPadKnown()
    {
        if (_services.Settings.Current.PlayStationPadSeen) return true;
        var saved = Path.Combine(PathHelper.AppDataDir, "controllers");
        // Each DualSense's or Edge's own file there (games.json is every controller's per-game profiles).
        var here = (Directory.Exists(saved) && Directory.EnumerateFiles(saved, "*.json").Any(f => !Path.GetFileName(f).Equals("games.json", StringComparison.OrdinalIgnoreCase)))
            || Peripherals.List(() => _services.Launcher.AnyGameRunning || MainWindow.RunningLibraryGames().Count > 0)
                .Any(card => card.Kind == "controller" && card.VendorId == SonyVendor);
        if (here) _services.Settings.UpdateProfile(s => s.PlayStationPadSeen = true);
        return here;
    }

    /// <summary>
    /// The game's Steam Input switch for Properties; null where it isn't offered. A game already sent through Steam keeps
    /// its switch, so it can be turned off whatever changed.
    /// </summary>
    private bool? ThroughSteamFor(GameEntry game)
    {
        var on = _services.Settings.Current.ThroughSteam.Contains(game.Id, StringComparer.OrdinalIgnoreCase);
        if (on) return true;
        if (SteamInstallation.ResolveRoot() is not { } root || SteamPlaytime.TryGetLocalConfigPath(root) is null) return null;
        if (ShortcutFor(game) is null || _dlss.GetStatus(game, refresh: false, consented: null, _bridgeLifetime.Token).AntiCheat is not null) return null;
        // Its Steam store page lists PlayStation controller support: the game reads the controller itself.
        if (CoverArtService.ExtractSteamAppIdFromUrl(game.CoverUrl) is { } steamId && InstalledAppInfo(root, [steamId]).Values.Any(SteamInputConfig.ReadsPlayStation))
            return null;
        return PlayStationPadKnown() ? false : null;
    }

    /// <summary>Properties > Steam Input on or off: the shortcut goes in or out with Steam's next close (now, when Steam is closed).</summary>
    private async Task<string?> ApplyThroughSteamAsync(GameEntry game, string value, CancellationToken ct)
    {
        if (value is not ("on" or "off")) throw new UserFacingException("On or off?");
        var on = value == "on";
        if (on)
        {
            if (ShortcutFor(game) is null) throw new UserFacingException("Exo can't start this game through Steam.");
            var answer = await Task.Run(() => _dlss.AntiCheatAnswer(game, ct), ct).ConfigureAwait(true);
            if (!AntiCheatCheck.Allows(game, answer, AntiCheatCheck.Change.StartThroughSteam))
                throw new UserFacingException(answer.Kind == AntiCheatCheck.Kind.Unknown
                    ? "Exo could not check this game's folder for anti-cheat, so it doesn't start it through Steam."
                    : $"{game.Title} uses {answer.Name}: Exo never starts it through Steam.");
        }
        _services.Settings.UpdateProfile(s =>
        {
            s.ThroughSteam.RemoveAll(id => string.Equals(id, game.Id, StringComparison.OrdinalIgnoreCase));
            if (on) s.ThroughSteam.Add(game.Id);
        });
        return await ApplySteamOverlayAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Play for a game sent through Steam whose shortcut Steam has: Steam starts it (signed in first, started hidden when
    /// it's closed). Null for every other game, and for this one until Steam has its shortcut: Play starts it as always.
    /// </summary>
    private async Task<object?> PlayThroughSteamAsync(GameEntry game)
    {
        if (!_services.Settings.Current.ThroughSteam.Contains(game.Id, StringComparer.OrdinalIgnoreCase) || !SteamShortcutsAllowed(game)) return null;
        // Anti-cheat added since it was turned on: Play starts the game the usual way.
        var answer = await Task.Run(() => AntiCheatCheck.Cached(game)).ConfigureAwait(true);
        if (!AntiCheatCheck.Allows(game, answer, AntiCheatCheck.Change.StartThroughSteam))
        {
            AppLog.Info($"Through Steam skipped: gameId={game.Id}; anti-cheat {answer.Name ?? "unknown"} ({answer.Evidence}).");
            return null;
        }
        var (record, _) = ShortcutRecord();
        if (!record.TryGetValue(game.Id, out var kept) || SteamInstallation.ResolveRoot() is not { } root) return null;
        PostEvent("launch.status", new { gameId = game.Id, ok = true, message = "Starting through Steam…", phase = "preparing" });
        if (!await EnsureSteamSignedInAsync(_bridgeLifetime.Token).ConfigureAwait(true))
        {
            var failed = new LaunchResult { Ok = false, Message = "Steam did not start. Open Steam, then try again." };
            PostEvent("launch.status", new { gameId = game.Id, ok = false, message = failed.Message, phase = "failed" });
            return MapDepAwareResult(failed);
        }
        using (ProcessHelper.StartHiddenCli(Path.Combine(root, "steam.exe"), $"steam://rungameid/{SteamShortcuts.GameId(kept.AppId).ToString(CultureInfo.InvariantCulture)}")) { }
        AppLog.Info($"Launch: gameId={game.Id}; through Steam Input (shortcut {kept.AppId.ToString(CultureInfo.InvariantCulture)}).");
        var result = new LaunchResult { Ok = true, Message = "Steam is starting it.", HandoffOnly = true, BackendStarted = "steam" };
        PostEvent("launch.status", new { gameId = game.Id, ok = true, message = result.Message, backendStarted = "steam", handoffOnly = true, phase = "handoff" });
        return MapDepAwareResult(result);
    }
}
