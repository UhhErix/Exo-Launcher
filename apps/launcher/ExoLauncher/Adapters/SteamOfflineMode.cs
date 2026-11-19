using System.Globalization;
using System.Text.RegularExpressions;
using ExoLauncher.Services;

namespace ExoLauncher.Adapters;

/// <summary>
/// When Exo puts Steam in its own offline mode (<see cref="SteamLoginUsers.WriteOffline"/>) and when it may restart
/// Steam for it. A game borrowed from the account's Steam Family starts with Steam offline when it plays offline, judged
/// from Steam's own record of it (appinfo): single-player or play on one screen, not an MMO, nothing Steam says needs
/// the internet for single-player, and no anti-cheat by Steam's marks or Exo's. A game Steam can't tell Exo about, or
/// with an update waiting (Steam updates only online), starts online as before. Steam restarts only while no Steam game
/// runs, it moves no files (a download or update in progress), and, going offline, it has a saved sign-in.
/// </summary>
internal static partial class SteamOfflineMode
{
    internal static bool ShouldRetryOnline(string message) => message != SteamProcessPath.LaunchNeedsAttention;

    /// <summary>Why a family game starts with Steam online; <see cref="Offline"/> when it starts offline.</summary>
    internal enum Online { Offline, UpdateWaiting, NoDetails, PlaysOnline, NeedsInternet, AntiCheat, WouldNotStart }

    /// <summary>What keeps Exo from restarting Steam now; <see cref="None"/> when nothing does.</summary>
    internal enum Hold { None, GameRunning, MovingFiles, NoSavedSignIn, NoInternet, SteamOnScreen }

    // Steam's store categories (appinfo common/category/category_N): 2 Single-player; 24 Shared/Split Screen, 37 Shared/Split
    // Screen PvP, 39 Shared/Split Screen Co-op (play on this PC); 20 MMO.
    private const string SinglePlayer = "category_2";
    private static readonly string[] OneScreen = ["category_24", "category_37", "category_39"];
    private const string Mmo = "category_20";

    /// <summary>
    /// Whether a borrowed game starts in offline mode. <paramref name="app"/>: its appinfo record (null when Steam has
    /// none). <paramref name="antiCheat"/>: what Exo found by the game's title or files (Valve Anti-Cheat doesn't count:
    /// it never stops a game offline).
    /// </summary>
    internal static Online Decide(SteamKeyValues? app, string? antiCheat, bool updateWaiting)
    {
        if (updateWaiting) return Online.UpdateWaiting;
        if (app is null || app.Keys.Count == 0) return Online.NoDetails;
        var common = app.Section("common");
        var category = common.Section("category");
        if (category.Keys.Count == 0) return Online.NoDetails;
        if (category.Value(Mmo) is not null) return Online.PlaysOnline;
        if (category.Value(SinglePlayer) is null && !OneScreen.Any(c => category.Value(c) is not null)) return Online.PlaysOnline;
        if (common.Section("steam_deck_compatibility/configuration").Value("requires_internet_for_singleplayer") == "1") return Online.NeedsInternet;
        // Valve Anti-Cheat, certain or possible, never stops a game offline.
        if (SteamMarksAntiCheat(app) || AntiCheatCheck.Split(antiCheat).Any(name => name is not (AntiCheatCheck.ValveAntiCheat or AntiCheatCheck.PossiblyVacName))) return Online.AntiCheat;
        return Online.Offline;
    }

    /// <summary>
    /// Steam's own word that a game runs anti-cheat: its Steam Deck and SteamOS reviews (a test result such as
    /// <c>#SteamDeckVerified_TestResult_UnsupportedAntiCheatConfiguration</c>) or the anti-cheat's support page it lists.
    /// </summary>
    internal static bool SteamMarksAntiCheat(SteamKeyValues app)
    {
        if (!string.IsNullOrWhiteSpace(app.Section("extended").Value("anti_cheat_support_url"))) return true;
        var deck = app.Section("common/steam_deck_compatibility");
        foreach (var list in new[] { "tests", "steamos_tests", "steam_machine_tests" })
            foreach (var (_, test) in deck.Section(list).Sections)
                if (test.Value("token")?.Contains("AntiCheat", StringComparison.OrdinalIgnoreCase) == true) return true;
        return false;
    }

    /// <summary>
    /// What keeps Exo from restarting Steam now. Going offline needs a saved sign-in and waits out a download; going
    /// online needs the internet. A restart Exo starts by itself (<paramref name="userAsked"/> false) also leaves a Steam
    /// open on screen alone.
    /// </summary>
    internal static Hold CanRestart(bool offline, bool userAsked, bool steamGameRunning, bool movingFiles, bool savedSignIn, bool internet, bool steamOnScreen)
    {
        if (steamGameRunning) return Hold.GameRunning;
        if (offline && !savedSignIn) return Hold.NoSavedSignIn;
        if (offline && movingFiles) return Hold.MovingFiles;
        if (!offline && !internet) return Hold.NoInternet;
        if (!userAsked && steamOnScreen) return Hold.SteamOnScreen;
        return Hold.None;
    }

    /// <summary>The hold as Settings says it.</summary>
    internal static string Words(Hold hold, bool offline) => hold switch
    {
        Hold.GameRunning => "A Steam game is running. Close it first.",
        Hold.MovingFiles => "Steam is downloading. Try again once it's done.",
        Hold.NoSavedSignIn => "Steam plays offline only with a saved sign-in. Sign in to Steam with Remember me on.",
        Hold.NoInternet => "There's no internet connection, so Steam stays offline.",
        Hold.SteamOnScreen => "Steam is open. Close its window first.",
        _ => offline ? "Steam is in offline mode." : "Steam is online.",
    };

    /// <summary>Play's line for a family game that starts with Steam online, after "Running online: ".</summary>
    internal static string Words(Online why) => why switch
    {
        Online.UpdateWaiting => "it has an update to get",
        Online.NoDetails => "Steam has no details for it yet",
        Online.PlaysOnline => "it plays online",
        Online.NeedsInternet => "it needs the internet",
        Online.AntiCheat => "its anti-cheat needs Steam online",
        Online.WouldNotStart => "Steam didn't start it offline",
        _ => "",
    };

    /// <summary>Play's line for a family game held online by Steam's state, after "Running online: ".</summary>
    internal static string PlayWords(Hold hold) => hold switch
    {
        Hold.GameRunning => "another Steam game is running",
        Hold.MovingFiles => "Steam is downloading",
        Hold.NoSavedSignIn => "Steam has no saved sign-in",
        Hold.SteamOnScreen => "Steam is open",
        _ => "",
    };

    /// <summary>
    /// Whether Steam is moving files now, from its content log since it started (<paramref name="since"/>): an app whose
    /// last state change says Update Running. A queued, paused or finished update says nothing of the kind.
    /// </summary>
    internal static bool MovingFiles(string? contentLog, DateTime since)
    {
        if (string.IsNullOrEmpty(contentLog)) return false;
        var last = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match line in StateLine().Matches(contentLog))
        {
            if (!DateTime.TryParseExact(line.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) || at < since)
                continue;
            last[line.Groups[2].Value] = line.Groups[3].Value;
        }
        return last.Values.Any(state => state.Contains("Update Running", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] AppID (\d+) state changed : (.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StateLine();
}
