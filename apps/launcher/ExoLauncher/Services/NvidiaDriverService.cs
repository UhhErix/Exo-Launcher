using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

internal static partial class NvidiaDriverService
{
    internal sealed record Latest(string Version, string Url, long Size, string? Released, string Kind);

    internal sealed record Progress(string Stage, int Percent, string? Message);

    private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromMinutes(30));
    private static readonly Lock Gate = new();
    internal const bool OfferHotfixes = true;
    private static readonly LatestCache<Latest> Drivers = new("NVIDIA's driver list", TimeSpan.FromHours(1), "nvidia-drivers.json");
    private static readonly LatestCache<Latest> ServerHotfixes = new("NVIDIA's hotfix packages", TimeSpan.FromHours(6));
    private static Progress _progress = new("idle", 0, null);
    private static Task? _running;

    internal static event Action<Progress>? Changed;

    internal static Progress Current { get { lock (Gate) return _progress; } }

    private static void Report(string stage, int percent = 0, string? message = null)
    {
        Progress next;
        lock (Gate) next = _progress = new(stage, percent, message);
        Changed?.Invoke(next);
    }

    internal static string? Installed() => WindowsDriverVersion()
        ?? (NvidiaProfiles.DriverVersion() is { } v and > 0 ? $"{v / 100}.{v % 100:00}" : null);

    internal static string? WindowsDriverVersion() => NvidiaAdapter("DriverVersion") is { } version ? FromWindowsVersion(version) : null;

    internal static int OsId(Version? osVersion = null) =>
        (osVersion ?? Environment.OSVersion.Version).Build >= 22000 ? 135 : 57;

    internal static string? PickNvidiaSubkeyMatch(IEnumerable<(string? Provider, string? MatchingDeviceId, string? Value)> entries, uint? activeDeviceId)
    {
        string? fallback = null;
        var needle = activeDeviceId is { } dev ? $"ven_10de&dev_{dev:x4}" : null;
        foreach (var (provider, matchingDeviceId, value) in entries)
        {
            if (provider is null || !provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || value is null) continue;
            fallback ??= value;
            if (needle is not null && matchingDeviceId is not null && matchingDeviceId.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return fallback;
    }

    private static string? NvidiaAdapter(string value)
    {
        try
        {
            using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (display is null) return null;
            var activeDev = PcHealth.ActiveNvidiaDeviceId();
            var rows = new List<(string? Provider, string? MatchingDeviceId, string? Value)>();
            foreach (var name in display.GetSubKeyNames().Where(name => name.All(char.IsDigit)))
            {
                using var adapter = display.OpenSubKey(name);
                if (adapter is null) continue;
                rows.Add((adapter.GetValue("ProviderName") as string, adapter.GetValue("MatchingDeviceId") as string, adapter.GetValue(value) as string));
            }
            return PickNvidiaSubkeyMatch(rows, activeDev);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }

    internal static bool CurrentBranches(string? hardwareId) =>
        hardwareId is not null && DeviceId().Match(hardwareId) is { Success: true } match
        && int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) >= 0x1E00;

    [System.Text.RegularExpressions.GeneratedRegex(@"ven_10de&dev_([0-9a-f]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DeviceId();

    internal static string? FromWindowsVersion(string version)
    {
        var parts = version.Split('.');
        if (parts.Length != 4 || !parts.All(part => part.Length > 0 && part.All(char.IsDigit)) || parts[3].Length > 4) return null;
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return null;
        var tail = digits[^5..];
        return $"{int.Parse(tail[..3], CultureInfo.InvariantCulture)}.{tail[3..]}";
    }

    internal static string? Card() => GpuCapability.Adapters().FirstOrDefault(adapter => GraphicsService.Vendors([adapter]).Contains("nvidia"));

    internal static bool Workstation(string card)
    {
        if (card.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) return false;
        var name = Plain(card);
        return name.StartsWith("Quadro", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RTX ", StringComparison.OrdinalIgnoreCase)
            || (name.Length is > 1 and <= 6 && name[0] == 'T' && name.Skip(1).All(char.IsDigit));
    }

    private static string Plain(string name) => name.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase).Replace(" GPU", "", StringComparison.OrdinalIgnoreCase).Trim();

    internal static bool ChecksHotfix(string card, bool workstation, bool studio, string? hardwareId) =>
        OfferHotfixes && !workstation && !studio && CurrentBranches(hardwareId)
        && card.Contains("GeForce", StringComparison.OrdinalIgnoreCase);

    internal static Task<Latest?> LatestAsync(string branch, CancellationToken ct, bool fresh = false)
    {
        SweepUnpacked();
        if (Card() is not { } card) return Task.FromResult<Latest?>(null);
        var workstation = Workstation(card);
        var studio = !workstation && branch == "studio";
        var hardwareId = NvidiaAdapter("MatchingDeviceId");
        var hotfix = ChecksHotfix(card, workstation, studio, hardwareId);
        var key = card + "|" + hardwareId + "|" + (workstation ? "workstation" : studio ? "studio" : hotfix ? "game|hf" : "game");
        return Drivers.GetAsync(key, lookup => FetchAsync(card, workstation, studio, hotfix, lookup), ct, fresh);
    }

    private static async Task<Latest?> FetchAsync(string card, bool workstation, bool studio, bool hotfix, CancellationToken ct)
    {
        Latest? latest = null;
        var products = await ProductsAsync(card, ct).ConfigureAwait(false);
        if (products.Count == 0) AppLog.Info($"Driver: NVIDIA lists no product named \"{Plain(card)}\".");
        foreach (var (series, product) in products)
        {
            var url = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup" +
                $"&psid={series}&pfid={product}&osID={OsId()}&languageCode=1033&isWHQL={(studio ? 0 : 1)}&dch=1&upCRD={(studio ? 1 : 0)}&sort1=0&numberOfResults=3";
            using var doc = JsonDocument.Parse(await GetTextAsync(url, ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in ids.EnumerateArray())
            {
                var info = entry.GetProperty("downloadInfo");
                if (info.TryGetProperty("IsBeta", out var beta) && beta.GetString() == "1") continue;
                var version = info.GetProperty("Version").GetString();
                var download = info.GetProperty("DownloadURL").GetString();
                if (version is null || download is null || !download.StartsWith("https://us.download.nvidia.com/", StringComparison.OrdinalIgnoreCase)) continue;
                var released = info.TryGetProperty("ReleaseDateTime", out var r) ? r.GetString() : null;
                var size = info.TryGetProperty("DownloadURLFileSize", out var sz) && double.TryParse(sz.GetString()?.Split(' ')[0],
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mb) ? (long)(mb * 1_000_000) : 0;
                var security = info.TryGetProperty("Name", out var n) && n.GetString()?.Contains("Security", StringComparison.OrdinalIgnoreCase) == true;
                if (latest is null || Compare(version, latest.Version) > 0)
                    latest = new Latest(version, download, size, released, workstation ? "workstation" : studio ? "studio" : security ? "security" : "game");
                break;
            }
        }
        if (products.Count > 0 && hotfix && await HotfixAsync(card, latest?.Version, ct).ConfigureAwait(false) is { } newer && (latest is null || Compare(newer.Version, latest.Version) > 0))
            latest = newer;
        return latest;
    }

    private static async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        Drivers.SlowDown(response);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static async Task<List<(string Series, string Product)>> ProductsAsync(string card, CancellationToken ct)
    {
        var name = Plain(card);
        var laptop = card.Contains("Laptop", StringComparison.OrdinalIgnoreCase) || card.Contains("Max-Q", StringComparison.OrdinalIgnoreCase);
        var type = Workstation(card) ? "3" : "1";
        var seriesDoc = XDocument.Parse(await GetTextAsync("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=2", ct).ConfigureAwait(false));
        var series = new Dictionary<string, bool>();
        foreach (var v in seriesDoc.Descendants("LookupValue"))
        {
            var id = (string?)v.Element("Value");
            var seriesName = (string?)v.Element("Name") ?? "";
            if (id is not null && (string?)v.Attribute("ParentID") == type)
                series[id] = seriesName.Contains("Notebook", StringComparison.OrdinalIgnoreCase) || seriesName.Contains("Laptop", StringComparison.OrdinalIgnoreCase);
        }
        var products = XDocument.Parse(await GetTextAsync("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3", ct).ConfigureAwait(false));
        var found = products.Descendants("LookupValue")
            .Where(v => string.Equals(Plain((string?)v.Element("Name") ?? ""), name, StringComparison.OrdinalIgnoreCase))
            .Select(v => (Series: (string?)v.Attribute("ParentID") ?? "", Product: (string?)v.Element("Value") ?? ""))
            .Where(v => v.Product.Length > 0 && series.ContainsKey(v.Series))
            .ToList();
        var own = found.Where(v => series[v.Series] == laptop).ToList();
        return own.Count > 0 || !laptop ? own : found;
    }

    private const string ForumsApi = "https://api-forums.nvidia.com/forums-api/1.0";

    private static async Task<Latest?> HotfixAsync(string card, string? gameReady, CancellationToken ct)
    {
        if (!card.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) return null;
        Latest? newest = null;
        try
        {
            var search = await GetTextAsync(ForumsApi + "/search?query=hotfix%20driver&order=created&_pageSize=20&_page=1", ct).ConfigureAwait(false);
            foreach (var id in HotfixTopics(search).Take(3))
                if (HotfixIn(await GetTextAsync(ForumsApi + "/topics?id=" + id, ct).ConfigureAwait(false)) is { } found
                    && (newest is null || Compare(found.Version, newest.Version) > 0))
                    newest = found;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            AppLog.Info("Driver: NVIDIA's hotfix announcements could not be read: " + ex.Message);
        }
        var known = newest is not null && (gameReady is null || Compare(newest.Version, gameReady) > 0) ? newest.Version : gameReady;
        if (known is not null)
        {
            try
            {
                if (await HotfixOnServerAsync(known, ct).ConfigureAwait(false) is { } posted) newest = posted;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                AppLog.Info("Driver: NVIDIA's hotfix server check could not be read: " + ex.Message);
            }
        }
        return newest is not null && (gameReady is null || Compare(newest.Version, gameReady) > 0) ? newest : null;
    }

    private static Task<Latest?> HotfixOnServerAsync(string newest, CancellationToken ct) =>
        ServerHotfixes.GetAsync(newest, async lookup =>
        {
            foreach (var batch in HotfixCandidates(newest).Chunk(16))
            {
                var answers = await Task.WhenAll(batch.Select(version => HotfixPackageAsync(version, lookup))).ConfigureAwait(false);
                if (answers.FirstOrDefault(a => a is not null) is not { } hit) continue;
                AppLog.Info($"Driver: hotfix {hit.Version} is on NVIDIA's download server.");
                return hit;
            }
            return null;
        }, ct);

    internal static List<string> HotfixCandidates(string newest)
    {
        var parts = newest.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) || minor is < 0 or > 99) return [];
        var candidates = new List<string>();
        for (var m = 30; m >= 0; m--) candidates.Add($"{major + 1}.{m:D2}");
        for (var m = 99; m > minor; m--) candidates.Add($"{major}.{m:D2}");
        return candidates;
    }

    internal static string HotfixUrl(string version) =>
        $"https://international.download.nvidia.com/Windows/{version}hf/{version}-desktop-notebook-win10-win11-64bit-international-dch.hf.exe";

    private static async Task<Latest?> HotfixPackageAsync(string version, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, HotfixUrl(version));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            ServerHotfixes.SlowDown(response);
            return null;
        }
        if (response.Content.Headers.ContentLength is not { } size || size < 100_000_000) return null;
        var released = response.Content.Headers.LastModified?.ToString("ddd MMM d, yyyy", CultureInfo.InvariantCulture);
        return new Latest(version, HotfixUrl(version), size, released, "hotfix");
    }

    internal static List<long> HotfixTopics(string searchJson)
    {
        using var doc = JsonDocument.Parse(searchJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return [];
        var topics = new List<(long Id, long Created)>();
        foreach (var group in data.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var topic in group.Value.EnumerateArray())
            {
                if (topic.ValueKind != JsonValueKind.Object || !topic.TryGetProperty("id", out var id) || !id.TryGetInt64(out var topicId)) continue;
                var title = topic.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? HtmlTag().Replace(t.GetString()!, "") : "";
                var official = topic.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                    && user.TryGetProperty("aclactions", out var acl) && acl.ValueKind == JsonValueKind.Object
                    && acl.TryGetProperty("officialreply", out var reply) && reply.ValueKind == JsonValueKind.True;
                if (!official || !title.Contains("hotfix", StringComparison.OrdinalIgnoreCase)) continue;
                topics.Add((topicId, topic.TryGetProperty("createdStamp", out var c) && c.TryGetInt64(out var at) ? at : 0));
            }
        }
        return topics.OrderByDescending(t => t.Created).Select(t => t.Id).Distinct().ToList();
    }

    internal static Latest? HotfixIn(string topicJson)
    {
        using var doc = JsonDocument.Parse(topicJson);
        var body = doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("topic", out var topic)
            && topic.TryGetProperty("firstComment", out var first) && first.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString()! : "";
        return HotfixLink().Matches(body)
            .Select(m => new Latest(m.Groups[1].Value, m.Value, 0, null, "hotfix"))
            .OrderByDescending(l => l.Version, Comparer<string>.Create(Compare))
            .FirstOrDefault();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<[^>]+>")]
    private static partial System.Text.RegularExpressions.Regex HtmlTag();

    [System.Text.RegularExpressions.GeneratedRegex(@"https://(?:international|us)\.download\.nvidia\.com/Windows/(\d{3}\.\d{2})hf/[\w.-]+\.hf\.exe")]
    private static partial System.Text.RegularExpressions.Regex HotfixLink();

    internal static int Compare(string a, string b) => VersionNumber.Compare(a, b);

    internal static string? Blocked() => _running is { IsCompleted: false } ? "A driver update is already running." : GpuDriverUpdate.Busy();

    internal static Latest At(string version, Latest? newest, bool laptop) =>
        new(version, newest is { Kind: "game" } n && n.Url.StartsWith("https://us.download.nvidia.com/Windows/", StringComparison.OrdinalIgnoreCase)
                ? n.Url.Replace(n.Version, version, StringComparison.Ordinal)
                : $"https://us.download.nvidia.com/Windows/{version}/{version}-{(laptop ? "notebook" : "desktop")}-win10-win11-64bit-international-dch-whql.exe",
            0, null, "game");

    internal static string? Start(Latest latest)
    {
        var card = Card();
        var laptop = card?.Contains("Laptop", StringComparison.OrdinalIgnoreCase) == true;
        if (DriverAdvisories.Shared.Refuse(DriverAdvisories.Gpu("nvidia", card), latest.Version) is { } advised) return advised;
        var audio = DisplayAudio();
        lock (Gate)
        {
            if (Blocked() is { } why) return why;
            _running = Task.Run(() => RunAsync(latest, laptop, audio));
        }
        return null;
    }

    internal static bool DisplayAudio(IEnumerable<(int State, string? Device)> endpoints) =>
        endpoints.Any(e => (e.State & 0xF) == 1 && e.Device?.Contains(@"HDAUDIO\FUNC_01&VEN_10DE", StringComparison.OrdinalIgnoreCase) == true);

    private static bool DisplayAudio()
    {
        try
        {
            using var render = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render");
            if (render is null) return false;
            var endpoints = new List<(int, string?)>();
            foreach (var name in render.GetSubKeyNames())
            {
                using var endpoint = render.OpenSubKey(name);
                using var properties = endpoint?.OpenSubKey("Properties");
                endpoints.Add((endpoint?.GetValue("DeviceState") is int state ? state : 0, properties?.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},2") as string));
            }
            return DisplayAudio(endpoints);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Info("Driver: the screens' audio wasn't read: " + ex.Message);
            return false;
        }
    }

    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher", "drivers");

    private static async Task RunAsync(Latest latest, bool laptop, bool audio)
    {
        var folder = Path.Combine(Root, latest.Version);
        var package = Path.Combine(folder, Path.GetFileName(new Uri(latest.Url).LocalPath));
        var unpacked = Path.Combine(folder, "unpacked");
        var stepped = false;
        void Step(string phase, string? message, DriverRestore.Outcome? outcome = null)
        {
            stepped = true;
            DriverInstallSignal.Report(new(phase, latest.Version, message, outcome?.Restored, outcome?.NotRestored));
        }
        InterruptMode.Chain? interrupts = null;
        var ran = false;
        try
        {
            Directory.CreateDirectory(folder);
            if (!File.Exists(package) || !AuthenticodeVerifier.HasPublisher(package, "NVIDIA Corporation"))
                await DownloadAsync(latest, package).ConfigureAwait(false);
            if (!AuthenticodeVerifier.HasPublisher(package, "NVIDIA Corporation"))
                throw new InvalidDataException("The download isn't signed by NVIDIA.");

            Report("unpacking", 0, "Unpacking the driver");
            if (Directory.Exists(unpacked)) DeletionGate.DeleteDirectory(unpacked, true);
            Directory.CreateDirectory(unpacked);
            string[] keep = ["setup.exe", "setup.cfg", "EULA.txt", "ListDevices.txt", "Display.Driver", "NVI2", .. laptop ? new[] { "NVPCF" } : [], .. audio ? new[] { "HDAudio" } : [],
                "NvApp/Unified_EULA", "NvApp/PrivacyPolicy", "NvApp/FunctionalConsent_*"];
            var unpackInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in (string[])["-xf", package, "-C", unpacked, .. keep]) unpackInfo.ArgumentList.Add(arg);
            using (var unpack = Process.Start(unpackInfo) ?? throw new InvalidOperationException("Windows' tar did not start."))
            {
                await unpack.WaitForExitAsync().ConfigureAwait(false);
                if (unpack.ExitCode != 0) throw new InvalidDataException("The driver package didn't unpack.");
            }
            var setup = Path.Combine(unpacked, "setup.exe");
            if (!File.Exists(setup) || !Directory.Exists(Path.Combine(unpacked, "Display.Driver"))) throw new InvalidDataException("The driver package didn't unpack.");
            if (!AuthenticodeVerifier.HasPublisher(setup, "NVIDIA Corporation")) throw new InvalidDataException("NVIDIA's installer isn't signed by NVIDIA.");

            await GpuDriverUpdate.WaitQuietAsync(GpuDriverUpdate.HoldingAsync, reason => { Report("waiting", 0, reason); Step("waiting", reason); }, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var screens = DriverRestore.Snapshot();

            Report("installing", 0, "Installing the driver. Your screens go black for a moment");
            Step("installing", "Installing the driver. Your screens go black for a moment");
            const string args = "-s -clean -noreboot -noeula -nofinish -nosplash";
            AppLog.Info($"Driver: clean install of {latest.Version} ({string.Join(", ", keep.Skip(4).Where(item => !item.StartsWith("NvApp/", StringComparison.Ordinal)))}).");
            interrupts = InterruptMode.ThisPc.ForDriverInstall();
            var extras = ReplacedApps.ForThisPc().DriverStepCommands();
            var chained = ChainedArguments(setup, unpacked, args, [.. interrupts.Lines, .. extras]);
            if (chained is null || interrupts.Lines.Count == 0)
            {
                if (chained is null && interrupts.Lines.Count > 0)
                {
                    AppLog.Info("Driver: the install's folder has a character cmd reads, so interrupts follow after it instead.");
                    InterruptMode.ThisPc.Rollback(interrupts);
                }
                interrupts = null;
            }
            int exit;
            try
            {
                var start = chained is null
                    ? new ProcessStartInfo(setup, args) { UseShellExecute = true, Verb = "runas", WorkingDirectory = unpacked }
                    : new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), chained)
                    {
                        UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Environment.SystemDirectory,
                    };
                using var install = Process.Start(start) ?? throw new InvalidOperationException("NVIDIA's installer did not start.");
                ran = true;
                if (chained is not null) AppLog.Info($"Driver: chained after setup: {interrupts?.Lines.Count ?? 0} interrupt value(s), {extras.Count} Clean up step(s).");
                await install.WaitForExitAsync().ConfigureAwait(false);
                exit = install.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Report("failed", 0, "Windows didn't get permission, so nothing changed");
                Step("failed", "Windows didn't get permission, so nothing changed");
                return;
            }
            Step("reloading", "Loading the new driver");
            var installed = Installed();
            for (var waited = 0; waited < 15 && exit == 0 && (installed is null || Compare(installed, latest.Version) != 0); waited++)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                installed = Installed();
            }
            AppLog.Info($"Driver: NVIDIA setup exited {exit}; driver now {installed ?? "unknown"}.");
            await MainWindow.ReloadAfterDriverInstallAsync().ConfigureAwait(false);
            if (exit != 0 || installed is null || Compare(installed, latest.Version) != 0)
            {
                Report("failed", 0, $"NVIDIA's installer stopped (code {exit}). Your driver is unchanged");
                Step("failed", $"NVIDIA's installer stopped (code {exit}). Your driver is unchanged");
                return;
            }

            NvidiaProfiles.Reinitialize();
            Drivers.Clear();
            if (interrupts is not null) InterruptMode.ThisPc.Written();
            Report("done", 100, $"Driver {latest.Version} installed");
            Step("restoring", "Putting your settings back");
            Step("done", $"Driver {latest.Version} installed",
                await DriverRestore.AfterInstallAsync("nvidia", screens, interruptsChained: interrupts is not null).ConfigureAwait(false));
            Prune(keepVersion: latest.Version);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Driver update failed: " + ex.Message);
            var message = ex is InvalidDataException ? ex.Message : "The update stopped. Your driver is unchanged";
            Report("failed", 0, message);
            if (stepped) Step("failed", message);
        }
        finally
        {
            if (interrupts is not null && !ran) InterruptMode.ThisPc.Rollback(interrupts);
            TryDelete(unpacked);
        }
    }

    internal static string? ChainedArguments(string setup, string folder, string args, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || setup.IndexOfAny(CmdSpecial) >= 0 || folder.IndexOfAny(CmdSpecial) >= 0) return null;
        return $"/d /v:on /s /c \"start \"\" /d \"{folder}\" /wait \"{setup}\" {args} & set \"EXO_RC=!ERRORLEVEL!\" & {string.Join(" & ", lines)} & exit !EXO_RC!\"";
    }

    private static readonly char[] CmdSpecial = ['"', '%', '!', '^'];

    private static async Task DownloadAsync(Latest latest, string path)
    {
        Report("downloading", 0, "Downloading from NVIDIA");
        var partial = path + ".part";
        try
        {
            using (var response = await Http.GetAsync(latest.Url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.Host is not ("us.download.nvidia.com" or "international.download.nvidia.com")) throw new InvalidDataException("The download didn't come from NVIDIA.");
                var total = response.Content.Headers.ContentLength ?? latest.Size;
                await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                var buffer = new byte[1 << 20];
                long done = 0;
                var lastPercent = -1;
                int read;
                while ((read = await StallGuard.ReadAsync(source, buffer).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    done += read;
                    var percent = total > 0 ? (int)(done * 100 / total) : 0;
                    if (percent != lastPercent) { lastPercent = percent; Report("downloading", percent, "Downloading from NVIDIA"); }
                }
                if (response.Content.Headers.ContentLength is { } length && done != length) throw new InvalidDataException("The download was cut short.");
            }
            File.Move(partial, path, true);
        }
        catch
        {
            try { DeletionGate.DeleteFile(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void Prune(string keepVersion)
    {
        try
        {
            var folders = Directory.EnumerateDirectories(Root).OrderByDescending(d => Path.GetFileName(d), Comparer<string>.Create(Compare)).ToList();
            foreach (var old in folders.Where(f => Path.GetFileName(f) != keepVersion).Skip(1)) TryDelete(old);
        }
        catch (Exception ex) { AppLog.Debug("Driver: pruning old installers: " + ex.Message); }
    }

    private static void SweepUnpacked()
    {
        lock (Gate) if (_running is { IsCompleted: false }) return;
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var version in Directory.EnumerateDirectories(Root)) TryDelete(Path.Combine(version, "unpacked"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Driver: sweeping unpacked drivers: " + ex.Message); }
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) DeletionGate.DeleteDirectory(folder, true); }
        catch (Exception ex) { AppLog.Debug("Driver: could not remove " + folder + ": " + ex.Message); }
    }
}
