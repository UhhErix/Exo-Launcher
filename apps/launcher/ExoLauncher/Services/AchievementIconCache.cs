using System.Buffers.Binary;
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

    /// <summary>The icon kept last that decodes at 64x64 or larger, so a preview looks like the real thing; else the one that ships.</summary>
    internal static string? Sample() => SampleFrom(Folder) ?? Bundled();

    internal static string? SampleFrom(string folder)
    {
        try
        {
            var kept = new DirectoryInfo(folder);
            if (!kept.Exists) return null;
            foreach (var file in kept.EnumerateFiles().Where(f => f.Extension.Length == 0).OrderByDescending(f => f.LastWriteTimeUtc))
            {
                try
                {
                    var bytes = File.ReadAllBytes(file.FullName);
                    if (Dimensions(bytes) is { Width: >= 64, Height: >= 64 } && DataUri(bytes) is { } picture)
                        return picture;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Achievements: no kept icon: " + ex.Message); }
        return null;
    }

    /// <summary>Reads pixel dimensions from PNG, GIF, WebP or JPEG headers; null when malformed or unknown.</summary>
    internal static (int Width, int Height)? Dimensions(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 24 && b.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) && b[12..16].SequenceEqual("IHDR"u8))
        {
            var w = BinaryPrimitives.ReadInt32BigEndian(b[16..20]);
            var h = BinaryPrimitives.ReadInt32BigEndian(b[20..24]);
            return w > 0 && h > 0 ? (w, h) : null;
        }
        if (b.Length >= 10 && b.StartsWith("GIF8"u8))
        {
            var w = BinaryPrimitives.ReadUInt16LittleEndian(b[6..8]);
            var h = BinaryPrimitives.ReadUInt16LittleEndian(b[8..10]);
            return w > 0 && h > 0 ? (w, h) : null;
        }
        if (b.Length >= 30 && b.StartsWith("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8))
        {
            var chunk = b[12..16];
            if (chunk.SequenceEqual("VP8 "u8) && b[23] == 0x9D && b[24] == 0x01 && b[25] == 0x2A)
            {
                var w = BinaryPrimitives.ReadUInt16LittleEndian(b[26..28]) & 0x3FFF;
                var h = BinaryPrimitives.ReadUInt16LittleEndian(b[28..30]) & 0x3FFF;
                return w > 0 && h > 0 ? (w, h) : null;
            }
            if (chunk.SequenceEqual("VP8L"u8) && b[20] == 0x2F)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(b[21..25]);
                var w = (int)(bits & 0x3FFF) + 1;
                var h = (int)((bits >> 14) & 0x3FFF) + 1;
                return w > 0 && h > 0 ? (w, h) : null;
            }
            if (chunk.SequenceEqual("VP8X"u8))
            {
                var w = 1 + (b[24] | (b[25] << 8) | (b[26] << 16));
                var h = 1 + (b[27] | (b[28] << 8) | (b[29] << 16));
                return w > 0 && h > 0 ? (w, h) : null;
            }
            return null;
        }
        if (b.Length >= 4 && b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            var pos = 2;
            while (pos + 3 < b.Length)
            {
                while (pos < b.Length && b[pos] == 0xFF) pos++;
                if (pos >= b.Length) break;
                var marker = b[pos++];
                if (marker is 0xD9 or 0xDA) break;
                if (marker is 0xD8 or 0x01 || (marker is >= 0xD0 and <= 0xD7)) continue;
                if (pos + 2 > b.Length) break;
                var len = BinaryPrimitives.ReadUInt16BigEndian(b[pos..(pos + 2)]);
                if (len < 2 || pos + len > b.Length) break;
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC) && len >= 7)
                {
                    var h = BinaryPrimitives.ReadUInt16BigEndian(b[(pos + 3)..(pos + 5)]);
                    var w = BinaryPrimitives.ReadUInt16BigEndian(b[(pos + 5)..(pos + 7)]);
                    return w > 0 && h > 0 ? (w, h) : null;
                }
                pos += len;
            }
        }
        return null;
    }

    /// <summary>The picture that ships: a preview's on a new PC, and a game's with no icon and no art.</summary>
    internal static string? Bundled()
    {
        try { return DataUri(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Overlay", "sample-icon.png"))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Achievements: the bundled icon is missing: " + ex.Message); return null; }
    }
}
