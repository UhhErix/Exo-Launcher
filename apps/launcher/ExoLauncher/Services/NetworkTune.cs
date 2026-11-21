using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// PC › Overview › Setup's network row: the Ethernet and Wi-Fi adapters' own settings that add delay or drop the link in
/// a game, set in one press with Undo. Only settings with evidence behind them (.claude/research/network-tuning.md), each
/// only where the adapter's driver offers it and accepts the value: Energy-Efficient Ethernet off (Intel's own fix for the
/// I225/I226 dropouts), Intel's DMA coalescing off ("may increase network latency"), and receive and transmit buffers an
/// optimizer shrank put back to the driver's own size (too few and the adapter runs out under load); Realtek's Green
/// Ethernet, Power Saving Mode and Gigabit Lite off; receive-side scaling and Intel Wi-Fi's Throughput Booster put back to
/// their defaults (on, off). The rest of the usual "network tweaks" (interrupt moderation, flow control, offloads, Nagle,
/// NetworkThrottlingIndex, SystemResponsiveness, QoS, selective suspend) have no evidence of helping a game and are left
/// alone (the reasons are in <see cref="NetworkBest"/>). DNS is its own item in the Network group (<see cref="CloudflareDns"/>). Wi-Fi's real fix needs no press: while a game
/// runs Exo stops Windows' background scans and keeps the radio at full power (<see cref="WifiGameMode"/>). Read without administrator rights (the network class key and its
/// Ndi\Params lists); written in one step Windows asks permission for, then each changed adapter restarts so its driver
/// takes the values, which drops the connection for a few seconds: never during a call or a game. Every value is backed
/// up first. A driver update that puts its defaults back brings the row back. An Ethernet link at 100 Mbps or less on an
/// adapter that does a gigabit or more is said too (a cable or port fault Exo can't fix).
/// </summary>
internal sealed partial class NetworkTune(NetworkTune.IAdapters adapters, NetworkTune.IWriter writer, string backupPath, Func<DateTimeOffset> now)
{
    internal const string ClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// One advanced setting as the driver lists it (Ndi\Params\keyword): its value now (null: unset, so <paramref name="Default"/>
    /// applies) and the values its enum accepts with their names (empty when it isn't a list, which Exo never writes).
    /// </summary>
    internal sealed record Param(string? Value, string? Default, IReadOnlyDictionary<string, string> Allowed)
    {
        internal string? Now => Value ?? Default;
    }

    /// <summary>
    /// A physical adapter that's up: its class subkey ("0003"), connection name, the device id Windows restarts it by,
    /// Wi-Fi or Ethernet, link speed (bits/s, 0 unknown) and its advanced settings by keyword.
    /// </summary>
    internal sealed record Adapter(string Key, string Name, string Description, string DeviceId, bool Wireless, long LinkBps, IReadOnlyDictionary<string, Param> Params);

    /// <summary>
    /// A keyword Exo sets to <paramref name="To"/> on Ethernet or Wi-Fi; <paramref name="What"/> names it on the row, several
    /// keywords sharing one name. <paramref name="UpToDefault"/>: a number put back to the driver's own default only when
    /// it is below it (To is filled in from the driver when the rule applies).
    /// </summary>
    internal sealed record Rule(string Keyword, string To, string What, bool Wireless, bool DesktopOnly = false, bool UpToDefault = false);

    /// <summary>A keyword Exo wrote and what it held before (null: unset).</summary>
    internal sealed record Change(string Keyword, string To, string? Was);

    internal sealed record Entry(string Key, string Name, string DeviceId, List<Change> Changes);

    internal sealed record Backup(DateTimeOffset WrittenAt, List<Entry> Adapters);

    /// <summary>What a press would set, adapter by adapter; whether Exo's values are on (with Undo); a link held back by its cable.</summary>
    internal sealed record State(IReadOnlyList<(Adapter Adapter, IReadOnlyList<Rule> Rules)> Pending, bool Applied, (Adapter Adapter, long CanBps)? SlowLink);

    internal interface IAdapters
    {
        IReadOnlyList<Adapter> List();
        /// <summary>One adapter's advanced settings by its class subkey, whether or not it's connected (it drops while it restarts).</summary>
        IReadOnlyDictionary<string, Param>? Params(string key);
        /// <summary>A PC with a battery (a laptop or handheld moves around, so its Wi-Fi keeps roaming).</summary>
        bool Portable();
        RssPlan? RssPlan() => null;
    }

    internal interface IWriter
    {
        /// <summary>Sets each adapter's values (null deletes one) and restarts it, in one step Windows asks permission for. Null when it ran, else the line to show.</summary>
        string? Write(IReadOnlyList<(Adapter Adapter, IReadOnlyList<(string Keyword, string? Value)> Values)> writes);
    }

    /// <summary>The settings Exo sets, by keyword: Microsoft's standardized ones first, then the vendors' own names for the same thing.</summary>
    internal static readonly Rule[] Rules =
    [
        // Energy-Efficient Ethernet (802.3az) sleeps the link between packets: the I225/I226's random disconnects were
        // Intel's to fix by turning it off, and switches that handle it badly drop the link. Each driver names it its own way.
        // Desktops only: on a laptop it saves battery, and its own port is rarely the I225/I226.
        new("*EEE", "0", "energy-efficient Ethernet on", Wireless: false, DesktopOnly: true),
        new("EEE", "0", "energy-efficient Ethernet on", Wireless: false, DesktopOnly: true),
        new("AdvancedEEE", "0", "energy-efficient Ethernet on", Wireless: false, DesktopOnly: true),
        new("EEELinkAdvertisement", "0", "energy-efficient Ethernet on", Wireless: false, DesktopOnly: true),
        // Realtek's own power savers beside 802.3az: Green Ethernet and Power Saving Mode cut the link's power when the
        // cable seems short or idle (desktops only, as above); Gigabit Lite holds a gigabit link at 500 Mbps, so it's off everywhere.
        new("EnableGreenEthernet", "0", "Green Ethernet on", Wireless: false, DesktopOnly: true),
        new("PowerSavingMode", "0", "power saving mode on", Wireless: false, DesktopOnly: true),
        new("GigaLite", "0", "Gigabit Lite on", Wireless: false),
        // Intel's DMA coalescing holds packets to save power; off by default, back off if something turned it on.
        new("DMACoalescing", "0", "DMA coalescing on", Wireless: false),
        // Buffers an "optimizer" shrank: too few and the adapter runs out of them under a download's load.
        new("*ReceiveBuffers", "", "network buffers shrunk", Wireless: false, UpToDefault: true),
        new("*TransmitBuffers", "", "network buffers shrunk", Wireless: false, UpToDefault: true),
        // Receive-side scaling is on by default (Microsoft's standardized *RSS): off, every packet is handled on one core.
        // Only put back; the queue count stays the driver's.
        new("*RSS", "1", "receive-side scaling off", Wireless: false),
        // Intel Wi-Fi's Throughput Booster is off by default: on, it bursts packets and takes airtime from every other
        // device on the router (Intel's own advanced-settings page). Put back off.
        new("ThroughputBoosterEnabled", "0", "Throughput Booster on", Wireless: true),
    ];

    internal const string PnpCapabilitiesKey = "PnPCapabilities";
    internal const string NicPolicyKey = "ExoNicDevicePolicy";
    internal const string NicMaskKey = "ExoNicAffinityMask";
    internal const string RssBaseProcKey = "*RssBaseProcNumber";
    internal const string RssMaxProcKey = "*RssMaxProcNumber";
    internal const string MaxRssProcessorsKey = "*MaxRssProcessors";

    internal sealed record RssPlan(int BaseCpu, int MaxCpu, int MaxProcessors, ulong AffinityMask)
    {
        internal string MaskHex => Convert.ToHexString(BitConverter.GetBytes(AffinityMask));
    }

    internal static RssPlan? PickRssPlan(MouseCore.Topology? topology, ulong reservedMask = 0)
    {
        if (topology is null || topology.Logical < 6 || topology.Logical > 64 || topology.Cores.Count < 4) return null;
        var top = topology.Cores.Max(c => c.Efficiency);
        var fast = topology.Cores
            .Where(c => c.Efficiency == top && !c.Threads.Contains(0) && !c.Threads.Any(t => (reservedMask & (1UL << t)) != 0))
            .OrderBy(c => c.Threads.Min())
            .ToList();
        if (fast.Count < 2) return null;

        var caches = topology.L3
            .Where(l => fast.Any(c => c.Threads.Any(t => (l.Mask & (1UL << t)) != 0)))
            .ToList();
        var pool = fast;
        if (caches.Count > 1)
        {
            var secondary = fast.Where(c => c.Threads.Any(t => (caches[^1].Mask & (1UL << t)) != 0)).ToList();
            if (secondary.Count >= 2) pool = secondary;
        }

        var chosen = pool.TakeLast(2).Select(c => c.Threads.Min()).OrderBy(x => x).ToArray();
        var mask = (1UL << chosen[0]) | (1UL << chosen[1]);
        return new RssPlan(chosen[0], chosen[1], 2, mask);
    }

    private static readonly long Gigabit = 1_000_000_000;

    internal State Read()
    {
        var list = adapters.List();
        var portable = adapters.Portable();
        var rssPlan = portable ? null : adapters.RssPlan();
        var pending = list.Select(a => (Adapter: a, Rules: Decide(a, portable, rssPlan))).Where(p => p.Rules.Count > 0).ToList();
        var backup = Load();
        var applied = pending.Count == 0 && backup is { Adapters.Count: > 0 } && backup.Adapters.Any(e => list.Any(a => a.Key == e.Key));
        return new State(pending, applied, SlowLink(list));
    }

    internal static IReadOnlyList<Rule> Decide(Adapter adapter, bool portable, RssPlan? rssPlan = null)
    {
        var rules = Rules.Where(r => r.Wireless == adapter.Wireless && !(r.DesktopOnly && portable))
            .Select(r => adapter.Params.TryGetValue(r.Keyword, out var p) ? Wanted(r, p) : null).OfType<Rule>()
            .DistinctBy(r => r.Keyword, StringComparer.OrdinalIgnoreCase).ToList();
        if (!portable && adapter.Params.TryGetValue(PnpCapabilitiesKey, out var pnp))
        {
            var cur = uint.TryParse(pnp.Now, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0u;
            if ((cur & 0x10) == 0)
                rules.Add(new Rule(PnpCapabilitiesKey, (cur | 0x18).ToString(CultureInfo.InvariantCulture), "adapter power management on", adapter.Wireless, DesktopOnly: true));
        }
        if (!portable && !adapter.Wireless && rssPlan is not null && adapter.DeviceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
            AddRssRules(adapter, rssPlan, rules);
        return rules;
    }

    private static void AddRssRules(Adapter adapter, RssPlan plan, List<Rule> rules)
    {
        if (adapter.Params.TryGetValue(RssBaseProcKey, out var rssBase))
        {
            var wantBase = plan.BaseCpu.ToString(CultureInfo.InvariantCulture);
            if ((rssBase.Allowed.Count == 0 || rssBase.Allowed.ContainsKey(wantBase)) && rssBase.Now != wantBase)
                rules.Add(new Rule(RssBaseProcKey, wantBase, "Ethernet RSS sharing Core 0", Wireless: false, DesktopOnly: true));
        }
        if (adapter.Params.TryGetValue(RssMaxProcKey, out var rssMax))
        {
            var wantMax = plan.MaxCpu.ToString(CultureInfo.InvariantCulture);
            if ((rssMax.Allowed.Count == 0 || rssMax.Allowed.ContainsKey(wantMax)) && rssMax.Now != wantMax)
                rules.Add(new Rule(RssMaxProcKey, wantMax, "Ethernet RSS sharing Core 0", Wireless: false, DesktopOnly: true));
        }
        if (adapter.Params.TryGetValue(MaxRssProcessorsKey, out var rssCount))
        {
            var wantCount = plan.MaxProcessors.ToString(CultureInfo.InvariantCulture);
            if ((rssCount.Allowed.Count == 0 || rssCount.Allowed.ContainsKey(wantCount)) && rssCount.Now != wantCount)
                rules.Add(new Rule(MaxRssProcessorsKey, wantCount, "Ethernet RSS sharing Core 0", Wireless: false, DesktopOnly: true));
        }
        if (adapter.Params.TryGetValue(NicPolicyKey, out var policy) && adapter.Params.TryGetValue(NicMaskKey, out var mask))
        {
            var customPinned = policy.Value == "4" && !string.IsNullOrEmpty(mask.Value) && !mask.Value.Equals(plan.MaskHex, StringComparison.OrdinalIgnoreCase);
            if (!customPinned)
            {
                if (policy.Value != "4")
                    rules.Add(new Rule(NicPolicyKey, "4", "Ethernet interrupts sharing Core 0", Wireless: false, DesktopOnly: true));
                if (!string.Equals(mask.Value, plan.MaskHex, StringComparison.OrdinalIgnoreCase))
                    rules.Add(new Rule(NicMaskKey, plan.MaskHex, "Ethernet interrupts sharing Core 0", Wireless: false, DesktopOnly: true));
            }
        }
    }

    /// <summary>The rule as it would write here, or null: a listed value not already set, or a number below the driver's own default.</summary>
    private static Rule? Wanted(Rule rule, Param p)
    {
        if (!rule.UpToDefault) return p.Allowed.ContainsKey(rule.To) && p.Now != rule.To ? rule : null;
        return int.TryParse(p.Default, NumberStyles.None, CultureInfo.InvariantCulture, out var standard)
            && int.TryParse(p.Now, NumberStyles.None, CultureInfo.InvariantCulture, out var now) && now < standard
            ? rule with { To = p.Default! } : null;
    }

    /// <summary>An Ethernet link at 100 Mbps or less on an adapter whose speed list reaches a gigabit or more, left to negotiate its speed.</summary>
    internal static (Adapter Adapter, long CanBps)? SlowLink(IReadOnlyList<Adapter> list)
    {
        foreach (var a in list.Where(a => !a.Wireless && a.LinkBps is > 0 and <= 100_000_000))
        {
            if (!a.Params.TryGetValue("*SpeedDuplex", out var speed) || speed.Now != "0") continue; // set by hand: the user's choice
            if (TopSpeed(a) is { } top && top >= Gigabit) return (a, top);
        }
        return null;
    }

    /// <summary>
    /// The fastest speed an adapter's *SpeedDuplex list names, read from each entry's name ("2.5 Gbps Full Duplex",
    /// "1,0 Gbit/s Vollduplex"): vendors number the speeds past Microsoft's standard ones their own way (Intel's 2.5 Gbps is 2500).
    /// </summary>
    internal static long? TopSpeed(Adapter a) =>
        a.Params.TryGetValue("*SpeedDuplex", out var speed) ? speed.Allowed.Values.Select(SpeedOf).Max() : null;

    private static long? SpeedOf(string name)
    {
        if (SpeedName().Match(name) is not { Success: true } m) return null;
        var number = double.Parse(m.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        return (long)(number * (m.Groups["unit"].Value.Equals("G", StringComparison.OrdinalIgnoreCase) ? Gigabit : 1_000_000));
    }

    [GeneratedRegex(@"(?<n>\d+(?:[.,]\d+)?)\s*(?<unit>[GM])(?:bps|bit)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpeedName();

    /// <summary>What one press writes, adapter by adapter, and what puts those values back; the backup as it was before it.</summary>
    internal sealed record Staged(
        IReadOnlyList<(Adapter Adapter, IReadOnlyList<Rule> Rules)> Pending,
        Backup? Before,
        IReadOnlyList<(Adapter Adapter, IReadOnlyList<(string Keyword, string? Value)> Values)> Writes,
        IReadOnlyList<(Adapter Adapter, IReadOnlyList<(string Keyword, string? Value)> Values)> Reverts);

    /// <summary>Exo's values on every adapter that has one to set, backed up first. Null when done or nothing to do.</summary>
    internal string? Apply()
    {
        if (Stage(out var problem) is not { } staged) return problem;
        return Finish(staged, writer.Write(staged.Writes));
    }

    /// <summary>
    /// Backs up what a press changes and says what it writes (Best settings' Network group runs it in its one elevated
    /// script, <see cref="NetworkBest"/>). Null with no problem when there is nothing to set.
    /// </summary>
    internal Staged? Stage(out string? problem)
    {
        problem = null;
        var state = Read();
        if (state.Pending.Count == 0) return null;
        var before = Load();
        var entries = before?.Adapters.Where(e => state.Pending.All(p => p.Adapter.Key != e.Key)).ToList() ?? [];
        var reverts = new List<(Adapter, IReadOnlyList<(string, string?)>)>();
        foreach (var (adapter, rules) in state.Pending)
        {
            var kept = before?.Adapters.Find(e => e.Key == adapter.Key)?.Changes ?? [];
            // A keyword Exo wrote before keeps what it held first, so Undo goes back to the user's value, not Exo's.
            var changes = kept.Where(c => rules.All(r => !r.Keyword.Equals(c.Keyword, StringComparison.OrdinalIgnoreCase))).ToList();
            var now = rules.Select(r => kept.Find(c => c.Keyword.Equals(r.Keyword, StringComparison.OrdinalIgnoreCase)) ?? new Change(r.Keyword, r.To, adapter.Params[r.Keyword].Value)).ToList();
            changes.AddRange(now);
            entries.Add(new Entry(adapter.Key, adapter.Name, adapter.DeviceId, changes));
            // Put back: each keyword this press writes, to what the adapter held just before it.
            reverts.Add((adapter, rules.Select(r => (r.Keyword, adapter.Params[r.Keyword].Value)).ToList()));
        }
        if (!Save(new Backup(now(), entries)))
        {
            problem = "Your current settings couldn't be saved for Undo, so nothing changed.";
            return null;
        }
        var writes = state.Pending.Select(p => (p.Adapter, (IReadOnlyList<(string, string?)>)p.Rules.Select(r => (r.Keyword, (string?)r.To)).ToList())).ToList();
        return new Staged(state.Pending, before, writes, reverts);
    }

    /// <summary>After the write ran (<paramref name="refused"/>: the line when it didn't): what took, the backup put back when nothing did.</summary>
    internal string? Finish(Staged staged, string? refused)
    {
        if (refused is not null)
        {
            Restore(staged.Before);
            return refused;
        }
        var missed = staged.Pending.Where(p => adapters.Params(p.Adapter.Key) is not { } now
            || p.Rules.Any(r => !now.TryGetValue(r.Keyword, out var v) || v.Now != r.To)).ToList();
        if (missed.Count == staged.Pending.Count)
        {
            Restore(staged.Before);
            return "Windows didn't take the change.";
        }
        AppLog.Info($"Network: {string.Join("; ", staged.Pending.Select(p => $"{p.Adapter.Description}: {string.Join(", ", p.Rules.Select(r => $"{r.Keyword}={r.To}"))}"))}{(missed.Count > 0 ? $"; {missed.Count} refused" : "")}.");
        return missed.Count > 0 ? $"{string.Join(" and ", missed.Select(m => m.Adapter.Name))} didn't take the change." : null;
    }

    /// <summary>The press was put back by the elevated script (it cut the connection): the backup as it was before it.</summary>
    internal void PutBack(Staged staged) => Restore(staged.Before);

    /// <summary>Puts back what Exo changed on every adapter still here, and restarts them.</summary>
    internal string? Undo()
    {
        if (Load() is not { Adapters.Count: > 0 } backup) return "There's nothing to undo.";
        var list = adapters.List();
        var writes = backup.Adapters
            .Select(e => (Adapter: list.FirstOrDefault(a => a.Key == e.Key), e.Changes))
            .Where(w => w.Adapter is not null)
            .Select(w => (w.Adapter!, (IReadOnlyList<(string, string?)>)w.Changes.Select(c => (c.Keyword, c.Was)).ToList()))
            .ToList();
        if (writes.Count == 0)
        {
            Forget();
            return null;
        }
        if (writer.Write(writes) is { } refused) return refused;
        Forget();
        AppLog.Info($"Network: {writes.Count} adapter(s) put back.");
        return null;
    }

    /// <summary>What the Setup row says is wrong, from the pending rules' names ("energy-efficient Ethernet on and network buffers shrunk").</summary>
    internal static string Names(IEnumerable<Rule> rules)
    {
        var names = rules.Select(r => r.What).Distinct().ToList();
        return names.Count <= 1 ? names.FirstOrDefault() ?? "" : string.Join(", ", names[..^1]) + " and " + names[^1];
    }

    /// <summary>A link speed in words: "100 Mbps", "2.5 Gbps".</summary>
    internal static string Speed(long bps) =>
        bps >= Gigabit ? (bps / (double)Gigabit).ToString("0.#", CultureInfo.InvariantCulture) + " Gbps" : (bps / 1_000_000) + " Mbps";

    /// <summary>Only what Windows itself names reaches reg.exe and pnputil: a four-digit class subkey, a plain keyword, a number, a PCI or USB device id.</summary>
    [GeneratedRegex(@"^\d{4}$")]
    internal static partial Regex KeyName();

    [GeneratedRegex(@"^\*?[A-Za-z0-9_]{1,64}$")]
    internal static partial Regex KeywordName();

    [GeneratedRegex(@"^-?\d{1,10}$")]
    internal static partial Regex NumberValue();

    [GeneratedRegex(@"^[0-9A-Fa-f]{16}$")]
    internal static partial Regex HexMaskValue();

    [GeneratedRegex(@"^(?:PCI|USB)\\[A-Z0-9_&]+\\[A-Z0-9_&]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex DeviceId();

    private Backup? Load()
    {
        try { return File.Exists(backupPath) ? JsonSerializer.Deserialize<Backup>(File.ReadAllText(backupPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    private bool Save(Backup backup)
    {
        try
        {
            var temp = backupPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(backup));
            File.Move(temp, backupPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Network: the backup couldn't be saved: " + ex.Message);
            return false;
        }
    }

    private void Restore(Backup? before)
    {
        if (before is null) Forget();
        else Save(before);
    }

    private void Forget()
    {
        try { DeletionGate.DeleteFile(backupPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Network: the backup couldn't be removed: " + ex.Message); }
    }
}
