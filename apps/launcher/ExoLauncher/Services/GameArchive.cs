using System.IO.Compression;

namespace ExoLauncher.Services;

/// <summary>
/// A game's zip, handled the way Exo's own store installs one: refused if any entry reaches outside its folder or is a
/// console game's file (a ROM or disc image, which no published game may carry), unpacked entry by entry into a new folder,
/// and on an update everything the game or the player made beside it (saves, settings, mods) carried across.
/// </summary>
internal static class GameArchive
{
    /// <summary>A file a console's game comes as: never inside a game Exo installs.</summary>
    private static readonly string[] ConsoleGameFiles = [".z64", ".n64", ".v64", ".iso", ".xex", ".rpx", ".nsp", ".xci", ".wbfs", ".rvz", ".gcm", ".cia", ".3ds", ".pkg"];

    /// <summary>The zip opens, and none of its entries is a console game's file or reaches outside its folder.</summary>
    internal static bool Clean(string zip)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            return archive.Entries.All(entry => SafeEntry(entry.FullName) && !ConsoleGameFiles.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A relative path that stays inside: no drive, root, ".." or name of only dots and spaces.</summary>
    internal static bool SafeEntry(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains(':') || name.StartsWith('/') || name.StartsWith('\\')) return false;
        return name.Split('/', '\\').All(part => part is not ".." && (part.Length == 0 || part.TrimEnd('.', ' ').Length > 0));
    }

    /// <summary>Unpacks into a new folder, each entry checked to land inside it.</summary>
    internal static void Unpack(string zip, string folder)
    {
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            if (!SafeEntry(entry.FullName)) throw new InvalidDataException("A file in the game points outside its folder.");
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A file in the game points outside its folder.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }

    /// <summary>Everything in the old folder the new version doesn't have, copied across (files the game or the player made).</summary>
    internal static void KeepMade(string old, string fresh, string? skip = null)
    {
        foreach (var file in Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(old, file);
            if (skip is not null && relative.Equals(skip, StringComparison.OrdinalIgnoreCase)) continue;
            var into = Path.Combine(fresh, relative);
            if (File.Exists(into)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(into)!);
            File.Copy(file, into);
        }
    }
}
