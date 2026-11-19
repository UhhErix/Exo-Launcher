using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Steam's offline mode on Exo's terms could go wrong, silently:
/// - loginusers.vdf (this PC's own file, its account name, persona, id and time replaced): a byte besides the two values
///   changes; the put-back leaves a "1" behind or a record and copy; turning off offline mode Steam had of its own touches
///   SkipOfflineModeWarning or anything else, or another account; a record written before reasons reads as something
///   other than an outage; a weaker reason (a family game) replaces the player's own choice, so Steam goes back online
///   under them, or a second switch overwrites the first values;
/// - which family games start offline: an online-only game (no single-player, an MMO, needing the internet, anti-cheat
///   by Steam's marks or Exo's) starts offline and fails; a single-player or one-screen game stays online for nothing;
///   Valve Anti-Cheat (which never stops a game offline) keeps a game online; a game with an update waiting starts
///   offline and can't get it; a game Steam has no record of is guessed at;
/// - the restart rules: Steam restarts under a running Steam game, mid-download going offline, into offline mode with no
///   saved sign-in, or online with no internet; a restart Exo starts by itself closes Steam open on the player's screen;
/// - Steam's content log: a queued, paused or finished update reads as moving files (offline mode then never comes), a
///   running one reads as idle, or a line from an earlier Steam run counts.
/// Runs with SteamOfflineTests, one at a time: both write Exo's offline record in the run's data folder.
/// </summary>
[CollectionDefinition("SteamOfflineRecord", DisableParallelization = true)]
public sealed class SteamOfflineRecordCollection;

[Collection("SteamOfflineRecord")]
public sealed class SteamOfflineModeTests
{
    private const string Id = "76561197960265729";

    private static string RealFile() =>
        File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "Steam", "loginusers.vdf")).ReplaceLineEndings("\n");

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void OnThisPcsFileOfflineFlipsOnlyTheTwoValues(string newline)
    {
        var original = RealFile().ReplaceLineEndings(newline);
        var (text, before) = SteamLoginUsers.SetOffline(original, Id)!.Value;
        Assert.Equal(new SteamLoginUsers.OfflineFlags("0", "0"), before);
        Assert.Equal(original.Length, text.Length);
        Assert.Equal(2, Enumerable.Range(0, text.Length).Count(i => text[i] != original[i]));
        Assert.Equal(new SteamLoginUsers.OfflineFlags("1", "1"), SteamLoginUsers.ReadOffline(text, Id));
        Assert.Equal(original, SteamLoginUsers.RestoreOffline(text, Id, before));
    }

    [Fact]
    public void SteamsOwnOfflineModeGoesWithItsOneValue()
    {
        // Steam's own Go Offline left both at 1; Settings' Offline mode off sets WantsOfflineMode 0 and nothing else.
        var steams = RealFile().Replace("\"WantsOfflineMode\"\t\t\"0\"", "\"WantsOfflineMode\"\t\t\"1\"", StringComparison.Ordinal)
            .Replace("\"SkipOfflineModeWarning\"\t\t\"0\"", "\"SkipOfflineModeWarning\"\t\t\"1\"", StringComparison.Ordinal);
        var online = SteamLoginUsers.SetOnline(steams, Id)!;
        Assert.Equal(new SteamLoginUsers.OfflineFlags("0", "1"), SteamLoginUsers.ReadOffline(online, Id));
        Assert.Equal(1, Enumerable.Range(0, online.Length).Count(i => online[i] != steams[i]));
        Assert.Null(SteamLoginUsers.SetOnline(steams, "76561197960265799"));
        // A file without the line gets none added: Steam reads a missing line as online.
        var older = RealFile().Replace("\t\t\"WantsOfflineMode\"\t\t\"0\"\n", "", StringComparison.Ordinal);
        Assert.Equal(older, SteamLoginUsers.SetOnline(older, Id));
    }

    [Fact]
    public void TheStrongerReasonStands()
    {
        Assert.Equal(SteamLoginUsers.ForOutage, SteamLoginUsers.Stronger(SteamLoginUsers.ForFamily, SteamLoginUsers.ForOutage));
        Assert.Equal(SteamLoginUsers.Chosen, SteamLoginUsers.Stronger(SteamLoginUsers.ForFamily, SteamLoginUsers.Chosen));
        Assert.Equal(SteamLoginUsers.Chosen, SteamLoginUsers.Stronger(SteamLoginUsers.Chosen, SteamLoginUsers.ForFamily));
        Assert.Equal(SteamLoginUsers.Chosen, SteamLoginUsers.Stronger(SteamLoginUsers.Chosen, SteamLoginUsers.ForOutage));
        Assert.Equal(SteamLoginUsers.ForOutage, SteamLoginUsers.Stronger(SteamLoginUsers.ForOutage, SteamLoginUsers.ForFamily));
    }

    [Fact]
    public void ARecordFromBeforeReasonsIsAnOutage()
    {
        var record = System.Text.Json.JsonSerializer.Deserialize<SteamLoginUsers.OfflineSwitch>(
            """{"Account":"abc","WantsOfflineMode":"0","SkipOfflineModeWarning":"0"}""")!;
        Assert.Equal(SteamLoginUsers.ForOutage, record.Why);
    }

    [Fact]
    public void AFamilySwitchOnACopyKeepsThePlayersChoiceAndPutsBackByteForByte()
    {
        using var steam = new SteamCopy(RealFile());
        Assert.Null(SteamLoginUsers.WriteOffline(steam.Root, Id, SteamLoginUsers.ForFamily));
        Assert.Equal(SteamLoginUsers.ForFamily, SteamLoginUsers.LoadOfflineSwitch()!.Why);
        Assert.Equal(new SteamLoginUsers.OfflineFlags("1", "1"), SteamLoginUsers.ReadOffline(steam.Text, Id));

        // The player turns offline mode on in Settings while it stands: theirs now, first values kept.
        SteamLoginUsers.ChooseOffline();
        Assert.Equal(new SteamLoginUsers.OfflineSwitch(SteamLoginUsers.KeyFor(Id), "0", "0", SteamLoginUsers.Chosen), SteamLoginUsers.LoadOfflineSwitch());
        // A later family game doesn't take it back.
        Assert.Null(SteamLoginUsers.WriteOffline(steam.Root, Id, SteamLoginUsers.ForFamily));
        Assert.Equal(SteamLoginUsers.Chosen, SteamLoginUsers.LoadOfflineSwitch()!.Why);

        Assert.True(SteamLoginUsers.WriteOnline(steam.Root));
        Assert.Equal(steam.Original, steam.Bytes);
        Assert.Null(SteamLoginUsers.LoadOfflineSwitch());
    }

    [Fact]
    public void SteamsOwnOfflineModeIsLeftUnlessThePlayerTurnsItOff()
    {
        var steams = RealFile().Replace("\"WantsOfflineMode\"\t\t\"0\"", "\"WantsOfflineMode\"\t\t\"1\"", StringComparison.Ordinal)
            .Replace("\"SkipOfflineModeWarning\"\t\t\"0\"", "\"SkipOfflineModeWarning\"\t\t\"1\"", StringComparison.Ordinal);
        using var steam = new SteamCopy(steams);
        // A family game finds Steam offline already: no record, so Exo never puts it back online.
        Assert.Null(SteamLoginUsers.WriteOffline(steam.Root, Id, SteamLoginUsers.ForFamily));
        Assert.Null(SteamLoginUsers.LoadOfflineSwitch());
        Assert.True(SteamLoginUsers.WriteOnline(steam.Root));
        Assert.Equal(steam.Original, steam.Bytes);
        // Settings' Offline mode off turns it off.
        Assert.True(SteamLoginUsers.WriteOnline(steam.Root, Id));
        Assert.Equal("0", SteamLoginUsers.ReadOffline(steam.Text, Id)!.WantsOfflineMode);
    }

    [Fact]
    public void OffInSettingsEndsOnlineEvenOverSteamsOwnOfflineFromBefore()
    {
        // Steam was offline of its own (WantsOfflineMode 1, the warning still on); an outage switch of Exo's stood over it.
        var steams = RealFile().Replace("\"WantsOfflineMode\"\t\t\"0\"", "\"WantsOfflineMode\"\t\t\"1\"", StringComparison.Ordinal);
        using var steam = new SteamCopy(steams);
        Assert.Null(SteamLoginUsers.WriteOffline(steam.Root, Id, SteamLoginUsers.ForOutage));
        Assert.Equal(new SteamLoginUsers.OfflineFlags("1", "0"), SteamLoginUsers.LoadOfflineSwitch()!.Before);
        // Exo letting go on its own puts Steam's own offline mode back.
        Assert.True(SteamLoginUsers.WriteOnline(steam.Root));
        Assert.Equal(steam.Original, steam.Bytes);
        // The player's Off in Settings ends online.
        Assert.Null(SteamLoginUsers.WriteOffline(steam.Root, Id, SteamLoginUsers.ForOutage));
        Assert.True(SteamLoginUsers.WriteOnline(steam.Root, Id));
        Assert.Null(SteamLoginUsers.LoadOfflineSwitch());
        Assert.Equal(new SteamLoginUsers.OfflineFlags("0", "0"), SteamLoginUsers.ReadOffline(steam.Text, Id));
    }

    // Steam's own records (appinfo.vdf) of real Steam Family games on the developer's PC, cut to what the decision reads.
    private static SteamKeyValues App(string categories, string? deckInternet = "0", string? deckTest = null, string? antiCheatUrl = null)
    {
        var cats = string.Join(' ', categories.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c => $"\"category_{c.Trim()}\" \"1\""));
        var config = deckInternet is null ? "" : $"\"configuration\" {{ \"requires_internet_for_setup\" \"0\" \"requires_internet_for_singleplayer\" \"{deckInternet}\" }}";
        var tests = deckTest is null ? "" : $"\"tests\" {{ \"0\" {{ \"display\" \"2\" \"token\" \"{deckTest}\" }} }}";
        var extended = antiCheatUrl is null ? "" : $"\"extended\" {{ \"anti_cheat_support_url\" \"{antiCheatUrl}\" }}";
        return SteamKeyValues.ParseText($"\"appinfo\" {{ \"common\" {{ \"category\" {{ {cats} }} \"steam_deck_compatibility\" {{ {tests} {config} }} }} {extended} }}")
            .Section("appinfo");
    }

    [Theory]
    // game, its Steam categories, Deck's requires_internet_for_singleplayer, a Deck test result, anti_cheat_support_url, Exo's anti-cheat, verdict
    // Single-player, nothing online: offline.
    [InlineData("007 First Light", "2,22,23,28,45,46,57,58,62,65,67,68,78", "0", null, null, null, "Offline")]
    [InlineData("The Witcher 3", "2,18,22,23,29,30,42,43,45,46,57,58,61,62,64", "0", null, null, null, "Offline")]
    // Valve Anti-Cheat never stops a game offline; L4D2's campaign plays alone.
    [InlineData("Left 4 Dead 2", "1,2,8,9,12,13,14,15,16,22,23,28,29,30,33,36,38", "0", null, null, "Valve Anti-Cheat", "Offline")]
    // No single-player, but play on one screen.
    [InlineData("Keep Talking and Nobody Explodes", "1,9,15,22,24,25,28,30,31,39,43,44,52,53,62", "0", null, null, null, "Offline")]
    // No Deck review at all: the categories decide.
    [InlineData("Civilization IV: Colonization", "1,2,62", null, null, null, null, "Offline")]
    // Single-player listed, BattlEye per Steam's Deck review (always online).
    [InlineData("Destiny 2", "1,2,9,18,22,33,35,36,38,45,46,49,55,57,62", "0", "#SteamDeckVerified_TestResult_UnsupportedAntiCheatConfiguration", null, null, "AntiCheat")]
    // Single-player, BattlEye's support page in its record.
    [InlineData("Unturned", "1,2,8,9,15,17,22,23,29,30,35,36,38", "0", null, "https://www.battleye.com/support/", null, "AntiCheat")]
    // Single-player per the store, anti-cheat by Exo's own title and file markers.
    [InlineData("a game with Easy Anti-Cheat", "2,22,23,28", "0", null, null, "Easy Anti-Cheat", "AntiCheat")]
    // Multiplayer only.
    [InlineData("PUBG: BATTLEGROUNDS", "1,15,18,36,41,42,49,62,68,69,70", "0", "#SteamDeckVerified_TestResult_UnsupportedAntiCheat_Other", null, null, "PlaysOnline")]
    [InlineData("ARC Raiders", "1,9,15,22,27,28,35,36,38,45,46,49,55,56,57,58,62", "0", null, null, null, "PlaysOnline")]
    // An MMO, single-player listed or not.
    [InlineData("Echo Of Soul", "1,9,20,36,38,49,62", "0", null, null, null, "PlaysOnline")]
    [InlineData("an MMO that lists single-player", "1,2,20,36", "0", null, null, null, "PlaysOnline")]
    // Steam says single-player needs the internet.
    [InlineData("Killing Floor 2", "1,2,8,9,17,22,28,29,30,33,35,36,38,42,43,45,46,49,62", "1", null, null, null, "NeedsInternet")]
    public void WhichFamilyGamesStartOffline(string game, string categories, string? deckInternet, string? deckTest, string? antiCheatUrl, string? antiCheat, string expected)
    {
        _ = game;
        Assert.Equal(Enum.Parse<SteamOfflineMode.Online>(expected),
            SteamOfflineMode.Decide(App(categories, deckInternet, deckTest, antiCheatUrl), antiCheat, updateWaiting: false));
    }

    [Fact]
    public void WhatSteamCantSayStartsOnline()
    {
        Assert.Equal(SteamOfflineMode.Online.UpdateWaiting, SteamOfflineMode.Decide(App("2"), null, updateWaiting: true));
        Assert.Equal(SteamOfflineMode.Online.NoDetails, SteamOfflineMode.Decide(null, null, updateWaiting: false));
        Assert.Equal(SteamOfflineMode.Online.NoDetails, SteamOfflineMode.Decide(SteamKeyValues.Empty, null, updateWaiting: false));
        Assert.Equal(SteamOfflineMode.Online.NoDetails, SteamOfflineMode.Decide(App(""), null, updateWaiting: false));
        Assert.Equal("it plays online", SteamOfflineMode.Words(SteamOfflineMode.Online.PlaysOnline));
    }

    [Theory]
    // offline, userAsked, game, moving, savedSignIn, internet, onScreen, hold
    [InlineData(true, true, true, false, true, true, false, "GameRunning")]
    [InlineData(false, true, true, false, true, true, false, "GameRunning")]
    [InlineData(true, true, false, false, false, true, false, "NoSavedSignIn")]
    [InlineData(true, false, false, true, true, true, false, "MovingFiles")]
    [InlineData(false, false, false, true, true, true, false, "None")]           // offline Steam moves nothing: online again is fine
    [InlineData(false, true, false, false, true, false, false, "NoInternet")]
    [InlineData(true, true, false, false, true, false, false, "None")]           // offline mode needs no internet
    [InlineData(true, false, false, false, true, true, true, "SteamOnScreen")]   // Exo by itself leaves an open Steam alone
    [InlineData(true, true, false, false, true, true, true, "None")]             // the player asked: Steam's window goes
    [InlineData(false, false, false, false, false, true, false, "None")]         // online needs no saved sign-in
    public void WhenSteamMayRestart(bool offline, bool userAsked, bool game, bool moving, bool savedSignIn, bool internet, bool onScreen, string hold) =>
        Assert.Equal(Enum.Parse<SteamOfflineMode.Hold>(hold), SteamOfflineMode.CanRestart(offline, userAsked, game, moving, savedSignIn, internet, onScreen));

    [Fact]
    public void SettingsSaysWhy()
    {
        Assert.Contains("saved sign-in", SteamOfflineMode.Words(SteamOfflineMode.Hold.NoSavedSignIn, offline: true), StringComparison.Ordinal);
        Assert.Equal("Steam is in offline mode.", SteamOfflineMode.Words(SteamOfflineMode.Hold.None, offline: true));
        Assert.Equal("Steam is online.", SteamOfflineMode.Words(SteamOfflineMode.Hold.None, offline: false));
        Assert.Equal("Steam is downloading", SteamOfflineMode.PlayWords(SteamOfflineMode.Hold.MovingFiles));
    }

    // Steam's content_log.txt on the developer's PC: an update Steam ran for itself, then this Steam's start.
    private const string ContentLog = """
        [2026-10-28 21:13:43] AppID 228980 scheduler update : Priority Auto Update, not played for 1793240023 seconds, update disabled for 0 seconds
        [2026-10-28 21:13:43] AppID 228980 state changed : Update Required,Fully Installed,Update Queued,
        [2026-10-28 21:13:43] AppID 228980 state changed : Update Required,Fully Installed,Update Queued,Update Running,
        [2026-10-28 21:13:43] AppID 228980 App update changed : Running Update,
        [2026-10-28 21:13:43] AppID 228980 state changed : Update Required,Fully Installed,Update Queued,Update Running,Update Started,
        [2026-10-28 21:13:43] AppID 228980 App update changed : Running Update,Committing,
        [2026-10-28 21:13:43] AppID 228980 state changed : Fully Installed,Update Queued,Update Running,
        [2026-10-28 21:13:43] AppID 228980 finished update, 7 mounted depots (BuildID 24098430)
        [2026-10-28 21:13:43] AppID 228980 state changed : Fully Installed,Update Queued,
        [2026-10-28 21:13:43] AppID 228980 state changed : Fully Installed,
        [2026-10-28 21:22:38] Client version: 1793235415
        [2026-10-28 21:23:26] AppID 4115450 state changed : Fully Installed,App Running,
        """;

    private static readonly DateTime SteamStarted = new(2026, 10, 28, 21, 13, 0);

    [Fact]
    public void SteamMovesFilesOnlyWhileAnUpdateRuns()
    {
        var running = ContentLog[..ContentLog.IndexOf("[2026-10-28 21:13:43] AppID 228980 finished", StringComparison.Ordinal)];
        Assert.True(SteamOfflineMode.MovingFiles(running, SteamStarted));
        Assert.False(SteamOfflineMode.MovingFiles(ContentLog, SteamStarted));
        // Queued only, and paused: not moving.
        var queued = ContentLog[..ContentLog.IndexOf("[2026-10-28 21:13:43] AppID 228980 state changed : Update Required,Fully Installed,Update Queued,Update Running,", StringComparison.Ordinal)];
        Assert.False(SteamOfflineMode.MovingFiles(queued, SteamStarted));
        Assert.False(SteamOfflineMode.MovingFiles("[2026-10-28 22:00:00] AppID 570 state changed : Update Required,Update Paused,\n", SteamStarted));
        // A run cut off mid-update by an earlier Steam: this Steam started after it.
        Assert.False(SteamOfflineMode.MovingFiles(running, new DateTime(2026, 10, 28, 21, 22, 30)));
        // A game running is not a transfer, and CRLF lines read the same.
        Assert.False(SteamOfflineMode.MovingFiles(ContentLog.ReplaceLineEndings("\r\n"), SteamStarted));
        Assert.True(SteamOfflineMode.MovingFiles(running.ReplaceLineEndings("\r\n"), SteamStarted));
        Assert.False(SteamOfflineMode.MovingFiles(null, SteamStarted));
    }

    /// <summary>A Steam folder in the test data directory holding a loginusers.vdf; Exo's record and copy go with it.</summary>
    private sealed class SteamCopy : IDisposable
    {
        internal string Root { get; } = Path.Combine(TestDataDirectory.Root, "steam-mode-" + Guid.NewGuid().ToString("N"));
        internal byte[] Original { get; }

        internal SteamCopy(string text)
        {
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            Original = System.Text.Encoding.UTF8.GetBytes(text);
            File.WriteAllBytes(SteamLoginUsers.PathFor(Root), Original);
        }

        internal byte[] Bytes => File.ReadAllBytes(SteamLoginUsers.PathFor(Root));
        internal string Text => File.ReadAllText(SteamLoginUsers.PathFor(Root));

        public void Dispose()
        {
            DeletionGate.DeleteFile(Path.Combine(PathHelper.AppDataDir, "steam-offline.json"));
            DeletionGate.DeleteFile(Path.Combine(PathHelper.AppDataDir, "steam-offline-loginusers.vdf"));
            DeletionGate.DeleteDirectory(Root, recursive: true);
        }
    }
}
