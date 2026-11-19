namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>Voice Tune's guided take, part by part, as the microphone gave it (48 kHz mono, before any processing).</summary>
internal sealed record TuneTake(float[] Quiet, float[] Reading, float[] Loud, float[] Soft, float[] Typing);

/// <summary>
/// What Voice Tune sets for one voice on one microphone: how far noise suppression may turn noise down (dB, at most
/// <see cref="NoiseSuppressor.MostDb"/>: more takes parts of the voice with it), and the gain to add (dB) so the loudest
/// words land near −6 dBFS. Spectrum is the voice's own tone (dB per band, <see cref="VoiceAnalysis.Bands"/>,
/// relative to its 500 Hz band), kept for reference: Exo doesn't correct tone, since a voice's natural tone
/// differs from the average as much as a coloured mic does, so a correction would change good voices.
/// </summary>
internal sealed record VoiceTuning(int SuppressionLimitDb, float GainDb, float[] Spectrum);

/// <summary>What Voice Tune heard, in dB (full scale unless said), and what it sets from that.</summary>
internal sealed record VoiceReport(
    bool Heard, float RoomDb, int? HumHz, bool BandLimited, float PeakDb, int Clipped,
    float SpeechDb, float SoftDb, float LoudDb, float PitchHz, int Pops, float TypingDb, VoiceTuning Tuning);

/// <summary>
/// Voice Tune's listening: one guided take measured offline (it runs once, so it can afford to look closely).
/// The room from the quiet part (its level, mains hum by its 50 or 60 Hz harmonics), the voice from the reading
/// (its active level as ITU-T P.56 measures speech, so a voice only a little over a noisy room still counts; its pitch by autocorrelation;
/// its tone in octave bands; its pops), the headroom from the
/// loud words, the soft ones, and the keyboard from the typing. Everything it sets is
/// bounded: it fixes what the mic and room do, never the voice itself.
/// </summary>
internal static class VoiceAnalysis
{
    internal const int Rate = 48000;
    internal static readonly int[] Bands = [125, 250, 500, 1000, 2000, 4000, 8000];
    private const int Frame = Rate / 50, Window = Rate / 10;

    internal static VoiceReport Analyze(TuneTake take)
    {
        var room = Floor(take.Quiet);
        var roomDb = Db(room);
        var reading = Speech(take.Reading, room);
        var heard = reading.Count >= 50; // a second of speech at the least
        var speech = heard ? Db(Power(take.Reading, reading)) : roomDb;
        var soft = Db(Power(take.Soft, Speech(take.Soft, room)));
        var loudPeak = Db(Peak(take.Loud));
        var peak = Db(Math.Max(Peak(take.Reading), Peak(take.Loud)));
        var clipped = Clips(take.Reading) + Clips(take.Loud) + Clips(take.Soft);
        // Two scratches as long as the longest take, which every filter below writes into in turn (each is read before the
        // next is made): each filter made a new array the take's length, about sixteen a Voice Tune and up to 7.7 MB each.
        var length = Math.Max(take.Reading.Length, take.Typing.Length);
        var (a, b) = (new float[length], new float[length]);
        var pitch = heard ? Pitch(take.Reading, reading, a) : 0f;
        var spectrum = heard ? Spectrum(take.Reading, reading, a) : new float[Bands.Length];
        var bandLimited = heard && BandLimited(take.Reading, reading, a);
        var pops = heard ? Pops(take.Reading, reading, a, b) : 0;
        var hum = Hum(take.Quiet);
        var typing = Db(Peak(VoiceBand(take.Typing, a), Rate / 200));

        // Typing and clicks aren't in the room's floor, so even a quiet room keeps enough to take them out.
        var gain = Math.Clamp(-6 - loudPeak, -20f, 20f);
        if (clipped > 0) gain = Math.Min(gain, -6f);
        var tuning = new VoiceTuning(Limit(speech, roomDb), MathF.Round(gain, 1), spectrum);
        return new VoiceReport(heard, R(roomDb), hum, bandLimited, R(peak), clipped, R(speech), R(soft), R(loudPeak), R(pitch),
            pops, R(typing), tuning);
    }

    /// <summary>How far suppression cuts for a voice level and the room under it: a quiet room less (more sounds muffled).</summary>
    internal static int Limit(float speechDb, float roomDb)
    {
        var snr = speechDb - roomDb;
        return snr >= 50 ? 24 : snr >= 40 ? 27 : snr >= 30 ? NoiseSuppressor.GentleDb : NoiseSuppressor.MostDb;
    }

    private static float R(float v) => float.IsFinite(v) ? MathF.Round(v, 1) : -120f;
    private static float Db(double rms) => (float)(20 * Math.Log10(Math.Max(rms, 1e-6)));

    // The room: the median of its 100 ms windows (a stray sound in the quiet doesn't count).
    private static float Floor(float[] x)
    {
        var levels = new List<double>();
        for (var at = 0; at + Window <= x.Length; at += Window) levels.Add(Rms(x, at, Window));
        if (levels.Count == 0) return 1e-5f;
        levels.Sort();
        return (float)levels[levels.Count / 2];
    }

    private static double Rms(ReadOnlySpan<float> x, int at, int n)
    {
        double sum = 0;
        for (var i = at; i < at + n; i++) sum += x[i] * x[i];
        return Math.Sqrt(sum / n);
    }

    // The 20 ms frames that hold speech, as ITU-T P.56 finds them: loudest first, each frame kept while it is within
    // 15.9 dB of the active level of those kept before it, and only frames 4 dB over the room at all.
    private static List<int> Speech(float[] x, double room)
    {
        var floor = room * room * Math.Pow(10, 0.4);
        var frames = new List<(int Frame, double Power)>();
        for (var f = 0; (f + 1) * Frame <= x.Length; f++)
        {
            var rms = Rms(x, f * Frame, Frame);
            if (rms * rms > floor) frames.Add((f, rms * rms));
        }
        frames.Sort((a, b) => b.Power.CompareTo(a.Power));
        double sum = 0;
        var kept = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            sum += frames[i].Power;
            if (10 * Math.Log10(sum / (i + 1) / frames[i].Power) > 15.9) break;
            kept = i + 1;
        }
        return frames.Take(kept).Select(f => f.Frame).Order().ToList();
    }

    private static double Power(ReadOnlySpan<float> x, List<int> frames)
    {
        if (frames.Count == 0) return 0;
        double sum = 0;
        foreach (var f in frames) for (var i = f * Frame; i < (f + 1) * Frame; i++) sum += x[i] * x[i];
        return Math.Sqrt(sum / (frames.Count * Frame));
    }

    private static float Peak(float[] x) => x.Length == 0 ? 0 : x.Max(MathF.Abs);

    // The loudest short block (keyboard clicks are a few ms).
    private static double Peak(ReadOnlySpan<float> x, int block)
    {
        double best = 0;
        for (var at = 0; at + block <= x.Length; at += block / 2) best = Math.Max(best, Rms(x, at, block));
        return best;
    }

    private static int Clips(float[] x) => x.Count(v => MathF.Abs(v) >= 0.999f);

    private static ReadOnlySpan<float> VoiceBand(float[] x, float[] scratch) =>
        Filter(Filter(x, Biquad.HighPass(150), scratch), Biquad.LowPass(4000), scratch);

    // A take through a filter into the start of a scratch (in place when it is that scratch already): what was there goes.
    private static Span<float> Filter(ReadOnlySpan<float> x, Biquad b, float[] scratch)
    {
        var y = scratch.AsSpan(0, x.Length);
        for (var i = 0; i < x.Length; i++) y[i] = (float)b.Run(x[i]);
        return y;
    }

    // Each band's power over the speech, in dB relative to the 500 Hz band.
    private static float[] Spectrum(float[] x, List<int> frames, float[] scratch)
    {
        var levels = new float[Bands.Length];
        for (var i = 0; i < Bands.Length; i++) levels[i] = Db(Power(Filter(x, Biquad.BandPass(Bands[i], 1.41), scratch), frames));
        var reference = levels[Array.IndexOf(Bands, 500)];
        return levels.Select(l => MathF.Round(l - reference, 1)).ToArray();
    }

    // Nothing above about 8 kHz while 1–2 kHz is strong: a Bluetooth hands-free or 16 kHz format.
    private static bool BandLimited(float[] x, List<int> frames, float[] scratch)
    {
        var top = Db(Power(Filter(Filter(x, Biquad.HighPass(10000), scratch), Biquad.HighPass(10000), scratch), frames));
        var mid = Db(Power(Filter(x, Biquad.BandPass(1500, 1.41), scratch), frames));
        return top < mid - 55;
    }

    // Pops: speech frames whose energy under 100 Hz jumps 15 dB over the speech's usual and outweighs its 300 Hz band.
    private static int Pops(float[] x, List<int> frames, float[] lowScratch, float[] bodyScratch)
    {
        Filter(Filter(x, Biquad.LowPass(100), lowScratch), Biquad.LowPass(100), lowScratch);
        Filter(x, Biquad.BandPass(300, 1.41), bodyScratch);
        var (low, body) = (lowScratch, bodyScratch);
        var levels = frames.Select(f => (Low: Db(Rms(low, f * Frame, Frame)), Body: Db(Rms(body, f * Frame, Frame)))).ToArray();
        if (levels.Length == 0) return 0;
        var usual = levels.Select(l => l.Low).Order().ElementAt(levels.Length / 2);
        var pops = 0;
        for (var i = 0; i < levels.Length; i++)
            if (levels[i].Low > usual + 15 && levels[i].Low > levels[i].Body && (i == 0 || levels[i - 1].Low <= usual + 15)) pops++;
        return pops;
    }

    // Mains hum: the first four harmonics of 50 or 60 Hz standing 10 dB over the room just beside them.
    private static int? Hum(float[] x)
    {
        if (x.Length < Rate) return null;
        double Score(int mains)
        {
            double on = 0, off = 0;
            for (var k = 1; k <= 4; k++)
            {
                on += Goertzel(x, mains * k);
                off += (Goertzel(x, mains * k - 7) + Goertzel(x, mains * k + 7)) / 2;
            }
            return 10 * Math.Log10(Math.Max(on, 1e-20) / Math.Max(off, 1e-20));
        }
        var fifty = Score(50);
        var sixty = Score(60);
        var best = Math.Max(fifty, sixty);
        return best < 10 ? null : fifty >= sixty ? 50 : 60;
    }

    private static double Goertzel(float[] x, double hz)
    {
        var w = 2 * Math.PI * hz / Rate;
        var c = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        foreach (var v in x)
        {
            var s0 = v + c * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return (s1 * s1 + s2 * s2 - c * s1 * s2) / x.Length;
    }

    // The voice's pitch: the median, over speech frames, of the strongest autocorrelation lag between 60 and 400 Hz.
    private static float Pitch(float[] x, List<int> frames, float[] scratch)
    {
        const int n = Rate / 25, minLag = Rate / 400, maxLag = Rate / 60;
        var low = Filter(x, Biquad.LowPass(1000), scratch);
        var pitches = new List<float>();
        foreach (var f in frames.Where((_, i) => i % 3 == 0))
        {
            var at = f * Frame;
            if (at + n + maxLag > low.Length) continue;
            double energy = 0;
            for (var i = at; i < at + n; i++) energy += low[i] * low[i];
            if (energy <= 0) continue;
            double best = 0;
            var lag = 0;
            for (var l = minLag; l <= maxLag; l++)
            {
                double sum = 0;
                for (var i = at; i < at + n; i++) sum += low[i] * low[i + l];
                if (sum > best) { best = sum; lag = l; }
            }
            if (lag > 0 && best / energy > 0.5) pitches.Add((float)Rate / lag);
        }
        if (pitches.Count == 0) return 0;
        pitches.Sort();
        return pitches[pitches.Count / 2];
    }

    private struct Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

        internal double Run(double x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }

        // RBJ cookbook filters.
        private static Biquad Make(double b0, double b1, double b2, double a0, double a1, double a2) =>
            new() { _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b2 / a0, _a1 = a1 / a0, _a2 = a2 / a0 };

        internal static Biquad HighPass(double hz, double q = 0.7071)
        {
            var w = 2 * Math.PI * hz / Rate; var a = Math.Sin(w) / (2 * q); var c = Math.Cos(w);
            return Make((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + a, -2 * c, 1 - a);
        }

        internal static Biquad LowPass(double hz, double q = 0.7071)
        {
            var w = 2 * Math.PI * hz / Rate; var a = Math.Sin(w) / (2 * q); var c = Math.Cos(w);
            return Make((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + a, -2 * c, 1 - a);
        }

        internal static Biquad BandPass(double hz, double q)
        {
            var w = 2 * Math.PI * hz / Rate; var a = Math.Sin(w) / (2 * q); var c = Math.Cos(w);
            return Make(a, 0, -a, 1 + a, -2 * c, 1 - a);
        }
    }
}

/// <summary>
/// The first listen of a call, for a microphone with no kept Voice Tune: three seconds of voice and one second of the
/// quiet between words set how far suppression cuts for this call, as a guided take would (a quiet room less, which
/// sounds more natural). Gain stays <see cref="MicSense.AutoMissing"/>'s, and nothing is kept: a few seconds of one call
/// is no measure of a mic. Nothing is allocated per frame.
/// </summary>
internal sealed class LiveTune
{
    /// <summary>3 s of talking and 1 s of quiet, at one 20 ms frame each.</summary>
    internal const int SpeechFrames = 150, QuietFrames = 50;

    private string? _mic;
    private int _speech, _quiet;
    private float _speechDb, _quietDb, _db;
    private bool _ready;

    /// <summary>The raw frame, before the chain changes it.</summary>
    internal void Sample(ReadOnlySpan<float> frame)
    {
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        _db = MathF.Max(-100f, 10 * MathF.Log10((float)(sum / Math.Max(1, frame.Length)) + 1e-10f));
    }

    /// <summary>The gate's verdict on the frame just sampled. The limit, once both sides have been heard, and null before.</summary>
    internal int? Classify(string mic, bool speaking, bool shut)
    {
        if (mic != _mic)
        {
            _mic = mic;
            _speech = _quiet = 0;
            _speechDb = _quietDb = 0;
            _ready = false;
        }
        if (_ready) return null;
        if (speaking && _db > -70f)
        {
            _speech++;
            _speechDb += (_db - _speechDb) / _speech;
        }
        else if (shut && _db > -100f)
        {
            _quiet++;
            _quietDb += (_db - _quietDb) / _quiet;
        }
        if (_speech < SpeechFrames || _quiet < QuietFrames) return null;
        _ready = true;
        return VoiceAnalysis.Limit(_speechDb, _quietDb);
    }
}
