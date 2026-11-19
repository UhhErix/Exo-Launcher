using ExoLauncher.Services.Account;

namespace ExoLauncher.Tests;

/// <summary>
/// The upscaler update setting is device-only (decided 2026-11-09: consent is per PC). Ways this could fail: the setting is synced to the
/// account, so one PC's consent to download upscaler files reaches another PC that never gave it; or it is dropped from the device list, so
/// it is no longer kept on this PC at all.
/// </summary>
public sealed class AutoUpdateUpscalersDeviceOnlyTests
{
    [Fact]
    public void AutoUpdateUpscalersStaysOnThisPcAndIsNeverSynced()
    {
        Assert.Contains("AutoUpdateUpscalers", AccountSyncMap.DeviceSettings);
        Assert.DoesNotContain("AutoUpdateUpscalers", AccountSyncMap.SyncedSettings);
    }
}
