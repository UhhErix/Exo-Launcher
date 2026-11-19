namespace ExoLauncher.Helpers;

/// <summary>
/// A path Exo may look at on this PC: fully qualified on a fixed or removable drive. A network share, a device path, an extended
/// path or a mapped network drive would make Exo contact another machine (a planted backup could name a share to leak a hash),
/// so none is probed.
/// </summary>
internal static class LocalPath
{
    internal static bool IsLocalDrivePath(string path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return false;
        var root = Path.GetPathRoot(path);
        if (root is not { Length: 3 } || !char.IsAsciiLetter(root[0]) || root[1] != ':') return false;
        try
        {
            var kind = new DriveInfo(root).DriveType;
            return kind is DriveType.Fixed or DriveType.Removable;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Whether the folder is there, checked only when its path is local (never a share).</summary>
    internal static bool ExistsLocally(string path) => IsLocalDrivePath(path) && Directory.Exists(path);
}
