using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways keeping Optimize on across game updates can go wrong:
/// - an update's reset is missed: Rocket League's rebuilt file (its [IniVersion] changed) or MW4's files after an update
///   with every line Exo set back at once stay Not optimized;
/// - the player's own choice is taken for an update: a line changed in the game's menu is put back once and, when the same
///   value comes back, is the player's for good (the cause rules, KeepRulesTests, say the rest);
/// - a group turned off in Exo, or an Optimize put back, comes back on;
/// - it writes while the game (or its anti-cheat launcher) runs, or a look as Exo starts, before the game has run its
///   update, uses the update up so the reset in the session after it is missed;
/// - what a line held before Exo is lost, so Put back after a re-apply gives the update's default, not the player's own;
/// - [IniVersion] or anything else the game wrote is changed;
/// - a player who optimized on an earlier version (only the lines kept) loses it, or gets a write on the first look;
/// - the page isn't told: no "again" for the page, no event for the notice, or "again" outlives the next session.
/// </summary>
public sealed class OptimizeStaysOnTests
{
    private static readonly DateTime Season1 = new(2026, 10, 14, 18, 34, 38, DateTimeKind.Utc);
    private static readonly DateTime Season2 = new(2026, 11, 18, 17, 0, 0, DateTimeKind.Utc);
    private const string NoGame = "exo-test-no-such-game";

    private static string NewFolder()
    {
        var folder = Path.Combine(TestDataDirectory.Root, "keep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Program(string folder, string name)
    {
        var program = Path.Combine(folder, name);
        File.WriteAllBytes(program, []);
        File.SetLastWriteTimeUtc(program, Season1);
        return program;
    }

    /// <summary>The game rewrites a file as it closes: its text with these values in place of those.</summary>
    private static void Game(string path, params (string From, string To)[] values)
    {
        var text = File.ReadAllText(path);
        foreach (var (from, to) in values)
        {
            Assert.Contains(from, text, StringComparison.Ordinal);
            text = text.Replace(from, to, StringComparison.Ordinal);
        }
        File.WriteAllText(path, text);
    }

    private static GameSettings.Keeping? Keeping(GameSettings.Profile profile) => GameSettings.KeepingFor(profile.Id);

    // ------------------------------------------------------------------ Modern Warfare 4: no mark, so its program and every line at once

    private const string Mw4Main = """
        1


        //
        // Display

        VSync@0;9203;8071 = 100% // one of disabled, 100%, 50%, 33%, 25%

        NvidiaReflex@0;7717;12207 = Disabled // one of Disabled, Enabled, Enabled + boost

        MotionBlur@0;11212;9876 = On // one of Off, On, Cinematic Only

        TextureFilter@0;63279;8284 = aniso 2x // one of aniso 2x, aniso 4x, aniso 8x, aniso 16x

        //
        // Audio

        MusicVolume@0;14317;21371 = 1.000000 // 0.000000 to 1.000000

        CinematicVolume@0;14317;21372 = 1.000000 // 0.000000 to 1.000000

        SkipSeasonIntroVideo@0;57752;48403 = false

        """;

    private const string Mw4Player = """
        1


        // 1st Person Field of View
        Fov@1;65095;45350 = 90.000000 // 60.000000 to 120.000000

        MusicVolume@0;54363;21371 = 1.000000 // 0.000000 to 1.000000

        """;

    /// <summary>MW4's PC file and one account's, in a folder of their own, with cod.exe as season 1 left it; optimized.</summary>
    private static (GameSettings.Profile Profile, string Main, string Player, string Program) Mw4Optimized(string process = NoGame)
    {
        var folder = NewFolder();
        var main = Path.Combine(folder, "s.1.0.cod26.txt");
        File.WriteAllText(main, Mw4Main.ReplaceLineEndings("\r\n"));
        var player = Path.Combine(Directory.CreateDirectory(Path.Combine(folder, "76561198000000001")).FullName, "g.cod26.1.0.l.txt");
        File.WriteAllText(player, Mw4Player.ReplaceLineEndings("\r\n"));
        var program = Program(folder, "cod.exe");
        var id = Guid.NewGuid().ToString("N");
        var profile = GameSettings.ModernWarfare4(new("nvidia", 5, 8)) with
        {
            Id = "mw4-" + id, Title = "MW4 " + id, File = main, PlayerFiles = () => [player], Processes = [process],
        };
        Assert.True(GameSettings.Set(profile, null, true, program));
        Assert.Contains("VSync@0;9203;8071 = disabled", File.ReadAllText(main), StringComparison.Ordinal);
        Assert.Contains("Fov@1;65095;45350 = 120.000000", File.ReadAllText(player), StringComparison.Ordinal);
        return (profile, main, player, program);
    }

    /// <summary>MW4's own recommended values, as an update that resets its settings writes them (the files before Optimize).</summary>
    private static void Mw4Reset(string main, string? player = null)
    {
        File.WriteAllText(main, Mw4Main.ReplaceLineEndings("\r\n"));
        if (player is not null) File.WriteAllText(player, Mw4Player.ReplaceLineEndings("\r\n"));
    }

    private static bool Applied(GameSettings.Profile profile) =>
        GameSettings.States(profile, GameSettings.ReadAll(profile), GameSettings.Kept(profile.Id)).TrueForAll(state => state.On);

    [Fact]
    public void AnMw4UpdateThatResetsEveryLineIsWrittenAgainAsTheGameCloses()
    {
        var (profile, main, player, program) = Mw4Optimized();
        var announced = new List<string>();
        void Heard(string title) { if (title == profile.Title) lock (announced) announced.Add(title); }
        GameSettings.AppliedAgain += Heard;
        try
        {
            // Season 2 rewrites cod.exe; its first session puts back the recommended values in both files.
            File.SetLastWriteTimeUtc(program, Season2);
            Mw4Reset(main, player);
            Assert.False(Applied(profile));

            Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
            Assert.True(Applied(profile));
            Assert.True(GameSettings.Again(profile));
            Assert.Equal(new[] { profile.Title }, announced);
        }
        finally { GameSettings.AppliedAgain -= Heard; }

        // What each line held before Exo is still what Put back returns, not the update's.
        Assert.Equal(new GameSettings.Remembered("100%", "disabled"), GameSettings.Kept(profile.Id)["VSync@0;9203;8071"]);
        Assert.True(GameSettings.Set(profile, null, false, program));
        Assert.Equal(Mw4Main.ReplaceLineEndings("\r\n"), File.ReadAllText(main));
        Assert.Null(Keeping(profile));
    }

    [Fact]
    public void OnlyTheFileTheUpdateResetIsWritten()
    {
        var (profile, main, player, program) = Mw4Optimized();
        var before = File.ReadAllBytes(player);
        File.SetLastWriteTimeUtc(program, Season2);
        Mw4Reset(main);
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.True(Applied(profile));
        Assert.Equal(before, File.ReadAllBytes(player));
    }

    [Fact]
    public void APresetPickedInTheGameAfterAnUpdateIsPutBackBecauseTheUpdateIsTheEvidence()
    {
        var (profile, main, _, program) = Mw4Optimized();
        File.SetLastWriteTimeUtc(program, Season2);
        // The menu's Extreme preset: the graphics lines change, the audio and intro lines don't. The game's build changed since Exo
        // last held them, so by the cause rules this is a reset: every moved line goes back, however few, worked out from the file as it is.
        Game(main, ("VSync@0;9203;8071 = disabled", "VSync@0;9203;8071 = 100%"), ("MotionBlur@0;11212;9876 = Off", "MotionBlur@0;11212;9876 = On"),
            ("TextureFilter@0;63279;8284 = aniso 16x", "TextureFilter@0;63279;8284 = aniso 8x"), ("NvidiaReflex@0;7717;12207 = Enabled", "NvidiaReflex@0;7717;12207 = Enabled + boost"));

        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.True(Applied(profile));
        Assert.True(GameSettings.Again(profile));
        Assert.Empty(Keeping(profile)!.Theirs);
    }

    [Fact]
    public void ALineChangedInTheGameTwiceToTheSameValueStaysThePlayersThroughAnUpdate()
    {
        var (profile, main, player, program) = Mw4Optimized();
        // The first time Exo cannot tell the player from the game's quiet drift: it puts the line back once and remembers what it found.
        Game(player, ("Fov@1;65095;45350 = 120.000000", "Fov@1;65095;45350 = 105.000000"));
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Contains("Fov@1;65095;45350 = 120.000000", File.ReadAllText(player), StringComparison.Ordinal);
        // The same value again is the player's: left, and kept out of every later write.
        Game(player, ("Fov@1;65095;45350 = 120.000000", "Fov@1;65095;45350 = 105.000000"));
        Assert.False(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Contains("76561198000000001/g.cod26.1.0.l.txt|Fov@1;65095;45350", Keeping(profile)!.Theirs);

        File.SetLastWriteTimeUtc(program, Season2);
        Mw4Reset(main, player);
        Game(player, ("Fov@1;65095;45350 = 90.000000", "Fov@1;65095;45350 = 105.000000"));
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        // The PC's file is Exo's again. The field of view stays what the player chose, never Exo's 120 over it.
        Assert.Contains("VSync@0;9203;8071 = disabled", File.ReadAllText(main), StringComparison.Ordinal);
        Assert.Contains("Fov@1;65095;45350 = 105.000000", File.ReadAllText(player), StringComparison.Ordinal);
    }

    /// <summary>The file with the line named <paramref name="name"/> taken out, as an update that drops or renames a key does.</summary>
    private static void DropLine(string path, string name)
    {
        var kept = File.ReadAllText(path).Split("\r\n").Where(line => !line.StartsWith(name + "@", StringComparison.Ordinal));
        File.WriteAllText(path, string.Join("\r\n", kept));
    }

    /// <summary>An update between looks: the game's program has a new write time, which is reset evidence.</summary>
    private static void Update(string program, int session) => File.SetLastWriteTimeUtc(program, Season2.AddDays(session));

    // Exo's value for VSync and the game's own, as each session's update writes it back.
    private const string VsyncOff = "VSync@0;9203;8071 = disabled", VsyncReset = "VSync@0;9203;8071 = 100%";

    [Fact]
    public void AYieldedLineStaysThePlayersAfterItsGroupFailedClosed()
    {
        var (profile, main, _, program) = Mw4Optimized();
        // The main file's music volume: put back once, then the same value again is the player's.
        Game(main, ("MusicVolume@0;14317;21371 = 0.000000", "MusicVolume@0;14317;21371 = 0.500000"));
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Contains("MusicVolume@0;14317;21371 = 0.000000", File.ReadAllText(main), StringComparison.Ordinal);
        Game(main, ("MusicVolume@0;14317;21371 = 0.000000", "MusicVolume@0;14317;21371 = 0.500000"));
        Assert.False(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Equal(KeepState.Yielded, Keep.Load().Games[profile.Id].Entries["MusicVolume@0;14317;21371"].State);

        // An update, and the file no longer holds another line of the same group: the group fails closed.
        File.SetLastWriteTimeUtc(program, Season2);
        DropLine(main, "CinematicVolume");
        Assert.False(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Contains("MusicVolume@0;14317;21371 = 0.500000", File.ReadAllText(main), StringComparison.Ordinal);
        Assert.Equal(KeepState.Yielded, Keep.Load().Games[profile.Id].Entries["MusicVolume@0;14317;21371"].State);

        // A session with no update at all: the player's value is still theirs.
        Assert.False(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Contains("MusicVolume@0;14317;21371 = 0.500000", File.ReadAllText(main), StringComparison.Ordinal);
        Assert.Equal(KeepState.Yielded, Keep.Load().Games[profile.Id].Entries["MusicVolume@0;14317;21371"].State);
    }

    [Fact]
    public void TheSameLinePutBackThreeSessionsRunningIsRefused()
    {
        var (profile, main, _, program) = Mw4Optimized();
        for (var session = 1; session <= 4; session++)
        {
            Update(program, session);
            Game(main, (VsyncOff, VsyncReset));
            var wrote = GameSettings.KeepOn(profile, program, afterSession: true);
            var text = File.ReadAllText(main);
            if (session <= 3)
            {
                Assert.True(wrote, $"session {session} wrote the line back");
                Assert.Contains(VsyncOff, text, StringComparison.Ordinal);
            }
            else
            {
                Assert.False(wrote, "session 4 does not fight it any more");
                Assert.Contains(VsyncReset, text, StringComparison.Ordinal);
            }
        }
        Assert.Equal(KeepState.Refused, Keep.Load().Games[profile.Id].Entries["VSync@0;9203;8071"].State);
        Assert.Equal(KeepStatus.Game.WontKeepSome, KeepStatus.Of(profile.Id));
    }

    [Fact]
    public void ALineResetForTheFirstTimeIsPutBackEvenWhenAnotherLineWasRefused()
    {
        var (profile, main, _, program) = Mw4Optimized();
        for (var session = 1; session <= 4; session++)
        {
            Update(program, session);
            Game(main, (VsyncOff, VsyncReset));
            GameSettings.KeepOn(profile, program, afterSession: true);
        }
        Assert.Equal(KeepState.Refused, Keep.Load().Games[profile.Id].Entries["VSync@0;9203;8071"].State);

        // An update resets a line never put back before: four sessions of another line's back-off say nothing about this one.
        Update(program, 5);
        Game(main, ("MotionBlur@0;11212;9876 = Off", "MotionBlur@0;11212;9876 = On"));
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Contains("MotionBlur@0;11212;9876 = Off", File.ReadAllText(main), StringComparison.Ordinal);
        Assert.Equal(KeepState.PutBack, Keep.Load().Games[profile.Id].Entries["MotionBlur@0;11212;9876"].State);
    }

    [Fact]
    public void ASessionWithoutAResetEndsTheStreak()
    {
        var (profile, main, _, program) = Mw4Optimized();
        for (var session = 1; session <= 5; session++)
        {
            Update(program, session);
            // Sessions 1, 2, 4 and 5 reset one line; session 3 only judged a drift of another, and a session that is not a
            // reset ends the streak (the game-wide counter used to keep it, and refused the line at session 5).
            if (session == 3) Game(main, ("MotionBlur@0;11212;9876 = Off", "MotionBlur@0;11212;9876 = On"));
            else Game(main, (VsyncOff, VsyncReset));
            GameSettings.KeepOn(profile, program, afterSession: true);
            var entry = Keep.Load().Games[profile.Id].Entries["VSync@0;9203;8071"];
            Assert.Contains(VsyncOff, File.ReadAllText(main), StringComparison.Ordinal);
            Assert.NotEqual(KeepState.Refused, entry.State);
            // Sessions 1 and 2 are two running; session 3 is not a reset and ends the streak; 4 and 5 start it over.
            int[] running = [1, 2, 0, 1, 2];
            Assert.Equal(running[session - 1], entry.ResetStreak);
        }
    }

    [Fact]
    public void ALineThePlayerKeepsChangingIsLeftAloneAfterTheSecondChange()
    {
        var (profile, main, player, program) = Mw4Optimized();
        // The game changes the field of view to 105: no evidence, so Exo puts it back once and remembers what it found.
        Game(player, ("Fov@1;65095;45350 = 120.000000", "Fov@1;65095;45350 = 105.000000"));
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Contains("Fov@1;65095;45350 = 120.000000", File.ReadAllText(player), StringComparison.Ordinal);
        // A second change, to another value: the player's, whatever the value, so the file keeps it.
        Game(player, ("Fov@1;65095;45350 = 120.000000", "Fov@1;65095;45350 = 100.000000"));
        Assert.False(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Contains("Fov@1;65095;45350 = 100.000000", File.ReadAllText(player), StringComparison.Ordinal);
        Assert.Equal(KeepState.Yielded, Keep.Load().Games[profile.Id].Entries["76561198000000001/g.cod26.1.0.l.txt|Fov@1;65095;45350"].State);
        // A third change is left too.
        Game(player, ("Fov@1;65095;45350 = 100.000000", "Fov@1;65095;45350 = 90.000000"));
        Assert.False(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Contains("Fov@1;65095;45350 = 90.000000", File.ReadAllText(player), StringComparison.Ordinal);
    }

    [Fact]
    public void AGroupTurnedOffInExoStaysOff()
    {
        var (profile, main, _, program) = Mw4Optimized();
        Assert.True(GameSettings.Set(profile, "audio", false, program));
        Assert.DoesNotContain("audio", Keeping(profile)!.Groups);
        File.SetLastWriteTimeUtc(program, Season2);
        Mw4Reset(main);
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        var text = File.ReadAllText(main);
        Assert.Contains("VSync@0;9203;8071 = disabled", text, StringComparison.Ordinal);
        Assert.Contains("MusicVolume@0;14317;21371 = 1.000000", text, StringComparison.Ordinal);
        Assert.Contains("CinematicVolume@0;14317;21372 = 1.000000", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PutBackForgetsIt()
    {
        var (profile, main, player, program) = Mw4Optimized();
        Assert.True(GameSettings.Set(profile, null, false, program));
        File.SetLastWriteTimeUtc(program, Season2);
        Mw4Reset(main, player);
        Assert.False(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Equal(Mw4Main.ReplaceLineEndings("\r\n"), File.ReadAllText(main));
    }

    [Fact]
    public void NothingIsWrittenWhileTheGameRuns()
    {
        // The test's own process stands in for the game's: the press is made before it "starts", and the look is what runs while it does.
        using var self = Process.GetCurrentProcess();
        var (profile, main, _, program) = Mw4Optimized();
        var running = profile with { Processes = [self.ProcessName] };
        var keeping = JsonSerializer.Serialize(Keeping(profile));
        File.SetLastWriteTimeUtc(program, Season2);
        Mw4Reset(main);
        Assert.False(GameSettings.KeepOn(running, program, afterSession: true));
        Assert.Equal(Mw4Main.ReplaceLineEndings("\r\n"), File.ReadAllText(main));
        // Nothing seen either: the next look, once it's closed, still finds the update.
        Assert.Equal(keeping, JsonSerializer.Serialize(Keeping(profile)));
        Assert.True(GameSettings.KeepOn(profile with { Processes = [NoGame] }, program, afterSession: true));
    }

    [Fact]
    public void ALookAsExoStartsBeforeTheGameRanItsUpdateKeepsTheUpdateForTheLookAfter()
    {
        var (profile, main, _, program) = Mw4Optimized();
        File.SetLastWriteTimeUtc(program, Season2);
        // Exo starts after the update downloaded; the game hasn't run it yet, so nothing is reset.
        Assert.False(GameSettings.KeepOn(profile, program, afterSession: false));
        Assert.Equal(Season1, Keeping(profile)!.Built);
        // Its first session resets everything; the look as it closes finds the update still new.
        Mw4Reset(main);
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Equal(Season2, Keeping(profile)!.Built);
    }

    [Fact]
    public void ExosStartWritesAResetFromASessionItDidntSee()
    {
        var (profile, main, _, program) = Mw4Optimized();
        // Updated and played while Exo was closed.
        File.SetLastWriteTimeUtc(program, Season2);
        Mw4Reset(main);
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: false));
        Assert.True(Applied(profile));
        Assert.True(GameSettings.Again(profile));
    }

    [Fact]
    public void AgainLastsUntilTheGameRunsAgain()
    {
        var (profile, main, _, program) = Mw4Optimized();
        File.SetLastWriteTimeUtc(program, Season2);
        Mw4Reset(main);
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        // Exo restarting doesn't end it.
        Assert.False(GameSettings.KeepOn(profile, program, afterSession: false));
        Assert.True(GameSettings.Again(profile));
        // The next session does.
        Assert.False(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.False(GameSettings.Again(profile));
        // So does pressing Optimize.
        Mw4Reset(main);
        File.SetLastWriteTimeUtc(program, Season2.AddDays(30));
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.True(GameSettings.Set(profile, null, true, program));
        Assert.False(GameSettings.Again(profile));
    }

    [Fact]
    public void AnOptimizeFromBeforeIsKeptOnWithoutAWrite()
    {
        var (profile, main, _, program) = Mw4Optimized();
        // 3.17.0 and 3.17.1 kept only the lines: what Optimize keeps on was never recorded.
        Keep.Update(doc => doc.Games[profile.Id].Keeping = null);
        Game(main, ("MusicVolume@0;14317;21371 = 0.000000", "MusicVolume@0;14317;21371 = 0.500000"));
        var text = File.ReadAllText(main);
        Assert.False(GameSettings.KeepOn(profile, program, afterSession: false));
        Assert.Equal(text, File.ReadAllText(main));
        var keeping = Keeping(profile)!;
        Assert.Contains("audio", keeping.Groups);
        Assert.Contains("latency", keeping.Groups);
        // A line that is not Exo's as this begins is the player's: nothing says otherwise.
        Assert.Equal(new[] { "MusicVolume@0;14317;21371" }, keeping.Theirs);
        Assert.Equal(Season1, keeping.Built);
    }

    [Fact]
    public void NeverOptimizedIsNeverWritten()
    {
        var folder = NewFolder();
        var main = Path.Combine(folder, "s.1.0.cod26.txt");
        File.WriteAllText(main, Mw4Main);
        var program = Program(folder, "cod.exe");
        var profile = GameSettings.ModernWarfare4(new("nvidia", 5, 8)) with { Id = "mw4-" + Guid.NewGuid().ToString("N"), File = main, PlayerFiles = null, Processes = [NoGame] };
        File.SetLastWriteTimeUtc(program, Season2);
        Assert.False(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Equal(Mw4Main, File.ReadAllText(main));
        Assert.Null(Keeping(profile));
    }

    [Fact]
    public void NothingIsWrittenWhileAHelperRuns()
    {
        // The test's own process stands in for the launcher's helper: it runs while the game does not.
        using var self = Process.GetCurrentProcess();
        var (profile, main, _, program) = Mw4Optimized();
        var withHelper = profile with { Helpers = [self.ProcessName] };
        Game(main, (VsyncOff, VsyncReset));
        var changed = File.ReadAllText(main);

        // A press and a look both say no, and the file stays as the game left it.
        Assert.False(GameSettings.Set(withHelper, null, true, program));
        Assert.Equal(changed, File.ReadAllText(main));
        Assert.False(GameSettings.KeepOn(withHelper, program, afterSession: true));
        Assert.Equal(changed, File.ReadAllText(main));
        // What Optimize keeps on is as it was: the press was refused, not half done.
        Assert.Contains("latency", Keeping(profile)!.Groups);

        // The helper gone: the look puts the line back, once.
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Contains(VsyncOff, File.ReadAllText(main), StringComparison.Ordinal);
    }

    [Fact]
    public void AGameThatStartsWhileTheLookReadsIsNotWrittenInto()
    {
        var (profile, main, _, program) = Mw4Optimized();
        Game(main, (VsyncOff, VsyncReset));
        var changed = File.ReadAllText(main);
        var before = JsonSerializer.Serialize(Keep.Load().Games[profile.Id].Entries);
        var previous = GameSettings.HelperPaths;
        try
        {
            // Nothing runs as the look starts; the launcher's helper starts once the look has read the file and worked out its facts.
            var started = false;
            GameSettings.HelperPaths = _ => started ? ["C:\\anywhere\\bootstrapper.exe"] : [];
            var starting = profile with
            {
                Helpers = ["bootstrapper"],
                Facts = () => { started = true; return new Dictionary<string, string>(); },
            };
            Assert.False(GameSettings.KeepOn(starting, program, afterSession: true));
        }
        finally { GameSettings.HelperPaths = previous; }

        Assert.Equal(changed, File.ReadAllText(main));
        var after = JsonSerializer.Serialize(Keep.Load().Games[profile.Id].Entries);
        Assert.Equal(before, after);
    }

    [Fact]
    public void APressWhileTheGameRunsIsRefusedAndWritesNothing()
    {
        var (profile, main, player, program) = Mw4Optimized();
        var before = (File.ReadAllBytes(main), File.ReadAllBytes(player));
        var record = JsonSerializer.Serialize(Keep.Load().Games[profile.Id]);
        using var self = new KeepKit.RunningGame();
        var running = profile with { Processes = [self.Name] };

        Assert.False(GameSettings.Set(running, null, true, program));
        Assert.False(GameSettings.Set(running, "audio", true, program));
        Assert.False(GameSettings.Set(running, null, false, program));

        // Nothing written, and the record exactly as it was: the press was refused, not half done.
        Assert.Equal([.. before.Item1], File.ReadAllBytes(main));
        Assert.Equal([.. before.Item2], File.ReadAllBytes(player));
        Assert.Equal(record, JsonSerializer.Serialize(Keep.Load().Games[profile.Id]));
    }

    /// <summary>What each entry is judged by: its state, its streak, and the last thing that happened to it (never the time).</summary>
    private static string Verified(GameSettings.Profile profile) => string.Join(" | ", Keep.Load().Games[profile.Id].Entries.OrderBy(pair => pair.Key)
        .Select(pair => $"{pair.Key} {pair.Value.State} {pair.Value.ResetStreak} {pair.Value.Found} {pair.Value.ReadBack} {pair.Value.History[^1]}"));

    [Fact]
    public void ARefusalAtTheLastMomentLeavesStatesStreaksAndSignalsAsTheyWere()
    {
        var (profile, main, _, program) = Mw4Optimized();
        // Two sessions of a reset put back: the entry's own streak is what a back-off counts, and a marker explains the session.
        var settings = profile with { Facts = () => new Dictionary<string, string>() };
        for (var session = 1; session <= 2; session++)
        {
            Update(program, session);
            Game(main, (VsyncOff, VsyncReset));
            Assert.True(GameSettings.KeepOn(settings, program, afterSession: true));
        }
        Assert.Equal(2, Keep.Load().Games[profile.Id].Entries["VSync@0;9203;8071"].ResetStreak);
        GameSettings.NoteMarker(profile.Id);
        Update(program, 3);
        Game(main, (VsyncOff, VsyncReset));
        var before = (verified: Verified(profile), text: File.ReadAllText(main), signals: string.Join(',', Keep.Load().Games[profile.Id].Signals));

        // The game starts again at the look's last check, after its own busy check: the write is refused at the very moment
        // it is about to be made (the check directly before the first file is written, in Commit).
        using (GameSettings.ForceBusyBeforeWriteForTest())
            Assert.False(GameSettings.KeepOn(settings, program, afterSession: true));

        // Nothing was written, and the record is as it was: no state moves, no streak changes, the marker is not used up.
        Assert.Equal(before.text, File.ReadAllText(main));
        Assert.Equal(before.verified, Verified(profile));
        Assert.Equal(before.signals, string.Join(',', Keep.Load().Games[profile.Id].Signals));

        // Free again: this session's put back counts, so the streak is the three running that is a back off, and the marker
        // that explained the session is used up by the put back that was written.
        Assert.True(GameSettings.KeepOn(settings, program, afterSession: true));
        Assert.Equal(3, Keep.Load().Games[profile.Id].Entries["VSync@0;9203;8071"].ResetStreak);
        Assert.DoesNotContain("marker", Keep.Load().Games[profile.Id].Signals);
    }

    [Fact]
    public void AHelperReachedThroughAShortPathCountsAsRunning()
    {
        var previous = GameSettings.HelperPaths;
        try
        {
            // The install folder's own name has a space, and Windows knows it as PROGRA~1: a launcher reporting its helper's path
            // that way names the same folder as another path, which cannot be compared as the two stand.
            var parent = NewFolder();
            var install = Directory.CreateDirectory(Path.Combine(parent, "Program Files", "Call of Duty")).FullName;
            var main = Path.Combine(install, "s.1.0.cod26.txt");
            File.WriteAllText(main, Mw4Main.ReplaceLineEndings("\r\n"));
            var program = Program(install, "cod.exe");
            var profile = GameSettings.ModernWarfare4(new("nvidia", 5, 8)) with
            {
                Id = "mw4-" + Guid.NewGuid().ToString("N"), Title = "MW4 " + Guid.NewGuid().ToString("N"), File = main, PlayerFiles = null,
                Processes = [NoGame], Helpers = ["bootstrapper"], Install = install,
            };
            Assert.True(GameSettings.Set(profile, null, true, program));
            var changed = File.ReadAllText(main);

            GameSettings.HelperPaths = _ => [Path.Combine(parent, "PROGRA~1", "Call of Duty", "bootstrapper.exe")];
            Assert.True(GameSettings.Busy(profile));
            Assert.False(GameSettings.KeepOn(profile, program, afterSession: true));
            Assert.Equal(changed, File.ReadAllText(main));
        }
        finally { GameSettings.HelperPaths = previous; }
    }

    [Fact]
    public void AHelperOnAnotherDriveWhenTheGamesFolderIsALinkCountsAsRunning()
    {
        // A junction as any user may make one: the game's folder is reached through a link.
        var link = Path.Combine(TestDataDirectory.Root, "games-link-" + Guid.NewGuid().ToString("N"));
        if (!Junction(link, out var real)) { Assert.Skip("Windows didn't make a junction here."); return; }
        var previous = GameSettings.HelperPaths;
        try
        {
            // The install folder is the junction; the helper's executable is reported under the folder it really lives in. The two
            // cannot be compared as they stand, so the helper counts as running rather than Exo risking a write beside it.
            var file = Path.Combine(link, "s.1.0.cod26.txt");
            File.WriteAllText(file, Mw4Main.ReplaceLineEndings("\r\n"));
            var program = Program(link, "cod.exe");
            var profile = GameSettings.ModernWarfare4(new("nvidia", 5, 8)) with
            {
                Id = "mw4-" + Guid.NewGuid().ToString("N"), Title = "MW4 " + Guid.NewGuid().ToString("N"), File = file, PlayerFiles = null,
                Processes = [NoGame], Helpers = ["bootstrapper"], Install = link,
            };
            Assert.True(GameSettings.Set(profile, null, true, program));
            var changed = File.ReadAllText(file);

            GameSettings.HelperPaths = _ => [Path.Combine(real, "bootstrapper.exe")];
            Assert.True(GameSettings.Busy(profile));
            Assert.False(GameSettings.KeepOn(profile, program, afterSession: true));
            Assert.Equal(changed, File.ReadAllText(file));
        }
        finally { GameSettings.HelperPaths = previous; }
    }

    /// <summary>A junction at <paramref name="link"/> to a new folder: true where Windows made one.</summary>
    private static bool Junction(string link, out string target)
    {
        target = Path.Combine(TestDataDirectory.Root, "junction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var made = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        made.WaitForExit();
        return Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null;
    }

    [Fact]
    public void AProgramWithAHelpersNameElsewhereOnThePcDoesNotStopTheLook()
    {
        var previous = GameSettings.HelperPaths;
        try
        {
            // Some other program's bootstrapper sits outside the game's install folder: it is not the launcher's helper.
            var (profile, main, _, program) = Mw4Optimized();
            Game(main, (VsyncOff, VsyncReset));
            var withInstall = profile with { Helpers = ["bootstrapper"], Install = Path.GetDirectoryName(main) };
            GameSettings.HelperPaths = _ => [Path.Combine(Path.GetTempPath(), "elsewhere", "bootstrapper.exe")];
            Assert.False(GameSettings.Busy(withInstall));
            Assert.True(GameSettings.KeepOn(withInstall, program, afterSession: true));
            Assert.Contains(VsyncOff, File.ReadAllText(main), StringComparison.Ordinal);
        }
        finally { GameSettings.HelperPaths = previous; }
    }

    [Fact]
    public void AHelperInsideTheGamesFolderStopsTheLook()
    {
        var previous = GameSettings.HelperPaths;
        try
        {
            var (profile, main, _, program) = Mw4Optimized();
            Game(main, (VsyncOff, VsyncReset));
            var changed = File.ReadAllText(main);
            var withInstall = profile with { Helpers = ["bootstrapper"], Install = Path.GetDirectoryName(main) };
            GameSettings.HelperPaths = _ => [Path.Combine(profile.File, "..", "bootstrapper.exe")];
            Assert.True(GameSettings.Busy(withInstall));
            Assert.False(GameSettings.KeepOn(withInstall, program, afterSession: true));
            Assert.Equal(changed, File.ReadAllText(main));
        }
        finally { GameSettings.HelperPaths = previous; }
    }

    [Fact]
    public void AHelperWhosePathCannotBeReadCountsAsRunning()
    {
        var previous = GameSettings.HelperPaths;
        try
        {
            var (profile, main, _, _) = Mw4Optimized();
            var withInstall = profile with { Helpers = ["bootstrapper"], Install = Path.GetDirectoryName(main) };
            // A protected process (an anti-cheat's) has no readable path: it could be the game's helper, so nothing is written.
            GameSettings.HelperPaths = _ => [null];
            Assert.True(GameSettings.Busy(withInstall));
        }
        finally { GameSettings.HelperPaths = previous; }
    }

    [Fact]
    public async Task AWaitOnAHelperSaysSoOnceAndAgainAfterTenMinutes()
    {
        var spans = new List<TimeSpan>();
        // Each line with how many waits had happened when it was said, so "once, and once more after ten minutes" is checked
        // rather than "at least once somewhere".
        var lines = new List<(string Line, int After)>();
        void Heard(string line) { lock (lines) lines.Add((line, spans.Count)); }
        KeepEvidence.Logged += Heard;
        try
        {
            // The launcher's helper never closes for 200 polls (about 50 minutes), and the game's own wait has no time limit: it
            // keeps looking, every 30 seconds once ten minutes have passed, and looks once the helper is gone.
            var polls = 0;
            var free = await KeepEvidence.UntilFreeAsync(() => ++polls <= 200, TimeSpan.FromSeconds(5), (span, _) => { spans.Add(span); return Task.CompletedTask; },
                CancellationToken.None, helper: () => "bootstrapper", title: "Modern Warfare 4");

            Assert.True(free);
            Assert.True(spans.Count >= 200, $"the wait polled {spans.Count} times");
            // Ten minutes of five seconds is 120 polls: the first sentence is said once before that, the second once after it,
            // and neither ever again however many polls follow (they would be noise in app.log every five seconds).
            var first = lines.Where(line => line.Line.Contains("waiting for its launcher helper bootstrapper", StringComparison.Ordinal)).ToList();
            var again = lines.Where(line => line.Line.Contains("still waiting on bootstrapper after 10 minutes", StringComparison.Ordinal)).ToList();
            Assert.Single(first);
            Assert.Single(again);
            Assert.InRange(first[0].After, 1, 120);
            Assert.True(again[0].After > 120, $"the ten-minute line was said after {again[0].After} polls");
            // Ten minutes of five seconds, then thirty-second looks from there on.
            Assert.Equal(TimeSpan.FromSeconds(5), spans[119]);
            Assert.Equal(TimeSpan.FromSeconds(30), spans[120]);
            Assert.DoesNotContain(spans, span => span > TimeSpan.FromSeconds(30));
        }
        finally { KeepEvidence.Logged -= Heard; }
    }

    // ------------------------------------------------------------------ Rocket League: its own mark of the defaults it rebuilt the file from

    private static byte[] RealIni() => Encoding.ASCII.GetBytes(
        File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "RocketLeague", "TASystemSettings.ini")).ReplaceLineEndings("\r\n"));

    private const string Stamps = "[IniVersion]\r\n0=1792002878.000000\r\n1=1792002878.000000\r\n";
    private const string NewStamps = "[IniVersion]\r\n0=1795021200.000000\r\n1=1795021200.000000\r\n";

    /// <summary>This PC's TASystemSettings.ini in a folder of its own, optimized (on this PC, texture filtering alone).</summary>
    private static (GameSettings.Profile Profile, string Ini, string Program) RocketLeagueOptimized()
    {
        var folder = NewFolder();
        var ini = Path.Combine(folder, "TASystemSettings.ini");
        File.WriteAllBytes(ini, RealIni());
        var program = Program(folder, "RocketLeague.exe");
        var id = Guid.NewGuid().ToString("N");
        var profile = GameSettings.RocketLeague(new("nvidia", 5, 8, 1, 165)) with
        {
            Id = "rocketleague-" + id, Title = "Rocket League " + id, File = ini, Processes = [NoGame, NoGame + "_EAC"],
        };
        Assert.True(GameSettings.Set(profile, null, true, program));
        Assert.True(Applied(profile));
        return (profile, ini, program);
    }

    /// <summary>The game's first start after an update: the whole file from its defaults, with the new Default*.ini times.</summary>
    private static void Rebuild(string ini) =>
        File.WriteAllText(ini, Encoding.ASCII.GetString(RealIni()).Replace(Stamps, NewStamps, StringComparison.Ordinal), Encoding.ASCII);

    [Fact]
    public void TheIniVersionIsRocketLeaguesMark()
    {
        Assert.Equal("0=1792002878.000000|1=1792002878.000000", GameSettings.IniVersion(GameSettings.ParseRocketLeague(RealIni())));
        Assert.Null(GameSettings.IniVersion(GameSettings.ParseRocketLeague(Encoding.ASCII.GetBytes("[SystemSettings]\r\nMaxAnisotropy=4\r\n"))));
    }

    [Fact]
    public void ARocketLeagueRebuildIsWrittenAgainAndItsMarkLeftAsTheGameWroteIt()
    {
        var (profile, ini, program) = RocketLeagueOptimized();
        Rebuild(ini);
        Assert.False(Applied(profile));
        // Its mark alone tells it: the program needn't change (the other store's copy rebuilds the same way).
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.True(Applied(profile));
        var text = File.ReadAllText(ini);
        Assert.EndsWith(NewStamps + "\r\n", text, StringComparison.Ordinal);
        Assert.Contains("MaxAnisotropy=16\r\n", text, StringComparison.Ordinal);
        // Only Exo's line differs from the rebuilt file.
        var rebuilt = Encoding.ASCII.GetString(RealIni()).Replace(Stamps, NewStamps, StringComparison.Ordinal);
        Assert.Equal(rebuilt.Replace("MaxAnisotropy=4\r\n", "MaxAnisotropy=16\r\n", StringComparison.Ordinal), text);
        // Put back still returns what the player had before Exo.
        Assert.Equal(new GameSettings.Remembered("4", "16"), GameSettings.Kept(profile.Id)["MaxAnisotropy"]);
    }

    [Fact]
    public void ACapPickedInRocketLeaguesMenuStaysThroughItsRebuild()
    {
        var (profile, ini, program) = RocketLeagueOptimized();
        Game(ini, ("CustomFPS=0", "CustomFPS=144"));
        Assert.False(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Contains("CustomFPS=144\r\n", File.ReadAllText(ini), StringComparison.Ordinal);

        Rebuild(ini);
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        var text = File.ReadAllText(ini);
        Assert.Contains("MaxAnisotropy=16\r\n", text, StringComparison.Ordinal);
        Assert.Contains("CustomFPS=0\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileTheGameWroteWithItsMarkUnchangedIsNotARebuildAndItsLineIsTheirsOnceTheSameValueComesBack()
    {
        var (profile, ini, program) = RocketLeagueOptimized();
        // Exo's line back without the mark changing: no evidence of a rebuild, so it is put back once, quietly, and the value found remembered.
        Game(ini, ("MaxAnisotropy=16", "MaxAnisotropy=4"));
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Contains("MaxAnisotropy=16\r\n", File.ReadAllText(ini), StringComparison.Ordinal);
        Assert.False(GameSettings.Again(profile));
        // The same value again is the player's.
        Game(ini, ("MaxAnisotropy=16", "MaxAnisotropy=4"));
        var text = File.ReadAllText(ini);
        Assert.False(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Equal(text, File.ReadAllText(ini));
        Assert.Contains("MaxAnisotropy", Assert.Single(Keeping(profile)!.Theirs), StringComparison.Ordinal);
    }
}
