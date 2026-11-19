using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// AMD's unreleased Fluid Motion Frames 3 switch, on a fake registry: no Radeon has run it, and this PC is an RTX 3070. The ways it could go wrong silently:
/// - the switch shows on other hardware, an older driver or a PC with no AMD Software;
/// - the value is set without a press, during a game, or when it wouldn't read back;
/// - off leaves a value that wasn't there before, or removes one that was;
/// - AMD's release goes unseen, or "FSR 3" or "AFMF 2.1" reads as it;
/// - after the release the switch stays a preview, a value of Exo's stays behind, or someone's own value is taken away.
/// </summary>
public sealed class Afmf3PreviewTests : IDisposable
{
    private const string Rx = "AMD Radeon RX 9070 XT", Driver = "26.10.2";
    private readonly string _folder = Directory.CreateTempSubdirectory("exo-afmf3-").FullName;
    private readonly Fake _store = new();
    private bool _playing;

    public void Dispose() => DeletionGate.DeleteDirectory(_folder, recursive: true);

    private Afmf3Preview Make() => new(_store, Path.Combine(_folder, "afmf3.json"), () => _playing);

    private sealed class Fake : Afmf3Preview.IStore
    {
        public bool Key = true;
        public int? Value;
        public int Writes;
        public bool Deaf;
        public bool Installed() => Key;
        public int? Read() => Value;

        public void Write(int? value)
        {
            Writes++;
            if (!Deaf) Value = value;
        }
    }

    [Theory]
    [InlineData("AMD Radeon RX 9070 XT", "26.10.2", true)]
    [InlineData("AMD Radeon RX 7800 XT", "26.11.1", true)]
    [InlineData("AMD Radeon RX 7600M XT", "27.1.1", true)]
    [InlineData("AMD Radeon RX 9070 XT", "26.10.1", false)]
    [InlineData("AMD Radeon RX 9070 XT", "26.9.3", false)]
    [InlineData("AMD Radeon RX 6600", "26.10.2", false)]
    [InlineData("AMD Radeon 780M Graphics", "26.10.2", false)]
    [InlineData("AMD Radeon PRO W7900", "26.10.2", false)]
    [InlineData("NVIDIA GeForce RTX 3070", "26.10.2", false)]
    [InlineData("Intel Arc B580", "26.10.2", false)]
    [InlineData("AMD Radeon RX 9070 XT", null, false)]
    [InlineData(null, "26.10.2", false)]
    public void It_shows_only_on_the_cards_and_drivers_it_was_reported_on(string? gpu, string? driver, bool covers) => Assert.Equal(covers, Afmf3Preview.Covers(gpu, driver));

    [Theory]
    [InlineData("AMD Fluid Motion Frames 3 is now available", true)]
    [InlineData("New: AFMF 3 with Enhanced mode", true)]
    [InlineData("AMD Fluid Motion Frames™ 3", true)]
    [InlineData("AMD Fluid Motion Frames&trade; 3", true)]
    [InlineData("AMD Fluid Motion Frames<sup>TM</sup> 3", true)]
    [InlineData("the afmf3dev value", false)]
    [InlineData("AMD Fluid Motion Frames 2.1 and FSR 3", false)]
    [InlineData("AFMF 2 with FSR 3.1", false)]
    [InlineData("AFMF 30 fps", false)]
    [InlineData("Radeon Super Resolution 3", false)]
    public void Only_amds_own_name_for_it_reads_as_released(string page, bool released) => Assert.Equal(released, Afmf3Preview.Released(page));

    [Fact]
    public void Nothing_is_written_until_the_press()
    {
        var state = Make().Read(Rx, Driver, false);
        Assert.True(state.Offer);
        Assert.False(state.On);
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public void With_no_amd_software_it_stays_away()
    {
        _store.Key = false;
        Assert.False(Make().Read(Rx, Driver, false).Offer);
        Assert.NotNull(Make().Set(true, Rx, Driver, false));
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public void On_other_hardware_a_press_writes_nothing()
    {
        Assert.False(Make().Read("NVIDIA GeForce RTX 3070", "581.42", false).Offer);
        Assert.NotNull(Make().Set(true, "NVIDIA GeForce RTX 3070", "581.42", false));
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public void One_press_on_one_press_off_and_what_wasnt_there_is_gone()
    {
        var preview = Make();
        Assert.Null(preview.Set(true, Rx, Driver, false));
        Assert.Equal(1, _store.Value);
        Assert.True(preview.Read(Rx, Driver, false).On);
        Assert.Null(preview.Set(false, Rx, Driver, false));
        Assert.Null(_store.Value);
        Assert.False(preview.Read(Rx, Driver, false).On);
    }

    [Fact]
    public void Off_puts_back_what_was_there()
    {
        _store.Value = 0;
        var preview = Make();
        Assert.Null(preview.Set(true, Rx, Driver, false));
        Assert.Null(preview.Set(false, Rx, Driver, false));
        Assert.Equal(0, _store.Value);
    }

    [Fact]
    public void A_game_refuses_both_ways()
    {
        var preview = Make();
        _playing = true;
        Assert.Contains("game", preview.Set(true, Rx, Driver, false));
        Assert.Equal(0, _store.Writes);
        _playing = false;
        Assert.Null(preview.Set(true, Rx, Driver, false));
        _playing = true;
        Assert.Contains("game", preview.Set(false, Rx, Driver, false));
        Assert.Equal(1, _store.Value);
    }

    [Fact]
    public void A_write_windows_ignored_says_so_and_nothing_is_kept()
    {
        _store.Deaf = true;
        var preview = Make();
        Assert.NotNull(preview.Set(true, Rx, Driver, false));
        _store.Deaf = false;
        Assert.False(preview.Read(Rx, Driver, false).On);
    }

    [Fact]
    public void Once_it_can_go_off_whatever_changed_about_the_driver()
    {
        var preview = Make();
        Assert.Null(preview.Set(true, Rx, Driver, false));
        Assert.True(preview.Read(Rx, "26.9.1", false).Offer);
        Assert.Null(preview.Set(false, Rx, "26.9.1", false));
    }

    [Fact]
    public void A_value_set_by_hand_shows_as_on_and_off_removes_it()
    {
        _store.Value = 1;
        var preview = Make();
        Assert.True(preview.Read(Rx, Driver, false).On);
        Assert.Null(preview.Set(false, Rx, Driver, false));
        Assert.Null(_store.Value);
    }

    [Fact]
    public void Amds_release_ends_the_preview_takes_off_exos_value_and_says_so_once()
    {
        var preview = Make();
        Assert.Null(preview.Set(true, Rx, Driver, false));
        var first = preview.Read(Rx, Driver, true);
        Assert.False(first.Offer);
        Assert.Contains("released", first.Note);
        Assert.Null(_store.Value);
        var later = preview.Read(Rx, Driver, false); // kept, even with AMD's page not answering now
        Assert.False(later.Offer);
        Assert.Null(later.Note);
        Assert.NotNull(preview.Set(true, Rx, Driver, false));
        Assert.Null(_store.Value);
    }

    [Fact]
    public void After_the_release_someones_own_value_stays_theirs()
    {
        _store.Value = 1;
        var state = Make().Read(Rx, Driver, true);
        Assert.False(state.Offer);
        Assert.Null(state.Note);
        Assert.Equal(1, _store.Value);
    }

    [Fact]
    public void A_preview_that_wont_come_off_after_the_release_keeps_its_switch()
    {
        var preview = Make();
        Assert.Null(preview.Set(true, Rx, Driver, false));
        _store.Deaf = true;
        var state = preview.Read(Rx, Driver, true);
        Assert.True(state.Offer);
        Assert.True(state.On);
        Assert.Contains("couldn't", state.Note);
    }
}
