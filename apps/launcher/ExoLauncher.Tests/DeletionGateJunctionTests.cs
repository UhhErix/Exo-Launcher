using System.Diagnostics;
using ExoLauncher.Helpers;

namespace ExoLauncher.Tests;

/// <summary>
/// A delete through a junction into a kept folder is refused. The delete's path looks harmless (it lies under a folder that is not
/// kept), but Windows opens the junction's target, which is Exo's backups. Ways this could fail: the gate compares the written
/// path only and lets the delete reach the kept folder through the link. Fixture folders are temporary and the junction is
/// made by mklink; nothing outside the temp folder is touched.
/// </summary>
public sealed class DeletionGateJunctionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exo-junction-" + Guid.NewGuid().ToString("N"));

    public DeletionGateJunctionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // rmdir takes out the junction itself, not what it points to; the gate would walk into the kept folder otherwise.
        var link = Path.Combine(_root, "Link");
        if (Directory.Exists(link))
        {
            using var rmdir = Process.Start(new ProcessStartInfo("cmd", $"/c rmdir \"{link}\"") { UseShellExecute = false, CreateNoWindow = true });
            rmdir?.WaitForExit();
        }
        if (Directory.Exists(_root)) DeletionGate.DeleteDirectory(_root, recursive: true);
    }

    private DeletionGate.Policy Policy(string kept) => new(
        KeptRoots: [kept],
        ExoData: Path.Combine(_root, "no-exo-data"),
        ShaderRoots: [],
        Running: () => [],
        ShaderBusy: () => false);

    [Fact]
    public void ADeleteThroughAJunctionIntoAKeptFolderIsRefused()
    {
        var kept = Path.Combine(_root, "Backups");
        Directory.CreateDirectory(kept);
        File.WriteAllText(Path.Combine(kept, "exo-launcher-backup-20261109-aaaaaaaa.json"), "{}");
        var link = Path.Combine(_root, "Link");
        var made = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{kept}\"") { UseShellExecute = false, CreateNoWindow = true });
        made!.WaitForExit();
        Assert.Equal(0, made.ExitCode);

        var why = DeletionGate.Refusal(Path.Combine(link, "exo-launcher-backup-20261109-aaaaaaaa.json"), clean: false, owners: null, Policy(kept));

        Assert.NotNull(why);
    }

    [Fact]
    public void ADeleteOutsideEveryKeptFolderIsStillAllowed()
    {
        var kept = Path.Combine(_root, "Backups");
        Directory.CreateDirectory(kept);
        var other = Path.Combine(_root, "Elsewhere", "file.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "x");

        Assert.Null(DeletionGate.Refusal(other, clean: false, owners: null, Policy(kept)));
    }
}
