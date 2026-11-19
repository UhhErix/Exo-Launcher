namespace ExoLauncher.Services;

/// <summary>
/// The installed display drivers as one string: every display adapter's driver version, joined, in the order the driver
/// class key lists them (<see cref="GpuCapability.DriverFingerprint"/> reads it fresh, one registry read). Two reads say the
/// same thing until a graphics driver is installed by anyone (Exo, Windows Update, the vendor's app, or by hand), which is
/// what <see cref="DriverKeep"/> compares. Null when it cannot be read at all, which is never taken for a driver change.
/// </summary>
internal static class DriverIdentity
{
    /// <summary>Tests only: where the identity comes from for the rest of this test's flow.</summary>
    internal static Func<string?> Read { get; set; } = FromRegistry;

    /// <summary>The installed display drivers' identity now, or null when it cannot be read.</summary>
    internal static string? Current()
    {
        string? read;
        try { read = Read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
        // Every adapter key without a version reads as empty entries: nothing to compare a driver change by.
        return string.IsNullOrWhiteSpace(read) || read.Trim('|').Length == 0 ? null : read;
    }

    /// <summary>
    /// NVIDIA's own drivers when the PC has an NVIDIA adapter (the only settings <see cref="DriverKeep"/> puts back are
    /// NVIDIA's, so an integrated GPU's or a virtual display's driver update is not a change); every adapter's otherwise.
    /// </summary>
    private static string FromRegistry()
    {
        var adapters = GpuCapability.DisplayDrivers();
        return Of("nvidia", adapters) is { Length: > 0 } nvidia ? nvidia : string.Join('|', adapters.Select(adapter => adapter.Version));
    }

    /// <summary>
    /// The identity of the drivers of <paramref name="vendor"/>'s adapters (name, driver version): their versions, each once,
    /// in a fixed order, so another vendor's driver, a virtual display or the order Windows lists them in changes nothing.
    /// Empty when the PC has no adapter of that vendor.
    /// </summary>
    internal static string Of(string vendor, IEnumerable<(string Name, string Version)> adapters) =>
        string.Join('|', adapters
            .Where(adapter => adapter.Name.Contains(vendor, StringComparison.OrdinalIgnoreCase) && adapter.Version.Length > 0)
            .Select(adapter => adapter.Version)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase));
}
