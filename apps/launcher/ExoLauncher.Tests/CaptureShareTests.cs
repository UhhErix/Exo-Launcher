using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways sending a capture to Discord, or what a new snip does, could go wrong for the player:
/// - a clip over Discord's 10 MB is posted as it is and Discord refuses it, or one that fits is re-encoded for nothing;
/// - a trim is ignored and the whole clip goes;
/// - a big picture is cut down blurry instead of the player being told to crop it;
/// - a file that isn't a PNG or MP4 is offered to Discord under a made-up type;
/// - a snip taken over a game opens the editor and pulls Exo over the game; Copy only still opens it; a setting written
///   by hand turns the editor off.
/// </summary>
public sealed class CaptureShareTests
{
    private const long MB = 1024 * 1024;

    [Theory]
    [InlineData(true, 9 * MB, false, CaptureShare.Plan.Original)]
    [InlineData(true, 10 * MB, false, CaptureShare.Plan.Original)]
    [InlineData(true, 10 * MB + 1, false, CaptureShare.Plan.DiscordCopy)]
    [InlineData(true, 400 * MB, false, CaptureShare.Plan.DiscordCopy)]
    [InlineData(true, 2 * MB, true, CaptureShare.Plan.DiscordCopy)]
    [InlineData(false, 3 * MB, false, CaptureShare.Plan.Original)]
    [InlineData(false, 3 * MB, true, CaptureShare.Plan.Original)]
    [InlineData(false, 10 * MB + 1, false, CaptureShare.Plan.TooBig)]
    internal void AClipIsCutToFitAndAPictureIsNever(bool video, long bytes, bool trimmed, CaptureShare.Plan plan) =>
        Assert.Equal(plan, CaptureShare.PlanFor(video, bytes, trimmed));

    [Fact]
    public void TheLimitIsDiscordsWithoutNitro() => Assert.Equal(10_485_760, CaptureShare.DiscordLimit);

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(0d, 0d, false)]
    [InlineData(1500d, 0d, true)]
    [InlineData(0d, 20_000d, true)]
    public void OnlyAPartOfTheClipIsATrim(double? start, double? end, bool trimmed) =>
        Assert.Equal(trimmed, CaptureShare.Trimmed(start, end));

    [Theory]
    [InlineData(@"C:\Captures\Hades\Hades 2026-11-19 10.00.00.png", "image/png")]
    [InlineData(@"C:\Captures\Hades\Clip.MP4", "video/mp4")]
    [InlineData(@"C:\Captures\Hades\Clip.mkv", null)]
    [InlineData(@"C:\Captures\Hades\notes.txt", null)]
    public void OnlyPicturesAndClipsGoToDiscord(string path, string? type) => Assert.Equal(type, CaptureShare.TypeOf(path));

    [Theory]
    [InlineData("snip", false, "edit", true)]
    [InlineData("snip", false, null, true)]
    [InlineData("snip", false, "nonsense", true)]
    [InlineData("snip", false, "copy", false)]
    [InlineData("snip", true, "edit", false)]
    [InlineData("clip", false, "edit", false)]
    [InlineData("trim", false, "edit", false)]
    public void ASnipOpensTheEditorOnlyOffTheGameAndWhenAsked(string kind, bool inGame, string? afterSnip, bool opens) =>
        Assert.Equal(opens, CaptureShare.OpensEditor(kind, inGame, afterSnip));

    [Theory]
    [InlineData("copy", "copy")]
    [InlineData("edit", "edit")]
    [InlineData(null, "edit")]
    [InlineData("Copy", "edit")]
    public void AfterASnipIsOneOfTwo(string? value, string normal) => Assert.Equal(normal, CaptureShare.NormalizeAfterSnip(value));
}
