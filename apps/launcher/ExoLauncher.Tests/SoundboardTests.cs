using System.Buffers.Binary;
using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a soundboard sound could play wrong in the call's own sound, where nobody sees why:
/// - an Ogg Opus sound comes out the wrong length (its pre-skip left in, the encoder's padding past its end kept), on one
///   channel only, late or early against its own start, or at the wrong speed;
/// - its OpusHead output gain is ignored, or a sound runs on past the most Exo decodes;
/// - a file cut short, a multichannel one, or one that isn't Opus is decoded into noise instead of refused;
/// - a WAV from Windows' decoder is read from its header or past its data chunk, or as one channel;
/// - a sound id that isn't Discord's goes into the CDN address.
/// </summary>
public sealed class SoundboardTests
{
    // One second of a tone sweeping 200 to 2000 Hz (one place it lines up with itself), encoded as a voice message is
    // (mono Opus in Ogg), and the tone itself.
    private static (byte[] Ogg, float[] Tone) Tone()
    {
        Repo.RequireVoice("opus.dll");
        var tone = new float[Opus.SampleRate];
        for (var i = 0; i < tone.Length; i++)
        {
            var t = (double)i / Opus.SampleRate;
            tone[i] = (float)(0.3 * Math.Sin(2 * Math.PI * (200 * t + 900 * t * t)));
        }
        var encoder = Opus.EncoderCreate(Opus.SampleRate, 1, Opus.ApplicationVoip, out _);
        try
        {
            var packets = new List<byte[]>();
            var data = new byte[1275];
            // One frame more than the tone: the encoder's lookahead comes out in it.
            for (var f = 0; f <= tone.Length / Opus.FrameSamples; f++)
            {
                var frame = new float[Opus.FrameSamples];
                if (f < tone.Length / Opus.FrameSamples) tone.AsSpan(f * Opus.FrameSamples, Opus.FrameSamples).CopyTo(frame);
                packets.Add(data[..Opus.Encode(encoder, frame, data)]);
            }
            return (OggOpus.Write(packets, Opus.Lookahead(encoder), tone.Length, 7), tone);
        }
        finally { Opus.EncoderDestroy(encoder); }
    }

    [Fact]
    public void AnOggOpusSoundComesBackWholeOnBothChannelsInTime()
    {
        var (ogg, tone) = Tone();
        Assert.True(OggOpus.IsOpus(ogg));
        var sound = OggOpus.Read(ogg, 10 * Opus.SampleRate);
        Assert.NotNull(sound);
        // Exactly the tone's length, pre-skip dropped and the padding trimmed, on both channels alike.
        Assert.Equal(tone.Length * 2, sound!.Length);
        for (var i = 0; i < tone.Length; i++) Assert.Equal(sound[2 * i], sound[2 * i + 1]);
        // In time with the tone (a pre-skip left in or a packet out of place is hundreds of samples off; Opus itself moves
        // a phase by a few), at its level.
        var left = Enumerable.Range(0, tone.Length).Select(i => sound[2 * i]).ToArray();
        var best = Enumerable.Range(-960, 1921).MaxBy(lag => Correlate(left, tone, lag));
        Assert.InRange(best, -8, 8);
        Assert.InRange(Correlate(left, tone, best), 0.9, 1.0);
        Assert.InRange(Rms(left[4800..]) / Rms(tone[4800..]), 0.85, 1.15);
    }

    [Fact]
    public void ItsGainIsHeldAndItsLengthIsCapped()
    {
        var (ogg, tone) = Tone();
        // OpusHead's output gain, Q7.8 dB: +6 dB doubles it.
        var louder = (byte[])ogg.Clone();
        var head = 27 + louder[26];
        BinaryPrimitives.WriteInt16LittleEndian(louder.AsSpan(head + 16), 6 * 256);
        var page = louder.AsSpan(0, head + louder.Skip(27).Take(louder[26]).Sum(b => b)).ToArray();
        page.AsSpan(22, 4).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(louder.AsSpan(22), OggOpus.Crc(page));
        var plain = OggOpus.Read(ogg, Opus.SampleRate)!;
        var loud = OggOpus.Read(louder, Opus.SampleRate)!;
        Assert.InRange(Rms(loud) / Rms(plain), 1.95, 2.05);
        // At most what Exo decodes: half a second of the second-long tone.
        Assert.Equal(Opus.SampleRate, OggOpus.Read(ogg, Opus.SampleRate / 2)!.Length);
        Assert.Equal(tone.Length * 2, plain.Length);
    }

    [Fact]
    public void WhatItCantPlayIsRefused()
    {
        var (ogg, _) = Tone();
        Assert.Null(OggOpus.Read(ogg[..^10], Opus.SampleRate)); // a page cut short
        var head = 27 + ogg[26];
        var surround = (byte[])ogg.Clone();
        surround[head + 9] = 6;
        Assert.Null(OggOpus.Read(surround, Opus.SampleRate));
        var mapped = (byte[])ogg.Clone();
        mapped[head + 18] = 1;
        Assert.Null(OggOpus.Read(mapped, Opus.SampleRate));
        var vorbis = (byte[])ogg.Clone();
        "\u0001vorbis"u8.CopyTo(vorbis.AsSpan(head));
        Assert.False(OggOpus.IsOpus(vorbis));
        Assert.False(OggOpus.IsOpus("ID3\u0004\0\0\0\0\0\0"u8));
        Assert.False(OggOpus.IsOpus([]));
    }

    [Fact]
    public void AWindowsWavIsReadFromItsDataChunk()
    {
        // RIFF, fmt, a LIST chunk of odd length (padded), then two stereo frames.
        using var wav = new MemoryStream();
        void Chunk(string id, byte[] body)
        {
            wav.Write(System.Text.Encoding.ASCII.GetBytes(id));
            wav.Write(BitConverter.GetBytes(body.Length));
            wav.Write(body);
            if (body.Length % 2 == 1) wav.WriteByte(0);
        }
        wav.Write("RIFF\0\0\0\0WAVE"u8);
        Chunk("fmt ", new byte[16]);
        Chunk("LIST", new byte[5]);
        Chunk("data", [.. BitConverter.GetBytes((short)16384), .. BitConverter.GetBytes((short)-16384), .. BitConverter.GetBytes(short.MaxValue), .. BitConverter.GetBytes((short)0)]);
        var samples = Soundboard.Pcm16(wav.ToArray());
        Assert.NotNull(samples);
        Assert.Equal([0.5f, -0.5f, 32767 / 32768f, 0f], samples);
        Assert.Null(Soundboard.Pcm16("RIFF\0\0\0\0WAVE"u8));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("1234567890123456789", true)]
    [InlineData("", false)]
    [InlineData("12a", false)]
    [InlineData("../1", false)]
    [InlineData("123456789012345678901", false)]
    public void OnlyDiscordsSoundIdsAreFetched(string id, bool fetched) => Assert.Equal(fetched, Soundboard.IsId(id));

    private static double Correlate(float[] a, float[] b, int lag)
    {
        double ab = 0, aa = 0, bb = 0;
        for (var i = 4800; i < b.Length - 4800; i++)
        {
            var x = a[i - lag];
            ab += x * b[i]; aa += x * x; bb += b[i] * b[i];
        }
        return ab / Math.Sqrt(aa * bb + 1e-20);
    }

    private static double Rms(float[] x) => Math.Sqrt(x.Sum(v => (double)v * v) / x.Length);
}
