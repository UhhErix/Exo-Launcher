using System.Collections.Concurrent;
using ExoLauncher.Models;

namespace ExoLauncher.Adapters;

/// <summary>
/// A program is a store's client when its executable's full path is under that client's install folder, never because a
/// file name matches: any program called Launcher.exe or itch.exe is not the store. Where each client lives is what its
/// adapter already finds (<see cref="OfficialClientLocator"/>, Steam's root, Riot's Client folder); a process whose path
/// cannot be read is not the store and is left alone.
/// </summary>
internal static class StoreClientIdentity
{
    /// <summary>The store whose install folder holds this path, or null.</summary>
    internal static StoreKind? Of(string? path) => Of(path, Roots);

    internal static StoreKind? Of(string? path, Func<StoreKind, IReadOnlyList<string>> rootsOf)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var store in Enum.GetValues<StoreKind>())
            if (BelongsTo(store, path, rootsOf)) return store;
        return null;
    }

    internal static bool BelongsTo(StoreKind store, string? path) => BelongsTo(store, path, Roots);

    internal static bool BelongsTo(StoreKind store, string? path, Func<StoreKind, IReadOnlyList<string>> rootsOf)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }
        return rootsOf(store).Any(root => IsUnder(full, root));
    }

    /// <summary>
    /// Under the folder, or (a root ending in *) starting with it: an app package's folder is its name plus a version.
    /// </summary>
    internal static bool IsUnder(string fullPath, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        if (root.EndsWith('*'))
            return fullPath.StartsWith(root[..^1], StringComparison.OrdinalIgnoreCase);
        var folder = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly ConcurrentDictionary<StoreKind, (long Until, IReadOnlyList<string> Roots)> s_roots = new();
    private static readonly long RootsLifetimeMs = 60_000;

    /// <summary>A store's client folders, looked up at most once a minute (the lookups read the registry).</summary>
    internal static IReadOnlyList<string> Roots(StoreKind store)
    {
        var now = Environment.TickCount64;
        if (s_roots.TryGetValue(store, out var cached) && now < cached.Until) return cached.Roots;
        IReadOnlyList<string> found;
        try { found = Discover(store); }
        catch { found = []; }
        s_roots[store] = (now + RootsLifetimeMs, found);
        return found;
    }

    /// <summary>The adapter that finds the client of a store whose client is an official program Exo can open.</summary>
    private static IOfficialStoreClient? ClientOf(StoreKind store) => store switch
    {
        StoreKind.Xbox => new XboxAdapter(),
        StoreKind.Ea => new EaAdapter(),
        StoreKind.Ubisoft => new UbisoftAdapter(),
        StoreKind.BattleNet => new BattleNetAdapter(),
        StoreKind.Amazon => new AmazonAdapter(),
        StoreKind.Rockstar => new RockstarAdapter(),
        StoreKind.Itch => new ItchAdapter(),
        StoreKind.Minecraft => new MinecraftAdapter(),
        StoreKind.Roblox => new RobloxAdapter(),
        StoreKind.Paradox => new ParadoxAdapter(),
        StoreKind.Wargaming => new WargamingAdapter(),
        _ => null,
    };

    private static IReadOnlyList<string> Discover(StoreKind store)
    {
        var roots = new List<string>();
        void Add(string? folder)
        {
            if (!string.IsNullOrWhiteSpace(folder)) roots.Add(folder);
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] programFiles =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];

        switch (store)
        {
            case StoreKind.Steam:
                Add(SteamInstallation.ResolveRoot());
                break;
            case StoreKind.Epic:
                // The launcher's own folder, whichever layout: its helpers sit beside or below it, never the games.
                Add(TrimTo(EpicAdapter.ResolveEpicLauncher(), @"Epic Games\Launcher"));
                Add(Path.Combine(local, "Epic Games", "Launcher"));
                foreach (var folder in programFiles) Add(Path.Combine(folder, "Epic Games", "Launcher"));
                break;
            case StoreKind.Gog:
                Add(DirectoryOf(GogAdapter.ResolveGalaxy()));
                foreach (var folder in programFiles) Add(Path.Combine(folder, "GOG Galaxy"));
                break;
            case StoreKind.Riot:
                // Riot Games\Riot Client only: never Vanguard or the games, which sit in their own folders.
                Add(DirectoryOf(RiotAdapter.TryResolveRiotClientServicesPublic()));
                break;
            default:
                if (ClientOf(store) is { } client)
                {
                    if (client.GetClientLaunchCommand() is { IsAppx: false } command) Add(DirectoryOf(command.FileName));
                    if (client is AgentPresentAdapterBase { AppxPackagePrefix: { } prefix })
                        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", prefix) + "*");
                }
                // Clients that keep a folder per version beside each other.
                if (store == StoreKind.Roblox) Add(Path.Combine(local, "Roblox"));
                if (store == StoreKind.Paradox)
                {
                    Add(Path.Combine(local, "Programs", "Paradox Interactive"));
                    foreach (var folder in programFiles) Add(Path.Combine(folder, "Paradox Interactive"));
                }
                break;
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? DirectoryOf(string? file)
    {
        try { return string.IsNullOrWhiteSpace(file) ? null : Path.GetDirectoryName(Path.GetFullPath(file)); }
        catch { return null; }
    }

    /// <summary>The path up to and including a folder chain (case-insensitive), or null when it is not in the path.</summary>
    internal static string? TrimTo(string? path, string folderChain)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var at = path.IndexOf(Path.DirectorySeparatorChar + folderChain, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? DirectoryOf(path) : path[..(at + 1 + folderChain.Length)];
    }
}
