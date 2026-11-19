using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// The locked-file report must name the holders only for a real lock: a sharing or lock HRESULT, or an access refusal that
/// Restart Manager can explain. These fail if: a file-not-found reads as locked, an access refusal with no holder loses the
/// caller's own message, the message drops a name or a pid, or the list is not capped.
/// </summary>
public sealed class LockedFilesTests
{
    private static IOException Io(int hresult) => new("test") { HResult = hresult };

    [Theory]
    [InlineData(unchecked((int)0x80070020))] // sharing violation
    [InlineData(unchecked((int)0x80070021))] // lock violation
    public void SharingAndLockViolationsAreLockErrors(int hresult)
    {
        Assert.True(LockedFiles.IsLockError(Io(hresult)));
        Assert.True(LockedFiles.MayBeLocked(Io(hresult)));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070002))] // file not found
    [InlineData(unchecked((int)0x80070005))] // access denied as an IOException, not a lock
    [InlineData(unchecked((int)0x80004005))] // generic failure
    public void OtherIoErrorsAreNotLocks(int hresult)
    {
        Assert.False(LockedFiles.IsLockError(Io(hresult)));
        Assert.False(LockedFiles.MayBeLocked(Io(hresult)));
    }

    [Fact]
    public void AccessRefusalMayBeALockButIsNotProvenOne()
    {
        var refused = new UnauthorizedAccessException("denied");
        Assert.False(LockedFiles.IsLockError(refused));
        Assert.True(LockedFiles.MayBeLocked(refused)); // asked of Restart Manager, which decides
    }

    [Fact]
    public void NobodyHoldingMeansNoMessage()
    {
        Assert.Null(LockedFiles.Describe("update.exe is", []));
    }

    [Fact]
    public void NamesEachHolderWithItsPid()
    {
        var text = LockedFiles.Describe("update.exe is", [new(1234, "Steam"), new(42, "Discord")]);
        Assert.Equal("update.exe is in use by: Steam (pid 1234), Discord (pid 42).", text);
    }

    [Fact]
    public void UnnamedProgramIsAProgramAndTheListIsCapped()
    {
        var holders = Enumerable.Range(1, LockedFiles.MaxNamed + 2).Select(i => new LockedFiles.Holder((uint)i, i == 1 ? " " : "App" + i)).ToList();
        var text = LockedFiles.Describe("Hades's files are", holders);
        Assert.StartsWith("Hades's files are in use by: a program (pid 1), App2 (pid 2)", text);
        Assert.EndsWith(", App5 (pid 5) and 2 more.", text);
        Assert.DoesNotContain("App6", text);
    }

    [Fact]
    public void ExplainKeepsTheCallersMessageWhenItIsNotALock()
    {
        var message = LockedFiles.Explain(Io(unchecked((int)0x80070002)), @"C:\nowhere", "x is", "fallback text");
        Assert.Equal("fallback text", message);
    }

    [Fact]
    public void ExplainKeepsTheCallersMessageWhenNobodyHoldsIt()
    {
        var missing = Path.Combine(Path.GetTempPath(), "exo-missing-" + Guid.NewGuid().ToString("N") + ".bin");
        var message = LockedFiles.Explain(new UnauthorizedAccessException("denied"), missing, "x is", "fallback text");
        Assert.Equal("fallback text", message);
    }
}

/// <summary>Restart Manager, for real: a file this process holds open exclusively must list this process.</summary>
public sealed class LockedFilesIntegrationTests
{
    [Fact]
    public void ReportsTheProcessHoldingAFileExclusively()
    {
        var path = Path.Combine(Path.GetTempPath(), "exo-lock-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                var holders = LockedFiles.Holders(path);
                Assert.Contains(holders, holder => holder.Pid == (uint)Environment.ProcessId);
            }
        }
        finally
        {
            DeletionGate.DeleteFile(path);
        }
    }

    [Fact]
    public void AFileNobodyHoldsListsNobody()
    {
        var path = Path.Combine(Path.GetTempPath(), "exo-free-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(path, "x");
        try
        {
            Assert.Empty(LockedFiles.Holders(path));
        }
        finally
        {
            DeletionGate.DeleteFile(path);
        }
    }
}
