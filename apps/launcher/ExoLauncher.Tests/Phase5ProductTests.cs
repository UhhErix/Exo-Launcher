using ExoLauncher.Helpers;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Regression tests for Phase 5.2 product polish:
/// - Windows sign-in launch (--startup) stays quiet via ForegroundWatcher.HasStartupArg;
/// - Vendor BIOS paths help players find EXPO/XMP and Resizable BAR on ASUS, MSI, Gigabyte, and ASRock boards,
///   while leaving unknown/null makers clean without parenthesized guesses.
/// </summary>
public sealed class Phase5ProductTests
{
    [Theory]
    [InlineData(new[] { "ExoLauncher.exe", "--startup" }, true)]
    [InlineData(new[] { "ExoLauncher.exe", "--STARTUP" }, true)]
    [InlineData(new[] { "--startup" }, true)]
    [InlineData(new[] { "ExoLauncher.exe" }, false)]
    [InlineData(new[] { "ExoLauncher.exe", "--open" }, false)]
    [InlineData(new string[0], false)]
    public void HasStartupArgDetectsQuietWindowsSignInLaunch(string[] args, bool expected)
    {
        Assert.Equal(expected, ForegroundWatcher.HasStartupArg(args));
    }

    [Theory]
    [InlineData("ASUS", "xmp", "Ai Tweaker")]
    [InlineData("ASUSTeK COMPUTER INC.", "xmp", "Ai Tweaker")]
    [InlineData("ASUS", "rebar", "Re-Size BAR")]
    [InlineData("MSI", "xmp", "A-XMP")]
    [InlineData("Micro-Star International Co., Ltd.", "rebar", "PCIe Subsystem")]
    [InlineData("Gigabyte", "xmp", "X.M.P.")]
    [InlineData("Gigabyte", "rebar", "Above 4G")]
    [InlineData("ASRock", "xmp", "OC Tweaker")]
    [InlineData("ASRock", "rebar", "C.A.M.")]
    public void BiosPathReturnsVendorMenuForMajorBoardMakers(string maker, string topic, string expectedFragment)
    {
        var path = PcHealth.BiosPath(maker, topic);
        Assert.NotNull(path);
        Assert.Contains(expectedFragment, path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "xmp")]
    [InlineData("", "xmp")]
    [InlineData("Dell Inc.", "xmp")]
    [InlineData("ASUS", "unknown")]
    public void BiosPathReturnsNullForUnknownMakerOrTopic(string? maker, string topic)
    {
        Assert.Null(PcHealth.BiosPath(maker, topic));
    }

    [Fact]
    public void DecideIncludesVendorBiosHintOnlyWhenBoardMakerIsKnown()
    {
        var dimms = new PcHealth.Dimm[] { new(2133, 2133, "CMK16GX4M2D3600C18", 0x09) };
        var gpu = new PcHealth.GpuBar("NVIDIA GeForce RTX 3070", false, false);

        var genericItems = PcHealth.Decide(new PcHealth.Reading(dimms, gpu, null, null, null, null, BoardMaker: null));
        Assert.Equal("Your RAM runs at 2133 MT/s; it's rated for 3600. Turn on EXPO/XMP in the BIOS.", genericItems[0].Detail);
        Assert.Equal("Off. Turn on Resizable BAR in the BIOS.", genericItems[1].Detail);

        var asusItems = PcHealth.Decide(new PcHealth.Reading(dimms, gpu, null, null, null, null, BoardMaker: "ASUS"));
        Assert.Equal("Your RAM runs at 2133 MT/s; it's rated for 3600. Turn on EXPO/XMP in the BIOS (Ai Tweaker → Ai Overclock Tuner).", asusItems[0].Detail);
        Assert.Equal("Off. Turn on Resizable BAR in the BIOS (Advanced → PCI Subsystem Settings → Re-Size BAR Support).", asusItems[1].Detail);
    }
}
