using System.Net.NetworkInformation;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// <see cref="NetworkTune"/> on this PC: the connected adapters from Windows' network list matched to their class keys
/// (NetCfgInstanceId), read without administrator rights, and its one elevated step (reg.exe for each value, then
/// pnputil restarting each changed adapter, chained in one cmd.exe so Windows asks once).
/// </summary>
internal sealed partial class NetworkTune
{
    internal static NetworkTune Shared { get; } = new(new NativeAdapters(), new ElevatedWriter(), Path.Combine(PathHelper.AppDataDir, "exo-network-tune.json"), () => DateTimeOffset.UtcNow);

    internal sealed class NativeAdapters : IAdapters
    {
        public bool Portable() => PcHealth.ThisPcIsLaptop();

        /// <summary>Every Ethernet or Wi-Fi adapter that's connected and is a PCI or USB device (no virtual switches, VPNs or debuggers).</summary>
        public IReadOnlyList<Adapter> List()
        {
            Dictionary<string, NetworkInterface> up;
            try
            {
                up = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211)
                    .GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            }
            catch (NetworkInformationException) { return []; }
            var list = new List<Adapter>();
            foreach (var (name, key) in ClassKeys())
            {
                using (key)
                {
                    if (key.GetValue("NetCfgInstanceId") is not string guid || !up.TryGetValue(guid, out var nic)) continue;
                    if (key.GetValue("DeviceInstanceID") is not string device || !DeviceId().IsMatch(device)) continue;
                    list.Add(new Adapter(name, nic.Name, nic.Description, device, nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                        Math.Max(0, nic.Speed), ReadParams(key)));
                }
            }
            return list;
        }

        public IReadOnlyDictionary<string, Param>? Params(string key)
        {
            if (!KeyName().IsMatch(key)) return null;
            try
            {
                using var adapter = Registry.LocalMachine.OpenSubKey($@"{ClassKey}\{key}");
                return adapter is null ? null : ReadParams(adapter);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }

        /// <summary>The network class's numbered subkeys (its Properties key can't be opened without rights and isn't one).</summary>
        private static IEnumerable<(string Name, RegistryKey Key)> ClassKeys()
        {
            RegistryKey? root;
            string[] names;
            try
            {
                root = Registry.LocalMachine.OpenSubKey(ClassKey);
                names = root?.GetSubKeyNames() ?? [];
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { yield break; }
            using (root)
            {
                foreach (var name in names.Where(n => KeyName().IsMatch(n)))
                {
                    RegistryKey? key;
                    try { key = root!.OpenSubKey(name); }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { continue; }
                    if (key is not null) yield return (name, key);
                }
            }
        }

        /// <summary>Each Ndi\Params keyword: the adapter's own value beside it, the driver's default, and its enum's values with their names.</summary>
        private static Dictionary<string, Param> ReadParams(RegistryKey adapter)
        {
            var result = new Dictionary<string, Param>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var pnp = adapter.GetValue(PnpCapabilitiesKey);
                result[PnpCapabilitiesKey] = new Param(pnp is int n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : null, "0", new Dictionary<string, string>());
                using var parameters = adapter.OpenSubKey(@"Ndi\Params");
                if (parameters is null) return result;
                foreach (var keyword in parameters.GetSubKeyNames())
                {
                    using var param = parameters.OpenSubKey(keyword);
                    using var choices = param?.OpenSubKey("Enum");
                    var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var value in choices?.GetValueNames() ?? [])
                    {
                        if (value.Length > 0) allowed[value] = choices!.GetValue(value)?.ToString() ?? "";
                    }
                    result[keyword] = new Param(adapter.GetValue(keyword)?.ToString(), param?.GetValue("default")?.ToString(), allowed);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { /* what was read so far */ }
            return result;
        }
    }

    internal sealed class ElevatedWriter : IWriter
    {
        public string? Write(IReadOnlyList<(Adapter Adapter, IReadOnlyList<(string Keyword, string? Value)> Values)> writes)
        {
            var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
            var pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
            var commands = new List<string>();
            foreach (var (adapter, values) in writes)
            {
                if (Commands(reg, pnputil, adapter, values) is not { } lines) return "Exo didn't recognize a network adapter, so nothing changed.";
                commands.AddRange(lines);
            }
            return ElevatedCommands.Run(commands, "Network");
        }
    }

    /// <summary>
    /// The lines for one adapter: a reg.exe per value (REG_SZ, as the driver's own INF writes them; null deletes it), then
    /// pnputil restarting the adapter so its driver reads them. Null when anything isn't what Windows itself names.
    /// </summary>
    internal static IReadOnlyList<string>? Commands(string reg, string pnputil, Adapter adapter, IReadOnlyList<(string Keyword, string? Value)> values)
    {
        if (!KeyName().IsMatch(adapter.Key) || !DeviceId().IsMatch(adapter.DeviceId)) return null;
        var key = $"\"HKLM\\{ClassKey}\\{adapter.Key}\"";
        var lines = new List<string>();
        foreach (var (keyword, value) in values)
        {
            if (!KeywordName().IsMatch(keyword) || value is not null && !NumberValue().IsMatch(value)) return null;
            var regType = keyword.Equals(PnpCapabilitiesKey, StringComparison.OrdinalIgnoreCase) ? "REG_DWORD" : "REG_SZ";
            lines.Add(value is null
                ? $"\"{reg}\" delete {key} /v \"{keyword}\" /f /reg:64"
                : $"\"{reg}\" add {key} /v \"{keyword}\" /t {regType} /d {value} /f /reg:64");
        }
        lines.Add($"\"{pnputil}\" /restart-device \"{adapter.DeviceId}\"");
        return lines;
    }
}
