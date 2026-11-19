using System.Text.Json;
using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Tests;

/// <summary>
/// Ways the start-up guard could fail silently, each on a fake of the registry, tasks, files, programs and window in front:
/// - an app rewrites its Run value, or flips StartupApproved back on, and the item starts again with nobody told;
/// - the player's own re-enable in Task Manager or Settings is fought, or a program's is mistaken for the player's;
/// - a task made again by `schtasks /Create /F` (SCSKiller) or under a new name is missed;
/// - a repeat offender loops, uses its switch twice, or Exo changes an updater service or anti-cheat;
/// - something Windows, security software, anti-cheat, a driver, input or audio needs is turned off, or an older Exo's removal of it stays;
/// - Put back is not exact, or the guard goes on watching;
/// - Windows' prompt is shown per item, or again after it was declined; a watch is left open with nothing to watch.
/// </summary>
public sealed class StartupGuardTests
{
    private const string Acme = "\"C:\\Acme\\acme.exe\" --tray";
    private const string Scs = @"C:\Users\Erix\AppData\Local\SCSKiller.App\current\cli\scskillerw.exe";
    private const string ScsTask = @"\SCSKiller\RewarmStale";
    private static readonly string ScsSettings = Environment.ExpandEnvironmentVariables(@"%LocalAppData%\SCSKiller\settings.json");

    private sealed class Rig : IDisposable
    {
        internal FakeStartupHost Host { get; } = new();
        internal StartupGuard Guard { get; }
        internal List<string> Log { get; } = [];
        private readonly IDisposable _scope;

        internal Rig()
        {
            _scope = StartupEntries.UseHost(Host);
            Guard = new StartupGuard(Host, debounce: TimeSpan.Zero, tick: TimeSpan.FromDays(1)) { Logged = Log.Add };
        }

        public void Dispose() { Guard.Dispose(); _scope.Dispose(); }

        internal string? Remove(string id) => StartupEntries.Remove(Host, id);
        internal string? Restore(string id) => StartupEntries.Restore(Host, id);
        internal StartupGuard.Item? Item(string id) => StartupGuard.Load(Host).Items.FirstOrDefault(item => item.Id == id);
        internal GuardState State(string id) => Item(id)?.State ?? GuardState.Untracked;
        internal bool IsOff(Hive hive, string key, string name) => Host.Approval(hive, key, name) is { Length: > 0 } bytes && (bytes[0] & 1) == 1;

        internal void AddAcme(Hive hive = Hive.User, string name = "Acme", string command = Acme)
        {
            Host.SetRun(hive, StartupKeys.Run, name, command);
            Host.Set(hive, StartupKeys.Approved + "Run", name, RegValue.Bin(FakeStartupHost.On()));
        }

        internal void AddScsTask()
        {
            Host.AddTask(ScsTask, Scs, "rewarm-stale --if-driver-changed");
            Host.WriteFile(ScsSettings, "{\"OnDriverUpdate\":\"Ask\",\"Threads\":9}");
            Host.FileWrites.Clear();
        }

        internal void Flip(string name = "Acme", Hive hive = Hive.User) =>
            Host.Set(hive, StartupKeys.Approved + "Run", name, RegValue.Bin(FakeStartupHost.On()));

        internal string Setting(string key) => JsonDocument.Parse(Host.ReadFile(ScsSettings)!).RootElement.GetProperty(key).ToString();
    }

    [Fact]
    public void AnAppThatWritesItsRunValueAgainStaysOffAndIsLoggedOnce()
    {
        using var rig = new Rig();
        rig.AddAcme();
        Assert.Null(rig.Remove("run:cu:Acme"));
        Assert.True(rig.IsOff(Hive.User, "Run", "Acme"));
        // Task Manager's way: the value stays where it was.
        Assert.Equal(Acme, rig.Host.Get(Hive.User, StartupKeys.Run, "Acme")?.Text);

        rig.Host.SetRun(Hive.User, StartupKeys.Run, "Acme", "\"C:\\Acme\\v2\\acme.exe\" --tray");
        rig.Host.ForegroundProgram = "acme";
        rig.Host.Raise();
        rig.Host.Raise();

        Assert.True(rig.IsOff(Hive.User, "Run", "Acme"));
        Assert.Single(rig.Log, line => line.Contains("wrote itself back"));
        Assert.Equal(GuardState.Off, rig.State("run:cu:Acme"));
    }

    [Fact]
    public void AnElectronStyleFlipWhileAnotherProgramIsInFrontIsPutOffAgainWithANewStamp()
    {
        using var rig = new Rig();
        rig.AddAcme();
        rig.Remove("run:cu:Acme");
        var first = rig.Host.Approval(Hive.User, "Run", "Acme")!;

        rig.Host.Now = rig.Host.Now.AddMinutes(5);
        rig.Flip();
        rig.Host.ForegroundProgram = "acme";
        rig.Host.Raise();

        var after = rig.Host.Approval(Hive.User, "Run", "Acme")!;
        Assert.Equal(3, after[0]);
        Assert.NotEqual(first, after);
        Assert.Equal(StartupApps.Approved(false, rig.Host.Now), after);
        Assert.Equal(GuardState.Back, rig.State("run:cu:Acme"));
        Assert.Single(rig.Item("run:cu:Acme")!.Comebacks);
        Assert.Contains(rig.Log, line => line.Contains("turned itself back on") && line.Contains("off again"));
    }

    [Fact]
    public void TheSameFlipWithTaskManagerInFrontIsThePlayersAndLeftOn()
    {
        using var rig = new Rig();
        rig.AddAcme();
        rig.Remove("run:cu:Acme");

        rig.Flip();
        rig.Host.ForegroundProgram = "taskmgr";
        rig.Host.Raise();

        Assert.False(rig.IsOff(Hive.User, "Run", "Acme"));
        Assert.Empty(StartupEntries.Kept(rig.Host));
        Assert.Equal(GuardState.PlayerOn, rig.State("run:cu:Acme"));
        Assert.Contains(rig.Log, line => line == "Startup: Acme turned back on in Task Manager; left on.");
        // Left on for good: the program turning it on again later is not fought.
        rig.Host.ForegroundProgram = "acme";
        rig.Host.Raise();
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.False(rig.IsOff(Hive.User, "Run", "Acme"));
        // Until the player turns it off in Exo again.
        Assert.Null(rig.Remove("run:cu:Acme"));
        Assert.Equal(GuardState.Off, rig.State("run:cu:Acme"));
        Assert.True(rig.IsOff(Hive.User, "Run", "Acme"));
    }

    [Fact]
    public void AComebackFoundAtStartWithNoEvidenceIsPutOffAgainOnce()
    {
        using var rig = new Rig();
        rig.AddAcme();
        rig.Remove("run:cu:Acme");
        rig.Flip();
        rig.Host.ForegroundProgram = "acme";
        rig.Guard.Start();   // no notification saw it: who did it can't be told

        Assert.True(rig.IsOff(Hive.User, "Run", "Acme"), string.Join(" | ", rig.Log));
        Assert.Equal(GuardState.Back, rig.State("run:cu:Acme"));
        Assert.Single(rig.Item("run:cu:Acme")!.Comebacks);
        Assert.Single(StartupEntries.Kept(rig.Host), item => item.Id == "run:cu:Acme");
        var line = Assert.Single(rig.Log, line => line.Contains("turned itself back on"));
        Assert.DoesNotContain("tool", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("off again", line);
    }

    [Fact]
    public void ASecondComebackWithNoEvidenceIsLeftOnAndTheRecordIsKept()
    {
        using var rig = new Rig();
        // A Run value, which Task Manager and Settings do show, and whose program has a switch in the catalog.
        rig.Host.SetRun(Hive.User, StartupKeys.Run, "SCSKiller", "\"" + Scs + "\" --tray");
        rig.Host.Set(Hive.User, StartupKeys.Approved + "Run", "SCSKiller", RegValue.Bin(FakeStartupHost.On()));
        rig.Host.WriteFile(ScsSettings, "{\"OnDriverUpdate\":\"Ask\",\"Threads\":9}");
        Assert.Null(rig.Remove("run:cu:SCSKiller"));
        rig.Host.FileWrites.Clear();

        rig.Flip("SCSKiller");
        rig.Guard.Start();   // no notification saw it: put off again once
        Assert.True(rig.IsOff(Hive.User, "Run", "SCSKiller"));
        Assert.Equal(GuardState.Back, rig.State("run:cu:SCSKiller"));

        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Flip("SCSKiller");
        rig.Guard.Check(StartupGuard.CheckKind.Tick);   // still no evidence: left on, and Exo stops
        Assert.False(rig.IsOff(Hive.User, "Run", "SCSKiller"));
        Assert.Equal(GuardState.Marked, rig.State("run:cu:SCSKiller"));
        Assert.Contains(StartupEntries.Kept(rig.Host), item => item.Id == "run:cu:SCSKiller");
        Assert.Equal("Ask", rig.Setting("OnDriverUpdate"));   // Exo used no switch of the program's
        Assert.Equal(0, rig.Host.FileWrites.GetValueOrDefault(ScsSettings));
        Assert.Contains(rig.Log, line => line == "Startup: SCSKiller shader re-warm came back twice and Exo couldn't tell who turned it on; left on.");

        // Put back still works, and forgets it.
        Assert.Null(rig.Restore("run:cu:SCSKiller"));
        Assert.DoesNotContain(StartupEntries.Kept(rig.Host), item => item.Id == "run:cu:SCSKiller");
        Assert.Null(rig.Item("run:cu:SCSKiller"));
        // A third time was never fought, and the program's setting was never touched.
        Assert.Equal(0, rig.Host.FileWrites.GetValueOrDefault(ScsSettings));
    }

    [Fact]
    public void AComebackOlderThanThirtyDaysWithNoEvidenceCountsAsAFirstOne()
    {
        using var rig = new Rig();
        rig.AddAcme();
        rig.Remove("run:cu:Acme");
        rig.Flip();
        rig.Guard.Start();
        Assert.Equal(GuardState.Back, rig.State("run:cu:Acme"));

        rig.Host.Now = rig.Host.Now.AddDays(31);
        rig.Flip();
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.True(rig.IsOff(Hive.User, "Run", "Acme"));
        Assert.Equal(GuardState.Back, rig.State("run:cu:Acme"));
        Assert.Single(rig.Item("run:cu:Acme")!.Comebacks);
    }

    [Fact]
    public void ATaskMadeAgainEnabledIsDisabledOnTheNextTick()
    {
        using var rig = new Rig();
        rig.AddScsTask();
        Assert.Null(rig.Remove("task:" + ScsTask));
        Assert.False(rig.Host.TaskList[0].Enabled);

        rig.Host.ReCreateTask(ScsTask);
        Assert.True(rig.Host.TaskList[0].Enabled);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);

        Assert.False(rig.Host.TaskList[0].Enabled);
        Assert.Equal(GuardState.Back, rig.State("task:" + ScsTask));
        Assert.Contains(rig.Log, line => line.Contains("turned itself back on (scheduled task"));
    }

    [Fact]
    public void ATaskMadeAgainUnderANewNameIsMatchedByWhatItRunsAndDisabled()
    {
        using var rig = new Rig();
        rig.Host.AddTask(@"\Acme\Update {A}", @"C:\Program Files\Acme\update.exe", "/c  /silent");
        Assert.Null(rig.Remove(@"task:\Acme\Update {A}"));

        rig.Host.TaskList.Clear();
        rig.Host.AddTask(@"\Acme\Update {B}", @"C:\Program Files\Acme\update.exe", "/c /silent");
        rig.Guard.Check(StartupGuard.CheckKind.Tick);

        Assert.False(rig.Host.TaskList.Single().Enabled);
        Assert.Contains(rig.Log, line => line.Contains(@"made again as task:\Acme\Update {B}"));
    }

    [Fact]
    public void ARunValueUnderANewNameIsTheSameItem()
    {
        using var rig = new Rig();
        const string Edge = "\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --no-startup-window --win-session-start";
        rig.AddAcme(name: "MicrosoftEdgeAutoLaunch_AAAA", command: Edge);
        Assert.Null(rig.Remove("run:cu:MicrosoftEdgeAutoLaunch_AAAA"));

        rig.AddAcme(name: "MicrosoftEdgeAutoLaunch_BBBB", command: Edge);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);

        Assert.True(rig.IsOff(Hive.User, "Run", "MicrosoftEdgeAutoLaunch_BBBB"));
    }

    [Fact]
    public void ASecondComebackWithinThirtyDaysUsesTheRealSwitchOnceWithNoLoop()
    {
        using var rig = new Rig();
        rig.AddScsTask();
        rig.Remove("task:" + ScsTask);

        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal("Ask", rig.Setting("OnDriverUpdate"));
        Assert.Equal(GuardState.Back, rig.State("task:" + ScsTask));

        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal("Off", rig.Setting("OnDriverUpdate"));
        Assert.Equal("9", rig.Setting("Threads"));
        Assert.False(rig.Host.TaskList[0].Enabled);
        Assert.Equal(GuardState.Switched, rig.State("task:" + ScsTask));
        Assert.Equal(1, rig.Host.FileWrites[ScsSettings]);

        // Back a third time: marked, no second switch, and then Exo stops.
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal(GuardState.Marked, rig.State("task:" + ScsTask));
        Assert.Equal(1, rig.Host.FileWrites[ScsSettings]);

        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        var calls = rig.Host.Applied.Count;
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal(calls, rig.Host.Applied.Count);
        Assert.Equal(1, rig.Host.FileWrites[ScsSettings]);
        Assert.Equal(GuardState.Marked, rig.State("task:" + ScsTask));
        Assert.Single(rig.Log, line => line.Contains("has stopped turning it off") && line.StartsWith("Startup: SCSKiller"));
        Assert.Single(rig.Log, line => line.Contains("turned it off in its own settings"));
    }

    [Fact]
    public void AComebackOlderThanThirtyDaysIsNotARepeat()
    {
        using var rig = new Rig();
        rig.AddScsTask();
        rig.Remove("task:" + ScsTask);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        rig.Host.Now = rig.Host.Now.AddDays(31);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal("Ask", rig.Setting("OnDriverUpdate"));
        Assert.Equal(GuardState.Back, rig.State("task:" + ScsTask));
        Assert.Single(rig.Item("task:" + ScsTask)!.Comebacks);
    }

    [Fact]
    public void ARealSwitchWaitsWhileItsAppRunsAndIsUsedOnceAfterItCloses()
    {
        using var rig = new Rig();
        rig.AddScsTask();
        rig.Remove("task:" + ScsTask);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);

        rig.Host.Processes["SCSKiller"] = [new RunningProgram(@"C:\Users\Erix\AppData\Local\SCSKiller.App\current\SCSKiller.exe", rig.Host.Now)];
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.False(rig.Host.TaskList[0].Enabled);
        Assert.Equal("Ask", rig.Setting("OnDriverUpdate"));
        Assert.Equal(GuardState.Back, rig.State("task:" + ScsTask));
        Assert.True(rig.Item("task:" + ScsTask)!.SwitchPending);

        rig.Host.Processes.Clear();
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal("Off", rig.Setting("OnDriverUpdate"));
        Assert.Equal(GuardState.Switched, rig.State("task:" + ScsTask));
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal(1, rig.Host.FileWrites[ScsSettings]);
    }

    [Fact]
    public void AnUpdatersEntryIsKeptOffAndItsServiceIsLeftAlone()
    {
        using var rig = new Rig();
        rig.Host.AddTask(@"\GoogleUpdateTaskMachineCore", @"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe", "/c");
        rig.Host.AddService("gupdate", @"""C:\Program Files (x86)\Google\Update\GoogleUpdate.exe"" /svc", start: 2);
        rig.Remove(@"task:\GoogleUpdateTaskMachineCore");
        for (var i = 0; i < 3; i++)
        {
            rig.Host.Now = rig.Host.Now.AddDays(1);
            rig.Host.ReCreateTask(@"\GoogleUpdateTaskMachineCore");
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
            Assert.False(rig.Host.TaskList[0].Enabled);
        }
        Assert.Equal(GuardState.Updater, rig.State(@"task:\GoogleUpdateTaskMachineCore"));
        Assert.Equal(2, rig.Host.Get(Hive.Machine, StartupKeys.Services + @"\gupdate", "Start")?.Number);
        Assert.DoesNotContain(rig.Host.Applied.SelectMany(changes => changes), change => change is SetReg { Hive: Hive.Machine });
        var row = StartupEntries.Read(rig.Host).Single(entry => entry.Id == @"task:\GoogleUpdateTaskMachineCore");
        Assert.Equal("Started by its updater service", row.Status);
        // The service row is shown, and it is not Exo's to turn off.
        var service = StartupEntries.ReadAll(rig.Host).Single(entry => entry.Id == "service:gupdate");
        Assert.True(service.ReadOnly);
        Assert.Equal("Exo only shows this one.", rig.Remove("service:gupdate"));
    }

    [Fact]
    public void VgtrayStartedByItsAntiCheatShowsAsRunningAndNothingIsWrittenToVanguard()
    {
        using var rig = new Rig();
        const string Tray = "\"C:\\Program Files\\Riot Vanguard\\vgtray.exe\"";
        rig.Host.SetRun(Hive.Machine, StartupKeys.Run, "Riot Vanguard", Tray);
        rig.Host.AddService("vgc", @"""C:\Program Files\Riot Vanguard\vgc.exe""", start: 2);
        // The player may take the tray icon out of start-up (AGENTS.md); the driver and the service are never touched.
        Assert.Null(rig.Remove("run:lm:Riot Vanguard"));
        Assert.True(rig.IsOff(Hive.Machine, "Run", "Riot Vanguard"));
        var writes = rig.Host.Applied.Count;

        rig.Host.Processes["vgtray"] = [new RunningProgram(@"C:\Program Files\Riot Vanguard\vgtray.exe", rig.Host.Now)];
        rig.Guard.NotifyGame();
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.Set(Hive.Machine, StartupKeys.Run, "Riot Vanguard", RegValue.Str(Tray));   // Vanguard writes its Run value again
        rig.Host.Raise();
        rig.Guard.NotifyGame();

        Assert.Equal(writes, rig.Host.Applied.Count);
        Assert.Equal(GuardState.AntiCheat, rig.State("run:lm:Riot Vanguard"));
        var row = StartupEntries.Read(rig.Host).Single(entry => entry.Id == "run:lm:Riot Vanguard");
        Assert.True(row.Enabled);
        Assert.Equal("Starts with Riot Vanguard when you play", row.Status);
        var vgc = StartupEntries.ReadAll(rig.Host).Single(entry => entry.Id == "service:vgc");
        Assert.Equal(LockKind.AntiCheat, vgc.Lock);
        Assert.Equal(StartupLocks.Sentence(LockKind.AntiCheat), rig.Remove("service:vgc"));
        Assert.Equal(writes, rig.Host.Applied.Count);
    }

    [Fact]
    public void PutBackRestoresTheExactBytesValueTaskStateAndRealSwitchAndTheGuardStops()
    {
        using var rig = new Rig();
        byte[] before = [2, 0, 0, 0, 9, 8, 7, 6, 5, 4, 3, 2];
        rig.Host.SetRun(Hive.User, StartupKeys.Run, "Acme", Acme);
        rig.Host.Set(Hive.User, StartupKeys.Approved + "Run", "Acme", RegValue.Bin(before));
        rig.Host.SetRun(Hive.User, StartupKeys.Run, "Plain", "\"C:\\Plain\\plain.exe\"");   // no StartupApproved value at all
        rig.AddScsTask();
        rig.Remove("run:cu:Acme");
        rig.Remove("run:cu:Plain");
        rig.Remove("task:" + ScsTask);
        for (var i = 0; i < 2; i++)
        {
            rig.Host.Now = rig.Host.Now.AddDays(1);
            rig.Host.ReCreateTask(ScsTask);
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
        }
        Assert.Equal("Off", rig.Setting("OnDriverUpdate"));
        Assert.Equal(1, rig.Host.OpenWatches);

        Assert.Null(rig.Restore("run:cu:Acme"));
        Assert.Null(rig.Restore("run:cu:Plain"));
        Assert.Null(rig.Restore("task:" + ScsTask));

        Assert.Equal(before, rig.Host.Approval(Hive.User, "Run", "Acme"));
        Assert.Equal(Acme, rig.Host.Get(Hive.User, StartupKeys.Run, "Acme")?.Text);
        Assert.Null(rig.Host.Approval(Hive.User, "Run", "Plain"));
        Assert.True(rig.Host.TaskList[0].Enabled);
        Assert.Equal("Ask", rig.Setting("OnDriverUpdate"));
        Assert.Equal("9", rig.Setting("Threads"));
        Assert.Empty(StartupEntries.Kept(rig.Host));
        Assert.Empty(StartupGuard.Load(rig.Host).Items);
        // The guard stops watching, and a later change is nobody's business.
        Assert.Equal(0, rig.Host.OpenWatches);
        var calls = rig.Host.Applied.Count;
        rig.Host.Raise();
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal(calls, rig.Host.Applied.Count);
    }

    [Fact]
    public void ARealSwitchTheProgramOrPlayerChangedSinceIsNotPutBackOverTheirs()
    {
        using var rig = new Rig();
        rig.AddScsTask();
        rig.Remove("task:" + ScsTask);
        for (var i = 0; i < 2; i++)
        {
            rig.Host.Now = rig.Host.Now.AddDays(1);
            rig.Host.ReCreateTask(ScsTask);
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
        }
        rig.Host.WriteFile(ScsSettings, "{\"OnDriverUpdate\":\"WhenIdle\",\"Threads\":9}");
        Assert.Null(rig.Restore("task:" + ScsTask));
        Assert.Equal("WhenIdle", rig.Setting("OnDriverUpdate"));
    }

    [Fact]
    public void TheWatchIsOpenOnlyWhileSomethingIsRemovedAndDisposeClosesIt()
    {
        using var rig = new Rig();
        rig.AddAcme();
        rig.Guard.Start();
        Assert.Equal(0, rig.Host.OpenWatches);
        Assert.False(rig.Guard.Watching);

        rig.Remove("run:cu:Acme");
        Assert.Equal(1, rig.Host.OpenWatches);
        rig.Restore("run:cu:Acme");
        Assert.Equal(0, rig.Host.OpenWatches);
        rig.Remove("run:cu:Acme");
        Assert.Equal(1, rig.Host.OpenWatches);
        rig.Guard.Dispose();
        Assert.Equal(0, rig.Host.OpenWatches);
    }

    [Fact]
    public void APackagedAppsTaskTurnedOnInSettingsIsLeftOnAndByAnotherProgramIsTurnedOffAgain()
    {
        using var rig = new Rig();
        rig.Host.Packages.Add(new PackageTaskDecl("Acme.App_123", "StartTask", "Acme app", "Acme", "acme.exe", ManifestEnabled: true));
        const string Key = StartupKeys.Packaged + @"\Acme.App_123\StartTask";
        Assert.Null(rig.Remove("package:Acme.App_123!StartTask"));
        Assert.Equal(1, rig.Host.Get(Hive.User, Key, "State")?.Number);

        rig.Host.Set(Hive.User, Key, "State", RegValue.DWord(2));
        rig.Host.ForegroundProgram = "acme";
        rig.Host.Raise();
        Assert.Equal(1, rig.Host.Get(Hive.User, Key, "State")?.Number);

        rig.Host.Set(Hive.User, Key, "State", RegValue.DWord(2));
        rig.Host.ForegroundProgram = "systemsettings";
        rig.Host.Raise();
        Assert.Equal(2, rig.Host.Get(Hive.User, Key, "State")?.Number);
        Assert.Equal(GuardState.PlayerOn, rig.State("package:Acme.App_123!StartTask"));
    }

    [Fact]
    public void AStartupFolderShortcutIsMarkedOffInPlaceAndNamedByWhatItIs()
    {
        using var rig = new Rig();
        const string Shell = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        rig.Host.UserFolder.Add(new FolderFile("Claude Remote Control.lnk", Shell));
        rig.Host.Infos[Shell] = new ProgramInfo(true, "Microsoft Corporation", "Windows PowerShell 5.1");

        var entry = StartupEntries.ReadAll(rig.Host).Single();
        Assert.Equal("Claude Remote Control", entry.Name);
        Assert.Equal(LockKind.None, entry.Lock);   // PowerShell only starts what the shortcut says
        Assert.Null(rig.Remove("folder:cu:Claude Remote Control.lnk"));
        Assert.Single(rig.Host.UserFolder);   // the shortcut stays, as Task Manager leaves it
        Assert.True(rig.IsOff(Hive.User, "StartupFolder", "Claude Remote Control.lnk"));
        Assert.Equal("Claude Remote Control", StartupEntries.Kept(rig.Host).Single().Name);
        Assert.Null(rig.Restore("folder:cu:Claude Remote Control.lnk"));
        Assert.Null(rig.Host.Approval(Hive.User, "StartupFolder", "Claude Remote Control.lnk"));
    }

    [Fact]
    public void ARunOnceValueIsTakenOutAndKeptForPutBack()
    {
        using var rig = new Rig();
        rig.Host.SetRun(Hive.User, StartupKeys.RunOnce, "Setup", "\"C:\\Acme\\setup.exe\" /finish");
        Assert.Null(rig.Remove("runonce:cu:Setup"));
        Assert.Null(rig.Host.Get(Hive.User, StartupKeys.RunOnce, "Setup"));
        Assert.Null(rig.Restore("runonce:cu:Setup"));
        Assert.Equal("\"C:\\Acme\\setup.exe\" /finish", rig.Host.Get(Hive.User, StartupKeys.RunOnce, "Setup")?.Text);
    }

    [Fact]
    public void WhatOnlyShowsIsNotRemovable()
    {
        using var rig = new Rig();
        rig.Host.SetRun(Hive.User, StartupKeys.PolicyRun, "Corp", "\"C:\\Corp\\agent.exe\"");
        rig.Host.Set(Hive.Machine, StartupKeys.Winlogon, "Shell", RegValue.Str("explorer.exe,C:\\Other\\shell.exe"));
        Assert.Equal("Exo only shows this one.", rig.Remove("policy:cu:Corp"));
        Assert.Equal("Exo only shows this one.", rig.Remove("winlogon:lm:Shell"));
        Assert.Empty(rig.Host.Applied);
    }

    [Fact]
    public void AnOlderExosRecordReadsAndPutsBackAsBefore()
    {
        using var rig = new Rig();
        // A 3.25 record: the Run value was deleted, StartupApproved had no value, and nothing newer is in it.
        const string Old = "[{\"Id\":\"run:cu:Acme\",\"Name\":\"Acme\",\"At\":\"2026-11-01T00:00:00Z\",\"Value\":\"\\\"C:\\\\Acme\\\\acme.exe\\\" --tray\",\"Expand\":false,\"Kept\":null,\"State\":null,\"Approval\":null,\"WasOn\":true}]";
        rig.Host.WriteFile(Path.Combine(rig.Host.DataFolder, "startup-removed.json"), Old);
        rig.Host.Set(Hive.User, StartupKeys.Approved + "Run", "Acme", RegValue.Bin(FakeStartupHost.Off(rig.Host.Now)));

        Assert.Equal("Acme", StartupEntries.Kept(rig.Host).Single().Name);
        Assert.Null(rig.Restore("run:cu:Acme"));
        Assert.Equal(Acme, rig.Host.Get(Hive.User, StartupKeys.Run, "Acme")?.Text);
        Assert.Null(rig.Host.Approval(Hive.User, "Run", "Acme"));
        Assert.Empty(StartupEntries.Kept(rig.Host));
    }

    [Fact]
    public void CleanUpHandsEverythingOverInOneRequest()
    {
        using var rig = new Rig();
        rig.Host.SetRun(Hive.User, StartupKeys.Run, "Bitwarden", "\"C:\\Program Files\\Bitwarden\\Bitwarden.exe\"");
        rig.Host.SetRun(Hive.Machine, StartupKeys.Run, "BraveSoftware Update", "\"C:\\Program Files (x86)\\BraveSoftware\\Update\\BraveUpdate.exe\" /c");
        rig.Host.AddTask(@"\EqualizerAPOUpdateChecker", @"C:\Program Files\EqualizerAPO\UpdateChecker.exe");
        var (removed, error) = StartupEntries.Clean(rig.Host);
        Assert.Null(error);
        Assert.Equal(3, removed);
        Assert.Single(rig.Host.Applied);
        Assert.Equal(1, rig.Host.ElevatedRequests);
        Assert.Equal(3, StartupEntries.Kept(rig.Host).Count);
        Assert.False(rig.Host.TaskList[0].Enabled);
    }

    [Fact]
    public void AfterCleanUpTheWatchIsOpenAndAReAddIsPutOffAgain()
    {
        using var rig = new Rig();
        rig.Host.SetRun(Hive.User, StartupKeys.Run, "Bitwarden", "\"C:\\Program Files\\Bitwarden\\Bitwarden.exe\"");
        rig.Host.Set(Hive.User, StartupKeys.Approved + "Run", "Bitwarden", RegValue.Bin(FakeStartupHost.On()));
        rig.Host.SetRun(Hive.User, StartupKeys.Run, "Spotify", "\"C:\\Users\\Erix\\AppData\\Roaming\\Spotify\\Spotify.exe\"");
        rig.Host.Set(Hive.User, StartupKeys.Approved + "Run", "Spotify", RegValue.Bin(FakeStartupHost.On()));

        var (removed, error) = StartupEntries.Clean(rig.Host);
        Assert.Null(error);
        Assert.Equal(2, removed);
        Assert.Equal(1, rig.Host.OpenWatches);

        // A program writes one item's Run value again: the open watch says so, and the item is put off again.
        rig.Host.Now = rig.Host.Now.AddMinutes(5);
        rig.Flip("Spotify");
        rig.Host.ForegroundProgram = "spotify";
        rig.Host.Raise();

        Assert.True(rig.IsOff(Hive.User, "Run", "Spotify"));
        Assert.Equal(GuardState.Back, rig.State("run:cu:Spotify"));
        Assert.Equal(1, rig.Host.OpenWatches);
    }

    [Fact]
    public void TheSameCommandInTheMachinesListIsNotTheItemThePlayerTurnedOff()
    {
        using var rig = new Rig();
        rig.AddAcme(Hive.User);       // the player's own
        rig.AddAcme(Hive.Machine);    // the same command, for everyone on this PC
        Assert.Null(rig.Remove("run:cu:Acme"));
        var asks = rig.Host.ElevatedRequests;

        rig.Host.Now = rig.Host.Now.AddMinutes(5);
        rig.Host.ForegroundProgram = "acme";
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        rig.Guard.Check(StartupGuard.CheckKind.Start);

        // Windows' machine-wide Run value is not Exo's to touch, and nobody was asked.
        Assert.Equal(asks, rig.Host.ElevatedRequests);
        Assert.True(rig.IsOff(Hive.User, "Run", "Acme"));
        Assert.False(rig.IsOff(Hive.Machine, "Run", "Acme"));
        Assert.Equal(GuardState.Off, rig.State("run:cu:Acme"));
        Assert.DoesNotContain(rig.Log, line => line.Contains("turned itself back on"));
    }

    [Fact]
    public void ARenamedEntryThatIsLockedIsLeftAlone()
    {
        using var rig = new Rig();
        rig.Host.AddTask(@"\Acme\Warm {A}", @"C:\Program Files\Acme\warm.exe", "--run");
        Assert.Null(rig.Remove(@"task:\Acme\Warm {A}"));

        // The same command under a name Exo never turns off ("Exo" and "Shaders" in it: Exo's own add-on).
        rig.Host.TaskList.Clear();
        rig.Host.AddTask(@"\ExoShaders\Exo Shaders Warm {B}", @"C:\Program Files\Acme\warm.exe", "--run");
        rig.Guard.Check(StartupGuard.CheckKind.Tick);

        Assert.True(rig.Host.TaskList.Single().Enabled);
        Assert.DoesNotContain(rig.Log, line => line.Contains("made again"));
    }

    [Fact]
    public void AtMostEightNamesOfARenamedItemAreKeptAndNothingIsTurnedOn()
    {
        using var rig = new Rig();
        const string Program = "\"C:\\Program Files\\Acme\\acme.exe\" --run";
        // The item Exo removed, and the first name it was made again under: still there, and off because Exo turned it off.
        Seed(rig.Host, new StartupEntries.Removal("run:cu:Acme_0000", "Acme", rig.Host.Now, Value: Program, WasOn: true) { Fingerprint = Program });
        rig.Host.SetRun(Hive.User, StartupKeys.Run, "Acme_v1", Program);
        rig.Host.Set(Hive.User, StartupKeys.Approved + "Run", "Acme_v1", RegValue.Bin(FakeStartupHost.Off(rig.Host.Now)));

        StartupEntries.NoteRenamed(rig.Host, "run:cu:Acme_0000",
            new StartupEntries.Also("run:cu:Acme_v1", "Acme", Program, Approval: FakeStartupHost.On()));
        for (var i = 2; i <= 9; i++)
            StartupEntries.NoteRenamed(rig.Host, "run:cu:Acme_0000", new StartupEntries.Also("run:cu:Acme_v" + i, "Acme", Program, Approval: FakeStartupHost.On()));

        // Eight names are kept; the oldest went, and nothing was turned on for it.
        var renamed = StartupEntries.Kept(rig.Host).Single().Renamed!;
        Assert.Equal(StartupEntries.RenamedCap, renamed.Count);
        Assert.Equal(8, renamed.Count);
        Assert.Equal(renamed.Select(one => one.Id).Order().ToArray(),
            new[] { 2, 3, 4, 5, 6, 7, 8, 9 }.Select(i => "run:cu:Acme_v" + i).Order().ToArray());
        Assert.Equal(FakeStartupHost.Off(rig.Host.Now), rig.Host.Approval(Hive.User, "Run", "Acme_v1"));
        Assert.Empty(rig.Host.Applied);

        // A name dropped from the list writes nothing, whether it is still there or gone.
        var calls = rig.Host.Applied.Count;
        StartupEntries.NoteRenamed(rig.Host, "run:cu:Acme_0000", new StartupEntries.Also("run:cu:Acme_gone", "Acme", Program, Approval: FakeStartupHost.On()));
        Assert.Equal(calls, rig.Host.Applied.Count);
        Assert.Equal(StartupEntries.RenamedCap, StartupEntries.Kept(rig.Host).Single().Renamed!.Count);
        Assert.Null(rig.Host.Approval(Hive.User, "Run", "Acme_v2"));   // made on, never touched: nothing turned it on
    }

    [Fact]
    public void RemovingATaskThatIsNoLongerThereIsNotReportedAsDone()
    {
        using var rig = new Rig();
        rig.Host.AddTask(@"\Acme\Update", @"C:\Program Files\Acme\update.exe", "/c");
        var changes = StartupEntries.OffChanges(rig.Host, @"task:\Acme\Update")!;
        Assert.NotEmpty(changes);

        rig.Host.TaskList.Clear();   // it went before the change could be made

        var bad = StartupEntries.Unverified(rig.Host, changes);
        Assert.NotNull(bad);
        Assert.IsType<SetTask>(bad);
    }

    [Fact]
    public void AChangeThatKeepsFailingIsLoggedOnceAndTheRecordIsNotRewritten()
    {
        using var rig = new Rig();
        rig.AddAcme();
        Assert.Null(rig.Remove("run:cu:Acme"));
        var record = Path.Combine(rig.Host.DataFolder, "startup-guard.json");
        rig.Host.FileWrites.Clear();

        for (var i = 0; i < 3; i++)
        {
            // Its program flips the approval back on, and again the moment the guard's off-change is made: it never holds.
            rig.Host.Now = rig.Host.Now.AddMinutes(10);
            rig.Flip();
            rig.Host.AfterApply = () => { rig.Host.AfterApply = null; rig.Flip(); };
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
            rig.Host.AfterApply = null;
        }

        // Said once, not once per check, and nothing in the record changed: its file is not written again.
        Assert.True(rig.Log.Any(line => line.Contains("was not put off", StringComparison.Ordinal)), string.Join(" | ", rig.Log));
        Assert.Equal(0, rig.Host.FileWrites.GetValueOrDefault(record));
        Assert.Equal(0, rig.Host.FileWrites.GetValueOrDefault(record + ".bak"));
        Assert.Equal(GuardState.Off, rig.State("run:cu:Acme"));
    }

    [Fact]
    public async Task ThePlayersPressWaitsOnlyForTheGuardsOwnShortWrite()
    {
        using var rig = new Rig();
        rig.AddAcme(name: "Alpha", command: "\"C:\\Alpha\\alpha.exe\"");
        rig.AddAcme(name: "Beta", command: "\"C:\\Beta\\beta.exe\"");
        Assert.Null(rig.Remove("run:cu:Alpha"));
        string? refused = null;
        Task? press = null;
        rig.Host.DuringApply = () =>
        {
            // The guard holds its lock for a write that never asks Windows anything: the player's Remove waits for that
            // short write and then goes through, and it is recorded.
            rig.Host.DuringApply = null;
            press = Task.Run(() => { refused = rig.Remove("run:cu:Beta"); });
            // It does not go through while the guard's own write is under way: it waits for it, not for Windows.
            Assert.False(SpinWait.SpinUntil(() => press.IsCompleted, 300), "the player's press did not wait for the guard's write");
        };

        rig.Host.Now = rig.Host.Now.AddMinutes(5);
        rig.Flip("Alpha");
        rig.Host.ForegroundProgram = "alpha";
        rig.Host.Raise();

        await press!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Null(refused);
        var kept = StartupEntries.Kept(rig.Host).Select(item => item.Id).Order().ToArray();
        Assert.Equal(["run:cu:Alpha", "run:cu:Beta"], kept);
        Assert.Equal(GuardState.Off, rig.State("run:cu:Beta"));
        Assert.Equal(GuardState.Back, rig.State("run:cu:Alpha"));
        Assert.True(rig.IsOff(Hive.User, "Run", "Beta"));
        // Nothing the player did was undone: the guard did not reverse the off-change it had made for Alpha.
        Assert.True(rig.IsOff(Hive.User, "Run", "Alpha"));
    }

    [Fact]
    public void TheGuardNeverRaisesAWindowsPrompt()
    {
        // Every comeback scenario there is for a machine-wide item, each check reading Windows' permission as never asked.
        using (var rig = new Rig())
        {
            // A visible Run value found back on with no evidence.
            rig.AddAcme(Hive.Machine, "Alpha", "\"C:\\Alpha\\alpha.exe\"");
            Assert.Null(rig.Remove("run:lm:Alpha"));
            var asks = rig.Host.ElevatedRequests;
            rig.Flip("Alpha", Hive.Machine);
            rig.Host.ForegroundProgram = "alpha";
            rig.Guard.Start();
            Assert.Equal(GuardState.NeedsOk, rig.State("run:lm:Alpha"));
            Assert.Equal(asks, rig.Host.ElevatedRequests);
        }
        using (var rig = new Rig())
        {
            // Found back on twice, and a third time.
            rig.AddAcme(Hive.Machine, "Alpha", "\"C:\\Alpha\\alpha.exe\"");
            Assert.Null(rig.Remove("run:lm:Alpha"));
            var asks = rig.Host.ElevatedRequests;
            for (var i = 0; i < 3; i++)
            {
                rig.Host.Now = rig.Host.Now.AddDays(1);
                rig.Flip("Alpha", Hive.Machine);
                rig.Host.ForegroundProgram = "alpha";
                rig.Guard.Check(StartupGuard.CheckKind.Tick);
            }
            Assert.Equal(asks, rig.Host.ElevatedRequests);
        }
        using (var rig = new Rig())
        {
            // Seen live with Task Manager in front: the player's, left on.
            rig.AddAcme(Hive.Machine, "Alpha", "\"C:\\Alpha\\alpha.exe\"");
            Assert.Null(rig.Remove("run:lm:Alpha"));
            var asks = rig.Host.ElevatedRequests;
            rig.Flip("Alpha", Hive.Machine);
            rig.Host.ForegroundProgram = "taskmgr";
            rig.Host.Raise();
            Assert.Equal(GuardState.PlayerOn, rig.State("run:lm:Alpha"));
            Assert.Equal(asks, rig.Host.ElevatedRequests);
        }
        using (var rig = new Rig())
        {
            // A machine-wide scheduled task made again: a task's own change may need Windows' permission to make, but
            // disabling it again is the program's (or in this case a driver's helper): Exo asks nothing to find out.
            rig.Host.AddTask(@"\Acme\Update", @"C:\Program Files\Acme\update.exe", "/c");
            Assert.Null(rig.Remove(@"task:\Acme\Update"));
            var asks = rig.Host.ElevatedRequests;
            rig.Host.ReCreateTask(@"\Acme\Update");
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
            Assert.Equal(asks, rig.Host.ElevatedRequests);
        }
        using (var rig = new Rig())
        {
            // What an older Exo removed machine-wide and Exo never turns off: left under Removed, never put back.
            rig.Host.SetRun(Hive.Machine, StartupKeys.Run, "NvContainer", "\"C:\\Program Files\\NVIDIA Corporation\\NvContainer\\nvcontainer.exe\"");
            Seed(rig.Host, Old("run:lm:NvContainer", "NVIDIA container", "\"C:\\Program Files\\NVIDIA Corporation\\NvContainer\\nvcontainer.exe\""));
            var asks = rig.Host.ElevatedRequests;
            rig.Guard.Start();
            Assert.Equal(asks, rig.Host.ElevatedRequests);
            Assert.NotNull(rig.Host.Get(Hive.Machine, StartupKeys.Run, "NvContainer"));   // still removed
            Assert.Contains(rig.Log, line => line.Contains("stays removed; putting it back needs your OK"));
        }
    }

    [Fact]
    public void AMachineWideItemThatCameBackWaitsForThePlayersOk()
    {
        using var rig = new Rig();
        rig.AddAcme(Hive.Machine, "Alpha", "\"C:\\Alpha\\alpha.exe\"");
        Assert.Null(rig.Remove("run:lm:Alpha"));
        var calls = rig.Host.Applied.Count;

        for (var i = 0; i < 3; i++)
        {
            rig.Host.Now = rig.Host.Now.AddDays(1);
            rig.Flip("Alpha", Hive.Machine);
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
        }

        // Left on, in NeedsOk, nothing applied, no comeback counted, and said once over the three checks.
        Assert.False(rig.IsOff(Hive.Machine, "Run", "Alpha"));
        var item = rig.Item("run:lm:Alpha")!;
        Assert.Equal(GuardState.NeedsOk, item.State);
        Assert.Empty(item.Comebacks);
        Assert.Equal(calls, rig.Host.Applied.Count);
        Assert.Equal("It came back, and turning it off again needs your OK", StartupGuard.StatusOf(item, null));
        Assert.Single(rig.Log, line => line == "Startup: Alpha came back; it is machine-wide, so Exo left it for you to turn it off again.");
    }

    [Fact]
    public void PressingRemoveAgainOnAnItemThatCameBackTurnsItOff()
    {
        using var rig = new Rig();
        rig.AddAcme(Hive.Machine, "Alpha", "\"C:\\Alpha\\alpha.exe\"");
        Assert.Null(rig.Remove("run:lm:Alpha"));
        var asks = rig.Host.ElevatedRequests;

        rig.Flip("Alpha", Hive.Machine);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal(GuardState.NeedsOk, rig.State("run:lm:Alpha"));
        Assert.False(rig.IsOff(Hive.Machine, "Run", "Alpha"));

        // The player presses Remove again on the row: that press is the OK, and it may ask Windows.
        Assert.Null(rig.Remove("run:lm:Alpha"));
        Assert.True(rig.IsOff(Hive.Machine, "Run", "Alpha"));
        Assert.Equal(asks + 1, rig.Host.ElevatedRequests);   // one press, one request
        Assert.Equal(GuardState.Off, rig.State("run:lm:Alpha"));
    }

    // ---- a task only an administrator can change (step B-15) ----

    private const string AdminTask = @"\Acme\UpdaterTask";

    /// <summary>An updater's task an installer created with administrator rights: `schtasks /change` as the user fails for it.</summary>
    private static void AddAdminTask(Rig rig, bool enabled = true) =>
        rig.Host.AddTask(AdminTask, @"C:\Program Files\Acme\acme-updater.exe", "/c", enabled: enabled);

    [Fact]
    public void ATaskOnlyAnAdministratorCanChangeWaitsForThePlayersOk()
    {
        using var rig = new Rig();
        AddAdminTask(rig);
        rig.Host.AdminTasks.Add(AdminTask);
        Assert.Null(rig.Remove("task:" + AdminTask));
        Assert.False(rig.Host.TaskList.Single().Enabled);
        var asks = rig.Host.ElevatedRequests;

        // Its program turns it on again, and three checks find it so.
        for (var i = 0; i < 3; i++)
        {
            rig.Host.Now = rig.Host.Now.AddMinutes(10);
            rig.Host.ReCreateTask(AdminTask);
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
        }

        var item = rig.Item("task:" + AdminTask)!;
        Assert.Equal(GuardState.NeedsOk, item.State);
        Assert.Empty(item.Comebacks);
        Assert.Equal(asks, rig.Host.ElevatedRequests);   // the guard asked Windows for nothing
        Assert.True(rig.Host.TaskList.Single().Enabled);   // and left it as the player's to turn off
        Assert.Single(rig.Log, line => line == "Startup: UpdaterTask came back; it is machine-wide, so Exo left it for you to turn it off again.");
        Assert.Equal("It came back, and turning it off again needs your OK", StartupGuard.StatusOf(item, null));
    }

    [Fact]
    public void PressingRemoveAgainOnSuchATaskTurnsItOffWithOnePrompt()
    {
        using var rig = new Rig();
        AddAdminTask(rig);
        rig.Host.AdminTasks.Add(AdminTask);
        Assert.Null(rig.Remove("task:" + AdminTask));
        var asks = rig.Host.ElevatedRequests;

        rig.Host.ReCreateTask(AdminTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal(GuardState.NeedsOk, rig.State("task:" + AdminTask));
        Assert.True(rig.Host.TaskList.Single().Enabled);

        // The player presses Remove again on the row: that press is the OK, and it may ask Windows.
        Assert.Null(rig.Remove("task:" + AdminTask));
        Assert.False(rig.Host.TaskList.Single().Enabled);
        Assert.Equal(asks + 1, rig.Host.ElevatedRequests);   // one press, one request
        Assert.Equal(GuardState.Off, rig.State("task:" + AdminTask));
    }

    [Fact]
    public void AnOlderExosRemovalOfSuchATaskIsLeftUnderRemovedAndTriedOnce()
    {
        using var rig = new Rig();
        // An Edge updater's task an installer created with administrator rights: one Exo never turns off, so an older Exo's
        // removal of it is put back — and Windows won't let the user put it back without its permission.
        const string Edge = @"\MicrosoftEdgeUpdateTaskMachineCore{0C6E}";
        const string EdgeName = "MicrosoftEdgeUpdateTaskMachineCore{0C6E}";
        rig.Host.AddTask(Edge, @"C:\Program Files (x86)\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe", "/c", enabled: false);
        rig.Host.AdminTasks.Add(Edge);
        Seed(rig.Host, Old("task:" + Edge, EdgeName));

        rig.Guard.Start();
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        rig.Guard.Check(StartupGuard.CheckKind.Game);

        // Left under Removed: nothing is put back, said once, and not tried again at every check.
        Assert.False(rig.Host.TaskList.Single().Enabled);
        Assert.Single(StartupEntries.Kept(rig.Host));
        Assert.Single(rig.Log, line => line == $"Startup: {EdgeName} stays under Removed; putting it back needs your OK.");
        Assert.True(rig.Host.Applied.Count <= 1, rig.Host.Applied.Count.ToString());   // handed over once, then handled
        Assert.Equal(0, rig.Host.ElevatedRequests);
        Assert.True(rig.Item("task:" + Edge)!.LockedHandled);
    }

    [Fact]
    public void ACheckAsksForTheTaskListAtMostTwice()
    {
        using var rig = new Rig();
        rig.AddScsTask();
        rig.Host.AddTask(@"\Acme\Sync", @"C:\Program Files\Acme\acme-sync.exe", "/c");
        Assert.Null(rig.Remove("task:" + ScsTask));
        Assert.Null(rig.Remove(@"task:\Acme\Sync"));
        // A third, removed by an older Exo although Exo never turns it off: put back at this check.
        const string Edge = @"\MicrosoftEdgeUpdateTaskMachineCore{0C6E}";
        rig.Host.AddTask(Edge, @"C:\Program Files (x86)\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe", "/c", enabled: false);
        Seed(rig.Host, [.. StartupEntries.Kept(rig.Host), Old("task:" + Edge, "MicrosoftEdgeUpdateTaskMachineCore{0C6E}")]);
        Assert.Equal(3, StartupEntries.Kept(rig.Host).Count);

        // A check with three removed tasks, one of which came back: one snapshot at its start, and at most one read more to
        // see what its own changes came to. Each read is bounded to five seconds (the guard holds the player's press off).
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Host.ResetTaskReads();
        rig.Guard.Check(StartupGuard.CheckKind.Tick);

        Assert.False(rig.Host.TaskList.Single(task => task.Path == ScsTask).Enabled);
        Assert.True(rig.Host.TaskList.Single(task => task.Path == Edge).Enabled);   // the older removal was put back
        Assert.InRange(rig.Host.TaskReads, 1, 2);
        Assert.All(rig.Host.TaskWaits, wait => Assert.Equal(TimeSpan.FromSeconds(5), wait));
    }

    [Fact]
    public void AMachineWideRunValueSeenInTaskManagerIsThePlayersAndLeftOn()
    {
        using var rig = new Rig();
        rig.AddAcme(Hive.Machine, "Alpha", "\"C:\\Alpha\\alpha.exe\"");
        Assert.Null(rig.Remove("run:lm:Alpha"));

        rig.Flip("Alpha", Hive.Machine);
        rig.Host.ForegroundProgram = "taskmgr";
        rig.Host.Raise();

        Assert.False(rig.IsOff(Hive.Machine, "Run", "Alpha"));
        Assert.Equal(GuardState.PlayerOn, rig.State("run:lm:Alpha"));
    }

    [Fact]
    public void PutBackAfterARenameRestoresTheEntryTheProgramUsesNow()
    {
        using var rig = new Rig();
        rig.Host.AddTask(@"\Acme\Update {A}", @"C:\Program Files\Acme\update.exe", "/c");
        Assert.Null(rig.Remove(@"task:\Acme\Update {A}"));

        rig.Host.TaskList.Clear();
        rig.Host.AddTask(@"\Acme\Update {B}", @"C:\Program Files\Acme\update.exe", "/c");
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.False(rig.Host.TaskList.Single().Enabled);

        // Put back restores the entry the program uses now, not only the old one Exo remembers.
        Assert.Null(rig.Restore(@"task:\Acme\Update {A}"));
        Assert.True(rig.Host.TaskList.Single().Enabled);
        Assert.Empty(StartupEntries.Kept(rig.Host));
        Assert.Null(rig.Item(@"task:\Acme\Update {A}"));
    }

    // ---- what must never be written before the change it belongs to held (step B-11) ----

    [Fact]
    public void ASwitchIsNotWrittenWhenItsStartupChangeDidNotHold()
    {
        using var rig = new Rig();
        rig.AddScsTask();
        var before = rig.Host.ReadFile(ScsSettings)!;
        Assert.Null(rig.Remove("task:" + ScsTask));
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);   // first comeback: off again
        Assert.Equal(GuardState.Back, rig.State("task:" + ScsTask));
        rig.Host.FileWrites.Clear();

        // The second comeback. The task goes before the off-change is made, so the change cannot hold.
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Host.DuringApply = () => rig.Host.TaskList.Clear();
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        rig.Host.DuringApply = null;

        // The program's own settings file is byte-identical to before, and the record says nothing of a switch.
        Assert.Equal(before, rig.Host.ReadFile(ScsSettings));
        Assert.Equal(0, rig.Host.FileWrites.GetValueOrDefault(ScsSettings));
        var item = rig.Item("task:" + ScsTask)!;
        Assert.Null(item.SwitchFile);
        Assert.Equal(GuardState.Back, item.State);   // not advanced to Switched
        Assert.Contains(rig.Log, line => line.Contains("did not hold"));
        Assert.DoesNotContain(rig.Log, line => line.Contains("own settings"));
    }

    [Fact]
    public void ASwitchThatWasWrittenCanAlwaysBeUndoneByPutBack()
    {
        using var rig = new Rig();
        rig.AddScsTask();
        var before = rig.Host.ReadFile(ScsSettings)!;
        Assert.Null(rig.Remove("task:" + ScsTask));
        for (var i = 0; i < 2; i++)
        {
            rig.Host.Now = rig.Host.Now.AddDays(1);
            rig.Host.ReCreateTask(ScsTask);
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
        }

        Assert.Equal("Off", rig.Setting("OnDriverUpdate"));
        var item = rig.Item("task:" + ScsTask)!;
        Assert.Equal(GuardState.Switched, item.State);
        // The record that says Exo set this switch is saved in the same commit that wrote it.
        Assert.NotNull(item.SwitchFile);
        Assert.NotNull(item.SwitchSet);

        Assert.Null(rig.Restore("task:" + ScsTask));
        Assert.Equal(before, rig.Host.ReadFile(ScsSettings));
    }

    [Fact]
    public void TwoChecksAtOnceCountOneComeback()
    {
        using var rig = new Rig();
        rig.AddAcme();
        Assert.Null(rig.Remove("run:cu:Acme"));
        var calls = rig.Host.Applied.Count;
        rig.Host.Now = rig.Host.Now.AddMinutes(5);
        rig.Flip();
        rig.Host.ForegroundProgram = "acme";
        // The 10-minute tick arrives while the guard is making its off-change.
        rig.Host.DuringApply = () =>
        {
            rig.Host.DuringApply = null;
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
        };
        rig.Host.Raise();

        Assert.Single(rig.Item("run:cu:Acme")!.Comebacks);
        Assert.Equal(calls + 1, rig.Host.Applied.Count);   // one Apply call for that change
        Assert.True(rig.IsOff(Hive.User, "Run", "Acme"));
        Assert.Equal(GuardState.Back, rig.State("run:cu:Acme"));
    }

    // ---- Put back after a rename: the entry's own before-state, taken before anything is applied (step B-11) ----

    [Fact]
    public void PutBackAfterARenameRestoresTheEntryTheProgramUsesNow_RunValue()
    {
        using var rig = new Rig();
        const string Program = "\"C:\\Program Files\\Acme\\acme.exe\" --run";
        rig.AddAcme(name: "Acme_AAAA", command: Program);
        Assert.Null(rig.Remove("run:cu:Acme_AAAA"));

        rig.AddAcme(name: "Acme_BBBB", command: Program);   // made again under a new name
        rig.Host.ForegroundProgram = "acme";
        rig.Host.Raise();
        Assert.True(rig.IsOff(Hive.User, "Run", "Acme_BBBB"), string.Join(" | ", rig.Log));

        Assert.Null(rig.Restore("run:cu:Acme_AAAA"));
        // On, with its exact original bytes: Exo's own "off" is not what Put back restores.
        Assert.Equal(FakeStartupHost.On(), rig.Host.Approval(Hive.User, "Run", "Acme_BBBB"));
        Assert.Equal(Program, rig.Host.Get(Hive.User, StartupKeys.Run, "Acme_BBBB")?.Text);
    }

    [Fact]
    public void PutBackAfterARenameRestoresTheEntryTheProgramUsesNow_StartupFolder()
    {
        using var rig = new Rig();
        const string Before = "Acme Remote Control.lnk";
        var command = "\"C:\\Program Files\\Acme\\acme.exe\" --tray";
        rig.Host.UserFolder.Add(new FolderFile(Before, command));
        Assert.Null(rig.Remove("folder:cu:" + Before));

        rig.Host.UserFolder.Clear();
        rig.Host.UserFolder.Add(new FolderFile("Acme Remote Control (2).lnk", command));
        rig.Host.Set(Hive.User, StartupKeys.Approved + "StartupFolder", "Acme Remote Control", RegValue.Bin(FakeStartupHost.Off(rig.Host.Now)));
        rig.Host.Set(Hive.User, StartupKeys.Approved + "StartupFolder", "Acme Remote Control (2).lnk", RegValue.Bin(FakeStartupHost.On()));
        rig.Host.ForegroundProgram = "acme";
        rig.Host.Raise();
        Assert.True(rig.IsOff(Hive.User, "StartupFolder", "Acme Remote Control (2).lnk"), string.Join(" | ", rig.Log));
        Assert.True(rig.IsOff(Hive.User, "StartupFolder", Before));

        Assert.Null(rig.Restore("folder:cu:" + Before));
        Assert.Equal(FakeStartupHost.On(), rig.Host.Approval(Hive.User, "StartupFolder", "Acme Remote Control (2).lnk"));
    }

    [Fact]
    public void PutBackAfterARenameRestoresTheEntryTheProgramUsesNow_PackagedApp()
    {
        using var rig = new Rig();
        rig.Host.Packages.Add(new PackageTaskDecl("Acme.App_123", "StartTask2", "Acme app", "Acme", "acme.exe", ManifestEnabled: true));
        var second = StartupKeys.Packaged + @"\Acme.App_123\StartTask2";
        rig.Host.Set(Hive.User, second, "State", RegValue.DWord(2));
        // Its startup task made again under a new id (a Store task's fingerprint carries its id, so the harness seeds the
        // match): the removal's own id is gone and this one is on.
        Seed(rig.Host, new StartupEntries.Removal("package:Acme.App_123!StartTask", "Acme app", rig.Host.Now, State: 2, WasOn: true)
            { Fingerprint = "package:acme.app_123!starttask2", Program = @"C:\Program Files\Acme\acme.exe" });

        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Single(rig.Log, line => line.Contains("made again as package:Acme.App_123!StartTask2"));
        Assert.Equal(1, rig.Host.Get(Hive.User, second, "State")?.Number);
        Assert.Equal(GuardState.Back, rig.State("package:Acme.App_123!StartTask"));

        Assert.Null(rig.Restore("package:Acme.App_123!StartTask"));
        // Back as it was, which is "on": Exo's own 1 is not what Put back restores.
        Assert.Equal(2, rig.Host.Get(Hive.User, second, "State")?.Number);
    }

    [Fact]
    public void AnUnknownComebackOfASwitchedItemIsLoggedOnceAndNotCounted()
    {
        using var rig = new Rig();
        rig.Host.SetRun(Hive.User, StartupKeys.Run, "SCSKiller", "\"" + Scs + "\" --tray");
        rig.Host.Set(Hive.User, StartupKeys.Approved + "Run", "SCSKiller", RegValue.Bin(FakeStartupHost.On()));
        rig.Host.WriteFile(ScsSettings, "{\"OnDriverUpdate\":\"Ask\",\"Threads\":9}");
        Assert.Null(rig.Remove("run:cu:SCSKiller"));
        for (var i = 0; i < 2; i++)
        {
            rig.Host.Now = rig.Host.Now.AddDays(1);
            rig.Flip("SCSKiller");
            rig.Host.ForegroundProgram = "scskillerw";
            rig.Host.Raise();
        }
        Assert.Equal(GuardState.Switched, rig.State("run:cu:SCSKiller"));
        Assert.True(rig.IsOff(Hive.User, "Run", "SCSKiller"));
        var comebacks = rig.Item("run:cu:SCSKiller")!.Comebacks.Count;

        // Found back on with no evidence of who put it there (nothing was seen when it changed): left in its state,
        // nothing applied, no comeback counted, and said once.
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ForegroundProgram = "explorer";
        var calls = rig.Host.Applied.Count;
        var writes = rig.Host.FileWrites.GetValueOrDefault(ScsSettings);
        rig.Guard.Start();
        rig.Guard.Check(StartupGuard.CheckKind.Tick);

        Assert.Equal(calls, rig.Host.Applied.Count);
        Assert.Equal(writes, rig.Host.FileWrites.GetValueOrDefault(ScsSettings));
        Assert.Equal(GuardState.Switched, rig.State("run:cu:SCSKiller"));
        Assert.Equal(comebacks, rig.Item("run:cu:SCSKiller")!.Comebacks.Count);
        Assert.Single(rig.Log, line => line.Contains("is in startup again", StringComparison.Ordinal));

        // Said once: a second check with still no evidence says nothing more.
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Single(rig.Log, line => line.Contains("is in startup again", StringComparison.Ordinal));
        Assert.Equal(comebacks, rig.Item("run:cu:SCSKiller")!.Comebacks.Count);
        Assert.Equal(calls, rig.Host.Applied.Count);
    }

    [Fact]
    public void ADamagedRecordFallsBackToTheLastGoodOne()
    {
        using var rig = new Rig();
        rig.AddAcme();
        rig.Remove("run:cu:Acme");
        var record = Path.Combine(rig.Host.DataFolder, "startup-guard.json");
        Assert.NotNull(rig.Host.ReadFile(record + ".bak"));

        rig.Host.WriteFile(record, "{ this is not the record");   // a half-written file
        Assert.Equal(GuardState.Off, rig.State("run:cu:Acme"));
        Assert.NotNull(rig.Host.ReadFile(record + ".unreadable"));
    }

    [Fact]
    public void ARecordThatCannotBeReadIsNotReplaced()
    {
        using var rig = new Rig();
        rig.AddAcme();
        rig.Remove("run:cu:Acme");
        var record = Path.Combine(rig.Host.DataFolder, "startup-guard.json");
        var good = rig.Host.ReadFile(record)!;
        // A file whose read throws: the guard can't see it, so it must neither move it aside nor save over it.
        rig.Host.Unreadable.Add(record);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);   // reads the record, cannot, and must not save over it

        var read = StartupGuard.Load(rig.Host);
        Assert.Empty(read.Items);
        rig.Host.Unreadable.Clear();
        Assert.Equal(good, rig.Host.ReadFile(record));
        Assert.False(rig.Host.FileExists(record + ".unreadable"));
    }

    [Fact]
    public void ASwitchWithNoProcessNameIsNeverUsed()
    {
        using var rig = new Rig();
        // A catalog route that names no process (a test catalog item that names none): Exo can't tell whether its program
        // runs, so it must never change the program's setting behind its back. The item is marked instead.
        var route = new RealSwitch("jsonSetting", ScsSettings, "OnDriverUpdate", "Off", Process: null, Status: "Turned off in its own settings");
        using var catalog = StartupCatalog.UseTestItem(new StartupCatalog.Item(["scskillerw.exe"], "SCSKiller shader re-warm", "x", "y", "remove", RealSwitch: route));
        rig.AddScsTask();
        Assert.Null(rig.Remove("task:" + ScsTask));
        rig.Host.FileWrites.Clear();

        // The first comeback: the task made again by its program.
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal(GuardState.Back, rig.State("task:" + ScsTask));
        Assert.False(rig.Host.TaskList[0].Enabled);

        // The second, the same way: this is where a switch with no process named must never be used.
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.True(rig.Item("task:" + ScsTask)!.Comebacks.Count == 2, string.Join(" | ", rig.Log));

        // The switch has no process named, so it is never used: the second comeback goes to Marked and stops there.
        Assert.Equal("Ask", rig.Setting("OnDriverUpdate"));
        Assert.Equal(0, rig.Host.FileWrites.GetValueOrDefault(ScsSettings));
        Assert.Equal(GuardState.Marked, rig.State("task:" + ScsTask));
        Assert.False(rig.Host.TaskList[0].Enabled, string.Join(" | ", rig.Log));   // turned off once more, its settings untouched
        Assert.True(rig.Item("task:" + ScsTask)!.SwitchPending == false);
        Assert.DoesNotContain(rig.Log, line => line.Contains("own settings"));

        // And it stops: a third comeback applies nothing.
        var calls = rig.Host.Applied.Count;
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal(calls, rig.Host.Applied.Count);
        Assert.Equal(GuardState.Marked, rig.State("task:" + ScsTask));
    }

    [Fact]
    public void TheSwitchWaitsWhileEitherOfTheProgramsExecutablesRuns()
    {
        using var rig = new Rig();
        rig.AddScsTask();
        rig.Remove("task:" + ScsTask);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);

        // The catalog names both executables the program runs: either one open holds the switch.
        rig.Host.Processes["scskiller"] = [new RunningProgram(@"C:\Users\Erix\AppData\Local\SCSKiller.App\current\scskiller.exe", rig.Host.Now)];
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal("Ask", rig.Setting("OnDriverUpdate"));
        Assert.True(rig.Item("task:" + ScsTask)!.SwitchPending);

        rig.Host.Processes.Clear();
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal("Off", rig.Setting("OnDriverUpdate"));
    }

    [Fact]
    public void AProgramThatStartsJustBeforeTheSwitchIsWrittenIsNotWrittenInto()
    {
        using var rig = new Rig();
        rig.AddScsTask();
        rig.Remove("task:" + ScsTask);
        var before = rig.Host.ReadFile(ScsSettings)!;
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);   // the first comeback: off again, no switch yet
        rig.Host.FileWrites.Clear();

        // The program closes; the second comeback. The task goes off, and the program starts before its own setting is
        // written: asked at the write, it is running, and nothing may be written into it.
        var asks = new List<string>();
        rig.Host.BeforeRunning = name =>
        {
            asks.Add(name);
            // Asked again after the task went off: this is the question just before the switch would be written.
            if (rig.Host.TaskList.All(task => !task.Enabled)) rig.Host.Processes["scskiller"] = [new RunningProgram(@"C:\Users\Erix\AppData\Local\SCSKiller.App\current\scskiller.exe", rig.Host.Now)];
        };
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        rig.Host.BeforeRunning = null;

        Assert.NotEmpty(asks);
        Assert.Equal(before, rig.Host.ReadFile(ScsSettings));
        Assert.Equal(0, rig.Host.FileWrites.GetValueOrDefault(ScsSettings));
        var item = rig.Item("task:" + ScsTask)!;
        Assert.Null(item.SwitchFile);
        Assert.Equal(GuardState.Back, item.State);   // not advanced to Switched
        Assert.True(item.SwitchPending);             // and the switch waits for the program to close
        Assert.Contains(rig.Log, line => line.Contains("is running; its own setting is left", StringComparison.Ordinal));

        // The program closes: the held switch is written then.
        rig.Host.Processes.Clear();
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal("Off", rig.Setting("OnDriverUpdate"));
        Assert.Equal(GuardState.Switched, rig.State("task:" + ScsTask));
    }

    [Fact]
    public void ASwitchIsNotWrittenWhenTheRecordCannotBeSaved()
    {
        using var rig = new Rig();
        rig.AddScsTask();
        rig.Remove("task:" + ScsTask);
        var before = rig.Host.ReadFile(ScsSettings)!;
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        rig.Host.FileWrites.Clear();

        // The record can't be saved: the intent (the bytes before, the value before) would be lost, so the program's file is
        // not touched at all. The save fails once; the record stays as it was, so the switch is tried again at the next check.
        rig.Host.Unwritable[Path.Combine(rig.Host.DataFolder, "startup-guard.json")] = 1;
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);

        Assert.Equal(before, rig.Host.ReadFile(ScsSettings));
        Assert.Equal(0, rig.Host.FileWrites.GetValueOrDefault(ScsSettings));
        var item = rig.Item("task:" + ScsTask)!;
        Assert.Null(item.SwitchFile);
        Assert.Equal(GuardState.Back, item.State);
        Assert.Contains(rig.Log, line => line.Contains("could not be recorded", StringComparison.Ordinal));

        // Nothing was written and nothing is left to undo: the item keeps what it had, and the next comeback uses the switch.
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal("Off", rig.Setting("OnDriverUpdate"));
        Assert.Equal(GuardState.Switched, rig.State("task:" + ScsTask));
    }

    [Fact]
    public void ASwitchInterruptedAfterTheRecordWasSavedCanStillBePutBack()
    {
        // The record is saved with the intent before the file is written, so a check that stops between the two leaves the
        // record saying what to undo—and the program's file exactly as it was.
        using (var rig = new Rig())
        {
            rig.AddScsTask();
            rig.Remove("task:" + ScsTask);
            var before = rig.Host.ReadFile(ScsSettings)!;
            rig.Host.ReCreateTask(ScsTask);
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
            rig.Host.FileWrites.Clear();

            // The record is saved with the intent, and the file write fails once: nothing was written into the program's file.
            rig.Host.Unwritable[ScsSettings] = 1;
            rig.Host.Now = rig.Host.Now.AddDays(1);
            rig.Host.ReCreateTask(ScsTask);
            rig.Guard.Check(StartupGuard.CheckKind.Tick);

            Assert.Equal(before, rig.Host.ReadFile(ScsSettings));
            var item = rig.Item("task:" + ScsTask)!;
            Assert.Equal(ScsSettings, item.SwitchFile);
            Assert.Equal(before, item.SwitchFileBefore);

            // Put back: the file is still exactly as it was, so there is nothing to undo, and the item is forgotten.
            var writes = rig.Host.FileWrites.GetValueOrDefault(ScsSettings);
            Assert.Null(rig.Restore("task:" + ScsTask));
            Assert.Equal(before, rig.Host.ReadFile(ScsSettings));
            Assert.Equal(writes, rig.Host.FileWrites.GetValueOrDefault(ScsSettings));
            Assert.Empty(StartupEntries.Kept(rig.Host));
            Assert.Null(rig.Item("task:" + ScsTask));
        }

        // A setting that already holds the off value: the bytes are left as they were, not rewritten in Exo's formatting.
        using (var rig = new Rig())
        {
            rig.AddScsTask();
            const string Compact = "{\"OnDriverUpdate\":\"Off\",\"Threads\":9}";
            rig.Host.WriteFile(ScsSettings, Compact);
            rig.Remove("task:" + ScsTask);
            rig.Host.ReCreateTask(ScsTask);
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
            rig.Host.Unwritable[ScsSettings] = 1;
            rig.Host.Now = rig.Host.Now.AddDays(1);
            rig.Host.ReCreateTask(ScsTask);
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
            Assert.Equal(Compact, rig.Host.ReadFile(ScsSettings));
            Assert.Equal(Compact, rig.Item("task:" + ScsTask)!.SwitchFileBefore);

            rig.Host.FileWrites.Clear();
            Assert.Null(rig.Restore("task:" + ScsTask));
            Assert.Equal(Compact, rig.Host.ReadFile(ScsSettings));
            Assert.Equal(0, rig.Host.FileWrites.GetValueOrDefault(ScsSettings));
            Assert.Null(rig.Item("task:" + ScsTask));
        }
    }

    [Fact]
    public void ASettingsFileThatIsBeingRewrittenIsTriedAgainNextTime()
    {
        using var rig = new Rig();
        rig.AddScsTask();
        Assert.Null(rig.Remove("task:" + ScsTask));
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);   // the first comeback: off again
        Assert.Equal(GuardState.Back, rig.State("task:" + ScsTask));

        // The second comeback, and the setting is half-written at that moment (its program is saving it).
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Host.WriteFile(ScsSettings, "{\"OnDriverUpdate\":\"A");
        rig.Guard.Check(StartupGuard.CheckKind.Tick);

        // Not given up on: the item is as it was, its start-up entry is off again, nothing of its setting was written.
        var item = rig.Item("task:" + ScsTask)!;
        Assert.Equal(GuardState.Back, item.State);
        Assert.False(rig.Host.TaskList.Single().Enabled);
        Assert.Equal("{\"OnDriverUpdate\":\"A", rig.Host.ReadFile(ScsSettings));
        Assert.Null(item.SwitchFile);
        Assert.Single(rig.Log, line => line.Contains("could not be read just now"));

        // The next comeback, with the file good: the switch is used, as the second comeback's switch is.
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Host.WriteFile(ScsSettings, "{\"OnDriverUpdate\":\"Ask\",\"Threads\":9}");
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal("Off", rig.Setting("OnDriverUpdate"));
        Assert.Equal(GuardState.Switched, rig.State("task:" + ScsTask));
    }

    [Fact]
    public void ASettingsFileThatNeverReadsIsGivenUpOnAfterThreeTries()
    {
        using var rig = new Rig();
        rig.AddScsTask();
        Assert.Null(rig.Remove("task:" + ScsTask));
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);   // the first comeback: off again
        rig.Host.WriteFile(ScsSettings, "{\"OnDriverUpdate\":\"A");

        // Two reads that fail this moment: still not marked, its start-up entry off again each time.
        for (var i = 0; i < 2; i++)
        {
            rig.Host.Now = rig.Host.Now.AddDays(1);
            rig.Host.ReCreateTask(ScsTask);
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
            Assert.Equal(GuardState.Back, rig.State("task:" + ScsTask));
            Assert.False(rig.Host.TaskList.Single().Enabled);
            Assert.Equal(i + 1, rig.Item("task:" + ScsTask)!.SwitchTries);
        }

        // The third: it cannot loop for ever, so it is marked and Exo stops.
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal(GuardState.Marked, rig.State("task:" + ScsTask));
        Assert.False(rig.Host.TaskList.Single().Enabled);
        Assert.Equal(3, rig.Item("task:" + ScsTask)!.SwitchTries);
        Assert.Equal("{\"OnDriverUpdate\":\"A", rig.Host.ReadFile(ScsSettings));   // never written
        Assert.Contains(rig.Log, line => line.Contains("Exo has stopped trying"));
        var calls = rig.Host.Applied.Count;
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        Assert.Equal(calls, rig.Host.Applied.Count);
    }

    [Fact]
    public void ASettingsFileOnANetworkDriveIsNeverWritten()
    {
        using var rig = new Rig();
        rig.AddScsTask();
        rig.Remove("task:" + ScsTask);
        var before = rig.Host.ReadFile(ScsSettings)!;
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        rig.Host.FileWrites.Clear();

        // Reading a file on a share can wait for as long as the share takes, and the guard holds its lock while it does:
        // the item is treated as having no switch at all, and marked.
        rig.Host.DriveFixed = path => !path.Equals(ScsSettings, StringComparison.OrdinalIgnoreCase);
        rig.Host.Now = rig.Host.Now.AddDays(1);
        rig.Host.ReCreateTask(ScsTask);
        rig.Guard.Check(StartupGuard.CheckKind.Tick);

        Assert.Equal(before, rig.Host.ReadFile(ScsSettings));
        Assert.Equal(0, rig.Host.FileWrites.GetValueOrDefault(ScsSettings));
        var item = rig.Item("task:" + ScsTask)!;
        Assert.Null(item.SwitchFile);
        Assert.Equal(GuardState.Marked, item.State);
    }

    [Fact]
    public void UndoingASwitchPutsTheFileBackByteForByteWhenNothingElseChangedIt()
    {
        using var rig = new Rig();
        const string Compact = "{\"OnDriverUpdate\":\"Ask\",\"Threads\":9}";
        rig.Host.SetRun(Hive.User, StartupKeys.Run, "SCSKiller", "\"" + Scs + "\" --tray");
        rig.Host.Set(Hive.User, StartupKeys.Approved + "Run", "SCSKiller", RegValue.Bin(FakeStartupHost.On()));
        rig.Host.WriteFile(ScsSettings, Compact);
        Assert.Null(rig.Remove("run:cu:SCSKiller"));
        for (var i = 0; i < 2; i++)
        {
            rig.Host.Now = rig.Host.Now.AddDays(1);
            rig.Flip("SCSKiller");
            rig.Host.ForegroundProgram = "scskillerw";   // seen: the program, not Task Manager or Settings
            rig.Host.Raise();
        }
        Assert.True(rig.Item("run:cu:SCSKiller")!.State == GuardState.Switched, string.Join(" | ", rig.Log));
        Assert.Equal("Off", rig.Setting("OnDriverUpdate"));
        Assert.NotEqual(Compact, rig.Host.ReadFile(ScsSettings));

        Assert.Null(rig.Restore("run:cu:SCSKiller"));
        Assert.Equal(Compact, rig.Host.ReadFile(ScsSettings));
    }

    [Fact]
    public void AMachineWideRunOnceValueOnlyShows()
    {
        using var rig = new Rig();
        rig.Host.SetRun(Hive.Machine, StartupKeys.RunOnce, "Setup", "\"C:\\Acme\\setup.exe\" /finish");
        var entry = StartupEntries.ReadAll(rig.Host).Single(item => item.Id == "runonce:lm:Setup");
        Assert.True(entry.ReadOnly);
        Assert.Equal("Exo only shows this one.", rig.Remove("runonce:lm:Setup"));
        Assert.NotNull(rig.Host.Get(Hive.Machine, StartupKeys.RunOnce, "Setup"));   // still there, to finish its job
        Assert.Empty(rig.Host.Applied);
        // The user's own RunOnce value is still Exo's to take out.
        rig.Host.SetRun(Hive.User, StartupKeys.RunOnce, "Mine", "\"C:\\Acme\\mine.exe\"");
        Assert.Null(rig.Remove("runonce:cu:Mine"));
    }

    [Fact]
    public void AServiceWithNoRecordedStartTypeIsNotPutBackAsAutomatic()
    {
        using var rig = new Rig();
        rig.Host.AddService("AcmeSvc", @"""C:\Program Files\Acme\acme.exe"" /svc", start: 2);
        var removal = new StartupEntries.Removal("service:AcmeSvc", "Acme service", rig.Host.Now, WasOn: true) { Program = @"C:\Program Files\Acme\acme.exe" };
        Seed(rig.Host, removal);
        rig.Host.Set(Hive.Machine, StartupKeys.Services + @"\AcmeSvc", "Start", RegValue.DWord(4));

        var error = rig.Restore("service:AcmeSvc");
        Assert.Equal("Exo can't put this one back, so turn it on in Windows' Services.", error);
        // Not written as Automatic (2) behind the player's back.
        Assert.Equal(4, rig.Host.Get(Hive.Machine, StartupKeys.Services + @"\AcmeSvc", "Start")?.Number);
        Assert.Single(StartupEntries.Kept(rig.Host));
    }

    // ---- what an older Exo removed that Exo never turns off ----

    private static StartupEntries.Removal Old(string id, string name, string? value = null, byte[]? approval = null) =>
        new(id, name, new DateTime(2026, 11, 1, 21, 59, 0, DateTimeKind.Utc), value, Approval: approval);

    private static void Seed(FakeStartupHost host, params StartupEntries.Removal[] removals) =>
        host.WriteFile(Path.Combine(host.DataFolder, "startup-removed.json"), JsonSerializer.Serialize(removals));

    [Fact]
    public void WhatAnOlderExoRemovedThatExoNeverTurnsOffIsPutBackOnceAndWindowsSecuritysTrayStaysOut()
    {
        using var rig = new Rig();
        var edge = rig.Host.AddTask(@"\MicrosoftEdgeUpdateTaskMachineCore{0C6E}", @"C:\Program Files (x86)\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe", "/c", enabled: false);
        rig.Host.SetRun(Hive.User, StartupKeys.Run, "Acme", Acme);
        rig.Host.Set(Hive.User, StartupKeys.Approved + "Run", "Acme", RegValue.Bin(FakeStartupHost.Off(rig.Host.Now)));
        rig.Host.SetRun(Hive.Machine, StartupKeys.Run, "NvContainer", "\"C:\\Program Files\\NVIDIA Corporation\\NvContainer\\nvcontainer.exe\"");
        Seed(rig.Host,
            Old(@"task:\MicrosoftEdgeUpdateTaskMachineCore{0C6E}", "Microsoft Edge updater"),
            Old("run:lm:SecurityHealth", "Windows Security tray", @"%windir%\system32\SecurityHealthSystray.exe"),
            Old("run:lm:Riot Vanguard", "Riot Vanguard tray", "\"C:\\Program Files\\Riot Vanguard\\vgtray.exe\""),
            Old("run:lm:NvContainer", "NVIDIA container", "\"C:\\Program Files\\NVIDIA Corporation\\NvContainer\\nvcontainer.exe\""),
            Old("run:cu:Acme", "Acme", Acme));

        rig.Guard.Start();

        Assert.True(edge.Enabled);   // the Edge updater: put back
        Assert.Null(rig.Host.Get(Hive.Machine, StartupKeys.Run, "SecurityHealth"));   // the owner's choice stays
        Assert.Null(rig.Host.Get(Hive.Machine, StartupKeys.Run, "Riot Vanguard"));   // the tray is the player's to take out
        Assert.True(rig.IsOff(Hive.User, "Run", "Acme"));   // an ordinary item stays off
        Assert.Equal(0, rig.Host.ElevatedRequests);   // the guard asks Windows for nothing: the driver's helper stays for the player
        Assert.NotNull(rig.Host.Get(Hive.Machine, StartupKeys.Run, "NvContainer"));
        var left = StartupEntries.Kept(rig.Host).Select(item => item.Id).Order().ToArray();
        Assert.Equal(["run:cu:Acme", "run:lm:NvContainer", "run:lm:Riot Vanguard", "run:lm:SecurityHealth"], left);
        Assert.Equal(GuardState.Locked, rig.State("run:lm:SecurityHealth"));
        Assert.Contains(rig.Log, line => line.Contains("Windows Security tray stays removed"));
        Assert.Contains(rig.Log, line => line.Contains("NVIDIA container stays removed; putting it back needs your OK"));

        // Once: nothing is put back or touched again, and it is not said again.
        var calls = rig.Host.Applied.Count;
        rig.Guard.Check(StartupGuard.CheckKind.Tick);
        rig.Guard.Check(StartupGuard.CheckKind.Game);
        Assert.Equal(calls, rig.Host.Applied.Count);
        Assert.Single(rig.Log, line => line.Contains("NVIDIA container stays removed"));

        // Shown under Removed with Put back, and locked from now on.
        var removed = StartupEntries.Kept(rig.Host).Single(item => item.Id == "run:lm:SecurityHealth");
        Assert.NotEqual(LockKind.None, StartupGuard.LockOf(rig.Host, removed));
        Assert.Equal(LockKind.None, StartupGuard.LockOf(rig.Host, StartupEntries.Kept(rig.Host).Single(item => item.Id == "run:cu:Acme")));
        rig.Host.SetRun(Hive.Machine, StartupKeys.Run, "SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe");
        Assert.Equal(StartupLocks.Sentence(LockKind.Security), rig.Remove("run:lm:SecurityHealth"));
        Assert.Null(rig.Restore("run:lm:SecurityHealth"));
        Assert.Equal(GuardState.Untracked, rig.State("run:lm:SecurityHealth"));
    }

    [Fact]
    public void UninstallPutsBackWhatTheUserScopeAllowsAndNeverPrompts()
    {
        using var rig = new Rig();
        rig.AddAcme();
        rig.AddAcme(Hive.Machine, "Alpha", "\"C:\\Alpha\\alpha.exe\"");
        rig.Host.AddTask(@"\Acme\Update", @"C:\Program Files\Acme\update.exe", "/c");
        Assert.Null(rig.Remove("run:cu:Acme"));
        Assert.Null(rig.Remove("run:lm:Alpha"));
        Assert.Null(rig.Remove(@"task:\Acme\Update"));
        var asks = rig.Host.ElevatedRequests;

        var back = StartupEntries.RestoreAllForUser(rig.Host);

        Assert.Equal(2, back);
        Assert.Equal(asks, rig.Host.ElevatedRequests);   // never Windows' prompt: the machine-wide one is left
        Assert.False(rig.IsOff(Hive.User, "Run", "Acme"));
        Assert.True(rig.Host.TaskList.Single().Enabled);
        Assert.True(rig.IsOff(Hive.Machine, "Run", "Alpha"));
        // The record lists what is still off: only the machine-wide one, which the uninstaller left alone.
        Assert.Equal(["run:lm:Alpha"], StartupEntries.Kept(rig.Host).Select(item => item.Id));
        Assert.Equal(["run:lm:Alpha"], StartupGuard.Load(rig.Host).Items.Select(item => item.Id));
    }

    // ---- the state table ----

    [Fact]
    public void EveryRowOfTheTableIsWalkedAndNoStateLacksAnExit()
    {
        var rows = StartupGuard.Table;
        // No two rows for one state and event, and each row is what Step answers.
        Assert.Equal(rows.Count, rows.Select(row => (row.State, row.Event)).Distinct().Count());
        foreach (var row in rows) Assert.Same(row, StartupGuard.Step(row.State, row.Event));

        // Every state has rows, and at least one leads out of it.
        foreach (var state in Enum.GetValues<GuardState>())
        {
            var own = rows.Where(row => row.State == state).ToList();
            Assert.NotEmpty(own);
            Assert.Contains(own, row => row.Next != state);
        }
        // Every state is reached from Untracked by walking rows.
        var reached = new HashSet<GuardState> { GuardState.Untracked };
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var row in rows.Where(row => reached.Contains(row.State))) changed |= reached.Add(row.Next);
        }
        Assert.Equal(Enum.GetValues<GuardState>().Length, reached.Count);
        // From every state a Put back leads to Untracked: nothing is guarded after it.
        foreach (var state in Enum.GetValues<GuardState>().Where(state => state != GuardState.Untracked))
            Assert.Equal(GuardState.Untracked, StartupGuard.Step(state, GuardEvent.PutBack)?.Next);
        // And a state's events that have no row are ignored, never a crash.
        Assert.Null(StartupGuard.Step(GuardState.Untracked, GuardEvent.ProgramBack));
    }

    [Fact]
    public void EveryActionInTheTableHasAnEffectATestCanSee()
    {
        // For each action that remains: one row that carries it, driven through Check, with its effect seen.
        // None: a row that ends in a state with no change (an updater's entry runs another way): nothing is applied.
        using (var rig = new Rig())
        {
            rig.Host.AddTask(@"\GoogleUpdateTaskMachineCore", @"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe", "/c");
            rig.Host.AddService("gupdate", @"""C:\Program Files (x86)\Google\Update\GoogleUpdate.exe"" /svc", start: 2);
            rig.Remove(@"task:\GoogleUpdateTaskMachineCore");
            rig.Host.Processes["googleupdate"] = [new RunningProgram(@"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe", rig.Host.Now)];
            var calls = rig.Host.Applied.Count;
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
            Assert.Equal(GuardState.Updater, rig.State(@"task:\GoogleUpdateTaskMachineCore"));
            Assert.Equal(calls, rig.Host.Applied.Count);
        }

        // RecordOff: the player turns something off in Exo: the record says Off and the entry is off.
        using (var rig = new Rig())
        {
            rig.AddAcme();
            Assert.Null(rig.Remove("run:cu:Acme"));
            Assert.Equal(GuardState.Off, rig.State("run:cu:Acme"));
            Assert.True(rig.IsOff(Hive.User, "Run", "Acme"));
        }

        // PutOffAgain: a Run value flipped on is off again, with a log line.
        using (var rig = new Rig())
        {
            rig.AddAcme();
            rig.Remove("run:cu:Acme");
            rig.Host.Now = rig.Host.Now.AddMinutes(5);
            rig.Flip();
            rig.Host.ForegroundProgram = "acme";
            rig.Host.Raise();
            Assert.True(rig.IsOff(Hive.User, "Run", "Acme"));
            Assert.Contains(rig.Log, line => line.Contains("turned itself back on") && line.Contains("off again"));
        }

        // PutOffAndSwitch: the second comeback of a Run value whose catalog item has a switch: off again AND the setting changed.
        using (var rig = new Rig())
        {
            rig.Host.SetRun(Hive.User, StartupKeys.Run, "SCSKiller", "\"" + Scs + "\" --tray");
            rig.Host.Set(Hive.User, StartupKeys.Approved + "Run", "SCSKiller", RegValue.Bin(FakeStartupHost.On()));
            rig.Host.WriteFile(ScsSettings, "{\"OnDriverUpdate\":\"Ask\"}");
            rig.Remove("run:cu:SCSKiller");
            for (var i = 0; i < 2; i++)
            {
                rig.Host.Now = rig.Host.Now.AddDays(1);
                rig.Flip("SCSKiller");
                rig.Host.ForegroundProgram = "scskillerw";
                rig.Host.Raise();
            }
            Assert.Equal(GuardState.Switched, rig.State("run:cu:SCSKiller"));
            Assert.True(rig.IsOff(Hive.User, "Run", "SCSKiller"));
            Assert.Equal("Off", rig.Setting("OnDriverUpdate"));
        }

        // ApplySwitch: a switch held back while the program ran is applied once it closes.
        using (var rig = new Rig())
        {
            rig.AddScsTask();
            rig.Remove("task:" + ScsTask);
            rig.Host.ReCreateTask(ScsTask);
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
            rig.Host.Processes["SCSKiller"] = [new RunningProgram(@"C:\Users\Erix\AppData\Local\SCSKiller.App\current\SCSKiller.exe", rig.Host.Now)];
            rig.Host.Now = rig.Host.Now.AddDays(1);
            rig.Host.ReCreateTask(ScsTask);
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
            Assert.True(rig.Item("task:" + ScsTask)!.SwitchPending);
            rig.Host.Processes.Clear();
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
            Assert.Equal(GuardState.Switched, rig.State("task:" + ScsTask));
            Assert.Equal("Off", rig.Setting("OnDriverUpdate"));
        }

        // UndoSwitchAndDrop: the player turns it on again after the switch was used: the setting goes back and the record goes.
        using (var rig = new Rig())
        {
            rig.AddScsTask();
            rig.Remove("task:" + ScsTask);
            for (var i = 0; i < 2; i++)
            {
                rig.Host.Now = rig.Host.Now.AddDays(1);
                rig.Host.ReCreateTask(ScsTask);
                rig.Guard.Check(StartupGuard.CheckKind.Tick);
            }
            Assert.Equal(GuardState.Switched, rig.State("task:" + ScsTask));
            rig.Host.ReCreateTask(ScsTask);
            rig.Host.ForegroundProgram = "taskschd";
            rig.Host.Raise();
            Assert.Equal(GuardState.PlayerOn, rig.State("task:" + ScsTask));
            Assert.Equal("Ask", rig.Setting("OnDriverUpdate"));
            Assert.Empty(StartupEntries.Kept(rig.Host));
        }

        // UndoSwitchAndForget: Put back after the switch was used: the setting goes back and the record goes.
        using (var rig = new Rig())
        {
            rig.AddScsTask();
            rig.Remove("task:" + ScsTask);
            for (var i = 0; i < 2; i++)
            {
                rig.Host.Now = rig.Host.Now.AddDays(1);
                rig.Host.ReCreateTask(ScsTask);
                rig.Guard.Check(StartupGuard.CheckKind.Tick);
            }
            Assert.Null(rig.Restore("task:" + ScsTask));
            Assert.Equal("Ask", rig.Setting("OnDriverUpdate"));
            Assert.Empty(StartupEntries.Kept(rig.Host));
        }

        // DropRemoval: the player turns it on in Task Manager: the record goes, and it is left on.
        using (var rig = new Rig())
        {
            rig.AddAcme();
            rig.Remove("run:cu:Acme");
            rig.Flip();
            rig.Host.ForegroundProgram = "taskmgr";
            rig.Host.Raise();
            Assert.False(rig.IsOff(Hive.User, "Run", "Acme"));
            Assert.Empty(StartupEntries.Kept(rig.Host));
            Assert.Equal(GuardState.PlayerOn, rig.State("run:cu:Acme"));
        }

        // Forget: Put back: the record and the guard's item go.
        using (var rig = new Rig())
        {
            rig.AddAcme();
            rig.Remove("run:cu:Acme");
            Assert.Null(rig.Restore("run:cu:Acme"));
            Assert.Empty(StartupEntries.Kept(rig.Host));
            Assert.Null(rig.Item("run:cu:Acme"));
            Assert.False(rig.IsOff(Hive.User, "Run", "Acme"));
        }

        // LogOnly: a marked item that comes back again logs once, and nothing is applied.
        using (var rig = new Rig())
        {
            rig.Host.SetRun(Hive.User, StartupKeys.Run, "SCSKiller", "\"" + Scs + "\" --tray");
            rig.Host.Set(Hive.User, StartupKeys.Approved + "Run", "SCSKiller", RegValue.Bin(FakeStartupHost.On()));
            rig.Host.WriteFile(ScsSettings, "{\"OnDriverUpdate\":\"Ask\"}");
            rig.Remove("run:cu:SCSKiller");
            for (var i = 0; i < 3; i++) { rig.Host.Now = rig.Host.Now.AddDays(1); rig.Flip("SCSKiller"); rig.Guard.Check(StartupGuard.CheckKind.Tick); }
            Assert.Equal(GuardState.Marked, rig.State("run:cu:SCSKiller"));
            var calls = rig.Host.Applied.Count;
            rig.Host.Now = rig.Host.Now.AddDays(1);
            rig.Flip("SCSKiller");
            rig.Guard.Check(StartupGuard.CheckKind.Tick);
            Assert.Equal(calls, rig.Host.Applied.Count);
            Assert.Contains(rig.Log, line => line.EndsWith("left on.", StringComparison.Ordinal));
        }

        // Every action the enum names is one of those rows, so nothing is added without a test that sees it.
        var seen = new[]
        {
            GuardAction.None, GuardAction.RecordOff, GuardAction.PutOffAgain, GuardAction.PutOffAndSwitch, GuardAction.ApplySwitch,
            GuardAction.UndoSwitchAndDrop, GuardAction.UndoSwitchAndForget, GuardAction.DropRemoval, GuardAction.Forget, GuardAction.LogOnly,
        };
        Assert.Equal(seen.Order(), Enum.GetValues<GuardAction>().Order());
        Assert.All(StartupGuard.Table, row => Assert.Contains(row.Action, seen));
    }
}
