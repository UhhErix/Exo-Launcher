using System.Diagnostics;
using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Tests;

/// <summary>
/// Clearing by itself, in a sandbox of temp folders and a test key. Ways it could fail, worst first: it takes something on the
/// never-selected list (a name alone, a shared file, a folder a running program uses, a store's folder, anti-cheat, a driver); it
/// runs after an uninstall that was cancelled, failed or left the program there; what programs already gone left goes on thin
/// evidence (an unplugged drive, a folder with files in it, a program still in its folder), during a game, while someone is
/// at the PC, or twice a day; Windows' prompt comes by surprise, once per item, or a decline loses what didn't need it; what's
/// waiting on a prompt raises one of its own; a tampered elevated script runs; what went can't be put back or isn't said.
/// </summary>
public sealed class AutoClearTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-autoclear-" + Guid.NewGuid().ToString("N"));
    private readonly string _test = @"Software\ExoLauncherTests\" + Guid.NewGuid().ToString("N");
    private string Hkcu => _test + @"\HKCU";
    private string Hklm => _test + @"\HKLM";

    private Dictionary<string, string> Places => new(StringComparer.OrdinalIgnoreCase)
    {
        ["LocalAppData"] = Path.Combine(_root, "Local"),
        ["AppData"] = Path.Combine(_root, "Roaming"),
        ["Desktop"] = Path.Combine(_root, "Desktop"),
        ["CommonDesktop"] = Path.Combine(_root, "PublicDesktop"),
        ["ProgramFiles"] = Path.Combine(_root, "Program Files"),
        ["ProgramData"] = Path.Combine(_root, "ProgramData"),
    };

    public AutoClearTests()
    {
        foreach (var place in Places.Values) Directory.CreateDirectory(place);
    }

    private string Place(string place, string rest) => Path.Combine(Places[place], rest);

    private string File_(string place, string rest)
    {
        var path = Place(place, rest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private LeftoverScan Scan(IReadOnlyCollection<string>? running = null, bool driveHere = true) =>
        new(Places, Hkcu, Hklm) { Running = () => running ?? [], DriveThere = _ => driveHere };

    private RemovalHistory History() => new(Path.Combine(_root, "removals.json"), _ => []);

    private static Task<bool> NoPrompt(IReadOnlyList<string> steps) => throw new InvalidOperationException("nothing here needs Windows' prompt");

    // ---- after an uninstall ----

    [Fact]
    public async Task NothingOnTheNeverSelectedListGoesEvenAfterAnUninstall()
    {
        // One press's worth of everything at once: its own folder and data, and all that isn't its to take.
        var home = Place("LocalAppData", @"Programs\Acme Paint");
        var mine = File_("LocalAppData", @"Programs\Acme Paint\leftover.dat");
        var byName = File_("AppData", @"Acme Paint\settings.ini");
        var running = File_("LocalAppData", @"Programs\Acme Busy\busy.exe");
        var sharedDll = File_("LocalAppData", @"Programs\Acme Shared\common.dll");
        var store = File_("LocalAppData", @"Programs\Acme Store\steamapps\saves.dat");
        using (var counts = Registry.CurrentUser.CreateSubKey(Hklm + @"\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs")) counts.SetValue(sharedDll, 2, RegistryValueKind.DWord);
        using (var driver = Registry.CurrentUser.CreateSubKey(Hklm + @"\SYSTEM\CurrentControlSet\Services\AcmeDriver"))
        {
            driver.SetValue("ImagePath", home + @"\acme.sys");
            driver.SetValue("Type", 1, RegistryValueKind.DWord);
        }
        var scan = Scan(running: [running]);
        var found = new List<Leftover>();
        found.AddRange(scan.Find(new Trail("Acme Paint") { Homes = [home], Names = ["Acme Paint"] }, deep: true));
        found.AddRange(scan.Find(new Trail("Acme Busy") { Homes = [Place("LocalAppData", @"Programs\Acme Busy")] }, deep: false));
        found.AddRange(scan.Find(new Trail("Acme Shared") { Homes = [Place("LocalAppData", @"Programs\Acme Shared")] }, deep: false));
        found.AddRange(scan.Find(new Trail("Acme Store") { Homes = [Place("LocalAppData", @"Programs\Acme Store")], HomeHeld = AppsOrphans.StoreHome }, deep: false));

        var its = AutoClear.Its(found);
        await AutoClear.ClearAsync(scan, "Acme Paint", its.Where(item => !item.Machine).ToList(), admin: null, History(), DateTime.UtcNow);

        Assert.True(File.Exists(byName));
        Assert.True(File.Exists(running));
        Assert.True(File.Exists(sharedDll));
        Assert.True(File.Exists(store));
        Assert.Contains(found, item => item.Path == Place("AppData", "Acme Paint") && !item.Selected);
        Assert.DoesNotContain(its, item => item.Path.EndsWith("AcmeDriver", StringComparison.Ordinal));
        // Its own folder is what the press was for.
        Assert.False(File.Exists(mine));
    }

    [Fact]
    public async Task AnAntiCheatsOwnFolderIsNeverTakenWhateverItsCalledFrom()
    {
        var home = Place("LocalAppData", @"Programs\Riot Vanguard");
        var exe = File_("LocalAppData", @"Programs\Riot Vanguard\vgc.exe");
        var scan = Scan();

        var found = scan.Find(new Trail("Riot Vanguard") { Homes = [home], Names = ["Riot Vanguard"] }, deep: true);
        await AutoClear.ClearAsync(scan, "Riot Vanguard", AutoClear.Its(found), admin: null, History(), DateTime.UtcNow);

        Assert.Empty(found);
        Assert.True(File.Exists(exe));
    }

    [Theory]
    [InlineData(@"EasyAntiCheat\EasyAntiCheat.exe")]
    [InlineData(@"BattlEye\BEService.exe")]
    [InlineData(@"tools\deep\vgc.exe")]
    [InlineData("easyanticheat_x64.dll")]
    public async Task AProgramsFolderWithAntiCheatInsideIsNeverTaken(string inside)
    {
        var home = Place("LocalAppData", @"Programs\Acme Game");
        var cheat = File_("LocalAppData", Path.Combine(@"Programs\Acme Game", inside));
        var scan = Scan();

        var found = scan.Find(new Trail("Acme Game") { Homes = [home] }, deep: true);
        await AutoClear.ClearAsync(scan, "Acme Game", AutoClear.Its(found), admin: null, History(), DateTime.UtcNow);

        Assert.DoesNotContain(found, item => item.Path.Equals(home, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(cheat));
    }

    [Fact]
    public void AGameFolderInsideTheProgramsFolderStays()
    {
        var home = Place("ProgramFiles", "Acme Store");
        var game = Place("ProgramFiles", @"Acme Store\library\Game");
        File_("ProgramFiles", @"Acme Store\library\Game\saves\one.sav");

        var found = Scan().Find(new Trail("Acme Store") { Homes = [home], Keep = [game] }, deep: false);

        Assert.DoesNotContain(AutoClear.Its(found), item => item.Kind == LeftoverKind.Folder);
    }

    [Fact]
    public void ItsPublishersFolderAndItsNameTogetherAreEvidenceAndAFolderHoldingItsExeIsToo()
    {
        var tied = File_("AppData", @"Acme Software\Acme Paint\settings.ini");
        var withExe = File_("ProgramData", @"Acme Paint\bin\acmepaint.exe");
        File_("ProgramFiles", @"Acme Paint\acmepaint.exe");
        File_("LocalAppData", @"Acme Paint\only-a-name.txt");

        var found = Scan().Find(new Trail("Acme Paint")
        {
            Homes = [Place("ProgramFiles", "Acme Paint")], Names = ["Acme Paint"], Publisher = "Acme Software", Exes = ["acmepaint.exe"],
        }, deep: false);

        Assert.Contains(found, item => item.Path == Path.GetDirectoryName(tied) && item.Selected);
        Assert.Contains(found, item => item.Path == Place("ProgramData", "Acme Paint") && item.Selected && withExe.Length > 0);
        Assert.Contains(found, item => item.Path == Place("LocalAppData", "Acme Paint") && !item.Selected);
    }

    [Fact]
    public void AnExeNamedLikeAnUpdaterIsNoEvidence()
    {
        File_("ProgramData", @"Acme Paint\update.exe");
        var found = Scan().Find(new Trail("Acme Paint") { Names = ["Acme Paint"], Exes = ["update.exe"] }, deep: false);
        Assert.Contains(found, item => item.Path == Place("ProgramData", "Acme Paint") && !item.Selected);
    }

    private static Task<string?> Ran(string? problem) => Task.FromResult(problem);

    [Fact]
    public async Task OnlyAFinishedUninstallClears()
    {
        var cleared = 0;
        Task<string> Clear(Uninstalled ended) { if (ended.MayClear) cleared++; return Task.FromResult("gone"); }

        Assert.Equal("gone", await AutoClear.AfterUninstallAsync(() => Ran(null), () => Task.FromResult(false), () => Task.CompletedTask, 3, Clear, _ => "still"));
        Assert.Equal(1, cleared);
    }

    [Fact]
    public async Task AnUninstallCancelledOrClosedEarlyClearsNothing()
    {
        var gone = 0;
        var result = await AutoClear.AfterUninstallAsync(() => Ran(null), () => Task.FromResult(true), () => Task.CompletedTask, 3,
            _ => { gone++; return Task.FromResult("gone"); }, _ => "still");
        Assert.Equal("still", result);
        Assert.Equal(0, gone);
    }

    [Fact]
    public async Task AnUninstallThatFailedClearsNothing()
    {
        var seen = new List<Uninstalled>();
        // The program's still there, and its uninstaller couldn't start.
        await AutoClear.AfterUninstallAsync(() => Ran("Its uninstaller didn't start."), () => Task.FromResult(true), () => Task.CompletedTask, 1, ended => Task.FromResult(ended), ended => { seen.Add(ended); return ended; });
        Assert.False(seen.Single().MayClear);
        // It's gone but something was said to have gone wrong: found, for a press, not cleared.
        var odd = await AutoClear.AfterUninstallAsync(() => Ran("Its uninstaller is still running."), () => Task.FromResult(false), () => Task.CompletedTask, 1, ended => Task.FromResult(ended), ended => ended);
        Assert.True(odd.Gone);
        Assert.False(odd.MayClear);
    }

    [Fact]
    public async Task ACancelledRunReachesNoClear()
    {
        var gone = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => AutoClear.AfterUninstallAsync<string>(() => throw new OperationCanceledException(), () => Task.FromResult(false), () => Task.CompletedTask, 1,
            _ => { gone++; return Task.FromResult("gone"); }, _ => "still"));
        Assert.Equal(0, gone);
    }

    [Fact]
    public async Task AProgramThatTakesAWhileToLeaveIsWaitedFor()
    {
        var left = 3;
        var result = await AutoClear.AfterUninstallAsync(() => Ran(null), () => Task.FromResult(left-- > 0), () => Task.CompletedTask, 30, _ => Task.FromResult("gone"), _ => "still");
        Assert.Equal("gone", result);
    }

    [Fact]
    public async Task WindowsPromptIsOneForTheWholeClearAndDecliningStillClearsTheRest()
    {
        var mine = File_("LocalAppData", @"Programs\Acme\a.txt");
        var machineFolder = Place("ProgramFiles", "Acme");
        File_("ProgramFiles", @"Acme\b.txt");
        var history = History();
        var scan = Scan();
        var chosen = new List<Leftover>
        {
            new(Path.GetDirectoryName(mine)!, false, null, false),
            new(machineFolder, false, null, true),
            new(@"HKLM\SOFTWARE\Acme", true, "One", true),
            new(@"HKLM\SOFTWARE\Acme", true, "Two", true),
        };
        var prompts = new List<IReadOnlyList<string>>();

        var done = await AutoClear.ClearAsync(scan, "Acme", chosen, steps => { prompts.Add(steps); return Task.FromResult(false); }, history, DateTime.UtcNow);

        var prompt = Assert.Single(prompts);
        Assert.Equal(3, prompt.Count);   // the folder, and each value
        Assert.True(done.Declined);
        Assert.False(File.Exists(mine));
        Assert.True(File.Exists(Path.Combine(machineFolder, "b.txt")));
        Assert.Equal(new LeftoverSummary(1, 0, 0, 0, 1), done.What);
        Assert.NotNull(done.Undo);
        Assert.Contains(done.Left, item => item.Path == machineFolder);
    }

    [Fact]
    public async Task WhatWentCanBePutBackFromItsRecord()
    {
        var file = File_("LocalAppData", @"Programs\Acme\a.txt");
        var history = History();

        var done = await AutoClear.ClearAsync(Scan(), "Acme", [new(Path.GetDirectoryName(file)!, false, null, false)], admin: null, history, DateTime.UtcNow);

        var record = history.All().Single(item => item.Id == done.Undo);
        Assert.Equal(Path.GetDirectoryName(file), record.Files.Single().Path);
        Assert.False(record.Automatic);
    }

    [Fact]
    public async Task NoAdministratorMeansNoPromptAndItsItemsStay()
    {
        var folder = Place("ProgramFiles", "Acme");
        File_("ProgramFiles", @"Acme\b.txt");

        var done = await AutoClear.ClearAsync(Scan(), "Acme", [new(folder, false, null, true)], admin: null, History(), DateTime.UtcNow);

        Assert.False(done.Declined);
        Assert.Empty(done.Removed);
        Assert.True(Directory.Exists(folder));
        Assert.Null(done.Undo);
    }

    // ---- what programs already gone left ----

    private static RememberedApp Gone(string name, string? location) => new("id:" + name, name, "Acme Soft", location, null, [], false, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(-1));

    private static (List<Orphan> Found, List<string> Remembered, List<string> Nothing) Gather(LeftoverScan scan, params RememberedApp[] gone) =>
        AppsOrphans.Gather(scan, gone, [], [], _ => false);

    private string Link(string name, string target)
    {
        var path = Place("Desktop", name);
        var local = System.Text.Encoding.ASCII.GetBytes(target + '\0');
        const int Header = 0x1C;
        var size = Header + local.Length + 1;
        var bytes = new byte[0x4C + size];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), 0x4Cu);
        BitConverter.TryWriteBytes(bytes.AsSpan(0x14), 0x2u);
        var at = bytes.AsSpan(0x4C);
        BitConverter.TryWriteBytes(at, (uint)size);
        BitConverter.TryWriteBytes(at[4..], (uint)Header);
        BitConverter.TryWriteBytes(at[8..], 1u);
        BitConverter.TryWriteBytes(at[0x10..], (uint)Header);
        BitConverter.TryWriteBytes(at[0x18..], (uint)(Header + local.Length));
        local.CopyTo(at[Header..]);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void AProgramGoneWithItsFolderGoneOnADriveThatsHereGoesWhileIdle()
    {
        var home = Place("ProgramFiles", "Old Tool");
        var link = Link("Old.lnk", home + @"\old.exe");
        File_("AppData", @"Acme Soft\Old Tool\settings.ini");   // publisher and name: tied to it

        var (found, _, _) = Gather(Scan(), Gone("Old Tool", home));

        var idle = AutoClear.ForIdle(found);
        Assert.Contains(idle, item => item.Path == link);
        Assert.Contains(idle, item => item.Path == Place("AppData", @"Acme Soft\Old Tool"));
    }

    [Fact]
    public void AFolderThatsOnlyEmptyCountsAsGone()
    {
        var home = Place("ProgramFiles", "Old Tool");
        Directory.CreateDirectory(Path.Combine(home, "empty"));
        Assert.True(Scan().GoneOrEmpty(home));
        File_("ProgramFiles", @"Old Tool\empty\kept.dat");
        Assert.False(Scan().GoneOrEmpty(home));
    }

    [Fact]
    public void ADriveThatsUnpluggedIsNeverGone()
    {
        var home = Place("ProgramFiles", "Old Tool");
        Assert.False(Scan(driveHere: false).GoneOrEmpty(home));
        Link("Old.lnk", home + @"\old.exe");
        File_("AppData", @"Acme Soft\Old Tool\settings.ini");

        var (found, _, _) = Gather(Scan(driveHere: false), Gone("Old Tool", home));

        Assert.Empty(AutoClear.ForIdle(found));
        Assert.DoesNotContain(found, orphan => orphan.Truly);
    }

    [Fact]
    public void AGoneProgramsFolderStillHoldingFilesWaitsForAPress()
    {
        var home = Place("ProgramFiles", "Old Tool");
        File_("ProgramFiles", @"Old Tool\saved-work.dat");

        var (found, _, _) = Gather(Scan(), Gone("Old Tool", home));

        Assert.Empty(AutoClear.ForIdle(found));
        Assert.Contains(AutoClear.StillWaiting(found), orphan => orphan.Item.Path == home);
    }

    [Fact]
    public void AProgramStillInItsFolderIsNotGoneWhateverItsEntrySays()
    {
        var home = Place("ProgramFiles", "Still Here");
        File_("ProgramFiles", @"Still Here\app.exe");

        var (found, remembered, _) = Gather(Scan(), Gone("Still Here", home));

        Assert.Empty(found);
        Assert.Empty(remembered);
    }

    [Fact]
    public void WhatTheUserChoseToLeaveIsNeverClearedByItself()
    {
        var link = Link("Old.lnk", Place("ProgramFiles", @"Old Tool\old.exe"));

        var (found, _, _) = AppsOrphans.Gather(Scan(), [], [], [], item => item.StartsWith(link, StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(found, orphan => orphan.Item.Path == link);
    }

    [Fact]
    public void WhatNeedsWindowsPromptNeverGoesWhileIdleAndWaitsInstead()
    {
        var item = new Leftover(@"HKLM\SYSTEM\CurrentControlSet\Services\OldSvc", true, null, true, false, LeftoverKind.Service);
        var orphans = new List<Orphan> { new(item, "", true) };

        Assert.Empty(AutoClear.ForIdle(orphans));
        Assert.Equal([item], AutoClear.Waiting(orphans));
        Assert.Equal([item], AutoClear.StillWaiting(orphans).Select(orphan => orphan.Item));
    }

    [Fact]
    public void WhatWaitsOnlyJoinsAPromptThatsComingAnyway()
    {
        var waiting = new Leftover(@"HKLM\SYSTEM\CurrentControlSet\Services\OldSvc", true, null, true, false, LeftoverKind.Service);
        var mineMachine = new Leftover(@"HKLM\SOFTWARE\Acme", true, null, true);
        var mineUser = new Leftover(@"HKCU\Software\Acme", true, null, false);

        Assert.Equal([mineMachine, waiting], AutoClear.WithWaiting([mineMachine], [waiting]));
        Assert.Equal([mineMachine], AutoClear.WithWaiting([mineMachine], [mineMachine]));
        Assert.Equal([mineUser], AutoClear.WithWaiting([mineUser], [waiting]));
    }

    [Fact]
    public void ARowIsOnlyForWhatOnePressWouldTake()
    {
        var byName = new Leftover(Place("AppData", "Old Tool"), false, null, false, Likely: true, As: LeftoverKind.Folder);
        var idle = new Leftover(Place("Desktop", "Old.lnk"), false, null, false, As: LeftoverKind.Shortcut);

        Assert.Empty(AutoClear.StillWaiting([new(byName, "Old Tool", true), new(idle, "Old Tool", true)]));
    }

    // The idle and game gates, around the real decision: clear is what must not run.
    private static bool Run(DateTime? last, DateTime now, bool game, bool idle, Action? clear = null) =>
        JunkAutoClean.ClearOrphansIfDue(last, now, () => game, () => idle, clear ?? (() => { }));

    private static readonly DateTime Now = new(2026, 11, 3, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NeverDuringAGame()
    {
        var ran = false;
        Assert.False(Run(null, Now, game: true, idle: true, () => ran = true));
        Assert.False(ran);
    }

    [Fact]
    public void NeverWhileSomeoneIsAtThePc()
    {
        var ran = false;
        Assert.False(Run(null, Now, game: false, idle: false, () => ran = true));
        Assert.False(ran);
    }

    [Fact]
    public void AtMostOnceADay()
    {
        var ran = 0;
        Assert.True(Run(null, Now, false, true, () => ran++));
        Assert.False(Run(Now.AddHours(-23), Now, false, true, () => ran++));
        Assert.True(Run(Now.AddHours(-24), Now, false, true, () => ran++));
        Assert.True(Run(Now.AddDays(3), Now, false, true, () => ran++));   // the clock set back: due, not never
        Assert.Equal(3, ran);
    }

    [Fact]
    public async Task WhatWentByItselfIsSaidOnceAndCanBePutBack()
    {
        var file = File_("AppData", @"Old\a.txt");
        var history = History();

        var done = await AutoClear.ClearAsync(Scan(), AppsOrphans.Name, [new(Path.GetDirectoryName(file)!, false, null, false)], admin: null, history, DateTime.UtcNow, automatic: true);

        Assert.Equal(done.Undo, history.NewlyCleared().Single().Id);
        Assert.Empty(history.NewlyCleared());
        Assert.True(history.All().Single().Automatic);
    }

    // ---- one prompt, however many steps ----

    [Fact]
    public void StepsThatFitAreOneCmdLine()
    {
        var plan = ElevatedSteps.For(["reg delete \"a\" /f", "reg delete \"b\" /f"], "unused");
        Assert.Equal("cmd.exe", plan.File);
        Assert.Equal("/d /c reg delete \"a\" /f & reg delete \"b\" /f", plan.Arguments);
        Assert.Null(plan.Script);
    }

    private (string Exe, string Args) Elevated(IReadOnlyList<string> steps, string scriptPath, out byte[] script)
    {
        var plan = ElevatedSteps.For(steps, scriptPath);
        script = plan.Script ?? throw new InvalidOperationException("should have needed a file");
        return (plan.File, plan.Arguments);
    }

    private static void RunIt((string Exe, string Args) command)
    {
        using var process = Process.Start(new ProcessStartInfo(command.Exe, command.Args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        process!.WaitForExit(60_000);
    }

    [Fact]
    public void StepsTooLongForOneLineStillRunOnceFromAFileAndAllOfThemRun()
    {
        var marks = Enumerable.Range(0, 3).Select(i => Path.Combine(_root, $"mark {i}.txt")).ToList();
        var padding = " & rem " + new string('a', 4000);
        var steps = marks.Select(mark => $"echo x > \"{mark}\"{padding}").ToList();
        var script = Path.Combine(_root, "steps.bin");

        var command = Elevated(steps, script, out var bytes);
        File.WriteAllBytes(script, bytes);
        RunIt(command);

        Assert.True(command.Args.Length < 8000);
        Assert.All(marks, mark => Assert.True(File.Exists(mark), mark));
    }

    [Fact]
    public void AScriptChangedAfterItWasWrittenRunsNothing()
    {
        var mark = Path.Combine(_root, "mark.txt");
        var evil = Path.Combine(_root, "evil.txt");
        var padding = " & rem " + new string('a', 8000);
        var script = Path.Combine(_root, "steps.bin");

        var command = Elevated([$"echo x > \"{mark}\"{padding}"], script, out _);
        File.WriteAllBytes(script, System.Text.Encoding.Unicode.GetBytes($"echo x > \"{evil}\""));
        RunIt(command);

        Assert.False(File.Exists(mark));
        Assert.False(File.Exists(evil));
    }

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_root, true); } catch (IOException) { }
        Registry.CurrentUser.DeleteSubKeyTree(_test, false);
    }
}
