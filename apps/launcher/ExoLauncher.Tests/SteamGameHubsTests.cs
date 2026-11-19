using ExoLauncher.Adapters;

namespace ExoLauncher.Tests;

/// <summary>
/// Steam's one Call of Duty app holding the games bought as its DLC. Ways it could go wrong without anyone seeing it:
/// another app takes the name; a free app with nothing bought is called Modern Warfare 4; the older game names the card
/// when a newer one is held; a pack such as Multiplayer stands in for buying the game; the app's own name is lost, so a
/// friend playing "Call of Duty®" no longer matches it; with Steam closed the card goes back to its old name; a manifest
/// overrides Steam's own answer after a refund.
/// </summary>
public sealed class SteamGameHubsTests
{
    private const string Modern4 = "4435490", BlackOps7 = "3606480", Multiplayer4 = "4435500";

    private static HashSet<string> Held(params string[] ids) => new(ids, StringComparer.Ordinal);

    private static string Manifest(string disabled, string? installedDlc = null) => $$"""
        "AppState"
        {
            "appid"     "1938090"
            "InstalledDepots"
            {
                "5092498"
                {
                    "manifest"      "1"
                    "dlcappid"      "{{installedDlc ?? "0"}}"
                }
            }
            "UserConfig"
            {
                "DisabledDLC"       "{{disabled}}"
            }
        }
        """;

    [Fact]
    public void Another_app_is_left_alone() =>
        Assert.Null(SteamGameHubs.Includes("730", "Counter-Strike 2", Held(Modern4), null));

    [Fact]
    public void With_nothing_bought_it_keeps_steams_name() =>
        Assert.Null(SteamGameHubs.Includes(SteamGameHubs.CallOfDuty, "Call of Duty®", Held(), null));

    [Fact]
    public void A_pack_alone_is_not_the_game() =>
        Assert.Null(SteamGameHubs.Includes(SteamGameHubs.CallOfDuty, "Call of Duty®", Held(Multiplayer4), null));

    [Fact]
    public void The_newest_game_held_comes_first_and_the_app_stays_on_the_list()
    {
        var list = SteamGameHubs.Includes(SteamGameHubs.CallOfDuty, "Call of Duty®", Held(BlackOps7, Modern4), null);
        Assert.NotNull(list);
        Assert.Equal([Modern4, BlackOps7, SteamGameHubs.CallOfDuty], list.Select(game => game.AppId));
        Assert.Equal("Call of Duty®: Modern Warfare® 4", list[0].Title);
        Assert.Equal("Call of Duty®", list[^1].Title);
    }

    [Fact]
    public void With_steam_closed_the_manifest_says_what_is_held()
    {
        var list = SteamGameHubs.Includes(SteamGameHubs.CallOfDuty, "Call of Duty®", null, Manifest($"{Modern4},{BlackOps7}", Multiplayer4));
        Assert.Equal(Modern4, list?[0].AppId);
        Assert.Null(SteamGameHubs.Includes(SteamGameHubs.CallOfDuty, "Call of Duty®", null, Manifest("", Multiplayer4)));
        Assert.Null(SteamGameHubs.Includes(SteamGameHubs.CallOfDuty, "Call of Duty®", null, null));
    }

    [Fact]
    public void Steams_answer_outranks_the_manifest() =>
        Assert.Null(SteamGameHubs.Includes(SteamGameHubs.CallOfDuty, "Call of Duty®", Held(), Manifest(Modern4)));

    [Fact]
    public void Only_the_games_themselves_are_asked_about_and_held()
    {
        Assert.True(SteamGameHubs.IsHeldGame(Modern4));
        Assert.False(SteamGameHubs.IsHeldGame(Multiplayer4));
        Assert.False(SteamGameHubs.IsHeldGame(SteamGameHubs.CallOfDuty));
        Assert.Contains(Modern4, SteamGameHubs.GameAppIds);
    }
}
