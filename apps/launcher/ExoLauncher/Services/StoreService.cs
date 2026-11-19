using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// The Store room, built like a launcher's storefront: the big games, top
/// sellers, popular new releases, the most wishlisted and popular upcoming
/// games and Steam's specials, made for this person (StoreService.ForYou), with search, a wishlist and
/// a page per game. The catalog is Steam's public storefront (ranked lists,
/// item facts, app details); where else a game is sold comes from CheapShark,
/// limited to the official launchers (Steam, Epic, GOG, Ubisoft). Exo sells nothing: Get
/// opens the launcher's own page. Everything is cached on disk, so the room
/// opens at once with the last answer and refreshes behind it.
/// </summary>
internal sealed partial class StoreService : IDisposable
{
    internal sealed record Tile(string AppId, string Name)
    {
        public int? Final { get; init; }
        public int? Initial { get; init; }
        public int Discount { get; init; }
        /// <summary>The currency of the prices when Steam named it ("USD", "EUR"); null when <see cref="PriceText"/> carries Steam's own text.</summary>
        public string? Currency { get; init; }
        /// <summary>Steam's text for <see cref="Final"/> and <see cref="Initial"/> in the region's currency ("$14.99", "19,99€").</summary>
        public string? PriceText { get; init; }
        public string? WasText { get; init; }
        public string? Review { get; init; }
        public int? ReviewPercent { get; init; }
        /// <summary>Free to play: Steam sells it for nothing.</summary>
        public bool Free { get; init; }
        public Art? Art { get; init; }
        /// <summary>Not out yet; <see cref="When"/> says when ("Oct 23", "Feb 2027").</summary>
        public bool Soon { get; init; }
        public string? When { get; init; }
        /// <summary>On this person's wishlist; set only on the home page's own picks and banner.</summary>
        public bool Wished { get; init; }
        /// <summary>The library games (their ids, closest first, up to three) this pick is like; set only on the home page's own picks and banner.</summary>
        public IReadOnlyList<string>? SimilarTo { get; init; }
    }

    internal sealed record Shelf(string Id, string Title, IReadOnlyList<Tile> Items);

    internal sealed record Home(DateTimeOffset At, IReadOnlyList<Tile> Hero, IReadOnlyList<Shelf> Shelves)
    {
        /// <summary>The region the prices are for; a home saved for another region is fetched again.</summary>
        public string? Country { get; init; }
    }

    internal sealed record Offer(string Store, string Name, int? Final, int? Initial, int Discount, string Url)
    {
        public string? Currency { get; init; }
        public string? PriceText { get; init; }
        public string? WasText { get; init; }
    }

    /// <summary>The lowest price CheapShark has seen the game at, on any store it follows: US cents, and when.</summary>
    internal sealed record Lowest(int Final, DateTimeOffset At);

    /// <summary>The other official launchers selling a game, and its lowest price ever (US only; null elsewhere or when CheapShark has none).</summary>
    internal sealed record OtherStores(IReadOnlyList<Offer> Offers, Lowest? Lowest);

    /// <summary>The last 30 days of Steam reviews: Steam's words ("Very Positive"; null under ten reviews, which Steam does not score), the share positive and how many.</summary>
    internal sealed record RecentReviews(string? Word, int Percent, int Count);

    /// <summary>One Steam price: cents (in <see cref="Currency"/> when Steam named it), and Steam's own text for the final and original price ("19,99€").</summary>
    internal sealed record SteamPrice(int? Final, int? Initial, int Discount, string? Currency, string? Formatted, string? WasFormatted = null);

    internal sealed record Media(string Thumb, string? Hls, bool Trailer)
    {
        /// <summary>Steam's AV1 stream (DASH): the same bitrate as the H.264 one, visibly sharper.</summary>
        public string? Av1 { get; init; }
    }

    /// <summary>One line of the PC requirements: OS, Processor, Memory, Graphics, ...</summary>
    internal sealed record Spec(string Label, string? Minimum, string? Recommended);

    internal sealed record GameDetail(
        string AppId, string Name, string? Description, string? Header, bool ComingSoon, string? Released,
        IReadOnlyList<string> Developers, IReadOnlyList<string> Publishers, IReadOnlyList<string> Genres,
        int? Metacritic, string? ReviewWord, int? ReviewPercent, int ReviewCount, bool Free,
        IReadOnlyList<Offer> Offers, IReadOnlyList<Media> Media, IReadOnlyList<string> Screenshots)
    {
        public Art? Art { get; init; }
        public IReadOnlyList<Spec> Specs { get; init; } = [];
        /// <summary>Steam's most voted tags for the game, by name.</summary>
        public IReadOnlyList<string> Tags { get; init; } = [];
        /// <summary>The developer's DRM notice in one line ("Denuvo Anti-tamper, 5 a day machine activation limit"); null when it names none.</summary>
        public string? Drm { get; init; }
        /// <summary>The account the game needs besides Steam ("Ubisoft Account"); null when it needs none.</summary>
        public string? Account { get; init; }
        /// <summary>"full" or "partial" controller support, as Steam lists it; null when it lists none.</summary>
        public string? Controller { get; init; }
        /// <summary>Steam lists DualShock or DualSense support as well.</summary>
        public bool PlayStation { get; init; }
        /// <summary>The languages Steam lists for the game, by English name.</summary>
        public IReadOnlyList<string> Languages { get; init; } = [];
    }

    internal sealed record WishItem(string AppId, string Name, DateTimeOffset AddedAt)
    {
        public int? Final { get; init; }
        public int? Initial { get; init; }
        public int Discount { get; init; }
        public string? Currency { get; init; }
        /// <summary>Steam's text for <see cref="Final"/> and <see cref="Initial"/> in the region's currency ("$14.99", "19,99€").</summary>
        public string? PriceText { get; init; }
        public string? WasText { get; init; }
        public DateTimeOffset? CheckedAt { get; init; }
        /// <summary>The price the user was last told about, so a sale is announced once.</summary>
        public int? NotifiedAt { get; init; }
    }

    private static readonly TimeSpan HomeFresh = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan WishFresh = TimeSpan.FromHours(6);
    private const string Steam = "https://store.steampowered.com/";
    private const string CheapShark = "https://www.cheapshark.com/api/1.0/";

    /// <summary>The official launchers in CheapShark's store list.</summary>
    private static readonly Dictionary<string, string> Official = new()
    {
        ["1"] = "Steam", ["25"] = "Epic Games Store", ["7"] = "GOG", ["13"] = "Ubisoft Store",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _homeRefresh = new(1, 1);
    private readonly SemaphoreSlim _wishCheck = new(1, 1);
    private Home? _home;
    private List<WishItem>? _wishlist;
    private Timer? _timer;
    private int _oldFilesGone;
    private Timer? _release;

    /// <summary>How long the Store's answers stay in memory after its last use; the next use reads them from disk (or Steam) again.</summary>
    private static readonly TimeSpan KeepWhileIdle = TimeSpan.FromMinutes(15);

    /// <summary>"home": the home page changed. "wishlist": the wishlist or its prices did. The room reads only that one again.</summary>
    internal event Action<string>? Changed;

    /// <summary>A wishlisted game went on sale or got cheaper.</summary>
    internal event Action<WishItem>? SaleFound;

    internal StoreService(string version)
    {
        _http = ExoHttp.Create(TimeSpan.FromSeconds(15), new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(6),
        });
        // CheapShark asks every client to name itself; Steam and Epic are fine with it too.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ExoLauncher/{version} (+https://getexo.app)");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static string Dir => Path.Combine(PathHelper.AppDataDir, "store");

    private static string HomeFile => Path.Combine(Dir, "home-v5.json");

    private static string WishFile => Path.Combine(Dir, "wishlist-v2.json");

    /// <summary>The Windows region as Steam's and Epic's country ("US", "DE"); US when Windows names none.</summary>
    private static string Country
    {
        get
        {
            try
            {
                var cc = RegionInfo.CurrentRegion.TwoLetterISORegionName;
                return cc.Length == 2 && cc.All(char.IsAsciiLetter) ? cc.ToUpperInvariant() : "US";
            }
            catch (ArgumentException)
            {
                return "US";
            }
        }
    }

    /// <summary>
    /// A minute after Exo starts, then every six hours: the wishlist's prices
    /// are checked when it has games, and the home page warms only once the
    /// Store has been opened (its saved home page exists). While a game runs
    /// the check waits, asking again every ten minutes, so it never takes the
    /// network or the processor from the game.
    /// </summary>
    internal void Start()
    {
        lock (_gate)
            _timer ??= new Timer(_ => _ = Task.Run(async () =>
            {
                if (App.Services.Launcher.AnyGameRunning || LeanSignal.Active)
                {
                    lock (_gate) _timer?.Change(TimeSpan.FromMinutes(10), WishFresh);
                    return;
                }
                // Home pages and card facts older versions saved, before cards carried scores, Steam's price text, release dates and adult marks.
                if (Interlocked.Exchange(ref _oldFilesGone, 1) == 0)
                    foreach (var old in new[] { "home.json", "home-v2.json", "home-v3.json", "home-v4.json", "items-v1.json", "items-v2.json" })
                        try { DeletionGate.DeleteFile(Path.Combine(Dir, old)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (File.Exists(HomeFile))
                    try { await HomeAsync(false, CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) { AppLog.Debug("Store: home warm failed: " + ex.Message); }
                if (Wishlist() is { Count: > 0 })
                    try { await CheckWishlistAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception ex) { AppLog.Debug("Store: wishlist check failed: " + ex.Message); }
            }), null, TimeSpan.FromMinutes(1), WishFresh);
    }

    // ------------------------------------------------------------------ home

    /// <summary>
    /// The saved home page at once (refreshed behind it when older than half an hour); a fresh one when
    /// there is none or on Refresh. A Refresh that fails answers null, so the room can say so.
    /// </summary>
    internal async Task<Home?> HomeAsync(bool refresh, CancellationToken ct)
    {
        Home? home;
        lock (_gate)
        {
            Used();
            home = _home;
        }
        home ??= Read<Home>(HomeFile);
        // Saved for another region: fetched again, shown only if that fails.
        if (home is not null && !refresh && home.Country == Country)
        {
            lock (_gate) _home = home;
            if (DateTimeOffset.UtcNow - home.At > HomeFresh) _ = Task.Run(() => RefreshHomeAsync(CancellationToken.None));
            return home;
        }
        return await RefreshHomeAsync(ct).ConfigureAwait(false) ?? (refresh ? null : home);
    }

    /// <summary>A fresh home page, or null when it failed; one already on its way is waited for, not fetched twice.</summary>
    private async Task<Home?> RefreshHomeAsync(CancellationToken ct)
    {
        Home? before;
        lock (_gate) before = _home;
        if (!await _homeRefresh.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await _homeRefresh.WaitAsync(ct).ConfigureAwait(false);
            _homeRefresh.Release();
            // The one waited for failed when it left the page as it was: a Refresh then says so.
            lock (_gate) return ReferenceEquals(_home, before) ? null : _home;
        }
        try
        {
            var total = Stopwatch.StartNew();
            var failed = 0;
            // One list failing costs that shelf, not the page.
            async Task<IReadOnlyList<T>> Shelf<T>(string what, Func<Task<IReadOnlyList<T>>> read)
            {
                try { return await read().ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref failed);
                    AppLog.Warn($"Store: {what} did not load. {ex.Message}");
                    return [];
                }
            }
            var sellers = Timed(() => Shelf("top sellers", () => QueryAsync("top sellers", Released, 200, ct)));
            var upcoming = Timed(() => Shelf("upcoming", () => QueryAsync("upcoming", ComingSoon, 80, ct)));
            var specials = Timed(() => Shelf("on sale", () => QueryAsync("on sale", OnSale, 60, ct)));
            var wished = Timed(() => Shelf("most wishlisted", async () => await WithItemsAsync(await ListAsync("filter=popularwishlist", 80, ct).ConfigureAwait(false), ct).ConfigureAwait(false)));
            await Task.WhenAll(sellers, upcoming, specials, wished).ConfigureAwait(false);
            var home = BuildHome(sellers.Result.Value, upcoming.Result.Value, wished.Result.Value, specials.Result.Value) with { Country = Country };
            AppLog.Info($"PERF store home sellersMs={sellers.Result.Ms} upcomingMs={upcoming.Result.Ms} saleMs={specials.Result.Ms} wishedMs={wished.Result.Ms} totalMs={total.ElapsedMilliseconds}");
            if (home.Hero.Count == 0 && home.Shelves.All(s => s.Items.Count == 0)) return null;
            // A whole page already here stays over a part of one, and a part of one is never saved as if Steam had sent it.
            if (failed > 0 && before is not null) return null;
            if (failed == 0) Write(HomeFile, home);
            lock (_gate) _home = home;
            Raise("home");
            return home;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            AppLog.Warn("Store: Steam did not answer. " + ex.Message);
            return null;
        }
        finally
        {
            _homeRefresh.Release();
        }
    }

    /// <summary>One of Steam's store lists (most wishlisted): games only, no demos, soundtracks, hardware or bundles.</summary>
    private async Task<IReadOnlyList<Tile>> ListAsync(string filter, int count, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{Steam}search/results/?{filter}&json=1&count={count + 20}&cc={Country}&l=english&category1=998", ct).ConfigureAwait(false);
        var tiles = new List<Tile>();
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            if (Str(item, "name") is not { } name || IsNotAGame(name)) continue;
            if (Str(item, "logo") is not { } logo || AppInLogo().Match(logo) is not { Success: true } m) continue;
            if (tiles.All(t => t.AppId != m.Groups[1].Value)) tiles.Add(new Tile(m.Groups[1].Value, name.Trim()));
            if (tiles.Count == count) break;
        }
        return tiles;
    }

    /// <summary>Steam prices in the region's currency for many apps at once (appdetails with filters=price_overview).</summary>
    private async Task<Dictionary<string, SteamPrice>> PricesAsync(IEnumerable<string> appIds, CancellationToken ct)
    {
        var country = Country;
        var result = new Dictionary<string, SteamPrice>(StringComparer.Ordinal);
        foreach (var chunk in appIds.Chunk(100))
        {
            using var doc = await GetJsonAsync($"{Steam}api/appdetails?appids={string.Join(',', chunk)}&filters=price_overview&cc={country}", ct).ConfigureAwait(false);
            foreach (var id in chunk)
            {
                if (!doc.RootElement.TryGetProperty(id, out var wrap) || !wrap.TryGetProperty("data", out var data)) continue;
                result[id] = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("price_overview", out var p)
                    ? ReadPriceOverview(p)
                    : new SteamPrice(0, 0, 0, null, null);
            }
        }
        return result;
    }

    /// <summary>appdetails' price_overview: cents, discount, currency and Steam's text for both prices.</summary>
    private static SteamPrice ReadPriceOverview(JsonElement p) =>
        new((int)Num(p, "final"), (int)Num(p, "initial"), (int)Num(p, "discount_percent"), Blank(Str(p, "currency")), Blank(Str(p, "final_formatted")), Blank(Str(p, "initial_formatted")));

    // ------------------------------------------------------------------ wishlist

    /// <summary>The wishlist, or null while wishlist.json cannot be read (busy), so the room never shows it as empty.</summary>
    internal IReadOnlyList<WishItem>? Wishlist()
    {
        lock (_gate) return LoadWishlistLocked()?.ToList();
    }

    /// <summary>
    /// The wishlist, or null while wishlist.json cannot be read. A failed read
    /// is never cached or saved: caching it as empty and saving that on the next
    /// change erased the whole list. Content that can never parse is moved aside.
    /// </summary>
    private List<WishItem>? LoadWishlistLocked()
    {
        if (_wishlist is not null) return _wishlist;
        try
        {
            if (!File.Exists(WishFile)) return _wishlist = [];
            return _wishlist = JsonSerializer.Deserialize<List<WishItem>>(File.ReadAllText(WishFile), Json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            AppLog.Warn("Store: wishlist.json could not be read: " + ex.GetType().Name);
            return PathHelper.MoveAsideUnreadable(WishFile) ? _wishlist = [] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Store: wishlist.json could not be read: " + ex.GetType().Name);
            return null;
        }
    }

    /// <summary>More than a person wishes for: a list past this drops its oldest, so each price check stays a few requests.</summary>
    internal const int MaxWishlist = 1000;

    /// <summary>
    /// Adds or removes a game and answers the new list; the caller shows it, so
    /// nothing is raised here. A new game's price is checked behind it, which
    /// raises "wishlist" once when it lands. Null, with nothing changed, while
    /// wishlist.json cannot be read or the change cannot be saved.
    /// </summary>
    internal IReadOnlyList<WishItem>? Wish(string appId, string name, bool add)
    {
        List<WishItem> trimmed = [];
        lock (_gate)
        {
            if (LoadWishlistLocked() is not { } current) return null;
            var list = current.ToList();
            list.RemoveAll(w => w.AppId == appId);
            if (add) list.Insert(0, new WishItem(appId, name, DateTimeOffset.UtcNow));
            if (list.Count > MaxWishlist)
            {
                trimmed = list.GetRange(MaxWishlist, list.Count - MaxWishlist);
                list.RemoveRange(MaxWishlist, list.Count - MaxWishlist);
            }
            // Shown only once it is on disk: a full disk or a lock must not leave a game on screen that the next start loses.
            if (!Write(WishFile, list)) return null;
            _wishlist = list;
        }
        Account.AccountSyncHooks.Wished(appId, name, DateTimeOffset.UtcNow, add);
        // Wishes the limit dropped are removals too: without them the other PCs keep (and send back) what this one forgot.
        foreach (var dropped in trimmed) Account.AccountSyncHooks.Wished(dropped.AppId, dropped.Name, DateTimeOffset.UtcNow, add: false);
        if (add) _ = Task.Run(async () =>
        {
            try { await CheckWishlistAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { AppLog.Debug("Store: wishlist check failed: " + ex.Message); }
        });
        return Wishlist();
    }

    /// <summary>
    /// A wish from the player's other PC (the account sync): a game added there joins this list with its own date, one removed
    /// there leaves it. Nothing is synced back, and a game already here keeps its prices and the sale last announced.
    /// </summary>
    internal void ApplySyncedWish(string value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(value)?.AsObject() ?? throw new FormatException("Not a wish.");
        var appId = node["appId"]?.GetValue<string>() is { Length: > 0 and <= 32 } id && id.All(char.IsAsciiDigit) ? id : throw new FormatException("A wish has no game.");
        WishItem? added = null;
        if (node["removed"]?.GetValue<bool>() != true)
        {
            var name = node["name"]?.GetValue<string>() ?? "";
            if (!DateTimeOffset.TryParse(node["addedAt"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
                at = DateTimeOffset.UtcNow;
            added = new WishItem(appId, name.Length > 300 ? name[..300] : name, at);
        }
        lock (_gate)
        {
            // Said, not swallowed: the sync logs that this wish was not applied.
            if (LoadWishlistLocked() is not { } list) throw new IOException("The wishlist could not be read, so a wish from another PC was not applied.");
            var here = list.FindIndex(w => w.AppId == appId);
            if (added is null ? here < 0 : here >= 0) return;
            if (added is null) list.RemoveAt(here);
            else
            {
                list.Add(added);
                list.Sort((a, b) => b.AddedAt.CompareTo(a.AddedAt));
                if (list.Count > MaxWishlist) list.RemoveRange(MaxWishlist, list.Count - MaxWishlist);
            }
            Write(WishFile, list);
        }
        Raise("wishlist");
    }

    /// <summary>Steam prices for the wishlist; a game on sale below the price last announced raises <see cref="SaleFound"/> once. Without <paramref name="raise"/> the caller shows the list itself.</summary>
    internal async Task CheckWishlistAsync(CancellationToken ct, bool raise = true)
    {
        // One check at a time, so two overlapping checks never announce the same sale twice.
        await _wishCheck.WaitAsync(ct).ConfigureAwait(false);
        try { await CheckWishlistOnceAsync(ct, raise).ConfigureAwait(false); }
        finally { _wishCheck.Release(); }
    }

    private async Task CheckWishlistOnceAsync(CancellationToken ct, bool raise)
    {
        if (Wishlist() is not { Count: > 0 } list) return;
        var prices = await PricesAsync(list.Select(w => w.AppId), ct).ConfigureAwait(false);
        var updated = new Dictionary<string, WishItem>(StringComparer.Ordinal);
        foreach (var w in list)
        {
            if (!prices.TryGetValue(w.AppId, out var p)) continue;
            var next = w with { Final = p.Final, Initial = p.Initial, Discount = p.Discount, Currency = p.Currency, PriceText = p.Formatted, WasText = p.WasFormatted, CheckedAt = DateTimeOffset.UtcNow };
            // A price last announced in another currency (the region changed) says nothing about this one.
            var told = w.Currency is null || w.Currency == p.Currency ? w.NotifiedAt : null;
            if (p.Discount > 0 && p.Final is { } final && (told is null || final < told))
            {
                next = next with { NotifiedAt = final };
                if (w.CheckedAt is not null) SaleFound?.Invoke(next);
            }
            else if (p.Discount == 0) next = next with { NotifiedAt = null };
            updated[w.AppId] = next;
        }
        lock (_gate)
        {
            if (_wishlist is not { } current) return;
            for (var i = 0; i < current.Count; i++)
                if (updated.TryGetValue(current[i].AppId, out var fresh)) current[i] = fresh;
            Write(WishFile, current);
        }
        if (raise) Raise("wishlist");
    }

    // ------------------------------------------------------------------ helpers

    private const int MaxJsonBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The resolver said the name does not exist, has no address of the kind asked for, or could not be reached
    /// (WSAHOST_NOT_FOUND, WSANO_DATA, WSATRY_AGAIN). A blip looks the same as a real outage, so a caller can ask again;
    /// it is never an empty catalog.
    /// </summary>
    internal static bool DnsMiss(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData or System.Net.Sockets.SocketError.TryAgain }) return true;
        return false;
    }

    /// <summary>
    /// How long a DNS miss waits before the next ask: about three seconds in all, in short steps. A Store page waited
    /// 1.3 s or 3.1 to 3.9 s (one or two of the old fixed one and two second waits) after a miss that Windows had often
    /// already cleared.
    /// </summary>
    private static readonly TimeSpan[] DnsRetryWaits = [TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)];

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ReadStoreJsonAsync(url, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < DnsRetryWaits.Length && DnsMiss(ex) && !ct.IsCancellationRequested)
            {
                AppLog.Info($"Store: {new Uri(url).Host} did not resolve, asking again in {DnsRetryWaits[attempt].TotalMilliseconds:F0} ms.");
                await Task.Delay(DnsRetryWaits[attempt], ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<JsonDocument> ReadStoreJsonAsync(string url, CancellationToken ct)
    {
        // HttpClient.Timeout stops at the headers; the deadline also bounds a stalled body.
        using var deadline = ExoHttpTransport.Deadline(_http, ct);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ExoHttpTransport.ReadJsonAsync(response.Content, MaxJsonBytes, deadline.Token).ConfigureAwait(false)
            ?? throw new JsonException("Store: empty, invalid or oversized JSON from " + new Uri(url).Host + ".");
    }

    /// <summary>A call and how long it took, for the PERF lines.</summary>
    private static async Task<(T Value, long Ms)> Timed<T>(Func<Task<T>> start)
    {
        var sw = Stopwatch.StartNew();
        var value = await start().ConfigureAwait(false);
        return (value, sw.ElapsedMilliseconds);
    }

    private static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    private static bool Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Store: could not save " + Path.GetFileName(path) + ": " + ex.Message);
            return false;
        }
    }

    private void Raise(string what)
    {
        try { Changed?.Invoke(what); } catch { }
    }

    /// <summary>The Store was used: what it keeps in memory stays until it is left alone <see cref="KeepWhileIdle"/>. Call under <see cref="_gate"/>.</summary>
    private void Used() => (_release ??= new Timer(_ => Release())).Change(KeepWhileIdle, Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Lets go of what the Store can read again from its files (home, the page made for you, card facts, tag names,
    /// titles looked up) or ask Steam for again (game pages, searches, lists for the calendar and your taste). The
    /// wishlist stays (the price check reads it), and so do the few other stores' prices and recent reviews: those
    /// services limit how often Exo may ask.
    /// </summary>
    private void Release()
    {
        bool unsaved;
        lock (_gate) unsaved = _items is not null && _itemsUnsaved;
        if (unsaved) SaveItems();
        lock (_gate)
        {
            _home = null;
            _items = null;
            _tags = null;
            _titleIds = null;
            _forYou = null;
            Volatile.Write(ref _forYouRead, 0);
            _calendarCandidates = null;
            _pools.Clear();
            _details.Clear();
            _searches.Clear();
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static decimal Dec(JsonElement e, string name) =>
        decimal.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static int? Cents(JsonElement e, string name) =>
        decimal.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d * 100) : null;

    private static long Num(JsonElement e, string name) =>
        long.TryParse(Str(e, name)?.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _release?.Dispose();
            _release = null;
            _search?.Dispose();
            _search = null;
        }
        _http.Dispose();
        _homeRefresh.Dispose();
    }

    /// <summary>
    /// Store lists are for games: demos, soundtracks, artbooks, season passes, DLC packs, hardware and bundles are left
    /// out. Words that also open real titles (DLC Quest, Costume Quest, Upgrade Complete) count only after the first
    /// word, where they describe an add-on ("Deluxe Upgrade", "Costume Pack"); the rest count anywhere.
    /// </summary>
    internal static bool IsNotAGame(string name)
    {
        name = name.Trim();
        if (NotAGameAnywhere().IsMatch(name) || EndsInCostume().IsMatch(name) || LeadingAddOn().IsMatch(name)) return true;
        // Any later mention counts: "DLC Quest Bundle" is a bundle. The title's own first word never does.
        return NotAGameAfterTitle().Matches(name).Any(m => m.Index > 0);
    }

    [GeneratedRegex(@"\b(demo|playtest|soundtrack|ost|artbook|art book|season pass|expansion pass|skin pack|steam deck|valve index|steam controller|steam frame|steam machine)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotAGameAnywhere();

    [GeneratedRegex(@"\b(dlc|upgrade|bundle|costume pack|costume set)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotAGameAfterTitle();

    /// <summary>A leading word that the rest of the name shows to be an add-on: "DLC Pack 1", "Upgrade Pack for X", "Costume Pack: Elf", "Bundle: X &amp; Y".</summary>
    [GeneratedRegex(@"^(dlc|upgrade|costume)\s+(pack|set)\b|^bundle\s*[:\-]", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingAddOn();

    /// <summary>"Hero Costume", "Warrior Costumes": an outfit sold on its own, after at least one other word.</summary>
    [GeneratedRegex(@"\S\s+costumes?$", RegexOptions.IgnoreCase)]
    private static partial Regex EndsInCostume();

    /// <summary>The app id in a Steam search result's capsule URL (…/steam/apps/1422450/…).</summary>
    [GeneratedRegex(@"/steam/apps/(\d+)/")]
    private static partial Regex AppInLogo();
}
