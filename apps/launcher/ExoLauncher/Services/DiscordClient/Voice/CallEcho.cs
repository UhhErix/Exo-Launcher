namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// Go Live's last guard against the call echoing into a screen's sound: what Exo knows is the call, the voices it plays
/// (<see cref="Heard"/>) and your microphone as the call gets it (<see cref="Said"/>), is compared with each app the
/// stream takes, so an app that plays either again (a mixer, a mic monitor, a voice app no list names) is recognized by
/// its sound. Each sound is cut into 10 ms blocks of its loudness on one clock (the performance counter); every 3 s of an
/// app's sound is matched against the call's over 30 ms before to 400 ms after (Pearson's r of the loudness, the best lag).
/// Two 3 s windows in a row at 0.7 or more at the same lag, with the call talking in them, mark the app as carrying it;
/// two without it (under 0.4 while the call talks) take that back. Measured on MUSAN's real speech, music and noise: 300
/// minutes of music, game-like noise, films and other people's talk against a call never once marked an app; the call
/// copied 6 dB over the rest of an app's sound was always found (in about 7 s), at the same level 58 to 60 times in 60
/// (in 9 to 20 s), 6 dB under it only now and then; a mic monitor playing your voice alone, always (StreamSoundTests check
/// the same on synthetic sound). Costs about 2 µs per app per 20 ms frame and 16 µs per app per 3 s window.
/// </summary>
internal static class CallEcho
{
    internal const long BlockTicks = 100_000; // 10 ms on the performance counter's 100 ns clock
    internal const int Window = 300; // 3 s
    internal const int Earliest = -3, Latest = 40; // lags in blocks: the copy at most 30 ms before the call, 400 ms after
    internal const float Hit = 0.7f, Gone = 0.4f;
    // A block "talks" at -50 dBFS and up; a window is judged when the call talks in 30 % of it and the app makes sound in 20 %.
    private const float Talking = 0.00316f, Sounding = 0.001f;

    /// <summary>What Exo plays of the call (the speakers' mix, silence while deafened), stamped as it is mixed.</summary>
    internal static readonly Envelope Heard = new();

    /// <summary>Your microphone as the call gets it (after Exo's processing; silence while muted or the gate is shut), stamped at capture.</summary>
    internal static readonly Envelope Said = new();

    /// <summary>Now, as a block number.</summary>
    internal static long NowBlock() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency)) / BlockTicks;

    /// <summary>
    /// The best correlation of an app's window of loudness with the call's over the lags, the lag it was at, and whether
    /// the window can be judged (enough of both known, the call talking and the app sounding). <paramref name="call"/>
    /// starts <paramref name="latest"/> blocks before the window and runs to <paramref name="earliest"/> blocks after it:
    /// at lag L the app's block b pairs with the call's block b - L. NaN is a block not known.
    /// </summary>
    internal static (float R, int Lag, bool Judged) Score(ReadOnlySpan<float> app, ReadOnlySpan<float> call, int earliest, int latest)
    {
        int known = 0, sounding = 0;
        foreach (var a in app)
        {
            if (float.IsNaN(a)) continue;
            known++;
            if (a >= Sounding) sounding++;
        }
        if (known < app.Length * 4 / 5 || sounding < app.Length / 5) return (0, 0, false);
        float best = -1;
        var bestLag = 0;
        var talked = 0;
        for (var lag = earliest; lag <= latest; lag++)
        {
            var offset = latest - lag;
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            int n = 0, talks = 0;
            for (var b = 0; b < app.Length; b++)
            {
                var x = app[b];
                var y = call[b + offset];
                if (float.IsNaN(x) || float.IsNaN(y)) continue;
                n++;
                if (y >= Talking) talks++;
                sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
            }
            if (n < app.Length * 4 / 5) continue;
            var spread = (n * sxx - sx * sx) * (n * syy - sy * sy);
            var r = spread > 0 ? (float)((n * sxy - sx * sy) / Math.Sqrt(spread)) : 0f;
            if (r > best) (best, bestLag, talked) = (r, lag, talks);
        }
        return (Math.Max(best, 0), bestLag, best > -1 && talked >= app.Length * 3 / 10);
    }
}

/// <summary>
/// A sound's loudness in 10 ms blocks on the performance counter's clock, the last ~10 s of it: each block's mean square
/// of both channels, from 48 kHz stereo frames stamped with when their first sample played or was captured.
/// </summary>
internal sealed class Envelope
{
    private const int Size = 1024, PerBlock = 480;
    private readonly long[] _block = new long[Size];
    private readonly float[] _energy = new float[Size];
    private readonly int[] _count = new int[Size];
    private readonly Lock _gate = new();

    /// <summary>A stereo frame (interleaved) whose first sample was at <paramref name="at"/> (100 ns).</summary>
    internal void Add(ReadOnlySpan<float> stereo, long at) => Put(stereo, stereo.Length / 2, at);

    /// <summary>That many frames of silence from <paramref name="at"/>.</summary>
    internal void Quiet(int frames, long at) => Put([], frames, at);

    private void Put(ReadOnlySpan<float> stereo, int frames, long at)
    {
        var block = at / CallEcho.BlockTicks;
        var into = (int)(at % CallEcho.BlockTicks * 48 / 10_000);
        lock (_gate)
            for (var i = 0; i < frames; block++, into = 0)
            {
                var n = Math.Min(frames - i, PerBlock - into);
                float sum = 0;
                if (!stereo.IsEmpty) foreach (var s in stereo.Slice(2 * i, 2 * n)) sum += s * s;
                var slot = (int)(block & (Size - 1));
                if (_block[slot] != block + 1) (_block[slot], _energy[slot], _count[slot]) = (block + 1, 0, 0);
                _energy[slot] += sum / 2;
                _count[slot] += n;
                i += n;
            }
    }

    /// <summary>Each block's loudness (RMS) from <paramref name="from"/> on, NaN where under half of it is known.</summary>
    internal void Read(long from, Span<float> into)
    {
        lock (_gate)
            for (var i = 0; i < into.Length; i++)
            {
                var block = from + i;
                var slot = (int)(block & (Size - 1));
                into[i] = _block[slot] == block + 1 && _count[slot] >= PerBlock / 2 ? MathF.Sqrt(_energy[slot] / _count[slot]) : float.NaN;
            }
    }
}

/// <summary>
/// One app's verdict over a stream: whether its sound carries the call's voices (<see cref="CallEcho.Heard"/>) or your
/// microphone (<see cref="CallEcho.Said"/>), from each 3 s of it once both are in. Only the stream's sound thread uses it.
/// </summary>
internal sealed class EchoWatch(long startBlock)
{
    private const int Settle = 50; // 500 ms: the app's sound and the call's are both in by then
    private readonly Track _heard = new(), _said = new();
    private readonly float[] _app = new float[CallEcho.Window];
    private readonly float[] _call = new float[CallEcho.Window + CallEcho.Latest - CallEcho.Earliest];
    private long _next = startBlock;

    /// <summary>The app's sound is the call's voices or your microphone now.</summary>
    internal bool Carrying => _heard.Carrying || _said.Carrying;

    /// <summary>Which: "the call" or "your mic", and the last r and lag that said so.</summary>
    internal (string What, float R, int Lag) Why => _heard.Carrying ? ("the call", _heard.R, _heard.Lag) : ("your mic", _said.R, _said.Lag);

    /// <summary>Scores the app's windows that are whole; true when <see cref="Carrying"/> changed.</summary>
    internal bool Check(Envelope app, Envelope heard, Envelope said, long now)
    {
        var was = Carrying;
        // At most two windows a call (a stalled thread skips ahead instead of catching up on old sound).
        if (now - _next > 3L * CallEcho.Window) _next = now - Settle - CallEcho.Window;
        for (var windows = 0; windows < 2 && _next + CallEcho.Window + Settle <= now; windows++, _next += CallEcho.Window)
        {
            app.Read(_next, _app);
            _heard.Step(Judge(heard));
            _said.Step(Judge(said));
        }
        return Carrying != was;
    }

    private (float, int, bool) Judge(Envelope call)
    {
        call.Read(_next - CallEcho.Latest, _call);
        return CallEcho.Score(_app, _call, CallEcho.Earliest, CallEcho.Latest);
    }

    private sealed class Track
    {
        internal bool Carrying;
        internal float R;
        internal int Lag;
        private int _run, _gone;

        internal void Step((float R, int Lag, bool Judged) score)
        {
            if (!score.Judged) return;
            var same = Math.Abs(score.Lag - Lag) <= 1;
            if (!Carrying)
            {
                _run = score.R < CallEcho.Hit ? 0 : _run > 0 && same ? _run + 1 : 1;
                if (_run == 1) Lag = score.Lag;
                if (_run < 2) return;
                (Carrying, R, _gone) = (true, score.R, 0);
                return;
            }
            if (score.R >= CallEcho.Hit && same) (_gone, R) = (0, score.R);
            else if (score.R < CallEcho.Gone && ++_gone >= 2) (Carrying, _run) = (false, 0);
        }
    }
}
