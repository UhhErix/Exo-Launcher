using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// What Exo remembers of programs, in a temp file. Ways it could fail: an update (new version, new key, same folder or same
/// name) reads as a program gone, and its folder is offered as leftovers; a program mid-update, missing for a minute, is
/// called gone; one that only lost its entry but is still in its folder is called gone; one back again stays gone; anti-cheat, a driver, a runtime or Exo itself is remembered and looked for later;
/// what the user chose to leave is offered again; a run read from Windows is lost when it forgets, or an older run replaces a
/// newer one; a device path maps to the wrong drive, or an unknown one to a guess.
/// </summary>
public sealed class AppsMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-apps-memory-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private AppsMemory Memory() => new(Path.Combine(_root, "apps-seen.json"));

    private static InstalledProgram Program(string id, string name, string? location = null, string? publisher = "Acme", string? care = null) =>
        new(id, name, publisher, null, null, null, location, null, false, false, null, null, null, true, care, null, null);

    private static RememberedApp Known(string id, string name, string? location = null, string? publisher = "Acme", DateTime? gone = null) =>
        new(id, name, publisher, location, null, [], false, Now.AddDays(-30), gone);

    private static List<RememberedApp> Compare(IReadOnlyList<RememberedApp> known, params InstalledProgram[] now) => AppsMemory.Compare(known, now, Now, _ => ["a.exe"]);

    [Theory]
    [InlineData("acmepaint.exe", true)]
    [InlineData("unins000.exe", false)]
    [InlineData("Update.exe", false)]
    [InlineData("crashpad_handler.exe", false)]
    public void AProgramStillInItsFolderIsNotGoneWhateverItsEntrySays(string exe, bool still)
    {
        var folder = Path.Combine(_root, "Acme");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, exe), "x");
        Assert.Equal(still, AppsMemory.StillHasProgram(folder));
    }

    [Fact]
    public void NoFolderHoldsNoProgram()
    {
        Assert.False(AppsMemory.StillHasProgram(null));
        Assert.False(AppsMemory.StillHasProgram(Path.Combine(_root, "not there")));
    }

    [Fact]
    public void ANewProgramIsRememberedWithItsExes()
    {
        var app = Assert.Single(Compare([], Program("a", "Acme", @"C:\Program Files\Acme")));
        Assert.Equal(["a.exe"], app.Exes);
        Assert.Null(app.GoneAt);
    }

    [Fact]
    public void AProgramMissingIsMarkedButNotGoneUntilItsStayedMissing()
    {
        var marked = Assert.Single(Compare([Known("a", "Acme Paint")]));
        Assert.Equal(Now, marked.GoneAt);
        Assert.Empty(AppsMemory.Gone([marked], Now.AddMinutes(1)));
        Assert.Single(AppsMemory.Gone([marked], Now.Add(AppsMemory.GoneAfter)));
    }

    [Fact]
    public void OneBackAgainIsNoLongerGone() =>
        Assert.Null(Assert.Single(Compare([Known("a", "Acme", gone: Now.AddHours(-1))], Program("a", "Acme"))).GoneAt);

    [Fact]
    public void AnUpdateInTheSameFolderIsNotAProgramGone()
    {
        var next = Compare([Known("old", "Acme Paint 1.0", @"C:\Program Files\Acme")], Program("new", "Acme Paint 2.0", @"C:\Program Files\Acme\"));
        Assert.Equal("new", Assert.Single(next).Id);
        Assert.Null(next[0].GoneAt);
    }

    [Fact]
    public void AnUpdateToANewFolderByItsNameAndPublisherIsNotAProgramGone() =>
        Assert.Equal("new", Assert.Single(Compare([Known("old", "Acme Paint 1.0", @"C:\Acme\1")], Program("new", "Acme Paint version 2.0", @"C:\Acme\2"))).Id);

    [Fact]
    public void TheSameNameFromAnotherPublisherIsAnotherProgram() =>
        Assert.Contains(Compare([Known("old", "Studio", @"C:\Acme\Studio")], Program("new", "Studio", @"C:\Other\Studio", publisher: "Other")), app => app.Id == "old" && app.GoneAt == Now);

    [Theory]
    [InlineData("Riot Vanguard", null)]
    [InlineData("EasyAntiCheat", null)]
    [InlineData("Acme", InstalledPrograms.Cares.AntiCheat)]
    [InlineData("Acme", InstalledPrograms.Cares.Driver)]
    [InlineData("Microsoft Visual C++", InstalledPrograms.Cares.Runtime)]
    [InlineData("Exo Launcher", InstalledPrograms.Cares.Exo)]
    public void WhatIsNeverAnyonesToClearIsNeverRemembered(string name, string? care) => Assert.Empty(Compare([], Program("a", name, care: care)));

    [Fact]
    public void WhatTheUserLeftIsNotOfferedAgainAcrossARestart()
    {
        Memory().Leave([@"C:\Program Files\Acme|"]);
        Assert.True(Memory().Left(@"c:\program files\acme|"));
        Assert.False(Memory().Left(@"C:\Program Files\Other|"));
    }

    [Fact]
    public void ForgettingAProgramDropsIt()
    {
        var memory = Memory();
        memory.Note([Program("a", "Acme Paint"), Program("b", "Other Tool", publisher: "Other")], _ => []);
        memory.Forget(["a"]);
        Assert.DoesNotContain(Memory().Note([], _ => []), app => app.Id == "a");
        Assert.Null(Memory().WatchedSince("a"));
        Assert.NotNull(Memory().WatchedSince("b"));
    }

    [Fact]
    public void ARunIsKeptAfterWindowsForgetsItAndOnlyANewerOneReplacesIt()
    {
        var memory = Memory();
        var first = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        memory.Runs(new Dictionary<string, DateTime> { [@"C:\Acme\a.exe"] = first });
        Assert.Equal(first, Memory().Runs(new Dictionary<string, DateTime>())[@"c:\acme\a.exe"]);
        Assert.Equal(first, Memory().Runs(new Dictionary<string, DateTime> { [@"C:\Acme\a.exe"] = first.AddDays(-1) })[@"C:\Acme\a.exe"]);
        Assert.Equal(first.AddDays(1), Memory().Runs(new Dictionary<string, DateTime> { [@"C:\Acme\a.exe"] = first.AddDays(1) })[@"C:\Acme\a.exe"]);
    }

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3\Program Files\Acme\a.exe", @"C:\Program Files\Acme\a.exe")]
    [InlineData(@"\Device\HarddiskVolume9\Acme\a.exe", null)]              // a drive that isn't here: no guess
    [InlineData(@"\Device\HarddiskVolume3\Acme\readme.txt", null)]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe", "Microsoft.WindowsCalculator_8wekyb3d8bbwe")]
    [InlineData("SequenceNumber", null)]
    public void WindowsRecordOfARunNamesAProgramOrNothing(string value, string? path)
    {
        var at = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var volumes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"\Device\HarddiskVolume3"] = "C:" };
        var run = AppsUsage.ModeratorRun(value, BitConverter.GetBytes(at.ToFileTimeUtc()), volumes);
        Assert.Equal(path, run?.Path);
        if (path is not null) Assert.Equal(at, run!.Value.At);
    }

    [Fact]
    public void ARecordTooShortOrWithNoTimeIsNoRun()
    {
        var volumes = new Dictionary<string, string> { [@"\Device\HarddiskVolume3"] = "C:" };
        Assert.Null(AppsUsage.ModeratorRun(@"\Device\HarddiskVolume3\a.exe", [1, 2], volumes));
        Assert.Null(AppsUsage.ModeratorRun(@"\Device\HarddiskVolume3\a.exe", new byte[8], volumes));
    }

    public void Dispose()
    {
        try { DeletionGate.DeleteDirectory(_root, true); } catch (IOException) { }
    }
}
