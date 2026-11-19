using ExoLauncher.Services.Hid;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the HID listing can reach devices Exo Mouse has no driver for: a vendor outside the drivers' list (a keyboard,
/// a printer, a security key) listed or opened because the page asked for every vendor; a FIDO collection (usage page
/// 0xF1D0) listed under a driver's vendor.
/// </summary>
public sealed class HidAllowListTests
{
    [Theory]
    [InlineData(0x373B, 0x0001)] // ATK receiver, a driver's vendor
    [InlineData(0x1532, 0x0001)] // Razer
    [InlineData(0x046D, 0x000C)] // Logitech, a generic page
    public void ADriverVendorsCollectionIsAllowed(int vendor, int page) =>
        Assert.True(HidService.Allowed(vendor, page));

    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x1234)]  // not a driver's vendor
    [InlineData(0x1050)]  // a security-key maker, not a mouse driver's vendor
    public void AVendorOutsideTheDriversIsNotAllowed(int vendor) =>
        Assert.False(HidService.Allowed(vendor, 0x0001));

    [Fact]
    public void AFidoCollectionIsNeverAllowedEvenUnderADriverVendor() =>
        Assert.False(HidService.Allowed(0x1532, HidService.FidoUsagePage));

    [Fact]
    public void TheListIsNotEmpty() =>
        Assert.Contains(0x373B, HidService.DriverVendors);
}
