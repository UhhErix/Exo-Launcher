using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services.Account;

/// <summary>How a type's conflicts land (CLIENT.md 7.3).</summary>
public enum MergeKind { Log, Seed, Object, Value }

/// <summary>One item as the wire carries it: a PUT entry. A deletion has no value.</summary>
internal sealed record SyncItem(string Type, string Id, long Base, long Updated, string? Value, bool Deleted);

/// <summary>One item on this PC: the last version the server gave, the local change, and for settings the keys changed since.</summary>
internal sealed class SyncEntry
{
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    /// <summary>The last server version this PC saw (0 when none).</summary>
    public long Base { get; set; }
    /// <summary>The device clock of the last local change. It decides only a value conflict (CLIENT.md 7.4).</summary>
    public long Updated { get; set; }
    public string? Value { get; set; }
    public bool Deleted { get; set; }
    /// <summary>Settings: the top-level keys this PC changed since Base, so a conflict keeps the server's other keys.</summary>
    public List<string> Changed { get; set; } = [];
    public string? BaseValue { get; set; }
    public Dictionary<string, long> ChangedAt { get; set; } = [];
}

/// <summary>The queue and the local copy, saved as account-sync.json (CLIENT.md 7.5). It is data, not a secret.</summary>
internal sealed class SyncState
{
    public long Cursor { get; set; }
    public Dictionary<string, SyncEntry> Items { get; set; } = [];
    /// <summary>Keys waiting to go up.</summary>
    public HashSet<string> Dirty { get; set; } = [];
    /// <summary>Keys held on this PC: too big, or refused by the server. Not retried until the value changes.</summary>
    public HashSet<string> Held { get; set; } = [];
    public DateTimeOffset LastChange { get; set; }
    public DateTimeOffset NextAttempt { get; set; }
    public int Failures { get; set; }
    public bool SignedOut { get; set; }
    /// <summary>The account is full: no new pushes until the user frees room; pulls go on.</summary>
    public bool PushStopped { get; set; }
    /// <summary>Why each held key is held, shown on Settings > Account (too big, or refused by the service).</summary>
    public Dictionary<string, string> HeldReasons { get; set; } = [];
    /// <summary>The last time a sync pass finished on this PC.</summary>
    public DateTimeOffset? LastSyncAt { get; set; }
    /// <summary>Pulled items this PC could not apply yet, and whether each was new to it: tried again at the next pass.</summary>
    public Dictionary<string, bool> Unapplied { get; set; } = [];
    /// <summary>This PC's choices from before sync went up to this account once (AccountSyncHost.SeedOnce).</summary>
    public bool Seeded { get; set; }
    /// <summary>The account this queue belongs to. A different account starts an empty queue.</summary>
    public string? AccountId { get; set; }
    /// <summary>Items per PUT. Halved when the service refuses a whole PUT as too big, and back to the full batch after one lands.</summary>
    public int BatchLimit { get; set; } = AccountSyncRules.MaxBatchItems;
    /// <summary>Settings: object conflicts in a row for each key. After three, the queue waits for a later retry.</summary>
    public Dictionary<string, int> ObjectTries { get; set; } = [];
}

/// <summary>The rules the sync engine applies, as pure functions the tests pin (CLIENT.md 7.2 to 7.4).</summary>
internal static partial class AccountSyncRules
{
    internal const int MaxValueBytes = 64 * 1024;
    internal const int MaxBatchItems = 100;
    internal const long MaxBodyBytes = 8L * 1024 * 1024;
    internal const string App = "launcher";
    internal static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    /// <summary>Types the Launcher syncs and how each merges. Anything else never becomes an item.</summary>
    internal static MergeKind? KindOf(string type) => type switch
    {
        "playtime" or "riot-ledger" => MergeKind.Log,
        "playtime-seed" => MergeKind.Seed,
        "settings" => MergeKind.Object,
        "cover" or "cover-image" or "collection" or "choice" or "wish" => MergeKind.Value,
        _ => null,
    };

    internal static bool ValidType(string type) => TypeRule().IsMatch(type);
    internal static bool ValidId(string id) => IdRule().IsMatch(id);

    internal static string Key(string type, string id) => type + "/" + id;

    /// <summary>A game's id: the game key when it fits the rule, else h. plus 40 hex of its SHA-256 (CLIENT.md 7.2).</summary>
    internal static string GameKeyId(string gameKey)
    {
        if (ValidId(gameKey)) return gameKey;
        return "h." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(gameKey)))[..40];
    }

    /// <summary>A log entry's id: s. plus 24 hex of SHA-256 of device and session, so two PCs never give one entry the same id.</summary>
    internal static string LogEntryId(string deviceId, string sessionId) =>
        "s." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId + "|" + sessionId)))[..24];

    /// <summary>A riot-ledger line's id: r. plus 24 hex of SHA-256 of its account and key. It does not depend on the device, so one line is one item on every PC.</summary>
    internal static string RiotLineId(string account, string key) =>
        "r." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(account + "|" + key)))[..24];

    /// <summary>A Riot game's playtime syncs as riot-ledger lines, per Riot ID; a playtime entry for the same minutes would count them twice.</summary>
    internal static bool PlaytimeCarriedByLedger(string gameId) => gameId.StartsWith("riot:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Retry delay for the nth failure: 5 s doubling to 10 minutes, each plus or minus 20% (CLIENT.md 7.6).</summary>
    internal static TimeSpan Backoff(int failures, Random random)
    {
        var seconds = Math.Min(600, 5 * Math.Pow(2, Math.Max(0, failures - 1)));
        var jitter = 1 + (random.NextDouble() * 0.4 - 0.2);
        return TimeSpan.FromSeconds(seconds * jitter);
    }

    /// <summary>The top-level keys whose value differs between two settings objects (a missing one counts as changed).</summary>
    internal static List<string> ChangedKeys(string? before, string? after)
    {
        var old = before is null ? new JsonObject() : JsonNode.Parse(before)?.AsObject() ?? new JsonObject();
        var now = after is null ? new JsonObject() : JsonNode.Parse(after)?.AsObject() ?? new JsonObject();
        return now.Select(p => p.Key)
            .Union(old.Select(p => p.Key))
            .Where(k => !JsonNode.DeepEquals(old[k], now[k]))
            .ToList();
    }

    /// <summary>The server's object with this PC's changed keys laid over it (CLIENT.md 7.4, object).</summary>
    internal static string Overlay(string? server, string? local, IEnumerable<string> changed)
    {
        var result = server is null ? new JsonObject() : JsonNode.Parse(server)?.AsObject() ?? new JsonObject();
        var mine = local is null ? new JsonObject() : JsonNode.Parse(local)?.AsObject() ?? new JsonObject();
        foreach (var key in changed)
        {
            result.Remove(key);
            if (mine[key] is { } value) result[key] = JsonNode.Parse(value.ToJsonString());
        }
        return result.ToJsonString();
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex TypeRule();

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9._:~@-]{1,128}$")]
    private static partial Regex IdRule();
}

/// <summary>
/// The sync engine: local writes mark items dirty, pushes wait for a quiet two seconds, pulls apply the server's items in
/// order, and each reply is applied by the rules of CLIENT.md 7.4 and 7.6. The HTTP calls are made by the caller, so the
/// same code runs against the fake server in the tests and the live service.
/// </summary>
internal sealed class AccountSyncEngine(SyncState state, Random? random = null)
{
    private readonly Random random = random ?? Random.Shared;
    private static readonly TimeSpan ObjectRetryWait = TimeSpan.FromMinutes(1);

    public SyncState State => state;

    /// <summary>Keys the last pulls brought from the server, and those new to this PC (a playtime entry to credit once).</summary>
    public List<string> Applied { get; } = [];
    public List<string> Added { get; } = [];

    /// <summary>Records a local change. A null value on a value type is a deletion. Unknown types are refused.</summary>
    public void Write(string type, string id, string? value, long updated, DateTimeOffset now, IReadOnlyCollection<string>? changedKeys = null, IReadOnlyDictionary<string, long>? changedAt = null)
    {
        var kind = AccountSyncRules.KindOf(type) ?? throw new ArgumentException("Not a synced type: " + type, nameof(type));
        if (!AccountSyncRules.ValidType(type) || !AccountSyncRules.ValidId(id)) throw new ArgumentException("Bad item name: " + type + "/" + id);
        var key = AccountSyncRules.Key(type, id);
        state.Items.TryGetValue(key, out var entry);
        if (kind == MergeKind.Object && changedKeys is null && entry?.Value == value) return; // nothing changed: nothing to send

        switch (kind)
        {
            case MergeKind.Log:
                if (entry is not null) return; // a duplicate id is dropped on this PC
                entry = new SyncEntry { Type = type, Id = id, Updated = updated, Value = value };
                break;
            case MergeKind.Seed:
                if (entry is not null) return; // the first seed wins
                entry = new SyncEntry { Type = type, Id = id, Updated = updated, Value = value };
                break;
            case MergeKind.Object:
                if (entry is null)
                {
                    var initialKeys = changedKeys is not null
                        ? changedKeys.Distinct(StringComparer.Ordinal).ToList()
                        : AccountSyncRules.ChangedKeys(null, value);
                    if (initialKeys.Count == 0 && changedKeys is not null) return;
                    entry = new SyncEntry { Type = type, Id = id, Changed = initialKeys, Value = value };
                    foreach (var k in initialKeys)
                        entry.ChangedAt[k] = changedAt is not null && changedAt.TryGetValue(k, out var ts) ? ts : updated;
                    entry.Updated = entry.ChangedAt.Count > 0 ? entry.ChangedAt.Values.Max() : updated;
                }
                else
                {
                    var nextValue = changedKeys is not null ? AccountSyncRules.Overlay(entry.Value, value, changedKeys) : value;
                    var diff = AccountSyncRules.ChangedKeys(entry.Value, nextValue);
                    var wasDirty = state.Dirty.Contains(key);
                    if (diff.Count == 0 && !wasDirty) return;
                    var tracked = changedKeys is not null
                        ? diff.Union(changedKeys.Where(k => entry.Changed.Contains(k))).ToList()
                        : diff;
                    if (tracked.Count == 0) return;
                    if (entry.BaseValue is null && !wasDirty && entry.Base > 0) entry.BaseValue = entry.Value;
                    entry.Changed = entry.Changed.Union(diff).ToList();
                    foreach (var k in tracked)
                        entry.ChangedAt[k] = changedAt is not null && changedAt.TryGetValue(k, out var ts) ? ts : updated;
                    entry.Value = nextValue;
                    entry.Updated = entry.ChangedAt.Count > 0 ? entry.ChangedAt.Values.Max() : updated;
                }
                break;
            default:
                entry ??= new SyncEntry { Type = type, Id = id };
                entry.Value = value;
                entry.Deleted = value is null;
                entry.Updated = updated;
                break;
        }

        state.Items[key] = entry;
        state.Held.Remove(key);
        state.Dirty.Add(key);
        state.LastChange = now;
    }

    /// <summary>The batch to send now, or null when there is nothing due: nothing dirty, the debounce is still running, a retry is waiting, or the account stopped pushes.</summary>
    public List<SyncItem>? NextPush(DateTimeOffset now, bool ignoreDebounce = false)
    {
        if (state.SignedOut || state.PushStopped || state.Dirty.Count == 0) return null;
        if (now < state.NextAttempt || (!ignoreDebounce && now - state.LastChange < AccountSyncRules.Debounce)) return null;

        var batch = new List<SyncItem>();
        long bytes = 0;
        foreach (var key in state.Dirty.OrderBy(k => k, StringComparer.Ordinal))
        {
            var entry = state.Items[key];
            var size = Encoding.UTF8.GetByteCount(entry.Value ?? "");
            if (size > AccountSyncRules.MaxValueBytes)
            {
                // Too big to send: it stays on this PC until its value changes (CLIENT.md 7.6).
                state.Dirty.Remove(key);
                state.Held.Add(key);
                state.HeldReasons[key] = HeldTooLarge;
                continue;
            }
            if (batch.Count == state.BatchLimit || bytes + size > AccountSyncRules.MaxBodyBytes) break;
            batch.Add(new SyncItem(entry.Type, entry.Id, entry.Base, entry.Updated, entry.Value, entry.Deleted));
            bytes += size;
        }
        return batch.Count == 0 ? null : batch;
    }

    /// <summary>Applies the answer to a PUT of <paramref name="sent"/> (null when no batch went, for an offline or stopped reply).</summary>
    public void ApplyPut(IReadOnlyList<SyncItem>? sent, AccountReply reply, DateTimeOffset now)
    {
        if (reply.Unreachable || reply.Status >= 500)
        {
            // Normal backoff; a busy answer (503 with Retry-After) also waits at least what the service asked (CLIENT.md 7.6).
            Fail(now, null);
            if (reply.RetryAfterSeconds is { } wait && now.AddSeconds(wait) > state.NextAttempt) state.NextAttempt = now.AddSeconds(wait);
            return;
        }
        if (reply.Error == "signed-out") { state.SignedOut = true; return; }
        if (reply.Error == "account-full") { state.PushStopped = true; return; }
        if (reply.Status == 429) { Fail(now, reply.RetryAfterSeconds ?? 60); return; }
        if (reply.Error is "too-big" or "slow-body")
        {
            // A refused whole PUT is split in halves (CLIENT.md 7.6) until the one item that is too big stands alone; only that item is held.
            // A slow body is the same split; one item that went quiet stays queued and is retried on the normal backoff.
            var count = sent?.Count ?? 0;
            if (count > 1) { state.BatchLimit = Math.Max(1, count / 2); return; }
            if (reply.Error == "slow-body") { Fail(now, null); return; }
            if (sent is { Count: 1 }) Hold(AccountSyncRules.Key(sent[0].Type, sent[0].Id), HeldTooLarge);
            return;
        }
        if (!reply.Ok)
        {
            // The account or the request as a whole (401, 403, 408, 429, a reply that is not JSON) waits on the backoff, as an outage does.
            if (reply.Status is 401 or 403 or 408 or 429 || reply.Error is null)
            {
                Fail(now, reply.RetryAfterSeconds);
                return;
            }
            // Any other refusal: the batch is halved until the one item it cannot take stands alone, and only that item is held (the
            // sweep retries it). The rest of the queue keeps going up.
            var count = sent?.Count ?? 0;
            if (count > 1) { state.BatchLimit = Math.Max(1, count / 2); return; }
            if (sent is { Count: 1 }) Hold(AccountSyncRules.Key(sent[0].Type, sent[0].Id), HeldRefused);
            return;
        }

        state.Failures = 0;
        state.BatchLimit = AccountSyncRules.MaxBatchItems;
        var body = reply.Body as JsonObject;
        var sentUpdated = (sent ?? []).ToDictionary(item => AccountSyncRules.Key(item.Type, item.Id), item => item.Updated);
        var sentValues = (sent ?? []).ToDictionary(item => AccountSyncRules.Key(item.Type, item.Id), item => item.Value);

        foreach (var written in Array(body, "written"))
        {
            var key = AccountSyncRules.Key(Str(written, "type"), Str(written, "id"));
            if (!state.Items.TryGetValue(key, out var entry)) continue;
            entry.Base = Num(written, "version");
            state.ObjectTries.Remove(key);
            // A change made while the batch was on the way stays queued.
            if (sentUpdated.TryGetValue(key, out var updated) && entry.Updated == updated)
            {
                state.Dirty.Remove(key);
                entry.Changed.Clear();
                entry.ChangedAt.Clear();
                entry.BaseValue = entry.Value;
            }
            else if (sentValues.TryGetValue(key, out var sentValue))
            {
                entry.BaseValue = sentValue;
            }
        }

        foreach (var conflict in Array(body, "conflicts"))
        {
            var key = AccountSyncRules.Key(Str(conflict, "type"), Str(conflict, "id"));
            if (!state.Items.TryGetValue(key, out var local)) continue;
            var serverValue = conflict["value"]?.GetValue<string>();
            var server = new SyncEntry
            {
                Type = Str(conflict, "type"), Id = Str(conflict, "id"), Base = Num(conflict, "version"),
                Updated = Num(conflict, "updated"), Value = serverValue, BaseValue = serverValue, Deleted = serverValue is null,
            };
            ResolveConflict(key, local, server, now);
        }
    }

    /// <summary>Applies one page of a pull (CLIENT.md 7.7 and 7.4). Returns whether the server has more pages.</summary>
    public bool Pull(AccountReply reply, DateTimeOffset now)
    {
        if (reply.Error == "signed-out") { state.SignedOut = true; return false; }
        if (!reply.Ok || reply.Body is not JsonObject body) return false;

        foreach (var item in Array(body, "items"))
        {
            if (item["app"]?.GetValue<string>() != AccountSyncRules.App) continue;
            var type = Str(item, "type");
            if (AccountSyncRules.KindOf(type) is not { } kind) continue;
            var key = AccountSyncRules.Key(type, Str(item, "id"));
            Applied.Add(key);
            if (!state.Items.ContainsKey(key)) Added.Add(key);
            var serverValue = item["value"]?.GetValue<string>();
            var server = new SyncEntry
            {
                Type = type, Id = Str(item, "id"), Base = Num(item, "version"), Updated = Num(item, "updated"),
                Value = serverValue, BaseValue = serverValue, Deleted = item["deleted"]?.GetValue<bool>() == true || serverValue is null,
            };
            state.Items.TryGetValue(key, out var local);
            var mine = state.Dirty.Contains(key) && local is not null;

            switch (kind)
            {
                case MergeKind.Log:
                    // Logs keep both copies by id: the server copy is added, or this PC rebases its own.
                    if (local is null) state.Items[key] = server;
                    else local.Base = server.Base;
                    state.Dirty.Remove(key);
                    break;
                case MergeKind.Seed:
                    // The first seed on the server wins.
                    state.Items[key] = server;
                    state.Dirty.Remove(key);
                    break;
                case MergeKind.Object when mine:
                    MergeObject(key, local!, server);
                    break;
                case MergeKind.Value when mine && local!.Updated > server.Updated:
                    Rebase(local!, server);
                    break;
                default:
                    state.Items[key] = server;
                    state.Dirty.Remove(key);
                    break;
            }
        }

        state.Cursor = Num(body, "cursor", state.Cursor);
        return body["more"]?.GetValue<bool>() == true;
    }

    /// <summary>The PUT body for a batch (CLIENT.md 7.4): one entry per item, the app is launcher.</summary>
    internal static JsonObject PutBody(IEnumerable<SyncItem> batch)
    {
        var items = new JsonArray();
        foreach (var item in batch)
        {
            var entry = new JsonObject { ["app"] = AccountSyncRules.App, ["type"] = item.Type, ["id"] = item.Id, ["base"] = item.Base, ["updated"] = item.Updated };
            if (item.Deleted) entry["deleted"] = true;
            else entry["value"] = item.Value;
            items.Add(entry);
        }
        return new JsonObject { ["items"] = items };
    }

    private void ResolveConflict(string key, SyncEntry local, SyncEntry server, DateTimeOffset now)
    {
        switch (AccountSyncRules.KindOf(local.Type))
        {
            case MergeKind.Log:
                // The entry is already on the server: drop the queued copy and keep the server's.
                state.Dirty.Remove(key);
                state.Items[key] = server;
                Applied.Add(key);
                break;
            case MergeKind.Seed:
                state.Dirty.Remove(key);
                state.Items[key] = server;
                Applied.Add(key);
                break;
            case MergeKind.Object:
                // Start from the server's object, overlay what this PC changed, and send it again on the new base.
                // Three conflicts in a row for one key: keep the queue and retry later, not in a tight loop (CLIENT.md 7.4).
                var stillDirty = MergeObject(key, local, server);
                Applied.Add(key);
                if (stillDirty)
                {
                    state.ObjectTries.TryGetValue(key, out var tries);
                    if (++tries >= 3)
                    {
                        state.ObjectTries.Remove(key);
                        state.NextAttempt = now.Add(ObjectRetryWait);
                    }
                    else state.ObjectTries[key] = tries;
                }
                break;
            default:
                // The newer local change wins and is sent again; otherwise the server's value stands.
                if (local.Updated > server.Updated) Rebase(local, server);
                else
                {
                    state.Items[key] = server;
                    state.Dirty.Remove(key);
                    Applied.Add(key);
                }
                break;
        }
    }

    private bool MergeObject(string key, SyncEntry local, SyncEntry server)
    {
        var (kept, keptAt) = ResolveObjectKeys(local, server);
        if (kept.Count == 0)
        {
            state.Items[key] = server;
            state.Dirty.Remove(key);
            state.ObjectTries.Remove(key);
            return false;
        }
        state.Items[key] = new SyncEntry
        {
            Type = local.Type,
            Id = local.Id,
            Base = server.Base,
            BaseValue = server.Value,
            Updated = keptAt.Values.Max(),
            Value = AccountSyncRules.Overlay(server.Value, local.Value, kept),
            Changed = kept,
            ChangedAt = keptAt,
        };
        state.Dirty.Add(key);
        return true;
    }

    private static (List<string> Kept, Dictionary<string, long> KeptAt) ResolveObjectKeys(SyncEntry local, SyncEntry server)
    {
        var serverObj = server.Value is null ? new JsonObject() : JsonNode.Parse(server.Value)?.AsObject() ?? new JsonObject();
        var localObj = local.Value is null ? new JsonObject() : JsonNode.Parse(local.Value)?.AsObject() ?? new JsonObject();
        var baseObj = local.BaseValue is null ? null : JsonNode.Parse(local.BaseValue)?.AsObject() ?? new JsonObject();
        var serverChangedAny = baseObj is not null && AccountSyncRules.ChangedKeys(local.BaseValue, server.Value).Count > 0;

        var kept = new List<string>();
        var keptAt = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var k in local.Changed.Distinct(StringComparer.Ordinal))
        {
            if (JsonNode.DeepEquals(serverObj[k], localObj[k])) continue;
            var keyUpdated = local.ChangedAt.TryGetValue(k, out var ts) ? ts : local.Updated;
            var serverTouchedKey = baseObj is null
                || !JsonNode.DeepEquals(baseObj[k], serverObj[k])
                || (server.Base > local.Base && !serverChangedAny);
            if (!serverTouchedKey || keyUpdated > server.Updated)
            {
                kept.Add(k);
                keptAt[k] = keyUpdated;
            }
        }
        return (kept, keptAt);
    }

    /// <summary>Keeps the local change queued on the server's new version.</summary>
    private SyncEntry Rebase(SyncEntry local, SyncEntry server)
    {
        local.Base = server.Base;
        state.Dirty.Add(AccountSyncRules.Key(local.Type, local.Id));
        return local;
    }

    /// <summary>Held reasons are codes; Settings > Account turns them into plain sentences.</summary>
    internal const string HeldTooLarge = "too-large";
    internal const string HeldRefused = "refused";

    /// <summary>Refused items go back in the queue to be sent again (hourly, and on sign-in): the service may take them now. Too-large ones wait for a new value.</summary>
    public void ReleaseHeld()
    {
        foreach (var key in state.Held.Where(key => state.HeldReasons.GetValueOrDefault(key) == HeldRefused).ToList())
        {
            state.Held.Remove(key);
            state.HeldReasons.Remove(key);
            state.Dirty.Add(key);
        }
    }

    private void Hold(string key, string reason)
    {
        state.HeldReasons[key] = reason;
        state.Dirty.Remove(key);
        state.Held.Add(key);
    }

    private void Fail(DateTimeOffset now, int? seconds)
    {
        state.Failures++;
        state.NextAttempt = now + (seconds is int s ? TimeSpan.FromSeconds(s) : AccountSyncRules.Backoff(state.Failures, random));
    }

    private static IEnumerable<JsonObject> Array(JsonObject? body, string name) =>
        body?[name] is JsonArray array ? array.OfType<JsonObject>() : [];

    private static string Str(JsonNode? node, string name) => node?[name]?.GetValue<string>() ?? "";

    private static long Num(JsonNode? node, string name, long fallback = 0) =>
        node?[name] is JsonValue value && value.TryGetValue(out long number) ? number : fallback;
}
