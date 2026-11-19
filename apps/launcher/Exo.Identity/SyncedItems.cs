using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// One sync sub-key's items as this PC holds them (plan §2.7): an app's own (<c>launcher</c>, …)
/// or the profile's (<see cref="ProfileItems"/>). They're <see cref="SyncBatch"/>'s
/// own items, merged by its rule (the newer modified wins, a tie to the higher device), so what this PC holds and what
/// another sends meet the same way in any order. A change made here gets a time raised past the last one, so this PC
/// never writes two versions at one moment, and this PC's device id; before the identity has one it's left blank and
/// stamped as the change first goes out (<see cref="Outgoing"/>). What changed here since it last went out is kept, so a
/// push sends only that; a change taken from another PC is never sent back.
/// Safe to call from several threads.
///
/// The wiring every app follows: on connecting to the sync box, push <see cref="Outgoing"/> and then call
/// <see cref="Sent"/> with what the mailbox stored; give <see cref="Take"/> every batch that arrives and show the
/// winners it answers; on a new box (a new sync key) or every few days, push <see cref="Snapshot"/>.
/// </summary>
public sealed class SyncedItems(string app)
{
    /// <summary>Tombstones older than this leave a snapshot (§2.7).</summary>
    public static readonly long TombstoneMs = (long)TimeSpan.FromDays(90).TotalMilliseconds;

    private readonly object _gate = new();
    private readonly Dictionary<(string Type, string Id), SyncBatch.Item> _items = [];
    private readonly HashSet<(string Type, string Id)> _out = [];
    private long _last;

    public string App { get; } = app;

    /// <summary>A change, made here or taken from another PC.</summary>
    public event Action? Changed;

    public IReadOnlyList<SyncBatch.Item> Items
    {
        get { lock (_gate) return [.. _items.Values]; }
    }

    public IReadOnlyList<SyncBatch.Item> Live(string type)
    {
        lock (_gate) return [.. _items.Values.Where(i => i.Type == type && i.Body is not null)];
    }

    public SyncBatch.Item? Get(string type, string id)
    {
        lock (_gate) return _items.GetValueOrDefault((type, id));
    }

    /// <summary>Whether anything changed here that hasn't gone out yet.</summary>
    public bool Pending
    {
        get { lock (_gate) return _out.Count > 0; }
    }

    /// <summary>What changed here and hasn't gone out yet, as held.</summary>
    public IReadOnlyList<SyncBatch.Item> Unsent
    {
        get { lock (_gate) return [.. _out.Select(key => _items[key])]; }
    }

    /// <summary>
    /// An item this PC held before (kept across a sign-out, or from an earlier copy): kept where it wins by the sync
    /// rule, with its own time and writer, and sent again, since the box may not have it. The same version again, or an
    /// older one, changes nothing.
    /// </summary>
    public bool Merge(SyncBatch.Item item)
    {
        lock (_gate)
        {
            var key = (item.Type, item.Id);
            if (_items.TryGetValue(key, out var held) && !SyncBatch.Wins(item, held)) return false;
            _items[key] = item with { Body = item.Body?.DeepClone() };
            _out.Add(key);
            _last = Math.Max(_last, item.Modified);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>A change made on this PC; a null body deletes (a tombstone, so the item doesn't come back). The same
    /// value again is no change: no new version, nothing to send, unless <paramref name="again"/> (the held version is
    /// older than something it must now win over, as a look field under a newer whole look).</summary>
    public SyncBatch.Item Put(string type, string id, JsonNode? body, string device = "", bool again = false)
    {
        SyncBatch.Item item;
        lock (_gate)
        {
            if (!again && _items.TryGetValue((type, id), out var held) && JsonNode.DeepEquals(held.Body, body)) return held;
            item = new SyncBatch.Item(type, id, Next(), device, body?.DeepClone());
            _items[(type, id)] = item;
            _out.Add((type, id));
        }
        Changed?.Invoke();
        return item;
    }

    /// <summary>
    /// A value this PC already had before it synced (a setting chosen earlier), unless the item is here: stamped at the
    /// start of time, so any choice another PC made on purpose wins over it. Seed after the first catch-up, so a PC that
    /// links in takes the identity's values first and seeds only what the identity doesn't have.
    /// </summary>
    public bool Seed(string type, string id, JsonNode body)
    {
        lock (_gate)
        {
            if (_items.ContainsKey((type, id))) return false;
            _items[(type, id)] = new SyncBatch.Item(type, id, 0, "", body.DeepClone());
            _out.Add((type, id));
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Items from another PC (a batch or a snapshot): each kept where it wins; the ones kept, for the page.</summary>
    public IReadOnlyList<SyncBatch.Item> Take(IEnumerable<SyncBatch.Item> incoming)
    {
        var taken = new List<SyncBatch.Item>();
        lock (_gate)
        {
            foreach (var item in incoming)
            {
                var key = (item.Type, item.Id);
                if (_items.TryGetValue(key, out var held) && !SyncBatch.Wins(item, held)) continue;
                _items[key] = item with { Body = item.Body?.DeepClone() };
                // Another PC's version is already out there; this PC's own change it replaced needn't go.
                _out.Remove(key);
                _last = Math.Max(_last, item.Modified);
                taken.Add(item);
            }
        }
        if (taken.Count > 0) Changed?.Invoke();
        return taken;
    }

    /// <summary>What goes out next: each item changed here since it last went out, stamped with this PC's
    /// <paramref name="device"/> where it has none yet (kept so, so the same version always carries the same writer).</summary>
    public IReadOnlyList<SyncBatch.Item> Outgoing(string device)
    {
        lock (_gate)
        {
            Stamp(device);
            return [.. _out.Select(key => _items[key]).OrderBy(i => i.Type, StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal)];
        }
    }

    /// <summary>Those items went out: unless one changed again meanwhile, it's done.</summary>
    public void Sent(IEnumerable<SyncBatch.Item> sent)
    {
        lock (_gate)
        {
            foreach (var item in sent)
                if (_items.TryGetValue((item.Type, item.Id), out var held) && held.Modified == item.Modified && held.Device == item.Device)
                    _out.Remove((item.Type, item.Id));
        }
    }

    /// <summary>A snapshot (§2.7): every live item, and every tombstone under 90 days old at <paramref name="nowMs"/>.
    /// Older tombstones are dropped here too.</summary>
    public IReadOnlyList<SyncBatch.Item> Snapshot(string device, long nowMs)
    {
        lock (_gate)
        {
            Stamp(device);
            foreach (var key in _items.Where(kv => kv.Value.Body is null && nowMs - kv.Value.Modified >= TombstoneMs).Select(kv => kv.Key).ToList())
            {
                _items.Remove(key);
                _out.Remove(key);
            }
            return [.. _items.Values.OrderBy(i => i.Type, StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal)];
        }
    }

    // Changes made before this PC had a device id take it now.
    private void Stamp(string device)
    {
        foreach (var key in _items.Where(kv => kv.Value.Device.Length == 0).Select(kv => kv.Key).ToList())
            _items[key] = _items[key] with { Device = device };
    }

    /// <summary>Every item, tombstones included, and whether it still has to go out: for the file this PC keeps.</summary>
    public JsonArray ToJson()
    {
        lock (_gate)
        {
            return new([.. _items.Values.OrderBy(i => i.Modified).Select(i =>
            {
                var json = new JsonObject { ["type"] = i.Type, ["id"] = i.Id, ["modified"] = i.Modified };
                if (i.Device.Length > 0) json["device"] = i.Device;
                if (i.Body is null) json["deleted"] = true;
                else json["body"] = i.Body.DeepClone();
                if (_out.Contains((i.Type, i.Id))) json["out"] = true;
                return (JsonNode)json;
            })]);
        }
    }

    public static SyncedItems FromJson(string app, JsonArray? items)
    {
        var held = new SyncedItems(app);
        foreach (var n in (items ?? []).OfType<JsonObject>())
        {
            if (Device.Text(n, "type") is not { Length: > 0 } type || Device.Text(n, "id") is not { Length: > 0 } id || Device.Int(n, "modified") is not { } modified) continue;
            var deleted = n["deleted"] is JsonValue flag && flag.GetValueKind() == System.Text.Json.JsonValueKind.True;
            var body = deleted ? null : n["body"]?.DeepClone();
            if (body is null && !deleted) continue;
            held._items[(type, id)] = new SyncBatch.Item(type, id, modified, Device.Text(n, "device") ?? "", body);
            // Kept before sync existed (no "out" field at all): it still has to go out.
            var outFlag = n["out"] is JsonValue o && o.GetValueKind() == System.Text.Json.JsonValueKind.True;
            if (outFlag || n["out"] is null && n["device"] is null) held._out.Add((type, id));
            held._last = Math.Max(held._last, modified);
        }
        return held;
    }

    // Unix milliseconds, raised past the last one this PC wrote or took (§2.7).
    private long Next() => _last = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _last + 1);
}
