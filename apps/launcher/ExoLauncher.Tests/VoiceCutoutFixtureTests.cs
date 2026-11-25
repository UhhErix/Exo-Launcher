using System.Globalization;
using System.Reflection;
using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

[Collection("DeepFilterNet")]
public sealed class VoiceCutoutFixtureTests
{
    private static readonly string? ConvoPath = Environment.GetEnvironmentVariable("EXO_VOICE_SPEECH_WAV");
    private static readonly string OutDir = Path.GetTempPath();

    private static float[] LoadWav48kMono(string path)
    {
        var b = File.ReadAllBytes(path);
        var channels = BitConverter.ToInt16(b, 22);
        var bits = BitConverter.ToInt16(b, 34);
        var at = b.Length;
        for (var i = 12; i + 8 <= b.Length; i += 8 + BitConverter.ToInt32(b, i + 4))
            if (b.AsSpan(i, 4).SequenceEqual("data"u8)) { at = i + 8; break; }
        var frames = (b.Length - at) / (bits / 8) / channels;
        var pcm = new float[frames];
        for (var i = 0; i < frames; i++)
            pcm[i] = BitConverter.ToInt16(b, at + i * channels * 2) / 32768f;
        return pcm;
    }

    private static float[] NormalizePeak(float[] src, double peakDb)
    {
        var peak = 0f;
        foreach (var s in src) peak = MathF.Max(peak, MathF.Abs(s));
        var scale = (float)(Math.Pow(10, peakDb / 20.0) / Math.Max(peak, 1e-6f));
        var dst = new float[src.Length];
        for (var i = 0; i < src.Length; i++) dst[i] = src[i] * scale;
        return dst;
    }

    private static float[] MakeNoise(int samples, double floorDb, double humShare, double fanShare, int seed)
    {
        var rng = new Random(seed);
        var hiss = new float[samples];
        var fan = new float[samples];
        var hum = new float[samples];
        double b0 = 0, b1 = 0, b2 = 0, brown = 0;
        double hissPow = 0, fanPow = 0, humPow = 0;
        for (var n = 0; n < samples; n++)
        {
            var w = rng.NextDouble() * 2 - 1;
            hiss[n] = (float)w;
            hissPow += w * w;
            b0 = 0.99765 * b0 + w * 0.0990460;
            b1 = 0.96300 * b1 + w * 0.2965164;
            b2 = 0.57000 * b2 + w * 1.0526913;
            brown = 0.995 * brown + w * 0.1;
            var f = (0.6 * (b0 + b1 + b2 + w * 0.1848) + 0.6 * brown) * (1 + 0.15 * Math.Sin(2 * Math.PI * 0.7 * n / Opus.SampleRate));
            fan[n] = (float)f;
            fanPow += f * f;
            var t = (double)n / Opus.SampleRate;
            var h = 0.5 * Math.Sin(2 * Math.PI * 60 * t) + 0.5 * Math.Sin(2 * Math.PI * 120 * t) + 0.5 * Math.Sin(2 * Math.PI * 180 * t);
            hum[n] = (float)h;
            humPow += h * h;
        }
        var totalPower = Math.Pow(10, floorDb / 10.0);
        var hissShare = Math.Max(0.0, 1.0 - humShare - fanShare);
        var gHiss = Math.Sqrt(totalPower * hissShare / Math.Max(hissPow / samples, 1e-12));
        var gFan = Math.Sqrt(totalPower * fanShare / Math.Max(fanPow / samples, 1e-12));
        var gHum = Math.Sqrt(totalPower * humShare / Math.Max(humPow / samples, 1e-12));
        var outBuf = new float[samples];
        for (var n = 0; n < samples; n++) outBuf[n] = (float)(hiss[n] * gHiss + fan[n] * gFan + hum[n] * gHum);
        return outBuf;
    }

    private static double RmsDb(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples) sum += s * (double)s;
        return Math.Max(-100.0, 10.0 * Math.Log10(sum / Math.Max(1, samples.Length) + 1e-10));
    }

    [Fact]
    public void MeasureTrueAudibleCutoutsWithLeadIn()
    {
        NoiseSuppressorTests.UseTestPlugin();
        Assert.SkipUnless(ConvoPath is not null && File.Exists(ConvoPath), "set EXO_VOICE_SPEECH_WAV to a 48 kHz mono or stereo speech WAV");
        var speechBase = LoadWav48kMono(ConvoPath!);

        var scenarios = new (string Name, double FloorDb, double HumShare, double FanShare, double SpeechPeakDb)[]
        {
            ("Fifine-clean (-68dB, pk -12)", -68, 0.00, 0.30, -12),
            ("AU-A04 -50dB mix (pk -12)", -50, 0.20, 0.40, -12),
            ("AU-A04 -45dB mix (pk -12)", -45, 0.20, 0.40, -12),
            ("AU-A04 -45dB hum80% (pk -12)", -45, 0.80, 0.10, -12),
            ("AU-A04 -40dB buzz95% (pk -12)", -40, 0.95, 0.05, -12),
            ("AU-A04 -42dB mix (pk -12)", -42, 0.25, 0.40, -12),
            ("AU-A04 -45dB quiet (pk -18)", -45, 0.20, 0.40, -18),
            ("AU-A04 -45dB hum80% quiet (pk -18)", -45, 0.80, 0.10, -18),
            ("AU-A04 -40dB hot (pk -6)", -40, 0.20, 0.40, -6),
        };

        var lines = new List<string>();
        var gateField = typeof(VoiceChain).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var supField = typeof(VoiceChain).GetField("_suppressor", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var micFloorField = typeof(VoiceGate).GetField("_micFloor", BindingFlags.NonPublic | BindingFlags.Instance)!;

        foreach (var sc in scenarios)
        {
            const int leadSilenceSec = 4, trailSilenceSec = 3;
            var leadSamples = leadSilenceSec * Opus.SampleRate;
            var totalSamples = leadSamples + speechBase.Length + trailSilenceSec * Opus.SampleRate;
            var totalFrames = totalSamples / Opus.FrameSamples;
            var frameSamples = Opus.FrameSamples;

            var scaledSpeech = NormalizePeak(speechBase, sc.SpeechPeakDb);
            var noise = MakeNoise(totalSamples, sc.FloorDb, sc.HumShare, sc.FanShare, seed: 42);
            var mixed = new float[totalSamples];
            for (var i = 0; i < totalSamples; i++)
            {
                var sp = i >= leadSamples && i < leadSamples + scaledSpeech.Length ? scaledSpeech[i - leadSamples] : 0f;
                mixed[i] = Math.Clamp(noise[i] + sp, -1f, 1f);
            }

            using var chain = new VoiceChain { StepsAside = false, Offline = true };
            chain.Prepare(VoiceProcessing.ExoVoice);
            var gate = (VoiceGate)gateField.GetValue(chain)!;
            var micFloor = (QuietFloor)micFloorField.GetValue(gate)!;

            var rawOpen = new bool[totalFrames];
            var sentWithLead = new bool[totalFrames];
            var isSpeech = new bool[totalFrames];
            var isPure = new bool[totalFrames];
            var heard = new float[totalFrames];
            var margin = new float[totalFrames];
            var snr = new float[totalFrames];
            var cleaned = new float[totalFrames];
            var frame = new float[VoiceAudio.FrameFloats];

            for (var f = 0; f < totalFrames; f++)
            {
                var srcIdx = f * frameSamples;
                double cleanSpeechDb = -100.0;
                if (srcIdx >= leadSamples && srcIdx + frameSamples <= leadSamples + scaledSpeech.Length)
                    cleanSpeechDb = RmsDb(scaledSpeech.AsSpan(srcIdx - leadSamples, frameSamples));
                isSpeech[f] = cleanSpeechDb > sc.SpeechPeakDb - 35.0;
                isPure[f] = f >= 120 && (srcIdx + frameSamples < leadSamples - 4800 || srcIdx > leadSamples + scaledSpeech.Length + 48000);

                for (var i = 0; i < frameSamples; i++) frame[2 * i] = frame[2 * i + 1] = mixed[srcIdx + i];
                var open = chain.Process(frame, VoiceProcessing.ExoVoice, "au-a04", systemGain: false);
                var lead = chain.TakeLead();
                rawOpen[f] = open;
                if (open)
                {
                    sentWithLead[f] = true;
                    for (var l = 1; l <= lead.Length && f - l >= 0; l++) sentWithLead[f - l] = true;
                }
                var sup = (NoiseSuppressor?)supField.GetValue(chain);
                heard[f] = sup?.InputDb ?? float.NaN;
                snr[f] = sup?.SpeechDb ?? float.NaN;
                margin[f] = float.IsNaN(heard[f]) ? float.NaN : heard[f] - micFloor.Db;
                cleaned[f] = chain.GateLevel;
            }

            int speechCount = 0, rawShut = 0, trueLost = 0, maxLostStreak = 0, curLost = 0, lostEvents = 0;
            int pureNoise = 0, pureNoiseOpen = 0;
            var lostSnr = new List<float>();
            var lostMargin = new List<float>();
            var pureSnr = new List<float>();
            var pureMargin = new List<float>();
            for (var f = 0; f < totalFrames; f++)
            {
                if (isPure[f])
                {
                    pureNoise++;
                    if (rawOpen[f]) pureNoiseOpen++;
                    if (!float.IsNaN(snr[f])) { pureSnr.Add(snr[f]); pureMargin.Add(margin[f]); }
                }
                if (isSpeech[f])
                {
                    speechCount++;
                    if (!rawOpen[f]) rawShut++;
                    if (!sentWithLead[f])
                    {
                        trueLost++;
                        curLost++;
                        if (curLost == 1) lostEvents++;
                        maxLostStreak = Math.Max(maxLostStreak, curLost);
                        if (!float.IsNaN(snr[f])) { lostSnr.Add(snr[f]); lostMargin.Add(margin[f]); }
                    }
                    else curLost = 0;
                }
                else curLost = 0;
            }
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{sc.Name,-35} | speech={speechCount} rawShut={rawShut,3} trueLost={trueLost,3} ({100.0 * trueLost / speechCount,4:0.0}%) maxLost={maxLostStreak,3}f({maxLostStreak * 20,4}ms) events={lostEvents,2} | noiseOpen={pureNoiseOpen,3}/{pureNoise}"));
            lines.Add("   pure-noise snr pct 50/90/99/max: " + Pct(pureSnr) + " | margin pct 50/90/99/max: " + Pct(pureMargin));
            lines.Add("   lost-speech snr pct 10/50/90: " + PctLow(lostSnr) + " | margin pct 10/50/90: " + PctLow(lostMargin));
        }

        File.WriteAllLines(Path.Combine(OutDir, "exo-voice-fixture.txt"), lines);
    }

    private static string Pct(List<float> v)
    {
        if (v.Count == 0) return "none";
        v.Sort();
        string At(double p) => v[Math.Min(v.Count - 1, (int)(p * v.Count))].ToString("0.0", CultureInfo.InvariantCulture);
        return $"{At(0.5)}/{At(0.9)}/{At(0.99)}/{v[^1].ToString("0.0", CultureInfo.InvariantCulture)}";
    }

    private static string PctLow(List<float> v)
    {
        if (v.Count == 0) return "none";
        v.Sort();
        string At(double p) => v[Math.Min(v.Count - 1, (int)(p * v.Count))].ToString("0.0", CultureInfo.InvariantCulture);
        return $"{At(0.1)}/{At(0.5)}/{At(0.9)}";
    }
}
