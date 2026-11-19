using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// A synced custom cover picture is stored only when it is what its file says: a PNG or JPEG by its first bytes, within the
/// size the sync sends (its base64 is refused before it is decoded). Ways this could fail: a file named .png holding anything
/// (a script, an executable) is saved into the cover cache; a base64 value of any size is decoded into memory. Fixture bytes are
/// a made-up image, not a real picture.
/// </summary>
public sealed class SyncedCoverPictureTests : IDisposable
{
    private const int MaxCoverBytes = 64 * 1024;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private readonly string _data = Path.Combine(Path.GetTempPath(), "exo-cover-picture-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorDataDir = Environment.GetEnvironmentVariable(PathHelper.DataDirOverrideVariable);

    public SyncedCoverPictureTests()
    {
        Directory.CreateDirectory(_data);
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _data);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathHelper.DataDirOverrideVariable, _priorDataDir);
        try { DeletionGate.DeleteDirectory(_data, recursive: true); } catch { /* temp */ }
    }

    private static string Value(string ext, byte[] bytes) =>
        new JsonObject { ["ext"] = ext, ["data"] = Convert.ToBase64String(bytes) }.ToJsonString();

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public void APngIsStored()
    {
        var sha = Sha(Png);

        AccountSyncApply.StoreCoverImage(sha, Value(".png", Png));

        Assert.True(File.Exists(Path.Combine(CoverArtService.CacheRoot, "custom-cover-" + sha + ".png")));
    }

    [Fact]
    public void AFileNamedAPngThatIsNotOneIsRefused()
    {
        var script = "Write-Output hi; not a picture at all"u8.ToArray();

        Assert.Throws<FormatException>(() => AccountSyncApply.StoreCoverImage(Sha(script), Value(".png", script)));
    }

    [Fact]
    public void AJpegSignatureDoesNotPassAsAPng()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };

        Assert.Throws<FormatException>(() => AccountSyncApply.StoreCoverImage(Sha(jpeg), Value(".png", jpeg)));
    }

    [Fact]
    public void AValueTooBigToSendIsRefusedBeforeItIsDecoded()
    {
        // The digest is right, so only the size can refuse it: 64 KiB of picture, whose base64 is over the item's limit.
        var big = new byte[MaxCoverBytes];
        Png.CopyTo(big, 0);

        Assert.Throws<FormatException>(() => AccountSyncApply.StoreCoverImage(Sha(big), Value(".png", big)));
    }
}
