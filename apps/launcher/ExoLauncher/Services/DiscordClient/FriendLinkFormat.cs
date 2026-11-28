using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// The friend link's crypto and wire form: a sealed channel between two friends' Exo apps over Exo's relay
/// (relay/README.md), for what exists only for other Exo users. Each Discord account on a PC has a long-lived P-256 key
/// (<see cref="FriendLinkKeys"/>); its public half travels in the account's own Discord presence
/// (<see cref="FriendKeyCarrier"/>), which Discord authenticates; ECDH and HKDF give two friends the same topic and key.
///
/// A message is <c>01 | nonce(12) | AES-256-GCM(JSON padded with spaces to <see cref="PlainBytes"/>) | tag(16)</c>, its
/// additional data the topic and the sender's direction. The JSON is <c>{ v, at, hello?, s: { type: section } }</c>: the
/// sender's whole current state, since the relay holds only each connection's last message. New things go on it as new
/// section types, without touching the pairing or the crypto, and an Exo that doesn't know a type ignores it.
///
/// How it can fail, and what stops it:
/// - The two sides derive different topics or keys: both sort by Discord id, so the result doesn't depend on who asks.
/// - A third party's key, or a server-mate copying a friend's key: the ids are in the derivation, and ECDH needs the
///   private half, so they get another topic and can't read this one.
/// - A key from a presence that isn't a point on the curve, or is another format: <see cref="TryDecodeKey"/> and the
///   import refuse it, and that friend is skipped.
/// - A message sent back at its sender, or moved to another topic: direction and topic are in the additional data.
/// - The relay replays an old message: <see cref="Receipt"/> refuses anything older than the last one taken, or older
///   than <see cref="FreshMs"/>. Clocks further apart than that show nothing, which errs on the side of silence. The
///   last one again is fine: it is what a reconnect is handed, and it says what is already known.
/// - The relay learns a title's length: every message is the same size.
/// - A friend's Exo sends something huge or strange: the size is fixed, the depth bounded, and each section type reads
///   its own bounds; one bad section loses only itself.
/// - A newer Exo's message: a higher envelope version is ignored; unknown section types are skipped.
/// </summary>
internal static partial class FriendLinkFormat
{
    /// <summary>A compressed P-256 point, base64url: 33 bytes.</summary>
    internal const int KeyChars = 44;
    /// <summary>Every message's plain size, so its length says nothing. Room for several sections.</summary>
    internal const int PlainBytes = 1536;
    /// <summary>A message older or further ahead than this is refused: at most this much of an old state can be replayed.</summary>
    internal const long FreshMs = 10 * 60 * 1000;
    internal const int MaxSections = 16, SectionNameChars = 32;
    private const byte Version = 1;
    private const int NonceBytes = 12, TagBytes = 16, PointBytes = 33, CoordinateBytes = 32;
    private static readonly byte[] Label = "Exo friend link v1"u8.ToArray();
    private static readonly byte[] AadLabel = "exo-friend-link/1"u8.ToArray();
    private static readonly JsonSerializerOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly BigInteger P256Prime = BigInteger.Parse("0ffffffff00000001000000000000000000000000ffffffffffffffffffffffff", NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private static readonly BigInteger P256B = BigInteger.Parse("05ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b", NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <param name="Topic">32 lowercase hex characters: the relay's name for this pair.</param>
    /// <param name="Direction">0 when this side's Discord id is the lower of the two, else 1: said in what this side seals.</param>
    internal sealed record Keys(string Topic, byte[] Key, byte Direction);

    /// <param name="AtMs">When it was sealed, on the sender's clock.</param>
    /// <param name="Hello">The sender just connected: it wants a fresh message if the one the relay held is old.</param>
    /// <param name="Sections">Each section by type; one this Exo doesn't know is simply never read.</param>
    internal sealed record Message(long AtMs, bool Hello, IReadOnlyDictionary<string, JsonElement> Sections);

    /// <summary>The public half as it travels: compressed and base64url.</summary>
    internal static string EncodeKey(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        var point = new byte[PointBytes];
        point[0] = (byte)(q.Y![CoordinateBytes - 1] % 2 == 0 ? 2 : 3);
        q.X!.CopyTo(point, 1);
        return Convert.ToBase64String(point).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Whether this has a compressed P-256 point's form. Whether it is on the curve shows at <see cref="Derive"/>.</summary>
    internal static bool LooksLikeKey(string? text) => text is { Length: KeyChars } && KeyShape().IsMatch(text);

    /// <summary>The two ids' topic and key, or null when their key can't be one (not on the curve, or this side's own).</summary>
    internal static Keys? Derive(ECDiffieHellman mine, string myId, string friendId, string friendKey)
    {
        var myKey = EncodeKey(mine);
        if (!Snowflake().IsMatch(myId) || !Snowflake().IsMatch(friendId) || myId == friendId || friendKey == myKey || !TryDecodeKey(friendKey, out var q)) return null;
        byte[] secret;
        try
        {
            using var theirs = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = q });
            secret = mine.DeriveRawSecretAgreement(theirs.PublicKey);
        }
        catch (CryptographicException) { return null; }
        var iAmLow = CompareIds(myId, friendId) < 0;
        var (lowId, highId, lowKey, highKey) = iAmLow ? (myId, friendId, myKey, friendKey) : (friendId, myId, friendKey, myKey);
        var who = Encoding.ASCII.GetBytes($"{lowId}|{highId}|{lowKey}|{highKey}|");
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, secret, Label);
        CryptographicOperations.ZeroMemory(secret);
        Span<byte> topic = stackalloc byte[16];
        var key = new byte[32];
        HKDF.Expand(HashAlgorithmName.SHA256, prk, topic, [.. who, .. "topic"u8]);
        HKDF.Expand(HashAlgorithmName.SHA256, prk, key, [.. who, .. "key"u8]);
        CryptographicOperations.ZeroMemory(prk);
        return new(Convert.ToHexStringLower(topic), key, iAmLow ? (byte)0 : (byte)1);
    }

    /// <summary>The sender's state as one message, or null when it doesn't fit (each section's own bounds should keep that from happening).</summary>
    internal static byte[]? Seal(Keys keys, long atMs, bool hello, IEnumerable<KeyValuePair<string, JsonNode?>> sections)
    {
        var body = new JsonObject();
        foreach (var (name, value) in sections) body[name] = value?.DeepClone();
        var root = new JsonObject { ["v"] = (int)Version, ["at"] = atMs };
        if (hello) root["hello"] = true;
        root["s"] = body;
        var plain = JsonSerializer.SerializeToUtf8Bytes(root, Writer);
        if (plain.Length > PlainBytes) return null;
        var padded = new byte[PlainBytes];
        plain.CopyTo(padded, 0);
        padded.AsSpan(plain.Length).Fill((byte)' ');
        var output = new byte[1 + NonceBytes + PlainBytes + TagBytes];
        output[0] = Version;
        var nonce = output.AsSpan(1, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(keys.Key, TagBytes);
        aes.Encrypt(nonce, padded, output.AsSpan(1 + NonceBytes, PlainBytes), output.AsSpan(1 + NonceBytes + PlainBytes), Aad(keys, keys.Direction));
        return output;
    }

    /// <summary>What the other side sealed, or null when it isn't: another pair's, this side's own, changed on the way, malformed, or a newer format.</summary>
    internal static Message? Open(Keys keys, ReadOnlySpan<byte> data)
    {
        if (data.Length != 1 + NonceBytes + PlainBytes + TagBytes || data[0] != Version) return null;
        var plain = new byte[PlainBytes];
        try
        {
            using var aes = new AesGcm(keys.Key, TagBytes);
            aes.Decrypt(data.Slice(1, NonceBytes), data.Slice(1 + NonceBytes, PlainBytes), data[(1 + NonceBytes + PlainBytes)..], plain, Aad(keys, (byte)(1 - keys.Direction)));
        }
        catch (AuthenticationTagMismatchException) { return null; }
        try
        {
            using var document = JsonDocument.Parse(plain, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("v", out var v) || !v.TryGetInt32(out var version) || version != Version) return null;
            if (!root.TryGetProperty("at", out var at) || !at.TryGetInt64(out var atMs) || atMs <= 0) return null;
            if (!root.TryGetProperty("s", out var s) || s.ValueKind != JsonValueKind.Object) return null;
            var sections = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var section in s.EnumerateObject().Take(MaxSections))
                if (section.Name.Length is > 0 and <= SectionNameChars) sections[section.Name] = section.Value.Clone();
            return new(atMs, root.TryGetProperty("hello", out var hello) && hello.ValueKind == JsonValueKind.True, sections);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>The checks on when a message was sealed, per friend: not older than the last taken, and not too far from this PC's clock.</summary>
    internal sealed class Receipt
    {
        private long _last;

        internal bool Accept(long atMs, long nowMs)
        {
            if (atMs < _last || Math.Abs(nowMs - atMs) > FreshMs) return false;
            _last = atMs;
            return true;
        }
    }

    /// <summary>The lower of two ids goes first: Discord's ids are numbers without leading zeros, so longer is larger.</summary>
    private static int CompareIds(string a, string b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);

    private static byte[] Aad(Keys keys, byte direction) => [.. AadLabel, .. Convert.FromHexString(keys.Topic), direction];

    // P-256: y^2 = x^3 - 3x + b over p, where p = 3 mod 4, so a root is the (p + 1) / 4th power; no root means no point.
    private static bool TryDecodeKey(string text, out ECPoint q)
    {
        q = default;
        if (!LooksLikeKey(text)) return false;
        var point = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/'));
        if (point.Length != PointBytes || point[0] is not (2 or 3)) return false;
        var x = new BigInteger(point.AsSpan(1), isUnsigned: true, isBigEndian: true);
        if (x >= P256Prime) return false;
        var square = (BigInteger.ModPow(x, 3, P256Prime) - 3 * x + P256B) % P256Prime;
        if (square.Sign < 0) square += P256Prime;
        var y = BigInteger.ModPow(square, (P256Prime + 1) / 4, P256Prime);
        if (BigInteger.ModPow(y, 2, P256Prime) != square) return false;
        if (y.IsEven != (point[0] == 2)) y = P256Prime - y;
        var bytes = y.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[CoordinateBytes];
        bytes.CopyTo(padded, CoordinateBytes - bytes.Length);
        q = new ECPoint { X = point[1..], Y = padded };
        return true;
    }

    [GeneratedRegex("^[0-9]{1,20}$")] private static partial Regex Snowflake();
    [GeneratedRegex("^[A-Za-z0-9_-]{44}$")] private static partial Regex KeyShape();
}
