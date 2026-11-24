using System.Diagnostics;
using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Tests;

/// <summary>
/// The deep clean for any app (System › Apps), in a sandbox: folders under a temp root, keys under a test key in HKCU.
/// Ways it could fail: what points into the app's folder (a service, a COM class, App Paths, UserAssist's ROT13 record, a
/// scheduled task) is missed, or another program's is taken; a folder or key matched only by name is deleted with the
/// sure ones instead of shown apart, or deleted without the user seeing which; a generic or company name, the publisher's
/// own, or a name that is a path ("Microsoft\Windows", "..") matches; a folder that holds something still installed goes;
/// the MSI registration is looked for under the wrong key; a junction at or above a leftover (one the user can write
/// to, as in ProgramData) carries an administrator's rd or del into what it leads to; one the Recycle Bin doesn't take
/// is deleted for good anyway; a quote, %, !, ^ or control character in a name breaks out of the elevated cmd.
/// Above all, something shared is selected: two programs in one folder, the one still here loses its shortcuts and services; a
/// file SharedDLLs counts for two; a folder a running program uses; a folder found by name that something live runs from; a
/// name alone counts as sure. Never touched, whatever points where: anti-cheat, drivers, Windows' own tasks. What's already
/// gone: a program on an unplugged drive or a file Windows won't show counts as gone, a program still installed and missing a
/// file is cleared rather than left to repair, a folder is offered with no evidence, and a portable app's or a game's folder is swept up.
/// </summary>
public sealed class LeftoverScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-leftovers-" + Guid.NewGuid().ToString("N"));
    private readonly string _hkcu;
    private readonly string _hklm;

    public LeftoverScanTests()
    {
        var test = @"Software\ExoLauncherTests\" + Guid.NewGuid().ToString("N");
        _hkcu = test + @"\HKCU";
        _hklm = test + @"\HKLM";
        foreach (var place in Places.Values) Directory.CreateDirectory(place);
    }

    private Dictionary<string, string> Places => new(StringComparer.OrdinalIgnoreCase)
    {
        ["LocalAppData"] = Path.Combine(_root, "Local"),
        ["LocalLow"] = Path.Combine(_root, "LocalLow"),
        ["AppData"] = Path.Combine(_root, "Roaming"),
        ["Desktop"] = Path.Combine(_root, "Desktop"),
        ["CommonDesktop"] = Path.Combine(_root, "PublicDesktop"),
        ["CommonPrograms"] = Path.Combine(_root, "CommonPrograms"),
        ["ProgramFiles"] = Path.Combine(_root, "Program Files"),
        ["ProgramData"] = Path.Combine(_root, "ProgramData"),
        ["Temp"] = Path.Combine(_root, "Temp"),
    };

    private string Place(string place, string rest) => Path.Combine(Places[place], rest);

    private string File_(string place, string rest)
    {
        var path = Place(place, rest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private RegistryKey Key(string hive, string path) => Registry.CurrentUser.CreateSubKey((hive == "HKLM" ? _hklm : _hkcu) + '\\' + path);

    private const string Product = "{12345678-90AB-CDEF-1234-567890ABCDEF}";

    [Fact]
    public void WhatPointsIntoTheAppIsSureWhatOnlyHasItsNameIsLikelyAndNothingElseIsFound()
    {
        var home = Place("ProgramFiles", "Acme Paint");
        File_("ProgramFiles", @"Acme Paint\acmepaint.exe");
        var other = File_("ProgramFiles", @"Other\other.exe");
        using (var service = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\AcmeUpdate")) service.SetValue("ImagePath", $"\"{home}\\updater.exe\" /service");
        using (var service = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\OtherSvc")) service.SetValue("ImagePath", other);
        using (var server = Key("HKLM", @"SOFTWARE\Classes\CLSID\{AAAAAAAA-0000-0000-0000-000000000001}\InprocServer32")) server.SetValue("", home + @"\shell.dll");
        using (var server = Key("HKLM", @"SOFTWARE\Classes\CLSID\{AAAAAAAA-0000-0000-0000-000000000002}\InprocServer32")) server.SetValue("", other);
        using (var paths = Key("HKCU", @"Software\Microsoft\Windows\CurrentVersion\App Paths\acmepaint.exe")) paths.SetValue("", home + @"\acmepaint.exe");
        using (var paths = Key("HKCU", @"Software\Microsoft\Windows\CurrentVersion\App Paths\other.exe")) paths.SetValue("", other);
        using (var assist = Key("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count"))
        {
            assist.SetValue(Rot13(home + @"\acmepaint.exe"), new byte[] { 1 });
            assist.SetValue(Rot13(other), new byte[] { 1 });
        }
        Key("HKLM", @"SOFTWARE\Classes\Installer\Products\87654321BA09FEDC2143658709BADCFE").Dispose();
        File_("AppData", @"Acme Paint\settings.ini");
        Key("HKCU", @"Software\Acme Software\Acme Paint").Dispose();
        Key("HKCU", @"Software\Acme Software\Other Tool").Dispose();
        File_("AppData", @"Data\keep.txt");
        File_("LocalAppData", @"CrashDumps\acmepaint.exe.4242.dmp");
        var scan = new LeftoverScan(Places, _hkcu, _hklm);
        scan.AddTestTask(@"\Acme\Update", home + @"\updater.exe");
        scan.AddTestTask(@"\Other\Run", other);

        var found = scan.Find(new Trail("Acme Paint")
        {
            Homes = [home], Names = ["Acme Paint", "Data"], Exes = ["acmepaint.exe"], Publisher = "Acme Software", ProductCode = Product,
        }, deep: true);

        string Of(Leftover item) => item.Value is null ? item.Path : item.Path + '|' + item.Value;
        var sure = found.Where(item => !item.Likely).Select(Of).ToList();
        var likely = found.Where(item => item.Likely).Select(Of).ToList();
        Assert.Contains(home, sure);
        Assert.Contains(@"HKLM\SYSTEM\CurrentControlSet\Services\AcmeUpdate", sure);
        Assert.Contains(@"HKLM\SOFTWARE\Classes\CLSID\{AAAAAAAA-0000-0000-0000-000000000001}", sure);
        Assert.Contains(@"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths\acmepaint.exe", sure);
        Assert.Contains(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}\Count|" + Rot13(home + @"\acmepaint.exe"), sure);
        Assert.Contains(@"HKLM\SOFTWARE\Classes\Installer\Products\87654321BA09FEDC2143658709BADCFE", sure);
        Assert.Contains(Place("LocalAppData", @"CrashDumps\acmepaint.exe.4242.dmp"), sure);
        Assert.Contains(@"\Acme\Update", sure);
        Assert.Equal([Place("AppData", "Acme Paint")], likely);
        Assert.Contains(@"HKCU\Software\Acme Software\Acme Paint", sure);   // its publisher and its name: two facts, not a name alone
        // Nothing of the other program's, and no generic name.
        Assert.DoesNotContain(found, item => Of(item).Contains("Other", StringComparison.OrdinalIgnoreCase) || Of(item).Contains(Rot13(other), StringComparison.Ordinal));
        Assert.DoesNotContain(found, item => item.Path.EndsWith(@"\Data", StringComparison.OrdinalIgnoreCase));
        // A service and a task go by their own commands, in the step Windows asks permission for.
        Assert.Equal("sc stop \"AcmeUpdate\" & sc delete \"AcmeUpdate\"", scan.AdminCommand(found.Single(item => item.Kind == LeftoverKind.Service)));
        Assert.Equal("schtasks /delete /tn \"\\Acme\\Update\" /f", scan.AdminCommand(found.Single(item => item.Kind == LeftoverKind.Task)));
    }

    // BCU's rule: an app's folder that holds another installed program (a shared tree) stays, and so does anything holding it.
    [Fact]
    public void AFolderHoldingSomethingStillInstalledStays()
    {
        var home = Place("ProgramFiles", @"Acme\Paint");
        File_("ProgramFiles", @"Acme\Paint\paint.exe");
        File_("ProgramFiles", @"Acme\Paint\Plugins\Brush\brush.exe");
        var found = new LeftoverScan(Places, _hkcu, _hklm).Find(new Trail("Acme Paint")
        {
            Homes = [home], Keep = [Place("ProgramFiles", @"Acme\Paint\Plugins\Brush")],
        }, deep: false);
        Assert.DoesNotContain(found, item => item.Path.Equals(home, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AProductCodeIsPackedAsWindowsInstallerKeysIt() =>
        Assert.Equal("87654321BA09FEDC2143658709BADCFE", LeftoverScan.Packed(Product));

    [Theory]
    [InlineData("7-Zip 26.01 (x64)", "7-Zip")]
    [InlineData("Stremio version 5.0.27", "Stremio")]
    [InlineData("Grok Bot 0.75.8", "Grok Bot")]
    [InlineData("Python 3.13.15 (64-bit)", "Python")]
    [InlineData("Devin (User)", "Devin")]
    [InlineData("Counter-Strike 2", "Counter-Strike 2")]
    public void AProgramsBareNameLosesItsVersionAndBitness(string name, string bare) => Assert.Equal(bare, WebHostBridge.BareName(name));

    // A program's name is anyone's to set: its publisher's key holds all its programs, a company's too, and a name that is
    // a path names another's.
    [Fact]
    public void ANameThatIsItsPublishersACompanysOrAPathIsNoLead()
    {
        foreach (var key in new[] { @"SOFTWARE\Acme Studio", @"SOFTWARE\Logitech", @"SOFTWARE\Microsoft\Windows", @"SOFTWARE\Acme Paint" }) Key("HKLM", key).Dispose();
        Directory.CreateDirectory(Place("ProgramData", "Acme Studio"));
        var found = new LeftoverScan(Places, _hkcu, _hklm).Find(new Trail("Acme Paint")
        {
            Names = ["Acme Studio", "Logitech", @"Microsoft\Windows", @"Acme\..\..", "Acme Paint"], Publisher = "Acme Studio",
        }, deep: false);
        Assert.Equal([@"HKLM\SOFTWARE\Acme Paint"], found.Select(item => item.Path));
        Assert.True(found.Single().Likely);
    }

    // A folder an app left in ProgramData is the user's to write: swapped for a junction (or one put above it), an
    // administrator's rd would go wherever it leads. Checked again right before it goes, it stays, and so does that.
    [Fact]
    public void ALinkAtOrAboveALeftoverIsRefusedAndWhatItLeadsToStays()
    {
        var keep = Outside();
        var link = Place("ProgramData", "Acme Paint");
        Assert.SkipUnless(Junction(link, Path.Combine(_root, "Outside")), "Windows didn't make a junction here");
        var scan = new LeftoverScan(Places, _hkcu, _hklm);
        var itself = new Leftover(link, false, null, true);
        var below = new Leftover(Path.Combine(link, "Acme"), false, null, true);

        Assert.Null(scan.AdminCommand(itself));
        Assert.Null(scan.AdminCommand(below));
        Assert.False(scan.Delete(below with { Machine = false }));

        Assert.True(File.Exists(keep));
        Assert.Equal(LeftoverScan.Linked, scan.Why([itself, below]));
        // A path whose words lead elsewhere is refused too; a real folder there still goes.
        var dotted = new Leftover(Place("ProgramData", @"Real\..\..\Outside"), false, null, true);
        Assert.Null(scan.AdminCommand(dotted));
        Assert.Equal(LeftoverScan.Barred, scan.Why([dotted]));
        var real = Place("ProgramData", "Real");
        Directory.CreateDirectory(real);
        Assert.Equal($"rd /s /q \"{real}\"", scan.AdminCommand(new Leftover(real, false, null, true)));
    }

    // The elevated step itself, run here as this user on the test's own folders: rd /s takes a link inside a folder (or
    // a folder swapped for one after the check) without going into it, and del takes a file only while it is one.
    [Fact]
    public void TheAdministratorsStepNeverGoesIntoALink()
    {
        var keep = Outside();
        var outside = Path.Combine(_root, "Outside");
        var folder = Place("ProgramData", "Acme Paint");
        var log = File_("ProgramData", @"Acme Paint\log.txt");
        Assert.SkipUnless(Junction(Path.Combine(folder, "Link"), outside), "Windows didn't make a junction here");
        var scan = new LeftoverScan(Places, _hkcu, _hklm);
        var rd = scan.AdminCommand(new Leftover(folder, false, null, true));
        Assert.Equal($"rd /s /q \"{folder}\"", rd);
        var file = File_("ProgramData", @"Acme Tools\notes.txt");
        var del = scan.AdminCommand(new Leftover(file, false, null, true));
        Assert.Equal($"(if not exist \"{file}\\*\" del /f /q \"{file}\")", del);

        Cmd(rd!);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(keep));
        // Swapped for a junction while Windows asked for permission (to keep.txt's own folder, which del would empty).
        DeletionGate.DeleteFile(file);
        Assert.True(Junction(file, Path.GetDirectoryName(keep)!));
        Cmd(del!);
        Assert.True(File.Exists(keep));
        Directory.CreateDirectory(folder);
        Assert.True(Junction(Path.Combine(folder, "Swapped"), outside));
        Cmd($"rd /s /q \"{Path.Combine(folder, "Swapped")}\"");
        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(log));
    }

    // The confirmation says the Recycle Bin: one it doesn't take stays, and says why.
    [Fact]
    public void OneTheRecycleBinDoesntTakeStays()
    {
        var file = File_("AppData", @"Acme Paint\settings.ini");
        var folder = new Leftover(Place("AppData", "Acme Paint"), false, null, false);
        var scan = new LeftoverScan(Places, _hkcu, _hklm) { Recycler = _ => LeftoverScan.TooBig };

        Assert.False(scan.Delete(folder));
        Assert.True(File.Exists(file));
        Assert.Equal(LeftoverScan.TooBig, scan.Why([folder]));
        // A bin that claims it went while it is still there is no success either.
        var silent = new LeftoverScan(Places, _hkcu, _hklm) { Recycler = _ => null };
        Assert.False(silent.Delete(folder));
        Assert.Equal(LeftoverScan.NotRecycled, silent.Why([folder]));
        // One gone already is gone, whatever the bin says.
        Assert.True(scan.Delete(new Leftover(Place("AppData", "Gone"), false, null, false)));
    }

    [Fact]
    public void OnlyAFixedDrivesRecycleBinTakesItAndOnlyWhenItFits()
    {
        Assert.Null(LeftoverScan.RecycleBinRefuses(100, DriveType.Fixed, false, 100));
        Assert.Equal(LeftoverScan.TooBig, LeftoverScan.RecycleBinRefuses(101, DriveType.Fixed, false, 100));
        Assert.Equal(LeftoverScan.BinOff, LeftoverScan.RecycleBinRefuses(1, DriveType.Fixed, true, 100));
        Assert.Equal(LeftoverScan.NoBin, LeftoverScan.RecycleBinRefuses(1, DriveType.Network, false, 100));
        Assert.Equal(LeftoverScan.NoBin, LeftoverScan.RecycleBinRefuses(1, DriveType.Removable, false, 100));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("%")]
    [InlineData("!")]
    [InlineData("^")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("\u0001")]
    [InlineData("\u001b")]
    [InlineData("\u007f")]
    [InlineData("\u0085")]
    public void TheElevatedCommandRefusesWhatQuotesDontHold(string bad)
    {
        var scan = new LeftoverScan(Places, _hkcu, _hklm);
        Assert.NotNull(scan.AdminCommand(new Leftover(@"HKLM\SOFTWARE\Acme Paint", true, "Path", true)));
        var key = new Leftover(@"HKLM\SOFTWARE\Acme" + bad + "Paint", true, null, true);
        Assert.Null(scan.AdminCommand(key));
        Assert.Null(scan.AdminCommand(new Leftover(@"HKLM\SOFTWARE\Acme Paint", true, "Path" + bad, true)));
        Assert.Equal(LeftoverScan.Unquotable, scan.Why([key]));
    }

    // Outside\Acme\keep.txt under the root: what a link leads to.
    private string Outside()
    {
        var keep = Path.Combine(_root, @"Outside\Acme\keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
        File.WriteAllText(keep, "x");
        return keep;
    }

    private readonly List<string> _links = [];

    // A real junction, as any user may make one; false where Windows didn't.
    private bool Junction(string link, string target)
    {
        _links.Add(link);
        Cmd($"mklink /J \"{link}\" \"{target}\"");
        return Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null;
    }

    [Fact]
    public void TasksAreReadFromTheHelpersLines()
    {
        var tasks = LeftoverScan.ParseTasks("\\MicrosoftEdgeUpdateTaskMachineUA\t%ProgramFiles(x86)%\\Microsoft\\EdgeUpdate\\MicrosoftEdgeUpdate.exe\r\n" +
            "\\Vendor\\Café\tC:\\Users\\José\\app.exe\r\nWARNING: a stray line\r\n\\NoProgram\t\r\n");
        Assert.Equal([("\\MicrosoftEdgeUpdateTaskMachineUA", "%ProgramFiles(x86)%\\Microsoft\\EdgeUpdate\\MicrosoftEdgeUpdate.exe"),
            ("\\Vendor\\Café", "C:\\Users\\José\\app.exe")], tasks);
        Assert.Empty(LeftoverScan.ParseTasks(null));   // the helper didn't run: nothing, not a crash
    }

    // ---- what is shared, and what one press takes ----

    private LeftoverScan Scan(IReadOnlyCollection<string>? running = null, Func<string, bool>? drive = null) =>
        new(Places, _hkcu, _hklm) { Running = () => running ?? [], DriveThere = drive ?? (_ => true) };

    [Fact]
    public void OfTwoProgramsInOneFolderTheOneStillHereKeepsWhatPointsThere()
    {
        // Acme Paint is being removed; Acme Studio lives in the same folder and stays.
        var shared = Place("ProgramFiles", "Acme");
        File_("ProgramFiles", @"Acme\studio.exe");
        using (var service = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\AcmeStudio")) service.SetValue("ImagePath", shared + @"\studio.exe");
        using (var paths = Key("HKCU", @"Software\Microsoft\Windows\CurrentVersion\App Paths\studio.exe")) paths.SetValue("", shared + @"\studio.exe");
        Link("Desktop", "Studio.lnk", shared + @"\studio.exe");
        var scan = Scan();
        scan.AddTestTask(@"\Acme\Studio", shared + @"\studio.exe");

        var found = scan.Find(new Trail("Acme Paint") { Homes = [shared], Keep = [shared] }, deep: true);

        Assert.Empty(found);
    }

    [Fact]
    public void WhatLivesInsideAnotherProgramsSubfolderStaysItsEvenWhenTheFolderIsTheAppsOwn()
    {
        var home = Place("ProgramFiles", "Acme");
        var inner = Place("ProgramFiles", @"Acme\Studio");
        File_("ProgramFiles", @"Acme\Studio\studio.exe");
        File_("ProgramFiles", @"Acme\paint.exe");
        using (var theirs = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\AcmeStudio")) theirs.SetValue("ImagePath", inner + @"\studio.exe");
        using (var ours = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\AcmePaint")) ours.SetValue("ImagePath", home + @"\paint.exe");

        var found = Scan().Find(new Trail("Acme Paint") { Homes = [home], Keep = [inner] }, deep: true);

        Assert.DoesNotContain(found, item => item.Path.EndsWith("AcmeStudio", StringComparison.Ordinal));
        Assert.Contains(found, item => item.Path.EndsWith("AcmePaint", StringComparison.Ordinal) && item.Selected);
        // The folder that holds the other program never goes, whoever asks.
        Assert.DoesNotContain(found, item => item.Path.Equals(home, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AFileAnotherProgramAlsoCountsInSharedDllsIsNoLeftoverAndHoldsItsFolder()
    {
        var home = Place("ProgramFiles", "Acme Paint");
        var dll = File_("ProgramFiles", @"Acme Paint\shared.dll");
        var mine = File_("ProgramFiles", @"Acme Paint\mine.dll");
        using (var counts = Key("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs"))
        {
            counts.SetValue(dll, 2, RegistryValueKind.DWord);
            counts.SetValue(mine, 1, RegistryValueKind.DWord);
        }

        var found = Scan().Find(new Trail("Acme Paint") { Homes = [home] }, deep: true);

        Assert.DoesNotContain(found, item => item.Value == dll);
        Assert.Contains(found, item => item.Value == mine);
        var folder = found.Single(item => item.Path.Equals(home, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(LeftoverScan.StillShared, folder.Held);
        Assert.False(folder.Selected);
    }

    [Fact]
    public void AFolderARunningProgramUsesIsNotSelected()
    {
        var home = Place("ProgramFiles", "Acme Paint");
        var exe = File_("ProgramFiles", @"Acme Paint\helper.exe");

        var folder = Scan(running: [exe]).Find(new Trail("Acme Paint") { Homes = [home] }, deep: false).Single(item => item.Path.Equals(home, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(LeftoverScan.StillRunning, folder.Held);
        Assert.False(folder.Selected);
    }

    [Fact]
    public void AFolderFoundByNameThatSomethingLiveRunsFromIsNotAFind()
    {
        // Same name as the app, but a service still runs from it: it is some other program's.
        var exe = File_("ProgramData", @"Acme Paint\agent.exe");
        using (var service = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\SomeAgent")) service.SetValue("ImagePath", exe);
        var scan = Scan();

        var found = scan.Find(new Trail("Acme Paint") { Homes = [Place("ProgramFiles", "Acme Paint")], Names = ["Acme Paint"] }, deep: true);

        Assert.DoesNotContain(found, item => item.Path.Equals(Place("ProgramData", "Acme Paint"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WhatOnlyHasItsNameIsNeverSelected()
    {
        File_("AppData", @"Acme Paint\settings.ini");

        var found = Scan().Find(new Trail("Acme Paint") { Names = ["Acme Paint"] }, deep: false);

        var folder = found.Single(item => item.Path.Equals(Place("AppData", "Acme Paint"), StringComparison.OrdinalIgnoreCase));
        Assert.True(folder.Likely);
        Assert.False(folder.Selected);
    }

    [Theory]
    [InlineData(@"EasyAntiCheat\EasyAntiCheat.exe", "EasyAntiCheat")]
    [InlineData(@"BattlEye\BEService.exe", "BEService")]
    [InlineData(@"Riot Vanguard\vgc.exe", "vgc")]
    [InlineData(@"FACEIT\faceitservice.exe", "FACEIT")]
    public void AntiCheatInsideAnAppsFolderIsNeverFoundNorListed(string inside, string name)
    {
        var home = Place("ProgramFiles", "Acme Game");
        var exe = Path.Combine(home, inside);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "x");
        using (var service = Key("HKLM", $@"SYSTEM\CurrentControlSet\Services\{name}")) service.SetValue("ImagePath", exe);
        Link("Desktop", name + ".lnk", exe);
        var scan = Scan();
        scan.AddTestTask($@"\{name} updater", exe);

        var found = scan.Find(new Trail("Acme Game") { Homes = [home] }, deep: true);

        Assert.DoesNotContain(found, item => item.Path.Contains(name, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, item => item.Kind is LeftoverKind.Service or LeftoverKind.Task);
    }

    [Theory]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\vgk", true)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\BEDaisy", true)]
    [InlineData(@"C:\Program Files\Riot Vanguard", false)]
    [InlineData(@"C:\ProgramData\BattlEye", false)]
    public void WhateverCarriesAnAntiCheatIsRefusedRightBeforeItGoes(string path, bool isKey)
    {
        var scan = Scan();
        var item = new Leftover(path, isKey, null, true);
        Assert.Equal(LeftoverScan.Barred, scan.Recheck(item));
        Assert.Null(scan.AdminCommand(item));
    }

    [Fact]
    public void ADriverPointingIntoAnAppsFolderIsNeverItsLeftover()
    {
        var home = Place("ProgramFiles", "Acme Paint");
        File_("ProgramFiles", @"Acme Paint\acme.sys");
        using (var driver = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\AcmeDriver"))
        {
            driver.SetValue("ImagePath", home + @"\acme.sys");
            driver.SetValue("Type", 1, RegistryValueKind.DWord);
        }

        var found = Scan().Find(new Trail("Acme Paint") { Homes = [home] }, deep: true);

        // No Start at all: Windows' own way of loading it is unknown, so it is never offered.
        Assert.DoesNotContain(found, item => item.Path.EndsWith("AcmeDriver", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ADriverTheAppLeftInItsFolderIsShownButNeverTakenByOnePress(int start)
    {
        var home = Place("ProgramFiles", "Acme Paint");
        File_("ProgramFiles", @"Acme Paint\acme.sys");
        using (var driver = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\AcmeDriver"))
        {
            driver.SetValue("ImagePath", @"\??\" + home + @"\acme.sys");
            driver.SetValue("Type", 1, RegistryValueKind.DWord);
            driver.SetValue("Start", start, RegistryValueKind.DWord);
        }
        var scan = Scan();

        var found = scan.Find(new Trail("Acme Paint") { Homes = [home] }, deep: true).Single(item => item.Path.EndsWith("AcmeDriver", StringComparison.Ordinal));

        Assert.Equal(LeftoverKind.Driver, found.Kind);
        Assert.False(found.Selected);
        Assert.Equal(LeftoverScan.DriverHeld, found.Held);
        Assert.Empty(AutoClear.Its([found]));
        Assert.Equal("sc stop \"AcmeDriver\" & sc delete \"AcmeDriver\"", scan.AdminCommand(found));
    }

    [Theory]
    [InlineData(0, "AcmeDriver", @"Acme Paint\acme.sys")]     // boot
    [InlineData(1, "AcmeDriver", @"Acme Paint\acme.sys")]     // system
    [InlineData(3, "BEDaisy", @"Acme Paint\BEDaisy.sys")]     // BattlEye's, by name
    [InlineData(3, "vgk", @"Acme Paint\vgk.sys")]             // Vanguard's
    [InlineData(3, "AcmeGuard", @"Acme Paint\EasyAntiCheat\eac.sys")] // anti-cheat by its path
    public void ABootDriverOrAntiCheatsIsNeverFound(int start, string name, string file)
    {
        var home = Place("ProgramFiles", "Acme Paint");
        var sys = File_("ProgramFiles", file);
        using (var driver = Key("HKLM", $@"SYSTEM\CurrentControlSet\Services\{name}"))
        {
            driver.SetValue("ImagePath", sys);
            driver.SetValue("Type", 1, RegistryValueKind.DWord);
            driver.SetValue("Start", start, RegistryValueKind.DWord);
        }

        var found = Scan().Find(new Trail("Acme Paint") { Homes = [home] }, deep: true);

        Assert.DoesNotContain(found, item => item.IsKey && item.Path.EndsWith('\\' + name, StringComparison.Ordinal));
        Assert.DoesNotContain(found, item => item.Kind == LeftoverKind.Driver);
    }

    [Fact]
    public void ADriverIsOnlyEverTheAppsWhenItsFileIsInTheAppsFolder()
    {
        var home = Place("ProgramFiles", "Acme Paint");
        File_("ProgramFiles", @"Acme Paint\acmepaint.exe");
        var elsewhere = File_("ProgramFiles", @"Other\other.sys");
        using (var driver = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\OtherDriver"))
        {
            driver.SetValue("ImagePath", elsewhere);
            driver.SetValue("Type", 1, RegistryValueKind.DWord);
            driver.SetValue("Start", 3, RegistryValueKind.DWord);
        }

        Assert.DoesNotContain(Scan().Find(new Trail("Acme Paint") { Homes = [home], Names = ["Acme Paint"] }, deep: true), item => item.Kind == LeftoverKind.Driver);
    }

    [Fact]
    public void AFoundTaskIsWrittenDownBeforeItGoesAndNamesOnlyWhatItRan()
    {
        var home = Place("ProgramFiles", "Acme Paint");
        File_("ProgramFiles", @"Acme Paint\updater.exe");
        var scan = new LeftoverScan(Places, _hkcu, _hklm) { Running = () => [], DriveThere = _ => true, TaskBackupFolder = Place("LocalAppData", "cleanup") };
        scan.AddTestTask(@"\Acme\Update it's", home + @"\updater.exe");

        var task = scan.Find(new Trail("Acme Paint") { Homes = [home] }, deep: true).Single(item => item.Kind == LeftoverKind.Task);
        var file = scan.TaskBackupFile("Acme Paint", task.Path);

        Assert.Equal(home + @"\updater.exe", scan.TaskExe(task.Path));
        Assert.NotNull(file);
        Assert.StartsWith(Place("LocalAppData", "cleanup"), file);
        Assert.EndsWith(".xml", file, StringComparison.Ordinal);
        // The export step carries the task's names as literals inside its encoded script: the quote in its name stays text.
        var step = LeftoverScan.TaskExportStep(task.Path, file!);
        Assert.NotNull(step);
        var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(step!.Split(' ')[^1]));
        Assert.Contains(@"-TaskPath '\Acme\' -TaskName 'Update it''s'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-Force", script, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsOwnScheduledTasksAreNeverLeftovers()
    {
        var home = Place("ProgramFiles", "Acme Paint");
        File_("ProgramFiles", @"Acme Paint\acmepaint.exe");
        var scan = Scan();
        scan.AddTestTask(@"\Microsoft\Windows\Acme", home + @"\acmepaint.exe");

        Assert.DoesNotContain(scan.Find(new Trail("Acme Paint") { Homes = [home] }, deep: true), item => item.Kind == LeftoverKind.Task);
    }

    // ---- what points at a program that's gone ----

    private string Link(string place, string name, string target)
    {
        // A shortcut as MS-SHLLINK writes one: the header, then its LinkInfo with the whole local path.
        var path = Place(place, name);
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

    // Only a name-relative path, as Riot's shortcuts have: resolved beside the shortcut it leads nowhere real.
    private string RelativeLink(string place, string name, string relative)
    {
        var path = Place(place, name);
        var words = System.Text.Encoding.Unicode.GetBytes(relative);
        var bytes = new byte[0x4C + 2 + words.Length];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), 0x4Cu);
        BitConverter.TryWriteBytes(bytes.AsSpan(0x14), 0x88u);
        BitConverter.TryWriteBytes(bytes.AsSpan(0x4C), (ushort)relative.Length);
        words.CopyTo(bytes, 0x4E);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // Each of these was wrongly called gone on a real PC.
    [Fact]
    public void AShortcutOnlyCountsAsDeadWhenItsWholeTargetIsAnExeThatIsntThere()
    {
        Directory.CreateDirectory(Place("Desktop", ""));
        // A help file that was never installed, in a folder with a space: a missing document is no program gone.
        Link("Desktop", "Help.lnk", Place("ProgramFiles", @"Acme Tool\help.chm"));
        Link("Desktop", "Notes.lnk", Place("ProgramFiles", @"Acme Tool\ReleaseNotes.html"));
        // A real exe in a folder with spaces: read whole, not cut at the first space.
        var there = File_("ProgramFiles", @"Acme Tool\tool.exe");
        Link("Desktop", "Tool.lnk", there);
        // Riot's: no whole path at all.
        RelativeLink("Desktop", "Riot Client.lnk", @"Riot Client\RiotClientServices.exe");
        RelativeLink("Desktop", "Game.lnk", @"..\..\Riot Games\game.exe");

        Assert.Empty(Scan().FindDead([]));
    }

    [Fact]
    public void AStoresFolderIsShownButNotTakenInOnePress()
    {
        var home = Place("ProgramFiles", "Acme Store");
        File_("ProgramFiles", @"Acme Store\steamapps\common\Game\game.exe");

        var folder = Scan().Find(new Trail("Acme Store") { Homes = [home], HomeHeld = "Your games and saves may be inside" }, deep: false)
            .Single(item => item.Path.Equals(home, StringComparison.OrdinalIgnoreCase));

        Assert.False(folder.Selected);
        Assert.Equal("Your games and saves may be inside", folder.Held);
    }

    [Fact]
    public void WhatPointsAtAProgramThatsGoneIsFoundAndWhatIsThereIsNot()
    {
        var goneExe = Place("ProgramFiles", @"Old Tool\old.exe");
        var hereExe = File_("ProgramFiles", @"New Tool\new.exe");
        var dead = Link("Desktop", "Old.lnk", goneExe);
        var live = Link("Desktop", "New.lnk", hereExe);
        using (var service = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\OldSvc")) service.SetValue("ImagePath", $"\"{goneExe}\" /run");
        using (var service = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\NewSvc")) service.SetValue("ImagePath", hereExe);
        using (var paths = Key("HKCU", @"Software\Microsoft\Windows\CurrentVersion\App Paths\old.exe")) paths.SetValue("", goneExe);
        var scan = Scan();
        scan.AddTestTask(@"\Old Tool Update", goneExe);
        scan.AddTestTask(@"\New Tool Update", hereExe);

        var found = scan.FindDead([]);

        Assert.Contains(found, item => item.Path == dead && item.Selected);
        Assert.Contains(found, item => item.Path.EndsWith("OldSvc", StringComparison.Ordinal) && item.Kind == LeftoverKind.Service && item.Selected);
        Assert.Contains(found, item => item.Path.EndsWith(@"App Paths\old.exe", StringComparison.Ordinal) && item.Selected);
        Assert.Contains(found, item => item.Path == @"\Old Tool Update" && item.Selected);
        Assert.DoesNotContain(found, item => item.Path == live || item.Path.EndsWith("NewSvc", StringComparison.Ordinal) || item.Path == @"\New Tool Update");
    }

    [Fact]
    public void AProgramOnADriveThatIsntPluggedInIsNotGone()
    {
        Link("Desktop", "Game.lnk", @"E:\Games\Acme\game.exe");
        using (var service = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\OnE")) service.SetValue("ImagePath", @"E:\Tools\tool.exe");

        Assert.Empty(Scan(drive: _ => false).FindDead([]));
    }

    [Fact]
    public void AGoneProgramsFolderIsFoundOnlyByItsEvidenceAndNeverSelected()
    {
        var left = File_("ProgramFiles", @"Old Tool\settings.ini");
        File_("ProgramFiles", @"Unrelated Tool\settings.ini");
        File_("AppData", @"Portable App\settings.ini");   // no entry anywhere: never swept
        Link("Desktop", "Old.lnk", Place("ProgramFiles", @"Old Tool\old.exe"));

        var found = Scan().FindDead([]);

        var folder = found.Single(item => item.Kind == LeftoverKind.Folder);
        Assert.Equal(Path.GetDirectoryName(left), folder.Path);
        Assert.False(folder.Selected);
        Assert.DoesNotContain(found, item => item.Path.Contains("Unrelated", StringComparison.Ordinal) || item.Path.Contains("Portable", StringComparison.Ordinal));
    }

    [Fact]
    public void WhatIsMissingFromAFolderAnInstalledProgramStillHasIsThatProgramsToRepair()
    {
        // A program still installed, one of its files gone: broken, not gone, and not Exo's to clear.
        var home = Place("ProgramFiles", "Acme Paint");
        File_("ProgramFiles", @"Acme Paint\acmepaint.exe");
        Link("Desktop", "Paint.lnk", home + @"\missing.exe");

        Assert.Empty(Scan().FindDead([home]));
    }

    [Theory]
    [InlineData(@"""C:\Program Files\Acme\a.exe"" --flag", @"C:\Program Files\Acme\a.exe")]
    [InlineData(@"C:\Program Files\Acme\a.exe --flag", @"C:\Program Files\Acme\a.exe")]
    [InlineData(@"\??\C:\Acme\a.exe", @"C:\Acme\a.exe")]
    [InlineData(@"svchost.exe -k netsvcs", null)]
    [InlineData(@"C:\Program Files\Acme\help.chm", null)]
    [InlineData(@"""C:\Program Files\Acme\help.chm""", null)]
    [InlineData(@"system32\drivers\acme.sys", null)]
    [InlineData(@"\\server\share\a.exe", null)]
    [InlineData("", null)]
    public void ACommandsProgramIsAFullPathOrNothing(string command, string? target) => Assert.Equal(target, LeftoverScan.CommandTarget(command));

    [Fact]
    public void ThePageSeesAPlacesNameNeverAUsersFolderAndNoEndlessPath()
    {
        var scan = Scan();
        var said = scan.Said(new Leftover(Place("LocalAppData", @"Acme\cache"), false, null, false, As: LeftoverKind.Folder));
        Assert.Equal(@"AppData\Local\Acme\cache", said);
        Assert.DoesNotContain(_root, said, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"HKCU\Software\Acme › Path", scan.Said(new Leftover(@"HKCU\Software\Acme", true, "Path", false)));
        Assert.Equal(200, scan.Said(new Leftover(Place("LocalAppData", new string('a', 300)), false, null, false, As: LeftoverKind.Folder)).Length);
    }

    [Fact]
    public void TheElevatedRecycleNamesEachPathAsALiteralAndRefusesAsTheOtherStepsDo()
    {
        var scan = Scan();
        var plain = Place("ProgramFiles", "Acme Paint");
        var quoted = Place("ProgramFiles", "Acme's Tool");
        File_("ProgramFiles", @"Acme Paint\a.txt");
        File_("ProgramFiles", @"Acme's Tool\a.txt");

        var steps = scan.RecycleAsAdmin([new Leftover(plain, false, null, true), new Leftover(quoted, false, null, true), new Leftover(Place("ProgramFiles", "Not There"), false, null, true)]);

        var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(Assert.Single(steps).Split(' ')[^1]));
        Assert.Contains($"'{plain}'", script);
        Assert.Contains($"'{quoted.Replace("'", "''")}'", script);
        Assert.DoesNotContain("Not There", script);
        Assert.Contains("SendToRecycleBin", script);
        Assert.DoesNotContain("DeletePermanently", script);
        // cmd's first quote would pair with the last on the line: the step starts bare.
        Assert.DoesNotContain("\"", steps[0]);
        // Whatever Exo refuses by hand it refuses here.
        Assert.Empty(scan.RecycleAsAdmin([new Leftover(@"C:\Windows\System32", false, null, true)]));
    }

    [Fact]
    public void ManyElevatedPathsStayUnderCmdsLineLimit()
    {
        var scan = Scan();
        var items = Enumerable.Range(0, 40).Select(i =>
        {
            File_("ProgramFiles", $@"{new string('a', 150)}{i}\a.txt");
            return new Leftover(Place("ProgramFiles", $"{new string('a', 150)}{i}"), false, null, true);
        }).ToList();

        var steps = scan.RecycleAsAdmin(items);

        Assert.True(steps.Count > 1);
        Assert.All(steps, step => Assert.True(step.Length < 7800, $"{step.Length} long"));
        var decoded = string.Join("\n", steps.Select(step => System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(step.Split(' ')[^1]))));
        Assert.All(items, item => Assert.Contains($"'{item.Path}'", decoded));

        var quoted = Enumerable.Range(0, 30).Select(i => $@"C:\Program Files\{new string('\'', 80)}App{i}").ToList();
        var quotedSteps = LeftoverScan.RecycleSteps(quoted);
        Assert.True(quotedSteps.Count > 1);
        Assert.All(quotedSteps, step => Assert.True(step.Length < 7800, $"{step.Length} long"));

        var longFolder = Place("ProgramFiles", $@"{new string('x', 130)}\{new string('y', 130)}");
        File_("ProgramFiles", $@"{new string('x', 130)}\{new string('y', 130)}\a.txt");
        Assert.True(longFolder.Length > 260);
        var longSteps = scan.RecycleAsAdmin([new Leftover(longFolder, false, null, true)]);
        Assert.Single(longSteps);
        Assert.Contains($"'{longFolder}'", System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(longSteps[0].Split(' ')[^1])));
    }

    [Fact]
    public void FindDeadSkipsProtectedSecurityServicesAndDeletePrunesEmptyStartMenuFolderAndCookies()
    {
        // Protected / Defender PPL services with missing binary paths must never be flagged as dead leftovers.
        using (var ppl = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\CustomPplSvc"))
        {
            ppl.SetValue("ImagePath", Place("ProgramFiles", @"MissingSecurity\guard.exe"));
            ppl.SetValue("Type", 0x10, RegistryValueKind.DWord);
            ppl.SetValue("LaunchProtected", 2, RegistryValueKind.DWord);
        }
        using (var defender = Key("HKLM", @"SYSTEM\CurrentControlSet\Services\WinDefend"))
        {
            defender.SetValue("ImagePath", Place("ProgramFiles", @"Windows Defender\MsMpEng.exe"));
            defender.SetValue("Type", 0x10, RegistryValueKind.DWord);
        }

        var dead = Scan().FindDead([]);
        Assert.DoesNotContain(dead, d => d.Path.EndsWith(@"\CustomPplSvc", StringComparison.OrdinalIgnoreCase)
            || d.Path.EndsWith(@"\WinDefend", StringComparison.OrdinalIgnoreCase));

        // Start Menu shortcut deletion prunes its empty app subfolder, leaving Programs root intact.
        var shortcut = File_("AppData", @"Microsoft\Windows\Start Menu\Programs\AcmeSuite\Acme.lnk");
        var appMenuFolder = Path.GetDirectoryName(shortcut)!;
        var programsRoot = Path.GetDirectoryName(appMenuFolder)!;
        var scan = Scan();
        var shortcutItem = new Leftover(shortcut, false, null, false, As: LeftoverKind.Shortcut);
        Assert.True(scan.Delete(shortcutItem));
        Assert.False(Directory.Exists(appMenuFolder));
        Assert.True(Directory.Exists(programsRoot));

        // Uninstalled app folder containing Electron Cookies/Local State is cleared on uninstall cleanup, while .ssh remains blocked.
        var appDataDir = Path.Combine(Place("AppData", "UninstalledChat"), "");
        File_("AppData", @"UninstalledChat\Cookies");
        File_("AppData", @"UninstalledChat\Local State");
        Assert.True(scan.Delete(new Leftover(appDataDir.TrimEnd('\\'), false, null, false)));
        Assert.False(Directory.Exists(appDataDir));
    }

    private static void Cmd(string command)
    {
        using var cmd = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c " + command)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath(),
        });
        cmd?.WaitForExit(30_000);
    }

    private static string Rot13(string text) => new([.. text.Select(c => c switch
    {
        >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
        >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
        _ => c,
    })]);

    public void Dispose()
    {
        // A junction first, on its own: Directory.Delete's walk takes one for a volume's mount point, fails, and leaves the root.
        foreach (var link in _links)
            try { DeletionGate.DeleteDirectory(link); } catch (IOException) { }
        try { DeletionGate.DeleteDirectory(_root, true); } catch (IOException) { }
        Registry.CurrentUser.DeleteSubKeyTree(_hkcu[..^5], false);
    }
}
