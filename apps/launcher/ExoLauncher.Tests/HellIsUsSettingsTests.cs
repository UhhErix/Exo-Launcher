using System.Text;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Hell is Us's Optimize can go wrong:
/// - writing changes a byte outside Exo's lines: the ";METADATA" first line, the engine's own section, CRLF;
/// - a line that isn't Exo's is taken for one: sg.ResolutionQuality and ResolutionScaleTSR (he found the game's render scale
///   fragile), the frame cap, anti-aliasing, or a line of the same name in another section;
/// - a value differs from BenchmarKing's list for this card, or his 8 GB textures rule isn't kept on 8 GB;
/// - two upscalers end up on (the game disables one), DLSS on a card without it, or DLSS Frame Generation turned on where
///   Exo's frame generation isn't, or off where it is;
/// - a value the line can't take goes in (a level past Ultra, a spelling the game doesn't list), or a line the file doesn't
///   hold makes Optimize fail instead of being left out;
/// - Put back loses what the line held;
/// - the profile reads another file than the game's, the Steam Cloud save is ever one of its files, or another game's
///   install is taken for Hell is Us.
/// The fixture is the game's own file on this PC, as it first wrote it (sg levels at 0, DLAA), before Exo touched it.
/// </summary>
public sealed class HellIsUsSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("exo-hellisus-").FullName;

    public void Dispose() => DeletionGate.DeleteDirectory(_dir, recursive: true);

    private const string Rtx3070 = "NVIDIA GeForce RTX 3070";

    private static byte[] Fixture() => Encoding.ASCII.GetBytes(
        File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "HellIsUs", "GameUserSettings.ini")).ReplaceLineEndings("\r\n"));

    private static GameSettings.Pc Owner => new("nvidia", 5, 8, 1, 165, Gpu: Rtx3070);

    /// <summary>Optimize as the host runs it: every group on, and the lines it set.</summary>
    private static Dictionary<string, string> Apply(GameSettings.Pc pc, bool frameGeneration = false, byte[]? bytes = null)
    {
        var file = GameSettings.ParseHellIsUs(bytes ?? Fixture());
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in GameSettings.HellIsUs(pc, frameGeneration).Groups(file))
            foreach (var (line, value) in GameSettings.Targets(file, group))
                written[line.Name] = value;
        return written;
    }

    [Fact]
    public void TheFileComesBackByteForByte()
    {
        var bytes = Fixture();
        Assert.Equal(bytes, GameSettings.Write(GameSettings.ParseHellIsUs(bytes), new Dictionary<int, string>()));
    }

    [Fact]
    public void OnlyTheVideoMenusLinesAreLines()
    {
        var names = GameSettings.ParseHellIsUs(Fixture()).Settings.Select(line => line.Name).ToHashSet();
        Assert.Contains("sg.ShadowQuality", names);
        Assert.Contains("NvidiaSuperResolution", names);
        Assert.DoesNotContain("sg.ResolutionQuality", names);
        Assert.DoesNotContain("sg.AntiAliasingQuality", names);
        Assert.DoesNotContain("ResolutionScaleTSR", names);
        Assert.DoesNotContain("FrameRateLimit", names);
        Assert.DoesNotContain("bUseDesiredScreenHeight", names);
    }

    [Fact]
    public void ALineOfTheSameNameInAnotherSectionIsLeftAlone()
    {
        var bytes = Encoding.ASCII.GetBytes("[SystemSettings]\r\nsg.ShadowQuality=4\r\nFullscreenMode=0\r\n[ScalabilityGroups]\r\nsg.ShadowQuality=3\r\n");
        var line = Assert.Single(GameSettings.ParseHellIsUs(bytes).Settings);
        Assert.Equal((4, "sg.ShadowQuality", "3"), (line.Index, line.Name, line.Value));
    }

    [Fact]
    public void OnAnRtx3070At1080ItIsHisListForEightGigabytes()
    {
        var written = Apply(Owner);
        Assert.Equal("2", written["sg.EffectsQuality"]);
        Assert.Equal("1", written["sg.FoliageQuality"]);
        Assert.Equal("2", written["sg.GlobalIlluminationQuality"]);
        Assert.Equal("4", written["sg.PostProcessQuality"]);
        Assert.Equal("2", written["sg.ReflectionQuality"]);
        Assert.Equal("2", written["sg.ShadingQuality"]);
        Assert.Equal("2", written["sg.ShadowQuality"]);
        Assert.Equal("1", written["sg.TextureQuality"]);
        Assert.Equal("2", written["sg.ViewDistanceQuality"]);
        Assert.Equal("Quality", written["NvidiaSuperResolution"]);
        Assert.Equal("Enabled", written["NvidiaReflex"]);
        Assert.Equal("True", written["bCompletedScalabilityBenchmark"]);
    }

    [Fact]
    public void AStrongCardTakesTheUpperOfHisTwoValuesAndMoreThanEightGigabytesLeavesMedium()
    {
        var written = Apply(new("nvidia", 7, 16, 1, 240, Gpu: "NVIDIA GeForce RTX 4080"));
        Assert.Equal("2", written["sg.FoliageQuality"]);
        Assert.Equal("3", written["sg.ViewDistanceQuality"]);
        Assert.Equal("2", written["sg.TextureQuality"]);
        Assert.Equal("2", written["sg.ShadowQuality"]);
    }

    [Fact]
    public void UnderEightGigabytesShadowsTakeHisFallback()
    {
        var written = Apply(new("nvidia", 5, 6, 1, 144, Gpu: "NVIDIA GeForce RTX 2060"));
        Assert.Equal("1", written["sg.ShadowQuality"]);
        Assert.Equal("1", written["sg.TextureQuality"]);
    }

    [Fact]
    public void NoMemoryReadingLeavesTexturesAlone() =>
        Assert.DoesNotContain("sg.TextureQuality", Apply(new("nvidia", 5, null, 1, 144, Gpu: Rtx3070)).Keys);

    [Fact]
    public void OneUpscalerAtATime()
    {
        var geforce = Apply(Owner);
        Assert.Equal(("Quality", "Off", "Off"), (geforce["NvidiaSuperResolution"], geforce["IntelXeSSMode"], geforce["AMDSuperResolutionMode"]));

        var radeon = Apply(new("amd", 5, 16, 1, 144, Gpu: "AMD Radeon RX 7900 XTX"));
        Assert.Equal(("Off", "Quality", "Off"), (radeon["NvidiaSuperResolution"], radeon["IntelXeSSMode"], radeon["AMDSuperResolutionMode"]));
        Assert.DoesNotContain("NvidiaReflex", radeon.Keys);

        // A GeForce without DLSS is on XeSS too; an unknown card keeps what it has.
        var gtx = Apply(new("nvidia", 5, 6, 1, 144, Gpu: "NVIDIA GeForce GTX 1660"));
        Assert.Equal(("Off", "Quality"), (gtx["NvidiaSuperResolution"], gtx["IntelXeSSMode"]));
        Assert.DoesNotContain("IntelXeSSMode", Apply(new(null, null, null)).Keys);
    }

    [Theory]
    [InlineData(1080, "Quality")]
    [InlineData(1440, "Quality")]
    [InlineData(2160, "Performance")]
    public void TheDlssModeFollowsTheOutput(int height, string mode)
    {
        var text = Encoding.ASCII.GetString(Fixture()).Replace("\r\nResolutionSizeY=1080\r\n", $"\r\nResolutionSizeY={height}\r\n", StringComparison.Ordinal);
        Assert.Equal(mode, Apply(Owner, bytes: Encoding.ASCII.GetBytes(text))["NvidiaSuperResolution"]);
    }

    [Fact]
    public void WithExosFrameGenerationItsLinesStayCompatibleAndTheLineSaysSo()
    {
        var written = Apply(Owner, frameGeneration: true);
        Assert.Equal("On2X", written["NvidiaFrameGeneration"]);
        Assert.Equal("False", written["bUseVSync"]);
        Assert.Equal("False", written["bAMDFrameGenerationEnabled"]);
        Assert.Equal("False", written["bIntelXeSSFrameGenerationEnabled"]);
    }

    [Fact]
    public void WithoutItFrameGenerationAndVSyncStayThePlayers()
    {
        var written = Apply(Owner);
        Assert.DoesNotContain("NvidiaFrameGeneration", written.Keys);
        Assert.DoesNotContain("bUseVSync", written.Keys);
    }

    [Theory]
    [InlineData(false, "1", GameSettings.Borderless)]
    [InlineData(true, "0", GameSettings.Fullscreen)]
    public void TheWindowModeChosenGoesInAndIsHeld(bool fullscreen, string line, string mode)
    {
        var profile = GameSettings.HellIsUs(Owner with { Fullscreen = fullscreen });
        var written = Apply(Owner with { Fullscreen = fullscreen });
        Assert.Equal(mode, profile.WindowMode);
        Assert.Equal(line, written["FullscreenMode"]);
        Assert.Equal(line, written["LastConfirmedFullscreenMode"]);
        Assert.Equal(line, written["PreferredFullscreenMode"]);
        Assert.Equal(["FullscreenMode", "LastConfirmedFullscreenMode", "PreferredFullscreenMode"], profile.WindowLines);
    }

    [Fact]
    public void TheHdrPeakGoesInOnlyOnAnHdrScreenAndInUnrealsRange()
    {
        Assert.DoesNotContain(GameSettings.UnrealHdrNits, Apply(Owner).Keys);
        Assert.Equal("1000", Apply(Owner with { HdrPeak = 1000 })[GameSettings.UnrealHdrNits]);
        Assert.Equal("2000", Apply(Owner with { HdrPeak = 4000 })[GameSettings.UnrealHdrNits]);
    }

    [Fact]
    public void WhatTheLineCantTakeIsLeftOut()
    {
        // A value the game spells otherwise, a line the file doesn't hold, and a line kept as a number.
        var text = Encoding.ASCII.GetString(Fixture())
            .Replace("NvidiaSuperResolution=DLAA\r\n", "", StringComparison.Ordinal)
            .Replace("sg.FoliageQuality=0\r\n", "", StringComparison.Ordinal);
        var written = Apply(Owner, bytes: Encoding.ASCII.GetBytes(text));
        Assert.DoesNotContain("NvidiaSuperResolution", written.Keys);
        Assert.DoesNotContain("sg.FoliageQuality", written.Keys);
        Assert.Equal("2", written["sg.ShadowQuality"]);

        var line = new GameSettings.Line(0, "NvidiaFrameGeneration", "NvidiaFrameGeneration", "Off", "one of Off, Auto, On2X, On3X, On4X", 0, 3);
        Assert.Null(GameSettings.Fit(line, "On5X"));
        var level = new GameSettings.Line(0, "sg.ShadowQuality", "sg.ShadowQuality", "3", "0 to 4", 0, 1);
        Assert.Null(GameSettings.Fit(level, "5"));
        Assert.Equal("4", GameSettings.Fit(level, "4"));
    }

    [Fact]
    public void AnEmptyFileHoldsNothingToWrite()
    {
        var file = GameSettings.ParseHellIsUs([]);
        Assert.All(GameSettings.HellIsUs(Owner, frameGeneration: true).Groups(file), group => Assert.Empty(GameSettings.Targets(file, group)));
    }

    [Fact]
    public void PutBackReturnsWhatEachLineHeldAndOnlyThoseLinesChange()
    {
        var bytes = Fixture();
        var file = GameSettings.ParseHellIsUs(bytes);
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var groups = GameSettings.HellIsUs(Owner, frameGeneration: true).Groups(file);
        var edits = new Dictionary<int, string>();
        foreach (var group in groups)
            foreach (var (index, value) in GameSettings.Plan(file, group, true, kept)) edits[index] = value;
        var optimized = GameSettings.Write(file, edits);

        // The file as optimized: every other line byte for byte.
        var before = Encoding.ASCII.GetString(bytes).Split("\r\n");
        var after = Encoding.ASCII.GetString(optimized).Split("\r\n");
        Assert.Equal(before.Length, after.Length);
        for (var i = 0; i < before.Length; i++)
            if (!edits.ContainsKey(i)) Assert.Equal(before[i], after[i]);
        Assert.Contains("sg.ResolutionQuality=60", after);
        Assert.Contains("FrameRateLimit=300.000000", after);

        var now = GameSettings.ParseHellIsUs(optimized);
        var undo = new Dictionary<int, string>();
        foreach (var group in GameSettings.HellIsUs(Owner, frameGeneration: true).Groups(now))
            foreach (var (index, value) in GameSettings.Plan(now, group, false, kept)) undo[index] = value;
        Assert.Equal(bytes, GameSettings.Write(now, undo));
        Assert.Empty(kept);
    }

    [Fact]
    public void ALevelChangedInTheGameSinceStaysThePlayers()
    {
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var file = GameSettings.ParseHellIsUs(Fixture());
        var group = GameSettings.HellIsUs(Owner).Groups(file).Single(g => g.Id == "quality");
        var written = GameSettings.Write(file, GameSettings.Plan(file, group, true, kept));
        var changed = GameSettings.ParseHellIsUs(Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(written).Replace("sg.ShadowQuality=2", "sg.ShadowQuality=4", StringComparison.Ordinal)));
        var undo = GameSettings.Plan(changed, group, false, kept);
        Assert.DoesNotContain(changed.Settings.Single(l => l.Name == "sg.ShadowQuality").Index, undo.Keys);
    }

    [Fact]
    public void TheProfileNeverCapsFramesAndReadsTheGamesOwnFile()
    {
        var profile = GameSettings.HellIsUs(Owner, frameGeneration: true);
        var keys = profile.Groups(GameSettings.ParseHellIsUs(Fixture())).SelectMany(group => group.Changes).Select(change => change.Key).ToList();
        Assert.DoesNotContain("FrameRateLimit", keys);
        Assert.DoesNotContain("sg.ResolutionQuality", keys);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HellIsUs", "Saved", "Config", "Windows", "GameUserSettings.ini"), profile.File);
        Assert.Null(profile.PlayerFiles);
        Assert.Null(profile.Nvidia);
        Assert.Contains("HellIsUs-Win64-Shipping", profile.Processes);
    }

    private string Install(string name, params string[] files)
    {
        var path = Path.Combine(_dir, name);
        foreach (var file in files)
        {
            var at = Path.Combine(path, file);
            Directory.CreateDirectory(Path.GetDirectoryName(at)!);
            File.WriteAllBytes(at, []);
        }
        return path;
    }

    private static GameEntry Game(string path) => new() { Id = "steam:1620730", Title = "Hell is Us", Store = StoreKind.Steam, Installed = true, Path = path };

    private const string Program = @"HellIsUs\Binaries\Win64\HellIsUs-Win64-Shipping.exe";

    [Fact]
    public void TheInstallGetsTheProfileAndTheProgramAnUpdateRewrites()
    {
        var path = Install("Hell Is Us", Program, "HellIsUs.exe");
        var game = Game(path);
        var profile = GameSettings.For(game, Owner);
        Assert.NotNull(profile);
        Assert.Equal((GameSettings.HellIsUsId, "Hell is Us"), (profile.Id, profile.Title));
        Assert.Equal(Path.Combine(path, Program), profile.Program!(game));
        Assert.DoesNotContain(profile.Groups(GameSettings.ParseHellIsUs(Fixture())).SelectMany(g => g.Changes), c => c.Key == "NvidiaFrameGeneration");
    }

    [Fact]
    public void ExosFrameGenerationInTheInstallIsSeen()
    {
        var path = Install("Hell Is Us", Program, @"HellIsUs\Binaries\Win64\exo-frame-gen.json");
        var groups = GameSettings.For(Game(path), Owner)!.Groups(GameSettings.ParseHellIsUs(Fixture()));
        Assert.Contains(groups.SelectMany(g => g.Changes), c => c.Key == "NvidiaFrameGeneration");
    }

    [Fact]
    public void NoOtherGameIsTakenForIt()
    {
        Assert.False(GameSettings.IsHellIsUs(Install("Phantom", @"ProjectPBZ\Binaries\Win64\PBZ.exe")));
        // The folder alone, as an uninstall leaves it, isn't an install.
        Assert.False(GameSettings.IsHellIsUs(Install("Left", @"HellIsUs\Saved\leftover.txt")));
        Assert.NotEqual(GameSettings.HellIsUsId, GameSettings.For(new GameEntry { Id = "steam:1", Title = "Other", Store = StoreKind.Steam, Installed = true, Path = Install("Other", "Other.exe") }, Owner)?.Id);
    }
}
