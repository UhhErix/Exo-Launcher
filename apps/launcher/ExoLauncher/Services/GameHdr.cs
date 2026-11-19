using System.Runtime.InteropServices;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

/// <summary>
/// Whether a game outputs real HDR, by Exo's own judgment, for System > HDR > HDR for HDR games. In order:
/// - the game's own settings (GameHdr.Read.cs): an HDR key its engine writes only for a game with HDR, or any HDR key set
///   on. They beat everything after them;
/// - what Exo learned on this PC: the game ran with Windows HDR on and its own HDR setting read on after, kept for good;
/// - PCGamingWiki's "hdr" field (<see cref="GameWiki"/>), only when the game says nothing;
/// - its files, weak: HDR calibration art it ships with HDR names in its program, only once the wiki was asked and had no
///   answer; they say yes or nothing, never no.
/// Nothing is injected and the game's memory is never read; its files and registry keys are read, never changed. The
/// verdict (yes, no or unknown) and where it came from are kept per game in game-hdr.json, with what Exo learned and the
/// weak signals per program version (read once).
/// </summary>
internal static partial class GameHdr
{
    /// <param name="Has">The game outputs HDR: true or false, null when this source says nothing.</param>
    /// <param name="On">The game's own HDR setting: on, off, or null when not known.</param>
    internal sealed record Signal(bool? Has, bool? On = null);

    /// <param name="Hdr">"yes", "no" or "unknown".</param>
    /// <param name="From">"settings", "learned", "wiki", "files", or null when unknown.</param>
    /// <param name="On">The game's own HDR setting as last read; null when not known.</param>
    internal sealed record Verdict(string Hdr, string? From, bool? On);

    /// <param name="Files">The weak signals (true or null) for the program named by <paramref name="FilesKey"/> (path, size, write time).</param>
    private sealed record Kept(string Hdr, string? From, bool? On, bool Learned = false, string? FilesKey = null, bool? Files = null);

    internal static readonly Signal Nothing = new(null);

    /// <summary>One verdict from what each source says (the wiki's field as <see cref="GameWiki.Cached"/> keeps it).</summary>
    internal static Verdict Merge(Signal settings, bool learned, string? wiki, Signal files)
    {
        if (settings.Has is { } has) return new(has ? "yes" : "no", "settings", settings.On);
        if (learned) return new("yes", "learned", settings.On);
        if (wiki == "true") return new("yes", "wiki", settings.On);
        if (wiki is "false" or "hackable") return new("no", "wiki", settings.On);
        return files.Has == true ? new("yes", "files", settings.On) : new("unknown", null, settings.On);
    }

    /// <summary>What Auto follows: true for yes, false for no, null while unknown.</summary>
    internal static bool? Supports(Verdict verdict) => verdict.Hdr switch { "yes" => true, "no" => false, _ => null };

    private static readonly Lock Gate = new();
    private static Dictionary<string, Kept>? _kept;
    // Asked every 5 s while a game runs: the last verdict holds 30 s unless the wiki's answer changed since.
    private static readonly Dictionary<string, (Verdict Verdict, string? Wiki, bool Files, long At)> Recent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The game's verdict now. <paramref name="readFiles"/> false: the weak signals as last kept, never read (a game being
    /// started, a page's first look before the wiki answered).
    /// </summary>
    internal static Verdict Judge(GameEntry game, bool readFiles = true)
    {
        var wiki = GameWiki.Cached(game.Id);
        lock (Gate)
            if (Recent.TryGetValue(game.Id, out var recent) && recent.Wiki == wiki && (recent.Files || !readFiles) &&
                Environment.TickCount64 - recent.At < 30_000)
                return recent.Verdict;
        var settings = ReadSettings(game);
        Kept? kept;
        lock (Gate) kept = Load().GetValueOrDefault(game.Id);
        var learned = kept?.Learned == true;
        // The weak signals only once the wiki was asked and had nothing, read once per program version.
        var files = settings.Has is null && !learned && wiki == "unknown"
            ? readFiles ? FilesSignal(game, ref kept) : new Signal(kept?.Files)
            : Nothing;
        var verdict = Merge(settings, learned, wiki, files);
        lock (Gate)
        {
            Recent[game.Id] = (verdict, wiki, readFiles, Environment.TickCount64);
            // On what is kept now, so a game learned meanwhile (AfterRun) stays learned.
            var current = Load().GetValueOrDefault(game.Id);
            var now = (current ?? new Kept(verdict.Hdr, verdict.From, verdict.On)) with
                { Hdr = verdict.Hdr, From = verdict.From, On = verdict.On, FilesKey = kept?.FilesKey, Files = kept?.Files };
            if (now != current)
            {
                Load()[game.Id] = now;
                Save();
            }
        }
        return verdict;
    }

    /// <summary>
    /// After a game ran while the main screen was in HDR: its own HDR setting reading on now (it writes its settings as it
    /// closes) makes it an HDR game for good on this PC.
    /// </summary>
    internal static void AfterRun(GameEntry game)
    {
        if (ReadSettings(game).On != true) return;
        lock (Gate)
        {
            var kept = Load().GetValueOrDefault(game.Id);
            if (kept?.Learned == true) return;
            Load()[game.Id] = (kept ?? new Kept("yes", "learned", true)) with { Learned = true };
            Recent.Remove(game.Id);
            Save();
        }
        AppLog.Info($"HDR: {game.Title} ran with its own HDR on; Exo keeps it as an HDR game.");
    }

    /// <summary>The weak signals for the game's program, read once per program version and kept.</summary>
    private static Signal FilesSignal(GameEntry game, ref Kept? kept)
    {
        if (string.IsNullOrEmpty(game.Path) || MainProgram(game.Path) is not { } program) return Nothing;
        string key;
        try
        {
            var info = new FileInfo(program);
            key = $"{program}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Nothing; }
        if (kept?.FilesKey == key) return new Signal(kept.Files);
        // The art first (file names only); the program is read only when the art is there.
        bool? has = ShipsHdrCalibration(game.Path) && ProgramNamesHdr(program) ? true : null;
        kept = (kept ?? new Kept("unknown", null, null)) with { FilesKey = key, Files = has };
        AppLog.Debug($"HDR: {game.Title}'s files read: {(has == true ? "HDR calibration art and HDR names in its program" : "nothing")}.");
        return new Signal(has);
    }

    // ------------------------------------------------------------------ the screen's real peak

    /// <summary>
    /// The screen's real peak brightness in nits and where it was read: Windows HDR Calibration's profile (behind Exo's own
    /// SDR gamma one while that is on: <paramref name="replaced"/>), else the screen's EDID, else what DXGI reports for it.
    /// </summary>
    internal static (int Nits, string From)? PeakNits(WindowsHdr.Screen screen, string? replaced)
    {
        if (SdrInHdr.CalibratedPeak(screen, replaced) is { } calibrated) return ((int)Math.Round(calibrated), "calibration");
        if (WindowsHdr.PeakNits(screen) is { } edid) return (edid, "edid");
        if (DxgiPeak(screen.GdiName) is { } dxgi) return (dxgi, "dxgi");
        return null;
    }

    /// <summary>The main screen's real peak when it can show HDR; null otherwise.</summary>
    internal static int? MainScreenPeak() =>
        WindowsHdr.Primary() is { Supported: true } screen
            ? PeakNits(screen, MainWindow.SdrGammaReplaced(App.Services.Settings, screen.DevicePath))?.Nits
            : null;

    /// <summary>IDXGIOutput6::GetDesc1's MaxLuminance for the output on that screen; null when DXGI doesn't say.</summary>
    private static unsafe int? DxgiPeak(string gdiName)
    {
        try
        {
            var factoryIid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            if (CreateDXGIFactory1(in factoryIid, out var factory) < 0) return null;
            try
            {
                var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)factory)[12]; // EnumAdapters1
                for (uint a = 0; a < 16; a++)
                {
                    IntPtr adapter;
                    if (enumAdapters(factory, a, &adapter) < 0) return null;
                    try
                    {
                        var enumOutputs = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)adapter)[7]; // EnumOutputs
                        for (uint o = 0; o < 16; o++)
                        {
                            IntPtr output;
                            if (enumOutputs(adapter, o, &output) < 0) break;
                            try
                            {
                                var output6Iid = new Guid("068346e8-aaec-4b84-add7-137f513f77a1"); // IDXGIOutput6
                                if (Marshal.QueryInterface(output, in output6Iid, out var output6) < 0) continue;
                                try
                                {
                                    OutputDesc1 desc;
                                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, OutputDesc1*, int>)(*(IntPtr**)output6)[27]; // GetDesc1
                                    if (getDesc1(output6, &desc) >= 0 && new string(desc.DeviceName).TrimEnd('\0').Equals(gdiName, StringComparison.OrdinalIgnoreCase))
                                        return desc.MaxLuminance > 0 ? (int)Math.Round(desc.MaxLuminance) : null;
                                }
                                finally { Marshal.Release(output6); }
                            }
                            finally { Marshal.Release(output); }
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
                return null;
            }
            finally { Marshal.Release(factory); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            AppLog.Debug("HDR: DXGI couldn't be asked for the screen's peak: " + ex.GetType().Name);
            return null;
        }
    }

    /// <summary>DXGI_OUTPUT_DESC1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct OutputDesc1
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public uint Rotation;
        public IntPtr Monitor;
        public uint BitsPerColor;
        public uint ColorSpace;
        public fixed float Primaries[8];
        public float MinLuminance, MaxLuminance, MaxFullFrameLuminance;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    // ------------------------------------------------------------------ kept

    private static string FilePath => Path.Combine(PathHelper.AppDataDir, "game-hdr.json");

    private static Dictionary<string, Kept> Load()
    {
        if (_kept is not null) return _kept;
        _kept = new Dictionary<string, Kept>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length < 4 * 1024 * 1024 &&
                JsonSerializer.Deserialize<Dictionary<string, Kept>>(File.ReadAllText(FilePath)) is { } read)
                foreach (var pair in read) _kept[pair.Key] = pair.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Debug("HDR verdicts unreadable: " + ex.GetType().Name);
        }
        return _kept;
    }

    private static void Save()
    {
        try
        {
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_kept));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("HDR verdicts not saved: " + ex.GetType().Name);
        }
    }
}
