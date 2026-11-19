using System.Diagnostics;
using ExoLauncher.Helpers;
using Microsoft.Win32;

namespace ExoLauncher.Services;

/// <summary>
/// An app Exo does the job of. <paramref name="Packages"/> are Microsoft Store package names, <paramref name="Uninstall"/>
/// matches its Apps &amp; features entries by name, <paramref name="Markers"/> are files that mean it is still there without
/// either (a copy whose entry is gone). <paramref name="Paths"/>, <paramref name="Keys"/> and <paramref name="Values"/>
/// (<c>key|name</c>) are what it leaves behind; <paramref name="Set"/> (<c>key|name=dword</c>) is written once it is gone.
/// <paramref name="Names"/> are its own folder and key names where programs keep theirs, and <paramref name="AppIds"/> the
/// ids Windows knows it by (notifications, recent apps): the deep clean's leads (<see cref="LeftoverScan"/>).
/// <paramref name="Undo"/> are keys its <paramref name="Set"/> wrote that go again if it is installed again.
/// Paths start with a place (<c>{LocalAppData}</c>); keys with HKCU or HKLM. A Set value is a DWORD when it is a number,
/// else text ({System} is Windows' System32).
/// </summary>
internal sealed record ReplacedApp(
    string Id,
    string Name,
    string Detail,
    string[] Packages,
    string[] Uninstall,
    string[] Markers,
    string[] Paths,
    string[] Keys,
    string[] Values,
    string[] Set,
    string[]? Names = null,
    string[]? AppIds = null,
    string[]? Undo = null);

/// <summary><paramref name="Found"/>: what is there now. <paramref name="Cleared"/>: what this scan or removal just cleared.</summary>
internal sealed record ReplacedAppState(string Id, string Name, string Detail, bool Installed, int Leftovers, long Bytes, bool NeedsAdmin,
    LeftoverSummary Found, LeftoverSummary? Cleared);

internal sealed record ReplacedAppRemoval(bool Ok, string? Message, int Removed, int Left, LeftoverSummary? Cleared = null);

/// <summary>The Microsoft Store side, apart so tests run without touching this PC's apps.</summary>
internal interface IAppPackages
{
    IReadOnlyList<string> Find(string name);
    Task<bool> RemoveAsync(string fullName, CancellationToken ct);
}

internal sealed class WindowsAppPackages : IAppPackages
{
    // This user's packages, listed once per scan (Windows lists them all for every name asked; a scan asks six) and again
    // after a removal.
    private List<(string Name, string FullName)>? _all;

    public IReadOnlyList<string> Find(string name)
    {
        try
        {
            _all ??= [.. new Windows.Management.Deployment.PackageManager().FindPackagesForUser("")
                .Where(package => !package.IsFramework).Select(package => (package.Id.Name, package.Id.FullName))];
            return _all.Where(package => package.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(package => package.FullName).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Debug("Replaced apps: package check failed: " + ex.GetType().Name);
            return [];
        }
    }

    public async Task<bool> RemoveAsync(string fullName, CancellationToken ct)
    {
        if (HostMode.IsDisposable) return false;
        _all = null;
        try
        {
            // Removed for this user with its data (LocalAppData\Packages\<family>): no admin rights, Windows closes it first.
            var result = await new Windows.Management.Deployment.PackageManager().RemovePackageAsync(fullName).AsTask(ct).ConfigureAwait(false);
            if (result.ExtendedErrorCode is null) return true;
            AppLog.Warn($"Replaced apps: {fullName} was not removed: 0x{result.ExtendedErrorCode.HResult:X8}");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"Replaced apps: {fullName} was not removed: {ex.GetType().Name}");
            return false;
        }
    }
}

/// <summary>
/// Deep removal of the apps Exo replaces, as Revo Uninstaller does it: the app's own uninstaller (or its Store package),
/// then every folder, file, registry key and value it leaves, and once it is gone, the deep clean (<see cref="LeftoverScan"/>,
/// what Revo's advanced scan and BCU find). Only what is tied to the app, never a search of the disk by a word, so nothing
/// that belongs to something else goes with it. The Xbox app (Xbox games) and the NVIDIA driver
/// stay: Exo needs them. A leftover of an app that is gone is junk; <see cref="CleanLeftovers"/> clears the ones in this
/// user's folders on its own, and a removal asks Windows once for what lies in Program Files or HKLM, and to take a Store
/// app out of Windows' image for every user, so a feature update can't bring it back (Win11Debloat and WinUtil do the same).
/// What the user removed is kept (replaced.json): what a driver install brings back goes again after each one (<see cref="AgainAsync"/>).
/// </summary>
internal sealed partial class ReplacedApps
{
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Programs = LeftoverScan.Programs;

    internal static readonly ReplacedApp[] Known =
    [
        new("discord", "Discord", "Exo has its own Discord: servers, DMs and calls",
            [], ["Discord", "Discord PTB", "Discord Canary"],
            [@"{LocalAppData}\Discord\Update.exe", @"{LocalAppData}\DiscordPTB\Update.exe", @"{LocalAppData}\DiscordCanary\Update.exe"],
            [@"{LocalAppData}\Discord", @"{LocalAppData}\DiscordPTB", @"{LocalAppData}\DiscordCanary",
             @"{AppData}\discord", @"{AppData}\discordptb", @"{AppData}\discordcanary",
             Programs + @"\Discord Inc", @"{Desktop}\Discord.lnk", @"{Desktop}\Discord PTB.lnk", @"{Desktop}\Discord Canary.lnk"],
            [@"HKCU\Software\Discord", @"HKCU\Software\Classes\discord", @"HKCU\" + Uninstall + @"\Discord",
             @"HKCU\" + Uninstall + @"\DiscordPTB", @"HKCU\" + Uninstall + @"\DiscordCanary"],
            [@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run|Discord", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run|Discord"],
            [],
            ["Discord", "DiscordPTB", "DiscordCanary"],
            ["com.squirrel.Discord.Discord", "com.squirrel.DiscordPTB.DiscordPTB", "com.squirrel.DiscordCanary.DiscordCanary"]),
        new("snipping", "Snipping Tool", "Exo snips with Print Screen and edits in Captures",
            ["Microsoft.ScreenSketch"], [], [], [], [], [],
            // Print Screen no longer opens Windows' snip, so Exo's takes it.
            [@"HKCU\Control Panel\Keyboard|PrintScreenKeyForSnippingEnabled=0"]),
        new("photos", "Photos", "Exo shows and trims your captures",
            ["Microsoft.Windows.Photos", "Microsoft.PhotosLegacy"], [], [], [], [], [], []),
        new("gamebar", "Xbox Game Bar", "Exo records clips and replays",
            ["Microsoft.XboxGamingOverlay", "Microsoft.XboxGameOverlay"], [], [], [], [], [],
            // Game Bar's recording and its button off, so Windows never asks for the removed app (ms-gamingoverlay); its
            // ms-gamebar links open nothing instead of asking which app should (Win11Debloat's fix), undone if it comes back.
            [@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR|AppCaptureEnabled=0", @"HKCU\System\GameConfigStore|GameDVR_Enabled=0",
             @"HKCU\Software\Microsoft\GameBar|UseNexusForGameBarEnabled=0",
             @"HKCU\Software\Classes\ms-gamebar|=URL:ms-gamebar", @"HKCU\Software\Classes\ms-gamebar|URL Protocol=", @"HKCU\Software\Classes\ms-gamebar|NoOpenWith=",
             @"HKCU\Software\Classes\ms-gamebar\shell\open\command|={System}\systray.exe",
             @"HKCU\Software\Classes\ms-gamebarservices|=URL:ms-gamebarservices", @"HKCU\Software\Classes\ms-gamebarservices|URL Protocol=",
             @"HKCU\Software\Classes\ms-gamebarservices|NoOpenWith=", @"HKCU\Software\Classes\ms-gamebarservices\shell\open\command|={System}\systray.exe",
             @"HKCU\Software\Classes\ms-gamingoverlay|=URL:ms-gamingoverlay", @"HKCU\Software\Classes\ms-gamingoverlay|URL Protocol=",
             @"HKCU\Software\Classes\ms-gamingoverlay|NoOpenWith=", @"HKCU\Software\Classes\ms-gamingoverlay\shell\open\command|={System}\systray.exe"],
            Undo: [@"HKCU\Software\Classes\ms-gamebar", @"HKCU\Software\Classes\ms-gamebarservices", @"HKCU\Software\Classes\ms-gamingoverlay"]),
        new("clipchamp", "Clipchamp", "Exo trims and shares your clips",
            ["Clipchamp.Clipchamp"], [], [], [], [], [], []),
        // The Control Panel is the Store app NVIDIA's setup provisions with every driver (Display.Driver\NVCPL); the app's
        // driver downloads (Downloader) and what the installer leaves (InstallerLeftovers) go too. NVIDIA's own per-user
        // switches turn off the "NVIDIA Settings" tray icon (NvTray StartOnLogin, read by the driver's container before it
        // starts the icon; NVIDIA's vGPU guide) and the desktop menu's entry (NvCplApi\Policies ContextUIPolicy, the Control
        // Panel's Desktop menu). The driver, its container, NvAPI, the profile store (Drs), PhysX and HD Audio stay.
        new("nvidia", "NVIDIA App and Control Panel", "Exo updates the driver, sets its profile and your screens; the driver stays",
            ["NVIDIACorp.NVIDIAControlPanel"], ["NVIDIA App", "NVIDIA GeForce Experience"],
            [@"{ProgramFiles}\NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe", @"{ProgramFiles}\NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe"],
            [@"{ProgramFiles}\NVIDIA Corporation\NVIDIA App", @"{ProgramData}\NVIDIA Corporation\NVIDIA App", @"{LocalAppData}\NVIDIA Corporation\NVIDIA App",
             @"{ProgramFiles}\NVIDIA Corporation\NVIDIA GeForce Experience", @"{ProgramData}\NVIDIA Corporation\GeForce Experience",
             @"{LocalAppData}\NVIDIA Corporation\GeForce Experience", @"{CommonPrograms}\NVIDIA Corporation\NVIDIA App.lnk",
             @"{CommonPrograms}\NVIDIA Corporation\GeForce Experience.lnk", @"{CommonDesktop}\NVIDIA App.lnk",
             @"{ProgramData}\NVIDIA Corporation\Downloader", @"{LocalAppData}\Packages\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj"],
            [@"HKCU\Software\NVIDIA Corporation\NVIDIA App", @"HKLM\SOFTWARE\NVIDIA Corporation\NVIDIA App", @"HKLM\SOFTWARE\NVIDIA Corporation\Global\GFExperience"],
            [],
            [@"HKCU\Software\NVIDIA Corporation\NvTray|StartOnLogin=0", @"HKCU\Software\NVIDIA Corporation\Global\NvCplApi\Policies|ContextUIPolicy=0"]),
    ];

    /// <summary>
    /// What a driver install brings back: NVIDIA's setup provisions the Control Panel again and its container shows the tray
    /// icon. Removed in Clean up once, it goes again after every driver install (<see cref="AgainAsync"/>).
    /// </summary>
    internal static readonly string[] DriverTied = ["nvidia"];

    private readonly LeftoverScan _scan;
    private readonly IAppPackages _packages;
    private readonly Func<string, CancellationToken, Task<string?>> _runUninstaller;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<bool>> _removeAsAdmin;
    private readonly string _hkcuBase;
    private readonly string _hklmBase;
    private readonly bool _isTest;
    private readonly string? _record;
    private IReadOnlyList<string>? _keep;

    /// <param name="places">Folder per place name. <paramref name="hkcuBase"/> and <paramref name="hklmBase"/> are prefixes
    /// under HKCU for tests (empty: the real hives). <paramref name="record"/>: where what the user removed is kept (null: nowhere).</param>
    internal ReplacedApps(IReadOnlyDictionary<string, string> places, IAppPackages packages,
        Func<string, CancellationToken, Task<string?>> runUninstaller, Func<IReadOnlyList<string>, CancellationToken, Task<bool>> removeAsAdmin,
        string hkcuBase = "", string hklmBase = "", string? record = null)
    {
        _scan = new LeftoverScan(places, hkcuBase, hklmBase);
        _packages = packages;
        _runUninstaller = runUninstaller;
        _removeAsAdmin = removeAsAdmin;
        _hkcuBase = hkcuBase;
        _hklmBase = hklmBase;
        _isTest = hkcuBase.Length > 0;
        _record = record;
    }

    internal static ReplacedApps ForThisPc() => new(LeftoverScan.ThisPc(), new WindowsAppPackages(),
        (command, ct) => UninstallRun.RunAsync(command, TimeSpan.FromMinutes(10), ct), RemoveAsAdminAsync,
        record: Path.Combine(PathHelper.AppDataDir, "replaced.json"));

    /// <param name="cleared">What a clean just cleared, per app id, said with each.</param>
    internal IReadOnlyList<ReplacedAppState> Scan(IReadOnlyDictionary<string, LeftoverSummary>? cleared = null) => Known.Select(app =>
    {
        var installed = Installed(app);
        if (installed) Reinstalled(app);
        var left = Leftovers(app, installed);
        var found = LeftoverScan.Summarize(left, LeftoverScan.Sizes(left));
        return new ReplacedAppState(app.Id, app.Name, app.Detail, installed, left.Count, found.Bytes, left.Any(item => item.Machine), found,
            cleared?.GetValueOrDefault(app.Id));
    }).ToList();

    /// <summary>Clears what apps that are gone left in this user's folders and keys; what it cleared, per app.</summary>
    internal Dictionary<string, LeftoverSummary> CleanLeftovers()
    {
        if (HostMode.IsDisposable) return new();
        var cleared = new Dictionary<string, LeftoverSummary>();
        foreach (var app in Known.Where(app => !Installed(app)))
        {
            var mine = Leftovers(app, installed: false).Where(item => !item.Machine && !item.Likely).ToList();
            if (mine.Count == 0) continue;
            var sizes = LeftoverScan.Sizes(mine);
            _scan.Backup(app.Name, mine);
            var gone = mine.Where(_scan.Delete).ToList();
            if (gone.Count > 0) cleared[app.Id] = LeftoverScan.Summarize(gone, sizes);
        }
        _scan.Forget();
        if (cleared.Count > 0) AppLog.Info($"Replaced apps: cleared {cleared.Values.Sum(summary => summary.Count)} leftovers of apps that are gone.");
        return cleared;
    }

    internal async Task<ReplacedAppRemoval> RemoveAsync(string id, CancellationToken ct)
    {
        if (HostMode.IsDisposable) return new(false, HostMode.Refusal, 0, 0);
        var app = Known.FirstOrDefault(known => known.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (app is null) return new(false, "Exo doesn't know that app.", 0, 0);
        // The user's choice, kept: what a driver brings back goes again after each install.
        if (_record is not null) Remember(app.Id, DriverTied.Contains(app.Id) && !_isTest ? NvidiaDriverService.Installed() : null);
        var problem = (string?)null;
        Close(app);
        foreach (var package in app.Packages.SelectMany(_packages.Find).ToList())
            if (!await _packages.RemoveAsync(package, ct).ConfigureAwait(false)) problem ??= $"Windows didn't remove {app.Name}.";
        foreach (var command in UninstallCommands(app))
        {
            var failed = await _runUninstaller(command, ct).ConfigureAwait(false);
            if (failed is not null) problem ??= failed;
        }
        Close(app);
        if (Installed(app))
            return new(false, problem ?? $"{app.Name} is still installed.", 0, Leftovers(app, installed: true).Count);

        // Gone: the deep clean, what it left and what Windows keeps about it.
        _scan.Forget();
        var list = Leftovers(app, installed: false, deep: true).Where(item => !item.Likely).ToList();
        var sizes = LeftoverScan.Sizes(list);
        _scan.Backup(app.Name, list);
        var removed = new List<Leftover>();
        var machine = new List<Leftover>();
        foreach (var item in list)
        {
            if (item.Machine) machine.Add(item);
            else if (_scan.Delete(item)) removed.Add(item);
        }
        var commands = machine.Select(_scan.AdminCommand).OfType<string>().ToList();
        commands.AddRange(Deprovision(app));
        var allowed = commands.Count > 0 && await _removeAsAdmin(commands, ct).ConfigureAwait(false);
        if (allowed) removed.AddRange(machine.Where(item => !_scan.Exists(item)));
        ApplySet(app);
        _scan.Forget();
        var remaining = Leftovers(app, installed: false).Where(item => !item.Likely).ToList();
        var left = remaining.Count;
        var why = _scan.Why(remaining);
        var cleared = LeftoverScan.Summarize(removed, sizes);
        AppLog.Info($"Replaced apps: {app.Name} removed with {removed.Count} leftovers ({cleared.Folders} folders, {cleared.Files} files, " +
            $"{cleared.Registry} registry entries, {cleared.Shortcuts} shortcuts); {left} left{(why is null ? "" : $" ({why})")}" +
            $"{(app.Packages.Length > 0 ? allowed ? "; taken out of Windows' image" : "; still in Windows' image" : "")}.");
        return new(left == 0, left == 0 ? null : why is null ? $"{left} of {app.Name}'s leftovers need Windows' permission." : $"{left} of {app.Name}'s leftovers stayed: {why}.",
            removed.Count, left, cleared);
    }

    // ---- again, after a driver install ----

    private sealed record Record(List<string> Removed, string? Driver);

    private static readonly Lock RecordGate = new();
    private static readonly SemaphoreSlim AgainGate = new(1, 1);
    private static int _following;

    private Record Load()
    {
        lock (RecordGate)
        {
            try
            {
                if (_record is not null && File.Exists(_record) && new FileInfo(_record).Length < 16 * 1024 &&
                    System.Text.Json.JsonSerializer.Deserialize<Record>(File.ReadAllText(_record)) is { } read)
                    return read with { Removed = read.Removed ?? [] };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                AppLog.Debug("Replaced apps: the record wasn't readable: " + ex.GetType().Name);
            }
            return new([], null);
        }
    }

    private void Save(Record record)
    {
        if (_record is null) return;
        lock (RecordGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_record)!);
                File.WriteAllText(_record + ".tmp", System.Text.Json.JsonSerializer.Serialize(record));
                File.Move(_record + ".tmp", _record, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Debug("Replaced apps: the record wasn't saved: " + ex.GetType().Name);
            }
        }
    }

    /// <summary>The user removed it in Clean up; <paramref name="driver"/>, the NVIDIA driver now, so a start doesn't do it again at once.</summary>
    private void Remember(string id, string? driver)
    {
        var record = Load();
        if (!record.Removed.Contains(id)) record.Removed.Add(id);
        Save(record with { Driver = driver ?? record.Driver });
    }

    /// <summary>Whether the NVIDIA driver isn't the one last seen: an install Exo didn't run (none seen yet counts).</summary>
    internal static bool DriverChanged(string? seen, string? now) => now is not null && !string.Equals(seen, now, StringComparison.OrdinalIgnoreCase);

    /// <summary>Taking a Store app out of Windows' image, for every user, so a feature update, a new account or a driver's setup doesn't bring it back.</summary>
    private static IEnumerable<string> Deprovision(ReplacedApp app) => app.Packages.Select(package =>
        $"powershell.exe -NoProfile -NonInteractive -Command \"Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq '{package}' | Remove-AppxProvisionedPackage -Online -AllUsers | Out-Null\"");

    /// <summary>What the user removed in Clean up that a driver brings back.</summary>
    private List<ReplacedApp> Removed()
    {
        var record = Load();
        return [.. Known.Where(app => DriverTied.Contains(app.Id) && record.Removed.Contains(app.Id))];
    }

    /// <summary>
    /// Exo's own NVIDIA install: the administrator's part of <see cref="AgainAsync"/>, for the installer's own elevated step
    /// (Windows asks once): the Control Panel out of Windows' image (setup provisions it again; started apart, so a slow
    /// image can't hold the install's ending) and what only an administrator may remove of what the user removed. Empty
    /// when the user removed nothing a driver brings back.
    /// </summary>
    internal List<string> DriverStepCommands()
    {
        var commands = new List<string>();
        try
        {
            foreach (var app in Removed())
            {
                commands.AddRange(Deprovision(app).Select(line => "start \"\" /b " + line));
                if (!Installed(app))
                    commands.AddRange(Leftovers(app, installed: false).Where(item => item.Machine && !item.Likely).Select(_scan.AdminCommand).OfType<string>());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warn("Replaced apps: the driver step's removals weren't read: " + ex.Message);
        }
        return commands;
    }

    /// <summary>
    /// After a driver install, what the user removed in Clean up that a driver brings back goes again, with no prompt of its
    /// own: the Store app for this user, the settings its removal wrote (NVIDIA's tray icon and desktop menu off) and what it
    /// left in this user's folders; what only an administrator may remove too when <paramref name="admin"/> (Windows won't
    /// ask; Exo's own install did it in its elevated step). An app whose uninstaller asks stays, said. <paramref name="driver"/>,
    /// the NVIDIA driver now, is kept so a start doesn't do it again. What went (a few words each) and what didn't (a
    /// sentence each that says what to do).
    /// </summary>
    internal async Task<(List<string> Done, List<string> Missed)> AgainAsync(string? driver, bool admin, string why, CancellationToken ct)
    {
        if (HostMode.IsDisposable) return (new List<string>(), new List<string>());
        var done = new List<string>();
        var missed = new List<string>();
        await AgainGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var apps = Removed();
            foreach (var app in apps)
            {
                var packages = 0;
                foreach (var package in app.Packages.SelectMany(_packages.Find).ToList())
                    if (await _packages.RemoveAsync(package, ct).ConfigureAwait(false)) packages++;
                var settings = ApplySet(app);
                if (Installed(app))
                {
                    AppLog.Info($"Replaced apps: {app.Name} is installed again {why}; its uninstaller asks, so Clean up removes it.");
                    missed.Add($"{app.Name} is installed again. Remove it in Clean up.");
                    continue;
                }
                _scan.Forget();
                var list = Leftovers(app, installed: false).Where(item => !item.Likely).ToList();
                _scan.Backup(app.Name, list);
                var cleared = list.Where(item => !item.Machine).Where(_scan.Delete).ToList();
                var machine = list.Where(item => item.Machine).ToList();
                List<string> commands = admin ? [.. machine.Select(_scan.AdminCommand).OfType<string>(), .. Deprovision(app)] : [];
                if (commands.Count > 0 && await _removeAsAdmin(commands, ct).ConfigureAwait(false)) cleared.AddRange(machine.Where(item => !_scan.Exists(item)));
                var left = list.Count - cleared.Count;
                AppLog.Info($"Replaced apps: {app.Name} removed again {why}: {packages} package(s), {settings} setting(s), {cleared.Count} leftover(s); {left} left.");
                if (packages + settings + cleared.Count > 0) done.Add($"{app.Name}, removed again");
                if (left > 0) missed.Add($"{left} of {app.Name}'s leftovers need Windows' permission. Remove them in Clean up.");
            }
            if (apps.Count > 0 && driver is not null) Save(Load() with { Driver = driver });
        }
        finally { AgainGate.Release(); }
        return (done, missed);
    }

    /// <summary>
    /// A driver installed outside Exo (noticed on start, or when NVIDIA's container restarted): when the NVIDIA driver isn't
    /// the one last seen and the user removed what a driver brings back, <see cref="AgainAsync"/> at a quiet moment (no game,
    /// no call, Exo not in front), with what only an administrator may remove when Windows won't ask.
    /// </summary>
    internal static async Task AfterDriverChangeAsync(string why, CancellationToken ct)
    {
        if (HostMode.IsDisposable) return;
        if (Interlocked.Exchange(ref _following, 1) == 1) return;
        try
        {
            var apps = ForThisPc();
            if (apps.Removed().Count == 0 || !DriverChanged(apps.Load().Driver, NvidiaDriverService.Installed())) return;
            await GpuDriverUpdate.WaitQuietAsync(QuietAsync, _ => { }, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            // Exo's own install does this in its restoring step.
            var driver = NvidiaDriverService.Installed();
            if (DriverInstallSignal.Installing || !DriverChanged(apps.Load().Driver, driver)) return;
            await apps.AgainAsync(driver, admin: !ElevatedCommands.WindowsAsks(), why, ct).ConfigureAwait(false);
            NvidiaTrayIcon.Soon();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("Replaced apps: removing again after a driver change failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _following, 0); }
    }

    /// <summary>What a follow-up by itself waits for, or null: nothing the user is doing.</summary>
    private static Task<string?> QuietAsync() => Task.FromResult<string?>(
        App.Services.Launcher.AnyGameRunning || GameRunningSignal.Running ? "a game"
        : DiscordClient.DiscordSession.Current?.InCall == true ? "a call"
        : App.MainAppWindow?.WindowIsActive == true ? "Exo in front"
        : null);

    // ---- what NVIDIA's installer leaves ----

    /// <summary>
    /// What NVIDIA's installer left in its own folders (Installer2, in Program Files and in ProgramData from older
    /// installers): a component's folder its records (HKLM\SOFTWARE\NVIDIA Corporation\Installer2) no longer name, such as
    /// the CoreTemp copy of itself each install makes (<see cref="UnusedInstallerFolder"/>). One they name is the installed
    /// driver's or PhysX's (Apps &amp; features uninstalls through it) and stays, and nothing goes when they can't be read.
    /// </summary>
    private IEnumerable<Leftover> InstallerLeftovers()
    {
        var records = InstallerRecords();
        if (records is null) return [];
        var keep = TrailFor(Known.First(app => app.Id == "nvidia")).Keep.Select(path => path.TrimEnd('\\') + '\\').ToList();
        var list = new List<Leftover>();
        foreach (var root in new[] { @"{ProgramFiles}\NVIDIA Corporation\Installer2", @"{ProgramData}\NVIDIA Corporation\Installer2" })
        {
            try
            {
                var folder = _scan.Expand(root);
                if (!Directory.Exists(folder)) continue;
                foreach (var dir in new DirectoryInfo(folder).EnumerateDirectories())
                    if (UnusedInstallerFolder(dir.FullName, records, dir.LastWriteTimeUtc, DateTime.UtcNow) &&
                        !keep.Any(other => other.StartsWith(dir.FullName + '\\', StringComparison.OrdinalIgnoreCase) || (dir.FullName + '\\').StartsWith(other, StringComparison.OrdinalIgnoreCase)))
                        list.Add(new Leftover(dir.FullName, false, null, true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return list;
    }

    /// <summary>
    /// Every key name, value name and text under NVIDIA's installer records (the paths of the folders it uses: in values, in
    /// lists of them, and as a value's name under Drivers), or null when they can't be read.
    /// </summary>
    private List<string>? InstallerRecords()
    {
        try
        {
            using var root = _scan.Open(@"HKLM\SOFTWARE\NVIDIA Corporation\Installer2", writable: false);
            if (root is null) return null;
            var texts = new List<string>();
            void Walk(RegistryKey key, int depth)
            {
                foreach (var name in key.GetValueNames())
                {
                    texts.Add(name);
                    if (key.GetValue(name) is string text) texts.Add(text);
                    else if (key.GetValue(name) is string[] list) texts.AddRange(list);
                }
                if (depth > 3) return;
                foreach (var name in key.GetSubKeyNames())
                {
                    texts.Add(name);
                    using var sub = key.OpenSubKey(name);
                    if (sub is not null) Walk(sub, depth + 1);
                }
            }
            Walk(root, 0);
            return texts;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    /// <summary>
    /// A folder of NVIDIA's installer that may go: a component's ("CoreTemp.{GUID}", "Display.Driver.{GUID}") that none of
    /// its records names, untouched for an hour (an install may be running).
    /// </summary>
    internal static bool UnusedInstallerFolder(string folder, IReadOnlyCollection<string> records, DateTime written, DateTime now)
    {
        var path = folder.TrimEnd('\\');
        return InstallerComponent().IsMatch(Path.GetFileName(path)) && now - written > TimeSpan.FromHours(1) &&
            !records.Any(text => text.Contains(path + '\\', StringComparison.OrdinalIgnoreCase) || text.TrimEnd('\\').Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9._-]+\.\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial System.Text.RegularExpressions.Regex InstallerComponent();

    // Squirrel (Discord) can't delete its own Update.exe: uninstalled, it leaves it with a .dead file beside it, to go at
    // the next start. A marker beside one is no install (3.15.4 read it as one, and stopped before the clean).
    private bool Installed(ReplacedApp app) =>
        app.Packages.Any(name => _packages.Find(name).Count > 0) || UninstallCommands(app).Count > 0 ||
        app.Markers.Select(_scan.Expand).Any(marker => File.Exists(marker) && !File.Exists(Path.Combine(Path.GetDirectoryName(marker)!, ".dead")));

    /// <summary>An app removed and installed again: what its removal wrote that would stop it working goes (Game Bar's links).</summary>
    private void Reinstalled(ReplacedApp app)
    {
        foreach (var key in app.Undo ?? [])
        {
            try
            {
                using var command = _scan.Open(key + @"\shell\open\command", writable: false);
                if (command?.GetValue(null) is string text && text.EndsWith("systray.exe", StringComparison.OrdinalIgnoreCase))
                    _scan.Delete(new Leftover(key, true, null, false));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The app's own uninstallers from its Apps &amp; features entries, the quiet one where it has one.</summary>
    private List<string> UninstallCommands(ReplacedApp app)
    {
        var commands = new List<string>();
        if (app.Uninstall.Length == 0) return commands;
        foreach (var (hive, prefix) in new[] { (Registry.CurrentUser, _hkcuBase), (_isTest ? Registry.CurrentUser : Registry.LocalMachine, _hklmBase) })
        foreach (var path in new[] { Uninstall, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            try
            {
                using var key = hive.OpenSubKey(prefix.Length == 0 ? path : prefix + '\\' + path);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var entry = key.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string display || !app.Uninstall.Any(match => NameMatches(display, match))) continue;
                    // An entry whose uninstaller is gone is a leftover itself, not an installed app.
                    if ((entry.GetValue("QuietUninstallString") ?? entry.GetValue("UninstallString")) is string command && !string.IsNullOrWhiteSpace(command) &&
                        File.Exists(Adapters.OfficialInstalledLibraries.SplitCommand(command).FileName))
                        commands.Add(command);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        }
        return commands;
    }

    /// <summary>"NVIDIA App" matches "NVIDIA App 11.0.4" but not "NVIDIA Apparatus".</summary>
    internal static bool NameMatches(string display, string name) =>
        display.Equals(name, StringComparison.OrdinalIgnoreCase) ||
        display.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase) && char.IsDigit(display[name.Length + 1]);

    /// <summary>A shortcut's target (<see cref="LeftoverScan.ShortcutTarget"/>).</summary>
    internal static string? ShortcutTarget(string link) => LeftoverScan.ShortcutTarget(link);

    /// <summary>Who the app is, for the deep clean: its listed folders, names, executables, ids and entries.</summary>
    private Trail TrailFor(ReplacedApp app) => new(app.Name)
    {
        Homes = [.. app.Paths.Where(path => !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))],
        Names = app.Names ?? [],
        NamesSure = true,
        Exes = [.. (app.Names ?? []).Select(name => name + ".exe")],
        Ids = [.. (app.AppIds ?? []).Concat(app.Packages.Select(package => package + '_'))],
        DisplayNames = app.Uninstall,
        // What is still installed stays, and so does anything holding it (BCU's rule): the NVIDIA tree holds the driver's.
        Keep = _isTest ? [] : _keep ??= [.. InstalledPrograms.List().Where(program => !program.Store && program.Replaces != app.Id && program.Location is not null).Select(program => program.Location!),
            .. Adapters.RecompInstallations.DataFolders()],
    };

    /// <param name="installed">Whether the app is still there: only a gone app's traces are its leftovers.</param>
    /// <param name="deep">Also what is slow to read (COM, services, tasks): after a removal.</param>
    private List<Leftover> Leftovers(ReplacedApp app, bool installed, bool deep = false)
    {
        var list = new List<Leftover>();
        foreach (var path in app.Paths)
        {
            var item = new Leftover(_scan.Expand(path), false, null, MachinePlace(path));
            if (_scan.Exists(item)) list.Add(item);
        }
        foreach (var key in app.Keys)
        {
            var item = new Leftover(key, true, null, key.StartsWith("HKLM", StringComparison.Ordinal));
            if (_scan.Exists(item)) list.Add(item);
        }
        foreach (var value in app.Values)
        {
            var bar = value.IndexOf('|');
            var item = new Leftover(value[..bar], true, value[(bar + 1)..], value.StartsWith("HKLM", StringComparison.Ordinal));
            if (_scan.Exists(item)) list.Add(item);
        }
        if (app.Id == "nvidia") list.AddRange(InstallerLeftovers());
        if (!installed) list.AddRange(_scan.Find(TrailFor(app), deep));
        return LeftoverScan.Outermost(list);
    }

    private static bool MachinePlace(string path) =>
        new[] { "{ProgramData}", "{ProgramFiles}", "{ProgramFilesX86}", "{CommonDesktop}", "{CommonPrograms}", "HKLM" }.Any(place => path.StartsWith(place, StringComparison.Ordinal));

    /// <summary>
    /// Writes the app's <see cref="ReplacedApp.Set"/> values that aren't so yet, once what they were is written down (a .reg
    /// in Exo's cleanup folder that puts them back, <see cref="UndoReg"/>); how many it wrote.
    /// </summary>
    private int ApplySet(ReplacedApp app)
    {
        var pending = app.Set.Select(Parse).Where(set => !Equals(Current(set).Value, set.Value)).ToList();
        if (pending.Count == 0) return 0;
        if (!_isTest) _scan.SaveReg(app.Name + " settings", UndoReg(pending.Select(set => (set.Key, set.Name, Current(set)))));
        foreach (var set in pending) Write(set);
        return pending.Count;
    }

    /// <summary>A Set line: its key, value name ("" is the key's default) and value (a DWORD as int, else text).</summary>
    private static (string Key, string Name, object Value) Parse(string set)
    {
        var bar = set.IndexOf('|');
        var equals = set.IndexOf('=', bar);
        var text = set[(equals + 1)..];
        return (set[..bar], set[(bar + 1)..equals],
            int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
                ? number : text.Replace("{System}", Environment.SystemDirectory, StringComparison.Ordinal));
    }

    /// <summary>What a Set line's value holds now: whether its key is there, and its DWORD or text (null when absent or another kind).</summary>
    private (bool KeyThere, object? Value) Current((string Key, string Name, object Value) set)
    {
        try
        {
            using var key = _scan.Open(set.Key, writable: false);
            if (key is null) return (false, null);
            return (true, key.GetValueKind(set.Name) is RegistryValueKind.DWord or RegistryValueKind.String ? key.GetValue(set.Name) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return (true, null); }
    }

    /// <summary>
    /// A .reg that puts Set values back as they were: a key that wasn't there goes, a value that wasn't there goes, a DWORD
    /// or text gets its old value.
    /// </summary>
    internal static string UndoReg(IEnumerable<(string Key, string Name, (bool KeyThere, object? Value) Was)> values)
    {
        var text = new System.Text.StringBuilder("Windows Registry Editor Version 5.00\r\n");
        foreach (var group in values.GroupBy(value => value.Key, StringComparer.OrdinalIgnoreCase))
        {
            var key = (group.Key.StartsWith("HKLM", StringComparison.Ordinal) ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER") + group.Key[4..];
            if (!group.First().Was.KeyThere)
            {
                text.Append($"\r\n[-{key}]\r\n");
                continue;
            }
            text.Append($"\r\n[{key}]\r\n");
            foreach (var (_, name, was) in group)
            {
                var label = name.Length == 0 ? "@" : $"\"{Escape(name)}\"";
                text.Append(was.Value switch
                {
                    int number => $"{label}=dword:{unchecked((uint)number):x8}\r\n",
                    string old => $"{label}=\"{Escape(old)}\"\r\n",
                    _ => $"{label}=-\r\n",
                });
            }
        }
        return text.ToString();

        static string Escape(string text) => text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private void Write((string Key, string Name, object Value) set)
    {
        try
        {
            var (hive, sub) = _scan.Hive(set.Key);
            using var key = hive.CreateSubKey(sub, writable: true);
            key.SetValue(set.Name, set.Value, set.Value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Debug("Replaced apps: a setting wasn't written: " + ex.GetType().Name);
        }
    }

    /// <summary>Closes the app where it runs from its own folders, so its files can go.</summary>
    private void Close(ReplacedApp app)
    {
        var folders = app.Paths.Select(_scan.Expand).Where(Directory.Exists).Select(folder => folder.TrimEnd('\\') + '\\').ToList();
        if (folders.Count == 0) return;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var exe = process.MainModule?.FileName;
                    if (exe is null || !folders.Any(folder => exe.StartsWith(folder, StringComparison.OrdinalIgnoreCase))) continue;
                    if (ProcessTermination.Kill(process, entireProcessTree: true)) process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
    }

    /// <summary>
    /// Runs the steps only an administrator may in one cmd Windows asks permission for once (several when they don't fit
    /// one command line, which cmd caps at 8191 characters).
    /// </summary>
    internal static async Task<bool> RemoveAsAdminAsync(IReadOnlyList<string> commands, CancellationToken ct)
    {
        if (HostMode.IsDisposable) return false;
        // One prompt, however many steps (ElevatedSteps); false when it was declined.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"exo-{Guid.NewGuid():N}.steps");
        var plan = ElevatedSteps.For(commands, scriptPath);
        try
        {
            if (plan.Script is not null) await File.WriteAllBytesAsync(scriptPath, plan.Script, ct).ConfigureAwait(false);
            using var process = Process.Start(new ProcessStartInfo(plan.File, plan.Arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.SystemDirectory,
            });
            if (process is null) return false;
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; }
        finally
        {
            try { DeletionGate.DeleteFile(scriptPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Debug("Elevated steps: their file stayed: " + ex.GetType().Name); }
        }
    }
}
