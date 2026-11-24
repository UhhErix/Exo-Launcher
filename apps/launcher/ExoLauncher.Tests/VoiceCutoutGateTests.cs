using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class VoiceCutoutGateTests
{
    private static float[] AtDb(double db)
    {
        var rms = Math.Pow(10, db / 20);
        var frame = new float[VoiceAudio.FrameFloats];
        for (var i = 0; i < frame.Length; i += 2) frame[i] = frame[i + 1] = (float)(rms * Math.Sqrt(2) * Math.Sin(2 * Math.PI * 200 * (i / 2) / 48000.0));
        return frame;
    }

    [Fact]
    public void AQuietWordOverANoisyCondensersFloorOpensTheGate()
    {
        var gate = new VoiceGate();
        var random = new Random(7);
        for (var f = 0; f < 300; f++)
        {
            var heard = -45 + (float)(random.NextDouble() * 2 - 1);
            Assert.False(gate.Voiced(AtDb(-100), automatic: true, -60, before: heard, speech: -15f), $"room frame {f}");
        }
        var opened = -1;
        for (var f = 0; f < 20 && opened < 0; f++)
            if (gate.Voiced(AtDb(-42), automatic: true, -60, before: -41f, speech: 6f)) opened = f;
        Assert.True(opened is >= 0 and <= VoiceGate.Confirm + 1, $"word opened at frame {opened}");
        for (var f = 0; f < 10; f++) Assert.True(gate.Voiced(AtDb(-42), automatic: true, -60, before: -41f, speech: 6f), $"word frame {f} after it opened");
    }

    [Fact]
    public void ARoomOverItsFloorNeverOpensTheGateEvenWhenSuppressionKeepsIt()
    {
        var gate = new VoiceGate();
        var random = new Random(9);
        for (var f = 0; f < 600; f++)
        {
            var heard = -45 + (float)(random.NextDouble() * 6 - 3);
            var kept = random.Next(3) == 0 ? -100 : heard - 3;
            Assert.False(gate.Voiced(AtDb(kept), automatic: true, -60, before: heard, speech: -15f), $"room frame {f}");
        }
    }

    [Fact]
    public void AWordScoredAsNoiseDoesNotOpenTheGateOnItsLevelAlone()
    {
        var gate = new VoiceGate();
        for (var f = 0; f < 300; f++) gate.Voiced(AtDb(-100), automatic: true, -60, before: -45f, speech: -15f);
        for (var f = 0; f < 20; f++) Assert.False(gate.Voiced(AtDb(-42), automatic: true, -60, before: -41f, speech: 0f), $"frame {f}");
    }
}
