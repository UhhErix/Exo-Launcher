using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Rocket League's Optimize can go wrong:
/// - writing changes a byte outside Exo's lines: another section, [IniVersion] (a changed one makes the game rebuild the
///   whole file from its defaults), the CRLF line breaks, a comment, a character outside ASCII;
/// - a line in a profile section the game doesn't play with ([SystemSettingsScreenshot]'s MaxAnisotropy=16, SplitScreen2's
///   DetailMode) is taken for the live one, or an unknown key, a comment or a merge command ("+Key=") is written;
/// - a true or false goes back in another casing, or a number out of the engine's range or in another format;
/// - turning Optimize off loses what the player had, or undoes what they changed in the game since;
/// - the Steam copy isn't recognized, or is given another file than Epic's (both read the same one);
/// - texture filtering is raised on graphics without memory of its own;
/// - a game the profile doesn't know (no Rocket League program) is treated as Rocket League.
/// </summary>
public sealed class RocketLeagueSettingsTests
{
    /// <summary>This PC's TASystemSettings.ini (Epic, October 2026), with the game's own CRLF whatever checkout did to it.</summary>
    private static byte[] Real() => Encoding.ASCII.GetBytes(
        File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "RocketLeague", "TASystemSettings.ini")).ReplaceLineEndings("\r\n"));

    private static GameSettings.Pc ThisPc => new("nvidia", 5, 8, 2, 165);
    private static GameSettings.Pc WithMemory(double? memory) => new("nvidia", 5, memory, 1, 165);

    private static GameSettings.Line Line(GameSettings.SettingsFile file, string name) => file.Settings.Single(l => l.Name == name);

    /// <summary>Optimize as the host runs it on one file: every group on, then the file as written.</summary>
    private static (byte[] Bytes, Dictionary<int, string> Edits) Apply(byte[] bytes, GameSettings.Pc pc, Dictionary<string, GameSettings.Remembered> kept)
    {
        var file = GameSettings.ParseRocketLeague(bytes);
        var edits = new Dictionary<int, string>();
        foreach (var group in GameSettings.RocketLeagueGroups(pc))
            foreach (var (index, value) in GameSettings.Plan(file, group, true, kept)) edits[index] = value;
        return (GameSettings.Write(file, edits), edits);
    }

    [Fact]
    public void TheRealFileComesBackByteForByte()
    {
        var bytes = Real();
        Assert.Equal(bytes, GameSettings.Write(GameSettings.ParseRocketLeague(bytes), new Dictionary<int, string>()));
    }

    [Fact]
    public void OnlyTheLiveSectionsKnownKeysAreLines()
    {
        var file = GameSettings.ParseRocketLeague(Real());
        // [SystemSettingsScreenshot] has its own MaxAnisotropy=16: never the live one.
        Assert.Equal("4", Line(file, "MaxAnisotropy").Value);
        var screenshot = Array.FindIndex(file.Lines, l => l == "[SystemSettingsScreenshot]");
        Assert.All(file.Settings, l => Assert.True(l.Index < screenshot));
        // Only keys the profile names: resolution, display mode and the menu's other choices are never lines.
        Assert.DoesNotContain(file.Settings, l => l.Name is "ResX" or "Fullscreen" or "Borderless" or "UseVsync" or "DetailMode" or "ScreenPercentage" or "UncappedFramerate");
        Assert.Equal(17, file.Settings.Count);
    }

    [Fact]
    public void OnThisPcOnlyTextureFilteringChanges()
    {
        var bytes = Real();
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var (written, edits) = Apply(bytes, ThisPc, kept);

        // The file already matches everything else, and Optimize caps nothing.
        Assert.Equal("16", Assert.Single(edits).Value);
        Assert.Equal(new[] { "MaxAnisotropy" }, kept.Keys);
        var before = Encoding.ASCII.GetString(bytes).Split("\r\n");
        var after = Encoding.ASCII.GetString(written).Split("\r\n");
        Assert.Equal(before.Length, after.Length);
        var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).ToList();
        Assert.Equal("MaxAnisotropy=16", after[Assert.Single(changed)]);
        // [IniVersion] and the line breaks as they were.
        Assert.EndsWith("[IniVersion]\r\n0=1792002878.000000\r\n1=1792002878.000000\r\n\r\n", Encoding.ASCII.GetString(written), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0.5, false)] // a laptop's shared-memory graphics
    [InlineData(2.0, true)]
    [InlineData(8.0, true)]
    public void SixteenTimesFilteringOnACardWithItsOwnMemory(double? memory, bool filtering) =>
        Assert.Equal(filtering, GameSettings.RocketLeagueGroups(WithMemory(memory)).Any(g => g.Id == "filtering"));

    [Fact]
    public void OnlyValuesTheEngineTakesAreWrittenInTheLinesOwnSpelling()
    {
        var file = GameSettings.ParseRocketLeague(Real());
        Assert.Equal("16", GameSettings.Fit(Line(file, "MaxAnisotropy"), "16"));
        Assert.Null(GameSettings.Fit(Line(file, "MaxAnisotropy"), "17"));
        Assert.Null(GameSettings.Fit(Line(file, "MaxAnisotropy"), "0"));
        Assert.Equal("100.000000", GameSettings.Fit(Line(file, "MinimumScreenScale"), "100"));
        Assert.Equal("162", GameSettings.Fit(Line(file, "CustomFPS"), "162"));
        Assert.Null(GameSettings.Fit(Line(file, "CustomFPS"), "5000"));
        // "WaitForGPU=false" stays lower case, "OneFrameThreadLag=True" title case.
        Assert.Equal("false", GameSettings.Fit(Line(file, "WaitForGPU"), "False"));
        Assert.Equal("True", GameSettings.Fit(Line(file, "OneFrameThreadLag"), "true"));
        Assert.Null(GameSettings.Fit(Line(file, "OneFrameThreadLag"), "1"));
        var upper = GameSettings.ParseRocketLeague(Encoding.ASCII.GetBytes("[SystemSettings]\r\nBloom=TRUE\r\n"));
        Assert.Equal("FALSE", GameSettings.Fit(Line(upper, "Bloom"), "False"));
    }

    [Fact]
    public void CommentsMergeCommandsAndOtherSectionsAreNeverLines()
    {
        const string text = "[SystemSettings]\r\n;MaxAnisotropy=1\r\n+MaxAnisotropy=2\r\nMaxAnisotropy = 4 \r\n[SystemSettingsSplitScreen2]\r\nBloom=True\r\n[IniVersion]\r\n0=1.000000\r\n";
        var file = GameSettings.ParseRocketLeague(Encoding.ASCII.GetBytes(text));
        var line = Assert.Single(file.Settings);
        Assert.Equal(("MaxAnisotropy", "4", 3), (line.Name, line.Value, line.Index));
        // The spaces the engine trims stay where they are.
        Assert.Equal(text.Replace("MaxAnisotropy = 4 ", "MaxAnisotropy = 16 ", StringComparison.Ordinal),
            Encoding.ASCII.GetString(GameSettings.Write(file, new Dictionary<int, string> { [line.Index] = "16" })));
    }

    [Fact]
    public void ACharacterOutsideAsciiElsewhereGoesBackUnchanged()
    {
        byte[] bytes = [.. Encoding.ASCII.GetBytes("[SystemSettings]\r\nMaxAnisotropy=4\r\n[Other]\r\nName=Caf"), 0xE9, .. Encoding.ASCII.GetBytes("\r\n")];
        var file = GameSettings.ParseRocketLeague(bytes);
        var written = GameSettings.Write(file, new Dictionary<int, string> { [Line(file, "MaxAnisotropy").Index] = "16" });
        byte[] expected = [.. Encoding.ASCII.GetBytes("[SystemSettings]\r\nMaxAnisotropy=16\r\n[Other]\r\nName=Caf"), 0xE9, .. Encoding.ASCII.GetBytes("\r\n")];
        Assert.Equal(expected, written);
    }

    [Fact]
    public void AUtf16FileHasNoLines()
    {
        byte[] bytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("[SystemSettings]\r\nMaxAnisotropy=4\r\n")];
        Assert.Empty(GameSettings.ParseRocketLeague(bytes).Settings);
    }

    [Fact]
    public void PutBackRestoresWhatItHeldButNotWhatTheGameChangedSince()
    {
        var bytes = Real();
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var (written, _) = Apply(bytes, ThisPc, kept);
        var file = GameSettings.ParseRocketLeague(written);
        Assert.Single(kept);

        // Put back on the file as Optimize left it: what it held, byte for byte.
        var back = new Dictionary<string, GameSettings.Remembered>(kept);
        var edits = new Dictionary<int, string>();
        foreach (var group in GameSettings.RocketLeagueGroups(ThisPc))
            foreach (var (index, value) in GameSettings.Plan(file, group, false, back)) edits[index] = value;
        Assert.Equal(bytes, GameSettings.Write(file, edits));
        Assert.Empty(back);

        // The player picks 8x in the game since: it stays theirs.
        file = GameSettings.ParseRocketLeague(GameSettings.Write(file, new Dictionary<int, string> { [Line(file, "MaxAnisotropy").Index] = "8" }));
        edits.Clear();
        foreach (var group in GameSettings.RocketLeagueGroups(ThisPc))
            foreach (var (index, value) in GameSettings.Plan(file, group, false, kept)) edits[index] = value;
        Assert.Empty(edits);
        Assert.Empty(kept);
    }

    [Fact]
    public void AStockFilesGroupsAreOnButNotExos()
    {
        var file = GameSettings.ParseRocketLeague(Real());
        var states = GameSettings.States(GameSettings.RocketLeague(ThisPc), file, new Dictionary<string, GameSettings.Remembered>());
        Assert.Equal(new[] { "frames", "resolution", "filtering", "effects", "ball" }, states.Select(s => s.Id));
        Assert.All(states, s => Assert.False(s.Exo));
        Assert.All(states.Where(s => s.Id != "filtering"), s => Assert.True(s.On));
        Assert.False(states.Single(s => s.Id == "filtering").On);
    }

    [Fact]
    public void FolkloreTweaksAreUndone()
    {
        // A file after a "max FPS" guide: the frame limiter off, the render thread forced in step, streaming pinned.
        var tweaked = Encoding.ASCII.GetString(Real())
            .Replace("OneFrameThreadLag=True", "OneFrameThreadLag=False", StringComparison.Ordinal)
            .Replace("AllowPerFrameSleep=True", "AllowPerFrameSleep=False", StringComparison.Ordinal)
            .Replace("OnlyStreamInTextures=False", "OnlyStreamInTextures=True", StringComparison.Ordinal)
            .Replace("WaitForGPU=false", "WaitForGPU=true", StringComparison.Ordinal);
        var (written, _) = Apply(Encoding.ASCII.GetBytes(tweaked), ThisPc, new());
        var file = GameSettings.ParseRocketLeague(written);
        Assert.Equal(("True", "True", "False", "false"),
            (Line(file, "OneFrameThreadLag").Value, Line(file, "AllowPerFrameSleep").Value, Line(file, "OnlyStreamInTextures").Value, Line(file, "WaitForGPU").Value));
    }

    // ------------------------------------------------------------------ Epic's and Steam's installs

    /// <summary>A Rocket League install as its store lays it out: TAGame and Binaries\Win64\RocketLeague.exe (empty here).</summary>
    private static string Install(params string[] parts)
    {
        var root = Path.Combine([TestDataDirectory.Root, "rl-" + Guid.NewGuid().ToString("N"), .. parts]);
        Directory.CreateDirectory(Path.Combine(root, "TAGame", "Config"));
        Directory.CreateDirectory(Path.Combine(root, "Binaries", "Win64"));
        File.WriteAllBytes(Path.Combine(root, "Binaries", "Win64", "RocketLeague.exe"), []);
        return root;
    }

    private static GameEntry Game(StoreKind store, string id, string path) => new() { Id = id, Title = "Rocket League", Store = store, Installed = true, Path = path };

    [Fact]
    public void EpicsAndSteamsCopiesShareOneProfileAndFile()
    {
        var epic = Game(StoreKind.Epic, "epic:Sugar", Install("Epic Games", "rocketleague"));
        var steam = Game(StoreKind.Steam, "steam:252950", Install("steamapps", "common", "rocketleague"));
        var fromEpic = GameSettings.For(epic, ThisPc);
        var fromSteam = GameSettings.For(steam, ThisPc);
        Assert.NotNull(fromEpic);
        Assert.NotNull(fromSteam);
        // One settings file per Windows user for both (only their saves differ), so one record of what Exo kept.
        Assert.Equal(("rocketleague", fromEpic.File), (fromSteam.Id, fromSteam.File));
        Assert.EndsWith(Path.Combine("My Games", "Rocket League", "TAGame", "Config", "TASystemSettings.ini"), fromSteam.File, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "RocketLeague", "RocketLeague_EAC" }, fromSteam.Processes);
        // Each copy's own program, for its NVIDIA profile and to see it updated.
        Assert.Equal(Path.Combine(steam.Path!, "Binaries", "Win64", "RocketLeague.exe"), fromSteam.Program!(steam));
        Assert.Equal(Path.Combine(epic.Path!, "Binaries", "Win64", "RocketLeague.exe"), fromEpic.Program!(epic));
    }

    [Fact]
    public void OnlyARocketLeagueInstallGetsTheProfile()
    {
        var folder = Install("steamapps", "common", "rocketleague");
        DeletionGate.DeleteFile(Path.Combine(folder, "Binaries", "Win64", "RocketLeague.exe"));
        Assert.Null(GameSettings.For(Game(StoreKind.Steam, "steam:252950", folder), ThisPc));
        Assert.Null(GameSettings.For(new GameEntry { Id = "steam:252950", Title = "Rocket League", Store = StoreKind.Steam, Installed = false, Path = Install("x") }, ThisPc));
        // Call of Duty is still Call of Duty's.
        var cod = Path.Combine(TestDataDirectory.Root, "cod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(cod, "cod26"));
        Assert.Equal("mw4", GameSettings.For(Game(StoreKind.Steam, "steam:1938090", cod), ThisPc)?.Id);
    }
}
