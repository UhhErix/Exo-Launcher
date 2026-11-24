using System.Collections.Concurrent;
using ExoLauncher.Services.Hid;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the no-access fallback for collections Windows keeps can go wrong:
/// - a Razer mouse (settings as feature reports on its mouse collection) is skipped and never shows;
/// - a keyboard collection is opened, reaching hardware Exo Mouse has no business with;
/// - a mouse collection with no feature reports (a Finalmouse receiver) is opened for nothing;
/// - a vendor collection is treated as Windows' own;
/// - a game controller is listed, so the mouse module could send a mouse command to a pad's firmware-update or pairing
///   report (a DualSense Edge's 0xA0 or 0x0A).
/// </summary>
public sealed class HidServiceTests
{
    private static HidService.Collection Caps(int page, int usage, int featureLength) =>
        new(page, usage, [0], [], featureLength > 0 ? [0] : [], 8, 0, featureLength);

    [Fact]
    public void RazerMouseCollectionOpensForItsFeatureReports() =>
        Assert.True(HidService.OpensForFeaturesOnly(Caps(0x01, 0x02, 91)));

    [Fact]
    public void PointerCollectionWithFeaturesOpens() =>
        Assert.True(HidService.OpensForFeaturesOnly(Caps(0x01, 0x01, 65)));

    [Theory]
    [InlineData(0x01, 0x06, 9)]      // keyboard
    [InlineData(0x01, 0x07, 9)]      // keypad
    [InlineData(0x01, 0x02, 0)]      // mouse with no feature reports
    [InlineData(0x0C, 0x01, 9)]      // consumer control
    [InlineData(0xFF00, 0x01, 65)]   // vendor collection: opened read-write, not here
    public void EverythingElseIsLeftAlone(int page, int usage, int featureLength) =>
        Assert.False(HidService.OpensForFeaturesOnly(Caps(page, usage, featureLength)));
}

/// <summary>
/// Ways a mouse that is still there can drop out of a listing:
/// - its serial is read again on every listing, and one failed read (a receiver busy relaying its
///   mouse) gives it another key, so the page sees it leave and a stranger arrive;
/// - a string that failed on the first listing is never read again, so the device stays nameless;
/// - two listings run side by side and the older answers last, closing a device the newer one found.
/// </summary>
public sealed class HidListingTests
{
    private const string Path = "hid#vid_361d&pid_0300&mi_01";

    [Fact]
    public void AFailedSerialReadKeepsTheSerialAlreadyRead()
    {
        var strings = new ConcurrentDictionary<string, (string? Product, string? Serial)>();
        Assert.Equal(("StarlightX Dongle", "937AEB5354AAB2ED"), HidService.Remember(strings, Path, () => "StarlightX Dongle", () => "937AEB5354AAB2ED"));
        var reads = 0;
        var again = HidService.Remember(strings, Path, () => { reads++; return null; }, () => { reads++; return null; });
        Assert.Equal(("StarlightX Dongle", "937AEB5354AAB2ED"), again);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void AStringThatFailedIsReadOnTheNextListing()
    {
        var strings = new ConcurrentDictionary<string, (string? Product, string? Serial)>();
        Assert.Equal(("StarlightX Dongle", ""), HidService.Remember(strings, Path, () => "StarlightX Dongle", () => null));
        Assert.Equal(("StarlightX Dongle", "937AEB5354AAB2ED"), HidService.Remember(strings, Path, () => "other", () => "937AEB5354AAB2ED"));
    }

    [Theory]
    [InlineData(0x01, 0x04)]         // joystick
    [InlineData(0x01, 0x05)]         // gamepad (a DualSense)
    [InlineData(0x01, 0x08)]         // multi-axis controller
    public void GameControllersStayOutOfReach(int page, int usage) =>
        Assert.True(HidService.IsGameController(new HidService.Collection(page, usage, [0], [], [0], 8, 0, 64)));

    [Theory]
    [InlineData(0x01, 0x02)]         // mouse
    [InlineData(0x01, 0x06)]         // keyboard
    [InlineData(0xFF00, 0x01)]       // a mouse's vendor collection
    [InlineData(0x0C, 0x01)]         // consumer control
    public void MiceAndTheirCollectionsStayListed(int page, int usage) =>
        Assert.False(HidService.IsGameController(new HidService.Collection(page, usage, [0], [], [0], 8, 0, 64)));

    [Fact]
    public void AnOlderListingThatAnswersLastIsNotApplied()
    {
        long applied = 0;
        Assert.True(HidService.IsNewest(2, ref applied));
        Assert.False(HidService.IsNewest(1, ref applied));
        Assert.Equal(2, applied);
        Assert.True(HidService.IsNewest(3, ref applied));
    }
}

public sealed class HidWriteGuardTests
{
    [Fact]
    public void BlocksBurstOverShortWindowPerDevice()
    {
        var now = new DateTime(2026, 11, 24, 14, 20, 0, DateTimeKind.Utc).Ticks;
        var guard = new HidWriteGuard(
            shortWindow: TimeSpan.FromSeconds(5),
            maxShort: 3,
            longWindow: TimeSpan.FromSeconds(60),
            maxLong: 10,
            utcNowTicks: () => now);

        Assert.True(guard.TryAcquire("dev-a").Allowed);
        Assert.True(guard.TryAcquire("dev-a").Allowed);
        Assert.True(guard.TryAcquire("dev-a").Allowed);

        var blocked = guard.TryAcquire("dev-a");
        Assert.False(blocked.Allowed);
        Assert.Equal(3, blocked.ShortCount);
        Assert.Equal(3, blocked.LongCount);

        Assert.True(guard.TryAcquire("dev-b").Allowed);

        now += TimeSpan.FromSeconds(6).Ticks;
        Assert.True(guard.TryAcquire("dev-a").Allowed);
    }

    [Fact]
    public void BlocksSustainedLoopOverLongWindowAndResetsOnClose()
    {
        var now = new DateTime(2026, 11, 24, 14, 20, 0, DateTimeKind.Utc).Ticks;
        var guard = new HidWriteGuard(
            shortWindow: TimeSpan.FromSeconds(5),
            maxShort: 3,
            longWindow: TimeSpan.FromSeconds(60),
            maxLong: 5,
            utcNowTicks: () => now);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(guard.TryAcquire("dev-a").Allowed);
            now += TimeSpan.FromSeconds(6).Ticks;
        }

        Assert.False(guard.TryAcquire("dev-a").Allowed);

        guard.Reset("dev-a");
        Assert.True(guard.TryAcquire("dev-a").Allowed);
    }

    [Fact]
    public void FormatsCompactHexHeadForDiagnostics()
    {
        Assert.Equal("-", HidWriteGuard.FormatHead([]));
        Assert.Equal("089f060000000002", HidWriteGuard.FormatHead([0x08, 0x9f, 0x06, 0x00, 0x00, 0x00, 0x00, 0x02, 0x02, 0xff]));
    }
}
