using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Best settings for a game: Exo's values for this PC written into the game's own settings file when Optimize is pressed
/// on the game's page (each group can also be put back on its own). Only the lines Exo knows change, each to a value the line itself lists as allowed
/// (an update that drops a choice drops that change); everything else stays as the game wrote it. Exo keeps what each
/// line held before (game-settings.json in its data folder, and the whole file as it first found it), so turning a
/// group off puts those back, unless the line was changed in the game since, which then stays. Nothing is written
/// while the game runs: it writes the file itself as it closes. Once pressed it stays on: a game update that resets the
/// file is followed by Exo writing it again (GameSettings.Keep.cs). Modern Warfare 4 (GameSettings.CallOfDuty.cs),
/// Rocket League (GameSettings.RocketLeague.cs, whose Unreal Engine 3 ini GameSettings.Ini.cs reads) and Phantom Blade
/// Zero (GameSettings.PhantomBlade.cs, an Unreal Engine 5 GameUserSettings.ini, read the same way), Hell is Us
/// (GameSettings.HellIsUs.cs, the same again, where the Video menu really is that file), CONTROL Resonant
/// (GameSettings.Control.cs, the renderer's JSON), the games in the table (GameSettings.Catalog.cs), and the HDR peak of
/// any other Unreal game with HDR (GameSettings.UnrealHdr.cs). Where a game has one, each profile follows BenchmarKing's
/// optimization guide (youtube.com/@benchmarking4386: every setting measured, on an RTX 3060 Ti 8 GB and a 4070 Super
/// 12 GB) and names the video; Exo goes further only where he doesn't cover it or where this PC calls for it.
/// </summary>
internal static partial class GameSettings
{
    /// <summary>
    /// A value for every line named <paramref name="Key"/> (Call of Duty lists some settings twice under different ids).
    /// Free: written as it is to a line that lists no allowed values (a refresh rate's "Auto:165.000"), never to one that does.
    /// Spare: a line some versions of the file don't hold (an HDR peak in a game without HDR), which is no less Applied for it.
    /// </summary>
    internal sealed record Change(string Key, string Value, bool Free = false, bool Spare = false);

    /// <summary>Settings that go together (and are put back together).</summary>
    internal sealed record Group(string Id, string Label, IReadOnlyList<Change> Changes);

    /// <summary>
    /// A game Exo has best settings for: its name, its settings file, its processes, and its groups for this PC and what
    /// the file itself says (the graphics card the game found, the resolution it outputs). Format reads its files (null:
    /// Call of Duty's, <see cref="Parse"/>). Program: the install's program, which an update rewrites. Mark: what a file
    /// says of the defaults the game built it from, for a game that rebuilds it when they change (Rocket League's
    /// [IniVersion]). WindowMode: the mode chosen on its Optimize line ("borderless", "fullscreen"), null for a game whose
    /// mode Exo can't set. WindowLines: the lines that hold it, which <see cref="Hold"/> keeps at that mode.
    /// Keep's facts: Facts, the game's own build facts now (Steam's build number, a launcher's content times; bound to the game
    /// by <see cref="For"/> from GameFacts); Helpers, the launcher and helper processes that must be gone with the game before
    /// anything is written; Inputs and InputLines, what the targets are worked out from (the PC's facts, and the file's own
    /// lines such as its resolution), recorded with every write. CloudChanged: whether the game's own cloud, not the player or
    /// Exo, wrote its files (the files, when Exo last looked, and whether a session here just ended). Install: the game's
    /// install folder, which a helper's executable must sit under to count as the game's (null: a helper is known by its name
    /// alone).
    /// </summary>
    internal sealed record Profile(string Id, string Title, string File, Func<SettingsFile, IReadOnlyList<Group>> Groups, IReadOnlyList<string> Processes,
        Func<IReadOnlyList<string>>? PlayerFiles = null, NvidiaGame? Nvidia = null, Func<byte[], SettingsFile>? Format = null,
        Func<GameEntry, string?>? Program = null, Func<SettingsFile, string?>? Mark = null, string? WindowMode = null, IReadOnlyList<string>? WindowLines = null,
        Func<IReadOnlyDictionary<string, string>>? Facts = null, IReadOnlyList<string>? Helpers = null,
        IReadOnlyDictionary<string, string>? Inputs = null, IReadOnlyList<string>? InputLines = null,
        Func<GameEntry, IReadOnlyDictionary<string, string>>? GameFacts = null, Func<IReadOnlyList<OpenFile>, DateTime?, bool, bool>? CloudChanged = null,
        string? Install = null);

    /// <summary>One of a profile's files as read, with the key its kept lines go under ("" for the main file).</summary>
    internal sealed record OpenFile(string Path, string Key, SettingsFile File);

    /// <summary>
    /// A group as it stands. On: every line it changes holds Exo's value. Exo: Exo changed at least one of
    /// them, so turning it off puts something back (a group already set that way in the game has nothing to put back).
    /// </summary>
    internal sealed record GroupState(string Id, string Label, bool On, bool Exo);

    /// <summary>
    /// What the values depend on: the graphics card's maker, the render threads the processor suits, the video memory,
    /// how many screens there are, the main screen's highest refresh rate, whether you listen on headphones (Windows'
    /// default output is headphones or a headset, or Exo EQ corrects a pair), the main screen's real HDR peak in nits
    /// (<see cref="GameHdr.PeakNits"/>; null when it can't show HDR), and whether this game was chosen to run in true full
    /// screen (<see cref="Chosen"/>, which <see cref="For"/> fills in).
    /// </summary>
    internal sealed record Pc(string? Vendor, int? RenderThreads, double? VideoMemoryGb, int Screens = 1, int TopRefresh = 0,
        bool Headphones = false, int? HdrPeak = null, string? Gpu = null, bool Fullscreen = false);

    /// <summary>One setting line: "Name@ids = value // allowed", where allowed is "one of a, b" or "low to high".</summary>
    internal sealed record Line(int Index, string Name, string Id, string Value, string? Allowed, int ValueStart, int ValueLength);

    /// <summary>
    /// A settings file as read: its lines (rejoined with its own line break), its BOM and its setting lines, and the
    /// encoding its bytes go back in (null: UTF-8).
    /// </summary>
    internal sealed record SettingsFile(string[] Lines, string NewLine, bool Bom, IReadOnlyList<Line> Settings, Encoding? Text = null);

    /// <summary>What a line held before Exo changed it, and what Exo set.</summary>
    internal sealed record Remembered(string Was, string Set);

    /// <summary>The profile for a game, if Exo has one: by what its install folder holds (a Riot game's folder is never looked in, and Riot games have none).</summary>
    internal static Profile? For(GameEntry game, Pc pc, bool smoothMotion = false) =>
        // Every writer is behind the one anti-cheat answer: a game's own settings file is allowed with anti-cheat found
        // (DIRECTION.md point 4), never for Riot or Vanguard, never when Exo could not read the game's install.
        AntiCheatCheck.Allows(game, AntiCheatCheck.Cached(game), AntiCheatCheck.Change.GameSettingsFile) && ProfileFor(game, pc, smoothMotion) is { } profile ? Described(profile, game, pc) : null;

    /// <summary>The profile with what Keep needs to know about this game and this PC.</summary>
    private static Profile Described(Profile profile, GameEntry game, Pc pc) => WithInputs(profile, pc) with
    {
        Install = string.IsNullOrEmpty(game.Path) ? null : game.Path,
        Facts = () =>
        {
            var facts = new Dictionary<string, string>(profile.GameFacts?.Invoke(game) ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            if (KeepContext.SteamBuildId(game) is { } build) facts["buildid"] = build;
            return facts;
        },
    };

    /// <summary>The profile with the PC's facts its targets were worked out from, which every write records. The unknown PC records none: they would be guesses.</summary>
    internal static Profile WithInputs(Profile profile, Pc pc) => profile with
    {
        Inputs = ReferenceEquals(pc, Unknown) ? null : new Dictionary<string, string>
        {
            ["screens"] = pc.Screens.ToString(CultureInfo.InvariantCulture), ["headphones"] = pc.Headphones ? "yes" : "no",
            ["fullscreen"] = pc.Fullscreen ? "yes" : "no", ["gpu"] = pc.Vendor ?? "", ["topRefresh"] = pc.TopRefresh.ToString(CultureInfo.InvariantCulture),
            ["hdrPeak"] = pc.HdrPeak?.ToString(CultureInfo.InvariantCulture) ?? "",
        },
    };

    /// <summary>The one sentence for Optimize's line when Exo has a profile for the game but its anti-cheat rule refuses it; else null.</summary>
    internal static string? AntiCheatNote(GameEntry game, Pc pc) =>
        ProfileFor(game, pc) is null ? null : AntiCheatCheck.Sentence(game, AntiCheatCheck.Cached(game), AntiCheatCheck.Change.GameSettingsFile);

    private static Profile? ProfileFor(GameEntry game, Pc pc, bool smoothMotion = false)
    {
        if (!game.Installed || string.IsNullOrEmpty(game.Path) || game.Store == StoreKind.Riot) return null;
        if (Directory.Exists(Path.Combine(game.Path, "cod26"))) return ModernWarfare4(Chosen(pc, "mw4"));
        if (IsRocketLeague(game.Path)) return RocketLeague(pc);
        if (IsControlResonant(game.Path)) return ControlResonant(Chosen(pc, "control-resonant"));
        if (IsHellIsUs(game.Path)) return HellIsUs(Chosen(pc, HellIsUsId), HellIsUsHasFrameGeneration(game.Path));
        if (IsPhantomBlade(game.Path)) return PhantomBladeZero(pc.HdrPeak, Chosen(pc, "pbz").Fullscreen);
        if (IsWutheringWaves(game)) return WutheringWaves(pc, game, smoothMotion);
        if (RecipeFor(game) is { } recipe)
        {
            var profile = ProfileOf(recipe, Chosen(pc, recipe.Id));
            return InSteamCloud(game, profile.File) ? null : profile;
        }
        return UnrealHdr(game, pc.HdrPeak);
    }

    /// <summary>
    /// The render threads a Call of Duty engine suits on this processor: its fast cores less one, which stays for the
    /// game's own main thread. Fast cores: a hybrid's performance cores (threads on E-cores stutter), and on a Ryzen with
    /// two CCDs one CCD's cores (the V-Cache one on an X3D: crossing CCDs costs more than it gives). At least 2, at most
    /// 16, the game's own range. Null when Windows doesn't say.
    /// </summary>
    internal static int? RenderThreads(MouseCore.Topology topology)
    {
        if (topology.Cores.Count == 0) return null;
        var fastest = topology.Cores.Max(core => core.Efficiency);
        var fast = topology.Cores.Where(core => core.Efficiency == fastest).ToList();
        if (topology.L3.Count > 1)
        {
            var ccd = topology.L3.OrderByDescending(cache => cache.Bytes).First().Mask;
            var inCcd = fast.Count(core => core.Threads.Length > 0 && core.Threads[0] < 64 && (ccd & (1UL << core.Threads[0])) != 0);
            if (inCcd > 0) return Math.Clamp(inCcd - 1, 2, 16);
        }
        return Math.Clamp(fast.Count - 1, 2, 16);
    }

    /// <summary>This PC's facts, read now.</summary>
    internal static Pc ThisPc()
    {
        var vendor = GraphicsService.Vendor();
        var screens = WindowsHdr.All();
        var top = WindowsHdr.Primary() is { } primary && DisplayOutput.RefreshRates(primary.GdiName).Rates is { Length: > 0 } rates ? rates[^1] : 0;
        return new(vendor, RenderThreads(MouseCore.Sampler.ReadTopology()), GpuCapability.VideoMemoryGb(), Math.Max(1, screens.Count), top,
            ListensOnHeadphones(), GameHdr.MainScreenPeak(), SystemSpecs.Gpu);
    }

    private static bool ListensOnHeadphones()
    {
        try
        {
            if (Peripherals.DefaultEndpoints().Any(endpoint => !endpoint.Capture && endpoint.FormFactor is 3 or 5)) return true;
            return ExoEq.Current.Outputs.Values.Any(output => output.Headphone is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    internal static bool Running(Profile profile)
    {
        foreach (var name in profile.Processes)
        {
            var found = Process.GetProcessesByName(name);
            foreach (var process in found) process.Dispose();
            if (found.Length > 0) return true;
        }
        return false;
    }

    /// <summary>
    /// The executable paths of the processes with this name, null for one whose path cannot be read. The one place Exo asks
    /// Windows about a process by name for a helper: tests swap it for their own in the flow of one test.
    /// </summary>
    internal static Func<string, IReadOnlyList<string?>> HelperPaths { get; set; } = RealHelperPaths;

    private static IReadOnlyList<string?> RealHelperPaths(string name)
    {
        var found = Process.GetProcessesByName(name);
        try
        {
            return [.. found.Select(process =>
            {
                try { return process.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
            })];
        }
        finally { foreach (var process in found) process.Dispose(); }
    }

    /// <summary>
    /// Whether a process of this helper's name runs. A helper counts only when its executable sits under the game's install
    /// folder: a name alone is another program's too (<c>bootstrapper</c>), which would stop every look for good. A path that
    /// cannot be read counts as running, and a profile with no install folder (a test's) falls back to the name alone.
    /// </summary>
    private static bool HelperRuns(Profile profile, string name)
    {
        var paths = HelperPaths(name);
        if (paths.Count == 0) return false;
        if (profile.Install is not { Length: > 0 } install) return true;
        return paths.Any(path => path is null || Inside(path, install));
    }

    /// <summary>
    /// Whether this executable sits under the game's install folder. Compared by path first; a path that says outside is then
    /// settled by the path Windows opens it at, so a folder reached through a junction or a link is still the same folder. Where
    /// that cannot be settled (a short 8.3 name, a drive standing in for a folder, a folder on another drive, the install folder
    /// reached through a link), the helper counts as running: a write while it does is the failure this is for.
    /// </summary>
    private static bool Inside(string path, string install)
    {
        string full, folder;
        try
        {
            full = Path.GetFullPath(path);
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(install)) + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
        if (full.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
        // The same folder reached another way: ask Windows where each path really opens.
        if (FinalPath.Of(path) is { } resolved && FinalPath.Of(install) is { } resolvedInstall)
            return resolved.StartsWith(Path.TrimEndingDirectorySeparator(resolvedInstall) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return Doubtful(path, install);
    }

    /// <summary>
    /// Whether the path could still be the install folder's behind a short name, a substituted drive or a link, which the plain
    /// comparison and the resolution above could not settle: then the helper counts as running.
    /// </summary>
    private static bool Doubtful(string path, string install)
    {
        if (path.Contains('~', StringComparison.Ordinal) || install.Contains('~', StringComparison.Ordinal)) return true;
        string root, installRoot;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
            installRoot = Path.GetPathRoot(Path.GetFullPath(install)) ?? "";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
        if (!root.Equals(installRoot, StringComparison.OrdinalIgnoreCase)) return true;
        return Linked(install);
    }

    /// <summary>The install folder, or a folder above it, is a link, a junction or a mount point; a folder that cannot be looked at answers yes.</summary>
    private static bool Linked(string install)
    {
        try
        {
            for (var folder = new DirectoryInfo(Path.GetFullPath(install)); folder is not null; folder = folder.Parent)
                if (folder.Exists && (folder.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or PathTooLongException)
        {
            return true;
        }
    }

    /// <summary>The launcher's helper running now, by process name; null when none is (the game's own process is not a helper).</summary>
    internal static string? HelperRunning(Profile profile) => (profile.Helpers ?? []).FirstOrDefault(name => HelperRuns(profile, name));

    // ------------------------------------------------------------------ reading and writing the game's file

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<ids>@[0-9;]+)?[ \t]*=[ \t]*(?<value>.*?)[ \t]*(?://[ \t]*(?<allowed>.*?))?[ \t]*$")]
    private static partial Regex SettingLine();

    [GeneratedRegex(@"^(-?\d+(?:\.\d+)?) to (-?\d+(?:\.\d+)?)$")]
    private static partial Regex Range();

    internal static SettingsFile Parse(byte[] bytes)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(newLine);
        var settings = new List<Line>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("//", StringComparison.Ordinal) || SettingLine().Match(lines[i]) is not { Success: true } m) continue;
            var value = m.Groups["value"];
            settings.Add(new Line(i, m.Groups["name"].Value, m.Groups["name"].Value + m.Groups["ids"].Value, value.Value,
                m.Groups["allowed"].Success ? m.Groups["allowed"].Value : null, value.Index, value.Length));
        }
        return new SettingsFile(lines, newLine, bom, settings);
    }

    /// <summary>The file with the given lines' values changed, everything else byte for byte.</summary>
    internal static byte[] Write(SettingsFile file, IReadOnlyDictionary<int, string> values)
    {
        var lines = (string[])file.Lines.Clone();
        foreach (var line in file.Settings)
            if (values.TryGetValue(line.Index, out var value))
                lines[line.Index] = lines[line.Index][..line.ValueStart] + value + lines[line.Index][(line.ValueStart + line.ValueLength)..];
        var body = (file.Text ?? Encoding.UTF8).GetBytes(string.Join(file.NewLine, lines));
        return file.Bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    /// <summary>
    /// The value as this line takes it: one of its listed choices (in the list's own spelling), a number in its range
    /// (written as the line writes its numbers), or true or false for a line that holds one; null when it can't.
    /// </summary>
    internal static string? Fit(Line line, string value)
    {
        if (line.Allowed is not { } allowed)
            return line.Value is "true" or "false" && value is "true" or "false" ? value : null;
        if (allowed.StartsWith("one of ", StringComparison.Ordinal))
            return allowed[7..].Split(", ").FirstOrDefault(choice => choice.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (Range().Match(allowed) is not { Success: true } range ||
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            number < double.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture) ||
            number > double.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture))
            return null;
        var decimals = line.Value.IndexOf('.', StringComparison.Ordinal) is var dot and >= 0 ? line.Value.Length - dot - 1 : 0;
        return number.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    /// <summary>The lines a group changes in this file and the value each takes; a line that can't take Exo's value is left out.</summary>
    internal static List<(Line Line, string Value)> Targets(SettingsFile file, Group group)
    {
        var targets = new List<(Line, string)>();
        foreach (var change in group.Changes)
            foreach (var line in file.Settings)
                if (line.Name == change.Key && (change.Free ? line.Allowed is null ? change.Value : null : Fit(line, change.Value)) is { } value)
                    targets.Add((line, value));
        return targets;
    }

    /// <summary>Each group as it stands; groups with nothing to change in this version of the file are left out.</summary>
    internal static List<GroupState> States(Profile profile, SettingsFile file, IReadOnlyDictionary<string, Remembered> kept, IReadOnlyCollection<string>? keptOn = null) =>
        States(profile, [new OpenFile(profile.File, "", file)], kept, keptOn);

    /// <summary>
    /// Each group as it stands across the profile's files (the main one first, whose facts pick the groups). With
    /// <paramref name="keptOn"/> (the groups Optimize keeps on), a group holding the window mode is on only once it's among
    /// them: lines that happen to hold its values aren't being held yet, and a press has to say so.
    /// </summary>
    internal static List<GroupState> States(Profile profile, IReadOnlyList<OpenFile> files, IReadOnlyDictionary<string, Remembered> kept, IReadOnlyCollection<string>? keptOn = null)
    {
        var states = new List<GroupState>();
        if (files.Count == 0) return states;
        foreach (var group in profile.Groups(files[0].File))
        {
            var targets = files.SelectMany(open => Targets(open.File, group).Select(t => (open.Key, t.Line, t.Value))).ToList();
            if (targets.Count == 0) continue;
            var held = keptOn is null || keptOn.Contains(group.Id) || !group.Changes.Any(change => profile.WindowLines?.Contains(change.Key) == true);
            states.Add(new GroupState(group.Id, group.Label, held && targets.TrueForAll(t => t.Line.Value == t.Value),
                targets.Exists(t => kept.TryGetValue(t.Key + t.Line.Id, out var was) && was.Set == t.Line.Value)));
        }
        return states;
    }

    /// <summary>
    /// What a press asked for that these files can't give: each setting no line holds, or no line takes Exo's value for, by
    /// its name. A wrong spelling or a line an update dropped is skipped as it is written, so this is what tells the difference.
    /// </summary>
    internal static List<string> Missing(Profile profile, IReadOnlyList<OpenFile> files)
    {
        if (files.Count == 0) return [];
        return [.. profile.Groups(files[0].File).SelectMany(group => group.Changes).Where(change => !change.Spare)
            .Where(change => !files.Any(open => Targets(open.File, new Group("", "", [change])).Count > 0))
            .Select(change => change.Key).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The line's word, which only Exo's finding and writing all it asked for may turn to Applied.</summary>
    internal enum Standing { Off, Partly, Applied, Unfound }

    /// <summary>
    /// Applied: every group holds Exo's values and nothing asked for is missing. Partly: they hold, but some of what was asked
    /// for isn't in the file. Unfound: the file is there and holds none of it. Off: anything else, which a press sets.
    /// </summary>
    internal static Standing Stands(IReadOnlyCollection<GroupState> states, int missing) =>
        states.Count == 0 ? Standing.Unfound
        : !states.All(state => state.On) ? Standing.Off
        : missing > 0 ? Standing.Partly
        : Standing.Applied;

    /// <summary>
    /// The edits that turn a group on or off, with what Exo now keeps. On: every line to Exo's value, remembering what
    /// it held the first time, but for <paramref name="theirs"/> (lines the player changed in the game since). Off: a
    /// line Exo set goes back to what it held, unless it was changed in the game since.
    /// </summary>
    internal static Dictionary<int, string> Plan(SettingsFile file, Group group, bool on, Dictionary<string, Remembered> kept, string key = "",
        IReadOnlySet<string>? theirs = null, IReadOnlySet<string>? only = null)
    {
        var edits = new Dictionary<int, string>();
        foreach (var (line, value) in Targets(file, group))
        {
            var id = key + line.Id;
            if (on)
            {
                if (line.Value == value || theirs?.Contains(id) == true || only?.Contains(id) == false) continue;
                kept[id] = kept.TryGetValue(id, out var was) ? was with { Set = value } : new Remembered(line.Value, value);
                edits[line.Index] = value;
            }
            else if (kept.Remove(id, out var was) && line.Value == was.Set)
                edits[line.Index] = was.Was;
        }
        return edits;
    }

    // ------------------------------------------------------------------ on this PC

    private static readonly Lock Gate = new();
    private static string Folder => Path.Combine(PathHelper.AppDataDir, "game-settings");
    private static string KeptPath => Path.Combine(Folder, "kept.json");
    private static string WindowModesPath => Path.Combine(Folder, "window-modes.json");
    internal const string Borderless = "borderless", Fullscreen = "fullscreen";

    /// <summary>This PC's facts with the window mode chosen for this game; Borderless until one is chosen.</summary>
    private static Pc Chosen(Pc pc, string profileId)
    {
        lock (Gate) return pc with { Fullscreen = LoadWindowModes().GetValueOrDefault(profileId) == Fullscreen };
    }

    /// <summary>Remembers the window mode chosen for this game, through a Put back too; false when it couldn't.</summary>
    internal static bool ChooseWindowMode(Profile profile, string mode)
    {
        lock (Gate)
        {
            var modes = LoadWindowModes();
            modes[profile.Id] = mode;
            try
            {
                Directory.CreateDirectory(Folder);
                var temporary = WindowModesPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(modes));
                File.Move(temporary, WindowModesPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Game settings: {profile.Title}'s window mode couldn't be saved: {ex.GetType().Name}");
                return false;
            }
        }
    }

    private static Dictionary<string, string> LoadWindowModes()
    {
        try
        {
            return File.Exists(WindowModesPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(WindowModesPath)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn($"Game settings: the chosen window modes couldn't be read: {ex.GetType().Name}");
            return [];
        }
    }

    /// <summary>The game's file as it is now; null when the game hasn't written one yet or it can't be read (<see cref="TryReadAll"/> tells which).</summary>
    internal static SettingsFile? Read(Profile profile)
    {
        try { return File.Exists(profile.File) ? (profile.Format ?? Parse)(File.ReadAllBytes(profile.File)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Game settings: {profile.Title}'s file couldn't be read: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// The profile's files that exist, as <see cref="ReadAll"/> reads them, and whether all of them could be read. False when a file
    /// is there and can't be read (the game or an antivirus holds it, or it is unreadable): nothing about it is known, so nothing is
    /// changed, and a Put back that stops there says it could not. An empty list with true is a game that has written nothing yet.
    /// </summary>
    internal static bool TryReadAll(Profile profile, out List<OpenFile> files)
    {
        var locked = false;
        files = [];
        try
        {
            if (!File.Exists(profile.File)) return true;
            files.Add(new(profile.File, "", (profile.Format ?? Parse)(File.ReadAllBytes(profile.File))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Game settings: {profile.Title}'s file couldn't be read: {ex.GetType().Name}");
            files = [];
            return false;
        }
        foreach (var path in profile.PlayerFiles?.Invoke() ?? [])
        {
            try
            {
                if (File.Exists(path))
                    files.Add(new(path, $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)}|", (profile.Format ?? Parse)(File.ReadAllBytes(path))));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Game settings: a {profile.Title} player file couldn't be read: {ex.GetType().Name}");
                locked = true;
            }
        }
        if (locked) files = [];
        return !locked;
    }

    /// <summary>
    /// The profile's files that exist, the main one first (none without it), each player file keyed by its name and the
    /// folder it sits in ("76561198000000001/g.cod26.1.0.l.txt|") so its kept lines never meet another's.
    /// </summary>
    internal static List<OpenFile> ReadAll(Profile profile) => TryReadAll(profile, out var files) ? files : [];

    /// <summary>
    /// Turns a group on or off (all groups for null), and keeps what stays on across the game's updates: every group
    /// after Optimize, none after Put back. <paramref name="program"/> is the game's (<see cref="Profile.Program"/>).
    /// False when the file couldn't be read or written, or while the game, its anti-cheat launcher or a helper of it runs: a
    /// press is refused then, and nothing is written or recorded (a helper writes the file itself as it closes).
    /// </summary>
    internal static bool Set(Profile profile, string? groupId, bool on, string? program = null)
    {
        lock (Gate)
        {
            // The game, its launcher or a helper runs: it writes the file itself as it closes, so nothing is written now and
            // what Optimize keeps on stays as it was. The page says so first; this is the answer that counts.
            if (Busy(profile)) return false;
            // NVIDIA-only profiles (such as Wuthering Waves) have no local settings file to edit or wait for.
            if (string.IsNullOrEmpty(profile.File)) return true;
            // A file the game or an antivirus holds is not "no file": nothing is known about it, so nothing changes and the answer is no.
            if (!TryReadAll(profile, out var files)) return false;
            if (files.Count == 0)
            {
                // Nothing to write yet: Optimize waits for the game's first run (Keep.cs), and Put back cancels the wait.
                if (!on) return DeleteKeeping(profile) || Failed(profile, "Put back");
                var pending = groupId is null ? ["*"] : new List<string> { groupId };
                var waiting = LoadKeeping(profile)?.Groups ?? [];
                if (!SaveKeeping(profile, files, [.. waiting.Union(pending)], program, null, null, null, pending: true)) return Failed(profile, on ? "Optimize" : "Put back");
                AppLog.Info($"Game settings: {profile.Title}: Optimize waits until the game has written its file, then applies after the first time it runs.");
                return true;
            }
            var kept = KeptOf(profile.Id);
            var groups = profile.Groups(files[0].File).Where(g => groupId is null || g.Id == groupId).ToList();
            var writes = Edits(files, groups, on, kept, allOfThem: groupId is null);
            if (Commit(profile, writes, kept) is not Committed.Written) return false;
            if (writes.Count > 0)
                AppLog.Info($"Game settings: {profile.Title}: {(groupId ?? "all")} {(on ? "on" : "off")}, {writes.Sum(w => w.Edits.Count)} lines in {writes.Count} files.");
            if (on && Missing(profile, files) is { Count: > 0 } missing)
                AppLog.Warn($"Game settings: {profile.Title}: {missing.Count} settings aren't in its file, so the line won't say Applied: {string.Join(", ", missing)}.");
            var ids = groups.Select(g => g.Id).ToList();
            var keeping = LoadKeeping(profile)?.Groups ?? [];
            List<string> stays = on ? [.. keeping.Union(ids)] : groupId is null ? [] : [.. keeping.Except(ids)];
            if (stays.Count == 0)
            {
                if (!DeleteKeeping(profile)) return Failed(profile, on ? "Optimize" : "Put back");
            }
            else
            {
                var now = writes.Count > 0 ? ReadAll(profile) : files;
                if (!SaveKeeping(profile, now, stays, program, ProgramTime(program), null, profile.Facts?.Invoke())) return Failed(profile, on ? "Optimize" : "Put back");
            }
            if (on) NoteOptimizePressed(profile, ids, groupId is null);
            return true;
        }
    }

    /// <summary>What Optimize keeps on could not be saved: said once, and the answer is no, so nothing acts as if it had.</summary>
    private static bool Failed(Profile profile, string what)
    {
        AppLog.Warn($"Game settings: {profile.Title}: {what} couldn't be kept in the record, so it is not done.");
        return false;
    }

    /// <summary>
    /// Optimize was pressed: every value it holds for these groups is Exo's again, in the player's or the game's place; Keep
    /// starts counting over. <paramref name="clearSignals"/> when this is the write after the game's first run, which is a
    /// session: the marker noted for it is used up by the write it explains.
    /// </summary>
    private static void NoteOptimizePressed(Profile profile, List<string> groups, bool all, bool clearSignals = false)
    {
        Keep.Update(doc =>
        {
            if (!doc.Games.TryGetValue(profile.Id, out var game)) return;
            foreach (var entry in game.Entries.Values)
            {
                // A write the file did not take stays refused: Optimize never shows it held.
                if (entry.ReadBack != entry.Intended || !(all || groups.Contains(GroupOf(entry)))) continue;
                entry.Go(KeepEvent.Pressed, entry.ReadBack, "Optimize");
                entry.Found = null;
                entry.ResetStreak = 0;
            }
            if (clearSignals) game.Signals = [];
            game.RefusedSaid = false;
        });
    }

    /// <summary>The group an entry's line belongs to ("mw4/audio": audio).</summary>
    internal static string GroupOf(Keep.KeepEntry entry) => entry.Owner.Contains('/') ? entry.Owner[(entry.Owner.IndexOf('/') + 1)..] : "";

    /// <summary>
    /// The edits that turn these groups on or off in each file, with what Exo now keeps; only files with an edit. With
    /// <paramref name="allOfThem"/> (the whole Optimize, not one group), a line Exo set that no group asks for any more (a
    /// frame cap from before, a line only the other window mode uses) also goes back to what it held, unless it was changed
    /// in the game since.
    /// </summary>
    private static List<(OpenFile Open, Dictionary<int, string> Edits)> Edits(IEnumerable<OpenFile> files, IReadOnlyList<Group> groups, bool on,
        Dictionary<string, Remembered> kept, IReadOnlySet<string>? theirs = null, bool allOfThem = false, IReadOnlySet<string>? only = null)
    {
        var wanted = groups.SelectMany(group => group.Changes).Select(change => change.Key).ToHashSet(StringComparer.Ordinal);
        var writes = new List<(OpenFile Open, Dictionary<int, string> Edits)>();
        foreach (var open in files)
        {
            var edits = new Dictionary<int, string>();
            foreach (var group in groups)
                foreach (var (index, value) in Plan(open.File, group, on, kept, open.Key, theirs, only))
                    edits[index] = value;
            if (allOfThem)
                foreach (var line in open.File.Settings.Where(line => !wanted.Contains(line.Name)))
                    if (kept.Remove(open.Key + line.Id, out var was) && line.Value == was.Set)
                        edits[line.Index] = was.Was;
            if (edits.Count > 0) writes.Add((open, edits));
        }
        return writes;
    }

    /// <summary>What a write did: it was written, it was refused because the game or a helper runs, or the file could not be written.</summary>
    private enum Committed { Written, Busy, Failed }

    /// <summary>
    /// Whether the game, its launcher or a helper runs, as the check directly before a write asks it: the same answer as
    /// <see cref="Busy"/>, with a test able to say so for the moment of the write alone (<see cref="ForceBusyBeforeWriteForTest"/>).
    /// </summary>
    private static bool BusyBeforeWrite(Profile profile) => ForcedBusy.Value || Busy(profile);

    private static readonly AsyncLocal<bool> ForcedBusy = new();

    /// <summary>Tests only: the game or a helper counts as running at the check directly before a write, until disposed.</summary>
    internal static IDisposable ForceBusyBeforeWriteForTest()
    {
        var previous = ForcedBusy.Value;
        ForcedBusy.Value = true;
        return new WriterRestore(() => ForcedBusy.Value = previous);
    }

    /// <summary>
    /// Writes each file's edits (the whole file as Exo first found it kept beside) and what Exo kept. What Exo is about to
    /// change goes into the record first, flushed, so a crash between the two leaves an undo for a change that may not have
    /// happened (Put back then finds the line as it was and changes nothing) and never a change with no undo.
    /// A write that fails puts the record back as it was. The one place every write goes through, so a game or a launcher
    /// helper that started after the caller's own check is looked for here, directly before the first file is written:
    /// nothing is written and the record is not touched, and <see cref="Committed.Busy"/> says that apart from a failure.
    /// </summary>
    private static Committed Commit(Profile profile, List<(OpenFile Open, Dictionary<int, string> Edits)> writes, Dictionary<string, Remembered> kept)
    {
        if (writes.Count == 0) return Committed.Written;
        if (BusyBeforeWrite(profile))
        {
            AppLog.Info($"Game settings: {profile.Title} or its launcher started as Exo was about to write, so nothing is changed.");
            return Committed.Busy;
        }
        var before = Keep.Load().Games.GetValueOrDefault(profile.Id);
        var snapshot = before is null ? null : JsonSerializer.Serialize(before.Entries);
        var written = new List<(string Path, byte[] Original)>();
        try
        {
            Directory.CreateDirectory(Folder);
            if (!SyncRecord(profile, writes, kept)) throw new IOException("the undo record could not be written");
            foreach (var (open, edits) in writes)
            {
                var name = open.Key.Length == 0 ? profile.Id + ".original" + Path.GetExtension(open.Path)
                    : profile.Id + "." + open.Key.TrimEnd('|').Replace('/', '.') + ".original";
                var original = Path.Combine(Folder, name);
                if (!File.Exists(original)) File.Copy(open.Path, original);
                var was = File.ReadAllBytes(open.Path);
                FileWriter(open.Path, Write(open.File, edits));
                written.Add((open.Path, was));
            }
            KeepEvidence.Record(profile, writes, kept);
            return Committed.Written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Game settings: {profile.Title}'s file couldn't be written: {ex.GetType().Name}");
            // A file already written goes back to what it held, so a half-done write leaves nothing changed.
            var left = new HashSet<string>(StringComparer.Ordinal);
            for (var i = written.Count - 1; i >= 0; i--)
            {
                try { FileWriter(written[i].Path, written[i].Original); }
                catch (Exception putBack) when (putBack is IOException or UnauthorizedAccessException) { left.Add(written[i].Path); }
            }
            var changed = writes.Where(write => left.Contains(write.Open.Path)).Select(write => write.Open.Key).ToList();
            Keep.Update(doc =>
            {
                if (changed.Count == 0)
                {
                    if (snapshot is null) doc.Games.Remove(profile.Id);
                    else if (doc.Games.TryGetValue(profile.Id, out var game))
                        game.Entries = JsonSerializer.Deserialize<Dictionary<string, Keep.KeepEntry>>(snapshot) ?? [];
                    return;
                }
                // A file that could not be put back keeps the new entries, which hold what it held before Exo wrote it;
                // everything else goes back to the record as it was before this write.
                if (!doc.Games.TryGetValue(profile.Id, out var now)) now = doc.Games[profile.Id] = new Keep.KeepGame();
                var restored = new Dictionary<string, Keep.KeepEntry>(StringComparer.Ordinal);
                foreach (var (key, entry) in now.Entries)
                    if (changed.Exists(fileKey => OfFile(key, fileKey))) restored[key] = entry;
                foreach (var (key, entry) in JsonSerializer.Deserialize<Dictionary<string, Keep.KeepEntry>>(snapshot ?? "{}") ?? [])
                    if (!changed.Exists(fileKey => OfFile(key, fileKey))) restored[key] = entry;
                now.Entries = restored;
            });
            if (left.Count > 0) AppLog.Warn($"Game settings: {profile.Title}: {left.Count} file(s) couldn't be put back, so their undo is kept.");
            return Committed.Failed;
        }
    }

    /// <summary>The record as the writes will leave it: an entry for every line Exo now keeps, none for a line that went back.</summary>
    private static bool SyncRecord(Profile profile, List<(OpenFile Open, Dictionary<int, string> Edits)> writes, Dictionary<string, Remembered> kept) =>
        Keep.Update(doc =>
        {
            var game = doc.Games.TryGetValue(profile.Id, out var existing) ? existing : doc.Games[profile.Id] = new Keep.KeepGame { Migrated = true };
            foreach (var key in game.Entries.Keys.Where(key => !kept.ContainsKey(key)).ToList()) game.Entries.Remove(key);
            var groups = writes.Count > 0 ? GroupsByLine(profile, writes[0].Open.File) : [];
            foreach (var (key, remembered) in kept)
            {
                if (!game.Entries.TryGetValue(key, out var entry))
                    game.Entries[key] = entry = new Keep.KeepEntry { Target = key, WrittenAt = DateTime.UtcNow, ExoVersion = Keep.ExoVersion };
                entry.Before = remembered.Was;
                entry.Intended = remembered.Set;
                entry.Owner = profile.Id + "/" + groups.GetValueOrDefault(LineNameOf(key), "");
            }
        });

    /// <summary>Whether a record key is one of a file's lines: it starts with the file's key ("" for the main file, whose keys hold no "|").</summary>
    private static bool OfFile(string key, string fileKey) => fileKey.Length == 0 ? !key.Contains('|') : key.StartsWith(fileKey, StringComparison.Ordinal);

    private static string LineNameOf(string key)
    {
        var id = key[(key.IndexOf('|') + 1)..];
        var at = id.IndexOf('@');
        return at < 0 ? id : id[..at];
    }

    /// <summary>The group each line name belongs to in this file (the first that holds it).</summary>
    internal static Dictionary<string, string> GroupsByLine(Profile profile, SettingsFile main)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in profile.Groups(main))
            foreach (var change in group.Changes) map.TryAdd(change.Key, group.Id);
        return map;
    }

    /// <summary>How a file is written: tests swap it in their own flow to look at what the record says at that moment.</summary>
    private static Action<string, byte[]> FileWriter => ForcedWriter.Value ?? WriteFile;

    private static readonly AsyncLocal<Action<string, byte[]>?> ForcedWriter = new();

    /// <summary>Tests only: files are written by this for the rest of this test's flow, until disposed.</summary>
    internal static IDisposable ForceWriterForTest(Action<string, byte[]> writer)
    {
        var previous = ForcedWriter.Value;
        ForcedWriter.Value = writer;
        return new WriterRestore(() => ForcedWriter.Value = previous);
    }

    private sealed class WriterRestore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>Beside it, then over it, keeping it read-only if it was.</summary>
    private static void WriteFile(string path, byte[] bytes)
    {
        var attributes = File.GetAttributes(path);
        var temporary = path + ".exo-tmp";
        File.WriteAllBytes(temporary, bytes);
        if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Move(temporary, path, overwrite: true);
        if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
    }
}
