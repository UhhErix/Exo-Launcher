using System.Runtime.InteropServices;
using ExoLauncher.Models;
using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Helpers;

internal static class LaptopDisplayChecks
{
    private const string DisplayClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string GraphicsDriversPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string DirectXGlobalValue = "DirectXUserGlobalSettings";

    internal sealed record CheckItem(string Id, string Label, string Status, bool Warn, string Detail);

    internal sealed record Snapshot(bool IsLaptop, bool? PluggedIn, bool HdrOn, string? ColorMode, bool? AutoHdrGlobal, List<CheckItem> Items);

    internal sealed record AutoHdrInfo(bool? Global, bool? Game, string Effective);

    internal sealed record RawSignals(
        bool IsLaptop,
        bool? PluggedIn,
        bool HdrOn,
        string? ColorMode,
        bool? AutoHdrGlobal,
        string BatterySaver,
        string Cabc,
        string IntelDpst,
        string AmdVariBright);

    internal static Snapshot Evaluate(RawSignals s)
    {
        var warnPower = s.PluggedIn == true && s.HdrOn;
        var items = new List<CheckItem>();

        if (s.IsLaptop)
        {
            items.Add(BuildPowerItem("batterySaver", "Battery Saver", s.BatterySaver, warnPower,
                "Battery Saver is on while plugged in and HDR is active — it can dim or crush HDR contrast.",
                "Off.",
                "Can't detect Battery Saver state."));

            items.Add(BuildCabcItem(s.Cabc, warnPower));

            if (s.IntelDpst != "not_applicable")
                items.Add(BuildPowerItem("intelDpst", "Intel DPST", s.IntelDpst, warnPower,
                    "Intel Display Power Saving Technology is enabled — it shifts panel backlight and crushes HDR contrast.",
                    "Disabled.",
                    "Can't detect Intel DPST on this driver."));

            if (s.AmdVariBright != "not_applicable")
                items.Add(BuildPowerItem("amdVariBright", "AMD Vari-Bright", s.AmdVariBright, warnPower,
                    "AMD Vari-Bright is active while plugged in and HDR is on — it lowers backlight and crushes HDR contrast.",
                    "Off.",
                    "Can't detect AMD Vari-Bright on this driver."));
        }

        return new Snapshot(s.IsLaptop, s.PluggedIn, s.HdrOn, s.ColorMode, s.AutoHdrGlobal, items);
    }

    private static CheckItem BuildPowerItem(string id, string label, string status, bool warnPower, string activeDetail, string offDetail, string unknownDetail) => status switch
    {
        "active" => new CheckItem(id, label, "active", warnPower, activeDetail),
        "off" => new CheckItem(id, label, "off", false, offDetail),
        _ => new CheckItem(id, label, "unknown", false, unknownDetail),
    };

    private static CheckItem BuildCabcItem(string status, bool warnPower) => status switch
    {
        "active" => new CheckItem("cabc", "Content adaptive brightness (CABC)", "active", warnPower,
            "Windows CABC is set to Always — it shifts backlight brightness by content even while plugged in, crushing HDR contrast."),
        "battery_only" => new CheckItem("cabc", "Content adaptive brightness (CABC)", "off", false,
            "Battery only (off while plugged in)."),
        "off" => new CheckItem("cabc", "Content adaptive brightness (CABC)", "off", false, "Off."),
        _ => new CheckItem("cabc", "Content adaptive brightness (CABC)", "unknown", false,
            "Can't detect Windows CABC on this PC."),
    };

    internal static Snapshot Read(WindowsHdr.Screen screen)
    {
        var isLaptop = screen.IsInternal || PcHealth.ThisPcIsLaptop();
        var (pluggedIn, batterySaver) = ReadPowerStatus();
        var cabc = isLaptop ? ReadCabc() : "off";
        var (hasIntel, intelDpst) = isLaptop ? ReadIntelDpst() : (false, "not_applicable");
        var amdVariBright = isLaptop ? ReadAmdVariBright(screen.Name) : "not_applicable";
        var autoHdrGlobal = ReadAutoHdrGlobal();

        return Evaluate(new RawSignals(
            isLaptop,
            pluggedIn,
            screen.Enabled,
            screen.ColorMode,
            autoHdrGlobal,
            batterySaver,
            cabc,
            hasIntel ? intelDpst : "not_applicable",
            amdVariBright));
    }

    internal static string ParseCabcOption(object? raw) => raw is int v ? v switch
    {
        0 => "off",
        1 => "active",
        2 => "battery_only",
        _ => "unknown",
    } : "unknown";

    internal static string ParseIntelFeatureTestControl(object? raw) => raw is int v
        ? ((v & 0x10) == 0 ? "active" : "off")
        : "unknown";

    internal static bool? ParseAutoHdrEnable(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        foreach (var part in data.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("AutoHDREnable=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(part["AutoHDREnable=".Length..], out var value))
                return (value & 1) != 0;
        }
        return null;
    }

    internal static AutoHdrInfo CombineAutoHdr(bool? global, bool? game)
    {
        var effective = game switch
        {
            true => "on",
            false => "off",
            null => global switch
            {
                true => "on",
                false => "off",
                null => "default",
            },
        };
        return new AutoHdrInfo(global, game, effective);
    }

    internal static bool? ReadAutoHdrGlobal()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GpuPreference.KeyPath);
            return ParseAutoHdrEnable(key?.GetValue(DirectXGlobalValue) as string);
        }
        catch { return null; }
    }

    internal static AutoHdrInfo ReadAutoHdrForGame(GameEntry game)
    {
        var global = ReadAutoHdrGlobal();
        bool? gameFlag = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GpuPreference.KeyPath);
            if (key is not null)
            {
                var candidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(game.Path) && GameHdr.MainProgram(game.Path) is { } mainExe)
                    candidates.Add(mainExe);
                foreach (var p in GpuPreference.ProgramsOf(game))
                    if (!candidates.Contains(p, StringComparer.OrdinalIgnoreCase)) candidates.Add(p);

                foreach (var exe in candidates)
                {
                    if (ParseAutoHdrEnable(key.GetValue(exe) as string) is { } found)
                    {
                        gameFlag = found;
                        break;
                    }
                }
            }
        }
        catch { }
        return CombineAutoHdr(global, gameFlag);
    }

    private static (bool? PluggedIn, string BatterySaver) ReadPowerStatus()
    {
        if (!GetSystemPowerStatus(out var status)) return (null, "unknown");
        bool? plugged = status.AcLineStatus switch
        {
            1 => true,
            0 => false,
            _ => null,
        };
        var saver = status.SystemStatusFlag == 1 ? "active" : "off";
        return (plugged, saver);
    }

    private static string ReadCabc()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(GraphicsDriversPath);
            return ParseCabcOption(key?.GetValue("CABCOption"));
        }
        catch { return "unknown"; }
    }

    private static (bool HasIntel, string State) ReadIntelDpst()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClassPath);
            if (root is null) return (false, "not_applicable");
            var foundIntel = false;
            foreach (var subName in root.GetSubKeyNames())
            {
                if (subName.Length != 4 || !subName.All(char.IsDigit)) continue;
                using var sub = root.OpenSubKey(subName);
                if (sub is null) continue;
                var provider = sub.GetValue("ProviderName") as string ?? "";
                var matchId = sub.GetValue("MatchingDeviceId") as string ?? "";
                if (!provider.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
                    !matchId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) continue;
                foundIntel = true;
                var state = ParseIntelFeatureTestControl(sub.GetValue("FeatureTestControl"));
                if (state != "unknown") return (true, state);
            }
            return foundIntel ? (true, "unknown") : (false, "not_applicable");
        }
        catch { return (false, "not_applicable"); }
    }

    private static string ReadAmdVariBright(string screenName)
    {
        var hasAmd = HasDisplayVendor("VEN_1002", "AMD") || HasDisplayVendor("VEN_1002", "Advanced Micro Devices");
        if (!hasAmd) return "not_applicable";
        if (string.IsNullOrWhiteSpace(screenName)) return "unknown";
        return AmdAdlx.ReadVariBright(screenName) switch
        {
            true => "active",
            false => "off",
            null => "unknown",
        };
    }

    private static bool HasDisplayVendor(string venToken, string providerToken)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClassPath);
            if (root is null) return false;
            foreach (var subName in root.GetSubKeyNames())
            {
                if (subName.Length != 4 || !subName.All(char.IsDigit)) continue;
                using var sub = root.OpenSubKey(subName);
                if (sub is null) continue;
                var provider = sub.GetValue("ProviderName") as string ?? "";
                var matchId = sub.GetValue("MatchingDeviceId") as string ?? "";
                if (matchId.Contains(venToken, StringComparison.OrdinalIgnoreCase) ||
                    provider.Contains(providerToken, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
