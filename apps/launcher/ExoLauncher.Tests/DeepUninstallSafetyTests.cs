using System.Text;
using ExoLauncher.Services;

namespace ExoLauncher.Tests;

/// <summary>
/// The safety around a deep uninstall, all against made-up files (never this PC's Task Scheduler, Installer folder or
/// System Restore). Ways it could fail: a saved task's XML changed since it was written (another program to run, or a
/// non-Exec action) is registered by an administrator; a task is put back over one of its name that is there again; a
/// declined prompt counts as put back. A Windows Installer copy that a product or patch still needs reads as orphaned (an
/// empty or failed read of what's registered makes every copy look orphaned); something in a subfolder ($PatchCache$), a
/// file that isn't .msi or .msp, or one written in the last week is offered. A restore point that wasn't made reads as made,
/// so the run goes on without one; the elevated step's answer path breaks out of its literal.
/// </summary>
public sealed class DeepUninstallSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-deep-" + Guid.NewGuid().ToString("N"));

    public DeepUninstallSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { ExoLauncher.Helpers.DeletionGate.DeleteDirectory(_root, true); } catch (IOException) { }
    }

    private const string Exe = @"C:\Program Files\Acme\updater.exe";

    private static string TaskXml(params string[] commands) =>
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions Context=\"Author\">" +
        string.Concat(commands.Select(command => $"<Exec><Command>{command}</Command><Arguments>/check</Arguments></Exec>")) + "</Actions></Task>";

    [Fact]
    public void ASavedTaskStaysInsideOnlyWhileItRunsWhatItRan()
    {
        Assert.True(LeftoverScan.TaskStaysInside(TaskXml(Exe), Exe));
        Assert.True(LeftoverScan.TaskStaysInside(TaskXml('"' + Exe + '"'), Exe));
        Assert.False(LeftoverScan.TaskStaysInside(TaskXml(@"C:\Windows\System32\cmd.exe"), Exe));
        Assert.False(LeftoverScan.TaskStaysInside(TaskXml(Exe, @"C:\evil.exe"), Exe));
        Assert.False(LeftoverScan.TaskStaysInside(TaskXml(), Exe));
        Assert.False(LeftoverScan.TaskStaysInside(TaskXml(Exe).Replace("<Exec>", "<ComHandler>").Replace("</Exec>", "</ComHandler>"), Exe));
        Assert.False(LeftoverScan.TaskStaysInside("<NotATask/>", Exe));
        Assert.False(LeftoverScan.TaskStaysInside("not xml", Exe));
    }

    [Theory]
    [InlineData(@"\Acme\Update", @"\Acme\", "Update")]
    [InlineData(@"\Update", @"\", "Update")]
    public void ATaskPathSplitsAsTaskSchedulerNamesIt(string path, string folder, string name) =>
        Assert.Equal((folder, name), LeftoverScan.TaskParts(path));

    [Theory]
    [InlineData("Acme")]
    [InlineData(@"\Acme\")]
    [InlineData("\\Acme\n\\x")]
    public void ATaskPathThatIsntOneIsRefused(string path) => Assert.Null(LeftoverScan.TaskParts(path));

    private RemovalHistory History() => new(Path.Combine(_root, "removals.json"), _ => []);

    private string Saved(string xml)
    {
        var file = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        return file;
    }

    [Fact]
    public async Task ASavedTaskComesBackInOneElevatedStepPinnedToTheBytesChecked()
    {
        var history = History();
        var file = Saved(TaskXml(Exe));
        var id = history.Add("Acme", new(0, 0, 1, 0, 0), [], [], [], DateTime.UtcNow, tasks: [new(@"\Acme\Update", Exe, file)]);
        var there = false;
        IReadOnlyList<string>? ran = null;

        var done = await history.UndoAsync(id, _ => true, steps => { ran = steps; there = true; return Task.FromResult(true); }, _ => there);

        Assert.NotNull(done);
        Assert.Equal(1, done!.Back);
        Assert.Equal(0, done.Lost);
        var step = Assert.Single(ran!);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(step.Split(' ')[^1]));
        Assert.Contains(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))), script, StringComparison.Ordinal);
        Assert.Contains("Register-ScheduledTask -TaskPath '\\Acme\\' -TaskName 'Update'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-Force", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATaskChangedSinceOrThereAgainStaysAsItIs()
    {
        var history = History();
        var changed = Saved(TaskXml(@"C:\Windows\System32\cmd.exe"));
        var again = Saved(TaskXml(Exe));
        var id = history.Add("Acme", new(0, 0, 2, 0, 0), [], [], [], DateTime.UtcNow,
            tasks: [new(@"\Acme\Changed", Exe, changed), new(@"\Acme\Again", Exe, again)]);

        var done = await history.UndoAsync(id, _ => true, _ => throw new InvalidOperationException("nothing may run elevated"), path => path == @"\Acme\Again");

        Assert.Equal(0, done!.Back);
        Assert.Equal(2, done.Lost);
    }

    [Fact]
    public async Task ADeclinedPromptPutsNoTaskBackAndCanBeTriedAgain()
    {
        var history = History();
        var id = history.Add("Acme", new(0, 0, 1, 0, 0), [], [], [], DateTime.UtcNow, tasks: [new(@"\Acme\Update", Exe, Saved(TaskXml(Exe)))]);

        var done = await history.UndoAsync(id, _ => true, _ => Task.FromResult(false), _ => false);

        Assert.True(done!.Declined);
        Assert.True(history.CanPutBack(history.All().Single(), new Dictionary<string, RemovalHistory.RecycleEntry>()));
    }

    // ---- Windows Installer's cache ----

    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private const string Folder = @"C:\Windows\Installer";
    private static InstallerCache.CachedFile Cached(string name, int daysOld = 30, long bytes = 1000) => new(Path.Combine(Folder, name), bytes, Now.AddDays(-daysOld));

    [Fact]
    public void OnlyACopyNoProductOrPatchIsRegisteredWithIsOrphaned()
    {
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Windows\Installer\1a2b.msi", @"C:\WINDOWS\INSTALLER\3c4d.MSP" };
        var files = new[]
        {
            Cached("1a2b.msi"), Cached("3c4d.msp"), Cached("5e6f.msi"), Cached("7a8b.msp"),
            Cached("young.msi", daysOld: 2), Cached("icon.ico"), Cached("SourceHash{X}"),
            new InstallerCache.CachedFile(@"C:\Windows\Installer\$PatchCache$\Managed\x.msp", 10, Now.AddDays(-90)),
        };

        var orphans = InstallerCache.Orphans(Folder, files, registered, Now).Select(file => Path.GetFileName(file.Path)).ToList();

        Assert.Equal(["5e6f.msi", "7a8b.msp"], orphans);
    }

    [Fact]
    public void AFailedOrEmptyReadOfWhatsRegisteredOffersNothing()
    {
        var files = new[] { Cached("5e6f.msi") };
        Assert.Empty(InstallerCache.Orphans(Folder, files, null, Now));
        Assert.Empty(InstallerCache.Orphans(Folder, files, new HashSet<string>(), Now));
    }

    // ---- System Restore ----

    [Theory]
    [InlineData("made", "Made")]
    [InlineData("recent\r\n", "Recent")]
    [InlineData("off", "Off")]
    [InlineData("", "Failed")]
    [InlineData(null, "Failed")]
    [InlineData("MADE", "Failed")]
    public void TheElevatedStepsWordIsReadStrictly(string? word, string outcome) => Assert.Equal(outcome, RestorePoint.Read(word).ToString());

    [Fact]
    public void OnlyARestorePointToGoBackToLetsTheRunGoOn()
    {
        Assert.Null(RestorePoint.Problem(RestorePoint.Outcome.Made));
        Assert.Null(RestorePoint.Problem(RestorePoint.Outcome.Recent));
        foreach (var outcome in new[] { RestorePoint.Outcome.Off, RestorePoint.Outcome.Declined, RestorePoint.Outcome.Failed })
            Assert.Contains("Nothing was removed", RestorePoint.Problem(outcome), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAnswerPathStaysALiteral()
    {
        var step = RestorePoint.Step(@"C:\Temp\it's.restore");
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(step.Split(' ')[^1]));
        Assert.Contains(@"[IO.File]::WriteAllText('C:\Temp\it''s.restore',$o)", script, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_UNINSTALL", script, StringComparison.Ordinal);
    }
}
