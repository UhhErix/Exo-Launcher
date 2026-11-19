using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the window mode on Optimize's line, and Optimize without frame caps, can go wrong (on fixtures in a temp folder):
/// - a mode is written in a spelling or number the game doesn't take, or Modern Warfare 4's two lines disagree;
/// - what only one mode needs stays behind in the other: the second screen's mouse line in true full screen;
/// - switching modes loses what the lines held, so Put back gives the other mode and not the player's own;
/// - a game Exo can't set the mode of (Rocket League, any Unreal game's HDR peak) offers a choice;
/// - the choice isn't remembered per game, or a Put back forgets it, or one game's choice reaches another;
/// - any profile still sets a frame cap, on any card or screen;
/// - a cap written by an earlier version survives a re-apply or a Put back, or a cap the player has picked since is undone;
/// - a Riot game gets a profile, or a look in its folder decides it;
/// - the hold doesn't hold the mode chosen as the game closes, as Exo starts or before Play; or it writes while the game
///   runs, before Optimize was pressed, after Put back or a group turned off; or touches a line beyond the window mode's;
///   or calls it an update; or lines already at the mode read as Applied before a press has armed it.
/// </summary>
public sealed class OptimizeWindowModeTests
{
    private const string NoGame = "exo-test-no-such-game";

    // Modern Warfare 4's own lines and allowed values (s.1.0.cod26.txt, October 2026).
    private const string Mw4 = """
        1

        FocusedMode@0;57752;48403 = false

        ConstrainMouse@1;17649;48403 = false

        DisplayMode@0;22564;43096 = Windowed // one of Windowed, Fullscreen, Fullscreen borderless window, Fullscreen borderless extended window

        PreferredDisplayMode@0;15994;11445 = Fullscreen borderless window // one of Fullscreen, Fullscreen borderless window, Fullscreen borderless extended window

        RefreshRate@0;56178;35888 = Auto:164.917

        CapFps@0;57752;20945 = false

        MaxFpsInGame@0;13305;58861 = 250 // 30 to 300

        MaxFpsOutOfFocus@0;35936;15032 = 60 // 5 to 300

        """;

    private static string NewFile(string name, string text)
    {
        var folder = Path.Combine(TestDataDirectory.Root, "mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, name);
        File.WriteAllText(file, text.ReplaceLineEndings("\r\n"));
        return file;
    }

    /// <summary>Modern Warfare 4's profile on a file of its own, with no player files.</summary>
    private static GameSettings.Profile Mw4Profile(string file, string id, GameSettings.Pc pc) =>
        GameSettings.ModernWarfare4(pc) with { Id = id, File = file, Processes = [NoGame], PlayerFiles = null, Nvidia = null, Program = null };

    private static string? Value(string file, string line)
    {
        var settings = GameSettings.Parse(File.ReadAllBytes(file));
        return settings.Settings.FirstOrDefault(s => s.Name == line)?.Value;
    }

    [Fact]
    public void ModernWarfareTakesEitherModeInTheMenusOwnWordsWithTheMouseLineOnlyWhereItCounts()
    {
        var file = GameSettings.Parse(Encoding.UTF8.GetBytes(Mw4.ReplaceLineEndings("\r\n")));
        var borderless = GameSettings.Targets(file, GameSettings.Display(new("nvidia", 7, 8, Screens: 2), "Auto:164.917"));
        Assert.Equal("Fullscreen borderless window", borderless.Single(t => t.Line.Name == "DisplayMode").Value);
        Assert.Equal("Fullscreen borderless window", borderless.Single(t => t.Line.Name == "PreferredDisplayMode").Value);
        Assert.Contains(borderless, t => t.Line.Name == "ConstrainMouse");

        var fullscreen = GameSettings.Targets(file, GameSettings.Display(new("nvidia", 7, 8, Screens: 2, Fullscreen: true), "Auto:164.917"));
        Assert.Equal("Fullscreen", fullscreen.Single(t => t.Line.Name == "DisplayMode").Value);
        Assert.Equal("Fullscreen", fullscreen.Single(t => t.Line.Name == "PreferredDisplayMode").Value);
        Assert.DoesNotContain(fullscreen, t => t.Line.Name == "ConstrainMouse");
    }

    [Fact]
    public void SwitchingModesTakesBackWhatOnlyTheOtherNeededAndPutBackGivesThePlayersOwn()
    {
        var file = NewFile("s.1.0.cod26.txt", Mw4);
        var before = File.ReadAllText(file);
        var id = "mw4-" + Guid.NewGuid().ToString("N");
        Assert.True(GameSettings.Set(Mw4Profile(file, id, new("nvidia", 7, 8, Screens: 2)), null, true));
        Assert.Equal("Fullscreen borderless window", Value(file, "DisplayMode"));
        Assert.Equal("true", Value(file, "ConstrainMouse"));

        var fullscreen = Mw4Profile(file, id, new("nvidia", 7, 8, Screens: 2, Fullscreen: true));
        Assert.True(GameSettings.Set(fullscreen, null, true));
        Assert.Equal("Fullscreen", Value(file, "DisplayMode"));
        Assert.Equal("Fullscreen", Value(file, "PreferredDisplayMode"));
        Assert.Equal("false", Value(file, "ConstrainMouse"));

        Assert.True(GameSettings.Set(fullscreen, null, false));
        Assert.Equal(before, File.ReadAllText(file));
    }

    [Theory]
    [InlineData(false, "1")]
    [InlineData(true, "0")]
    public void PhantomBladeWritesUnrealsNumbers(bool fullscreen, string mode)
    {
        var group = GameSettings.UnrealWindow(fullscreen);
        Assert.Equal(["FullscreenMode", "LastConfirmedFullscreenMode", "PreferredFullscreenMode"], group.Changes.Select(c => c.Key));
        Assert.All(group.Changes, c => Assert.Equal(mode, c.Value));
        Assert.Equal(fullscreen ? GameSettings.Fullscreen : GameSettings.Borderless, GameSettings.PhantomBladeZero(fullscreen: fullscreen).WindowMode);
    }

    [Theory]
    [InlineData(false, "1", 165)]
    [InlineData(true, "2", 165)]
    [InlineData(true, "2", 0)] // the screen's rate unknown: the mode is still written
    public void ControlWritesItsOwnEnum(bool fullscreen, string mode, int top)
    {
        var settings = GameSettings.ParseControl(Encoding.UTF8.GetBytes("{\n\t\"m_eWindowMode\": 0,\n\t\"m_fDLSSFGTargetFrameRate\": 60.0\n}\n"));
        var profile = GameSettings.ControlResonant(new("nvidia", null, 8, TopRefresh: top, Gpu: "NVIDIA GeForce RTX 3070", Fullscreen: fullscreen));
        var targets = GameSettings.Targets(settings, profile.Groups(settings).Single(g => g.Id == "frames"));
        Assert.Equal(mode, targets.Single(t => t.Line.Name == "m_eWindowMode").Value);
        Assert.Equal(fullscreen ? GameSettings.Fullscreen : GameSettings.Borderless, profile.WindowMode);
    }

    [Fact]
    public void RocketLeagueOffersNoChoice() => Assert.Null(GameSettings.RocketLeague(new("nvidia", 5, 8)).WindowMode);

    [Fact]
    public void TheChoiceIsRememberedPerGameAndBorderlessUntilChosen()
    {
        var folder = Path.Combine(TestDataDirectory.Root, "install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "ProjectPBZ"));
        var game = new GameEntry { Id = "steam:4115450", Title = "Phantom Blade Zero", Store = StoreKind.Steam, Installed = true, Path = folder };
        var pc = new GameSettings.Pc("nvidia", 5, 8);
        var profile = GameSettings.For(game, pc)!;
        Assert.Equal(GameSettings.Borderless, profile.WindowMode);
        try
        {
            Assert.True(GameSettings.ChooseWindowMode(profile, GameSettings.Fullscreen));
            var chosen = GameSettings.For(game, pc)!;
            Assert.Equal(GameSettings.Fullscreen, chosen.WindowMode);
            Assert.Equal("0", chosen.Groups(GameSettings.ParsePhantomBlade([])).Single().Changes[0].Value);
            // Another game's stays its own.
            var cod = Path.Combine(TestDataDirectory.Root, "install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(cod, "cod26"));
            Assert.Equal(GameSettings.Borderless, GameSettings.For(new GameEntry { Id = "steam:1938090", Title = "Call of Duty", Store = StoreKind.Steam, Installed = true, Path = cod }, pc)!.WindowMode);
        }
        finally { GameSettings.ChooseWindowMode(profile, GameSettings.Borderless); }
    }

    [Theory]
    [InlineData("nvidia", 240)]
    [InlineData("amd", 165)]
    [InlineData("intel", 144)]
    [InlineData(null, 360)]
    public void NoProfileSetsAFrameCapOnAnyCardOrScreen(string? vendor, int top)
    {
        var pc = new GameSettings.Pc(vendor, 7, 16, 2, top, Gpu: vendor == "nvidia" ? "NVIDIA GeForce RTX 3070" : null);
        var mw4 = GameSettings.Parse(Encoding.UTF8.GetBytes(Mw4.ReplaceLineEndings("\r\n")));
        var keys = GameSettings.CallOfDutyGroups(pc, mw4)
            .Concat(GameSettings.RocketLeagueGroups(pc))
            .Concat(GameSettings.ControlResonant(pc).Groups(GameSettings.ParseControl("{}"u8.ToArray())))
            .Concat(GameSettings.PhantomBladeZero(1000).Groups(GameSettings.ParsePhantomBlade([])))
            .Concat(GameSettings.HellIsUs(pc, frameGeneration: true).Groups(GameSettings.ParseHellIsUs([])))
            .SelectMany(group => group.Changes).Select(change => change.Key).ToList();
        Assert.DoesNotContain(keys, key => key is "CapFps" or "MaxFpsInGame" or "MaxFpsOutOfFocus" or "CustomFPS" or "m_maxFrameRate" or "FrameRateLimit");
        Assert.Empty(GameSettings.RocketLeagueNvidia.Values(GameSettings.ParseRocketLeague([])));
    }

    /// <summary>An Optimize as earlier versions pressed it, with their cap; the profile now as it stands.</summary>
    private static (string File, GameSettings.Profile Now) CappedBefore()
    {
        var file = NewFile("s.1.0.cod26.txt", Mw4);
        var id = "mw4-" + Guid.NewGuid().ToString("N");
        var now = Mw4Profile(file, id, new("amd", 7, 8));
        var before = now with
        {
            Groups = settings => [.. now.Groups(settings), new("pacing", "Frames held just under your refresh",
                [new("CapFps", "true"), new("MaxFpsInGame", "162"), new("MaxFpsOutOfFocus", "30")])],
        };
        Assert.True(GameSettings.Set(before, null, true));
        Assert.Equal("162", Value(file, "MaxFpsInGame"));
        return (file, now);
    }

    [Fact]
    public void AReApplyTakesBackACapAnEarlierVersionSet()
    {
        var (file, now) = CappedBefore();
        Assert.True(GameSettings.Set(now, null, true));
        Assert.Equal(("false", "250", "60"), (Value(file, "CapFps"), Value(file, "MaxFpsInGame"), Value(file, "MaxFpsOutOfFocus")));
        Assert.DoesNotContain(GameSettings.Kept(now.Id).Keys, key => key.StartsWith("MaxFps", StringComparison.Ordinal) || key.StartsWith("CapFps", StringComparison.Ordinal));
        // Done: another press changes nothing.
        var text = File.ReadAllText(file);
        Assert.True(GameSettings.Set(now, null, true));
        Assert.Equal(text, File.ReadAllText(file));
    }

    [Fact]
    public void APutBackTakesBackACapAnEarlierVersionSet()
    {
        var (file, now) = CappedBefore();
        Assert.True(GameSettings.Set(now, null, false));
        Assert.Equal(Mw4.ReplaceLineEndings("\r\n"), File.ReadAllText(file));
    }

    [Fact]
    public void ACapThePlayerPickedSinceStaysTheirs()
    {
        var (file, now) = CappedBefore();
        File.WriteAllText(file, File.ReadAllText(file).Replace("MaxFpsInGame@0;13305;58861 = 162", "MaxFpsInGame@0;13305;58861 = 144", StringComparison.Ordinal));
        Assert.True(GameSettings.Set(now, null, true));
        Assert.Equal("144", Value(file, "MaxFpsInGame"));
        Assert.Equal("false", Value(file, "CapFps"));
    }

    [Fact]
    public void WithoutAPressACapStaysWhereItIs()
    {
        var (file, now) = CappedBefore();
        var text = File.ReadAllText(file);
        GameSettings.KeepOn(now, null, afterSession: true);
        GameSettings.KeepOn(now, null, afterSession: false);
        Assert.Equal(text, File.ReadAllText(file));
    }

    /// <summary>The game moves its window mode and an effect back, as a game of Modern Warfare 4 might.</summary>
    private static void GameMoves(string file) => File.WriteAllText(file, File.ReadAllText(file)
        .Replace("DisplayMode@0;22564;43096 = Fullscreen borderless window", "DisplayMode@0;22564;43096 = Windowed", StringComparison.Ordinal)
        .Replace("FocusedMode@0;57752;48403 = false", "FocusedMode@0;57752;48403 = true", StringComparison.Ordinal));

    private static (string File, GameSettings.Profile Profile) Optimized(GameSettings.Pc? pc = null)
    {
        var file = NewFile("s.1.0.cod26.txt", Mw4);
        var profile = Mw4Profile(file, "mw4-" + Guid.NewGuid().ToString("N"), pc ?? new("nvidia", 7, 8));
        Assert.True(GameSettings.Set(profile, null, true));
        return (file, profile);
    }

    [Fact]
    public void BeforePlayOnlyTheWindowModeLinesGoBack()
    {
        var (file, profile) = Optimized();
        GameMoves(file);
        Assert.True(GameSettings.Hold(profile, "before launch"));
        Assert.Equal("Fullscreen borderless window", Value(file, "DisplayMode"));
        Assert.Equal("true", Value(file, "FocusedMode")); // the game's other line stays as it wrote it
        Assert.False(GameSettings.Again(profile));
        Assert.False(GameSettings.Hold(profile, "before launch")); // nothing left to do, nothing written
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AsItClosesAndAsExoStartsTheWindowModeGoesBack(bool afterSession)
    {
        var (file, profile) = Optimized();
        for (var time = 0; time < 2; time++)
        {
            GameMoves(file);
            GameSettings.KeepOn(profile, null, afterSession);
            Assert.Equal("Fullscreen borderless window", Value(file, "DisplayMode"));
        }
    }

    [Fact]
    public void TheModeHeldIsTheOneChosen()
    {
        var (file, profile) = Optimized(new("nvidia", 7, 8, Fullscreen: true));
        Assert.Equal("Fullscreen", Value(file, "DisplayMode"));
        File.WriteAllText(file, File.ReadAllText(file).Replace("DisplayMode@0;22564;43096 = Fullscreen //", "DisplayMode@0;22564;43096 = Fullscreen borderless window //", StringComparison.Ordinal));
        Assert.True(GameSettings.Hold(profile, "before launch"));
        Assert.Equal("Fullscreen", Value(file, "DisplayMode"));
    }

    [Fact]
    public void NothingIsWrittenWhileTheGameRuns()
    {
        // The press is made before the game "starts"; it is the hold and the look that run while it does.
        var (file, profile) = Optimized();
        GameMoves(file);
        var moved = File.ReadAllText(file);
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var running = profile with { Processes = [self.ProcessName] };
        Assert.False(GameSettings.Hold(running, "before launch"));
        GameSettings.KeepOn(running, null, afterSession: true);
        Assert.Equal(moved, File.ReadAllText(file));
    }

    [Fact]
    public void PutBackAndAGroupTurnedOffStopIt()
    {
        var (file, profile) = Optimized();
        Assert.True(GameSettings.Set(profile, null, false));
        var back = File.ReadAllText(file);
        Assert.False(GameSettings.Hold(profile, "before launch"));
        GameSettings.KeepOn(profile, null, afterSession: true);
        Assert.Equal(back, File.ReadAllText(file));

        var (other, on) = Optimized();
        Assert.True(GameSettings.Set(on, "display", false));
        var off = File.ReadAllText(other);
        Assert.False(GameSettings.Hold(on, "before launch"));
        Assert.Equal(off, File.ReadAllText(other));
    }

    [Fact]
    public void NothingIsHeldBeforeAPressNorForAGameWithoutTheLines()
    {
        var file = NewFile("s.1.0.cod26.txt", Mw4);
        var text = File.ReadAllText(file);
        Assert.False(GameSettings.Hold(Mw4Profile(file, "mw4-" + Guid.NewGuid().ToString("N"), new("nvidia", 7, 8)), "before launch"));
        Assert.Equal(text, File.ReadAllText(file));
        Assert.Null(GameSettings.RocketLeague(new("nvidia", 5, 8)).WindowLines);
        Assert.False(GameSettings.Hold(GameSettings.RocketLeague(new("nvidia", 5, 8)), "before launch"));
    }

    [Fact]
    public void WindowModeLinesAlreadyInPlaceArentAppliedUntilAPressHoldsThem()
    {
        var bytes = "[/Script/ProjectPBZ.PBZSettingsLocal]\r\nFullscreenMode=1\r\nLastConfirmedFullscreenMode=1\r\nPreferredFullscreenMode=1\r\n"u8.ToArray();
        var settings = GameSettings.ParsePhantomBlade(bytes);
        var profile = GameSettings.PhantomBladeZero();
        Assert.False(GameSettings.States(profile, settings, new Dictionary<string, GameSettings.Remembered>(), []).Single().On);
        Assert.True(GameSettings.States(profile, settings, new Dictionary<string, GameSettings.Remembered>(), ["display"]).Single().On);
        // A game with no window mode lines keeps the old rule.
        var rocket = GameSettings.ParseRocketLeague("[SystemSettings]\r\nMaxAnisotropy=16\r\n"u8.ToArray());
        Assert.True(GameSettings.States(GameSettings.RocketLeague(new("nvidia", 5, 8)), rocket, new Dictionary<string, GameSettings.Remembered>(), [])
            .Single(state => state.Id == "filtering").On);
    }

    [Fact]
    public void ARiotGameHasNoProfileAndItsFolderDecidesNothing()
    {
        var cod = Path.Combine(TestDataDirectory.Root, "riot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(cod, "cod26"));
        foreach (var product in new[] { "valorant", "league_of_legends", "teamfighttactics", "lion", "bacon" })
            Assert.Null(GameSettings.For(new GameEntry { Id = "riot:" + product, Title = product, Store = StoreKind.Riot, Installed = true, Path = cod, LaunchTarget = product },
                new("nvidia", null, 8, HdrPeak: 1000)));
    }
}
