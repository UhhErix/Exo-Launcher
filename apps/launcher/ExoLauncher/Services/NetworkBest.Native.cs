using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// <see cref="NetworkBest"/> on this PC: the adapters from Windows' network list with their device ids from the network
/// class key, their DNS from Tcpip's and Dnscache's keys (all read without administrator rights), Windows' TCP settings
/// from PowerShell's NetTCPSetting (read once a minute at most), the one elevated PowerShell step, and the quick check.
/// </summary>
internal sealed partial class NetworkBest
{
    internal static NetworkBest Shared { get; } = new(NetworkTune.Shared, new NativeSystem(), Path.Combine(PathHelper.AppDataDir, "exo-network-best.json"), () => DateTimeOffset.UtcNow);

    internal sealed class NativeSystem : ISystem
    {
        private const string Tcpip = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
        private const string Tcpip6 = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces";
        private const string WellKnown = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DohWellKnownServers";

        private (DateTimeOffset At, Tcp? Value)? _tcp;

        public IReadOnlyList<CloudflareDns.Adapter> Adapters()
        {
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (NetworkInformationException) { return []; }
            var devices = DeviceIds();
            var list = new List<CloudflareDns.Adapter>();
            foreach (var n in all)
            {
                try
                {
                    var ip = n.GetIPProperties();
                    var ipv6 = n.Supports(NetworkInterfaceComponent.IPv6);
                    var index = n.Supports(NetworkInterfaceComponent.IPv4) ? ip.GetIPv4Properties()?.Index ?? 0 : ipv6 ? ip.GetIPv6Properties()?.Index ?? 0 : 0;
                    var kind = n.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => CloudflareDns.Kind.Ethernet,
                        NetworkInterfaceType.Wireless80211 => CloudflareDns.Kind.WiFi,
                        _ => CloudflareDns.Kind.Other,
                    };
                    var gateway = ip.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
                    var nic = new CloudflareDns.Nic(n.Id, index, n.Name, n.Description, kind, n.OperationalStatus == OperationalStatus.Up,
                        devices.GetValueOrDefault(n.Id), gateway, ipv6);
                    if (!CloudflareDns.GuidName().IsMatch(n.Id)) continue;
                    var now4 = ip.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
                    // Windows lists fec0:0:0:ffff::1-3 on an interface with no IPv6 DNS: placeholders, not servers.
                    var now6 = ip.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6SiteLocal && a.ScopeId == 0).Select(a => a.ToString()).ToList();
                    list.Add(new(nic, new CloudflareDns.Servers(Static(Tcpip, n.Id), Static(Tcpip6, n.Id), now4, now6, DohKeys(n.Id, "Doh"), DohKeys(n.Id, "Doh6"))));
                }
                catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { /* an adapter that went while it was read */ }
            }
            return list;
        }

        public IReadOnlySet<string> Registered()
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(WellKnown);
                foreach (var name in root?.GetSubKeyNames() ?? [])
                {
                    using var key = root!.OpenSubKey(name);
                    if (key?.GetValue("Template") is string template && template.Equals(CloudflareDns.Template, StringComparison.OrdinalIgnoreCase) && CloudflareDns.Address(name) is { } address)
                        known.Add(address);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { /* unknown: Exo registers them, which Windows takes as it is */ }
            return known;
        }

        public Tcp? Tcp()
        {
            if (_tcp is { } cached && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(1)) return cached.Value;
            var value = ReadTcp();
            _tcp = (DateTimeOffset.UtcNow, value);
            return value;
        }

        /// <summary>The Internet template's auto-tuning level and receive-side scaling, as numbers (PowerShell's names are the same in every language).</summary>
        private static Tcp? ReadTcp()
        {
            const string command = "[int](Get-NetTCPSetting -SettingName Internet).AutoTuningLevelLocal;[int](Get-NetOffloadGlobalSetting).ReceiveSideScaling";
            try
            {
                using var process = Process.Start(new ProcessStartInfo(LeftoverScan.PowerShell, "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                });
                if (process is null) return null;
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(10_000) || !output.Wait(1000)) return null;
                var lines = output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return lines.Length == 2 && int.TryParse(lines[0], out var tuning) && int.TryParse(lines[1], out var rss) ? new Tcp(tuning, rss == 1) : null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        }

        public (int? Code, string? Problem) Elevated(string script)
        {
            _tcp = null;
            try
            {
                using var process = Process.Start(new ProcessStartInfo(LeftoverScan.PowerShell,
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Environment.SystemDirectory,
                });
                if (process is null) return (null, "Windows didn't start the change.");
                // Each item may wait up to about 25 seconds for a lookup after an adapter restarts.
                if (!process.WaitForExit(TimeSpan.FromMinutes(3))) return (Unfinished, "The change didn't finish.");
                return (process.ExitCode, null);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return (null, "Windows' permission was declined, so nothing changed.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                AppLog.Warn("Network best settings: the change didn't start: " + ex.Message);
                return (null, "Windows didn't start the change.");
            }
        }

        public bool Online() => Lookup() is not null;

        public Sample Measure()
        {
            var ping = Pings(IPAddress.Parse(CloudflareDns.V4[0]), 6);
            var router = Gateway() is { } gateway ? Pings(gateway, 4) : [];
            var lookups = Enumerable.Range(0, 3).Select(_ => Lookup()).OfType<double>().ToList();
            return new Sample(Median(ping), ping.Count > 1 ? ping.Zip(ping.Skip(1), (a, b) => Math.Abs(a - b)).Average() : null, Median(router), Median(lookups));
        }

        /// <summary>
        /// One lookup's time: a name under Exo's own domain no resolver has cached (a fresh random label, which answers
        /// "no such name"), so it measures the trip to the DNS server and back. Null when no answer came in 3 s.
        /// </summary>
        private static double? Lookup()
        {
            var name = $"check-{Guid.NewGuid():N}.getexo.app";
            var watch = Stopwatch.StartNew();
            try
            {
                var task = Dns.GetHostAddressesAsync(name);
                if (!task.Wait(TimeSpan.FromSeconds(3))) return null;
            }
            catch (AggregateException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }) { /* the answer: no such name */ }
            catch (AggregateException) { return null; }
            return watch.Elapsed.TotalMilliseconds;
        }

        private static List<double> Pings(IPAddress to, int count)
        {
            var times = new List<double>();
            using var ping = new Ping();
            for (var i = 0; i < count; i++)
            {
                try
                {
                    var reply = ping.Send(to, 1000);
                    if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
                }
                catch (PingException) { break; }
            }
            return times;
        }

        private static IPAddress? Gateway()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(n => n.GetIPProperties().GatewayAddresses).Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            }
            catch (NetworkInformationException) { return null; }
        }

        private static double? Median(List<double> values)
        {
            if (values.Count == 0) return null;
            var sorted = values.Order().ToList();
            return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        }

        /// <summary>Servers set by hand for one family (Tcpip's or Tcpip6's NameServer), or null when it's empty: from DHCP.</summary>
        private static IReadOnlyList<string>? Static(string root, string guid)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{root}\{guid}");
                var servers = (key?.GetValue("NameServer") as string ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(CloudflareDns.Address).OfType<string>().ToList();
                return servers.Count == 0 ? null : servers;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }

        private static IReadOnlyList<CloudflareDns.Doh> DohKeys(string guid, string sub)
        {
            var keys = new List<CloudflareDns.Doh>();
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey($@"{CloudflareDns.InterfaceKey}\{guid}\DohInterfaceSettings\{sub}");
                foreach (var name in root?.GetSubKeyNames() ?? [])
                {
                    using var key = root!.OpenSubKey(name);
                    long? flags = key?.GetValue("DohFlags") switch { long q => q, int d => d, _ => null };
                    keys.Add(new(name, flags));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { /* what was read so far */ }
            return keys;
        }

        /// <summary>Each network adapter's interface GUID to its device instance id (PCI\…, USB\…, ROOT\… for software ones).</summary>
        private static Dictionary<string, string> DeviceIds()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(NetworkTune.ClassKey);
                foreach (var name in (root?.GetSubKeyNames() ?? []).Where(n => NetworkTune.KeyName().IsMatch(n)))
                {
                    try
                    {
                        using var key = root!.OpenSubKey(name);
                        if (key?.GetValue("NetCfgInstanceId") is string guid && key.GetValue("DeviceInstanceID") is string device) map[guid] = device;
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { /* the Properties key */ }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { /* none known: no adapter is in use */ }
            return map;
        }
    }
}
