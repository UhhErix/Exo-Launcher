using System.Text.Json;
using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// What a sync envelope (kind 0x01) carries (plan §2.7): one app's items as canonical JSON,
/// <c>{"app":"launcher","items":[…],"v":1}</c>. An item is <c>{"body","device","id","modified","type"}</c>, or a delete
/// (a tombstone) <c>{"deleted":true,"device","id","modified","type"}</c> with no body:
/// <list type="bullet">
/// <item><c>type</c> 1 to 32 of a-z, 0-9 and '-'; <c>id</c> 1 to 256 printable ASCII characters (U+0021 to U+007E);
/// the pair names the item, at most once per batch;</item>
/// <item><c>modified</c> Unix milliseconds; a writer that saves an item again within the same millisecond raises it past
/// the last one, so one device never writes two versions with the same time;</item>
/// <item><c>device</c> the id of the device that made this version (in a snapshot, the original writer's), so "the newest
/// modified wins, a tie to the higher device id" merges the same on every device;</item>
/// <item><c>body</c> any value of the canonical subset.</item>
/// </list>
/// A snapshot is a batch like any other, holding every live item and every tombstone under 90 days old. A reader takes
/// a batch whole or not at all: its bytes must be exactly their canonical form, its app the one whose sub-key opened
/// it, and every item well-formed. Unknown fields are refused, in the batch and in an item (plan
/// §2.15): a new field comes with a new <c>v</c>, and a reader refuses a <c>v</c> it doesn't know, so two apps never
/// read one batch two ways.
/// </summary>
public static class SyncBatch
{
    public const int MaxType = 32, MaxId = 256;

    /// <param name="Body">The item's value; null for a delete.</param>
    public sealed record Item(string Type, string Id, long Modified, string Device, JsonNode? Body);

    /// <summary>The canonical bytes; throws <see cref="ArgumentException"/> for anything outside the format.</summary>
    public static byte[] Write(string app, IEnumerable<Item> items)
    {
        var list = new JsonArray();
        foreach (var item in items) list.Add((JsonNode)Json(item));
        byte[] bytes;
        try { bytes = CanonicalJson.Serialize(new JsonObject { ["v"] = 1, ["app"] = app, ["items"] = list }); }
        catch (FormatException e) { throw new ArgumentException(e.Message, nameof(items), e); }
        return Read(bytes, app) is not null ? bytes : throw new ArgumentException("A batch is one app's items, each well-formed and named once.", nameof(items));
    }

    /// <summary>
    /// The items as batches that each fit one envelope (plan §2.7): greedily, in order, the batch's frame
    /// (<c>{"app":"…","items":[],"v":1}</c>) plus the items' canonical bytes and a comma between each two at most
    /// <see cref="Envelope.MaxData"/> (65,381) bytes. Each batch is whole and canonical on its own, so a reader never
    /// reassembles anything. An item that can't go (too big for an envelope alone, or outside the format) is left out
    /// alone and answered in <c>Skipped</c>: it stays on this PC unsent, and the caller logs it; the rest go.
    /// </summary>
    public static (IReadOnlyList<(byte[] Data, IReadOnlyList<Item> Items)> Batches, IReadOnlyList<Item> Skipped) Split(string app, IReadOnlyList<Item> items)
    {
        var frame = CanonicalJson.Serialize(new JsonObject { ["v"] = 1, ["app"] = app, ["items"] = new JsonArray() }).Length;
        var batches = new List<(byte[], IReadOnlyList<Item>)>();
        var skipped = new List<Item>();
        var current = new List<Item>();
        var size = frame;
        foreach (var item in items)
        {
            int length;
            try { length = Read(Write(app, [item]), app) is { Count: 1 } ? CanonicalJson.Serialize(Json(item)).Length : int.MaxValue; }
            catch (ArgumentException) { length = int.MaxValue; }
            if (length > Envelope.MaxData - frame)
            {
                skipped.Add(item);
                continue;
            }
            var adds = length + (current.Count > 0 ? 1 : 0);
            if (size + adds > Envelope.MaxData)
            {
                batches.Add((Write(app, current), current));
                (current, size, adds) = ([], frame, length);
            }
            current.Add(item);
            size += adds;
        }
        if (current.Count > 0) batches.Add((Write(app, current), current));
        return (batches, skipped);
    }

    /// <summary>The items, or null unless the bytes are a well-formed batch of <paramref name="app"/>'s in canonical form.</summary>
    public static IReadOnlyList<Item>? Read(ReadOnlySpan<byte> data, string app)
    {
        // Three fields and these three: anything else is an unknown field.
        if (CanonicalJson.ParseObject(data) is not { Count: 3 } batch || Device.Int(batch, "v") != 1 || Device.Text(batch, "app") != app ||
            batch["items"] is not JsonArray { Count: > 0 } list)
            return null;
        try
        {
            if (!data.SequenceEqual(CanonicalJson.Serialize(batch))) return null;
        }
        catch (FormatException) { return null; }
        var items = new List<Item>(list.Count);
        var named = new HashSet<(string, string)>();
        foreach (var node in list)
        {
            if (node is not JsonObject { Count: 5 } item || Device.Text(item, "type") is not { Length: > 0 and <= MaxType } type ||
                !type.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') ||
                Device.Text(item, "id") is not { Length: > 0 and <= MaxId } id || !id.All(c => c is >= '!' and <= '~') ||
                Device.Int(item, "modified") is not { } modified || modified < 0 ||
                Device.Text(item, "device") is not { Length: 32 } device || !device.All(char.IsAsciiHexDigitLower) ||
                !named.Add((type, id)))
                return null;
            // Five fields: the four above and either body or deleted, so a fifth of any other name is refused.
            JsonNode? body;
            if (item["body"] is { } value) body = value.DeepClone();
            else if (item["deleted"] is JsonValue flag && flag.GetValueKind() == JsonValueKind.True) body = null;
            else return null;
            items.Add(new(type, id, modified, device, body));
        }
        return items;
    }

    /// <summary>
    /// Plan §2.7's merge for one item (type, id): the newer <c>modified</c> wins; a tie goes to the higher <c>device</c>
    /// (ordinal compare of the hex). A delete is an item like any other, so a newer delete beats an older body and a newer
    /// body an older delete. Two equal in both (a device writing the same moment twice, which it never should) go to the
    /// higher canonical bytes, so every app ends the same whatever order it read them in.
    /// </summary>
    public static bool Wins(Item challenger, Item current)
    {
        if (challenger.Modified != current.Modified) return challenger.Modified > current.Modified;
        var device = string.CompareOrdinal(challenger.Device, current.Device);
        if (device != 0) return device > 0;
        return CompareBytes(CanonicalJson.Serialize(Json(challenger)), CanonicalJson.Serialize(Json(current))) > 0;
    }

    /// <summary>
    /// What an app holds after reading <paramref name="incoming"/> on top of <paramref name="current"/> (a snapshot and
    /// batches in any order end the same): one item per (type, id), the winner by <see cref="Wins"/>, ordered by type
    /// and then id (ordinal). Tombstones stay; dropping those past 90 days is a snapshot writer's business.
    /// </summary>
    public static IReadOnlyList<Item> Merge(IEnumerable<Item> current, IEnumerable<Item> incoming)
    {
        var held = new Dictionary<(string, string), Item>();
        foreach (var item in current.Concat(incoming))
        {
            var key = (item.Type, item.Id);
            if (!held.TryGetValue(key, out var was) || Wins(item, was)) held[key] = item;
        }
        return [.. held.Values.OrderBy(item => item.Type, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal)];
    }

    /// <summary>An item as a batch carries it: its four fields, then its body or <c>deleted: true</c>.</summary>
    public static JsonObject Json(Item item)
    {
        var json = new JsonObject { ["type"] = item.Type, ["id"] = item.Id, ["modified"] = item.Modified, ["device"] = item.Device };
        if (item.Body is null) json["deleted"] = true;
        else json["body"] = item.Body.DeepClone();
        return json;
    }

    private static int CompareBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);
}
