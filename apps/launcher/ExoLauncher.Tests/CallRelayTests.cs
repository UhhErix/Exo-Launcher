using System.Text;
using ExoLauncher.Services.DiscordClient;
using F = ExoLauncher.Services.DiscordClient.CallRelayFormat;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the call relay's format could leak a song or let a wrong one through, all silent in the app:
/// - two members derive different topics or keys from the same exporter secret (nobody hears anybody), or the topic
///   and key aren't independent (the relay, which sees the topic, learns about the key);
/// - a message opens under another call's or another epoch's key, or after being moved to another topic;
/// - a byte changed on the way, or another version, still opens;
/// - the sealed size tells the relay how long the title is;
/// - a long title, album or many artists make a message the relay refuses (over 2048 bytes);
/// - a malformed message is taken: a sender that isn't a Discord id, an unknown service, a negative or huge position,
///   too many artists, a missing field;
/// - a track id in the wrong form reaches a play URI (another service's, or something else entirely);
/// - the relay's frames are read in the wrong byte order (wrong connection, wrong age, so a wrong position);
/// - the Music room's ids are turned into the wrong service's form.
/// </summary>
public sealed class CallRelayTests
{
    private static readonly byte[] Secret = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private static readonly F.Song Song = new("Get Lucky", ["Daft Punk", "Pharrell Williams"], "Random Access Memories", 369_000, 42_000, true, "spotify", "spotify:track:69kOkLUCkxIZYexIgSG8rq");

    [Fact]
    public void DerivesTheTopicAndKeyAsHkdfExpand()
    {
        // Expected values from an independent HKDF-Expand (Python's hmac): T(1) = HMAC-SHA256(secret, info || 0x01).
        var keys = F.Derive(Secret);
        Assert.Equal("c6bdfe61eb273401b3ded1a98b10ae75", keys.Topic);
        Assert.Equal("b278cc6332713bfc26f12fbf2c6ad367cb4ac17d4c2d4555f820f9ffde43ed06", Convert.ToHexStringLower(keys.Key));
        Assert.Equal(keys, F.Derive(Secret) with { Key = keys.Key });
        Assert.Equal(keys.Key, F.Derive(Secret).Key);
        Assert.DoesNotContain(keys.Topic, Convert.ToHexStringLower(keys.Key), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => F.Derive(new byte[16]));
    }

    [Fact]
    public void AnotherSecretGivesAnotherTopicAndKey()
    {
        var other = (byte[])Secret.Clone();
        other[31] ^= 1;
        var (a, b) = (F.Derive(Secret), F.Derive(other));
        Assert.NotEqual(a.Topic, b.Topic);
        Assert.NotEqual(a.Key, b.Key);
    }

    [Fact]
    public void OpensWhatItSealed()
    {
        var keys = F.Derive(Secret);
        var message = F.Open(keys, F.Seal(keys, new("123456789012345678", 7, Song)));
        Assert.NotNull(message);
        Assert.Equal("123456789012345678", message.From);
        Assert.Equal(7, message.Seq);
        Assert.Equal(Song.Title, message.Song!.Title);
        Assert.Equal(Song.Artists, message.Song.Artists);
        Assert.Equal(Song with { Artists = message.Song.Artists }, message.Song);

        var none = F.Open(keys, F.Seal(keys, new("123456789012345678", 8, null)));
        Assert.NotNull(none);
        Assert.Null(none.Song);
    }

    [Fact]
    public void EachSealIsFresh()
    {
        var keys = F.Derive(Secret);
        var message = new F.Message("1", 1, Song);
        Assert.NotEqual(F.Seal(keys, message), F.Seal(keys, message));
    }

    [Fact]
    public void OnlyTheSameEpochOnTheSameTopicOpensIt()
    {
        var keys = F.Derive(Secret);
        var other = (byte[])Secret.Clone();
        other[0] ^= 0x80;
        var sealedMessage = F.Seal(keys, new("1", 1, Song));
        Assert.Null(F.Open(F.Derive(other), sealedMessage));
        // The right key, but the message moved to another topic.
        Assert.Null(F.Open(keys with { Topic = F.Derive(other).Topic }, sealedMessage));
    }

    [Fact]
    public void AnyChangedByteFailsTheCheck()
    {
        var keys = F.Derive(Secret);
        var sealedMessage = F.Seal(keys, new("1", 1, Song));
        for (var i = 0; i < sealedMessage.Length; i++)
        {
            var changed = (byte[])sealedMessage.Clone();
            changed[i] ^= 0x01;
            Assert.Null(F.Open(keys, changed));
        }
        Assert.Null(F.Open(keys, sealedMessage[..^1]));
        Assert.Null(F.Open(keys, [.. sealedMessage, 0]));
        Assert.Null(F.Open(keys, []));
    }

    [Fact]
    public void ItsSizeShowsOnlyABucket()
    {
        var keys = F.Derive(Secret);
        // Only the bucket shows: titles of any length within one bucket seal to the same size, and every size is a bucket.
        int Size(string title) => F.Seal(keys, new("123456789012345678", 1, Song with { Title = title, Album = null, Id = null })).Length;
        Assert.Equal(256 + 29, Size("A"));
        Assert.Equal(Size("A"), Size("A much longer song title, though still short"));
        Assert.All(new[] { "A", new string('x', 150), new string('x', 200), new string('é', 200) },
            title => Assert.Contains(Size(title) - 29, new[] { 256, 512, 1024, 1536 }));
    }

    [Fact]
    public void TheBiggestSongStillFitsTheRelay()
    {
        var keys = F.Derive(Secret);
        // Four-byte characters and characters JSON escapes, everywhere, far past every cap.
        var huge = string.Concat(Enumerable.Repeat("😀\"\\\u0001", 400));
        var song = new F.Song(huge, [.. Enumerable.Repeat(huge, 20)], huge, 1000, 0, true, "youtube", "ytm:track:dQw4w9WgXcQ");
        var sealedMessage = F.Seal(keys, new("123456789012345678", long.MaxValue, song));
        Assert.True(sealedMessage.Length <= 2048, $"{sealedMessage.Length} bytes");
        var opened = F.Open(keys, sealedMessage);
        Assert.NotNull(opened?.Song);
        Assert.True(opened.Song.Title.Length <= F.TitleChars);
        Assert.False(char.IsHighSurrogate(opened.Song.Title[^1]));
        Assert.InRange(opened.Song.Artists.Count, 1, F.Artists);
    }

    [Theory]
    [InlineData("""{"v":1,"from":"12","n":1,"song":null}""", true)]
    [InlineData("""{"v":2,"from":"12","n":1,"song":null}""", false)]
    [InlineData("""{"v":1,"from":"me","n":1,"song":null}""", false)]
    [InlineData("""{"v":1,"from":"123456789012345678901","n":1,"song":null}""", false)]
    [InlineData("""{"v":1,"from":"12","n":-1,"song":null}""", false)]
    [InlineData("""{"v":1,"from":"12","song":null}""", false)]
    [InlineData("""{"v":1,"from":"12","n":1}""", false)]
    [InlineData("""{"v":1,"from":"12","n":1,"song":"x"}""", false)]
    [InlineData("""[1]""", false)]
    [InlineData("""{"v":1,"from":"12","n":1,"song":{"title":"T","artists":["A"],"pos":0,"playing":true,"service":"spotify"}}""", true)]
    [InlineData("""{"v":1,"from":"12","n":1,"song":{"title":"","artists":["A"],"pos":0,"playing":true,"service":"spotify"}}""", false)]
    [InlineData("""{"v":1,"from":"12","n":1,"song":{"title":"T","artists":["A"],"pos":0,"playing":true,"service":"tidal"}}""", false)]
    [InlineData("""{"v":1,"from":"12","n":1,"song":{"title":"T","artists":["A"],"pos":-5,"playing":true,"service":"apple"}}""", false)]
    [InlineData("""{"v":1,"from":"12","n":1,"song":{"title":"T","artists":["A"],"pos":86400001,"playing":true,"service":"apple"}}""", false)]
    [InlineData("""{"v":1,"from":"12","n":1,"song":{"title":"T","artists":["A"],"pos":0,"ms":0,"playing":true,"service":"apple"}}""", false)]
    [InlineData("""{"v":1,"from":"12","n":1,"song":{"title":"T","artists":["A"],"pos":0,"playing":"yes","service":"apple"}}""", false)]
    [InlineData("""{"v":1,"from":"12","n":1,"song":{"title":"T","artists":["A","B","C","D","E","F","G"],"pos":0,"playing":true,"service":"apple"}}""", false)]
    [InlineData("""{"v":1,"from":"12","n":1,"song":{"title":"T","artists":[""],"pos":0,"playing":true,"service":"apple"}}""", false)]
    [InlineData("""{"v":1,"from":"12","n":1,"song":{"title":"T","artists":"A","pos":0,"playing":true,"service":"apple"}}""", false)]
    [InlineData("""{"v":1,"from":"12","n":1,"song":{"title":"T","artists":["A"],"album":3,"pos":0,"playing":true,"service":"apple"}}""", false)]
    public void TakesOnlyAWellFormedMessage(string json, bool taken)
    {
        var keys = F.Derive(Secret);
        Assert.Equal(taken, F.Open(keys, SealRaw(keys, json)) is not null);
    }

    [Theory]
    [InlineData("spotify", "spotify:track:69kOkLUCkxIZYexIgSG8rq", true)]
    [InlineData("spotify", "spotify:episode:69kOkLUCkxIZYexIgSG8rq", false)]
    [InlineData("spotify", "spotify:track:69kOkLUCkxIZYexIgSG8r", false)]
    [InlineData("spotify", "ytm:track:dQw4w9WgXcQ", false)]
    [InlineData("youtube", "ytm:track:dQw4w9WgXcQ", true)]
    [InlineData("youtube", "ytm:track:dQw4w9WgXc", false)]
    [InlineData("youtube", "ytm:playlist:dQw4w9WgXcQ", false)]
    [InlineData("apple", "am:songs:1440833098", true)]
    [InlineData("apple", "am:library-songs:i.abc", false)]
    [InlineData("apple", "am:songs:1440833098 ", false)]
    [InlineData("apple", "javascript:alert(1)", false)]
    public void PassesOnlyATrackIdOfItsOwnService(string service, string id, bool kept)
    {
        var keys = F.Derive(Secret);
        var json = """{"v":1,"from":"12","n":1,"song":{"title":"T","artists":["A"],"pos":0,"playing":true,"service":"SERVICE","id":"TRACK"}}"""
            .Replace("SERVICE", service, StringComparison.Ordinal).Replace("TRACK", id, StringComparison.Ordinal);
        var song = F.Open(keys, SealRaw(keys, json))?.Song;
        Assert.NotNull(song);
        Assert.Equal(kept ? id : null, song.Id);
    }

    [Fact]
    public void ReadsTheRelaysFramesBigEndian()
    {
        var message = F.ParseFrame([1, 0x01, 0x02, 0x03, 0x04, 0x00, 0x00, 0x01, 0x00, 9, 8]);
        Assert.Equal(new F.Frame(false, 0x01020304, 256, message!.Payload), message);
        Assert.Equal([9, 8], message.Payload);
        Assert.Equal(new F.Frame(true, 0xA0B0C0D0, 0, []).Conn, F.ParseFrame([2, 0xA0, 0xB0, 0xC0, 0xD0])!.Conn);
        Assert.True(F.ParseFrame([2, 0xA0, 0xB0, 0xC0, 0xD0])!.Gone);
        Assert.Null(F.ParseFrame([1, 0, 0, 0, 1, 0, 0, 0, 0]));
        Assert.Null(F.ParseFrame([2, 0, 0, 0]));
        Assert.Null(F.ParseFrame([3, 0, 0, 0, 1]));
        Assert.Null(F.ParseFrame([]));
    }

    [Theory]
    [InlineData("spotify", "spotify:track:69kOkLUCkxIZYexIgSG8rq", "spotify:track:69kOkLUCkxIZYexIgSG8rq")]
    [InlineData("spotify", "spotify:local:a:b:c:1", null)]
    [InlineData("youtube", "dQw4w9WgXcQ", "ytm:track:dQw4w9WgXcQ")]
    [InlineData("youtube", "ad:Some advert", null)]
    [InlineData("apple", "1440833098", "am:songs:1440833098")]
    [InlineData("apple", "i.abcdef", null)]
    [InlineData("apple", null, null)]
    public void TakesTheMusicRoomsIdsInTheRelaysForm(string service, string? pageId, string? id)
    {
        var song = DiscordSession.CallSongOf(service, "playing", "T", "A", 1000, 200_000, pageId);
        Assert.NotNull(song);
        Assert.Equal(id, song.Id);
    }

    [Fact]
    public void SendsNoSongForNothingPlaying()
    {
        Assert.Null(DiscordSession.CallSongOf("spotify", "none", "T", "A", 0, 1, null));
        Assert.Null(DiscordSession.CallSongOf("spotify", "stopped", "T", "A", 0, 1, null));
        Assert.Null(DiscordSession.CallSongOf("spotify", "playing", " ", "A", 0, 1, null));
        Assert.Null(DiscordSession.CallSongOf("tidal", "playing", "T", "A", 0, 1, null));
        var paused = DiscordSession.CallSongOf("apple", "paused", "T", "A", 500_000, 200_000, null);
        Assert.False(paused!.Playing);
        Assert.Equal(200_000, paused.PositionMs);
    }

    // A message as another Exo (or someone in the call writing their own) could seal it.
    private static byte[] SealRaw(F.Keys keys, string json)
    {
        var plain = Encoding.UTF8.GetBytes(json);
        var output = new byte[1 + 12 + plain.Length + 16];
        output[0] = 1;
        System.Security.Cryptography.RandomNumberGenerator.Fill(output.AsSpan(1, 12));
        using var aes = new System.Security.Cryptography.AesGcm(keys.Key, 16);
        aes.Encrypt(output.AsSpan(1, 12), plain, output.AsSpan(13, plain.Length), output.AsSpan(13 + plain.Length), [.. "exo-call-song/1"u8, .. Convert.FromHexString(keys.Topic)]);
        return output;
    }
}
