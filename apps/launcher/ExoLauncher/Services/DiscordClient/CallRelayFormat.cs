using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services.DiscordClient;

/// <summary>
/// What Exo apps in one Discord call say to each other through the call relay (relay/README.md), and how it is
/// sealed. The topic and key come from the call's own DAVE group: 32 bytes of its MLS exporter under
/// <see cref="ExporterLabel"/>, expanded with HKDF-SHA256 into a 128-bit topic and an AES-256-GCM key, so only the
/// call's members in that epoch can find or read them, and every join or leave changes both. A message is
/// <c>01 | nonce(12) | AES-256-GCM(JSON padded with spaces to 256/512/1024/1536 bytes)</c>, its additional data the
/// topic, so the relay sees neither who nor what, nor how long a title is. The JSON says who sent it (their Discord
/// id, which the receiver checks against the call), a sequence number, and the song playing in Exo's Music room or
/// null. Everyone in the call holds the same key, so a member could put another member's id on a message; the
/// receiver accepts only ids in the call.
/// </summary>
internal static partial class CallRelayFormat
{
    internal const string ExporterLabel = "Exo call relay v1";
    internal const int ExporterBytes = 32;
    internal const int MaxPlain = 1536;
    internal const int TitleChars = 200, AlbumChars = 200, ArtistChars = 100, Artists = 6, IdChars = 160;
    internal const long MaxMs = 24 * 60 * 60 * 1000;
    internal static readonly string[] Services = ["spotify", "apple", "youtube"];
    private const byte Version = 1;
    private const int NonceBytes = 12, TagBytes = 16;
    private static readonly byte[] AadLabel = "exo-call-song/1"u8.ToArray();
    private static readonly int[] Buckets = [256, 512, 1024, MaxPlain];
    private static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="Topic">32 lowercase hex characters: the relay's name for this call's epoch.</param>
    internal sealed record Keys(string Topic, byte[] Key);

    /// <param name="Service">spotify, apple or youtube.</param>
    /// <param name="PositionMs">Where the song is as the message is sent.</param>
    /// <param name="Id">The service's own track: spotify:track:…, ytm:track:… or am:songs:…, when it has one.</param>
    internal sealed record Song(string Title, IReadOnlyList<string> Artists, string? Album, long? DurationMs, long PositionMs, bool Playing, string Service, string? Id);

    /// <param name="From">The sender's Discord user id.</param>
    /// <param name="Seq">Counts up per sender; an older one from the same connection is stale.</param>
    /// <param name="Song">What plays; null when nothing does.</param>
    internal sealed record Message(string From, long Seq, Song? Song);

    /// <summary>One frame from the relay: a connection's message (with how long the relay held it) or its leaving.</summary>
    internal sealed record Frame(bool Gone, uint Conn, uint AgeMs, byte[] Payload);

    internal static Keys Derive(ReadOnlySpan<byte> exporterSecret)
    {
        if (exporterSecret.Length != ExporterBytes) throw new ArgumentException("The exporter secret is 32 bytes.", nameof(exporterSecret));
        Span<byte> topic = stackalloc byte[16];
        var key = new byte[32];
        HKDF.Expand(HashAlgorithmName.SHA256, exporterSecret, topic, "topic"u8);
        HKDF.Expand(HashAlgorithmName.SHA256, exporterSecret, key, "song key"u8);
        return new(Convert.ToHexStringLower(topic), key);
    }

    internal static byte[] Seal(Keys keys, Message message)
    {
        var plain = Encode(message);
        var padded = new byte[Buckets.First(size => size >= plain.Length)];
        plain.CopyTo(padded, 0);
        padded.AsSpan(plain.Length).Fill((byte)' ');
        var output = new byte[1 + NonceBytes + padded.Length + TagBytes];
        output[0] = Version;
        var nonce = output.AsSpan(1, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(keys.Key, TagBytes);
        aes.Encrypt(nonce, padded, output.AsSpan(1 + NonceBytes, padded.Length), output.AsSpan(1 + NonceBytes + padded.Length), Aad(keys));
        return output;
    }

    /// <summary>The message, or null when it isn't one for these keys (another epoch, changed on the way, malformed).</summary>
    internal static Message? Open(Keys keys, ReadOnlySpan<byte> data)
    {
        var length = data.Length - 1 - NonceBytes - TagBytes;
        if (length < 1 || length > MaxPlain || data[0] != Version) return null;
        var plain = new byte[length];
        try
        {
            using var aes = new AesGcm(keys.Key, TagBytes);
            aes.Decrypt(data.Slice(1, NonceBytes), data.Slice(1 + NonceBytes, length), data[(1 + NonceBytes + length)..], plain, Aad(keys));
        }
        catch (AuthenticationTagMismatchException) { return null; }
        try { return Decode(plain); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    internal static Frame? ParseFrame(ReadOnlySpan<byte> data)
    {
        if (data.Length == 5 && data[0] == 2) return new(true, BinaryPrimitives.ReadUInt32BigEndian(data[1..]), 0, []);
        if (data.Length > 9 && data[0] == 1)
            return new(false, BinaryPrimitives.ReadUInt32BigEndian(data[1..]), BinaryPrimitives.ReadUInt32BigEndian(data[5..]), data[9..].ToArray());
        return null;
    }

    private static byte[] Aad(Keys keys) => [.. AadLabel, .. Convert.FromHexString(keys.Topic)];

    // The song cut to the caps, then smaller still until it fits: no album, one artist, shorter names.
    private static byte[] Encode(Message message)
    {
        var song = message.Song is { } s
            ? s with
            {
                Title = Cut(s.Title, TitleChars),
                Album = s.Album is { Length: > 0 } album ? Cut(album, AlbumChars) : null,
                Artists = [.. s.Artists.Where(a => !string.IsNullOrWhiteSpace(a)).Take(Artists).Select(a => Cut(a, ArtistChars))],
                Id = Valid(s.Service, s.Id) ? s.Id : null,
            }
            : null;
        var plain = Write(message.From, message.Seq, song);
        if (plain.Length > MaxPlain && song is not null) plain = Write(message.From, message.Seq, song = song with { Album = null });
        if (plain.Length > MaxPlain && song is not null) plain = Write(message.From, message.Seq, song = song with { Artists = [.. song.Artists.Take(1)] });
        if (plain.Length > MaxPlain && song is not null)
            plain = Write(message.From, message.Seq, song with { Title = Cut(song.Title, 60), Artists = [.. song.Artists.Select(a => Cut(a, 60))] });
        return plain;
    }

    private static byte[] Write(string from, long seq, Song? song)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer, Writer))
        {
            json.WriteStartObject();
            json.WriteNumber("v", Version);
            json.WriteString("from", from);
            json.WriteNumber("n", seq);
            if (song is null) json.WriteNull("song");
            else
            {
                json.WriteStartObject("song");
                json.WriteString("title", song.Title);
                json.WriteStartArray("artists");
                foreach (var artist in song.Artists) json.WriteStringValue(artist);
                json.WriteEndArray();
                if (song.Album is not null) json.WriteString("album", song.Album);
                if (song.DurationMs is { } ms) json.WriteNumber("ms", ms);
                json.WriteNumber("pos", song.PositionMs);
                json.WriteBoolean("playing", song.Playing);
                json.WriteString("service", song.Service);
                if (song.Id is not null) json.WriteString("id", song.Id);
                json.WriteEndObject();
            }
            json.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static Message? Decode(byte[] plain)
    {
        using var document = JsonDocument.Parse(plain);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("v", out var v) || !v.TryGetInt32(out var version) || version != Version) return null;
        if (!root.TryGetProperty("from", out var from) || from.GetString() is not { } sender || !Snowflake().IsMatch(sender)) return null;
        if (!root.TryGetProperty("n", out var n) || !n.TryGetInt64(out var seq) || seq < 0) return null;
        if (!root.TryGetProperty("song", out var s)) return null;
        if (s.ValueKind == JsonValueKind.Null) return new(sender, seq, null);
        if (s.ValueKind != JsonValueKind.Object) return null;
        var title = Text(s, "title", TitleChars);
        var service = Text(s, "service", 16);
        if (string.IsNullOrWhiteSpace(title) || service is null || !Services.Contains(service)) return null;
        if (!s.TryGetProperty("artists", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > Artists) return null;
        List<string> artists = [];
        foreach (var artist in list.EnumerateArray())
        {
            if (artist.GetString() is not { Length: > 0 and <= ArtistChars } name) return null;
            artists.Add(name);
        }
        var album = Text(s, "album", AlbumChars);
        if (s.TryGetProperty("album", out _) && album is null) return null;
        long? duration = null;
        if (s.TryGetProperty("ms", out var ms))
        {
            if (!ms.TryGetInt64(out var value) || value <= 0 || value > MaxMs) return null;
            duration = value;
        }
        if (!s.TryGetProperty("pos", out var pos) || !pos.TryGetInt64(out var position) || position < 0 || position > MaxMs) return null;
        if (!s.TryGetProperty("playing", out var playing) || playing.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
        var id = Text(s, "id", IdChars);
        return new(sender, seq, new(title, artists, album, duration, position, playing.GetBoolean(), service, Valid(service, id) ? id : null));
    }

    /// <summary>A string property no longer than max, else null (missing, not a string, or too long).</summary>
    private static string? Text(JsonElement parent, string name, int max) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= max ? text : null;

    /// <summary>A track id in the form its service gives it, for that service.</summary>
    internal static bool Valid(string service, string? id) => id is not null && service switch
    {
        "spotify" => SpotifyTrack().IsMatch(id),
        "youtube" => YouTubeTrack().IsMatch(id),
        "apple" => AppleSong().IsMatch(id),
        _ => false,
    };

    private static string Cut(string text, int max)
    {
        text = text.Trim();
        if (text.Length <= max) return text;
        return char.IsHighSurrogate(text[max - 1]) ? text[..(max - 1)] : text[..max];
    }

    [GeneratedRegex("^[0-9]{1,20}$")] private static partial Regex Snowflake();
    [GeneratedRegex("^spotify:track:[A-Za-z0-9]{22}$")] private static partial Regex SpotifyTrack();
    [GeneratedRegex("^ytm:track:[A-Za-z0-9_-]{11}$")] private static partial Regex YouTubeTrack();
    [GeneratedRegex("^am:songs:[0-9]{1,20}$")] private static partial Regex AppleSong();
}
