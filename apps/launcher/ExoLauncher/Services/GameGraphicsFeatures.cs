using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

internal static partial class GameGraphicsFeatures
{
    internal const string FeatureUpscalers = "upscalers";
    internal const string FeatureReflex = "reflex";
    internal const string FeatureRayTracing = "rayTracing";
    internal const string FeaturePathTracing = "pathTracing";
    internal const string FeatureHiddenSettings = "hiddenSettings";

    internal const string StateOn = "on";
    internal const string StateOff = "off";
    internal const string StateInGame = "inGame";

    internal const string RouteConfig = "config";
    internal const string RouteDriver = "driver";
    internal const string RouteDll = "dll";
    internal const string RouteInGame = "inGame";
    internal const string RouteInfo = "info";

    internal static readonly string[] AllFeatureIds =
    [
        FeatureUpscalers,
        FeatureReflex,
        FeatureRayTracing,
        FeaturePathTracing,
        FeatureHiddenSettings,
    ];

    internal enum EngineKind
    {
        Generic,
        Unreal5,
        Unreal4,
        Unreal3,
        WutheringWaves,
        Northlight,
        RedEngine,
        FromSoftware,
        IdTech,
        ReEngine,
        Frostbite,
        FixedFunctionDx89,
    }

    internal sealed record FeatureRow(
        string Id,
        string Name,
        string State,
        string Route,
        string RouteKind,
        bool CanToggle,
        bool NeedsAntiCheatConfirm,
        string? AntiCheatWarning = null,
        string? ClosestOption = null,
        string? Details = null);

    internal sealed record Snapshot(
        bool Ok,
        string? Message,
        string Engine,
        bool Running,
        string? AntiCheat,
        IReadOnlyList<FeatureRow> Features);

    internal sealed record DetectedHardwareAndFiles(
        EngineKind Engine,
        string EngineLabel,
        string? ProgramPath,
        string? ProgramFolder,
        bool HasDlss,
        bool HasFsr,
        bool HasXess,
        bool HasReflex,
        bool HasAntiLag2,
        bool HasXell,
        bool HasOptiScaler,
        bool HasSpecialK,
        bool HasRtxRemix,
        bool IsDx89FixedFunction,
        string? UnrealGameUserSettingsPath,
        string? UnrealEngineIniPath,
        string? NorthlightConfigPath,
        string? CyberpunkConfigPath,
        string? SekiroConfigPath,
        string? WuWaLocalStoragePath,
        string? ReEngineConfigPath,
        string? FrostbiteConfigPath,
        string? IdTechConfigPath);

    internal sealed class PersistedState
    {
        public Dictionary<string, GameRecord> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class GameRecord
    {
        public HashSet<string> EnabledFeatures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, FileBackupRecord> FileBackups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<uint, uint?> NvidiaBefore { get; set; } = [];
        public Dictionary<uint, uint> NvidiaSet { get; set; } = [];
        public string? NvidiaProgramExe { get; set; }
        public string? NvidiaTitle { get; set; }
    }

    internal sealed record FileBackupRecord(
        string OriginalPath,
        string BackupRelativePath,
        bool ExistedBefore,
        string? OriginalSha256);

    internal sealed record RuntimeContext(
        GameSettings.Pc Pc,
        AntiCheatCheck.Answer AntiCheat,
        string? ModConsent,
        bool Running,
        string StateRoot);

    private static readonly Lock Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private static readonly HashSet<string> KnownRtxRemixTitleKeys = new(StringComparer.Ordinal)
    {
        "halflife2",
        "portal",
        "theelderscrollsiiimorrowind",
        "morrowind",
        "maxpayne",
        "maxpayne2",
        "battlefield1942",
        "conflictdesertstorm",
        "thewitcherenhancededition",
        "sidmeierscivilizationiv",
        "sidmeierscivilizationivbeyondthesword",
        "sidmeierscivilizationivwarlords",
        "sidmeierscivilizationivcolonization",
        "callofdutyworldatwar",
        "callofduty2",
        "callofduty4modernwarfare",
        "grandtheftautoi",
        "grandtheftautoiii",
        "grandtheftautovicecity",
        "grandtheftautosanandreas",
        "needforspeedunderground",
        "needforspeedunderground2",
        "needforspeedmostwanted",
        "unrealtournament2004",
        "returntocastlewolfenstein",
        "left4dead2",
        "teamfortress2",
        "darkmessiahofmightandmagic",
    };

    private static readonly HashSet<string> NativeReflexTitleKeys = new(StringComparer.Ordinal)
    {
        "valorant",
        "counterstrike2",
        "apexlegends",
        "overwatch2",
        "fortnite",
        "cyberpunk2077",
        "callofdutymodernwarfare4",
        "callofdutymodernwarfare",
        "callofdutywarzone",
        "hellisus",
        "controlresonant",
        "alanwake2",
        "phantombladezero",
        "wutheringwaves",
        "beastofreincarnation",
        "fragpunk",
        "thefinals",
        "marvelrivals",
    };

    internal static string DefaultStateRoot => Path.Combine(PathHelper.AppDataDir, "graphics-features");
    internal static string StateFilePath(string root) => Path.Combine(root, "state.json");
    internal static string BackupsDir(string root) => Path.Combine(root, "backups");

    internal static string OfflineModeModNote(string gameTitle)
    {
        var title = string.IsNullOrWhiteSpace(gameTitle) ? "This game" : gameTitle.Trim();
        return $"{title} runs in its official offline mode without anti-cheat. Mods load only in that offline mode.";
    }

    internal static bool IsKnownFeature(string? featureId) =>
        featureId is FeatureUpscalers or FeatureReflex or FeatureRayTracing or FeaturePathTracing or FeatureHiddenSettings;

    internal static bool IsOfficialOfflineMode(AntiCheatCheck.Answer antiCheat) =>
        antiCheat.Kind == AntiCheatCheck.Kind.None
        && string.Equals(antiCheat.Evidence, AntiCheatCheck.WithoutAntiCheatEvidence, StringComparison.Ordinal);

    internal static bool AllowsDllMod(GameEntry game, AntiCheatCheck.Answer antiCheat)
    {
        if (AntiCheatCheck.IsRiot(game) || antiCheat.Names.Contains(DlssSwapService.RiotVanguard)) return false;
        return antiCheat.Kind == AntiCheatCheck.Kind.None;
    }

    internal static bool NeedsOfflineOneTimeNote(GameEntry game, AntiCheatCheck.Answer antiCheat, string? consent) =>
        AllowsDllMod(game, antiCheat)
        && IsOfficialOfflineMode(antiCheat)
        && !string.Equals(consent, AntiCheatCheck.WithoutAntiCheatEvidence, StringComparison.OrdinalIgnoreCase);

    internal static bool AllowsConfigOrDriver(GameEntry game, AntiCheatCheck.Answer antiCheat) =>
        !AntiCheatCheck.IsRiot(game) && !antiCheat.Names.Contains(DlssSwapService.RiotVanguard);

    internal static Snapshot Inspect(GameEntry game, RuntimeContext? context = null)
    {
        var ctx = context ?? CreateLiveContext(game, null);
        if (!game.Installed || string.IsNullOrEmpty(game.Path))
        {
            return new Snapshot(
                true,
                null,
                "Not installed",
                false,
                ctx.AntiCheat.Kind == AntiCheatCheck.Kind.None ? null : ctx.AntiCheat.Label,
                BuildNotInstalledRows());
        }

        var detected = Detect(game);
        PersistedState state;
        lock (Gate) state = LoadState(ctx.StateRoot);
        state.Games.TryGetValue(game.Id, out var record);
        var enabled = record?.EnabledFeatures ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rows = new List<FeatureRow>
        {
            BuildUpscalersRow(game, detected, ctx, enabled, record),
            BuildReflexRow(game, detected, ctx, enabled, record),
            BuildRayTracingRow(game, detected, ctx, enabled),
            BuildPathTracingRow(game, detected, ctx, enabled),
            BuildHiddenSettingsRow(game, detected, ctx, enabled, record),
        };

        return new Snapshot(
            true,
            null,
            detected.EngineLabel,
            ctx.Running,
            ctx.AntiCheat.Kind == AntiCheatCheck.Kind.None ? null : ctx.AntiCheat.Label,
            rows);
    }

    internal static Snapshot Set(
        GameEntry game,
        string featureId,
        bool on,
        bool acceptAntiCheatRisk,
        Action<string, string?>? saveModConsent = null,
        RuntimeContext? context = null)
    {
        if (!IsKnownFeature(featureId))
            return new Snapshot(false, "Unknown graphics feature.", "Unknown", false, null, []);

        var ctx = context ?? CreateLiveContext(game, null);
        if (!game.Installed || string.IsNullOrEmpty(game.Path))
            return new Snapshot(false, "Install this game first.", "Not installed", false, null, BuildNotInstalledRows());

        if (ctx.Running)
            return new Snapshot(false, $"Close {game.Title} first.", Detect(game).EngineLabel, true, ctx.AntiCheat.Label, Inspect(game, ctx).Features);

        var detected = Detect(game);
        var current = Inspect(game, ctx);
        var target = current.Features.FirstOrDefault(f => f.Id == featureId);
        if (target is null)
            return new Snapshot(false, "Feature not found.", detected.EngineLabel, false, ctx.AntiCheat.Label, current.Features);

        if (!target.CanToggle)
            return new Snapshot(false, target.ClosestOption ?? "This feature is controlled inside the game or unavailable on this engine.", detected.EngineLabel, false, ctx.AntiCheat.Label, current.Features);

        if (on && target.RouteKind == RouteDll)
        {
            if (!AllowsDllMod(game, ctx.AntiCheat))
                return new Snapshot(false, "Online or anti-cheat protected games use config, in-game, and driver routes only (no DLL mods).", detected.EngineLabel, false, ctx.AntiCheat.Label, current.Features);

            if (target.NeedsAntiCheatConfirm)
            {
                if (!acceptAntiCheatRisk)
                    return new Snapshot(false, target.AntiCheatWarning ?? OfflineModeModNote(game.Title), detected.EngineLabel, false, ctx.AntiCheat.Label, current.Features);

                saveModConsent?.Invoke(game.Id, AntiCheatCheck.WithoutAntiCheatEvidence);
                ctx = ctx with { ModConsent = AntiCheatCheck.WithoutAntiCheatEvidence };
            }
        }

        if (on && target.RouteKind is RouteConfig or RouteDriver && !AllowsConfigOrDriver(game, ctx.AntiCheat))
            return new Snapshot(false, "Exo never changes Riot games or games with Vanguard.", detected.EngineLabel, false, ctx.AntiCheat.Label, current.Features);

        lock (Gate)
        {
            var state = LoadState(ctx.StateRoot);
            if (!state.Games.TryGetValue(game.Id, out var record))
            {
                record = new GameRecord();
                state.Games[game.Id] = record;
            }

            var updatedSet = new HashSet<string>(record.EnabledFeatures, StringComparer.OrdinalIgnoreCase);
            if (on) updatedSet.Add(featureId);
            else updatedSet.Remove(featureId);

            var error = ReconcileGameChanges(game, detected, ctx, record, updatedSet);
            if (error is not null)
                return new Snapshot(false, error, detected.EngineLabel, false, ctx.AntiCheat.Label, Inspect(game, ctx).Features);

            record.EnabledFeatures = updatedSet;
            if (record.EnabledFeatures.Count == 0 && record.FileBackups.Count == 0 && record.NvidiaSet.Count == 0)
                state.Games.Remove(game.Id);

            SaveState(ctx.StateRoot, state);
        }

        return Inspect(game, ctx);
    }

    internal static int ApplyBestSettingsSafe(IEnumerable<GameEntry> games, GameSettings.Pc pc, string? stateRoot = null)
    {
        var applied = 0;
        var root = stateRoot ?? DefaultStateRoot;
        foreach (var game in games)
        {
            if (!game.Installed || string.IsNullOrEmpty(game.Path)) continue;
            var ac = AntiCheatCheck.Cached(game);
            if (!AllowsConfigOrDriver(game, ac)) continue;
            var ctx = new RuntimeContext(pc, ac, null, false, root);
            var snapshot = Inspect(game, ctx);
            foreach (var row in snapshot.Features)
            {
                if (row.Id != FeatureReflex) continue;
                if (!row.CanToggle || row.State != StateOff) continue;
                if (row.RouteKind == RouteDll && ac.Kind != AntiCheatCheck.Kind.None) continue;
                if (row.RouteKind == RouteDll) continue;
                var res = Set(game, row.Id, true, false, null, ctx);
                if (res.Ok) applied++;
            }
        }
        return applied;
    }

    internal static RuntimeContext CreateLiveContext(GameEntry game, SettingsService? settings)
    {
        var pc = GameSettings.ThisPc();
        var ac = AntiCheatCheck.Cached(game);
        var consent = settings?.GraphicsModConsent(game.Id);
        var running = MainWindow.RunningLibraryGames().ContainsKey(game.Id);
        return new RuntimeContext(pc, ac, consent, running, DefaultStateRoot);
    }

    internal static DetectedHardwareAndFiles Detect(GameEntry game)
    {
        var path = game.Path ?? "";
        var titleKey = StoreService.TitleKey(game.Title ?? "");
        var isRiot = AntiCheatCheck.IsRiot(game);

        string? program = null;
        if (!isRiot && Directory.Exists(path))
        {
            if (GameSettings.WutheringWavesProgram(game) is { } wuwaExe) program = wuwaExe;
            else if (GameSettings.PhantomBladeProgram(game) is { } pbzExe) program = pbzExe;
            else if (File.Exists(Path.Combine(path, "Binaries", "Win64", "RocketLeague.exe"))) program = Path.Combine(path, "Binaries", "Win64", "RocketLeague.exe");
            else program = GameHdr.MainProgram(path);
        }
        var programFolder = program is not null ? Path.GetDirectoryName(program) : null;

        bool hasDlss = false, hasFsr = false, hasXess = false, hasReflex = false, hasAntiLag2 = false, hasXell = false;
        bool hasOptiScaler = false, hasSpecialK = false, hasRtxRemix = false;

        if (!isRiot && Directory.Exists(path))
        {
            ScanLibraries(path, programFolder, out hasDlss, out hasFsr, out hasXess, out hasReflex, out hasAntiLag2, out hasXell, out hasOptiScaler, out hasSpecialK, out hasRtxRemix);
        }

        if (NativeReflexTitleKeys.Contains(titleKey)) hasReflex = true;

        var engine = EngineKind.Generic;
        var engineLabel = "Generic";
        string? unrealGus = null;
        string? unrealEngineIni = null;
        string? northlight = null;
        string? cyberpunk = null;
        string? sekiro = null;
        string? wuwaDb = null;
        string? reEngine = null;
        string? frostbite = null;
        string? idTech = null;

        if (!isRiot && Directory.Exists(path))
        {
            if (GameSettings.IsWutheringWaves(game))
            {
                engine = EngineKind.WutheringWaves;
                engineLabel = "Unreal Engine 4.26 (Kuro)";
                var root = Directory.Exists(Path.Combine(path, "Wuthering Waves Game", "Client"))
                    ? Path.Combine(path, "Wuthering Waves Game")
                    : path;
                wuwaDb = Path.Combine(root, "Client", "Saved", "LocalStorage", "LocalStorage.db");
                unrealGus = Path.Combine(root, "Client", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");
                unrealEngineIni = Path.Combine(root, "Client", "Saved", "Config", "WindowsNoEditor", "Engine.ini");
            }
            else if (GameSettings.IsControlResonant(path) || File.Exists(Path.Combine(path, "AlanWake2.exe")))
            {
                engine = EngineKind.Northlight;
                engineLabel = "Northlight";
                northlight = GameSettings.IsControlResonant(path)
                    ? GameSettings.ControlResonantConfig
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remedy", "AlanWake2", "renderer.ini");
            }
            else if (File.Exists(Path.Combine(path, "bin", "x64", "Cyberpunk2077.exe")))
            {
                engine = EngineKind.RedEngine;
                engineLabel = "REDengine 4";
                cyberpunk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CD Projekt Red", "Cyberpunk 2077", "UserSettings.json");
            }
            else if (File.Exists(Path.Combine(path, "sekiro.exe")))
            {
                engine = EngineKind.FromSoftware;
                engineLabel = "FromSoftware (DX11)";
                sekiro = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sekiro", "GraphicsConfig.xml");
            }
            else if (File.Exists(Path.Combine(path, "re_chunk_000.pak")))
            {
                engine = EngineKind.ReEngine;
                engineLabel = "RE Engine";
                reEngine = Path.Combine(path, "config.ini");
            }
            else if (GameHdr.UnrealProjects(path) is [var project, ..])
            {
                (unrealGus, unrealEngineIni) = ResolveUnrealConfigPaths(project, game);
                var isUe5 = IsUnreal5Project(path, unrealGus);
                engine = isUe5 ? EngineKind.Unreal5 : EngineKind.Unreal4;
                engineLabel = isUe5 ? "Unreal Engine 5" : "Unreal Engine 4";
            }
            else if (Directory.Exists(Path.Combine(path, "TAGame")) || File.Exists(Path.Combine(path, "Binaries", "Win64", "RocketLeague.exe")))
            {
                engine = EngineKind.Unreal3;
                engineLabel = "Unreal Engine 3 (DX11)";
            }
            else if (Directory.Exists(Path.Combine(path, "base")) && (File.Exists(Path.Combine(path, "DOOMEternalx64vk.exe")) || File.Exists(Path.Combine(path, "TheGreatCircle.exe"))))
            {
                engine = EngineKind.IdTech;
                engineLabel = "idTech";
                idTech = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "id Software", "DOOMEternal", "base", "DOOMEternalConfig.local");
            }
            else if (File.Exists(Path.Combine(path, "Engine.BuildInfo")) || Directory.Exists(Path.Combine(path, "Data", "Win32")))
            {
                engine = EngineKind.Frostbite;
                engineLabel = "Frostbite";
                frostbite = FindFrostbiteProfile(game);
            }
        }

        var isDx89 = KnownRtxRemixTitleKeys.Contains(titleKey) || (program is not null && IsDx89FixedFunctionBinary(program));
        if (engine == EngineKind.Generic && isDx89)
        {
            engine = EngineKind.FixedFunctionDx89;
            engineLabel = "DirectX 8/9 (RTX Remix)";
        }

        return new DetectedHardwareAndFiles(
            engine,
            engineLabel,
            program,
            programFolder,
            hasDlss,
            hasFsr,
            hasXess,
            hasReflex,
            hasAntiLag2,
            hasXell,
            hasOptiScaler,
            hasSpecialK,
            hasRtxRemix,
            isDx89,
            unrealGus,
            unrealEngineIni,
            northlight,
            cyberpunk,
            sekiro,
            wuwaDb,
            reEngine,
            frostbite,
            idTech);
    }

    private static (string? Gus, string? EngineIni) ResolveUnrealConfigPaths(string project, GameEntry game)
    {
        var gus = GameHdr.UnrealSettingsFile(project, game);
        if (gus is not null)
        {
            var dir = Path.GetDirectoryName(gus)!;
            return (gus, Path.Combine(dir, "Engine.ini"));
        }
        var existingEngine = GameHdr.UnrealEngineIniFiles(game).FirstOrDefault();
        if (existingEngine is not null)
        {
            var dir = Path.GetDirectoryName(existingEngine)!;
            return (Path.Combine(dir, "GameUserSettings.ini"), existingEngine);
        }
        var projectName = Path.GetFileName(project);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var winDir = Path.Combine(local, projectName, "Saved", "Config", "Windows");
        var noEdDir = Path.Combine(local, projectName, "Saved", "Config", "WindowsNoEditor");
        var chosen = Directory.Exists(winDir) ? winDir : Directory.Exists(noEdDir) ? noEdDir : winDir;
        return (Path.Combine(chosen, "GameUserSettings.ini"), Path.Combine(chosen, "Engine.ini"));
    }

    private static bool IsUnreal5Project(string installPath, string? gusPath)
    {
        if (gusPath is not null && gusPath.Contains($"{Path.DirectorySeparatorChar}Config{Path.DirectorySeparatorChar}Windows{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return true;
        if (gusPath is not null && gusPath.Contains($"{Path.DirectorySeparatorChar}Config{Path.DirectorySeparatorChar}WindowsNoEditor{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return false;
        return Directory.Exists(Path.Combine(installPath, "Engine", "Binaries", "ThirdParty", "NVIDIA", "NVaftermath"))
            || Directory.Exists(Path.Combine(installPath, "Engine", "Plugins", "Runtime", "Nvidia", "Streamline"));
    }

    private static string? FindFrostbiteProfile(GameEntry game)
    {
        foreach (var folder in GameHdr.TitleFolders(game))
        {
            var candidate = Path.Combine(folder, "settings", "PROFSAVE_profile");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void ScanLibraries(
        string installRoot,
        string? programFolder,
        out bool hasDlss,
        out bool hasFsr,
        out bool hasXess,
        out bool hasReflex,
        out bool hasAntiLag2,
        out bool hasXell,
        out bool hasOptiScaler,
        out bool hasSpecialK,
        out bool hasRtxRemix)
    {
        hasDlss = hasFsr = hasXess = hasReflex = hasAntiLag2 = hasXell = hasOptiScaler = hasSpecialK = hasRtxRemix = false;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 9, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(installRoot, "*", opts))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("sl.dlss.dll", StringComparison.OrdinalIgnoreCase))
                    hasDlss = true;
                else if (name.Equals("amd_fidelityfx_upscaler_dx12.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("amd_fidelityfx_dx12.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("amd_fidelityfx_vk.dll", StringComparison.OrdinalIgnoreCase))
                    hasFsr = true;
                else if (name.Equals("libxess.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("libxess_dx11.dll", StringComparison.OrdinalIgnoreCase))
                    hasXess = true;
                else if (name.Equals("sl.reflex.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("sl.pcl.dll", StringComparison.OrdinalIgnoreCase))
                    hasReflex = true;
                else if (name.Equals("amd_antilag2.dll", StringComparison.OrdinalIgnoreCase) || name.Contains("antilag2", StringComparison.OrdinalIgnoreCase))
                    hasAntiLag2 = true;
                else if (name.Equals("libxell.dll", StringComparison.OrdinalIgnoreCase))
                    hasXell = true;
                else if (name.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase))
                    hasOptiScaler = true;
                else if (name.Equals("SpecialK64.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("SpecialK.ini", StringComparison.OrdinalIgnoreCase))
                    hasSpecialK = true;
                else if (name.Equals("rtx.conf", StringComparison.OrdinalIgnoreCase))
                    hasRtxRemix = true;
            }
            if (programFolder is not null)
            {
                if (DlssSwapService.HasSpecialK(programFolder)) hasSpecialK = true;
                if (DlssSwapService.HasRtxRemix(programFolder)) hasRtxRemix = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static bool IsDx89FixedFunctionBinary(string exePath)
    {
        try
        {
            if (!File.Exists(exePath)) return false;
            var imports = ReadPeImportNames(exePath);
            var hasLegacy = imports.Any(d => d.Equals("d3d8.dll", StringComparison.OrdinalIgnoreCase) || d.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase));
            var hasModern = imports.Any(d =>
                d.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase)
                || d.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase)
                || d.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase)
                || d.Equals("vulkan-1.dll", StringComparison.OrdinalIgnoreCase));
            return hasLegacy && !hasModern;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<string> ReadPeImportNames(string path)
    {
        var result = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var br = new BinaryReader(fs);
        if (fs.Length < 128) return result;
        if (br.ReadUInt16() != 0x5A4D) return result;
        fs.Seek(60, SeekOrigin.Begin);
        var peOffset = br.ReadUInt32();
        if (peOffset + 24 > fs.Length) return result;
        fs.Seek(peOffset, SeekOrigin.Begin);
        if (br.ReadUInt32() != 0x00004550) return result;
        fs.Seek(2, SeekOrigin.Current);
        var sectionCount = br.ReadUInt16();
        fs.Seek(12, SeekOrigin.Current);
        var optionalHeaderSize = br.ReadUInt16();
        fs.Seek(2, SeekOrigin.Current);
        var optStart = fs.Position;
        if (optStart + optionalHeaderSize > fs.Length || optionalHeaderSize < 112) return result;
        var magic = br.ReadUInt16();
        var dataDirOffset = magic == 0x10B ? 96 : magic == 0x20B ? 112 : -1;
        if (dataDirOffset < 0 || dataDirOffset + 16 > optionalHeaderSize) return result;
        fs.Seek(optStart + dataDirOffset + 8, SeekOrigin.Begin);
        var importRva = br.ReadUInt32();
        if (importRva == 0) return result;

        fs.Seek(optStart + optionalHeaderSize, SeekOrigin.Begin);
        var sections = new List<(uint Va, uint Size, uint Raw)>(sectionCount);
        for (var i = 0; i < sectionCount; i++)
        {
            if (fs.Position + 40 > fs.Length) break;
            fs.Seek(8, SeekOrigin.Current);
            var vsize = br.ReadUInt32();
            var vaddr = br.ReadUInt32();
            var rsize = br.ReadUInt32();
            var rptr = br.ReadUInt32();
            fs.Seek(16, SeekOrigin.Current);
            sections.Add((vaddr, Math.Max(vsize, rsize), rptr));
        }

        long? RvaToOffset(uint rva)
        {
            foreach (var (va, sz, raw) in sections)
            {
                if (rva >= va && rva < va + sz) return raw + (rva - va);
            }
            return null;
        }

        var importOffset = RvaToOffset(importRva);
        if (importOffset is null) return result;

        for (var i = 0; i < 256; i++)
        {
            var entryPos = importOffset.Value + i * 20L;
            if (entryPos + 20 > fs.Length) break;
            fs.Seek(entryPos + 12, SeekOrigin.Begin);
            var nameRva = br.ReadUInt32();
            if (nameRva == 0) break;
            var namePos = RvaToOffset(nameRva);
            if (namePos is null || namePos.Value >= fs.Length) continue;
            fs.Seek(namePos.Value, SeekOrigin.Begin);
            var bytes = new List<byte>(64);
            while (bytes.Count < 128 && fs.Position < fs.Length)
            {
                var b = br.ReadByte();
                if (b == 0) break;
                bytes.Add(b);
            }
            if (bytes.Count > 0) result.Add(Encoding.ASCII.GetString(bytes.ToArray()));
        }
        return result;
    }

    private static List<FeatureRow> BuildNotInstalledRows() =>
    [
        new(FeatureUpscalers, "Upscalers", StateOff, "Install game to inspect", RouteInfo, false, false),
        new(FeatureReflex, "Reflex & low latency", StateOff, "Install game to inspect", RouteInfo, false, false),
        new(FeatureRayTracing, "Ray tracing", StateOff, "Install game to inspect", RouteInfo, false, false),
        new(FeaturePathTracing, "Path tracing", StateOff, "Install game to inspect", RouteInfo, false, false),
        new(FeatureHiddenSettings, "Hidden settings", StateOff, "Install game to inspect", RouteInfo, false, false),
    ];

    private static FeatureRow BuildUpscalersRow(
        GameEntry game,
        DetectedHardwareAndFiles d,
        RuntimeContext ctx,
        HashSet<string> enabled,
        GameRecord? record)
    {
        var isNvidia = string.Equals(ctx.Pc.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase);
        var isAmd = string.Equals(ctx.Pc.Vendor, "amd", StringComparison.OrdinalIgnoreCase);
        var series = isNvidia ? GpuProfile.NvidiaSeries(ctx.Pc.Gpu ?? "") : null;
        var hasAnyUpscaler = d.HasDlss || d.HasFsr || d.HasXess;
        var isOn = enabled.Contains(FeatureUpscalers);
        var dllAllowed = AllowsDllMod(game, ctx.AntiCheat);
        var needsOfflineNote = NeedsOfflineOneTimeNote(game, ctx.AntiCheat, ctx.ModConsent);

        if (d.Engine == EngineKind.WutheringWaves)
        {
            var wuWaOn = isOn || (IsWuWaDlssEnabledOnDisk(d.WuWaLocalStoragePath) && HasNvidiaDlssOverride(record));
            var route = isNvidia && series >= 20
                ? "LocalStorage DLSS + NVIDIA DRS override"
                : "LocalStorage upscaler config";
            return new FeatureRow(FeatureUpscalers, "Upscalers", wuWaOn ? StateOn : StateOff, route, RouteConfig, AllowsConfigOrDriver(game, ctx.AntiCheat), false, null, null, "DLSS, FSR & XeSS");
        }

        if (d.Engine == EngineKind.Northlight && d.NorthlightConfigPath is not null)
        {
            var route = isNvidia && series >= 20 ? "Northlight DLSS + driver preset" : "Northlight FSR / XeSS config";
            return new FeatureRow(FeatureUpscalers, "Upscalers", isOn ? StateOn : StateOff, route, RouteConfig, true, false, null, null, "DLSS / FSR / XeSS");
        }

        if (hasAnyUpscaler)
        {
            var list = new List<string>();
            if (d.HasDlss) list.Add("DLSS");
            if (d.HasFsr) list.Add("FSR");
            if (d.HasXess) list.Add("XeSS");
            var names = string.Join(" / ", list);

            if (d.HasOptiScaler && dllAllowed)
            {
                return new FeatureRow(FeatureUpscalers, "Upscalers", isOn ? StateOn : StateInGame, $"OptiScaler bridge ({names})", RouteDll, true, needsOfflineNote, needsOfflineNote ? OfflineModeModNote(game.Title) : null);
            }

            if (isNvidia && series >= 20 && d.HasDlss && d.ProgramPath is not null && AllowsConfigOrDriver(game, ctx.AntiCheat))
            {
                var drsOn = isOn || HasNvidiaDlssOverride(record);
                return new FeatureRow(FeatureUpscalers, "Upscalers", drsOn ? StateOn : StateInGame, $"{names} · NVIDIA DRS DLSS 4/4.5 override", RouteDriver, true, false);
            }

            if (isAmd && d.HasFsr)
            {
                return new FeatureRow(FeatureUpscalers, "Upscalers", StateInGame, $"{names} · Radeon FSR 4 driver upgrade", RouteInGame, false, false);
            }

            return new FeatureRow(FeatureUpscalers, "Upscalers", StateInGame, $"Built in ({names})", RouteInGame, false, false);
        }

        if (d.Engine is EngineKind.Unreal4 or EngineKind.Unreal5 && d.UnrealGameUserSettingsPath is not null && AllowsConfigOrDriver(game, ctx.AntiCheat))
        {
            return new FeatureRow(FeatureUpscalers, "Upscalers", isOn ? StateOn : StateOff, "Engine resolution scaling (no DLSS/FSR/XeSS DLL)", RouteConfig, true, false, null, "Resolution scale via GameUserSettings.ini");
        }

        return new FeatureRow(FeatureUpscalers, "Upscalers", StateOff, "No temporal upscaler — driver scaling only", RouteInfo, false, false, null, "Use NVIDIA Image Scaling or AMD RSR in driver");
    }

    private static FeatureRow BuildReflexRow(
        GameEntry game,
        DetectedHardwareAndFiles d,
        RuntimeContext ctx,
        HashSet<string> enabled,
        GameRecord? record)
    {
        var isNvidia = string.Equals(ctx.Pc.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase);
        var isAmd = string.Equals(ctx.Pc.Vendor, "amd", StringComparison.OrdinalIgnoreCase);
        var isOn = enabled.Contains(FeatureReflex);
        var dllAllowed = AllowsDllMod(game, ctx.AntiCheat);
        var needsOfflineNote = NeedsOfflineOneTimeNote(game, ctx.AntiCheat, ctx.ModConsent);

        if (d.HasSpecialK && dllAllowed)
        {
            return new FeatureRow(FeatureReflex, "Reflex & low latency", StateInGame, "Special K Reflex injection detected", RouteInGame, false, false);
        }

        if (d.HasOptiScaler && dllAllowed && !isNvidia && d.HasReflex)
        {
            var targetApi = isAmd ? "AMD Anti-Lag 2" : "LatencyFlex";
            return new FeatureRow(FeatureReflex, "Reflex & low latency", isOn ? StateOn : StateOff, $"OptiScaler + fakenvapi ({targetApi})", RouteDll, true, needsOfflineNote, needsOfflineNote ? OfflineModeModNote(game.Title) : null);
        }

        if (d.Engine is EngineKind.Unreal4 or EngineKind.Unreal5 or EngineKind.WutheringWaves && d.HasReflex && AllowsConfigOrDriver(game, ctx.AntiCheat))
        {
            var route = isNvidia
                ? "NVIDIA Reflex (config + driver Low Latency Ultra)"
                : isAmd ? "In-game Reflex + AMD Anti-Lag" : "Reflex / XeLL config";
            return new FeatureRow(FeatureReflex, "Reflex & low latency", isOn ? StateOn : StateInGame, route, RouteConfig, true, false);
        }

        if (d.HasReflex && AntiCheatCheck.IsRiot(game))
        {
            return new FeatureRow(FeatureReflex, "Reflex & low latency", StateInGame, "NVIDIA Reflex built into game", RouteInGame, false, false);
        }

        if (isNvidia && d.ProgramPath is not null && AllowsConfigOrDriver(game, ctx.AntiCheat))
        {
            var drsOn = isOn || HasNvidiaLowLatencyOverride(record);
            var route = d.HasReflex
                ? "NVIDIA Reflex + driver Low Latency Mode Ultra"
                : "NVIDIA driver Low Latency Mode Ultra";
            return new FeatureRow(FeatureReflex, "Reflex & low latency", drsOn ? StateOn : StateOff, route, RouteDriver, true, false);
        }

        if (isAmd)
        {
            var antiLagOn = IsRadeonAntiLagActive();
            var label = d.HasAntiLag2 ? "AMD Anti-Lag 2 (in game + driver)" : "AMD Radeon Anti-Lag (driver)";
            return new FeatureRow(FeatureReflex, "Reflex & low latency", antiLagOn ? StateOn : StateOff, label, RouteInGame, false, false);
        }

        if (d.HasReflex || d.HasAntiLag2 || d.HasXell)
        {
            var tech = d.HasReflex ? "NVIDIA Reflex" : d.HasAntiLag2 ? "AMD Anti-Lag 2" : "Intel XeLL";
            return new FeatureRow(FeatureReflex, "Reflex & low latency", StateInGame, $"{tech} built into game", RouteInGame, false, false);
        }

        return new FeatureRow(FeatureReflex, "Reflex & low latency", StateOff, "Frame rate limiter (last resort)", RouteInfo, false, false, null, "Cap frame rate slightly below refresh to keep GPU queue empty");
    }

    private static FeatureRow BuildRayTracingRow(
        GameEntry game,
        DetectedHardwareAndFiles d,
        RuntimeContext ctx,
        HashSet<string> enabled)
    {
        var isOn = enabled.Contains(FeatureRayTracing);
        var allowed = AllowsConfigOrDriver(game, ctx.AntiCheat);

        return d.Engine switch
        {
            EngineKind.Northlight when d.NorthlightConfigPath is not null =>
                new FeatureRow(FeatureRayTracing, "Ray tracing", isOn ? StateOn : StateOff, "Northlight renderer.ini (all GPUs unlocked)", RouteConfig, allowed, false),

            EngineKind.WutheringWaves when d.WuWaLocalStoragePath is not null =>
                new FeatureRow(FeatureRayTracing, "Ray tracing", (isOn || IsWuWaRtEnabledOnDisk(d.WuWaLocalStoragePath)) ? StateOn : StateOff, "LocalStorage + Engine.ini (GPU whitelist unlocked)", RouteConfig, allowed, false),

            EngineKind.RedEngine when d.CyberpunkConfigPath is not null =>
                new FeatureRow(FeatureRayTracing, "Ray tracing", isOn ? StateOn : StateOff, "REDengine UserSettings.json", RouteConfig, allowed, false),

            EngineKind.Unreal5 when d.UnrealEngineIniPath is not null =>
                new FeatureRow(FeatureRayTracing, "Ray tracing", isOn ? StateOn : StateOff, "UE5 Lumen Hardware RT (Engine.ini)", RouteConfig, allowed, false),

            EngineKind.ReEngine when d.ReEngineConfigPath is not null =>
                new FeatureRow(FeatureRayTracing, "Ray tracing", isOn ? StateOn : StateOff, "RE Engine config.ini", RouteConfig, allowed, false),

            EngineKind.IdTech when d.IdTechConfigPath is not null =>
                new FeatureRow(FeatureRayTracing, "Ray tracing", isOn ? StateOn : StateOff, "idTech config (r_raytracing)", RouteConfig, allowed, false),

            EngineKind.Frostbite when d.FrostbiteConfigPath is not null =>
                new FeatureRow(FeatureRayTracing, "Ray tracing", isOn ? StateOn : StateOff, "Frostbite PROFSAVE_profile", RouteConfig, allowed, false),

            EngineKind.FromSoftware =>
                new FeatureRow(FeatureRayTracing, "Ray tracing", StateOff, "No RT renderer — max SSR & SSAO in config", RouteInfo, false, false, null, "No RT renderer; use Hidden settings for max reflections & SSAO"),

            EngineKind.Unreal4 =>
                new FeatureRow(FeatureRayTracing, "Ray tracing", StateOff, "No DX12 RT shaders — raster lighting only", RouteInfo, false, false, null, "DX11 UE4 build has no RT renderer; max shadows & post-process via Hidden settings"),

            EngineKind.FixedFunctionDx89 =>
                new FeatureRow(FeatureRayTracing, "Ray tracing", d.HasRtxRemix ? StateOn : StateOff, AllowsDllMod(game, ctx.AntiCheat) ? "Full path tracing via RTX Remix (DX8/DX9)" : "Raster lighting (online anti-cheat — config & driver only)", RouteInfo, false, false, null, AllowsDllMod(game, ctx.AntiCheat) ? "Enable Path tracing via RTX Remix runtime" : "Online anti-cheat games use config, in-game, and driver routes only"),

            _ =>
                new FeatureRow(FeatureRayTracing, "Ray tracing", StateOff, "No RT renderer — raster lighting only", RouteInfo, false, false, null, "This engine has no hardware RT pipeline"),
        };
    }

    private static FeatureRow BuildPathTracingRow(
        GameEntry game,
        DetectedHardwareAndFiles d,
        RuntimeContext ctx,
        HashSet<string> enabled)
    {
        var isOn = enabled.Contains(FeaturePathTracing);
        var allowed = AllowsConfigOrDriver(game, ctx.AntiCheat);
        var dllAllowed = AllowsDllMod(game, ctx.AntiCheat);
        var needsOfflineNote = NeedsOfflineOneTimeNote(game, ctx.AntiCheat, ctx.ModConsent);

        return d.Engine switch
        {
            EngineKind.Northlight when d.NorthlightConfigPath is not null =>
                new FeatureRow(FeaturePathTracing, "Path tracing", isOn ? StateOn : StateOff, "Northlight renderer.ini + DLSS RR", RouteConfig, allowed, false),

            EngineKind.RedEngine when d.CyberpunkConfigPath is not null =>
                new FeatureRow(FeaturePathTracing, "Path tracing", isOn ? StateOn : StateOff, "REDengine RT Overdrive (UserSettings.json)", RouteConfig, allowed, false),

            EngineKind.Unreal5 when d.UnrealEngineIniPath is not null =>
                new FeatureRow(FeaturePathTracing, "Path tracing", isOn ? StateOn : StateOff, "UE5 Path Tracer (Engine.ini)", RouteConfig, allowed, false),

            EngineKind.IdTech when d.IdTechConfigPath is not null =>
                new FeatureRow(FeaturePathTracing, "Path tracing", isOn ? StateOn : StateOff, "idTech config (r_pathtracing)", RouteConfig, allowed, false),

            EngineKind.FixedFunctionDx89 when dllAllowed =>
                new FeatureRow(
                    FeaturePathTracing,
                    "Path tracing",
                    d.HasRtxRemix ? StateOn : StateOff,
                    d.HasRtxRemix ? "NVIDIA RTX Remix runtime active" : "NVIDIA RTX Remix runtime (DX8/DX9 MIT)",
                    RouteDll,
                    false,
                    needsOfflineNote,
                    needsOfflineNote ? OfflineModeModNote(game.Title) : null,
                    "Drop NVIDIAGameWorks/rtx-remix runtime beside the DX8/DX9 executable"),

            EngineKind.FixedFunctionDx89 =>
                new FeatureRow(
                    FeaturePathTracing,
                    "Path tracing",
                    StateOff,
                    "Raster lighting (online anti-cheat — config & driver only)",
                    RouteInfo,
                    false,
                    false,
                    null,
                    "Online anti-cheat games do not load DLL runtimes such as RTX Remix"),

            EngineKind.WutheringWaves =>
                new FeatureRow(FeaturePathTracing, "Path tracing", StateOff, "No path tracer — use Ray tracing (GI, reflections, shadows)", RouteInfo, false, false, null, "Wuthering Waves ships Hardware RT (GI, Reflections, Shadows) without full path tracing"),

            _ =>
                new FeatureRow(FeaturePathTracing, "Path tracing", StateOff, "No path tracer — raster lighting only", RouteInfo, false, false, null, "This game does not ship a path tracer and is not a DX8/DX9 fixed-function classic"),
        };
    }

    private static FeatureRow BuildHiddenSettingsRow(
        GameEntry game,
        DetectedHardwareAndFiles d,
        RuntimeContext ctx,
        HashSet<string> enabled,
        GameRecord? record)
    {
        var isOn = enabled.Contains(FeatureHiddenSettings);
        var allowed = AllowsConfigOrDriver(game, ctx.AntiCheat);
        var isNvidia = string.Equals(ctx.Pc.Vendor, "nvidia", StringComparison.OrdinalIgnoreCase);

        return d.Engine switch
        {
            EngineKind.WutheringWaves when d.WuWaLocalStoragePath is not null =>
                new FeatureRow(FeatureHiddenSettings, "Hidden settings", (isOn || IsWuWa120FpsEnabledOnDisk(d.WuWaLocalStoragePath)) ? StateOn : StateOff, "120 FPS unlock + Aniso/LOD (LocalStorage & INI)", RouteConfig, allowed, false),

            EngineKind.Unreal5 or EngineKind.Unreal4 when d.UnrealEngineIniPath is not null || d.UnrealGameUserSettingsPath is not null =>
                new FeatureRow(FeatureHiddenSettings, "Hidden settings", isOn ? StateOn : StateOff, "FPS uncap + above-Ultra LOD/AF (UE config)", RouteConfig, allowed, false),

            EngineKind.Northlight when d.NorthlightConfigPath is not null =>
                new FeatureRow(FeatureHiddenSettings, "Hidden settings", isOn ? StateOn : StateOff, "FPS uncap + ultrawide + max LOD (renderer.ini)", RouteConfig, allowed, false),

            EngineKind.FromSoftware when d.SekiroConfigPath is not null =>
                new FeatureRow(FeatureHiddenSettings, "Hidden settings", isOn ? StateOn : StateOff, "Max quality overrides (GraphicsConfig.xml)", RouteConfig, allowed, false),

            EngineKind.RedEngine when d.CyberpunkConfigPath is not null =>
                new FeatureRow(FeatureHiddenSettings, "Hidden settings", isOn ? StateOn : StateOff, "Ultra decals, 16x AF & crowd (UserSettings.json)", RouteConfig, allowed, false),

            _ when isNvidia && d.ProgramPath is not null && allowed =>
                new FeatureRow(FeatureHiddenSettings, "Hidden settings", (isOn || HasNvidiaHiddenOverride(record)) ? StateOn : StateOff, "16x AF, max refresh & ReBAR (NVIDIA DRS)", RouteDriver, true, false),

            _ =>
                new FeatureRow(FeatureHiddenSettings, "Hidden settings", StateInGame, "Managed in game", RouteInGame, false, false),
        };
    }

    private static string? ReconcileGameChanges(
        GameEntry game,
        DetectedHardwareAndFiles detected,
        RuntimeContext ctx,
        GameRecord record,
        HashSet<string> targetFeatures)
    {
        var restoreErr = RestoreAllTrackedFiles(ctx.StateRoot, record);
        if (restoreErr is not null) return restoreErr;

        var nvidiaRestoreErr = RestoreNvidiaOverrides(record);
        if (nvidiaRestoreErr is not null) return nvidiaRestoreErr;

        if (targetFeatures.Count == 0)
            return null;

        var configErr = ApplyTargetConfigs(game, detected, ctx, record, targetFeatures);
        if (configErr is not null)
        {
            RestoreAllTrackedFiles(ctx.StateRoot, record);
            return configErr;
        }

        var driverErr = ApplyTargetDriverOverrides(game, detected, ctx, record, targetFeatures);
        if (driverErr is not null)
        {
            RestoreAllTrackedFiles(ctx.StateRoot, record);
            RestoreNvidiaOverrides(record);
            return driverErr;
        }

        return null;
    }

    internal static PersistedState LoadState(string stateRoot)
    {
        var path = StateFilePath(stateRoot);
        try
        {
            if (!File.Exists(path)) return new PersistedState();
            return JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(path), JsonOptions) ?? new PersistedState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PersistedState();
        }
    }

    internal static void SaveState(string stateRoot, PersistedState state)
    {
        Directory.CreateDirectory(stateRoot);
        var path = StateFilePath(stateRoot);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    internal static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
