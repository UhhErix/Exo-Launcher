using System.Buffers.Binary;
using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// The items under the <c>profile</c> sub-key (<c>exo sync v1/profile</c>, plan §2.17), which every Exo app reads and
/// writes: the person's own profile, and what the identity itself needs every PC to hold. Each is an ordinary
/// <see cref="SyncBatch"/> item, merged per (type, id) by <see cref="SyncBatch.Wins"/>:
/// <list type="bullet">
/// <item><c>("profile", "name")</c>: the name shown on the chip, a string with a device name's rules
/// (<see cref="Device.NameOk"/>: 1 to 64 visible characters, no space at either end).</item>
/// <item><c>("profile", "picture")</c>: <c>{"data":"&lt;base64url&gt;","format":"webp","size":256}</c>, a still WebP
/// whose canvas is 256 by 256, at most 48,000 bytes (so its base64url and the batch around it fit one envelope). A
/// tombstone takes the picture away.</item>
/// <item><c>("profile", "banner")</c>: <c>{"data":"&lt;base64url&gt;","format":"webp","height":320,"width":960}</c>, the
/// wide picture across the profile's top: a still WebP whose canvas is 960 by 320, at most 48,000 bytes, as the picture.
/// A tombstone takes it away.</item>
/// <item><c>("profile", "look")</c> and <c>("profile", "canvas")</c>: how the profile looks and its layout
/// (<see cref="Look"/>, <see cref="Canvas"/>).</item>
/// <item><c>("profile", "stats")</c> and <c>("profile", "watching")</c> (/17): what the showcases draw, from the person's
/// library and watching, so every app and a friend's draws the same page (<see cref="Stats"/>,
/// <see cref="Watching"/>).</item>
/// <item><c>("device-change", "&lt;64 hex&gt;")</c>: one signed change of the device list (§2.3), exactly as it
/// travels, its id the change's hash (<see cref="CanonicalJson.Hash"/>). Never deleted, so their union is every change
/// any PC made; a reader takes them through <see cref="DeviceList.Take"/> in seq order.</item>
/// <item><c>("recovery-box", "current")</c>: <c>{"code":n,"key":"&lt;base64url 32 bytes&gt;"}</c>, the recovery box's key
/// that every linked PC keeps (never the code, §2.12) and the code's number; a tombstone when recovery is off.</item>
/// <item><c>("account-box", "current")</c>: the same for the Exo account's entry (§2.17): its box's key and the entry's
/// number; a tombstone while account sign-in is off.</item>
/// </list>
/// A reader keeps items it doesn't know (so a snapshot carries them on) and uses only these, each checked by
/// <see cref="Valid"/>; one that isn't valid is ignored.
/// </summary>
public static class ProfileItems
{
    public const string App = "profile";
    public const string ProfileType = "profile", NameId = "name", PictureId = "picture", BannerId = "banner";
    public const string DeviceChangeType = "device-change";
    public const string RecoveryBoxType = "recovery-box", AccountBoxType = "account-box", RecoveryBoxId = "current";
    public const int PictureSize = 256, MaxPictureBytes = 48_000, BannerWidth = 960, BannerHeight = 320;

    /// <summary>The name's body; throws <see cref="ArgumentException"/> for a name a device couldn't have.</summary>
    public static JsonNode NameBody(string name) =>
        Device.NameOk(name) ? JsonValue.Create(name) : throw new ArgumentException("A name is 1 to 64 visible characters, with no space at either end.", nameof(name));

    public static string? Name(SyncBatch.Item item) =>
        item is { Type: ProfileType, Id: NameId, Body: JsonValue value } && value.GetValueKind() == JsonValueKind.String &&
        value.GetValue<string>() is var name && Device.NameOk(name) ? name : null;

    public const string LookId = "look";
    public const int MaxLookBytes = 4096;
    public const long MaxMediaBytes = 200L * 1024 * 1024;
    public static readonly IReadOnlyList<string> MediaSlots = ["avatar", "banner", "background"];
    public static readonly IReadOnlyList<string> MediaTypes = ["image/png", "image/jpeg", "image/webp", "image/gif", "image/avif", "video/mp4", "video/webm"];
    public static readonly IReadOnlyList<string> Effects = ["none", "embers", "snow", "stars", "rain", "sparkles", "aurora", "matrix", "petals", "fireflies", "lightning"];
    public static readonly IReadOnlyList<string> Frames = ["none", "glow", "orbit", "holo", "fire"];
    public static readonly IReadOnlyList<string> NameStyles = ["plain", "glow", "shimmer", "wave", "gradient", "neon", "glitch", "typewriter", "chrome"];
    public static readonly IReadOnlyList<string> Fonts = ["exo", "orbitron", "bebas", "pixel", "marker", "script", "tube"];
    /// <summary>The showcases a look or canvas can show; since /17 <c>watching</c>, drawn from <see cref="Watching"/>.</summary>
    public static readonly IReadOnlyList<string> Showcases = ["stats", "most-played", "recent", "setup", "trackers", "shelf", "pc", "gear", "watching"];
    /// <summary>The figures a trackers showcase can show, each worked out from what Exo knows on the PC.</summary>
    public static readonly IReadOnlyList<string> Trackers = ["hours", "games", "played", "top-game", "this-year", "installed", "stores", "pcs"];

    /// <summary>
    /// <c>("profile", "look")</c> (plan §2.17): how the profile looks beyond its still picture and banner, the person's
    /// own, the same in every Exo app. <c>{"v":1}</c> with any of: <c>avatar</c>, <c>banner</c>, <c>background</c>, each a
    /// media file named by its SHA-256 (<c>{"bytes","sha","type"}</c>: an image, a GIF, animated WebP or AVIF, or an MP4 or
    /// WebM loop; the file travels apart); <c>accent</c> (<c>#rrggbb</c>); <c>effect</c>, <c>frame</c> and <c>name</c>,
    /// each one of its list; and <c>showcases</c>, the ones shown in their order, each once. Since /15 also
    /// <c>accent2</c> (a second <c>#rrggbb</c>, for the gradient name), <c>font</c> (the name's, one of <see cref="Fonts"/>)
    /// and, on a media, its view (<see cref="IsMedia"/>). A field this version doesn't know is kept and left alone; a
    /// known one off its list makes the look invalid. At most 4,096 bytes.
    /// </summary>
    public static JsonObject? Look(SyncBatch.Item item)
    {
        if (item is not { Type: ProfileType, Id: LookId, Body: JsonObject look } || Device.Int(look, "v") != 1 ||
            CanonicalJson.Serialize(look).Length > MaxLookBytes)
            return null;
        foreach (var slot in MediaSlots)
            if (look[slot] is { } media && !IsMedia(media)) return null;
        foreach (var key in new[] { "accent", "accent2" })
            if (look.ContainsKey(key) && !IsColor(Device.Text(look, key))) return null;
        foreach (var (key, list) in new[] { ("effect", Effects), ("frame", Frames), ("name", NameStyles), ("font", Fonts) })
            if (look.ContainsKey(key) && !(Device.Text(look, key) is { } value && list.Contains(value))) return null;
        if (look.ContainsKey("showcases") && !(look["showcases"] is JsonArray shown && shown.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).ToList() is var names &&
                                               names.All(n => n is not null && Showcases.Contains(n)) && names.Distinct().Count() == names.Count))
            return null;
        return look;
    }

    /// <summary>The look's body; throws <see cref="ArgumentException"/> unless <see cref="Look"/> takes it.</summary>
    public static JsonObject LookBody(JsonObject look) =>
        Look(new SyncBatch.Item(ProfileType, LookId, 0, "", look)) is not null
            ? (JsonObject)look.DeepClone()
            : throw new ArgumentException("That look isn't one Exo can keep.", nameof(look));

    /// <summary>A media file as a look names it: <c>{"bytes","sha","type"}</c>, at most 200 MB of a type Exo plays.</summary>
    public static JsonObject Media(string sha, string type, long bytes) => new() { ["bytes"] = bytes, ["sha"] = sha, ["type"] = type };

    /// <summary>
    /// A media as the look or a canvas names it: <c>bytes</c>, <c>sha</c> (lowercase SHA-256 of the file) and <c>type</c>,
    /// and (since /15) how it's shown, each optional: <c>focus</c> <c>[x, y]</c> (0 to 100, the point kept in view),
    /// <c>zoom</c> (100 to 400 percent) and, for a video, <c>trim</c> <c>[start, end]</c> in milliseconds (the loop played,
    /// 0 &lt;= start &lt; end &lt;= one hour). No other field: the file is the same everywhere, only the view differs.
    /// </summary>
    public static bool IsMedia(JsonNode? node)
    {
        if (node is not JsonObject media || Device.Int(media, "bytes") is not (> 0 and <= MaxMediaBytes) ||
            Device.Text(media, "sha") is not { Length: 64 } sha || !sha.All(char.IsAsciiHexDigitLower) ||
            Device.Text(media, "type") is not { } type || !MediaTypes.Contains(type))
            return false;
        foreach (var (key, _) in media)
            if (key is not ("bytes" or "sha" or "type" or "focus" or "zoom" or "trim")) return false;
        if (media.ContainsKey("focus") && !(Ints(media["focus"], 2) is [var fx, var fy] && fx is >= 0 and <= 100 && fy is >= 0 and <= 100)) return false;
        if (media.ContainsKey("zoom") && Device.Int(media, "zoom") is not (>= 100 and <= 400)) return false;
        if (media.ContainsKey("trim") && !(type.StartsWith("video/", StringComparison.Ordinal) &&
                                           Ints(media["trim"], 2) is [var start, var end] && start >= 0 && start < end && end <= 3_600_000)) return false;
        return true;
    }

    private static bool IsColor(string? text) => text is { Length: 7 } && text[0] == '#' && text[1..].All(char.IsAsciiHexDigitLower);

    /// <summary>A JSON array of exactly <paramref name="count"/> integers, or null.</summary>
    private static long[]? Ints(JsonNode? node, int count)
    {
        if (node is not JsonArray array || array.Count != count) return null;
        var values = new long[count];
        for (var i = 0; i < count; i++)
            if (array[i] is JsonValue v && CanonicalJson.Integer(v) is { } n) values[i] = n;
            else return null;
        return values;
    }

    public const string CanvasId = "canvas";
    public const int MaxCanvasBytes = 16_384, MaxBlocks = 48, CanvasColumns = 24, MaxCanvasRows = 500, MaxBlockRows = 120, MaxTextChars = 2000;
    /// <summary>The canvas's grids (/16): 24 columns, or 96 for four times finer placing, its rows finer by as much. Absent: 24.</summary>
    public static readonly IReadOnlyList<int> Grids = [24, 96];
    public static readonly IReadOnlyList<string> BlockKinds = ["showcase", "media", "text", "banner", "avatar", "name"];
    /// <summary>Where the profile's banner, picture and name are drawn (/16): the strip across the top, or as blocks of the canvas.</summary>
    public static readonly IReadOnlyList<string> HeroPlaces = ["strip", "blocks"];
    public static readonly IReadOnlyList<string> Surfaces = ["glass", "clear", "window", "tint"];
    public static readonly IReadOnlyList<string> Blends = ["normal", "screen", "lighten", "overlay", "multiply", "add"];
    public static readonly IReadOnlyList<string> TextAligns = ["left", "center", "right"];
    public static readonly IReadOnlyList<string> TextSizes = ["s", "m", "l", "xl"];
    /// <summary>Who sees a block (/18, account plan item 8): everyone (absent), friends, or only the profile's owner.</summary>
    public static readonly IReadOnlyList<string> Privacies = ["everyone", "friends", "me"];

    /// <summary>
    /// <c>("profile", "canvas")</c> (/15, plan §2.17): the profile's page as the person laid it out, the same in every Exo
    /// app. <c>{"v":1,"blocks":[...]}</c>, at most 48 blocks and 16,384 bytes. The page is 24 columns wide and its rows
    /// are as tall as a column is wide, so a layout keeps its shape at any width. Each block: <c>id</c> (1 to 12 of
    /// <c>a-z0-9</c>, each once), <c>kind</c>, <c>x</c> 0 to 23, <c>w</c> 1 to 24 (<c>x + w</c> at most 24), <c>y</c> 0 to
    /// 499, <c>h</c> 1 to 120; optional <c>z</c> (-50 to 50, higher on top), <c>rot</c> (-180 to 180 degrees),
    /// <c>opacity</c> (10 to 100), <c>blend</c>, <c>surface</c> (glass, clear, window: what's behind shown brighter, tint:
    /// the accent), <c>radius</c> (0 to 64 px) and <c>frame</c>. A <c>showcase</c> block names one of
    /// <see cref="Showcases"/> as <c>showcase</c>; trackers carry <c>trackers</c> (1 to 3 of <see cref="Trackers"/>, each
    /// once), the shelf <c>shelf</c> (<c>favorite</c> <c>{"game", "note"?}</c>, and <c>rotation</c>, <c>want</c> (each at most
    /// 6 games) and <c>finished</c> (at most 12): game ids as the library names them, 1 to 128 characters, a note at most
    /// 140), and since /16 setup its <c>gear</c> (<see cref="GearOk"/>) and <c>build</c> (<see cref="BuildOk"/>). Since /16 any block
    /// may carry <c>font</c> (one of <see cref="Fonts"/>) and <c>size</c> (one of <see cref="TextSizes"/>: a showcase drawn smaller
    /// or larger), the showcases <c>pc</c> and <c>gear</c> are a setup's two halves (its parts, its gear) on their own, the kinds <c>banner</c>, <c>avatar</c> and <c>name</c> draw the look's banner,
    /// picture and name where they are put, and the canvas's <c>hero</c> (<see cref="HeroPlaces"/>) says whether those three are
    /// blocks or the strip across the top (absent: the strip). Since /16 the canvas's <c>grid</c> may be 96 (<see cref="Grids"/>):
    /// four columns and rows for each of 24's, so a block is placed four times finer; every limit on x, y, w and h scales with it. A <c>media</c> block carries <c>media</c> (<see cref="IsMedia"/>). A <c>text</c> block carries <c>text</c>
    /// (1 to 2,000 characters of Exo's small markup, drawn as text, never as HTML), and optional <c>align</c> and
    /// <c>size</c>. A block of a kind this version doesn't know is kept and not drawn; a field it doesn't know is kept.
    /// </summary>
    public static JsonObject? Canvas(SyncBatch.Item item)
    {
        if (item is not { Type: ProfileType, Id: CanvasId, Body: JsonObject canvas } || Device.Int(canvas, "v") != 1 ||
            canvas["blocks"] is not JsonArray blocks || blocks.Count > MaxBlocks || CanonicalJson.Serialize(canvas).Length > MaxCanvasBytes ||
            canvas.ContainsKey("hero") && !(Device.Text(canvas, "hero") is { } hero && HeroPlaces.Contains(hero)) ||
            // Compared whole: cut to 32 bits first, 2^32 + 24 passed as 24.
            canvas.ContainsKey("grid") && !(Device.Int(canvas, "grid") is { } g && Grids.Any(grid => grid == g)))
            return null;
        var columns = (int)(Device.Int(canvas, "grid") ?? CanvasColumns);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in blocks)
            if (node is not JsonObject block || !BlockOk(block, columns, columns / CanvasColumns) || !ids.Add(Device.Text(block, "id")!)) return null;
        return canvas;
    }

    /// <summary>The canvas's body; throws <see cref="ArgumentException"/> unless <see cref="Canvas"/> takes it.</summary>
    public static JsonObject CanvasBody(JsonObject canvas) =>
        Canvas(new SyncBatch.Item(ProfileType, CanvasId, 0, "", canvas)) is not null
            ? (JsonObject)canvas.DeepClone()
            : throw new ArgumentException("That layout isn't one Exo can keep.", nameof(canvas));

    /// <summary>Whether a block is one a canvas of <paramref name="columns"/> (24 or 96) would take (<see cref="Canvas"/>).</summary>
    public static bool BlockFits(JsonObject block, int columns) => Grids.Contains(columns) && BlockOk(block, columns, columns / CanvasColumns);

    /// <param name="columns">The canvas's grid (<see cref="Grids"/>); <paramref name="fine"/> how many of its rows and columns make one of 24's.</param>
    private static bool BlockOk(JsonObject block, int columns, int fine)
    {
        if (Device.Text(block, "id") is not { Length: >= 1 and <= 12 } id || !id.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterLower(c)) ||
            Device.Text(block, "kind") is not { Length: > 0 } kind ||
            Device.Int(block, "x") is not { } x || x < 0 || x >= columns || Device.Int(block, "w") is not { } w || w < 1 || x + w > columns ||
            Device.Int(block, "y") is not { } y || y < 0 || y >= MaxCanvasRows * fine || Device.Int(block, "h") is not { } h || h < 1 || h > MaxBlockRows * fine)
            return false;
        bool InRange(string key, long low, long high) => !block.ContainsKey(key) || Device.Int(block, key) is { } n && n >= low && n <= high;
        bool OneOf(string key, IReadOnlyList<string> list) => !block.ContainsKey(key) || Device.Text(block, key) is { } t && list.Contains(t);
        if (!InRange("z", -50, 50) || !InRange("rot", -180, 180) || !InRange("opacity", 10, 100) || !InRange("radius", 0, 64) ||
            !OneOf("blend", Blends) || !OneOf("surface", Surfaces) || !OneOf("frame", Frames) || !OneOf("font", Fonts) || !OneOf("size", TextSizes) || !OneOf("privacy", Privacies))
            return false;
        return kind switch
        {
            "showcase" => Device.Text(block, "showcase") is { } showcase && Showcases.Contains(showcase) &&
                          (!block.ContainsKey("trackers") || Names(block["trackers"], Trackers) is { Count: >= 1 and <= 3 }) &&
                          (!block.ContainsKey("shelf") || ShelfOk(block["shelf"])) &&
                          (!block.ContainsKey("gear") || GearOk(block["gear"])) &&
                          (!block.ContainsKey("build") || BuildOk(block["build"])),
            "media" => IsMedia(block["media"]),
            "text" => Device.Text(block, "text") is { Length: >= 1 and <= MaxTextChars } && OneOf("align", TextAligns) && OneOf("size", TextSizes),
            "banner" or "avatar" or "name" => true,
            _ => true,
        };
    }

    /// <summary>An array of distinct names from <paramref name="list"/>, or null.</summary>
    private static List<string>? Names(JsonNode? node, IReadOnlyList<string> list)
    {
        if (node is not JsonArray array) return null;
        var names = array.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList();
        return names.Count == array.Count && names.All(list.Contains) && names.Distinct().Count() == names.Count ? names : null;
    }

    /// <summary>A game's or a title's id as a shelf, the stats and watching name it: 1 to 128 characters, no control character.</summary>
    private static bool GameId(string? id) => id is { Length: >= 1 and <= 128 } && !id.Any(char.IsControl);

    private static bool ShelfOk(JsonNode? node)
    {
        static bool Games(JsonNode? list, int max) => list is JsonArray array && array.Count <= max &&
            array.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).ToList() is var ids &&
            ids.All(GameId) && ids.Distinct().Count() == ids.Count;
        if (node is not JsonObject shelf) return false;
        foreach (var (key, value) in shelf)
        {
            var ok = key switch
            {
                "favorite" => value is JsonObject favorite && GameId(Device.Text(favorite, "game")) &&
                              favorite.All(f => f.Key is "game" or "note") &&
                              (!favorite.ContainsKey("note") || Device.Text(favorite, "note") is { Length: <= 140 }),
                "rotation" or "want" => Games(value, 6),
                "finished" => Games(value, 12),
                _ => true,
            };
            if (!ok) return false;
        }
        return true;
    }

    public const int MaxGear = 8;
    public static readonly IReadOnlyList<string> GearKinds = ["mouse", "keyboard", "audio", "controller", "mousepad", "microphone"];
    public static readonly IReadOnlyList<string> GearLooks = ["headphones", "iem", "headset"];
    /// <summary>The PC's parts a setup block's <c>build</c> can name, in the order a part list shows them.</summary>
    public static readonly IReadOnlyList<string> BuildParts = ["cpu", "gpu", "memory", "motherboard", "storage", "case", "psu", "cooler", "fans"];

    /// <summary>
    /// A setup block's <c>build</c> (/16): the PC's parts as the person names them, each <c>part</c> (one of
    /// <see cref="BuildParts"/>, each once) and <c>name</c> (1 to 64 characters), for what Exo can't read from the PC
    /// (its case, power supply, cooler, fans) or a name the person would rather show than the one Exo reads. A field this
    /// version doesn't know is kept.
    /// </summary>
    private static bool BuildOk(JsonNode? node)
    {
        if (node is not JsonArray build || build.Count > BuildParts.Count) return false;
        var parts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in build)
            if (item is not JsonObject entry || Device.Text(entry, "part") is not { } part || !BuildParts.Contains(part) || !parts.Add(part) ||
                Device.Text(entry, "name") is not { Length: >= 1 and <= 64 } name || name.Any(char.IsControl))
                return false;
        return true;
    }

    /// <summary>
    /// A setup block's <c>gear</c> (/16): at most 8 pieces, in the order shown, each <c>kind</c> (one of
    /// <see cref="GearKinds"/>), <c>maker</c> (1 to 40 characters) and <c>model</c> (1 to 64) as Exo's device list names
    /// them, and optional <c>variant</c> (1 to 40: a color or a layout), <c>look</c> (audio only, one of
    /// <see cref="GearLooks"/>) and <c>picture</c> (the maker's picture as a still image media, <see cref="IsMedia"/>). A
    /// field this version doesn't know is kept.
    /// </summary>
    private static bool GearOk(JsonNode? node)
    {
        static bool Text(JsonObject pick, string key, int max) => Device.Text(pick, key) is { Length: >= 1 } t && t.Length <= max && !t.Any(char.IsControl);
        if (node is not JsonArray gear || gear.Count > MaxGear) return false;
        foreach (var item in gear)
        {
            if (item is not JsonObject pick || Device.Text(pick, "kind") is not { } kind || !GearKinds.Contains(kind) ||
                !Text(pick, "maker", 40) || !Text(pick, "model", 64) || pick.ContainsKey("variant") && !Text(pick, "variant", 40) ||
                pick.ContainsKey("look") && !(kind == "audio" && Device.Text(pick, "look") is { } look && GearLooks.Contains(look)) ||
                pick.ContainsKey("picture") && !(IsMedia(pick["picture"]) && Device.Text(pick["picture"]!.AsObject(), "type")!.StartsWith("image/", StringComparison.Ordinal)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// <c>("hidden-game", "&lt;id&gt;")</c> (/18, account plan item 8): a game or title, by the id the stats, shelf and
    /// watching name it (<see cref="GameId"/>), that other people never see on the profile; it still counts in the
    /// owner's own figures. Its body is <c>true</c>; a tombstone shows it again.
    /// </summary>
    public const string HiddenGameType = "hidden-game";

    public static bool HiddenGame(SyncBatch.Item item) =>
        item is { Type: HiddenGameType, Body: JsonValue value } && value.GetValueKind() == JsonValueKind.True && GameId(item.Id);

    public const string StatsId = "stats", WatchingId = "watching";
    public const int MaxStatsGames = 48, MaxStatsList = 8, MaxStatsBytes = 32_768, MaxWatching = 12, MaxWatchingBytes = 8192, MaxTitleChars = 128;
    public const long MaxCoverBytes = 128 * 1024, MaxHeroBytes = 512 * 1024, MaxFigure = 1_000_000_000, MaxDay = 1_000_000;
    /// <summary>The figures the stats carry (/17): the trackers' own, total time played in minutes, and the year this-year counts.</summary>
    public static readonly IReadOnlyList<string> StatsFigures = ["minutes", "games", "played", "this-year", "year", "installed", "stores", "pcs"];
    public static readonly IReadOnlyList<string> WatchKinds = ["movie", "series"];

    /// <summary>
    /// <c>("profile", "stats")</c> (/17): what the profile's showcases draw from the PC Exo Launcher runs on, so every app,
    /// and a friend's, draws the same page. Launcher writes it (its library is there); the others only read it.
    /// <c>{"v":1}</c> with any of: <c>figures</c>, an object of <see cref="StatsFigures"/> (integers 0 to 10^9; a figure
    /// this version doesn't know is kept); <c>games</c>, at most 48 each once by <c>id</c> (a shelf's rule), with
    /// <c>title</c> (1 to 128 characters, no control character), and optional <c>minutes</c> played, <c>day</c> last played
    /// (days since 1970, never the time of day), <c>art</c> (the store's own reference: <c>store</c>, 1 to 32 lowercase
    /// letters and digits, and <c>app</c>, 1 to 64 characters) and <c>cover</c> (a still image media of at most 128 KB);
    /// <c>recent</c> and <c>most</c>, at most 8 ids each, and <c>top</c>, an id, every one of them in <c>games</c>;
    /// <c>hero</c>, the top game's wide art (a still image media of at most 512 KB) for a banner of none's own;
    /// <c>build</c>, the PC's parts as the page shows them (<see cref="BuildOk"/>), only while the canvas shows the PC; and
    /// <c>gear</c> (<see cref="GearOk"/>), the gear a setup with no picks shows. At most 32,768 bytes. When it was made is
    /// the item's own time.
    /// </summary>
    public static JsonObject? Stats(SyncBatch.Item item)
    {
        if (item is not { Type: ProfileType, Id: StatsId, Body: JsonObject stats } || Device.Int(stats, "v") != 1 ||
            CanonicalJson.Serialize(stats).Length > MaxStatsBytes)
            return null;
        if (stats.ContainsKey("figures") && !(stats["figures"] is JsonObject figures &&
                                             figures.All(f => !StatsFigures.Contains(f.Key) || Device.Int(figures, f.Key) is >= 0 and <= MaxFigure)))
            return null;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (stats.ContainsKey("games"))
        {
            if (stats["games"] is not JsonArray games || games.Count > MaxStatsGames) return null;
            foreach (var node in games)
                if (node is not JsonObject game || Device.Text(game, "id") is not { } id || !GameId(id) || !ids.Add(id) || !Line(Device.Text(game, "title"), MaxTitleChars) ||
                    game.ContainsKey("minutes") && Device.Int(game, "minutes") is not (>= 0 and <= MaxFigure) ||
                    game.ContainsKey("day") && Device.Int(game, "day") is not (>= 0 and <= MaxDay) ||
                    game.ContainsKey("art") && !ArtOk(game["art"]) ||
                    game.ContainsKey("cover") && !IsImage(game["cover"], MaxCoverBytes))
                    return null;
        }
        bool Named(string key) => !stats.ContainsKey(key) || stats[key] is JsonArray list && list.Count <= MaxStatsList &&
            list.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).ToList() is var named &&
            named.All(n => n is not null && ids.Contains(n)) && named.Distinct().Count() == named.Count;
        if (!Named("recent") || !Named("most") || stats.ContainsKey("top") && !(Device.Text(stats, "top") is { } top && ids.Contains(top)) ||
            stats.ContainsKey("hero") && !IsImage(stats["hero"], MaxHeroBytes) ||
            stats.ContainsKey("build") && !BuildOk(stats["build"]) || stats.ContainsKey("gear") && !GearOk(stats["gear"]))
            return null;
        return stats;
    }

    /// <summary>The stats' body; throws <see cref="ArgumentException"/> unless <see cref="Stats"/> takes it.</summary>
    public static JsonObject StatsBody(JsonObject stats) =>
        Stats(new SyncBatch.Item(ProfileType, StatsId, 0, "", stats)) is not null
            ? (JsonObject)stats.DeepClone()
            : throw new ArgumentException("Those stats aren't ones Exo can keep.", nameof(stats));

    /// <summary>
    /// <c>("profile", "watching")</c> (/17): what the person watched lately, for a Watching showcase in every app.
    /// Launcher never writes it. <c>{"v":1,"titles":[…]}</c>, at most 12 titles each once by <c>id</c> (a
    /// shelf's rule, e.g. <c>imdb:tt0903747</c>), with <c>title</c> (1 to 128 characters, no control character), <c>kind</c>
    /// (one of <see cref="WatchKinds"/>), and optional <c>day</c> last watched (days since 1970) and <c>poster</c> (a still
    /// image media of at most 128 KB). A field this version doesn't know is kept. At most 8,192 bytes.
    /// </summary>
    public static JsonObject? Watching(SyncBatch.Item item)
    {
        if (item is not { Type: ProfileType, Id: WatchingId, Body: JsonObject watching } || Device.Int(watching, "v") != 1 ||
            CanonicalJson.Serialize(watching).Length > MaxWatchingBytes || watching["titles"] is not JsonArray titles || titles.Count > MaxWatching)
            return null;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in titles)
            if (node is not JsonObject title || Device.Text(title, "id") is not { } id || !GameId(id) || !ids.Add(id) ||
                !Line(Device.Text(title, "title"), MaxTitleChars) || Device.Text(title, "kind") is not { } kind || !WatchKinds.Contains(kind) ||
                title.ContainsKey("day") && Device.Int(title, "day") is not (>= 0 and <= MaxDay) ||
                title.ContainsKey("poster") && !IsImage(title["poster"], MaxCoverBytes))
                return null;
        return watching;
    }

    /// <summary>Watching's body; throws <see cref="ArgumentException"/> unless <see cref="Watching"/> takes it.</summary>
    public static JsonObject WatchingBody(JsonObject watching) =>
        Watching(new SyncBatch.Item(ProfileType, WatchingId, 0, "", watching)) is not null
            ? (JsonObject)watching.DeepClone()
            : throw new ArgumentException("That watching list isn't one Exo can keep.", nameof(watching));

    /// <summary>A line of words: 1 to <paramref name="max"/> characters, no control character.</summary>
    private static bool Line(string? text, int max) => text is { Length: >= 1 } t && t.Length <= max && !t.Any(char.IsControl);

    /// <summary>A still image media (<see cref="IsMedia"/>) of at most <paramref name="max"/> bytes.</summary>
    private static bool IsImage(JsonNode? node, long max) =>
        IsMedia(node) && node is JsonObject media && Device.Text(media, "type")!.StartsWith("image/", StringComparison.Ordinal) &&
        Device.Int(media, "bytes") <= max;

    /// <summary>A store's own reference to a game: <c>store</c> (1 to 32 lowercase letters and digits) and <c>app</c> (1 to 64 characters, no control character).</summary>
    private static bool ArtOk(JsonNode? node) =>
        node is JsonObject art && Device.Text(art, "store") is { Length: >= 1 and <= 32 } store && store.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)) &&
        Line(Device.Text(art, "app"), 64);

    /// <summary>The picture's body; throws <see cref="ArgumentException"/> unless <paramref name="webp"/> is a still 256 px
    /// WebP of at most 48,000 bytes.</summary>
    public static JsonObject PictureBody(ReadOnlySpan<byte> webp) =>
        IsPicture(webp)
            ? new JsonObject { ["data"] = Base64Url.EncodeToString(webp), ["format"] = "webp", ["size"] = PictureSize }
            : throw new ArgumentException("A profile picture is a still 256 by 256 WebP of at most 48,000 bytes.", nameof(webp));

    /// <summary>The picture's WebP bytes, or null unless the item is a well-formed picture.</summary>
    public static byte[]? Picture(SyncBatch.Item item)
    {
        if (item is not { Type: ProfileType, Id: PictureId, Body: JsonObject { Count: 3 } body } || Device.Text(body, "format") != "webp" ||
            Device.Int(body, "size") != PictureSize || Device.Text(body, "data") is not { } data || CanonicalJson.Bytes(data) is not { } webp)
            return null;
        return IsPicture(webp) ? webp : null;
    }

    /// <summary>The banner's body; throws <see cref="ArgumentException"/> unless <paramref name="webp"/> is a still 960 by
    /// 320 WebP of at most 48,000 bytes.</summary>
    public static JsonObject BannerBody(ReadOnlySpan<byte> webp) =>
        IsBanner(webp)
            ? new JsonObject { ["data"] = Base64Url.EncodeToString(webp), ["format"] = "webp", ["width"] = BannerWidth, ["height"] = BannerHeight }
            : throw new ArgumentException("A profile banner is a still 960 by 320 WebP of at most 48,000 bytes.", nameof(webp));

    /// <summary>The banner's WebP bytes, or null unless the item is a well-formed banner.</summary>
    public static byte[]? Banner(SyncBatch.Item item)
    {
        if (item is not { Type: ProfileType, Id: BannerId, Body: JsonObject { Count: 4 } body } || Device.Text(body, "format") != "webp" ||
            Device.Int(body, "width") != BannerWidth || Device.Int(body, "height") != BannerHeight || Device.Text(body, "data") is not { } data ||
            CanonicalJson.Bytes(data) is not { } webp)
            return null;
        return IsBanner(webp) ? webp : null;
    }

    /// <summary>A still WebP whose canvas is 256 by 256, of at most 48,000 bytes (<see cref="IsStillWebp"/>).</summary>
    public static bool IsPicture(ReadOnlySpan<byte> webp) => IsStillWebp(webp, PictureSize, PictureSize);

    /// <summary>A still WebP whose canvas is 960 by 320, of at most 48,000 bytes (<see cref="IsStillWebp"/>).</summary>
    public static bool IsBanner(ReadOnlySpan<byte> webp) => IsStillWebp(webp, BannerWidth, BannerHeight);

    /// <summary>
    /// A still WebP whose canvas is <paramref name="wide"/> by <paramref name="high"/>, of at most 48,000 bytes: "RIFF",
    /// its size (the file less 8 bytes), "WEBP", then a lossy (VP8 ), lossless (VP8L) or extended (VP8X, without the
    /// animation flag) first chunk whose header gives the size. Only the header is read; drawing it is the page's.
    /// </summary>
    private static bool IsStillWebp(ReadOnlySpan<byte> webp, int wide, int high)
    {
        if (webp.Length is < 30 or > MaxPictureBytes || !webp[..4].SequenceEqual("RIFF"u8) || !webp[8..12].SequenceEqual("WEBP"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(webp[4..]) != webp.Length - 8)
            return false;
        var chunk = webp[12..16];
        int width, height;
        if (chunk.SequenceEqual("VP8 "u8))
        {
            // A key frame: its 3-byte tag, the start code 9d 01 2a, then 14-bit width and height.
            if ((webp[20] & 1) != 0 || webp[23] != 0x9d || webp[24] != 0x01 || webp[25] != 0x2a) return false;
            width = BinaryPrimitives.ReadUInt16LittleEndian(webp[26..]) & 0x3fff;
            height = BinaryPrimitives.ReadUInt16LittleEndian(webp[28..]) & 0x3fff;
        }
        else if (chunk.SequenceEqual("VP8L"u8))
        {
            // The signature 0x2f, then width - 1 and height - 1 in 14 bits each.
            if (webp[20] != 0x2f) return false;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(webp[21..]);
            width = (int)(bits & 0x3fff) + 1;
            height = (int)((bits >> 14) & 0x3fff) + 1;
        }
        else if (chunk.SequenceEqual("VP8X"u8))
        {
            // Flags (animation is 0x02), three reserved bytes, then canvas width - 1 and height - 1 in 24 bits each.
            if ((webp[20] & 0x02) != 0) return false;
            width = (webp[24] | webp[25] << 8 | webp[26] << 16) + 1;
            height = (webp[27] | webp[28] << 8 | webp[29] << 16) + 1;
        }
        else return false;
        return width == wide && height == high;
    }

    /// <summary>A device-list change as the item that carries it.</summary>
    public static (string Id, JsonObject Body) DeviceChange(JsonObject change) => (CanonicalJson.Hash(change), (JsonObject)change.DeepClone());

    /// <summary>The change an item carries, or null unless its id is that change's hash. Whether it applies is the
    /// device list's to decide (<see cref="DeviceList.Take"/>).</summary>
    public static JsonObject? Change(SyncBatch.Item item)
    {
        if (item is not { Type: DeviceChangeType, Body: JsonObject change } || item.Id.Length != 64) return null;
        try { return CanonicalJson.Hash(change) == item.Id ? (JsonObject)change.DeepClone() : null; }
        catch (FormatException) { return null; }
    }

    public static JsonObject RecoveryBoxBody(uint code, ReadOnlySpan<byte> key) =>
        code > 0 && P256.InRange(key)
            ? new JsonObject { ["code"] = (long)code, ["key"] = Base64Url.EncodeToString(key) }
            : throw new ArgumentException("A recovery box key is a P-256 scalar, its code numbered from 1.", nameof(key));

    /// <summary>The recovery box's key and its code's number, or null unless the item is well-formed and live.</summary>
    public static (uint Code, byte[] Key)? RecoveryBox(SyncBatch.Item item) => item.Type == RecoveryBoxType ? Box(item) : null;

    /// <summary>The account box's key and the account entry's number, as <see cref="RecoveryBox"/>; its body is the same
    /// (<see cref="RecoveryBoxBody"/>).</summary>
    public static (uint Code, byte[] Key)? AccountBox(SyncBatch.Item item) => item.Type == AccountBoxType ? Box(item) : null;

    private static (uint Code, byte[] Key)? Box(SyncBatch.Item item)
    {
        if (item is not { Id: RecoveryBoxId, Body: JsonObject { Count: 2 } body } ||
            Device.Int(body, "code") is not { } code || code is < 1 or > uint.MaxValue ||
            Device.Text(body, "key") is not { } text || CanonicalJson.Bytes(text) is not { } key || !P256.InRange(key))
            return null;
        return ((uint)code, key);
    }

    /// <summary>Whether a known item is well-formed (a tombstone of a known item is); true for an item this version
    /// doesn't know, which a reader keeps and leaves alone.</summary>
    public static bool Valid(SyncBatch.Item item) => (item.Type, item.Id) switch
    {
        (ProfileType, NameId) => item.Body is null || Name(item) is not null,
        (ProfileType, PictureId) => item.Body is null || Picture(item) is not null,
        (ProfileType, BannerId) => item.Body is null || Banner(item) is not null,
        (ProfileType, LookId) => item.Body is null || Look(item) is not null,
        (ProfileType, CanvasId) => item.Body is null || Canvas(item) is not null,
        (ProfileType, StatsId) => item.Body is null || Stats(item) is not null,
        (ProfileType, WatchingId) => item.Body is null || Watching(item) is not null,
        (DeviceChangeType, _) => item.Body is not null && Change(item) is not null,
        (RecoveryBoxType, RecoveryBoxId) => item.Body is null || RecoveryBox(item) is not null,
        (AccountBoxType, RecoveryBoxId) => item.Body is null || AccountBox(item) is not null,
        (HiddenGameType, _) => item.Body is null ? GameId(item.Id) : HiddenGame(item),
        (ProfileFields.LookFieldType or ProfileFields.CanvasBlockType or ProfileFields.CanvasMetaType, _) => ProfileFields.Valid(item),
        (ProfileType or RecoveryBoxType or AccountBoxType, _) => false,
        _ => true,
    };
}
