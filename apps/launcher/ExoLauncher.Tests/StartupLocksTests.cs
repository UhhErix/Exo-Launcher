using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways Exo could turn off what Windows, security software, anti-cheat, a driver, input or audio, or Exo itself needs:
/// - an item is judged by its name alone (a lookalike name is locked; the real thing under another name is not);
/// - a shortcut or task that only runs PowerShell is called Windows' own because PowerShell is;
/// - a group is locked in the list but Remove goes ahead anyway;
/// - the catalog says "keep" where it must say "locked", or a real switch names something Exo does not know.
/// </summary>
public sealed class StartupLocksTests
{
    private static FakeStartupHost Host() => new();

    private static LockKind Kind(FakeStartupHost host, string command, string? service = null, int type = 0, string? image = null, string name = "Item")
    {
        var program = StartupEntries.ProgramOf(command);
        var known = program is null ? null : StartupCatalog.Find(name, [Path.GetFileName(program).ToLowerInvariant()], program);
        return StartupLocks.Classify(host, new LockCandidate(name, command, program, service, type, image), known);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\SecurityHealthSystray.exe", "Security")]
    [InlineData(@"C:\Program Files\Windows Defender\MSASCuiL.exe", "Security")]
    [InlineData(@"C:\Windows\System32\ctfmon.exe", "Windows")]
    [InlineData(@"C:\Windows\System32\sihost.exe", "Windows")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe", "Updater")]
    [InlineData(@"C:\Program Files\Riot Vanguard\vgc.exe", "AntiCheat")]
    [InlineData(@"C:\Program Files (x86)\Common Files\Easy Anti-Cheat\EasyAntiCheat_EOS.exe", "AntiCheat")]
    [InlineData(@"C:\Program Files (x86)\Common Files\BattlEye\BEService.exe", "AntiCheat")]
    [InlineData(@"C:\Program Files\NVIDIA Corporation\NvContainer\nvcontainer.exe", "Driver")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\realtek.inf_amd64_1\RtkAudUService64.exe", "Driver")]
    [InlineData(@"C:\Program Files\LGHUB\lghub_agent.exe", "InputAudio")]
    [InlineData(@"C:\Program Files\Acme\acme.exe", "None")]
    [InlineData(@"C:\Program Files\Riot Vanguard\vgtray.exe", "None")]
    public void EachGroupIsRecognisedByWhatItIs(string program, string expected)
    {
        Assert.Equal(Enum.Parse<LockKind>(expected), Kind(Host(), "\"" + program + "\""));
    }

    [Fact]
    public void ASecurityProductWindowsSecurityListsIsLockedWhateverItIsCalled()
    {
        var host = Host();
        host.Security.Add(@"C:\Program Files\Totally Unknown AV\guard.exe");
        Assert.Equal(LockKind.Security, Kind(host, "\"C:\\Program Files\\Totally Unknown AV\\guard.exe\""));
        Assert.Equal(LockKind.Security, Kind(host, "\"C:\\Program Files\\Totally Unknown AV\\tray.exe\""));   // its tray, beside it
        Assert.Equal(LockKind.None, Kind(host, "\"C:\\Program Files\\Other\\guard.exe\""));
    }

    [Fact]
    public void AProgramSignedAsMicrosoftWindowsIsWindowsEvenOutsideTheWindowsFolder()
    {
        var host = Host();
        host.Signers[@"C:\Program Files\WindowsApps\Foo\foo.exe"] = "Microsoft Windows";
        Assert.Equal(LockKind.Windows, Kind(host, "\"C:\\Program Files\\WindowsApps\\Foo\\foo.exe\""));
        Assert.Equal(LockKind.None, Kind(host, "\"C:\\Program Files\\Acme\\acme.exe\""));
    }

    [Fact]
    public void AServiceInsideAGamesFolderIsAntiCheat()
    {
        var host = Host();
        var before = StartupLocks.GameFolders;
        try
        {
            StartupLocks.GameFolders = () => [@"D:\Games\Some Game"];
            Assert.Equal(LockKind.AntiCheat, Kind(host, "\"D:\\Games\\Some Game\\Binaries\\guard.exe\"", service: "SomeGuard", type: 0x10, image: "\"D:\\Games\\Some Game\\Binaries\\guard.exe\""));
            Assert.Equal(LockKind.None, Kind(host, "\"D:\\Games\\Other\\guard.exe\""));
        }
        finally { StartupLocks.GameFolders = before; }
    }

    [Fact]
    public void KernelServicesAndAudioServicesAreLocked()
    {
        var host = Host();
        Assert.Equal(LockKind.Driver, Kind(host, @"C:\Windows\System32\drivers\foo.sys", service: "foo", type: 1));
        Assert.Equal(LockKind.InputAudio, Kind(host, @"C:\Windows\System32\svchost.exe -k LocalServiceNetworkRestricted", service: "Audiosrv", type: 0x20, image: @"C:\Windows\System32\svchost.exe -k LocalServiceNetworkRestricted"));
    }

    [Fact]
    public void ExoAndItsAddOnsAreLocked()
    {
        var host = Host();
        Assert.Equal(LockKind.Exo, Kind(host, "\"" + Path.Combine(AppContext.BaseDirectory, "ExoLauncher.exe") + "\" --tray"));
        Assert.Equal(LockKind.Exo, Kind(host, "\"" + Path.Combine(AppContext.BaseDirectory, "addons", "lucid", "Lucid.exe") + "\""));
        Assert.Equal(LockKind.Exo, Kind(host, @"C:\Users\Erix\AppData\Local\ExoLauncher\addons\shaders\ExoShaders.Service.exe"));
    }

    [Fact]
    public void AProgramInExosAddOnsFolderIsLocked()
    {
        var host = Host();
        // Where Exo installs its add-ons (%LOCALAPPDATA%\ExoLauncher\addons, as CaptureAddon and ShadersAddon build it),
        // run by a program that is neither Exo's own executable nor an add-on Exo names.
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExoLauncher", "addons", "toolbox");
        Assert.Equal(LockKind.Exo, Kind(host, "\"" + Path.Combine(folder, "helper.exe") + "\"", name: "Add-on helper"));
    }

    [Fact]
    public void AProgramThatOnlyRunsOtherProgramsIsNeverWindowsOwn()
    {
        var host = Host();
        Assert.Equal(LockKind.None, Kind(host, "\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -File C:\\Users\\Erix\\claude.ps1"));
        Assert.Equal(LockKind.None, Kind(host, "\"C:\\Windows\\System32\\cmd.exe\" /c C:\\Games\\launch.bat"));
    }

    [Fact]
    public void EveryLockedItemInTheListRefusesRemoveAndWritesNothing()
    {
        var host = Host();
        host.SetRun(Hive.Machine, StartupKeys.Run, "SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe");
        host.SetRun(Hive.Machine, StartupKeys.Run, "AcmeAV", "\"C:\\Program Files\\Acme AV\\avtray.exe\"");
        host.Security.Add(@"C:\Program Files\Acme AV\avtray.exe");
        host.SetRun(Hive.Machine, StartupKeys.Run, "NVIDIA", "\"C:\\Program Files\\NVIDIA Corporation\\NvContainer\\nvcontainer.exe\"");
        host.SetRun(Hive.User, StartupKeys.Run, "EdgeUpdate", "\"C:\\Program Files (x86)\\Microsoft\\EdgeUpdate\\MicrosoftEdgeUpdate.exe\" /c");
        host.AddService("vgc", @"""C:\Program Files\Riot Vanguard\vgc.exe""", start: 2);
        host.AddService("NVDisplay.ContainerLocalSystem", @"""C:\Windows\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_1\Display.NvContainer\NVDisplay.Container.exe""", start: 2);
        host.AddTask(@"\MicrosoftEdgeUpdateTaskMachineUA", @"C:\Program Files (x86)\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe", "/ua");
        using var _ = StartupEntries.UseHost(host);

        var entries = StartupEntries.ReadAll(host).Where(entry => entry.Locked).ToList();
        Assert.Equal(7, entries.Count);
        foreach (var entry in entries)
        {
            Assert.Equal(StartupLocks.Sentence(entry.Lock), StartupEntries.Remove(host, entry.Id));
            Assert.False(StartupEntries.Unneeded(entry));
        }
        Assert.Empty(host.Applied);
        Assert.Empty(StartupEntries.Kept(host));
    }

    [Fact]
    public void WhenSecuritySoftwareCannotBeListedUnknownItemsAreNotTurnedOff()
    {
        var host = Host();
        host.SecurityKnown = false;
        // What an uninstalled app left behind: Clean up would take it, but not while it can't vouch for it.
        host.SetRun(Hive.User, StartupKeys.Run, "Unknown", "\"C:\\Program Files\\Acme\\acme.exe\"");
        host.MissingPrograms.Add(@"C:\Program Files\Acme\acme.exe");
        using var _ = StartupEntries.UseHost(host);

        Assert.Equal("Exo couldn't check this PC's security software, so it left this on.", StartupEntries.Remove(host, "run:cu:Unknown"));
        Assert.Empty(host.Applied);
        Assert.Empty(StartupEntries.Kept(host));
        var (removed, error) = StartupEntries.Clean(host);
        Assert.Equal(0, removed);
        // Clean up says the same thing Remove does, instead of reporting success with nothing removed.
        Assert.Equal(StartupEntries.SecuritySentence, error);
        Assert.Equal("Exo couldn't check this PC's security software, so it left this on.", error);
        Assert.Empty(host.Applied);
    }

    [Fact]
    public void AnItemTheCatalogSaysIsSafeToTurnOffCanStillBeTurnedOffThen()
    {
        var host = Host();
        host.SecurityKnown = false;
        host.SetRun(Hive.User, StartupKeys.Run, "Spotify", "\"C:\\Users\\Erix\\AppData\\Roaming\\Spotify\\Spotify.exe\"");
        using var _ = StartupEntries.UseHost(host);

        // The catalog says this one is safe to turn off: Exo does not need the security list to know it.
        Assert.Null(StartupEntries.Remove(host, "run:cu:Spotify"));
        Assert.True(StartupApps.IsEnabled(host.Get(Hive.User, StartupKeys.Approved + "Run", "Spotify")?.Bytes) is false);
        Assert.Single(StartupEntries.Kept(host));

        // And a locked one is refused for its own reason, not this one.
        host.SetRun(Hive.Machine, StartupKeys.Run, "SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe");
        Assert.Equal(StartupLocks.Sentence(LockKind.Security), StartupEntries.Remove(host, "run:lm:SecurityHealth"));
    }

    [Fact]
    public void AFailedSecurityListIsNotAskedForAgainForEveryEntry()
    {
        var host = new RealStartupHost();
        var asked = 0;
        var at = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        host.SecurityClock = () => at;
        // This PC's security software can't be listed (the case the whole check exists for): every read fails.
        host.SecurityRead = () => { Interlocked.Increment(ref asked); return ([], false, "Windows Security answered nothing."); };

        // Twenty entries of one check ask twenty times: the read runs once, in the background, and the answer is the same for
        // all of them. Nothing waits for it.
        for (var i = 0; i < 20; i++) host.SecurityPrograms();
        SpinWait.SpinUntil(() => Volatile.Read(ref asked) == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref asked));    // once for all twenty entries of a check
        Assert.False(host.SecurityKnown);             // and Exo never says it knows what it couldn't read

        // A minute later it is asked again, once more; ten minutes for an answer that worked.
        at = at.AddSeconds(59);
        host.SecurityPrograms();
        Assert.Equal(1, Volatile.Read(ref asked));
        at = at.AddSeconds(2);
        host.SecurityPrograms();
        SpinWait.SpinUntil(() => Volatile.Read(ref asked) == 2, TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref asked));
        Assert.False(host.SecurityKnown);

        // A read that works is kept, and says it knows.
        host.SecurityRead = () => { Interlocked.Increment(ref asked); return ([@"C:\Program Files\Acme AV\avtray.exe"], true, null); };
        at = at.AddMinutes(2);
        host.SecurityPrograms();
        SpinWait.SpinUntil(() => host.SecurityKnown, TimeSpan.FromSeconds(5));
        Assert.True(host.SecurityKnown);
        Assert.Single(host.SecurityPrograms());
        Assert.Equal(3, Volatile.Read(ref asked));

        at = at.AddMinutes(5);
        Assert.Single(host.SecurityPrograms());       // ten minutes for one that worked
        Assert.Equal(3, Volatile.Read(ref asked));
    }

    [Fact]
    public void AskingForTheSecurityListNeverWaitsForTheRead()
    {
        var host = new RealStartupHost();
        using var asked = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        host.SecurityRead = () =>
        {
            asked.Set();
            // The read takes a long time (PowerShell starting on a busy PC): the caller must not be held.
            Assert.True(answer.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            return ([@"C:\Program Files\Acme AV\avtray.exe"], true, null);
        };

        // The first question starts the read and comes back at once, with nothing known yet.
        var list = host.SecurityPrograms();
        Assert.Empty(list);
        Assert.False(host.SecurityKnown);
        Assert.True(asked.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // The read finishes: the answer is there at once, and known.
        answer.Set();
        SpinWait.SpinUntil(() => host.SecurityKnown, TimeSpan.FromSeconds(5));
        Assert.True(host.SecurityKnown);
        Assert.Single(host.SecurityPrograms());
        Assert.Equal(@"C:\Program Files\Acme AV\avtray.exe", host.SecurityPrograms()[0]);
    }

    [Fact]
    public void SignaturesAreReadOncePerFileAndNotAgainUntilTheFileChanges()
    {
        var reads = 0;
        var stamp = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        string? Read(string path) { reads++; return "Acme Ltd"; }
        DateTime Written(string path) => stamp;
        var cache = new SignerCache(Read, Written);

        cache.Of(@"C:\Program Files\Acme\acme.exe");
        cache.Of(@"C:\Program Files\Acme\acme.exe");
        Assert.Equal(1, reads);
        // A file written again is a new file: read again, and the old answer is not kept.
        stamp = stamp.AddHours(1);
        cache.Of(@"C:\Program Files\Acme\acme.exe");
        Assert.Equal(2, reads);
    }

    [Fact]
    public void TheCatalogLocksWhatItMustAndEveryRealSwitchIsOneExoKnows()
    {
        Assert.Equal("locked", StartupCatalog.Find("Windows Security", ["securityhealthsystray.exe"], null)?.Verdict);
        Assert.Equal("locked", StartupCatalog.Find("Edge", ["microsoftedgeupdate.exe"], null)?.Verdict);
        Assert.NotEqual("locked", StartupCatalog.Find("Riot Vanguard", ["vgtray.exe"], null)?.Verdict);
        Assert.Equal("antiCheat", StartupCatalog.Find("Riot Vanguard", ["vgtray.exe"], null)?.RealSwitch?.Kind);
        var scs = StartupCatalog.Find("SCSKiller", ["scskillerw.exe"], null)?.RealSwitch;
        Assert.Equal("jsonSetting", scs?.Kind);
        Assert.Equal("OnDriverUpdate", scs?.Path);
        foreach (var item in StartupCatalog.All.Where(item => item.RealSwitch is not null))
        {
            var switchKind = item.RealSwitch!.Kind;
            Assert.Contains(switchKind, new[] { "jsonSetting", "updaterService", "antiCheat" });
            if (switchKind == "jsonSetting") Assert.False(string.IsNullOrEmpty(item.RealSwitch.File) || string.IsNullOrEmpty(item.RealSwitch.Path) || string.IsNullOrEmpty(item.RealSwitch.Off));
            Assert.False(string.IsNullOrEmpty(item.RealSwitch.Status));
        }
        // No item that Exo would offer to remove is one of the groups it never turns off.
        var host = Host();
        foreach (var item in StartupCatalog.All.Where(item => item.Verdict == "remove"))
            foreach (var match in item.Match.Where(match => match.EndsWith(".exe", StringComparison.Ordinal)))
                Assert.Equal(LockKind.None, StartupLocks.Classify(host, new LockCandidate(item.Name, match, @"C:\Program Files\X\" + match), item));
    }

    [Fact]
    public void AnEmptySuccessfulSecurityListCountsAsKnownAndAllowsRemove()
    {
        var real = new RealStartupHost();
        real.SecurityRead = () => ([], true, null);
        real.EnsureSecurityReady(TimeSpan.FromSeconds(5));
        Assert.True(real.SecurityKnown);

        var host = Host();
        host.SecurityKnown = true;
        host.SetRun(Hive.User, StartupKeys.Run, "UnknownApp", "\"C:\\Program Files\\Acme\\acme.exe\"");
        host.MissingPrograms.Add(@"C:\Program Files\Acme\acme.exe");
        using var _ = StartupEntries.UseHost(host);

        Assert.Null(StartupEntries.Remove(host, "run:cu:UnknownApp"));
        Assert.Null(host.Get(Hive.User, StartupKeys.Run, "UnknownApp"));
        Assert.Empty(StartupEntries.Kept(host));
    }

    [Fact]
    public void MissingStartupItemsDeletePermanentlyAcrossRunFolderAndTaskWithoutLooping()
    {
        var host = Host();
        var deadExe = @"C:\Program Files\RemovedApp\removed.exe";
        host.MissingPrograms.Add(deadExe);

        // Missing Run entry (with existing StartupApproved mark)
        host.SetRun(Hive.User, StartupKeys.Run, "RemovedApp", $"\"{deadExe}\"");
        host.Set(Hive.User, StartupKeys.Approved + "Run", "RemovedApp", RegValue.Bin(FakeStartupHost.On()));

        // Missing Startup folder shortcut
        var shortcutPath = Path.Combine(host.FolderPath(false), "RemovedApp.lnk");
        host.UserFolder.Add(new FolderFile("RemovedApp.lnk", deadExe));
        host.Files[shortcutPath] = [1, 2, 3];
        host.Set(Hive.User, StartupKeys.Approved + "StartupFolder", "RemovedApp.lnk", RegValue.Bin(FakeStartupHost.On()));

        // Missing scheduled task
        host.AddTask(@"\RemovedAppTask", deadExe, enabled: true);

        using var _ = StartupEntries.UseHost(host);
        var (removed, error) = StartupEntries.Clean(host);
        Assert.Equal(3, removed);
        Assert.Null(error);
        Assert.Null(host.Get(Hive.User, StartupKeys.Run, "RemovedApp"));
        Assert.Null(host.Get(Hive.User, StartupKeys.Approved + "Run", "RemovedApp"));
        Assert.False(host.FileExists(shortcutPath));
        Assert.Null(host.Get(Hive.User, StartupKeys.Approved + "StartupFolder", "RemovedApp.lnk"));
        Assert.DoesNotContain(host.TaskList, t => t.Path == @"\RemovedAppTask");
        Assert.Empty(StartupEntries.Kept(host));

        // Second Clean finds nothing left (no infinite "removed 3 of 3" loop).
        var (again, _) = StartupEntries.Clean(host);
        Assert.Equal(0, again);
    }
}
