using System.Text;
using ExoLauncher.Helpers;
using ExoLauncher.Services;
using Microsoft.Win32;

namespace ExoLauncher.Tests;

/// <summary>
/// What PR #31 left for later, on fixtures only (temp folders, a test key; never this PC's packages, drivers or defaults).
/// Ways it could fail, worst first: a part of Windows (the Store, winget, a framework, WebView2, Gaming Services, Xbox sign-in,
/// Windows Security, the shell) is offered for removal; a removal for every account is said to be undoable, or one for you
/// that can't come back is; a name that isn't a package's reaches the elevated script; what needs Windows' permission runs
/// without the restore point that was asked for, or a second restore point is asked for inside Windows' one-a-day limit; a
/// driver's store package goes before it is saved, goes with /force or /uninstall, or its service goes when the package
/// couldn't; Put back adds a driver from anywhere but the administrators-only folder; a file type's signed default is
/// written, or another app's default is called dead; a firewall rule of someone else's group goes, or a group sibling is
/// pre-selected; anything less than certain is pre-selected.
/// </summary>
public sealed class UninstallerDepthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-depth-" + Guid.NewGuid().ToString("N"));
    private readonly string _test = @"Software\ExoLauncherTests\" + Guid.NewGuid().ToString("N");
    private string Hkcu => _test + @"\HKCU";
    private string Hklm => _test + @"\HKLM";

    private Dictionary<string, string> Places => new(StringComparer.OrdinalIgnoreCase)
    {
        ["LocalAppData"] = Path.Combine(_root, "Local"),
        ["AppData"] = Path.Combine(_root, "Roaming"),
        ["ProgramFiles"] = Path.Combine(_root, "Program Files"),
        ["ProgramData"] = Path.Combine(_root, "ProgramData"),
        ["Temp"] = Path.Combine(_root, "Temp"),
    };

    public UninstallerDepthTests()
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

    private RegistryKey Key(string hive, string path) => Registry.CurrentUser.CreateSubKey((hive == "HKLM" ? Hklm : Hkcu) + '\\' + path);

    private LeftoverScan Scan() => new(Places, Hkcu, Hklm) { Running = () => [], DriveThere = _ => true };

    // ---- Store and preinstalled apps ----

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.StorePurchaseApp")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.Winget.Source")]
    [InlineData("Microsoft.VCLibs.140.00.UWPDesktop")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("Microsoft.NET.Native.Framework.2.2")]
    [InlineData("Microsoft.WindowsAppRuntime.1.6")]
    [InlineData("MicrosoftCorporationII.WinAppRuntime.Main.1.5")]
    [InlineData("Microsoft.MicrosoftEdge.Stable")]
    [InlineData("Microsoft.WebView2")]
    [InlineData("Microsoft.GamingServices")]
    [InlineData("Microsoft.GamingApp")]
    [InlineData("Microsoft.XboxIdentityProvider")]
    [InlineData("Microsoft.Xbox.TCUI")]
    [InlineData("Microsoft.XboxGameCallableUI")]
    [InlineData("Microsoft.SecHealthUI")]
    [InlineData("Microsoft.Windows.ShellExperienceHost")]
    [InlineData("Microsoft.Windows.StartMenuExperienceHost")]
    [InlineData("Microsoft.Windows.Search")]
    [InlineData("MicrosoftWindows.Client.CBS")]
    [InlineData("Microsoft.AAD.BrokerPlugin")]
    [InlineData("Microsoft.LanguageExperiencePackfr-FR")]
    [InlineData("Microsoft.HEVCVideoExtension")]
    [InlineData("1527c705-839a-4832-9118-54d4Bd6a0c89")]
    [InlineData("windows.immersivecontrolpanel")]
    public void WhatWindowsStoreAppsOrGamesNeedIsNeverOffered(string name) => Assert.NotNull(StorePackages.Never(name));

    [Theory]
    [InlineData("Microsoft.BingNews")]
    [InlineData("king.com.CandyCrushSaga")]
    [InlineData("Clipchamp.Clipchamp")]
    [InlineData("Microsoft.Windows.Photos")]
    [InlineData("MicrosoftWindows.Client.WebExperience")]
    [InlineData("Microsoft.WindowsCalculator")]
    public void OptionalAppsAreOffered(string name) => Assert.Null(StorePackages.Never(name));

    [Fact]
    public void APackageWindowsSignedAsItsOwnIsNeverOffered() => Assert.NotNull(StorePackages.Never("Contoso.Anything", system: true));

    [Theory]
    [InlineData("Microsoft.BingNews_4.55.1.0_x64__8wekyb3d8bbwe", "Microsoft.BingNews", "Microsoft.BingNews_8wekyb3d8bbwe")]
    [InlineData("Clipchamp.Clipchamp_4.4.1.0_neutral_~_yxz26nhyzhsrt", "Clipchamp.Clipchamp", "Clipchamp.Clipchamp_yxz26nhyzhsrt")]
    public void AFullNameGivesItsPackageAndFamily(string full, string name, string family) => Assert.Equal((name, family), StorePackages.Parts(full));

    [Theory]
    [InlineData("Microsoft.BingNews_4.55.1.0_neutral_split.scale-125_8wekyb3d8bbwe")]
    [InlineData("Microsoft.BingNews_8wekyb3d8bbwe")]
    [InlineData("Evil'Name_1.0.0.0_x64__8wekyb3d8bbwe")]
    [InlineData("")]
    public void AResourcePackageOrAnythingElseIsNoPackage(string full) => Assert.Null(StorePackages.Parts(full));

    [Fact]
    public void EveryAccountsPackagesAreOneRowEachAndWhatsNeverOfferedIsOnlyCounted()
    {
        var mine = new[]
        {
            new PackageSeen("Microsoft.BingNews_4.55.1.0_x64__8wekyb3d8bbwe", "Microsoft News", "Microsoft Corporation", false, false),
            new PackageSeen("Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe", "VCLibs", "Microsoft", true, false),
            new PackageSeen("Microsoft.WindowsStore_22410.1401.5.0_x64__8wekyb3d8bbwe", "Microsoft Store", "Microsoft", false, false),
        };
        IReadOnlyCollection<string>[] others =
        [
            ["Microsoft.BingNews_4.50.0.0_x64__8wekyb3d8bbwe", "Microsoft.WindowsMaps_11.2.0.0_x64__8wekyb3d8bbwe", "Microsoft.WindowsStore_22410.1401.5.0_x64__8wekyb3d8bbwe"],
            ["Microsoft.BingNews_4.55.1.0_x64__8wekyb3d8bbwe"],
        ];
        var provisioned = new[] { "Microsoft.BingNews_4.55.1.0_neutral_~_8wekyb3d8bbwe", "Clipchamp.Clipchamp_4.4.1.0_neutral_~_yxz26nhyzhsrt", "Microsoft.Todos_2.1.0.0_neutral_~_8wekyb3d8bbwe" };
        var (packages, hidden) = StorePackages.Gather(mine, others, provisioned, new HashSet<string>(["Microsoft.Todos_8wekyb3d8bbwe"], StringComparer.OrdinalIgnoreCase));

        var news = Assert.Single(packages, package => package.Name == "Microsoft.BingNews");
        Assert.Equal(("Microsoft News", 2, true), (news.Display, news.Others, news.ForMe));
        Assert.Equal("Microsoft.BingNews_4.55.1.0_neutral_~_8wekyb3d8bbwe", news.Provisioned);
        var maps = Assert.Single(packages, package => package.Name == "Microsoft.WindowsMaps");
        Assert.Equal(("Windows Maps", false, true), (maps.Display, maps.ForMe, maps.Staged));
        Assert.Contains(packages, package => package.Name == "Clipchamp.Clipchamp" && !package.ForMe && package.Provisioned is not null);
        // Taken out of Windows' image already: new accounts don't get it.
        Assert.DoesNotContain(packages, package => package.Name == "Microsoft.Todos");
        // The Store is never a row; a framework isn't an app.
        Assert.DoesNotContain(packages, package => package.Name.StartsWith("Microsoft.WindowsStore", StringComparison.Ordinal) || package.Name.StartsWith("Microsoft.VCLibs", StringComparison.Ordinal));
        Assert.Equal(1, hidden);
    }

    [Fact]
    public void ThePreviewSaysPlainlyWhatCanComeBackAndWhatCannot()
    {
        var staged = new StorePackage("Microsoft.BingNews_8wekyb3d8bbwe", "Microsoft.BingNews", "News", null, "Microsoft.BingNews_4.55.1.0_x64__8wekyb3d8bbwe", 0, "Microsoft.BingNews_4.55.1.0_neutral_~_8wekyb3d8bbwe");
        var alone = staged with { Provisioned = null };
        var notMine = staged with { Mine = null };

        Assert.True(StorePackages.Line(staged, everyone: false)!.Undoable);
        Assert.Contains("new accounts still get it", StorePackages.Line(staged, everyone: false)!.Does, StringComparison.Ordinal);
        Assert.False(StorePackages.Line(alone, everyone: false)!.Undoable);
        Assert.Contains("Microsoft Store", StorePackages.Line(alone, everyone: false)!.Back, StringComparison.Ordinal);
        // For every account is never said to come back here.
        Assert.False(StorePackages.Line(staged, everyone: true)!.Undoable);
        Assert.Contains("Can't be undone here", StorePackages.Line(staged, everyone: true)!.Back, StringComparison.Ordinal);
        // Nothing of it is yours to remove for you.
        Assert.Null(StorePackages.Line(notMine, everyone: false));
    }

    [Fact]
    public void TheElevatedRemovalNamesOnlyThatPackageAndRefusesAnyOther()
    {
        var news = new StorePackage("Microsoft.BingNews_8wekyb3d8bbwe", "Microsoft.BingNews", "News", null, null, 1, "Microsoft.BingNews_4.55.1.0_neutral_~_8wekyb3d8bbwe");
        var step = StorePackages.EveryoneStep(news);
        Assert.NotNull(step);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(step![(step.LastIndexOf(' ') + 1)..]));
        Assert.Contains("Get-AppxPackage -AllUsers -Name 'Microsoft.BingNews'", script, StringComparison.Ordinal);
        Assert.Contains("$_.PackageFamilyName -eq 'Microsoft.BingNews_8wekyb3d8bbwe'", script, StringComparison.Ordinal);
        Assert.Contains("Remove-AppxPackage -Package $_.PackageFullName -AllUsers", script, StringComparison.Ordinal);
        Assert.Contains("$_.PackageName -eq 'Microsoft.BingNews_4.55.1.0_neutral_~_8wekyb3d8bbwe'}|Remove-AppxProvisionedPackage -Online", script, StringComparison.Ordinal);

        Assert.Null(StorePackages.EveryoneStep(news with { Family = "Microsoft.BingNews_8wekyb3d8bbwe';Remove-Item C:\\" }));
        Assert.Null(StorePackages.EveryoneStep(news with { Name = "Microsoft.Other" }));
        Assert.Null(StorePackages.EveryoneStep(news with { Provisioned = "Microsoft.Other_1.0.0.0_neutral_~_8wekyb3d8bbwe" }));
        Assert.Null(StorePackages.EveryoneStep(new StorePackage("Microsoft.WindowsStore_8wekyb3d8bbwe", "Microsoft.WindowsStore", "Store", null, null, 1, null)));
        Assert.Equal("ms-windows-store://pdp/?PFN=Microsoft.BingNews_8wekyb3d8bbwe", StorePackages.StoreLink(news.Family));
        Assert.Null(StorePackages.StoreLink("x\" & calc"));
    }

    [Fact]
    public void AnAppOnlyOtherAccountsHaveIsNamedFromItsPackage()
    {
        Assert.Equal("Windows Maps", StorePackages.Pretty("Microsoft.WindowsMaps"));
        Assert.Equal("Candy Crush Saga", StorePackages.Pretty("king.com.CandyCrushSaga"));
    }

    // ---- restore points ----

    [Fact]
    public void BehindARestorePointEveryStepWaitsForItsAnswer()
    {
        var steps = RestorePoint.Gated(@"C:\Temp\exo-1.restore", ["reg delete \"HKLM\\SOFTWARE\\Acme\" /f", "sc stop \"x\" & sc delete \"x\""]);
        Assert.Equal(3, steps.Count);
        Assert.Contains("-EncodedCommand", steps[0], StringComparison.Ordinal);
        Assert.Equal("findstr /x \"made recent\" \"C:\\Temp\\exo-1.restore\" >nul && (reg delete \"HKLM\\SOFTWARE\\Acme\" /f)", steps[1]);
        // A step with & of its own stays whole inside the parentheses: sc delete never runs without the point.
        Assert.EndsWith("&& (sc stop \"x\" & sc delete \"x\")", steps[2], StringComparison.Ordinal);
    }

    [Fact]
    public void OnePointADayStandsForTheRestOfIt()
    {
        var made = new DateTime(2026, 11, 19, 8, 0, 0, DateTimeKind.Utc);
        Assert.True(RestorePoint.Standing(made.AddHours(23), made));
        Assert.False(RestorePoint.Standing(made.AddHours(24), made));
        Assert.False(RestorePoint.Standing(made.AddMinutes(-5), made));
        Assert.False(RestorePoint.Standing(made, null));
        Assert.Null(RestorePoint.Problem(RestorePoint.Read("recent")));
    }

    [Fact]
    public async Task WithoutTheRestorePointNothingThatNeedsThePromptGoes()
    {
        using (Key("HKLM", @"SOFTWARE\Acme Paint")) { }
        var item = new Leftover(@"HKLM\SOFTWARE\Acme Paint", true, null, true);
        var scan = Scan();
        IReadOnlyList<string>? asked = null;
        // Windows' prompt accepted, but nothing ran (no answer written): the point failed, so the step behind it didn't run.
        var done = await AutoClear.ClearAsync(scan, "Acme Paint", [item], steps => { asked = steps; return Task.FromResult(true); },
            new RemovalHistory(Path.Combine(_root, "removals.json"), _ => []), DateTime.UtcNow, restore: true);
        Assert.NotNull(asked);
        Assert.Contains("-EncodedCommand", asked![0], StringComparison.Ordinal);
        Assert.All(asked.Skip(1), step => Assert.StartsWith("findstr /x \"made recent\"", step, StringComparison.Ordinal));
        Assert.NotNull(done.Point);
        Assert.Single(done.Left);
        Assert.False(done.Declined);
    }

    // ---- drivers in the driver store ----

    private const string Inf = """
        [Version]
        Signature="$WINDOWS NT$"
        [Acme.Install.Services]
        AddService = AcmeFilter, 0x00000002, Acme_Service
        [Acme_Service]
        ServiceBinary = %13%\acmefilter.sys
        """;

    [Fact]
    public void ADriversStorePackageIsTheOemInfThatAddsItsServiceWithItsFile()
    {
        var infs = new[] { ("oem12.inf", Inf), ("oem13.inf", Inf.Replace("acmefilter.sys", "other.sys", StringComparison.Ordinal)),
            ("oem14.inf", Inf.Replace("AcmeFilter,", "OtherFilter,", StringComparison.Ordinal)), ("acme.inf", Inf), ("oem15.inf", "; AddService = AcmeFilter, acmefilter.sys in a comment") };
        Assert.Equal(["oem12.inf"], LeftoverScan.InfsFor("AcmeFilter", @"\SystemRoot\System32\drivers\acmefilter.sys", infs));
        Assert.Equal(["oem12.inf"], LeftoverScan.InfsFor("AcmeFilter", "System32\\drivers\\acmefilter.sys", [("OEM12.INF", Inf.Replace("AddService = AcmeFilter", "AddService = \"AcmeFilter\"", StringComparison.Ordinal))]));
        Assert.Empty(LeftoverScan.InfsFor("AcmeFilter", "", infs));
    }

    [Fact]
    public void ADriversPackageIsSavedBeforeItGoesAndItsServiceGoesOnlyIfThePackageDid()
    {
        var scan = Scan();
        var item = new Leftover(@"HKLM\SYSTEM\CurrentControlSet\Services\AcmeFilter", true, null, true, false, LeftoverKind.Driver) { Infs = ["oem12.inf"] };
        var command = scan.AdminCommand(item);
        Assert.NotNull(command);
        var (folder, infs) = scan.DriverBackups[item];
        Assert.Equal(["oem12.inf"], infs);
        Assert.StartsWith(scan.DriverBackupRoot + '\\', folder, StringComparison.OrdinalIgnoreCase);
        int At(string text) => command!.IndexOf(text, StringComparison.Ordinal);
        Assert.True(At($"mkdir \"{folder}\"") < At("/export-driver oem12.inf"));
        Assert.True(At($"takeown /f \"{folder}\" /a") < At("/export-driver oem12.inf"));
        Assert.True(At("/export-driver oem12.inf") < At("/delete-driver oem12.inf"));
        Assert.True(At("/delete-driver oem12.inf") < At("sc delete \"AcmeFilter\""));
        Assert.Contains("/delete-driver oem12.inf >nul && (sc stop", command, StringComparison.Ordinal);
        Assert.DoesNotContain("/force", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/uninstall", command, StringComparison.OrdinalIgnoreCase);
        // Anything but a driver store name stays, with why.
        var odd = item with { Infs = ["..\\acme.inf"] };
        Assert.Null(scan.AdminCommand(odd));
        Assert.Equal(LeftoverScan.Unquotable, scan.Stayed[odd]);
    }

    [Fact]
    public void PutBackAddsADriverOnlyFromItsOwnAdministratorsOnlyFolder()
    {
        var root = Path.Combine(_root, "DriverBackup");
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "acme.inf"), Inf);
        var step = RemovalHistory.DriverStepBack(new RemovedDriver(folder, ["oem12.inf"]), root);
        Assert.Equal($"\"{Path.Combine(Environment.SystemDirectory, "pnputil.exe")}\" /add-driver \"{folder}\\*.inf\" /subdirs", step);
        Assert.DoesNotContain("/install", step!, StringComparison.Ordinal);
        Assert.Null(RemovalHistory.DriverStepBack(new RemovedDriver(Path.Combine(_root, "Elsewhere", Guid.NewGuid().ToString("N")), ["oem12.inf"]), root));
        Assert.Null(RemovalHistory.DriverStepBack(new RemovedDriver(Path.Combine(root, "not-a-guid"), ["oem12.inf"]), root));
        Assert.Null(RemovalHistory.DriverStepBack(new RemovedDriver(Path.Combine(root, Guid.NewGuid().ToString("N")), ["oem12.inf"]), root));
    }

    // ---- file types and firewall groups ----

    [Fact]
    public void AFileTypeIsDeadOnlyWhenItsDefaultIsTheAppsOrNamesItAndIsGone()
    {
        var progIds = new HashSet<string>(["AcmePaint.Document"], StringComparer.OrdinalIgnoreCase);
        var choices = new[]
        {
            (".acme", "AcmePaint.Document"), (".acmx", @"Applications\acmepaint.exe"), (".acmz", "AcmePaint.Old"),
            (".txt", "txtfile"), (".png", "AppX43hnxtbyyps62jhe9sqpdzxn1790zetc"), (".old", "OtherApp.File"), ("noDot", "AcmePaint.Document"),
        };
        var registered = new HashSet<string>(["txtfile", "AppX43hnxtbyyps62jhe9sqpdzxn1790zetc"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal([".acme", ".acmx", ".acmz"], LeftoverScan.DeadChoices(choices, progIds, registered.Contains, ["Acme Paint"], ["acmepaint.exe"]));
    }

    [Theory]
    [InlineData("v2.30|Action=Allow|Dir=In|App=C:\\x.exe|Name=Acme|EmbedCtxt=Acme Paint|", "Acme Paint")]
    [InlineData("v2.30|Action=Allow|EmbedCtxt=@FirewallAPI.dll,-28502|", null)]
    [InlineData("v2.30|Action=Allow|EmbedCtxt=Steam|", null)]
    [InlineData("v2.30|Action=Allow|Name=Acme|", null)]
    public void AFirewallGroupIsTheAppsOwnNeverWindows(string rule, string? group) => Assert.Equal(group, LeftoverScan.FirewallGroup(rule));

    [Fact]
    public void ReviewSaysHowSureEachIsAndOnlyCertainIsPicked()
    {
        var home = Place("ProgramFiles", "Acme Paint");
        File_("ProgramFiles", @"Acme Paint\acmepaint.exe");
        File_("AppData", @"Acme Paint\settings.ini");
        File_("Temp", @"Acme Paint\log.txt");
        const string Rules = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
        using (var rules = Key("HKLM", Rules))
        {
            rules.SetValue("{A}", $"v2.30|Action=Allow|Active=TRUE|Dir=In|App={home}\\acmepaint.exe|Name=Acme Paint|EmbedCtxt=Acme Paint Group|");
            rules.SetValue("{B}", "v2.30|Action=Allow|Active=TRUE|Dir=In|Protocol=17|LPort=7777|Name=Acme Paint sync|EmbedCtxt=Acme Paint Group|");
            rules.SetValue("{C}", "v2.30|Action=Allow|Active=TRUE|Dir=In|Protocol=6|LPort=80|Name=Web|EmbedCtxt=Other Server|");
            rules.SetValue("{D}", "v2.30|Action=Allow|Active=TRUE|Dir=In|Protocol=6|LPort=445|Name=@FirewallAPI.dll,-1|EmbedCtxt=@FirewallAPI.dll,-28502|");
        }
        using (var command = Key("HKCU", @"Software\Classes\AcmePaint.Document\shell\open\command")) command.SetValue("", $"\"{home}\\acmepaint.exe\" \"%1\"");
        using (var choice = Key("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.acme\UserChoice")) choice.SetValue("ProgId", "AcmePaint.Document");
        using (var choice = Key("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.txt\UserChoice")) choice.SetValue("ProgId", "txtfile");

        var scan = Scan();
        var found = scan.Find(new Trail("Acme Paint") { Homes = [home], Names = ["Acme Paint", "Acme"], Exes = ["acmepaint.exe"] }, deep: true);

        Leftover Rule(string id) => found.Single(item => item.Value == id);
        Assert.Equal(Certainty.Certain, found.Single(item => item.Path == home).Sure);
        Assert.Equal("Certain: inside the app's folder", LeftoverScan.Said(Certainty.Certain, found.Single(item => item.Path == home).Because));
        Assert.Equal(Certainty.Certain, Rule("{A}").Sure);
        Assert.True(Rule("{A}").Selected);
        Assert.Equal(Certainty.Possible, Rule("{B}").Sure);
        Assert.False(Rule("{B}").Selected);
        Assert.DoesNotContain(found, item => item.Value is "{C}" or "{D}");
        Assert.Equal(Certainty.Likely, found.Single(item => item.Path == Place("AppData", "Acme Paint")).Sure);
        Assert.Equal(Certainty.Possible, found.Single(item => item.Path == Place("Temp", "Acme Paint")).Sure);
        Assert.All(found.Where(item => item.Selected), item => Assert.Equal(Certainty.Certain, item.Sure));
        // Its file type's default is read, never written: only .acme, whose default is its own ProgId.
        Assert.Equal([".acme"], scan.Defaults);
        using var still = Registry.CurrentUser.OpenSubKey(Hkcu + @"\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.acme\UserChoice");
        Assert.Equal("AcmePaint.Document", still?.GetValue("ProgId"));
    }

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_root, true); } catch (IOException) { }
        Registry.CurrentUser.DeleteSubKeyTree(_test, false);
    }
}
