using static ExoLauncher.Services.CloudflareDns;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Cloudflare DNS could go wrong silently:
/// - it lands on a Hyper-V switch, a VPN, a tunnel or an adapter with no gateway (a lookup then leaves by the wrong way),
///   or misses a USB Wi-Fi adapter in use;
/// - an adapter without IPv6 gets IPv6 servers it can't take, or one with IPv6 is called done with only IPv4 set;
/// - DoH is written to the wrong key (Doh vs Doh6, a value that isn't a QWORD), or written again over a key Windows
///   Settings already turned on (17 = automatic template plus designated-resolver discovery), or fallback alone (0x4)
///   counted as encrypted;
/// - Off turns a DHCP adapter into fixed servers, loses the order of the user's own servers, leaves Exo's DoH keys behind
///   or deletes a key that was the user's;
/// - a recorded value that isn't an address or GUID (a backup someone edited) reaches the elevated script.
/// </summary>
public sealed class CloudflareDnsTests
{
    private const string Guid = "{223d47ff-8ac3-4986-84ea-edd78f436a6d}";
    private const string Intel = @"PCI\VEN_8086&DEV_125C&SUBSYS_88671043&REV_06\6&102E3ADF&0&0048020A";

    internal static Nic Ethernet(bool ipv6 = true, string guid = Guid, int index = 12, string name = "Ethernet") =>
        new(guid, index, name, "Intel(R) Ethernet Controller I226-V", Kind.Ethernet, true, Intel, true, ipv6);

    internal static Adapter Dhcp(Nic nic) =>
        new(nic, new Servers(null, null, ["192.168.1.254"], [], [], []));

    [Fact]
    public void InUse_TakesPhysicalAdaptersWithAGatewayOnly()
    {
        Assert.True(InUse(Ethernet()));
        Assert.True(InUse(new Nic("{00000000-0000-0000-0000-000000000001}", 5, "Wi-Fi", "Realtek RTL8812BU Wireless LAN 802.11ac USB NIC", Kind.WiFi, true, @"USB\VID_0BDA&PID_B812\123456", true, true)));
        Assert.False(InUse(Ethernet() with { Description = "Hyper-V Virtual Ethernet Adapter", DeviceId = @"ROOT\VMS_MP\0000" }));
        Assert.False(InUse(Ethernet() with { Description = "WireGuard Tunnel", DeviceId = @"SWD\WireGuard\{abc}" }));
        // A VPN driver that registers as a PCI-looking id still says what it is.
        Assert.False(InUse(Ethernet() with { Description = "TAP-Windows Adapter V9" }));
        Assert.False(InUse(Ethernet() with { DeviceId = null }));
        Assert.False(InUse(Ethernet() with { Gateway = false }));
        Assert.False(InUse(Ethernet() with { Up = false }));
        Assert.False(InUse(Ethernet() with { Kind = Kind.Other, Description = "Software Loopback Interface 1" }));
        Assert.False(InUse(Ethernet() with { Guid = "Ethernet" }));
    }

    [Fact]
    public void Apply_SetsDohBeforeTheServers_OnBothFamilies()
    {
        var lines = Apply(Dhcp(Ethernet()))!;
        Assert.Equal(5, lines.Count);
        Assert.Equal(@"New-Item -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\" + Guid + @"\DohInterfaceSettings\Doh\1.1.1.1' -Force | Out-Null; New-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\" + Guid + @"\DohInterfaceSettings\Doh\1.1.1.1' -Name DohFlags -PropertyType QWord -Value 1 -Force | Out-Null", lines[0]);
        Assert.Contains(@"\DohInterfaceSettings\Doh\1.0.0.1'", lines[1]);
        Assert.Contains(@"\DohInterfaceSettings\Doh6\2606:4700:4700::1111'", lines[2]);
        Assert.Contains(@"\DohInterfaceSettings\Doh6\2606:4700:4700::1001'", lines[3]);
        Assert.Equal("Set-DnsClientServerAddress -InterfaceIndex 12 -ServerAddresses '1.1.1.1','1.0.0.1','2606:4700:4700::1111','2606:4700:4700::1001'", lines[4]);
    }

    [Fact]
    public void Apply_WithoutIpv6_SetsOnlyIpv4_AndLeavesKeysAlreadyOn()
    {
        var a = new Adapter(Ethernet(ipv6: false), new Servers(null, null, [], [], [new("1.1.1.1", 17)], []));
        var lines = Apply(a)!;
        Assert.Equal(2, lines.Count);
        Assert.Contains(@"\Doh\1.0.0.1'", lines[0]);
        Assert.DoesNotContain(lines, l => l.Contains("Doh6") || l.Contains("2606"));
        Assert.Equal("Set-DnsClientServerAddress -InterfaceIndex 12 -ServerAddresses '1.1.1.1','1.0.0.1'", lines[1]);
    }

    [Fact]
    public void IsOn_NeedsCloudflareAndATemplateBit_OnEveryFamilyTheAdapterRuns()
    {
        Doh[] v4On = [new("1.0.0.1", 1), new("1.1.1.1", 17)];
        Doh[] v6On = [new("2606:4700:4700::1111", 2), new("2606:4700:4700::1001", 1)];
        var on = new Adapter(Ethernet(), new Servers(null, null, ["1.0.0.1", "1.1.1.1"], ["2606:4700:4700::1001", "2606:4700:4700::1111"], v4On, v6On));
        Assert.True(IsOn(on));
        Assert.False(IsOn(on with { Dns = on.Dns with { Doh6 = [new("2606:4700:4700::1111", 4), v6On[1]] } }));
        Assert.False(IsOn(on with { Dns = on.Dns with { Doh4 = [new("1.1.1.1", null), v4On[0]] } }));
        Assert.False(IsOn(on with { Dns = on.Dns with { Now6 = [] } }));
        Assert.True(ServersOnly(on with { Dns = on.Dns with { Doh6 = [] } }));
        Assert.False(IsOn(on with { Dns = on.Dns with { Now4 = ["1.1.1.1", "1.0.0.1", "8.8.8.8"] } }));
        // IPv6 not bound: IPv4 alone is the whole of it.
        Assert.True(IsOn(on with { Nic = on.Nic with { Ipv6 = false }, Dns = on.Dns with { Now6 = [], Doh6 = [] } }));
    }

    [Fact]
    public void Snapshot_KeepsDhcpAsDhcp_AndOnlyCloudflaresKeys()
    {
        var a = new Adapter(Ethernet(), new Servers(null, ["2001:4860:4860::8888"], ["192.168.1.254"], ["2001:4860:4860::8888"],
            [new("1.1.1.1", 17), new("9.9.9.9", 1)], []));
        var e = Snapshot(a);
        Assert.Null(e.Static4);
        Assert.Equal(["2001:4860:4860::8888"], e.Static6);
        Assert.Equal([new Doh("1.1.1.1", 17)], e.Doh4);
        Assert.True(e.Ipv6);
    }

    [Fact]
    public void Revert_PutsBackDhcpAndTheUsersOwnServersInOrder_AndEachKeyAsItWas()
    {
        var dhcp = Revert(new Entry(Guid, 12, "Ethernet", null, null, true, [], []))!;
        Assert.Equal("netsh interface ipv4 set dnsservers name=12 source=dhcp", dhcp[0]);
        Assert.Equal("netsh interface ipv6 set dnsservers name=12 source=dhcp", dhcp[1]);
        Assert.Equal(4, dhcp.Count(l => l.StartsWith("Remove-Item -LiteralPath", StringComparison.Ordinal)));

        var own = Revert(new Entry(Guid, 7, "Wi-Fi", ["8.8.8.8", "8.8.4.4", "9.9.9.9"], null, false, [new("1.1.1.1", 17), new("1.0.0.1", null)], []))!;
        Assert.Equal("netsh interface ipv4 set dnsservers name=7 source=static address=8.8.8.8 validate=no", own[0]);
        Assert.Equal("netsh interface ipv4 add dnsservers name=7 address=8.8.4.4 index=2 validate=no", own[1]);
        Assert.Equal("netsh interface ipv4 add dnsservers name=7 address=9.9.9.9 index=3 validate=no", own[2]);
        Assert.Contains(own, l => l.Contains(@"\Doh\1.1.1.1'") && l.EndsWith("-Value 17 -Force | Out-Null", StringComparison.Ordinal));
        Assert.Contains(own, l => l.StartsWith("Remove-ItemProperty", StringComparison.Ordinal) && l.Contains(@"\Doh\1.0.0.1'"));
        // No IPv6 was set, so none is put back.
        Assert.DoesNotContain(own, l => l.Contains("ipv6") || l.Contains("Doh6"));
    }

    [Fact]
    public void Revert_RefusesARecordThatIsntWhatWindowsNames()
    {
        Assert.Null(Revert(new Entry(Guid, 12, "Ethernet", ["8.8.8.8' ; calc ; '"], null, false, [], [])));
        Assert.Null(Revert(new Entry(Guid, 12, "Ethernet", ["2001:4860:4860::8888"], null, false, [], [])));
        Assert.Null(Revert(new Entry("{x}'; calc", 12, "Ethernet", null, null, false, [], [])));
        Assert.Null(Revert(new Entry(Guid, 0, "Ethernet", null, null, false, [], [])));
        Assert.Null(Apply(Dhcp(Ethernet(guid: "Ethernet"))));
    }

    [Fact]
    public void Register_OnlyCloudflaresAddresses_WithAutoUpgradeAndNoFallback()
    {
        Assert.Equal(["Add-DnsClientDohServerAddress -ServerAddress '1.1.1.1' -DohTemplate 'https://cloudflare-dns.com/dns-query' -AutoUpgrade $true -AllowFallbackToUdp $false"], Register(["1.1.1.1"]));
        Assert.Null(Register(["8.8.8.8"]));
        Assert.Equal(["Remove-DnsClientDohServerAddress -ServerAddress '1.0.0.1' -Confirm:$false"], Unregister(["1.0.0.1", "8.8.8.8"]));
    }

    [Fact]
    public void Address_DropsZonesAndJunk()
    {
        Assert.Equal("2606:4700:4700::1111", Address("2606:4700:4700:0:0:0:0:1111"));
        Assert.Null(Address("fe80::1%12"));
        Assert.Null(Address("1.1.1.1 & calc"));
        Assert.Null(Address(null));
    }
}
