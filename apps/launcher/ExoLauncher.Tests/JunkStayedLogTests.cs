namespace ExoLauncher.Tests;

/// <summary>
/// The line that says which junk files stayed and who holds them. Ways it goes wrong: logged as a Warn, so it rides in
/// crash reports (the holder's program names are not what a crash report should carry); or it logs a path instead of a name.
/// </summary>
public sealed class JunkStayedLogTests
{
    private static readonly string Source = File.ReadAllText(Repo.PathOf("ExoLauncher", "Services", "WebHostBridge.Apps.cs"));

    [Fact]
    public void TheStayedLineIsAnInfoLineNotAWarn()
    {
        Assert.Contains("AppLog.Info($\"Junk: {failed.Count} files stayed", Source);
        Assert.DoesNotContain("AppLog.Warn($\"Junk: {failed.Count} files stayed", Source);
    }

    [Fact]
    public void TheStayedLineNamesFilesByNameOnly()
    {
        Assert.Contains("Path.GetFileName(path)", Source);
    }
}
