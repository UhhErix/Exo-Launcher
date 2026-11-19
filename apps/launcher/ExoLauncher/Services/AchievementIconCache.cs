using System.Security.Cryptography;
using System.Text;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Achievement art: the exact host policy for provider-owned pictures (untrusted values become null before persistence or
/// WebView projection), and the pictures themselves, kept on disk (achievement-art) from when a game starts so a banner
/// never waits on the network, and handed to its page as a data: address, the one kind of picture its CSP allows.
/// </summary>
public static class AchievementIconCache
{
    private const int MaxIconBytes = 2_000_000;
    private static string Folder => Path.Combine(PathHelper.AppDataDir, "achievement-art");

    private static readonly HashSet<string> ApprovedProviderImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.akamai.steamstatic.com",
        "cdn.cloudflare.steamstatic.com",
        "shared.steamstatic.com",
        "shared.akamai.steamstatic.com",
        "shared.cloudflare.steamstatic.com",
        "steamcdn-a.akamaihd.net",
        "shared-static-prod.epicgames.com",
        "cdn1.epicgames.com",
        "cdn2.unrealengine.com",
        "images.gog.com",
        "images.gog-statics.com",
    };

    internal static bool TryGetHttpsUri(string? source, out Uri uri)
    {
        if (Uri.TryCreate(source?.Trim(), UriKind.Absolute, out var parsed) &&
            parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            parsed.IsDefaultPort &&
            string.IsNullOrEmpty(parsed.UserInfo) &&
            ApprovedProviderImageHosts.Contains(parsed.IdnHost))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    /// <summary>
    /// Canonicalizes provider-owned achievement art. Untrusted legacy values
    /// become null before persistence or WebView projection.
    /// </summary>
    internal static string? SanitizeProviderImageUrl(string? source) =>
        source is { Length: <= 2_048 } && TryGetHttpsUri(source, out var uri)
            ? uri.AbsoluteUri
            : null;

    /// <summary>A picture's bytes as a data: address, by what the bytes are (not what the server said); null for anything else.</summary>
    internal static string? DataUri(byte[] bytes)
    {
        if (bytes.Length is < 12 or > MaxIconBytes) return null;
        ReadOnlySpan<byte> b = bytes;
        var type = b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? "image/jpeg"
            : b.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]) ? "image/png"
            : b.StartsWith("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
            : b.StartsWith("GIF8"u8) ? "image/gif"
            : null;
        return type is null ? null : $"data:{type};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string FileFor(Uri uri) =>
        Path.Combine(Folder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))));

    /// <summary>The icon as a data: address: from disk, else fetched now and kept. Null when it can't be had.</summary>
    internal static async Task<string?> PictureAsync(string? url, HttpClient http, CancellationToken token)
    {
        if (!TryGetHttpsUri(url, out var uri)) return null;
        try
        {
            var file = FileFor(uri);
            if (File.Exists(file)) return DataUri(await File.ReadAllBytesAsync(file, token).ConfigureAwait(false));
            var bytes = await http.GetByteArrayAsync(uri, token).ConfigureAwait(false);
            if (DataUri(bytes) is not { } picture) return null;
            Directory.CreateDirectory(Folder);
            var part = file + ".part";
            await File.WriteAllBytesAsync(part, bytes, CancellationToken.None).ConfigureAwait(false);
            File.Move(part, file, overwrite: true);
            return picture;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            AppLog.Debug($"Achievements: no icon from {uri.Host}: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// When a game starts: the icons of what it could still unlock, one at a time, so its banners have them. Stops with
    /// the game, or after three misses in a row (the store's away; each unlock asks once more for its own).
    /// </summary>
    internal static async Task FillAsync(IEnumerable<string?> urls, HttpClient http, CancellationToken token)
    {
        var misses = 0;
        foreach (var url in urls.Distinct(StringComparer.Ordinal))
        {
            if (token.IsCancellationRequested || misses >= 3) return;
            if (!TryGetHttpsUri(url, out var uri) || File.Exists(FileFor(uri))) continue;
            misses = await PictureAsync(url, http, token).ConfigureAwait(false) is null ? misses + 1 : 0;
        }
    }

    /// <summary>The icon kept last, so a preview looks like the real thing; else the one that ships.</summary>
    internal static string? Sample()
    {
        try
        {
            var kept = new DirectoryInfo(Folder);
            if (kept.Exists && kept.EnumerateFiles().Where(f => f.Extension.Length == 0).MaxBy(f => f.LastWriteTimeUtc) is { } newest && DataUri(File.ReadAllBytes(newest.FullName)) is { } picture)
                return picture;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Achievements: no kept icon: " + ex.Message); }
        return Bundled();
    }

    /// <summary>The picture that ships: a preview's on a new PC, and a game's with no icon and no art.</summary>
    internal static string? Bundled()
    {
        try { return DataUri(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Overlay", "sample-icon.png"))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Achievements: the bundled icon is missing: " + ex.Message); return null; }
    }
}
