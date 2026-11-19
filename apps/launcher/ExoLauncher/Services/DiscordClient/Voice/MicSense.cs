using System.Globalization;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>What the microphone's own sound says about it (<see cref="MicSense"/>), for Voice settings and app.log.</summary>
/// <param name="SpeechDb">The voice as it comes in (dBFS, its frames' mean over about the last 10 s of talking), once 3 s are heard.</param>
/// <param name="QuietDb">The quiet between words (dBFS), once heard; null where it is digital silence (<paramref name="Gated"/>).</param>
/// <param name="AddedDb">What Exo adds to it before cleaning it: a kept Voice Tune's gain, as far as the voice still needs it.</param>
internal readonly record struct MicHeard(float? SpeechDb, float? QuietDb, bool Clipping, bool Quiet, bool Gated, bool Cleaned, float AddedDb)
{
    /// <summary>One word for what the sound says, the most pressing first; app.log says it again only when it changes.</summary>
    internal string Verdict => SpeechDb is null ? "listening" : Clipping ? "clipping" : Gated ? "gated" : Quiet ? "quiet" : Cleaned ? "cleaned" : "good";

    public override string ToString()
    {
        if (SpeechDb is not { } speech) return "no voice heard yet";
        var said = string.Create(CultureInfo.InvariantCulture, $"voice at {speech:0} dBFS");
        if (QuietDb is { } quiet) said += string.Create(CultureInfo.InvariantCulture, $", {quiet:0} dBFS between words");
        said += Verdict switch
        {
            "clipping" => "; it clips: turn its gain down",
            "gated" => "; something before Exo gates it (digital silence between words): turn that gate off",
            "quiet" => "; quiet: turn its gain up",
            "cleaned" => "; already cleaned before Exo",
            _ => "",
        };
        if (MathF.Abs(AddedDb) >= 0.5f) said += string.Create(CultureInfo.InvariantCulture, $"; Exo adds {AddedDb:+0;-0} dB of its kept tuning");
        return said;
    }
}

/// <summary>
/// The microphone as its own sound tells it, read on each raw 20 ms frame before Exo changes it, with the gate's verdict
/// on that frame: how loud the voice comes in, how quiet it is between words, whether it clips (samples at full scale:
/// its gain is too high, and nothing after the converter can undo it), and whether something before Exo gates it (the
/// quiet between words is digital silence: the mic's own gate, or an app's). The chain follows it: a kept Voice Tune's
/// gain adds only what the voice still needs (<see cref="Missing"/>). A compressor or auto level before Exo isn't read:
/// by its sound it can't be told from a steady talker (on a recorded voice, even a heavy 8:1 one narrowed the spread of
/// its frame levels by under 1 dB), so nothing steps aside on a guess. Nothing is allocated per frame.
/// </summary>
internal sealed class MicSense
{
    /// <summary>Frames of talking (3 s) before the voice's level is said; a kept gain follows it from the first half second.</summary>
    internal const int Learned = 150, TrimFrom = 25;
    // About the last 10 s of talking, and the last 5 s of quiet.
    private const int Span = 500, QuietSpan = 250;
    /// <summary>A sample this close to full scale (-0.1 dBFS) is the top of the converter.</summary>
    internal const float FullScale = 0.989f;
    /// <summary>A voice under this the leveler can't bring to its level (12 dB at most) and the gate hears near the room.</summary>
    internal const float QuietVoiceDb = -40f;
    /// <summary>Quieter than a microphone and its preamp are on their own between words: something cleaned it first.</summary>
    internal const float CleanedDb = -90f;
    private const float SilentDb = -100f;

    /// <summary>A frame this far over the room's floor while the gate held it shut is talking too quiet to open it.</summary>
    internal const float AboveFloorDb = 15f;
    /// <summary>The most Exo adds on its own to a quiet microphone (no Voice Tune kept for it).</summary>
    internal const float AutoGainCapDb = 18f;

    private float _db, _speechDb, _quietDb = float.NaN, _clipShare, _silentShare, _heldDb;
    private int _held;
    private bool _clips, _clipping, _quiet, _gated;
    private int _heard, _quietHeard;
    private readonly QuietFloor _floor = new();

    /// <summary>Takes one raw 20 ms stereo frame, before Exo changes it.</summary>
    internal void Hear(ReadOnlySpan<float> frame)
    {
        double sum = 0;
        var full = 0;
        foreach (var s in frame)
        {
            sum += s * s;
            if (MathF.Abs(s) >= FullScale) full++;
        }
        _db = MathF.Max(SilentDb, 10 * MathF.Log10((float)(sum / Math.Max(1, frame.Length)) + 1e-10f));
        // Two moments at full scale on both channels: one peak that only touches the top isn't a clip yet.
        _clips = full >= 4;
    }

    /// <summary>The gate's verdict on the frame last heard: a voice (its own rules passed it), or quiet (held shut).</summary>
    internal void Judge(bool speaking, bool shut)
    {
        if (speaking && _db > -70f)
        {
            if (_heard < Span) _heard++;
            _speechDb += (_db - _speechDb) / _heard;
            // The share of talking that clips: 1% (five frames in the last 10 s) says so, under 0.2% it's gone.
            _clipShare += ((_clips ? 1f : 0f) - _clipShare) / Span;
            _clipping = _clipping ? _clipShare > 0.002f : _clipShare >= 0.01f;
            if (_heard >= Learned) _quiet = _quiet ? _speechDb < QuietVoiceDb + 3f : _speechDb < QuietVoiceDb;
            return;
        }
        if (!shut) return;
        // Held shut but well over the room's floor: a voice the gate can't hear (a mic whose own gain can't go up). Its
        // level is learned here too, or a quiet mic that never opens the gate would never be heard at all.
        if (_floor.Db is var held && held < 0f && _db > held + AboveFloorDb && _db > -70f)
        {
            if (_held < Span) _held++;
            _heldDb += (_db - _heldDb) / _held;
            return;
        }
        if (_quietHeard < QuietSpan) _quietHeard++;
        var silent = _db <= SilentDb;
        // Half the quiet as digital silence, while a voice comes through: a gate closes before Exo. A mic muted on its own
        // button is silent too, but sends no voice (MicSilence says that one).
        _silentShare += ((silent ? 1f : 0f) - _silentShare) / QuietSpan;
        _gated = _heard >= Learned && (_gated ? _silentShare > 0.2f : _silentShare >= 0.5f);
        if (silent) return;
        _floor.Add(_db);
        if (_floor.Db is var floor && floor < 0f) _quietDb = float.IsNaN(_quietDb) ? floor : _quietDb + (floor - _quietDb) / QuietSpan;
    }

    /// <summary>
    /// The part of a kept Voice Tune gain (dB) the voice still needs to reach <paramref name="speakingDb"/>: none until its
    /// level is heard (half a second of talking), none once the mic itself is turned up to it, and never more than the
    /// tuning's own, either way.
    /// </summary>
    internal float Missing(float tunedDb, float speakingDb) => _heard < TrimFrom || tunedDb == 0f ? 0f
        : tunedDb > 0f ? Math.Clamp(speakingDb - _speechDb, 0f, tunedDb) : Math.Clamp(speakingDb - _speechDb, tunedDb, 0f);

    /// <summary>
    /// What Exo adds on its own to a microphone too quiet for its leveler (under <see cref="QuietVoiceDb"/>) when no
    /// Voice Tune is kept for it: enough to bring the voice 6 dB into the leveler's reach (which takes it the rest of the
    /// way), at most <see cref="AutoGainCapDb"/>. Judged from the raw talking the gate passed and the talking it held back,
    /// so a voice it cut is lifted before suppression and the gate decide on it. None for a mic loud enough on its own.
    /// </summary>
    internal float AutoMissing()
    {
        var heard = _heard + _held;
        if (heard < Learned) return 0f;
        var level = (_speechDb * _heard + _heldDb * _held) / heard;
        return level < QuietVoiceDb ? Math.Clamp(QuietVoiceDb + 6f - level, 0f, AutoGainCapDb) : 0f;
    }

    /// <summary>What the sound says now; <paramref name="addedDb"/> is what the chain adds to it.</summary>
    internal MicHeard Reading(float addedDb)
    {
        var learned = _heard >= Learned;
        float? quiet = !_gated && _quietHeard >= Learned && !float.IsNaN(_quietDb) ? _quietDb : null;
        return new MicHeard(learned ? _speechDb : null, quiet, learned && _clipping, _quiet, _gated, learned && quiet < CleanedDb, addedDb);
    }
}
