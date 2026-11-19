using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// YouTube Music's synced lyrics from LRCLIB. Ways it could fail silently: an LRC time read wrong (centiseconds taken
/// as milliseconds, a line sung twice kept once, tag lines shown as words, [offset] ignored or the wrong way), so every
/// line lights early or late; an instrumental shown with stale text; LRCLIB's unsynced text replacing the page's own;
/// a title with &amp; or # cutting the request short; a page that didn't read the song's length still sending a lookup
/// that can't match; the request carrying more than title, artists, album and length.
/// </summary>
public sealed class SyncedLyricsTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void LrcTimesAreMillisecondsFromTheSongsStart()
    {
        var lines = SyncedLyrics.ParseLrc("[00:13.13] Yeah\n[01:02.345] Long\n[02:05] Whole\n[00:01:50] Colon");
        Assert.Equal([1500, 13130, 62345, 125000], lines.Select(l => l.Start));
        Assert.Equal(["Colon", "Yeah", "Long", "Whole"], lines.Select(l => l.Text));
    }

    [Fact]
    public void ALineSungTwiceIsKeptAtBothTimesInOrder()
    {
        var lines = SyncedLyrics.ParseLrc("[00:40.00][00:10.00] Chorus\r\n[00:20.00] Verse\r\n");
        Assert.Equal([(10000, "Chorus"), (20000, "Verse"), (40000, "Chorus")], lines.Select(l => (l.Start, l.Text)));
    }

    [Fact]
    public void TagLinesAreSkippedAndOffsetMovesLinesEarlier()
    {
        var lines = SyncedLyrics.ParseLrc("[ar:Someone]\n[ti:Song]\n[length: 03:20]\n[offset:+500]\n[00:10.00] One\n[00:00.20] Zero");
        Assert.Equal([(0, "Zero"), (9500, "One")], lines.Select(l => (l.Start, l.Text)));
        Assert.Equal(10500, SyncedLyrics.ParseLrc("[offset:-500]\n[00:10.00] One")[0].Start);
    }

    [Fact]
    public void AWaitWithNothingSungIsAnEmptyLine()
    {
        var lines = SyncedLyrics.ParseLrc("[00:16.56] ♪\n[00:20.00]\n[00:21.00] ♪ la la ♪");
        Assert.Equal(["", "", "♪ la la ♪"], lines.Select(l => l.Text));
    }

    [Fact]
    public void ATimeOnlyCountsAtTheStartOfALine()
    {
        var lines = SyncedLyrics.ParseLrc("words [00:10.00] more\n[00:99.00] bad seconds\nno time at all");
        Assert.Empty(lines);
    }

    [Fact]
    public void NeverMoreThanTheLinesThePageCarries()
    {
        var lrc = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"[{i / 60:00}:{i % 60:00}.00] line {i}"));
        Assert.Equal(400, SyncedLyrics.ParseLrc(lrc).Count);
    }

    [Fact]
    public void SyncedLyricsWinOverPlainOnes()
    {
        var found = SyncedLyrics.FromAnswer(Json("""{"instrumental":false,"plainLyrics":"One","syncedLyrics":"[00:01.00] One"}"""));
        Assert.True(found.Synced);
        Assert.Equal(1000, Assert.Single(found.Lines).Start);
    }

    [Fact]
    public void PlainLyricsAreUnsyncedWithoutTrailingBlankLines()
    {
        var found = SyncedLyrics.FromAnswer(Json("""{"instrumental":false,"plainLyrics":"One\r\n\r\nTwo\n\n","syncedLyrics":null}"""));
        Assert.False(found.Synced);
        Assert.Equal(["One", "", "Two"], found.Lines.Select(l => l.Text));
        Assert.All(found.Lines, line => Assert.Equal(0, line.Start));
    }

    [Fact]
    public void AnInstrumentalOrAnEmptyAnswerHasNoLines()
    {
        Assert.Empty(SyncedLyrics.FromAnswer(Json("""{"instrumental":true,"plainLyrics":"Old words","syncedLyrics":"[00:01.00] Old"}""")).Lines);
        Assert.Empty(SyncedLyrics.FromAnswer(Json("""{"instrumental":false,"plainLyrics":"","syncedLyrics":""}""")).Lines);
        Assert.Empty(SyncedLyrics.FromAnswer(Json("""[]""")).Lines);
    }

    [Fact]
    public void TheSongIsReadFromThePagesAnswer()
    {
        var song = SyncedLyrics.SongOf(Json("""{"lines":[],"song":{"title":" STAY ","artists":["The Kid LAROI","Justin Bieber",""],"album":"F*CK LOVE 3","durationMs":141600}}"""));
        Assert.Equal(new SyncedLyrics.Song("STAY", "The Kid LAROI, Justin Bieber", "F*CK LOVE 3", 142), song);
    }

    [Theory]
    [InlineData("""{"lines":[]}""")]
    [InlineData("""{"song":null}""")]
    [InlineData("""{"song":{"title":"Song","artists":["A"],"album":"","durationMs":0}}""")]
    [InlineData("""{"song":{"title":"","artists":["A"],"album":"","durationMs":200000}}""")]
    [InlineData("""{"song":{"title":"Song","artists":[],"album":"","durationMs":200000}}""")]
    public void NoLookupWithoutATitleArtistAndLength(string page) => Assert.Null(SyncedLyrics.SongOf(Json(page)));

    [Fact]
    public void TheRequestCarriesOnlyTheSongsFactsEscaped()
    {
        var uri = new Uri(SyncedLyrics.RequestUri(new("Rock & Roll #1", "AC/DC, Beyoncé", "", 200)));
        Assert.Equal("lrclib.net", uri.Host);
        Assert.Equal("/api/get", uri.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal(["track_name", "artist_name", "duration"], query.AllKeys);
        Assert.Equal("Rock & Roll #1", query["track_name"]);
        Assert.Equal("AC/DC, Beyoncé", query["artist_name"]);
        Assert.Equal("200", query["duration"]);
        Assert.Contains("&album_name=After%20Hours&", SyncedLyrics.RequestUri(new("Blinding Lights", "The Weeknd", "After Hours", 200)));
    }

    [Fact]
    public void OnlySyncedLyricsReplaceThePagesOwn()
    {
        static SyncedLyrics.Lyrics Found(bool synced) => new([new(synced ? 1000 : 0, "Line")], synced);
        var timed = Found(true);
        Assert.Same(timed, SyncedLyrics.Better(timed, pageHasLines: true));
        Assert.Null(SyncedLyrics.Better(Found(false), pageHasLines: true));
        Assert.NotNull(SyncedLyrics.Better(Found(false), pageHasLines: false));
        Assert.Null(SyncedLyrics.Better(new([], false), pageHasLines: false));
        Assert.Null(SyncedLyrics.Better(null, pageHasLines: false));
    }

    [Fact]
    public void APageOfBlankLinesHasNoLyrics()
    {
        Assert.False(SyncedLyrics.HasLines(Json("""{"lines":[{"start":0,"text":""},{"start":0,"text":"  "}]}""")));
        Assert.True(SyncedLyrics.HasLines(Json("""{"lines":[{"start":0,"text":""},{"start":0,"text":"Words"}]}""")));
        Assert.False(SyncedLyrics.HasLines(Json("""{"synced":false}""")));
    }
}
