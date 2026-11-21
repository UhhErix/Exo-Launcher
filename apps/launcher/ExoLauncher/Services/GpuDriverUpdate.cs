using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

internal static partial class GpuDriverUpdate
{
    internal sealed record Latest(string Vendor, string Version, string Url, long Size, string? Released, string Kind, string? Sha512 = null, string? Page = null);

    internal sealed record Ending(bool Ok, string Message);

    internal const string AmdPublisher = "Advanced Micro Devices";
    internal const string IntelPublisher = "Intel Corporation";
    private const long MaxBytes = 4L << 30;

    private static readonly HttpClient Http = ExoHttp.Create(TimeSpan.FromMinutes(30), new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
    private static readonly Lock Gate = new();
    private static readonly LatestCache<Latest> Drivers = new("AMD's and Intel's driver pages", TimeSpan.FromHours(1), "gpu-drivers.json");
    private static NvidiaDriverService.Progress _progress = new("idle", 0, null);
    private static Task? _running;
    private static string? _vendor;

    internal static event Action<NvidiaDriverService.Progress>? Changed;

    internal static NvidiaDriverService.Progress Current { get { lock (Gate) return _progress; } }

    private static void Report(string stage, int percent = 0, string? message = null)
    {
        NvidiaDriverService.Progress next;
        lock (Gate) next = _progress = new(stage, percent, message);
        Changed?.Invoke(next);
    }

    internal static string Maker(string vendor) => vendor == "amd" ? "AMD" : "Intel";

    internal static string? Card(string vendor) => Pick(vendor, GpuCapability.Adapters());

    internal static string? Pick(string vendor, IEnumerable<string> adapters) =>
        adapters.Where(name => GraphicsService.Vendors([name]).Contains(vendor)).OrderByDescending(GraphicsService.Discrete).FirstOrDefault();

    internal static string? PickInstalledValue(string vendor, string? radeonSoftwareVersion, string? driverVersion)
    {
        var radeon = string.IsNullOrWhiteSpace(radeonSoftwareVersion) ? null : radeonSoftwareVersion.Trim();
        var driver = string.IsNullOrWhiteSpace(driverVersion) ? null : driverVersion.Trim();
        return vendor == "amd" ? radeon ?? driver : driver;
    }

    internal static string? Installed(string vendor, string card)
    {
        var provider = vendor == "amd" ? "Advanced Micro Devices" : "Intel";
        string? fallback = null;
        try
        {
            using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (display is null) return null;
            foreach (var name in display.GetSubKeyNames().Where(name => name.Length == 4 && name.All(char.IsAsciiDigit)))
            {
                using var adapter = display.OpenSubKey(name);
                if (adapter is null) continue;
                var picked = PickInstalledValue(vendor,
                    adapter.GetValue("RadeonSoftwareVersion") as string,
                    adapter.GetValue("DriverVersion") as string);
                if (picked is null) continue;
                if (adapter.GetValue("DriverDesc") is string desc && string.Equals(desc.Trim(), card, StringComparison.OrdinalIgnoreCase)) return picked;
                if (adapter.GetValue("ProviderName") is string maker && maker.Contains(provider, StringComparison.OrdinalIgnoreCase)) fallback ??= picked;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return fallback;
    }

    internal static bool Newer(string latest, string? installed) => VersionNumber.Newer(latest, installed);

    internal static async Task<Latest?> LatestAsync(string vendor, CancellationToken ct, bool fresh = false)
    {
        SweepUnpacked(vendor);
        if (Card(vendor) is not { } card) return null;
        var cpu = PcHealth.CpuName();
        var key = $"{vendor}|{card}|{cpu}|hf";
        return await Drivers.GetAsync(key, lookup => FetchAsync(vendor, card, cpu, lookup), ct, fresh).ConfigureAwait(false);
    }

    private static async Task<Latest?> FetchAsync(string vendor, string card, string? cpu, CancellationToken ct)
    {
        Task<string> Get(string url) => GetTextAsync(vendor, url, ct);
        Task<string?> TryGet(string url) => TryGetTextAsync(vendor, url, ct);
        var latest = vendor == "amd"
            ? await AmdLatestAsync(card, cpu, Get, TryGet).ConfigureAwait(false)
            : await IntelLatestAsync(card, cpu, Get).ConfigureAwait(false);
        if (latest is null) AppLog.Info($"Driver: {Maker(vendor)}'s pages name no driver for {card}{(cpu is null ? "" : " with " + cpu)}.");
        return latest;
    }

    internal static HttpRequestMessage Request(string vendor, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (vendor == "amd")
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ExoLauncher");
            request.Headers.Referrer = new Uri("https://www.amd.com/");
        }
        else request.Headers.TryAddWithoutValidation("User-Agent", "ExoLauncher/1.0 (+https://getexo.app)");
        return request;
    }

    internal static async Task<string> GetTextAsync(string vendor, string url, CancellationToken ct)
    {
        using var request = Request(vendor, url);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        Drivers.SlowDown(response);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<string?> TryGetTextAsync(string vendor, string url, CancellationToken ct)
    {
        using var request = Request(vendor, url);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        Drivers.SlowDown(response);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<string?> HoldingAsync() =>
        GameRunning() ? "Waiting for your game to close"
        : DiscordClient.DiscordSession.Current?.InCall == true ? "Waiting for your Discord call to end"
        : ShadersAddon.Live is { } shaders && await shaders.CompilingAsync(CancellationToken.None).ConfigureAwait(false) ? "Waiting for Exo Shaders to finish compiling"
        : null;

    internal static string? Busy() =>
        GameRunning() ? "Close your game first."
        : DiscordClient.DiscordSession.Current?.InCall == true ? "Leave your Discord call first."
        : null;

    private static bool GameRunning() =>
        App.Services.Launcher.AnyGameRunning || LeanSignal.Active || MainWindow.RunningLibraryGames().Count > 0;

    internal static async Task WaitQuietAsync(Func<Task<string?>> holding, Action<string> waiting, TimeSpan poll, CancellationToken ct = default)
    {
        string? said = null;
        while (await holding().ConfigureAwait(false) is { } reason)
        {
            if (reason != said) waiting(said = reason);
            await Task.Delay(poll, ct).ConfigureAwait(false);
        }
    }

    internal static string? Blocked() => _running is { IsCompleted: false } ? "A driver update is already running." : Busy();

    internal static string? Updating { get { lock (Gate) return _running is { IsCompleted: false } ? _vendor : null; } }

    internal static string? Start(Latest latest)
    {
        if (Card(latest.Vendor) is not { } card) return "Exo can't find the graphics card.";
        if (DriverAdvisories.Shared.Refuse(DriverAdvisories.Gpu(latest.Vendor, card), latest.Version) is { } advised) return advised;
        lock (Gate)
        {
            if (Blocked() is { } why) return why;
            _vendor = latest.Vendor;
            _running = Task.Run(() => RunAsync(latest, card));
        }
        return null;
    }

    internal static string AmdArguments(string log, bool software) => $"-INSTALL {(software ? "-VIEW:2" : "-DRIVERONLY")} -LOG \"{log}\"";

    internal const string IntelArguments = "--overwrite --noExtras -s";

    internal static int? AmdResult(string? log)
    {
        if (log is null) return null;
        var text = log.Replace("\0", "", StringComparison.Ordinal);
        var at = text.LastIndexOf("[ResponseResult]", StringComparison.OrdinalIgnoreCase);
        var match = AmdResultCode().Match(text, Math.Max(at, 0));
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
    }

    internal static Ending Outcome(Latest latest, int exitCode, int? amdResult, string? installed)
    {
        var amd = latest.Vendor == "amd";
        var updated = VersionNumber.AtLeast(installed, latest.Version);
        var restart = amd ? amdResult == 2 : exitCode is 2 or 14;
        var shutdown = !amd && (exitCode is 17 or 18);
        var failed = amd ? amdResult == 1 || (amdResult is null && exitCode != 0) : exitCode is not (0 or 2 or 14 or 17 or 18);
        if (updated || ((restart || shutdown) && !failed))
            return new(true, $"Driver {latest.Version} installed{(shutdown ? ". Shut down your PC, then turn it on to finish" : restart ? ". Restart your PC to finish" : "")}");
        var maker = Maker(latest.Vendor);
        if (!failed) return new(false, $"{maker}'s installer finished, but the driver is still {installed ?? "the one before"}");
        return new(false, amd
            ? amdResult == 1 ? "AMD's installer stopped. Your driver is unchanged" : $"AMD's installer stopped (code {exitCode}). Your driver is unchanged"
            : exitCode switch
            {
                6 => "The install was closed. Your driver is unchanged",
                8 => "Intel's driver doesn't support this graphics card. Your driver is unchanged",
                11 => "There isn't enough free disk space. Your driver is unchanged",
                13 => "Restart your PC, then update again",
                15 => "An app Intel's installer needs closed is still open. Restart your PC, then update again",
                16 => "Another install is running. Update again when it's done",
                _ => $"Intel's installer stopped (code {exitCode}). Your driver is unchanged",
            });
    }

    internal static string? AmdPackageVersion(string? manifest)
    {
        if (manifest is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(manifest.TrimStart('﻿'));
            return doc.RootElement.TryGetProperty("BuildInfo", out var info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("RadeonSoftwareVersion", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Root(string vendor) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher", "drivers-" + vendor);

    private static async Task RunAsync(Latest latest, string card)
    {
        var vendor = latest.Vendor;
        var maker = Maker(vendor);
        var folder = Path.Combine(Root(vendor), latest.Version);
        var package = Path.Combine(folder, Path.GetFileName(new Uri(latest.Url).LocalPath));
        var unpacked = Path.Combine(folder, "unpacked");
        var log = Path.Combine(folder, "install.log");
        var stepped = false;
        void Step(string phase, string? message, DriverRestore.Outcome? outcome = null)
        {
            stepped = true;
            DriverInstallSignal.Report(new(phase, latest.Version, message, outcome?.Restored, outcome?.NotRestored));
        }
        try
        {
            if (Directory.Exists(Root(vendor)))
                foreach (var old in Directory.EnumerateDirectories(Root(vendor)).Where(old => !string.Equals(old, folder, StringComparison.OrdinalIgnoreCase)).ToList())
                    TryDelete(old);
            Directory.CreateDirectory(folder);
            if (!Genuine(latest, package)) await DownloadAsync(latest, package).ConfigureAwait(false);
            if (!Genuine(latest, package)) throw new InvalidDataException($"The download isn't {maker}'s signed driver.");

            var installer = package;
            var arguments = IntelArguments;
            if (vendor == "amd")
            {
                Report("unpacking", 0, "Unpacking the driver");
                await UnpackAsync(package, unpacked).ConfigureAwait(false);
                installer = Path.Combine(unpacked, "Bin64", "ATISetup.exe");
                if (!AuthenticodeVerifier.HasPublisher(installer, AmdPublisher)) throw new InvalidDataException("AMD's installer isn't signed by AMD.");
                var manifest = Path.Combine(unpacked, "Config", "InstallManifest.json");
                if (AmdPackageVersion(File.Exists(manifest) ? File.ReadAllText(manifest) : null) != latest.Version)
                    throw new InvalidDataException($"AMD's package isn't AMD Software {latest.Version}.");
                DeletionGate.DeleteFile(log);
                arguments = AmdArguments(log, FluidMotionFrames.Runs(card));
            }

            await WaitQuietAsync(HoldingAsync, reason => { Report("waiting", 0, reason); Step("waiting", reason); }, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var screens = DriverRestore.Snapshot();
            var before = Installed(vendor, card);

            Report("installing", 0, "Installing the driver. Your screens go black for a moment");
            Step("installing", "Installing the driver. Your screens go black for a moment");
            AppLog.Info($"Driver: installing {maker} {latest.Version} over {before ?? "no driver"} for {card}: {Path.GetFileName(installer)} {arguments}");
            int exit;
            try
            {
                using var install = Process.Start(new ProcessStartInfo(installer, arguments) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(installer)! })
                    ?? throw new InvalidOperationException($"{maker}'s installer did not start.");
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
            if (vendor == "amd") await WaitAmdInstallAsync(unpacked, log, card, before, latest.Version).ConfigureAwait(false);
            var result = vendor == "amd" ? AmdResult(ReadLog(log)) : null;
            var installed = Installed(vendor, card);
            for (var waited = 0; waited < 15 && !Same(installed, latest.Version); waited++)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                installed = Installed(vendor, card);
            }
            var end = Outcome(latest, exit, result, installed);
            AppLog.Info($"Driver: {maker}'s installer exited {exit}{(result is { } code ? $", result {code}" : "")}; driver now {installed ?? "unknown"}: {end.Message}.");
            await MainWindow.ReloadAfterDriverInstallAsync().ConfigureAwait(false);
            if (!end.Ok)
            {
                Report("failed", 0, end.Message);
                Step("failed", end.Message);
                return;
            }
            Drivers.Clear();
            Report("done", 100, end.Message);
            Step("restoring", "Putting your settings back");
            Step("done", end.Message, await DriverRestore.AfterInstallAsync(vendor, screens, interruptsChained: false).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Driver: {maker} update failed: {ex.Message}");
            var message = ex is InvalidDataException ? ex.Message : "The update stopped. Your driver is unchanged";
            Report("failed", 0, message);
            if (stepped) Step("failed", message);
        }
        finally
        {
            TryDelete(unpacked);
        }
    }

    private static bool Same(string? installed, string latest) => VersionNumber.Same(installed, latest);

    internal enum AmdWait { Waiting, Ended, Stopped, TimedOut }

    internal static readonly TimeSpan AmdSettle = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan AmdGiveUp = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan AmdLimit = TimeSpan.FromHours(1);

    internal static AmdWait AmdInstallState(bool running, bool finished, TimeSpan quiet, TimeSpan elapsed) =>
        elapsed >= AmdLimit ? AmdWait.TimedOut
        : running ? AmdWait.Waiting
        : finished ? quiet >= AmdSettle ? AmdWait.Ended : AmdWait.Waiting
        : quiet >= AmdGiveUp ? AmdWait.Stopped
        : AmdWait.Waiting;

    private static readonly string[] AmdInstallers = ["ATISetup", "AMDSoftwareInstaller", "AMDSplashScreen", "InstallManagerApp", "AMDCleanupUtility", "drvinst"];

    private static async Task WaitAmdInstallAsync(string unpacked, string log, string card, string? before, string version)
    {
        var clock = Stopwatch.StartNew();
        var lastRan = TimeSpan.Zero;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var running = AmdInstallerRunning(unpacked);
            if (running is not null)
            {
                lastRan = clock.Elapsed;
                if (seen.Add(running)) AppLog.Info($"Driver: AMD's install runs {running}.exe ({clock.Elapsed.TotalSeconds:0} s after the installer returned).");
            }
            var installed = Installed("amd", card);
            var finished = AmdResult(ReadLog(log)) is not null || (!VersionNumber.Same(before, installed) && Same(installed, version));
            var state = AmdInstallState(running is not null, finished, clock.Elapsed - lastRan, clock.Elapsed);
            if (state != AmdWait.Waiting)
            {
                AppLog.Info($"Driver: AMD's install {state.ToString().ToLowerInvariant()} {clock.Elapsed.TotalSeconds:0} s after the installer returned; driver {installed ?? "unknown"}.");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
    }

    private static string? AmdInstallerRunning(string unpacked)
    {
        var root = Adapters.ProcessHelper.RootPrefix(unpacked);
        foreach (var row in Helpers.ProcessSnapshot.Read())
        {
            if (row.Pid <= 4 || string.IsNullOrEmpty(row.Name)) continue;
            var name = Helpers.ProcessSnapshot.StripExe(row.Name);
            if (AmdInstallers.Contains(name, StringComparer.OrdinalIgnoreCase)) return name;
            if (root is not null && Adapters.ProcessHelper.QueryFullProcessImageName(row.Pid) is { } path
                && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return null;
    }

    private static string? ReadLog(string log)
    {
        try
        {
            if (!File.Exists(log)) return null;
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool Genuine(Latest latest, string path)
    {
        if (!File.Exists(path) || !AuthenticodeVerifier.HasPublisher(path, latest.Vendor == "amd" ? AmdPublisher : IntelPublisher)) return false;
        if (latest.Sha512 is null) return true;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA512.HashData(stream)).Equals(latest.Sha512, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadAsync(Latest latest, string path)
    {
        var maker = Maker(latest.Vendor);
        Report("downloading", 0, $"Downloading from {maker}");
        var partial = path + ".part";
        try
        {
            using (var request = Request(latest.Vendor, latest.Url))
            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.Host != DownloadHost(latest.Vendor)) throw new InvalidDataException($"The download didn't come from {maker}.");
                var total = response.Content.Headers.ContentLength ?? latest.Size;
                if (total > MaxBytes) throw new InvalidDataException($"{maker}'s download is larger than a driver.");
                await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                var buffer = new byte[1 << 20];
                long done = 0;
                var lastPercent = -1;
                int read;
                while ((read = await StallGuard.ReadAsync(source, buffer).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > MaxBytes) throw new InvalidDataException($"{maker}'s download is larger than a driver.");
                    await target.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    var percent = total > 0 ? (int)Math.Min(100, done * 100 / total) : 0;
                    if (percent != lastPercent) { lastPercent = percent; Report("downloading", percent, $"Downloading from {maker}"); }
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

    internal static string DownloadHost(string vendor) => vendor == "amd" ? "drivers.amd.com" : "downloadmirror.intel.com";

    private static async Task UnpackAsync(string package, string unpacked)
    {
        TryDelete(unpacked);
        Directory.CreateDirectory(unpacked);
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in (string[])["-xf", package, "-C", unpacked]) info.ArgumentList.Add(arg);
        using var unpack = Process.Start(info) ?? throw new InvalidOperationException("Windows' tar did not start.");
        await unpack.WaitForExitAsync().ConfigureAwait(false);
        if (unpack.ExitCode != 0 || !File.Exists(Path.Combine(unpacked, "Setup.exe"))) throw new InvalidDataException("The driver package didn't unpack.");
    }

    private static void SweepUnpacked(string vendor)
    {
        lock (Gate) if (_running is { IsCompleted: false }) return;
        try
        {
            if (!Directory.Exists(Root(vendor))) return;
            foreach (var version in Directory.EnumerateDirectories(Root(vendor))) TryDelete(Path.Combine(version, "unpacked"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Driver: sweeping unpacked drivers: " + ex.Message); }
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) DeletionGate.DeleteDirectory(folder, true); }
        catch (Exception ex) { AppLog.Debug("Driver: could not remove " + folder + ": " + ex.Message); }
    }
}
