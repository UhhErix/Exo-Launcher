using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// The live microphone in Discord settings > Voice & Video: while the page shows it, the chosen microphone is opened
/// (shared, alongside a call if there is one, as a call opens it) and run through the call's own chain
/// (<see cref="VoiceChain"/>, with the kept tuning), so what it shows is what a call sends. About 16 times a second
/// discord.micLevel carries the loudest level going in, as the gate judged it (after noise suppression: the Input
/// Sensitivity meter) and coming out since the last one (dBFS, -100 for silence; out is silence while the gate holds
/// the voice back), whether the gate is open, and what ran on the sound (echo cancellation as a call on these speakers
/// would run it: this meter plays nothing, so there is no echo to take out). The page
/// renews it every few seconds; left alone for 15 seconds (the page closed), it stops by itself.
/// </summary>
internal static class MicTest
{
    private static readonly Lock Gate = new();
    private static VoiceAudio? _audio;
    private static VoiceChain? _chain;
    private static Timer? _watchdog;
    private static long _renewedAt;
    private static volatile bool _onHead;
    private static bool _monitor;

    // The meter's own voice in the speaker mix while Hear Your Voice is on: never a call's SSRC.
    private const uint MonitorSsrc = 0x45584F;

    /// <summary>Whether the speakers play back the voice as the call sends it (Hear Your Voice), and whether they are headphones.</summary>
    internal static (bool On, bool OnHead) Monitoring => (_monitor, _onHead);

    /// <summary>
    /// Hear Your Voice: the mic's frames, processed exactly as a call sends them, mixed onto the speakers. With the
    /// speakers not on the head the echo canceller is up too (it only exists when both sides run), so the played-back
    /// voice doesn't come straight back through the mic; a real howl is still possible on loud speakers.
    /// </summary>
    internal static void Monitor(bool on)
    {
        lock (Gate)
        {
            if (_monitor == on) return;
            _monitor = on;
            AppLog.Info($"Discord voice: hear your voice {(on ? "on" : "off")}.");
        }
        // Whether the speakers are on your head goes back to the page with this call: read now (one property of the
        // endpoint). The render side (and its echo canceller) can't join a capture already running: the meter restarts.
        if (on) ReadOutput();
        Later(() => { if (on || _audio is not null) { StopAudio(); Open(); } });
    }

    // Opening and closing the microphone and speakers (and waiting up to a second each for their threads to stop) never
    // runs on the caller's thread, which is the page's UI thread, and runs in the order it was asked.
    private static readonly Lock WorkGate = new();
    private static Task _work = Task.CompletedTask;

    private static void Later(Action work)
    {
        lock (WorkGate)
            _work = _work.ContinueWith(_ =>
            {
                try { work(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { AppLog.Warn("Discord voice: the meter didn't open or close (" + ex.GetType().Name + ": " + ex.Message + ")."); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>The page shows the meter: it opens (off the caller's thread), or a running one is renewed.</summary>
    internal static void Start()
    {
        lock (Gate)
        {
            _renewedAt = Environment.TickCount64;
            if (_audio is not null) return;
        }
        Later(Open);
    }

    private static void Open()
    {
        lock (Gate)
        {
            _renewedAt = Environment.TickCount64;
            if (_audio is not null) return;
            ReadOutput();
            var audio = new VoiceAudio();
            // A mic that cleans itself: Exo's noise suppression steps aside here as in a call, so the meter hears what friends do.
            var chain = new VoiceChain { StepsAside = VoiceChain.MicStepsAside(App.Services.Settings.Shared.DiscordInputDevice) };
            var frames = 0;
            float before = -100f, judged = -100f, after = -100f;
            var voiced = false;
            (VoiceStages, string) shown = default;
            object? stages = null;
            // The last 15 s of voice as the chain cleaned it, kept so Teach Exo My Voice learns from live talking, not a
            // recording (a teach takes eight seconds of speech; a call keeps its own, TeachTake).
            var take = new TeachTake { Learns = true };
            chain.Teach = take;
            lock (Gate) _take = take;
            audio.Captured += frame =>
            {
                var processing = VoiceProcessing.Current;
                before = MathF.Max(before, Level(frame));
                var open = chain.Process(frame, processing, audio.MicId, audio.SystemGain);
                judged = MathF.Max(judged, chain.GateLevel);
                if (open) after = MathF.Max(after, Level(frame));
                if (_monitor && open)
                {
                    // What the call sends: the lead-in ahead of the opening, then the frame itself; held-back sound
                    // queues nothing, so the speakers go quiet exactly as the call does.
                    foreach (var lead in chain.TakeLead()) Play(audio, lead);
                    Play(audio, frame);
                }
                voiced |= open;
                if (++frames % 3 != 0) return;
                // The stages go as they are, rebuilt only when they change.
                var now = (chain.Stages, !processing.EchoCancellation ? "off" : _onHead ? "head" : "on");
                if (stages is null || now != shown)
                {
                    shown = now;
                    stages = Describe(now.Stages, now.Item2, processing);
                }
                // Turned into JSON and posted to the page on the pool, not on the voice thread, 17 times a second.
                var level = new { level = MathF.Round(before, 1), gateLevel = MathF.Round(judged, 1), after = MathF.Round(after, 1), voiced, stages, heard = Describe(chain.Heard) };
                ThreadPool.UnsafeQueueUserWorkItem(static level => DiscordSession.Publish("discord.micLevel", level), (object)level, preferLocal: false);
                (before, judged, after, voiced) = (-100f, -100f, -100f, false);
            };
            audio.Start(capture: true, render: _monitor);
            _audio = audio;
            _chain = chain;
            // The page renews every 5 s but a throttled WebView can drop several: outlast 45 s of misses, not 15.
            _watchdog = new Timer(_ => { if (Environment.TickCount64 - _renewedAt > 45_000) Stop(); }, null, 5000, 5000);
        }
    }

    internal static void Stop()
    {
        _monitor = false; // the page left: hear your voice ends with the meter
        Later(StopAudio);
    }

    private static TeachTake? _take;

    /// <summary>The meter's last 15 s of voice, while it runs (<see cref="TeachTake"/>).</summary>
    internal static TeachTake? Take()
    {
        lock (Gate) return _take;
    }

    private static void StopAudio()
    {
        VoiceAudio? audio;
        VoiceChain? chain;
        lock (Gate)
        {
            (audio, chain) = (_audio, _chain);
            (_audio, _chain) = (null, null);
            _take = null;
            _watchdog?.Dispose();
            _watchdog = null;
        }
        audio?.Dispose();
        // After the capture thread stopped: it is the chain's only user.
        chain?.Dispose();
    }

    private static void Play(VoiceAudio audio, float[] frame)
    {
        var copy = audio.Rent();
        frame.CopyTo(copy, 0);
        audio.Play(MonitorSsrc, copy, level: false);
    }

    /// <summary>Speakers were chosen (or the page opened): whether they are on your head, read off the audio thread.</summary>
    internal static void OutputChanged() => Later(ReadOutput);

    private static void ReadOutput() => _onHead = AudioDevices.OnYourHead(App.Services.Settings.Current.DiscordOutputDevice);

    /// <summary>A new microphone was chosen: the meter follows it (off the caller's thread).</summary>
    internal static void Reopen() => Later(() =>
    {
        var stepsAside = VoiceChain.MicStepsAside(App.Services.Settings.Shared.DiscordInputDevice);
        lock (Gate)
        {
            if (_chain is not null) _chain.StepsAside = stepsAside;
            _audio?.Reopen(captureSide: true);
        }
    });

    private static float Level(float[] frame)
    {
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        return MathF.Max(-100f, 10 * MathF.Log10((float)(sum / frame.Length) + 1e-10f));
    }

    // What the mic's own sound says, rounded to whole dB: the page shows it as it is.
    private static object Describe(MicHeard h) => new
    {
        speechDb = h.SpeechDb is { } s ? MathF.Round(s) : (float?)null,
        quietDb = h.QuietDb is { } q ? MathF.Round(q) : (float?)null,
        clipping = h.Clipping, quiet = h.Quiet, gated = h.Gated, cleaned = h.Cleaned,
        addedDb = MathF.Round(h.AddedDb),
    };

    private static object Describe(VoiceStages s, string echo, VoiceProcessing processing) => new
    {
        profile = processing.Profile,
        suppression = s.Suppression,
        suppressionLimitDb = s.SuppressionLimitDb,
        focus = s.Focus,
        gain = s.Gain,
        echo,
        tuned = s.Tuned,
    };
}
