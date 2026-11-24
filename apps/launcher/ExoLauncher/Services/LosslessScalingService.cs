using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ExoLauncher.Helpers;
using ExoLauncher.Models;
using Microsoft.Win32;

namespace ExoLauncher.Services;

internal sealed record LosslessScalingInfo(
    bool Installed,
    string? ExePath,
    string? SettingsPath,
    string? Hotkey,
    string StoreUrl = LosslessScalingService.SteamStoreUrl);

internal sealed record LosslessGameEntry(
    string GameId,
    string Title,
    string ExePath,
    string Mode,
    int Multiplier,
    bool ExistedBefore,
    string? PreviousProfileXml);

internal sealed partial class LosslessScalingService
{
    internal const string SteamAppId = "993090";
    internal const string SteamStoreUrl = "https://store.steampowered.com/app/993090/Lossless_Scaling/";
    internal const string DefaultMode = "FIXED";
    internal const int DefaultMultiplier = 2;
    internal const int DefaultAutoScaleDelaySeconds = 5;
    internal const int DefaultAdaptiveTargetFps = 165;

    private static readonly object StoreGate = new();
    private static readonly ConcurrentDictionary<string, bool> StartedForGame = new(StringComparer.OrdinalIgnoreCase);

    internal static LosslessScalingService Shared { get; } = new();

    internal string StatePath { get; init; } = Path.Combine(PathHelper.AppDataDir, "frame-gen", "lossless.json");
    internal Func<string?> SettingsPathProvider { get; init; } = DefaultSettingsPath;
    internal Func<string?> ExePathProvider { get; init; } = FindInstalledExe;
    internal Func<bool> IsProcessRunningFunc { get; init; } = () => Process.GetProcessesByName("LosslessScaling").Length > 0;
    internal Func<string, bool> LaunchProcessFunc { get; init; } = StartMinimizedProcess;
    internal Action CloseProcessAction { get; init; } = CloseGracefully;

    private static string DefaultSettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lossless Scaling", "Settings.xml");

    internal LosslessScalingInfo Detect()
    {
        var exe = ExePathProvider();
        var installed = !string.IsNullOrWhiteSpace(exe) && File.Exists(exe);
        var settingsPath = SettingsPathProvider();
        var hotkey = settingsPath is not null && File.Exists(settingsPath) ? ReadHotkey(settingsPath) : null;
        return new LosslessScalingInfo(installed, installed ? exe : null, settingsPath, hotkey);
    }

    internal LosslessGameEntry? GetGame(string gameId)
    {
        lock (StoreGate) return LoadStore().GetValueOrDefault(gameId);
    }

    internal bool IsEnabled(string gameId) => GetGame(gameId) is not null;

    internal (bool Ok, string Message) EnableGame(GameEntry game, string? exePath, string? mode = null, int? multiplier = null)
    {
        var info = Detect();
        if (!info.Installed) return (false, "Install Lossless Scaling from Steam first.");
        if (string.IsNullOrWhiteSpace(exePath)) return (false, "Exo could not find this game's executable.");
        var normalizedMode = NormalizeMode(mode);
        var normalizedMultiplier = NormalizeMultiplier(multiplier);
        var settingsPath = info.SettingsPath ?? DefaultSettingsPath();
        lock (StoreGate)
        {
            var store = LoadStore();
            var existingEntry = store.GetValueOrDefault(game.Id);
            string? previousXml = existingEntry?.PreviousProfileXml;
            var existedBefore = existingEntry?.ExistedBefore ?? false;
            try
            {
                var doc = LoadOrCreateSettings(settingsPath);
                var profiles = EnsureProfilesElement(doc);
                var match = FindProfile(profiles, exePath);
                if (existingEntry is null)
                {
                    existedBefore = match is not null;
                    previousXml = match?.ToString(SaveOptions.DisableFormatting);
                }
                var built = BuildProfileElement(game.Title, exePath, normalizedMode, normalizedMultiplier);
                if (match is not null) match.ReplaceWith(built);
                else profiles.Add(built);
                SaveXmlAtomically(settingsPath, doc);
                store[game.Id] = new LosslessGameEntry(game.Id, game.Title, exePath, normalizedMode, normalizedMultiplier, existedBefore, previousXml);
                SaveStore(store);
                AppLog.Info($"Lossless Scaling on: gameId={game.Id}; exe={exePath}; mode={normalizedMode}; mult={normalizedMultiplier}x.");
                return (true, $"Lossless Scaling LSFG 3 ({normalizedMultiplier}x) ready.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                AppLog.Warn($"Lossless Scaling enable failed for {game.Title}: {ex.Message}");
                return (false, "Could not update Lossless Scaling settings.");
            }
        }
    }

    internal (bool Ok, string Message) DisableGame(string gameId)
    {
        lock (StoreGate)
        {
            var store = LoadStore();
            if (!store.TryGetValue(gameId, out var entry)) return (true, "Off.");
            var settingsPath = SettingsPathProvider() ?? DefaultSettingsPath();
            try
            {
                if (File.Exists(settingsPath))
                {
                    var doc = XDocument.Load(settingsPath);
                    var profiles = doc.Root?.Element("GameProfiles");
                    if (profiles is not null)
                    {
                        var match = FindProfile(profiles, entry.ExePath);
                        if (entry.ExistedBefore && !string.IsNullOrWhiteSpace(entry.PreviousProfileXml))
                        {
                            var restored = XElement.Parse(entry.PreviousProfileXml);
                            if (match is not null) match.ReplaceWith(restored);
                            else profiles.Add(restored);
                        }
                        else
                        {
                            match?.Remove();
                        }
                        SaveXmlAtomically(settingsPath, doc);
                    }
                }
                store.Remove(gameId);
                SaveStore(store);
                AppLog.Info($"Lossless Scaling off: gameId={gameId}.");
                return (true, "Off.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                AppLog.Warn($"Lossless Scaling disable failed for {gameId}: {ex.Message}");
                return (false, "Could not update Lossless Scaling settings.");
            }
        }
    }

    internal void WatchGames(LaunchOrchestrator launcher)
    {
        launcher.GameLaunching += OnGameLaunching;
        launcher.GameSessionCompleted += OnGameEnded;
        launcher.GameLaunchFailed += OnGameEnded;
    }

    internal void OnGameLaunching(GameEntry game)
    {
        var entry = GetGame(game.Id);
        if (entry is null) return;
        var info = Detect();
        if (!info.Installed || info.ExePath is null) return;
        try
        {
            var settingsPath = info.SettingsPath ?? DefaultSettingsPath();
            var doc = LoadOrCreateSettings(settingsPath);
            var profiles = EnsureProfilesElement(doc);
            var match = FindProfile(profiles, entry.ExePath);
            var built = BuildProfileElement(entry.Title, entry.ExePath, entry.Mode, entry.Multiplier);
            if (match is not null) match.ReplaceWith(built);
            else profiles.Add(built);
            SaveXmlAtomically(settingsPath, doc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            AppLog.Warn($"Lossless Scaling profile sync before launch failed for {game.Title}: {ex.Message}");
        }
        if (IsProcessRunningFunc()) return;
        if (LaunchProcessFunc(info.ExePath))
        {
            StartedForGame[game.Id] = true;
            AppLog.Info($"Lossless Scaling started minimized for {game.Title}.");
        }
    }

    internal void OnGameEnded(GameEntry game)
    {
        if (!StartedForGame.TryRemove(game.Id, out _)) return;
        if (!StartedForGame.IsEmpty) return;
        try
        {
            CloseProcessAction();
            AppLog.Info($"Lossless Scaling closed after {game.Title}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Debug("Lossless Scaling close: " + ex.Message);
        }
    }

    internal static string NormalizeMode(string? mode) =>
        string.Equals(mode, "ADAPTIVE", StringComparison.OrdinalIgnoreCase) ? "ADAPTIVE" : DefaultMode;

    internal static int NormalizeMultiplier(int? multiplier) =>
        multiplier is >= 2 and <= 4 ? multiplier.Value : DefaultMultiplier;

    internal static XDocument LoadOrCreateSettings(string path)
    {
        if (File.Exists(path)) return XDocument.Load(path);
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("Settings",
                new XElement("Hotkey", "S"),
                new XElement("HotkeyModifierKeys", "Alt Control"),
                new XElement("MinimizeToTray", "false"),
                new XElement("CloseToTray", "false"),
                new XElement("GameProfiles")));
    }

    private static XElement EnsureProfilesElement(XDocument doc)
    {
        var root = doc.Root ?? throw new System.Xml.XmlException("Missing root Settings element.");
        var profiles = root.Element("GameProfiles");
        if (profiles is null)
        {
            profiles = new XElement("GameProfiles");
            root.Add(profiles);
        }
        return profiles;
    }

    private static XElement? FindProfile(XElement profiles, string exePath)
    {
        var norm = NormalizeExePath(exePath);
        return profiles.Elements("Profile")
            .FirstOrDefault(p => p.Element("Path")?.Value is { Length: > 0 } existing &&
                                 NormalizeExePath(existing).Equals(norm, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeExePath(string path)
    {
        try { return Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path.Trim(); }
    }

    internal static XElement BuildProfileElement(string title, string exePath, string mode, int multiplier) =>
        new("Profile",
            new XElement("Title", title),
            new XElement("Path", NormalizeExePath(exePath)),
            new XElement("AutoScale", "true"),
            new XElement("AutoScaleDelay", DefaultAutoScaleDelaySeconds.ToString(CultureInfo.InvariantCulture)),
            new XElement("ScalingMode", "Auto"),
            new XElement("ScalingFitMode", "AspectRatio"),
            new XElement("ScaleFactor", "1"),
            new XElement("ResizeBeforeScaling", "false"),
            new XElement("WindowedMode", "false"),
            new XElement("ScalingType", "Off"),
            new XElement("FSRType", "ORIGINAL"),
            new XElement("LS1Type", "BALANCED"),
            new XElement("LSFG2Mode", "X2"),
            new XElement("LSFG3Mode1", mode),
            new XElement("LSFG3Multiplier", multiplier.ToString(CultureInfo.InvariantCulture)),
            new XElement("LSFG3Target", DefaultAdaptiveTargetFps.ToString(CultureInfo.InvariantCulture)),
            new XElement("LSFGFlowScale", "100"),
            new XElement("LSFGSize", "BALANCED"),
            new XElement("Anime4kType", "S"),
            new XElement("Sharpness", "5"),
            new XElement("LS1Sharpness", "1"),
            new XElement("VRS", "false"),
            new XElement("FrameGeneration", "LSFG3"),
            new XElement("ClipCursor", "true"),
            new XElement("AdjustCursorSpeed", "false"),
            new XElement("HideCursor", "false"),
            new XElement("ScaleCursor", "false"),
            new XElement("SyncMode", "DEFAULT"),
            new XElement("MaxFrameLatency", "3"),
            new XElement("GsyncSupport", "true"),
            new XElement("HdrSupport", "false"),
            new XElement("DrawFps", "false"),
            new XElement("CaptureApi", "DXGI"),
            new XElement("QueueTarget", "1"),
            new XElement("PreferredGpuId", "0"),
            new XElement("OutputDisplayId", "0"),
            new XElement("CropInput", "false"),
            new XElement("CropInputLeft", "0"),
            new XElement("CropInputTop", "0"),
            new XElement("CropInputRight", "0"),
            new XElement("CropInputBottom", "0"),
            new XElement("MultiDisplayMode", "false"));

    internal static string? ReadHotkey(string settingsPath)
    {
        try
        {
            var doc = XDocument.Load(settingsPath);
            var key = doc.Root?.Element("Hotkey")?.Value?.Trim();
            if (string.IsNullOrEmpty(key) || key.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;
            var mods = (doc.Root?.Element("HotkeyModifierKeys")?.Value ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(m => !m.Equals("None", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Equals("Control", StringComparison.OrdinalIgnoreCase) ? "Ctrl" : m)
                .ToList();
            mods.Add(key);
            return string.Join("+", mods);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static void SaveXmlAtomically(string path, XDocument doc)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        doc.Save(temp);
        File.Move(temp, path, overwrite: true);
    }

    private Dictionary<string, LosslessGameEntry> LoadStore()
    {
        try
        {
            return File.Exists(StatePath) && JsonSerializer.Deserialize<Dictionary<string, LosslessGameEntry>>(File.ReadAllText(StatePath)) is { } read
                ? new Dictionary<string, LosslessGameEntry>(read, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, LosslessGameEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, LosslessGameEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveStore(Dictionary<string, LosslessGameEntry> store)
    {
        var dir = Path.GetDirectoryName(StatePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(store));
        File.Move(temp, StatePath, overwrite: true);
    }

    internal static string? FindInstalledExe()
    {
        foreach (var library in EnumerateSteamLibraries())
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{SteamAppId}.acf");
            if (File.Exists(manifest))
            {
                try
                {
                    var text = File.ReadAllText(manifest);
                    var dirMatch = InstallDirRegex().Match(text);
                    var folder = dirMatch.Success ? dirMatch.Groups[1].Value : "Lossless Scaling";
                    var exe = Path.Combine(library, "steamapps", "common", folder, "LosslessScaling.exe");
                    if (File.Exists(exe)) return exe;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            var fallback = Path.Combine(library, "steamapps", "common", "Lossless Scaling", "LosslessScaling.exe");
            if (File.Exists(fallback)) return fallback;
        }
        return null;
    }

    internal static IEnumerable<string> EnumerateSteamLibraries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? steamPath = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steamPath = key?.GetValue("SteamPath") as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            var norm = steamPath.Replace('/', '\\');
            if (Directory.Exists(norm) && seen.Add(norm)) yield return norm;
            var vdf = Path.Combine(norm, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                string? content = null;
                try { content = File.ReadAllText(vdf); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (content is not null)
                {
                    foreach (Match match in LibraryPathRegex().Matches(content))
                    {
                        var lib = match.Groups[1].Value.Replace(@"\\", @"\");
                        if (Directory.Exists(lib) && seen.Add(lib)) yield return lib;
                    }
                }
            }
        }
    }

    private static bool StartMinimizedProcess(string exePath)
    {
        try
        {
            var info = new ProcessStartInfo(exePath)
            {
                Arguments = "-StartMinimized",
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                UseShellExecute = true,
            };
            return Process.Start(info) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppLog.Warn("Lossless Scaling did not start: " + ex.Message);
            return false;
        }
    }

    private static void CloseGracefully()
    {
        foreach (var proc in Process.GetProcessesByName("LosslessScaling"))
        {
            try
            {
                proc.CloseMainWindow();
                proc.WaitForExit(3000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { proc.Dispose(); }
        }
    }

    [GeneratedRegex(@"""installdir""\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallDirRegex();

    [GeneratedRegex(@"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LibraryPathRegex();
}
