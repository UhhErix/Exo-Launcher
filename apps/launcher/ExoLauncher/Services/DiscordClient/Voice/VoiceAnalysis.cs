namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// A kept Voice Tune for one voice on one microphone: how far noise suppression may turn noise down (dB, at most
/// <see cref="NoiseSuppressor.MostDb"/>: more takes parts of the voice with it), and the gain to add (dB) so the loudest
/// words land near −6 dBFS. Spectrum is the voice's own tone (dB per band, <see cref="VoiceAnalysis.Bands"/>,
/// relative to its 500 Hz band), kept for reference: Exo doesn't correct tone, since a voice's natural tone
/// differs from the average as much as a coloured mic does, so a correction would change good voices.
/// </summary>
internal sealed record VoiceTuning(int SuppressionLimitDb, float GainDb, float[] Spectrum);

/// <summary>The rate Exo Voice runs at, the bands a kept tuning's spectrum is in, and how far suppression cuts for a voice.</summary>
internal static class VoiceAnalysis
{
    internal const int Rate = 48000;
    internal static readonly int[] Bands = [125, 250, 500, 1000, 2000, 4000, 8000];

    /// <summary>How far suppression cuts for a voice level and the room under it: a quiet room less (more sounds muffled).</summary>
    internal static int Limit(float speechDb, float roomDb)
    {
        var snr = speechDb - roomDb;
        return snr >= 50 ? 24 : snr >= 40 ? 27 : snr >= 30 ? NoiseSuppressor.GentleDb : NoiseSuppressor.MostDb;
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
