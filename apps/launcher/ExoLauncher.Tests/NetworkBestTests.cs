using ExoLauncher.Services;
using static ExoLauncher.Services.NetworkBest;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Best settings' Network group could go wrong silently (none of it touches this PC: Windows is a fake here):
/// - the record of what was there is written after the change, or a second press overwrites the user's DNS with Cloudflare's;
/// - a declined prompt leaves a record of a change that never happened;
/// - an item that cut the connection is reported as set, or its record kept, so Off "puts back" the wrong thing;
/// - an item the player turned off comes back with the next Use best settings, or Off forgets to remember it;
/// - DoH that Windows refused is reported as done, or an adapter with no IPv6 is held as not done forever;
/// - the script sets an item with no lookup check after it, or puts the wrong item back;
/// - TCP settings that are already Windows' defaults are written, or put back to something they never were;
/// - the adapters' own settings skip their backup when they run inside the group's script;
/// - the check says things improved when the numbers are noise.
/// </summary>
public sealed class NetworkBestTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("exo-netbest-").FullName;
    private string SavedPath => Path.Combine(_folder, "exo-network-best.json");
    private string TunePath => Path.Combine(_folder, "exo-network-tune.json");

    public void Dispose() => DeletionGate.DeleteDirectory(_folder, recursive: true);

    private static readonly CloudflareDns.Doh[] V4On = [new("1.1.1.1", 1), new("1.0.0.1", 1)];
    private static readonly CloudflareDns.Doh[] V6On = [new("2606:4700:4700::1111", 1), new("2606:4700:4700::1001", 1)];

    private sealed class FakeSystem : ISystem
    {
        public List<CloudflareDns.Adapter> List { get; } = [];
        public Tcp? TcpNow { get; set; } = new(3, true);
        public bool IsOnline { get; set; } = true;
        public int? Code { get; set; } = Ran;
        public string? Refused { get; set; }
        public List<string> Scripts { get; } = [];
        /// <summary>What Windows does when the script runs (before its exit code comes back).</summary>
        public Action<string>? OnRun { get; set; }
        public HashSet<string> Known { get; } = [.. CloudflareDns.V4, .. CloudflareDns.V6];

        public IReadOnlyList<CloudflareDns.Adapter> Adapters() => List;
        public IReadOnlySet<string> Registered() => Known;
        public Tcp? Tcp() => TcpNow;
        public (int? Code, string? Problem) Elevated(string script)
        {
            Scripts.Add(script);
            if (Refused is not null) return (null, Refused);
            OnRun?.Invoke(script);
            return (Code, null);
        }
        public bool Online() => IsOnline;
        public bool Running { get; set; }
        public bool StillRunning() => Running;
        public Sample Measure() => new(20, 2, 1, 30);

        /// <summary>Windows taking Cloudflare's DNS on every adapter (with or without its DoH keys).</summary>
        public void TakeDns(bool doh = true)
        {
            for (var i = 0; i < List.Count; i++)
            {
                var a = List[i];
                List[i] = a with { Dns = a.Dns with { Static4 = CloudflareDns.V4, Now4 = CloudflareDns.V4, Static6 = a.Nic.Ipv6 ? CloudflareDns.V6 : null,
                    Now6 = a.Nic.Ipv6 ? CloudflareDns.V6 : [], Doh4 = doh ? V4On : [], Doh6 = doh && a.Nic.Ipv6 ? V6On : [] } };
            }
        }
    }

    private sealed class NoAdapters : NetworkTune.IAdapters
    {
        public IReadOnlyList<NetworkTune.Adapter> List() => [];
        public IReadOnlyDictionary<string, NetworkTune.Param>? Params(string key) => null;
        public bool Portable() => false;
    }

    private sealed class NoWriter : NetworkTune.IWriter
    {
        public string? Write(IReadOnlyList<(NetworkTune.Adapter Adapter, IReadOnlyList<(string Keyword, string? Value)> Values)> writes) => "not here";
    }

    private (NetworkBest Best, FakeSystem System) Make(NetworkTune.IAdapters? adapters = null)
    {
        var system = new FakeSystem();
        var tune = new NetworkTune(adapters ?? new NoAdapters(), new NoWriter(), TunePath, () => DateTimeOffset.UnixEpoch);
        return (new NetworkBest(tune, system, SavedPath, () => DateTimeOffset.UnixEpoch), system);
    }

    private static CloudflareDns.Adapter Ethernet(bool ipv6 = true) => CloudflareDnsTests.Dhcp(CloudflareDnsTests.Ethernet(ipv6));

    [Fact]
    public void Read_OffersDnsOnlyForAdaptersInUse_AndTcpOnlyWhenSomethingChangedIt()
    {
        var (best, system) = Make();
        system.List.Add(CloudflareDnsTests.Dhcp(CloudflareDnsTests.Ethernet() with { Description = "Hyper-V Virtual Ethernet Adapter", DeviceId = @"ROOT\VMS_MP\0000" }));
        Assert.Empty(best.Read().Items);

        system.List.Add(Ethernet());
        system.TcpNow = new(0, true);
        var items = best.Read().Items;
        Assert.Equal([DnsKey, TcpKey], items.Select(i => i.Key));
        Assert.All(items, i => Assert.False(i.On));
        Assert.Equal("Receive window tuning disabled.", items[1].Detail);
    }

    [Fact]
    public void Apply_RecordsFirst_RunsOneScriptWithACheckAfterEachItem_AndSaysNothingWhenAllWent()
    {
        var (best, system) = Make();
        system.List.Add(Ethernet());
        system.TcpNow = new(0, false);
        system.OnRun = _ =>
        {
            // The record is on disk before Windows changes anything.
            Assert.True(File.Exists(SavedPath));
            system.TakeDns();
            system.TcpNow = new(3, true);
        };

        Assert.Null(best.Apply());
        var script = Assert.Single(system.Scripts);
        Assert.Contains("# dns\n", script);
        Assert.Contains("# tcp\n", script);
        Assert.Equal(2, script.Split("if(-not (T)){").Length - 1);
        Assert.Contains("netsh interface ipv4 set dnsservers name=12 source=dhcp", script);
        Assert.Contains("netsh interface tcp set global autotuninglevel=normal", script);
        Assert.Contains("netsh interface tcp set global autotuninglevel=disabled", script);
        Assert.Contains("netsh interface tcp set global rss=disabled", script);
        Assert.EndsWith($"exit ({Ran} + $r)\n", script, StringComparison.Ordinal);

        var state = best.Read();
        Assert.True(state.Applied);
        Assert.All(state.Items, i => Assert.True(i.CanUndo));
        Assert.NotNull(best.LastCheck);
        Assert.Equal("No change you'd notice: your connection was already fine.", best.LastCheck!.Said);
    }

    [Fact]
    public void Apply_SecondPressKeepsTheUsersOwnDns()
    {
        var (best, system) = Make();
        var own = Ethernet() with { Dns = new CloudflareDns.Servers(["8.8.8.8"], null, ["8.8.8.8"], [], [], []) };
        system.List.Add(own);
        system.OnRun = _ => system.TakeDns(doh: false);
        Assert.NotNull(best.Apply()); // encryption refused: said, and the servers stay
        system.OnRun = _ => system.TakeDns();
        Assert.Null(best.Apply());

        system.Scripts.Clear();
        system.OnRun = _ => system.List[0] = own;
        Assert.Null(best.TurnOff(DnsKey));
        var off = Assert.Single(system.Scripts);
        Assert.Contains("netsh interface ipv4 set dnsservers name=12 source=static address=8.8.8.8 validate=no", off);
    }

    [Fact]
    public void Apply_DohRefused_SaysSoPlainly_AndKeepsTheRecordForOff()
    {
        var (best, system) = Make();
        system.List.Add(Ethernet());
        system.OnRun = _ => system.TakeDns(doh: false);
        Assert.Equal("Encryption didn't turn on for Ethernet, so its lookups go to Cloudflare unencrypted.", best.Apply());
        Assert.True(best.Read().Items.Single().CanUndo);
    }

    [Fact]
    public void Apply_NoIpv6_IsDoneWithIpv4_AndSaysSo()
    {
        var (best, system) = Make();
        system.List.Add(Ethernet(ipv6: false));
        system.OnRun = _ => system.TakeDns();
        Assert.Null(best.Apply());
        Assert.DoesNotContain("2606:", system.Scripts.Single());
        var item = best.Read().Items.Single();
        Assert.True(item.On);
        Assert.EndsWith("IPv4 only on Ethernet: it has no IPv6.", item.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_ItemThatCutTheConnection_IsPutBackAndForgotten()
    {
        var (best, system) = Make();
        system.List.Add(Ethernet());
        system.Code = Ran + PutBackBit[DnsKey];
        Assert.Equal("DNS lookups stopped answering with Cloudflare, so Exo put your DNS back.", best.Apply());
        var item = best.Read().Items.Single();
        Assert.False(item.On);
        Assert.False(item.CanUndo);
    }

    [Fact]
    public void Apply_Declined_LeavesNoRecord()
    {
        var (best, system) = Make();
        system.List.Add(Ethernet());
        system.Refused = "Windows' permission was declined, so nothing changed.";
        Assert.Equal(system.Refused, best.Apply());
        Assert.False(best.Read().Items.Single().CanUndo);
    }

    [Fact]
    public void Apply_Offline_ChangesNothing()
    {
        var (best, system) = Make();
        system.List.Add(Ethernet());
        system.IsOnline = false;
        Assert.StartsWith("You're offline", best.Apply(), StringComparison.Ordinal);
        Assert.Empty(system.Scripts);
    }

    [Fact]
    public void Apply_RegistersOnlyWhatWindowsDoesntKnow_AndOffTakesThoseBack()
    {
        var (best, system) = Make();
        system.List.Add(Ethernet());
        system.Known.Remove("1.0.0.1");
        system.OnRun = _ => system.TakeDns();
        Assert.Null(best.Apply());
        var script = system.Scripts.Single();
        Assert.Contains("Add-DnsClientDohServerAddress -ServerAddress '1.0.0.1'", script);
        Assert.DoesNotContain("Add-DnsClientDohServerAddress -ServerAddress '1.1.1.1'", script);

        system.OnRun = _ => system.List[0] = Ethernet();
        Assert.Null(best.TurnOff(DnsKey));
        Assert.Contains("Remove-DnsClientDohServerAddress -ServerAddress '1.0.0.1' -Confirm:$false", system.Scripts[^1]);
        Assert.DoesNotContain("Remove-DnsClientDohServerAddress -ServerAddress '1.1.1.1'", system.Scripts[^1]);
    }

    [Fact]
    public void TurnOff_IsRemembered_UntilTheSwitchIsOnAgain()
    {
        var (best, system) = Make();
        system.List.Add(Ethernet());
        system.OnRun = _ => system.TakeDns();
        Assert.Null(best.Apply());
        system.OnRun = _ => system.List[0] = Ethernet();
        Assert.Null(best.TurnOff(DnsKey));

        var item = best.Read().Items.Single();
        Assert.True(item.Off);
        Assert.False(item.On);
        Assert.True(best.Read().Applied); // an item turned off doesn't count as left to set
        system.Scripts.Clear();
        Assert.Null(best.Apply()); // Use best settings leaves it
        Assert.Empty(system.Scripts);

        system.OnRun = _ => system.TakeDns();
        Assert.Null(best.Apply([DnsKey])); // its switch on again
        Assert.Single(system.Scripts);
        Assert.False(best.Read().Items.Single().Off);
    }

    [Fact]
    public void TurnOff_DnsExoDidntSet_SaysWhere()
    {
        var (best, system) = Make();
        system.List.Add(Ethernet());
        system.TakeDns();
        Assert.Contains("Windows Settings", best.TurnOff(DnsKey));
        Assert.Empty(system.Scripts);
    }

    [Fact]
    public void Tcp_PutsBackWhatItWas()
    {
        var (best, system) = Make();
        system.TcpNow = new(1, true);
        system.OnRun = _ => system.TcpNow = new(3, true);
        Assert.Null(best.Apply([TcpKey]));
        system.OnRun = _ => system.TcpNow = new(1, true);
        Assert.Null(best.TurnOff(TcpKey));
        Assert.Contains("netsh interface tcp set global autotuninglevel=highlyrestricted", system.Scripts[^1]);
        Assert.Contains("netsh interface tcp set global rss=enabled", system.Scripts[^1]);
    }

    private sealed class EeeAdapters : NetworkTune.IAdapters
    {
        public Dictionary<string, NetworkTune.Param> Settings { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["*EEE"] = new("1", "1", new Dictionary<string, string> { ["0"] = "Disabled", ["1"] = "Enabled" }),
        };
        public IReadOnlyList<NetworkTune.Adapter> List() =>
            [new("0001", "Ethernet", "Intel(R) Ethernet Controller I226-V", @"PCI\VEN_8086&DEV_125C&SUBSYS_88671043&REV_06\6&102E3ADF&0&0048020A", false, 2_500_000_000, Settings)];
        public IReadOnlyDictionary<string, NetworkTune.Param>? Params(string key) => Settings;
        public bool Portable() => false;
    }

    [Fact]
    public void Adapter_RunsInTheSameScript_BackedUpFirst_WithItsOwnPutBack()
    {
        var adapters = new EeeAdapters();
        var (best, system) = Make(adapters);
        system.List.Add(Ethernet());
        system.OnRun = _ =>
        {
            Assert.True(File.Exists(TunePath));
            adapters.Settings["*EEE"] = adapters.Settings["*EEE"] with { Value = "0" };
            system.TakeDns();
        };
        Assert.Null(best.Apply());
        var script = system.Scripts.Single();
        Assert.Contains("# adapter\n", script);
        Assert.Contains("/v \"*EEE\" /t REG_SZ /d 0 /f /reg:64", script);
        Assert.Contains("/v \"*EEE\" /t REG_SZ /d 1 /f /reg:64", script); // its put back, after the check
        Assert.Contains("/restart-device", script);
        Assert.True(script.IndexOf("# adapter", StringComparison.Ordinal) < script.IndexOf("# dns", StringComparison.Ordinal));
        Assert.All(best.Read().Items, i => Assert.True(i.On));
    }

    [Fact]
    public void Adapter_CutTheConnection_BackupGoesBackToBefore()
    {
        var adapters = new EeeAdapters();
        var (best, system) = Make(adapters);
        system.Code = Ran + PutBackBit[AdapterKey];
        Assert.Equal("Your adapters' new settings cut the connection, so Exo put them back.", best.Apply());
        Assert.False(File.Exists(TunePath));
    }

    [Theory]
    [InlineData(30, 12, "Lookups fell from 30 ms to 12 ms.")]
    [InlineData(30, 29, "No change you'd notice: your connection was already fine.")]
    [InlineData(10, 12, "No change you'd notice: your connection was already fine.")]
    [InlineData(12, 30, "Lookups rose from 12 ms to 30 ms.")]
    public void Compare_SaysOnlyWhatIsPastTheNoise(double before, double after, string said) =>
        Assert.Equal(said, Compare(new Sample(20, 2, 1, before), new Sample(21, 2, 1, after)));

    [Fact]
    public void Script_RunsCmdLinesAsWritten_AndChecksALookup()
    {
        Assert.Equal("C '\"C:\\Windows\\system32\\reg.exe\" add ''x'''", Cmd("\"C:\\Windows\\system32\\reg.exe\" add 'x'"));
        Assert.Contains("Resolve-DnsName getexo.app -Type A -DnsOnly -QuickTimeout", Prelude);
    }

    // Review fixes (PR #37's Devin threads).

    [Fact]
    public void Retry_DeclinedOrPutBack_KeepsTheRecordFromTheFirstPress()
    {
        var (best, system) = Make();
        var own = Ethernet() with { Dns = new CloudflareDns.Servers(["8.8.8.8"], null, ["8.8.8.8"], [], [], []) };
        system.List.Add(own);
        system.OnRun = _ => system.TakeDns(doh: false);
        Assert.NotNull(best.Apply());

        system.OnRun = null;
        system.Refused = "Windows' permission was declined, so nothing changed.";
        Assert.Equal(system.Refused, best.Apply());
        system.Refused = null;
        system.Code = Ran + PutBackBit[DnsKey];
        Assert.NotNull(best.Apply());

        system.Code = Ran;
        system.OnRun = _ => system.List[0] = own;
        Assert.Null(best.TurnOff(DnsKey));
        Assert.Contains("source=static address=8.8.8.8 validate=no", system.Scripts[^1]);
    }

    [Fact]
    public void TurnOff_UsesTheAdaptersNumberNow_AndWaitsForOneThatIsAway()
    {
        var (best, system) = Make();
        system.List.Add(Ethernet());
        system.OnRun = _ => system.TakeDns();
        Assert.Null(best.Apply());

        // Reinstalled: same GUID, a new interface number.
        system.List[0] = system.List[0] with { Nic = system.List[0].Nic with { Index = 31 } };
        system.OnRun = _ => system.List[0] = Ethernet() with { Nic = Ethernet().Nic with { Index = 31 } };
        Assert.Null(best.TurnOff(DnsKey));
        Assert.Contains("set dnsservers name=31 source=dhcp", system.Scripts[^1]);
        Assert.DoesNotContain("name=12", system.Scripts[^1]);

        system.List[0] = Ethernet();
        system.OnRun = _ => system.TakeDns();
        Assert.Null(best.Apply([DnsKey]));
        system.List.Clear();
        system.Scripts.Clear();
        Assert.Contains("isn't connected", best.TurnOff(DnsKey));
        Assert.Empty(system.Scripts);
        Assert.Contains(DnsKey, System.Text.Json.JsonSerializer.Deserialize<Saved>(File.ReadAllText(SavedPath))!.Off); // the player's Off holds
        system.List.Add(Ethernet());
        system.TakeDns();
        system.OnRun = _ => system.List[0] = Ethernet();
        Assert.Null(best.TurnOff(DnsKey));
    }

    [Fact]
    public void TurnOff_ThatWindowsDidntTake_KeepsItsRecordToTryAgain()
    {
        var (best, system) = Make();
        system.List.Add(Ethernet());
        system.OnRun = _ => system.TakeDns();
        Assert.Null(best.Apply());
        system.OnRun = null; // the restore ran, but Windows still shows Cloudflare
        Assert.StartsWith("Windows didn't put back the DNS of Ethernet", best.TurnOff(DnsKey), StringComparison.Ordinal);
        var item = best.Read().Items.Single();
        Assert.True(item.CanUndo);
        Assert.False(item.Off);

        system.TcpNow = new(0, true);
        system.OnRun = _ => system.TcpNow = new(3, true);
        Assert.Null(best.Apply([TcpKey]));
        system.OnRun = null;
        Assert.StartsWith("Windows didn't put back its old TCP settings", best.TurnOff(TcpKey), StringComparison.Ordinal);
        Assert.True(best.Read().Items.Single(i => i.Key == TcpKey).CanUndo);
    }

    [Fact]
    public async Task Overlapping_Presses_TheSecondIsToldToWait()
    {
        var (best, system) = Make();
        system.List.Add(Ethernet());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new SemaphoreSlim(0, 1);
        system.OnRun = _ =>
        {
            entered.TrySetResult();
            release.Wait();
            system.TakeDns();
        };

        var first = Task.Factory.StartNew(() =>
        {
            try { return best.Apply(); }
            finally { entered.TrySetResult(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        string? second = "not asked";
        string? firstResult;
        try
        {
            await entered.Task;
            second = best.TurnOff(DnsKey);
        }
        finally
        {
            release.Release();
            firstResult = await first;
        }

        Assert.Null(firstResult);
        Assert.Equal(Busy, second);
        Assert.Single(system.Scripts);
        Assert.True(best.Read().Items.Single().CanUndo);

        // A script that outlived its wait holds every change until it ends.
        system.Running = true;
        system.Scripts.Clear();
        Assert.Equal(Busy, best.TurnOff(DnsKey));
        Assert.Equal(Busy, best.Apply([TcpKey]));
        Assert.Empty(system.Scripts);
    }

    [Fact]
    public void Reentrant_Press_OnSameThread_IsToldToWait()
    {
        var (best, system) = Make();
        system.List.Add(Ethernet());
        string? reentrant = "not asked";
        system.OnRun = _ =>
        {
            reentrant = best.TurnOff(DnsKey);
            system.TakeDns();
        };
        Assert.Null(best.Apply());
        Assert.Equal(Busy, reentrant);
        Assert.Single(system.Scripts);
        Assert.True(best.Read().Items.Single().CanUndo);
    }

    [Fact]
    public void Unfinished_KeepsEveryRecord()
    {
        var adapters = new EeeAdapters();
        var (best, system) = Make(adapters);
        system.List.Add(Ethernet());
        system.Code = Unfinished;
        Assert.StartsWith("The change didn't finish in time", best.Apply(), StringComparison.Ordinal);
        Assert.True(File.Exists(TunePath));
        Assert.True(best.Read().Items.Single(i => i.Key == DnsKey).CanUndo);
    }

    [Fact]
    public void Partial_OneFamilyTaken_KeepsTheRecord()
    {
        var (best, system) = Make();
        var own = Ethernet() with { Dns = new CloudflareDns.Servers(["9.9.9.9"], ["2620:fe::fe"], ["9.9.9.9"], ["2620:fe::fe"], [], []) };
        system.List.Add(own);
        system.OnRun = _ => system.List[0] = own with { Dns = own.Dns with { Static4 = CloudflareDns.V4, Now4 = CloudflareDns.V4, Doh4 = V4On } };
        Assert.Equal("Ethernet didn't take Cloudflare's DNS.", best.Apply());
        Assert.True(best.Read().Items.Single().CanUndo);
        system.OnRun = _ => system.List[0] = own;
        Assert.Null(best.TurnOff(DnsKey));
        Assert.Contains("netsh interface ipv6 set dnsservers name=12 source=static address=2620:fe::fe validate=no", system.Scripts[^1]);
    }

    [Fact]
    public void Fallback_IsNotEncrypted_AndIsWrittenWithout()
    {
        var a = Ethernet(ipv6: false) with { Dns = new CloudflareDns.Servers(CloudflareDns.V4, null, CloudflareDns.V4, [], [new("1.1.1.1", 5), new("1.0.0.1", 6)], []) };
        Assert.False(CloudflareDns.IsOn(a));
        var lines = CloudflareDns.Apply(a)!;
        Assert.Equal(2, lines.Count(l => l.Contains("-Value 1 -Force")));
        Assert.Contains(CloudflareDns.Revert(CloudflareDns.Snapshot(a))!, l => l.Contains(@"\Doh\1.1.1.1'") && l.Contains("-Value 5 -Force"));
    }

    [Fact]
    public void Prelude_ChecksRedundantDnsTargets()
    {
        Assert.Contains("getexo.app", Prelude);
        Assert.Contains("one.one.one.one", Prelude);
        Assert.Contains("dns.msftncsi.com", Prelude);
        Assert.Contains("1.1.1.1", Prelude);
    }
}
