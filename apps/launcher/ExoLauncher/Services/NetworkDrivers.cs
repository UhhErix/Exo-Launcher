using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// The network part of Exo's Drivers list (<see cref="DriverCatalog"/>, through <see cref="IDriverProvider"/>): every Ethernet and Wi-Fi adapter that is a PCI
/// or USB device, Ethernet first, with its driver as Windows records it (the network class key's DriverVersion and
/// DriverDate, the INF's DriverVer, read without administrator rights) and the maker's newest, from official pages only:
///
/// - Intel Ethernet: download 706171 (Intel Network Adapter Driver for Windows 11). Its page names the package
///   (DownloadVersion 31.3), its Wired_driver zip on downloadmirror.intel.com and the zip's SHA-512. The package version
///   isn't a driver version, so Exo reads the zip's directory and each Windows 11 INF (PRO1000, PRO2500, ...\Winx64\W11)
///   with range requests, a few kilobytes each, and the INF whose models list the adapter's PCI ID gives the newest
///   DriverVer. Install downloads the zip, checks its SHA-512 and the driver catalog's signature, keeps the driver it
///   replaces (pnputil /export-driver) and adds the INF with pnputil /install in one Windows prompt. Roll back stages the
///   kept driver and deletes the new one from the device, which takes the kept one again.
/// - Intel Wi-Fi (Killer Wi-Fi too): download 19351 names the package (24.80.0); only Intel's installer exists, so the row
///   opens Intel's page. Its first three parts are the current adapters' driver version; adapters on Intel's legacy 22.x
///   line aren't compared.
/// - Realtek PCIe GbE/2.5G/5G/10G (one family page, cate_id 584): its NDIS and NetAdapterCx packages' versions
///   (<see cref="RealtekCompare"/>); only Realtek's installer exists, so the row opens Realtek's page.
/// - Killer Ethernet (E3000/E3100G and E5000 on Realtek chips, E3100 on Intel's I225): Intel's Killer Performance Suite
///   page (download 19779) lists each model's Windows 11 driver; only its 200 MB installer exists, so the row opens it.
/// - Marvell AQtion, MediaTek, Qualcomm: recognized, with no maker page to read; Windows Update delivers their drivers.
///
/// Requests carry only the page's address (no device ID, no user data) under Exo's neutral agent.
/// </summary>
internal sealed partial class NetworkDrivers(NetworkDrivers.IHost host, string folder) : IDriverProvider
{
    internal const string IntelWiredPage = "https://www.intel.com/content/www/us/en/download/706171/intel-network-adapter-driver-for-windows-11.html";
    internal const string IntelWifiPage = "https://www.intel.com/content/www/us/en/download/19351/intel-wireless-wi-fi-drivers-for-windows-10-and-windows-11.html";
    internal const string RealtekPage = "https://www.realtek.com/Download/List?cate_id=584";
    internal const string KillerPage = "https://www.intel.com/content/www/us/en/download/19779/intel-killer-performance-suite.html";
    internal const string IntelHost = "downloadmirror.intel.com";
    internal const string Whql = "Microsoft Windows Hardware Compatibility Publisher";
    internal const long MaxZipBytes = 300L << 20;

    internal enum Source { None, IntelWired, IntelWifi, Realtek, Killer, Marvell, MediaTek, Qualcomm }


    /// <summary>An adapter as Windows records it. DeviceId is its PnP instance ("PCI\VEN_8086&amp;DEV_125C&amp;...\6&amp;..."), Inf its published INF (oem5.inf).</summary>
    internal sealed record Adapter(string DeviceId, string Name, bool Wifi, string? Provider, string? Version, string? Date, string? Inf);

    /// <summary>The newest Intel wired package: its version, zip, SHA-512 and date, and each Windows 11 INF it carries.</summary>
    internal sealed record IntelPackage(string Version, string Url, string? Sha512, string? Date);

    /// <summary>One Windows 11 INF in Intel's zip: its path in the zip, DriverVer and date, and the PCI IDs its models list.</summary>
    internal sealed record PackageInf(string Path, string Version, string? Date, IReadOnlyList<string> Ids);

    internal sealed record IntelWired(IntelPackage Package, IReadOnlyList<PackageInf> Infs);

    /// <summary>Realtek's two Windows 11 packages (each with power saving): NDIS ("10.80.50") and NetAdapterCx ("11.031.50").</summary>
    internal sealed record RealtekPackages(string? Ndis, string? Cx);

    /// <summary>A zip entry from the central directory.</summary>
    internal sealed record ZipEntry(string Name, int Method, long Compressed, long Offset);

    /// <summary>What Exo did, kept to roll back: the device, the INF it had and its version, the kept copy, the INF Exo added.</summary>
    internal sealed record Installed(string DeviceId, string OldInf, string? OldVersion, string Kept, string? NewInf);

    internal interface IHost
    {
        IReadOnlyList<Adapter> Adapters();
        Task<string> GetTextAsync(string url, CancellationToken ct);
        /// <summary>Bytes [from, from+length) of a file on Intel's server; from &lt; 0 reads the last -from bytes. Returns the bytes and the file's whole length.</summary>
        Task<(byte[] Bytes, long Total)> GetRangeAsync(string url, long from, int length, CancellationToken ct);
        Task DownloadAsync(string url, string path, IProgress<int>? progress, CancellationToken ct);
        bool Signed(string catalog);
        bool GameRunning();
        bool InCall();
        bool OtherInstall();
        /// <summary>A game downloads or installs now: the link drop would stall it.</summary>
        bool Downloading();
        /// <summary>Exo itself downloads or installs an update now.</summary>
        bool ExoUpdating();
        /// <summary>Runs pnputil lines as administrator (one prompt); null when ran, else the line to show.</summary>
        string? Elevated(IReadOnlyList<string> commands);
        /// <summary>Why a version mustn't install on this PC (a known problem, <see cref="DriverAdvisories"/>), or null.</summary>
        string? Advised(string subject, string version);
    }

    internal static NetworkDrivers Shared { get; } = new(new NativeHost(), Path.Combine(PathHelper.AppDataDir, "network-driver"));

    private readonly LatestCache<IntelWired> _intel = new("Intel's network driver page", TimeSpan.FromHours(12), folder + "-intel.json");
    private readonly LatestCache<string> _wifi = new("Intel's Wi-Fi driver page", TimeSpan.FromHours(12));
    private readonly LatestCache<RealtekPackages> _realtek = new("Realtek's network driver page", TimeSpan.FromHours(12));
    private readonly LatestCache<Dictionary<string, string>> _killer = new("Intel's Killer driver page", TimeSpan.FromHours(12));
    private int _running;

    private string KeptPath => Path.Combine(folder, "installed.json");

    // ---- Reading ----

    public async Task<IReadOnlyList<DriverItem>> ReadAsync(CancellationToken ct)
    {
        var items = new List<DriverItem>();
        var kept = LoadInstalled();
        foreach (var adapter in host.Adapters().OrderBy(a => a.Wifi).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            var source = Identify(adapter.DeviceId, adapter.Wifi);
            string? latest = null, latestDate = null, page = null;
            var behind = false;
            try
            {
                switch (source)
                {
                    case Source.IntelWired when await IntelAsync(ct).ConfigureAwait(false) is { } intel && Match(intel.Infs, adapter.DeviceId) is { } inf:
                        (latest, latestDate) = (inf.Version, inf.Date);
                        behind = VersionNumber.Newer(latest, adapter.Version);
                        break;
                    case Source.IntelWifi when await _wifi.GetAsync("page", async c => IntelWifiVersion(await host.GetTextAsync(IntelWifiPage, c).ConfigureAwait(false)), ct).ConfigureAwait(false) is { } wifi:
                        page = IntelWifiPage;
                        if (WifiBehind(adapter.Version, wifi) is { } wifiBehind) (latest, behind) = (wifi, wifiBehind);
                        break;
                    case Source.Realtek when await _realtek.GetAsync("page", async c => RealtekVersions(await host.GetTextAsync(RealtekPage, c).ConfigureAwait(false)), ct).ConfigureAwait(false) is { } realtek:
                        page = RealtekPage;
                        if (RealtekCompare(adapter.Version, realtek) is { } compared) (latest, behind) = compared;
                        break;
                    case Source.Killer when await _killer.GetAsync("page", async c => KillerVersions(await host.GetTextAsync(KillerPage, c).ConfigureAwait(false)), ct).ConfigureAwait(false) is { } killer:
                        page = KillerPage;
                        if (KillerModel(adapter.DeviceId) is { } model && killer.TryGetValue(model, out var version)) (latest, behind) = (version, VersionNumber.Newer(version, adapter.Version));
                        break;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException or InvalidDataException)
            {
                AppLog.Info($"Network driver: {source} not read: {ex.Message}");
            }
            var how = !behind ? DriverHow.None : source == Source.IntelWired ? DriverHow.Install : DriverHow.Page;
            items.Add(new DriverItem("network:" + adapter.DeviceId, "network", adapter.Name, Maker(source, adapter.Provider),
                adapter.Version, adapter.Date, latest, latestDate, how, page,
                kept is { } k && k.DeviceId == adapter.DeviceId && k.NewInf == adapter.Inf)
            { Source = latest is null ? null : Maker(source, adapter.Provider), Device = Subject(adapter) });
        }
        return items;
    }

    private Task<IntelWired?> IntelAsync(CancellationToken ct) => _intel.GetAsync("wired", async c =>
    {
        var package = IntelWiredPackage(await host.GetTextAsync(IntelWiredPage, c).ConfigureAwait(false));
        if (package is null) return null;
        // The zip's directory is in its last 64 KB at most (Intel's 31.3: 473 entries, 35 KB).
        var (tail, total) = await host.GetRangeAsync(package.Url, -65536, 65536, c).ConfigureAwait(false);
        var infs = new List<PackageInf>();
        foreach (var entry in ZipDirectory(tail, total).Where(e => W11Inf().IsMatch(e.Name)))
        {
            // Local header (30 bytes + name + extra, under 1 KB) then the deflated INF.
            var (bytes, _) = await host.GetRangeAsync(package.Url, entry.Offset, (int)Math.Min(entry.Compressed + 1024, 1 << 20), c).ConfigureAwait(false);
            var text = InfText(Inflate(bytes, entry));
            if (DriverVer(text) is { } ver) infs.Add(new PackageInf(entry.Name, ver.Version, ver.Date, PciIds(text)));
        }
        return new IntelWired(package, infs);
    }, ct);

    // ---- Installing ----

    public string? Busy() =>
        Volatile.Read(ref _running) == 1 ? "A driver update is already running."
        : host.GameRunning() ? "Close your game first."
        : host.InCall() ? "Leave your Discord call first."
        : host.OtherInstall() ? "Another driver update is running."
        : host.Downloading() ? "Wait for your download to finish."
        : host.ExoUpdating() ? "Exo is updating."
        : null;

    /// <summary>The button's working word while an install or roll back runs: the link drops for a few seconds.</summary>
    internal const string WorkingWords = "Updating · network drops for a few seconds";

    public string Working(DriverItem item) => WorkingWords;

    public async Task<DriverResult> InstallAsync(DriverItem item, IProgress<int>? progress, CancellationToken ct)
    {
        if (Busy() is { } busy) return new(false, busy);
        if (item.How != DriverHow.Install) return new(false, "Exo can't install this driver.");
        if (host.Adapters().FirstOrDefault(a => "network:" + a.DeviceId == item.Id) is not { Inf: { } oldInf } adapter
            || Identify(adapter.DeviceId, adapter.Wifi) != Source.IntelWired) return new(false, "Exo can't find this adapter.");
        if (Interlocked.Exchange(ref _running, 1) == 1) return new(false, "A driver update is already running.");
        var zip = "";
        try
        {
            if (await IntelAsync(ct).ConfigureAwait(false) is not { } intel || Match(intel.Infs, adapter.DeviceId) is not { } inf)
                return new(false, "Intel's page names no driver for this adapter.");
            Directory.CreateDirectory(folder);
            zip = Path.Combine(folder, Path.GetFileName(new Uri(intel.Package.Url).LocalPath));
            AppLog.Info($"Network driver: {adapter.Name} has {adapter.Version}; downloading {inf.Version} from {intel.Package.Url}.");
            await host.DownloadAsync(intel.Package.Url, zip, progress, ct).ConfigureAwait(false);
            if (intel.Package.Sha512 is not { } sha || !Sha512(zip).Equals(sha, StringComparison.OrdinalIgnoreCase))
                return new(false, "The download isn't Intel's package, so nothing changed.");
            var target = Path.Combine(folder, intel.Package.Version);
            var infPath = Unpack(zip, inf.Path, target);
            if (infPath is null || DriverVer(InfText(File.ReadAllBytes(infPath)))?.Version != inf.Version)
                return new(false, "Intel's package isn't what its page says, so nothing changed.");
            if (Directory.GetFiles(Path.GetDirectoryName(infPath)!, "*.cat") is not [var catalog] || !host.Signed(catalog))
                return new(false, "Intel's driver isn't signed, so nothing changed.");
            // A game or call may have started during the download; the advisories may have learned of this driver meanwhile.
            if (host.GameRunning() || host.InCall()) return new(false, host.GameRunning() ? "Close your game first." : "Leave your Discord call first.");
            if (host.Advised("network:" + Subject(adapter), inf.Version) is { } advised) return new(false, advised);
            // The driver it replaces is exported here (an inbox driver stays in Windows by itself).
            var kept = Path.Combine(folder, "kept", Path.GetFileNameWithoutExtension(oldInf));
            if (Directory.Exists(kept)) DeletionGate.DeleteDirectory(kept, true);
            Directory.CreateDirectory(kept);
            if (InstallCommands(Pnputil, oldInf, kept, infPath) is not { } commands) return new(false, "Exo didn't recognize the driver, so nothing changed.");
            if (host.Elevated(commands) is { } refused) return new(false, refused);
            var now = await SettleAsync(adapter.DeviceId, inf.Version, ct).ConfigureAwait(false);
            var updated = VersionNumber.AtLeast(now?.Version, inf.Version);
            AppLog.Info($"Network driver: {adapter.Name} now {now?.Version ?? "unknown"} ({now?.Inf}); kept {oldInf} {adapter.Version} in {kept}.");
            // Roll back deletes the INF Exo added, so it's kept only when the adapter really runs a new one.
            if (updated && now?.Inf is { } newInf && OemInf().IsMatch(newInf) && !newInf.Equals(oldInf, StringComparison.OrdinalIgnoreCase))
                SaveInstalled(new Installed(adapter.DeviceId, oldInf, adapter.Version, kept, newInf));
            return updated ? new(true, $"Updated to {inf.Version}.") : new(false, $"Windows kept {now?.Version ?? adapter.Version}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Warn("Network driver: update failed: " + ex.Message);
            return new(false, "The update stopped. Your driver is unchanged.");
        }
        finally
        {
            try
            {
                if (zip.Length > 0) { DeletionGate.DeleteFile(zip); DeletionGate.DeleteFile(zip + ".part"); }
                foreach (var unpacked in Directory.Exists(folder) ? Directory.GetDirectories(folder).Where(d => VersionNumber.Parse(Path.GetFileName(d)) is not null) : []) DeletionGate.DeleteDirectory(unpacked, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Network driver: the package wasn't removed: " + ex.Message); }
            Volatile.Write(ref _running, 0);
        }
    }

    public async Task<DriverResult> RollBackAsync(DriverItem item, CancellationToken ct)
    {
        if (Busy() is { } busy) return new(false, busy);
        if (LoadInstalled() is not { NewInf: { } newInf } kept || "network:" + kept.DeviceId != item.Id || newInf.Equals(kept.OldInf, StringComparison.OrdinalIgnoreCase))
            return new(false, "There's no earlier driver to go back to.");
        // An exported driver is its one INF in the kept folder; an inbox driver (net*.inf) needs none.
        var keptInf = Directory.Exists(kept.Kept) ? Directory.GetFiles(kept.Kept, "*.inf", SearchOption.AllDirectories) : [];
        if ((OemInf().IsMatch(kept.OldInf) && keptInf.Length != 1) || RollBackCommands(Pnputil, keptInf.FirstOrDefault(), newInf) is not { } commands)
            return new(false, "The earlier driver is gone, so nothing changed.");
        if (Interlocked.Exchange(ref _running, 1) == 1) return new(false, "A driver update is already running.");
        try
        {
            if (host.Elevated(commands) is { } refused) return new(false, refused);
            var now = await SettleAsync(kept.DeviceId, kept.OldVersion, ct).ConfigureAwait(false);
            AppLog.Info($"Network driver: rolled back to {now?.Version ?? "unknown"} ({now?.Inf}).");
            if (!VersionNumber.Same(now?.Version, kept.OldVersion)) return new(false, $"Windows kept {now?.Version ?? "the newer driver"}.");
            SaveInstalled(null);
            return new(true, $"Back to {kept.OldVersion}.");
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    /// <summary>The adapter after a change: Windows restarts it for a few seconds; waits up to 30 s for <paramref name="version"/>.</summary>
    private async Task<Adapter?> SettleAsync(string deviceId, string? version, CancellationToken ct)
    {
        Adapter? now = null;
        for (var i = 0; i < 30; i++)
        {
            now = host.Adapters().FirstOrDefault(a => a.DeviceId == deviceId) ?? now;
            if (VersionNumber.Same(now?.Version, version)) break;
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        return now;
    }

    private static string Pnputil => Path.Combine(Environment.SystemDirectory, "pnputil.exe");

    /// <summary>
    /// Keep the driver in use (a published oemN.inf is exported from the driver store; an inbox net*.inf stays in Windows),
    /// then add Intel's INF and install it on the adapter. Null when a path or INF name isn't plain.
    /// </summary>
    internal static IReadOnlyList<string>? InstallCommands(string pnputil, string oldInf, string kept, string inf) =>
        !PlainPath(kept) || !PlainPath(inf) ? null
        : OemInf().IsMatch(oldInf) ? [$"\"{pnputil}\" /export-driver {oldInf} \"{kept}\"", $"\"{pnputil}\" /add-driver \"{inf}\" /install"]
        : InboxInf().IsMatch(oldInf) ? [$"\"{pnputil}\" /add-driver \"{inf}\" /install"]
        : null;

    /// <summary>Stage the kept driver (if one was exported), then remove Exo's from the adapter and rescan: Windows installs the best one left, the kept one.</summary>
    internal static IReadOnlyList<string>? RollBackCommands(string pnputil, string? keptInf, string newInf) =>
        !OemInf().IsMatch(newInf) || (keptInf is not null && !PlainPath(keptInf)) ? null :
        [.. keptInf is null ? [] : new[] { $"\"{pnputil}\" /add-driver \"{keptInf}\"" }, $"\"{pnputil}\" /delete-driver {newInf} /uninstall", $"\"{pnputil}\" /scan-devices"];

    /// <summary>A path cmd reads as is: absolute, no quotes, no cmd metacharacters.</summary>
    private static bool PlainPath(string path) => Path.IsPathFullyQualified(path) && path.IndexOfAny(['"', '&', '|', '<', '>', '^', '%', '!']) < 0;

    /// <summary>Unpacks the INF's folder from the zip into <paramref name="target"/>; the INF's path, or null.</summary>
    private static string? Unpack(string zip, string infName, string target)
    {
        var dir = infName[..(infName.LastIndexOfAny(['\\', '/']) + 1)];
        if (Directory.Exists(target)) DeletionGate.DeleteDirectory(target, true);
        using var archive = ZipFile.OpenRead(zip);
        string? inf = null;
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('/', '\\');
            if (!name.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || name.EndsWith('\\') || name[dir.Length..].Contains('\\')) continue;
            var path = Path.GetFullPath(Path.Combine(target, name[dir.Length..]));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            Directory.CreateDirectory(target);
            entry.ExtractToFile(path, overwrite: true);
            if (name.Equals(infName, StringComparison.OrdinalIgnoreCase)) inf = path;
        }
        return inf;
    }

    private static string Sha512(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA512.HashData(stream));
    }

    private Installed? LoadInstalled()
    {
        try { return File.Exists(KeptPath) ? System.Text.Json.JsonSerializer.Deserialize<Installed>(File.ReadAllText(KeptPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    private void SaveInstalled(Installed? installed)
    {
        try
        {
            if (installed is null) { DeletionGate.DeleteFile(KeptPath); return; }
            Directory.CreateDirectory(folder);
            File.WriteAllText(KeptPath, System.Text.Json.JsonSerializer.Serialize(installed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Network driver: the roll back record wasn't saved: " + ex.Message); }
    }

    // ---- Pure: chips, pages, INFs, versions ----

    /// <summary>The maker source for a PnP instance by its vendor (and Killer's Realtek device IDs).</summary>
    internal static Source Identify(string deviceId, bool wifi)
    {
        if (Pci().Match(deviceId) is not { Success: true } m) return Source.None;
        var dev = m.Groups[2].Value.ToUpperInvariant();
        return m.Groups[1].Value.ToUpperInvariant() switch
        {
            // Killer E3100: Intel's I225 with Killer's subsystem and driver. Killer Wi-Fi is on Intel's Wi-Fi package.
            "8086" when !wifi && KillerSubsys().IsMatch(deviceId) => Source.Killer,
            "8086" => wifi ? Source.IntelWifi : Source.IntelWired,
            // Killer E2600 (2600), E3000/E3100 (3000), E5000 (5000): Realtek silicon on Killer's driver.
            "10EC" when !wifi && dev is "2600" or "3000" or "5000" => Source.Killer,
            "10EC" when !wifi => Source.Realtek,
            "1969" => Source.Killer,
            "1D6A" => Source.Marvell,
            "14C3" => Source.MediaTek,
            "17CB" or "168C" => Source.Qualcomm,
            _ => Source.None,
        };
    }

    internal static string Maker(Source source, string? provider) => source switch
    {
        Source.IntelWired or Source.IntelWifi => "Intel",
        Source.Realtek => "Realtek",
        Source.Killer => "Killer",
        Source.Marvell => "Marvell",
        Source.MediaTek => "MediaTek",
        Source.Qualcomm => "Qualcomm",
        _ => provider ?? "",
    };

    /// <summary>What the advisories match for an adapter: its device id and name (the chip's revision is in the id).</summary>
    internal static string Subject(Adapter adapter) => $"{adapter.DeviceId} {adapter.Name}";

    /// <summary>Intel's wired page: the package version, its Wired_driver zip, the zip's SHA-512 and the page's date.</summary>
    internal static IntelPackage? IntelWiredPackage(string html)
    {
        var version = DownloadVersion().Match(html);
        var link = WiredZip().Match(html);
        if (!version.Success || !link.Success) return null;
        var next = html.IndexOf(IntelHost, link.Index + link.Length, StringComparison.OrdinalIgnoreCase);
        var sha = Sha512Text().Match(html[link.Index..(next < 0 ? html.Length : next)]);
        var date = PageDate().Match(html);
        return new(version.Groups[1].Value, link.Value, sha.Success ? sha.Groups[1].Value.ToLowerInvariant() : null,
            date.Success && DateTime.TryParseExact(date.Groups[1].Value, "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null);
    }

    /// <summary>Intel's Wi-Fi page: the package version its WiFi-…-Driver64 installer carries ("24.80.0").</summary>
    internal static string? IntelWifiVersion(string html) =>
        WifiExe().Match(html) is { Success: true } m && DownloadVersion().Match(html) is { Success: true } v && v.Groups[1].Value == m.Groups[1].Value ? m.Groups[1].Value : null;

    /// <summary>Intel Wi-Fi: behind when the installed driver's first three parts are older; null (no claim) on Intel's legacy 22.x line or an unreadable version.</summary>
    internal static bool? WifiBehind(string? installed, string? latest) =>
        VersionNumber.Parse(installed) is not { Length: >= 3 } have || have[0] < 23 || VersionNumber.Parse(latest) is null ? null
        : VersionNumber.Newer(latest, $"{have[0]}.{have[1]}.{have[2]}");

    /// <summary>Realtek's family page: its Windows 11 packages with power saving, NDIS ("10.80.50") and NetAdapterCx ("11.031.50"); null when it names neither.</summary>
    internal static RealtekPackages? RealtekVersions(string html)
    {
        string? Find(string name) =>
            RealtekRow().Matches(html).FirstOrDefault(m => m.Groups[1].Value.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) is { } m ? m.Groups[2].Value : null;
        var packages = new RealtekPackages(Find("Win10/Win11 Auto Installation Program (NDIS)"), Find("Win11 Auto Installation Program (NetAdapterCx)"));
        return packages.Ndis is null && packages.Cx is null ? null : packages;
    }

    /// <summary>
    /// Realtek: the newest for the installed driver's line and whether it's behind, or null (no claim). A NetAdapterCx
    /// driver is the chip's number first ("1125.31.50.603", which Intel's Killer page lists for the 11.031.50 package), so
    /// its second and third parts are the package's ("1125.31.50"). An NDIS driver (same major as the package) compares by
    /// its first two parts.
    /// </summary>
    internal static (string Latest, bool Behind)? RealtekCompare(string? installed, RealtekPackages packages)
    {
        if (VersionNumber.Parse(installed) is not { Length: >= 3 } have) return null;
        if (have[0] >= 1100 && VersionNumber.Parse(packages.Cx) is { Length: >= 3 } cx)
            return ($"{have[0]}.{cx[1]}.{cx[2]}", have[1] < cx[1] || (have[1] == cx[1] && have[2] < cx[2]));
        if (VersionNumber.Parse(packages.Ndis) is { Length: >= 2 } ndis && have[0] == ndis[0])
            return (packages.Ndis!, have[1] < ndis[1]);
        return null;
    }

    /// <summary>The Killer model whose driver Intel's Killer page lists: E3000 (also E3100G) and E5000 on Realtek silicon, E3100 on Intel's I225/I226 with Killer's (Rivet Networks') subsystem.</summary>
    internal static string? KillerModel(string deviceId)
    {
        if (Pci().Match(deviceId) is not { Success: true } m) return null;
        var (ven, dev) = (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant());
        return ven == "10EC" ? dev switch { "3000" => "E3000", "5000" => "E5000", _ => null }
            : ven == "8086" && KillerSubsys().IsMatch(deviceId) ? "E3100" : null;
    }

    /// <summary>Intel's Killer Performance Suite page: each Ethernet model with its Windows 11 driver ("2.1.5.7 (Windows 11*) for Intel® Killer™ Ethernet E3100").</summary>
    internal static Dictionary<string, string>? KillerVersions(string html)
    {
        var text = Tags().Replace(html, " ");
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in KillerLine().Matches(text))
        {
            foreach (var model in m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) versions[model] = m.Groups[1].Value;
        }
        return versions.Count == 0 ? null : versions;
    }

    /// <summary>The INF's DriverVer: its version and date (yyyy-MM-dd).</summary>
    internal static (string Version, string? Date)? DriverVer(string inf)
    {
        if (DriverVerLine().Match(inf) is not { Success: true } m) return null;
        var date = DateTime.TryParseExact(m.Groups[1].Value, ["M/d/yyyy", "MM/dd/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        return (m.Groups[2].Value, date);
    }

    /// <summary>Every PCI ID an INF's models list ("PCI\VEN_8086&amp;DEV_125C", with SUBSYS or REV where it names one), upper case.</summary>
    internal static IReadOnlyList<string> PciIds(string inf) =>
        [.. PciId().Matches(inf).Select(m => m.Value.ToUpperInvariant()).Distinct()];

    /// <summary>Whether an INF's ID fits the device: the same vendor and device, and every SUBSYS or REV it names is the device's.</summary>
    internal static bool Covers(string infId, string deviceId)
    {
        var device = deviceId.ToUpperInvariant();
        var hw = device.StartsWith(@"PCI\", StringComparison.Ordinal) ? device[4..].Split('\\')[0].Split('&') : [];
        return infId.ToUpperInvariant()[4..].Split('&') is var parts && parts.Length >= 2 && parts.All(p => hw.Contains(p));
    }

    /// <summary>The package INF that lists the device, the most specific ID first (as Windows ranks them).</summary>
    internal static PackageInf? Match(IReadOnlyList<PackageInf> infs, string deviceId) =>
        infs.Select(inf => (inf, best: inf.Ids.Where(id => Covers(id, deviceId)).Select(id => id.Count(c => c == '&')).DefaultIfEmpty(-1).Max()))
            .Where(x => x.best >= 0).OrderByDescending(x => x.best).Select(x => x.inf).FirstOrDefault();

    /// <summary>An INF's text: UTF-16 with its BOM (Intel's are), else UTF-8.</summary>
    internal static string InfText(byte[] bytes) =>
        bytes is [0xFF, 0xFE, ..] ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
        : bytes is [0xEF, 0xBB, 0xBF, ..] ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);

    /// <summary>A zip's entries from its central directory, found in <paramref name="tail"/>, the last bytes of a <paramref name="total"/>-byte file.</summary>
    internal static IReadOnlyList<ZipEntry> ZipDirectory(ReadOnlySpan<byte> tail, long total)
    {
        var end = tail.LastIndexOf("PK\u0005\u0006"u8);
        if (end < 0 || end + 22 > tail.Length) throw new InvalidDataException("No zip directory.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(tail[(end + 10)..]);
        var at = BinaryPrimitives.ReadUInt32LittleEndian(tail[(end + 16)..]) - (total - tail.Length);
        if (at < 0) throw new InvalidDataException("The zip directory is larger than the read.");
        var list = new List<ZipEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var p = (int)at;
            if (p + 46 > tail.Length || BinaryPrimitives.ReadUInt32LittleEndian(tail[p..]) != 0x02014b50) throw new InvalidDataException("A bad zip directory.");
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(tail[(p + 28)..]), extra = BinaryPrimitives.ReadUInt16LittleEndian(tail[(p + 30)..]), comment = BinaryPrimitives.ReadUInt16LittleEndian(tail[(p + 32)..]);
            list.Add(new ZipEntry(Encoding.Latin1.GetString(tail.Slice(p + 46, nameLength)), BinaryPrimitives.ReadUInt16LittleEndian(tail[(p + 10)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(tail[(p + 20)..]), BinaryPrimitives.ReadUInt32LittleEndian(tail[(p + 42)..])));
            at += 46 + nameLength + extra + comment;
        }
        return list;
    }

    /// <summary>One entry's bytes from a read starting at its local header (stored or deflated).</summary>
    internal static byte[] Inflate(byte[] local, ZipEntry entry)
    {
        if (local.Length < 30 || BinaryPrimitives.ReadUInt32LittleEndian(local) != 0x04034b50) throw new InvalidDataException("A bad zip entry.");
        var start = 30 + BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(28));
        if (start + entry.Compressed > local.Length) throw new InvalidDataException("A short zip entry.");
        var data = new MemoryStream(local, start, (int)entry.Compressed);
        if (entry.Method == 0) return data.ToArray();
        if (entry.Method != 8) throw new InvalidDataException("An unknown zip method.");
        using var inflate = new DeflateStream(data, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return output.ToArray();
    }

    [GeneratedRegex(@"^PCI\\VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex Pci();

    [GeneratedRegex(@"PCI\\VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}(?:&SUBSYS_[0-9A-F]{8})?(?:&REV_[0-9A-F]{2})?", RegexOptions.IgnoreCase)]
    private static partial Regex PciId();

    [GeneratedRegex(@"^\s*DriverVer\s*=\s*(\d{1,2}/\d{1,2}/\d{4})\s*,\s*(\d+(?:\.\d+){1,3})", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex DriverVerLine();

    [GeneratedRegex(@"^PRO[0-9A-Z]+\\Winx64\\W11\\[^\\]+\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex W11Inf();

    [GeneratedRegex(@"<meta\s+name=""DownloadVersion""\s+content=""(\d+(?:\.\d+){1,3})""", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadVersion();

    [GeneratedRegex(@"https://downloadmirror\.intel\.com/\d+/Wired_driver_[\d.]+_x64\.zip", RegexOptions.IgnoreCase)]
    private static partial Regex WiredZip();

    [GeneratedRegex(@"https://downloadmirror\.intel\.com/\d+/WiFi-(\d+\.\d+\.\d+)-Driver64-Win10-Win11\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex WifiExe();

    [GeneratedRegex(@"SHA512:\s*([0-9a-fA-F]{128})\b")]
    private static partial Regex Sha512Text();

    [GeneratedRegex(@"Date</label>\s*<span[^>]*>\s*(\d{1,2}/\d{1,2}/\d{4})\s*</span>")]
    private static partial Regex PageDate();

    [GeneratedRegex(@"<td>([^<]*Auto Installation Program[^<]*)</td>\s*<td>\s*(\d+(?:\.\d+){1,3})\s*</td>", RegexOptions.IgnoreCase)]
    private static partial Regex RealtekRow();

    [GeneratedRegex(@"(\d+(?:\.\d+){3})\s*\(Windows 11\*?\)\s*for Intel(?:®|&reg;|\(R\))?\s*Killer(?:™|&trade;|\(TM\))?\s*Ethernet\s+(E\d{4}G?(?:\s*,\s*E\d{4}G?)*)", RegexOptions.IgnoreCase)]
    private static partial Regex KillerLine();

    [GeneratedRegex(@"&SUBSYS_[0-9A-F]{4}1A56", RegexOptions.IgnoreCase)]
    private static partial Regex KillerSubsys();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"^oem\d{1,5}\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex OemInf();

    [GeneratedRegex(@"^[a-z0-9_]{1,40}\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex InboxInf();

    internal const int MaxRedirects = 5;

    internal static async Task<HttpResponseMessage> SendWithRedirectsAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        string url,
        Func<HttpRequestMessage>? buildRequest,
        CancellationToken ct)
    {
        var current = new Uri(url, UriKind.Absolute);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.AbsoluteUri };
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = buildRequest?.Invoke() ?? NativeHost.Request(current.AbsoluteUri);
            request.RequestUri = current;
            var response = await send(request, ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } loc)
            {
                var next = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
                response.Dispose();
                if (next.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                    next.Host.EndsWith(".intel.com", StringComparison.OrdinalIgnoreCase))
                {
                    next = new UriBuilder(next) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
                }
                if (!visited.Add(next.AbsoluteUri))
                    throw new HttpRequestException($"Intel redirect loop detected at {next.AbsoluteUri}.");
                current = next;
                continue;
            }
            return response;
        }
        throw new HttpRequestException($"Too many redirects reading {url}.");
    }

    private sealed class NativeHost : IHost
    {
        private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromMinutes(10),
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.All });

        /// <summary>Every PCI or USB Ethernet or Wi-Fi adapter Windows lists now (connected or not), from the network class keys.</summary>
        public IReadOnlyList<Adapter> Adapters()
        {
            Dictionary<string, NetworkInterface> nics;
            try
            {
                nics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211)
                    .GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            }
            catch (NetworkInformationException) { return []; }
            var list = new List<Adapter>();
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(NetworkTune.ClassKey);
                foreach (var name in root?.GetSubKeyNames() ?? [])
                {
                    if (!NetworkTune.KeyName().IsMatch(name)) continue;
                    try
                    {
                        using var key = root!.OpenSubKey(name);
                        if (key?.GetValue("NetCfgInstanceId") is not string guid || !nics.TryGetValue(guid, out var nic)) continue;
                        if (key.GetValue("DeviceInstanceID") is not string device || !NetworkTune.DeviceId().IsMatch(device)) continue;
                        list.Add(new Adapter(device, key.GetValue("DriverDesc") as string ?? nic.Description, nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                            key.GetValue("ProviderName") as string, key.GetValue("DriverVersion") as string, ClassDate(key.GetValue("DriverDate") as string), key.GetValue("InfPath") as string));
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { /* the next adapter */ }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { /* what was read */ }
            return list;
        }

        /// <summary>The class key's DriverDate ("3-24-2026") as yyyy-MM-dd.</summary>
        private static string? ClassDate(string? text) =>
            DateTime.TryParseExact(text, ["M-d-yyyy", "MM-dd-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

        public async Task<string> GetTextAsync(string url, CancellationToken ct)
        {
            using var response = await SendWithRedirectsAsync((req, c) => Http.SendAsync(req, c), url, null, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        public async Task<(byte[] Bytes, long Total)> GetRangeAsync(string url, long from, int length, CancellationToken ct)
        {
            using var response = await SendWithRedirectsAsync((req, c) => Http.SendAsync(req, c), url, () =>
            {
                var request = Request(url);
                request.Headers.Range = from < 0 ? new RangeHeaderValue(null, -from) : new RangeHeaderValue(from, from + length - 1);
                return request;
            }, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Host != IntelHost) throw new InvalidDataException("The package didn't come from Intel.");
            if (response.StatusCode != System.Net.HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.Length is not { } total)
                throw new InvalidDataException("Intel's server didn't send part of the package.");
            if (total > MaxZipBytes) throw new InvalidDataException("Intel's package is larger than a driver.");
            return (await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), total);
        }

        public async Task DownloadAsync(string url, string path, IProgress<int>? progress, CancellationToken ct)
        {
            var partial = path + ".part";
            using var response = await SendWithRedirectsAsync((req, c) => Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, c), url, null, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Host != IntelHost) throw new InvalidDataException("The download didn't come from Intel.");
            var total = response.Content.Headers.ContentLength ?? 0;
            if (total > MaxZipBytes) throw new InvalidDataException("Intel's download is larger than a driver.");
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await StallGuard.ReadAsync(source, buffer, ct).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > MaxZipBytes) throw new InvalidDataException("Intel's download is larger than a driver.");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    if (total > 0) progress?.Report((int)(done * 100 / total));
                }
                if (total > 0 && done != total) throw new InvalidDataException("The download was cut short.");
            }
            File.Move(partial, path, true);
        }

        /// <summary>Intel's driver catalog is signed for Windows (WHQL) or by Intel.</summary>
        public bool Signed(string catalog) => AuthenticodeVerifier.HasPublisher(catalog, Whql, GpuDriverUpdate.IntelPublisher);

        public bool GameRunning() => App.Services.Launcher.AnyGameRunning || GameRunningSignal.Running || MainWindow.RunningLibraryGames().Count > 0;
        public bool InCall() => DiscordClient.DiscordSession.Current?.InCall == true;
        public bool OtherInstall() => DriverInstallSignal.Installing || GpuDriverUpdate.Updating is not null;
        public bool Downloading() => App.Services.Launcher.CurrentProgress.IsActive;
        public bool ExoUpdating() => App.Services.Updater.Installing;
        public string? Elevated(IReadOnlyList<string> commands) => ElevatedCommands.Run(commands, "Network driver");
        public string? Advised(string subject, string version) => DriverAdvisories.Shared.Refuse(subject, version);

        internal static HttpRequestMessage Request(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Intel's site turns browser-style and bare agents away and lets one through that names itself with a link (GpuDriverUpdate.Request).
            request.Headers.TryAddWithoutValidation("User-Agent", "ExoLauncher/1.0 (+https://getexo.app)");
            return request;
        }
    }
}
