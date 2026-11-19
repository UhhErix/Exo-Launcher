using ExoLauncher.Services.DiscordClient;

namespace ExoLauncher.Tests;

/// <summary>
/// Sharing a capture to a Discord conversation (ClipShare). Ways it could fail:
/// - a clip over 25 MB starts uploading and fails halfway instead of being refused first;
/// - a file that is not a capture goes out as one;
/// - a name carries a folder, a control character or a bidi override into the conversation, or is longer than it should be;
/// - an empty file posts an empty attachment.
/// </summary>
public sealed class ClipShareTests
{
    [Theory]
    [InlineData("Marvel Rivals 2026-10-29.mp4", 1_000_000, "video/mp4")]
    [InlineData("snip.PNG", 500, "image/png")]
    [InlineData("big.mp4", ClipShare.MostBytes, "video/mp4")]
    public void ACaptureWithinTheLimitMayGo(string name, long bytes, string type)
    {
        var (plan, error) = ClipShare.Check(name, bytes);
        Assert.Null(error);
        Assert.Equal(type, plan!.Type);
    }

    [Fact]
    public void OneByteOverTheLimitIsRefusedBeforeAnythingIsRead()
    {
        var (plan, error) = ClipShare.Check("clip.mp4", ClipShare.MostBytes + 1);
        Assert.Null(plan);
        Assert.Contains("25 MB", error);
        Assert.Contains("Shrink", error);
    }

    [Theory]
    [InlineData("notes.txt", 100)]
    [InlineData("game.exe", 100)]
    [InlineData("clip.mp4.exe", 100)]
    [InlineData("clip", 100)]
    [InlineData("clip.mp4", 0)]
    [InlineData("clip.mp4", -1)]
    public void WhatIsNotACaptureOrIsEmptyIsGone(string name, long bytes)
    {
        var (plan, error) = ClipShare.Check(name, bytes);
        Assert.Null(plan);
        Assert.Equal("That capture is gone.", error);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Captures\Hades II\clip 1.mp4", "clip 1.mp4")]
    [InlineData("a\u0007b\u202Ec.mp4", "abc.mp4")]
    [InlineData("   .mp4", "capture.mp4")]
    public void TheNameIsTheFilesOwnAndClean(string path, string expected) => Assert.Equal(expected, ClipShare.SafeName(path));

    [Fact]
    public void ALongNameIsCutWithItsExtensionKept()
    {
        var name = ClipShare.SafeName(new string('x', 400) + ".mp4");
        Assert.Equal(100, name.Length);
        Assert.EndsWith(".mp4", name);
    }

    [Fact]
    public void ALongNameIsNotCutThroughASurrogatePair()
    {
        var name = ClipShare.SafeName(new string('x', 95) + "\U0001F600\U0001F600\U0001F600.mp4");
        Assert.True(name.Length <= 100);
        Assert.DoesNotContain('\uFFFD', name);
        Assert.False(char.IsHighSurrogate(name[^5]));
    }
}
