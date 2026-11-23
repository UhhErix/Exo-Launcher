using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Services;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// The player's choices that sync and the files that hold them, in a temp data folder. Ways it could fail: two PCs that
/// change two different page choices overwrite each other (the settings item merges by top-level key); a page choice Exo does
/// not keep, or one too large, lands in settings.json; a key another PC sends that Exo does not keep is applied; the Riot ID
/// stays a DPAPI blob a new Windows install cannot open; a wish removed on another PC stays here, or comes back from this
/// PC's first sync; a wish already here loses its prices when it is pulled again; a choice from before sync never reaches
/// the account.
/// </summary>
public sealed class PlayerChoicesSyncTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "exo-choices-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    public PlayerChoicesSyncTests()
    {
        Directory.CreateDirectory(_data);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _data);
    }

    public void Dispose()
    {
        AccountSyncHooks.Attach(null);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorDataDir);
        try { DeletionGate.DeleteDirectory(_data, recursive: true); } catch { /* temp */ }
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    /// <summary>Every page choice and where it belongs, as the reviewer and the owner decided: only choices about the player sync.</summary>
    public static TheoryData<string, bool> Scopes => new()
    {
        { "discord", true }, { "discordCamera", false }, { "discordPerformance", true }, { "discordPinnedDms", true },
        { "driverBranch", false }, { "wearing", false }, { "shadersOrder", true }, { "goLiveQuality", false },
        { "goLivePreview", true }, { "voiceChat", true }, { "tourSeen", true }, { "setupProgress", false },
    };

    [Fact]
    public void Every_page_choice_has_a_scope_here()
    {
        Assert.Equal(Scopes.Select(row => row.Data.Item1).Order(StringComparer.Ordinal), PlayerData.UiPrefKeys.Order(StringComparer.Ordinal));
        Assert.All(PlayerData.UiPrefs, pref => Assert.True(pref.Why.Length > 12, pref.Key + " says why"));
    }

    [Theory]
    [MemberData(nameof(Scopes))]
    public void A_page_choice_reaches_another_PC_only_when_it_is_about_the_player(string key, bool syncs)
    {
        var here = new AppSettings { UiPrefs = { [key] = Json("\"value-from-pc-a\"") } };
        var value = AccountSyncMap.SettingsValue(here);
        Assert.Equal(syncs, value.ContainsKey("UiPrefs." + key));

        // Even an account that holds one (an older build sent it) never sets this PC's own.
        var other = new AppSettings { UiPrefs = { [key] = Json("\"this-pc\"") } };
        AccountSyncMap.ApplySettings(other, $$"""{"UiPrefs.{{key}}":"value-from-pc-a"}""");
        Assert.Equal(syncs ? "value-from-pc-a" : "this-pc", other.UiPrefs[key].GetString());
    }

    [Fact]
    public void Two_PCs_changing_two_page_choices_keep_both()
    {
        var before = new AppSettings();
        var pcA = new AppSettings { UiPrefs = { ["shadersOrder"] = Json("""["steam:730"]""") } };
        var pcB = new AppSettings { UiPrefs = { ["discord"] = Json("""{"streamerMode":true}""") } };
        var server = AccountSyncMap.SettingsValue(pcB).ToJsonString();
        var local = AccountSyncMap.SettingsValue(pcA).ToJsonString();

        var merged = AccountSyncRules.Overlay(server, local, AccountSyncRules.ChangedKeys(AccountSyncMap.SettingsValue(before).ToJsonString(), local));
        var target = new AppSettings();
        AccountSyncMap.ApplySettings(target, merged);

        Assert.Equal("steam:730", target.UiPrefs["shadersOrder"][0].GetString());
        Assert.True(target.UiPrefs["discord"].GetProperty("streamerMode").GetBoolean());
    }

    [Fact]
    public void A_pulled_page_choice_Exo_does_not_keep_is_left_out()
    {
        var target = new AppSettings();

        AccountSyncMap.ApplySettings(target, """{"UiPrefs.somethingNew":true,"UiPrefs.voiceChat":false}""");

        Assert.Equal(["voiceChat"], target.UiPrefs.Keys);
    }

    [Fact]
    public void A_moved_choice_never_beats_the_accounts_and_goes_up_only_where_the_account_has_none()
    {
        var settings = new SettingsService();
        settings.Load();
        settings.SetUiPrefs(new Dictionary<string, JsonElement> { ["voiceChat"] = Json("true") });

        // The first start after the update: the page moves its months-old copies in.
        Assert.False(settings.MoveUiPref("voiceChat", Json("false")));          // this PC already had one: kept
        Assert.True(settings.MoveUiPref("discord", Json("""{"fontSize":12}""")));
        Assert.True(settings.MoveUiPref("shadersOrder", Json("""["old"]""")));
        Assert.True(settings.Current.UiPrefs["voiceChat"].GetBoolean());
        // Until the account has been read in full, a moved choice is no change to sync.
        var synced = AccountSyncMap.SettingsValue(settings.Current);
        Assert.False(synced.ContainsKey("UiPrefs.discord"));
        Assert.False(synced.ContainsKey("UiPrefs.shadersOrder"));

        // The account holds a newer discord choice from another PC, and no Shaders order.
        settings.SettleMovedUiPrefs("""{"UiPrefs.discord":{"fontSize":20}}""");

        Assert.Equal(20, settings.Current.UiPrefs["discord"].GetProperty("fontSize").GetInt32());
        Assert.Equal("old", AccountSyncMap.SettingsValue(settings.Current)["UiPrefs.shadersOrder"]![0]!.GetValue<string>());
        Assert.Empty(settings.Current.UiPrefsMoved);
    }
    [Fact]
    public void Settings_keep_only_the_page_choices_Exo_knows_and_none_too_large()
    {
        var settings = new SettingsService();
        settings.Load();

        settings.SetUiPrefs(new Dictionary<string, JsonElement> { ["goLivePreview"] = Json("false") });
        Assert.Throws<InvalidDataException>(() => settings.SetUiPrefs(new Dictionary<string, JsonElement> { ["notAChoice"] = Json("1") }));
        Assert.Throws<InvalidDataException>(() => settings.SetUiPrefs(new Dictionary<string, JsonElement> { ["shadersOrder"] = Json("\"" + new string('x', PlayerData.MaxUiPrefBytes) + "\"") }));
        settings.SetUiPrefs(new Dictionary<string, JsonElement> { ["goLivePreview"] = Json("null"), ["voiceChat"] = Json("true") });

        Assert.Equal(["voiceChat"], settings.Current.UiPrefs.Keys);
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(_data, "settings.json")))!.AsObject();
        Assert.True(file["uiPrefs"]!["voiceChat"]!.GetValue<bool>());
    }

    [Fact]
    public void The_Riot_ID_is_a_plain_file_and_an_older_sealed_one_becomes_one()
    {
        var path = Path.Combine(_data, RiotIdStore.FileName);
        File.WriteAllBytes(path, ExoDpapi.Protect(Encoding.UTF8.GetBytes("Player One#EUW")));

        Assert.Equal("Player One#EUW", RiotIdStore.TryRead());
        Assert.Equal("Player One#EUW", File.ReadAllText(path));

        Assert.True(RiotIdStore.Save("Someone Else #NA1"));
        Assert.Equal("Someone Else#NA1", File.ReadAllText(path));
        Assert.True(RiotIdStore.Save(""));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_pulled_choice_sets_the_Riot_ID_and_the_music_service()
    {
        AccountSyncApply.ApplyChoice(AccountSyncHooks.RiotIdChoice, """{"riotId":"Player One#EUW"}""");
        AccountSyncApply.ApplyChoice(AccountSyncHooks.MusicServiceChoice, """{"service":"youtube"}""");
        AccountSyncApply.ApplyChoice(AccountSyncHooks.MusicServiceChoice, """{"service":"not-a-service"}""");

        Assert.Equal("Player One#EUW", File.ReadAllText(Path.Combine(_data, RiotIdStore.FileName)));
        Assert.Equal("youtube", WebClientPreferences.Reload().MusicService);
    }

    [Fact]
    public void A_wish_from_another_PC_joins_the_list_and_one_removed_there_leaves_it()
    {
        Directory.CreateDirectory(Path.Combine(_data, "store"));
        File.WriteAllText(Path.Combine(_data, "store", "wishlist-v2.json"), """[{"appId":"620","name":"Portal 2","addedAt":"2026-11-01T00:00:00+00:00","final":999,"notifiedAt":999}]""");
        var store = new StoreService("3.26.0");

        store.ApplySyncedWish(AccountSyncHooks.WishValue("400", "Portal", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        store.ApplySyncedWish(AccountSyncHooks.WishValue("620", "Portal 2 renamed", DateTimeOffset.UnixEpoch));
        Assert.Equal(["620", "400"], store.Wishlist()!.Select(w => w.AppId));
        // A game already here keeps its name, its price and the sale last announced.
        Assert.Equal("Portal 2", store.Wishlist()![0].Name);
        Assert.Equal(999, store.Wishlist()![0].NotifiedAt);

        store.ApplySyncedWish(AccountSyncHooks.WishRemoved("620", DateTimeOffset.UnixEpoch));
        Assert.Equal(["400"], store.Wishlist()!.Select(w => w.AppId));
        Assert.Equal(["400"], new StoreService("3.26.0").Wishlist()!.Select(w => w.AppId));
    }

    [Fact]
    public void A_wishlist_that_cannot_be_read_says_so_instead_of_dropping_the_wish()
    {
        Directory.CreateDirectory(Path.Combine(_data, "store"));
        var path = Path.Combine(_data, "store", "wishlist-v2.json");
        File.WriteAllText(path, "[]");
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<IOException>(() => new StoreService("3.26.0").ApplySyncedWish(AccountSyncHooks.WishValue("400", "Portal", DateTimeOffset.UnixEpoch)));
    }

    [Fact]
    public async Task A_wish_item_name_says_nothing_about_the_game_and_a_removal_keeps_which_game_inside()
    {
        var session = new AccountSession("token-value", "acc-1", "person@example.com");
        var answer = new Account("""{"items":[],"cursor":1,"more":false}""");
        var host = new AccountSyncHost(_data, new ExoAccountClient(answer), () => session, _ => { });
        AccountSyncHooks.Attach(host);
        await host.SyncAsync(CancellationToken.None);

        AccountSyncHooks.Wished("620", "Portal 2", DateTimeOffset.UnixEpoch, add: true);
        AccountSyncHooks.Wished("620", "Portal 2", DateTimeOffset.UnixEpoch, add: true);
        var item = Assert.Single(host.ItemsOf("wish"));
        Assert.DoesNotContain("620", item.Id);
        Assert.Matches("^w\\.[0-9a-f]{24}$", item.Id);

        AccountSyncHooks.Wished("620", "", DateTimeOffset.UnixEpoch, add: false);
        var removed = Assert.Single(host.ItemsOf("wish"));
        Assert.Equal(item.Id, removed.Id);
        Assert.Equal("620", JsonNode.Parse(removed.Value)!["appId"]!.GetValue<string>());
        Assert.True(JsonNode.Parse(removed.Value)!["removed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_choice_from_before_sync_goes_up_once_and_a_wish_removed_elsewhere_stays_removed()
    {
        var session = new AccountSession("token-value", "acc-1", "person@example.com");
        var removedElsewhere = AccountSyncHooks.WishRemoved("620", DateTimeOffset.UnixEpoch).Replace("\"", "\\\"");
        var answer = new Account($$"""{"items":[{"app":"launcher","type":"wish","id":"w.000000000000000000000001","version":3,"updated":5,"value":"{{removedElsewhere}}"}],"cursor":3,"more":false}""");
        var seeds = new List<(string, string, string, string?)>
        {
            ("wish", AccountSyncHooks.NewWishId(), AccountSyncHooks.WishValue("620", "Portal 2", DateTimeOffset.UnixEpoch), "appId"),
            ("wish", AccountSyncHooks.NewWishId(), AccountSyncHooks.WishValue("400", "Portal", DateTimeOffset.UnixEpoch), "appId"),
            ("choice", AccountSyncHooks.RiotIdChoice, """{"riotId":"Player One#EUW"}""", null),
        };
        var host = new AccountSyncHost(_data, new ExoAccountClient(answer), () => session, _ => { }) { Seeds = () => seeds };

        await host.SyncAsync(CancellationToken.None);
        var state = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(Path.Combine(_data, AccountSyncStore.FileName)))!;

        var wishes = state.Items.Values.Where(entry => entry.Type == "wish").Select(entry => JsonNode.Parse(entry.Value!)!).ToList();
        Assert.Equal(2, wishes.Count);
        Assert.True(wishes.Single(wish => wish["appId"]!.GetValue<string>() == "620")["removed"]!.GetValue<bool>());
        Assert.Contains(wishes, wish => wish["appId"]!.GetValue<string>() == "400" && wish["removed"] is null);
        Assert.Contains("choice/riot-id", state.Items.Keys);
        Assert.True(state.Seeded);

        // A later start (a new host on the same queue file) seeds nothing again.
        seeds.Add(("wish", AccountSyncHooks.NewWishId(), AccountSyncHooks.WishValue("730", "Counter-Strike 2", DateTimeOffset.UnixEpoch), "appId"));
        await new AccountSyncHost(_data, new ExoAccountClient(answer), () => session, _ => { }) { Seeds = () => seeds }.SyncAsync(CancellationToken.None);
        var after = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(Path.Combine(_data, AccountSyncStore.FileName)))!;
        Assert.DoesNotContain(after.Items.Values, entry => entry.Value?.Contains("\"730\"") == true);
    }
    // ------------------------------------------------------------------ one wish per game

    private static string Pulled(string id, string value, int version) =>
        $$"""{"app":"launcher","type":"wish","id":"{{id}}","version":{{version}},"updated":{{version}},"value":{{JsonSerializer.Serialize(value)}}}""";

    /// <summary>A host on this test's data folder whose pulls go through AccountSyncApply, as the Launcher wires it.</summary>
    private AccountSyncHost WishHost(HttpMessageHandler answer, StoreService store)
    {
        var session = new AccountSession("token-value", "acc-1", "person@example.com");
        var settings = new SettingsService();
        settings.Load();
        AccountSyncHost? host = null;
        host = new AccountSyncHost(_data, new ExoAccountClient(answer), () => session, item => AccountSyncApply.Pulled(item, settings));
        AccountSyncApply.Wishes = value => store.ApplySyncedWish(value);
        AccountSyncApply.WishItems = () => host.ItemsOf("wish");
        AccountSyncApply.WriteWish = (id, value) => host.Write("wish", id, value);
        AccountSyncHooks.Attach(host);
        return host;
    }

    private void Wishlist(string json)
    {
        Directory.CreateDirectory(Path.Combine(_data, "store"));
        File.WriteAllText(Path.Combine(_data, "store", "wishlist-v2.json"), json);
    }

    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Wishes_the_limit_drops_are_written_as_removals_so_they_leave_the_other_PCs()
    {
        // A list past the limit (a sync brought more than this PC keeps): the next change trims it, and every wish it drops
        // is a removal on the account, as if the player had removed it.
        var wishes = Enumerable.Range(1, StoreService.MaxWishlist + 2)
            .Select(i => $$"""{"appId":"{{i}}","name":"Game {{i}}","addedAt":"2026-11-01T00:00:00+00:00"}""");
        Wishlist("[" + string.Join(",", wishes) + "]");
        using var store = new StoreService("3.26.0");
        var host = WishHost(new Account("""{"items":[],"cursor":0,"more":false}"""), store);

        // Nothing wished or removed by this call itself (the game is not on the list): only the trim changes anything.
        Assert.Equal(StoreService.MaxWishlist, store.Wish("999999", "Not there", add: false)!.Count);

        var removed = host.ItemsOf("wish").Where(item => JsonNode.Parse(item.Value)!["removed"]?.GetValue<bool>() == true)
            .Select(item => AccountSyncHooks.AppIdOf(item.Value)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { (StoreService.MaxWishlist + 1).ToString(), (StoreService.MaxWishlist + 2).ToString(), "999999" }.Order(StringComparer.Ordinal), removed);
    }

    [Fact]
    public async Task Two_items_for_one_game_are_one_wish_and_the_newest_change_wins_whatever_order_they_come_in()
    {
        Wishlist("""[{"appId":"620","name":"Portal 2","addedAt":"2026-11-01T00:00:00+00:00"}]""");
        var store = new StoreService("3.26.0");
        // PC B removed it on Tuesday; PC C, offline, had wished it again on Monday: the removal arrives first.
        var page = $$"""{"items":[{{Pulled("w.00000000000000000000000b", AccountSyncHooks.WishRemoved("620", Monday.AddDays(1)), 3)}},{{Pulled("w.00000000000000000000000c", AccountSyncHooks.WishValue("620", "Portal 2", Monday, Monday), 4)}}],"cursor":4,"more":false}""";
        var host = WishHost(new Account(page), store);

        await host.SyncAsync(CancellationToken.None);

        Assert.DoesNotContain(store.Wishlist()!, wish => wish.AppId == "620");
        // The removal is written onto PC C's item too, so every PC ends with the game removed.
        Assert.All(host.ItemsOf("wish"), item => Assert.True(JsonNode.Parse(item.Value)!["removed"]?.GetValue<bool>() == true, item.Id));
    }

    [Fact]
    public async Task A_removal_made_before_the_first_pull_wins_over_an_older_add_that_arrives_later()
    {
        Wishlist("""[{"appId":"620","name":"Portal 2","addedAt":"2026-11-01T00:00:00+00:00"}]""");
        var store = new StoreService("3.26.0");
        var page = $$"""{"items":[{{Pulled("w.00000000000000000000000c", AccountSyncHooks.WishValue("620", "Portal 2", Monday, Monday), 4)}}],"cursor":4,"more":false}""";
        var host = WishHost(new Account(page), store);

        // Removed on this PC before it has pulled anything: it knows no item for the game yet.
        Assert.NotNull(store.Wish("620", "Portal 2", add: false));
        Assert.Single(host.ItemsOf("wish"));
        await host.SyncAsync(CancellationToken.None);

        Assert.DoesNotContain(store.Wishlist()!, wish => wish.AppId == "620");
        Assert.Equal(2, host.ItemsOf("wish").Count);
        Assert.All(host.ItemsOf("wish"), item => Assert.True(JsonNode.Parse(item.Value)!["removed"]?.GetValue<bool>() == true, item.Id));
    }

    [Fact]
    public async Task A_pulled_wish_that_could_not_be_applied_is_tried_again_at_the_next_pass()
    {
        Wishlist("[]");
        var store = new StoreService("3.26.0");
        var page = $$"""{"items":[{{Pulled("w.00000000000000000000000c", AccountSyncHooks.WishValue("400", "Portal", Monday, Monday), 4)}}],"cursor":4,"more":false}""";
        var host = WishHost(new OnceAccount(page), store);

        using (new FileStream(Path.Combine(_data, "store", "wishlist-v2.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await host.SyncAsync(CancellationToken.None);
        Assert.Contains("wish/w.00000000000000000000000c", JsonSerializer.Deserialize<SyncState>(File.ReadAllText(Path.Combine(_data, AccountSyncStore.FileName)))!.Unapplied.Keys);

        // The cursor moved on, so the page brings it no more: the retry does.
        await host.SyncAsync(CancellationToken.None);

        Assert.Equal(["400"], new StoreService("3.26.0").Wishlist()!.Select(wish => wish.AppId));
        Assert.Empty(JsonSerializer.Deserialize<SyncState>(File.ReadAllText(Path.Combine(_data, AccountSyncStore.FileName)))!.Unapplied);
    }
    /// <summary>The account service: a pull answers the page given; a push is taken.</summary>
    private sealed class Account(string page) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Get ? page : """{"items":[]}""", Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>The account service with one page to give: later pulls find nothing new.</summary>
    private sealed class OnceAccount(string page) : HttpMessageHandler
    {
        private bool given;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Method != HttpMethod.Get ? """{"items":[]}""" : given ? """{"items":[],"cursor":4,"more":false}""" : page;
            if (request.Method == HttpMethod.Get) given = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}