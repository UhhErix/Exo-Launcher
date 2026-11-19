using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo could get Steam Input wrong for a player:
/// - turning it off for a game that reads only Xbox controllers (a DualSense would then do nothing there);
/// - leaving it off for a game with partial or no controller support, where only Steam Input makes a controller work;
/// - turning it on for a tool that runs beside games (its layout would follow the controller into every game);
/// - losing the value it replaced, so letting go doesn't put Steam back as it was;
/// - writing over a change the player made in Steam after Exo set it, on that pass or any later one;
/// - keeping an old pick from Properties over the rule.
/// The store entries are the shapes in this PC's appinfo.vdf (type, controller_support, categories).
/// </summary>
public sealed class SteamInputConfigTests
{
    private const string GodOfWar = "1593500";   // full controller support, PlayStation support listed
    private const string HollowKnight = "367520"; // full controller support, Xbox controllers only
    private const string Cs2 = "730";             // no controller support listed

    internal static SteamKeyValues App(string type, string? support, params int[] categories) =>
        SteamKeyValues.ParseText("\"common\"\n{\n\"type\"\t\"" + type + "\"\n"
            + (support is null ? "" : "\"controller_support\"\t\"" + support + "\"\n")
            + "\"category\"\n{\n" + string.Concat(categories.Select(c => $"\"category_{c}\"\t\"1\"\n")) + "}\n}\n");

    internal static readonly SteamKeyValues GodOfWarApp = App("Game", "full", 2, 22, 23, 28, 43, 45, 46, 55, 56, 57, 58, 62);
    internal static readonly SteamKeyValues HollowKnightApp = App("Game", "full", 2, 22, 23, 28, 29, 41, 42, 43, 62);
    internal static readonly SteamKeyValues DotaApp = App("game", null, 1, 8, 9, 29, 30, 35, 40, 45, 46, 63, 66, 67, 68);
    internal static readonly SteamKeyValues Cs2App = App("Game", null, 1, 8, 15, 27, 29, 30, 33, 35, 41, 42, 43, 45, 46, 51, 63, 64, 66, 67, 68, 69, 70, 74);
    internal static readonly SteamKeyValues NbaApp = App("Game", "partial", 1, 2, 9, 18, 22, 23, 24, 35, 36, 37, 38, 39, 44, 49, 55, 56, 57, 58, 60, 62);
    internal static readonly SteamKeyValues RedistributablesApp = App("Tool", null);

    private static SteamKeyValues Apps(params (string App, string Value)[] values) =>
        SteamKeyValues.ParseText("\"UserLocalConfigStore\"\n{\n\"apps\"\n{\n"
            + string.Concat(values.Select(v => $"\"{v.App}\"\n{{\n\"UseSteamControllerConfig\"\t\t\"{v.Value}\"\n}}\n")) + "}\n}\n")
        .Section("UserLocalConfigStore/apps");

    private static readonly Dictionary<string, string> None = [];

    [Fact]
    public void TheRuleFollowsEachGamesControllerSupport()
    {
        // Reads an Xbox and a PlayStation controller itself: off.
        Assert.Equal("0", SteamInputConfig.Desired(GodOfWarApp));
        // No controller support listed (Dota 2, CS2) or partial (NBA 2K26): Steam Input maps the controller.
        Assert.Equal("2", SteamInputConfig.Desired(DotaApp));
        Assert.Equal("2", SteamInputConfig.Desired(Cs2App));
        Assert.Equal("2", SteamInputConfig.Desired(NbaApp));
        // Full support for Xbox controllers only: Steam's own default, so a DualSense still works there.
        Assert.Null(SteamInputConfig.Desired(HollowKnightApp));
        // Not a game: left alone.
        Assert.Null(SteamInputConfig.Desired(RedistributablesApp));
        // Support named only by the category (28 full, 18 partial) reads the same.
        Assert.Equal("full", SteamInputConfig.Support(App("Game", null, 28)));
        Assert.Equal("partial", SteamInputConfig.Support(App("Game", null, 18)));
    }

    [Fact]
    public void InUseAsksAboutAPlayStationControllerOnlyWhereSteamsDefaultDecides()
    {
        static bool Never() => throw new InvalidOperationException("not asked");
        Assert.True(SteamInputConfig.InUse("2", Cs2App, Never));
        Assert.False(SteamInputConfig.InUse("0", HollowKnightApp, Never));
        // Steam's default maps a PlayStation controller in a game that can't read one, and leaves an Xbox controller to it.
        Assert.True(SteamInputConfig.InUse(null, HollowKnightApp, () => true));
        Assert.False(SteamInputConfig.InUse(null, HollowKnightApp, () => false));
        Assert.False(SteamInputConfig.InUse("1", GodOfWarApp, () => true));
        Assert.False(SteamInputConfig.InUse(null, RedistributablesApp, () => true));
    }

    [Fact]
    public void ThePlanWritesTheRuleAndRemembersWhatWasThere()
    {
        var rule = new Dictionary<string, string?> { [GodOfWar] = "0", [Cs2] = "2", [HollowKnight] = null };
        var (writes, record, _) = SteamInputConfig.Plan(Apps(), rule, None, None);
        Assert.Equal(new Dictionary<string, string> { [GodOfWar] = "0", [Cs2] = "2", [SteamInputConfig.DesktopLayout] = "0" }, writes);
        Assert.Equal(">0", record[GodOfWar]);   // there was no line
        Assert.Equal(">2", record[Cs2]);
        Assert.False(record.ContainsKey(HollowKnight));
    }

    [Fact]
    public void LettingGoPutsBackExactlyWhatWasThere()
    {
        // God of War uninstalled (no rule now), a game Exo turned off from "2": each goes back; the desktop layout stays off.
        var apps = Apps((GodOfWar, "0"), (Cs2, "0"), (SteamInputConfig.DesktopLayout, "0"));
        var record = new Dictionary<string, string> { [GodOfWar] = ">0", [Cs2] = "2>0", [SteamInputConfig.DesktopLayout] = "1>0" };
        var (writes, next, _) = SteamInputConfig.Plan(apps, new Dictionary<string, string?>(), None, record);
        // God of War had no line: it is taken out again, not set to "1".
        Assert.Equal(new Dictionary<string, string> { [GodOfWar] = "", [Cs2] = "2" }, writes);
        Assert.Equal(new Dictionary<string, string> { [SteamInputConfig.DesktopLayout] = "1>0" }, next);
    }

    [Fact]
    public void AChangeMadeInSteamStaysThePlayersOnEveryLaterPass()
    {
        // Exo set God of War off; the player then turned Steam Input on for it in Steam.
        var apps = Apps((GodOfWar, "2"));
        var rule = new Dictionary<string, string?> { [GodOfWar] = "0" };
        var first = SteamInputConfig.Plan(apps, rule, None, new Dictionary<string, string> { [GodOfWar] = ">0" });
        Assert.False(first.Writes.ContainsKey(GodOfWar));
        Assert.Equal(SteamInputConfig.Players, first.Record[GodOfWar]);
        var second = SteamInputConfig.Plan(apps, rule, None, first.Record);
        Assert.False(second.Writes.ContainsKey(GodOfWar));
        Assert.Equal(SteamInputConfig.Players, second.Record[GodOfWar]);
    }

    [Fact]
    public void AnOldPickFromPropertiesGivesWayToTheRule()
    {
        // Picked Steam Input in Properties before (Exo wrote "2"); the rule says the game reads the controller itself.
        var (writes, record, _) = SteamInputConfig.Plan(Apps((GodOfWar, "2")), new Dictionary<string, string?> { [GodOfWar] = "0" }, None,
            new Dictionary<string, string> { [GodOfWar] = ">2" });
        Assert.Equal("0", writes[GodOfWar]);
        Assert.Equal(">0", record[GodOfWar]);
    }

    [Fact]
    public void ExosOwnShortcutsGetSteamInputAndLoseItWhenThePlayerTakesOver()
    {
        const string shortcut = "3123456789";
        var choices = new Dictionary<string, string> { [shortcut] = "steam" };
        var (writes, record, kept) = SteamInputConfig.Plan(Apps(), new Dictionary<string, string?>(), choices, None);
        Assert.Equal("2", writes[shortcut]);
        Assert.Equal(">2", record[shortcut]);
        Assert.True(kept.ContainsKey(shortcut));
        var (_, _, after) = SteamInputConfig.Plan(Apps((shortcut, "0")), new Dictionary<string, string?>(), choices, record);
        Assert.False(after.ContainsKey(shortcut));
    }

    [Fact]
    public void TheFileTakesOnlyRealValues()
    {
        var text = "\"UserLocalConfigStore\"\n{\n\t\"apps\"\n\t{\n\t}\n}\n";
        var written = SteamInputConfig.Write(text, new Dictionary<string, string> { [GodOfWar] = "0" });
        Assert.Equal("0", SteamInputConfig.Read(SteamKeyValues.ParseText(written).Section("UserLocalConfigStore/apps"), GodOfWar));
        // Taken out again: the key's line goes, the section stays as Steam would leave it.
        var back = SteamInputConfig.Write(written, new Dictionary<string, string> { [GodOfWar] = "" });
        Assert.Null(SteamInputConfig.Read(SteamKeyValues.ParseText(back).Section("UserLocalConfigStore/apps"), GodOfWar));
        Assert.DoesNotContain("UseSteamControllerConfig", back);
        Assert.Throws<ArgumentException>(() => SteamInputConfig.Write(text, new Dictionary<string, string> { [GodOfWar] = "3" }));
        Assert.Throws<ArgumentException>(() => SteamInputConfig.Write(text, new Dictionary<string, string> { ["../x"] = "0" }));
    }
}
