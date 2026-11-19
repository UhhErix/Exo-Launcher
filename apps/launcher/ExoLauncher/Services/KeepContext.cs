using System.Globalization;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using ExoLauncher.Adapters.Cli;

namespace ExoLauncher.Services;

/// <summary>
/// The facts in force when Keep looks at a value: the game's build, the graphics driver, Windows' build, how many screens
/// there are, which audio device is the default, and the file's own time. When a value has moved, the difference between
/// these now and the ones when it was last seen held is the first thing that says why (a driver install, a game update,
/// a second screen), so each look keeps them and the log names what changed.
/// Ways it could fail: reading a fact throws or blocks (each read is guarded and a fact that can't be read is left out,
/// never invented); a fact that was never read is reported as a change (a fact is compared only when both sides have it).
/// </summary>
internal static class KeepContext
{
    /// <summary>Where the PC's facts come from; a test swaps it for its own in the flow of one test.</summary>
    internal sealed record PcFacts(string? Driver, int WindowsBuild, int Screens, string? Audio);

    private static readonly AsyncLocal<Func<PcFacts>?> Forced = new();

    /// <summary>Tests only: this PC's facts for the rest of this test's flow, until disposed.</summary>
    internal static IDisposable ForceForTest(Func<PcFacts> source)
    {
        var previous = Forced.Value;
        Forced.Value = source;
        return new Restore(previous);
    }

    private sealed class Restore(Func<PcFacts>? previous) : IDisposable
    {
        public void Dispose() => Forced.Value = previous;
    }

    private static PcFacts ReadPc()
    {
        string? driver = null, audio = null;
        var screens = 0;
        try { driver = NvidiaDriverService.Installed(); } catch (Exception ex) when (Soft(ex)) { }
        try { screens = WindowsHdr.All().Count; } catch (Exception ex) when (Soft(ex)) { }
        try { audio = Peripherals.DefaultEndpoints().FirstOrDefault(endpoint => !endpoint.Capture)?.Name; } catch (Exception ex) when (Soft(ex)) { }
        return new(driver, Environment.OSVersion.Version.Build, screens, audio);
    }

    private static bool Soft(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidOperationException
        or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException;

    /// <summary>
    /// The facts now: the PC's, the game's (<see cref="Profile.Facts"/> and its program's time, as "game.*"), and the main
    /// file's own time. Keys with nothing to say are left out.
    /// </summary>
    internal static SortedDictionary<string, string> Now(GameSettings.Profile profile, string? program)
    {
        var facts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var pc = (Forced.Value ?? ReadPc)();
        if (pc.Driver is { Length: > 0 }) facts["driver"] = pc.Driver;
        facts["windows"] = pc.WindowsBuild.ToString(CultureInfo.InvariantCulture);
        if (pc.Screens > 0) facts["screens"] = pc.Screens.ToString(CultureInfo.InvariantCulture);
        if (pc.Audio is { Length: > 0 }) facts["audio"] = pc.Audio;
        try
        {
            foreach (var (name, value) in profile.Facts?.Invoke() ?? new Dictionary<string, string>()) facts["game." + name] = value;
        }
        catch (Exception ex) when (Soft(ex)) { /* the game's facts are left out */ }
        if (ProgramTime(program) is { } built) facts["game.program"] = built;
        if (FileTime(profile.File) is { } file) facts["file"] = file;
        return facts;
    }

    internal static string? ProgramTime(string? program) => FileTime(program);

    private static string? FileTime(string? path)
    {
        try { return path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path).ToString("O", CultureInfo.InvariantCulture) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// What changed between two sets of facts, as "driver 617.42 -> 621.14, screens 1 -> 2"; "nothing else changed" when
    /// neither side differs (a fact only one side has is not a change). The file's own time is one of them.
    /// </summary>
    internal static string Diff(IReadOnlyDictionary<string, string> then, IReadOnlyDictionary<string, string> now)
    {
        var changes = now.Where(pair => then.TryGetValue(pair.Key, out var was) && was != pair.Value)
            .Select(pair => $"{Plain(pair.Key)} {then[pair.Key]} -> {pair.Value}").ToList();
        return changes.Count == 0 ? "nothing else changed" : string.Join(", ", changes);
    }

    private static string Plain(string key) => key switch
    {
        "game.program" => "game program",
        "game.buildid" => "game build",
        "windows" => "Windows build",
        "file" => "file time",
        _ => key.StartsWith("game.", StringComparison.Ordinal) ? "game " + key[5..] : key,
    };

    /// <summary>
    /// Steam's own build number for an app: buildid in appmanifest_{app id}.acf of the library the game's folder sits in
    /// (steamapps\common\{folder} beside steamapps\appmanifest_{id}.acf). Null for a game that isn't a Steam one, or when the
    /// manifest isn't there or has none. Read only.
    /// </summary>
    internal static string? SteamBuildId(GameEntry game)
    {
        if (game.Store != StoreKind.Steam || string.IsNullOrEmpty(game.Path)) return null;
        var appId = game.Id[(game.Id.IndexOf(':') + 1)..];
        if (appId.Length == 0 || !appId.All(char.IsAsciiDigit)) return null;
        try
        {
            for (var folder = new DirectoryInfo(game.Path); folder is not null; folder = folder.Parent)
            {
                if (!folder.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) continue;
                var manifest = System.IO.Path.Combine(folder.FullName, $"appmanifest_{appId}.acf");
                return File.Exists(manifest) ? SteamProtocol.ParseAcfFields(File.ReadAllText(manifest)).GetValueOrDefault("buildid") : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* no build number */ }
        return null;
    }
}
