using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal static partial class GameSettings
{
    private const string MinecraftJavaId = "minecraft";
    private const string ModrinthApiBase = "https://api.modrinth.com/v2/project/";
    private const string ModrinthCdnHost = "cdn.modrinth.com";
    private static readonly string[] MinecraftPerfModSlugs = ["sodium", "lithium", "ferrite-core"];

    private static string MinecraftJvmBackupPath => Path.Combine(Folder, "minecraft-jvm.json");
    private static string MinecraftModsLedgerPath => Path.Combine(Folder, "minecraft-mods.json");

    internal sealed record MinecraftRoots(
        string? VanillaRoot = null,
        string? PrismInstancesRoot = null,
        string? CurseForgeInstancesRoot = null,
        string? ModrinthProfilesRoot = null);

    internal sealed record MinecraftInstance(
        string Id,
        string Name,
        string Launcher,
        string GameDir,
        string OptionsPath,
        string? GameVersion,
        string Loader,
        bool IsFabric,
        string? ModsDir,
        string? JvmConfigPath,
        string? JvmProfileKey);

    internal sealed record MinecraftModRecord(
        string Slug,
        string FileName,
        string Sha512,
        string VersionId,
        DateTime InstalledAtUtc);

    internal sealed record MinecraftModActionResult(
        bool Ok,
        string Message,
        IReadOnlyList<string> Installed,
        IReadOnlyList<string> Removed);

    private sealed class MinecraftJvmBackupDoc
    {
        public Dictionary<string, MinecraftJvmBackupEntry> Instances { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record MinecraftJvmBackupEntry(
        string Launcher,
        string ConfigPath,
        string? ProfileKey,
        string OriginalContentBase64,
        string AppliedArgs,
        int AppliedHeapGb);

    private sealed class MinecraftModsLedgerDoc
    {
        public Dictionary<string, MinecraftModsInstanceRecord> Instances { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class MinecraftModsInstanceRecord
    {
        public string ModsDir { get; set; } = "";
        public string? GameVersion { get; set; }
        public List<MinecraftModRecord> Mods { get; set; } = [];
    }

    [GeneratedRegex(@"^(?<name>[A-Za-z0-9_.]+):(?<value>.*)$")]
    private static partial Regex MinecraftOptionLine();

    [GeneratedRegex(@"(?<!\d)(\d+\.\d+(?:\.\d+)?)(?!\d)")]
    private static partial Regex MinecraftVersionPattern();

    private static readonly Dictionary<string, string> MinecraftOptionKeys = new(StringComparer.Ordinal)
    {
        ["fullscreen"] = Toggle,
        ["enableVsync"] = Toggle,
        ["maxFps"] = "10 to 260",
        ["graphicsMode"] = "one of 0, 1, 2",
        ["renderClouds"] = "one of \"false\", \"fast\", \"true\", false, fast, true",
        ["particles"] = "one of 0, 1, 2",
        ["entityShadows"] = Toggle,
        ["ao"] = Toggle,
        ["biomeBlendRadius"] = "0 to 7",
        ["mipmapLevels"] = "0 to 4",
        ["renderDistance"] = "2 to 32",
        ["simulationDistance"] = "5 to 32",
        ["entityDistanceScaling"] = "0.5 to 5.0",
    };

    internal static MinecraftRoots DefaultMinecraftRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var modrinth = Path.Combine(appData, "ModrinthApp", "profiles");
        if (!Directory.Exists(modrinth))
        {
            var legacy = Path.Combine(appData, "com.modrinth.theseus", "profiles");
            if (Directory.Exists(legacy)) modrinth = legacy;
        }
        return new(
            Path.Combine(appData, ".minecraft"),
            Path.Combine(appData, "PrismLauncher", "instances"),
            Path.Combine(userProfile, "curseforge", "minecraft", "Instances"),
            modrinth);
    }

    internal static bool IsMinecraftJava(GameEntry game)
    {
        if (game.Id.Equals("minecraft:bedrock", StringComparison.OrdinalIgnoreCase)) return false;
        if (game.Id.Equals("minecraft:java", StringComparison.OrdinalIgnoreCase)) return true;
        if (game.Store == StoreKind.Minecraft && !string.IsNullOrEmpty(game.Path)) return true;
        if (string.IsNullOrEmpty(game.Path)) return false;
        return File.Exists(Path.Combine(game.Path, "options.txt"))
            || File.Exists(Path.Combine(game.Path, "launcher_profiles.json"));
    }

    internal static bool IsMinecraftServerDirectory(string dir) =>
        !string.IsNullOrEmpty(dir)
        && File.Exists(Path.Combine(dir, "server.properties"))
        && !File.Exists(Path.Combine(dir, "options.txt"))
        && !File.Exists(Path.Combine(dir, "launcher_profiles.json"));

    internal static IReadOnlyList<MinecraftInstance> DiscoverMinecraftInstances(MinecraftRoots? roots = null)
    {
        var r = roots ?? DefaultMinecraftRoots();
        var instances = new List<MinecraftInstance>();
        DiscoverVanillaInstances(r.VanillaRoot, instances);
        DiscoverPrismInstances(r.PrismInstancesRoot, instances);
        DiscoverCurseForgeInstances(r.CurseForgeInstancesRoot, instances);
        DiscoverModrinthInstances(r.ModrinthProfilesRoot, instances);
        return instances;
    }

    private static void DiscoverVanillaInstances(string? root, List<MinecraftInstance> output)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root) || IsMinecraftServerDirectory(root)) return;
        var profilesPath = Path.Combine(root, "launcher_profiles.json");
        var added = false;
        if (File.Exists(profilesPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(profilesPath));
                if (doc.RootElement.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in profiles.EnumerateObject())
                    {
                        var item = prop.Value;
                        var name = ReadJsonStr(item, "name") ?? prop.Name;
                        var lastVersion = ReadJsonStr(item, "lastVersionId") ?? "";
                        var gameDir = ReadJsonStr(item, "gameDir");
                        var resolvedDir = string.IsNullOrWhiteSpace(gameDir) ? root : gameDir;
                        if (IsMinecraftServerDirectory(resolvedDir)) continue;
                        var (loader, version) = ParseVanillaVersion(lastVersion);
                        var isFabric = string.Equals(loader, "fabric", StringComparison.OrdinalIgnoreCase);
                        output.Add(new(
                            $"vanilla:{prop.Name}",
                            string.IsNullOrWhiteSpace(name) ? "Minecraft Java" : name,
                            "Vanilla",
                            resolvedDir,
                            Path.Combine(resolvedDir, "options.txt"),
                            version,
                            loader,
                            isFabric,
                            Path.Combine(resolvedDir, "mods"),
                            profilesPath,
                            prop.Name));
                        added = true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        if (!added && File.Exists(Path.Combine(root, "options.txt")))
        {
            output.Add(new(
                "vanilla:default",
                "Minecraft Java",
                "Vanilla",
                root,
                Path.Combine(root, "options.txt"),
                null,
                "vanilla",
                false,
                Path.Combine(root, "mods"),
                File.Exists(profilesPath) ? profilesPath : null,
                null));
        }
    }

    private static (string Loader, string? Version) ParseVanillaVersion(string lastVersionId)
    {
        if (string.IsNullOrWhiteSpace(lastVersionId)) return ("vanilla", null);
        var loader = lastVersionId.Contains("fabric", StringComparison.OrdinalIgnoreCase) ? "fabric"
            : lastVersionId.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ? "neoforge"
            : lastVersionId.Contains("forge", StringComparison.OrdinalIgnoreCase) ? "forge"
            : lastVersionId.Contains("quilt", StringComparison.OrdinalIgnoreCase) ? "quilt"
            : "vanilla";
        var matches = MinecraftVersionPattern().Matches(lastVersionId);
        string? version = null;
        if (matches.Count > 0)
            version = matches[^1].Groups[1].Value;
        return (loader, version);
    }

    private static void DiscoverPrismInstances(string? root, List<MinecraftInstance> output)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var cfgPath = Path.Combine(dir, "instance.cfg");
                var packPath = Path.Combine(dir, "mmc-pack.json");
                if (!File.Exists(cfgPath) && !File.Exists(packPath)) continue;
                var name = Path.GetFileName(dir);
                if (File.Exists(cfgPath))
                {
                    foreach (var line in File.ReadAllLines(cfgPath))
                    {
                        if (line.StartsWith("name=", StringComparison.OrdinalIgnoreCase) && line.Length > 5)
                            name = line[5..].Trim();
                    }
                }
                var loader = "vanilla";
                string? version = null;
                if (File.Exists(packPath))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(packPath));
                        if (doc.RootElement.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var comp in comps.EnumerateArray())
                            {
                                var uid = ReadJsonStr(comp, "uid") ?? "";
                                if (uid.Equals("net.minecraft", StringComparison.OrdinalIgnoreCase))
                                    version = ReadJsonStr(comp, "version");
                                else if (uid.Equals("net.fabricmc.fabric-loader", StringComparison.OrdinalIgnoreCase))
                                    loader = "fabric";
                                else if (uid.Equals("net.neoforged", StringComparison.OrdinalIgnoreCase))
                                    loader = "neoforge";
                                else if (uid.Equals("net.minecraftforge", StringComparison.OrdinalIgnoreCase))
                                    loader = "forge";
                                else if (uid.Equals("org.quiltmc.quilt-loader", StringComparison.OrdinalIgnoreCase))
                                    loader = "quilt";
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
                }
                var gameDir = Directory.Exists(Path.Combine(dir, ".minecraft"))
                    ? Path.Combine(dir, ".minecraft")
                    : Directory.Exists(Path.Combine(dir, "minecraft"))
                        ? Path.Combine(dir, "minecraft")
                        : Path.Combine(dir, ".minecraft");
                if (IsMinecraftServerDirectory(gameDir)) continue;
                output.Add(new(
                    $"prism:{Path.GetFileName(dir)}",
                    name,
                    "Prism",
                    gameDir,
                    Path.Combine(gameDir, "options.txt"),
                    version,
                    loader,
                    string.Equals(loader, "fabric", StringComparison.OrdinalIgnoreCase),
                    Path.Combine(gameDir, "mods"),
                    File.Exists(cfgPath) ? cfgPath : null,
                    null));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void DiscoverCurseForgeInstances(string? root, List<MinecraftInstance> output)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var metaPath = Path.Combine(dir, "minecraftinstance.json");
                if (!File.Exists(metaPath) || IsMinecraftServerDirectory(dir)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                    var rootEl = doc.RootElement;
                    var name = ReadJsonStr(rootEl, "name") ?? Path.GetFileName(dir);
                    string? version = ReadJsonStr(rootEl, "gameVersion");
                    var loader = "vanilla";
                    if (rootEl.TryGetProperty("baseModLoader", out var ml) && ml.ValueKind == JsonValueKind.Object)
                    {
                        version ??= ReadJsonStr(ml, "minecraftVersion");
                        var mlName = ReadJsonStr(ml, "name") ?? "";
                        var mlType = ml.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : -1;
                        if (mlName.Contains("fabric", StringComparison.OrdinalIgnoreCase) || mlType == 4) loader = "fabric";
                        else if (mlName.Contains("neoforge", StringComparison.OrdinalIgnoreCase) || mlType == 6) loader = "neoforge";
                        else if (mlName.Contains("forge", StringComparison.OrdinalIgnoreCase) || mlType == 1) loader = "forge";
                        else if (mlName.Contains("quilt", StringComparison.OrdinalIgnoreCase) || mlType == 5) loader = "quilt";
                    }
                    output.Add(new(
                        $"curseforge:{Path.GetFileName(dir)}",
                        name,
                        "CurseForge",
                        dir,
                        Path.Combine(dir, "options.txt"),
                        version,
                        loader,
                        string.Equals(loader, "fabric", StringComparison.OrdinalIgnoreCase),
                        Path.Combine(dir, "mods"),
                        metaPath,
                        null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void DiscoverModrinthInstances(string? root, List<MinecraftInstance> output)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var profilePath = Path.Combine(dir, "profile.json");
                if (!File.Exists(profilePath) || IsMinecraftServerDirectory(dir)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));
                    var rootEl = doc.RootElement;
                    var name = ReadJsonStr(rootEl, "name") ?? Path.GetFileName(dir);
                    var version = ReadJsonStr(rootEl, "game_version");
                    var loader = (ReadJsonStr(rootEl, "loader") ?? "vanilla").ToLowerInvariant();
                    output.Add(new(
                        $"modrinth:{Path.GetFileName(dir)}",
                        name,
                        "Modrinth",
                        dir,
                        Path.Combine(dir, "options.txt"),
                        version,
                        loader,
                        string.Equals(loader, "fabric", StringComparison.OrdinalIgnoreCase),
                        Path.Combine(dir, "mods"),
                        profilePath,
                        null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string? ReadJsonStr(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

    internal static SettingsFile ParseMinecraftOptions(byte[] bytes)
    {
        var latin1 = Encoding.Latin1;
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..]) return new SettingsFile([latin1.GetString(bytes)], "\n", false, [], latin1);
        var bom = bytes is [0xEF, 0xBB, 0xBF, ..];
        var text = latin1.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(newLine);
        var settings = new List<Line>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (MinecraftOptionLine().Match(lines[i]) is not { Success: true } m || !MinecraftOptionKeys.TryGetValue(m.Groups["name"].Value, out var allowed)) continue;
            var value = m.Groups["value"];
            settings.Add(new Line(i, m.Groups["name"].Value, m.Groups["name"].Value, value.Value,
                allowed == Toggle ? ToggleChoices(value.Value) : allowed, value.Index, value.Length));
        }
        return new SettingsFile(lines, newLine, bom, settings, latin1);
    }

    internal static Profile MinecraftJava(Pc pc, MinecraftRoots? roots = null)
    {
        var instances = DiscoverMinecraftInstances(roots);
        var existing = instances.Select(i => i.OptionsPath).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
        var primary = existing.Count > 0
            ? existing[0]
            : instances.FirstOrDefault()?.OptionsPath ?? Path.Combine(DefaultMinecraftRoots().VanillaRoot!, "options.txt");
        Func<IReadOnlyList<string>>? extras = () =>
            [.. DiscoverMinecraftInstances(roots).Select(i => i.OptionsPath).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => File.Exists(path) && !string.Equals(path, primary, StringComparison.OrdinalIgnoreCase))];
        return new(
            MinecraftJavaId,
            "Minecraft Java",
            primary,
            file => MinecraftJavaGroups(pc, file),
            ["javaw", "java", "MinecraftLauncher"],
            PlayerFiles: extras,
            Format: ParseMinecraftOptions,
            WindowMode: pc.Fullscreen ? Fullscreen : Borderless,
            WindowLines: ["fullscreen"],
            Advice: _ => MinecraftAdvice(pc, instances));
    }

    internal static IReadOnlyList<Group> MinecraftJavaGroups(Pc pc, SettingsFile? file = null)
    {
        var strong = Strong(pc.Gpu);
        var highRam = pc.RamGb >= 16;
        var midRam = pc.RamGb is >= 12 or 0;
        var cloudsValue = file is not null && ValueOf(file, "renderClouds") is { } currentClouds && currentClouds.StartsWith('"')
            ? "\"false\""
            : "false";
        var renderDistance = (strong && highRam) ? "16" : (pc.VideoMemoryGb is >= 6 && midRam) ? "12" : "8";
        var simulationDistance = (pc.X3d || pc.RenderThreads is >= 7) ? "10" : pc.RenderThreads is >= 5 ? "8" : "6";
        var graphicsMode = (strong && pc.VideoMemoryGb is > 8.5) ? "1" : "0";
        var mipmaps = pc.VideoMemoryGb is >= 4 ? "4" : "2";
        var aoValue = pc.VideoMemoryGb is <= 4.5 ? "false" : "true";
        var entityScale = strong ? "1.0" : "0.75";

        List<Group> groups =
        [
            new("display", pc.Fullscreen ? "Fullscreen" : "Windowed / Borderless",
                [new("fullscreen", pc.Fullscreen ? "true" : "false")]),
            new("latency", "V-Sync off",
                [new("enableVsync", "false")]),
            new("graphics", "Balanced view distance and low-stutter rendering",
            [
                new("graphicsMode", graphicsMode),
                new("renderClouds", cloudsValue),
                new("particles", "1"),
                new("entityShadows", "false"),
                new("ao", aoValue),
                new("biomeBlendRadius", "1"),
                new("mipmapLevels", mipmaps),
                new("renderDistance", renderDistance),
                new("simulationDistance", simulationDistance, Spare: true),
                new("entityDistanceScaling", entityScale, Spare: true),
            ]),
        ];

        if (VrrFrameCap(pc) is { } cap)
        {
            var clamped = Math.Clamp(cap, 30, 250).ToString(CultureInfo.InvariantCulture);
            groups.Add(new("framecap", $"Frame cap at {clamped} FPS for VRR", [new("maxFps", clamped)]));
        }

        return groups;
    }

    internal static int ComputeMinecraftHeapGb(int ramGb)
    {
        if (ramGb <= 0) return 2;
        if (ramGb < 4) return 2;
        var half = ramGb / 2;
        return ramGb >= 24 ? Math.Min(6, half) : Math.Clamp(half, 2, 4);
    }

    internal static string BuildMinecraftJvmArgs(int ramGb)
    {
        var heap = ComputeMinecraftHeapGb(ramGb);
        return $"-Xms{heap}G -Xmx{heap}G -XX:+UseG1GC -XX:+ParallelRefProcEnabled -XX:MaxGCPauseMillis=200 "
            + "-XX:+UnlockExperimentalVMOptions -XX:+DisableExplicitGC -XX:G1NewSizePercent=30 "
            + "-XX:G1MaxNewSizePercent=40 -XX:G1HeapRegionSize=8M -XX:G1ReservePercent=20 "
            + "-XX:G1HeapWastePercent=5 -XX:G1MixedGCCountTarget=4 -XX:InitiatingHeapOccupancyPercent=15 "
            + "-XX:G1MixedGCLiveThresholdPercent=90 -XX:G1RSetUpdatingPauseTimePercent=5 "
            + "-XX:SurvivorRatio=32 -XX:+PerfDisableSharedMem -XX:MaxTenuringThreshold=1";
    }

    internal static bool IsMinecraftJvmApplied(string instanceId)
    {
        lock (Gate) return LoadMinecraftJvmBackups().Instances.ContainsKey(instanceId);
    }

    internal static bool ApplyMinecraftJvm(MinecraftInstance instance, int ramGb)
    {
        lock (Gate)
        {
            if (IsMinecraftBusy() || string.IsNullOrEmpty(instance.JvmConfigPath) || !File.Exists(instance.JvmConfigPath)) return false;
            if (IsMinecraftServerDirectory(instance.GameDir)) return false;
            var heap = ComputeMinecraftHeapGb(ramGb);
            var args = BuildMinecraftJvmArgs(ramGb);
            var heapMb = heap * 1024;
            try
            {
                var originalBytes = File.ReadAllBytes(instance.JvmConfigPath);
                var backups = LoadMinecraftJvmBackups();
                if (!backups.Instances.ContainsKey(instance.Id))
                {
                    backups.Instances[instance.Id] = new(
                        instance.Launcher,
                        instance.JvmConfigPath,
                        instance.JvmProfileKey,
                        Convert.ToBase64String(originalBytes),
                        args,
                        heap);
                    SaveMinecraftJvmBackups(backups);
                }
                byte[] updated;
                switch (instance.Launcher)
                {
                    case "Vanilla":
                        updated = PatchVanillaJvm(originalBytes, instance.JvmProfileKey, args);
                        break;
                    case "Prism":
                        updated = PatchPrismJvm(originalBytes, heapMb, args);
                        break;
                    case "CurseForge":
                        updated = PatchCurseForgeJvm(originalBytes, heapMb, args);
                        break;
                    case "Modrinth":
                        updated = PatchModrinthJvm(originalBytes, heapMb, args);
                        break;
                    default:
                        return false;
                }
                FileWriter(instance.JvmConfigPath, updated);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                AppLog.Warn($"Minecraft JVM: could not update {instance.Name}: {ex.GetType().Name}");
                return false;
            }
        }
    }

    internal static bool UndoMinecraftJvm(MinecraftInstance instance)
    {
        lock (Gate)
        {
            if (IsMinecraftBusy()) return false;
            var backups = LoadMinecraftJvmBackups();
            if (!backups.Instances.TryGetValue(instance.Id, out var entry)) return true;
            if (string.IsNullOrEmpty(entry.ConfigPath) || !File.Exists(entry.ConfigPath))
            {
                backups.Instances.Remove(instance.Id);
                SaveMinecraftJvmBackups(backups);
                return true;
            }
            try
            {
                FileWriter(entry.ConfigPath, Convert.FromBase64String(entry.OriginalContentBase64));
                backups.Instances.Remove(instance.Id);
                SaveMinecraftJvmBackups(backups);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                AppLog.Warn($"Minecraft JVM: could not undo {instance.Name}: {ex.GetType().Name}");
                return false;
            }
        }
    }

    private static readonly JsonSerializerOptions MinecraftJsonWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static byte[] PatchVanillaJvm(byte[] originalBytes, string? profileKey, string args)
    {
        var node = JsonNode.Parse(Encoding.UTF8.GetString(originalBytes))?.AsObject()
            ?? throw new JsonException("Invalid launcher_profiles.json");
        var profiles = node["profiles"]?.AsObject() ?? throw new JsonException("Missing profiles");
        var key = profileKey ?? profiles.Select(p => p.Key).FirstOrDefault() ?? throw new JsonException("No profile");
        if (profiles[key] is JsonObject profile)
            profile["javaArgs"] = args;
        return Encoding.UTF8.GetBytes(node.ToJsonString(MinecraftJsonWriteOptions));
    }

    private static byte[] PatchPrismJvm(byte[] originalBytes, int heapMb, string args)
    {
        var text = Encoding.UTF8.GetString(originalBytes);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(newLine).ToList();
        SetCfgKey(lines, "OverrideMemory", "true");
        SetCfgKey(lines, "MinMemAlloc", heapMb.ToString(CultureInfo.InvariantCulture));
        SetCfgKey(lines, "MaxMemAlloc", heapMb.ToString(CultureInfo.InvariantCulture));
        SetCfgKey(lines, "OverrideJavaArgs", "true");
        SetCfgKey(lines, "JvmArgs", args);
        return Encoding.UTF8.GetBytes(string.Join(newLine, lines));
    }

    private static void SetCfgKey(List<string> lines, string key, string value)
    {
        var prefix = key + "=";
        var index = lines.FindIndex(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) lines[index] = prefix + value;
        else lines.Add(prefix + value);
    }

    private static byte[] PatchCurseForgeJvm(byte[] originalBytes, int heapMb, string args)
    {
        var node = JsonNode.Parse(Encoding.UTF8.GetString(originalBytes))?.AsObject()
            ?? throw new JsonException("Invalid minecraftinstance.json");
        node["allocatedMemory"] = heapMb;
        node["javaArgsOverride"] = args;
        return Encoding.UTF8.GetBytes(node.ToJsonString(MinecraftJsonWriteOptions));
    }

    private static byte[] PatchModrinthJvm(byte[] originalBytes, int heapMb, string args)
    {
        var node = JsonNode.Parse(Encoding.UTF8.GetString(originalBytes))?.AsObject()
            ?? throw new JsonException("Invalid profile.json");
        var memory = node["memory"] as JsonObject ?? new JsonObject();
        memory["maximum"] = heapMb;
        node["memory"] = memory;
        var java = node["java"] as JsonObject ?? new JsonObject();
        var array = new JsonArray();
        foreach (var arg in args.Split(' ', StringSplitOptions.RemoveEmptyEntries)) array.Add(arg);
        java["extra_arguments"] = array;
        node["java"] = java;
        return Encoding.UTF8.GetBytes(node.ToJsonString(MinecraftJsonWriteOptions));
    }

    private static MinecraftJvmBackupDoc LoadMinecraftJvmBackups()
    {
        try
        {
            if (!File.Exists(MinecraftJvmBackupPath)) return new MinecraftJvmBackupDoc();
            return JsonSerializer.Deserialize<MinecraftJvmBackupDoc>(File.ReadAllText(MinecraftJvmBackupPath)) ?? new MinecraftJvmBackupDoc();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new MinecraftJvmBackupDoc();
        }
    }

    private static void SaveMinecraftJvmBackups(MinecraftJvmBackupDoc doc)
    {
        Directory.CreateDirectory(Folder);
        var tmp = MinecraftJvmBackupPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc));
        File.Move(tmp, MinecraftJvmBackupPath, overwrite: true);
    }

    internal static IReadOnlyList<MinecraftModRecord> InstalledMinecraftMods(string instanceId)
    {
        lock (Gate)
        {
            var ledger = LoadMinecraftModsLedger();
            if (!ledger.Instances.TryGetValue(instanceId, out var rec)) return [];
            return [.. rec.Mods.Where(m => File.Exists(Path.Combine(rec.ModsDir, m.FileName)))];
        }
    }

    internal static async Task<MinecraftModActionResult> InstallMinecraftPerformanceModsAsync(
        MinecraftInstance instance,
        HttpMessageHandler? handler = null,
        CancellationToken ct = default)
    {
        if (IsMinecraftBusy())
            return new(false, "Close Minecraft first.", [], []);
        if (!instance.IsFabric || !string.Equals(instance.Loader, "fabric", StringComparison.OrdinalIgnoreCase))
            return new(false, "Sodium, Lithium and FerriteCore can only be installed on Fabric instances.", [], []);
        if (string.IsNullOrWhiteSpace(instance.GameVersion))
            return new(false, "Could not detect the Minecraft version for this Fabric instance.", [], []);
        if (IsMinecraftServerDirectory(instance.GameDir) || string.IsNullOrEmpty(instance.ModsDir))
            return new(false, "Exo never modifies Minecraft servers.", [], []);
        if (!IsSafeModsDir(instance.GameDir, instance.ModsDir))
            return new(false, "The instance's mods folder is outside its game directory or is a link.", [], []);

        using var http = handler is null
            ? ExoHttp.Create(TimeSpan.FromSeconds(30))
            : ExoHttp.Create(TimeSpan.FromSeconds(30), handler, disposeHandler: false);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ExoLauncher/1.0 (getexo.app)");

        Directory.CreateDirectory(instance.ModsDir);
        var installed = new List<string>();
        var records = new List<MinecraftModRecord>();
        var stagedPaths = new List<string>();

        try
        {
            foreach (var slug in MinecraftPerfModSlugs)
            {
                var query = $"{ModrinthApiBase}{slug}/version?loaders=%5B%22fabric%22%5D&game_versions=%5B%22{Uri.EscapeDataString(instance.GameVersion)}%22%5D";
                using var versionResp = await http.GetAsync(query, ct).ConfigureAwait(false);
                if (!versionResp.IsSuccessStatusCode)
                    return RollbackStaged(stagedPaths, $"Modrinth did not return a Fabric {instance.GameVersion} build for {slug}.");
                var json = await versionResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (PickModrinthFile(json, instance.GameVersion) is not { } picked)
                    return RollbackStaged(stagedPaths, $"No compatible Fabric {instance.GameVersion} release found for {slug}.");
                if (!IsSafeModFileName(picked.FileName))
                    return RollbackStaged(stagedPaths, $"Rejected unsafe mod filename for {slug}.");
                if (!Uri.TryCreate(picked.Url, UriKind.Absolute, out var downloadUri)
                    || !downloadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    || !downloadUri.Host.Equals(ModrinthCdnHost, StringComparison.OrdinalIgnoreCase))
                    return RollbackStaged(stagedPaths, $"Rejected non-Modrinth download host for {slug}.");

                using var fileResp = await http.GetAsync(downloadUri, ct).ConfigureAwait(false);
                if (!fileResp.IsSuccessStatusCode)
                    return RollbackStaged(stagedPaths, $"Failed to download {slug} from Modrinth.");
                var jarBytes = await fileResp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                var actualSha512 = Convert.ToHexString(SHA512.HashData(jarBytes)).ToLowerInvariant();
                if (!string.Equals(actualSha512, picked.Sha512, StringComparison.OrdinalIgnoreCase))
                    return RollbackStaged(stagedPaths, $"Hash check failed for {slug}; nothing was installed.");

                var destPath = Path.Combine(instance.ModsDir, picked.FileName);
                var tmpPath = destPath + ".exo-tmp";
                await File.WriteAllBytesAsync(tmpPath, jarBytes, ct).ConfigureAwait(false);
                File.Move(tmpPath, destPath, overwrite: true);
                stagedPaths.Add(destPath);
                installed.Add(slug);
                records.Add(new(slug, picked.FileName, actualSha512, picked.VersionId, DateTime.UtcNow));
            }

            lock (Gate)
            {
                var ledger = LoadMinecraftModsLedger();
                if (!ledger.Instances.TryGetValue(instance.Id, out var existing))
                    ledger.Instances[instance.Id] = existing = new MinecraftModsInstanceRecord { ModsDir = instance.ModsDir, GameVersion = instance.GameVersion };
                existing.ModsDir = instance.ModsDir;
                existing.GameVersion = instance.GameVersion;
                foreach (var rec in records)
                {
                    existing.Mods.RemoveAll(m => m.Slug.Equals(rec.Slug, StringComparison.OrdinalIgnoreCase));
                    existing.Mods.Add(rec);
                }
                SaveMinecraftModsLedger(ledger);
            }

            return new(true, $"Installed {string.Join(", ", installed)} into {instance.Name}.", installed, []);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or JsonException)
        {
            return RollbackStaged(stagedPaths, $"Could not install performance mods: {ex.Message}");
        }
    }

    internal static MinecraftModActionResult UndoMinecraftPerformanceMods(string instanceId)
    {
        lock (Gate)
        {
            if (IsMinecraftBusy())
                return new(false, "Close Minecraft first.", [], []);
            var ledger = LoadMinecraftModsLedger();
            if (!ledger.Instances.TryGetValue(instanceId, out var rec))
                return new(true, "No Exo-installed performance mods found for this instance.", [], []);
            var removed = new List<string>();
            var remaining = new List<MinecraftModRecord>();
            foreach (var mod in rec.Mods)
            {
                var full = Path.Combine(rec.ModsDir, mod.FileName);
                if (!File.Exists(full)) continue;
                try
                {
                    var currentSha512 = Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(full))).ToLowerInvariant();
                    if (!string.Equals(currentSha512, mod.Sha512, StringComparison.OrdinalIgnoreCase))
                    {
                        remaining.Add(mod);
                        continue;
                    }
                    DeletionGate.DeleteFile(full);
                    removed.Add(mod.Slug);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    remaining.Add(mod);
                }
            }
            if (remaining.Count == 0) ledger.Instances.Remove(instanceId);
            else rec.Mods = remaining;
            SaveMinecraftModsLedger(ledger);
            return new(remaining.Count == 0,
                removed.Count > 0 ? $"Removed {string.Join(", ", removed)}." : "Nothing removed.",
                [],
                removed);
        }
    }

    private sealed record ModrinthPickedFile(string VersionId, string FileName, string Url, string Sha512);

    private static ModrinthPickedFile? PickModrinthFile(string json, string gameVersion)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        ModrinthPickedFile? fallback = null;
        foreach (var version in doc.RootElement.EnumerateArray())
        {
            if (!JsonArrayContains(version, "loaders", "fabric") || !JsonArrayContains(version, "game_versions", gameVersion)) continue;
            var versionId = ReadJsonStr(version, "id") ?? "";
            var versionType = ReadJsonStr(version, "version_type") ?? "";
            if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
            JsonElement? chosen = null;
            foreach (var file in files.EnumerateArray())
            {
                var fn = ReadJsonStr(file, "filename") ?? "";
                if (!fn.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True) { chosen = file; break; }
                chosen ??= file;
            }
            if (chosen is not { } f) continue;
            var fileName = ReadJsonStr(f, "filename");
            var url = ReadJsonStr(f, "url");
            var sha512 = f.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object ? ReadJsonStr(hashes, "sha512") : null;
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(sha512)) continue;
            var candidate = new ModrinthPickedFile(versionId, fileName, url, sha512);
            if (versionType.Equals("release", StringComparison.OrdinalIgnoreCase)) return candidate;
            fallback ??= candidate;
        }
        return fallback;
    }

    private static bool JsonArrayContains(JsonElement obj, string propName, string expected)
    {
        if (!obj.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && string.Equals(item.GetString(), expected, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool IsSafeModFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        && fileName.IndexOfAny(['\\', '/', ':']) < 0
        && !fileName.Contains("..", StringComparison.Ordinal);

    private static bool IsSafeModsDir(string gameDir, string modsDir)
    {
        try
        {
            var fullGame = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir)) + Path.DirectorySeparatorChar;
            var fullMods = Path.GetFullPath(modsDir);
            if (!fullMods.StartsWith(fullGame, StringComparison.OrdinalIgnoreCase)) return false;
            if (Directory.Exists(fullMods) && new DirectoryInfo(fullMods).Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) { return false; }
    }

    private static MinecraftModActionResult RollbackStaged(List<string> stagedPaths, string message)
    {
        foreach (var path in stagedPaths)
        {
            try { if (File.Exists(path)) DeletionGate.DeleteFile(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new(false, message, [], []);
    }

    private static bool IsMinecraftBusy()
    {
        var probe = new Profile(MinecraftJavaId, "Minecraft Java", "", _ => [], ["javaw", "java", "MinecraftLauncher"]);
        return Busy(probe);
    }

    private static MinecraftModsLedgerDoc LoadMinecraftModsLedger()
    {
        try
        {
            if (!File.Exists(MinecraftModsLedgerPath)) return new MinecraftModsLedgerDoc();
            return JsonSerializer.Deserialize<MinecraftModsLedgerDoc>(File.ReadAllText(MinecraftModsLedgerPath)) ?? new MinecraftModsLedgerDoc();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new MinecraftModsLedgerDoc();
        }
    }

    private static void SaveMinecraftModsLedger(MinecraftModsLedgerDoc doc)
    {
        Directory.CreateDirectory(Folder);
        var tmp = MinecraftModsLedgerPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc));
        File.Move(tmp, MinecraftModsLedgerPath, overwrite: true);
    }

    internal static IReadOnlyList<string> MinecraftAdvice(Pc pc, IReadOnlyList<MinecraftInstance> instances)
    {
        var heap = ComputeMinecraftHeapGb(pc.RamGb);
        List<string> notes =
        [
            $"Recommended JVM heap for this PC: {heap} GB with G1GC flags (capped at half of system RAM, minimum 2 GB).",
            "Sodium, Lithium and FerriteCore are opt-in for Fabric client instances only; Exo never modifies servers or non-Fabric instances.",
        ];
        if (instances.Count > 0 && instances.All(i => !i.IsFabric))
            notes.Add("No Fabric instance detected yet; create a Fabric instance in Vanilla, Prism, CurseForge or Modrinth App to install Sodium, Lithium and FerriteCore.");
        return notes;
    }
}
