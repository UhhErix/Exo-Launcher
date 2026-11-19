using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// What the audio page may ask of a device. The ways this could go wrong, each silent in the app:
/// - a volume that isn't a number, or past 100, written to Windows as something else (a NaN from the page, 1.5 as a level);
/// - a format's block size or byte rate that doesn't follow from it, which a device simply refuses, so a real format looks unsupported;
/// - PCM and float mixed up, so the device format or the mix is wrong without an error;
/// - 24 bits offered twice (in a 24 and a 32-bit container), or a device's current format missing from the list, so the row shows nothing;
/// - a surround device's speakers lost by writing a stereo mask;
/// - a format that didn't change read as one that did.
/// </summary>
public sealed class AudioDetailsTests
{
    [Theory]
    [InlineData(0d, 0)]
    [InlineData(100d, 100)]
    [InlineData(62.4d, 62)]
    [InlineData(-5d, 0)]
    [InlineData(150d, 100)]
    public void AVolumeStaysBetweenZeroAndAHundred(double given, int level) => Assert.Equal(level, AudioDetails.Level(given));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(null)]
    public void WhatIsNoVolumeIsRefused(double? given) => Assert.Null(AudioDetails.Level(given));

    [Fact]
    public void AFormatsSizesFollowFromIt()
    {
        var format = AudioDetails.WaveFormat(48000, 24, 32, 2, 0x3, floating: false);
        Assert.Equal(40, format.Length);
        Assert.Equal(0xFFFE, BitConverter.ToUInt16(format, 0));
        Assert.Equal(2, BitConverter.ToUInt16(format, 2));
        Assert.Equal(48000, BitConverter.ToInt32(format, 4));
        Assert.Equal(48000 * 8, BitConverter.ToInt32(format, 8));
        Assert.Equal(8, BitConverter.ToUInt16(format, 12));
        Assert.Equal(32, BitConverter.ToUInt16(format, 14));
        Assert.Equal(22, BitConverter.ToUInt16(format, 16));
        Assert.Equal(24, BitConverter.ToUInt16(format, 18));
        Assert.Equal(0x3u, BitConverter.ToUInt32(format, 20));
        Assert.Equal(new AudioDetails.Format(48000, 24, 2), AudioDetails.FormatOf(format));
    }

    [Fact]
    public void PcmAndFloatAreTheirOwnSubformats()
    {
        Assert.Equal(1, BitConverter.ToUInt16(AudioDetails.WaveFormat(48000, 24, 24, 2, 3, floating: false), 24));
        Assert.Equal(3, BitConverter.ToUInt16(AudioDetails.WaveFormat(48000, 32, 32, 2, 3, floating: true), 24));
    }

    [Fact]
    public void EachRateAndDepthIsOfferedOnce()
    {
        var offers = AudioDetails.OffersOf(AudioDetails.Candidates(2), offer => offer.Rate == 48000, new AudioDetails.Format(48000, 24, 2), 32);
        Assert.Equal([16, 24, 32], offers.Formats.Select(o => o.Bits));
        Assert.Equal("48000-24-24-2", offers.Current);
    }

    [Fact]
    public void TheFormatItIsOnIsOfferedWhenExclusiveModeAnswersNo()
    {
        var offers = AudioDetails.OffersOf(AudioDetails.Candidates(1), _ => false, new AudioDetails.Format(48000, 16, 1), 16);
        var only = Assert.Single(offers.Formats);
        Assert.Equal("48000-16-16-1", only.Key);
        Assert.Equal(only.Key, offers.Current);
    }

    [Fact]
    public void ANewFormatKeepsItsDevicesSpeakers()
    {
        var surround = AudioDetails.WaveFormat(48000, 24, 24, 6, 0x60F, floating: false);
        Assert.Equal(0x60Fu, AudioDetails.MaskFor(surround, 6));
        Assert.Equal(0x3u, AudioDetails.MaskFor(new byte[18], 2));
        Assert.Equal(0x4u, AudioDetails.MaskFor(AudioDetails.WaveFormat(48000, 16, 16, 2, 3, false), 1));
    }

    [Fact]
    public void OnlyTheFormatAskedForCountsAsTaken()
    {
        var asked = new AudioDetails.Offer("96000-24-24-2", 96000, 24, 24, 2);
        Assert.True(AudioDetails.Took(new AudioDetails.Format(96000, 24, 2), asked));
        Assert.False(AudioDetails.Took(new AudioDetails.Format(48000, 24, 2), asked));
        Assert.False(AudioDetails.Took(null, asked));
    }
}
