using System.Globalization;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// What the chain did to the last frame. Suppression: "standard" (WebRTC's own, Discord's Standard: <see cref="WebRtcNoise"/>), "on" (DeepFilterNet ran, turning noise down by up to SuppressionLimitDb), "light" (its first stage alone, while the CPU can't keep up with both: <see cref="Overload"/>),
/// "mic" (the microphone's own stands in), "windows" (Windows' own stands in while Exo's isn't here), "loading", "missing" (its plugin isn't here or won't load) or "off". Gain: "windows" (Windows' own, where the microphone's driver keeps it on) or "exo" (Exo's leveler: <see cref="SpeechLeveler"/>). Tuned: a kept Voice Tune tuning (or Voice Tune's take) set the
/// suppression's strength. app.log says it as the chain runs, in its order.
/// </summary>
internal readonly record struct VoiceStages(string Suppression, int SuppressionLimitDb, bool Focus, string Gain, bool Tuned)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"low cut, noise suppression {Suppression}{(Suppression is "on" or "light" ? $" (up to {SuppressionLimitDb} dB)" : "")}, {(Focus ? "Voice Focus" : "gate")}, gain control {Gain}{(Focus ? "" : Gain == "off" ? ", release fade" : ", voice shape, release fade")}, {(Tuned ? "tuned for this mic" : "not tuned")}");
}

/// <summary>
/// The microphone's processing, one chain for a call, Voice Check and Voice Tune's "after", so what they let you
/// hear is what the call sends, in WebRTC's order (Chrome's and Discord's): the low cut and the knock detector; noise suppression, Exo AI (DeepFilterNet at the level Krisp runs its own model at, see
/// <see cref="NoiseSuppressor"/>) or Standard (WebRTC's own, <see cref="WebRtcNoise"/>), stepped aside where the
/// microphone's own is as good, e.g. NVIDIA Broadcast; then Voice Focus or the level gate, which decides on the cleaned
/// frame, measured against the microphone's sound it came from (<see cref="NoiseSuppressor.InputDb"/>, suppression's
/// delay back) and against floors learned from both, so the room's fans and hum never open it
/// (<see cref="VoiceGate.Voiced"/>); the voice's shape (EQ and a soft compressor, <see cref="VoiceShape"/>) and
/// the leveler (<see cref="SpeechLeveler"/>) where Windows' gain control isn't on; and the gate's release fade and lead-in
/// (Discord's 100 ms before a word). With no Voice Tune kept, the first seconds of a call set how far suppression
/// cuts for it from what the mic hears (<see cref="LiveTune"/>). Nothing waits on a later frame: none of it adds delay. Fed 20 ms stereo frames in place, from one thread.
/// </summary>
internal sealed class VoiceChain : IDisposable
{
    private readonly VoiceGate _gate = new();
    private readonly LowCut _lowCut = new();
    private readonly KnockDetector _knock = new();
    private WebRtcNoise? _standard;
    private WebRtcAgc2? _agc2;
    private bool _agc2Failed;
    private NoiseSuppressor? _suppressor;
    private NeuralNoiseSuppressor? _neural;
    private Task<NeuralNoiseSuppressor?>? _neuralLoading;
    private VoiceModelTier _neuralTier = VoiceModelTier.FallbackDfn3;
    private int _neuralRetryIn;
    private bool _hqOverBudget;
    private string? _lastVoiceTierPref;
    private readonly VoiceShape _shape = new();
    /// <summary>The level the leveler brings a voice to (dBFS, its frames' mean), and a kept tuning's gain toward.</summary>
    internal const float SpeakingDb = -22f;
    private readonly SpeechLeveler _leveler = new(SpeakingDb);
    private readonly MicSense _sense = new();
    private readonly LiveTune _live = new();
    private int? _liveLimit;
    private float _addedDb;
    private MyVoice? _myVoice;
    private Task<MyVoice?>? _myVoiceLoading;
    private Task<(int Teach, float[]? Print)>? _relearning;
    private int _myVoiceRetryIn, _myVoiceTeach;
    private VoiceFocus? _focus;
    // The kept tuning for the microphone open now, read again only when the mic or the kept tunings change.
    private (string? Mic, Dictionary<string, string>? Tunes, VoiceTuning? Tuning) _kept;

    /// <summary>A tuning to use instead of the microphone's kept one (Voice Tune's take, before it is kept).</summary>
    internal VoiceTuning? Tuning { get; init; }

    /// <summary>
    /// Whether the microphone's own noise suppression stands in for Exo's (<see cref="MicStepsAside"/>, read off the audio
    /// thread and set here: by Voice Tune, the meter, and a call as it starts or its mic changes).
    /// </summary>
    internal bool? StepsAside { get; set; }

    /// <summary>
    /// Muted: Voice Focus drops the frame it holds, so unmuting never sends sound from before, and the gate's hold ends
    /// (cut at once, no fade: mute is meant to be instant); the first frame after it fades in.
    /// </summary>
    internal void Mute()
    {
        _focus?.Mute();
        _gate.Mute();
        _sending = false;
        _leadCount = 0;
        _leadReady = false;
    }

    /// <summary>What ran on the last frame (<see cref="Process"/> sets it).</summary>
    internal VoiceStages Stages { get; private set; }

    /// <summary>
    /// A recording's chain (<see cref="RecordedVoice"/>), not a call's: the call's noise suppression and level, but no gate
    /// or fade (the voice runs on whole).
    /// </summary>
    internal bool Recording { get; init; }

    /// <summary>
    /// A saved clip being cleaned, not a microphone (<see cref="ClipVoice"/>): frames come faster than they were spoken,
    /// with all the time they need. It never runs light (a slow run here costs nothing), and Only My Voice judges each
    /// frame in turn and learns nothing (what a clip holds was heard once already).
    /// </summary>
    internal bool Offline { get; init; }

    /// <summary>
    /// The taps on a call's chain (<see cref="Listen"/>): each frame cleaned but not yet faded, led in or
    /// focused, with the gate's verdict and the chain's delay then, so one noise suppression serves both. Read at once:
    /// the frame goes on to be sent.
    /// </summary>
    internal Action<float[], bool, int>? Cleaned { get; private set; }

    private readonly Lock _taps = new();

    /// <summary>A recording hears the call's voice from here on (<see cref="RecordedVoice"/>).</summary>
    internal void Listen(Action<float[], bool, int> tap)
    {
        lock (_taps) Cleaned += tap;
    }

    /// <summary>That recording stopped hearing it.</summary>
    internal void Unlisten(Action<float[], bool, int> tap)
    {
        lock (_taps) Cleaned -= tap;
    }

    /// <summary>
    /// Where the user's voice is kept for learning it (<see cref="TeachTake"/>): each frame the gate's own rules call voice
    /// (not its hold), as Only My Voice judges it, after noise suppression and before the voice's shape and leveler.
    /// </summary>
    internal TeachTake? Teach { get; set; }


    /// <summary>What the noise gate did since the last read, with the lead-in frames sent ahead of its openings (<see cref="GateStats"/>).</summary>
    internal GateStats TakeGateStats()
    {
        var stats = _gate.TakeStats();
        (stats.Lead, _leadSent) = (_leadSent, 0);
        return stats;
    }

    private int _leadSent;

    /// <summary>Whether the gate let the last frame through, before Only My Voice held it back (Voice Check learns from these).</summary>
    internal bool Voiced { get; private set; }

    /// <summary>What the microphone's own sound says about it, and what the chain adds to it (<see cref="MicSense"/>).</summary>
    internal MicHeard Heard => _sense.Reading(_addedDb);

    /// <summary>The last frame's level as the gate judged it (dBFS): after noise suppression, before gain control.</summary>
    internal float GateLevel => _gate.Level;

    /// <summary>
    /// How far behind the microphone the last frame's sound came out of the chain, in samples at 48 kHz: noise
    /// suppression's measured delay and Voice Focus's, where they ran.
    /// </summary>
    internal int DelaySamples { get; private set; }

    /// <summary>Runs one frame through in place; whether the gate is open (voice to send).</summary>
    internal bool Process(float[] frame, VoiceProcessing processing, string? micId, bool systemGain)
    {
        var exo = processing.Profile == "exo";
        // The microphone as it came: how loud the voice is, the quiet between words, clipping (MicSense).
        _sense.Hear(frame);
        var tuning = exo ? Tuning ?? Kept(micId) : null;
        // No kept tuning: listen to this raw frame, and set this call's limit after the gate has judged it.
        var learning = exo && tuning is null && !Recording;
        if (learning) _live.Sample(frame);
        // A kept Voice Tune for this microphone (or Voice Tune's own take before it is kept): its gain and its
        // suppression limit, so a quiet mic's voice reaches the chain at the level it is meant to be heard at. The gain
        // adds only what the voice still needs, gliding 5 dB a second so it never steps: none once the mic itself is
        // turned up to it, and none twice where keeping the tuning already set it on Windows' input level (added again on
        // every frame, it brought a voice up to 20 dB hot, and the voice's shape squashed it).
        // With no tuning kept, a microphone too quiet to reach the leveler (one whose own gain can't go up) is lifted by
        // Exo itself into the leveler's reach (up to 18 dB), so suppression and the gate judge a voice, not a whisper.
        var want = tuning is { } t ? _sense.Missing(t.GainDb, SpeakingDb) : exo ? _sense.AutoMissing() : 0f;
        _addedDb += Math.Clamp(want - _addedDb, -0.1f, 0.1f);
        if (MathF.Abs(_addedDb) >= 0.05f)
        {
            var gain = MathF.Pow(10, _addedDb / 20);
            for (var i = 0; i < frame.Length; i++) frame[i] *= gain;
        }
        // First, the low cut: what is under a voice (a desk's knocks through the stand, rumble, breath pops) never reaches
        // gain control, suppression or the gate, which read it as sound to send.
        _lowCut.Process(frame);
        // A knock is judged in the frame it comes out of suppression in, its delay later.
        _knocks = (_knocks << 1) | (_knock.Hit(frame) ? 1u : 0u);
        // Exo Voice's own AI runs DeepFilterNet's default thresholds and its post-filter: that chain won the
        // voice-chain evaluation (a WebRTC pass first, and low thresholds, each distorted more than they caught).
        var limit = Math.Min(tuning?.SuppressionLimitDb ?? _liveLimit ?? NoiseSuppressor.MostDb, NoiseSuppressor.MostDb);
        var suppression = "off";
        float? heard = null, speech = null;
        DelaySamples = 0;
        // A mic that cleans itself (NVIDIA Broadcast, AMD Noise Suppression and the like): Exo's steps aside, so nothing runs twice.
        if (processing.NoiseSuppression && StepsAside == true) suppression = "mic";
        else if (processing.NoiseSuppression && processing.Suppressor == "standard")
        {
            if ((_standard ??= WebRtcNoise.Create()) is { } standard)
            {
                standard.Process(frame);
                heard = standard.InputDb;
                suppression = "standard";
            }
            else suppression = "missing";
        }
        else if (processing.NoiseSuppression && NeuralSuppressor(processing) is { } neural)
        {
            neural.Process(frame, limit);
            heard = neural.InputDb;
            speech = neural.SpeechDb;
            suppression = neural.Tier switch
            {
                VoiceModelTier.VoxLockTse => "voxlock",
                VoiceModelTier.DpdfNet48kHr => "exo-hq",
                _ => neural.Load.Light ? "light" : "exo-v2",
            };
            DelaySamples = neural.DelaySamples;
        }
        // Exo Voice and Custom's AI run Exo's own chain: DeepFilterNet plus Exo's stages.
        else if (processing.NoiseSuppression && Suppressor() is { } suppressor)
        {
            suppressor.Process(frame, limit);
            heard = suppressor.InputDb;
            speech = suppressor.SpeechDb;
            suppression = suppressor.Load.Light ? "light" : "on";
            DelaySamples = suppressor.DelaySamples;
        }
        // Exo's own isn't here, but Windows' cleans the stream (VoiceAudio turned it on): suppression runs, and the gate
        // isn't held shut as it is while Exo's loads.
        else if (processing.NoiseSuppression) suppression = VoiceAudio.WindowsSuppresses ? "windows" : _missing ? "missing" : "loading";
        var agc2 = processing.MicAutoGain && !processing.Focus ? Agc2() : null;
        Stages = new VoiceStages(suppression, limit, processing.Focus, agc2 is not null ? "agc2" : systemGain ? "windows" : exo ? "exo" : "off", tuning is not null);
        var focus = processing.Focus && !Recording;
        var cleaned = Cleaned;
        var open = false;
        var held = false;
        if (!focus || cleaned is not null)
        {
            // The gate judges what suppression left (Discord's gate reads Krisp's verdict the same way), against the sound it came from.
            var behind = Math.Min(DelaySamples / Opus.FrameSamples, 30);
            var knock = (_knocks >> behind & 1) != 0;
            open = _gate.Voiced(frame, processing.AutoSensitivity, processing.Threshold, heard, speech, knock);
            // While suppression is still loading the raw room would pass the gate: hold everything until it is on
            // (the first seconds of a call went out uncleaned before, fan and all).
            if (suppression == "loading") open = false;
            // Only My Voice: a voice judged not the user's is held back (MyVoice), judged on what the gate let through.
            // With a voiceprint the judging runs whether or not Only My Voice is on, so it keeps learning the user's
            // voice (VoiceprintLearning); holding back is Only My Voice's alone.
            if (!focus)
            {
                if ((processing.OnlyMyVoice || Voiceprint.Learned) && Mine() is { } mine)
                {
                    mine.Feed(frame, open, processing.OnlyMyVoice, AsLoudAs(_gate.Level, _yourDb));
                    if (Offline) mine.Settle();
                    // A voice that was going out ends on this frame, faded to silence as the gate's own close fades it
                    // (cut off mid-word, the other side heard a click each time); one that wasn't stays shut. Only a voice
                    // quieter than the user's own is held back (see AsLoudAs).
                    if (processing.OnlyMyVoice && open && mine.HoldingBack && !AsLoudAs(_gate.Level, _yourDb)) (held, open) = (true, _sending);
                    if (open && !held && !mine.HoldingBack) _yourDb = YourLevel(_yourDb, _gate.Level);
                }
                Voiced = open && !held;
                _sense.Judge(_gate.Speaking && !held, !open);
                // The raw frame was sampled before any gain. Count it only once the model is cleaning, so the
                // load's held-shut frames are not mistaken for a silent room.
                if (learning && suppression != "loading"
                    && _live.Classify(micId ?? "default", _gate.Speaking && !held, !open) is { } heardLimit)
                    _liveLimit = heardLimit;
                // Learned from what Only My Voice judges: taught on the sent voice instead (shaped, leveled, and with each
                // sentence's half-second hold after it), it learned a voice Only My Voice never hears.
                Teach?.Add(frame, open && _gate.Speaking && !held);
            }
            cleaned?.Invoke(frame, open && !held, DelaySamples);
        }
        if (!focus && exo)
        {
            // Exo Voice's voice as it is sent: EQ and a soft compressor shape it, and the leveler evens it toward one
            // speaking level, stood down where Windows' own gain control already rides it (Voice Focus has its own).
            // None and a custom chain send the voice as the gate left it.
            _shape.Process(frame);
            if (agc2 is null && !systemGain) _leveler.Process(frame, open && !held);
        }
        if (!focus && agc2 is not null) agc2.Process(frame);
        if (focus)
        {
            _focus ??= new VoiceFocus();
            DelaySamples += VoiceFocus.DelaySamples;
            return _focus.Process(frame, processing.AutoSensitivity, processing.Threshold, false);
        }
        // Push to Talk sends by its key, whatever the gate says: nothing to fade or lead in.
        if (!processing.PushToTalk && !Recording)
        {
            Fade(frame, open, closing: _gate.Closing || held);
            Lead(frame, open, processing.AutoSensitivity);
        }
        return open;
    }

    // The gate's release (and Only My Voice's, on the frame it starts holding back): the hold's last frame fades out over its 20 ms (a raised cosine, to silence on its last
    // sample), so a voice never ends on a step the other side hears as a click, and Opus's last frame ends at silence.
    // The next opening fades in over its first 2.5 ms from there. Mute cuts at once instead (see Mute).
    private static readonly float[] Release = Ramp(Opus.FrameSamples, down: true), Attack = Ramp(120, down: false);
    private bool _sending;

    private void Fade(float[] frame, bool open, bool closing)
    {
        // An opening with a lead-in fades in at the lead-in's first frame instead (see Lead).
        var opening = _leadCount > 0 ? _lead[(_leadAt - _leadCount + Ring) % Ring] : frame;
        if (open && !_sending)
            for (var i = 0; i < Attack.Length; i++) { opening[2 * i] *= Attack[i]; opening[2 * i + 1] *= Attack[i]; }
        if (open && closing)
            for (var i = 0; i < Release.Length; i++) { frame[2 * i] *= Release[i]; frame[2 * i + 1] *= Release[i]; }
        // Faded out to silence: whatever opens next fades in from there.
        _sending = open && !closing;
    }

    // Discord's vadLeading (5 frames): the 100 ms before the sound that opened the gate are kept, and go out just ahead of
    // the frame that opened it, so a word's first sound (its consonant, under what opens the gate) is never cut. The
    // automatic gate opens on the sound's second frame (VoiceGate.Confirm), so its lead-in takes the first one as well.
    // Kept only while shut.
    internal const int LeadFrames = 5;
    private const int Ring = LeadFrames + VoiceGate.Confirm;
    private readonly float[][] _lead = [.. Enumerable.Range(0, Ring).Select(_ => new float[VoiceAudio.FrameFloats])];
    private int _leadAt, _leadCount;
    private bool _leadReady;

    private void Lead(float[] frame, bool open, bool automatic)
    {
        if (open) { _leadReady = !_leadReady && _leadCount > 0 && !_wasOpen; _wasOpen = true; if (!_leadReady) _leadCount = 0; return; }
        _wasOpen = false;
        _leadReady = false;
        frame.CopyTo(_lead[_leadAt], 0);
        _leadAt = (_leadAt + 1) % Ring;
        _leadCount = Math.Min(_leadCount + 1, LeadFrames + (automatic ? VoiceGate.Confirm : 0));
    }

    private bool _wasOpen;

    /// <summary>
    /// On the frame that opened the gate: the frames just before it to send first, oldest first (none otherwise). Each
    /// is the chain's own buffer, good until the next frame. Every other frame gets the shared empty array: the voice
    /// thread asks on each frame it sends, and an iterator here was an allocation each time.
    /// </summary>
    internal float[][] TakeLead()
    {
        if (!_leadReady) return [];
        _leadReady = false;
        _leadSent += _leadCount;
        var lead = new float[_leadCount][];
        for (var i = 0; i < lead.Length; i++) lead[i] = _lead[(_leadAt - lead.Length + i + Ring) % Ring];
        _leadCount = 0;
        return lead;
    }

    private static float[] Ramp(int samples, bool down)
    {
        var ramp = new float[samples];
        for (var i = 0; i < samples; i++)
            ramp[i] = down ? 0.5f * (1 + MathF.Cos(MathF.PI * (i + 1) / samples)) : 0.5f * (1 - MathF.Cos(MathF.PI * i / samples));
        return ramp;
    }

    /// <summary>
    /// Loads noise suppression now, on this thread (Hear your voice and Voice Tune, before their first frame, so it is
    /// cleaned like every other, with the same model and thresholds a call runs). A call needn't: its chain loads it
    /// off the audio thread while the first frames pass. Whether noise suppression can run as asked.
    /// </summary>
    internal bool Prepare(VoiceProcessing processing)
    {
        if (Offline && processing.OnlyMyVoice && MyVoice.Create() is { } mine)
        {
            mine.Learns = false;
            _myVoice = mine;
        }
        if (!processing.NoiseSuppression || processing.Suppressor == "standard") return true;
        _suppressor ??= NoiseSuppressor.Create();
        if (_suppressor is not null) _suppressor.NeverLight = Offline;
        return _suppressor is not null;
    }

    // Only My Voice holds back only a voice clearly quieter than the user's own at their microphone: someone across the
    // room or a TV reaches a close mic 10 dB and more under the voice talking into it, while the user's own voice in a game
    // (shouting, excited) is as loud or louder, and was what the voiceprint misjudged and held back, again and again, while
    // they played. Their level is learned from what goes out as theirs, from the level a tuned mic brings speech to.
    internal const float CloseDb = 8f;
    private float _yourDb = SpeechLeveler.TargetDb;

    /// <summary>A frame at <paramref name="db"/> is as loud as the user's own voice at <paramref name="yourDb"/>: never held back.</summary>
    internal static bool AsLoudAs(float db, float yourDb) => db >= yourDb - CloseDb;

    /// <summary>The user's level after a frame of theirs: about the last 1.5 s of talking (75 frames), kept to a voice's range so a shout or a whisper never runs off with it.</summary>
    internal static float YourLevel(float yourDb, float db) => db > -70f ? Math.Clamp(yourDb + (db - yourDb) / 75, -40f, -10f) : yourDb;

    // The voiceprint and model are read off the audio thread; until they are, nothing is held back.
    /// <summary>Only My Voice's safety stop came in this chain: it held back the user's own speech and stopped holding back.</summary>
    internal bool MyVoiceStopped => _myVoice?.Stopped == true;

    private MyVoice? Mine()
    {
        if (Offline) return _myVoice;
        // Learned again (Exo Voice relearns the voice after a safety stop, or Start over): the new voiceprint, read off the
        // audio thread, goes into the judge already listening, so the voice it holds back now stays held while it loads
        // and after; its stop starts afresh, so Only My Voice comes back within the call.
        if (_myVoice is not null && _myVoiceTeach != Voiceprint.Teaches)
        {
            if (_relearning is null) _relearning = Task.Run(() => (Voiceprint.Teaches, Voiceprint.Load()));
            else if (_relearning.IsCompleted)
            {
                // One that can't be read keeps the voiceprint it had until the next learn.
                if (_relearning.IsCompletedSuccessfully)
                {
                    if (_relearning.Result.Print is { } print) _myVoice.Relearned(print);
                    _myVoiceTeach = _relearning.Result.Teach;
                }
                _relearning = null;
            }
        }
        if (_myVoice is not null) return _myVoice;
        if (_myVoiceLoading is null && --_myVoiceRetryIn <= 0)
        {
            _myVoiceTeach = Voiceprint.Teaches;
            _myVoiceLoading = Task.Run(MyVoice.Create);
        }
        if (_myVoiceLoading is not { IsCompleted: true } done) return null;
        _myVoiceLoading = null;
        _myVoice = done.IsCompletedSuccessfully ? done.Result : null;
        if (_myVoice is null) _myVoiceRetryIn = 250; // the model or voiceprint isn't readable: looked for again in 5 s
        return _myVoice;
    }

    private uint _knocks;

    private NeuralNoiseSuppressor? NeuralSuppressor(VoiceProcessing processing)
    {
        if (Offline || !ExoVoiceMl.EnsureLoaded()) return null;
        if (!string.Equals(_lastVoiceTierPref, processing.VoiceTier, StringComparison.Ordinal))
        {
            _lastVoiceTierPref = processing.VoiceTier;
            _hqOverBudget = false;
        }
        var gameInForeground = GameRunningNow();
        var want = VoiceTierPolicy.SelectTier(
            processing.Suppressor,
            processing.VoiceTier,
            VoiceModels.BaselineReady(),
            VoiceModels.HighQualityReady(),
            VoiceModels.VoxLockReady(),
            processing.OnlyMyVoice && processing.VoiceIsolation,
            gameInForeground,
            gpuHeadroomMs: gameInForeground ? 0.0 : 1.0,
            cpuLogicalCores: Environment.ProcessorCount);
        if (_hqOverBudget && want == VoiceModelTier.DpdfNet48kHr && VoiceModels.BaselineReady())
            want = VoiceModelTier.DpdfNetBaseline;
        if (want == VoiceModelTier.FallbackDfn3)
        {
            if (_neural is not null)
            {
                _neural.Dispose();
                _neural = null;
                _neuralTier = VoiceModelTier.FallbackDfn3;
            }
            return null;
        }
        if (_neural is not null)
        {
            if (_neuralTier == want)
            {
                if (_neuralTier == VoiceModelTier.DpdfNet48kHr && _neural.MeasuredMsPer10Ms > 0 && !VoiceTierPolicy.MeetsCpuBudget(_neural.MeasuredMsPer10Ms) && VoiceModels.BaselineReady())
                {
                    _hqOverBudget = true;
                    _neural.Dispose();
                    _neural = null;
                    _neuralTier = VoiceModelTier.FallbackDfn3;
                    _neuralRetryIn = 0;
                    _neuralLoading = Task.Run(() => NeuralNoiseSuppressor.Create(VoiceModelTier.DpdfNetBaseline));
                    return null;
                }
                return _neural;
            }
            _neural.Dispose();
            _neural = null;
            _neuralTier = VoiceModelTier.FallbackDfn3;
        }
        if (_neuralLoading is { IsCompleted: true } done)
        {
            _neuralLoading = null;
            _neural = done.IsCompletedSuccessfully ? done.Result : null;
            if (_neural is not null)
                _neuralTier = _neural.Tier;
            else
                _neuralRetryIn = 100;
            return _neural;
        }
        if (_neuralLoading is null && --_neuralRetryIn <= 0)
            _neuralLoading = Task.Run(() => NeuralNoiseSuppressor.Create(want));
        return null;
    }

    private static bool GameRunningNow()
    {
        try { return Adapters.SteamLoginUsers.IsGameRunning(); }
        catch { return false; }
    }

    // DeepFilterNet's model takes a moment to load (and its plugin a hash check the first time): never on the audio
    // thread. Until it is in, frames pass as they are; while the plugin isn't here (still downloading), it is looked
    // for again each second.
    private NoiseSuppressor? Suppressor()
    {
        if (_suppressor is not null) return _suppressor;
        if (_loading is { IsCompleted: true } done)
        {
            _loading = null;
            _suppressor = done.IsCompletedSuccessfully ? done.Result : null;
            _missing = _suppressor is null;
            if (_missing) _retryIn = 50;
            return _suppressor;
        }
        if (_loading is null && --_retryIn <= 0) _loading = Task.Run(NoiseSuppressor.Create);
        return null;
    }

    private Task<NoiseSuppressor?>? _loading;
    private int _retryIn;
    private bool _missing;

    /// <summary>Whether the chosen microphone's own noise suppression is as good as Exo's (reads the device: not for the audio thread).</summary>
    internal static bool MicStepsAside(string? chosen) =>
        AudioDevices.Identify(chosen) is { } id && MicCatalog.StandsDown(MicCatalog.Describe(id), MicStage.NoiseSuppression);

    private VoiceTuning? Kept(string? mic)
    {
        var tunes = App.Services.Settings.Shared.DiscordVoiceTunes;
        if (mic != _kept.Mic || !ReferenceEquals(tunes, _kept.Tunes)) _kept = (mic, tunes, VoiceTune.For(mic));
        return _kept.Tuning;
    }


    private WebRtcAgc2? Agc2()
    {
        if (_agc2 is not null || _agc2Failed) return _agc2;
        _agc2 = WebRtcAgc2.Create();
        if (_agc2 is null) _agc2Failed = true;
        return _agc2;
    }

    public void Dispose()
    {
        _standard?.Dispose();
        _standard = null;
        _agc2?.Dispose();
        _agc2 = null;
        _suppressor?.Dispose();
        _suppressor = null;
        _neural?.Dispose();
        _neural = null;
        _neuralLoading?.ContinueWith(t => t.Result?.Dispose(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        _neuralLoading = null;
        // One still loading is let go of when it is in.
        _loading?.ContinueWith(t => t.Result?.Dispose(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        _loading = null;
    }
}

/// <summary>
/// The microphone's low cut: a 4th-order Butterworth high-pass at 80 Hz (two biquads, as WebRTC's), per channel of the
/// interleaved stereo frame. A voice's lowest fundamental is about 85 Hz; a knock on the desk, a stand's rumble and a
/// pop are mostly under it.
/// </summary>
internal sealed class LowCut
{
    private const double Hz = 80, Rate = 48000;
    private readonly double[] _b = new double[3], _a = new double[2];
    // Per channel, per stage: x1, x2, y1, y2.
    private readonly double[,] _state = new double[4, 4];

    internal LowCut()
    {
        var w = 2 * Math.PI * Hz / Rate;
        var alpha = Math.Sin(w) / (2 * 0.7071);
        var cos = Math.Cos(w);
        var a0 = 1 + alpha;
        _b[0] = (1 + cos) / 2 / a0; _b[1] = -(1 + cos) / a0; _b[2] = (1 + cos) / 2 / a0;
        _a[0] = -2 * cos / a0; _a[1] = (1 - alpha) / a0;
    }

    internal void Process(float[] stereo)
    {
        Channel(stereo, 0, 0);
        Channel(stereo, 1, 2);
    }

    // One channel through its two stages, the same sums in the same order, with the state in locals for the frame: each
    // sample read and wrote the two-dimensional array a dozen times, and that was most of the low cut's 35 us a frame.
    private void Channel(float[] stereo, int offset, int stage)
    {
        double b0 = _b[0], b1 = _b[1], b2 = _b[2], a1 = _a[0], a2 = _a[1];
        double px1 = _state[stage, 0], px2 = _state[stage, 1], py1 = _state[stage, 2], py2 = _state[stage, 3];
        double qx1 = _state[stage + 1, 0], qx2 = _state[stage + 1, 1], qy1 = _state[stage + 1, 2], qy2 = _state[stage + 1, 3];
        for (var i = 0; i + 1 < stereo.Length; i += 2)
        {
            double x = stereo[i + offset];
            var y = b0 * x + b1 * px1 + b2 * px2 - a1 * py1 - a2 * py2;
            px2 = px1; px1 = x; py2 = py1; py1 = y;
            var z = b0 * y + b1 * qx1 + b2 * qx2 - a1 * qy1 - a2 * qy2;
            qx2 = qx1; qx1 = y; qy2 = qy1; qy1 = z;
            stereo[i + offset] = (float)z;
        }
        (_state[stage, 0], _state[stage, 1], _state[stage, 2], _state[stage, 3]) = (px1, px2, py1, py2);
        (_state[stage + 1, 0], _state[stage + 1, 1], _state[stage + 1, 2], _state[stage + 1, 3]) = (qx1, qx2, qy1, qy2);
    }
}

/// <summary>
/// A knock, tap or click (the desk through the mic's stand, a key, the mouse): sound that jumps more than 10 dB within 5 ms
/// from what came just before, to a level that matters (-55 dBFS and up), and has no pitch. A word can start that fast in a
/// quiet room too, but a voice repeats itself (its pitch, 80 to 400 Hz: the frame's normalized autocorrelation at that
/// lag is high) where a knock is noise. Read on the microphone's sound after the low cut, before anything else.
/// </summary>
internal sealed class KnockDetector
{
    private const int Block = 240; // 5 ms of a channel at 48 kHz
    // 10 dB in 5 ms: a light tap or a mouse click over a fan's noise jumps that much (20 let them through); a voice that
    // starts as fast has a pitch and is told apart by it.
    private const float Jump = 10f, Loud = -55f;
    private float _last = -100f;

    /// <summary>Whether a knock begins in this 20 ms stereo frame.</summary>
    internal bool Hit(float[] stereo)
    {
        var hit = false;
        var samples = stereo.Length / 2;
        for (var start = 0; start + Block <= samples; start += Block)
        {
            double sum = 0;
            for (var i = start; i < start + Block; i++) sum += stereo[i * 2] * (double)stereo[i * 2];
            var db = (float)(10 * Math.Log10(sum / Block + 1e-10));
            if (db >= Loud && db - _last > Jump) hit = true;
            _last = db;
        }
        return hit && !Pitched(stereo, samples);
    }

    // The frame's normalized autocorrelation at any lag of 2.5 to 12.5 ms (400 to 80 Hz) over 0.5: a voice's pitch.
    private static bool Pitched(float[] stereo, int samples)
    {
        for (var lag = 120; lag <= 600 && lag < samples / 2; lag += 4)
        {
            double cross = 0, a = 0, b = 0;
            for (var i = 0; i + lag < samples; i++)
            {
                double x = stereo[i * 2], y = stereo[(i + lag) * 2];
                cross += x * y; a += x * x; b += y * y;
            }
            if (cross / Math.Sqrt(a * b + 1e-20) > 0.5) return true;
        }
        return false;
    }
}
