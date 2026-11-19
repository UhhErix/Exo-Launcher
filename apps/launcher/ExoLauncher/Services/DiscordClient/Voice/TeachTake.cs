using ExoLauncher.Helpers;

namespace ExoLauncher.Services.DiscordClient.Voice;

/// <summary>
/// The last 15 s of the user's voice, however long ago, for learning it. Exo Voice learns it by itself, with no button,
/// while Only My Voice is on (<see cref="Learns"/>): a take holding 15 s of voice teaches it while there is no voiceprint,
/// and again after Only My Voice's safety stop (<see cref="LearnAgain"/>: it held back the user's own speech, so what it
/// learned is wrong). The chain keeps it (<see cref="VoiceChain.Teach"/>) as Only My Voice judges
/// the voice: mono 48 kHz frames after noise suppression, before the voice's shape and leveler, where the gate's own rules
/// heard speech. The live meter keeps one and so does a call. Only voice is kept: a window of the last 20 s held about
/// 8 s of voice in a conversation, so a teach after talking all call long still said to talk first. Fed on the audio
/// thread, read on the page's; nothing is allocated per frame.
/// </summary>
internal sealed class TeachTake
{
    /// <summary>The voice kept: 15 s of it.</summary>
    internal const int Frames = 750;

    private readonly Lock _gate = new();
    private readonly float[][] _ring = [.. Enumerable.Range(0, Frames).Select(_ => new float[Opus.FrameSamples])];
    private int _at, _count;

    /// <summary>The take of the call sending the microphone now, if one is.</summary>
    internal static TeachTake? Call { get; set; }

    /// <summary>This take teaches Exo Voice the voice by itself (a call's and the live meter's do).</summary>
    internal bool Learns { get; init; }

    // One learn at a time, across takes; one that couldn't (too little voice, the model not fetched) waits a minute.
    private static int _learning;
    private static volatile bool _again;
    private static long _nextTry;

    /// <summary>What was learned is wrong (Only My Voice held back the user's own voice): learned again from the next full take.</summary>
    internal static void LearnAgain() => _again = true;

    /// <summary>Frames of voice kept.</summary>
    internal int Voice
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>Keeps one 20 ms stereo frame as the chain left it, if it went out as voice.</summary>
    internal void Add(float[] stereo, bool voiced)
    {
        if (!voiced) return;
        lock (_gate)
        {
            var into = _ring[_at];
            for (var i = 0; i < Opus.FrameSamples; i++) into[i] = (stereo[2 * i] + stereo[2 * i + 1]) * 0.5f;
            _at = (_at + 1) % Frames;
            if (_count < Frames) _count++;
            if (_count < Frames) return;
        }
        if (Learns && (_again || !Voiceprint.Learned) && Environment.TickCount64 >= Volatile.Read(ref _nextTry) && Wanted()) Learn();
    }

    // Only My Voice on, in Exo Voice: the voiceprint is for it alone.
    private static bool Wanted() => App.Services.Settings.Shared is var s && VoiceProcessing.ExoOn(s) && s.DiscordOnlyMyVoice;

    private void Learn()
    {
        if (Interlocked.Exchange(ref _learning, 1) == 1) return;
        var again = _again;
        _again = false;
        _ = Task.Run(async () =>
        {
            string? problem;
            try { problem = Take() is { } take ? await MyVoice.LearnAsync(take.Cleaned, take.Voiced, CancellationToken.None).ConfigureAwait(false) : "nothing heard"; }
            catch (Exception ex) { problem = ex.GetType().Name; }
            if (problem is null) AppLog.Info($"Discord voice: Exo Voice learned your voice {(again ? "again" : "by itself")}.");
            else
            {
                AppLog.Info($"Discord voice: Exo Voice couldn't learn your voice yet ({problem.TrimEnd('.')}).");
                _again |= again;
                Volatile.Write(ref _nextTry, Environment.TickCount64 + 60_000);
            }
            Volatile.Write(ref _learning, 0);
        });
    }

    /// <summary>The voice kept, oldest first (every frame voiced); null before any.</summary>
    internal (float[] Cleaned, bool[] Voiced, int Voice)? Take()
    {
        lock (_gate)
        {
            if (_count == 0) return null;
            var cleaned = new float[_count * Opus.FrameSamples];
            for (var i = 0; i < _count; i++) Array.Copy(_ring[(_at - _count + i + Frames) % Frames], 0, cleaned, i * Opus.FrameSamples, Opus.FrameSamples);
            var voiced = new bool[_count];
            Array.Fill(voiced, true);
            return (cleaned, voiced, _count);
        }
    }

    /// <summary>The meter's take or the call's, whichever holds more voice.</summary>
    internal static (float[] Cleaned, bool[] Voiced, int Voice)? Best(TeachTake? meter, TeachTake? call)
    {
        var (a, b) = (meter?.Take(), call?.Take());
        return a is not { } m ? b : b is not { } c ? a : c.Voice > m.Voice ? c : m;
    }
}
