using ExoLauncher.Adapters;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Which processes are a library game: an image inside an installed game's folder (not a helper folder), and not a
/// store client, overlay, anti-cheat or launcher by name. <paramref name="gameRoots"/> are folder prefixes as
/// <see cref="ProcessHelper.RootPrefix"/> makes them (full path, trailing separator). The image's own folders are walked
/// from the deepest up, so each test is a set lookup, not a scan of every folder.
/// </summary>
internal static class GameStartMatcher
{
    internal static bool IsGameProcess(string? imagePath, string processName, IReadOnlySet<string> gameRoots)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || gameRoots.Count == 0) return false;
        if (GameProcessRegistry.IsReservedProcessName(processName)) return false;
        string image;
        try { image = Path.GetFullPath(imagePath); }
        catch { return false; }
        for (var directory = Path.GetDirectoryName(image); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (gameRoots.Contains(prefix) && !ProcessHelper.IsInHelperFolder(image, prefix)) return true;
        }
        return false;
    }
}

/// <summary>
/// The installed games' folders as a set, built once and rebuilt only after the library changes. Every window event
/// reads it, so a build is a library walk: it must not happen per event.
/// </summary>
internal sealed class GameFolderSet(Func<IReadOnlyList<string>> load)
{
    private HashSet<string>? _roots;

    /// <summary>How many times the set has been built (a test reads it; the bridge does not).</summary>
    internal int Builds { get; private set; }

    /// <summary>The library changed: the next read builds the set again.</summary>
    internal void Invalidate() => _roots = null;

    internal IReadOnlySet<string> Roots => _roots ??= Build();

    private HashSet<string> Build()
    {
        Builds++;
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in load())
        {
            if (ProcessHelper.RootPrefix(folder) is { } prefix) roots.Add(prefix);
        }
        return roots;
    }
}

/// <summary>
/// The window-event decision: a process id (from a window that showed or came to the front) is a game when its image
/// is in an installed game's folder. The image is resolved once per process (<see cref="ProcessImageCache"/>), and the
/// folder set is built once per library change (<see cref="GameFolderSet"/>), so an event with nothing new costs a
/// dictionary lookup and a few set lookups.
/// </summary>
internal sealed class GameWindowWatch(Func<uint, string?> resolveImage, GameFolderSet folders)
{
    private readonly ProcessImageCache _images = new(resolveImage);

    internal bool IsGameWindow(uint processId)
    {
        var image = _images.ImageOf(processId);
        return image is not null && GameStartMatcher.IsGameProcess(image, Path.GetFileNameWithoutExtension(image), folders.Roots);
    }

    /// <summary>The library changed (the existing LibraryUpdated signal): the folders are read again on the next event.</summary>
    internal void LibraryChanged() => folders.Invalidate();
}

/// <summary>
/// When the run-state scan runs. With window events, a scan runs after a window of a game's folder shows or comes to the
/// front, after a watched game process exits, and once when Exo launches a game. Nothing repeats while every running game
/// is watched. Without the window hooks, or while a running game's exit can't be watched, it runs every 15 s.
/// </summary>
internal static class RunStatePlan
{
    internal static readonly TimeSpan FallbackEvery = TimeSpan.FromSeconds(15);

    /// <summary>The repeat interval, or null for none.</summary>
    internal static TimeSpan? PeriodicEvery(bool eventsAvailable, bool anyRunningGameUnwatched) =>
        !eventsAvailable || anyRunningGameUnwatched ? FallbackEvery : null;
}

/// <summary>
/// Which WinEvents mean "a top-level window appeared". <c>EVENT_OBJECT_SHOW</c> fires for every window and control; only
/// the window itself (idObject OBJID_WINDOW, idChild CHILDID_SELF) that is a top-level window counts. Owned popups and
/// child controls don't.
/// </summary>
internal static class WindowStarts
{
    internal const uint EventObjectShow = 0x8002;
    internal const int ObjidWindow = 0;

    internal static bool IsTopLevelShow(uint evt, int idObject, int idChild, bool topLevel) =>
        evt == EventObjectShow && idObject == ObjidWindow && idChild == 0 && topLevel;
}

/// <summary>
/// The image path of each process a window belongs to, resolved once per process id. Repeated shows and focus changes of
/// the same process cost a dictionary lookup, not a process open. A failed resolve (access denied) is kept too, so a
/// protected process is not reopened on every show. The cache is cleared when it fills; a process id reused by another
/// program can carry a stale path into one match, which costs at most one extra scan. Used from the UI thread only.
/// </summary>
internal sealed class ProcessImageCache(Func<uint, string?> resolve, int capacity = 256)
{
    private readonly Dictionary<uint, string?> _images = new();

    internal int Count => _images.Count;

    internal string? ImageOf(uint processId)
    {
        if (_images.TryGetValue(processId, out var known)) return known;
        if (_images.Count >= capacity) _images.Clear();
        var image = resolve(processId);
        _images[processId] = image;
        return image;
    }
}
