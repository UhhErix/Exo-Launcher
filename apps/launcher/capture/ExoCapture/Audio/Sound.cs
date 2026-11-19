using ExoCapture.Encode;
using ExoCapture.Native;

namespace ExoCapture.Audio;

/// <summary>
/// One source's samples laid out by when they were heard: a ring indexed by the sample's place on the session's clock
/// (QPC), so sources that start late, pause (loopback sends nothing during silence) or drift a little still line up
/// with the picture and each other. A packet within 20 ms of where the last one ended joins it seamlessly.
/// </summary>
internal sealed class SoundRing
{
    internal const int Rate = 48000, Channels = 2;
    private readonly float[] _samples;
    private readonly int _frames;
    private readonly long _epoch;
    private long _next = long.MinValue;
    private long _readCursor;
    private readonly Lock _gate = new();

    internal SoundRing(long epoch, int seconds = 4)
    {
        _epoch = epoch;
        _frames = Rate * seconds;
        _samples = new float[_frames * Channels];
    }

    internal long PositionOf(long time) => (long)((Int128)(time - _epoch) * Rate / 10_000_000);

    internal void Write(ReadOnlySpan<float> interleaved, int frames, long time)
    {
        lock (_gate)
        {
            var at = PositionOf(time);
            if (_next != long.MinValue && Math.Abs(at - _next) < Rate / 50) at = _next;
            _next = at + frames;
            for (var i = 0; i < frames; i++)
            {
                var p = at + i;
                if (p < _readCursor || p >= _readCursor + _frames) continue; // too late, or too far ahead
                var slot = (int)(p % _frames) * Channels;
                _samples[slot] += interleaved[i * Channels];
                _samples[slot + 1] += interleaved[i * Channels + 1];
            }
        }
    }

    /// <summary>Takes <paramref name="frames"/> frames from <paramref name="position"/> into <paramref name="into"/> (adding), leaving silence behind.</summary>
    internal void Take(long position, int frames, float[] into, float gain = 1f)
    {
        lock (_gate)
        {
            for (var i = 0; i < frames; i++)
            {
                var p = position + i;
                if (p < 0) continue;
                var slot = (int)(p % _frames) * Channels;
                into[i * 2] += _samples[slot] * gain;
                into[i * 2 + 1] += _samples[slot + 1] * gain;
                _samples[slot] = _samples[slot + 1] = 0;
            }
            _readCursor = Math.Max(_readCursor, position + frames);
        }
    }
}

/// <summary>The four sound tracks of a clip, in order.</summary>
internal static class Tracks
{
    internal const int Mix = 0, Game = 1, Voice = 2, Mic = 3, Count = 4;
    internal static readonly string[] Names = ["Mix", "Game", "Voice", "Microphone"];
}

/// <summary>
/// The session's sound: the game (or the desktop), the call's voices and the microphone, each its own AAC track, and
/// their mix as the first track. Every 1024 samples of each go to the AAC encoder about 150 ms after they were heard,
/// so late packets still land in place. A source that is missing is a silent track, never a failed clip.
/// </summary>
internal sealed class SoundMixer : IDisposable
{
    internal delegate void AacHandler(int track, byte[] frame, long time);

    private readonly long _epoch;
    private readonly SoundRing[] _rings;
    private readonly AacCodec[] _encoders;
    private readonly AacHandler _output;
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _cursor;
    private readonly WasapiCapture?[] _captures = new WasapiCapture?[Tracks.Count];
    private readonly Lock _gate = new();
    private volatile bool _voiceInMix = true;
    private volatile bool _fed;
    private readonly Lock _fedGate = new();
    private readonly List<(long From, long To)> _fedSpans = [];

    /// <summary>The last second's peak of each track (0..1), for the state Exo shows.</summary>
    internal readonly float[] Peaks = new float[Tracks.Count];

    internal SoundMixer(long epoch, AacHandler output)
    {
        _epoch = epoch;
        _output = output;
        _rings = [.. Enumerable.Range(0, Tracks.Count).Select(_ => new SoundRing(epoch))];
        _encoders = [.. Enumerable.Range(0, Tracks.Count).Select(_ => new AacCodec(encoder: true))];
        _cursor = _rings[0].PositionOf(Win32.Now());
        _thread = new Thread(Run) { IsBackground = true, Name = "Exo Capture mixer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Points a track at a source (or none, for silence). The mix track takes no source of its own.</summary>
    internal void Use(int track, SoundSource? source, uint pid = 0, string? device = null)
    {
        WasapiCapture? old;
        lock (_gate) { old = _captures[track]; _captures[track] = null; }
        old?.Dispose();
        if (source is not { } s) return;
        if (track == Tracks.Mic && _fed) return;
        var ring = _rings[track];
        var capture = WasapiCapture.Open(s, pid, device, (data, frames, time) => ring.Write(data, frames, time), Tracks.Names[track]);
        lock (_gate) _captures[track] = capture;
    }

    /// <summary>Whether the call's voices go into the mix (Record the call's voices).</summary>
    internal bool VoiceInMix { set => _voiceInMix = value; }

    /// <summary>Exo's cleaned voice takes the microphone track (voice.feed): the add-on's own microphone closes.</summary>
    internal void Feed(ReadOnlySpan<float> interleaved, int frames, long time)
    {
        if (!_fed)
        {
            _fed = true;
            Use(Tracks.Mic, null);
            lock (_fedGate) _fedSpans.Add((time, time));
        }
        lock (_fedGate) if (_fedSpans.Count > 0) _fedSpans[^1] = (_fedSpans[^1].From, time + frames * 10_000_000L / SoundRing.Rate);
        _rings[Tracks.Mic].Write(interleaved, frames, time);
    }

    /// <summary>The feed ended: the add-on's own microphone again.</summary>
    internal void FeedEnded(string? micDevice)
    {
        if (!_fed) return;
        _fed = false;
        Use(Tracks.Mic, SoundSource.Microphone, 0, micDevice);
    }

    internal bool Fed => _fed;

    /// <summary>The times (100 ns, session clock) when the microphone track held Exo's cleaned voice.</summary>
    internal List<(long From, long To)> FedSpans()
    {
        lock (_fedGate) return [.. _fedSpans];
    }

    private void Run()
    {
        var frame = new float[AacCodec.FrameSamples * 2];
        var mix = new float[AacCodec.FrameSamples * 2];
        var pcm = new byte[AacCodec.FrameSamples * 4];
        var count = new long[Tracks.Count];
        const int lagFrames = SoundRing.Rate * 150 / 1000;
        try
        {
            while (!_stop)
            {
                Thread.Sleep(10);
                var now = _rings[0].PositionOf(Win32.Now());
                while (_cursor + AacCodec.FrameSamples <= now - lagFrames)
                {
                    var time = _epoch + (long)((Int128)_cursor * 10_000_000 / SoundRing.Rate);
                    Array.Clear(mix);
                    for (var t = 1; t < Tracks.Count; t++)
                    {
                        Array.Clear(frame);
                        _rings[t].Take(_cursor, AacCodec.FrameSamples, frame);
                        if (t != Tracks.Voice || _voiceInMix)
                            for (var i = 0; i < frame.Length; i++) mix[i] += frame[i];
                        Encode(t, frame, pcm, time, count);
                    }
                    Encode(Tracks.Mix, mix, pcm, time, count);
                    _cursor += AacCodec.FrameSamples;
                }
            }
        }
        catch (Exception ex) { Host.Log("Sound encode: " + ex.Message); }
    }

    private void Encode(int track, float[] samples, byte[] pcm, long time, long[] count)
    {
        var peak = 0f;
        for (var i = 0; i < samples.Length; i++)
        {
            var v = Math.Clamp(samples[i], -1f, 1f);
            peak = Math.Max(peak, Math.Abs(v));
            var s = (short)(v * 32767f);
            pcm[i * 2] = (byte)s;
            pcm[i * 2 + 1] = (byte)(s >> 8);
        }
        Peaks[track] = Math.Max(peak, Peaks[track] * 0.98f);
        foreach (var aac in _encoders[track].Process(pcm))
        {
            _output(track, aac, time);
            count[track]++;
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
        for (var t = 0; t < Tracks.Count; t++) Use(t, null);
        foreach (var e in _encoders) e.Dispose();
    }
}
