using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExoLauncher.Helpers;

namespace ExoLauncher.Services;

/// <summary>
/// Windows Update's driver catalog for the Drivers list: the Windows Update Agent's search for drivers not installed
/// (Microsoft.Update.Session, IsInstalled=0 and Type='Driver'), each matched to the device whose hardware or compatible IDs
/// name it, with the driver that device runs (Win32_PnPSignedDriver). The search needs no administrator rights (checked on
/// this PC: about 9 s); installing does. It runs in Windows PowerShell out of Exo's process (no COM binder in a trimmed
/// app), hidden, below normal priority, and is kept 6 hours. Every driver here is Microsoft-signed.
///
/// Install (<see cref="InstallBatchAsync"/>) is one elevated run for the batch, so Windows asks once: for each driver it
/// keeps the device's current package first (pnputil /export-driver, maker packages only), downloads and installs the
/// update through the agent, and adds the kept package back when the install fails. A driver with a license to accept is
/// left out (Exo doesn't accept terms for you). Right before the batch each driver is checked once more against the
/// advisories (<paramref name="refuse"/>: why one mustn't install, or null); one with a known problem stays out. As each
/// driver finishes, the script writes the answers so far to a second file, read every second, so Update all counts the
/// drivers in as they land rather than all at the end.
/// </summary>
internal sealed partial class WindowsUpdateDrivers(string folder, Func<DriverItem, string?>? refuse = null) : IDriverProvider
{
    /// <summary>
    /// An offer from Windows Update: its update id and title ("Realtek - Audio - 6.0.9600.1", newer titles "Microsoft
    /// Corporation AudioProcessingObject Driver Update (1.0.4.7057)"), the hardware id it's for, its class ("Display",
    /// "System", "Net"...), maker, date, whether hidden, and the device it matched with that device's driver.
    /// </summary>
    internal sealed record Offer(string Id, string Title, string Class, string? Maker, string? Model, string? Version, string? Date, bool Hidden,
        string? DeviceId, string? Name, string? Installed, string? InstalledDate, string? Inf)
    {
        /// <summary>
        /// A vendor's app shipped as a driver (class SoftwareComponent: a component INF that adds a service or an app,
        /// learn.microsoft.com/windows-hardware/drivers/install/using-a-component-inf-file), not a device's driver.
        /// </summary>
        internal bool App => Class.Equals("SoftwareComponent", StringComparison.OrdinalIgnoreCase);

        internal DriverItem Item() => new("windows:" + Id, "windows", Model ?? Name ?? Title, Maker, Installed, InstalledDate, Version, Date,
            DriverHow.Install, null, false) { Source = "Windows Update", Device = $"{DeviceId} {Name}".Trim(), App = App };
    }

    internal static WindowsUpdateDrivers Shared { get; } = new(Path.Combine(PathHelper.AppDataDir, "drivers"), item => DriverAdvisories.Shared.Refuse(item.Subject, item.Latest));

    private static readonly TimeSpan Keep = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTime At, IReadOnlyList<Offer> Offers)? _last;
    private int _running;

    private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    // ---- Reading ----

    /// <summary>
    /// The agent's driver offers, kept 6 hours, across Exo's restarts too (the search's answer is kept on disk: the search
    /// takes Windows 10 s and more, so Drivers shows at once); empty when the search fails (logged).
    /// </summary>
    internal async Task<IReadOnlyList<Offer>> OffersAsync(CancellationToken ct, bool fresh = false)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _last ??= Saved();
            if (!fresh && _last is { } last && DateTime.UtcNow - last.At < Keep) return last.Offers;
            var json = await RunAsync(SearchScript, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
            var offers = json is null ? null : Parse(json);
            if (offers is null) return _last?.Offers ?? [];
            _last = (DateTime.UtcNow, offers);
            Save(json!);
            AppLog.Info($"Drivers: Windows Update offers {offers.Count} driver(s).");
            return offers;
        }
        finally { _gate.Release(); }
    }

    private string SavedPath => Path.Combine(folder, "offers.json");

    /// <summary>The last search's answer from disk, as of when it was written; null when there's none or it doesn't read.</summary>
    private (DateTime At, IReadOnlyList<Offer> Offers)? Saved()
    {
        try
        {
            var file = new FileInfo(SavedPath);
            return file.Exists && Parse(File.ReadAllText(file.FullName)) is { } offers ? (file.LastWriteTimeUtc, offers) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void Save(string json)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var temp = SavedPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, SavedPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("Drivers: Windows Update's answer wasn't kept: " + ex.GetType().Name); }
    }

    public async Task<IReadOnlyList<DriverItem>> ReadAsync(CancellationToken ct) =>
        DriverCatalog.Merge([], await OffersAsync(ct).ConfigureAwait(false), [], null);

    /// <summary>The search's JSON (an array of objects), or null when it isn't one.</summary>
    internal static IReadOnlyList<Offer>? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.Trim() is { Length: > 0 } t ? t : "[]");
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object) return [Read(root)];
            if (root.ValueKind != JsonValueKind.Array) return null;
            return [.. root.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(Read)];
        }
        catch (JsonException ex)
        {
            AppLog.Warn("Drivers: Windows Update's answer wasn't read: " + ex.Message);
            return null;
        }

        static Offer Read(JsonElement e)
        {
            string? S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
            bool B(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            var title = S("Title") ?? "";
            return new Offer(S("Id") ?? "", title, S("Class") ?? "", S("Maker"), S("Model"), TitleVersion(title), S("Date"), B("Hidden"),
                S("Device"), S("Name"), S("Version"), S("VersionDate"), S("Inf"));
        }
    }

    /// <summary>The driver version in an update's title: "(1.0.4.7057)" at its end, else the last " - " part ("Intel - System - 10.1.1.44").</summary>
    internal static string? TitleVersion(string title)
    {
        if (Parenthesized().Match(title) is { Success: true } p) return p.Groups[1].Value;
        var last = title.Split(" - ").LastOrDefault()?.Trim();
        return last is not null && VersionNumber.Parse(last) is not null && last.Contains('.') ? last : null;
    }

    [GeneratedRegex(@"\((\d+(?:\.\d+)+)\)\s*$")]
    private static partial Regex Parenthesized();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex Guid();

    // ---- Installing ----

    public string? Busy() => Volatile.Read(ref _running) == 1 ? "Drivers are updating." : null;

    public string Working(DriverItem item) => "Updating";

    public async Task<DriverResult> InstallAsync(DriverItem item, IProgress<int>? progress, CancellationToken ct) =>
        (await InstallBatchAsync([item], ct).ConfigureAwait(false)).FirstOrDefault().Result ?? new DriverResult(false, "Windows Update didn't install it.");

    /// <summary>The kept package goes back only when an install fails; a driver that installed is rolled back from Windows' own Roll Back Driver.</summary>
    public Task<DriverResult> RollBackAsync(DriverItem item, CancellationToken ct) =>
        Task.FromResult(new DriverResult(false, "There's no earlier driver to go back to."));

    /// <summary>
    /// Installs the batch under one Windows prompt; each item's result, in order. <paramref name="landed"/> hears each
    /// driver as it finishes (its result then may still change: a later driver can make the batch want a restart).
    /// </summary>
    internal async Task<IReadOnlyList<(DriverItem Item, DriverResult Result)>> InstallBatchAsync(IReadOnlyList<DriverItem> items, CancellationToken ct,
        Action<DriverItem, DriverResult>? landed = null)
    {
        var held = items.Select(i => (Item: i, Why: refuse?.Invoke(i))).Where(h => h.Why is not null).ToList();
        if (held.Count > 0)
        {
            foreach (var (item, why) in held) landed?.Invoke(item, new DriverResult(false, why!));
            var rest = items.Where(i => held.All(h => h.Item != i)).ToList();
            var results = rest.Count == 0 ? [] : await InstallBatchAsync(rest, ct, landed).ConfigureAwait(false);
            return [.. items.Select(i => held.FirstOrDefault(h => h.Item == i) is { Why: { } why } ? (i, new DriverResult(false, why)) : results.First(r => r.Item == i))];
        }
        var ids = items.Select(i => i.Id["windows:".Length..]).Where(id => Guid().IsMatch(id)).ToList();
        if (ids.Count == 0) return [.. items.Select(i => (i, new DriverResult(false, "Windows Update didn't name this driver.")))];
        if (Interlocked.Exchange(ref _running, 1) == 1) return [.. items.Select(i => (i, new DriverResult(false, "Drivers are updating.")))];
        try
        {
            Directory.CreateDirectory(folder);
            var result = Path.Combine(folder, "windows-update-result.json");
            var sofar = Path.Combine(folder, "windows-update-progress.json");
            DeletionGate.DeleteFile(result);
            DeletionGate.DeleteFile(sofar);
            var run = Task.Run(() => Elevated(InstallScript(ids, Path.Combine(folder, "kept"), result, sofar), TimeSpan.FromMinutes(30)), ct);
            if (landed is not null)
            {
                var told = new HashSet<string>(StringComparer.Ordinal);
                while (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false) != run)
                    foreach (var (item, done) in Results(items, ReadShared(sofar), landedOnly: true))
                        if (told.Add(item.Id)) landed(item, done);
            }
            var refused = await run.ConfigureAwait(false);
            if (refused is not null) return [.. items.Select(i => (i, new DriverResult(false, refused)))];
            var answer = File.Exists(result) ? await File.ReadAllTextAsync(result, ct).ConfigureAwait(false) : null;
            _last = null;
            return Results(items, answer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Drivers: Windows Update install failed: " + ex.Message);
            return [.. items.Select(i => (i, new DriverResult(false, "The update stopped. Your drivers are unchanged.")))];
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    /// <summary>
    /// The install script's answer ({"Reboot":bool,"Results":[{"Id","Ok","Code","RolledBack","Error"}]}) as each item's
    /// result. The agent's result codes: 2 succeeded, 3 succeeded with errors, 4 failed, 5 aborted. With
    /// <paramref name="landedOnly"/> (its answers so far, mid-run), only the items it has answered; nothing when it can't
    /// be read yet, never the rest as failed.
    /// </summary>
    internal static IReadOnlyList<(DriverItem Item, DriverResult Result)> Results(IReadOnlyList<DriverItem> items, string? json, bool landedOnly = false)
    {
        var byId = new Dictionary<string, (bool Ok, bool Back, string? Error)>(StringComparer.OrdinalIgnoreCase);
        var reboot = false;
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            reboot = doc.RootElement.TryGetProperty("Reboot", out var r) && r.ValueKind == JsonValueKind.True;
            if (doc.RootElement.TryGetProperty("Results", out var list))
                foreach (var e in list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : Enumerable.Repeat(list, 1))
                    if (e.TryGetProperty("Id", out var id) && id.GetString() is { } key)
                        byId[key] = (e.TryGetProperty("Ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                            e.TryGetProperty("RolledBack", out var back) && back.ValueKind == JsonValueKind.True,
                            e.TryGetProperty("Error", out var err) ? err.GetString() : null);
        }
        catch (JsonException) { /* no answer: every item reads as not installed (mid-run: none has landed) */ }
        return [.. items.Where(item => !landedOnly || byId.ContainsKey(item.Id["windows:".Length..])).Select(item => (item, byId.TryGetValue(item.Id["windows:".Length..], out var r)
            ? r.Ok ? new DriverResult(true, reboot ? "Installed · restart to finish" : $"Updated to {item.Latest}.", reboot)
                : new DriverResult(false, r.Back ? "Didn't install. Your earlier driver is back." : r.Error is "license" ? "Its license needs accepting in Windows Update." : "Didn't install. Your driver is unchanged.")
            : new DriverResult(false, "Windows Update didn't install it.")))];
    }

    /// <summary>A file the elevated script may be writing right now, read without standing in its way; null when it can't be.</summary>
    private static string? ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- PowerShell ----

    /// <summary>Runs a script hidden, below normal priority; its standard output, or null when it failed or ran out of time.</summary>
    private static async Task<string?> RunAsync(string script, TimeSpan limit, CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(PowerShell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Encode(script))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            });
            if (process is null) return null;
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { /* already ended */ }
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limit);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(false);
                if (process.ExitCode != 0) AppLog.Warn($"Drivers: Windows Update search ended {process.ExitCode}: {(await error.ConfigureAwait(false)).Trim()}");
                return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
            }
            finally
            {
                try { timeout.Cancel(); } catch { }
                try { if (!process.HasExited) ProcessTermination.Kill(process, entireProcessTree: true); } catch { }
                try { if (!process.HasExited) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
                await ProcessOutput.DrainAndObserveAsync(output, error, TimeSpan.FromSeconds(2), timeout).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            AppLog.Warn("Drivers: Windows Update search ran out of time.");
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AppLog.Warn("Drivers: Windows Update search didn't start: " + ex.Message);
            return null;
        }
    }

    /// <summary>Runs a script as administrator (one Windows prompt) and waits for it; null when it ran, else the line to show.</summary>
    private static string? Elevated(string script, TimeSpan limit)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(PowerShell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Encode(script))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.SystemDirectory,
            });
            if (process is null) return "Windows didn't start the update.";
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* elevated: not ours to set */ }
            if (!process.WaitForExit(limit)) return "The update is taking long; it goes on in the background.";
            if (process.ExitCode != 0) AppLog.Warn($"Drivers: Windows Update install ended with exit code {process.ExitCode}.");
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Windows' permission was declined, so nothing changed.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn("Drivers: Windows Update install didn't start: " + ex.Message);
            return "Windows didn't start the update.";
        }
    }

    private static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>A path in single quotes for PowerShell (a quote doubled).</summary>
    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    // Windows Update's service stops when idle and can refuse the first search while it starts again (0x8024001E, seen
    // as Exo opened): up to three tries, 5 s apart.
    private const string Search = """
        $r = $null
        foreach ($try in 1..3) {
          try { $r = $s.CreateUpdateSearcher().Search("IsInstalled=0 and Type='Driver'"); break }
          catch { if ($try -eq 3) { throw }; Start-Sleep -Seconds 5 }
        }
        """;

    private const string Devices = """
        $devs = @(Get-CimInstance Win32_PnPEntity -Property DeviceID,Name,HardwareID,CompatibleID)
        $signed = @{}
        foreach ($d in Get-CimInstance Win32_PnPSignedDriver -Property DeviceID,DriverVersion,DriverDate,InfName) { if ($d.DeviceID) { $signed[$d.DeviceID] = $d } }
        function Find($h) { $devs | Where-Object { ($_.HardwareID -contains $h) -or ($_.CompatibleID -contains $h) } | Select-Object -First 1 }
        """;

    internal const string SearchScript = "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'\n"
        + "$s = New-Object -ComObject Microsoft.Update.Session\n$s.ClientApplicationID = 'Exo Launcher'\n"
        + Search + "\n"
        + Devices + "\n"
        + """
        $out = foreach ($u in $r.Updates) {
          $h = [string]$u.DriverHardwareID
          $dev = Find $h
          $sd = if ($dev) { $signed[$dev.DeviceID] } else { $null }
          [pscustomobject]@{
            Id = [string]$u.Identity.UpdateID; Title = [string]$u.Title; Model = [string]$u.DriverModel
            Maker = [string]$u.DriverManufacturer; Class = [string]$u.DriverClass
            Date = if ($u.DriverVerDate) { ([datetime]$u.DriverVerDate).ToString('yyyy-MM-dd') } else { $null }
            Hidden = [bool]$u.IsHidden
            Device = if ($dev) { [string]$dev.DeviceID } else { $null }; Name = if ($dev) { [string]$dev.Name } else { $null }
            Version = if ($sd) { [string]$sd.DriverVersion } else { $null }
            VersionDate = if ($sd -and $sd.DriverDate) { ([datetime]$sd.DriverDate).ToString('yyyy-MM-dd') } else { $null }
            Inf = if ($sd) { [string]$sd.InfName } else { $null }
          }
        }
        ConvertTo-Json -InputObject @($out) -Depth 3 -Compress
        """;

    /// <summary>
    /// The elevated batch: keep, download, install, put the kept package back on failure, write the answer. After each
    /// driver it writes the answers so far to <paramref name="sofar"/>; a write that fails (Exo reading it that moment)
    /// is skipped, never the batch.
    /// </summary>
    internal static string InstallScript(IReadOnlyList<string> ids, string kept, string result, string sofar) =>
        "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'\n"
        + $"$ids = @({string.Join(",", ids.Select(Quote))})\n$kept = {Quote(kept)}\n$result = {Quote(result)}\n$sofar = {Quote(sofar)}\n"
        + "$results = @(); $reboot = $false\ntry {\n"
        + "$s = New-Object -ComObject Microsoft.Update.Session\n$s.ClientApplicationID = 'Exo Launcher'\n"
        + Search + "\n"
        + Devices + "\n"
        + """
        function Landed { try { [pscustomobject]@{ Reboot = $reboot; Results = @($results) } | ConvertTo-Json -Depth 4 -Compress | Set-Content -Encoding UTF8 -Path $sofar } catch { } }
        foreach ($u in $r.Updates) {
          $id = [string]$u.Identity.UpdateID
          if ($ids -notcontains $id) { continue }
          if (-not $u.EulaAccepted) { $results += [pscustomobject]@{ Id = $id; Ok = $false; Error = 'license' }; Landed; continue }
          $dev = Find ([string]$u.DriverHardwareID)
          $inf = if ($dev -and $signed[$dev.DeviceID]) { [string]$signed[$dev.DeviceID].InfName } else { $null }
          $keep = $null
          if ($inf -match '^oem\d+\.inf$') {
            $keep = Join-Path $kept $id
            New-Item -ItemType Directory -Force -Path $keep | Out-Null
            & "$env:SystemRoot\System32\pnputil.exe" /export-driver $inf $keep | Out-Null
          }
          $c = New-Object -ComObject Microsoft.Update.UpdateColl
          [void]$c.Add($u)
          $ok = $false; $err = $null
          try {
            $dl = $s.CreateUpdateDownloader(); $dl.Updates = $c
            if ($dl.Download().ResultCode -eq 2) {
              $in = $s.CreateUpdateInstaller(); $in.Updates = $c
              $ir = $in.Install()
              $ok = $ir.ResultCode -in 2,3
              if ($ir.RebootRequired) { $reboot = $true }
            }
          } catch { $ok = $false; $err = $_.Exception.Message }
          $back = $false
          if (-not $ok -and $keep) {
            foreach ($k in Get-ChildItem -Path $keep -Filter *.inf -Recurse) { & "$env:SystemRoot\System32\pnputil.exe" /add-driver $k.FullName /install | Out-Null; $back = $true }
          }
          $results += [pscustomobject]@{ Id = $id; Ok = $ok; RolledBack = $back; Error = $err }
          Landed
        }
        } catch { $results += [pscustomobject]@{ Id = ''; Ok = $false; Error = $_.Exception.Message } }
        [pscustomobject]@{ Reboot = $reboot; Results = @($results) } | ConvertTo-Json -Depth 4 -Compress | Set-Content -Encoding UTF8 -Path $result
        """;
}
