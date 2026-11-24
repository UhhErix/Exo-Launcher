using System.Text.Json;
using Microsoft.Win32;

namespace ExoLauncher.Adapters;

internal static class EpicClassicManifests
{
    internal sealed record ClassicInstall(
        string AppName,
        string? DisplayName,
        string InstallLocation,
        string? LaunchExecutable,
        string? AppVersion,
        string? CatalogNamespace,
        string? CatalogItemId,
        IReadOnlyList<string> Categories,
        DateTime WrittenUtc);

    internal static string DefaultManifestsDir()
    {
        foreach (var subKey in new[]
                 {
                     @"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher",
                     @"SOFTWARE\Epic Games\EpicGamesLauncher",
                 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey);
                if (key?.GetValue("AppDataPath") is string dataPath && !string.IsNullOrWhiteSpace(dataPath))
                {
                    var candidate = Path.Combine(dataPath, "Manifests");
                    if (Directory.Exists(candidate)) return candidate;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
    }

    internal static string DefaultLauncherInstalledPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");

    internal static IReadOnlyList<ClassicInstall> ReadInstallations(
        string? manifestsDir = null,
        string? launcherInstalledPath = null)
    {
        manifestsDir ??= DefaultManifestsDir();
        launcherInstalledPath ??= DefaultLauncherInstalledPath();

        var results = new List<ClassicInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(manifestsDir) && Directory.Exists(manifestsDir))
        {
            string[] files;
            try { files = Directory.GetFiles(manifestsDir, "*.item"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = []; }

            foreach (var file in files)
            {
                if (TryReadItemFile(file, out var install) && install is not null && seen.Add(install.AppName))
                    results.Add(install);
            }
        }

        if (!string.IsNullOrWhiteSpace(launcherInstalledPath) && File.Exists(launcherInstalledPath))
        {
            foreach (var install in ReadLauncherInstalledDat(launcherInstalledPath))
            {
                if (seen.Add(install.AppName))
                    results.Add(install);
            }
        }

        return results;
    }

    internal static bool IsLayoutRecognized(
        string? v2DataDir,
        string? manifestsDir,
        string? launcherInstalledPath)
    {
        if (!string.IsNullOrWhiteSpace(v2DataDir))
        {
            var jsonl = Path.Combine(v2DataDir, "installations.jsonl");
            if (File.Exists(jsonl) && !IsValidV2Log(jsonl))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(manifestsDir) && Directory.Exists(manifestsDir))
        {
            string[] files;
            try { files = Directory.GetFiles(manifestsDir, "*.item"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = []; }

            if (files.Length > 0 && !files.Any(IsRecognizedItemFile))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(launcherInstalledPath) && File.Exists(launcherInstalledPath))
        {
            if (!IsRecognizedLauncherInstalledDat(launcherInstalledPath))
                return false;
        }

        return true;
    }

    private static bool TryReadItemFile(string path, out ClassicInstall? install)
    {
        install = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var appName = Text(root, "AppName");
            var installLocation = Text(root, "InstallLocation");
            if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(installLocation)) return false;

            if (Bool(root, "bIsIncompleteInstall") == true) return false;
            if (Bool(root, "bIsApplication") == false) return false;

            var mainGame = Text(root, "MainGameAppName");
            if (!string.IsNullOrWhiteSpace(mainGame) && !string.Equals(mainGame, appName, StringComparison.OrdinalIgnoreCase))
                return false;

            var catalogNamespace = Text(root, "CatalogNamespace");
            var categories = ReadCategories(root);
            if (IsNonGame(appName, catalogNamespace, categories)) return false;

            var displayName = Text(root, "DisplayName");
            var launchExecutable = Text(root, "LaunchExecutable");
            var appVersion = Text(root, "AppVersionString");
            var catalogItemId = Text(root, "CatalogItemId");
            var writtenUtc = File.GetLastWriteTimeUtc(path);

            install = new ClassicInstall(
                appName,
                displayName,
                installLocation,
                launchExecutable,
                appVersion,
                catalogNamespace,
                catalogItemId,
                categories,
                writtenUtc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<ClassicInstall> ReadLauncherInstalledDat(string path)
    {
        var results = new List<ClassicInstall>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("InstallationList", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return results;

            var writtenUtc = File.GetLastWriteTimeUtc(path);
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var appName = Text(entry, "AppName") ?? Text(entry, "ArtifactId");
                var installLocation = Text(entry, "InstallLocation");
                if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(installLocation)) continue;

                var ns = Text(entry, "NamespaceId");
                if (IsNonGame(appName, ns, [])) continue;

                results.Add(new ClassicInstall(
                    appName,
                    null,
                    installLocation,
                    null,
                    Text(entry, "AppVersion"),
                    ns,
                    Text(entry, "ItemId"),
                    [],
                    writtenUtc));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return results;
    }

    private static bool IsRecognizedItemFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.ValueKind == JsonValueKind.Object && Text(doc.RootElement, "AppName") is { Length: > 0 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool IsRecognizedLauncherInstalledDat(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0) return true;
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("InstallationList", out var list) &&
                list.ValueKind == JsonValueKind.Array;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool IsValidV2Log(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n');
            var nonHeaderLines = 0;
            var recognized = 0;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object) return false;
                if (e.TryGetProperty("version", out _) && !e.TryGetProperty("installationId", out _)) continue;
                nonHeaderLines++;
                if (Text(e, "installationId") is { Length: > 0 }) recognized++;
            }
            return nonHeaderLines == 0 || recognized > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool IsNonGame(string appName, string? catalogNamespace, IReadOnlyList<string> categories)
    {
        if (string.Equals(catalogNamespace, "ue", StringComparison.OrdinalIgnoreCase) ||
            appName.StartsWith("UE_", StringComparison.OrdinalIgnoreCase))
            return true;

        var hasGames = categories.Any(c => string.Equals(c, "games", StringComparison.OrdinalIgnoreCase));
        if (!hasGames && categories.Any(c =>
                string.Equals(c, "engines", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c, "plugins", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c, "editors", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private static IReadOnlyList<string> ReadCategories(JsonElement root)
    {
        if (!root.TryGetProperty("AppCategories", out var cats) || cats.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var item in cats.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                list.Add(value);
        }
        return list;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}
