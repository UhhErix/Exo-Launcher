using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// What a game's own files say about HDR, read only. Its settings, per engine:
/// - Unreal Engine 4 and 5: GameUserSettings.ini and Engine.ini in each project's Saved\Config (under %LOCALAPPDATA%, the
///   install or My Games). Every Unreal game writes bUseHDRDisplayOutput and HDRDisplayOutputNits (they are the engine's
///   own settings class's), so they say only "on" when bUseHDRDisplayOutput=True; r.HDR.EnableHDROutput=1 the same. A
///   settings save (Saved\SaveGames\*Setting*.sav, the engine's GVAS) naming an HDR property says the game has HDR
///   (Stellar Blade's HDRAddtionalMaxLumiance);
/// - Call of Duty: its system file's "HDR@... = Automatic // one of Off, On, Automatic" (s.1.0.cod26.txt) and each
///   account's HDRGamma, HDRMaxLum and the like;
/// - Frostbite: PROFSAVE_profile's "GstRender.HighDynamicRangeOutput 1" in its Documents folder;
/// - RE Engine (re_chunk_000.pak beside it): the HDR keys of config.ini beside the game;
/// - Square Enix's XML (FINAL FANTASY XVI's config.xml: &lt;isFullscreenHDR_ v="false"/&gt;) and any other settings file
///   in its folder under Documents, My Games or Saved Games;
/// - the registry: Unity's PlayerPrefs (Software\{company}\{product} from its app.info; "HDR_h1234" names) and any
///   Software\{publisher}\{the game} key, as Glacier's (007 First Light: HDR=1, HDRBrightness) and Insomniac's
///   (Marvel's Spider-Man Remastered\Graphics: HDR, HDRMaxNits, HDRPaperWhite) keep theirs.
/// Any other HDR key there means the game has HDR output; one named for the switch itself and set on means it's on.
/// Weak signals from its files: HDR calibration art it ships loose, and then HDR names in its program (ST2084, HDR10,
/// scRGB...). The names alone say nothing: on this PC every Unreal program has them (the engine's), Marvel Rivals' too
/// without HDR, while God of War's and FINAL FANTASY XVI's have none and both have HDR.
/// </summary>
internal static partial class GameHdr
{
    /// <summary>What the game's own settings say, from every file and registry key above that it has.</summary>
    internal static Signal ReadSettings(GameEntry game)
    {
        if (!game.Installed || string.IsNullOrEmpty(game.Path) || game.Store == StoreKind.Riot || !Directory.Exists(game.Path)) return Nothing;
        var signals = new List<Signal>();
        try
        {
            foreach (var path in SettingsFiles(game))
                if (Small(path, 1 << 20) is { } bytes) signals.Add(FromPairs(Pairs(Text(bytes))));
            foreach (var path in SettingsSaves(game))
                if (Small(path, 4 << 20) is { } bytes) signals.Add(FromSave(bytes));
            signals.Add(FromRegistry(game));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Debug($"HDR: {game.Title}'s settings couldn't all be read: {ex.GetType().Name}");
        }
        return Combine(signals);
    }

    /// <summary>Has when any source has it; on when any says on, else off when any says off.</summary>
    internal static Signal Combine(IEnumerable<Signal> signals)
    {
        var list = signals.ToList();
        return new Signal(list.Exists(s => s.Has == true) ? true : null,
            list.Exists(s => s.On == true) ? true : list.Exists(s => s.On == false) ? false : null);
    }

    // ------------------------------------------------------------------ keys

    /// <summary>The switch itself, by its name past any "Section." prefix (and Unity's "_h1234" hash).</summary>
    private static readonly HashSet<string> Switches = new(StringComparer.OrdinalIgnoreCase)
    {
        "HDR", "HDREnabled", "HDREnable", "HDROn", "HDRMode", "HDROutput", "EnableHDR", "UseHDR", "bUseHDR", "bEnableHDR", "IsHDR",
        "IsHDREnabled", "isFullscreenHDR", "isFullscreenHDR_", "HighDynamicRangeOutput", "HighDynamicRange", "EnableHDROutput",
        "bUseHDRDisplayOutput",
    };

    /// <summary>What a set of settings keys says: any HDR key, or Unreal's two that every game writes only when on.</summary>
    internal static Signal FromPairs(IEnumerable<(string Key, string? Value)> pairs)
    {
        bool? has = null, on = null;
        foreach (var (key, value) in pairs)
        {
            var name = UnityHash().Replace(key[(key.LastIndexOf('.') + 1)..], "");
            if (name == "HDRDisplayOutputNits" || !HdrName().IsMatch(name) || NotOutput().IsMatch(name)) continue;
            var set = Switches.Contains(name) ? OnOff(value) : null;
            // Every Unreal game has these two (the engine's settings class); only "on" says the game has HDR.
            if (name is "bUseHDRDisplayOutput" or "EnableHDROutput")
            {
                if (set == true) has = true;
            }
            else has = true;
            if (set is { } s) on = s || on == true;
        }
        return new Signal(has, on);
    }

    /// <summary>A setting's value read as on or off; null for anything else (a level, a mode's number past 1).</summary>
    internal static bool? OnOff(string? value) => value?.Trim().Trim('"').ToLowerInvariant() switch
    {
        "1" or "true" or "on" or "enabled" or "enable" or "yes" or "auto" or "automatic" => true,
        "0" or "false" or "off" or "disabled" or "disable" or "no" => false,
        _ => null,
    };

    /// <summary>
    /// Every "key = value" a settings file holds, in the shapes games write: ini and Call of Duty ("Name@ids = value //
    /// allowed"), Frostbite's PROFSAVE ("GstRender.Key value"), Square Enix's XML (&lt;key v="value"/&gt;) and JSON.
    /// </summary>
    internal static IEnumerable<(string Key, string? Value)> Pairs(string text)
    {
        foreach (var regex in (Regex[])[IniPair(), SpacePair(), XmlPair(), JsonPair()])
            foreach (Match match in regex.Matches(text))
                yield return (match.Groups["key"].Value, match.Groups["value"].Value);
    }

    /// <summary>A settings file's text: UTF-16 or UTF-8 by its BOM, else byte for byte (ANSI and UTF-8 keys read the same).</summary>
    internal static string Text(byte[] bytes) => bytes switch
    {
        [0xFF, 0xFE, ..] => Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2),
        [0xEF, 0xBB, 0xBF, ..] => Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3),
        _ => Encoding.Latin1.GetString(bytes),
    };

    /// <summary>Unreal's settings save (GVAS) naming an HDR property: the game has HDR; whether it's on isn't read.</summary>
    internal static Signal FromSave(byte[] bytes) =>
        // GVAS first, or after a game's own wrapper (Stellar Blade's "EVAS", 8 bytes).
        bytes.AsSpan(0, Math.Min(bytes.Length, 64)).IndexOf("GVAS"u8) >= 0 && SaveHdrName().IsMatch(Encoding.Latin1.GetString(bytes)) ? new Signal(true) : Nothing;

    [GeneratedRegex(@"HDR(?![Ii])|Hdr(?![Ii])|(?<![A-Za-z])hdr(?![Ii])|HighDynamicRangeOutput")]
    private static partial Regex HdrName();

    // HDR that isn't the screen's: older games' HDR lighting and bloom (rendered in HDR, shown in SDR) and HDR audio (Lyra's
    // bUseHDRAudioMode, Wwise).
    [GeneratedRegex("bloom|lighting|render|level|exposure|adapt|audio|sound", RegexOptions.IgnoreCase)]
    private static partial Regex NotOutput();

    [GeneratedRegex(@"_h\d+$")]
    private static partial Regex UnityHash();

    [GeneratedRegex(@"^[ \t]*(?<key>[A-Za-z_][\w.]*)(?:@[\d;]+)?[ \t]*=[ \t]*(?<value>[^\r\n]*?)[ \t]*(?://[^\r\n]*)?\r?$", RegexOptions.Multiline)]
    private static partial Regex IniPair();

    [GeneratedRegex(@"^(?<key>GstRender\.[\w.]+)[ \t]+(?<value>[^\s]+)[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex SpacePair();

    [GeneratedRegex(@"<(?<key>[A-Za-z_][\w.]*)\s+v=""(?<value>[^""]*)""")]
    private static partial Regex XmlPair();

    [GeneratedRegex(@"""(?<key>[A-Za-z_][\w.]*)""\s*:\s*""?(?<value>[^"",}\s]*)")]
    private static partial Regex JsonPair();

    // A property name in the save: letters around HDR, ending with the string's NUL.
    [GeneratedRegex(@"[A-Za-z_]{0,40}HDR(?!I)[A-Za-z0-9_]{0,40}\0")]
    private static partial Regex SaveHdrName();

    [GeneratedRegex(@"^cod\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex CallOfDutyFolder();

    [GeneratedRegex(@"setting|option|config", RegexOptions.IgnoreCase)]
    private static partial Regex SettingsSaveName();

    // ------------------------------------------------------------------ where

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private static string MyGames => Path.Combine(Documents, "My Games");

    /// <summary>
    /// An Unreal install's projects: each folder (or one level down, MarvelGame\Marvel) with Binaries\Win64 or WinGDK and the
    /// engine's Engine folder beside it.
    /// </summary>
    internal static List<string> UnrealProjects(string install)
    {
        static bool Project(string dir) => !Path.GetFileName(dir).Equals("Engine", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(Path.Combine(Path.GetDirectoryName(dir)!, "Engine")) &&
            (Directory.Exists(Path.Combine(dir, "Binaries", "Win64")) || Directory.Exists(Path.Combine(dir, "Binaries", "WinGDK")));
        var found = new List<string>();
        foreach (var dir in Dirs(install))
        {
            if (Project(dir)) found.Add(dir);
            else found.AddRange(Dirs(dir).Where(Project));
        }
        return found;
    }

    /// <summary>A project's Saved folders, wherever its game keeps them: %LOCALAPPDATA%, the install, My Games.</summary>
    private static IEnumerable<string> UnrealSaved(string project, GameEntry game)
    {
        var name = Path.GetFileName(project);
        return new[] { Path.Combine(LocalAppData, name), project, Path.Combine(MyGames, name) }
            .Concat(TitleFolders(game).Select(folder => Path.Combine(folder, name)))
            .Select(root => Path.Combine(root, "Saved")).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The project's GameUserSettings.ini that holds its HDR peak (the first that exists); null before the game wrote one.</summary>
    internal static string? UnrealSettingsFile(string project, GameEntry game) =>
        UnrealSaved(project, game).SelectMany(saved => ((string[])["Windows", "WindowsNoEditor", "WinGDK", "WindowsClient"])
            .Select(platform => Path.Combine(saved, "Config", platform, "GameUserSettings.ini"))).FirstOrDefault(File.Exists);

    /// <summary>Every existing <c>Engine.ini</c> under each Unreal project's <c>Saved\Config</c> folders.</summary>
    internal static IEnumerable<string> UnrealEngineIniFiles(GameEntry game) =>
        string.IsNullOrEmpty(game.Path) || !Directory.Exists(game.Path)
            ? []
            : UnrealProjects(game.Path)
                .SelectMany(project => UnrealSaved(project, game))
                .SelectMany(saved => ((string[])["Windows", "WindowsNoEditor", "WinGDK", "WindowsClient"])
                    .Select(platform => Path.Combine(saved, "Config", platform, "Engine.ini")))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase);

    private static List<string> SettingsFiles(GameEntry game)
    {
        var install = game.Path!;
        var files = new List<string>();
        foreach (var project in UnrealProjects(install))
            foreach (var saved in UnrealSaved(project, game))
                foreach (var platform in (string[])["Windows", "WindowsNoEditor", "WinGDK", "WindowsClient"])
                {
                    files.Add(Path.Combine(saved, "Config", platform, "GameUserSettings.ini"));
                    files.Add(Path.Combine(saved, "Config", platform, "Engine.ini"));
                }
        var players = GameSettings.CallOfDutyPlayers;
        foreach (var cod in Dirs(install).Select(Path.GetFileName).Where(name => CallOfDutyFolder().IsMatch(name!)))
        {
            files.AddRange(Files(players, $"s.*.{cod}.txt"));
            foreach (var account in Dirs(players)) files.AddRange(Files(account, $"g.{cod}.*.txt"));
        }
        if (File.Exists(Path.Combine(install, "re_chunk_000.pak"))) files.AddRange(Files(install, "*.ini"));
        foreach (var folder in TitleFolders(game)) files.AddRange(TextFiles(folder, 3));
        return [.. files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Unreal settings saves: Saved\SaveGames (two levels down) files named for settings, options or config.</summary>
    private static List<string> SettingsSaves(GameEntry game)
    {
        var saves = new List<string>();
        foreach (var project in UnrealProjects(game.Path!))
            foreach (var saved in UnrealSaved(project, game))
            {
                var root = Path.Combine(saved, "SaveGames");
                foreach (var dir in Dirs(root).Prepend(root))
                    saves.AddRange(Files(dir, "*.sav").Where(file => SettingsSaveName().IsMatch(Path.GetFileName(file))));
            }
        return saves;
    }

    /// <summary>The game's own folders under My Games, Documents and Saved Games: named for its title or its install folder.</summary>
    internal static List<string> TitleFolders(GameEntry game)
    {
        var names = new[] { Key(game.Title), Key(Path.GetFileName(game.Path)) }.Where(key => key.Length >= 3).ToHashSet(StringComparer.Ordinal);
        var savedGames = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
        return [.. new[] { MyGames, Documents, savedGames }.SelectMany(Dirs).Where(dir => names.Contains(Key(Path.GetFileName(dir))))];
    }

    /// <summary>A name as Exo compares it: letters and digits only, lower case ("Marvel's Spider-Man" is "marvelsspiderman").</summary>
    internal static string Key(string? name) => new([.. (name ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    private static readonly string[] TextExtensions = [".ini", ".xml", ".cfg", ".json", ".txt"];

    /// <summary>Settings-like files in a folder and below (logs, crashes and screenshots left out), at most 60.</summary>
    private static IEnumerable<string> TextFiles(string folder, int depth)
    {
        var found = Files(folder, "*").Where(file => TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) ||
            Path.GetFileName(file).Equals("PROFSAVE_profile", StringComparison.OrdinalIgnoreCase));
        if (depth > 0)
            foreach (var dir in Dirs(folder).Where(dir => Path.GetFileName(dir) is not ("Logs" or "Crashes" or "CrashReportClient" or "Screenshots" or "Shaders")))
                found = found.Concat(TextFiles(dir, depth - 1));
        return found.Take(60);
    }

    // ------------------------------------------------------------------ the registry

    /// <summary>Unity's PlayerPrefs key and every Software\{publisher}\{the game} key, with the keys one level below.</summary>
    private static Signal FromRegistry(GameEntry game)
    {
        var names = new[] { Key(game.Title), Key(Path.GetFileName(game.Path)) }.Where(key => key.Length >= 3).ToHashSet(StringComparer.Ordinal);
        var paths = new List<string>();
        if (UnityKey(game.Path!) is { } unity) paths.Add(unity);
        using (var software = Registry.CurrentUser.OpenSubKey("Software"))
        {
            foreach (var publisher in software?.GetSubKeyNames() ?? [])
            {
                if (publisher is "Microsoft" or "Classes" or "Policies" or "Wow6432Node") continue;
                using var key = software!.OpenSubKey(publisher);
                foreach (var product in key?.GetSubKeyNames() ?? [])
                    if (names.Contains(Key(product))) paths.Add($@"Software\{publisher}\{product}");
            }
        }
        var pairs = new List<(string, string?)>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            if (key is null) continue;
            pairs.AddRange(Values(key));
            foreach (var sub in key.GetSubKeyNames())
            {
                using var inner = key.OpenSubKey(sub);
                if (inner is not null) pairs.AddRange(Values(inner));
            }
        }
        return FromPairs(pairs);
    }

    /// <summary>A key's values as text: a DWORD as its number, a string as itself, a PlayerPrefs string (bytes) as its text.</summary>
    private static IEnumerable<(string, string?)> Values(RegistryKey key) => key.GetValueNames().Select(name => (name, key.GetValue(name) switch
    {
        int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string text => text,
        byte[] bytes when bytes.Length < 64 => Encoding.UTF8.GetString(bytes).TrimEnd('\0'),
        _ => (string?)null,
    }));

    /// <summary>A Unity game's PlayerPrefs key, from {game}_Data\app.info: its company, then its product.</summary>
    internal static string? UnityKey(string install)
    {
        if (!File.Exists(Path.Combine(install, "UnityPlayer.dll"))) return null;
        foreach (var data in Dirs(install).Where(dir => dir.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)))
            if (Small(Path.Combine(data, "app.info"), 4096) is { } bytes &&
                Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.TrimEntries) is [{ Length: > 0 } company, { Length: > 0 } product, ..])
                return $@"Software\{company}\{product}";
        return null;
    }

    // ------------------------------------------------------------------ weak: the game's files

    private static readonly byte[][] Needles =
        [.. ((string[])["ST2084", "ST-2084", "SMPTE2084", "HDR10", "scRGB", "G2084"]).SelectMany(name => new[] { Encoding.ASCII.GetBytes(name), Encoding.Unicode.GetBytes(name) })];

    /// <summary>
    /// The game's own program: an Unreal project's shipping build (Binaries\Win64\*-Shipping.exe), else the biggest program
    /// in the install or one folder down (a launcher, crash reporter or installer is small, and left out by name too).
    /// </summary>
    internal static string? MainProgram(string install)
    {
        var shipping = UnrealProjects(install).SelectMany(project => Files(Path.Combine(project, "Binaries", "Win64"), "*-Shipping.exe")).ToList();
        var programs = shipping.Count > 0 ? shipping : Files(install, "*.exe").Concat(Dirs(install).SelectMany(dir => Files(dir, "*.exe"))).ToList();
        return programs.Where(path => !NotTheGame().IsMatch(Path.GetFileNameWithoutExtension(path)))
            .Select(path => (Path: path, Size: SizeOf(path))).OrderByDescending(p => p.Size).FirstOrDefault().Path;
    }

    [GeneratedRegex(@"crash|unins|setup|redist|launcher|helper|report|install|handler|_be$|_eac$", RegexOptions.IgnoreCase)]
    private static partial Regex NotTheGame();

    /// <summary>True when the program names an HDR format in its first 768 MB.</summary>
    internal static bool ProgramNamesHdr(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            return NamesHdr(stream, 768L << 20);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The scan itself, in 4 MB pieces that overlap by a name's length, up to <paramref name="limit"/> bytes.</summary>
    internal static bool NamesHdr(Stream stream, long limit)
    {
        var longest = Needles.Max(needle => needle.Length);
        var buffer = new byte[(4 << 20) + longest];
        var carry = 0;
        long read = 0;
        while (read < limit)
        {
            var count = stream.Read(buffer, carry, buffer.Length - carry);
            if (count <= 0) return false;
            read += count;
            var span = buffer.AsSpan(0, carry + count);
            foreach (var needle in Needles)
                if (span.IndexOf(needle) >= 0) return true;
            carry = Math.Min(longest - 1, span.Length);
            span[^carry..].CopyTo(buffer);
        }
        return false;
    }

    /// <summary>HDR calibration art the game ships loose (HDRCalibration.png, hdr_calibration_bg.dds), four folders down at most.</summary>
    internal static bool ShipsHdrCalibration(string install)
    {
        var seen = 0;
        bool Look(string dir, int depth)
        {
            foreach (var file in Files(dir, "*"))
                if (++seen > 20_000 || CalibrationArt().IsMatch(Path.GetFileName(file))) return seen <= 20_000;
            return depth > 0 && Dirs(dir).Any(inner => Look(inner, depth - 1));
        }
        return Look(install, 4);
    }

    [GeneratedRegex(@"hdr[ _-]?calib|calib\w*?[ _-]?hdr", RegexOptions.IgnoreCase)]
    private static partial Regex CalibrationArt();

    // ------------------------------------------------------------------ quietly

    private static IEnumerable<string> Dirs(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> Files(string path, string pattern)
    {
        try { return Directory.Exists(path) ? Directory.GetFiles(path, pattern) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The file's bytes when it exists and is at most <paramref name="limit"/> bytes; null otherwise.</summary>
    private static byte[]? Small(string path, int limit)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > limit) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
