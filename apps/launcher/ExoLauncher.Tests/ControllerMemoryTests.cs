using System.Text.Json;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// What Exo remembers about a controller, and the bytes it says to one. The reports here are what a DualSense Edge
/// answered on the owner's PC (read only), with the serial and pairing address left out.
/// </summary>
public sealed class ControllerMemoryTests
{
    private static readonly ushort[] Factory = [494, 697, 592, 652, 3325, 3414, 3471, 3414, 1972, 2086, 2001, 2066];
    private static readonly ushort[] CenterOnly = [494, 697, 592, 652, 3325, 3414, 3471, 3414, 1966, 2065, 1989, 2080];
    private static readonly ushort[] Fit = [451, 692, 537, 662, 3428, 3522, 3501, 3501, 1956, 2111, 2001, 2079];

    [Fact]
    public void ACalibrationThatDoesNotReadBackKeepsWhatWasWrittenNotTheOneBefore()
    {
        // An Edge doesn't answer straight after a write: keeping the earlier one lost every range fit.
        Assert.Equal(Fit, ControllerLab.ToKeep(null, Fit, CenterOnly));
        Assert.Equal(Fit, ControllerLab.ToKeep(Fit, CenterOnly, Factory));
        Assert.Equal(CenterOnly, ControllerLab.ToKeep(null, null, CenterOnly));
    }

    [Fact]
    public void APortsFileAndTheUnitsFileBecomeOneTheNewerWinning()
    {
        // This PC's two files: the unit's older, the port's written while the serial hadn't read.
        var older = new ControllerLab.Saved(Factory, CenterOnly, false, true, new ControllerLab.PadSettings { Light = "off", MicOff = true, Vibration = "off" });
        var newer = new ControllerLab.Saved(Factory, Fit, false, true, new ControllerLab.PadSettings { Light = "#FFFFFF", MicOff = true, Vibration = "off" });
        var merged = ControllerLab.Merged(older, newer);
        Assert.Equal(Fit, merged.Calibrated);
        Assert.Equal(Factory, merged.Original);
        Assert.Equal("#FFFFFF", merged.Settings?.Light);
    }

    [Fact]
    public void MergedKeepsWhatTheNewerDoesNotSay()
    {
        var test = new ControllerLab.StickTest(DateTimeOffset.UnixEpoch, new ControllerLab.Rest(1, 1, 1), null, null, null);
        var older = new ControllerLab.Saved(Factory, Fit, true, true, new ControllerLab.PadSettings { Light = "off" }, DateTimeOffset.UnixEpoch, test);
        var merged = ControllerLab.Merged(older, ControllerLab.Saved.Empty);
        Assert.Equal(older.Calibrated, merged.Calibrated);
        Assert.Equal(older.Original, merged.Original);
        Assert.Equal(older.Settings, merged.Settings);
        Assert.Equal(older.PutBack, merged.PutBack);
        Assert.Equal(older.Tested, merged.Tested);
        Assert.True(merged.Kept);
        Assert.True(merged.Trimmed);
    }

    [Fact]
    public void OnlyThisControllersPortFileIsFolded()
    {
        var mine = new ControllerLab.Saved(Factory, null, false);
        var other = new ControllerLab.Saved([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], Fit, false);
        var settingsOnly = ControllerLab.Saved.Empty with { Settings = new ControllerLab.PadSettings { Light = "off" } };
        Assert.True(ControllerLab.SameController(new ControllerLab.Saved(Factory, Fit, false), ownPort: false, mine, null));
        Assert.False(ControllerLab.SameController(other, ownPort: true, mine, Fit));
        Assert.True(ControllerLab.SameController(settingsOnly, ownPort: true, mine, null));
        Assert.False(ControllerLab.SameController(settingsOnly, ownPort: false, mine, null));
        // No factory values to compare: it must hold the file's values.
        Assert.True(ControllerLab.SameController(other, ownPort: false, ControllerLab.Saved.Empty, Fit));
        Assert.False(ControllerLab.SameController(other, ownPort: false, ControllerLab.Saved.Empty, null));
    }

    [Fact]
    public void WhatIsSavedReadsBackAfterARestart()
    {
        var at = new DateTimeOffset(2026, 11, 3, 6, 22, 0, TimeSpan.Zero);
        var saved = new ControllerLab.Saved(Factory, Fit, false, true,
            new ControllerLab.PadSettings { Light = "#FFFFFF", MicOff = true, SpeakerOff = true, Vibration = "off", TriggerEffects = "off" }, at,
            new ControllerLab.StickTest(at, new ControllerLab.Rest(0.4, -2, 0), new ControllerLab.Rest(0.4, 2, 0), new ControllerLab.Edge(100, 2.2, 1), new ControllerLab.Edge(100, 2.6, 1)));
        var back = JsonSerializer.Deserialize<ControllerLab.Saved>(JsonSerializer.Serialize(saved));
        Assert.NotNull(back);
        Assert.Equal(Fit, back.Calibrated);
        Assert.Equal(saved.Settings, back.Settings);
        Assert.Equal(at, back.PutBack);
        Assert.Equal(saved.Tested, back.Tested);
    }

    [Fact]
    public void AFileFromBeforeTheseFieldsStillReads()
    {
        const string old = """{"Original":[494,697,592,652,3325,3414,3471,3414,1972,2086,2001,2066],"Calibrated":null,"Kept":false,"Trimmed":true,"Settings":{"Light":"off","PlayerLight":false,"MicOff":true,"SpeakerOff":true,"Vibration":"off","TriggerEffects":"off"}}""";
        var back = JsonSerializer.Deserialize<ControllerLab.Saved>(old);
        Assert.Equal("off", back?.Settings?.Light);
        Assert.Null(back?.PutBack);
        Assert.Null(back?.Tested);
    }

    [Fact]
    public void ARestStepStartsATestARolledOneFinishesIt()
    {
        var at = DateTimeOffset.UnixEpoch;
        var rest = new ControllerLab.Rest(1, 2, 1);
        var edge = new ControllerLab.Edge(99, 3, 1);
        var first = ControllerLab.TestWith(new ControllerLab.StickTest(at, null, null, edge, edge), at, false, rest, rest, null, null);
        Assert.Null(first.EdgeLeft);
        var done = ControllerLab.TestWith(first, at, true, null, null, edge, edge);
        Assert.Equal(rest, done.RestLeft);
        Assert.Equal(edge, done.EdgeRight);
    }

    [Fact]
    public void ASerialThatDidNotReadIsAskedAgainSoon()
    {
        Assert.Equal(3, Peripherals.AskAgainAfter(1));
        Assert.Equal(3, Peripherals.AskAgainAfter(5));
        Assert.Equal(60, Peripherals.AskAgainAfter(6));
    }

    [Fact]
    public void ThisEdgesSerialCarriesItsColor() => Assert.Equal("Midnight Black", Peripherals.ColorOf("xxxx01xxxxxxxxxxx"));

    [Fact]
    public void AnEdgesSlotsReadAsItAnswers()
    {
        byte[] first = [0x70, 0x00, 0x01, 0, 0, 0, .. System.Text.Encoding.Unicode.GetBytes("Default Profile"), 0, 0];
        Assert.Equal(new ControllerLab.Slot(1, false, "Default Profile"), ControllerLab.SlotOf(1, first));
        byte[] empty = [0x73, 0x10, 0, 0, 0, 0, 0, 0];
        Assert.Equal(new ControllerLab.Slot(2, true, null), ControllerLab.SlotOf(2, empty));
        Assert.Null(ControllerLab.SlotOf(3, empty));
        Assert.Null(ControllerLab.SlotOf(5, empty));
        Assert.Equal(new ControllerLab.Slot(1, false, null), ControllerLab.SlotOf(1, [0x70, 0, 0, 0, 0, 0, 0x02, 0x00]));
    }

    [Fact]
    public void AnEdgesBackAndFnButtonsReadFromTheirBits()
    {
        var report = new byte[64];
        report[0] = 0x01;
        report[1 + 9] = (byte)(ControllerLab.BackLeft | ControllerLab.FnRight | 0x01);
        Assert.Equal(ControllerLab.BackLeft | ControllerLab.FnRight, ControllerLab.Parse(report, usb: true, edge: true)?.Extras);
        Assert.Equal(0, ControllerLab.Parse(report, usb: true, edge: false)?.Extras);
    }

    // ------------------------------------------------------------------ the bytes: dualshock-tools' ds5-controller.js and ds5-edge-controller.js

    [Fact]
    public void TheCommandsAreTheDocumentedBytes()
    {
        Assert.Equal([0x01, 0x13], ControllerLab.ReadSerial);
        Assert.Equal([0x03, 0x03], ControllerLab.NvStatus);
        Assert.Equal([0x03, 0x01], ControllerLab.NvLock);
        Assert.Equal([3, 2, 101, 50, 64, 12], ControllerLab.NvUnlock);
        Assert.Equal([12, 4], ControllerLab.FinetuneRead(edge: true));
        Assert.Equal([12, 2], ControllerLab.FinetuneRead(edge: false));
        Assert.Equal([1, 1, 1], ControllerLab.CenterBegin);
        Assert.Equal([3, 1, 1], ControllerLab.CenterSample);
        Assert.Equal([2, 1, 1], ControllerLab.CenterEnd);
        Assert.Equal([1, 1, 2], ControllerLab.RangeBegin);
        Assert.Equal([2, 1, 2], ControllerLab.RangeEnd);
    }

    [Fact]
    public void AFineTuneWriteIsItsCommandAndTwelveLittleEndianValues()
    {
        var payload = ControllerLab.FinetunePayload(Fit);
        Assert.Equal(26, payload.Length);
        Assert.Equal([12, 1, 451 & 0xFF, 451 >> 8], payload[..4]);
        Assert.Equal([2079 & 0xFF, 2079 >> 8], payload[24..26]);
        Assert.True(ControllerLab.Allowed(0x80, payload, set: true, edge: true));
    }

    [Theory]
    // An Edge's stick modules: unlock, store, lock. One open project's sequence, and it needs the controller opened.
    [InlineData(0x80, new byte[] { 21, 6, 0, 11 })]
    [InlineData(0x80, new byte[] { 21, 4, 0, 8 })]
    [InlineData(0x80, new byte[] { 21, 5, 0 })]
    // Sony's profile writes: left out until two sources agree on them.
    [InlineData(0x60, new byte[] { 0 })]
    [InlineData(0x6B, new byte[] { 0 })]
    // Firmware, pairing, power.
    [InlineData(0xA0, new byte[] { 0 })]
    [InlineData(0xF0, new byte[] { 0 })]
    public void WhatCouldHarmAnEdgeIsRefused(int id, byte[] payload) => Assert.False(ControllerLab.Allowed((byte)id, payload, set: true, edge: true));

    [Fact]
    public void AnAudioFormatReadsFromWhatWindowsKeeps()
    {
        // WAVEFORMATEXTENSIBLE, 48 kHz stereo, 24 bits in a 32-bit container.
        var extensible = new byte[40];
        BitConverter.TryWriteBytes(extensible.AsSpan(0), (ushort)0xFFFE);
        BitConverter.TryWriteBytes(extensible.AsSpan(2), (ushort)2);
        BitConverter.TryWriteBytes(extensible.AsSpan(4), 48000);
        BitConverter.TryWriteBytes(extensible.AsSpan(14), (ushort)32);
        BitConverter.TryWriteBytes(extensible.AsSpan(18), (ushort)24);
        Assert.Equal(new AudioDetails.Format(48000, 24, 2), AudioDetails.FormatOf(extensible));
        var plain = new byte[18];
        BitConverter.TryWriteBytes(plain.AsSpan(0), (ushort)1);
        BitConverter.TryWriteBytes(plain.AsSpan(2), (ushort)1);
        BitConverter.TryWriteBytes(plain.AsSpan(4), 44100);
        BitConverter.TryWriteBytes(plain.AsSpan(14), (ushort)16);
        Assert.Equal(new AudioDetails.Format(44100, 16, 1), AudioDetails.FormatOf(plain));
        Assert.Null(AudioDetails.FormatOf(new byte[8]));
    }
}
