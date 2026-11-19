namespace ExoLauncher.Services;

/// <summary>
/// The release list the upscaler reads: the compiled-in pins (DLSS Swapper's archive builds among them, from Exo's cache),
/// and the newer releases VendorWatch found on each vendor's own repository. No manifest is fetched; a file is installed
/// only when its SHA-256 matches a pin, or a release VendorWatch recorded with its GitHub blob hash.
/// </summary>
public sealed partial class DlssSwapService
{
    private static readonly Lock CatalogGate = new();
    private static IReadOnlyList<PinnedFile>? _allReleases;

    /// <summary>Pinned releases and the vendors' releases found since, newest first per file.</summary>
    internal static IReadOnlyList<PinnedFile> AllReleases
    {
        get
        {
            lock (CatalogGate)
            {
                if (_allReleases is not null) return _allReleases;
                _watched ??= LoadWatched();
                // Releases found on GitHub first, so a new SDK that ships a file unchanged is the one it is known by; a
                // release found before Exo pinned it is the pin's, not listed twice.
                var known = _watched.Concat(SwapperWatched())
                    .Where(found => !PinnedFiles.Any(pin => pin.Release == found.Release && pin.FileName.Equals(found.FileName, StringComparison.OrdinalIgnoreCase)))
                    .Concat(PinnedFiles).ToList();
                return _allReleases = known
                    .GroupBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(group => group.OrderByDescending(ReleaseOrder))
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Newest first. FSR 3.1's loader version does not follow its release
    /// (1.0.2.38022 is FSR 3.1.2, older than 3.1.4 at 1.0.1.41314), so AMD
    /// files sort by the release they are known by; the rest by file version.
    /// </summary>
    private static Version ReleaseOrder(PinnedFile file) =>
        (file.Vendor == "AMD" ? TryParseVersion(file.Display) : null) ?? TryParseVersion(file.Version) ?? new Version(0, 0);

    /// <summary>The SHA-256 a release is checked against: its pinned one (every release has one).</summary>
    internal static string? KnownSha(PinnedFile file) => file.Sha256.Length > 0 ? file.Sha256 : null;
}
