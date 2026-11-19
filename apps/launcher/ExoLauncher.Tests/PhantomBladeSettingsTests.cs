using System.Text;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Phantom Blade Zero's Optimize can go wrong:
/// - writing changes a byte outside Exo's lines: the ";METADATA" first line, [ScalabilityGroups], [GraphicsQualitySettings],
///   [PBZQuickVerify], the benchmark's repeated lines, the CRLF line breaks;
/// - a line the game restores from its sealed save at every start (the gq.* and sg.* mirror of the Video page, V-Sync, the
///   frame cap) or the benchmark's fingerprint is taken for one Exo may write;
/// - a window mode goes in out of Unreal's range (3, -1) or in another format ("1.000000");
/// - full screen or windowed isn't made borderless, Put back loses the player's mode, or a mode picked in the game since is
///   undone; on this PC (already borderless) Exo claims a change;
/// - an update's reset to full screen isn't written again and said, or the mode chosen isn't held once the game has closed;
/// - the profile reads another file than the one the game reads, or the sealed saves are ever one of its files;
/// - another game's install is taken for Phantom Blade Zero (or it for Rocket League or Call of Duty), the Steam launcher
///   stub is taken for the game's program, or a write happens while either PBZ.exe runs;
/// - BenchmarKing's settings, all on the sealed page, are written.
/// </summary>
public sealed class PhantomBladeSettingsTests
{
    private const string NoGame = "exo-test-no-such-game";
    private static readonly DateTime Launch = new(2026, 10, 28, 20, 58, 0, DateTimeKind.Utc);
    private static readonly DateTime Patch = new(2026, 11, 12, 18, 0, 0, DateTimeKind.Utc);

    /// <summary>This PC's GameUserSettings.ini (Steam, launch night), with the game's own CRLF whatever checkout did to it.</summary>
    private static byte[] Real() => Encoding.ASCII.GetBytes(
        File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "PhantomBladeZero", "GameUserSettings.ini")).ReplaceLineEndings("\r\n"));

    /// <summary>The real file with its window mode lines as the game writes them for full screen (0) or windowed (2).</summary>
    private static byte[] Mode(int mode, int preferred = 1) => Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Real())
        .Replace("\r\nFullscreenMode=1\r\n", $"\r\nFullscreenMode={mode}\r\n", StringComparison.Ordinal)
        .Replace("LastConfirmedFullscreenMode=1", $"LastConfirmedFullscreenMode={mode}", StringComparison.Ordinal)
        .Replace("PreferredFullscreenMode=1", $"PreferredFullscreenMode={preferred}", StringComparison.Ordinal));

    private static GameSettings.Line Line(GameSettings.SettingsFile file, string name) => file.Settings.Single(l => l.Name == name);

    /// <summary>Optimize as the host runs it on one file: every group on, then the file as written.</summary>
    private static (byte[] Bytes, Dictionary<int, string> Edits) Apply(byte[] bytes, Dictionary<string, GameSettings.Remembered> kept)
    {
        var file = GameSettings.ParsePhantomBlade(bytes);
        var edits = new Dictionary<int, string>();
        foreach (var group in new[] { GameSettings.UnrealWindow(false) })
            foreach (var (index, value) in GameSettings.Plan(file, group, true, kept)) edits[index] = value;
        return (GameSettings.Write(file, edits), edits);
    }

    [Fact]
    public void HisSettingsAreNeverWritten()
    {
        var keys = GameSettings.PhantomBladeZero(1000).Groups(GameSettings.ParsePhantomBlade(Real())).SelectMany(g => g.Changes).Select(c => c.Key);
        Assert.Equal(["FullscreenMode", "LastConfirmedFullscreenMode", "PreferredFullscreenMode", GameSettings.UnrealHdrNits], keys);
    }

    [Fact]
    public void TheRealFileComesBackByteForByte()
    {
        var bytes = Real();
        Assert.Equal(bytes, GameSettings.Write(GameSettings.ParsePhantomBlade(bytes), new Dictionary<int, string>()));
    }

    [Fact]
    public void OnlyTheWindowModeAndHdrPeakLinesOfTheSettingsSectionAreLines()
    {
        var file = GameSettings.ParsePhantomBlade(Real());
        Assert.Equal(new[] { "FullscreenMode", "LastConfirmedFullscreenMode", "PreferredFullscreenMode", "HDRDisplayOutputNits" }, file.Settings.Select(l => l.Name));
        var section = Array.IndexOf(file.Lines, "[/Script/ProjectPBZ.PBZSettingsLocal]");
        var verify = Array.IndexOf(file.Lines, "[PBZQuickVerify]");
        Assert.True(section > 0 && verify > section);
        Assert.All(file.Settings, l => Assert.InRange(l.Index, section + 1, verify - 1));
        // The sealed save's mirror, the menu's V-Sync and cap, and the benchmark are never lines.
        Assert.DoesNotContain(file.Settings, l => l.Name.StartsWith("gq.", StringComparison.Ordinal) || l.Name.StartsWith("sg.", StringComparison.Ordinal)
            || l.Name is "r.GraphicsQuality" or "bUseVSync" or "FrameRateLimit" or "bUseDynamicResolution" or "ResolutionSizeX" or "AdapterName" or "DriverVer"
            || (l.Name.StartsWith("Last", StringComparison.Ordinal) && l.Name != "LastConfirmedFullscreenMode"));
    }

    [Fact]
    public void OnThisPcItIsAlreadyBorderlessAndNothingIsWritten()
    {
        var bytes = Real();
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var (written, edits) = Apply(bytes, kept);
        Assert.Empty(edits);
        Assert.Empty(kept);
        Assert.Equal(bytes, written);
        var state = Assert.Single(GameSettings.States(GameSettings.PhantomBladeZero(), GameSettings.ParsePhantomBlade(bytes), kept));
        Assert.Equal(("display", true, false), (state.Id, state.On, state.Exo));
    }

    [Theory]
    [InlineData(0, 0)] // exclusive full screen, Alt+Enter to it as well
    [InlineData(0, 1)]
    [InlineData(2, 1)] // windowed
    public void FullScreenOrWindowedBecomesBorderlessAndNothingElseChanges(int mode, int preferred)
    {
        var bytes = Mode(mode, preferred);
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var (written, _) = Apply(bytes, kept);

        var before = Encoding.ASCII.GetString(bytes).Split("\r\n");
        var after = Encoding.ASCII.GetString(written).Split("\r\n");
        Assert.Equal(before.Length, after.Length);
        var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).Select(i => after[i]).ToList();
        Assert.Equal(preferred == 1 ? new[] { "FullscreenMode=1", "LastConfirmedFullscreenMode=1" }
            : new[] { "FullscreenMode=1", "LastConfirmedFullscreenMode=1", "PreferredFullscreenMode=1" }, changed);
        Assert.Equal(new GameSettings.Remembered(mode.ToString(System.Globalization.CultureInfo.InvariantCulture), "1"), kept["FullscreenMode"]);
        Assert.StartsWith(";METADATA=(Diff=true, UseCommands=true)\r\n[ScalabilityGroups]\r\n", Encoding.ASCII.GetString(written), StringComparison.Ordinal);
        Assert.Contains(GameSettings.States(GameSettings.PhantomBladeZero(), GameSettings.ParsePhantomBlade(written), kept), s => s is { Id: "display", On: true, Exo: true });
    }

    [Fact]
    public void OnlyModesUnrealTakesAreWrittenInTheLinesOwnFormat()
    {
        var file = GameSettings.ParsePhantomBlade(Real());
        Assert.Equal("1", GameSettings.Fit(Line(file, "FullscreenMode"), "1"));
        Assert.Equal("1", GameSettings.Fit(Line(file, "FullscreenMode"), "1.0"));
        Assert.Null(GameSettings.Fit(Line(file, "FullscreenMode"), "3"));
        Assert.Null(GameSettings.Fit(Line(file, "FullscreenMode"), "-1"));
        Assert.Null(GameSettings.Fit(Line(file, "PreferredFullscreenMode"), "2"));
        Assert.Null(GameSettings.Fit(Line(file, "FullscreenMode"), "WindowedFullscreen"));
    }

    [Fact]
    public void PutBackReturnsTheModeButNotOneChangedInTheGameSince()
    {
        var bytes = Mode(0, 0);
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var (written, _) = Apply(bytes, kept);
        var file = GameSettings.ParsePhantomBlade(written);

        // Put back on the file as Optimize left it: the player's full screen, byte for byte.
        var back = new Dictionary<string, GameSettings.Remembered>(kept);
        var undo = GameSettings.Plan(file, GameSettings.UnrealWindow(false), false, back);
        Assert.Equal(bytes, GameSettings.Write(file, undo));
        Assert.Empty(back);

        // Windowed picked in the game since: those two lines stay windowed, Alt+Enter's goes back.
        file = GameSettings.ParsePhantomBlade(GameSettings.Write(file, new Dictionary<int, string>
            { [Line(file, "FullscreenMode").Index] = "2", [Line(file, "LastConfirmedFullscreenMode").Index] = "2" }));
        var after = GameSettings.Write(file, GameSettings.Plan(file, GameSettings.UnrealWindow(false), false, kept));
        Assert.Equal(Encoding.ASCII.GetString(Mode(2, 0)), Encoding.ASCII.GetString(after));
    }

    // ------------------------------------------------------------------ on disk: kept on through an update

    private static (GameSettings.Profile Profile, string Ini, string Program) Optimized(int mode)
    {
        var folder = Path.Combine(TestDataDirectory.Root, "pbz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var ini = Path.Combine(folder, "GameUserSettings.ini");
        File.WriteAllBytes(ini, Mode(mode));
        var program = Path.Combine(folder, "PBZ.exe");
        File.WriteAllBytes(program, []);
        File.SetLastWriteTimeUtc(program, Launch);
        var id = Guid.NewGuid().ToString("N");
        var profile = GameSettings.PhantomBladeZero() with { Id = "pbz-" + id, Title = "Phantom Blade Zero " + id, File = ini, Processes = [NoGame] };
        Assert.True(GameSettings.Set(profile, null, true, program));
        Assert.Equal(Real(), File.ReadAllBytes(ini));
        return (profile, ini, program);
    }

    [Fact]
    public void APatchThatPutsFullScreenBackIsWrittenAgain()
    {
        var (profile, ini, program) = Optimized(0);
        File.SetLastWriteTimeUtc(program, Patch);
        File.WriteAllBytes(ini, Mode(0));
        Assert.True(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Equal(Real(), File.ReadAllBytes(ini));
        Assert.True(GameSettings.Again(profile));
        // Put back still returns the player's own full screen, not the patch's.
        Assert.True(GameSettings.Set(profile, null, false, program));
        Assert.Equal(Mode(0), File.ReadAllBytes(ini));
    }

    [Fact]
    public void FullScreenPickedInTheGameGoesBackToTheModeChosenWithoutCallingItAPatch()
    {
        var (profile, ini, program) = Optimized(2);
        File.WriteAllBytes(ini, Mode(0));
        Assert.False(GameSettings.KeepOn(profile, program, afterSession: true));
        Assert.Equal(Real(), File.ReadAllBytes(ini));
        Assert.False(GameSettings.Again(profile));
    }

    [Fact]
    public void NothingIsWrittenWhileTheGameRuns()
    {
        // The press is made before the game "starts"; it is the look that runs while it does.
        var (profile, ini, program) = Optimized(0);
        using var running = System.Diagnostics.Process.GetCurrentProcess();
        var playing = profile with { Processes = [running.ProcessName] };
        File.SetLastWriteTimeUtc(program, Patch);
        File.WriteAllBytes(ini, Mode(0));
        Assert.False(GameSettings.KeepOn(playing, program, afterSession: true));
        Assert.Equal(Mode(0), File.ReadAllBytes(ini));
    }

    // ------------------------------------------------------------------ the install and the file

    /// <summary>Steam's PBZ install: the Unreal project folder, the launcher stub and the game under platform\steam (empty here).</summary>
    private static string Install()
    {
        var root = Path.Combine(TestDataDirectory.Root, "pbz-install-" + Guid.NewGuid().ToString("N"), "PBZ");
        Directory.CreateDirectory(Path.Combine(root, "ProjectPBZ", "Saved"));
        Directory.CreateDirectory(Path.Combine(root, "steam"));
        Directory.CreateDirectory(Path.Combine(root, "platform", "steam"));
        File.WriteAllBytes(Path.Combine(root, "steam", "PBZ.exe"), []);
        File.WriteAllBytes(Path.Combine(root, "platform", "steam", "PBZ.exe"), []);
        return root;
    }

    private static GameEntry Game(string path) => new() { Id = "steam:4115450", Title = "Phantom Blade Zero", Store = StoreKind.Steam, Installed = true, Path = path };

    [Fact]
    public void TheInstallGetsTheProfileWithTheFileTheGameReads()
    {
        var game = Game(Install());
        var profile = GameSettings.For(game, new("nvidia", 5, 8, 2, 165, false));
        Assert.NotNull(profile);
        Assert.Equal(("pbz", "Phantom Blade Zero"), (profile.Id, profile.Title));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectPBZ", "Saved", "Config", "Windows", "GameUserSettings.ini"), profile.File);
        Assert.Null(profile.PlayerFiles);
        Assert.Null(profile.Nvidia);
        Assert.Equal(new[] { "PBZ" }, profile.Processes);
        // The game, not Steam's launcher stub that starts it.
        Assert.Equal(Path.Combine(game.Path!, "platform", "steam", "PBZ.exe"), profile.Program!(game));
        // The same with no facts about the PC (keeping Optimize on as Exo starts).
        Assert.Equal("pbz", GameSettings.For(game, new(null, null, null))?.Id);
    }

    [Fact]
    public void OnlyAPhantomBladeInstallGetsTheProfile()
    {
        var folder = Install();
        DeletionGate.DeleteDirectory(Path.Combine(folder, "ProjectPBZ"), recursive: true);
        Assert.Null(GameSettings.For(Game(folder), new("nvidia", 5, 8)));
        Assert.Null(GameSettings.For(new GameEntry { Id = "steam:4115450", Title = "Phantom Blade Zero", Store = StoreKind.Steam, Installed = false, Path = Install() }, new("nvidia", 5, 8)));
        // Without platform\{store}\PBZ.exe there's no program to tell an update by, but the settings still are the game's.
        var bare = Install();
        DeletionGate.DeleteDirectory(Path.Combine(bare, "platform"), recursive: true);
        Assert.Null(GameSettings.PhantomBladeProgram(Game(bare)));
        // Call of Duty is still Call of Duty's.
        var cod = Path.Combine(TestDataDirectory.Root, "cod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(cod, "cod26"));
        Assert.Equal("mw4", GameSettings.For(new GameEntry { Id = "steam:1938090", Title = "Call of Duty", Store = StoreKind.Steam, Installed = true, Path = cod }, new("nvidia", 5, 8))?.Id);
    }

    [Fact]
    public void AUtf16FileHasNoLines()
    {
        byte[] bytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("[/Script/ProjectPBZ.PBZSettingsLocal]\r\nFullscreenMode=0\r\n")];
        Assert.Empty(GameSettings.ParsePhantomBlade(bytes).Settings);
    }
}
