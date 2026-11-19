using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ExoLauncher.Services;

/// <summary>
/// Best settings › Network › Cloudflare DNS, encrypted: on each network adapter in use (up, a physical Ethernet or Wi-Fi
/// device with a gateway; never a virtual switch, VPN, Hyper-V or loopback adapter), the DNS servers Windows Settings
/// calls "Manual": 1.1.1.1 and 1.0.0.1, and 2606:4700:4700::1111 and 2606:4700:4700::1001 where the adapter runs IPv6,
/// each with DNS over HTTPS on with the automatic template (Windows 11 knows Cloudflare's,
/// https://cloudflare-dns.com/dns-query, and the address is registered with it first where it isn't). The per-adapter
/// switch is the one Settings writes: InterfaceSpecificParameters\{guid}\DohInterfaceSettings\Doh\&lt;ip&gt; (Doh6 for IPv6),
/// DohFlags a QWORD whose bits are DNS_DOH_SERVER_SETTINGS' (0x1 automatic template, 0x2 own template, 0x4 fallback to
/// plain text, 0x10 discovery of designated resolvers); Exo writes 1 (automatic, no fallback) and counts either template
/// bit without the fallback bit as encrypted. Everything here is pure: the adapter filter, the detection and the PowerShell lines that set and put
/// back each adapter, built only from addresses, GUIDs and interface numbers checked here. <see cref="NetworkBest"/> runs
/// them in its one elevated step.
/// </summary>
internal static partial class CloudflareDns
{
    internal static readonly string[] V4 = ["1.1.1.1", "1.0.0.1"];
    internal static readonly string[] V6 = ["2606:4700:4700::1111", "2606:4700:4700::1001"];
    internal const string Template = "https://cloudflare-dns.com/dns-query";
    internal const string InterfaceKey = @"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters";

    /// <summary>DNS_DOH_SERVER_SETTINGS_ENABLE_AUTO and _ENABLE: either one means the server is asked over HTTPS.</summary>
    internal const long AutoTemplate = 0x1, OwnTemplate = 0x2;

    /// <summary>DNS_DOH_SERVER_SETTINGS_FALLBACK_TO_UDP: a key with it isn't counted as encrypted, and Exo's write leaves it out.</summary>
    internal const long Fallback = 0x4;

    internal enum Kind { Ethernet, WiFi, Other }

    /// <summary>
    /// A network adapter as Windows lists it: its interface GUID ("{…}") and number, connection name, description, kind,
    /// whether it's up, its device instance id from the network class key (null when it has none: a software adapter),
    /// whether it has a gateway and whether IPv6 is bound to it.
    /// </summary>
    internal sealed record Nic(string Guid, int Index, string Name, string Description, Kind Kind, bool Up, string? DeviceId, bool Gateway, bool Ipv6);

    /// <summary>One per-adapter DoH key for a server address as it is: DohFlags (null: the key without the value).</summary>
    internal sealed record Doh(string Server, long? Flags);

    /// <summary>
    /// An adapter's DNS: the servers set by hand per family (null: from DHCP, the "Automatic" of Settings), the servers it
    /// uses now per family, and its per-adapter DoH keys for each address.
    /// </summary>
    internal sealed record Servers(IReadOnlyList<string>? Static4, IReadOnlyList<string>? Static6, IReadOnlyList<string> Now4, IReadOnlyList<string> Now6, IReadOnlyList<Doh> Doh4, IReadOnlyList<Doh> Doh6);

    internal sealed record Adapter(Nic Nic, Servers Dns);

    /// <summary>
    /// What one adapter had before Exo set it, for Off to put back exactly: the servers by hand per family (null: DHCP,
    /// which stays DHCP), whether Exo set IPv6 on it, and Cloudflare's DoH keys as they were (a key not listed didn't exist
    /// and is removed).
    /// </summary>
    internal sealed record Entry(string Guid, int Index, string Name, List<string>? Static4, List<string>? Static6, bool Ipv6, List<Doh> Doh4, List<Doh> Doh6);

    // Software adapters that can still look like Ethernet: Hyper-V's switch, VPNs and tunnels, other hypervisors, capture drivers.
    [GeneratedRegex(@"Hyper-V|Virtual|VPN|TAP-|WireGuard|Wintun|Tailscale|ZeroTier|VMware|VirtualBox|Npcap|Loopback|Pseudo|Wi-Fi Direct|Bluetooth", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Software();

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    internal static partial Regex GuidName();

    /// <summary>An adapter in use: up, Ethernet or Wi-Fi, a PCI or USB device, with a gateway, and not a software adapter by its name.</summary>
    internal static bool InUse(Nic nic) =>
        nic.Up && nic.Gateway && nic.Kind is Kind.Ethernet or Kind.WiFi && nic.Index > 0 && GuidName().IsMatch(nic.Guid)
        && nic.DeviceId is { } device && NetworkTune.DeviceId().IsMatch(device) && !Software().IsMatch(nic.Description);

    /// <summary>An address as Windows writes it ("2606:4700:4700::1111"), or null: not an address, or one with a zone (%12).</summary>
    internal static string? Address(string? text) =>
        text?.Trim() is { Length: > 0 } t && t.All(c => char.IsAsciiHexDigit(c) || c is '.' or ':')
        && IPAddress.TryParse(t, out var ip)
        && (ip.AddressFamily == AddressFamily.InterNetworkV6 ? ip.ScopeId == 0 : t.Count(c => c == '.') == 3) ? ip.ToString() : null;

    /// <summary>The same servers, order aside.</summary>
    internal static bool Same(IReadOnlyList<string> servers, IReadOnlyList<string> wanted) =>
        servers.Select(Address).ToHashSet() is var have && have.SetEquals(wanted);

    /// <summary>DoH on with a template and no fallback to plain text (0x4): with fallback, a blocked HTTPS query goes out readable.</summary>
    internal static bool Encrypted(IReadOnlyList<Doh> keys, string server) =>
        keys.FirstOrDefault(k => Address(k.Server) == server) is { Flags: { } flags } && (flags & (AutoTemplate | OwnTemplate)) != 0 && (flags & Fallback) == 0;

    /// <summary>The same record for the same adapter: its servers by hand per family, in order, and Cloudflare's DoH keys (number and name aside).</summary>
    internal static bool Same(Entry a, Entry b) =>
        a.Guid.Equals(b.Guid, StringComparison.OrdinalIgnoreCase)
        && (a.Static4 ?? []).SequenceEqual(b.Static4 ?? []) && (a.Static6 ?? []).SequenceEqual(b.Static6 ?? [])
        && SameKeys(a.Doh4, b.Doh4) && SameKeys(a.Doh6, b.Doh6);

    private static bool SameKeys(IReadOnlyList<Doh> a, IReadOnlyList<Doh> b) =>
        a.Select(k => (Address(k.Server), k.Flags)).ToHashSet().SetEquals(b.Select(k => (Address(k.Server), k.Flags)));

    /// <summary>Cloudflare's IPv4 pair with DoH on both, and where the adapter runs IPv6, its IPv6 pair with DoH on both.</summary>
    internal static bool IsOn(Adapter a) =>
        Same(a.Dns.Now4, V4) && V4.All(s => Encrypted(a.Dns.Doh4, s))
        && (!a.Nic.Ipv6 || Same(a.Dns.Now6, V6) && V6.All(s => Encrypted(a.Dns.Doh6, s)));

    /// <summary>Servers on, encryption missing: what a refused DoH step leaves.</summary>
    internal static bool ServersOnly(Adapter a) =>
        Same(a.Dns.Now4, V4) && (!a.Nic.Ipv6 || Same(a.Dns.Now6, V6)) && !IsOn(a);

    /// <summary>What Off puts back for this adapter, read before Exo changes it.</summary>
    internal static Entry Snapshot(Adapter a) => new(
        a.Nic.Guid, a.Nic.Index, a.Nic.Name,
        a.Dns.Static4 is { Count: > 0 } s4 ? [.. s4] : null,
        a.Dns.Static6 is { Count: > 0 } s6 ? [.. s6] : null,
        a.Nic.Ipv6,
        [.. a.Dns.Doh4.Where(k => V4.Contains(Address(k.Server)))],
        [.. a.Dns.Doh6.Where(k => V6.Contains(Address(k.Server)))]);

    /// <summary>
    /// The PowerShell lines that register Cloudflare's addresses Windows doesn't know yet with its template (automatic
    /// upgrade on, no fallback to plain text). Null when an address isn't one of Cloudflare's.
    /// </summary>
    internal static IReadOnlyList<string>? Register(IReadOnlyList<string> missing)
    {
        if (missing.Any(m => !V4.Contains(m) && !V6.Contains(m))) return null;
        return missing.Select(m => $"Add-DnsClientDohServerAddress -ServerAddress '{m}' -DohTemplate '{Template}' -AutoUpgrade $true -AllowFallbackToUdp $false").ToList();
    }

    /// <summary>
    /// The lines that set one adapter: DoH on with the automatic template for each of Cloudflare's addresses not already
    /// encrypted (before the servers, so the first lookup already goes over HTTPS), then the servers themselves. IPv6 only
    /// where the adapter runs it. Null when anything isn't what Windows itself names.
    /// </summary>
    internal static IReadOnlyList<string>? Apply(Adapter a)
    {
        if (!GuidName().IsMatch(a.Nic.Guid) || a.Nic.Index <= 0) return null;
        var lines = new List<string>();
        foreach (var server in V4.Where(s => !Encrypted(a.Dns.Doh4, s))) lines.Add(Flags(a.Nic.Guid, "Doh", server, AutoTemplate));
        if (a.Nic.Ipv6)
        {
            foreach (var server in V6.Where(s => !Encrypted(a.Dns.Doh6, s))) lines.Add(Flags(a.Nic.Guid, "Doh6", server, AutoTemplate));
        }
        var servers = a.Nic.Ipv6 ? V4.Concat(V6) : V4;
        lines.Add($"Set-DnsClientServerAddress -InterfaceIndex {a.Nic.Index} -ServerAddresses {string.Join(',', servers.Select(s => $"'{s}'"))}");
        return lines;
    }

    /// <summary>
    /// The lines that put one adapter back as <paramref name="e"/> recorded it: each family to DHCP or to its servers by
    /// hand (netsh, which sets one family at a time), and Cloudflare's DoH keys to their old DohFlags or gone. Null when
    /// anything recorded isn't an address, a GUID or an interface number.
    /// </summary>
    internal static IReadOnlyList<string>? Revert(Entry e)
    {
        if (!GuidName().IsMatch(e.Guid) || e.Index <= 0) return null;
        var lines = new List<string>();
        if (Family("ipv4", e.Index, e.Static4) is not { } v4) return null;
        lines.AddRange(v4);
        if (e.Ipv6)
        {
            if (Family("ipv6", e.Index, e.Static6) is not { } v6) return null;
            lines.AddRange(v6);
        }
        if (Keys(e.Guid, "Doh", V4, e.Doh4) is not { } d4) return null;
        lines.AddRange(d4);
        if (e.Ipv6)
        {
            if (Keys(e.Guid, "Doh6", V6, e.Doh6) is not { } d6) return null;
            lines.AddRange(d6);
        }
        return lines;
    }

    /// <summary>The lines that take back the registrations Exo added (only Cloudflare's addresses).</summary>
    internal static IReadOnlyList<string> Unregister(IEnumerable<string> added) =>
        added.Where(a => V4.Contains(a) || V6.Contains(a)).Select(a => $"Remove-DnsClientDohServerAddress -ServerAddress '{a}' -Confirm:$false").ToList();

    private static IReadOnlyList<string>? Family(string family, int index, IReadOnlyList<string>? servers)
    {
        if (servers is not { Count: > 0 }) return [$"netsh interface {family} set dnsservers name={index} source=dhcp"];
        var checkedServers = servers.Select(Address).ToList();
        var wanted = family == "ipv4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        if (checkedServers.Any(s => s is null || IPAddress.Parse(s).AddressFamily != wanted)) return null;
        var lines = new List<string> { $"netsh interface {family} set dnsservers name={index} source=static address={checkedServers[0]} validate=no" };
        for (var i = 1; i < checkedServers.Count; i++) lines.Add($"netsh interface {family} add dnsservers name={index} address={checkedServers[i]} index={i + 1} validate=no");
        return lines;
    }

    private static IReadOnlyList<string>? Keys(string guid, string sub, IReadOnlyList<string> servers, IReadOnlyList<Doh> before)
    {
        var lines = new List<string>();
        foreach (var server in servers)
        {
            var was = before.FirstOrDefault(k => Address(k.Server) == server);
            if (was is null) lines.Add($"Remove-Item -LiteralPath '{Path(guid, sub, server)}' -Recurse -Force");
            else if (was.Flags is { } flags) lines.Add(Flags(guid, sub, server, flags));
            else lines.Add($"Remove-ItemProperty -LiteralPath '{Path(guid, sub, server)}' -Name DohFlags -Force");
        }
        return lines;
    }

    /// <summary>The per-adapter DoH key: InterfaceSpecificParameters\{guid}\DohInterfaceSettings\Doh\1.1.1.1 (Doh6 for IPv6).</summary>
    internal static string Path(string guid, string sub, string server) =>
        $@"HKLM:\{InterfaceKey}\{guid}\DohInterfaceSettings\{sub}\{server}";

    private static string Flags(string guid, string sub, string server, long flags) =>
        $"New-Item -Path '{Path(guid, sub, server)}' -Force | Out-Null; New-ItemProperty -LiteralPath '{Path(guid, sub, server)}' -Name DohFlags -PropertyType QWord -Value {flags} -Force | Out-Null";
}
