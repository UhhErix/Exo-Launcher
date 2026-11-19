using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways a synced custom cover goes wrong on another PC: a picture lands under a name that is not its hash, bytes that do not
/// hash to their id are stored, a file of another type is named as a cover, or a picture over one item's limit is accepted.
/// Each test can fail: the picture rules are the code under test, and the cover cache is this run's temp folder.
/// </summary>
public sealed class AccountPictureSyncTests
{
    private static readonly byte[] Picture = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Enumerable.Range(9, 32).Select(i => (byte)i)];
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public void ACoverFileNameIsOnlyAHashAndAPngOrJpeg()
    {
        var sha = Hash(Picture);

        Assert.Equal("custom-cover-" + sha + ".png", AccountSyncApply.CoverFileName(sha, ".png"));
        Assert.Equal("custom-cover-" + sha + ".jpg", AccountSyncApply.CoverFileName(sha, ".jpg"));
        Assert.Null(AccountSyncApply.CoverFileName(sha, ".gif"));
        Assert.Null(AccountSyncApply.CoverFileName(sha.ToUpperInvariant(), ".png"));
        Assert.Null(AccountSyncApply.CoverFileName("..\\..\\escape", ".png"));
    }

    [Fact]
    public void APicturesBytesMustHashToItsIdAndItLandsUnderItsHashName()
    {
        var sha = Hash(Picture);
        var value = new JsonObject { ["ext"] = ".png", ["data"] = Convert.ToBase64String(Picture) }.ToJsonString();

        AccountSyncApply.StoreCoverImage(sha, value);

        Assert.True(File.Exists(Path.Combine(CoverArtService.CacheRoot, "custom-cover-" + sha + ".png")));
    }

    [Fact]
    public void APictureWhoseBytesDoNotHashToItsIdIsRefused()
    {
        var other = Hash(new byte[] { 9, 9, 9 });
        var value = new JsonObject { ["ext"] = ".png", ["data"] = Convert.ToBase64String(Picture) }.ToJsonString();

        Assert.Throws<FormatException>(() => AccountSyncApply.StoreCoverImage(other, value));
        Assert.False(File.Exists(Path.Combine(CoverArtService.CacheRoot, "custom-cover-" + other + ".png")));
    }

    [Fact]
    public void APictureOverOneItemsLimitIsRefusedEvenWithItsRightId()
    {
        var big = new byte[AccountSyncApply.MaxCoverImageBytes + 1];
        var sha = Hash(big);
        var value = new JsonObject { ["ext"] = ".jpg", ["data"] = Convert.ToBase64String(big) }.ToJsonString();

        Assert.Throws<FormatException>(() => AccountSyncApply.StoreCoverImage(sha, value));
    }

    private static (List<(string[] Games, string? File)> Calls, Action<IEnumerable<string>, string?> Set) Recorder()
    {
        var calls = new List<(string[], string?)>();
        return (calls, (games, file) => calls.Add((games.ToArray(), file)));
    }

    private static string CustomChoice(string sha, string game) =>
        new JsonObject { ["source"] = "custom", ["sha256"] = sha, ["ext"] = ".png", ["game"] = game }.ToJsonString();

    private static string PictureJson(byte[] picture) =>
        new JsonObject { ["ext"] = ".png", ["data"] = Convert.ToBase64String(picture) }.ToJsonString();

    private static void RemoveCached(string sha)
    {
        var path = Path.Combine(CoverArtService.CacheRoot, "custom-cover-" + sha + ".png");
        if (File.Exists(path)) DeletionGate.DeleteFile(path);
    }

    [Fact]
    public void ACustomChoiceWaitsForItsPictureAndIsAppliedOnceItIsHere()
    {
        // A choice whose picture is not on this PC changes nothing; the same choice applies once the picture is stored.
        var picture = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7, 7 };
        var sha = Hash(picture);
        RemoveCached(sha);
        var (calls, set) = Recorder();

        AccountSyncApply.ApplyCover(CustomChoice(sha, "steam:1"), set);
        Assert.Empty(calls);

        AccountSyncApply.StoreCoverImage(sha, PictureJson(picture));
        AccountSyncApply.ApplyCover(CustomChoice(sha, "steam:1"), set);
        var call = Assert.Single(calls);
        Assert.Equal(new[] { "steam:1" }, call.Games);
        Assert.Equal("custom-cover-" + sha + ".png", call.File);
    }

    [Fact]
    public void ACoverChoiceThatCameBeforeItsPictureIsCompletedByThePicture()
    {
        // The picture arrives after its choice: storing it applies that choice, and a choice for another picture stays unapplied.
        var picture = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 3, 1 };
        var sha = Hash(picture);
        var otherSha = Hash(new byte[] { 2, 7, 1, 8 });
        RemoveCached(sha);
        RemoveCached(otherSha);
        var (calls, set) = Recorder();
        var choices = new List<string> { CustomChoice(sha, "steam:2"), CustomChoice(otherSha, "steam:9") };

        AccountSyncApply.ApplyCoverImage(sha, PictureJson(picture), () => choices, set);

        var call = Assert.Single(calls);
        Assert.Equal(new[] { "steam:2" }, call.Games);
        Assert.Equal("custom-cover-" + sha + ".png", call.File);
    }

    [Fact]
    public void AStoreCoverChoiceRestoresTheStoresCover()
    {
        var (calls, set) = Recorder();

        AccountSyncApply.ApplyCover("{\"source\":\"store\",\"game\":\"steam:3\"}", set);

        var call = Assert.Single(calls);
        Assert.Equal(new[] { "steam:3" }, call.Games);
        Assert.Null(call.File);
    }
}
