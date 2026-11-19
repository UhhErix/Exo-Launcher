using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services.Account;

/// <summary>The queue file, account-sync.json (CLIENT.md 7.5). Written atomically; a damaged file is set aside, not deleted.</summary>
internal static class AccountSyncStore
{
    internal const string FileName = "account-sync.json";

    internal static SyncState Load(string dataDir, string accountId)
    {
        var path = Path.Combine(dataDir, FileName);
        if (!File.Exists(path)) return new SyncState { AccountId = accountId };
        try
        {
            var state = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(path));
            if (state is not null && state.AccountId == accountId) return state;
            // Another account's queue never goes to this one.
            if (state is not null) AppLog.Warn("Account sync queue belonged to another account; starting an empty queue.");
            return new SyncState { AccountId = accountId };
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Account sync queue could not be read; it is set aside: " + e.GetType().Name);
            try { File.Move(path, path + ".damaged", overwrite: true); } catch (IOException) { /* kept where it is */ }
            return new SyncState { AccountId = accountId };
        }
    }

    internal static void Save(string dataDir, SyncState state)
    {
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Runs the sync for the signed-in account: pulls on start and then every 30 seconds, pushes what is due, and persists the
/// queue after each change. Local changes come in through <see cref="AccountSyncHooks"/>. Signed out, it does nothing; a
/// session the service ended is cleared (<see cref="AccountSessionStore.EndIfCurrent"/>), so the PC is asked to sign in again.
/// </summary>
internal sealed class AccountSyncHost : IDisposable
{
    private readonly string dataDir;
    private readonly ExoAccountClient client;
    private readonly Func<AccountSession?> session;
    private readonly Func<PulledItem, bool> onPulled;
    private readonly object gate = new();
    private readonly SemaphoreSlim running = new(1, 1);
    private AccountSyncEngine? engine;
    private string? deviceId;
    private Timer? timer;

    /// <summary>What changed while no account was signed in, until one is (see Remember).</summary>
    internal const string PendingFile = "account-sync-pending.json";

    private DateTimeOffset heldSweepAt;
    private string? lastToken;

    /// <summary>A pulled item this PC should apply: its type, id and value, and whether it is new to this PC.</summary>
    internal sealed record PulledItem(string Type, string Id, string? Value, bool IsNew);

    internal AccountSyncHost(string dataDir, ExoAccountClient client, Func<AccountSession?> session, Action<PulledItem> onPulled)
        : this(dataDir, client, session, item => { onPulled(item); return true; }) { }

    /// <param name="onPulled">Applies one pulled item; false when it could not be applied, so the next pass tries it again.</param>
    internal AccountSyncHost(string dataDir, ExoAccountClient client, Func<AccountSession?> session, Func<PulledItem, bool> onPulled)
    {
        this.dataDir = dataDir;
        this.client = client;
        this.session = session;
        this.onPulled = onPulled;
    }

    internal sealed record HeldItem(string Key, string Reason);

    /// <summary>A snapshot of the sync for the settings page.</summary>
    internal sealed record AccountSyncStatus(DateTimeOffset? LastSyncAt, int Pending, IReadOnlyList<HeldItem> Held, bool SignedOut, bool PushStopped);

    /// <summary>A pass pulls and pushes every 30 s; a local change pulls the push in after the debounce (Write). Nothing runs while signed out.</summary>
    private static readonly TimeSpan SyncEvery = TimeSpan.FromSeconds(30);

    internal void Start() => timer ??= new Timer(_ => _ = SyncAsync(CancellationToken.None), null, TimeSpan.Zero, SyncEvery);

    /// <summary>Records a local change to a synced item. Ignored when no account is signed in.</summary>
    internal void Write(string type, string id, string? value)
    {
        lock (gate)
        {
            var sign = session();
            if (sign is null)
            {
                Remember(type, id, value);
                return;
            }
            var state = Engine(sign);
            try
            {
                state.Write(type, id, value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow);
                AccountSyncStore.Save(dataDir, state.State);
                timer?.Change(AccountSyncRules.Debounce, SyncEvery);
            }
            catch (ArgumentException e)
            {
                AppLog.Warn("Sync write refused: " + e.Message);
            }
        }
    }

    /// <summary>This PC's playtime entry for one play session (the id is device and session).</summary>
    internal void Playtime(string gameId, DateTimeOffset start, DateTimeOffset end, int minutes)
    {
        string id;
        lock (gate) id = AccountSyncRules.LogEntryId(DeviceId(), gameId + "|" + start.ToString("O"));
        Write("playtime", id, AccountSyncMap.PlaytimeValue(gameId, start, end, minutes));
    }

    /// <summary>One pass: pull every page, then push what is due. Concurrent calls wait for the one running.</summary>
    internal async Task SyncAsync(CancellationToken ct)
    {
        if (!await running.WaitAsync(0, ct)) return;
        try
        {
            var sign = session();
            if (sign is null) return;
            lock (gate)
            {
                DrainPending(sign);
                // Refused items are sent again hourly (ReleaseHeld), and at sign-in.
                if (DateTimeOffset.UtcNow >= heldSweepAt)
                {
                    Engine(sign).ReleaseHeld();
                    heldSweepAt = DateTimeOffset.UtcNow + TimeSpan.FromHours(1);
                }
            }
            RetryUnapplied(sign);
            if (await PullAllAsync(sign, ct))
            {
                SeedOnce(sign);
                try { AfterFullPull?.Invoke(); }
                catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException) { AppLog.Warn("Sync: after a pull: " + e.GetType().Name); }
            }
            await PushDueAsync(sign, ct);
            bool ended;
            lock (gate)
            {
                if (engine is not null && !engine.State.SignedOut) engine.State.LastSyncAt = DateTimeOffset.UtcNow;
                if (engine is not null) AccountSyncStore.Save(dataDir, engine.State);
                ended = engine?.State.SignedOut == true;
            }
            // The service ended this session: the session file goes (its queue stays on disk), so the page asks to sign in.
            if (ended) AccountSessionStore.EndIfCurrent(dataDir, sign.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            running.Release();
        }
    }

    /// <summary>Applies pulled items; one that could not be applied (a wishlist that could not be read) is kept to try again.</summary>
    private void Apply(AccountSession sign, List<PulledItem> pulled)
    {
        foreach (var item in pulled)
        {
            var applied = onPulled(item);
            lock (gate)
            {
                var state = Engine(sign).State;
                var key = AccountSyncRules.Key(item.Type, item.Id);
                if (applied) state.Unapplied.Remove(key);
                else state.Unapplied[key] = item.IsNew || state.Unapplied.GetValueOrDefault(key);
            }
        }
    }

    /// <summary>The pulled items that could not be applied before, with the value this PC has for them now.</summary>
    private void RetryUnapplied(AccountSession sign)
    {
        List<PulledItem> again;
        lock (gate)
        {
            var state = Engine(sign).State;
            again = [.. state.Unapplied.Where(pair => state.Items.ContainsKey(pair.Key))
                .Select(pair => state.Items[pair.Key])
                .Select(entry => new PulledItem(entry.Type, entry.Id, entry.Value, state.Unapplied[AccountSyncRules.Key(entry.Type, entry.Id)]))];
            foreach (var gone in state.Unapplied.Keys.Where(key => !state.Items.ContainsKey(key)).ToList()) state.Unapplied.Remove(gone);
        }
        if (again.Count > 0) Apply(sign, again);
    }

    /// <summary>Pulls every page; true when the last page came in (the account's items are all here).</summary>
    private async Task<bool> PullAllAsync(AccountSession sign, CancellationToken ct)
    {
        // Signed out, or a refusal's backoff (429 with Retry-After, 503, a server error) still running: no request yet.
        lock (gate)
        {
            var waiting = Engine(sign).State;
            if (waiting.SignedOut || DateTimeOffset.UtcNow < waiting.NextAttempt) return false;
        }
        for (var page = 0; page < 1000; page++)
        {
            AccountReply reply;
            long after;
            lock (gate) after = Engine(sign).State.Cursor;
            reply = await client.PullAsync(sign.Token, after);
            if (!reply.Ok)
            {
                lock (gate)
                {
                    var state = Engine(sign);
                    state.ApplyPut(null, reply, DateTimeOffset.UtcNow); // signed-out and failures stop here
                    AccountSyncStore.Save(dataDir, state.State);
                }
                return false;
            }

            List<PulledItem> pulled = [];
            bool more;
            lock (gate)
            {
                var state = Engine(sign);
                state.Applied.Clear();
                state.Added.Clear();
                more = state.Pull(reply, DateTimeOffset.UtcNow);
                foreach (var key in state.Applied)
                {
                    var entry = state.State.Items[key];
                    pulled.Add(new PulledItem(entry.Type, entry.Id, entry.Value, state.Added.Contains(key)));
                }
            }
            Apply(sign, pulled);
            // The cursor moves only once the page is applied, so a page cut short is pulled again (what was applied is idempotent).
            lock (gate) AccountSyncStore.Save(dataDir, Engine(sign).State);
            if (!more) return true;
            ct.ThrowIfCancellationRequested();
        }
        return false;
    }

    private async Task PushDueAsync(AccountSession sign, CancellationToken ct)
    {
        for (var batch = 0; batch < 1000; batch++)
        {
            List<SyncItem>? due;
            lock (gate) due = Engine(sign).NextPush(DateTimeOffset.UtcNow);
            if (due is null) return;

            var reply = await client.PutItemsAsync(sign.Token, due);
            lock (gate)
            {
                var state = Engine(sign);
                state.ApplyPut(due, reply, DateTimeOffset.UtcNow);
                AccountSyncStore.Save(dataDir, state.State);
            }
            // A backoff makes NextPush return null next time; a split or a hold goes on with the rest of the queue.
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// A change made with no account signed in waits here (AccountSyncPending), so it reaches the account at sign-in. Nothing
    /// here may fail the caller: a play session's end and a settings save are kept on this PC even when this file cannot be written.
    /// </summary>
    private void Remember(string type, string id, string? value)
    {
        try
        {
            var items = AccountSyncPending.Load(PendingPath());
            AccountSyncPending.Add(items, new PendingItem(type, id, value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), AccountSyncPending.MaxBytes);
            AccountSyncPending.Save(PendingPath(), items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("Sync: a change made with no account signed in was not kept: " + ex.GetType().Name);
        }
    }

    /// <summary>The waiting changes join the account queue (an item already queued is replaced by its later value), then the file goes.</summary>
    private void DrainPending(AccountSession sign)
    {
        List<PendingItem> items;
        try { items = AccountSyncPending.Load(PendingPath()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("Sync: the waiting changes could not be read at sign-in: " + ex.GetType().Name);
            return;
        }
        if (items.Count == 0) return;
        var state = Engine(sign);
        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            try { state.Write(item.Type, item.Id, item.Value, item.Updated, now); }
            catch (ArgumentException e) { AppLog.Warn("Sync write refused: " + e.Message); }
        }
        AccountSyncStore.Save(dataDir, state.State);
        try { if (File.Exists(PendingPath())) DeletionGate.DeleteFile(PendingPath()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Sync: the waiting changes were not cleared: " + ex.GetType().Name); }
    }

    private string PendingPath() => Path.Combine(dataDir, PendingFile);

    /// <summary>
    /// This PC's choices from before they synced (a wishlist, a Riot ID, a music service), as items: each goes up once, after a
    /// pull, and only when the account has never had that item. One another PC removed is a deletion the pull brought, so it
    /// is not put back.
    /// </summary>
    /// <remarks>
    /// <c>Same</c> names a property of the value that says what the item is about (a wish's appId): an item of that type already
    /// about the same thing, a removed one too, means the account has had it. Without it the item's name decides. The seed
    /// runs once per account, kept in the queue file, so a later start never puts back what another PC removed.
    /// </remarks>
    internal Func<IEnumerable<(string Type, string Id, string Value, string? Same)>>? Seeds { get; init; }

    private void SeedOnce(AccountSession sign)
    {
        if (Seeds is null) return;
        lock (gate)
        {
            var state = Engine(sign);
            if (state.State.Seeded || state.State.SignedOut) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var (type, id, value, same) in Seeds())
            {
                if (same is null ? state.State.Items.ContainsKey(AccountSyncRules.Key(type, id)) : Holds(state.State, type, same, value)) continue;
                try { state.Write(type, id, value, now.ToUnixTimeMilliseconds(), now); }
                catch (ArgumentException e) { AppLog.Warn("Sync write refused: " + e.Message); }
            }
            state.State.Seeded = true;
            AccountSyncStore.Save(dataDir, state.State);
        }
    }

    /// <summary>Whether an item of the type is already about what <paramref name="value"/> is about (its <paramref name="same"/> property).</summary>
    private static bool Holds(SyncState state, string type, string same, string value)
    {
        var wanted = JsonNode.Parse(value)?[same]?.ToJsonString();
        return wanted is not null && state.Items.Values.Any(entry => entry.Type == type && entry.Value is not null
            && JsonNode.Parse(entry.Value)?[same]?.ToJsonString() == wanted);
    }

    /// <summary>The live items of one type with their names, for a hook that finds its own (a wish by its game).</summary>
    internal IReadOnlyList<(string Id, string Value)> ItemsOf(string type)
    {
        lock (gate)
            return engine?.State.Items.Values
                .Where(entry => entry.Type == type && !entry.Deleted && entry.Value is not null)
                .Select(entry => (entry.Id, entry.Value!))
                .ToList() ?? [];
    }

    /// <summary>This account's last known value of one item, as the pull left it (null when there is none).</summary>
    internal string? KnownValue(string type, string id)
    {
        lock (gate)
            return engine?.State.Items.TryGetValue(AccountSyncRules.Key(type, id), out var entry) == true && !entry.Deleted ? entry.Value : null;
    }

    /// <summary>Runs after each pull that read the whole account (the moved page choices settle against it).</summary>
    internal Action? AfterFullPull { get; init; }

    private AccountSyncEngine Engine(AccountSession sign)
    {
        if (engine is null || engine.State.AccountId != sign.AccountId)
            engine = new AccountSyncEngine(AccountSyncStore.Load(dataDir, sign.AccountId));
        // A new sign-in (a new token) lifts a signed-out stop left by the old one.
        if (sign.Token != lastToken)
        {
            engine.State.SignedOut = false;
            engine.ReleaseHeld();
            lastToken = sign.Token;
        }
        return engine;
    }

    private string DeviceId() => deviceId ??= AccountDevice.Id(dataDir);

    /// <summary>The values of this account's synced items of one type, live ones only (a cover picture's arrival re-reads its choices).</summary>
    internal IReadOnlyList<string> ValuesOf(string type)
    {
        lock (gate)
            return engine?.State.Items.Values
                .Where(entry => entry.Type == type && !entry.Deleted && entry.Value is not null)
                .Select(entry => entry.Value!)
                .ToList() ?? new List<string>();
    }

    /// <summary>What Settings > Account shows: the last sync, what waits, and what is held and why.</summary>
    internal AccountSyncStatus Status(AccountSession? sign)
    {
        lock (gate)
        {
            var state = sign is null ? null : Engine(sign).State;
            if (state is null) return new AccountSyncStatus(null, 0, [], false, false);
            var held = state.Held.OrderBy(key => key, StringComparer.Ordinal)
                .Select(key => new HeldItem(key, state.HeldReasons.GetValueOrDefault(key, "Held on this PC."))).ToList();
            return new AccountSyncStatus(state.LastSyncAt, state.Dirty.Count, held, state.SignedOut, state.PushStopped);
        }
    }

    /// <summary>The account is gone or signed out: its queue and cursor are dropped. Local settings and playtime stay.</summary>
    internal void Forget()
    {
        lock (gate)
        {
            engine = null;
            try { DeletionGate.DeleteFile(Path.Combine(dataDir, AccountSyncStore.FileName)); }
            catch (IOException) { /* the next sign-in starts an empty queue for its account */ }
        }
    }

    public void Dispose()
    {
        timer?.Dispose();
        running.Dispose();
    }
}

/// <summary>The hooks the Launcher's own services call. They do nothing until the host is attached at start.</summary>
internal static class AccountSyncHooks
{
    private static volatile AccountSyncHost? host;

    internal static void Attach(AccountSyncHost? value) => host = value;

    /// <summary>The running sync, or null before the Launcher starts it.</summary>
    internal static AccountSyncHost? Current => host;

    /// <summary>A play session ended: its minutes are one playtime entry (CLIENT.md 10).</summary>
    internal static void PlaySessionEnded(string gameId, DateTimeOffset start, DateTimeOffset end, int minutes)
    {
        if (minutes <= 0 || AccountSyncRules.PlaytimeCarriedByLedger(gameId)) return;
        host?.Playtime(gameId, start, end, minutes);
    }

    /// <summary>Settings were saved: the synced part goes up when it changed.</summary>
    internal static void SettingsSaved(AppSettings settings)
    {
        host?.Write("settings", AccountSyncMap.SettingsId, AccountSyncMap.SettingsValue(settings).ToJsonString());
    }

    /// <summary>
    /// A custom cover was picked: its choice syncs, and its picture too when it fits one item (48 KiB of base64, CLIENT.md 8).
    /// A bigger picture stays on this PC, so another PC shows the store's cover until it has the picture.
    /// </summary>
    internal static void CoverChosen(string gameId, string fileName)
    {
        var sync = host;
        if (sync is null) return;
        try
        {
            var sha = Path.GetFileNameWithoutExtension(fileName)["custom-cover-".Length..];
            var ext = Path.GetExtension(fileName);
            var image = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(CoverArtService.CacheRoot, fileName)));
            sync.Write("cover", AccountSyncRules.GameKeyId(gameId), new JsonObject { ["source"] = "custom", ["sha256"] = sha, ["ext"] = ext, ["game"] = gameId }.ToJsonString());
            if (image.Length <= AccountSyncApply.MaxCoverImageBase64)
                sync.Write("cover-image", sha, new JsonObject { ["ext"] = ext, ["data"] = image }.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Debug("A cover choice was not synced: " + e.GetType().Name);
        }
    }

    /// <summary>A Xbox hours seed was converted on this PC: it syncs once (CLIENT.md 8, playtime-seed).</summary>
    internal static void XboxSeedAdded(ReportedHours.TitleHours title) =>
        host?.Write("playtime-seed", AccountSyncRules.GameKeyId("xbox:" + title.TitleId), new JsonObject
        {
            ["title"] = title.TitleId,
            ["name"] = title.Name,
            ["pfn"] = title.Pfn,
            ["minutes"] = title.Minutes,
            ["lastPlayed"] = title.LastPlayedUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        }.ToJsonString());

    /// <summary>The ids of the "choice" items: one value each, newest wins (PlayerData).</summary>
    internal const string RiotIdChoice = "riot-id";
    internal const string MusicServiceChoice = "music-service";

    /// <summary>The player set their Riot ID: it is public, and every PC of theirs uses the same one.</summary>
    internal static void RiotIdChosen(string riotId) => host?.Write("choice", RiotIdChoice, new JsonObject { ["riotId"] = riotId }.ToJsonString());

    /// <summary>The player chose a music service: their other PCs open the same one.</summary>
    internal static void MusicServiceChosen(string service) => host?.Write("choice", MusicServiceChoice, new JsonObject { ["service"] = service }.ToJsonString());

    /// <summary>
    /// A game was wished for, or no longer. Each wish is an item whose name is random ("w." and 24 hex), so the item names the
    /// service stores in the clear say nothing about the games; the game is inside the sealed value, with when it changed.
    /// Every item about one game is one wish (two PCs that wished it offline made two): a change is written onto each of
    /// them, and a new item only when this PC knows none yet (before its first pull, too: a removal made then is kept and
    /// wins over an older add that arrives later, <see cref="AccountSyncApply.SettleWish"/>). A removal is an item marked
    /// removed, not a deletion, so it still says which game it was about.
    /// </summary>
    internal static void Wished(string appId, string name, DateTimeOffset at, bool add)
    {
        var sync = host;
        if (sync is null) return;
        var value = add ? WishValue(appId, name, at, at) : WishRemoved(appId, at);
        var mine = WishItemsFor(sync.ItemsOf("wish"), appId);
        if (mine.Count == 0) mine.Add(NewWishId());
        foreach (var id in mine) sync.Write("wish", id, value);
    }

    /// <summary>The names of the items about one game.</summary>
    internal static List<string> WishItemsFor(IEnumerable<(string Id, string Value)> items, string appId) =>
        [.. items.Where(item => AppIdOf(item.Value) == appId).Select(item => item.Id)];

    internal static string? AppIdOf(string value)
    {
        try { return JsonNode.Parse(value)?["appId"]?.GetValue<string>(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>When a wish value was made (its change time; an older value's addedAt), for "the newest wins".</summary>
    internal static DateTimeOffset WishChangedAt(string value)
    {
        var node = JsonNode.Parse(value);
        var text = node?["at"]?.GetValue<string>() ?? node?["addedAt"]?.GetValue<string>();
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MinValue;
    }

    internal static string NewWishId() => "w." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    internal static string WishValue(string appId, string name, DateTimeOffset addedAt, DateTimeOffset? at = null) =>
        new JsonObject { ["appId"] = appId, ["name"] = name, ["addedAt"] = addedAt.ToString("O", CultureInfo.InvariantCulture), ["at"] = (at ?? addedAt).ToString("O", CultureInfo.InvariantCulture) }.ToJsonString();

    internal static string WishRemoved(string appId, DateTimeOffset at) =>
        new JsonObject { ["appId"] = appId, ["removed"] = true, ["at"] = at.ToString("O", CultureInfo.InvariantCulture) }.ToJsonString();
    /// <summary>The cover went back to the store's: that choice syncs.</summary>
    internal static void CoverReset(string gameId) =>
        host?.Write("cover", AccountSyncRules.GameKeyId(gameId), new JsonObject { ["source"] = "store", ["game"] = gameId }.ToJsonString());

    /// <summary>Settings › Account › Sync now: a pull and push at once, whether or not anything waits.</summary>
    internal static Task SyncNowAsync() => host?.SyncAsync(CancellationToken.None) ?? Task.CompletedTask;
}

/// <summary>Starts the account sync on this PC and applies what it pulls (settings, and other PCs' playtime).</summary>
internal static class AccountSyncRuntime
{
    internal static void Start(SettingsService settings, Func<StoreService> store)
    {
        AccountSyncApply.Wishes = value => store().ApplySyncedWish(value);
        AccountSyncApply.WishItems = () => AccountSyncHooks.Current?.ItemsOf("wish") ?? [];
        AccountSyncApply.WriteWish = (id, value) => AccountSyncHooks.Current?.Write("wish", id, value);
        AccountSyncHost? host = null;
        host = new AccountSyncHost(
            PathHelper.AppDataDir,
            new ExoAccountClient(),
            () => AccountSessionStore.Load(PathHelper.AppDataDir),
            item => AccountSyncApply.Pulled(item, settings))
        {
            Seeds = () => Seeds(store()),
            AfterFullPull = () => settings.SettleMovedUiPrefs(host?.KnownValue("settings", AccountSyncMap.SettingsId)),
        };
        AccountSyncHooks.Attach(host);
        host.Start();
    }

    /// <summary>The choices this PC had before they synced: its wishlist (each wish found by its game), its Riot ID and its music service.</summary>
    internal static IEnumerable<(string Type, string Id, string Value, string? Same)> Seeds(StoreService store)
    {
        foreach (var wish in store.Wishlist() ?? [])
            yield return ("wish", AccountSyncHooks.NewWishId(), AccountSyncHooks.WishValue(wish.AppId, wish.Name, wish.AddedAt), "appId");
        if (RiotIdStore.TryRead() is { } riotId && RiotIdStore.Normalize(riotId) is { } id)
            yield return ("choice", AccountSyncHooks.RiotIdChoice, new JsonObject { ["riotId"] = id }.ToJsonString(), null);
        if (WebClientPreferences.Read().MusicService is { } service)
            yield return ("choice", AccountSyncHooks.MusicServiceChoice, new JsonObject { ["service"] = service }.ToJsonString(), null);
    }
}

/// <summary>Lays one pulled item over this PC: synced settings, and a playtime entry from another PC, credited once.</summary>
internal static class AccountSyncApply
{
    /// <summary>Applies one pulled item; false only when a file it goes into could not be read or written, so the sync tries again.</summary>
    internal static bool Pulled(AccountSyncHost.PulledItem item, SettingsService settings)
    {
        try
        {
            if (item.Value is null) return true;
            if (item.Type == "wish")
                SettleWish(AccountSyncHooks.AppIdOf(item.Value) ?? throw new FormatException("A wish has no game."));
            else if (item.Type == "choice")
                ApplyChoice(item.Id, item.Value);
            else if (item.Type == "settings" && item.Id == AccountSyncMap.SettingsId)
                settings.UpdateProfile(target => AccountSyncMap.ApplySettings(target, item.Value));
            else if (item.Type == "riot-ledger")
                RiotLedger.Merge(item.Value);
            else if (item.Type == "playtime-seed")
                PlaytimeService.AddSyncedSeed(item.Value);
            else if (item.Type == "cover")
                ApplyCover(item.Value, settings.SetCustomCoverImages);
            else if (item.Type == "cover-image")
                // A picture may come after its choice: once it is on this PC, every synced choice is looked at again.
                ApplyCoverImage(item.Id, item.Value, () => AccountSyncHooks.Current?.ValuesOf("cover") ?? new List<string>(), settings.SetCustomCoverImages);
            else if (item.Type == "playtime" && item.IsNew)
            {
                var entry = System.Text.Json.Nodes.JsonNode.Parse(item.Value)?.AsObject();
                var game = entry?["game"]?.GetValue<string>() ?? "";
                var minutes = entry?["minutes"]?.GetValue<int>() ?? 0;
                CheckPlaytimeMinutes(item.Value);
                if (!DateTimeOffset.TryParse(entry?["start"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start))
                    throw new FormatException("A playtime entry has no start.");
                // A Riot game's minutes come from its riot-ledger lines; crediting this entry too would count them twice.
                if (!AccountSyncRules.PlaytimeCarriedByLedger(game)) PlaytimeService.CreditSynced(game, start, minutes);
            }
        }
        // A file that could not be read or written now (the wishlist, held by a scan) is tried again at the next pass.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("A synced item could not be applied yet; the next sync tries again: " + e.GetType().Name);
            return false;
        }
        // One item that cannot be applied (a bad seed, a value of another shape) never stops the page: the next items still apply.
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppLog.Warn("A synced item could not be applied: " + e.GetType().Name);
        }
        return true;
    }

    /// <summary>The wish items this account has (each a name and a value), and how one is written.</summary>
    internal static Func<IReadOnlyList<(string Id, string Value)>>? WishItems { get; set; }
    internal static Action<string, string>? WriteWish { get; set; }

    /// <summary>
    /// Every item about one game is one wish: the newest change across them (its time, not its arrival order) is applied to
    /// this PC's wishlist and written onto every other item about that game, so a removal reaches an item another PC made
    /// offline, and one pulled later.
    /// </summary>
    internal static void SettleWish(string appId)
    {
        var items = (WishItems?.Invoke() ?? []).Where(item => AccountSyncHooks.AppIdOf(item.Value) == appId).ToList();
        if (items.Count == 0) return;
        var newest = items.OrderByDescending(item => AccountSyncHooks.WishChangedAt(item.Value)).ThenBy(item => item.Value, StringComparer.Ordinal).First();
        Wishes?.Invoke(newest.Value);
        foreach (var item in items)
            if (!string.Equals(item.Value, newest.Value, StringComparison.Ordinal)) WriteWish?.Invoke(item.Id, newest.Value);
    }

    /// <summary>Where a pulled wish lands (the Store's wishlist), set when the sync starts: its value, a game added or removed.</summary>
    internal static Action<string>? Wishes { get; set; }

    /// <summary>A pulled choice: the Riot ID into its file, the music service into the connected-app preferences.</summary>
    internal static void ApplyChoice(string id, string value)
    {
        var node = JsonNode.Parse(value)?.AsObject() ?? throw new FormatException("Not a choice.");
        if (id == AccountSyncHooks.RiotIdChoice)
            RiotIdStore.ApplySynced(node["riotId"]?.GetValue<string>());
        else if (id == AccountSyncHooks.MusicServiceChoice && node["service"]?.GetValue<string>() is { } service && MusicWebPolicy.IsService(service))
            WebClientPreferences.ApplySyncedMusicService(service);
    }

    /// <summary>Largest picture the sync sends or accepts: the base64 must fit one item (CLIENT.md 8).</summary>
    internal const int MaxCoverImageBase64 = 48 * 1024;
    internal const int MaxCoverImageBytes = 64 * 1024;

    /// <summary>The cover cache's file name for a custom picture: its id is the SHA-256 of the bytes, and only PNG or JPEG pass.</summary>
    internal static string? CoverFileName(string sha256, string ext)
    {
        if (sha256.Length != 64 || !sha256.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))) return null;
        return ext is ".png" or ".jpg" ? "custom-cover-" + sha256 + ext : null;
    }

    /// <summary>A synced cover choice: the custom picture when it is on this PC, else the store's cover. A picture not here yet changes nothing.</summary>
    internal static void ApplyCover(string value, Action<IEnumerable<string>, string?> setCover)
    {
        var node = JsonNode.Parse(value)?.AsObject() ?? throw new FormatException("Not a cover choice.");
        var game = node["game"]?.GetValue<string>() ?? "";
        if (game.Length == 0) return;
        if (node["source"]?.GetValue<string>() != "custom")
        {
            setCover([game], null);
            return;
        }
        var file = CoverFileName(node["sha256"]?.GetValue<string>() ?? "", node["ext"]?.GetValue<string>() ?? "");
        if (file is null || !File.Exists(Path.Combine(CoverArtService.CacheRoot, file))) return;
        setCover([game], file);
    }

    /// <summary>A synced picture lands in the cover cache only when its bytes hash to its id, and it is a PNG or JPEG of a size the sync allows.</summary>
    /// <summary>
    /// A synced picture lands in the cover cache, then every synced cover choice is applied again: a choice that came before
    /// its picture is completed here. Only the choices that name this picture change anything.
    /// </summary>
    internal static void ApplyCoverImage(string sha256, string value, Func<IReadOnlyList<string>> choices, Action<IEnumerable<string>, string?> setCover)
    {
        StoreCoverImage(sha256, value);
        foreach (var choice in choices()) ApplyCover(choice, setCover);
    }

    /// <summary>The picture's first bytes are its kind: a file named .png must be a PNG, a .jpg a JPEG.</summary>
    internal static bool IsPictureOf(ReadOnlySpan<byte> bytes, string ext) => ext switch
    {
        ".png" => bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        ".jpg" => bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
        _ => false,
    };

    /// <summary>A synced playtime entry is one session: a week at most. A longer one is refused, so a line credits no more than that.</summary>
    internal static void CheckPlaytimeMinutes(string value)
    {
        var minutes = JsonNode.Parse(value)?["minutes"]?.GetValue<int>() ?? 0;
        if (minutes > RiotLedger.MaxSessionMinutes) throw new FormatException("A playtime entry is longer than a week.");
    }

    internal static void StoreCoverImage(string sha256, string value)
    {
        var node = JsonNode.Parse(value)?.AsObject() ?? throw new FormatException("Not a cover picture.");
        var ext = node["ext"]?.GetValue<string>() ?? "";
        var file = CoverFileName(sha256, ext) ?? throw new FormatException("Not a cover picture.");
        // The base64 is checked for size before it is decoded: a value that cannot fit one item is never read into memory.
        var encoded = node["data"]?.GetValue<string>() ?? "";
        if (encoded.Length > MaxCoverImageBase64) throw new FormatException("A cover picture is too big.");
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > MaxCoverImageBytes || !IsPictureOf(bytes, ext) || Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256)
            throw new FormatException("A cover picture does not match its id.");
        var path = Path.Combine(CoverArtService.CacheRoot, file);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(CoverArtService.CacheRoot);
        var temp = path + ".sync.tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
