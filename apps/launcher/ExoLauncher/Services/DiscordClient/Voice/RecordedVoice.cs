using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Your voice for Exo Capture's microphone track, while it records: the chosen microphone through a
/// chain of its own, a call's chain made for a recording (<see cref="VoiceChain.Recording"/>: not gated, so the voice runs
/// on whole instead of being cut between words as a call's gate cuts it, and noise turned down gently so it keeps its
/// tone, and WebRTC's AGC2 brings it to a recording's level). In a call too: a call's frames are gated for Discord, which a
/// recording heard as choppy. While a call runs, the recording or replay takes the call's own chain's voice from before its gate
/// (<see cref="VoiceChain.Cleaned"/>) rather than run a second noise suppression on the same microphone, which doubled
/// the voice's CPU (28 % of a core each, beside a game). A replay only being kept takes none of this: the add-on holds the
/// microphone raw and <see cref="ClipVoice"/> cleans what a save keeps, so nothing of Exo's runs for sound nobody may ever
/// save (it was a fifth of a core at top priority, beside a game, all the time). Each 20 ms frame goes as 16-bit stereo,
/// stamped with when its sound was captured (the chain's delay taken off, so it stays in step with the picture), down an
/// anonymous pipe only the two processes hold (<see cref="CaptureAddon.ShareHandle"/>). The voice thread never waits on
/// it: a frame the add-on can't take in time (half a second queued) is dropped.
/// </summary>
internal static class RecordedVoice
{
    // Frame packet: when (performance counter, 100 ns), sample frames, channels; then the samples.
    private const int HeaderBytes = 16, Channels = Opus.Channels;
    private const int PacketBytes = HeaderBytes + Opus.FrameSamples * Channels * sizeof(short);

    private static readonly Lock Gate = new();
    private static bool _wanted, _ownChain;
    private static CaptureAddon? _addon;
    private static Channel<byte[]>? _frames;
    private static AnonymousPipeServerStream? _pipe;
    private static VoiceAudio? _audio;
    private static VoiceChain? _chain;
    private static (VoiceChain Chain, Func<long> FrameAt)? _call; // the call's chain, while one captures
    private static (VoiceChain Chain, Action<float[], bool, int> Tap)? _tapped; // the call's chain this recording takes its voice from

    /// <summary>A call's chain started capturing the microphone: a recording or replay takes its voice from it from now on.</summary>
    internal static void Call(VoiceChain chain, Func<long> frameAt)
    {
        lock (Gate)
        {
            Untap();
            _call = (chain, frameAt);
            Apply();
        }
    }

    /// <summary>That call's chain is going: a recording cleans the microphone itself again, and a replay holds it raw again.</summary>
    internal static void Ended(VoiceChain chain)
    {
        lock (Gate)
        {
            if (_call?.Chain != chain) return;
            _call = null;
            Untap();
            Apply();
        }
    }

    /// <summary>Whether Exo can clean a clip when it is saved: its noise suppression is here, or isn't asked for. Windows' own stand-in only works live.</summary>
    internal static bool CanCleanLater => !VoiceProcessing.Current.NoiseSuppression || VoiceProcessing.Current.Suppressor == "standard" || NoiseSuppressorDownload.Ready();

    /// <summary>
    /// Whether Exo runs a chain of its own for the microphone track: a recording, which is written as it goes, and a replay
    /// only where cleaning it when it is saved can't work (an add-on that can't, or Exo's noise suppression missing).
    /// </summary>
    internal static bool OwnChain(bool recording, bool holding, bool addonCleans, bool canCleanLater) =>
        recording || holding && !(addonCleans && canCleanLater);

    /// <summary>Whether the track takes a voice from Exo now: from its own chain, or a call's, whose chain runs anyway.</summary>
    internal static bool Feeds(bool capturing, bool ownChain, bool call) => capturing && (ownChain || call);

    // Under Gate: the feed and chain as things stand now.
    private static void Apply()
    {
        if (!Feeds(_wanted, _ownChain, _call is not null)) { Close(); return; }
        if (_frames is null && !Open(_addon!)) { _wanted = false; return; }
        if (_call is not null)
        {
            StopOwn();
            if (_tapped is null) Tap();
        }
        else if (_chain is null) StartChain();
    }

    private static void Tap()
    {
        if (_call is not { } call) return;
        var gate = new RecordingGate();
        Action<float[], bool, int> tap = (frame, open, delay) =>
        {
            if (!VoiceProcessing.Current.NoiseSuppression) { Send(frame, call.FrameAt(), delay); return; }
            var (late, at) = gate.Push(frame, open, call.FrameAt());
            if (late is not null) Send(late, at, delay);
        };
        call.Chain.Listen(tap);
        _tapped = (call.Chain, tap);
    }

    private static void Untap()
    {
        if (_tapped is { } tapped) tapped.Chain.Unlisten(tapped.Tap);
        _tapped = null;
    }

    private static void StopOwn()
    {
        var (audio, chain) = (_audio, _chain);
        (_audio, _chain) = (null, null);
        audio?.Dispose();
        chain?.Dispose();
    }

    /// <summary>What Exo Capture is doing now: keeping a replay, recording, or neither (stopped, failed or gone); and whether it can leave a replay's microphone for Exo to clean on saving.</summary>
    internal static void Set(CaptureAddon addon, bool holding, bool recording, bool addonCleans)
    {
        lock (Gate)
        {
            var wanted = holding || recording;
            var own = OwnChain(recording, holding, addonCleans, CanCleanLater);
            if (wanted == _wanted && own == _ownChain) return;
            (_wanted, _ownChain, _addon) = (wanted, own, addon);
            Apply();
        }
    }

    private static bool Open(CaptureAddon addon)
    {
        var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None, 1 << 16);
        nint remote;
        try { remote = addon.ShareHandle(pipe.ClientSafePipeHandle); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            AppLog.Warn("Recorded voice: Exo Capture can't take it: " + ex.Message);
            pipe.Dispose();
            return false;
        }
        pipe.DisposeLocalCopyOfClientHandle();
        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(25) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest },
            dropped => ArrayPool<byte>.Shared.Return(dropped));
        (_pipe, _frames) = (pipe, frames);
        new Thread(() => Pump(pipe, frames)) { IsBackground = true, Name = "Exo recorded voice" }.Start();
        _ = addon.CallAsync("voice.feed", new JsonObject { ["pipe"] = (long)remote }, CancellationToken.None)
            .ContinueWith(t => AppLog.Warn("Recorded voice: " + t.Exception?.GetBaseException().Message), TaskContinuationOptions.OnlyOnFaulted);
        AppLog.Info(_call is null ? "Recorded voice: Exo Capture's microphone track takes the cleaned voice." : "Recorded voice: Exo Capture's microphone track takes the call's cleaned voice.");
        return true;
    }

    private static void Close()
    {
        Untap();
        // The chain after the capture thread stopped: it is the chain's only user.
        StopOwn();
        _frames?.Writer.TryComplete();
        _pipe?.Dispose();
        (_frames, _pipe) = (null, null);
    }

    // The model in first, off this thread: until the first frames come the track keeps the add-on's own microphone, where
    // a chain still loading would turn the voice down as a pause.
    private static void StartChain()
    {
        var chain = new VoiceChain { Recording = true };
        _chain = chain;
        _ = Task.Run(() =>
        {
            chain.Prepare(VoiceProcessing.Current);
            lock (Gate)
            {
                if (_chain == chain) OpenMic(chain);
                else chain.Dispose();
            }
        });
    }

    private static void OpenMic(VoiceChain chain)
    {
        var audio = new VoiceAudio();
        var gate = new RecordingGate();
        audio.Captured += frame =>
        {
            var processing = VoiceProcessing.Current;
            var open = chain.Process(frame, processing, audio.MicId, audio.SystemGain);
            if (!processing.NoiseSuppression) { Send(frame, audio.FrameAt, chain.DelaySamples); return; }
            var (late, at) = gate.Push(frame, open, audio.FrameAt);
            if (late is not null) Send(late, at, chain.DelaySamples);
        };
        try { audio.Start(capture: true, render: false); }
        catch (Exception ex)
        {
            AppLog.Warn("Recorded voice: the microphone didn't open: " + ex.Message);
            audio.Dispose();
            chain.Dispose();
            _chain = null;
            return;
        }
        _audio = audio;
    }

    private static void Send(float[] frame, long frameAt, int delaySamples)
    {
        if (_frames is not { } frames) return;
        var packet = ArrayPool<byte>.Shared.Rent(PacketBytes);
        var at = frameAt - (long)delaySamples * 10_000_000 / Opus.SampleRate;
        BitConverter.TryWriteBytes(packet.AsSpan(0, 8), at);
        BitConverter.TryWriteBytes(packet.AsSpan(8, 4), Opus.FrameSamples);
        BitConverter.TryWriteBytes(packet.AsSpan(12, 4), Channels);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(packet.AsSpan(HeaderBytes, PacketBytes - HeaderBytes));
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)Math.Clamp(frame[i] * 32767f, -32768f, 32767f);
        if (!frames.Writer.TryWrite(packet)) ArrayPool<byte>.Shared.Return(packet);
    }

    // The pipe's own thread: a slow or gone add-on never holds up the voice thread.
    private static void Pump(AnonymousPipeServerStream pipe, Channel<byte[]> frames)
    {
        try
        {
            while (frames.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (frames.Reader.TryRead(out var packet))
                {
                    try { pipe.Write(packet, 0, PacketBytes); }
                    finally { ArrayPool<byte>.Shared.Return(packet); }
                }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            AppLog.Debug("Recorded voice: the pipe closed: " + ex.Message);
        }
    }
}

/// <summary>
/// A recording's pauses, turned down: the gate's verdict (with its 500 ms hold) as a gentle expander instead of a cut,
/// so what noise suppression leaves of a fan or the room between words sits <see cref="FloorDb"/> under the voice while
/// a breath or a quiet word still comes through. A recording can wait: each frame goes out <see cref="Ahead"/> frames
/// late (the call's 100 ms lead-in and the frame the gate waits on before it opens, <see cref="VoiceGate.Confirm"/>), so
/// the gain is already up when a word begins (no first syllable turned down) and it glides across a frame, never
/// stepping. Frames keep their own capture times, so the delay never moves the voice off the picture.
/// </summary>
internal sealed class RecordingGate
{
    internal const int Ahead = VoiceChain.LeadFrames + VoiceGate.Confirm;
    internal const float FloorDb = -30f;
    private static readonly float Floor = MathF.Pow(10, FloorDb / 20);
    private readonly float[][] _frames = [.. Enumerable.Range(0, Ahead + 1).Select(_ => new float[VoiceAudio.FrameFloats])];
    private readonly bool[] _open = new bool[Ahead + 1];
    private readonly long[] _at = new long[Ahead + 1];
    private int _next, _count;
    private float _gain = 1f;

    /// <summary>Takes a frame and the gate's verdict on it; gives back the frame from 100 ms ago with its gain, once there is one.</summary>
    internal (float[]? Frame, long At) Push(float[] frame, bool open, long at)
    {
        frame.CopyTo(_frames[_next], 0);
        (_open[_next], _at[_next]) = (open, at);
        _next = (_next + 1) % _frames.Length;
        if (++_count <= Ahead) return (null, 0);
        // The oldest frame held, and up if it or any frame after it (the next 100 ms) is a voice.
        var oldest = _next;
        var target = _open.Any(o => o) ? 1f : Floor;
        var late = _frames[oldest];
        var step = (target - _gain) / (late.Length / 2);
        for (var i = 0; i + 1 < late.Length; i += 2)
        {
            _gain += step;
            late[i] *= _gain;
            late[i + 1] *= _gain;
        }
        _gain = target;
        return (late, _at[oldest]);
    }
}

/// <summary>
/// A saved clip's microphone, cleaned then rather than all the while it waited in the replay: the same chain and the same
/// late gate as a recording (<see cref="VoiceChain.Recording"/>, <see cref="RecordingGate"/>), each frame in turn on one
/// thread below every game's, at its own pace, so a save costs a burst once and holding a replay costs nothing. A running
/// chain knows the room and the voice, so its first seconds are spent hearing the clip's own before the clip is cleaned.
/// Exo Capture hands the voice over as a file and takes the file back; spans it was already fed (a call, a recording) are
/// clean and stay as they are. Whatever goes wrong, the add-on keeps the clip it has, with the raw microphone.
/// </summary>
internal static class ClipVoice
{
    // About as long as the leveler, the lift for a quiet mic (5 dB a second) and the gate's floors take to settle.
    internal const int WarmFrames = 500;
    private const int FrameBytes = VoiceAudio.FrameFloats * sizeof(short), Samples = Opus.FrameSamples;
    private static readonly Lock OneAtATime = new();

    /// <summary>
    /// The add-on's clip.voice: the clip's voice is cleaned on a thread of its own and the add-on told how it went.
    /// <paramref name="lost"/>: the add-on went before it could hear (a new one knows nothing of the clip), so its saved
    /// event never comes.
    /// </summary>
    internal static void Start(CaptureAddon addon, JsonNode data, Action lost)
    {
        new Thread(() =>
        {
            try
            {
                var path = data["path"]?.GetValue<string>();
                bool ok;
                lock (OneAtATime) ok = Clean(data);
                var heard = false;
                try { heard = addon.CallAsync("clip.voiced", new JsonObject { ["path"] = path, ["ok"] = ok }, CancellationToken.None).GetAwaiter().GetResult()?.GetValue<bool>() == true; }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException) { AppLog.Debug("Recorded voice: " + ex.Message); }
                if (heard) return;
                AppLog.Warn("Recorded voice: Exo Capture went before it took a clip's voice; the clip keeps its raw microphone.");
                lost();
            }
            // Past here it would end Exo.
            catch (Exception ex) { AppLog.Warn("Recorded voice: a clip's voice: " + ex.GetType().Name + ": " + ex.Message); }
        }) { IsBackground = true, Name = "Exo clip voice", Priority = ThreadPriority.BelowNormal }.Start();
    }

    private static bool Clean(JsonNode data)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var processing = VoiceProcessing.Current;
            using var chain = new VoiceChain { Recording = true, Offline = true };
            if (!chain.Prepare(processing))
            {
                AppLog.Warn("Recorded voice: noise suppression didn't load, so a clip keeps its raw microphone.");
                return false;
            }
            var settings = App.Services.Settings.Shared;
            var mic = AudioDevices.MicId(settings.DiscordInputDevice);
            using var raw = new FileStream(data["raw"]!.GetValue<string>(), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var clean = new FileStream(data["clean"]!.GetValue<string>(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
            Run(raw, clean, FedFrames(data["fed"] as JsonArray), frame => chain.Process(frame, processing, mic, systemGain: false),
                () => chain.DelaySamples, processing.NoiseSuppression, settings.DiscordInputVolume / 100f);
            AppLog.Info($"Recorded voice: cleaned a clip's {raw.Length / (double)(Opus.SampleRate * 4):0} s in {Stopwatch.GetElapsedTime(started).TotalSeconds:0.0} s ({chain.Stages}).");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Recorded voice: a clip keeps its raw microphone: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The add-on's spans (milliseconds into the clip, in order) as the frames they touch.</summary>
    internal static List<(long From, long To)> FedFrames(JsonArray? spans)
    {
        var frames = new List<(long, long)>();
        if (spans is null) return frames;
        foreach (var span in spans)
            if (span is JsonArray { Count: 2 } pair)
                frames.Add(((long)(pair[0]!.GetValue<double>() / 20), (long)Math.Ceiling(pair[1]!.GetValue<double>() / 20)));
        return frames;
    }

    /// <summary>
    /// The voice in raw (20 ms frames of 16-bit stereo) cleaned into clean, sample for sample: the sound that comes out of
    /// the chain late is put back where it went in, as a live frame's stamp puts it. Frames in fed are copied. process runs one frame in place and says whether the gate is
    /// open; delay is how late the chain's sound is, in samples; gated: the voice's pauses turned down (with noise
    /// suppression on, as a recording's are). gain is Input Volume.
    /// </summary>
    internal static void Run(Stream raw, Stream clean, IReadOnlyList<(long From, long To)> fed, Func<float[], bool> process, Func<int> delay, bool gated, float gain)
    {
        var total = (raw.Length + FrameBytes - 1) / FrameBytes;
        var bytes = new byte[FrameBytes];
        var frame = new float[VoiceAudio.FrameFloats];
        clean.SetLength(raw.Length);
        var heard = 0;
        for (var i = 0L; i < total && heard < WarmFrames; i++)
        {
            if (Fed(fed, i)) continue;
            Read(raw, i, bytes, frame, gain);
            process(frame);
            heard++;
        }
        for (var i = 0L; i < total;)
        {
            var end = i + 1;
            var copy = Fed(fed, i);
            while (end < total && Fed(fed, end) == copy) end++;
            if (copy)
            {
                raw.Position = clean.Position = i * FrameBytes;
                var left = (end - i) * FrameBytes;
                while (left > 0 && raw.Read(bytes, 0, (int)Math.Min(left, FrameBytes)) is var got and > 0) { clean.Write(bytes, 0, got); left -= got; }
            }
            else
            {
                var run = new Placing(clean, i * Samples, Math.Min(end * Samples, raw.Length / (Opus.Channels * sizeof(short))));
                var gate = gated ? new RecordingGate() : null;
                void Out(long at, bool open)
                {
                    if (gate is null) { run.Write(frame, at * Samples - delay()); return; }
                    var (late, lateAt) = gate.Push(frame, open, at);
                    if (late is not null) run.Write(late, lateAt * Samples - delay());
                }
                for (var n = i; n < end; n++)
                {
                    Read(raw, n, bytes, frame, gain);
                    Out(n, process(frame));
                }
                // What the chain and the gate still hold, pushed out by silence.
                var tail = (gated ? RecordingGate.Ahead : 0) + (delay() + Samples - 1) / Samples;
                for (var n = end; n < end + tail; n++)
                {
                    Array.Clear(frame);
                    Out(n, process(frame));
                }
            }
            i = end;
        }
    }

    private static bool Fed(IReadOnlyList<(long From, long To)> fed, long frame)
    {
        foreach (var (from, to) in fed) if (frame >= from && frame < to) return true;
        return false;
    }

    private static void Read(Stream raw, long frame, byte[] bytes, float[] floats, float gain)
    {
        raw.Position = frame * FrameBytes;
        var got = raw.ReadAtLeast(bytes, FrameBytes, throwOnEndOfStream: false);
        Array.Clear(bytes, got, FrameBytes - got);
        var samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan());
        for (var i = 0; i < floats.Length; i++) floats[i] = samples[i] / 32767f * gain;
    }

    // The cleaned sound of one run of frames going into the file. It runs on without a seam unless it is told to move by
    // more than a frame, as the add-on's own track does, and nothing goes outside the run.
    private sealed class Placing(Stream clean, long from, long to)
    {
        private readonly byte[] _bytes = new byte[FrameBytes];
        private long _at = long.MinValue;

        internal void Write(float[] frame, long at)
        {
            if (_at == long.MinValue || Math.Abs(at - _at) > Samples) _at = at;
            var start = Math.Max(_at, from);
            var end = Math.Min(_at + Samples, to);
            if (end > start)
            {
                var samples = MemoryMarshal.Cast<byte, short>(_bytes.AsSpan());
                var count = (int)(end - start) * Opus.Channels;
                var skip = (int)(start - _at) * Opus.Channels;
                for (var i = 0; i < count; i++) samples[i] = (short)Math.Clamp(frame[skip + i] * 32767f, -32768f, 32767f);
                clean.Position = start * Opus.Channels * sizeof(short);
                clean.Write(_bytes, 0, count * sizeof(short));
            }
            _at += Samples;
        }
    }
}
