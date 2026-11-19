using System.Globalization;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Exo EQ's text for Equalizer APO (ExoEqConfig). Ways it could fail: a decimal comma under another culture; a block for
/// one output leaks onto the rest because Device and Channel aren't put back; the preamp guard reads one filter's gain and
/// misses two boosts side by side, or leaves headroom on a chain that only cuts; Exo's Include lands twice, or its removal
/// takes a line of the player's, turns CRLF into LF, or loses a last line without a newline; Lucid's block still plays
/// after it's turned off, or Lucid Voice's goes with it; an earlier Exo's block stays, or takes Lucid's block out with it.
/// Ids are made up.
/// </summary>
public sealed class ExoEqConfigTests
{
    private const string Phones = "{11111111-2222-3333-4444-555555555555}";
    private const string Speakers = "{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}";

    [Fact]
    public void The_file_is_written_exactly()
    {
        var text = ExoEqConfig.Text(
        [
            new(Phones, ["Headphones (USB Audio)", "Sennheiser HD 600: correction from AutoEq (MIT), measured by oratory1990"],
                [new(EqKind.LowShelf, 105, 5.5, 0.7), new(EqKind.Peak, 2000, -2.25, 1.41)]),
            new(Speakers, [], []),
        ]);
        Assert.Equal(
            ExoEqConfig.Header + "\r\n" +
            "\r\n" +
            "Device: " + Phones + "\r\n" +
            "Channel: all\r\n" +
            "# Headphones (USB Audio)\r\n" +
            "# Sennheiser HD 600: correction from AutoEq (MIT), measured by oratory1990\r\n" +
            "Preamp: -5.5 dB\r\n" +
            "Filter: ON LSC Fc 105 Hz Gain 5.5 dB Q 0.70\r\n" +
            "Filter: ON PK Fc 2000 Hz Gain -2.3 dB Q 1.41\r\n" +
            "Device: all\r\n" +
            "Channel: all\r\n",
            text);
    }

    [Fact]
    public void Off_writes_no_filter_at_all() =>
        Assert.Equal(ExoEqConfig.Header + "\r\n# Off\r\n",
            ExoEqConfig.Text([new(Phones, [], [new(EqKind.Peak, 100, 6, 1)])], "Off"));

    [Fact]
    public void Numbers_use_a_dot_in_every_culture()
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("Filter: ON HSC Fc 10000.5 Hz Gain -1.5 dB Q 0.71", ExoEqConfig.Line(new(EqKind.HighShelf, 10000.5, -1.5, 0.71)));
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void A_name_cannot_break_out_of_its_comment()
    {
        var text = ExoEqConfig.Text([new(Phones, ["Bad\r\nPreamp: 20 dB"], [new(EqKind.Peak, 100, -1, 1)])]);
        Assert.Contains("# Bad  Preamp: 20 dB\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\nPreamp: 20", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_id_that_is_not_an_endpoint_id_gets_no_block() =>
        Assert.DoesNotContain("Device: all\r\nDevice", ExoEqConfig.Text([new("all", [], [new(EqKind.Peak, 100, 3, 1)])]), StringComparison.Ordinal);

    [Fact]
    public void The_guard_reads_the_whole_curve()
    {
        // Two +3 dB peaks on one frequency reach +6 dB together.
        Assert.Equal(-6.0, ExoEqConfig.Preamp([new(EqKind.Peak, 1000, 3, 1), new(EqKind.Peak, 1000, 3, 1)]));
        // A shelf reaches its full gain.
        Assert.Equal(-4.0, ExoEqConfig.Preamp([new(EqKind.LowShelf, 105, 4, 0.71)]));
        // Only cuts: no headroom taken.
        Assert.Equal(0.0, ExoEqConfig.Preamp([new(EqKind.Peak, 1000, -6, 1), new(EqKind.HighShelf, 8000, -3, 0.71)]));
        Assert.Equal(0.0, ExoEqConfig.Preamp([]));
        // A cut next to a boost lowers the peak: the guard is less than the sum of the boosts.
        var near = ExoEqConfig.Preamp([new(EqKind.Peak, 1000, 4, 2), new(EqKind.Peak, 1100, -4, 2)]);
        Assert.InRange(near, -4.0, -0.1);
    }

    [Fact]
    public void The_curve_matches_the_cookbook_at_its_centre()
    {
        Assert.Equal(6.0, ExoEqConfig.Response([new(EqKind.Peak, 1000, 6, 1)], 1000), 3);
        Assert.Equal(0.0, ExoEqConfig.Response([new(EqKind.Peak, 1000, 6, 1)], 20), 1);
        Assert.Equal(-3.0, ExoEqConfig.Response([new(EqKind.HighShelf, 1000, -6, 0.71)], 1000), 1);
    }

    [Fact]
    public void Filters_are_kept_inside_their_limits()
    {
        var f = ExoEqConfig.Clamp(new(EqKind.Peak, 5, 40, 0));
        Assert.Equal((20.0, 20.0, 0.1), (f.Hz, f.Gain, f.Q));
        Assert.Equal(1000, ExoEqConfig.Clamp(new(EqKind.Peak, double.NaN, 0, 1)).Hz);
    }

    [Fact]
    public void AutoEq_and_Lucid_files_are_read()
    {
        var filters = ExoEqConfig.Parse("""
            Preamp: -5.9 dB
            Filter 1: ON LSC Fc 105 Hz Gain 0.6 dB Q 0.70
            Filter 2: ON PK Fc 127 Hz Gain -3.6 dB Q 0.21
            Filter: ON HS Fc 16000 Hz Gain +0.5 dB Q 0.50
            Filter 3: OFF PK Fc 1000 Hz Gain 9 dB Q 1
            Filter 4: ON LP Fc 1000 Hz
            VSTPlugin: Library "x.dll"
            """);
        Assert.Equal([new(EqKind.LowShelf, 105, 0.6, 0.7), new(EqKind.Peak, 127, -3.6, 0.21), new(EqKind.HighShelf, 16000, 0.5, 0.5)], filters);
    }

    private const string Config = "Preamp: -6 dB\r\nInclude: example.txt\r\nGraphicEQ: 25 0; 40 0\r\n";

    [Fact]
    public void The_include_goes_in_once_at_the_end_with_the_files_line_breaks()
    {
        var once = ExoEqConfig.WithInclude(Config);
        Assert.Equal(Config + "Include: exo.txt\r\n", once);
        Assert.Equal(once, ExoEqConfig.WithInclude(once));
        Assert.Equal("a\nInclude: exo.txt\n", ExoEqConfig.WithInclude("a\n"));
        Assert.Equal("a\r\nInclude: exo.txt\r\n", ExoEqConfig.WithInclude("a"));
        Assert.Equal("Include: exo.txt\r\n", ExoEqConfig.WithInclude(""));
        // Written another way by hand, it still counts as Exo's.
        Assert.Equal("include:  \"EXO.TXT\" \r\n", ExoEqConfig.WithInclude("include:  \"EXO.TXT\" \r\n"));
    }

    [Fact]
    public void Removing_the_include_keeps_every_other_byte()
    {
        Assert.Equal(Config, ExoEqConfig.WithoutInclude(ExoEqConfig.WithInclude(Config)));
        const string mixed = "a\r\nInclude: exo.txt\nb\r\nInclude: exo-eq.txt\r\nlast";
        Assert.Equal("a\r\nb\r\nInclude: exo-eq.txt\r\nlast", ExoEqConfig.WithoutInclude(mixed));
        Assert.Equal(mixed.Replace("Include: exo.txt\n", "", StringComparison.Ordinal), ExoEqConfig.WithoutInclude(ExoEqConfig.WithoutInclude(mixed)));
    }

    private const string Lucid = "# Equalizer APO Configuration\r\n\r\n# Lucid EQ begin\r\nDevice: " + Phones + "\r\nInclude: lucid_temp.txt\r\nDevice: all\r\n# Lucid EQ end\r\n\r\n" +
        "# Lucid Voice begin\r\nDevice: " + Speakers + "\r\nInclude: lucid_voice.txt\r\nDevice: all\r\n# Lucid Voice end\r\n";

    [Fact]
    public void Lucids_eq_is_turned_off_and_its_voice_block_stays()
    {
        Assert.True(ExoEqConfig.LucidActive(Lucid));
        var off = ExoEqConfig.LucidOff(Lucid);
        Assert.False(ExoEqConfig.LucidActive(off));
        Assert.Contains("# Exo EQ took over: Include: lucid_temp.txt\r\n", off, StringComparison.Ordinal);
        Assert.Contains("# Lucid Voice begin\r\nDevice: " + Speakers + "\r\nInclude: lucid_voice.txt\r\n", off, StringComparison.Ordinal);
        Assert.Equal(off, ExoEqConfig.LucidOff(off));
        Assert.Equal(Lucid.Length + 3 * ExoEqConfig.LucidOffMark.Length, off.Length);
    }

    [Fact]
    public void An_earlier_Exos_block_goes_and_Lucids_inside_it_stays()
    {
        var config = "# Exo EQ begin (Exo Launcher writes only between this line and the end line)\r\n\r\n" +
            "# Lucid EQ begin\r\nDevice: " + Phones + "\r\nInclude: lucid_temp.txt\r\nDevice: all\r\n# Lucid EQ end\r\n\r\n" +
            "Device: " + Speakers + "\r\nInclude: exo-eq.txt\r\nDevice: all\r\n# Exo EQ end\r\n\r\n# Equalizer APO Configuration\r\n\r\n";
        Assert.Equal(
            "# Lucid EQ begin\r\nDevice: " + Phones + "\r\nInclude: lucid_temp.txt\r\nDevice: all\r\n# Lucid EQ end\r\n\r\n# Equalizer APO Configuration\r\n\r\nInclude: exo.txt\r\n",
            ExoEqConfig.Prepared(config, lucidOff: false));
        Assert.Equal(ExoEqConfig.Prepared(config, lucidOff: false), ExoEqConfig.Prepared(ExoEqConfig.Prepared(config, lucidOff: false), lucidOff: false));
        Assert.False(ExoEqConfig.LucidActive(ExoEqConfig.Prepared(config, lucidOff: true)));
        // A file with no such block is untouched but for the one line.
        Assert.Equal(Config + "Include: exo.txt\r\n", ExoEqConfig.Prepared(Config, lucidOff: false));
    }
}
