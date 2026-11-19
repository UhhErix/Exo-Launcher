using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Adapters;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Exo keeps Steam's notifications, sounds and dialogs before a game off by writing Steam's own switches. It fails if a
/// switch Steam reads is left on, a section Steam hasn't written yet is not added, the friends list's JSON is broken or
/// left loud, anything else in the file changes, or a file already quiet is written again. For the dialogs before a
/// game (Fixtures/Steam/localconfig.vdf: this PC's WebStorage after "Grab a controller and kick back!" was OK'd without
/// its box, the account's own keys left out): a box ticked without the list of games Steam reads it beside, so the
/// dialog still shows; the list Steam already keeps replaced; a version Steam has seen lowered; Unclaimed content or the
/// VR notice turned off; a dialog marked in another section (WebStorage's own "apps", the games' "apps"); a file that
/// can't take the write counted as waiting forever, so Steam is closed for it again and again.
/// </summary>
public sealed partial class StoreQuietTests
{
    private const string Shared = """
        "UserRoamingConfigStore"
        {
        	"Software"
        	{
        		"Valve"
        		{
        			"Steam"
        			{
        				"SteamDefaultDialog"		"#app_games"
        				"FriendsUI"
        				{
        					"FriendsUIJSON"		"{\"bNotifications_ShowIngame\":true,\"bSounds_PlayMessage\":true,\"bSounds_PlayOnline\":false,\"nChatFlashMode\":0,\"nChatFontSize\":2,\"strNote\":\"a \\\"b\\\"\"}"
        				}
        			}
        		}
        	}
        	"DisableAllToasts"		"0"
        }

        """;

    private const string Local = """
        "UserLocalConfigStore"
        {
        	"system"
        	{
        		"InGameOverlayShowFPSCorner"		"0"
        	}
        	"friends"
        	{
        		"Notifications_ShowIngame"		"1"
        		"Sounds_PlayMessage"		"1"
        		"ChatFlashMode"		"0"
        	}
        }

        """;

    [Fact]
    public void SharedConfigGoesQuiet()
    {
        var quiet = StoreQuiet.QuietShared(Shared);
        var store = SteamKeyValues.ParseText(quiet).Section("UserRoamingConfigStore");
        Assert.Equal("1", store.Value("DisableAllToasts"));
        Assert.Equal("1", store.Value("DisableToastsInGame"));
        Assert.Equal("0", store.Value("PlaySoundOnToast"));
        Assert.Equal("0", store.Value("AchievementNotificationSound"));
        Assert.Equal("#app_games", store.Section("Software/Valve/Steam").Value("SteamDefaultDialog"));

        var json = System.Text.Json.Nodes.JsonNode.Parse(store.Section("Software/Valve/Steam/FriendsUI").Value("FriendsUIJSON")!)!;
        Assert.False(json["bNotifications_ShowIngame"]!.GetValue<bool>());
        Assert.False(json["bSounds_PlayMessage"]!.GetValue<bool>());
        Assert.Equal(2, json["nChatFlashMode"]!.GetValue<int>());
        Assert.Equal(2, json["nChatFontSize"]!.GetValue<int>());
        Assert.Equal("a \"b\"", json["strNote"]!.GetValue<string>());

        Assert.Equal(quiet, StoreQuiet.QuietShared(quiet));
    }

    [Fact]
    public void LocalConfigGoesQuiet()
    {
        var quiet = StoreQuiet.QuietLocal(Local);
        var store = SteamKeyValues.ParseText(quiet).Section("UserLocalConfigStore");
        Assert.Equal("0", store.Section("friends").Value("Notifications_ShowIngame"));
        Assert.Equal("0", store.Section("friends").Value("Notifications_ShowOnline"));
        Assert.Equal("0", store.Section("friends").Value("Sounds_PlayMessage"));
        Assert.Equal("2", store.Section("friends").Value("ChatFlashMode"));
        Assert.Equal("0", store.Section("system").Value("InGameOverlayScreenshotPlaySound"));
        Assert.Equal("0", store.Section("system").Value("InGameOverlayShowFPSCorner"));
        Assert.Equal("0", store.Section("news").Value("NotifyAvailableGames"));
        Assert.StartsWith("\"UserLocalConfigStore\"\n{\n\t\"system\"\n\t{\n\t\t\"InGameOverlayShowFPSCorner\"\t\t\"0\"\n", quiet.ReplaceLineEndings("\n"));
        Assert.Equal(quiet, StoreQuiet.QuietLocal(quiet));
    }

    private static string RealLocalConfig() =>
        File.ReadAllText(Repo.PathOf("ExoLauncher.Tests", "Fixtures", "Steam", "localconfig.vdf")).ReplaceLineEndings("\n");

    private static SteamKeyValues Storage(string text) => SteamKeyValues.ParseText(text).Section("UserLocalConfigStore/WebStorage");

    /// <summary>
    /// Steam's own reading of a dialog's keys (steamui's sp.js, October 2026: ConfiguratorInterstitials'
    /// LoadCacheFromLocalStorage and BHasUserSeenInterstitial): with a box, nothing counts until the box and a list of
    /// games are both there, then a ticked box skips it for every game, else it is seen only for a game on the list at
    /// its version; a dialog shown every time without a ticked box always shows.
    /// </summary>
    private static bool SteamSkips(SteamKeyValues storage, string name, int version, bool box, int appId)
    {
        var seen = int.TryParse(storage.Value("Deck_ConfiguratorInterstitialsVersionSeen_" + name), out var was) ? was : 0;
        if (box)
        {
            var checkbox = storage.Value("Deck_ConfiguratorInterstitialsCheckbox_" + name);
            JsonNode? apps;
            try { apps = JsonNode.Parse(storage.Value("Deck_ConfiguratorInterstitialApps_" + name) ?? ""); }
            catch (JsonException) { apps = null; }
            if (checkbox is null || apps is null) return false;
            if (checkbox == "1") return true;
            if (apps is not JsonArray list || !list.Any(app => app?.GetValue<int>() == appId)) return false;
        }
        return version != 0 && seen >= version;
    }

    [Fact]
    public void SteamSkipsItsControllerDialogsForEveryGame()
    {
        var text = RealLocalConfig();
        var before = Storage(text);
        // As found: the dialog OK'd for the games on its list, still shown for any other.
        Assert.True(SteamSkips(before, "GamepadRecommended", 1, true, 1790600));
        Assert.False(SteamSkips(before, "GamepadRecommended", 1, true, 1091500));

        var quiet = StoreQuiet.QuietDialogs(text);
        var storage = Storage(quiet);
        foreach (var (name, version, box) in StoreQuiet.LaunchDialogs)
            foreach (var appId in new[] { 1790600, 1091500, 620 })
                Assert.True(SteamSkips(storage, name, version, box, appId), $"{name} for {appId}");
        Assert.Equal("[1790600,620]", storage.Value("Deck_ConfiguratorInterstitialApps_GamepadRecommended"));
        Assert.Equal("[]", storage.Value("Deck_ConfiguratorInterstitialApps_GamepadRequired"));
        Assert.Equal("0", storage.Value("Deck_ConfiguratorInterstitialsVersionSeen_GamepadRequired"));
        Assert.Null(storage.Value("Deck_ConfiguratorInterstitialsCheckbox_IntroToSteamInputGames"));
        Assert.Equal("3", storage.Value("Deck_ConfiguratorInterstitialsVersionSeen_Intro"));
        Assert.DoesNotContain(StoreQuiet.LaunchDialogs, dialog => dialog.Name is "UnclaimedEntitlement" or "VRRequired");

        // Only WebStorage's dialog lines change: the rest of the file, WebStorage's own "apps" and the games' "apps" too.
        Assert.Equal("1", storage.Section("apps/4126040/DefaultLaunchOption").Value("bde72369"));
        Assert.Equal("\"center-bottom\"", storage.Value("keyboard_initial_location_desktop"));
        var games = SteamKeyValues.ParseText(quiet).Section("UserLocalConfigStore/apps");
        Assert.Equal(["OverlayAppEnable"], games.Section("1790600").Keys);
        var start = text.IndexOf("\t\t\"Deck_ConfiguratorInterstitialsCheckbox_GamepadRecommended\"", StringComparison.Ordinal);
        Assert.Equal(text[..start], quiet[..start]);
        var close = text.IndexOf("\t}\n\t\"news\"", StringComparison.Ordinal);
        Assert.Equal(text[close..], quiet[(quiet.Length - (text.Length - close))..]);
        Assert.Equal(quiet, StoreQuiet.QuietDialogs(quiet));
    }

    [Fact]
    public void AnAccountWithoutWebStorageGetsIt()
    {
        var quiet = StoreQuiet.QuietLocal(Local);
        var storage = Storage(quiet);
        foreach (var (name, version, box) in StoreQuiet.LaunchDialogs)
            Assert.True(SteamSkips(storage, name, version, box, 1790600), name);
        Assert.Equal(quiet, StoreQuiet.QuietLocal(quiet));
    }

    [Fact]
    public void ABrokenListOfGamesIsReplaced()
    {
        const string Broken = "\"UserLocalConfigStore\"\n{\n\t\"WebStorage\"\n\t{\n" +
            "\t\t\"Deck_ConfiguratorInterstitialsCheckbox_GamepadRecommended\"\t\t\"1\"\n" +
            "\t\t\"Deck_ConfiguratorInterstitialApps_GamepadRecommended\"\t\t\"[1790600\"\n" +
            "\t\t\"Deck_ConfiguratorInterstitialsVersionSeen_GamepadRecommended\"\t\t\"7\"\n\t}\n}\n";
        Assert.False(SteamSkips(Storage(Broken), "GamepadRecommended", 1, true, 1790600));
        var storage = Storage(StoreQuiet.QuietDialogs(Broken));
        Assert.Equal("[]", storage.Value("Deck_ConfiguratorInterstitialApps_GamepadRecommended"));
        // A version Steam has seen beyond Exo's stays.
        Assert.Equal("7", storage.Value("Deck_ConfiguratorInterstitialsVersionSeen_GamepadRecommended"));
        Assert.True(SteamSkips(storage, "GamepadRecommended", 1, true, 1790600));
    }

    [Fact]
    public void DialogsWaitOnlyUntilWritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-quiet-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "userdata", "123", "config");
        var halfWritten = Path.Combine(root, "userdata", "456", "config");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(halfWritten);
        try
        {
            // A file Steam left ending inside a section is never counted as waiting: Steam writes it whole as it exits.
            File.WriteAllText(Path.Combine(halfWritten, "localconfig.vdf"), "\"UserLocalConfigStore\"\n{\n\t\"WebStorage\"\n\t{\n");
            Assert.False(StoreQuiet.LaunchDialogsPending(root));
            File.WriteAllText(Path.Combine(config, "localconfig.vdf"), RealLocalConfig());
            Assert.True(StoreQuiet.LaunchDialogsPending(root));
            foreach (var edit in StoreQuiet.SteamEdits(root)) SteamOverlayConfig.WriteFile(edit.Path, edit.Text);
            Assert.False(StoreQuiet.LaunchDialogsPending(root));
            Assert.Empty(StoreQuiet.SteamEdits(root));
        }
        finally { DeletionGate.DeleteDirectory(root, recursive: true); }
    }

    [Fact]
    public void FilesAlreadyQuietAreNotEdited()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-quiet-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "userdata", "123", "config");
        var remote = Path.Combine(root, "userdata", "123", "7", "remote");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(remote);
        try
        {
            File.WriteAllText(Path.Combine(config, "localconfig.vdf"), Local);
            File.WriteAllText(Path.Combine(remote, "sharedconfig.vdf"), StoreQuiet.QuietShared(Shared));
            var edits = StoreQuiet.SteamEdits(root);
            Assert.Equal([Path.Combine(config, "localconfig.vdf")], edits.Select(edit => edit.Path));
        }
        finally { DeletionGate.DeleteDirectory(root, recursive: true); }
    }
}
