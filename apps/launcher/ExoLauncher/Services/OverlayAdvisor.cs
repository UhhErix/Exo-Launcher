using System.Text.Json;
using System.Text.Json.Nodes;
using ExoLauncher.Adapters;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

internal sealed class OverlayAdvisor(OverlayAdvisor.ISystem system, string backupPath)
{
    internal const string FileName = "exo-overlay-advisor.json";
    internal static string DefaultPath => Path.Combine(PathHelper.AppDataDir, FileName);

    internal const string NvidiaFiltersId = "nvidia-filters";
    internal const string DiscordId = "discord";
    internal const string SteamId = "steam";
    internal const string GameBarId = "gamebar";

    internal static OverlayAdvisor Shared { get; } = new(new NativeSystem(), DefaultPath);

    internal sealed record NvidiaOverlayReading(bool Installed, bool? FiltersEnabled, bool? OverlayEnabled, bool CanWrite, string? StoragePath);

    internal sealed record DiscordOverlayReading(bool Installed, bool? OverlayEnabled, bool CanWrite, string? SettingsPath);

    internal sealed record SteamOverlayReading(bool Installed, bool Running, bool? OverlayEnabled, IReadOnlyList<string> ConfigPaths);

    internal sealed record GameBarReading(bool DvrEnabled, bool HeldByExo, bool X3dNeedsGameBar);

    internal interface ISystem
    {
        NvidiaOverlayReading ReadNvidia();
        bool WriteNvidiaFilters(string? storagePath, bool enabled);
        DiscordOverlayReading ReadDiscord();
        bool WriteDiscordOverlay(string settingsPath, bool enabled);
        SteamOverlayReading ReadSteam();
        bool WriteSteamGlobalOverlay(IReadOnlyList<string> configPaths, bool enabled);
        GameBarReading ReadGameBar();
        string? SetGameBarDvrOff(bool x3dKeepApp);
        string? RestoreGameBarDvr();
    }

    internal sealed record OverlayItem(
        string Id,
        string Name,
        string Status,
        bool Enabled,
        string Cost,
        string Detail,
        bool CanToggle,
        bool CanUndo,
        string? ManualSteps = null);

    internal sealed record State(IReadOnlyList<OverlayItem> Items);

    private sealed record BackupDoc(Dictionary<string, bool> DisabledByExo);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();

    internal State Read()
    {
        lock (_gate)
        {
            var backup = Load();
            var items = new List<OverlayItem>();

            var nv = system.ReadNvidia();
            if (nv.Installed)
            {
                var on = nv.FiltersEnabled == true;
                var status = nv.FiltersEnabled switch { true => "on", false => "off", null => "unknown" };
                var detail = on
                    ? "Game Filters and Photo Mode are enabled in NVIDIA App, hooking DirectX/Vulkan even with no filters active."
                    : nv.FiltersEnabled == false
                        ? "Game Filters and Photo Mode are off."
                        : "NVIDIA App is installed. Check that Game Filters and Photo Mode are turned off.";
                items.Add(new(
                    NvidiaFiltersId,
                    "NVIDIA App Game Filters & Photo Mode",
                    status,
                    on,
                    "Up to 15% FPS loss (avg 2-12%) even with zero filters active",
                    detail,
                    nv.CanWrite && (on || backup.DisabledByExo.ContainsKey(NvidiaFiltersId)),
                    backup.DisabledByExo.ContainsKey(NvidiaFiltersId),
                    nv.CanWrite ? null : "Open NVIDIA App -> Settings -> Features -> Overlay -> turn off Game Filters and Photo Mode."));
            }

            var dc = system.ReadDiscord();
            if (dc.Installed)
            {
                var on = dc.OverlayEnabled == true;
                var status = dc.OverlayEnabled switch { true => "on", false => "off", null => "unknown" };
                var detail = on
                    ? "Discord desktop overlay injects DiscordHook64.dll into games and can drop MPO Independent Flip."
                    : dc.OverlayEnabled == false
                        ? "Discord desktop in-game overlay is off."
                        : "Discord desktop app is installed. Turn off its in-game overlay if you use Exo's Discord room.";
                items.Add(new(
                    DiscordId,
                    "Discord in-game overlay",
                    status,
                    on,
                    "1-4% avg FPS, 5-10% worse 1% lows, +1-3 ms latency",
                    detail,
                    dc.CanWrite && (on || backup.DisabledByExo.ContainsKey(DiscordId)),
                    backup.DisabledByExo.ContainsKey(DiscordId),
                    dc.CanWrite ? null : "Open Discord -> User Settings -> Game Overlay -> turn off Enable in-game overlay."));
            }

            var steam = system.ReadSteam();
            if (steam.Installed)
            {
                var on = steam.OverlayEnabled != false;
                var status = on ? "on" : "off";
                var detail = on
                    ? "Steam's global overlay is enabled (Shift+Tab). Needed for Steam invites, purchases, or Steam Input; Exo already turns it off per game where unused."
                    : "Steam's global in-game overlay is off.";
                items.Add(new(
                    SteamId,
                    "Steam in-game overlay",
                    status,
                    on,
                    "0.5-2% avg FPS and occasional Vulkan/DX12 1% low stutter",
                    detail,
                    steam.ConfigPaths.Count > 0,
                    backup.DisabledByExo.ContainsKey(SteamId),
                    steam.Running ? "Close Steam first so Steam doesn't overwrite localconfig.vdf." : null));
            }

            var gb = system.ReadGameBar();
            var gbDetail = gb.X3dNeedsGameBar
                ? (gb.DvrEnabled
                    ? "Background Game DVR recording is on. Turning it off keeps the Xbox Game Bar app installed so AMD's dual-CCD X3D driver keeps parking cores."
                    : "Background Game DVR recording is off. Xbox Game Bar stays installed because AMD's dual-CCD X3D driver needs it for core parking.")
                : (gb.DvrEnabled
                    ? "Xbox Game Bar background recording (Game DVR) is on."
                    : "Xbox Game Bar background recording (Game DVR) is off.");
            items.Add(new(
                GameBarId,
                gb.X3dNeedsGameBar ? "Xbox Game Bar DVR (X3D-safe)" : "Xbox Game Bar & Game DVR",
                gb.DvrEnabled ? "on" : "off",
                gb.DvrEnabled,
                "2-5% GPU and encoder overhead when background recording is on",
                gbDetail,
                true,
                gb.HeldByExo || backup.DisabledByExo.ContainsKey(GameBarId)));

            return new(items);
        }
    }

    internal string? Disable(string id)
    {
        lock (_gate)
        {
            var backup = Load();
            switch (id)
            {
                case NvidiaFiltersId:
                {
                    var nv = system.ReadNvidia();
                    if (!nv.Installed || !nv.CanWrite)
                        return "Open NVIDIA App -> Settings -> Features -> Overlay to turn off Game Filters and Photo Mode.";
                    if (!system.WriteNvidiaFilters(nv.StoragePath, false))
                        return "Exo couldn't update NVIDIA App's filter setting. Turn it off inside NVIDIA App.";
                    backup.DisabledByExo[NvidiaFiltersId] = true;
                    Save(backup);
                    return null;
                }
                case DiscordId:
                {
                    var dc = system.ReadDiscord();
                    if (!dc.Installed || !dc.CanWrite || string.IsNullOrWhiteSpace(dc.SettingsPath))
                        return "Open Discord -> User Settings -> Game Overlay to turn off the in-game overlay.";
                    if (!system.WriteDiscordOverlay(dc.SettingsPath, false))
                        return "Exo couldn't update Discord's settings.json. Close Discord and try again.";
                    backup.DisabledByExo[DiscordId] = true;
                    Save(backup);
                    return null;
                }
                case SteamId:
                {
                    var steam = system.ReadSteam();
                    if (!steam.Installed || steam.ConfigPaths.Count == 0)
                        return "Steam's localconfig.vdf wasn't found on this PC.";
                    if (steam.Running)
                        return "Close Steam first so it doesn't overwrite localconfig.vdf.";
                    if (!system.WriteSteamGlobalOverlay(steam.ConfigPaths, false))
                        return "Exo couldn't update Steam's localconfig.vdf.";
                    backup.DisabledByExo[SteamId] = true;
                    Save(backup);
                    return null;
                }
                case GameBarId:
                {
                    var gb = system.ReadGameBar();
                    var err = system.SetGameBarDvrOff(gb.X3dNeedsGameBar);
                    if (err is not null) return err;
                    backup.DisabledByExo[GameBarId] = true;
                    Save(backup);
                    return null;
                }
                default:
                    return "Exo doesn't know that overlay.";
            }
        }
    }

    internal string? DisableAllButStores()
    {
        var failed = Read().Items
            .Where(i => i.Enabled && i.CanToggle && i.Id != SteamId)
            .Select(i => Disable(i.Id) is null ? null : i.Name)
            .OfType<string>()
            .ToList();
        return failed.Count == 0 ? null : $"{string.Join(", ", failed)} couldn't be turned off.";
    }

    internal string? Restore(string id)
    {
        lock (_gate)
        {
            var backup = Load();
            switch (id)
            {
                case NvidiaFiltersId:
                {
                    var nv = system.ReadNvidia();
                    if (!nv.CanWrite || !system.WriteNvidiaFilters(nv.StoragePath, true))
                        return "Exo couldn't restore NVIDIA App's filter setting.";
                    backup.DisabledByExo.Remove(NvidiaFiltersId);
                    Save(backup);
                    return null;
                }
                case DiscordId:
                {
                    var dc = system.ReadDiscord();
                    if (!dc.CanWrite || string.IsNullOrWhiteSpace(dc.SettingsPath) || !system.WriteDiscordOverlay(dc.SettingsPath, true))
                        return "Exo couldn't restore Discord's overlay setting.";
                    backup.DisabledByExo.Remove(DiscordId);
                    Save(backup);
                    return null;
                }
                case SteamId:
                {
                    var steam = system.ReadSteam();
                    if (steam.Running)
                        return "Close Steam first so it doesn't overwrite localconfig.vdf.";
                    if (steam.ConfigPaths.Count == 0 || !system.WriteSteamGlobalOverlay(steam.ConfigPaths, true))
                        return "Exo couldn't restore Steam's overlay setting.";
                    backup.DisabledByExo.Remove(SteamId);
                    Save(backup);
                    return null;
                }
                case GameBarId:
                {
                    var err = system.RestoreGameBarDvr();
                    if (err is not null) return err;
                    backup.DisabledByExo.Remove(GameBarId);
                    Save(backup);
                    return null;
                }
                default:
                    return "Exo doesn't know that overlay.";
            }
        }
    }

    private BackupDoc Load()
    {
        try
        {
            if (File.Exists(backupPath) && JsonSerializer.Deserialize<BackupDoc>(File.ReadAllText(backupPath), Json) is { } doc)
                return new(doc.DisabledByExo ?? new(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new(new(StringComparer.OrdinalIgnoreCase));
    }

    private void Save(BackupDoc doc)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            var tmp = backupPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
            File.Move(tmp, backupPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal sealed class NativeSystem : ISystem
    {
        private const string ShadowPlayKey = @"Software\NVIDIA Corporation\Global\ShadowPlay\NVSPCAPS";
        private static readonly string[] GlobalOverlayPath = ["UserLocalConfigStore", "system"];

        public NvidiaOverlayReading ReadNvidia()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var nvAppDir = Path.Combine(localAppData, "NVIDIA Corporation", "NVIDIA App");
            var nvOverlaySettings = Path.Combine(localAppData, "NVIDIA Corporation", "NVIDIA Overlay", "settings.json");
            var nvBackendJson = Path.Combine(nvAppDir, "NvBackend", "ApplicationStorage.json");
            var installed = Directory.Exists(nvAppDir) || File.Exists(nvOverlaySettings) || HasShadowPlayKey();
            if (!installed) return new(false, null, null, false, null);

            if (File.Exists(nvOverlaySettings) && TryReadJsonBool(nvOverlaySettings, out var filtersOn, "gameFiltersEnabled", "filtersEnabled", "photoModeEnabled"))
                return new(true, filtersOn, null, true, nvOverlaySettings);

            if (TryReadShadowPlayFilters(out var regFilters, out var regOverlay))
                return new(true, regFilters, regOverlay, true, ShadowPlayKey);

            if (File.Exists(nvBackendJson) && TryReadJsonBool(nvBackendJson, out var backendFilters, "gameFiltersEnabled", "filtersEnabled"))
                return new(true, backendFilters, null, false, nvBackendJson);

            return new(true, null, null, false, null);
        }

        public bool WriteNvidiaFilters(string? storagePath, bool enabled)
        {
            if (HostMode.IsDisposable || string.IsNullOrWhiteSpace(storagePath)) return false;
            if (string.Equals(storagePath, ShadowPlayKey, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(ShadowPlayKey, writable: true);
                    if (key is null) return false;
                    byte[] val = enabled ? [1, 0, 0, 0] : [0, 0, 0, 0];
                    if (key.GetValue("FiltersEnabled") is not null) key.SetValue("FiltersEnabled", val, RegistryValueKind.Binary);
                    if (key.GetValue("GameFiltersEnabled") is not null) key.SetValue("GameFiltersEnabled", val, RegistryValueKind.Binary);
                    if (key.GetValue("PhotoModeEnabled") is not null) key.SetValue("PhotoModeEnabled", val, RegistryValueKind.Binary);
                    return true;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
            }
            return TryWriteJsonBools(storagePath, enabled, "gameFiltersEnabled", "filtersEnabled", "photoModeEnabled");
        }

        public DiscordOverlayReading ReadDiscord()
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var settingsPath = Path.Combine(roaming, "discord", "settings.json");
            var installed = File.Exists(settingsPath) || Directory.Exists(Path.Combine(local, "Discord"));
            if (!installed) return new(false, null, false, null);
            if (File.Exists(settingsPath) && TryReadJsonBool(settingsPath, out var overlayOn, "ENABLE_OVERLAY", "enableOverlay"))
                return new(true, overlayOn, true, settingsPath);
            return new(true, null, false, File.Exists(settingsPath) ? settingsPath : null);
        }

        public bool WriteDiscordOverlay(string settingsPath, bool enabled)
        {
            if (HostMode.IsDisposable) return false;
            return TryWriteJsonBools(settingsPath, enabled, "ENABLE_OVERLAY", "enableOverlay");
        }

        public SteamOverlayReading ReadSteam()
        {
            var root = LibraryWatchers.TrySteamRoot();
            if (string.IsNullOrWhiteSpace(root)) return new(false, false, null, []);
            var paths = StoreQuiet.Accounts(root)
                .Select(acc => Path.Combine(acc, "config", "localconfig.vdf"))
                .Where(File.Exists)
                .ToList();
            if (paths.Count == 0) return new(true, ProcessHelper.IsProcessRunning("steam"), null, []);
            var anyOn = paths.Any(p => SteamOverlayConfig.GlobalEnabled(SteamPlaytime.ReadSharedText(p)));
            return new(true, ProcessHelper.IsProcessRunning("steam"), anyOn, paths);
        }

        public bool WriteSteamGlobalOverlay(IReadOnlyList<string> configPaths, bool enabled)
        {
            if (HostMode.IsDisposable || ProcessHelper.IsProcessRunning("steam")) return false;
            var changed = false;
            foreach (var path in configPaths)
            {
                if (SteamPlaytime.ReadSharedText(path) is not { Length: > 0 } text) continue;
                try
                {
                    var next = SteamOverlayConfig.SetValue(text, GlobalOverlayPath, "EnableGameOverlay", enabled ? "1" : "0");
                    if (next != text)
                    {
                        var tmp = path + ".exo.tmp";
                        File.WriteAllText(tmp, next);
                        File.Move(tmp, path, overwrite: true);
                        changed = true;
                    }
                    else
                    {
                        changed = true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    return false;
                }
            }
            return changed;
        }

        public GameBarReading ReadGameBar()
        {
            var x3d = Autopilot.CpuClass is PowerProfiles.CpuClass.AmdDualCcdX3d or PowerProfiles.CpuClass.AmdDualCcdZen5;
            var appCapture = UserRegistry.Shared.Get(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled")?.Number;
            var gameDvr = UserRegistry.Shared.Get(@"System\GameConfigStore", "GameDVR_Enabled")?.Number;
            var dvrEnabled = appCapture != 0 || gameDvr != 0;
            return new(dvrEnabled, WindowsGameBar.Shared.State == GameBarState.Held, x3d);
        }

        public string? SetGameBarDvrOff(bool x3dKeepApp)
        {
            if (HostMode.IsDisposable) return HostMode.Refusal;
            return WindowsGameBar.On();
        }

        public string? RestoreGameBarDvr()
        {
            if (HostMode.IsDisposable) return HostMode.Refusal;
            return WindowsGameBar.Off();
        }

        private static bool HasShadowPlayKey()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ShadowPlayKey);
                return key is not null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }

        private static bool TryReadShadowPlayFilters(out bool filtersOn, out bool? overlayOn)
        {
            filtersOn = false;
            overlayOn = null;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ShadowPlayKey);
                if (key is null) return false;
                overlayOn = ReadRegFlag(key.GetValue("ShareEnabled"));
                var f1 = ReadRegFlag(key.GetValue("FiltersEnabled"));
                var f2 = ReadRegFlag(key.GetValue("GameFiltersEnabled"));
                var f3 = ReadRegFlag(key.GetValue("PhotoModeEnabled"));
                if (f1 is null && f2 is null && f3 is null) return false;
                filtersOn = f1 == true || f2 == true || f3 == true;
                return true;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }

        private static bool? ReadRegFlag(object? raw) => raw switch
        {
            int i => i != 0,
            byte[] b when b.Length > 0 => b[0] != 0,
            _ => null,
        };

        private static bool TryReadJsonBool(string path, out bool value, params string[] keys)
        {
            value = false;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (node is null) return false;
                var found = false;
                foreach (var k in keys)
                {
                    if (node.TryGetPropertyValue(k, out var prop) && prop is JsonValue jv && jv.TryGetValue<bool>(out var b))
                    {
                        found = true;
                        if (b) value = true;
                    }
                }
                return found;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
        }

        private static bool TryWriteJsonBools(string path, bool value, params string[] keys)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (node is null) return false;
                var any = false;
                foreach (var k in keys)
                {
                    if (node.ContainsKey(k))
                    {
                        node[k] = value;
                        any = true;
                    }
                }
                if (!any) return false;
                var tmp = path + ".exo.tmp";
                File.WriteAllText(tmp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
        }
    }
}
