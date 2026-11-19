using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// The profile's look and layout as one item per field (/18, account plan item 3), so two apps editing at once never
/// undo each other's changes: one changing the accent while another moves a block both keep theirs.
/// <list type="bullet">
/// <item><c>("look-field", "&lt;field&gt;")</c>: one field of the look (<see cref="ProfileItems.Look"/>), its value as the
/// whole look would hold it; a tombstone removes the field.</item>
/// <item><c>("canvas-block", "&lt;block id&gt;")</c>: one block of the canvas (<see cref="ProfileItems.Canvas"/>) with
/// <c>at</c>, its place in the list (0 to 47; equal places go by id); a tombstone removes the block.</item>
/// <item><c>("canvas-meta", "grid" | "hero")</c>: the canvas's own fields; a tombstone puts back the default.</item>
/// </list>
/// The whole items <c>("profile", "look")</c> and <c>("profile", "canvas")</c> that came before stay the base: a field item
/// counts only when it is newer than the whole item, so a profile saved before /18 reads as it was, and an app from
/// before /18 that saves the whole look still changes what every app shows. Apps from /18 on write field items only.
/// </summary>
public static class ProfileFields
{
    public const string LookFieldType = "look-field", CanvasBlockType = "canvas-block", CanvasMetaType = "canvas-meta";
    public const string GridId = "grid", HeroId = "hero";
    public const int MaxFieldBytes = 4096;

    /// <summary>Whether an item is one of these types.</summary>
    public static bool Is(string type) => type is LookFieldType or CanvasBlockType or CanvasMetaType;

    /// <summary>
    /// The look as the profile's items make it: the whole look, with every look field newer than it laid over it. Null
    /// when there's none (no look yet, or one taken away).
    /// </summary>
    public static JsonObject? Look(IEnumerable<SyncBatch.Item> items)
    {
        var all = items as IReadOnlyCollection<SyncBatch.Item> ?? [.. items];
        var whole = all.FirstOrDefault(i => i.Type == ProfileItems.ProfileType && i.Id == ProfileItems.LookId);
        var since = whole?.Modified ?? long.MinValue;
        var look = whole is not null && ProfileItems.Look(whole) is { } w ? (JsonObject)w.DeepClone() : null;
        foreach (var field in all.Where(i => i.Type == LookFieldType && i.Modified > since && Valid(i)).OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            look ??= new JsonObject { ["v"] = 1 };
            if (field.Body is null) look.Remove(field.Id);
            else look[field.Id] = field.Body.DeepClone();
        }
        // Every field taken away is no look, as a look never made is.
        return look is null || look.Count == 1 ? null : look;
    }

    /// <summary>
    /// The canvas as the profile's items make it: the whole canvas, with every block and canvas field newer than it laid
    /// over it, its blocks in their places (at most 48; a block that doesn't fit the grid is left out). Null when there's
    /// none.
    /// </summary>
    public static JsonObject? Canvas(IEnumerable<SyncBatch.Item> items)
    {
        var all = items as IReadOnlyCollection<SyncBatch.Item> ?? [.. items];
        var whole = all.FirstOrDefault(i => i.Type == ProfileItems.ProfileType && i.Id == ProfileItems.CanvasId);
        var since = whole?.Modified ?? long.MinValue;
        var baseCanvas = whole is not null ? ProfileItems.Canvas(whole) : null;
        var meta = new JsonObject();
        foreach (var key in new[] { GridId, HeroId })
            if (baseCanvas?[key] is { } value) meta[key] = value.DeepClone();
        var blocks = new Dictionary<string, (long At, JsonObject Block)>(StringComparer.Ordinal);
        if (baseCanvas?["blocks"] is JsonArray list)
            for (var i = 0; i < list.Count; i++)
                if (list[i] is JsonObject block && Device.Text(block, "id") is { } id) blocks[id] = (i, (JsonObject)block.DeepClone());
        foreach (var item in all.Where(i => (i.Type is CanvasMetaType or CanvasBlockType) && i.Modified > since && Valid(i)))
        {
            if (item.Type == CanvasMetaType)
            {
                if (item.Body is null) meta.Remove(item.Id);
                else meta[item.Id] = item.Body.DeepClone();
            }
            else if (item.Body is not JsonObject block) blocks.Remove(item.Id);
            else
            {
                var copy = (JsonObject)block.DeepClone();
                var at = Device.Int(copy, "at") ?? 0;
                copy.Remove("at");
                blocks[item.Id] = (at, copy);
            }
        }
        // Every block and field taken away is no canvas (the showcases as a list), as one never made is.
        if (blocks.Count == 0 && meta.Count == 0) return null;
        var canvas = new JsonObject { ["v"] = 1 };
        foreach (var (key, value) in meta) canvas[key] = value?.DeepClone();
        var columns = (int)(Device.Int(canvas, "grid") ?? ProfileItems.CanvasColumns);
        var kept = new JsonArray();
        foreach (var (_, block) in blocks.OrderBy(b => b.Value.At).ThenBy(b => b.Key, StringComparer.Ordinal).Select(b => b.Value))
        {
            if (kept.Count >= ProfileItems.MaxBlocks) break;
            if (ProfileItems.BlockFits(block, columns)) kept.Add((JsonNode)block);
        }
        canvas["blocks"] = kept;
        return canvas;
    }

    /// <summary>The field items that make <paramref name="look"/> (null: no look) out of what <paramref name="items"/>
    /// make now: only what differs. Throws <see cref="ArgumentException"/> for a look <see cref="ProfileItems.Look"/>
    /// wouldn't take.</summary>
    public static IReadOnlyList<(string Type, string Id, JsonNode? Body)> LookPuts(IEnumerable<SyncBatch.Item> items, JsonObject? look)
    {
        if (look is not null) ProfileItems.LookBody(look);
        var now = Look(items) ?? new JsonObject();
        var next = look ?? new JsonObject();
        var puts = new List<(string, string, JsonNode?)>();
        foreach (var (key, value) in next)
            if (key != "v" && !JsonNode.DeepEquals(now[key], value)) puts.Add((LookFieldType, key, value?.DeepClone()));
        foreach (var (key, _) in now)
            if (key != "v" && !next.ContainsKey(key)) puts.Add((LookFieldType, key, null));
        return puts;
    }

    /// <summary>The block and canvas items that make <paramref name="canvas"/> (null: none) out of what
    /// <paramref name="items"/> make now: only what differs. Throws <see cref="ArgumentException"/> for a canvas
    /// <see cref="ProfileItems.Canvas"/> wouldn't take.</summary>
    public static IReadOnlyList<(string Type, string Id, JsonNode? Body)> CanvasPuts(IEnumerable<SyncBatch.Item> items, JsonObject? canvas)
    {
        if (canvas is not null) ProfileItems.CanvasBody(canvas);
        var now = Canvas(items);
        var puts = new List<(string, string, JsonNode?)>();
        foreach (var key in new[] { GridId, HeroId })
            if (!JsonNode.DeepEquals(now?[key], canvas?[key])) puts.Add((CanvasMetaType, key, canvas?[key]?.DeepClone()));
        var held = Placed(now);
        var wanted = Placed(canvas);
        foreach (var (id, block) in wanted)
            if (!held.TryGetValue(id, out var was) || !JsonNode.DeepEquals(was, block)) puts.Add((CanvasBlockType, id, block));
        foreach (var id in held.Keys)
            if (!wanted.ContainsKey(id)) puts.Add((CanvasBlockType, id, null));
        return puts;
    }

    // Each block with its place, as its item carries it.
    private static Dictionary<string, JsonObject> Placed(JsonObject? canvas)
    {
        var placed = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (canvas?["blocks"] is not JsonArray blocks) return placed;
        for (var i = 0; i < blocks.Count; i++)
            if (blocks[i] is JsonObject block && Device.Text(block, "id") is { } id)
            {
                var copy = (JsonObject)block.DeepClone();
                copy["at"] = i;
                placed[id] = copy;
            }
        return placed;
    }

    /// <summary>Whether one of these items is well-formed (a tombstone is): a look field as the look would take it, a
    /// block as a 96-column canvas would (its own grid is checked when the canvas is made), a grid or a hero.</summary>
    public static bool Valid(SyncBatch.Item item)
    {
        if (item.Body is null) return item.Type switch
        {
            LookFieldType => item.Id.Length is >= 1 and <= 32 && item.Id != "v",
            CanvasBlockType => BlockId(item.Id),
            CanvasMetaType => item.Id is GridId or HeroId,
            _ => true,
        };
        switch (item.Type)
        {
            case LookFieldType:
                if (item.Id.Length is < 1 or > 32 || item.Id == "v" || CanonicalJson.Serialize(item.Body).Length > MaxFieldBytes) return false;
                return ProfileItems.Look(new SyncBatch.Item(ProfileItems.ProfileType, ProfileItems.LookId, 0, "", new JsonObject { ["v"] = 1, [item.Id] = item.Body.DeepClone() })) is not null;
            case CanvasBlockType:
                if (!BlockId(item.Id) || item.Body is not JsonObject block || Device.Text(block, "id") != item.Id ||
                    Device.Int(block, "at") is not (>= 0 and < ProfileItems.MaxBlocks) || CanonicalJson.Serialize(block).Length > ProfileItems.MaxCanvasBytes)
                    return false;
                var copy = (JsonObject)block.DeepClone();
                copy.Remove("at");
                return ProfileItems.BlockFits(copy, ProfileItems.Grids.Max());
            case CanvasMetaType:
                var probe = new JsonObject { ["v"] = 1, [item.Id] = item.Body.DeepClone(), ["blocks"] = new JsonArray() };
                return item.Id is GridId or HeroId && ProfileItems.Canvas(new SyncBatch.Item(ProfileItems.ProfileType, ProfileItems.CanvasId, 0, "", probe)) is not null;
            default:
                return true;
        }
    }

    private static bool BlockId(string id) => id.Length is >= 1 and <= 12 && id.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterLower(c));
}
