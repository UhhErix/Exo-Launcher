using System.Text;
using ExoLauncher.Models;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the table of games can go wrong:
/// - a line name doesn't match the game's own file, so Optimize writes less than it says: every recipe runs against a fixture
///   of its file and must find all it asks for, on every kind of card, and end Applied once written;
/// - a write changes a byte outside the lines it set, or breaks the file's format;
/// - a value differs from BenchmarKing's list, a strong card doesn't get his upper value, or 8 GB doesn't get his 8 GB one;
/// - a frame cap or a V-Sync line creeps in;
/// - a game is taken for another (Steam by the wrong app, a store's install by the wrong program), or the Xbox app's copy, which
///   keeps its config elsewhere, gets a profile;
/// - a file Steam Cloud holds is written (Sekiro's GraphicsConfig.xml, Little Nightmares Enhanced Edition's ini);
/// - Cyberpunk's value and its index part company; the file holds both and the game trusts both.
/// Fixtures (Fixtures/Catalog), each as its source shows it:
/// - rdr2: github.com/Forceflow/rdr2_settings_parser's real file;
/// - cyberpunk2077: github.com/dariulone/cyberpunk-vr-port's real file (version 140), the controls trimmed;
/// - alanwake2: TechPowerUp's print of the whole file;
/// - dyinglightbeast, dyinglight2: Steam guides quoting the file and its comments;
/// - crimsondesert: the menu lines of a Steam discussion's file;
/// - outlaws: a player's file from a Steam discussion;
/// - ue.Remnant2: a player's file from a Steam discussion;
/// - ue.Altar: a player's file from a Steam discussion, which trimmed the [ScalabilityGroups] header (added back);
/// - ue.MGSDelta: the block pcoptimizedsettings.com quotes, with the engine's window lines added below it, unseen for this game;
/// - cs2: a player's file attached to a GitHub issue; poe2: a pastebin of the file.
/// </summary>
public sealed class CatalogProfileTests
{
    private static readonly GameSettings.Pc Ordinary8 = new("nvidia", 5, 8, 1, 165, Gpu: "NVIDIA GeForce RTX 3070");
    private static readonly GameSettings.Pc Strong16 = new("nvidia", 8, 16, 1, 240, Gpu: "NVIDIA GeForce RTX 4080", HdrPeak: 1000);
    private static readonly GameSettings.Pc Radeon8 = new("amd", 8, 8, 1, 144, Gpu: "AMD Radeon RX 6600");
    private static readonly GameSettings.Pc Fullscreen12 = new("nvidia", 8, 12, 1, 144, Gpu: "NVIDIA GeForce RTX 4070 SUPER", Fullscreen: true);
    private static readonly GameSettings.Pc Unknown = new(null, null, null);

    private static GameSettings.Recipe Recipe(string id) => Assert.Single(GameSettings.Catalog, recipe => recipe.Id == id);

    private static byte[] Fixture(string id)
    {
        var name = Assert.Single(Directory.GetFiles(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "Catalog"), id + ".*"));
        return System.IO.File.ReadAllBytes(name);
    }

    /// <summary>Optimize as Exo runs it: every group on, each file line it sets, and the file it leaves.</summary>
    private static (Dictionary<string, string> Set, GameSettings.SettingsFile After, byte[] Bytes) Apply(GameSettings.Recipe recipe, GameSettings.Pc pc)
    {
        var profile = GameSettings.ProfileOf(recipe, pc);
        var bytes = Fixture(recipe.Id);
        var file = profile.Format!(bytes);
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var edits = new Dictionary<int, string>();
        var set = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in profile.Groups(file))
        {
            foreach (var (line, value) in GameSettings.Targets(file, group)) set[line.Name] = value;
            foreach (var (index, value) in GameSettings.Plan(file, group, true, kept)) edits[index] = value;
        }
        var written = GameSettings.Write(file, edits);
        return (set, profile.Format!(written), written);
    }

    public static TheoryData<string> Ids()
    {
        var ids = new TheoryData<string>();
        foreach (var recipe in GameSettings.Catalog) ids.Add(recipe.Id);
        return ids;
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void ItFindsAllItAsksForAndEndsApplied(string id)
    {
        foreach (var pc in new[] { Ordinary8, Strong16, Radeon8, Fullscreen12, Unknown })
        {
            var recipe = Recipe(id);
            var profile = GameSettings.ProfileOf(recipe, pc);
            var (_, after, _) = Apply(recipe, pc);
            List<GameSettings.OpenFile> files = [new("", "", after)];
            Assert.Empty(GameSettings.Missing(profile, files));
            var kept = new Dictionary<string, GameSettings.Remembered>();
            var keptOn = profile.Groups(after).Select(group => group.Id).ToList();
            Assert.Equal(GameSettings.Standing.Applied, GameSettings.Stands(GameSettings.States(profile, files, kept, keptOn), 0));
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void OnlyItsLinesChange(string id)
    {
        var recipe = Recipe(id);
        var bytes = Fixture(id);
        var profile = GameSettings.ProfileOf(recipe, Strong16);
        Assert.Equal(bytes, GameSettings.Write(profile.Format!(bytes), new Dictionary<int, string>()));
        var (_, after, written) = Apply(recipe, Strong16);
        var before = Encoding.Latin1.GetString(bytes).Split('\n');
        var now = Encoding.Latin1.GetString(written).Split('\n');
        Assert.Equal(before.Length, now.Length);
        var theirs = after.Settings.Select(line => line.Index).ToHashSet();
        for (var i = 0; i < before.Length; i++)
            if (!theirs.Contains(i)) Assert.Equal(before[i], now[i]);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void NoFrameCapAndNoVSync(string id)
    {
        foreach (var pc in new[] { Ordinary8, Strong16, Radeon8, Fullscreen12 })
            foreach (var change in GameSettings.GroupsOf(Recipe(id), pc).SelectMany(group => group.Changes))
                Assert.DoesNotMatch("(?i)vsync|framerate|frame_?cap|fps|limit", change.Key);
    }

    [Fact]
    public void RedDeadTakesHisValuesForTheCard()
    {
        var ordinary = Apply(Recipe("rdr2"), Ordinary8).Set;
        var strong = Apply(Recipe("rdr2"), Strong16).Set;
        Assert.Equal("kSettingLevel_High", ordinary["shadowQuality"]);
        Assert.Equal("kSettingLevel_Ultra", strong["shadowQuality"]);
        Assert.Equal("kSettingLevel_Medium", ordinary["lightingQuality"]);
        Assert.Equal("kSettingLevel_High", strong["lightingQuality"]);
        Assert.Equal("kSettingLevel_Medium", strong["reflectionQuality"]);
        Assert.Equal("false", strong["treeTessellationEnabled"]);
        Assert.Equal("2", ordinary["windowed"]);
        Assert.Equal("0", Apply(Recipe("rdr2"), Fullscreen12).Set["windowed"]);
        Assert.Equal(GameSettings.Borderless, GameSettings.ProfileOf(Recipe("rdr2"), Ordinary8).WindowMode);
    }

    [Fact]
    public void CyberpunksWordAndPlaceStayTogether()
    {
        var recipe = Recipe("cyberpunk2077");
        var (set, after, written) = Apply(recipe, Strong16);
        Assert.Equal(("High", "3"), (set["LocalShadowsQuality"], set["LocalShadowsQuality.index"]));
        Assert.Equal(("Medium", "2"), (set["AmbientOcclusion"], set["AmbientOcclusion.index"]));
        var (ordinary, _, _) = Apply(recipe, Ordinary8);
        Assert.Equal(("Low", "1"), (ordinary["AmbientOcclusion"], ordinary["AmbientOcclusion.index"]));

        // The file itself still says each word is at that place in the option's own list.
        using var document = System.Text.Json.JsonDocument.Parse(written);
        var options = document.RootElement.GetProperty("data").EnumerateArray().SelectMany(group => group.GetProperty("options").EnumerateArray()).ToList();
        foreach (var option in options.Where(option => option.TryGetProperty("values", out _) && set.ContainsKey(option.GetProperty("name").GetString() + ".index")))
        {
            var list = option.GetProperty("values").EnumerateArray().ToList();
            Assert.Equal(list[option.GetProperty("index").GetInt32()].ToString(), option.GetProperty("value").ToString());
        }
        Assert.Equal(2, after.Settings.Count(line => line.Name == "CrowdDensity"));
        Assert.Equal("true", set["ContactShadows"]);
        Assert.Null(GameSettings.ProfileOf(recipe, Ordinary8).WindowMode);
    }

    [Fact]
    public void AlanWakeKeepsRayTracingFromTwelveGigabytes()
    {
        Assert.Equal("0", Apply(Recipe("alanwake2"), Ordinary8).Set["m_ePTQuality"]);
        Assert.False(Apply(Recipe("alanwake2"), Strong16).Set.ContainsKey("m_ePTQuality"));
        Assert.Equal("2", Apply(Recipe("alanwake2"), Ordinary8).Set["m_eSSAAMethod"]);
        Assert.Equal("3", Apply(Recipe("alanwake2"), Radeon8).Set["m_eSSAAMethod"]);
        Assert.False(Apply(Recipe("alanwake2"), Unknown).Set.ContainsKey("m_eSSAAMethod"));
        Assert.Equal("false", Apply(Recipe("alanwake2"), Ordinary8).Set["m_bLensDistortion"]);
        Assert.Null(GameSettings.ProfileOf(Recipe("alanwake2"), Ordinary8).WindowMode);
    }

    [Fact]
    public void DyingLightTheBeastWritesInsideItsBrackets()
    {
        var (set, _, written) = Apply(Recipe("dyinglightbeast"), Strong16);
        var text = Encoding.Latin1.GetString(written);
        Assert.Contains("TextureQuality(\"Medium\")", text, StringComparison.Ordinal);
        Assert.Contains("ReflectionsQuality(2)", text, StringComparison.Ordinal);
        Assert.Equal("150", set["LodMultiplier"]);
        Assert.Equal("130", Apply(Recipe("dyinglightbeast"), Ordinary8).Set["LodMultiplier"]);
        Assert.Equal("2", Apply(Recipe("dyinglightbeast"), Fullscreen12).Set["WindowMode"]);
    }

    [Fact]
    public void DyingLight2LeavesItsCommentsAlone()
    {
        var (set, _, written) = Apply(Recipe("dyinglight2"), Ordinary8);
        Assert.Equal(("0", "4", "3"), (set["AmbientOcclusionQuality"], set["ContactShadowsQuality"], set["VoxelGiQuality"]));
        Assert.Equal("2", Apply(Recipe("dyinglight2"), Strong16).Set["AmbientOcclusionQuality"]);
        Assert.Contains("!FogQuality(i)", Encoding.Latin1.GetString(written), StringComparison.Ordinal);
    }

    [Fact]
    public void CrimsonDesertLeavesThePresetForCustom()
    {
        var (set, _, written) = Apply(Recipe("crimsondesert"), Ordinary8);
        Assert.Equal("Medium", set["_shadowQualityLevelSelect"]);
        Assert.Equal("Ultra", Apply(Recipe("crimsondesert"), Strong16).Set["_shadowQualityLevelSelect"]);
        Assert.Contains("Template=\"/OS_M_Windows::Custom.engineoptiongraphics\"", Encoding.Latin1.GetString(written), StringComparison.Ordinal);
        Assert.Contains("_enableAdvancedWeatherEffect\" _value=\"True\"", Encoding.Latin1.GetString(written), StringComparison.Ordinal);
    }

    [Fact]
    public void OutlawsLeavesItsHashAlone()
    {
        var (set, _, written) = Apply(Recipe("outlaws"), Strong16);
        Assert.Equal("Medium", set["particle quality"]);
        Assert.Equal("Low", Apply(Recipe("outlaws"), Ordinary8).Set["particle quality"]);
        var text = Encoding.Latin1.GetString(written);
        Assert.Contains("hash = 500912139", text, StringComparison.Ordinal);
        Assert.Contains("[\"particle quality\"] = \"Medium\",", text, StringComparison.Ordinal);
        Assert.Contains("[\"shadow proxies\"] = true,", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OblivionFollowsTheCardAndItsMemory()
    {
        var ordinary = Apply(Recipe("ue.Altar"), Ordinary8).Set;
        Assert.Equal(("1", "0", "1"), (ordinary["sg.ShadowQuality"], ordinary["sg.TextureQuality"], ordinary["FullscreenMode"]));
        var strong = Apply(Recipe("ue.Altar"), Strong16).Set;
        Assert.Equal(("2", "2", "2"), (strong["sg.ShadowQuality"], strong["sg.TextureQuality"], strong["sg.PostProcessQuality"]));
        Assert.Equal("0", Apply(Recipe("ue.Altar"), Fullscreen12).Set["PreferredFullscreenMode"]);
        Assert.Equal(GameSettings.Fullscreen, GameSettings.ProfileOf(Recipe("ue.Altar"), Fullscreen12).WindowMode);
        // A memory unknown leaves textures alone.
        Assert.False(Apply(Recipe("ue.Altar"), Unknown).Set.ContainsKey("sg.TextureQuality"));
        // No HDR line in this fixture: a spare, so no less Applied.
        Assert.Contains(GameSettings.GroupsOf(Recipe("ue.Altar"), Strong16), group => group.Id == "hdr");
    }

    [Fact]
    public void MetalGearKeepsHisTexturesAt8Gb()
    {
        Assert.Equal("2", Apply(Recipe("ue.MGSDelta"), Ordinary8).Set["sg.TextureQuality"]);
        Assert.Equal("3", Apply(Recipe("ue.MGSDelta"), Strong16).Set["sg.TextureQuality"]);
        Assert.Equal(("1", "2"), (Apply(Recipe("ue.MGSDelta"), Ordinary8).Set["sg.ShadowQuality"], Apply(Recipe("ue.MGSDelta"), Strong16).Set["sg.ShadowQuality"]));
    }

    [Fact]
    public void RemnantHasNoTextureAdviceToFollow()
    {
        Assert.Equal(("2", "3"), (Apply(Recipe("ue.Remnant2"), Ordinary8).Set["sg.FoliageQuality"], Apply(Recipe("ue.Remnant2"), Strong16).Set["sg.FoliageQuality"]));
        Assert.False(Apply(Recipe("ue.Remnant2"), Radeon8).Set.ContainsKey("sg.TextureQuality"));
        Assert.Equal("1", Apply(Recipe("ue.Remnant2"), Radeon8).Set["FullscreenMode"]);
    }

    [Fact]
    public void CounterStrikeHasItsWindowModeAlone()
    {
        Assert.Equal(("0", "1"), (Apply(Recipe("cs2"), Ordinary8).Set["setting.fullscreen"], Apply(Recipe("cs2"), Ordinary8).Set["setting.nowindowborder"]));
        Assert.Equal(("1", "0"), (Apply(Recipe("cs2"), Fullscreen12).Set["setting.fullscreen"], Apply(Recipe("cs2"), Fullscreen12).Set["setting.nowindowborder"]));
        Assert.Equal(2, Apply(Recipe("cs2"), Ordinary8).Set.Count);
        // These lines tell it to rebuild the file if they change.
        Assert.Contains("\"VendorID\"\t\t\"4098\"", Encoding.Latin1.GetString(Apply(Recipe("cs2"), Ordinary8).Bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void PathOfExileHasDlssOnlyWhereItsSpellingIsKnown()
    {
        Assert.Equal(("DLSS", "Quality"), (Apply(Recipe("poe2"), Ordinary8).Set["upscale"], Apply(Recipe("poe2"), Ordinary8).Set["upscale_quality"]));
        Assert.False(Apply(Recipe("poe2"), Radeon8).Set.ContainsKey("upscale"));
        Assert.Equal(("true", "false"), (Apply(Recipe("poe2"), Fullscreen12).Set["fullscreen"], Apply(Recipe("poe2"), Fullscreen12).Set["borderless_windowed_fullscreen"]));
    }

    private static GameEntry Entry(string id, StoreKind store, string path) => new() { Id = id, Title = "T", Store = store, Installed = true, Path = path };

    [Fact]
    public void AGameIsKnownByItsAppOrItsProgram()
    {
        var folder = Directory.CreateTempSubdirectory("exo-catalog-").FullName;
        try
        {
            Assert.Equal("rdr2", GameSettings.RecipeFor(Entry("steam:1174180", StoreKind.Steam, folder))?.Id);
            Assert.Null(GameSettings.RecipeFor(Entry("steam:1", StoreKind.Steam, folder)));
            // A store's copy needs its program, and the Xbox app's never counts.
            Assert.Null(GameSettings.RecipeFor(Entry("epic:x", StoreKind.Epic, folder)));
            System.IO.File.WriteAllBytes(Path.Combine(folder, "PlayRDR2.exe"), []);
            Assert.Equal("rdr2", GameSettings.RecipeFor(Entry("rockstar:x", StoreKind.Rockstar, folder))?.Id);
            Assert.Null(GameSettings.RecipeFor(Entry("xbox:x", StoreKind.Xbox, folder)));
        }
        finally { DeletionGate.DeleteDirectory(folder, recursive: true); }
    }

    [Fact]
    public void AFileSteamCloudHoldsIsNoFileOfOurs()
    {
        string[] cache =
        [
            "\"2149010\"", "{", "\t\"ChangeNumber\"\t\t\"7\"", "\t\"Little Nightmares Enhanced Edition/Saved/Config/WindowsNoEditor/GameUserSettings.ini\"", "\t{", "\t\t\"root\"\t\t\"11\"", "\t}", "}",
        ];
        Assert.True(GameSettings.CloudLists(cache, @"C:\Users\Someone\AppData\Local\Little Nightmares Enhanced Edition\Saved\Config\WindowsNoEditor\GameUserSettings.ini"));
        Assert.False(GameSettings.CloudLists(cache, @"C:\Users\Someone\AppData\Local\Little Nightmares Enhanced Edition\Saved\Config\WindowsNoEditor\Engine.ini"));
        Assert.False(GameSettings.CloudLists(cache, @"C:\Users\Someone\AppData\Local\Another\Saved\Config\Windows\GameUserSettings.ini"));
        Assert.True(GameSettings.CloudLists(["\t\"Sekiro/GraphicsConfig.xml\""], @"C:\Users\Someone\AppData\Roaming\Sekiro\GraphicsConfig.xml"));
    }

    [Fact]
    public void AUtf16FileHasNoLinesRatherThanBrokenOnes()
    {
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<windowed value=\"2\" />\r\n")).ToArray();
        var profile = GameSettings.ProfileOf(Recipe("rdr2"), Ordinary8);
        var file = profile.Format!(utf16);
        Assert.Empty(file.Settings);
        Assert.Equal(utf16, GameSettings.Write(file, new Dictionary<int, string>()));
    }
}
