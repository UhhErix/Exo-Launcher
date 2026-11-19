using System.Text;
using System.Text.Json.Nodes;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>Which upscaler file is newer, and what the vendor watch (DlssSwapService.VendorWatch.cs) will fetch.</summary>
public sealed class UpscalerReleaseTests
{
    private static DlssSwapService.PinnedFile Fsr31(string version, string label) =>
        new("amd_fidelityfx_dx12.dll", "fsr-" + label, "FidelityFX " + label, version, label, "GPUOpen-LibrariesAndSDKs/FidelityFX-SDK", "", "", 1, "");

    // Call of Duty ships FSR 3.1.2 as loader 1.0.2.38022, above 3.1.4's 1.0.1.41314: Exo took it for newer than its
    // own and never offered 3.1.4.
    [Fact]
    public void Fsr31BuildsAreComparedByTheReleaseTheyAreKnownBy()
    {
        DlssSwapService.PinnedFile[] releases = [Fsr31("1.0.1.41314", "3.1.4"), Fsr31("1.0.2.38022", "3.1.2")];

        Assert.Null(DlssSwapService.SkipApplyReason("amd_fidelityfx_dx12.dll", "1.0.2.38022", "1.0.1.41314", releases));
        Assert.NotNull(DlssSwapService.SkipApplyReason("amd_fidelityfx_dx12.dll", "1.0.1.41314", "1.0.2.38022", releases));
        // A build no list names is compared by its file version.
        Assert.NotNull(DlssSwapService.SkipApplyReason("amd_fidelityfx_dx12.dll", "1.0.1.42386", "1.0.1.41314", releases));
    }

    [Fact]
    public void TheWatchFetchesOnlyTheSdksOwnDllsThatAGameShips()
    {
        var fsr = DlssSwapService.VendorSdks.Single(sdk => sdk.ReleasePrefix == "fsr-");
        var blob = new string('a', 40);
        var listing = JsonNode.Parse($$"""
            [
              { "type": "file", "name": "amd_fidelityfx_upscaler_dx12.dll", "size": 28761864, "sha": "{{blob}}" },
              { "type": "file", "name": "amd_fidelityfx_denoiser_dx12.dll", "size": 13782280, "sha": "{{blob}}" },
              { "type": "file", "name": "readme.txt", "size": 10, "sha": "{{blob}}" },
              { "type": "dir", "name": "amd_fidelityfx_loader_dx12.dll", "size": 0, "sha": "{{blob}}" },
              { "type": "file", "name": "amd_fidelityfx_framegeneration_dx12.dll", "size": 1, "sha": "not-a-hash" }
            ]
            """);
        var used = new HashSet<string> { "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll" };

        var wanted = DlssSwapService.SdkListing(listing, fsr, used);

        Assert.Equal([("amd_fidelityfx_upscaler_dx12.dll", 28761864L, blob)], wanted);
    }

    [Theory]
    [InlineData("https://updates.getexo.app/v1/cache/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/60f4ea81909200d8542eca14dccb2628b763a9a3/Kits/FidelityFX/signedbin/amd_fidelityfx_upscaler_dx12.dll", true)]
    [InlineData("https://updates.getexo.app/v1/cache/NVIDIA/DLSS/374959484e79a640feaba44c93ac8cfb0a03f5b5/lib/Windows_x86_64/rel/nvngx_dlss.dll", true)]
    [InlineData("https://updates.getexo.app/v1/cache/intel/xess/8fe81bdbbaf00b3c1b733fd0d830c333dc84e6f0/bin/libxess.dll", true)]
    // A tag or branch instead of a commit, another folder, another file, another repository, extra path.
    [InlineData("https://updates.getexo.app/v1/cache/NVIDIA/DLSS/main/lib/Windows_x86_64/rel/nvngx_dlss.dll", false)]
    [InlineData("https://updates.getexo.app/v1/cache/NVIDIA/DLSS/374959484e79a640feaba44c93ac8cfb0a03f5b5/lib/Windows_x86_64/dev/nvngx_dlss.dll", false)]
    [InlineData("https://updates.getexo.app/v1/cache/intel/xess/8fe81bdbbaf00b3c1b733fd0d830c333dc84e6f0/bin/basic_sample_frame_generation.exe", false)]
    [InlineData("https://updates.getexo.app/v1/cache/someone/DLSS/374959484e79a640feaba44c93ac8cfb0a03f5b5/lib/Windows_x86_64/rel/nvngx_dlss.dll", false)]
    [InlineData("https://updates.getexo.app/v1/cache/intel/xess/8fe81bdbbaf00b3c1b733fd0d830c333dc84e6f0/bin/libxess.dll?x=1", false)]
    [InlineData("https://updates.getexo.app/v1/cache/intel/xess/8FE81BDBBAF00B3C1B733FD0D830C333DC84E6F0/bin/libxess.dll", false)]
    public void TheWatchFetchesOnlyAVendorsSdkDllAtACommit(string url, bool allowed) =>
        Assert.Equal(allowed, DlssSwapService.IsVendorSdkUrl(url));

    [Fact]
    public void GitBlobHashMatchesGits()
    {
        var path = Path.Combine(Path.GetTempPath(), "exo-blob-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("hello\n"));
            // git hash-object of "hello\n".
            Assert.Equal("CE013625030BA8DBA906F756967F9E9CA394464A", DlssSwapService.GitBlobSha1(path));
        }
        finally { DeletionGate.DeleteFile(path); }
    }
    /// <summary>A vendor DLL is taken only when signed by that vendor's exact publisher. Ways it could fail: another vendor's
    /// signature, a lookalike name, or a DLL no vendor owns.</summary>
    [Theory]
    [InlineData("nvngx_dlss.dll", "NVIDIA Corporation", true)]
    [InlineData("amd_fidelityfx_upscaler_dx12.dll", "Advanced Micro Devices", true)]
    [InlineData("libxess.dll", "Intel Corporation", true)]
    [InlineData("nvngx_dlss.dll", "Advanced Micro Devices", false)]
    [InlineData("libxess.dll", "NVIDIA Corporation", false)]
    [InlineData("nvngx_dlss.dll", "NVIDIA Corporation Ltd", false)]
    [InlineData("nvngx_dlss.dll", "nvidia corporation", false)]
    [InlineData("unknown.dll", "NVIDIA Corporation", false)]
    [InlineData("nvngx_dlss.dll", null, false)]
    public void Publisher_must_be_the_vendors_exact_name(string file, string? publisher, bool accepted) =>
        Assert.Equal(accepted, DlssSwapService.PublisherAccepted(file, publisher));

    /// <summary>An unsigned file with a vendor's name is refused: the signature check runs before anything is trusted.</summary>
    [Fact]
    public void An_unsigned_vendor_named_file_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), "exo-unsigned-" + Guid.NewGuid().ToString("N"), "nvngx_dlss.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllBytes(path, new byte[4096]);
            Assert.False(DlssSwapService.HasTrustedVendorSignature(path, "nvngx_dlss.dll"));
        }
        finally { DeletionGate.DeleteDirectory(Path.GetDirectoryName(path)!, recursive: true); }
    }

    /// <summary>The file version must carry the release's tag and be newer than every known version of that file. Ways it
    /// could fail: an older signed DLL under a newer tag (downgrade), a tag that names another build, an equal version
    /// re-accepted, or an unreadable version.</summary>
    [Theory]
    [InlineData("310.9.17", "310.9.17.0", new[] { "310.9.16.0" }, null)]
    [InlineData("310.9.17", "310.9.16.0", new[] { "310.9.1.0" }, "isn't the release's")]
    [InlineData("310.9.1", "310.9.1.0", new[] { "310.9.16.0" }, "isn't newer")]
    [InlineData("310.9.17", "310.9.17.0", new[] { "310.9.17.0" }, "isn't newer")]
    [InlineData("310.9.17", "garbage", new string[0], "isn't readable")]
    [InlineData("310.9.17", "310.9.17.0", new string[0], null)]
    public void A_vendor_file_must_match_its_tag_and_be_newer_than_known(string release, string fileVersion, string[] known, string? reason)
    {
        var why = DlssSwapService.VendorReleaseRejection(release, fileVersion, known);
        if (reason is null) Assert.Null(why);
        else Assert.Contains(reason, why);
    }
}
