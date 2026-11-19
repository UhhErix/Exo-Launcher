using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Tests;

/// <summary>
/// NVIDIA App and Control Panel removed in Clean up, and again after each driver install, in a sandbox (folders under a
/// temp root, keys under a test key in HKCU). Ways it could fail: a driver install brings the Control Panel back and it
/// stays; it goes on a PC whose user never removed it; NVIDIA's tray icon and desktop menu stay on; an installer folder the
/// driver's own uninstall uses (named in NVIDIA's records), or one an install is writing now, goes; the installer's
/// CoreTemp copy stays forever; the driver step's removals aren't the ones Clean up would run, or hold the install's
/// ending; a driver Exo didn't install (Windows Update) isn't noticed, or the same one is followed up on every start; the
/// undo file can't put the old settings back.
/// </summary>
public sealed class NvidiaCleanupTests : IDisposable
{
    private const string ControlPanel = "NVIDIACorp.NVIDIAControlPanel_8.1.969.0_x64__56jybvy8sckqj";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-nvidia-" + Guid.NewGuid().ToString("N"));
    private readonly string _hkcu, _hklm;
    private readonly FakePackages _packages = new();
    private readonly List<string> _admin = [];

    public NvidiaCleanupTests()
    {
        var test = @"Software\ExoLauncherTests\" + Guid.NewGuid().ToString("N");
        _hkcu = test + @"\HKCU";
        _hklm = test + @"\HKLM";
        foreach (var place in Places.Values) Directory.CreateDirectory(place);
    }

    private Dictionary<string, string> Places => new(StringComparer.OrdinalIgnoreCase)
    {
        ["LocalAppData"] = Path.Combine(_root, "Local"),
        ["AppData"] = Path.Combine(_root, "Roaming"),
        ["Desktop"] = Path.Combine(_root, "Desktop"),
        ["CommonDesktop"] = Path.Combine(_root, "PublicDesktop"),
        ["CommonPrograms"] = Path.Combine(_root, "CommonPrograms"),
        ["ProgramFiles"] = Path.Combine(_root, "Program Files"),
        ["ProgramData"] = Path.Combine(_root, "ProgramData"),
        ["Temp"] = Path.Combine(_root, "Temp"),
    };

    private ReplacedApps Apps() => new(Places, _packages, (_, _) => Task.FromResult<string?>(null),
        (commands, _) =>
        {
            _admin.AddRange(commands);
            foreach (var command in commands.Where(command => command.StartsWith("rd ", StringComparison.Ordinal)))
                DeletionGate.DeleteDirectory(command.Split('"')[1], true);
            return Task.FromResult(true);
        },
        _hkcu, _hklm, Path.Combine(_root, "replaced.json"));

    private string Installer2 => Path.Combine(Places["ProgramFiles"], @"NVIDIA Corporation\Installer2");

    /// <summary>
    /// NVIDIA's installer folders as this PC had them after 621.12, its records as Windows kept them: the driver's named in
    /// a value, PhysX's only in a list (Extensions), another only as a value's name (Drivers); the installer core; CoreTemp
    /// named nowhere.
    /// </summary>
    private (string CoreTemp, string Driver, string Core) InstallerFolders(TimeSpan age)
    {
        var coreTemp = Path.Combine(Installer2, "CoreTemp.{259C54FE-3EAD-4C96-ADEE-055282D7520D}");
        var driver = Path.Combine(Installer2, "Display.Driver.{3CD35E46-8DB8-47EF-B40B-615104224B98}");
        var physx = Path.Combine(Installer2, "Display.PhysX.{CEACFA87-CF4C-4844-8032-0F4B8BBA58AE}");
        var named = Path.Combine(Installer2, "installer.{D75A1F1A-686B-4F40-8E1A-6B15AED90E72}");
        var core = Path.Combine(Installer2, "InstallerCore");
        foreach (var folder in new[] { coreTemp, driver, physx, named, core })
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "NVI2.DLL"), "x");
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow - age);
        }
        const string records = @"\SOFTWARE\NVIDIA Corporation\Installer2";
        using (var cache = Registry.CurrentUser.CreateSubKey(_hklm + records + @"\Cache"))
            cache.SetValue("Display.Driver/621.12", Path.Combine(driver, "DisplayDriver.nvi"));
        using (var extensions = Registry.CurrentUser.CreateSubKey(_hklm + records + @"\Extensions"))
            extensions.SetValue("Display.PhysX/9.26.703", new[] { Path.Combine(physx, "PhysXExt.dll") }, RegistryValueKind.MultiString);
        using (var drivers = Registry.CurrentUser.CreateSubKey(_hklm + records + @"\Drivers"))
            drivers.SetValue(Path.Combine(named, "nv_dispi.inf"), "Display.Driver/621.12");
        return (coreTemp, driver, core);
    }

    private object? Hkcu(string key, string name)
    {
        using var opened = Registry.CurrentUser.OpenSubKey(_hkcu + '\\' + key);
        return opened?.GetValue(name);
    }

    [Fact]
    public async Task RemovedOnceItGoesAgainAfterADriverInstallWithTheTrayIconAndMenuOff()
    {
        _packages.Installed.Add(ControlPanel);
        var apps = Apps();

        // Not removed in Clean up: a driver install leaves it alone.
        var (before, _) = await apps.AgainAsync("621.01", admin: false, "test", CancellationToken.None);
        Assert.Empty(before);
        Assert.Contains(ControlPanel, _packages.Installed);

        var result = await apps.RemoveAsync("nvidia", CancellationToken.None);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(0, Hkcu(@"Software\NVIDIA Corporation\NvTray", "StartOnLogin"));
        Assert.Equal(0, Hkcu(@"Software\NVIDIA Corporation\Global\NvCplApi\Policies", "ContextUIPolicy"));
        // Out of Windows' image too, in the one elevated step.
        Assert.Contains(_admin, command => command.Contains("Remove-AppxProvisionedPackage", StringComparison.Ordinal) && command.Contains("'NVIDIACorp.NVIDIAControlPanel'", StringComparison.Ordinal));

        // The driver's setup provisions it again and the tray icon is turned back on.
        _packages.Installed.Add(ControlPanel);
        using (var tray = Registry.CurrentUser.CreateSubKey(_hkcu + @"\Software\NVIDIA Corporation\NvTray")) tray.SetValue("StartOnLogin", 1);
        var (done, missed) = await apps.AgainAsync("621.12", admin: false, "test", CancellationToken.None);

        Assert.Equal(["NVIDIA App and Control Panel, removed again"], done);
        Assert.Empty(missed);
        Assert.DoesNotContain(ControlPanel, _packages.Installed);
        Assert.Equal(0, Hkcu(@"Software\NVIDIA Corporation\NvTray", "StartOnLogin"));
    }

    [Fact]
    public async Task OnlyTheInstallersOwnUnnamedFoldersGoAndOnlyWithWindowsPermission()
    {
        var (coreTemp, driver, core) = InstallerFolders(TimeSpan.FromHours(2));
        var apps = Apps();
        var nvidia = apps.Scan().Single(app => app.Id == "nvidia");
        Assert.False(nvidia.Installed);
        Assert.True(nvidia.NeedsAdmin);
        Assert.Equal(1, nvidia.Found.Folders);

        await apps.RemoveAsync("nvidia", CancellationToken.None);
        Assert.False(Directory.Exists(coreTemp));
        Assert.True(Directory.Exists(driver)); // Apps & features uninstalls the driver through it
        Assert.True(Directory.Exists(core));

        // The next install's CoreTemp: with Windows' permission it goes; without, it's said.
        Directory.CreateDirectory(coreTemp);
        Directory.SetLastWriteTimeUtc(coreTemp, DateTime.UtcNow - TimeSpan.FromHours(2));
        var (_, missed) = await apps.AgainAsync("621.12", admin: false, "test", CancellationToken.None);
        Assert.Equal(["1 of NVIDIA App and Control Panel's leftovers need Windows' permission. Remove them in Clean up."], missed);
        Assert.True(Directory.Exists(coreTemp));
        var (done, _) = await apps.AgainAsync("621.12", admin: true, "test", CancellationToken.None);
        Assert.Single(done);
        Assert.False(Directory.Exists(coreTemp));
    }

    [Fact]
    public void AFolderAnInstallIsWritingStays()
    {
        InstallerFolders(TimeSpan.FromMinutes(5));
        Assert.Equal(0, Apps().Scan().Single(app => app.Id == "nvidia").Leftovers);
    }

    [Fact]
    public async Task TheDriverStepTakesTheControlPanelOutOfWindowsImageWithoutHoldingTheInstall()
    {
        var apps = Apps();
        Assert.Empty(apps.DriverStepCommands()); // nothing removed in Clean up yet
        await apps.RemoveAsync("nvidia", CancellationToken.None);
        var (coreTemp, driver, _) = InstallerFolders(TimeSpan.FromHours(2));

        var commands = apps.DriverStepCommands();

        Assert.Contains(commands, command => command.StartsWith("start \"\" /b powershell.exe ", StringComparison.Ordinal) && command.Contains("Remove-AppxProvisionedPackage", StringComparison.Ordinal));
        Assert.Contains($"rd /s /q \"{coreTemp}\"", commands);
        Assert.DoesNotContain(commands, command => command.Contains(driver, StringComparison.OrdinalIgnoreCase));
        Assert.All(commands, command => Assert.DoesNotContain("!", command, StringComparison.Ordinal)); // the step runs with delayed expansion
    }

    [Theory]
    [InlineData("CoreTemp.{259C54FE-3EAD-4C96-ADEE-055282D7520D}", 120, true)]
    [InlineData("Display.Driver.{3CD35E46-8DB8-47EF-B40B-615104224B98}", 120, false)] // named in the records
    [InlineData("installer.{F214A111-6880-4BA8-909B-9DE48B7986AA}", 120, false)]      // PhysX's setup.CFG
    [InlineData("CoreTemp.{259C54FE-3EAD-4C96-ADEE-055282D7520D}", 30, false)]        // an install may be running
    [InlineData("InstallerCore", 120, false)]                                       // not a component's copy
    [InlineData("NVIDIA App", 120, false)]
    public void InstallerFoldersGoOnlyWhenNoRecordNamesThem(string name, int minutesOld, bool unused)
    {
        const string root = @"C:\Program Files\NVIDIA Corporation\Installer2";
        string[] records =
        [
            root + @"\Display.Driver.{3CD35E46-8DB8-47EF-B40B-615104224B98}\DisplayDriver.nvi",
            root + @"\installer.{F214A111-6880-4BA8-909B-9DE48B7986AA}\setup.CFG",
            "Display.Driver/621.12",
        ];
        var now = new DateTime(2026, 10, 28, 20, 0, 0, DateTimeKind.Utc);
        Assert.Equal(unused, ReplacedApps.UnusedInstallerFolder(Path.Combine(root, name), records, now - TimeSpan.FromMinutes(minutesOld), now));
    }

    [Theory]
    [InlineData(null, "621.12", true)]    // removed before Exo kept the driver: followed up once
    [InlineData("621.01", "621.12", true)] // Windows Update or NVIDIA's own installer
    [InlineData("621.12", "621.12", false)]
    [InlineData("621.12", null, false)]   // no NVIDIA driver to read
    public void ADriverExoDidntInstallIsNoticedOnce(string? seen, string? now, bool changed) =>
        Assert.Equal(changed, ReplacedApps.DriverChanged(seen, now));

    [Fact]
    public void TheUndoFilePutsEachSettingBackAsItWas()
    {
        var reg = ReplacedApps.UndoReg(
        [
            (@"HKCU\Software\NVIDIA Corporation\NvTray", "StartOnLogin", (false, null)),
            (@"HKCU\Software\NVIDIA Corporation\Global\NvCplApi\Policies", "ContextUIPolicy", (true, (object?)2)),
            (@"HKCU\Software\NVIDIA Corporation\Global\NvCplApi\Policies", "Other", (true, null)),
            (@"HKCU\Software\Classes\ms-gamebar\shell\open\command", "", (true, (object?)@"C:\Program Files\x.exe ""%1""")),
        ]);

        Assert.StartsWith("Windows Registry Editor Version 5.00\r\n", reg);
        Assert.Contains("\r\n[-HKEY_CURRENT_USER\\Software\\NVIDIA Corporation\\NvTray]\r\n", reg);
        Assert.Contains("[HKEY_CURRENT_USER\\Software\\NVIDIA Corporation\\Global\\NvCplApi\\Policies]\r\n\"ContextUIPolicy\"=dword:00000002\r\n\"Other\"=-\r\n", reg);
        Assert.Contains("@=\"C:\\\\Program Files\\\\x.exe \\\"%1\\\"\"\r\n", reg);
    }

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_root, true); } catch (IOException) { }
        Registry.CurrentUser.DeleteSubKeyTree(_hkcu[..^5], false);
    }

    private sealed class FakePackages : IAppPackages
    {
        public List<string> Installed { get; } = [];

        public IReadOnlyList<string> Find(string name) => Installed.Where(full => full.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)).ToList();

        public Task<bool> RemoveAsync(string fullName, CancellationToken ct)
        {
            Installed.Remove(fullName);
            return Task.FromResult(true);
        }
    }
}
