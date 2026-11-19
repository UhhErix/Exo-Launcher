using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo's own HDR verdict can go wrong, each silent in the app (HDR simply comes on, or doesn't):
/// - Unreal's bUseHDRDisplayOutput and HDRDisplayOutputNits, which every Unreal game writes, are taken for HDR support, so
///   an SDR Unreal game (Marvel Rivals) switches Windows HDR on; or bUseHDRDisplayOutput=True is missed;
/// - an engine's HDR line is misread: Call of Duty's "HDR@ids = Automatic // one of Off, On, Automatic", Frostbite's
///   "GstRender.HighDynamicRangeOutput 1", Square Enix's &lt;isFullscreenHDR_ v="false"/&gt;, a registry DWORD, Unity's
///   hashed PlayerPrefs name, an Unreal settings save's property;
/// - HDR that isn't the screen's counts: older games' HDR lighting and bloom (bDoHighDynamicRange, HDRBloom,
///   mat_hdr_level), HDR audio (Lyra's bUseHDRAudioMode), an HDRI sky, a word that only contains the letters (withdraw);
/// - the wiki or the weak file signals overrule the game's own settings or what Exo learned, or the weak signals decide
///   while a stronger source says something;
/// - the weak signals say no (God of War and FINAL FANTASY XVI have HDR and no HDR names in their programs), or the program
///   scan misses a name across its 4 MB pieces or reads past its limit;
/// - the HDR peak goes in outside Unreal's range, in another section, or Put back doesn't return the game's own value.
/// </summary>
public sealed class GameHdrTests
{
    private static GameHdr.Signal Read(string text) => GameHdr.FromPairs(GameHdr.Pairs(text));

    // ------------------------------------------------------------------ Unreal

    /// <summary>This PC's Phantom Blade Zero GameUserSettings.ini (no personal data in it), CRLF.</summary>
    private static string PhantomBlade() =>
        File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "PhantomBladeZero", "GameUserSettings.ini")).ReplaceLineEndings("\r\n");

    [Fact]
    public void Unreals_own_hdr_lines_say_only_on()
    {
        // As the game wrote it: HDR off, the engine's default 1000 nits. Every Unreal game has these, HDR or not.
        Assert.Equal(new GameHdr.Signal(null, false), Read(PhantomBlade()));
        // Turned on in the game's menu: it has HDR and it is on.
        Assert.Equal(new GameHdr.Signal(true, true), Read(PhantomBlade().Replace("bUseHDRDisplayOutput=False", "bUseHDRDisplayOutput=True", StringComparison.Ordinal)));
        // Hell is Us writes 0 nits until HDR is touched: still nothing.
        Assert.Equal(new GameHdr.Signal(null, false), Read("[/Script/Charlie.CharlieGameUserSettings]\r\nbUseHDRDisplayOutput=False\r\nHDRDisplayOutputNits=0\r\n"));
        // Forced in Engine.ini by the player.
        Assert.Equal(new GameHdr.Signal(true, true), Read("[SystemSettings]\r\nr.HDR.EnableHDROutput=1\r\nr.HDR.Display.OutputDevice=5\r\n"));
    }

    [Fact]
    public void An_unreal_settings_save_naming_an_hdr_property_has_hdr()
    {
        // Stellar Blade's StellarBladeSetting.sav as on this PC: its own "EVAS" wrapper, GVAS, then property names as NUL-ended strings.
        static byte[] Save(string name) =>
            [.. "EVAS"u8, 1, 0, 0, 0, .. "GVAS"u8, 2, 0, 0, 0, .. Encoding.ASCII.GetBytes("\u0018\0\0\0" + name + "\0"), 0x0D, 0, 0, 0, .. "IntProperty\0"u8];
        Assert.Equal(new GameHdr.Signal(true), GameHdr.FromSave(Save("HDRAddtionalMaxLumiance")));
        Assert.Equal(new GameHdr.Signal(true), GameHdr.FromSave(Save("HDRAddtionalMaxLumiance")[8..]));
        Assert.Equal(GameHdr.Nothing, GameHdr.FromSave(Save("HDRI_SkyIntensity")));
        Assert.Equal(GameHdr.Nothing, GameHdr.FromSave(Save("MouseSensitivity")));
        // Not a GVAS file: not read as one.
        Assert.Equal(GameHdr.Nothing, GameHdr.FromSave([.. "SAVE"u8, .. "HDRMode\0"u8]));
    }

    [Fact]
    public void Unreal_projects_are_the_folders_with_binaries_beside_the_engine()
    {
        var root = Path.Combine(TestDataDirectory.Root, "ue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Engine", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(root, "HellIsUs", "Binaries", "Win64"));
        Directory.CreateDirectory(Path.Combine(root, "MarvelGame", "Engine"));
        Directory.CreateDirectory(Path.Combine(root, "MarvelGame", "Marvel", "Binaries", "Win64"));
        Assert.Equal(["HellIsUs", "Marvel"], GameHdr.UnrealProjects(root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        // Binaries without Unreal's Engine folder is some other engine.
        var other = Path.Combine(TestDataDirectory.Root, "other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(other, "Game", "Binaries", "Win64"));
        Assert.Empty(GameHdr.UnrealProjects(other));
    }

    // ------------------------------------------------------------------ other engines

    [Theory]
    // Call of Duty's system file (s.1.0.cod26.txt on this PC): Automatic follows Windows HDR, so it's on.
    [InlineData("// Set HDR activation mode. Option only takes effect on HDR Display.\nHDR@0;3781;43096 = Automatic // one of Off, On, Automatic\n", true, true)]
    [InlineData("HDR@0;3781;43096 = Off // one of Off, On, Automatic\n", true, false)]
    // Its account file: HDR settings that aren't the switch.
    [InlineData("// HDR Max\nHDRMaxLum@0;52557;45613 = 0.800000 // 0.600000 to 1.000000\n\nHDRUseWindowsDisplayMapping@0;57752;20945 = true\n", true, null)]
    // Frostbite's PROFSAVE_profile.
    [InlineData("GstRender.FullscreenMode 1\nGstRender.HighDynamicRangeOutput 1\nGstRender.ResolutionScale 1.000000\n", true, true)]
    [InlineData("GstRender.HighDynamicRangeOutput 0\n", true, false)]
    // RE Engine's config.ini.
    [InlineData("[Display]\r\nHDRMode=On\r\nFrameRate=Variable\r\n", true, true)]
    // FINAL FANTASY XVI's config.xml (this PC's line).
    [InlineData("<config>\n  <isFullscreenHDR_ v=\"false\"/>\n  <isVsync_ v=\"false\"/>\n</config>", true, false)]
    // A JSON settings file.
    [InlineData("{ \"Resolution\": \"2560x1440\", \"EnableHDR\": true }", true, true)]
    public void Each_engines_hdr_line_reads(string text, bool? has, bool? on) =>
        Assert.Equal(new GameHdr.Signal(has, on), Read(text));

    [Fact]
    public void Registry_settings_read_by_name()
    {
        // 007 First Light (Glacier): HKCU\Software\IO Interactive\007 First Light, as on this PC.
        Assert.Equal(new GameHdr.Signal(true, true), GameHdr.FromPairs([("HDR", "1"), ("HDRBrightness", "1132068864"), ("AutoHDROptOutApplicable", "0")]));
        // Marvel's Spider-Man Remastered (Insomniac): ...\Graphics, HDR off.
        Assert.Equal(new GameHdr.Signal(true, false), GameHdr.FromPairs([("HDR", "0"), ("HDRMaxNits", "270000"), ("HDRPaperWhite", "1128792064")]));
        // Unity's PlayerPrefs, each name with its hash.
        Assert.Equal(new GameHdr.Signal(true, true), GameHdr.FromPairs([("HDR_h2089530843", "1"), ("Screenmanager Resolution Width_h182942802", "2560")]));
        Assert.Equal(GameHdr.Nothing, GameHdr.FromPairs([("Screenmanager Resolution Width_h182942802", "2560"), ("UnitySelectMonitor_h17969598", "0")]));
    }

    [Fact]
    public void Hdr_that_isnt_the_screens_says_nothing()
    {
        Assert.Equal(GameHdr.Nothing, GameHdr.FromPairs([
            ("bDoHighDynamicRange", "1"), ("HDRBloom", "1"), ("bHDRLighting", "1"), ("mat_hdr_level", "2"), ("bUseHDRAudioMode", "True"),
            ("HDRIIntensity", "1.0"), ("bWithdrawOnRetreat", "1"), ("ThreadedRendering", "1"),
        ]));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("True", true)]
    [InlineData("\"Automatic\"", true)]
    [InlineData("Off", false)]
    [InlineData("0", false)]
    [InlineData("2", null)]
    [InlineData("1000.000000", null)]
    public void A_switch_reads_on_or_off(string value, bool? on) => Assert.Equal(on, GameHdr.OnOff(value));

    [Fact]
    public void A_utf16_settings_file_reads_like_any_other() =>
        Assert.Equal(new GameHdr.Signal(true, true), Read(GameHdr.Text([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("[Video]\r\nHDROutput=1\r\n")])));

    [Fact]
    public void Folder_names_compare_by_letters_and_digits()
    {
        Assert.Equal("marvelsspidermanremastered", GameHdr.Key("Marvel's Spider-Man Remastered"));
        Assert.Equal(GameHdr.Key("Stellar Blade™"), GameHdr.Key("StellarBlade"));
    }

    // ------------------------------------------------------------------ the verdict

    [Fact]
    public void The_games_own_settings_beat_everything_and_the_wiki_is_a_fallback()
    {
        GameHdr.Signal silent = GameHdr.Nothing, has = new(true, false), weakYes = new(true);
        // The game's settings over the wiki and the files.
        Assert.Equal(new GameHdr.Verdict("yes", "settings", false), GameHdr.Merge(has, false, "false", silent));
        // What Exo learned over the wiki.
        Assert.Equal(new GameHdr.Verdict("yes", "learned", null), GameHdr.Merge(silent, true, "false", silent));
        // The wiki only when the game says nothing; hackable is no.
        Assert.Equal(new GameHdr.Verdict("yes", "wiki", null), GameHdr.Merge(silent, false, "true", silent));
        Assert.Equal(new GameHdr.Verdict("no", "wiki", null), GameHdr.Merge(silent, false, "hackable", weakYes));
        Assert.Equal(new GameHdr.Verdict("no", "wiki", false), GameHdr.Merge(new(null, false), false, "false", silent));
        // The weak signals last.
        Assert.Equal(new GameHdr.Verdict("yes", "files", null), GameHdr.Merge(silent, false, "unknown", weakYes));
        Assert.Equal(new GameHdr.Verdict("yes", "settings", null), GameHdr.Merge(new(true), false, "unknown", weakYes));
        Assert.Equal(new GameHdr.Verdict("unknown", null, null), GameHdr.Merge(silent, false, "unknown", silent));
        Assert.Null(GameHdr.Supports(new("unknown", null, null)));
    }

    [Fact]
    public void Many_sources_have_hdr_when_any_does_and_on_wins()
    {
        Assert.Equal(new GameHdr.Signal(true, true), GameHdr.Combine([new(null, false), new(true, true), GameHdr.Nothing]));
        Assert.Equal(new GameHdr.Signal(null, false), GameHdr.Combine([new(null, false), GameHdr.Nothing]));
        Assert.Equal(GameHdr.Nothing, GameHdr.Combine([]));
    }

    // ------------------------------------------------------------------ weak: the program

    [Fact]
    public void The_program_scan_finds_a_name_anywhere_up_to_its_limit()
    {
        static MemoryStream With(byte[] name, int at)
        {
            var bytes = new byte[(9 << 20) + 100];
            name.CopyTo(bytes, at);
            return new MemoryStream(bytes);
        }
        var ascii = Encoding.ASCII.GetBytes("ST2084");
        var wide = Encoding.Unicode.GetBytes("scRGB");
        Assert.True(GameHdr.NamesHdr(With(ascii, 1234), long.MaxValue));
        Assert.True(GameHdr.NamesHdr(With(wide, 8 << 20), long.MaxValue));
        // Across the end of the first piece (4 MB and the longest name, 18 bytes).
        Assert.True(GameHdr.NamesHdr(With(ascii, (4 << 20) + 15), long.MaxValue));
        Assert.True(GameHdr.NamesHdr(With(wide, (4 << 20) + 13), long.MaxValue));
        Assert.False(GameHdr.NamesHdr(new MemoryStream(new byte[(9 << 20) + 100]), long.MaxValue));
        // Past the limit it stops.
        Assert.False(GameHdr.NamesHdr(With(ascii, 8 << 20), 4 << 20));
    }

    // ------------------------------------------------------------------ the peak, written

    [Theory]
    [InlineData(250, "400")]
    [InlineData(650, "650")]
    [InlineData(1015, "1015")]
    [InlineData(4000, "2000")]
    public void The_peak_goes_in_inside_unreals_range(int screen, string written) =>
        Assert.Equal(written, GameSettings.UnrealHdrGroup(screen).Changes.Single().Value);

    [Fact]
    public void The_peak_is_only_its_line_and_put_back_returns_the_games_value()
    {
        var bytes = Encoding.ASCII.GetBytes(PhantomBlade());
        var file = GameSettings.ParsePhantomBlade(bytes);
        var kept = new Dictionary<string, GameSettings.Remembered>();
        var on = GameSettings.Plan(file, GameSettings.UnrealHdrGroup(650), true, kept);
        var written = GameSettings.Write(file, on);
        var before = PhantomBlade().Split("\r\n");
        var after = Encoding.ASCII.GetString(written).Split("\r\n");
        Assert.Equal(["HDRDisplayOutputNits=650"], Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).Select(i => after[i]));
        Assert.Equal(new GameSettings.Remembered("1000", "650"), kept["HDRDisplayOutputNits"]);
        var back = GameSettings.ParsePhantomBlade(written);
        Assert.Equal(bytes, GameSettings.Write(back, GameSettings.Plan(back, GameSettings.UnrealHdrGroup(650), false, kept)));
        // With the screen's peak known, Phantom Blade Zero's Optimize carries it.
        Assert.Contains(GameSettings.PhantomBladeZero(650).Groups(file), group => group.Id == "hdr");
        Assert.DoesNotContain(GameSettings.PhantomBladeZero().Groups(file), group => group.Id == "hdr");
    }

    [Fact]
    public void The_peak_goes_in_the_section_that_holds_it()
    {
        Assert.Equal("/Script/Charlie.CharlieGameUserSettings", GameSettings.SectionHolding([
            "[ScalabilityGroups]", "sg.ResolutionQuality=100", "[/Script/Charlie.CharlieGameUserSettings]", "bUseHDRDisplayOutput=False",
            "HDRDisplayOutputNits=0", "", "[/Script/Engine.GameUserSettings]", "bUseDesiredScreenHeight=False",
        ], GameSettings.UnrealHdrNits));
        Assert.Equal("/Script/ProjectPBZ.PBZSettingsLocal", GameSettings.SectionHolding(PhantomBlade().Split("\r\n"), GameSettings.UnrealHdrNits));
        Assert.Null(GameSettings.SectionHolding(["[SystemSettings]", "r.HDR.EnableHDROutput=1"], GameSettings.UnrealHdrNits));
    }
}
