using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the Steam overlay switch could be written wrong (the fixture follows this PC's
/// localconfig.vdf: the switch under UserLocalConfigStore › apps, tabs, LF):
/// - the key lands in the wrong section (Software › Valve › Steam › apps, which Steam ignores,
///   a same-named key under another game or deeper), or a second copy is added instead of the
///   value being replaced;
/// - a game with no section, or a file with no apps section, is skipped or breaks the braces;
/// - anything else in the file changes (other games, other keys, layout);
/// - a value other than an app id reaches the file;
/// - the plan writes a game that is already right, fights a switch changed in Steam itself (on any later pass),
///   forgets a write Steam has not taken yet, or keeps an old Properties switch over the rule;
/// - the rule leaves the overlay off where a game needs it (purchases through Steam, online play with friends,
///   Remote Play Together, Steam Input) or on for a single-player game that doesn't.
/// </summary>
public sealed class SteamOverlayConfigTests
{
    private const string LocalConfig =
        "\"UserLocalConfigStore\"\n{\n\t\"system\"\n\t{\n\t\t\"InGameOverlayShowFPSCorner\"\t\t\"1\"\n\t}\n" +
        // Where play time lives: a switch here is not Steam's (1593500 is still on).
        "\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
        "\t\t\t\t\t\"1593500\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"OverlayAppEnable\"\t\t\"0\"\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n" +
        "\t\"apps\"\n\t{\n" +
        "\t\t\"730\"\n\t\t{\n\t\t\t\"cloud\"\n\t\t\t{\n\t\t\t\t\"OverlayAppEnable\"\t\t\"1\"\n\t\t\t}\n\t\t}\n" +
        "\t\t\"3768760\"\n\t\t{\n\t\t\t\"OverlayAppEnable\"\t\t\"1\"\n\t\t\t\"Playtime\"\t\t\"12\"\n\t\t}\n" +
        "\t}\n\t\"LastPlayedTimesSyncTime\"\t\t\"1\"\n}\n";

    [Fact]
    public void ReplacesAnExistingSwitchInPlace()
    {
        var written = SteamOverlayConfig.Write(LocalConfig, new Dictionary<string, bool> { ["3768760"] = false });
        Assert.Equal(LocalConfig.Replace("\"OverlayAppEnable\"\t\t\"1\"\n\t\t\t\"Playtime\"", "\"OverlayAppEnable\"\t\t\"0\"\n\t\t\t\"Playtime\""), written);
        Assert.False(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(written), "3768760"));
    }

    [Fact]
    public void ReadsOnlyTheSwitchSteamReads()
    {
        // 1593500 is off only under Software › Valve › Steam › apps, which Steam ignores: it still gets the overlay.
        Assert.Null(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(LocalConfig), "1593500"));
    }

    [Fact]
    public void AddsTheKeyToAGameWhoseOnlySameNamedKeyIsDeeper()
    {
        // 730's "OverlayAppEnable" sits under "cloud": not the game's own switch.
        Assert.Null(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(LocalConfig), "730"));
        var written = SteamOverlayConfig.Write(LocalConfig, new Dictionary<string, bool> { ["730"] = false });
        Assert.Contains("\t\t\t}\n\t\t\t\"OverlayAppEnable\"\t\t\"0\"\n\t\t}\n\t\t\"3768760\"", written);
        Assert.False(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(written), "730"));
        Assert.True(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(written), "3768760"));
        Assert.Equal(LocalConfig, written.Replace("\t\t\t}\n\t\t\t\"OverlayAppEnable\"\t\t\"0\"\n", "\t\t\t}\n"));
    }

    [Fact]
    public void AddsAGameAndMissingSectionsInSteamsLayout()
    {
        var written = SteamOverlayConfig.Write(LocalConfig, new Dictionary<string, bool> { ["1593500"] = false });
        Assert.Contains("\t\t\"1593500\"\n\t\t{\n\t\t\t\"OverlayAppEnable\"\t\t\"0\"\n\t\t}\n\t}\n\t\"LastPlayedTimesSyncTime\"", written);
        Assert.False(SteamOverlayConfig.Read(SteamOverlayConfig.Apps(written), "1593500"));
        // The ignored copy under Software stays as it was.
        Assert.Contains("\t\t\t\t\t\"1593500\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"OverlayAppEnable\"\t\t\"0\"\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}", written);

        const string bare = "\"UserLocalConfigStore\"\n{\n\t\"system\"\n\t{\n\t}\n}\n";
        var built = SteamOverlayConfig.Write(bare, new Dictionary<string, bool> { ["10"] = false, ["20"] = true });
        var apps = SteamOverlayConfig.Apps(built);
        Assert.False(SteamOverlayConfig.Read(apps, "10"));
        Assert.True(SteamOverlayConfig.Read(apps, "20"));
        Assert.Equal(built.Count(c => c == '{'), built.Count(c => c == '}'));
        Assert.StartsWith("\"UserLocalConfigStore\"\n{\n\t\"system\"\n\t{\n\t}\n\t\"apps\"\n\t{\n", built);
        // Written twice, still one key.
        Assert.Equal(built, SteamOverlayConfig.Write(built, new Dictionary<string, bool> { ["10"] = false }));
    }

    [Fact]
    public void OverlayWritesPreserveExistingEulaVersionsWithoutAddingConsent()
    {
        var consented = SteamOverlayConfig.SetValue(LocalConfig,
            ["UserLocalConfigStore", "Software", "Valve", "Steam", "apps", "1593500"], "1593500_eula_0", "4");
        var wanted = new OverlayPass.Wanted(new Dictionary<string, bool> { ["3768760"] = false },
            new Dictionary<string, string> { ["3768760"] = "off?" }, new Dictionary<string, string>());
        var written = OverlayPass.Written(consented, wanted).Text;
        Assert.Equal(consented, SteamOverlayConfig.Write(written, new Dictionary<string, bool> { ["3768760"] = true }));
        Assert.Equal("4", SteamKeyValues.ParseText(written)
            .Section("UserLocalConfigStore/Software/Valve/Steam/apps/1593500").Value("1593500_eula_0"));
        Assert.DoesNotContain("3768760_eula_1", written);
    }

    [Fact]
    public void RefusesAnythingButAnAppId()
    {
        Assert.Throws<ArgumentException>(() => SteamOverlayConfig.Write(LocalConfig, new Dictionary<string, bool> { ["730\"\n}"] = false }));
        Assert.Throws<ArgumentException>(() => SteamOverlayConfig.Write(LocalConfig, new Dictionary<string, bool> { [""] = false }));
    }

    [Fact]
    public void TheOverlayIsOnOnlyForGamesThatNeedIt()
    {
        // Dota 2 and CS2 sell items through Steam's own window.
        Assert.True(SteamOverlayConfig.Needed(SteamInputConfigTests.DotaApp, steamInput: false));
        Assert.True(SteamOverlayConfig.Needed(SteamInputConfigTests.Cs2App, steamInput: false));
        // Hollow Knight and God of War, single-player: off.
        Assert.False(SteamOverlayConfig.Needed(SteamInputConfigTests.HollowKnightApp, steamInput: false));
        Assert.False(SteamOverlayConfig.Needed(SteamInputConfigTests.GodOfWarApp, steamInput: false));
        // Overcooked! 2 (Remote Play Together, online co-op), and Remote Play Together alone.
        Assert.True(SteamOverlayConfig.Needed(SteamInputConfigTests.App("Game", "full", 2, 1, 49, 36, 37, 9, 38, 39, 24, 22, 28, 29, 23, 41, 42, 43, 44, 62), steamInput: false));
        Assert.True(SteamOverlayConfig.Needed(SteamInputConfigTests.App("Game", "full", 2, 44), steamInput: false));
        // Online co-op alone: friends join and are invited through the overlay.
        Assert.True(SteamOverlayConfig.Needed(SteamInputConfigTests.App("Game", "full", 2, 38), steamInput: false));
        // A single-player game whose controller Steam Input maps: Steam Input works through the overlay.
        Assert.True(SteamOverlayConfig.Needed(SteamInputConfigTests.HollowKnightApp, steamInput: true));
        Assert.False(SteamOverlayConfig.Needed(SteamInputConfigTests.RedistributablesApp, steamInput: false));
    }

    [Fact]
    public void PlanWritesOnlyWhatDiffersAndNeverFightsSteam()
    {
        var apps = SteamOverlayConfig.Apps(LocalConfig);
        var none = new Dictionary<string, string>();
        // 3768760 (on in Steam) and 1593500 (no key Steam reads, so on) need it off: both written; nothing else.
        var (writes, record) = SteamOverlayConfig.Plan(apps, new Dictionary<string, bool> { ["3768760"] = false, ["1593500"] = false }, none);
        Assert.Equal(["1593500", "3768760"], writes.Keys.Order());
        Assert.All(writes.Values, Assert.False);
        Assert.Equal("off?", record["3768760"]);

        // A game that needs it and has no key is already on; nothing to write.
        Assert.Empty(SteamOverlayConfig.Plan(apps, new Dictionary<string, bool> { ["1593500"] = true }, none).Writes);

        // Exo set it off and Steam has it on again: changed in Steam, so it is the player's, on this pass and the next.
        var adopted = SteamOverlayConfig.Plan(apps, new Dictionary<string, bool> { ["3768760"] = false }, new Dictionary<string, string> { ["3768760"] = "off" });
        Assert.Empty(adopted.Writes);
        Assert.Equal("on!", adopted.Record["3768760"]);
        var later = SteamOverlayConfig.Plan(apps, new Dictionary<string, bool> { ["3768760"] = false }, adopted.Record);
        Assert.Empty(later.Writes);
        Assert.Equal("on!", later.Record["3768760"]);

        // A write Steam has not taken yet ("?") is written again, not adopted back.
        var pending = SteamOverlayConfig.Plan(apps, new Dictionary<string, bool> { ["3768760"] = false }, new Dictionary<string, string> { ["3768760"] = "off?" });
        Assert.False(pending.Writes["3768760"]);

        // A switch turned on in Properties before (Exo wrote it on) gives way to the rule.
        var picked = SteamOverlayConfig.Plan(apps, new Dictionary<string, bool> { ["3768760"] = false }, new Dictionary<string, string> { ["3768760"] = "on" });
        Assert.False(picked.Writes["3768760"]);
        Assert.Equal("off?", picked.Record["3768760"]);

        // The player's choice in Properties ("=") wins over the rule until Steam takes it, and stays theirs after.
        var choice = SteamOverlayConfig.Plan(apps, new Dictionary<string, bool> { ["3768760"] = true }, new Dictionary<string, string> { ["3768760"] = "off=?" });
        Assert.False(choice.Writes["3768760"]);
        Assert.Equal("off=?", choice.Record["3768760"]);
        var taken = SteamOverlayConfig.Plan(SteamOverlayConfig.Apps(SteamOverlayConfig.Write(LocalConfig, choice.Writes)),
            new Dictionary<string, bool> { ["3768760"] = true }, new Dictionary<string, string> { ["3768760"] = "off=" });
        Assert.Empty(taken.Writes);
        Assert.Equal("off=", taken.Record["3768760"]);

        // Turned back on in Steam itself afterwards: Steam's switch is the player's from then on.
        var overruled = SteamOverlayConfig.Plan(apps, new Dictionary<string, bool> { ["3768760"] = false }, new Dictionary<string, string> { ["3768760"] = "off=" });
        Assert.Empty(overruled.Writes);
        Assert.Equal("on!", overruled.Record["3768760"]);
    }
}
