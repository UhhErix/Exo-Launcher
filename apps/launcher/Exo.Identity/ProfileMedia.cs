using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Exo.Identity;

/// <summary>
/// The profile's media on this PC (plan §2.17), one place for every Exo app: the pictures, GIFs, animated WebP or AVIF and
/// MP4 or WebM loops a profile shows as its picture, banner or background (and on its canvas), each kept once under its
/// SHA-256 in <see cref="Folder"/> beside the identity, so a file picked in one app plays in all of them. A file is taken
/// by what its bytes are, never its name, up to <see cref="ProfileItems.MaxMediaBytes"/>; the look names it by its hash
/// (<see cref="ProfileItems.Media"/>), and the bytes reach other PCs apart from the look. Each app serves the folder to its
/// own page from a host of its own; a test copy passes a folder of its own, so it never adds to the real one.
/// </summary>
public static class ProfileMedia
{
    /// <summary>%LOCALAPPDATA%\Exo\profile-media.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Exo", "profile-media");

    /// <param name="Sha">The file's SHA-256, lowercase hex: its name in the folder, with its type's extension.</param>
    public sealed record Kept(string Sha, string Type, long Bytes);

    /// <summary>The file at <paramref name="path"/> copied in under its hash (once: a file already there stays), or null when
    /// it isn't media Exo plays or is too big.</summary>
    public static Kept? Keep(string path, string? folder = null)
    {
        folder ??= Folder;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is 0 or > ProfileItems.MaxMediaBytes) return null;
        Span<byte> head = stackalloc byte[32];
        string? type;
        using (var file = File.OpenRead(path))
        {
            var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            type = TypeOf(head[..read]);
        }
        if (type is null) return null;
        string sha;
        using (var file = File.OpenRead(path)) sha = Convert.ToHexStringLower(SHA256.HashData(file));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, sha + Extension(type));
        if (!File.Exists(target))
        {
            var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.Copy(path, temp, overwrite: true);
            try { File.Move(temp, target, overwrite: false); }
            catch (IOException) when (File.Exists(target)) { DeletionGate.DeleteFile(temp); }
        }
        return new Kept(sha, type, info.Length);
    }

    /// <summary>Where a look's or a canvas block's media is on this PC (its full path), or null when it isn't a media
    /// <see cref="ProfileItems.IsMedia"/> takes or its file isn't here (yet).</summary>
    public static string? FileOf(JsonNode? media, string? folder = null) =>
        ProfileItems.IsMedia(media) && Device.Text(media!.AsObject(), "sha") is { } sha && Device.Text(media.AsObject(), "type") is { } type &&
        Path.Combine(folder ?? Folder, sha + Extension(type)) is var path && File.Exists(path)
            ? path
            : null;

    /// <summary>A media type from a file's first bytes: PNG, JPEG, GIF, WebP, AVIF, MP4 (an ISO box with an MP4 brand) or
    /// WebM (EBML with the webm doc type near the start); null for anything else.</summary>
    public static string? TypeOf(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G'])) return "image/png";
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return "image/jpeg";
        if (head.StartsWith("GIF8"u8)) return "image/gif";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = head[8..12];
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return "image/avif";
            if (brand.SequenceEqual("isom"u8) || brand.SequenceEqual("mp41"u8) || brand.SequenceEqual("mp42"u8) || brand.SequenceEqual("iso2"u8) ||
                brand.SequenceEqual("avc1"u8) || brand.SequenceEqual("M4V "u8) || brand.SequenceEqual("dash"u8))
                return "video/mp4";
        }
        if (head.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]) && head.IndexOf("webm"u8) >= 0) return "video/webm";
        return null;
    }

    /// <summary>A media type's extension in the folder (and on the page's host).</summary>
    public static string Extension(string type) => type switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/avif" => ".avif",
        "video/mp4" => ".mp4",
        _ => ".webm",
    };
}
