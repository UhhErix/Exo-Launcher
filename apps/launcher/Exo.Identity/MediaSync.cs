using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// The profile's media following the account (account plan item 6): each media file the profile names (its picture,
/// banner and background, a canvas's pictures, gear pictures, the stats' covers and art, watching's posters) is put in a
/// box of its own, <c>profile-media</c> (§2.7: its box key and sub-key from the sync key, so a new sync key starts a new
/// box), sealed and signed like every sync envelope. Another PC reads the box, puts each file together, checks its
/// SHA-256 and keeps it under its hash in the profile's media folder (<see cref="ProfileMedia"/>), so every app on that
/// PC plays it.
///
/// A row is <c>u32 big-endian header length, header, bytes</c>; the header is canonical JSON
/// <c>{"bytes","part","parts","sha","type","v":1}</c>. A file of at most <see cref="MaxFileBytes"/> travels in the rows
/// themselves, at most <see cref="ChunkBytes"/> each (the mailbox writes a row at a time: 20, then one every 30 s). A
/// bigger one, up to <see cref="ProfileItems.MaxMediaBytes"/>, goes to the mailbox's media store (<see cref="MediaStore"/>,
/// R2) in parts of at most <see cref="StoredPartBytes"/>, each sealed with AES-256-GCM under a key from the box's
/// sub-key and named by a keyed hash of the file's SHA-256 and its part, so the store sees neither; its box row is a
/// pointer: the header with <c>"store":"r2"</c> and no bytes. A repost (<see cref="Options.Repost"/>, about weekly) puts
/// every row again and trims what's below it, so nothing expires behind newer rows, and takes stored files no longer
/// named out of the store.
/// </summary>
public sealed class MediaSync(MailboxConnect connect, MediaSync.Options options)
{
    public const string App = "profile-media";
    public const int ChunkBytes = 60_000;
    public const long MaxFileBytes = 1L << 20;
    public const int StoredPartBytes = 32 * 1024 * 1024;

    /// <summary>A media file the profile names.</summary>
    public sealed record Wanted(string Sha, string Type, long Bytes);

    /// <param name="Folder">The profile's media folder on this PC.</param>
    /// <param name="Wanted">Every media file the profile names now.</param>
    /// <param name="Posted">The files this PC knows are in the box (put by it, or read whole from it).</param>
    /// <param name="Stored">The files this PC put in the media store, by SHA-256, with their part counts.</param>
    /// <param name="Repost">Put every row again and trim below (a new box, or about weekly).</param>
    /// <param name="Store">The mailbox's media store; without one only files that fit rows travel.</param>
    public sealed record Options(
        uint Epoch,
        byte[] SyncKey,
        IDeviceSigner Signer,
        Func<IReadOnlyList<byte[]>> Senders,
        string Folder,
        IReadOnlyList<Wanted> Wanted,
        IReadOnlySet<string> Posted,
        uint Cursor,
        bool Repost,
        MailboxPace? Pace = null,
        MediaStore? Store = null,
        IReadOnlyDictionary<string, int>? Stored = null);

    /// <summary>What a run did: the cursor it read to, the files now known to be in the box, the ones this PC keeps in
    /// the store, and the files it fetched.</summary>
    public sealed record Result(uint Cursor, IReadOnlySet<string> Posted, IReadOnlyDictionary<string, int> Stored, IReadOnlyList<string> Fetched);

    private readonly byte[] _boxKey = Envelope.SyncSubKey(options.SyncKey, App);
    private readonly byte[] _subKey = Envelope.SyncSubKey(options.SyncKey, App);

    /// <summary>Whether a media file can travel in rows: small enough, of a type Exo plays.</summary>
    public static bool Travels(Wanted media) => media.Bytes is > 0 and <= MaxFileBytes && Named(media);

    /// <summary>Whether a media file goes to the media store instead: bigger than rows take, at most 200 MB.</summary>
    public static bool Stores(Wanted media) => media.Bytes > MaxFileBytes && media.Bytes <= ProfileItems.MaxMediaBytes && Named(media);

    private static bool Named(Wanted media) => ProfileItems.MediaTypes.Contains(media.Type) && media.Sha is { Length: 64 } sha && sha.All(char.IsAsciiHexDigitLower);

    /// <summary>The media a profile's items name: the look's, the canvas's (its media blocks and gear pictures), the stats'
    /// covers and art, and watching's posters; each once.</summary>
    public static IReadOnlyList<Wanted> Named(JsonObject? look, JsonObject? canvas, JsonObject? stats, JsonObject? watching)
    {
        var found = new Dictionary<string, Wanted>(StringComparer.Ordinal);
        void Add(JsonNode? media)
        {
            if (!ProfileItems.IsMedia(media)) return;
            var m = media!.AsObject();
            var wanted = new Wanted(Device.Text(m, "sha")!, Device.Text(m, "type")!, Device.Int(m, "bytes")!.Value);
            found.TryAdd(wanted.Sha, wanted);
        }
        foreach (var slot in ProfileItems.MediaSlots) Add(look?[slot]);
        foreach (var block in (canvas?["blocks"] as JsonArray ?? []).OfType<JsonObject>())
        {
            Add(block["media"]);
            foreach (var pick in (block["gear"] as JsonArray ?? []).OfType<JsonObject>()) Add(pick["picture"]);
        }
        foreach (var game in (stats?["games"] as JsonArray ?? []).OfType<JsonObject>()) Add(game["cover"]);
        Add(stats?["hero"]);
        foreach (var title in (watching?["titles"] as JsonArray ?? []).OfType<JsonObject>()) Add(title["poster"]);
        return [.. found.Values];
    }

    public async Task<Result> RunAsync(CancellationToken ct)
    {
        using var boxSigner = new EcdsaSigner(P256.Signer(Mailbox.BoxScalar(_boxKey)));
        await using var box = await MailboxBox.OpenAsync(connect, boxSigner, ct, options.Pace).ConfigureAwait(false);
        var boxId = Convert.FromHexString(Mailbox.BoxId(SyncSession.BoxPublicKey(_boxKey)));
        var cursor = SyncSession.CursorMissed(options.Cursor, box.First, box.Last) ? 0 : options.Cursor;
        var posted = new HashSet<string>(options.Posted, StringComparer.Ordinal);
        var stored = new Dictionary<string, int>(options.Stored ?? new Dictionary<string, int>(), StringComparer.Ordinal);
        var store = options.Store;
        var wanted = options.Wanted.Where(w => Travels(w) || store is not null && Stores(w)).ToDictionary(w => w.Sha, StringComparer.Ordinal);
        var fetched = new List<string>();
        var parts = Path.Combine(options.Folder, ".parts");

        // Read what came since the cursor: a file in rows put together once all its parts are here, a stored one fetched.
        foreach (var message in await box.SinceAsync(cursor, ct).ConfigureAwait(false))
        {
            cursor = Math.Max(cursor, message.Seq);
            if (Envelope.OpenSync(message.Blob, boxId, _subKey, options.Senders()) is not { } opened || Read(opened.Data) is not var (header, bytes)) continue;
            if (!wanted.TryGetValue(header.Sha, out var want) || want.Type != header.Type || want.Bytes != header.Bytes) continue;
            if (Have(want)) { posted.Add(want.Sha); continue; }
            if (header.Stored)
            {
                if (store is not null && await FetchAsync(store, boxSigner, boxId, want, header.Parts, ct).ConfigureAwait(false))
                {
                    posted.Add(want.Sha);
                    fetched.Add(want.Sha);
                }
                continue;
            }
            Directory.CreateDirectory(parts);
            await File.WriteAllBytesAsync(Path.Combine(parts, $"{header.Sha}.{header.Part}"), bytes, ct).ConfigureAwait(false);
            if (await AssembleAsync(want, header.Parts, parts, ct).ConfigureAwait(false))
            {
                posted.Add(want.Sha);
                fetched.Add(want.Sha);
            }
        }

        // Put what this PC has and the box doesn't (everything, on a repost).
        uint? first = null;
        foreach (var want in wanted.Values.OrderBy(w => w.Bytes))
        {
            if (!options.Repost && posted.Contains(want.Sha)) continue;
            if (FileOf(want) is not { } path) continue;
            var data = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (data.LongLength != want.Bytes || Convert.ToHexStringLower(SHA256.HashData(data)) != want.Sha) continue;
            if (Stores(want))
            {
                var count = (int)((data.LongLength + StoredPartBytes - 1) / StoredPartBytes);
                // Up once: a repost only puts its pointer row again.
                if (!stored.ContainsKey(want.Sha) && !posted.Contains(want.Sha))
                {
                    for (var part = 0; part < count; part++)
                    {
                        var chunk = data.AsSpan(part * StoredPartBytes, Math.Min(StoredPartBytes, data.Length - part * StoredPartBytes));
                        var name = PartName(want.Sha, part);
                        await store!.PutAsync(boxSigner, name, Seal(boxId, name, chunk), ct).ConfigureAwait(false);
                    }
                    stored[want.Sha] = count;
                }
                // Its pointer row always goes (??= would skip the put once an earlier row set first).
                var pointer = await box.PutAsync(Envelope.SealSync(boxId, options.Epoch, _subKey, Row(want, 0, count, [], stored: true), options.Signer), ct).ConfigureAwait(false);
                first ??= pointer;
            }
            else
            {
                var count = Math.Max(1, (data.Length + ChunkBytes - 1) / ChunkBytes);
                for (var part = 0; part < count; part++)
                {
                    var chunk = data.AsSpan(part * ChunkBytes, Math.Min(ChunkBytes, data.Length - part * ChunkBytes));
                    var seq = await box.PutAsync(Envelope.SealSync(boxId, options.Epoch, _subKey, Row(want, part, count, chunk), options.Signer), ct).ConfigureAwait(false);
                    first ??= seq;
                }
            }
            posted.Add(want.Sha);
        }
        if (options.Repost)
        {
            // A repost holds every file named now: what's below it goes once this PC had read everything before it, and
            // stored files no longer named leave the store.
            if (first is > 1 and var start && cursor + 1 == start) await box.TrimAsync(start - 1, ct).ConfigureAwait(false);
            if (store is not null)
                foreach (var (sha, count) in stored.Where(s => !wanted.ContainsKey(s.Key)).ToList())
                {
                    for (var part = 0; part < count; part++) await store.DeleteAsync(boxSigner, PartName(sha, part), ct).ConfigureAwait(false);
                    stored.Remove(sha);
                }
        }
        return new Result(cursor, posted, stored, fetched);
    }

    private bool Have(Wanted want) => FileOf(want) is not null;

    private string? FileOf(Wanted want) =>
        Path.Combine(options.Folder, want.Sha + ProfileMedia.Extension(want.Type)) is var path && File.Exists(path) ? path : null;

    private string PartName(string sha, int part) => PartName(_subKey, sha, part);
    private byte[] Seal(ReadOnlySpan<byte> boxId, string name, ReadOnlySpan<byte> plain) => Seal(_subKey, boxId, name, plain);
    private byte[]? Open(ReadOnlySpan<byte> boxId, string name, ReadOnlySpan<byte> sealedPart) => Open(_subKey, boxId, name, sealedPart);

    /// <summary>A stored part's name: hex HMAC-SHA256 under HKDF(sub-key, "exo media/1 name") of <c>"&lt;sha&gt;/&lt;part&gt;"</c>,
    /// so the store can't tell the file.</summary>
    public static string PartName(ReadOnlySpan<byte> subKey, string sha, int part)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, subKey.ToArray(), 32, [], "exo media/1 name"u8.ToArray());
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"{sha}/{part}")));
    }

    /// <summary>A stored part sealed: the nonce (random unless a test gives one), then AES-256-GCM's ciphertext and tag
    /// under HKDF(sub-key, "exo media/1 key"), with <see cref="Bound"/> as its associated data.</summary>
    public static byte[] Seal(ReadOnlySpan<byte> subKey, ReadOnlySpan<byte> boxId, string name, ReadOnlySpan<byte> plain, ReadOnlySpan<byte> nonce = default)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, subKey.ToArray(), 32, [], "exo media/1 key"u8.ToArray());
        var sealedPart = new byte[12 + plain.Length + 16];
        if (nonce.Length == 12) nonce.CopyTo(sealedPart);
        else RandomNumberGenerator.Fill(sealedPart.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(sealedPart.AsSpan(0, 12), plain, sealedPart.AsSpan(12, plain.Length), sealedPart.AsSpan(12 + plain.Length), Bound(boxId, name));
        return sealedPart;
    }

    public static byte[]? Open(ReadOnlySpan<byte> subKey, ReadOnlySpan<byte> boxId, string name, ReadOnlySpan<byte> sealedPart)
    {
        if (sealedPart.Length < 28) return null;
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, subKey.ToArray(), 32, [], "exo media/1 key"u8.ToArray());
        var plain = new byte[sealedPart.Length - 28];
        using var aes = new AesGcm(key, 16);
        try { aes.Decrypt(sealedPart[..12], sealedPart[12..^16], sealedPart[^16..], plain, Bound(boxId, name)); }
        catch (AuthenticationTagMismatchException) { return null; }
        return plain;
    }

    /// <summary>A stored part's associated data: <c>"exo media/1" || box id (16 bytes) || name (ASCII)</c>.</summary>
    public static byte[] Bound(ReadOnlySpan<byte> boxId, string name) => [.. "exo media/1"u8, .. boxId, .. Encoding.ASCII.GetBytes(name)];

    private async Task<bool> FetchAsync(MediaStore store, IDeviceSigner boxSigner, byte[] boxId, Wanted want, int count, CancellationToken ct)
    {
        using var whole = new MemoryStream();
        for (var part = 0; part < count; part++)
        {
            var name = PartName(want.Sha, part);
            if (await store.GetAsync(boxSigner, name, ct).ConfigureAwait(false) is not { } sealedPart || Open(boxId, name, sealedPart) is not { } plain) return false;
            await whole.WriteAsync(plain, ct).ConfigureAwait(false);
        }
        return await KeepAsync(want, whole.ToArray(), ct).ConfigureAwait(false);
    }

    private async Task<bool> AssembleAsync(Wanted want, int count, string parts, CancellationToken ct)
    {
        var files = Enumerable.Range(0, count).Select(i => Path.Combine(parts, $"{want.Sha}.{i}")).ToList();
        if (!files.All(File.Exists)) return false;
        using var whole = new MemoryStream();
        foreach (var file in files) await whole.WriteAsync(await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        foreach (var file in files) DeletionGate.DeleteFile(file);
        return await KeepAsync(want, whole.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>Only the file the profile names, byte for byte, goes in the folder.</summary>
    private async Task<bool> KeepAsync(Wanted want, byte[] bytes, CancellationToken ct)
    {
        if (bytes.LongLength != want.Bytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != want.Sha) return false;
        Directory.CreateDirectory(options.Folder);
        var target = Path.Combine(options.Folder, want.Sha + ProfileMedia.Extension(want.Type));
        var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
        try { File.Move(temp, target, overwrite: false); }
        catch (IOException) when (File.Exists(target)) { DeletionGate.DeleteFile(temp); }
        return true;
    }

    private sealed record Header(string Sha, string Type, long Bytes, int Part, int Parts, bool Stored);

    /// <summary>One row's data: its header's length, the header, then the part's bytes (none for a stored file's pointer).</summary>
    public static byte[] Row(Wanted media, int part, int parts, ReadOnlySpan<byte> bytes, bool stored = false)
    {
        var json = new JsonObject { ["bytes"] = media.Bytes, ["part"] = part, ["parts"] = parts, ["sha"] = media.Sha, ["type"] = media.Type, ["v"] = 1 };
        if (stored) json["store"] = "r2";
        var header = CanonicalJson.Serialize(json);
        var row = new byte[4 + header.Length + bytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(row, (uint)header.Length);
        header.CopyTo(row.AsSpan(4));
        bytes.CopyTo(row.AsSpan(4 + header.Length));
        return row;
    }

    private static (Header, byte[])? Read(byte[] row)
    {
        if (row.Length < 4) return null;
        var length = BinaryPrimitives.ReadUInt32BigEndian(row);
        if (length > 1024 || 4 + length > row.Length) return null;
        JsonObject? json;
        try { json = JsonNode.Parse(Encoding.UTF8.GetString(row, 4, (int)length)) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
        if (json is null || Device.Int(json, "v") != 1 || Device.Text(json, "sha") is not { } sha || Device.Text(json, "type") is not { } type ||
            Device.Int(json, "bytes") is not { } bytes || Device.Int(json, "parts") is not { } parts || parts is < 1 or > 64 ||
            Device.Int(json, "part") is not { } part || part < 0 || part >= parts)
            return null;
        var stored = json.ContainsKey("store");
        if (stored && Device.Text(json, "store") != "r2") return null;
        var media = new Wanted(sha, type, bytes);
        if (!(stored ? Stores(media) : Travels(media))) return null;
        return (new Header(sha, type, bytes, (int)part, (int)parts, stored), row[(4 + (int)length)..]);
    }
}
