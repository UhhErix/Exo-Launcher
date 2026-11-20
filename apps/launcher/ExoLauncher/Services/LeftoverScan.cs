using System.Runtime.InteropServices;
using System.Text;
using ExoLauncher.Helpers;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ExoLauncher.Services;

internal enum LeftoverKind { Folder, File, Shortcut, Registry, Service, Task, Driver }

/// <summary>
/// How sure the scan is that a leftover is the app's (Review says it beside each, Revo's and BCU's confidence): Certain, it's
/// inside the app's folder or points there, or carries its id, entry or Installer record; Likely, it has the app's own folder
/// name; Possible, it's only named like it, or sits in a firewall group beside its rules. Only Certain is ever pre-selected.
/// </summary>
internal enum Certainty { Certain, Likely, Possible }

/// <summary>
/// One thing an app left: a file, folder or shortcut, a registry key (<see cref="Value"/> null) or value, a service (its
/// key) or a scheduled task (its path). Machine: only an administrator may remove it. Likely: matched by a name alone, as
/// BCU's "questionable": shown apart and only deleted when asked for on its own. Held: why something else may still need
/// it (a running program, a shared file inside). Selected: one press takes it, only when it is sure and nothing holds it.
/// </summary>
internal sealed record Leftover(string Path, bool IsKey, string? Value, bool Machine, bool Likely = false, LeftoverKind? As = null)
{
    internal string? Held { get; init; }

    /// <summary>One press takes it only when it is <see cref="Certainty.Certain"/> and nothing holds it.</summary>
    internal bool Selected => !Likely && Held is null;

    /// <summary>Of what's matched by a name alone, the weaker evidence: named like it, or beside its own in a firewall group.</summary>
    internal bool Loose { get; init; }

    /// <summary>What ties it to the app, in the page's words ("inside the app's folder"); null says it plainly by its certainty.</summary>
    internal string? Because { get; init; }

    /// <summary>A driver's packages in Windows' driver store (oemNN.inf) that install its service: removed with it, saved first.</summary>
    internal IReadOnlyList<string>? Infs { get; init; }

    internal Certainty Sure => !Likely ? Certainty.Certain : Loose ? Certainty.Possible : Certainty.Likely;

    /// <summary>Read once, while it is still there.</summary>
    internal LeftoverKind Kind { get; } = As ?? (IsKey ? LeftoverKind.Registry
        : Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ? LeftoverKind.Shortcut
        : Directory.Exists(Path) ? LeftoverKind.Folder : LeftoverKind.File);
}

/// <summary>What an app left, by kind: folders and files (with their size), registry entries (services and tasks too), shortcuts.</summary>
internal sealed record LeftoverSummary(int Folders, int Files, int Registry, int Shortcuts, long Bytes)
{
    internal int Count => Folders + Files + Registry + Shortcuts;
}

/// <summary>
/// Who an app is, for finding what it left: its own folders (<see cref="Homes"/>: anything pointing into one is its), its
/// own folder and key names (<see cref="Names"/>, sure only when a list names them, else likely), its executables'
/// names (crash reports), the ids Windows knows it by (app ids; a Store package family as "Family_"), its Apps &amp;
/// features entries and names, its publisher, its MSI product code, and folders that hold something still installed
/// (<see cref="Keep"/>: never deleted, nor anything holding them).
/// </summary>
internal sealed record Trail(string Name)
{
    internal IReadOnlyList<string> Homes { get; init; } = [];
    internal IReadOnlyList<string> Names { get; init; } = [];
    internal bool NamesSure { get; init; }
    internal IReadOnlyList<string> Exes { get; init; } = [];
    internal IReadOnlyList<string> Ids { get; init; } = [];
    internal IReadOnlyList<string> Entries { get; init; } = [];
    internal IReadOnlyList<string> DisplayNames { get; init; } = [];
    internal string? Publisher { get; init; }
    internal string? ProductCode { get; init; }
    internal IReadOnlyList<string> Keep { get; init; } = [];

    /// <summary>Why its own folder isn't to be taken in one press (a store's holds games and saves).</summary>
    internal string? HomeHeld { get; init; }
}

/// <summary>
/// The deep clean, as Revo Uninstaller's advanced scan and BCU's junk search find leftovers, for any app once it is gone.
/// Each lead is tied to the app (<see cref="Trail"/>), never a word searched for across the disk:
/// <list type="bullet">
/// <item>its folders, and its names where programs keep theirs (Local, LocalLow, Roaming, ProgramData, Program Files,
/// Temp) and their keys (Software, and under its publisher), its crash dumps and crash reports, its VirtualStore copy;</item>
/// <item>anything pointing into its folders: shortcuts (desktop, Start, the taskbar, Startup), Windows' records of a
/// program by its path (the name cache, compatibility, startup, recent apps, UserAssist, Game Bar's list), link and file
/// handlers, COM classes and type libraries, App Paths, services, scheduled tasks, event log sources, SharedDLLs,
/// Installer folders and firewall rules;</item>
/// <item>its app ids (notifications, recent apps) and a Store package's own records;</item>
/// <item>its MSI registration (by product code) and Apps &amp; features entries whose uninstaller is gone.</item>
/// </list>
/// Never Windows' folders or keys, a place itself, a folder holding something still installed, a generic or company name,
/// or a name of four letters or fewer (BCU's rules). <c>deep</c> adds what is slow to read (COM, services, tasks): after an
/// uninstall, not for a passing look.
/// </summary>
internal sealed partial class LeftoverScan
{
    private readonly IReadOnlyDictionary<string, string> _places;
    private readonly string _hkcuBase, _hklmBase;
    private readonly bool _isTest;
    private Sources? _sources;

    /// <param name="places">Folder per place name. <paramref name="hkcuBase"/> and <paramref name="hklmBase"/> are prefixes
    /// under HKCU for tests (empty: the real hives); tests delete for good instead of to the Recycle Bin.</param>
    internal LeftoverScan(IReadOnlyDictionary<string, string> places, string hkcuBase = "", string hklmBase = "")
    {
        _places = places;
        _hkcuBase = hkcuBase;
        _hklmBase = hklmBase;
        _isTest = hkcuBase.Length > 0;
        Recycler = _isTest ? ForGood : Recycle;
    }

    /// <summary>Moves a file or folder to the Recycle Bin: null when it went, else why it stayed. Tests delete for good, or set their own.</summary>
    internal Func<string, string?> Recycler { get; init; }

    /// <summary>Each one Exo refused to delete, with why, said plainly ("too big for the Recycle Bin").</summary>
    internal Dictionary<Leftover, string> Stayed { get; } = [];

    // Why one stayed, said after "stayed: ".
    internal const string Barred = "Exo doesn't delete there";
    internal const string Linked = "it's a link, or inside one";
    internal const string Moved = "it now leads somewhere else";
    internal const string Unquotable = "its name can't be passed to Windows safely";
    internal const string TooBig = "too big for the Recycle Bin";
    internal const string NoBin = "its drive has no Recycle Bin";
    internal const string BinOff = "the Recycle Bin is off for its drive";
    internal const string NotRecycled = "in use, or it didn't go to the Recycle Bin";
    internal const string InUse = "in use, or Windows' permission was declined";

    internal static Dictionary<string, string> ThisPc() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["LocalAppData"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ["LocalLow"] = Path.Combine(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!, "LocalLow"),
        ["AppData"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ["Desktop"] = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ["CommonDesktop"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        ["CommonPrograms"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        ["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ["ProgramFilesX86"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ["Temp"] = Path.GetTempPath().TrimEnd('\\'),
    };

    internal const string Programs = @"{AppData}\Microsoft\Windows\Start Menu\Programs";
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly string[] NameRoots = [@"{LocalAppData}", @"{LocalAppData}\Programs", @"{LocalLow}", @"{AppData}", @"{ProgramData}", @"{ProgramFiles}", @"{ProgramFilesX86}", @"{Temp}"];
    private static readonly string[] MachineRoots = ["{ProgramData}", "{ProgramFiles}", "{ProgramFilesX86}", "{CommonDesktop}", "{CommonPrograms}", "HKLM"];

    // Names no app owns alone (BCU's generic and company names, and Windows' own folders): a company's key or folder holds
    // all its programs.
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "install", "installer", "settings", "config", "configuration", "users", "data", "common", "common files", "shared", "tools", "update", "updates",
        "updater", "app", "apps", "program", "programs", "games", "game", "launcher", "client", "cache", "temp", "logs", "bin", "x64", "x86",
        "microsoft", "windows", "windowsapps", "packages", "package cache", "uninstall information", "directx", "nvidia corporation", "amd",
        "intel", "google", "mozilla", "adobe", "steam", "epic games", "riot games", "ubisoft", "electronic arts", "valve", "software", "classes",
        "wow6432node", "policies", "clients", "system", "drivers", "runtime", "runtimes", "plugins", "extensions", "services", "helper",
        "microsoft corporation", "nvidia", "intel corporation", "advanced micro devices, inc.", "google llc", "mozilla corporation", "adobe inc.",
        "adobe systems incorporated", "apple", "apple inc.", "oracle", "oracle corporation", "valve corporation", "epic games, inc.", "riot games, inc.",
        "ubisoft entertainment", "blizzard entertainment", "activision", "bethesda softworks", "rockstar games", "unity technologies", "jetbrains",
        "autodesk", "logitech", "razer", "razer inc.", "corsair", "asus", "asustek computer inc.", "realtek", "realtek semiconductor corp.", "samsung",
        "lenovo", "dell", "dell inc.", "hp inc.", "hewlett-packard", "western digital", "seagate", "steelseries", "elgato", "dolby", "khronos group",
        "python software foundation", "meta", "oculus",
    };

    /// <summary>Everything that app left, each once (none inside another that goes), only what is there now.</summary>
    internal List<Leftover> Find(Trail trail, bool deep)
    {
        var homes = Homes(trail.Homes);
        var keep = trail.Keep.Select(path => path.TrimEnd('\\') + '\\').ToList();
        // What points into a folder another program still lives in, or names an anti-cheat, is never this app's: of two
        // programs in one folder, the one still here keeps its shortcuts, services and tasks.
        var theirs = Homes(trail.Keep);
        bool Ours(string? text) => PointsInto(text, homes) && !PointsInto(text, theirs) && !AntiCheatIn(text);
        // A name alone (likely) is never a generic or company name, nor the publisher's own: its key holds all its programs.
        var names = trail.Names.Where(name => OneName(name) && (trail.NamesSure || name.Length > 4 && !Generic.Contains(name) &&
            !string.Equals(name, trail.Publisher?.Trim(), StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var likely = !trail.NamesSure;
        var list = new List<Leftover>();
        // Its publisher's folder and its own name together are two facts, not one: that's the program's own, and it goes with it.
        var tied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var home in homes) list.Add(new(home.TrimEnd('\\'), false, null, MachinePlace(home)) { Because = InFolder });
        // Its own folder's name is likely; a name the app only goes by (its name without its version) when its folder is
        // called something else is only possible, as is anything named like it in Temp.
        var folderNames = homes.Select(home => Path.GetFileName(home.TrimEnd('\\'))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var loose = likely && folderNames.Count > 0 && !folderNames.Contains(name);
            foreach (var root in NameRoots) list.Add(new(Expand(root + '\\' + name), false, null, MachinePlace(root), likely) { Loose = loose || likely && root == "{Temp}" });
            list.Add(new(@"HKCU\Software\" + name, true, null, false, likely) { Loose = loose });
            list.Add(new(@"HKLM\SOFTWARE\" + name, true, null, true, likely) { Loose = loose });
            list.Add(new(@"HKLM\SOFTWARE\WOW6432Node\" + name, true, null, true, likely) { Loose = loose });
            if (trail.Publisher is { Length: > 4 } publisher && OneName(publisher) && !Generic.Contains(publisher) && !publisher.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new($@"HKCU\Software\{publisher}\{name}", true, null, false, likely) { Loose = loose });
                list.Add(new($@"HKLM\SOFTWARE\{publisher}\{name}", true, null, true, likely) { Loose = loose });
                foreach (var root in new[] { "{LocalAppData}", "{AppData}", "{ProgramData}" }) list.Add(new(Expand($@"{root}\{publisher}\{name}"), false, null, MachinePlace(root), likely) { Loose = loose });
                tied.UnionWith([$@"HKCU\Software\{publisher}\{name}", $@"HKLM\SOFTWARE\{publisher}\{name}", .. new[] { "{LocalAppData}", "{AppData}", "{ProgramData}" }.Select(root => Expand($@"{root}\{publisher}\{name}"))]);
            }
        }
        // Crash dumps and Windows Error Reporting's reports, by the executable's name.
        foreach (var exe in trail.Exes.Where(exe => exe.Length > 4 && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var dump in Matching(Expand(@"{LocalAppData}\CrashDumps"), exe + ".*.dmp")) list.Add(new(dump, false, null, false) { Because = Crashes });
            foreach (var (root, machine) in new[] { (@"{LocalAppData}\Microsoft\Windows\WER", false), (@"{ProgramData}\Microsoft\Windows\WER", true) })
            foreach (var store in new[] { "ReportArchive", "ReportQueue" })
            foreach (var report in Folders(Expand($@"{root}\{store}"), $"AppCrash_{exe}_*"))
                list.Add(new(report, false, null, machine) { Because = Crashes });
        }
        // A program in Program Files that wrote where it may not was given its own copy there (VirtualStore).
        foreach (var home in homes)
            if (home.Length > 3 && home[1] == ':')
                list.Add(new(Expand(@"{LocalAppData}\VirtualStore") + home[2..].TrimEnd('\\'), false, null, false) { Because = "Windows' copy of the app's folder" });
        // A Store app's own records, by its package family.
        foreach (var family in trail.Ids.Where(id => id.EndsWith('_')).Select(id => id.TrimEnd('_')))
            foreach (var (path, isKey) in StoreRecords(family)) list.Add(new(path, isKey, null, false) { Because = ItsId });
        list.AddRange(DeadEntries(trail));
        foreach (var entry in trail.Entries) list.Add(new(entry, true, null, entry.StartsWith("HKLM", StringComparison.Ordinal)) { Because = "Its Installed apps entry" });
        if (trail.ProductCode is { } product && Packed(product) is { } packed)
        {
            list.Add(new($@"HKLM\SOFTWARE\Classes\Installer\Products\{packed}", true, null, true) { Because = Installer });
            list.Add(new($@"HKLM\SOFTWARE\Classes\Installer\Features\{packed}", true, null, true) { Because = Installer });
            list.Add(new($@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\{packed}", true, null, true) { Because = Installer });
            list.Add(new($@"HKCU\Software\Microsoft\Installer\Products\{packed}", true, null, false) { Because = Installer });
            list.Add(new($@"HKCU\Software\Microsoft\Installer\Features\{packed}", true, null, false) { Because = Installer });
        }
        var ids = trail.Ids;
        if (homes.Count > 0 || ids.Count > 0)
        {
            var sources = _sources ??= ReadSources(deep);
            if (deep && !sources.Deep) sources = _sources = ReadSources(deep);
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, name, text, machine) in sources.Values)
                if (Ours(name) || Ours(text) ||
                    ids.Any(id => name.Contains(id, StringComparison.OrdinalIgnoreCase) || text.Contains(id, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(new(key, true, name, machine) { Because = Ours(name) || Ours(text) ? PointsThere : ItsId });
                    if (key.Equals(FirewallRules, StringComparison.OrdinalIgnoreCase) && FirewallGroup(text) is { } group) groups.Add(group);
                }
            // The firewall group its own rules are in: the group's other rules (a port it opened, naming no program) are possible.
            foreach (var (key, name, text, machine) in sources.Values)
                if (groups.Count > 0 && key.Equals(FirewallRules, StringComparison.OrdinalIgnoreCase) && FirewallGroup(text) is { } group && groups.Contains(group) && !AntiCheatIn(text))
                    list.Add(new(key, true, name, machine, Likely: true) { Loose = true, Because = SameGroup });
            foreach (var (key, text, machine, kind) in sources.Keys)
            {
                // A driver whose file is in the app's folder: shown, never taken by one press (a device may still need it),
                // with the driver store's package that installs it (pnputil refuses one a device still uses).
                if (kind == KeySource.Driver)
                {
                    if (Ours(text)) list.Add(new(key, true, null, true, false, LeftoverKind.Driver) { Held = DriverHeld, Because = PointsThere, Infs = DriverInfs(key.Split('\\')[^1], text) });
                    continue;
                }
                if (Ours(text) || kind == KeySource.AppId && ids.Any(id => text.StartsWith(id, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new(key, true, null, machine, false, kind == KeySource.Service ? LeftoverKind.Service : null) { Because = Ours(text) ? PointsThere : ItsId });
            }
            // A shortcut by where it points; an internet shortcut (a store's game: steam://rungameid/730) also by its link.
            foreach (var (path, target, machine) in sources.Shortcuts)
                if (Ours(target) || ids.Any(id => target.StartsWith(id + '|', StringComparison.OrdinalIgnoreCase)))
                    list.Add(new(path, false, null, machine) { Because = Ours(target) ? PointsThere : ItsId });
            foreach (var (path, exe) in sources.Tasks)
                if (Ours(exe)) list.Add(new(path, false, null, true, false, LeftoverKind.Task) { Because = PointsThere });
            // What names the app's COM classes and file types (Revo's moderate scan): its shell extensions' entries and
            // their approval, a file type it was the handler of, its Open With entries.
            var classes = list.Where(item => item.IsKey && item.Value is null && item.Path.Contains(@"\CLSID\{", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Path[(item.Path.LastIndexOf('\\') + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var progIds = list.Where(item => item.IsKey && item.Value is null && ClassRoots.Any(root => item.Path.StartsWith(root.Path + '\\', StringComparison.OrdinalIgnoreCase)) &&
                    item.Path.Count(c => c == '\\') == ClassRoots.First(root => item.Path.StartsWith(root.Path + '\\', StringComparison.OrdinalIgnoreCase)).Path.Count(c => c == '\\') + 1)
                .Select(item => item.Path[(item.Path.LastIndexOf('\\') + 1)..]).Where(name => !name.StartsWith('{') && !name.StartsWith('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, name, target, machine) in sources.Refs)
                if (classes.Contains(target) || progIds.Contains(target))
                    list.Add(name is null ? new(key, true, null, machine) { Because = Handler } : new(key, true, name, machine) { Because = Handler });
            // A file type Windows still opens with it: its UserChoice is signed, so Exo never writes it; Default apps changes it.
            _defaults.UnionWith(DeadChoices(sources.Choices, progIds, ClassThere, names, trail.Exes));
        }
        // An MSI's upgrade code still naming it.
        if (trail.ProductCode is { } code && Packed(code) is { } upgraded)
        {
            const string Upgrades = @"HKLM\SOFTWARE\Classes\Installer\UpgradeCodes";
            ReadSubKeys(Upgrades, (upgrade, key) =>
            {
                if (key.GetValueNames().Contains(upgraded, StringComparer.OrdinalIgnoreCase))
                    list.Add(key.GetValueNames().Length == 1 ? new($@"{Upgrades}\{upgrade}", true, null, true) : new($@"{Upgrades}\{upgrade}", true, upgraded, true));
            });
        }
        // A startup entry that goes takes Task Manager's on/off record of it along.
        foreach (var run in list.Where(item => item.Value is { } name && item.Path.EndsWith(@"\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase)).ToList())
            foreach (var approved in new[] { "Run", "Run32" })
                list.Add(new(run.Path.Replace(@"\CurrentVersion\Run", @"\CurrentVersion\Explorer\StartupApproved\" + approved, StringComparison.OrdinalIgnoreCase), true, run.Value, run.Machine));
        var found = Weighed(Outermost(list.Where(item => Allowed(item, keep) && Exists(item)).ToList()), theirs)
            .Select(item => item.Likely && item.Because != SameGroup && (tied.Contains(item.Path) || HoldsItsExe(item, trail.Exes))
                ? item with { Likely = false, Loose = false, Because = tied.Contains(item.Path) ? "Its name, in its publisher's folder" : "Holds the app's own program" } : item).ToList();
        return trail.HomeHeld is not { } held ? found : found.Select(item => item.Held is null && homes.Contains(item.Path + '\\', StringComparer.OrdinalIgnoreCase) ? item with { Held = held } : item).ToList();
    }

    /// <summary>
    /// What is really free to go, of what was found: a file another program also counts in SharedDLLs, and that count, aren't
    /// leftovers at all; a folder found by name alone that something still there runs from isn't either, nor is one with
    /// anti-cheat inside; a folder with a shared file or a running program inside is held, so it is shown but nothing takes it unless it is picked by hand.
    /// </summary>
    private List<Leftover> Weighed(List<Leftover> found, List<string> theirs)
    {
        var shared = SharedFiles();
        var running = Running?.Invoke() ?? RunningPrograms();
        var alive = found.Any(item => item.Likely && item.Kind == LeftoverKind.Folder) ? LivePointers(theirs) : [];
        var kept = new List<Leftover>();
        foreach (var item in found)
        {
            if (item.IsKey && item.Value is { } value && item.Path.EndsWith(@"\SharedDLLs", StringComparison.OrdinalIgnoreCase) && shared.Contains(value)) continue;
            if (item.Kind == LeftoverKind.File && shared.Contains(item.Path)) continue;
            if (item.Kind != LeftoverKind.Folder) { kept.Add(item); continue; }
            var inside = item.Path.TrimEnd('\\') + '\\';
            bool In(string path) => path.StartsWith(inside, StringComparison.OrdinalIgnoreCase);
            if (item.Likely && alive.Any(In) || HoldsAntiCheat(item.Path)) continue;
            kept.Add(shared.Any(In) ? item with { Held = StillShared } : running.Any(In) ? item with { Held = StillRunning } : item);
        }
        return kept;
    }

    // Taking a folder takes all that's in it: one with anti-cheat inside isn't found at all, however it's tied to the program.
    private static bool HoldsAntiCheat(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
                .Any(entry => AntiCheatIn(entry.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    // A folder found by name that also holds one of the program's own exes (never its updater or a helper): that's its.
    private static bool HoldsItsExe(Leftover item, IReadOnlyList<string> exes)
    {
        if (item.Kind != LeftoverKind.Folder || exes.Count == 0) return false;
        try
        {
            return Directory.EnumerateFiles(item.Path, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                .Select(Path.GetFileName).OfType<string>().Any(exe => AppsUsage.Opened(exe) && exes.Contains(exe, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal const string DriverHeld = "A driver: only take it if no device needs it";

    // What ties a leftover to the app, said after its certainty in Review.
    internal const string InFolder = "Inside the app's folder";
    internal const string PointsThere = "Points into the app's folder";
    internal const string ItsId = "Carries the app's own id";
    internal const string Installer = "Its Windows Installer record";
    internal const string Crashes = "Its crash reports";
    internal const string Handler = "Names the app's file types or shell add-ons";
    internal const string SameGroup = "In a firewall group with its rules";
    internal const string NamedAfter = "Named after it";
    internal const string NamedLike = "Only named like it";

    /// <summary>A leftover's certainty and what it rests on, as Review says it: "Certain: inside the app's folder".</summary>
    internal static string Said(Certainty sure, string? because)
    {
        var why = because ?? (sure == Certainty.Certain ? "Tied to the app" : sure == Certainty.Likely ? NamedAfter : NamedLike);
        return (sure switch { Certainty.Certain => "Certain", Certainty.Likely => "Likely", _ => "Possible" }) + ": " + char.ToLowerInvariant(why[0]) + why[1..];
    }

    /// <summary>File types Windows opens with the app (the last found): it's gone, so each needs a new default from Default apps.</summary>
    internal IReadOnlyList<string> Defaults => [.. _defaults.Order(StringComparer.Ordinal)];

    private readonly HashSet<string> _defaults = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// File types whose default (UserChoice's ProgId) is the app's: one of its own ProgIds, its Applications\exe entry, or a
    /// ProgId no longer registered anywhere that carries its name. Windows signs UserChoice, so Exo never forges or deletes it:
    /// the page sends the player to Default apps. A ProgId that's still registered and isn't the app's is never counted.
    /// </summary>
    internal static List<string> DeadChoices(IEnumerable<(string Ext, string ProgId)> choices, IReadOnlySet<string> progIds, Func<string, bool> classThere,
        IReadOnlyList<string> names, IReadOnlyList<string> exes) => choices
        .Where(choice => choice.Ext.StartsWith('.') && choice.ProgId.Length > 0 && (progIds.Contains(choice.ProgId) ||
            exes.Any(exe => choice.ProgId.Equals(@"Applications\" + exe, StringComparison.OrdinalIgnoreCase)) ||
            !classThere(choice.ProgId) && names.Any(name => name.Length > 4 && choice.ProgId.Contains(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))))
        .Select(choice => choice.Ext.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToList();

    private bool ClassThere(string progId) =>
        ClassRoots.Any(root => { try { using var key = Open(root.Path + '\\' + progId, writable: false); return key is not null; } catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException or ArgumentException) { return true; } });

    /// <summary>A firewall rule's group (its EmbedCtxt), when it's the app's own: never one of Windows' (named by a resource, "@FirewallAPI.dll,-…") or a short generic one.</summary>
    internal static string? FirewallGroup(string rule)
    {
        foreach (var part in rule.Split('|'))
            if (part.StartsWith("EmbedCtxt=", StringComparison.OrdinalIgnoreCase) && part[10..].Trim() is { Length: > 4 } group && !group.StartsWith('@') && !Generic.Contains(group))
                return group;
        return null;
    }

    /// <summary>Where Windows keeps its driver store's INFs; tests set their own.</summary>
    internal string? InfFolder { get; init; }

    /// <summary>The driver store's packages (oemNN.inf) that add this driver's service with its file: what pnputil /delete-driver takes.</summary>
    private List<string> DriverInfs(string service, string image)
    {
        var folder = InfFolder ?? (_isTest ? null : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF"));
        if (folder is null || !Directory.Exists(folder)) return [];
        var infs = new List<(string, string)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "oem*.inf").Take(2000))
                if (new FileInfo(file).Length < 4 << 20) infs.Add((Path.GetFileName(file), File.ReadAllText(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Leftovers: INFs unreadable: " + ex.GetType().Name); }
        return InfsFor(service, image, infs);
    }

    /// <summary>
    /// Of the driver store's INFs (name, text), those named oemNN.inf whose AddService installs <paramref name="service"/> and
    /// that copy its file (the service's ImagePath's file name): never an INF that only mentions one of them.
    /// </summary>
    internal static List<string> InfsFor(string service, string image, IEnumerable<(string Name, string Text)> infs)
    {
        var file = Path.GetFileName(Environment.ExpandEnvironmentVariables(image).Trim().Trim('"'));
        if (file.Length == 0 || service.Length == 0) return [];
        var adds = new System.Text.RegularExpressions.Regex(@"^\s*AddService\s*=\s*""?" + System.Text.RegularExpressions.Regex.Escape(service) + @"""?\s*,",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return infs.Where(inf => OemInf().IsMatch(inf.Name) && adds.IsMatch(inf.Text) && inf.Text.Contains(file, StringComparison.OrdinalIgnoreCase))
            .Select(inf => inf.Name.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToList();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^oem\d{1,4}\.inf\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    internal static partial System.Text.RegularExpressions.Regex OemInf();

    /// <summary>Where a driver's store package is exported before it goes: a folder only administrators may write, made in the elevated step.</summary>
    internal string DriverBackupRoot => Path.Combine(_isTest ? Expand("{ProgramData}") : Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ExoLauncher", "DriverBackup");

    /// <summary>Each driver's backup folder, by the leftover, set when its elevated step was written (<see cref="AdminCommand"/>).</summary>
    internal Dictionary<Leftover, (string Folder, IReadOnlyList<string> Infs)> DriverBackups { get; } = [];
    internal const string StillShared = "Another program shares a file in it";
    internal const string StillRunning = "A running program uses it";

    /// <summary>Tests: the programs running now, by their full paths; null reads them from Windows.</summary>
    internal Func<IReadOnlyCollection<string>>? Running { get; init; }

    private static List<string> RunningPrograms()
    {
        var paths = new List<string>();
        foreach (var row in Helpers.ProcessSnapshot.Read())
        {
            if (row.Pid <= 4) continue;
            if (Adapters.ProcessHelper.QueryFullProcessImageName(row.Pid) is { Length: > 0 } path) paths.Add(path);
        }
        return paths;
    }

    /// <summary>The files SharedDLLs counts for more than one program.</summary>
    private HashSet<string> SharedFiles()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Open(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", writable: false);
            foreach (var name in key?.GetValueNames() ?? [])
                if (key!.GetValue(name) is int count && count > 1) files.Add(name);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        return files;
    }

    /// <summary>What a service, task, shortcut or App Path runs that is really there, outside what is another program's anyway.</summary>
    private List<string> LivePointers(List<string> theirs)
    {
        var sources = _sources ??= ReadSources(deep: false);
        return sources.Keys.Where(key => key.Kind is not (KeySource.AppId or KeySource.Driver)).Select(key => key.Text)
            .Concat(sources.Shortcuts.Select(link => link.Target)).Concat(sources.Tasks.Select(task => task.Exe))
            .Select(CommandTarget).OfType<string>().Where(path => File.Exists(path) && !PointsInto(path, theirs)).ToList();
    }

    // ---- what points at a program that is gone ----

    /// <summary>
    /// What is left of programs already gone, with nothing to go on but its own evidence: a shortcut (never an internet one),
    /// a service, a scheduled task or an App Path whose program isn't there on a drive that is. The folder such a program had
    /// is found too, by that alone, so it is only ever likely. Never a blind sweep of the folders programs write to: a
    /// portable app's settings and a game's saves have no entry anywhere and look just like what's left of a program.
    /// </summary>
    internal List<Leftover> FindDead(IReadOnlyList<string> keep)
    {
        var theirs = Homes(keep);
        var kept = keep.Select(path => path.TrimEnd('\\') + '\\').ToList();
        var sources = _sources is { Deep: true } read ? read : _sources = ReadSources(deep: true);
        var list = new List<Leftover>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Dead(string target) { if (HomeOf(target) is { } home) folders.Add(home); }
        bool Gone(string? text, out string target)
        {
            target = CommandTarget(text) ?? "";
            return target.Length > 0 && !UnderWindows(target) && !AntiCheatIn(text) && !PointsInto(target, theirs) && Missing(target);
        }
        // A shortcut's own absolute target only, written as a command so its spaces stay: a relative one doesn't say where it
        // really leads (Riot's read as a folder under the Start menu), and a missing help file isn't a program gone.
        foreach (var (path, _, machine) in sources.Shortcuts)
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ShortcutTarget(path, relativeToo: false) is { } absolute && Gone($"\"{absolute}\"", out var exe))
                { list.Add(new(path, false, null, machine)); Dead(exe); }
        foreach (var (key, text, machine, kind) in sources.Keys)
        {
            if (kind is KeySource.AppId or KeySource.Driver || kind == KeySource.Path && !AppPaths.Any(root => key.StartsWith(root.Path + '\\', StringComparison.OrdinalIgnoreCase))) continue;
            if (Gone(text, out var exe)) { list.Add(new(key, true, null, machine, false, kind == KeySource.Service ? LeftoverKind.Service : null)); Dead(exe); }
        }
        foreach (var (path, exe) in sources.Tasks)
            if (Gone(exe, out var target)) { list.Add(new(path, false, null, true, false, LeftoverKind.Task)); Dead(target); }
        foreach (var folder in folders)
            if (Path.GetFileName(folder) is { Length: > 4 } name && !Generic.Contains(name)) list.Add(new(folder, false, null, MachinePlace(folder), Likely: true));
        return Weighed(Outermost(list.Where(item => Allowed(item, kept) && Exists(item)).ToList()), theirs);
    }

    /// <summary>
    /// A command line's program as a full path (quotes, arguments and variables dealt with); null when it doesn't name an exe.
    /// Anything else unquoted can't be told from its arguments ("C:\\Program Files\\Acme\\a.chm" would read as "C:\\Program").
    /// </summary>
    internal static string? CommandTarget(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = Environment.ExpandEnvironmentVariables(command).Trim();
        if (text.StartsWith(@"\??\", StringComparison.Ordinal)) text = text[4..];
        var (file, _) = Adapters.OfficialInstalledLibraries.SplitCommand(text);
        return file.Length > 3 && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(file) && file[1] == ':' ? Full(file) : null;
    }

    /// <summary>
    /// Whether a program is really gone: on a drive that is here, and with Windows able to say so. A drive that isn't plugged
    /// in is not gone, nor is a file Windows won't let this user look for: File.Exists says no to either, so the nearest
    /// folder that is there has to be read, and one that can't be read says nothing.
    /// </summary>
    internal bool Missing(string path)
    {
        if (!(DriveThere?.Invoke(path) ?? DriveIsHere(path))) return false;
        try
        {
            if (File.Exists(path) || Directory.Exists(path)) return false;
            var parent = Path.GetDirectoryName(path);
            while (parent is not null && !Directory.Exists(parent)) parent = Path.GetDirectoryName(parent);
            if (parent is null) return false;
            using var listing = new DirectoryInfo(parent).EnumerateFileSystemInfos().GetEnumerator();
            listing.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    /// <summary>A program's folder on a drive that's here, gone or holding no file at all: nothing suggests it only moved or is out of reach.</summary>
    internal bool GoneOrEmpty(string folder)
    {
        if (!(DriveThere?.Invoke(folder) ?? DriveIsHere(folder))) return false;
        if (!Directory.Exists(folder)) return Missing(folder);
        try { return !Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    /// <summary>Tests: whether a path's drive is here; null asks Windows (a fixed drive that's ready).</summary>
    internal Func<string, bool>? DriveThere { get; init; }

    private static bool DriveIsHere(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            return drive.DriveType == DriveType.Fixed && drive.IsReady;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The program's own folder a path lay in: the one right under the place programs go. Null anywhere else.</summary>
    private string? HomeOf(string path)
    {
        foreach (var root in new[] { "{ProgramFiles}", "{ProgramFilesX86}", @"{LocalAppData}\Programs" })
        {
            var place = Expand(root);
            if (place.StartsWith('{') || !path.StartsWith(place + '\\', StringComparison.OrdinalIgnoreCase)) continue;
            var rest = path[(place.Length + 1)..];
            var step = rest.IndexOf('\\');
            return step > 0 ? Path.Combine(place, rest[..step]) : null;
        }
        return null;
    }

    /// <summary>
    /// The app's own folders, each with a trailing backslash, as Windows reads them (".." and doubled slashes resolved): deep
    /// enough, never a place itself or Windows'.
    /// </summary>
    internal List<string> Homes(IEnumerable<string> paths) => paths
        .Select(Expand)
        .Where(Path.IsPathFullyQualified)
        .Select(path => Full(path)?.TrimEnd('\\') ?? "")
        .Where(path => path.Count(c => c == '\\') >= 2 && !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && !UnderWindows(path) &&
            !_places.Values.Any(place => string.Equals(place.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase)))
        .Select(path => path + '\\')
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? Full(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    /// <summary>One folder or key name, never a path: "Microsoft\Windows" or ".." would name another's (a program's name is anyone's to set).</summary>
    private static bool OneName(string name) =>
        name.Length > 0 && name.Trim() == name && !name.EndsWith('.') && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>
    /// What may go at all: never Windows' folders, a place, a folder holding something still installed or Exo's own, a
    /// shallow key or one of the keys every program shares.
    /// </summary>
    private bool Allowed(Leftover item, List<string> keep)
    {
        if (AntiCheatIn(item.Path) || AntiCheatIn(item.Value)) return false;
        if (item.Kind == LeftoverKind.Task) return item.Path.StartsWith('\\') && !item.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);
        if (item.IsKey)
        {
            var parts = item.Path.Split('\\');
            if (parts.Length < 3 || item.Value is null && parts.Length == 3 && Generic.Contains(parts[2])) return false;
            if (item.Value is null && Protected.Any(key => item.Path.Equals(key, StringComparison.OrdinalIgnoreCase))) return false;
            return true;
        }
        var path = item.Path.TrimEnd('\\');
        // Written as Windows reads it: a "..", a trailing dot or a doubled slash would lead somewhere the words don't say.
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Full(path), path, StringComparison.OrdinalIgnoreCase) ||
            path.Count(c => c == '\\') < 2 || UnderWindows(path)) return false;
        if (_places.Values.Any(place => string.Equals(place.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase))) return false;
        var mine = path + '\\';
        if (keep.Any(other => other.StartsWith(mine, StringComparison.OrdinalIgnoreCase) || mine.StartsWith(other, StringComparison.OrdinalIgnoreCase))) return false;
        if (!_isTest && (PathHelper.AppDataDir.TrimEnd('\\') + '\\').StartsWith(mine, StringComparison.OrdinalIgnoreCase)) return false;
        // A recomp's data folder holds its saves and the user's own copy of the game: never anything's leftover.
        if (!_isTest && Adapters.RecompInstallations.DataFolders().Select(folder => folder.TrimEnd('\\') + '\\')
                .Any(data => data.StartsWith(mine, StringComparison.OrdinalIgnoreCase) || mine.StartsWith(data, StringComparison.OrdinalIgnoreCase))) return false;
        try { if (Directory.Exists(path) && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.System)) return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        return true;
    }

    // Anti-cheat by what it's called anywhere in a path, a key or a command: never found, never listed, never touched.
    private static readonly string[] AntiCheatWords =
    [
        "vanguard", "easyanticheat", "easy anti-cheat", "eaanticheat", "battleye", "faceit", "esea", "punkbuster", "xigncode", "gameguard", "nprotect",
        "anticheatexpert", "anti-cheat", "anticheat", "javelin", "ricochet", "mhyprot", "wellbia", "bedaisy", "npggsvc", "xhunter", "pnkbstr", "ace-base", "ace-guard", "aceservice",
    ];

    internal static bool AntiCheatIn(string? text) => !string.IsNullOrEmpty(text) && (
        AntiCheatWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)) ||
        text.Split(['\\', '/', '"', ' ', '|']).Any(part => part.Length > 0 && ProcessTermination.IsAntiCheat(Path.GetFileNameWithoutExtension(part))));

    // Keys that hold every program's: never deleted whole, whatever points at them.
    private static readonly string[] Protected =
    [
        @"HKCU\Software", @"HKLM\SOFTWARE", @"HKCU\Software\Classes", @"HKLM\SOFTWARE\Classes", @"HKLM\SOFTWARE\WOW6432Node", @"HKCU\Software\Microsoft",
        @"HKLM\SOFTWARE\Microsoft", @"HKCU\Software\Classes\CLSID", @"HKLM\SOFTWARE\Classes\CLSID", @"HKLM\SYSTEM\CurrentControlSet\Services",
        @"HKCU\Software\Classes\*", @"HKLM\SOFTWARE\Classes\*", @"HKCU\Software\Classes\Directory", @"HKLM\SOFTWARE\Classes\Directory",
        @"HKCU\Software\Classes\Folder", @"HKLM\SOFTWARE\Classes\Folder", @"HKCU\Software\Classes\exefile", @"HKLM\SOFTWARE\Classes\exefile",
    ];

    private static bool UnderWindows(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length > 0 && (path.TrimEnd('\\') + '\\').StartsWith(windows.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether only an administrator may change it: a machine place, by its name or its path, or HKLM.</summary>
    private bool MachinePlace(string path) =>
        MachineRoots.Any(place => path.StartsWith(place, StringComparison.Ordinal)) ||
        MachineRoots.Where(place => place.StartsWith('{')).Select(Expand).Any(root => !root.StartsWith('{') &&
            (path.TrimEnd('\\') + '\\').StartsWith(root.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase));

    /// <summary>What Windows keeps is read again on the next find (after anything was deleted).</summary>
    internal void Forget() => _sources = null;

    /// <summary>Whether a path, a command line or a value's name points into one of the folders (variables expanded, either slash).</summary>
    internal static bool PointsInto(string? text, List<string> homes)
    {
        if (string.IsNullOrEmpty(text) || homes.Count == 0) return false;
        var plain = Environment.ExpandEnvironmentVariables(text).Replace('/', '\\');
        return homes.Any(home => plain.Contains(home, StringComparison.OrdinalIgnoreCase) ||
            plain.EndsWith(home.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
            plain.Contains(home.TrimEnd('\\') + '"', StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The app's Apps &amp; features entries (by display name) whose uninstaller is gone.</summary>
    private IEnumerable<Leftover> DeadEntries(Trail trail)
    {
        if (trail.DisplayNames.Count == 0) yield break;
        foreach (var (hive, view) in new[] { ("HKCU", Uninstall), ("HKLM", Uninstall), ("HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall") })
        {
            var entries = new List<string>();
            try
            {
                using var key = Open(hive + '\\' + view, writable: false);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var entry = key.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string display || !trail.DisplayNames.Any(match => ReplacedApps.NameMatches(display, match))) continue;
                    var command = (entry.GetValue("QuietUninstallString") ?? entry.GetValue("UninstallString")) as string;
                    if (string.IsNullOrWhiteSpace(command) || !File.Exists(Adapters.OfficialInstalledLibraries.SplitCommand(command).FileName))
                        entries.Add(name);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { continue; }
            foreach (var name in entries) yield return new(hive + '\\' + view + '\\' + name, true, null, hive == "HKLM");
        }
    }

    /// <summary>A Store package's records Windows keeps per user, and its data folder.</summary>
    private IEnumerable<(string Path, bool IsKey)> StoreRecords(string family)
    {
        const string AppModel = @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel";
        yield return ($@"{AppModel}\SystemAppData\{family}", true);
        yield return ($@"{AppModel}\Repository\Families\{family}", true);
        yield return (Expand(@"{LocalAppData}\Packages\" + family), false);
    }

    /// <summary>An MSI product code as Windows Installer keys it: each group's hex reversed (the last two by byte pairs).</summary>
    internal static string? Packed(string productCode)
    {
        if (!Guid.TryParse(productCode, out var guid)) return null;
        var text = guid.ToString("N").ToUpperInvariant();
        var builder = new StringBuilder(32);
        builder.Append(Reverse(text[..8])).Append(Reverse(text[8..12])).Append(Reverse(text[12..16]));
        for (var i = 16; i < 32; i += 2) builder.Append(text[i + 1]).Append(text[i]);
        return builder.ToString();

        static string Reverse(string part) => new([.. part.Reverse()]);
    }

    /// <summary>Each leftover once, and none inside another that goes (a shortcut in a folder, a value or key in a key).</summary>
    internal static List<Leftover> Outermost(List<Leftover> list)
    {
        var unique = list.DistinctBy(item => (item.IsKey, item.Kind == LeftoverKind.Task, item.Path.ToUpperInvariant(), item.Value?.ToUpperInvariant()))
            // A sure find beats the same thing found by name.
            .GroupBy(item => (item.IsKey, item.Path.ToUpperInvariant(), item.Value?.ToUpperInvariant())).Select(group => group.OrderBy(item => item.Likely).First()).ToList();
        var folders = unique.Where(item => item.Kind == LeftoverKind.Folder).Select(item => item.Path.TrimEnd('\\') + '\\').ToList();
        var keys = unique.Where(item => item.IsKey && item.Value is null).Select(item => item.Path + '\\').ToList();
        return unique.Where(item => !Inside(item)).ToList();

        bool Inside(Leftover item) => item.Kind == LeftoverKind.Task ? false : item.IsKey
            ? keys.Any(key => (item.Path + '\\').StartsWith(key, StringComparison.OrdinalIgnoreCase) && (item.Value is not null || (item.Path + '\\').Length > key.Length))
            : folders.Any(folder => item.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
    }

    internal static Dictionary<Leftover, long> Sizes(IEnumerable<Leftover> items) =>
        items.ToDictionary(item => item, item => item.Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut ? Size(item.Path) : 0);

    internal static LeftoverSummary Summarize(IReadOnlyCollection<Leftover> items, IReadOnlyDictionary<Leftover, long> sizes) => new(
        items.Count(item => item.Kind == LeftoverKind.Folder), items.Count(item => item.Kind == LeftoverKind.File),
        items.Count(item => item.Kind is LeftoverKind.Registry or LeftoverKind.Service or LeftoverKind.Task or LeftoverKind.Driver), items.Count(item => item.Kind == LeftoverKind.Shortcut),
        items.Sum(item => sizes.GetValueOrDefault(item)));

    internal static long Size(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Sum(file => file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    // ---- what is there, and taking it away ----

    internal bool Exists(Leftover item)
    {
        if (item.Kind == LeftoverKind.Task) return _isTest ? _testTasks.Contains(item.Path) : TaskExists(item.Path);
        if (!item.IsKey) return Directory.Exists(item.Path) || File.Exists(item.Path);
        try
        {
            using var key = Open(item.Path, writable: false);
            return key is not null && (item.Value is null || key.GetValueNames().Contains(item.Value, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Why one may not go now, or null when it may: checked again right before it goes, as this user, so what goes (an
    /// administrator's rd included) is what was found. Refused: a place Exo never deletes; a link (a junction, symbolic link
    /// or mount point) at it or on the way down from its place, which whoever may write there could point into Windows; a
    /// path Windows opens somewhere other than where its words say (every link followed), or one that leads where Exo never
    /// deletes. Another kind of reparse point isn't a link (OneDrive keeps each synced file and folder as one) and still
    /// has to open where it says.
    /// </summary>
    internal string? Recheck(Leftover item)
    {
        if (!Allowed(item, [])) return Barred;
        if (item.IsKey || item.Kind == LeftoverKind.Task) return null;
        var path = item.Path.TrimEnd('\\');
        if (!Directory.Exists(path) && !File.Exists(path)) return null;
        if (DeletionGate.Check(path) is { } kept) return kept;
        var root = Root(path);
        for (var step = path; step.Length > root.Length + 1; step = Path.GetDirectoryName(step) ?? root)
            if (IsLink(step)) return Linked;
        if (FinalPath.Of(path) is not { } final || FinalPath.Of(root + '\\') is not { } top ||
            !final.Equals(top.TrimEnd('\\') + path[root.Length..], StringComparison.OrdinalIgnoreCase)) return Moved;
        return Allowed(item with { Path = final }, []) ? null : Barred;
    }

    // The deepest place it lies in, else its drive's root; without a trailing backslash. What is above it is Windows' or
    // the user's own, never the app's.
    private string Root(string path) =>
        _places.Values.Select(place => place.TrimEnd('\\')).Where(place => place.Length > 0 && path.StartsWith(place + '\\', StringComparison.OrdinalIgnoreCase))
            .MaxBy(place => place.Length) ?? (Path.GetPathRoot(path) ?? "").TrimEnd('\\');

    // A junction, symbolic link or mount point (.NET reads a target only from those); unreadable counts as one.
    private static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private bool Stay(Leftover item, string why)
    {
        Stayed[item] = why;
        AppLog.Debug($"Leftovers: one stayed: {why}.");
        return false;
    }

    /// <summary>Why these, still there, stayed (<see cref="InUse"/> for those Exo didn't refuse); null when Exo refused none of them.</summary>
    internal string? Why(IReadOnlyCollection<Leftover> left) =>
        left.Any(Stayed.ContainsKey) ? string.Join("; ", left.Select(item => Stayed.GetValueOrDefault(item) ?? InUse).Distinct()) : null;

    /// <summary>
    /// Deletes one this user may, once <see cref="Recheck"/> passes: a file or folder only to the Recycle Bin, as the
    /// confirmation says (one the bin doesn't take stays, unless it's gone anyway), a key or value after <see cref="Backup"/>
    /// wrote it down. One Exo refused is in <see cref="Stayed"/>.
    /// </summary>
    internal bool Delete(Leftover item)
    {
        if (HostMode.IsDisposable) return false;
        try
        {
            if (item.Kind == LeftoverKind.Task) return false;
            if (Recheck(item) is { } refused) return Stay(item, refused);
            if (!item.IsKey)
            {
                if (!Directory.Exists(item.Path) && !File.Exists(item.Path)) return true;
                var why = Recycler(item.Path);
                return !Directory.Exists(item.Path) && !File.Exists(item.Path) || Stay(item, why ?? NotRecycled);
            }
            var (hive, sub) = Hive(item.Path);
            if (item.Value is null)
            {
                hive.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
                return true;
            }
            using var key = hive.OpenSubKey(sub, writable: true);
            key?.DeleteValue(item.Value, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Debug($"Leftovers: one stayed ({ex.GetType().Name}).");
            return false;
        }
    }

    /// <summary>
    /// One cmd step for a leftover only an administrator may remove, or null for one that stays (in <see cref="Stayed"/>):
    /// one <see cref="Recheck"/> refuses now, so what the administrator deletes is what was found, or one cmd can't name
    /// safely. Every name must stay inside double quotes, where &amp;, | and &lt; &gt; are only text; what quotes don't
    /// hold is refused: a quote, % (expanded even inside them), ! (delayed expansion), ^ and every control character (a
    /// line break ends the command). A folder goes with rd /s, which removes a link inside it (a junction, a symbolic link)
    /// without going into it; a file with del only while it is still a file (del given a folder empties it). What it names
    /// came from the app's own trail, never from the page.
    /// </summary>
    internal string? AdminCommand(Leftover item)
    {
        var why = $"{item.Path}{item.Value}".Any(c => char.IsControl(c) || c is '"' or '%' or '!' or '^') ? Unquotable : Recheck(item);
        if (why is not null)
        {
            Stay(item, why);
            return null;
        }
        if (item.Kind == LeftoverKind.Task) return $"schtasks /delete /tn \"{item.Path}\" /f";
        if (item.Kind == LeftoverKind.Driver && item.Infs is { Count: > 0 } infs) return DriverStoreCommand(item, infs);
        if (item.Kind is LeftoverKind.Service or LeftoverKind.Driver) return $"sc stop \"{item.Path.Split('\\')[^1]}\" & sc delete \"{item.Path.Split('\\')[^1]}\"";
        if (!item.IsKey)
            return Directory.Exists(item.Path) ? $"rd /s /q \"{item.Path}\""
                : File.Exists(item.Path) ? $"(if not exist \"{item.Path}\\*\" del /f /q \"{item.Path}\")" : null;
        var key = _isTest && item.Path.StartsWith("HKLM", StringComparison.Ordinal) ? @"HKCU\" + Join(_hklmBase, item.Path[5..]) : item.Path;
        return item.Value is null ? $"reg delete \"{key}\" /f" : item.Value.Length == 0 ? $"reg delete \"{key}\" /ve /f" : $"reg delete \"{key}\" /v \"{item.Value}\" /f";
    }

    /// <summary>
    /// A driver with its driver store package: the package is exported first into a new folder only administrators may write
    /// (made, owned and locked in the same step, so nothing this user runs can swap what Put back adds again), then pnputil
    /// /delete-driver takes it, never /force nor /uninstall, so Windows refuses one a device still uses; only once it went
    /// does the service go. Each part runs only after the one before it worked (&amp;&amp;). Null (it stays) when a name isn't
    /// an oemNN.inf or can't sit in quotes.
    /// </summary>
    internal string? DriverStoreCommand(Leftover item, IReadOnlyList<string> infs)
    {
        var service = item.Path.Split('\\')[^1];
        var root = DriverBackupRoot;
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        if (infs.Any(inf => !OemInf().IsMatch(inf)) || $"{service}{folder}".Any(c => char.IsControl(c) || c is '"' or '%' or '!' or '^' or '&' or '|' or '<' or '>' or '(' or ')'))
        {
            Stay(item, Unquotable);
            return null;
        }
        var pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        const string Locked = "/inheritance:r /grant:r \"*S-1-5-32-544:(OI)(CI)F\" \"*S-1-5-18:(OI)(CI)F\" \"*S-1-5-32-545:(OI)(CI)RX\"";
        // takeown /a makes the Administrators group the owner (a folder this user made first loses its say), then the locked ACL.
        var parts = new List<string>
        {
            $"takeown /f \"{root}\" /a >nul", $"icacls \"{root}\" {Locked} >nul",
            $"mkdir \"{folder}\"", $"takeown /f \"{folder}\" /a >nul", $"icacls \"{folder}\" {Locked} >nul",
        };
        foreach (var inf in infs) parts.AddRange([$"\"{pnputil}\" /export-driver {inf} \"{folder}\" >nul", $"\"{pnputil}\" /delete-driver {inf} >nul"]);
        parts.Add($"(sc stop \"{service}\" >nul & sc delete \"{service}\")");
        DriverBackups[item] = (folder, infs);
        return $"(if not exist \"{root}\" mkdir \"{root}\") & " + string.Join(" && ", parts);
    }

    /// <summary>
    /// The elevated steps that send what only an administrator may remove to the Recycle Bin, not away for good, so Put back
    /// can bring it back: the same checks as <see cref="AdminCommand"/>, and whatever the bin wouldn't keep stays. No word of
    /// it passes through cmd: the paths travel encoded, in a script of literals. Several paths share a step, each step under
    /// cmd's line limit once encoded.
    /// </summary>
    internal List<string> RecycleAsAdmin(IEnumerable<Leftover> items)
    {
        var paths = new List<string>();
        foreach (var item in items)
        {
            var why = item.Path.Any(char.IsControl) ? Unquotable : Recheck(item) ?? (_isTest ? null : RecycleBinRefuses(item.Path));
            if (why is not null) { Stay(item, why); continue; }
            if (!Directory.Exists(item.Path) && !File.Exists(item.Path)) continue;
            paths.Add(item.Path);
        }
        return RecycleSteps(paths);
    }

    /// <summary>
    /// The elevated steps that send these, already checked by the caller, to the Recycle Bin: the paths travel encoded, as
    /// literals, several to a step, each step under cmd's line limit once encoded. A path with a control character is left out.
    /// </summary>
    internal static List<string> RecycleSteps(IEnumerable<string> paths)
    {
        var scripts = new List<List<string>> { new() };
        foreach (var path in paths.Where(path => !path.Any(char.IsControl)))
        {
            if (scripts[^1].Sum(literal => literal.Length + 3) + path.Length > 2000) scripts.Add([]);
            scripts[^1].Add("'" + path.Replace("'", "''") + "'");
        }
        return scripts.Where(literals => literals.Count > 0).Select(literals => $"{PowerShell} -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(RecycleScript.Replace("{0}", string.Join(",", literals), StringComparison.Ordinal)))).ToList();
    }

    private const string RecycleScript =
        "Add-Type -AssemblyName Microsoft.VisualBasic;foreach($p in @({0})){try{" +
        "if([IO.Directory]::Exists($p)){[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($p,'OnlyErrorDialogs','SendToRecycleBin','DoNothing')}" +
        "elseif([IO.File]::Exists($p)){[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($p,'OnlyErrorDialogs','SendToRecycleBin','DoNothing')}}catch{}}";

    /// <summary>
    /// Writes the registry leftovers down before they go (reg export of each key, a value's with its key) in Exo's
    /// cleanup folder, one .reg per hive, since only an administrator may bring back HKLM's. Put back imports them, and the
    /// user can double-click one. The newest 100 are kept. None in tests or with none.
    /// </summary>
    internal List<string> Backup(string app, IEnumerable<Leftover> items)
    {
        if (HostMode.IsDisposable) return [];
        var keys = items.Where(item => item.IsKey && item.Kind != LeftoverKind.Task).Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var files = new List<string>();
        if (_isTest || keys.Count == 0) return files;
        foreach (var hive in keys.GroupBy(key => key[..4]))
            if (BackupHive(app, [.. hive]) is { } file) files.Add(file);
        return files;
    }

    private string? BackupHive(string app, List<string> keys)
    {
        try
        {
            var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
            foreach (var key in keys)
            {
                var part = Path.Combine(Path.GetTempPath(), $"exo-{Guid.NewGuid():N}.reg");
                using (var export = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("reg.exe", $"export \"{key}\" \"{part}\" /y")
                       { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }))
                    export?.WaitForExit(15_000);
                if (!File.Exists(part)) continue;
                var lines = File.ReadAllText(part).Split("\r\n", 2);
                text.Append(lines.Length > 1 ? lines[1] : "");
                DeletionGate.DeleteFile(part);
            }
            return SaveReg($"{app} {keys[0][..4]}", text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Debug("Leftovers: backup not written: " + ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Where a scheduled task's definition is written down before it goes: Exo's cleanup folder; tests set their own, else none.</summary>
    internal string? TaskBackupFolder { get; init; }

    /// <summary>The program a found scheduled task runs, as Task Scheduler said when it was read; null when it wasn't read this scan.</summary>
    internal string? TaskExe(string path) => _sources?.Tasks.FirstOrDefault(task => string.Equals(task.Path, path, StringComparison.OrdinalIgnoreCase)).Exe;

    /// <summary>A new file for one task's XML, named for the app, the time and the task; null in a disposable host, or in a test that set no folder.</summary>
    internal string? TaskBackupFile(string app, string path)
    {
        if (HostMode.IsDisposable) return null;
        var folder = TaskBackupFolder ?? (_isTest ? null : Path.Combine(PathHelper.AppDataDir, "cleanup"));
        if (folder is null) return null;
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        static string Safe(string text) => new string(text.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' ? c : '_').ToArray()).Trim();
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss} {Safe(app)} {Safe(path.TrimStart('\\'))}";
        return Path.Combine(folder, (name.Length > 120 ? name[..120] : name) + $" {Guid.NewGuid():N}"[..9] + ".xml");
    }

    /// <summary>A .reg in Exo's cleanup folder, named for the app and the time; the newest 100 are kept. Null in tests or when it can't be written.</summary>
    internal string? SaveReg(string app, string text)
    {
        if (HostMode.IsDisposable || _isTest) return null;
        try
        {
            var folder = Path.Combine(PathHelper.AppDataDir, "cleanup");
            Directory.CreateDirectory(folder);
            var safe = new string(app.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' ? c : '_').ToArray()).Trim();
            var file = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss} {safe}.reg");
            File.WriteAllText(file, text, Encoding.Unicode);
            foreach (var old in new DirectoryInfo(folder).GetFiles("*.reg").OrderByDescending(f => f.CreationTimeUtc).Skip(100)) DeletionGate.DeleteFile(old.FullName);
            foreach (var old in new DirectoryInfo(folder).GetFiles("*.xml").OrderByDescending(f => f.CreationTimeUtc).Skip(200)) DeletionGate.DeleteFile(old.FullName);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Leftovers: backup not written: " + ex.GetType().Name);
            return null;
        }
    }

    private static readonly (string Place, string Said)[] PlaceWords =
    [
        ("LocalAppData", @"AppData\Local"), ("LocalLow", @"AppData\LocalLow"), ("AppData", @"AppData\Roaming"), ("Desktop", "Desktop"), ("CommonDesktop", "Desktop"),
        ("CommonPrograms", "Start menu"), ("ProgramFiles", "Program Files"), ("ProgramFilesX86", "Program Files (x86)"), ("ProgramData", "ProgramData"), ("Temp", "Temp"),
    ];

    /// <summary>A leftover as the page shows it, bounded: where it is by the place's name, so a user's folder never leaves native code; a value after its key.</summary>
    internal string Said(Leftover item)
    {
        var text = item.Value is null ? item.Path : $"{item.Path} › {item.Value}";
        var places = PlaceWords.Where(word => _places.ContainsKey(word.Place)).Select(word => (Root: _places[word.Place].TrimEnd('\\'), word.Said))
            .Append((Root: Expand(Programs), Said: "Start menu")).OrderByDescending(place => place.Root.Length);
        foreach (var (root, said) in places)
            if (root.Length > 0 && text.StartsWith(root + '\\', StringComparison.OrdinalIgnoreCase)) { text = said + text[root.Length..]; break; }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0 && text.StartsWith(profile + '\\', StringComparison.OrdinalIgnoreCase)) text = "Your folder" + text[profile.Length..];
        return text.Length <= 200 ? text : "…" + text[^199..];
    }

    internal string Expand(string path)
    {
        var end = path.IndexOf('}');
        return path.StartsWith('{') && end > 0 && _places.TryGetValue(path[1..end], out var root) ? root + path[(end + 1)..] : path;
    }

    internal RegistryKey? Open(string path, bool writable)
    {
        var (hive, sub) = Hive(path);
        return hive.OpenSubKey(sub, writable);
    }

    internal (RegistryKey Hive, string Sub) Hive(string path) => path[..4] switch
    {
        "HKCU" => (Registry.CurrentUser, Join(_hkcuBase, path[5..])),
        "HKLM" when _isTest => (Registry.CurrentUser, Join(_hklmBase, path[5..])),
        "HKLM" => (Registry.LocalMachine, path[5..]),
        _ => throw new InvalidOperationException("Unexpected hive."),
    };

    private static string Join(string prefix, string path) => prefix.Length == 0 ? path : prefix + '\\' + path;

    private static IEnumerable<string> Matching(string folder, string pattern, bool deep = false)
    {
        if (!Directory.Exists(folder)) return [];
        try
        {
            return Directory.EnumerateFiles(folder, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = deep, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> Folders(string folder, string pattern)
    {
        if (!Directory.Exists(folder)) return [];
        try { return Directory.EnumerateDirectories(folder, pattern, new EnumerationOptions { IgnoreInaccessible = true }).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // ---- the Recycle Bin ----

    /// <summary>
    /// To the Recycle Bin, quietly: null when it went, else why it stayed. Told not to ask, Windows deletes for good what
    /// its bin can't take, so that is looked at first (<see cref="RecycleBinRefuses(string)"/>) and stays.
    /// </summary>
    private static string? Recycle(string path)
    {
        if (RecycleBinRefuses(path) is { } why) return why;
        return DeletionNative.SendToRecycleBin(path, clean: true) ? null : NotRecycled;
    }

    // Tests: for good (a junction goes on its own; Directory.Delete never follows one).
    private static string? ForGood(string path)
    {
        return DeletionGate.Clean(path, null, out var why) ? null : why;
    }

    /// <summary>
    /// Why Windows would delete it for good rather than keep it: its drive's Recycle Bin as Windows keeps it (Recycle Bin ›
    /// Properties, per drive). A drive Windows never used the bin on has no size saved yet: 1% of the drive, a cautious guess.
    /// </summary>
    internal static string? RecycleBinRefuses(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            using var bin = Volume() is { } id ? Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{id}") : null;
            var capacity = bin?.GetValue("MaxCapacity") is int megabytes ? megabytes * (1L << 20) : drive.TotalSize / 100;
            return RecycleBinRefuses(Size(path), drive.DriveType, bin?.GetValue("NukeOnDelete") is 1, capacity);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException) { return NoBin; }

        // The volume's id, as the bin's settings are keyed: {GUID} of \\?\Volume{GUID}\.
        string? Volume()
        {
            var mount = new char[1024];
            var name = new char[64];
            if (!GetVolumePathNameW(path, mount, (uint)mount.Length) || !GetVolumeNameForVolumeMountPointW(new string(mount).TrimEnd('\0'), name, (uint)name.Length)) return null;
            var text = new string(name).TrimEnd('\0');
            var open = text.IndexOf('{');
            var close = text.IndexOf('}');
            return open >= 0 && close > open ? text[open..(close + 1)] : null;
        }
    }

    /// <summary>Only a fixed drive keeps a Recycle Bin, it can be set to remove files at once, and nothing bigger than it goes in. Null when it takes it.</summary>
    internal static string? RecycleBinRefuses(long bytes, DriveType drive, bool removesAtOnce, long capacity) =>
        drive != DriveType.Fixed ? NoBin : removesAtOnce ? BinOff : bytes > capacity ? TooBig : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, [Out] char[] volumePath, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, [Out] char[] volumeName, uint length);


    // ---- tests ----

    private readonly HashSet<string> _testTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Path, string Exe)> _testTaskList = [];

    /// <summary>Tests: a scheduled task as if Windows' Task Scheduler had it.</summary>
    internal void AddTestTask(string path, string exe)
    {
        _testTasks.Add(path);
        _testTaskList.Add((path, exe));
    }
}
