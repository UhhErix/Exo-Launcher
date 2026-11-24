using System.Globalization;
using System.Text;
using System.Text.Json;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Best settings › Network: the network items Exo sets, each with its own switch, an exact record of what was there and
/// detection read from Windows. Use best settings (or a switch turned on) sets every item that's on and not yet set in one
/// PowerShell script Windows asks permission for once. After each item the script clears the DNS cache and checks a lookup
/// still answers; one that doesn't is put back at once, there, and the answer says so. The items:
/// <list type="bullet">
/// <item><c>adapter</c>: the adapters' own settings (<see cref="NetworkTune"/>: Energy-Efficient Ethernet and Realtek's
/// Green Ethernet, Power Saving Mode and Gigabit Lite off, DMA coalescing off, shrunk buffers, receive-side scaling and
/// Throughput Booster back to their defaults).</item>
/// <item><c>dns</c>: Cloudflare DNS, encrypted (<see cref="CloudflareDns"/>).</item>
/// <item><c>tcp</c>: Windows' TCP receive window auto-tuning back to normal and receive-side scaling on, only where
/// something turned them off (netsh's own values; "disabled" caps every download on a distant server).</item>
/// </list>
/// Left out, with why: interrupt moderation off (microseconds of DPC time at a large CPU cost, nothing a game's tick
/// sees), flow control and offloads (no gaming evidence; offloads off only works around one buggy driver), RSS queue
/// counts (driver-specific), NetworkThrottlingIndex (a Vista-era multimedia cap games never reach; off raises NDIS DPC
/// latency), SystemResponsiveness (Windows clamps below 10 to 20, so the usual 0 does nothing), TcpAckFrequency and
/// TCPNoDelay (games run on UDP or set TCP_NODELAY themselves; TCPNoDelay isn't a value TCP/IP reads), ECN (Windows'
/// default), Delivery Optimization (internet peers are opt-in, its uploads back off by themselves (LEDBAT), and the
/// choice can't be read without administrator rights), Wi-Fi band, roaming and MIMO power save (the user's router and
/// room decide; Exo stops scans during a game instead, <see cref="WifiGameMode"/>), and never network discovery, NetBIOS,
/// SMB or IPv6 (desktop adapters also set <c>PnPCapabilities</c> to stop Windows powering down the NIC). Turning an item's switch off puts back exactly what it recorded and keeps it out of Best settings until
/// the switch is on again.
/// </summary>
internal sealed partial class NetworkBest(NetworkTune tune, NetworkBest.ISystem system, string path, Func<DateTimeOffset> now)
{
    internal const string AdapterKey = "adapter", DnsKey = "dns", TcpKey = "tcp";
    internal static readonly string[] Keys = [AdapterKey, DnsKey, TcpKey];

    /// <summary>netsh's receive window auto-tuning levels, by the number Windows reports them as (3 is the default).</summary>
    internal static readonly string[] AutoTuning = ["disabled", "highlyrestricted", "restricted", "normal", "experimental"];
    private const int Normal = 3;

    /// <summary>Windows' TCP settings: the Internet template's receive window auto-tuning level, and receive-side scaling.</summary>
    internal sealed record Tcp(int AutoTuning, bool Rss);

    /// <summary>
    /// What Exo keeps: the items the player turned off, each adapter's DNS before Exo set Cloudflare's, Cloudflare's
    /// addresses Exo registered with their template, and Windows' TCP settings before Exo put them back.
    /// </summary>
    internal sealed record Saved(DateTimeOffset WrittenAt, List<string> Off, List<CloudflareDns.Entry> Dns, List<string> Registered, Tcp? Tcp);

    /// <summary>One item of the group: on (set, or nothing to set), whether Exo has something to put back, and whether the player turned it off.</summary>
    internal sealed record Item(string Key, string Label, string Detail, bool On, bool CanUndo, bool Off);

    /// <summary>The group as read: its items (only what this PC has), and the adapters' own reading (its slow link, for This PC).</summary>
    internal sealed record State(IReadOnlyList<Item> Items, NetworkTune.State Tune)
    {
        /// <summary>Every item the player keeps on is set.</summary>
        internal bool Applied => Items.Count > 0 && Items.All(i => i.On || i.Off);
    }

    /// <summary>A quick measure: ping to Cloudflare (1.1.1.1) and to the router, its jitter, and a DNS lookup's time, in milliseconds (null: no answer).</summary>
    internal sealed record Sample(double? Ping, double? Jitter, double? Router, double? Lookup);

    /// <summary>A check before and after a press, and what it says in words.</summary>
    internal sealed record Check(Sample? Before, Sample After, string Said);

    internal interface ISystem
    {
        /// <summary>Every network adapter with its DNS, unfiltered (<see cref="CloudflareDns.InUse"/> picks).</summary>
        IReadOnlyList<CloudflareDns.Adapter> Adapters();
        /// <summary>The addresses Windows knows a DoH template for, Cloudflare's among them.</summary>
        IReadOnlySet<string> Registered();
        /// <summary>Windows' TCP settings, or null when they can't be read.</summary>
        Tcp? Tcp();
        /// <summary>Runs the script as administrator, Windows asking once: its exit code (<see cref="Unfinished"/> when it was still running at the end of the wait), or the line to show when it didn't run.</summary>
        (int? Code, string? Problem) Elevated(string script);
        /// <summary>
        /// An elevated script that outlived its wait is still running: no other change starts until it ends, so it can't
        /// change Windows under a record another press just wrote.
        /// </summary>
        bool StillRunning();
        /// <summary>A DNS lookup answers now.</summary>
        bool Online();
        Sample Measure();
    }

    /// <summary>The script's exit codes: 8 ran to its end, plus a bit for each item it put back because a lookup stopped answering.</summary>
    internal const int Ran = 8;
    internal static readonly IReadOnlyDictionary<string, int> PutBackBit = new Dictionary<string, int> { [AdapterKey] = 1, [DnsKey] = 2, [TcpKey] = 4 };

    internal Check? LastCheck { get; private set; }

    internal State Read()
    {
        var saved = Load();
        var off = saved?.Off ?? [];
        var items = new List<Item>();
        var net = tune.Read();
        if (net.Pending.Count > 0)
            items.Add(new(AdapterKey, "Adapter settings for games", Capital(string.Join("; ", net.Pending.Select(p => NetworkTune.Names(p.Rules) + (net.Pending.Count > 1 ? $" on your {(p.Adapter.Wireless ? "Wi-Fi" : "Ethernet")}" : "")))) + ". Briefly reconnects the adapter for a few seconds.", false, net.Applied, off.Contains(AdapterKey)));
        else if (net.Applied)
            items.Add(new(AdapterKey, "Adapter settings for games", "Power saving that drops the link is off.", true, true, off.Contains(AdapterKey)));

        var inUse = InUse();
        if (inUse.Count > 0)
        {
            var on = inUse.All(CloudflareDns.IsOn);
            var noV6 = inUse.Where(a => !a.Nic.Ipv6).Select(a => a.Nic.Name).ToList();
            var detail = "Faster lookups, and your DNS is private." + (noV6.Count > 0 ? $" IPv4 only on {string.Join(" and ", noV6)}: it has no IPv6." : "");
            items.Add(new(DnsKey, "Cloudflare DNS, encrypted", detail, on, saved?.Dns.Any(e => inUse.Any(a => a.Nic.Guid == e.Guid)) == true, off.Contains(DnsKey)));
        }

        if (system.Tcp() is { } tcp && (TcpOff(tcp) is not null || saved?.Tcp is not null))
            items.Add(new(TcpKey, "Windows' TCP defaults", TcpOff(tcp) is { } why ? Capital(why) : "Downloads tune their own speed.", TcpOff(tcp) is null, saved?.Tcp is not null, off.Contains(TcpKey)));
        return new State(items, net);
    }

    /// <summary>What's wrong with Windows' TCP settings, in words, or null when both are Windows' defaults.</summary>
    internal static string? TcpOff(Tcp tcp)
    {
        var wrong = new List<string>();
        if (tcp.AutoTuning != Normal) wrong.Add($"receive window tuning {(tcp.AutoTuning is >= 0 and < 5 ? AutoTuning[tcp.AutoTuning] : "changed")}");
        if (!tcp.Rss) wrong.Add("receive-side scaling off");
        return wrong.Count == 0 ? null : string.Join(" and ", wrong);
    }

    private List<CloudflareDns.Adapter> InUse() => system.Adapters().Where(a => CloudflareDns.InUse(a.Nic)).ToList();

    /// <summary>
    /// Sets the items in <paramref name="only"/> (a switch turned on: it's taken out of Off), or with null every item the
    /// player keeps on that isn't set yet (Use best settings), in one elevated script. Null when all went; else what didn't,
    /// plainly. One change at a time: a second press while one runs is told so, so neither writes over the other's record.
    /// </summary>
    internal string? Apply(IReadOnlyCollection<string>? only = null)
    {
        if (!Monitor.TryEnter(_gate)) return Busy;
        try { return Logged(system.StillRunning() ? Busy : ApplyNow(only)); }
        finally { Monitor.Exit(_gate); }
    }

    /// <summary>Turns one item off: puts back what Exo recorded for it and keeps it out of Best settings until it's on again.</summary>
    internal string? TurnOff(string key)
    {
        if (!Monitor.TryEnter(_gate)) return Busy;
        try { return Logged(system.StillRunning() ? Busy : TurnOffNow(key)); }
        finally { Monitor.Exit(_gate); }
    }

    /// <summary>What the player is told reaches app.log too.</summary>
    private static string? Logged(string? said)
    {
        if (said is not null) AppLog.Warn("Network best settings: " + said);
        return said;
    }

    private readonly object _gate = new();
    internal const string Busy = "Exo is still changing your network settings. Try again when it's done.";

    private string? ApplyNow(IReadOnlyCollection<string>? only)
    {
        var saved = Load() ?? new Saved(now(), [], [], [], null);
        if (only is not null && only.Any(k => !Keys.Contains(k))) return "Exo doesn't know that setting.";
        var state = Read();
        var wanted = state.Items.Where(i => !i.On && (only?.Contains(i.Key) ?? !i.Off)).Select(i => i.Key).ToList();
        if (only is not null && saved.Off.Any(only.Contains))
        {
            saved = saved with { Off = [.. saved.Off.Where(k => !only.Contains(k))] };
            if (!Save(saved)) return "Exo couldn't save your choice, so nothing changed.";
        }
        if (wanted.Count == 0) return null;
        if (!system.Online()) return "You're offline, so Exo couldn't check the change. Connect, then try again.";

        // The record as it was before this press: what goes back when the press is declined or an item is put back, so a
        // record an earlier press made (the user's own DNS) is never lost to a retry.
        var prior = saved;
        var before = system.Measure();
        var script = new StringBuilder(Prelude);
        var said = new List<string>();

        // The adapters' own settings: backed up by NetworkTune, written and put back by its reg.exe and pnputil lines.
        NetworkTune.Staged? staged = null;
        if (wanted.Contains(AdapterKey))
        {
            staged = tune.Stage(out var problem);
            if (problem is not null) said.Add(problem);
            if (staged is not null)
            {
                var reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
                var pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
                var set = staged.Writes.Select(w => NetworkTune.Commands(reg, pnputil, w.Adapter, w.Values)).ToList();
                var back = staged.Reverts.Select(w => NetworkTune.Commands(reg, pnputil, w.Adapter, w.Values)).ToList();
                if (set.Any(s => s is null) || back.Any(s => s is null))
                {
                    tune.PutBack(staged);
                    staged = null;
                    said.Add("Exo didn't recognize a network adapter, so its settings didn't change.");
                }
                else Step(script, AdapterKey, set.SelectMany(s => s!).Select(Cmd), back.SelectMany(s => s!).Select(Cmd));
            }
        }

        // Cloudflare DNS: each adapter's DNS recorded before anything changes (a record Exo made before keeps the user's).
        var dnsAdapters = new List<CloudflareDns.Adapter>();
        var snapshots = new Dictionary<string, CloudflareDns.Entry>(StringComparer.OrdinalIgnoreCase);
        var registered = new List<string>();
        if (wanted.Contains(DnsKey))
        {
            dnsAdapters = InUse().Where(a => !CloudflareDns.IsOn(a)).ToList();
            var known = system.Registered();
            var v6 = dnsAdapters.Any(a => a.Nic.Ipv6);
            registered = CloudflareDns.V4.Concat(v6 ? CloudflareDns.V6 : []).Where(a => !known.Contains(a)).ToList();
            foreach (var a in dnsAdapters) snapshots[a.Nic.Guid] = CloudflareDns.Snapshot(a);
            var entries = saved.Dns.Where(e => dnsAdapters.All(a => a.Nic.Guid != e.Guid)).ToList();
            entries.AddRange(dnsAdapters.Select(a => saved.Dns.Find(e => e.Guid == a.Nic.Guid) ?? snapshots[a.Nic.Guid]));
            var set = dnsAdapters.Select(CloudflareDns.Apply).ToList();
            var back = dnsAdapters.Select(a => CloudflareDns.Revert(snapshots[a.Nic.Guid])).ToList();
            if (set.Any(s => s is null) || back.Any(s => s is null) || CloudflareDns.Register(registered) is not { } add)
            {
                dnsAdapters = [];
                said.Add("Exo didn't recognize a network adapter, so its DNS didn't change.");
            }
            else
            {
                saved = saved with { Dns = entries, Registered = [.. saved.Registered.Union(registered)] };
                Step(script, DnsKey, add.Concat(set.SelectMany(s => s!)),
                    back.SelectMany(s => s!).Concat(CloudflareDns.Unregister(registered)));
            }
        }

        // Windows' TCP settings: what they were, then Windows' defaults.
        Tcp? tcpBefore = null;
        if (wanted.Contains(TcpKey) && system.Tcp() is { } tcp && TcpOff(tcp) is not null)
        {
            tcpBefore = tcp;
            saved = saved with { Tcp = saved.Tcp ?? tcp };
            Step(script, TcpKey, TcpLines(new Tcp(Normal, true)), TcpLines(tcp));
        }

        if (staged is null && dnsAdapters.Count == 0 && tcpBefore is null) return Said(said);
        if (!Save(saved with { WrittenAt = now() }))
        {
            if (staged is not null) tune.PutBack(staged);
            return "Your current settings couldn't be saved for Undo, so nothing changed.";
        }
        script.Append($"exit ({Ran} + $r)\n");

        var (code, refused) = system.Elevated(script.ToString());
        if (code == Unfinished)
        {
            // Still running past the wait: Windows may have changed some of it already, so every record stays for Off.
            AppLog.Warn("Network best settings: the elevated step didn't finish in time; records kept.");
            return "The change didn't finish in time. Exo kept what it needs to put it back: turn a switch off to undo it.";
        }
        if (code is not { } exit || exit < Ran || exit >= Ran * 2)
        {
            // Declined, or it stopped before it ran: nothing changed, so the record goes back to before this press.
            if (staged is not null) tune.PutBack(staged);
            Save(prior);
            return refused ?? "The change didn't finish.";
        }

        if (staged is not null)
        {
            if ((exit & PutBackBit[AdapterKey]) != 0)
            {
                tune.PutBack(staged);
                said.Add("Your adapters' new settings cut the connection, so Exo put them back.");
            }
            else if (tune.Finish(staged, null) is { } missed) said.Add(missed);
        }
        if (dnsAdapters.Count > 0)
        {
            if ((exit & PutBackBit[DnsKey]) != 0)
            {
                saved = saved with { Dns = prior.Dns, Registered = prior.Registered };
                said.Add("DNS lookups stopped answering with Cloudflare, so Exo put your DNS back.");
            }
            else
            {
                var after = InUse().Where(a => dnsAdapters.Any(d => d.Nic.Guid == a.Nic.Guid)).ToList();
                // An adapter Windows left exactly as it was keeps the record from before this press; one with any change
                // (one family taken, keys written) keeps the record that puts it back.
                var unchanged = after.Where(a => CloudflareDns.Same(CloudflareDns.Snapshot(a), snapshots[a.Nic.Guid]))
                    .Select(a => a.Nic.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (unchanged.Count > 0)
                    saved = saved with { Dns = [.. saved.Dns.Where(e => !unchanged.Contains(e.Guid)), .. prior.Dns.Where(e => unchanged.Contains(e.Guid))] };
                if (unchanged.Count == after.Count)
                {
                    saved = saved with { Registered = prior.Registered };
                    said.Add("Windows didn't take Cloudflare's DNS.");
                }
                else said.AddRange(DnsMissed(after));
            }
        }
        if (tcpBefore is not null)
        {
            var tcpNow = system.Tcp();
            if ((exit & PutBackBit[TcpKey]) != 0)
            {
                saved = saved with { Tcp = prior.Tcp };
                said.Add("Windows' TCP defaults cut the connection, so Exo put the old ones back.");
            }
            else if (tcpNow is null || TcpOff(tcpNow) is not null)
            {
                if (tcpNow == tcpBefore) saved = saved with { Tcp = prior.Tcp };
                said.Add("Windows didn't take its TCP defaults.");
            }
        }
        Save(saved);
        var afterSample = system.Measure();
        LastCheck = new Check(before, afterSample, Compare(before, afterSample));
        AppLog.Info($"Network best settings: {string.Join(", ", wanted)}; exit {exit}; {LastCheck.Said}");
        return Said(said);
    }

    /// <summary>What an adapter that didn't take everything is missing, plainly: its encryption, or Cloudflare's servers.</summary>
    internal static IEnumerable<string> DnsMissed(IEnumerable<CloudflareDns.Adapter> after) =>
        after.Where(a => !CloudflareDns.IsOn(a)).Select(a => CloudflareDns.ServersOnly(a)
            ? $"Encryption didn't turn on for {a.Nic.Name}, so its lookups go to Cloudflare unencrypted."
            : $"{a.Nic.Name} didn't take Cloudflare's DNS.");

    private string? TurnOffNow(string key)
    {
        if (!Keys.Contains(key)) return "Exo doesn't know that setting.";
        var saved = Load() ?? new Saved(now(), [], [], [], null);
        string? problem = null;
        switch (key)
        {
            case AdapterKey:
                if (tune.Read().Applied) problem = tune.Undo();
                break;
            case DnsKey when saved.Dns.Count > 0:
            {
                // Each record by its adapter's GUID, at the interface number it has now (a reinstalled adapter gets a new
                // one); a record whose adapter isn't here waits for it.
                var adapters = system.Adapters();
                var here = saved.Dns.Select(e => Find(adapters, e.Guid) is { } a ? e with { Index = a.Nic.Index } : null).OfType<CloudflareDns.Entry>().ToList();
                var away = saved.Dns.Where(e => Find(adapters, e.Guid) is null).ToList();
                var lines = here.Select(CloudflareDns.Revert).ToList();
                if (lines.Any(l => l is null)) return "Exo's record of your DNS couldn't be read, so nothing changed.";
                if (here.Count > 0)
                {
                    var script = new StringBuilder(Prelude);
                    var unregister = away.Count == 0 ? CloudflareDns.Unregister(saved.Registered) : [];
                    foreach (var line in lines.SelectMany(l => l!).Concat(unregister)) script.Append(line).Append('\n');
                    script.Append($"Clear-DnsClientCache\nexit {Ran}\n");
                    if (Run(script.ToString()) is { } refused) return refused;
                }
                // A record stays until Windows shows its adapter as recorded, so a restore that didn't take can be tried again.
                var now = system.Adapters();
                var notBack = here.Where(e => !Restored(e, now)).ToList();
                var kept = saved.Dns.Where(e => away.Contains(e) || notBack.Any(n => n.Guid == e.Guid)).ToList();
                saved = saved with { Dns = kept, Registered = kept.Count == 0 ? [] : saved.Registered };
                if (notBack.Count > 0)
                {
                    Save(saved);
                    return $"Windows didn't put back the DNS of {string.Join(" and ", notBack.Select(e => e.Name))}. Try again.";
                }
                if (away.Count > 0)
                {
                    // The player's Off holds for what was put back; the adapter that's away keeps its record.
                    if (!saved.Off.Contains(key)) saved = saved with { Off = [.. saved.Off, key] };
                    Save(saved);
                    return $"{string.Join(" and ", away.Select(e => e.Name))} isn't connected: its DNS is put back when you turn this off again with it connected.";
                }
                break;
            }
            case DnsKey:
                if (InUse().Any(CloudflareDns.IsOn)) problem = "Exo didn't set this DNS, so there's nothing to put back. Change it in Windows Settings › Network.";
                break;
            case TcpKey when saved.Tcp is { } tcp:
            {
                var script = new StringBuilder(Prelude);
                foreach (var line in TcpLines(tcp)) script.Append(line).Append('\n');
                script.Append($"exit {Ran}\n");
                problem = Run(script.ToString());
                if (problem is null && system.Tcp() != tcp) problem = "Windows didn't put back its old TCP settings. Try again.";
                if (problem is null) saved = saved with { Tcp = null };
                break;
            }
        }
        if (problem is not null) return problem;
        if (!saved.Off.Contains(key)) saved = saved with { Off = [.. saved.Off, key] };
        return Save(saved) ? null : "Exo couldn't save your choice.";
    }

    private static CloudflareDns.Adapter? Find(IReadOnlyList<CloudflareDns.Adapter> adapters, string guid) =>
        adapters.FirstOrDefault(a => a.Nic.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase));

    /// <summary>The adapter shows what it was recorded with: its servers per family (DHCP as DHCP) and Cloudflare's DoH keys as they were.</summary>
    internal static bool Restored(CloudflareDns.Entry e, IReadOnlyList<CloudflareDns.Adapter> adapters) =>
        Find(adapters, e.Guid) is { } a && CloudflareDns.Same(CloudflareDns.Snapshot(a) with { Index = e.Index, Name = e.Name, Ipv6 = e.Ipv6 }, e);

    /// <summary>Measures now, for the group's check line (no press).</summary>
    internal Check Measure() => LastCheck = new Check(null, system.Measure(), "");

    private string? Run(string script)
    {
        var (code, problem) = system.Elevated(script);
        return code == Ran ? null : code == Unfinished ? "The change didn't finish in time. Try again." : problem ?? "The change didn't finish.";
    }

    /// <summary>The exit code <see cref="ISystem.Elevated"/> gives when the script was still running at the end of its wait.</summary>
    internal const int Unfinished = -1;

    /// <summary>
    /// The script's start: errors don't stop it (each item is checked by what Windows reads afterwards), $r gathers the
    /// items put back, C runs one fixed cmd line as written, and T says whether a fresh lookup answers within 20 seconds
    /// (an adapter that restarts takes a few to come back).
    /// </summary>
    internal const string Prelude =
        "$ErrorActionPreference='SilentlyContinue'\n$r=0\n" +
        "function C($l){$i=New-Object Diagnostics.ProcessStartInfo;$i.FileName=[Environment]::SystemDirectory+'\\cmd.exe';$i.Arguments='/d /s /c \"'+$l+'\"';$i.UseShellExecute=$false;$i.CreateNoWindow=$true;$p=[Diagnostics.Process]::Start($i);$p.WaitForExit()}\n" +
        "function T{foreach($n in 1..25){Clear-DnsClientCache;if(Resolve-DnsName getexo.app -Type A -DnsOnly -QuickTimeout){return $true};if((Resolve-DnsName one.one.one.one -Type A -DnsOnly -QuickTimeout) -or (Resolve-DnsName dns.msftncsi.com -Type A -DnsOnly -QuickTimeout) -or (Resolve-DnsName one.one.one.one -Server 1.1.1.1 -Type A -DnsOnly -QuickTimeout)){return $true};Start-Sleep -Milliseconds 800};return $false}\n";

    /// <summary>One item: its lines, then the lookup check; when it fails, the lines that put it back and its bit in $r.</summary>
    internal static void Step(StringBuilder script, string key, IEnumerable<string> set, IEnumerable<string> back)
    {
        script.Append("# ").Append(key).Append('\n');
        foreach (var line in set) script.Append(line).Append('\n');
        script.Append("Clear-DnsClientCache\nif(-not (T)){\n");
        foreach (var line in back) script.Append(line).Append('\n');
        script.Append("Clear-DnsClientCache\n$r=$r -bor ").Append(PutBackBit[key]).Append("\n}\n");
    }

    /// <summary>A cmd line for the script's C: single quotes doubled for PowerShell's string.</summary>
    internal static string Cmd(string line) => $"C '{line.Replace("'", "''")}'";

    /// <summary>netsh's lines that set Windows' TCP settings to <paramref name="tcp"/>.</summary>
    internal static IReadOnlyList<string> TcpLines(Tcp tcp) =>
    [
        $"netsh interface tcp set global autotuninglevel={AutoTuning[Math.Clamp(tcp.AutoTuning, 0, AutoTuning.Length - 1)]}",
        $"netsh interface tcp set global rss={(tcp.Rss ? "enabled" : "disabled")}",
    ];

    /// <summary>
    /// What the check says, honestly: a change only where it's past the noise (a fifth and at least 3 ms), and "no change
    /// you'd notice" when nothing is.
    /// </summary>
    internal static string Compare(Sample? before, Sample after)
    {
        if (before is null) return "";
        var parts = new List<string>();
        Say(parts, "Lookups", before.Lookup, after.Lookup);
        Say(parts, "Ping", before.Ping, after.Ping);
        Say(parts, "Jitter", before.Jitter, after.Jitter);
        Say(parts, "Ping to your router", before.Router, after.Router);
        return parts.Count == 0 ? "No change you'd notice: your connection was already fine." : string.Join(". ", parts) + ".";
    }

    private static void Say(List<string> parts, string what, double? was, double? now)
    {
        if (was is not { } a || now is not { } b) return;
        var change = b - a;
        if (Math.Abs(change) < 3 || Math.Abs(change) < a / 5) return;
        parts.Add($"{what} {(change < 0 ? "fell" : "rose")} from {Ms(a)} to {Ms(b)}");
    }

    internal static string Ms(double ms) => Math.Round(ms).ToString(CultureInfo.InvariantCulture) + " ms";

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..] + (text.EndsWith('.') ? "" : ".");

    private static string? Said(List<string> said) => said.Count == 0 ? null : string.Join(" ", said);

    private Saved? Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    private bool Save(Saved saved)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Network best settings: the record couldn't be saved: " + ex.Message);
            return false;
        }
    }
}
