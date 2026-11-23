using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using ExoLauncher.Adapters;

namespace ExoLauncher.Helpers;

internal static class LosslessScalingConfig
{
    internal const string SteamAppId = "993090";
    internal const string ExeName = "LosslessScaling.exe";
    private const string DefaultInstallDir = "Lossless Scaling";
    private const string SettingsFileName = "settings.xml";
    private const string ProcessName = "LosslessScaling";

    internal sealed record InstallInfo(bool Installed, string? ExePath, string? SettingsPath);

    internal static InstallInfo DetectLive()
    {
        var steamRoot = SteamInstallation.ResolveRoot();
        string? libraryVdf = null;
        if (steamRoot is not null)
        {
            var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            libraryVdf = SteamPlaytime.ReadSharedText(vdfPath);
        }
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Detect(steamRoot, libraryVdf, localAppData, File.Exists, SteamPlaytime.ReadSharedText);
    }

    internal static InstallInfo Detect(
        string? steamRoot,
        string? libraryFoldersVdf,
        string? localAppData,
        Func<string, bool> fileExists,
        Func<string, string?> readText)
    {
        var settingsPath = string.IsNullOrWhiteSpace(localAppData)
            ? null
            : Path.Combine(localAppData, DefaultInstallDir, SettingsFileName);
        if (string.IsNullOrWhiteSpace(steamRoot))
            return new InstallInfo(false, null, settingsPath);

        var manifestPath = SteamGameProperties.FindManifest(steamRoot, SteamAppId, libraryFoldersVdf, fileExists);
        if (manifestPath is null)
            return new InstallInfo(false, null, settingsPath);

        var installDir = DefaultInstallDir;
        if (readText(manifestPath) is { Length: > 0 } acf)
        {
            var parsed = SteamKeyValues.ParseText(acf).Section("AppState").Value("installdir");
            if (!string.IsNullOrWhiteSpace(parsed)) installDir = parsed;
        }

        var steamAppsDir = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrWhiteSpace(steamAppsDir))
            return new InstallInfo(false, null, settingsPath);

        var exePath = Path.Combine(steamAppsDir, "common", installDir, ExeName);
        var installed = fileExists(exePath);
        return new InstallInfo(installed, installed ? exePath : null, settingsPath);
    }

    internal static bool IsRunning()
    {
        var procs = Process.GetProcessesByName(ProcessName);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    internal static bool HasAutoScaleProfile(string? existingXml, string gameTitle, string? exePathOrName)
    {
        if (string.IsNullOrWhiteSpace(existingXml)) return false;
        try
        {
            var doc = XDocument.Parse(existingXml);
            var profiles = doc.Root?.Element("GameProfiles")?.Elements("Profile");
            if (profiles is null) return false;
            var match = FindProfile(profiles, gameTitle, exePathOrName);
            if (match is null) return false;
            var autoScale = match.Element("AutoScale")?.Value;
            return string.Equals(autoScale, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static string UpsertProfileXml(
        string? existingXml,
        string gameTitle,
        string exePathOrName,
        bool autoScale,
        int autoScaleDelay = 3)
    {
        XDocument doc;
        if (string.IsNullOrWhiteSpace(existingXml))
        {
            doc = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("Settings", new XElement("GameProfiles")));
        }
        else
        {
            doc = XDocument.Parse(existingXml);
            if (doc.Root is null) doc.Add(new XElement("Settings"));
        }

        var root = doc.Root!;
        var profiles = root.Element("GameProfiles");
        if (profiles is null)
        {
            profiles = new XElement("GameProfiles");
            root.Add(profiles);
        }

        var profile = FindProfile(profiles.Elements("Profile"), gameTitle, exePathOrName);
        if (profile is null)
        {
            profile = new XElement("Profile");
            profiles.Add(profile);
        }

        SetElement(profile, "Title", gameTitle);
        SetElement(profile, "Path", exePathOrName);
        SetElement(profile, "AutoScale", autoScale ? "true" : "false");
        EnsureElement(profile, "AutoScaleDelay", Math.Clamp(autoScaleDelay, 1, 30).ToString());
        EnsureElement(profile, "FrameGeneration", "LSFG 3.1");
        EnsureElement(profile, "ScalingType", "Off");

        var sb = new StringBuilder();
        using (var writer = new Utf8StringWriter(sb))
            doc.Save(writer);
        return sb.ToString();
    }

    internal static (bool Ok, string? Error) ConfigureProfile(
        string settingsPath,
        string gameTitle,
        string exePathOrName,
        bool autoScale,
        Func<bool>? isRunning = null)
    {
        if ((isRunning ?? IsRunning)())
            return (false, "Close Lossless Scaling before changing its profile.");

        try
        {
            var dir = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            string? existing = null;
            if (File.Exists(settingsPath))
            {
                existing = File.ReadAllText(settingsPath, Encoding.UTF8);
                var backupPath = settingsPath + ".exo.bak";
                if (!File.Exists(backupPath)) File.Copy(settingsPath, backupPath, overwrite: false);
            }

            var updated = UpsertProfileXml(existing, gameTitle, exePathOrName, autoScale);
            var tempPath = settingsPath + ".exo.tmp";
            File.WriteAllText(tempPath, updated, new UTF8Encoding(false));
            File.Move(tempPath, settingsPath, overwrite: true);
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return (false, "Could not update Lossless Scaling settings: " + ex.Message);
        }
    }

    internal static bool LaunchMinimizedIfNeeded(string? exePath, Func<bool>? isRunning = null)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;
        if ((isRunning ?? IsRunning)()) return true;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized,
            });
            return proc is not null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Lossless Scaling launch failed: " + ex.Message);
            return false;
        }
    }

    private static XElement? FindProfile(IEnumerable<XElement> profiles, string gameTitle, string? exePathOrName)
    {
        var targetName = string.IsNullOrWhiteSpace(exePathOrName) ? null : Path.GetFileName(exePathOrName);
        foreach (var profile in profiles)
        {
            var pathVal = profile.Element("Path")?.Value ?? profile.Element("Filter")?.Value;
            if (!string.IsNullOrWhiteSpace(pathVal) && !string.IsNullOrWhiteSpace(exePathOrName))
            {
                if (string.Equals(pathVal, exePathOrName, StringComparison.OrdinalIgnoreCase))
                    return profile;
                if (targetName is not null && string.Equals(Path.GetFileName(pathVal), targetName, StringComparison.OrdinalIgnoreCase))
                    return profile;
            }
            var titleVal = profile.Element("Title")?.Value;
            if (!string.IsNullOrWhiteSpace(titleVal) && string.Equals(titleVal, gameTitle, StringComparison.OrdinalIgnoreCase))
                return profile;
        }
        return null;
    }

    private static void SetElement(XElement parent, string name, string value)
    {
        var el = parent.Element(name);
        if (el is null) parent.Add(new XElement(name, value));
        else el.Value = value;
    }

    private static void EnsureElement(XElement parent, string name, string value)
    {
        if (parent.Element(name) is null) parent.Add(new XElement(name, value));
    }

    private sealed class Utf8StringWriter(StringBuilder sb) : StringWriter(sb)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
