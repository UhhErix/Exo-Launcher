using ExoLauncher.Services.DiscordClient.Voice;
using Xunit;

namespace ExoLauncher.Tests;

// Ways keeping the voice to learn from could fail silently: pauses between words fill it, so a conversation never
// reaches the seconds a teach needs; it keeps growing past its 15 s; the newest voice is dropped instead of the oldest.
public sealed class TeachTakeTests
{
    private static float[] Frame(float value)
    {
        var frame = new float[Opus.FrameSamples * 2];
        Array.Fill(frame, value);
        return frame;
    }

    [Fact]
    public void OnlyVoiceIsKeptSoAConversationFillsIt()
    {
        var take = new TeachTake();
        for (var i = 0; i < 3000; i++) take.Add(Frame(0.1f), voiced: i % 3 == 0); // a minute, a third of it talking
        Assert.Equal(TeachTake.Frames, take.Voice);
        var kept = take.Take()!.Value;
        Assert.All(kept.Voiced, Assert.True);
        Assert.Equal(TeachTake.Frames * Opus.FrameSamples, kept.Cleaned.Length);
    }

    [Fact]
    public void TheNewestVoiceStaysAndTheOldestGoes()
    {
        var take = new TeachTake();
        take.Add(Frame(0.5f), voiced: true);
        for (var i = 0; i < TeachTake.Frames; i++) take.Add(Frame(0.25f), voiced: true);
        var kept = take.Take()!.Value.Cleaned;
        Assert.DoesNotContain(0.5f, kept);
        Assert.Equal(0.25f, kept[^1]);
    }

    [Fact]
    public void TheTakeWithMoreVoiceIsLearnedFrom()
    {
        var meter = new TeachTake();
        var call = new TeachTake();
        for (var i = 0; i < 100; i++) meter.Add(Frame(0.1f), voiced: true);
        for (var i = 0; i < 400; i++) call.Add(Frame(0.2f), voiced: true);
        Assert.Equal(400, TeachTake.Best(meter, call)!.Value.Voice);
        Assert.Equal(100, TeachTake.Best(meter, null)!.Value.Voice);
        Assert.Null(TeachTake.Best(null, new TeachTake()));
    }
}
