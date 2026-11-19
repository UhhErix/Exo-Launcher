using System.Text.Json;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// The page's 30-second report of a watched stream on screen, as app.log's line: a number from the page that is huge,
/// negative, fractional where a count is meant, not a number or missing reaching the log as written; a word other than
/// the two known ones for the codec or decoder reaching it at all; the game in front said wrong (none running, one
/// running behind, one in front).
/// </summary>
public sealed class WatchedStreamDrawnTests
{
    private static string Line(string json, string? game)
    {
        using var doc = JsonDocument.Parse(json);
        return WebHostBridge.StreamDrawnLine(doc.RootElement, game);
    }

    [Fact]
    public void A_report_reads_as_the_page_counted_it()
    {
        var line = Line("""
            {"received":1802,"decoded":1800,"presented":1790,"dropped":10,"waiting":2,"errors":0,"late":12,
             "decodeMs":3.14,"decodeMax":9.8,"queueMax":3,"heldMs":28.2,"heldMax":61,"depthMax":4,"leadMs":24.5,
             "gapMs":16.7,"gapMax":66.6,"seconds":30,"codec":"h265","decoder":"hardware"}
            """, "Phantom Blade Zero");
        Assert.Equal("Discord video: the watched stream on screen (30 s): 1802 came, 1800 decoded, 1790 shown; 10 dropped, " +
            "2 skipped waiting for a key frame, 0 decoder errors, 12 shown late; decoding 3.1 ms (9.8 max, 3 queued at most), hardware H.265; " +
            "held 28.2 ms from arrival to its time (61.0 max, 4 pictures at most, aiming 24.5 ms past the earliest); " +
            "a picture every 16.7 ms (66.6 max); Phantom Blade Zero in front.", line);
    }

    [Fact]
    public void Numbers_out_of_bounds_are_clamped_and_words_are_one_of_two()
    {
        var line = Line("""
            {"received":-5,"decoded":99999999,"presented":"12","late":1.5,"decodeMs":-3,"decodeMax":1e12,"heldMs":"NaN",
             "seconds":99999,"codec":"vp9<script>","decoder":"software"}
            """, null);
        Assert.Contains("(3600 s): 0 came, 100000 decoded, 0 shown;", line);
        Assert.Contains("0 shown late; decoding 0.0 ms (60000.0 max, 0 queued at most), hardware H.264;", line);
        Assert.Contains("held 0.0 ms from arrival", line);
        Assert.DoesNotContain("vp9", line);
        Assert.DoesNotContain("script", line);
        Assert.EndsWith("no game running.", line);
    }

    [Fact]
    public void A_decoder_the_browser_picked_and_a_game_behind_are_said()
    {
        var line = Line("""{"codec":"h264","decoder":"any"}""", "");
        Assert.Contains("H.264 on whatever decoder the browser picks (hardware failed before)", line);
        Assert.EndsWith("a game running, not in front.", line);
    }
}
