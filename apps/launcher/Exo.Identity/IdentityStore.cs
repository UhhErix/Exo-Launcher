namespace Exo.Identity;

/// <summary>
/// Where this Windows user's Exo identity lives: one folder outside any app's own data, %LOCALAPPDATA%\Exo\identity. A test
/// copy passes its own folder (or sets <see cref="FolderVariable"/>); its keys then carry a test suffix
/// (<see cref="CngDeviceKeys.TestSuffix"/>) and it talks only to a mailbox on this PC (<see cref="Mailbox"/>).
/// </summary>
public static class IdentityStore
{
    /// <summary>A test copy's folder, for an app that has no data-folder override of its own.</summary>
    public const string FolderVariable = "EXO_IDENTITY_DIR";

    /// <summary>The real folder: %LOCALAPPDATA%\Exo\identity.</summary>
    public static string RealFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Exo", "identity");

    /// <summary><see cref="FolderVariable"/> when it is set, else the dev servers' own folder while they are on
    /// (<see cref="DevServers"/>), else <see cref="RealFolder"/>.</summary>
    public static string Folder => Environment.GetEnvironmentVariable(FolderVariable) is { Length: > 0 } folder ? folder
        : DevServers.Current is not null ? DevServers.IdentityFolder : RealFolder;

    /// <summary>Whether <paramref name="folder"/> is a test copy's: anything but the real folder.</summary>
    public static bool IsTest(string folder) => Normalize(folder) != Normalize(RealFolder);

    /// <summary>The folder's full path in lowercase without a trailing separator, as its test suffix hashes it.</summary>
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToLowerInvariant();
}
