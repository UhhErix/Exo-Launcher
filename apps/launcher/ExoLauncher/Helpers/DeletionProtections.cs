#if EXO_IDENTITY_LINKED
namespace ExoLauncher.Helpers.Linked;
#else
namespace ExoLauncher.Helpers;
#endif

/// <summary>
/// The one list the deletion gate reads. Add a path here, never in a caller. Shared with Exo.Identity (linked, no host
/// dependencies), so it reads Exo's data folder the way PathHelper does.
/// <see cref="KeptSegments"/>: a path with these consecutive names is never deleted by Exo's cleaners, and an Exo file of
/// its own (its sign-in files, a browser profile it made) only by the code that owns it. <see cref="KeptRoots"/>: never by
/// anyone. <see cref="ShaderRoots"/> and <see cref="ShaderSegments"/>: Exo Shaders' caches, which Exo's cleaners never delete.
/// </summary>
internal static class DeletionProtections
{
    /// <summary>Sign-in data, credentials, SSH keys and game saves, by the names they carry (case-insensitive, consecutive).</summary>
    internal static readonly string[][] KeptSegments =
    [
        [".ssh"],
        ["Credentials"],
        ["Vault"],
        ["Cookies"],
        ["Login Data"],
        ["Local State"],
        ["SaveGames"],
        ["Saved Games"],
        ["My Games"],
    ];

    /// <summary>Folders never deleted by anyone: Exo's backups in Documents.</summary>
    internal static IReadOnlyList<string> KeptRoots => [Path.Combine(Documents, "Exo", "Backups")];

    /// <summary>Where the graphics drivers and DirectX keep shader caches, and Exo Shaders' own data. Exo's cleaners never delete inside them.</summary>
    internal static IReadOnlyList<string> ShaderRoots =>
    [
        Path.Combine(Local, "D3DSCache"), Path.Combine(Local, "NVIDIA"), Path.Combine(LocalLow, "NVIDIA"), Path.Combine(Local, "AMD"),
        Path.Combine(Local, "Intel"), Path.Combine(LocalLow, "Intel"), Path.Combine(Local, "SCSKiller"),
    ];

    /// <summary>Shader caches by the folder names Steam uses for them (steamapps\shadercache\&lt;app&gt;).</summary>
    internal static readonly string[][] ShaderSegments = [["steamapps", "shadercache"]];

    /// <summary>Exo's own data folder: the same override PathHelper reads, else %LocalAppData%\ExoLauncher.</summary>
    internal static string ExoDataDir
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("EXO_LAUNCHER_DATA_DIR");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(!string.IsNullOrWhiteSpace(overridden) ? overridden : Path.Combine(Local, "ExoLauncher")));
        }
    }


    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string LocalLow => Path.Combine(Path.GetDirectoryName(Local) ?? Local, "LocalLow");
    private static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
}
